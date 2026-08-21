using System.Drawing;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed class SpFarmWorkflow : IMacroWorkflow
{
    private static readonly RectangleF ChallengeCardOcrRegion = new(0.245f, 0.17f, 0.255f, 0.70f);
    private static readonly RectangleF ChallengeCardBodyRegion = new(0.255f, 0.19f, 0.235f, 0.65f);
    private static readonly RectangleF ChallengeCardLeftBorderRegion = new(0.248f, 0.176f, 0.010f, 0.688f);
    private static readonly RectangleF ChallengeCardRightBorderRegion = new(0.487f, 0.176f, 0.010f, 0.688f);
    private static readonly RectangleF ChallengeCardTopBorderRegion = new(0.248f, 0.176f, 0.249f, 0.018f);
    private static readonly RectangleF ChallengeCardBottomBorderRegion = new(0.248f, 0.841f, 0.249f, 0.022f);

    public MacroKind Kind => MacroKind.FarmarSp;

    public async Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        int? startingSkillPoints = null;
        int? targetSkillPoints = null;
        int? targetRaceCount = null;
        if (request.TargetSkillPoints is { } requestedTarget)
        {
            if (requestedTarget is < 1 or > 999)
            {
                throw new AutomationFaultException($"Meta de SP inválida para o farm integrado: {requestedTarget}.");
            }

            var tracked = context.Resources.Current;
            if (tracked.SkillPoints is not { } exactPoints || tracked.SkillPointsEstimated)
            {
                throw new CalibrationRequiredException(
                    "O Farm de SP integrado exige um saldo inicial exato lido na Maestria. Nenhuma corrida foi iniciada.");
            }

            if (exactPoints >= requestedTarget)
            {
                context.Logger.State(workflow, "MetaJaAtingida", $"Saldo exato {exactPoints} SP já atende à meta {requestedTarget}.");
                return;
            }

            var pointsPerRace = context.Settings.Sp.PointsPerRace;
            if (pointsPerRace is < 1 or > 999)
            {
                throw new AutomationFaultException(
                    "PointsPerRace deve estar entre 1 e 999 para calcular a meta integrada.");
            }

            startingSkillPoints = exactPoints;
            targetSkillPoints = requestedTarget;
            targetRaceCount = (int)Math.Ceiling((requestedTarget - exactPoints) / (double)pointsPerRace);
        }

        context.Logger.State(
            workflow,
            "Inicio",
            "Início na rua: o BOT confirma ou seleciona o Impreza 22B antes da corrida. " +
            "Assistência Total permanece um pré-requisito informado, sem verificação dinâmica. " +
            (targetRaceCount is { } races
                ? $"Meta integrada: {targetSkillPoints} SP a partir de {startingSkillPoints}; " +
                  $"até {races} corrida(s), confirmadas visualmente antes da contagem."
                : "Execução contínua até F8, sem estimar o saldo total de SP."));
        context.Telemetry.UpdateStage(
            "Validando carro",
            "Confirmando o Subaru Impreza 22B antes de abrir o EventLab.");

        var carSelector = new RequiredCarSelector(context);
        await carSelector.EnsureSelectedAsync(RequiredCarDefinition.SkillPoints, cancellationToken);
        await OpenEventLabChallengeWithVisionAsync(context, cancellationToken);
        await RunRaceLoopAsync(
            context,
            startingSkillPoints,
            targetSkillPoints,
            targetRaceCount,
            cancellationToken);
    }

    private static async Task OpenEventLabChallengeAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        context.Logger.State(
            workflow,
            "EntrarNoDesafio",
            "Executando o fluxo Pulover calibrado para abrir o desafio 122697651, sem OCR intermediário.");

        await Task.Delay(333, cancellationToken);
        await Task.Delay(1_218, cancellationToken);
        await PulseTimedAsync(context, GameKey.Menu, 141, 750, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 78, 188, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 93, 141, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 78, 422, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 94, 703, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 109, 672, cancellationToken);
        await PulseTimedAsync(context, GameKey.Down, 172, 422, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 109, 735, cancellationToken);
        await PulseTimedAsync(context, GameKey.Backspace, 109, 562, cancellationToken);
        await PulseTimedAsync(context, GameKey.Up, 109, 453, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 125, 1_235, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad1, 62, 141, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad2, 109, 157, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad2, 78, 828, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad6, 78, 141, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad9, 125, 109, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad7, 62, 329, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad6, 78, 156, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad5, 94, 93, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad1, 110, 1_094, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 78, 906, cancellationToken);
        await PulseTimedAsync(context, GameKey.Down, 109, 172, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 94, 1_609, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 172, 0, cancellationToken);
    }

    private static async Task PulseTimedAsync(
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

    private static async Task OpenEventLabChallengeWithVisionAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        context.Logger.State(workflow, "AbrirMenuRua", "Abrindo o menu da rua.");
        context.Telemetry.UpdateStage(
            "Abrindo menu da rua",
            "Confirmando visualmente o menu antes de navegar até o EventLab.");
        var pauseMenuOpened = await context.Vision.ContainsAnyTextAsync(
            ["CENTRAL CRIATIVA", "MEU HORIZON", "ONLINE", "MAPA DO MUNDO", "CONFIGURAÇÕES", "CONFIGURACOES"],
            cancellationToken);
        if (pauseMenuOpened)
        {
            context.Logger.State(
                workflow,
                "AbrirMenuRua",
                "O seletor de carro já deixou o menu da rua aberto e confirmado.");
        }

        for (var attempt = 1; attempt <= 3 && !pauseMenuOpened; attempt++)
        {
            await context.Input.TapAsync(GameKey.Menu, cancellationToken);
            var attemptDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < attemptDeadline && !pauseMenuOpened)
            {
                await Task.Delay(700, cancellationToken);
                pauseMenuOpened = await context.Vision.ContainsAnyTextAsync(
                    ["CENTRAL CRIATIVA", "MEU HORIZON", "ONLINE", "MAPA DO MUNDO", "CONFIGURAÇÕES", "CONFIGURACOES"],
                    cancellationToken);
            }
            if (!pauseMenuOpened)
            {
                if (await context.Vision.ContainsAnyTextAsync(
                        ["ENTRAR NA CASA", "CASA EM TÓQUIO", "CASA EM TOQUIO"],
                        cancellationToken))
                {
                    context.Logger.State(
                        workflow,
                        "SairDaEntradaDaCasa",
                        "A entrada da casa interceptou Esc; avançando com RT antes da próxima tentativa.");
                    await context.Input.HoldAsync(GameKey.W, 2_000, cancellationToken);
                    await Task.Delay(2_500, cancellationToken);
                }

                context.Logger.State(
                    workflow,
                    "AbrirMenuRua",
                    $"O menu ainda não apareceu após Esc ({attempt}/3); tentando novamente.");
            }
        }

        if (!pauseMenuOpened)
        {
            _ = await context.Vision.WaitForAnyTextAsync(
                workflow,
                "AbrirMenuRuaConfirmado",
                ["CENTRAL CRIATIVA", "MAPA DO MUNDO", "CONFIGURAÇÕES", "CONFIGURACOES"],
                cancellationToken,
                TimeSpan.FromSeconds(8));
        }

        context.Logger.State(
            workflow,
            "CentralCriativa",
            "Procurando a aba Central Criativa com LB, sem mover o foco do Windows.");
        context.Telemetry.UpdateStage(
            "Central Criativa",
            "Localizando a aba do EventLab por reconhecimento visual.");
        var creativeTabOpen = false;
        for (var attempt = 1; attempt <= 6 && !creativeTabOpen; attempt++)
        {
            creativeTabOpen = await context.Vision.ContainsAnyTextAsync(
                ["CRIAR E NAVEGAR POR EVENTOS", "MINHA CENTRAL CRIATIVA"],
                cancellationToken);
            if (creativeTabOpen)
            {
                break;
            }

            context.Logger.State(workflow, "CentralCriativa", $"Avançando uma aba com LB ({attempt}/6).");
            await context.Input.TapAsync(GameKey.Shift, cancellationToken, 60);
            await Task.Delay(700, cancellationToken);
        }

        if (!creativeTabOpen)
        {
            _ = await context.Vision.WaitForAnyTextAsync(
                workflow,
                "CentralCriativaConfirmada",
                ["CRIAR E NAVEGAR POR EVENTOS", "MINHA CENTRAL CRIATIVA"],
                cancellationToken,
                TimeSpan.FromSeconds(5));
        }

        context.Logger.State(workflow, "EventLab", "O banner EventLab está selecionado; abrindo com A.");
        context.Telemetry.UpdateStage(
            "EventLab",
            "Abrindo a lista de desafios e preparando a busca pelo código configurado.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "EventLabConfirmado",
            ["LABORATÓRIO DE EVENTOS", "LABORATORIO DE EVENTOS", "JOGAR DESAFIOS"],
            cancellationToken,
            TimeSpan.FromSeconds(15));
        context.Logger.State(
            workflow,
            "JogarDesafios",
            "A tela inicia em Jogar Evento; descendo uma posição e confirmando Jogar Desafios.");
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "JogarDesafiosConfirmado",
            ["OPÇÕES DO DESAFIO", "OPCOES DO DESAFIO"],
            cancellationToken);

        context.Logger.State(workflow, "Buscar", "Abrindo filtros com Backspace.");
        context.Telemetry.UpdateStage(
            "Buscando desafio",
            "Preenchendo e confirmando o código de compartilhamento.");
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "CodigoCompartilhamento",
            ["CÓD. DE COMPARTILHAMENTO", "COD. DE COMPARTILHAMENTO", "CÓDIGO DE COMPARTILHAMENTO"],
            cancellationToken,
            TimeSpan.FromSeconds(8));
        context.Logger.State(
            workflow,
            "CodigoCompartilhamento",
            "Localizando visualmente a borda verde do campo Cód. de Compartilhamento.");
        // Na busca de Desafios, o campo fica logo acima de Confirmar. Use
        // apenas a lateral esquerda da linha para não confundir a borda
        // horizontal verde do botão Confirmar quando ele está selecionado.
        var codeRow = new RectangleF(0.275f, 0.605f, 0.03f, 0.045f);
        var codeSelected = await context.Vision.HasLimeSelectionAsync(
            codeRow,
            cancellationToken);
        for (var step = 1; step <= 12 && !codeSelected; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            codeSelected = await context.Vision.HasLimeSelectionAsync(codeRow, cancellationToken);
        }
        for (var step = 1; step <= 12 && !codeSelected; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            codeSelected = await context.Vision.HasLimeSelectionAsync(codeRow, cancellationToken);
        }
        if (!codeSelected)
        {
            throw new CalibrationRequiredException(
                "Não foi possível posicionar a seleção em Cód. de Compartilhamento pelo controle virtual.");
        }
        context.Logger.State(
            workflow,
            "CodigoCompartilhamento",
            "Borda verde confirmada no campo; abrindo com A.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "CampoCodigoPronto",
            ["DIGITE O CÓDIGO DE COMPARTILHAMENTO", "DIGITE O CODIGO DE COMPARTILHAMENTO"],
            cancellationToken,
            TimeSpan.FromSeconds(8));
        await Task.Delay(500, cancellationToken);
        await context.Input.TypeTextAsync(context.Settings.Sp.ShareCode, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        context.Logger.State(
            workflow,
            "ConfirmarBusca",
            "Voltando ao filtro, descendo uma posição até Confirmar e selecionando com A.");
        await Task.Delay(900, cancellationToken);
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ResultadoBusca",
            ["ALWAYS WIN2", "RESULTADOS"],
            cancellationToken,
            TimeSpan.FromSeconds(20));
        var initialCard = await CaptureStableChallengeCardAsync(context, cancellationToken);
        if (!initialCard.IsStable)
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, workflow, "ConfirmarCardDesafio");
            throw new CalibrationRequiredException(
                "A busca terminou, mas o card selecionado de Always Win2 não foi confirmado em duas de três capturas OCR/CV sem conflito. " +
                $"Nenhuma seleção foi enviada. Evidência: {initialCard.Evidence}. Diagnóstico: {diagnostic}");
        }

        context.Logger.State(
            workflow,
            "SelecionarEvento",
            "Grade e card selecionado de Always Win2 confirmados em duas de três capturas; selecionando com A.");
        context.Telemetry.UpdateStage(
            "Carregando corrida",
            "Desafio localizado; aguardando a apresentação e a contagem regressiva.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
        await Task.Delay(1_600, cancellationToken);

        var residualCard = await CaptureStableChallengeCardAsync(context, cancellationToken);
        if (residualCard.IsStable)
        {
            context.Logger.State(
                workflow,
                "SelecionarEventoNovamente",
                "A mesma grade e o mesmo card permaneceram confirmados após o primeiro A; repetindo a seleção uma única vez.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
        }

        context.Logger.State(
            workflow,
            "Cinematica",
            "A seleção limitada do evento terminou; o HUD ainda será confirmado antes de qualquer acelerador.");
    }

    private static async Task<ChallengeCardConsensus> CaptureStableChallengeCardAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var matches = 0;
        var conflicts = 0;
        var consecutiveMatches = 0;
        ChallengeCardCheckpoint last = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await context.Vision.AnalyzeScreenWithScaledRegionsAsync(
                [ChallengeCardOcrRegion, SpRaceVision.HudRegion],
                requestedScale: 3,
                AnalyzeChallengeCardCheckpoint,
                cancellationToken);
            if (last.IsConflict)
            {
                conflicts++;
                consecutiveMatches = 0;
            }
            else if (last.IsMatch)
            {
                matches++;
                consecutiveMatches++;
            }
            else
            {
                consecutiveMatches = 0;
            }

            if (attempt < 3)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        return new ChallengeCardConsensus(
            consecutiveMatches >= 2 && conflicts == 0,
            $"confirmações={matches}/3, consecutivas finais={consecutiveMatches}, " +
            $"conflitos={conflicts}/3, último={last.Evidence}");
    }

    private static ChallengeCardCheckpoint AnalyzeChallengeCardCheckpoint(
        Bitmap bitmap,
        OcrDocument document,
        IReadOnlyList<OcrDocument> scaledRegions)
    {
        if (scaledRegions.Count != 2)
        {
            throw new ArgumentException(
                "O checkpoint do card de SP exige as regiões do card e do HUD na mesma captura.",
                nameof(scaledRegions));
        }

        var screenText = GameVisionService.Normalize(document.Text);
        var cardText = GameVisionService.Normalize(scaledRegions[0].Text);
        var hudText = GameVisionService.Normalize(scaledRegions[1].Text);
        var compactCard = cardText.Replace(" ", string.Empty, StringComparison.Ordinal);
        var challengeGrid = screenText.Contains("DESAFIOS", StringComparison.Ordinal) &&
                            (screenText.Contains("MEUS DESAFIOS", StringComparison.Ordinal) ||
                             screenText.Contains("RESULTADOS DA BUSCA", StringComparison.Ordinal));
        var targetCard = compactCard.Contains("ALWAYSWIN2", StringComparison.Ordinal) &&
                         compactCard.Contains("DEFINIRROTA", StringComparison.Ordinal);
        var yellowBody = ColorFillRatio(bitmap, ChallengeCardBodyRegion, IsChallengeCardYellow);
        var leftBorder = ColorFillRatio(bitmap, ChallengeCardLeftBorderRegion, IsChallengeCardLime);
        var rightBorder = ColorFillRatio(bitmap, ChallengeCardRightBorderRegion, IsChallengeCardLime);
        var topBorder = ColorFillRatio(bitmap, ChallengeCardTopBorderRegion, IsChallengeCardLime);
        var bottomBorder = ColorFillRatio(bitmap, ChallengeCardBottomBorderRegion, IsChallengeCardLime);
        var selectedCard = yellowBody >= 0.50 &&
                           leftBorder >= 0.10 &&
                           rightBorder >= 0.10 &&
                           topBorder >= 0.12 &&
                           bottomBorder >= 0.08;
        var conflict = SpRaceVision.IsActiveHud(hudText) ||
                       SpRaceVision.HasRetryText(screenText) ||
                       screenText.Contains("SAIR", StringComparison.Ordinal) ||
                       screenText.Contains("DESAFIO CONCLUIDO", StringComparison.Ordinal) ||
                       screenText.Contains("DESAFIO NAO CONCLUIDO", StringComparison.Ordinal);
        return new ChallengeCardCheckpoint(
            challengeGrid && targetCard && selectedCard && !conflict,
            conflict,
            $"grade={challengeGrid}, alvo={targetCard}, selecionado={selectedCard}, " +
            $"amarelo={yellowBody:P1}, bordas={leftBorder:P1}/{rightBorder:P1}/{topBorder:P1}/{bottomBorder:P1}, " +
            $"card='{cardText}', hud='{hudText}'");
    }

    private static double ColorFillRatio(
        Bitmap bitmap,
        RectangleF normalizedRegion,
        Func<Color, bool> matches)
    {
        var left = Math.Clamp((int)Math.Round(bitmap.Width * normalizedRegion.Left), 0, bitmap.Width - 1);
        var top = Math.Clamp((int)Math.Round(bitmap.Height * normalizedRegion.Top), 0, bitmap.Height - 1);
        var right = Math.Clamp((int)Math.Round(bitmap.Width * normalizedRegion.Right), left + 1, bitmap.Width);
        var bottom = Math.Clamp((int)Math.Round(bitmap.Height * normalizedRegion.Bottom), top + 1, bitmap.Height);
        var matching = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                sampled++;
                if (matches(bitmap.GetPixel(x, y)))
                {
                    matching++;
                }
            }
        }

        return sampled == 0 ? 0 : matching / (double)sampled;
    }

    private static bool IsChallengeCardLime(Color color) =>
        color.G >= 190 &&
        color.G >= color.R * 1.15 &&
        color.G >= color.B * 1.50;

    private static bool IsChallengeCardYellow(Color color) =>
        color.R >= 170 &&
        color.G >= 120 &&
        color.B <= 80 &&
        color.R >= color.G * 0.90;

    private readonly record struct ChallengeCardCheckpoint(
        bool IsMatch,
        bool IsConflict,
        string Evidence);

    private readonly record struct ChallengeCardConsensus(
        bool IsStable,
        string Evidence);

    private static async Task RunRaceLoopAsync(
        AutomationContext context,
        int? startingSkillPoints,
        int? targetSkillPoints,
        int? targetRaceCount,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        var earnedPoints = 0;
        var trackedSkillPoints = startingSkillPoints ?? context.Resources.Current.SkillPoints;
        var race = 1;
        var raceStartAlreadyConfirmed = false;
        var consecutiveFailedAttempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Telemetry.UpdateStage(
                "Corrida em andamento",
                $"Corrida {race}: executando a sequência calibrada.");
            context.Logger.State(
                workflow,
                "Corrida",
                $"Corrida {race}: aguardando o HUD específico antes de acelerar.");
            if (!raceStartAlreadyConfirmed)
            {
                await ConfirmInitialRaceStartAsync(context, cancellationToken);
            }

            raceStartAlreadyConfirmed = false;
            SpRaceResultKind raceResult;
            await context.Input.KeyDownAsync(GameKey.W, cancellationToken);
            try
            {
                // A rota calibrada costuma terminar por volta de 37,5 s. Mantenha
                // o acelerador pressionado enquanto o resultado é observado para
                // não transformar uma máquina lenta numa derrota artificial.
                await Task.Delay(35_000, cancellationToken);
                raceResult = await ConfirmCompletedRaceAsync(context, cancellationToken);
            }
            finally
            {
                await context.Input.KeyUpAsync(GameKey.W, CancellationToken.None);
            }

            if (raceResult == SpRaceResultKind.Failure)
            {
                consecutiveFailedAttempts++;
                if (consecutiveFailedAttempts >= 3)
                {
                    throw new CalibrationRequiredException(
                        "O desafio falhou em três tentativas consecutivas confirmadas. " +
                        "Nenhum SP dessas tentativas foi contabilizado; revise o carro e as assistências antes de continuar.");
                }

                context.Logger.State(
                    workflow,
                    "DesafioNaoConcluido",
                    $"Tentativa {consecutiveFailedAttempts}/3 não concluída; nenhum SP foi contabilizado. " +
                    "A ação Tentar Novamente foi confirmada e será acionada com A.");
                await RestartRaceAsync(
                    context,
                    SpRaceResultKind.Failure,
                    GameKey.Enter,
                    cancellationToken);
                raceStartAlreadyConfirmed = true;
                continue;
            }

            consecutiveFailedAttempts = 0;
            earnedPoints += context.Settings.Sp.PointsPerRace;
            var counterEvidence = "saldo total indisponível sem uma leitura inicial";
            if (trackedSkillPoints is { } previousSkillPoints)
            {
                trackedSkillPoints = Math.Min(
                    999,
                    previousSkillPoints + context.Settings.Sp.PointsPerRace);
                context.Resources.SetSkillPoints(trackedSkillPoints.Value, estimated: true);
                counterEvidence = $"contador estimado atualizado para {trackedSkillPoints.Value} SP";
            }

            context.Logger.State(
                workflow,
                "Resultado",
                $"Corrida {race} contabilizada; {earnedPoints} SP ganhos nesta execução; {counterEvidence}.");

            if (targetRaceCount is { } maximumRaces && race >= maximumRaces)
            {
                var projectedPoints = Math.Min(999, startingSkillPoints!.Value + earnedPoints);
                if (projectedPoints < targetSkillPoints!.Value)
                {
                    throw new AutomationFaultException(
                        $"O limite calculado terminou em {projectedPoints} SP, abaixo da meta {targetSkillPoints.Value}.");
                }

                context.Logger.State(
                    workflow,
                    "MetaCalculada",
                    $"{race} corrida(s) confirmada(s); projeção limitada pelo teto: {projectedPoints} SP. " +
                    "Saindo do evento para reler o saldo exato na Maestria.");
                await new GameNavigator(context).ExitCurrentEventToStreetMenuAsync(
                    workflow,
                    cancellationToken,
                    allowConfirmedResultProbe: true);
                return;
            }

            context.Logger.State(
                workflow,
                "TentarNovamente",
                "Sucesso e ação Tentar Novamente confirmados; pressionando B/Esc para iniciar a próxima corrida.");
            context.Telemetry.UpdateStage(
                "Preparando nova corrida",
                $"Corrida {race}: retornando para iniciar a próxima repetição.");
            await RestartRaceAsync(
                context,
                SpRaceResultKind.Success,
                GameKey.Escape,
                cancellationToken);
            raceStartAlreadyConfirmed = true;
            race++;
        }
    }

    private static async Task<SpRaceResultKind> ConfirmCompletedRaceAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var recentKinds = new Queue<SpRaceResultKind>(3);
        var observed = new Queue<string>(5);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var checkpoint = await CaptureRaceResultAsync(context, cancellationToken);
            var kind = checkpoint.Kind;
            var observation =
                $"{kind}: title='{checkpoint.TitleText}'; actions='{checkpoint.ActionsText}'; " +
                $"retry={checkpoint.Evidence.RetryVisible}; exit={checkpoint.Evidence.ExitVisible}; " +
                $"A={checkpoint.Evidence.RetryGreenRatio:P1}; " +
                $"B-repetir={checkpoint.Evidence.SuccessRetryRedRatio:P1}; " +
                $"B-sair={checkpoint.Evidence.ExitRedRatio:P1}";
            if (observed.Count == 5)
            {
                _ = observed.Dequeue();
            }

            observed.Enqueue(observation);
            if (recentKinds.Count == 3)
            {
                _ = recentKinds.Dequeue();
            }

            recentKinds.Enqueue(kind);
            if (recentKinds.Count < 3)
            {
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var successes = recentKinds.Count(item => item == SpRaceResultKind.Success);
            var failures = recentKinds.Count(item => item == SpRaceResultKind.Failure);
            var stableKind = successes >= 2 && failures == 0
                ? SpRaceResultKind.Success
                : failures >= 2 && successes == 0
                    ? SpRaceResultKind.Failure
                    : SpRaceResultKind.Unknown;
            if (stableKind != SpRaceResultKind.Unknown)
            {
                context.Logger.State(
                    "FarmarSP",
                    stableKind == SpRaceResultKind.Success ? "ResultadoConfirmado" : "FalhaConfirmada",
                    stableKind == SpRaceResultKind.Success
                        ? "Desafio concluído e ação Tentar Novamente confirmados em duas de três capturas OCR/CV sem conflito."
                        : "Desafio não concluído e retry por A confirmados em duas de três capturas OCR/CV sem conflito.");
                return stableKind;
            }

            await Task.Delay(500, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, "FarmarSP", "ConfirmarResultadoCorrida");
        throw new CalibrationRequiredException(
            "A corrida permaneceu acelerando, mas sucesso ou falha não foram distinguidos em duas de três capturas sem conflito durante 45 segundos. " +
            $"Nenhum SP foi contabilizado para a meta. OCR: '{string.Join(" | ", observed)}'. " +
            $"Diagnóstico local: {diagnostic}");
    }

    private static async Task RestartRaceAsync(
        AutomationContext context,
        SpRaceResultKind expectedResult,
        GameKey retryKey,
        CancellationToken cancellationToken)
    {
        for (var inputAttempt = 1; inputAttempt <= 2; inputAttempt++)
        {
            await context.Input.TapAsync(retryKey, cancellationToken, 120);
            if (await ConfirmRaceRestartTransitionAsync(
                    context,
                    expectedResult,
                    allowSameResultRetry: inputAttempt == 1,
                    cancellationToken))
            {
                return;
            }

            context.Logger.Warn(
                $"O retry {inputAttempt}/2 foi absorvido; o mesmo resultado {expectedResult} " +
                "permanece confirmado e a ação será repetida uma única vez.");
        }

        throw new CalibrationRequiredException(
            "O retry foi confirmado visualmente, mas duas entradas limitadas não iniciaram uma nova corrida. " +
            "Nenhum resultado será contabilizado novamente.");
    }

    private static async Task<bool> ConfirmRaceRestartTransitionAsync(
        AutomationContext context,
        SpRaceResultKind expectedResult,
        bool allowSameResultRetry,
        CancellationToken cancellationToken)
    {
        var consecutiveEventFrames = 0;
        var observed = new Queue<string>(5);
        var attempt = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            var hud = await context.Vision.ReadScaledRegionAsync(
                SpRaceVision.HudRegion,
                requestedScale: 3,
                cancellationToken);
            var normalized = GameVisionService.Normalize(hud.Text);
            if (observed.Count == 5)
            {
                _ = observed.Dequeue();
            }

            observed.Enqueue(normalized);
            if (SpRaceVision.IsActiveHud(normalized))
            {
                consecutiveEventFrames++;
                if (consecutiveEventFrames >= 2)
                {
                    context.Logger.State(
                        "FarmarSP",
                        "NovaCorridaIniciada",
                        "A tela de conclusão desapareceu e o HUD específico da corrida foi confirmado em duas capturas consecutivas; acelerando a nova corrida.");
                    return true;
                }
            }
            else
            {
                consecutiveEventFrames = 0;
            }

            if (allowSameResultRetry && attempt == 6 &&
                await HasStableRaceResultAsync(context, expectedResult, cancellationToken))
            {
                return false;
            }

            await Task.Delay(500, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, "FarmarSP", "ConfirmarNovaCorrida");
        throw new CalibrationRequiredException(
            "A tentativa de repetir foi enviada, mas o HUD da nova corrida não apareceu em duas capturas consecutivas dentro de 45 segundos. " +
            "A mesma corrida não será contabilizada novamente. " +
            $"OCR: '{string.Join(" | ", observed)}'. Diagnóstico local: {diagnostic}");
    }

    private static async Task<bool> HasStableRaceResultAsync(
        AutomationContext context,
        SpRaceResultKind expectedResult,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        var conflicts = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var checkpoint = await CaptureRaceResultAsync(context, cancellationToken);
            if (checkpoint.Kind == expectedResult)
            {
                confirmations++;
            }
            else if (checkpoint.Kind != SpRaceResultKind.Unknown)
            {
                conflicts++;
            }

            if (attempt < 3)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        return confirmations >= 2 && conflicts == 0;
    }

    private static Task<SpRaceResultObservation> CaptureRaceResultAsync(
        AutomationContext context,
        CancellationToken cancellationToken) =>
        context.Vision.AnalyzeScreenWithScaledRegionsAsync(
            SpRaceVision.ResultOcrRegions,
            requestedScale: 3,
            SpRaceVision.AnalyzeResultCheckpoint,
            cancellationToken);

    private static async Task ConfirmInitialRaceStartAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var recentHudFrames = new Queue<bool>(3);
        var observed = new Queue<string>(5);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            var hud = await context.Vision.ReadScaledRegionAsync(
                SpRaceVision.HudRegion,
                requestedScale: 3,
                cancellationToken);
            var normalized = GameVisionService.Normalize(hud.Text);
            if (observed.Count == 5)
            {
                _ = observed.Dequeue();
            }

            observed.Enqueue(normalized);
            var hudActive = SpRaceVision.IsActiveHud(normalized);
            recentHudFrames.Enqueue(hudActive);
            if (recentHudFrames.Count > 3)
            {
                _ = recentHudFrames.Dequeue();
            }

            if (recentHudFrames.Count == 3 &&
                hudActive &&
                recentHudFrames.Count(active => active) >= 2)
            {
                context.Logger.State(
                    "FarmarSP",
                    "CorridaIniciada",
                    "HUD Tempo Restante/Atual confirmado em duas de três capturas, com a mais recente positiva; acelerando imediatamente.");
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, "FarmarSP", "ConfirmarInicioCorrida");
        throw new CalibrationRequiredException(
            "O desafio foi aberto, mas o HUD Tempo Restante/Atual não apareceu " +
            "em duas de três capturas dentro de três minutos. " +
            $"Nenhum acelerador foi acionado. OCR: '{string.Join(" | ", observed)}'. Diagnóstico: {diagnostic}");
    }
}
