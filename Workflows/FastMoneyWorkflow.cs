using System.Diagnostics;
using System.Drawing;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed class FastMoneyWorkflow : IMacroWorkflow
{
    private const string Workflow = "CR Farm";
    private const int PositionThrottleHoldMilliseconds = 550;
    private const double PositionCandidateThreshold = 0.70;
    private const double MinimumPositionAuthorizationThreshold = 0.90;
    private const int PositionHandbrakeHoldMilliseconds = 25_000;
    private const int PositionHandbrakeProbeMilliseconds = 700;
    private const int PositionHandbrakeFrameIntervalMilliseconds = 35;
    private const int PositionHandbrakeConsensusFrames = 3;
    private const long MinimumMaximumPlausibleCreditGain = 2_000_000;
    // A gravação nominal usava 250 ms, mas captura + inferência faziam cada
    // passo durar ~896 ms. O orçamento de 310 ms preserva esse intervalo físico;
    // uma sonda imediata agora elimina a janela cega sem alongar o passo quando erra.
    private const int PositionThrottleSettleMilliseconds = 310;
    private const int PreRaceReadyTimeoutMilliseconds = 8_000;
    private const int PreRaceStartConfirmationMilliseconds = 900;
    private const int PreRaceStartSettleMilliseconds = 6_000;
    private const int PreRaceStartMaximumAttempts = 2;
    private const int PreRaceExitSettleMilliseconds = 12_000;
    private static readonly double[] PositionThrottleRamp = [0.32, 0.32, 0.32, 0.37, 0.37, 0.42];
    private static readonly RectangleF[] PreRaceMenuSelectionRows =
    [
        new(0.034f, 0.608f, 0.018f, 0.035f),
        new(0.034f, 0.658f, 0.018f, 0.035f),
        new(0.034f, 0.708f, 0.018f, 0.035f),
        new(0.034f, 0.758f, 0.018f, 0.035f),
        new(0.034f, 0.808f, 0.018f, 0.035f)
    ];
    private static readonly RectangleF ExitConfirmationYesSelection = new(0.32f, 0.51f, 0.36f, 0.06f);
    private static readonly ClassicalGameStateDetector ClassicalState = new();

    public MacroKind Kind => MacroKind.Farmar200kMin;

    public async Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetCredits is < 1 or > 999_999_999)
        {
            throw new AutomationFaultException($"Meta de CR inválida para o farm integrado: {request.TargetCredits}.");
        }

        if (!context.Input.IsBackgroundInputAvailable && !context.Input.TryEnableBackgroundInput())
        {
            throw new CalibrationRequiredException(
                "O Farm de CR exige o ViGEmBus mesmo em primeiro plano, porque o encaixe lento entre as placas " +
                "usa aceleração analógica. Instale ou valide o ViGEmBus antes de iniciar.");
        }

        _ = context.GameWindow.GetRequiredGameWindow();
        var mode = context.Settings.InputMode == InputMode.Foreground
            ? "primeiro plano"
            : "segundo plano experimental";
        context.Logger.State(
            Workflow,
            "Iniciar",
            $"Máquina de estados ativa em {mode}; ONNX decide a posição e o menu confirma o resultado real.");
        context.Telemetry.UpdateStage(
            "Preparando rua",
            $"Validando o estado inicial do Farm de CR em {mode}.");

        var carSelector = new RequiredCarSelector(context);
        await carSelector.EnsureSelectedAsync(RequiredCarDefinition.CrFarm, cancellationToken);
        context.Logger.State(
            Workflow,
            "CarroRequisito",
            "Nissan S-Cargo S1 800 confirmada antes da primeira tentativa. " +
            "Dificuldade e assistências permanecem avisos de pré-requisito, sem leitura dinâmica.");

        var attemptNumber = 0;
        var consecutiveRecoveries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Telemetry.UpdateStage(
                "Preparando rua",
                "Confirmando um estado seguro antes da próxima tentativa.");
            await PrepareStreetMenuAsync(context, cancellationToken);
            var creditsBefore = await TryReadConfirmedCreditsAsync(
                context,
                "CreditosAntes",
                maximumPlausibleValue: null,
                cancellationToken);
            if (creditsBefore is null)
            {
                throw new CalibrationRequiredException(
                    "O saldo inicial de CR não pôde ser confirmado em duas leituras. " +
                    "Nenhum evento foi aberto; verifique a interface do jogo antes de tentar novamente.");
            }

            if (request.TargetCredits is { } targetCredits && creditsBefore >= targetCredits)
            {
                context.Logger.State(
                    Workflow,
                    "MetaAtingida",
                    $"Saldo confirmado de {creditsBefore:N0} CR atingiu a meta integrada de {targetCredits:N0} CR.");
                context.Telemetry.UpdateStage(
                    "Meta de CR atingida",
                    "Retornando ao WheelSpin para releitura independente dos recursos.");
                return;
            }

            attemptNumber++;
            context.Logger.State(Workflow, "Tentativa", $"Attempt {attemptNumber}: abrindo o evento e posicionando o carro.");
            context.Telemetry.UpdateStage(
                "Posicionando carro",
                $"Tentativa {attemptNumber}: abrindo o evento e alinhando o carro entre as placas.");
            await OpenEventAndPositionAsync(context, cancellationToken);

            using var immediateHandbrakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            context.Telemetry.UpdateStage(
                "Validando posição",
                $"Tentativa {attemptNumber}: confirmando a posição com consenso visual.");
            var (prediction, frames, immediateHandbrakeTask) = await AlignAndEvaluatePositionAsync(
                context,
                immediateHandbrakeCts,
                cancellationToken);

            CrFarmAttempt attempt;
            try
            {
                try
                {
                    attempt = context.CrFarmSamples.BeginAttempt(frames, prediction);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    context.Logger.Warn(
                        $"A coleta da tentativa {attemptNumber} falhou sem interromper a inferência: {exception.Message}");
                    attempt = new CrFarmAttempt(
                        $"runtime-{attemptNumber}",
                        null,
                        [],
                        prediction);
                }

                context.Logger.State(
                    Workflow,
                    "PosicaoONNX",
                    $"Attempt {attemptNumber} — Prediction: {prediction.Label} " +
                    $"(Valid={prediction.ValidProbability:P1}, min={prediction.MinimumValidProbability:P1}, " +
                    $"max={prediction.MaximumValidProbability:P1}, {prediction.Elapsed.TotalMilliseconds:F0} ms).");
            }
            catch
            {
                if (immediateHandbrakeTask is not null)
                {
                    await immediateHandbrakeCts.CancelAsync();
                    try
                    {
                        await immediateHandbrakeTask;
                    }
                    catch (Exception releaseException)
                    {
                        context.Logger.Warn(
                            $"O freio de mão foi liberado durante a falha de coleta: {releaseException.Message}");
                    }
                }

                throw;
            }
            finally
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }

            if (prediction.Label != CrPositionLabel.Valid)
            {
                context.CrFarmSamples.KeepPending(
                    attempt,
                    $"RejectedByModel:{prediction.Label}");
                context.Logger.Warn(
                    $"Attempt {attemptNumber}: posição {prediction.Label}; o freio de mão não será executado. " +
                    "A amostra permanece Pending porque uma previsão isolada não é ground truth.");
                var rejectionMenu = await OpenAndDetectOutcomeMenuAsync(context, cancellationToken);
                if (rejectionMenu.Kind != GameContextKind.EventMenu)
                {
                    context.CrFarmSamples.KeepPending(
                        attempt,
                        "RejectedPositionMenuUnknown",
                        rejectionMenu.Kind);
                    await ThrowUnknownContextAsync(context, "RejeicaoPosicao", rejectionMenu, cancellationToken);
                }

                consecutiveRecoveries++;
                await RecoverFromEventMenuAsync(context, rejectionMenu, cancellationToken);
                EnsureRecoveryBudget(context, consecutiveRecoveries);
                continue;
            }

            context.Telemetry.UpdateStage(
                "Executando evento",
                $"Tentativa {attemptNumber}: posição autorizada; executando a etapa calibrada.");
            await ExecuteCrAttemptAsync(
                context,
                immediateHandbrakeTask ?? throw new AutomationFaultException(
                    "O encaixe foi liberado sem iniciar o freio de mão."),
                cancellationToken);
            context.Telemetry.UpdateStage(
                "Validando resultado",
                $"Tentativa {attemptNumber}: verificando o menu final e a variação real de créditos.");
            var outcomeMenu = await OpenAndDetectOutcomeMenuAsync(context, cancellationToken);
            if (outcomeMenu.Kind == GameContextKind.StreetMenu)
            {
                var maximumPlausibleGain = Math.Max(
                    MinimumMaximumPlausibleCreditGain,
                    (long)Math.Max(1, context.Settings.CrFarm.MinimumSuccessfulCreditGain) * 10);
                var maximumPlausibleCredits = Math.Min(
                    999_999_999,
                    creditsBefore.Value + maximumPlausibleGain);
                var creditsAfter = await TryReadConfirmedCreditsAsync(
                    context,
                    "CreditosDepois",
                    maximumPlausibleValue: maximumPlausibleCredits,
                    cancellationToken);
                if (creditsAfter is null)
                {
                    context.CrFarmSamples.KeepPending(
                        attempt,
                        "StreetMenuWithoutReliableCreditDelta",
                        outcomeMenu.Kind);
                    throw new CalibrationRequiredException(
                        "O jogo voltou à rua, mas não foi possível confirmar o ganho de CR. " +
                        "A tentativa permaneceu Pending e o BOT parou para não confundir retorno à rua com pontuação.");
                }

                if (creditsAfter < creditsBefore)
                {
                    using var diagnosticFrame = await context.Capture.CaptureAsync(cancellationToken);
                    var diagnosticPath = context.Capture.SaveDiagnostic(
                        diagnosticFrame.Bitmap,
                        Workflow,
                        "CreditosDepoisRegressao");
                    context.CrFarmSamples.KeepPending(
                        attempt,
                        $"UnreliableCreditDelta:{creditsBefore}->{creditsAfter}",
                        outcomeMenu.Kind);
                    throw new CalibrationRequiredException(
                        $"A leitura de CR regrediu de {creditsBefore:N0} para {creditsAfter:N0}; " +
                        $"a tentativa permaneceu Pending e o BOT parou com segurança. Diagnóstico: {diagnosticPath}");
                }

                var delta = creditsAfter.Value - creditsBefore.Value;
                var minimumSuccessfulGain = Math.Max(
                    1,
                    context.Settings.CrFarm.MinimumSuccessfulCreditGain);
                var groundTruth = delta >= minimumSuccessfulGain
                    ? CrAttemptGroundTruth.Valid
                    : CrAttemptGroundTruth.Invalid;
                var comparison = Compare(prediction.Label, groundTruth);
                var saved = CompleteAttemptSafely(
                    context,
                    attempt,
                    groundTruth,
                    comparison,
                    outcomeMenu.Kind);
                context.Logger.State(
                    Workflow,
                    "Resultado",
                    $"Attempt {attemptNumber} — GroundTruth: {groundTruth}; Outcome: StreetMenu; " +
                    $"CR: {creditsBefore:N0} -> {creditsAfter:N0} (delta {delta:+#,0;-#,0;0}; " +
                    $"mínimo {minimumSuccessfulGain:N0}); " +
                    $"Classification: {comparison}; Sample(s): {saved.Count}.");
                if (groundTruth == CrAttemptGroundTruth.Invalid)
                {
                    context.Logger.Warn(
                        $"Attempt {attemptNumber}: voltou à rua sem pontuar; a posição foi rotulada Invalid, " +
                        "pois StreetMenu sozinho não comprova encaixe nas placas.");
                    consecutiveRecoveries++;
                    EnsureRecoveryBudget(context, consecutiveRecoveries);
                }
                else
                {
                    consecutiveRecoveries = 0;
                    if (!request.Nested)
                    {
                        context.Telemetry.CycleCompleted(
                            $"Tentativa {attemptNumber} confirmada com ganho real de {delta:N0} CR.");
                    }
                }

                if (groundTruth == CrAttemptGroundTruth.Valid &&
                    request.TargetCredits is { } targetCreditsAfter &&
                    creditsAfter >= targetCreditsAfter)
                {
                    context.Logger.State(
                        Workflow,
                        "MetaAtingida",
                        $"Saldo confirmado de {creditsAfter:N0} CR atingiu a meta integrada de {targetCreditsAfter:N0} CR.");
                    context.Telemetry.UpdateStage(
                        "Meta de CR atingida",
                        "Retornando ao WheelSpin para releitura independente dos recursos.");
                    return;
                }

                await Task.Delay(2_000, cancellationToken);
                continue;
            }

            if (outcomeMenu.Kind == GameContextKind.EventMenu)
            {
                var comparison = Compare(prediction.Label, CrAttemptGroundTruth.Invalid);
                var saved = CompleteAttemptSafely(
                    context,
                    attempt,
                    CrAttemptGroundTruth.Invalid,
                    comparison,
                    outcomeMenu.Kind);
                context.Logger.State(
                    Workflow,
                    "Resultado",
                    $"Attempt {attemptNumber} — GroundTruth: Invalid; Outcome: EventMenu; " +
                    $"Classification: {comparison}; Sample(s): {saved.Count}.");

                consecutiveRecoveries++;
                await RecoverFromEventMenuAsync(context, outcomeMenu, cancellationToken);
                EnsureRecoveryBudget(context, consecutiveRecoveries);
                continue;
            }

            context.CrFarmSamples.KeepPending(attempt, "OutcomeUnknown", outcomeMenu.Kind);
            await ThrowUnknownContextAsync(context, "Resultado", outcomeMenu, cancellationToken);
        }
    }

    private static async Task PrepareStreetMenuAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        for (var pass = 1; pass <= 3; pass++)
        {
            var state = await context.GameContext.DetectAsync(cancellationToken);
            if (state.Kind == GameContextKind.StreetMenu)
            {
                return;
            }

            if (HasExitConfirmation(state))
            {
                context.Logger.State(
                    Workflow,
                    "RetomarConfirmacaoSaida",
                    "Modal de saída já aberto; retomando pela confirmação validada, sem renavegar o menu.");
                _ = await ConfirmOpenExitAndOpenStreetMenuAsync(context, state, cancellationToken);
                return;
            }

            if (state.Kind == GameContextKind.EventMenu)
            {
                await RecoverFromEventMenuAsync(context, state, cancellationToken);
                continue;
            }

            if (state.Kind == GameContextKind.ControllerDisconnected)
            {
                context.Telemetry.Recovery(
                    "Aviso de controle desconectado confirmado; tentando reconectar uma única vez.");
                context.Logger.State(
                    Workflow,
                    "ReconectarControle",
                    "Controle virtual já validado; confirmando o aviso do jogo uma única vez.");
                await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
                await Task.Delay(1_200, cancellationToken);
                continue;
            }

            if (state.Kind == GameContextKind.EventPreRaceMenu)
            {
                await RecoverFromPreRaceMenuAsync(context, state, cancellationToken);
                return;
            }

            if (state.Kind == GameContextKind.WorldMap)
            {
                context.Logger.State(
                    Workflow,
                    "FecharMapa",
                    "Mapa-múndi detectado; fechando-o uma única vez antes de sondar o menu da rua.");
                await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
                await Task.Delay(1_000, cancellationToken);
                continue;
            }

            if (state.Kind == GameContextKind.Garage)
            {
                throw new CalibrationRequiredException(
                    "O Farm de CR precisa iniciar na rua; a garagem foi detectada e nenhuma entrada foi enviada.");
            }

            // Street quase sempre tem pouco texto. Abrir o menu uma vez é a
            // sonda segura que diferencia rua/evento sem dirigir o carro.
            await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
            await Task.Delay(context.Settings.CrFarm.OutcomeSettleMs, cancellationToken);
            var menu = await DetectMenuWithConsensusAsync(context, cancellationToken);
            if (menu.Kind == GameContextKind.StreetMenu)
            {
                return;
            }

            if (menu.Kind == GameContextKind.EventMenu)
            {
                await RecoverFromEventMenuAsync(context, menu, cancellationToken);
                continue;
            }

            await ThrowUnknownContextAsync(context, "PrepararRua", menu, cancellationToken);
        }

        throw new CalibrationRequiredException(
            "Não foi possível preparar o menu da rua após uma recuperação limitada.");
    }

    private static async Task RecoverFromPreRaceMenuAsync(
        AutomationContext context,
        GameContextResult preRaceMenu,
        CancellationToken cancellationToken)
    {
        if (preRaceMenu.Kind != GameContextKind.EventPreRaceMenu)
        {
            throw new AutomationFaultException(
                $"Recuperação recusada porque o contexto é {preRaceMenu.Kind}, não EventPreRaceMenu.");
        }

        context.Telemetry.Recovery(
            "Menu pré-corrida remanescente confirmado; retornando à rua por um caminho validado.");

        var selectedRow = await context.Vision.FindLimeSelectionAsync(
            PreRaceMenuSelectionRows,
            cancellationToken,
            minimumRatio: 0.08);
        if (selectedRow < 0)
        {
            await ThrowUnknownContextAsync(
                context,
                "LocalizarFocoMenuPreCorrida",
                preRaceMenu,
                cancellationToken);
        }

        context.Logger.State(
            Workflow,
            "SairMenuPreCorrida",
            $"Menu pré-corrida remanescente detectado; foco validado na linha {selectedRow + 1}/5. " +
            "Navegando somente até Sair da Corrida, sem iniciar nem dirigir.");
        for (var row = selectedRow; row < PreRaceMenuSelectionRows.Length - 1; row++)
        {
            await PulseAsync(context, GameKey.Down, 70, 110, cancellationToken);
        }

        var exitRow = await context.Vision.FindLimeSelectionAsync(
            PreRaceMenuSelectionRows,
            cancellationToken,
            minimumRatio: 0.08);
        if (exitRow != PreRaceMenuSelectionRows.Length - 1)
        {
            await ThrowUnknownContextAsync(
                context,
                "SelecionarSaidaMenuPreCorrida",
                preRaceMenu,
                cancellationToken);
        }

        context.Logger.State(
            Workflow,
            "SelecionarSaidaPreCorrida",
            "Sair da Corrida confirmado pela borda verde; abrindo o modal de saída.");
        await OpenSelectedExitAndConfirmStreetMenuAsync(context, cancellationToken);
    }

    private static async Task OpenEventAndPositionAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        await PulseAsync(context, GameKey.PageDown, 31, 265, cancellationToken);
        await PulseAsync(context, GameKey.PageDown, 31, 265, cancellationToken);
        await PulseAsync(context, GameKey.PageDown, 31, 265, cancellationToken);
        await PulseAsync(context, GameKey.PageDown, 31, 265, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 31, 265, cancellationToken);
        await Task.Delay(333, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 31, 500, cancellationToken);
        await Task.Delay(333, cancellationToken);
        await PulseAsync(context, GameKey.Backspace, 31, 400, cancellationToken);
        await PulseAsync(context, GameKey.Up, 31, 265, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 31, 265, cancellationToken);
        await PulseAsync(context, GameKey.NumPad1, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad7, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad9, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad3, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad9, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad3, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad6, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad9, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.NumPad5, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 60, 200, cancellationToken);
        await PulseAsync(context, GameKey.Down, 60, 100, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 60, 2_500, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 79, 1_878, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 140, 2_832, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 78, 12_518, cancellationToken);
        await StartEventFromPreRaceMenuAsync(context, cancellationToken);
        await DriveToPlatesAsync(context, cancellationToken);
    }

    private static async Task StartEventFromPreRaceMenuAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ClassicalGameStateResult? last = null;
        while (stopwatch.ElapsedMilliseconds < PreRaceReadyTimeoutMilliseconds)
        {
            using var captured = await context.Capture.CaptureAsync(cancellationToken);
            last = ClassicalState.Analyze(captured.Bitmap);
            if (last.Kind == ClassicalGameStateKind.EventPreRaceMenu)
            {
                for (var startAttempt = 1;
                     startAttempt <= PreRaceStartMaximumAttempts;
                     startAttempt++)
                {
                    context.Logger.State(
                        Workflow,
                        "IniciarCorrida",
                        $"Menu pré-corrida confirmado por visão clássica ({last.Evidence}, " +
                        $"{last.Elapsed.TotalMilliseconds:F1} ms); iniciando no instante calibrado " +
                        $"({startAttempt}/{PreRaceStartMaximumAttempts}).");

                    await PulseAsync(context, GameKey.Enter, 120, 0, cancellationToken);
                    var startReleasedTimestamp = Stopwatch.GetTimestamp();
                    await Task.Delay(PreRaceStartConfirmationMilliseconds, cancellationToken);

                    using var afterStartFrame = await context.Capture.CaptureAsync(cancellationToken);
                    var afterStart = ClassicalState.Analyze(afterStartFrame.Bitmap);
                    if (afterStart.Kind == ClassicalGameStateKind.EventPreRaceMenu)
                    {
                        if (startAttempt < PreRaceStartMaximumAttempts)
                        {
                            context.Logger.Warn(
                                $"O comando de iniciar corrida não fechou o menu pré-corrida " +
                                $"({startAttempt}/{PreRaceStartMaximumAttempts}); repetindo A uma única vez.");
                            continue;
                        }

                        throw new CalibrationRequiredException(
                            "O menu pré-corrida permaneceu aberto após duas confirmações limitadas; " +
                            "nenhuma entrada de direção foi enviada.");
                    }

                    if (afterStart.Kind != ClassicalGameStateKind.Unknown)
                    {
                        throw new CalibrationRequiredException(
                            $"O início da corrida saiu para um contexto clássico inesperado " +
                            $"({afterStart.Kind}: {afterStart.Evidence}); nenhuma entrada de direção foi enviada.");
                    }

                    var elapsedSinceRelease = Stopwatch.GetElapsedTime(startReleasedTimestamp);
                    var remainingSettleMilliseconds = PreRaceStartSettleMilliseconds -
                        (int)Math.Ceiling(elapsedSinceRelease.TotalMilliseconds);
                    if (remainingSettleMilliseconds > 0)
                    {
                        await Task.Delay(remainingSettleMilliseconds, cancellationToken);
                    }

                    context.Logger.State(
                        Workflow,
                        "ConfirmarInicioCorrida",
                        $"Menu pré-corrida desapareceu após {startAttempt}/{PreRaceStartMaximumAttempts}; " +
                        "rota liberada no mesmo deadline calibrado.");
                    return;
                }
            }

            await Task.Delay(150, cancellationToken);
        }

        throw new CalibrationRequiredException(
            "O menu pré-corrida não foi confirmado pela visão clássica; " +
            $"nenhuma entrada de direção foi enviada. Última evidência: {last?.Evidence ?? "sem frame"}.");
    }

    private static async Task DriveToPlatesAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        // Estes quatro pulsos e esperas são a calibração Pulover que já
        // apresentava boa taxa de encaixe. O ONNX só valida o resultado.
        // O trecho preciso roda fora da UI e usa temporizador de alta resolução
        // somente durante estes ~6 s; o watchdog de foco continua ativo.
        await Task.Run(async () =>
        {
            await PrecisePulseAsync(context, GameKey.W, 600, 188, cancellationToken);
            await PrecisePulseAsync(context, GameKey.A, 200, 953, cancellationToken);
            await PrecisePulseAsync(context, GameKey.A, 180, 1_485, cancellationToken);
            await PrecisePulseAsync(context, GameKey.W, 300, 188, cancellationToken);
            await context.Input.DelayPreciselyAsync(1_843, cancellationToken);
        }, cancellationToken);
    }

    private static async Task<(CrPositionPrediction Prediction, List<Bitmap> Frames)> EvaluatePositionAsync(
        AutomationContext context,
        CancellationToken cancellationToken,
        int? frameIntervalMs = null)
    {
        var cr = context.Settings.CrFarm;
        var frameCount = Math.Clamp(cr.ConsensusFrames, 2, 5);
        var probabilities = new List<double>(frameCount);
        var frames = new List<Bitmap>(frameCount);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            for (var index = 0; index < frameCount; index++)
            {
                using var captured = await context.Capture.CaptureAsync(cancellationToken);
                probabilities.Add(await context.CrPosition.PredictValidProbabilityAsync(
                    captured.Bitmap,
                    cancellationToken));
                frames.Add(new Bitmap(captured.Bitmap));
                if (index + 1 < frameCount)
                {
                    await Task.Delay(
                        Math.Max(60, frameIntervalMs ?? cr.FrameIntervalMs),
                        cancellationToken);
                }
            }

            stopwatch.Stop();
            return (context.CrPosition.Aggregate(probabilities, stopwatch.Elapsed), frames);
        }
        catch
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            throw;
        }
    }

    private static async Task<(CrPositionPrediction Prediction, List<Bitmap> Frames)>
        EvaluatePositionUnderHandbrakeAsync(
            AutomationContext context,
            CancellationToken cancellationToken)
    {
        const int frameCount = PositionHandbrakeConsensusFrames;
        var probabilities = new List<double>(frameCount);
        var frames = new List<Bitmap>(frameCount);
        var stopwatch = Stopwatch.StartNew();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(PositionHandbrakeProbeMilliseconds);
        var probeToken = probeCts.Token;
        try
        {
            while (true)
            {
                using var captured = await context.Capture.CaptureAsync(probeToken);
                var probability = await context.CrPosition.PredictValidProbabilityAsync(
                    captured.Bitmap,
                    probeToken);
                probeToken.ThrowIfCancellationRequested();

                if (frames.Count == frameCount)
                {
                    frames[0].Dispose();
                    frames.RemoveAt(0);
                    probabilities.RemoveAt(0);
                }

                frames.Add(new Bitmap(captured.Bitmap));
                probabilities.Add(probability);

                if (probabilities.Count == frameCount)
                {
                    var prediction = context.CrPosition.Aggregate(
                        probabilities,
                        stopwatch.Elapsed);
                    if (prediction.Label == CrPositionLabel.Valid)
                    {
                        stopwatch.Stop();
                        return (prediction with { Elapsed = stopwatch.Elapsed }, frames);
                    }
                }

                await Task.Delay(
                    PositionHandbrakeFrameIntervalMilliseconds,
                    probeToken);
            }
        }
        catch (OperationCanceledException) when (
            probeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            if (probabilities.Count == frameCount)
            {
                return (
                    context.CrPosition.Aggregate(probabilities, stopwatch.Elapsed),
                    frames);
            }

            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            throw new AutomationFaultException(
                $"A sonda de posição sob o freio não obteve {frameCount} frames " +
                $"em {PositionHandbrakeProbeMilliseconds} ms.");
        }
        catch
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            throw;
        }
    }

    private static async Task<(
        CrPositionPrediction Prediction,
        List<Bitmap> Frames,
        Task? ImmediateHandbrakeTask)> AlignAndEvaluatePositionAsync(
        AutomationContext context,
        CancellationTokenSource immediateHandbrakeCts,
        CancellationToken cancellationToken)
    {
        var probability = await EvaluateSinglePositionProbabilityAsync(context, cancellationToken);
        if (probability >= PositionCandidateThreshold)
        {
            var candidate = await EvaluateCandidatePositionAsync(
                context,
                probability,
                immediateHandbrakeCts,
                cancellationToken,
                "posição inicial");
            return candidate;
        }

        context.Logger.State(
            Workflow,
            "EncaixarPlacas",
            $"Passo 0/{PositionThrottleRamp.Length}: encaixe Valid={probability:P1}.");

        for (var index = 0; index < PositionThrottleRamp.Length; index++)
        {
            var throttle = PositionThrottleRamp[index];
            await context.Input.PulseAcceleratorAsync(
                throttle,
                PositionThrottleHoldMilliseconds,
                cancellationToken);
            var settleStartedTimestamp = Stopwatch.GetTimestamp();

            // Capture o primeiro frame disponível assim que o acelerador for solto.
            // Se o carro cruzar a borda candidata, o KeyDown do freio acontece antes
            // de logs e antes dos clones usados pelo consenso/dataset.
            probability = await EvaluateSinglePositionProbabilityAsync(context, cancellationToken);
            if (probability >= PositionCandidateThreshold)
            {
                var candidate = await EvaluateCandidatePositionAsync(
                    context,
                    probability,
                    immediateHandbrakeCts,
                    cancellationToken,
                    "sonda imediata",
                    settleStartedTimestamp);
                return candidate;
            }

            var settleBudget = TimeSpan.FromMilliseconds(PositionThrottleSettleMilliseconds);
            var settleElapsed = Stopwatch.GetElapsedTime(settleStartedTimestamp);
            if (settleElapsed < settleBudget)
            {
                await Task.Delay(settleBudget - settleElapsed, cancellationToken);

                probability = await EvaluateSinglePositionProbabilityAsync(context, cancellationToken);
                if (probability >= PositionCandidateThreshold)
                {
                    var candidate = await EvaluateCandidatePositionAsync(
                        context,
                        probability,
                        immediateHandbrakeCts,
                        cancellationToken,
                        "sonda final",
                        settleStartedTimestamp);
                    return candidate;
                }
            }

            context.Logger.State(
                Workflow,
                "EncaixarPlacas",
                $"Passo {index + 1}/{PositionThrottleRamp.Length}: " +
                $"acelerador {throttle:P0}, encaixe Valid={probability:P1}.");
        }

        // Nunca autorize a partir de um consenso capturado com o carro livre.
        // Depois da rampa, congele primeiro e só então forme a janela de 3 frames.
        return await EvaluateCandidatePositionAsync(
            context,
            probability,
            immediateHandbrakeCts,
            cancellationToken,
            "sonda final protegida");
    }

    private static async Task<(
        CrPositionPrediction Prediction,
        List<Bitmap> Frames,
        Task? ImmediateHandbrakeTask)> EvaluateCandidatePositionAsync(
        AutomationContext context,
        double initialProbability,
        CancellationTokenSource immediateHandbrakeCts,
        CancellationToken cancellationToken,
        string probeOrigin,
        long? acceleratorReleasedTimestamp = null)
    {
        var authorizationThreshold = Math.Max(
            MinimumPositionAuthorizationThreshold,
            context.Settings.CrFarm.ValidThreshold);
        var protectiveProbe = initialProbability < authorizationThreshold;
        var handbrakeMilliseconds = PositionHandbrakeHoldMilliseconds +
            (protectiveProbe ? PositionHandbrakeProbeMilliseconds : 0);
        var probeStartedTimestamp = Stopwatch.GetTimestamp();
        // Arme o watchdog antes do KeyDown. Assim, nem o log síncrono executado
        // logo após o acionamento pode prolongar um freio ainda não autorizado.
        immediateHandbrakeCts.CancelAfter(PositionHandbrakeProbeMilliseconds);
        var handbrakeTask = StartImmediateHandbrake(
            context,
            handbrakeMilliseconds,
            immediateHandbrakeCts.Token);
        var handbrakeHandled = false;
        List<Bitmap>? frames = null;

        try
        {
            var releaseToHandbrake = acceleratorReleasedTimestamp is long releasedTimestamp
                ? Stopwatch.GetElapsedTime(releasedTimestamp).TotalMilliseconds
                : (double?)null;
            context.Logger.State(
                Workflow,
                "FreioMao",
                $"Candidato detectado por {probeOrigin}; freio de mão acionado após o KeyDown. " +
                (releaseToHandbrake is double measuredMilliseconds
                    ? $"Soltura do acelerador -> freio: {measuredMilliseconds:F0} ms. "
                    : string.Empty) +
                $"Sem consenso ele é liberado em até {PositionHandbrakeProbeMilliseconds} ms; " +
                "confirmado, permanece por pelo menos 25 s.");
            context.Logger.State(
                Workflow,
                "ProtegerEncaixe",
                $"Borda candidata de {initialProbability:P1}; freio protetivo acionado " +
                "antes do rebote, sem autorizar a pontuação.");

            var confirmedUnderHandbrake = await EvaluatePositionUnderHandbrakeAsync(
                context,
                cancellationToken);
            frames = confirmedUnderHandbrake.Frames;
            if (confirmedUnderHandbrake.Prediction.Label == CrPositionLabel.Valid)
            {
                var probeElapsed = Stopwatch.GetElapsedTime(probeStartedTimestamp);
                if (probeElapsed >= TimeSpan.FromMilliseconds(PositionHandbrakeProbeMilliseconds))
                {
                    handbrakeHandled = true;
                    await ReleaseCandidateHandbrakeAsync(
                        immediateHandbrakeCts,
                        handbrakeTask);
                    context.Logger.State(
                        Workflow,
                        "ValidarSobFreio",
                        $"Consenso chegou após o deadline físico ({probeElapsed.TotalMilliseconds:F0} ms); " +
                        "freio liberado e autorização rejeitada.");
                    var expiredFrames = frames;
                    frames = null;
                    return (
                        confirmedUnderHandbrake.Prediction with { Label = CrPositionLabel.Unknown },
                        expiredFrames,
                        null);
                }

                immediateHandbrakeCts.CancelAfter(Timeout.InfiniteTimeSpan);
                if (immediateHandbrakeCts.IsCancellationRequested || handbrakeTask.IsCompleted)
                {
                    handbrakeHandled = true;
                    await handbrakeTask;
                    throw new AutomationFaultException(
                        "O freio de mão terminou antes da confirmação da posição.");
                }

                handbrakeHandled = true;
                var returnedFrames = frames;
                frames = null;
                return (
                    confirmedUnderHandbrake.Prediction,
                    returnedFrames,
                    handbrakeTask);
            }

            handbrakeHandled = true;
            await ReleaseCandidateHandbrakeAsync(
                immediateHandbrakeCts,
                handbrakeTask);
            context.Logger.State(
                Workflow,
                "ValidarSobFreio",
                $"Borda de {initialProbability:P1} não confirmou Valid sob o freio " +
                $"(média={confirmedUnderHandbrake.Prediction.ValidProbability:P1}, " +
                $"min={confirmedUnderHandbrake.Prediction.MinimumValidProbability:P1}); " +
                "freio liberado e posição rejeitada.");
            var rejectedFrames = frames;
            frames = null;
            return (
                confirmedUnderHandbrake.Prediction,
                rejectedFrames,
                null);
        }
        finally
        {
            if (!handbrakeHandled)
            {
                try
                {
                    await ReleaseCandidateHandbrakeAsync(
                        immediateHandbrakeCts,
                        handbrakeTask);
                }
                catch (Exception exception)
                {
                    context.Logger.Warn(
                        $"Falha adicional ao liberar o freio de mão: {exception.Message}");
                }
            }

            if (frames is not null)
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }
        }
    }

    private static Task StartImmediateHandbrake(
        AutomationContext context,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        // HoldAsync envia o KeyDown antes de retornar o Task. A confirmação
        // seguinte já captura o carro sob o freio, impedindo o rebote das placas.
        return context.Input.HoldAsync(GameKey.Space, holdMilliseconds, cancellationToken);
    }

    private static async Task ReleaseCandidateHandbrakeAsync(
        CancellationTokenSource cancellation,
        Task handbrakeTask)
    {
        await cancellation.CancelAsync();
        try
        {
            await handbrakeTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cancelamento esperado: HoldAsync já liberou a tecla no finally.
        }
    }

    private static async Task<double> EvaluateSinglePositionProbabilityAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        using var captured = await context.Capture.CaptureAsync(cancellationToken);
        return await context.CrPosition.PredictValidProbabilityAsync(
            captured.Bitmap,
            cancellationToken);
    }

    private static async Task ResetPositionForCompletionAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        await PulseAsync(context, GameKey.Menu, 125, 1_000, cancellationToken);
        var eventMenu = await DetectMenuWithConsensusAsync(context, cancellationToken);
        if (eventMenu.Kind != GameContextKind.EventMenu)
        {
            await ThrowUnknownContextAsync(
                context,
                "ConfirmarMenuAntesResetPosicao",
                eventMenu,
                cancellationToken);
        }

        await PulseAsync(context, GameKey.X, 125, 500, cancellationToken);
        var confirmation = await context.GameContext.DetectAsync(cancellationToken);
        if (confirmation.Kind != GameContextKind.CarPositionResetConfirmation)
        {
            await Task.Delay(350, cancellationToken);
            confirmation = await context.GameContext.DetectAsync(cancellationToken);
        }

        if (confirmation.Kind != GameContextKind.CarPositionResetConfirmation)
        {
            await ThrowUnknownContextAsync(
                context,
                "ConfirmarResetPosicao",
                confirmation,
                cancellationToken);
        }

        context.Logger.State(
            Workflow,
            "ResetarPosicao",
            "Modal Redefinir Posição do Carro confirmado por texto e visão clássica; confirmando uma vez.");
        await PulseAsync(context, GameKey.Enter, 78, 2_422, cancellationToken);
    }

    private static async Task ExecuteCrAttemptAsync(
        AutomationContext context,
        Task immediateHandbrakeTask,
        CancellationToken cancellationToken)
    {
        context.Logger.State(
            Workflow,
            "Executar",
            "Freio de mão ativo; ao completar 25 s, resetando a posição e concluindo a corrida.");
        await immediateHandbrakeTask;
        await Task.Delay(343, cancellationToken);
        await ResetPositionForCompletionAsync(context, cancellationToken);
        await PulseAsync(context, GameKey.W, 700, 282, cancellationToken);
        await PulseAsync(context, GameKey.A, 2_700, 735, cancellationToken);
        await PulseAsync(context, GameKey.W, 156, 50, cancellationToken);
        await PulseAsync(context, GameKey.W, 156, 50, cancellationToken);
        await PulseAsync(context, GameKey.W, 312, 8_844, cancellationToken);
        await PulseAsync(context, GameKey.Enter, 62, 800, cancellationToken);
        await Task.Delay(12_000, cancellationToken);
    }

    private static async Task<GameContextResult> OpenAndDetectOutcomeMenuAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var current = await context.GameContext.DetectAsync(cancellationToken);
        if (current.Kind is GameContextKind.StreetMenu or GameContextKind.EventMenu)
        {
            return current;
        }

        var closedWorldMap = false;
        if (current.Kind == GameContextKind.WorldMap)
        {
            context.Logger.State(
                Workflow,
                "FecharMapa",
                "Mapa-múndi detectado após a transição; fechando antes de abrir o menu de contexto.");
            await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
            await Task.Delay(1_000, cancellationToken);
            current = await context.GameContext.DetectAsync(cancellationToken);
            closedWorldMap = true;
        }

        if (current.Kind == GameContextKind.Unknown && !closedWorldMap)
        {
            // O retorno bem-sucedido pode exibir por alguns segundos o nome
            // da região. Menu enviado durante essa vinheta é descartado pelo
            // jogo; aguarde a HUD estabilizar antes da única sonda por ESC.
            await Task.Delay(6_000, cancellationToken);
        }

        await context.Input.TapAsync(GameKey.Menu, cancellationToken, 110);
        await Task.Delay(context.Settings.CrFarm.OutcomeSettleMs, cancellationToken);
        return await DetectMenuWithConsensusAsync(context, cancellationToken);
    }

    private static async Task<GameContextResult> DetectMenuWithConsensusAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        GameContextResult? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await context.GameContext.DetectAsync(cancellationToken);
            if (last.Kind is GameContextKind.StreetMenu or GameContextKind.EventMenu)
            {
                return last;
            }

            if (attempt < 3)
            {
                await Task.Delay(900, cancellationToken);
            }
        }

        return last!;
    }

    private static async Task RecoverFromEventMenuAsync(
        AutomationContext context,
        GameContextResult eventMenu,
        CancellationToken cancellationToken)
    {
        if (eventMenu.Kind != GameContextKind.EventMenu)
        {
            throw new AutomationFaultException(
                $"Recuperação recusada porque o contexto é {eventMenu.Kind}, não EventMenu.");
        }

        context.Telemetry.Recovery(
            "Menu do evento confirmado; retornando à rua antes de uma nova tentativa.");

        context.Logger.State(
            Workflow,
            "Recuperar",
            "EventMenu confirmado; navegando pelo controle até Sair do Evento.");

        var exitSelected = false;
        for (var navigationAttempt = 1; navigationAttempt <= 2; navigationAttempt++)
        {
            if (navigationAttempt > 1)
            {
                // Reabrir o menu normaliza o foco em Mapa do Mundo. Continuar
                // enviando Right quando a borda não foi reconhecida atravessa
                // as abas do menu e deixa de ser uma recuperação segura.
                await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
                await Task.Delay(700, cancellationToken);
                await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
                await Task.Delay(context.Settings.CrFarm.OutcomeSettleMs, cancellationToken);
                var normalizedMenu = await context.GameContext.DetectAsync(cancellationToken);
                if (normalizedMenu.Kind != GameContextKind.EventMenu)
                {
                    await ThrowUnknownContextAsync(
                        context,
                        "NormalizarMenuEvento",
                        normalizedMenu,
                        cancellationToken);
                }
            }

            await context.Input.TapAsync(GameKey.Right, cancellationToken, 90);
            exitSelected = await context.Vision.HasLimeSelectionAsync(
                // A borda superior do cartão Sair do Evento é muito mais
                // estável que uma faixa vertical diluída pela altura do card.
                new RectangleF(0.72f, 0.232f, 0.16f, 0.018f),
                cancellationToken,
                minimumRatio: 0.08);
            if (exitSelected)
            {
                context.Logger.State(
                    Workflow,
                    "SelecionarSaidaEvento",
                    $"Cartão Sair do Evento focado na tentativa normalizada {navigationAttempt}/2.");
                break;
            }
        }

        if (!exitSelected)
        {
            await ThrowUnknownContextAsync(context, "SelecionarSaidaEvento", eventMenu, cancellationToken);
        }

        _ = await OpenSelectedExitAndConfirmStreetMenuAsync(context, cancellationToken);
        await CloseStreetMenuAsync(context, cancellationToken);
        context.Logger.State(
            Workflow,
            "AguardarNovaTentativa",
            $"Street confirmado; aguardando {context.Settings.CrFarm.RecoveryDelaySeconds} s antes da nova tentativa.");
        await Task.Delay(
            TimeSpan.FromSeconds(Math.Max(1, context.Settings.CrFarm.RecoveryDelaySeconds)),
            cancellationToken);
    }

    private static async Task<GameContextResult> OpenSelectedExitAndConfirmStreetMenuAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
        await Task.Delay(700, cancellationToken);
        var afterExit = await context.GameContext.DetectAsync(cancellationToken);
        if (!HasExitConfirmation(afterExit))
        {
            await Task.Delay(500, cancellationToken);
            afterExit = await context.GameContext.DetectAsync(cancellationToken);
        }

        if (!HasExitConfirmation(afterExit))
        {
            await ThrowUnknownContextAsync(
                context,
                "AguardarConfirmacaoSaidaEvento",
                afterExit,
                cancellationToken);
        }

        return await ConfirmOpenExitAndOpenStreetMenuAsync(
            context,
            afterExit,
            cancellationToken);
    }

    private static async Task<GameContextResult> ConfirmOpenExitAndOpenStreetMenuAsync(
        AutomationContext context,
        GameContextResult afterExit,
        CancellationToken cancellationToken)
    {
        if (!HasExitConfirmation(afterExit))
        {
            throw new AutomationFaultException(
                $"Confirmação de saída recusada porque o contexto é {afterExit.Kind}.");
        }

        for (var confirmationAttempt = 1;
             confirmationAttempt <= 3 && HasExitConfirmation(afterExit);
             confirmationAttempt++)
        {
            var yesSelected = await context.Vision.HasLimeSelectionAsync(
                ExitConfirmationYesSelection,
                cancellationToken,
                minimumRatio: 0.05);
            if (!yesSelected)
            {
                await ThrowUnknownContextAsync(
                    context,
                    "ValidarSimConfirmacaoSaida",
                    afterExit,
                    cancellationToken);
            }

            // "Sim" já é a opção selecionada pelo jogo. Um único A/Enter é
            // validado pela borda verde antes de cada tentativa limitada.
            await context.Input.TapAsync(GameKey.Enter, cancellationToken, 160);
            await Task.Delay(900, cancellationToken);
            afterExit = await context.GameContext.DetectAsync(cancellationToken);
            if (HasExitConfirmation(afterExit))
            {
                context.Logger.Warn(
                    $"A confirmação Sair do Evento permaneceu aberta após {confirmationAttempt}/3; repetindo A de forma limitada.");
            }
        }

        if (HasExitConfirmation(afterExit))
        {
            await ThrowUnknownContextAsync(context, "ConfirmarSaidaEvento", afterExit, cancellationToken);
        }

        // O carregamento é intencionalmente ocioso. Uma única sonda por ESC é
        // feita depois, evitando OCR/polling contínuo durante a transição.
        await Task.Delay(PreRaceExitSettleMilliseconds, cancellationToken);
        var streetMenu = await OpenAndDetectOutcomeMenuAsync(context, cancellationToken);
        if (streetMenu.Kind != GameContextKind.StreetMenu)
        {
            await ThrowUnknownContextAsync(context, "ConfirmarRetornoRua", streetMenu, cancellationToken);
        }

        return streetMenu;
    }

    private static async Task CloseStreetMenuAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
        await Task.Delay(700, cancellationToken);
    }

    private static async Task<long?> TryReadCreditsAsync(
        AutomationContext context,
        string state,
        long? maximumPlausibleValue,
        CancellationToken cancellationToken)
    {
        try
        {
            var credits = await context.Vision.ReadYellowNumberAsync(
                new RectangleF(0.68f, 0.015f, 0.19f, 0.16f),
                checked((int)Math.Clamp(
                    maximumPlausibleValue ?? 999_999_999,
                    0,
                    999_999_999)),
                Workflow,
                state,
                cancellationToken);
            return credits;
        }
        catch (Exception exception) when (
            exception is CalibrationRequiredException or AutomationFaultException or IOException)
        {
            context.Logger.Warn(
                $"{state}: saldo de CR não pôde ser confirmado sem interromper a captura: {exception.Message}");
            return null;
        }
    }

    private static async Task<long?> TryReadConfirmedCreditsAsync(
        AutomationContext context,
        string state,
        long? maximumPlausibleValue,
        CancellationToken cancellationToken)
    {
        var readings = new List<long>(3);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var reading = await TryReadCreditsAsync(
                context,
                $"{state}{attempt}",
                maximumPlausibleValue,
                cancellationToken);
            if (reading is not null)
            {
                readings.Add(reading.Value);
            }

            var confirmed = readings
                .GroupBy(value => value)
                .FirstOrDefault(group => group.Count() >= 2);
            if (confirmed is not null)
            {
                var value = confirmed.Key;
                context.Resources.SetCredits(value, estimated: false);
                context.Logger.State(
                    Workflow,
                    state,
                    $"Consenso confirmado: [{string.Join(", ", readings.Select(item => item.ToString("N0")))}]; " +
                    $"valor {value:N0}.");
                return value;
            }

            if (attempt < 3)
            {
                await Task.Delay(600, cancellationToken);
            }
        }

        context.Logger.Warn(
            $"{state}: não houve consenso exato em até 3 leituras " +
            $"([{string.Join(", ", readings.Select(value => value.ToString("N0")))}]).");
        return null;
    }

    private static bool HasExitConfirmation(GameContextResult result)
    {
        var text = GameVisionService.Normalize(result.Document.Text);
        return result.Kind == GameContextKind.EventExitConfirmation ||
               (text.Contains("SAIR DO EVENTO", StringComparison.Ordinal) ||
                text.Contains("SAIR DA CORRIDA", StringComparison.Ordinal)) &&
               text.Contains("SIM", StringComparison.Ordinal) &&
               text.Contains("NAO", StringComparison.Ordinal);
    }

    private static async Task PulseAsync(
        AutomationContext context,
        GameKey key,
        int holdMilliseconds,
        int waitAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        await context.Input.HoldAsync(key, holdMilliseconds, cancellationToken);
        if (waitAfterMilliseconds > 0)
        {
            await Task.Delay(waitAfterMilliseconds, cancellationToken);
        }
    }

    private static async Task PrecisePulseAsync(
        AutomationContext context,
        GameKey key,
        int holdMilliseconds,
        int waitAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        await context.Input.HoldPreciselyAsync(key, holdMilliseconds, cancellationToken);
        if (waitAfterMilliseconds > 0)
        {
            await context.Input.DelayPreciselyAsync(waitAfterMilliseconds, cancellationToken);
        }
    }

    private static IReadOnlyList<string> CompleteAttemptSafely(
        AutomationContext context,
        CrFarmAttempt attempt,
        CrAttemptGroundTruth groundTruth,
        string comparison,
        GameContextKind menu)
    {
        try
        {
            return context.CrFarmSamples.CompleteAttempt(
                attempt,
                groundTruth,
                comparison,
                menu);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Warn(
                $"Ground truth {groundTruth} confirmado, mas não foi possível consolidar a amostra: {exception.Message}");
            return [];
        }
    }

    private static string Compare(
        CrPositionLabel prediction,
        CrAttemptGroundTruth groundTruth) =>
        (prediction, groundTruth) switch
        {
            (CrPositionLabel.Valid, CrAttemptGroundTruth.Valid) => "TruePositive",
            (CrPositionLabel.Valid, CrAttemptGroundTruth.Invalid) => "FalsePositive",
            (CrPositionLabel.Invalid, CrAttemptGroundTruth.Valid) => "FalseNegative",
            (CrPositionLabel.Invalid, CrAttemptGroundTruth.Invalid) => "TrueNegative",
            _ => "UnknownPrediction"
        };

    private static void EnsureRecoveryBudget(
        AutomationContext context,
        int consecutiveRecoveries)
    {
        if (consecutiveRecoveries > Math.Max(1, context.Settings.CrFarm.MaximumRecoveries))
        {
            throw new CalibrationRequiredException(
                $"O Farm de CR atingiu o limite de {context.Settings.CrFarm.MaximumRecoveries} " +
                "recuperações consecutivas e parou sem enviar novas entradas.");
        }
    }

    private static async Task ThrowUnknownContextAsync(
        AutomationContext context,
        string state,
        GameContextResult observed,
        CancellationToken cancellationToken)
    {
        using var frame = await context.Capture.CaptureAsync(cancellationToken);
        var path = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, state);
        throw new CalibrationRequiredException(
            $"Contexto inseguro em {state}: {observed.Kind} ({observed.Evidence}). Diagnóstico: {path}");
    }

}
