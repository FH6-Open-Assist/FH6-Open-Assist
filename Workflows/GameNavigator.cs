using System.Drawing;
using System.Text.RegularExpressions;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed record MasterySnapshot(int SkillPoints, bool SubaruSelected, string OcrText);

public sealed class GameNavigator(AutomationContext context)
{
    private const string Workflow = "Navegação";
    private const int MasterySkillPointsOcrScale = 3;
    private const int MaximumPauseTabMoves = 6;
    private const int EventExitSettleMilliseconds = 12_000;
    private const double ActiveTabDarkRatio = 0.42;
    private const double ActiveTabLimeRatio = 0.04;
    private const double PauseTabUnderlineStartRatio = 0.60;
    private const double PauseTabUnderlineLimeRatio = 0.60;
    private const double TravelCardOutlineRatio = 0.78;
    private const double WelcomeContinueOutlineRatio = 0.80;
    private static readonly RectangleF TravelYesRegion = new(0.318f, 0.508f, 0.360f, 0.064f);
    private static readonly RectangleF EventExitCardFocusRegion = new(0.72f, 0.232f, 0.16f, 0.018f);
    private static readonly RectangleF EventExitYesRegion = new(0.32f, 0.51f, 0.36f, 0.06f);
    private static readonly RectangleF MasterySkillPointsRegion = new(0.155f, 0.842f, 0.229f, 0.050f);
    private static readonly RectangleF WelcomeContinueRegion = new(0.024f, 0.771f, 0.211f, 0.058f);
    private static readonly string[] GameReadyAliases =
    [
        "DIRIGIR",
        "MEU HORIZON",
        "MAPA DO MUNDO",
        "CENTRAL CRIATIVA",
        "CONTROLE DESCONECTADO"
    ];
    private static readonly EventChallengeRatingOptionDefinition[] EventChallengeRatingOptions =
    [
        new(EventChallengeRatingOption.Curtir, "CURTIR", new RectangleF(0.31f, 0.495f, 0.38f, 0.075f)),
        new(EventChallengeRatingOption.NaoGostei, "NAOGOSTEI", new RectangleF(0.31f, 0.548f, 0.38f, 0.075f)),
        new(EventChallengeRatingOption.Cancelar, "CANCELAR", new RectangleF(0.31f, 0.597f, 0.38f, 0.075f))
    ];
    private static readonly string[] TravelCardAliases =
    [
        "VIAGEM RAPIDA PARA CASA",
        "VIAJAR PARA CASA",
        "VOLTAR PARA CASA"
    ];
    private static readonly string[] TravelConfirmationTitleAliases =
    [
        "VIAJAR PARA CASA",
        "VIAGEM RAPIDA ATE CASA"
    ];
    private static readonly string[] PhotoModeExitQuestionAliases =
    [
        "QUER MESMO SAIR DO MODO FOTO",
        "QUER SAIR DO MODO FOTO"
    ];
    private static readonly PauseTabDefinition[] PauseTabs =
    [
        new("CAMPANHA", new RectangleF(0.238f, 0.178f, 0.093f, 0.052f)),
        new("CARROS", new RectangleF(0.331f, 0.178f, 0.074f, 0.052f)),
        new("MEU HORIZON", new RectangleF(0.405f, 0.178f, 0.102f, 0.052f)),
        new("ONLINE", new RectangleF(0.507f, 0.178f, 0.071f, 0.052f)),
        new("CENTRAL CRIATIVA", new RectangleF(0.578f, 0.178f, 0.116f, 0.052f)),
        new("LOJA", new RectangleF(0.694f, 0.178f, 0.066f, 0.052f))
    ];
    private static readonly GarageTabDefinition[] GarageTabs =
    [
        new("CAMPANHA", new RectangleF(0.091f, 0.142f, 0.091f, 0.038f)),
        new("COMPRAR E VENDER", new RectangleF(0.182f, 0.142f, 0.138f, 0.038f)),
        new("CARROS", new RectangleF(0.320f, 0.142f, 0.073f, 0.038f)),
        new("GARAGEM PERSONALIZAVEL", new RectangleF(0.393f, 0.142f, 0.177f, 0.038f)),
        new("PERSONAGEM", new RectangleF(0.570f, 0.142f, 0.104f, 0.038f))
    ];
    private int _difficultyRowIndex;

    public async Task ReconnectControllerIfNeededAsync(CancellationToken cancellationToken)
    {
        var confirmations = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await context.Vision.ContainsAnyTextAsync(
                    ["CONTROLE DESCONECTADO", "RECONECTE UM CONTROLE"],
                    cancellationToken))
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            return;
        }

        context.Logger.State(
            Workflow,
            "ReconectarControle",
            "O jogo reconheceu o Xbox virtual em duas de três capturas; confirmando a reconexão com A.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var consecutiveMisses = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (await context.Vision.ContainsAnyTextAsync(
                    ["CONTROLE DESCONECTADO", "RECONECTE UM CONTROLE"],
                    cancellationToken))
            {
                consecutiveMisses = 0;
            }
            else
            {
                consecutiveMisses++;
                if (consecutiveMisses >= 2)
                {
                    context.Logger.State(
                        Workflow,
                        "ControleReconectado",
                        "O aviso de controle desconectado desapareceu em duas capturas consecutivas.");
                    return;
                }
            }

            await Task.Delay(180, cancellationToken);
        }

        throw new CalibrationRequiredException(
            "O aviso de controle desconectado permaneceu após confirmar com A; " +
            "nenhuma navegação adicional será enviada.");
    }

    private async Task DismissNoSuperWheelspinInfoIfVisibleAsync(
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        var latestConfirmed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var document = await context.Vision.ReadScreenAsync(cancellationToken);
            latestConfirmed = IsNoSuperWheelspinInfoText(
                GameVisionService.Normalize(document.Text));
            if (latestConfirmed)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(140, cancellationToken);
            }
        }

        if (confirmations == 0)
        {
            return;
        }

        if (confirmations < 2 || !latestConfirmed)
        {
            using var ambiguousFrame = await context.Capture.CaptureAsync(CancellationToken.None);
            var ambiguousDiagnostic = context.Capture.SaveDiagnostic(
                ambiguousFrame.Bitmap,
                Workflow,
                "AvisoSemSupersorteiosAmbiguo");
            throw new CalibrationRequiredException(
                "Houve evidência parcial do aviso de nenhum Supersorteio, mas a assinatura exata não ficou " +
                $"estável em 2/3 com a captura mais recente positiva. Nenhuma entrada foi enviada. Diagnóstico: {ambiguousDiagnostic}");
        }

        context.Logger.State(
            Workflow,
            "FecharAvisoSemSupersorteios",
            "O aviso exato de nenhum Supersorteio restante foi confirmado em 2/3; fechando uma vez com A/OK.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        var consecutiveMisses = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            var document = await context.Vision.ReadScreenAsync(cancellationToken);
            if (IsNoSuperWheelspinInfoText(GameVisionService.Normalize(document.Text)))
            {
                consecutiveMisses = 0;
            }
            else
            {
                consecutiveMisses++;
                if (consecutiveMisses >= 2)
                {
                    await Task.Delay(320, cancellationToken);
                    if (await ConfirmPauseTabStateAsync(cancellationToken) == PauseTabState.MeuHorizon)
                    {
                        context.Logger.State(
                            Workflow,
                            "AvisoSemSupersorteiosFechado",
                            "O aviso desapareceu e a aba Meu Horizon foi reconfirmada em 2/3.");
                        return;
                    }

                    break;
                }
            }

            await Task.Delay(180, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(
            frame.Bitmap,
            Workflow,
            "FecharAvisoSemSupersorteios");
        throw new CalibrationRequiredException(
            "O aviso de nenhum Supersorteio foi confirmado, mas não retornou de forma estável à aba Meu Horizon " +
            $"após um único A/OK. Diagnóstico: {diagnostic}");
    }

    private static bool IsNoSuperWheelspinInfoText(string normalized) =>
        normalized.Contains("NENHUM SUPERSORTEIO RESTANTE", StringComparison.Ordinal) &&
        normalized.Contains("NAO TEM SUPERSORTEIOS DISPONIVEIS", StringComparison.Ordinal);

    private async Task<WelcomeContinueState> ConfirmWelcomeContinueMenuAsync(
        CancellationToken cancellationToken)
    {
        var signatureConfirmations = 0;
        var focusConfirmations = 0;
        var readyConflicts = 0;
        var latestFocused = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(
                AnalyzeWelcomeContinueFrame,
                cancellationToken);
            latestFocused = observation.Focused;
            if (observation.SignatureVisible)
            {
                signatureConfirmations++;
            }
            if (observation.Focused)
            {
                focusConfirmations++;
            }
            if (observation.SignatureVisible && observation.Ready)
            {
                readyConflicts++;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        if (signatureConfirmations >= 2 &&
            focusConfirmations >= 2 &&
            latestFocused &&
            readyConflicts == 0)
        {
            return WelcomeContinueState.Stable;
        }

        return signatureConfirmations > 0
            ? WelcomeContinueState.Ambiguous
            : WelcomeContinueState.Absent;
    }

    private async Task WaitForGameReadyAfterStartupAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var recentReady = new Queue<bool>(3);
        var recentWelcomeSignature = new Queue<bool>(3);
        var recentWelcomeFocus = new Queue<bool>(3);
        var welcomeAdvanceUsed = false;
        WelcomeContinueObservation? lastObservation = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastObservation = await context.Vision.AnalyzeScreenAsync(
                AnalyzeWelcomeContinueFrame,
                cancellationToken);

            if (lastObservation.SignatureVisible && lastObservation.Ready)
            {
                using var conflictFrame = await context.Capture.CaptureAsync(CancellationToken.None);
                var conflictDiagnostic = context.Capture.SaveDiagnostic(
                    conflictFrame.Bitmap,
                    Workflow,
                    "BoasVindasComEstadoConflitante");
                throw new CalibrationRequiredException(
                    "A mesma captura combinou o menu de boas-vindas com um marcador de jogo carregado; " +
                    $"nenhuma decisão será tomada. Diagnóstico: {conflictDiagnostic}");
            }

            EnqueueBounded(recentReady, lastObservation.Ready, 3);
            EnqueueBounded(recentWelcomeSignature, lastObservation.SignatureVisible, 3);
            EnqueueBounded(recentWelcomeFocus, lastObservation.Focused, 3);

            if (recentReady.Count == 3 &&
                lastObservation.Ready &&
                recentReady.Count(value => value) >= 2)
            {
                context.Logger.State(
                    Workflow,
                    "AguardarJogo",
                    "Carregamento concluído em duas de três capturas; retomando a navegação segura.");
                return;
            }

            if (recentWelcomeSignature.Count == 3 &&
                recentWelcomeSignature.Count(value => value) >= 2)
            {
                if (welcomeAdvanceUsed)
                {
                    using var persistedFrame = await context.Capture.CaptureAsync(CancellationToken.None);
                    var persistedDiagnostic = context.Capture.SaveDiagnostic(
                        persistedFrame.Bitmap,
                        Workflow,
                        "BoasVindasPersistente");
                    throw new CalibrationRequiredException(
                        "O menu de boas-vindas permaneceu após a confirmação única de Continuar; " +
                        $"nenhum segundo A será enviado. Diagnóstico: {persistedDiagnostic}");
                }

                var welcomeFocused = lastObservation.Focused &&
                                     recentWelcomeFocus.Count(value => value) >= 2;
                if (!welcomeFocused || lastObservation.Ready)
                {
                    using var ambiguousFrame = await context.Capture.CaptureAsync(CancellationToken.None);
                    var ambiguousDiagnostic = context.Capture.SaveDiagnostic(
                        ambiguousFrame.Bitmap,
                        Workflow,
                        "ConfirmarBoasVindas");
                    throw new CalibrationRequiredException(
                        "O menu de boas-vindas foi reconhecido, mas o foco em Continuar não foi " +
                        "confirmado por CV em duas de três capturas; nenhuma entrada foi enviada. " +
                        $"Melhor contorno={lastObservation.OutlineRatio:P1}. Diagnóstico: {ambiguousDiagnostic}");
                }

                context.Logger.State(
                    Workflow,
                    "ContinuarBoasVindas",
                    $"Boas-vindas e foco de Continuar confirmados em 2/3 por OCR+CV " +
                    $"(contorno={lastObservation.OutlineRatio:P1}); avançando uma única vez com A.");
                await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
                welcomeAdvanceUsed = true;
                deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                recentReady.Clear();
                recentWelcomeSignature.Clear();
                recentWelcomeFocus.Clear();
                await Task.Delay(2_500, cancellationToken);
                continue;
            }

            await Task.Delay(300, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "AguardarJogo");
        var observed = lastObservation?.ObservedText.ReplaceLineEndings(" ").Trim();
        if (observed?.Length > 220)
        {
            observed = observed[..220] + "…";
        }
        throw new CalibrationRequiredException(
            $"O jogo não confirmou o carregamento após a inicialização. OCR observado: '{observed}'. " +
            $"Diagnóstico: {diagnostic}");
    }

    private static WelcomeContinueObservation AnalyzeWelcomeContinueFrame(
        Bitmap bitmap,
        OcrDocument document)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        var signatureVisible = normalized.Contains("CONTINUAR", StringComparison.Ordinal) &&
                               normalized.Contains("OPCOES", StringComparison.Ordinal) &&
                               normalized.Contains("SAIR", StringComparison.Ordinal);
        var ready = HasAny(normalized, GameReadyAliases);
        var outlineRatio = signatureVisible
            ? LimeHorizontalBorderRatio(bitmap, WelcomeContinueRegion)
            : 0;
        return new WelcomeContinueObservation(
            signatureVisible,
            signatureVisible && outlineRatio >= WelcomeContinueOutlineRatio,
            ready,
            outlineRatio,
            document.Text);
    }

    private static void EnqueueBounded<T>(Queue<T> queue, T value, int capacity)
    {
        queue.Enqueue(value);
        while (queue.Count > capacity)
        {
            _ = queue.Dequeue();
        }
    }

    public async Task EnsureGarageAsync(CancellationToken cancellationToken)
    {
        _ = await context.GameWindow.WaitForGameAsync(cancellationToken);
        var startScreenVisible = await context.Vision.ContainsAnyTextAsync(
                ["COMEÇAR JOGO", "COMECAR JOGO"],
                cancellationToken);
        if (startScreenVisible)
        {
            context.Logger.State(
                Workflow,
                "IniciarJogo",
                "Tela inicial detectada; iniciando com A pelo controle virtual, sem alterar o foco.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            await WaitForGameReadyAfterStartupAsync(cancellationToken);
        }
        else
        {
            var welcomeState = await ConfirmWelcomeContinueMenuAsync(cancellationToken);
            if (welcomeState == WelcomeContinueState.Stable)
            {
                await WaitForGameReadyAfterStartupAsync(cancellationToken);
            }
            else if (welcomeState == WelcomeContinueState.Ambiguous)
            {
                using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
                var diagnostic = context.Capture.SaveDiagnostic(
                    frame.Bitmap,
                    Workflow,
                    "ConfirmarBoasVindas");
                throw new CalibrationRequiredException(
                    "A tela de boas-vindas apareceu sem confirmação estável do foco em Continuar; " +
                    $"nenhuma entrada foi enviada. Diagnóstico: {diagnostic}");
            }
        }

        await ReconnectControllerIfNeededAsync(cancellationToken);
        await DismissNoSuperWheelspinInfoIfVisibleAsync(cancellationToken);

        if (await IsPhotoModeExitConfirmationOpenAsync(cancellationToken))
        {
            await ConfirmPhotoModeExitAsync(cancellationToken);
        }

        if (await context.Vision.ContainsAnyTextAsync(
                ["EXPLODIR", "OCULTAR UI", "ALTERNAR ALTURA DA CÂMERA"],
                cancellationToken))
        {
            context.Logger.State(
                Workflow,
                "FecharApresentacaoCarro",
                "Tela de apresentação do carro detectada; voltando com B antes de localizar a garagem.");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);
            await Task.Delay(1_500, cancellationToken);
        }

        await ExitPhotoModeIfNeededAsync(cancellationToken);
        await ExitChallengeResultIfNeededAsync(cancellationToken);

        // Depois de alguns segundos sem entrada, a garagem oculta toda a UI.
        // D-pad Up abre o Modo Foto quando o carro está na rua, portanto nunca
        // o use como sonda. Se o contexto já confirma rua/menu, siga sem wake;
        // em tela inconclusiva, LB/Shift apenas desperta a garagem sem abrir um
        // overlay destrutivo na rua.
        var initialContext = await context.GameContext.DetectAsync(cancellationToken);
        var initialNormalized = GameVisionService.Normalize(initialContext.Document.Text);
        var pauseMenuConfirmed = initialContext.Kind == GameContextKind.StreetMenu ||
                                 IsPauseMenuText(initialNormalized) ||
                                 IsTravelConfirmationText(initialNormalized) ||
                                 HasAny(initialNormalized, TravelCardAliases);
        var streetConfirmed = initialContext.Kind == GameContextKind.Street || pauseMenuConfirmed;
        if (!pauseMenuConfirmed && IsGarageText(initialNormalized))
        {
            context.Logger.State(Workflow, "GarantirGaragem", "A garagem já está aberta.");
            return;
        }

        for (var wakeAttempt = 1; wakeAttempt <= 2 && !streetConfirmed; wakeAttempt++)
        {
            await context.Input.TapAsync(GameKey.Shift, cancellationToken, 40);
            await Task.Delay(1_200, cancellationToken);
            var wakeScreen = await context.Vision.ReadScreenAsync(cancellationToken);
            var wakeNormalized = GameVisionService.Normalize(wakeScreen.Text);
            if (IsPauseMenuText(wakeNormalized) ||
                IsTravelConfirmationText(wakeNormalized) ||
                HasAny(wakeNormalized, TravelCardAliases))
            {
                pauseMenuConfirmed = true;
                streetConfirmed = true;
                context.Logger.State(
                    Workflow,
                    "GarantirGaragem",
                    "Menu de pausa reconhecido durante a sonda; interrompendo os comandos LB.");
                break;
            }

            if (IsGarageText(wakeNormalized))
            {
                context.Logger.State(Workflow, "GarantirGaragem", "A garagem já está aberta.");
                return;
            }
        }

        if (!pauseMenuConfirmed)
        {
            await LeaveHouseEntranceIfNeededAsync(cancellationToken);
        }
        var screen = await context.Vision.ReadScreenAsync(cancellationToken);
        var normalized = GameVisionService.Normalize(screen.Text);
        pauseMenuConfirmed = pauseMenuConfirmed ||
                             IsPauseMenuText(normalized) ||
                             IsTravelConfirmationText(normalized) ||
                             HasAny(normalized, TravelCardAliases);

        // Uma transição de submenu pode terminar entre a última tentativa de
        // despertar e este frame. Revalide o próprio texto que será usado para
        // decidir a próxima ação, evitando enviar Esc/cliques de rua dentro da
        // garagem.
        if (!pauseMenuConfirmed && IsGarageText(normalized))
        {
            context.Logger.State(Workflow, "GarantirGaragem", "Menu da garagem confirmado na releitura.");
            return;
        }

        if (!pauseMenuConfirmed && HasAny(
                normalized,
                "MEUS CARROS",
                "IR PARA FABRICANTE",
                "PONTOS DISPONIVEIS",
                "MAESTRIA DE CARRO",
                "APRIMORAMENTO PERSONALIZADO",
                "COMPRAR CARRO"))
        {
            context.Logger.State(
                Workflow,
                "RetornarDoSubmenu",
                "Submenu da garagem detectado; retornando ao menu inicial com B.");
            await ReturnToGarageMenuAsync(cancellationToken);
            return;
        }

        // Ao concluir um desafio do EventLab, o jogo pode pedir uma avaliação
        // antes de devolver o controle à rua. Enquanto esse diálogo estiver
        // aberto, Esc apenas o cancela e ainda não abre o menu de pausa.
        var houseRecoveryPerformedAfterRating = false;
        if (await DismissEventChallengeRatingIfVisibleAsync(
                Workflow,
                normalized,
                cancellationToken))
        {
            screen = await context.Vision.ReadScreenAsync(cancellationToken);
            normalized = GameVisionService.Normalize(screen.Text);

            // O cancelamento da avaliação pode devolver o carro diretamente
            // sobre o gatilho da Casa em Tóquio. Trate esse estado antes da
            // primeira sonda Start para que ela não seja absorvida pelo painel
            // contextual que ainda está surgindo.
            if (HasAny(normalized, "ENTRAR NA CASA"))
            {
                await LeaveHouseEntranceIfNeededAsync(cancellationToken);
                houseRecoveryPerformedAfterRating = true;
                screen = await context.Vision.ReadScreenAsync(cancellationToken);
                normalized = GameVisionService.Normalize(screen.Text);
            }
        }

        if (HasAny(
                normalized,
                "MEUS DESAFIOS",
                "CRIADOR FAVORITO",
                "OPCOES DO DESAFIO",
                "LABORATORIO DE EVENTOS",
                "RESULTADOS DA BUSCA",
                "COD DE COMPARTILHAMENTO"))
        {
            context.Logger.State(
                Workflow,
                "SairDoEventLab",
                "Submenu do EventLab detectado; voltando com B até o menu de pausa.");
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                await context.Input.TapAsync(GameKey.Escape, cancellationToken);
                await Task.Delay(900, cancellationToken);
                screen = await context.Vision.ReadScreenAsync(cancellationToken);
                normalized = GameVisionService.Normalize(screen.Text);

                if (HasAny(normalized, "MAPA DO MUNDO", "CENTRAL CRIATIVA", "MEU HORIZON") &&
                    HasAny(normalized, "CAMPANHA", "ONLINE", "CONFIGURACOES"))
                {
                    context.Logger.State(
                        Workflow,
                        "SairDoEventLab",
                        $"Menu de pausa recuperado após {attempt} comando(s) B.");
                    break;
                }
            }
        }

        var confirmationOpen = IsTravelConfirmationText(normalized);
        var travelBannerOpen = HasAny(normalized, TravelCardAliases);
        // O OCR ocasionalmente omite somente o rótulo pequeno "Meu Horizon"
        // mesmo com a aba visível. Identifique o menu também pelos cartões e
        // pelas demais abas exclusivas dessa tela.
        var pauseMenuOpen = pauseMenuConfirmed ||
                            IsPauseMenuText(normalized) ||
                            (HasAny(normalized, "MAPA DO MUNDO", "CENTRAL CRIATIVA", "ONLINE") &&
                             HasAny(normalized, "CAMPANHA", "CONFIGURAÇÕES", "CONFIGURACOES", "MEU HORIZON"));

        if (!confirmationOpen && !travelBannerOpen)
        {
            if (!pauseMenuOpen)
            {
                context.Logger.State(
                    Workflow,
                    "GarantirGaragem",
                    "Garagem e menu de pausa ainda não confirmados; abrindo a pausa com uma sonda Start/Esc.");
                await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                await Task.Delay(1_000, cancellationToken);
                var afterEscape = await context.Vision.ReadScreenAsync(cancellationToken);
                var afterEscapeNormalized = GameVisionService.Normalize(afterEscape.Text);

                if (IsPhotoModeText(afterEscapeNormalized))
                {
                    context.Logger.Warn(
                        "O Modo Foto apareceu durante a abertura da pausa; recuperando antes de uma única nova tentativa.");
                    await ExitPhotoModeIfNeededAsync(cancellationToken);
                    await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                    await Task.Delay(1_000, cancellationToken);
                    afterEscape = await context.Vision.ReadScreenAsync(cancellationToken);
                    afterEscapeNormalized = GameVisionService.Normalize(afterEscape.Text);
                }

                // Na saída do EventLab o carro pode nascer exatamente sobre
                // o gatilho da Casa. Nessa situação o primeiro Esc revela
                // "Entrar na Casa" em vez do menu de pausa. Afaste o carro e
                // tente Esc novamente, sempre soltando W no finally do helper.
                if (HasAny(afterEscapeNormalized, "ENTRAR NA CASA"))
                {
                    await LeaveHouseEntranceIfNeededAsync(cancellationToken);
                    await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                    await Task.Delay(1_000, cancellationToken);
                    afterEscape = await context.Vision.ReadScreenAsync(cancellationToken);
                    afterEscapeNormalized = GameVisionService.Normalize(afterEscape.Text);
                }
                if (IsGarageText(afterEscapeNormalized))
                {
                    context.Logger.State(
                        Workflow,
                        "GarantirGaragem",
                        "O menu da garagem reapareceu após Esc; cancelando a navegação de rua.");
                    return;
                }
                if (await WaitForPauseMenuAfterProbeAsync(
                        afterEscapeNormalized,
                        houseRecoveryPerformedAfterRating,
                        cancellationToken))
                {
                    context.Logger.State(
                        Workflow,
                        "GarantirGaragem",
                        "A garagem reapareceu durante a espera passiva pelo menu de pausa.");
                    return;
                }
            }
            else
            {
                context.Logger.State(Workflow, "GarantirGaragem", "Menu de pausa já está aberto; mantendo-o aberto.");
            }

        }

        if (!confirmationOpen)
        {
            await OpenMeuHorizonTabAsync(cancellationToken);
            await WaitForFocusedTravelCardAsync(cancellationToken);
            context.Logger.State(
                Workflow,
                "ViajarParaCasa",
                "Aba Meu Horizon e contorno do cartão de viagem confirmados em 2/3; abrindo com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }
        else
        {
            context.Logger.State(Workflow, "GarantirGaragem", "Confirmação de viagem já está aberta.");
        }

        var travelDecision = await WaitForTravelDecisionAsync(cancellationToken);
        if (travelDecision == TravelDecisionState.Confirmation)
        {
            if (!await IsTravelYesFocusedAsync(cancellationToken))
            {
                using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
                var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "ConfirmarViagem");
                throw new CalibrationRequiredException(
                    "A confirmação de viagem apareceu, mas o foco em 'Sim' não foi comprovado " +
                    $"por CV em duas de três capturas. Diagnóstico: {diagnostic}");
            }

            context.Logger.State(Workflow, "ConfirmarViagem", "Confirmando Sim com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }
        else
        {
            context.Logger.State(
                Workflow,
                "ConfirmarViagem",
                "A viagem já foi confirmada pela ação anterior; a garagem abriu sem uma confirmação pendente.");
        }

        await WaitForGarageConfirmedAsync(cancellationToken);
    }

    public async Task OpenDifficultyAsync(CancellationToken cancellationToken)
    {
        await EnsureGarageAsync(cancellationToken);
        await ReturnToGarageMenuAsync(cancellationToken);
        await OpenGarageTabAsync(
            "AbaCampanha",
            "CAMPANHA",
            // "Dirigir" também existe no rodapé de todas as abas. O Diário
            // é exclusivo da Campanha e evita validar a aba Carros por engano.
            ["DIÁRIO DE COLEÇÃO", "DIARIO DE COLECAO"],
            cancellationToken);

        context.Logger.State(
            Workflow,
            "AbrirConfiguracoes",
            "Normalizando o menu Campanha no topo e abrindo Configurações.");
        for (var step = 0; step < 5; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        for (var step = 0; step < 3; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AbrirConfiguracoesConfirmado",
            ["ACESSIBILIDADE VISUAL", "GRÁFICOS E DESEMPENHO", "GRAFICOS E DESEMPENHO"],
            cancellationToken);

        context.Logger.State(
            Workflow,
            "SelecionarDificuldade",
            "Dificuldade é a primeira categoria e já vem selecionada; ativando o painel com Enter.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "SelecionarDificuldadeConfirmado",
            ["VOLTAR AO PADRÃO", "VOLTAR AO PADRAO"],
            cancellationToken);
        _difficultyRowIndex = 0;
    }

    public async Task SetDifficultyOptionAsync(
        string rowLabel,
        string desiredValue,
        GameKey direction,
        int maximumSteps,
        CancellationToken cancellationToken)
    {
        var desiredNormalized = GameVisionService.Normalize(desiredValue);
        var screen = await context.Vision.ReadScreenAsync(cancellationToken);
        if (GameVisionService.Normalize(screen.Text).Contains(desiredNormalized, StringComparison.Ordinal))
        {
            context.Logger.State(Workflow, "ConfigurarDificuldade", $"'{desiredValue}' já está aplicado.");
            return;
        }

        var rowIndex = GameVisionService.Normalize(rowLabel)
            .Contains("PREDEFINICAO DE ASSISTENCIA", StringComparison.Ordinal)
            ? 1
            : 0;
        context.Logger.State(
            Workflow,
            "SelecionarLinhaDificuldade",
            $"Movendo da linha {_difficultyRowIndex + 1} para a linha {rowIndex + 1}: '{rowLabel}'.");
        while (_difficultyRowIndex < rowIndex)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            _difficultyRowIndex++;
        }
        while (_difficultyRowIndex > rowIndex)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            _difficultyRowIndex--;
        }

        for (var step = 1; step <= maximumSteps; step++)
        {
            await context.Input.TapAsync(direction, cancellationToken);
            screen = await context.Vision.ReadScreenAsync(cancellationToken);
            if (GameVisionService.Normalize(screen.Text).Contains(desiredNormalized, StringComparison.Ordinal))
            {
                context.Logger.State(
                    Workflow,
                    "ConfigurarDificuldade",
                    $"'{rowLabel}' definido como '{desiredValue}' em {step} ajuste(s).");
                return;
            }
        }

        throw new CalibrationRequiredException(
            $"Não foi possível definir '{rowLabel}' como '{desiredValue}' após {maximumSteps} passos.");
    }

    public async Task SaveDifficultyAndReturnAsync(CancellationToken cancellationToken)
    {
        context.Logger.State(
            Workflow,
            "SalvarDificuldade",
            "Saindo do painel com B; se houver alterações, confirmaremos 'Salvar e Continuar'.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);

        var outcome = await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "ConfirmarSalvarDificuldade",
            [
                "ALTERAÇÕES NÃO SALVAS",
                "ALTERACOES NAO SALVAS",
                "SALVAR E CONTINUAR",
                "DIRIGIR",
                "CONFIGURAÇÕES",
                "DIÁRIO DE COLEÇÃO"
            ],
            cancellationToken,
            TimeSpan.FromSeconds(8));

        var normalizedOutcome = GameVisionService.Normalize(outcome.Line.Text);
        if (normalizedOutcome.Contains("NAO SALVAS", StringComparison.Ordinal) ||
            normalizedOutcome.Contains("SALVAR E CONTINUAR", StringComparison.Ordinal))
        {
            context.Logger.State(
                Workflow,
                "ConfirmarSalvarDificuldade",
                "Confirmando a opção selecionada 'Salvar e Continuar' com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }

        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "RetornoGaragem",
            ["DIRIGIR", "CONFIGURAÇÕES", "DIÁRIO DE COLEÇÃO"],
            cancellationToken,
            TimeSpan.FromSeconds(10));
    }

    public async Task<MasterySnapshot> OpenMasteryAndReadAsync(
        CancellationToken cancellationToken,
        bool normalizeGarageMenu = true,
        bool startFromGarageHome = false)
    {
        if (startFromGarageHome)
        {
            context.Logger.State(
                Workflow,
                "AbaCarros",
                "Partindo da aba Campanha: avançando duas vezes com RB/PgDn até Carros.");
            await context.Input.TapAsync(GameKey.PageDown, cancellationToken);
            await context.Input.TapAsync(GameKey.PageDown, cancellationToken);
            await context.Vision.WaitForAnyTextAsync(
                Workflow,
                "AbaCarros",
                ["MEUS CARROS", "APRIMORAR E TUNAR"],
                cancellationToken);
        }
        else
        {
            if (normalizeGarageMenu)
            {
                await ReturnToGarageMenuAsync(cancellationToken);
            }
            await OpenGarageTabAsync(
                "AbaCarros",
                "CARROS",
                ["MEUS CARROS", "APRIMORAR E TUNAR"],
                cancellationToken);
        }
        context.Logger.State(
            Workflow,
            "AprimorarETunar",
            startFromGarageHome
                ? "Descendo uma posição e abrindo Aprimorar e Tunar."
                : "Normalizando a seleção no topo e abrindo Aprimorar e Tunar.");
        if (!startFromGarageHome)
        {
            await TapNavigationRepeatedAsync(GameKey.Up, 7, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AprimorarETunarConfirmado",
            ["MAESTRIA DE CARRO"],
            cancellationToken);

        context.Logger.State(
            Workflow,
            "MaestriaDeCarro",
            startFromGarageHome
                ? "Descendo sete posições e abrindo Maestria de Carro."
                : "Normalizando a seleção no topo e descendo sete posições até Maestria de Carro.");
        if (!startFromGarageHome)
        {
            await TapNavigationRepeatedAsync(GameKey.Up, 8, cancellationToken);
        }
        await TapNavigationRepeatedAsync(GameKey.Down, 7, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "MaestriaDeCarroConfirmada",
            ["PONTOS DISPONÍVEIS", "PONTOS DISPONIVEIS"],
            cancellationToken);

        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var points = await ReadMasterySkillPointsAsync(cancellationToken);
        var normalized = GameVisionService.Normalize(document.Text);
        var isSubaru = normalized.Contains("SUBARU", StringComparison.Ordinal) &&
                       (normalized.Contains("22B", StringComparison.Ordinal) ||
                        normalized.Contains("IMPREZA", StringComparison.Ordinal));
        context.Logger.State(
            Workflow,
            "LerPontosECarro",
            $"SP exatos: {points}; Subaru 22B selecionado: {(isSubaru ? "sim" : "não")}.");
        return new MasterySnapshot(points, isSubaru, document.Text);
    }

    public async Task<int> ReadMasterySkillPointsAsync(CancellationToken cancellationToken)
    {
        var readings = new List<(int? Value, string Text)>(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var document = await context.Vision.ReadScaledRegionAsync(
                MasterySkillPointsRegion,
                MasterySkillPointsOcrScale,
                cancellationToken);
            readings.Add((ParseMasterySkillPoints(document), CompactOcrText(document.Text)));

            if (attempt < 2)
            {
                await Task.Delay(90, cancellationToken);
            }
        }

        var consensus = readings
            .Where(reading => reading.Value is not null)
            .GroupBy(reading => reading.Value!.Value)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .FirstOrDefault();
        if (consensus is null || consensus.Count() < 2)
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "LerPontosDisponiveis");
            throw new CalibrationRequiredException(
                "O saldo de SP não estabilizou em duas de três leituras da região sem o ícone amarelo. " +
                $"OCR observado: [{string.Join(" | ", readings.Select(reading => reading.Text))}]. " +
                $"Diagnóstico: {diagnostic}");
        }

        var points = consensus.Key;
        context.Logger.State(
            Workflow,
            "LerPontosDisponiveis",
            $"SP confirmado em {consensus.Count()}/3 leituras da região sem o ícone amarelo: {points}; " +
            $"OCR: [{string.Join(" | ", readings.Select(reading => reading.Text))}].");
        context.Resources.SetSkillPoints(points, estimated: false);
        return points;
    }

    private static int? ParseMasterySkillPoints(OcrDocument document)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        if (!normalized.Contains("PONTOS DISPONIVEIS", StringComparison.Ordinal) ||
            normalized.Contains("CUSTO", StringComparison.Ordinal))
        {
            return null;
        }

        var candidates = Regex.Matches(
                document.Text,
                @"(?<![0-9A-Za-z])[0-9]{1,3}(?![0-9A-Za-z])",
                RegexOptions.CultureInvariant)
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value is >= 0 and <= 999)
            .Distinct()
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string CompactOcrText(string text)
    {
        var compact = Regex.Replace(text.ReplaceLineEndings(" ").Trim(), @"\s+", " ");
        return compact.Length <= 80 ? compact : compact[..80] + "…";
    }

    public async Task SelectSubaru22BAsync(CancellationToken cancellationToken)
    {
        context.Logger.State(Workflow, "SelecionarSubaru", "O carro atual não é o Subaru 22B; abrindo Meus Carros.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await Task.Delay(800, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "RetornarAoMenuCarros",
            ["MEUS CARROS"],
            cancellationToken);

        context.Logger.State(Workflow, "AbrirMeusCarros", "Normalizando no topo e abrindo Meus Carros.");
        for (var step = 0; step < 7; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AbrirMeusCarrosConfirmado",
            ["IR PARA FABRICANTE", "CARRO ATUAL"],
            cancellationToken);

        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "ListaDeFabricantes",
            ["FABRICANTE", "SUBARU"],
            cancellationToken);

        await SelectSubaruManufacturerAsync(cancellationToken);

        await FocusSubaru22BCardAsync(cancellationToken);
        context.Logger.State(Workflow, "Selecionar22B", "22B confirmado pela borda verde; abrindo as ações com Enter.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AcoesDo22B",
            ["ENTRAR NO CARRO"],
            cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "EntrarNoSubaruConfirmado",
            ["DIRIGIR", "CONFIGURAÇÕES", "CONFIGURACOES"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
        await Task.Delay(2_000, cancellationToken);
    }

    private async Task FocusSubaru22BCardAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var game = context.GameWindow.GetRequiredGameWindow();
        var width = game.ClientBounds.Width;
        var height = game.ClientBounds.Height;
        var target = document.Lines.FirstOrDefault(line =>
        {
            var normalized = GameVisionService.Normalize(line.Text);
            return normalized.Contains("IMPREZA 22B", StringComparison.Ordinal) ||
                   (normalized.Contains("22B", StringComparison.Ordinal) &&
                    normalized.Contains("STI", StringComparison.Ordinal));
        });
        if (target is null)
        {
            throw new CalibrationRequiredException(
                "O filtro Subaru abriu, mas não foi possível localizar o cartão do Impreza 22B-STI pelo texto.");
        }

        // A posição é inferida do texto do próprio cartão. Assim, carros Subaru
        // adquiridos depois podem deslocar o 22B sem quebrar a navegação.
        const double columnSpacing = 0.18;
        const double rowSpacing = 0.232;
        var targetColumn = Math.Clamp(
            (int)Math.Round((target.Center.X / (double)width - 0.25) / columnSpacing),
            0,
            3);
        var targetRow = Math.Clamp(
            (int)Math.Round((target.Center.Y / (double)height - 0.14) / rowSpacing),
            0,
            2);
        var firstColumnCenter = target.Center.X / (double)width - targetColumn * columnSpacing;
        var firstRowTitleCenter = target.Center.Y / (double)height - targetRow * rowSpacing;
        var cells = Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 4).Select(column =>
                new RectangleF(
                    (float)(firstColumnCenter + column * columnSpacing - 0.087),
                    (float)(firstRowTitleCenter + row * rowSpacing - 0.045),
                    0.174f,
                    0.225f)))
            .ToArray();
        var selectedCell = await context.Vision.FindLimeSelectionAsync(cells, cancellationToken);
        if (selectedCell < 0)
        {
            throw new CalibrationRequiredException(
                "Não foi possível localizar o cartão atualmente selecionado antes de escolher o Impreza 22B-STI.");
        }

        var selectedRow = selectedCell / 4;
        var selectedColumn = selectedCell % 4;
        context.Logger.State(
            Workflow,
            "Selecionar22B",
            $"Cartão atual na linha {selectedRow + 1}, coluna {selectedColumn + 1}; " +
            $"22B detectado na linha {targetRow + 1}, coluna {targetColumn + 1}. Movendo somente a diferença exata.");

        while (selectedRow > targetRow)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            selectedRow--;
        }

        while (selectedRow < targetRow)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            selectedRow++;
        }

        while (selectedColumn > targetColumn)
        {
            await context.Input.TapAsync(GameKey.Left, cancellationToken);
            selectedColumn--;
        }

        while (selectedColumn < targetColumn)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken);
            selectedColumn++;
        }

        if (!await context.Vision.HasLimeSelectionAsync(
                cells[targetRow * 4 + targetColumn],
                cancellationToken))
        {
            throw new CalibrationRequiredException(
                "O Impreza 22B-STI foi localizado, mas a borda verde não confirmou sua seleção.");
        }
    }

    private async Task ExitPhotoModeIfNeededAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var screen = await context.Vision.ReadScreenAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(screen.Text);
            if (IsPhotoModeExitConfirmationText(normalized))
            {
                await ConfirmPhotoModeExitAsync(cancellationToken);
                continue;
            }

            if (!IsPhotoModeText(normalized))
            {
                return;
            }

            context.Logger.State(
                Workflow,
                "SairModoFotoAtivo",
                $"Modo Foto ativo reconhecido por múltiplos marcadores; saindo com B ({attempt}/2).");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            var absentFrames = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(350, cancellationToken);
                var checkpoint = await context.Vision.ReadScreenAsync(cancellationToken);
                var checkpointText = GameVisionService.Normalize(checkpoint.Text);
                if (IsPhotoModeExitConfirmationText(checkpointText))
                {
                    await ConfirmPhotoModeExitAsync(cancellationToken);
                    break;
                }

                if (IsPhotoModeText(checkpointText))
                {
                    absentFrames = 0;
                }
                else
                {
                    absentFrames++;
                    if (absentFrames >= 2)
                    {
                        context.Logger.State(
                            Workflow,
                            "SairModoFotoAtivo",
                            "Modo Foto desapareceu em duas capturas consecutivas.");
                        return;
                    }
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        var finalScreen = await context.Vision.ReadScreenAsync(CancellationToken.None);
        if (IsPhotoModeText(GameVisionService.Normalize(finalScreen.Text)))
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "SairModoFotoAtivo");
            throw new CalibrationRequiredException(
                $"O Modo Foto continuou aberto após duas recuperações limitadas. Diagnóstico: {diagnostic}");
        }
    }

    private async Task<bool> IsPhotoModeExitConfirmationOpenAsync(CancellationToken cancellationToken)
    {
        var screen = await context.Vision.ReadScreenAsync(cancellationToken);
        return IsPhotoModeExitConfirmationText(GameVisionService.Normalize(screen.Text));
    }

    private async Task ExitChallengeResultIfNeededAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var successes = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var checkpoint = await CaptureSpRaceResultAsync(cancellationToken);
            if (checkpoint.Kind == SpRaceResultKind.Failure)
            {
                failures++;
            }
            else if (checkpoint.Kind == SpRaceResultKind.Success)
            {
                successes++;
            }

            if (attempt < 2)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        if (successes >= 2 && failures == 0)
        {
            context.Logger.State(
                Workflow,
                "SairDoResultadoConcluido",
                "Sucesso do desafio confirmado em duas de três capturas; acionando A/Continuar antes de procurar o menu da rua.");
            await ExitCurrentEventToStreetMenuAsync(
                Workflow,
                cancellationToken,
                allowConfirmedResultProbe: true);
            return;
        }

        if (failures < 2 || successes > 0)
        {
            return;
        }

        context.Logger.State(
            Workflow,
            "SairDoResultadoDoDesafio",
            "Falha do desafio confirmada por OCR/CV em duas de três capturas; acionando B/Sair uma única vez antes de procurar a garagem.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken, 120);

        var safeSuccessorFrames = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(500, cancellationToken);
            var state = await context.GameContext.DetectAsync(cancellationToken);
            if (await DismissEventChallengeRatingIfVisibleAsync(
                    Workflow,
                    GameVisionService.Normalize(state.Document.Text),
                    cancellationToken))
            {
                context.Logger.State(
                    Workflow,
                    "ResultadoDoDesafioFechado",
                    "B/Sair abriu a avaliação esperada; Cancelar foi confirmado e a recuperação pode continuar.");
                return;
            }

            if (state.Kind is GameContextKind.Street or GameContextKind.Event or
                GameContextKind.StreetMenu or GameContextKind.EventMenu)
            {
                safeSuccessorFrames++;
                if (safeSuccessorFrames >= 2)
                {
                    context.Logger.State(
                        Workflow,
                        "ResultadoDoDesafioFechado",
                        $"A tela de falha saiu para um contexto seguro confirmado ({state.Kind}).");
                    return;
                }
            }
            else
            {
                safeSuccessorFrames = 0;
                if (attempt >= 5)
                {
                    var checkpoint = await CaptureSpRaceResultAsync(cancellationToken);
                    if (checkpoint.Kind == SpRaceResultKind.Failure)
                    {
                        break;
                    }
                }
            }
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "SairDoResultadoDoDesafio");
        throw new CalibrationRequiredException(
            "A tela de falha não transitou para rua/evento/menu seguro após B/Sair. " +
            $"Diagnóstico: {diagnostic}");
    }

    private async Task ConfirmPhotoModeExitAsync(CancellationToken cancellationToken)
    {
        var confirmations = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(
                AnalyzePhotoModeExitConfirmation,
                cancellationToken);
            if (observation.DialogVisible && observation.YesLimeRatio >= 0.04)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(140, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "ConfirmarSaidaModoFoto");
            throw new CalibrationRequiredException(
                "A confirmação de saída do Modo Foto apareceu, mas o foco em 'Sim' não foi comprovado " +
                $"por CV em duas de três capturas. Diagnóstico: {diagnostic}");
        }

        context.Logger.State(
            Workflow,
            "ConfirmarSaidaModoFoto",
            $"Título, pergunta e foco em Sim confirmados em {confirmations}/3 capturas; saindo com A.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await Task.Delay(1_200, cancellationToken);

        var afterConfirmation = await context.Vision.ReadScreenAsync(cancellationToken);
        if (IsPhotoModeExitConfirmationText(GameVisionService.Normalize(afterConfirmation.Text)))
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "SaidaModoFotoNaoProcessada");
            throw new CalibrationRequiredException(
                $"A confirmação de saída do Modo Foto continuou aberta após A. Diagnóstico: {diagnostic}");
        }
    }

    private static bool IsPhotoModeText(string normalized)
    {
        string[] markers =
        [
            "TIRAR FOTO",
            "MODO DE EFEITOS",
            "INCLINACAO",
            "GUINADA",
            "ROLAR",
            "FOTO RELAMPAGO",
            "OCULTAR UI",
            "ALTITUDE"
        ];
        return markers.Count(marker => normalized.Contains(marker, StringComparison.Ordinal)) >= 3;
    }

    private static bool IsPhotoModeExitConfirmationText(string normalized) =>
        normalized.Contains("SAIR DO MODO FOTO", StringComparison.Ordinal) &&
        HasAny(normalized, PhotoModeExitQuestionAliases);

    private async Task SelectSubaruManufacturerAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var game = context.GameWindow.GetRequiredGameWindow();
        var width = game.ClientBounds.Width;
        var height = game.ClientBounds.Height;
        var target = document.Lines.FirstOrDefault(line =>
            GameVisionService.Normalize(line.Text).Contains("SUBARU", StringComparison.Ordinal));
        if (target is null)
        {
            throw new CalibrationRequiredException(
                "Não foi possível localizar Subaru pelo texto na grade de fabricantes.");
        }

        var rowCenters = document.Lines
            .Where(line =>
                line.Center.X >= width * 0.08 && line.Center.X <= width * 0.92 &&
                line.Center.Y >= height * 0.20 && line.Center.Y <= height * 0.90)
            .Select(line => line.Center.Y)
            .OrderBy(y => y)
            .Aggregate(
                new List<double>(),
                (rows, y) =>
                {
                    if (rows.Count == 0 || y - rows[^1] > height * 0.018)
                    {
                        rows.Add(y);
                    }
                    else
                    {
                        rows[^1] = (rows[^1] + y) / 2;
                    }

                    return rows;
                });

        var targetRow = rowCenters
            .Select((y, index) => new { Distance = Math.Abs(y - target.Center.Y), Index = index })
            .OrderBy(item => item.Distance)
            .First().Index;
        var targetColumn = Math.Clamp(
            (int)Math.Round((target.Center.X / (double)width - 0.20) / 0.20),
            0,
            3);

        var firstColumnCenter = target.Center.X - targetColumn * width * 0.20;
        var cells = rowCenters
            .SelectMany((y, rowIndex) => Enumerable.Range(0, 4).Select(columnIndex =>
                new RectangleF(
                    (float)((firstColumnCenter + columnIndex * width * 0.20) / width - 0.095),
                    (float)(y / height - 0.027),
                    0.19f,
                    0.054f)))
            .ToArray();
        var selectedCell = await context.Vision.FindLimeSelectionAsync(cells, cancellationToken);
        if (selectedCell < 0)
        {
            throw new CalibrationRequiredException(
                "Não foi possível localizar o contorno verde atual na grade antes de selecionar Subaru.");
        }

        var selectedRow = selectedCell / 4;
        var selectedColumn = selectedCell % 4;
        context.Logger.State(
            Workflow,
            "FiltrarSubaru",
            $"Foco atual na linha {selectedRow + 1}, coluna {selectedColumn + 1}; " +
            $"Subaru detectado na linha {targetRow + 1}, coluna {targetColumn + 1}. Movendo somente a diferença exata.");

        while (selectedRow > targetRow)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            selectedRow--;
        }

        while (selectedRow < targetRow)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            selectedRow++;
        }

        while (selectedColumn > targetColumn)
        {
            await context.Input.TapAsync(GameKey.Left, cancellationToken);
            selectedColumn--;
        }

        while (selectedColumn < targetColumn)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken);
            selectedColumn++;
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "FiltrarSubaruConfirmado",
            ["22B-STI", "22B STI", "IMPREZA 22B"],
            cancellationToken);
    }

    public async Task DriveAsync(CancellationToken cancellationToken)
    {
        await ReturnToGarageMenuAsync(cancellationToken);
        // "Dirigir" também aparece no rodapé como atalho "Tab Dirigir".
        // Clicar no primeiro resultado do OCR podia acertar esse rodapé e não
        // sair da garagem. Abra a aba Campanha e normalize a lista no topo;
        // nela, Dirigir é sempre a primeira opção.
        await OpenGarageTabAsync(
            "AbaCampanhaParaDirigir",
            "CAMPANHA",
            ["DIÁRIO DE COLEÇÃO", "DIARIO DE COLECAO"],
            cancellationToken);
        context.Logger.State(Workflow, "Dirigir", "Selecionando o primeiro item da aba Campanha por teclado.");
        for (var step = 0; step < 8; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        context.Logger.State(Workflow, "AguardarRua", "Aguardando a saída da garagem.");
        // O HUD da rua aparece antes de o jogo voltar a aceitar Esc. Com oito
        // segundos o comando ainda era descartado em algumas saídas da garagem.
        await Task.Delay(15_000, cancellationToken);

        // Ao sair da Casa em Tóquio, o painel contextual "Entrar na Casa"
        // pode continuar capturando Esc indefinidamente enquanto o carro fica
        // parado na entrada. Afaste o carro por um instante e só então deixe o
        // workflow abrir o menu de pausa. O finally garante W solto inclusive
        // quando F8 cancela durante esse movimento.
        await LeaveHouseEntranceIfNeededAsync(cancellationToken);
    }

    public async Task ExitCurrentEventToStreetMenuAsync(
        string sourceWorkflow,
        CancellationToken cancellationToken,
        bool allowConfirmedResultProbe = false)
    {
        var menu = await OpenEventOrStreetMenuAsync(
            sourceWorkflow,
            cancellationToken,
            allowConfirmedResultProbe);
        if (menu.Kind == GameContextKind.StreetMenu)
        {
            context.Logger.State(
                sourceWorkflow,
                "RetornoRua",
                "Menu da rua já confirmado após a corrida-alvo; handoff liberado.");
            return;
        }

        if (menu.Kind != GameContextKind.EventMenu)
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "AbrirMenuParaSaida",
                $"A corrida-alvo terminou, mas o menu de evento ou da rua não foi confirmado. Contexto: {menu.Kind}.");
        }

        context.Logger.State(
            sourceWorkflow,
            "SairDoEvento",
            "Menu do evento confirmado; localizando Sair do Evento pelo contorno verde.");
        var exitSelected = false;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1)
            {
                // Reabrir o menu normaliza o foco no primeiro cartão. Se isso
                // não ocorrer, nenhuma sequência adicional de direcionais é autorizada.
                await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
                await Task.Delay(700, cancellationToken);
                await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
                await Task.Delay(context.Settings.CrFarm.OutcomeSettleMs, cancellationToken);
                var normalizedMenu = await DetectContextMenuWithConsensusAsync(cancellationToken);
                if (normalizedMenu.Kind != GameContextKind.EventMenu)
                {
                    throw await CreateEventHandoffFailureAsync(
                        sourceWorkflow,
                        "NormalizarMenuEvento",
                        $"A normalização deixou o contexto em {normalizedMenu.Kind}; a saída não será selecionada.");
                }
            }

            await context.Input.TapAsync(GameKey.Right, cancellationToken, 90);
            exitSelected = await HasStableLimeSelectionAsync(
                EventExitCardFocusRegion,
                minimumRatio: 0.08,
                cancellationToken);
            if (exitSelected)
            {
                context.Logger.State(
                    sourceWorkflow,
                    "SelecionarSaidaEvento",
                    $"Sair do Evento confirmado por CV na tentativa {attempt}/2.");
                break;
            }
        }

        if (!exitSelected)
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "SelecionarSaidaEvento",
                "O contorno verde de Sair do Evento não foi confirmado em duas tentativas normalizadas.");
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
        await Task.Delay(700, cancellationToken);
        var confirmation = await context.GameContext.DetectAsync(cancellationToken);
        if (!IsEventExitConfirmation(confirmation))
        {
            await Task.Delay(500, cancellationToken);
            confirmation = await context.GameContext.DetectAsync(cancellationToken);
        }

        if (!IsEventExitConfirmation(confirmation))
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "ConfirmacaoSaidaEvento",
                $"O modal específico de saída não foi confirmado após abrir o cartão. Contexto: {confirmation.Kind}.");
        }

        for (var attempt = 1; attempt <= 3 && IsEventExitConfirmation(confirmation); attempt++)
        {
            if (!await HasStableLimeSelectionAsync(
                    EventExitYesRegion,
                    minimumRatio: 0.05,
                    cancellationToken))
            {
                throw await CreateEventHandoffFailureAsync(
                    sourceWorkflow,
                    "ValidarSimSaidaEvento",
                    "O modal foi reconhecido, mas o foco em Sim não foi confirmado por CV em duas de três capturas.");
            }

            await context.Input.TapAsync(GameKey.Enter, cancellationToken, 160);
            await Task.Delay(900, cancellationToken);
            confirmation = await context.GameContext.DetectAsync(cancellationToken);
            if (IsEventExitConfirmation(confirmation))
            {
                context.Logger.Warn(
                    $"A confirmação de saída continuou aberta após {attempt}/3; repetindo somente porque Sim segue validado.");
            }
        }

        if (IsEventExitConfirmation(confirmation))
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "ConfirmarSaidaEvento",
                "O modal de saída permaneceu aberto após três confirmações visualmente validadas.");
        }

        await Task.Delay(EventExitSettleMilliseconds, cancellationToken);
        var postExitScreen = await context.Vision.ReadScreenAsync(cancellationToken);
        await DismissEventChallengeRatingIfVisibleAsync(
            sourceWorkflow,
            GameVisionService.Normalize(postExitScreen.Text),
            cancellationToken);

        var streetMenu = await OpenEventOrStreetMenuAsync(
            sourceWorkflow,
            cancellationToken,
            allowConfirmedResultProbe: false);
        if (streetMenu.Kind != GameContextKind.StreetMenu)
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "ConfirmarRetornoRua",
                $"A saída foi confirmada, mas o menu da rua não estabilizou. Contexto: {streetMenu.Kind}.");
        }

        context.Logger.State(
            sourceWorkflow,
            "RetornoRua",
            "Saída do evento e menu da rua confirmados; handoff liberado.");
    }

    private async Task<GameContextResult> OpenEventOrStreetMenuAsync(
        string sourceWorkflow,
        CancellationToken cancellationToken,
        bool allowConfirmedResultProbe)
    {
        var current = await context.GameContext.DetectAsync(cancellationToken);
        if (await DismissEventChallengeRatingIfVisibleAsync(
                sourceWorkflow,
                GameVisionService.Normalize(current.Document.Text),
                cancellationToken))
        {
            current = await context.GameContext.DetectAsync(cancellationToken);
        }

        if (current.Kind is GameContextKind.EventMenu or GameContextKind.StreetMenu)
        {
            return current;
        }

        if (current.Kind == GameContextKind.WorldMap)
        {
            context.Logger.State(
                sourceWorkflow,
                "FecharMapaNoHandoff",
                "Mapa-múndi detectado; fechando-o antes da única sonda do menu de contexto.");
            await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
            await Task.Delay(1_000, cancellationToken);
            current = await context.GameContext.DetectAsync(cancellationToken);
        }

        if (allowConfirmedResultProbe &&
            current.Kind is not (GameContextKind.EventMenu or GameContextKind.StreetMenu) &&
            await HasStableSpRaceCompletionAsync(cancellationToken))
        {
            current = await ContinueCompletedSpResultAsync(sourceWorkflow, cancellationToken);
            if (current.Kind is GameContextKind.EventMenu or GameContextKind.StreetMenu)
            {
                return current;
            }

            allowConfirmedResultProbe = false;
        }

        if (current.Kind == GameContextKind.Unknown)
        {
            // A vinheta final/carregamento pode ocultar HUD e textos por alguns segundos.
            // Observe passivamente antes da única sonda reversível; nunca envie Menu
            // enquanto o frame atual ainda for desconhecido.
            for (var observation = 1; observation <= 10 && current.Kind == GameContextKind.Unknown; observation++)
            {
                await Task.Delay(1_000, cancellationToken);
                current = await context.GameContext.DetectAsync(cancellationToken);
                if (await DismissEventChallengeRatingIfVisibleAsync(
                        sourceWorkflow,
                        GameVisionService.Normalize(current.Document.Text),
                        cancellationToken))
                {
                    current = await context.GameContext.DetectAsync(cancellationToken);
                }

                if (current.Kind is GameContextKind.EventMenu or GameContextKind.StreetMenu)
                {
                    return current;
                }
            }

            if (current.Kind is not (GameContextKind.Event or GameContextKind.Street or GameContextKind.Unknown))
            {
                return current;
            }

            if (current.Kind == GameContextKind.Unknown && !allowConfirmedResultProbe)
            {
                return current;
            }
        }
        else if (current.Kind is not (GameContextKind.Event or GameContextKind.Street))
        {
            return current;
        }

        GameContextResult? observedAfterProbe = null;
        for (var probe = 1; probe <= 2; probe++)
        {
            await context.Input.TapAsync(GameKey.Menu, cancellationToken, 110);
            await Task.Delay(context.Settings.CrFarm.OutcomeSettleMs, cancellationToken);
            observedAfterProbe = await DetectContextMenuWithConsensusAsync(cancellationToken);
            if (observedAfterProbe.Kind is GameContextKind.EventMenu or GameContextKind.StreetMenu)
            {
                return observedAfterProbe;
            }

            if (probe == 2)
            {
                break;
            }

            var safeRetry = observedAfterProbe.Kind is GameContextKind.Event or GameContextKind.Street;
            if (!safeRetry)
            {
                break;
            }

            context.Logger.Warn(
                $"A sonda de menu {probe}/2 não abriu um menu estável ({observedAfterProbe.Kind}); " +
                "repetindo somente porque o contexto seguro foi reconfirmado.");
        }

        return observedAfterProbe ?? current;
    }

    private async Task<GameContextResult> ContinueCompletedSpResultAsync(
        string sourceWorkflow,
        CancellationToken cancellationToken)
    {
        var houseRecoveryUsed = false;
        for (var continueAttempt = 1; continueAttempt <= 2; continueAttempt++)
        {
            if (!await HasStableSpRaceCompletionAsync(cancellationToken))
            {
                throw await CreateEventHandoffFailureAsync(
                    sourceWorkflow,
                    "ConfirmarContinuarResultadoSP",
                    "A tela positiva deixou de ser estável antes de A/Continuar; nenhuma entrada foi enviada.");
            }

            context.Logger.State(
                sourceWorkflow,
                "ContinuarResultadoSP",
                $"Resultado positivo e A/Continuar reconfirmados; enviando A ({continueAttempt}/2). B/Tentar Novamente não será acionado.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            var persistentResultFrames = 0;
            var safeSuccessorFrames = 0;
            GameContextKind? safeSuccessorKind = null;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, cancellationToken);
                var current = await context.GameContext.DetectAsync(cancellationToken);
                var normalized = GameVisionService.Normalize(current.Document.Text);

                if (await DismissEventChallengeRatingIfVisibleAsync(
                        sourceWorkflow,
                        normalized,
                        cancellationToken))
                {
                    deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                    current = await context.GameContext.DetectAsync(cancellationToken);
                    normalized = GameVisionService.Normalize(current.Document.Text);
                }

                if (HasAny(normalized, "ENTRAR NA CASA"))
                {
                    if (houseRecoveryUsed)
                    {
                        throw await CreateEventHandoffFailureAsync(
                            sourceWorkflow,
                            "RecuperarEntradaAposResultadoSP",
                            "O painel da Casa reapareceu após a única recuperação física e sonda Start permitidas.");
                    }

                    await LeaveHouseEntranceIfNeededAsync(cancellationToken);
                    houseRecoveryUsed = true;
                    context.Logger.State(
                        sourceWorkflow,
                        "AbrirMenuAposResultadoSP",
                        "Saída da Casa executada uma vez; enviando uma única sonda Start e exigindo StreetMenu estável.");
                    await context.Input.TapAsync(GameKey.Menu, cancellationToken, 110);
                    await Task.Delay(1_000, cancellationToken);
                    var streetMenu = await DetectContextMenuWithConsensusAsync(cancellationToken);
                    if (streetMenu.Kind == GameContextKind.StreetMenu)
                    {
                        return streetMenu;
                    }

                    throw await CreateEventHandoffFailureAsync(
                        sourceWorkflow,
                        "AbrirMenuAposResultadoSP",
                        $"A sonda Start após a Casa não confirmou StreetMenu. Contexto: {streetMenu.Kind}.");
                }

                var safeSuccessor = current.Kind is GameContextKind.Street or GameContextKind.StreetMenu or
                                    GameContextKind.EventMenu;
                if (safeSuccessor)
                {
                    if (safeSuccessorKind == current.Kind)
                    {
                        safeSuccessorFrames++;
                    }
                    else
                    {
                        safeSuccessorKind = current.Kind;
                        safeSuccessorFrames = 1;
                    }

                    if (safeSuccessorFrames >= 2)
                    {
                        context.Logger.State(
                            sourceWorkflow,
                            "ResultadoSPContinuado",
                            $"A/Continuar levou a um sucessor seguro confirmado em dois frames ({current.Kind}).");
                        return current;
                    }
                }
                else
                {
                    safeSuccessorFrames = 0;
                    safeSuccessorKind = null;
                }

                var result = await CaptureSpRaceResultAsync(cancellationToken);
                if (result.Kind == SpRaceResultKind.Failure ||
                    SpRaceVision.IsActiveHud(result.Evidence.NormalizedText))
                {
                    throw await CreateEventHandoffFailureAsync(
                        sourceWorkflow,
                        "ContinuarResultadoSP",
                        "A/Continuar levou a uma falha ou corrida ativa inesperada; o handoff foi interrompido.");
                }

                persistentResultFrames = result.Kind == SpRaceResultKind.Success
                    ? persistentResultFrames + 1
                    : 0;
                if (persistentResultFrames >= 2)
                {
                    break;
                }
            }

            if (persistentResultFrames >= 2 && continueAttempt < 2)
            {
                context.Logger.Warn(
                    "A tela positiva permaneceu após A/Continuar; repetindo uma única vez somente após nova confirmação 2/3.");
                continue;
            }

            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "ContinuarResultadoSP",
                persistentResultFrames >= 2
                    ? "A tela positiva permaneceu após duas tentativas validadas de A/Continuar."
                    : "A/Continuar não produziu rua ou menu seguro dentro do prazo limitado.");
        }

        throw await CreateEventHandoffFailureAsync(
            sourceWorkflow,
            "ContinuarResultadoSP",
            "O orçamento limitado de A/Continuar foi esgotado.");
    }

    private async Task<bool> HasStableSpRaceCompletionAsync(CancellationToken cancellationToken)
    {
        var consecutiveConfirmations = 0;
        var conflicts = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var checkpoint = await CaptureSpRaceResultAsync(cancellationToken);
            if (checkpoint.Kind == SpRaceResultKind.Success)
            {
                consecutiveConfirmations++;
            }
            else
            {
                consecutiveConfirmations = 0;
                if (checkpoint.Kind == SpRaceResultKind.Failure)
                {
                    conflicts++;
                }
            }

            if (attempt < 3)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        return consecutiveConfirmations >= 2 && conflicts == 0;
    }

    private Task<SpRaceResultObservation> CaptureSpRaceResultAsync(
        CancellationToken cancellationToken) =>
        context.Vision.AnalyzeScreenWithScaledRegionsAsync(
            SpRaceVision.ResultOcrRegions,
            requestedScale: 3,
            SpRaceVision.AnalyzeResultCheckpoint,
            cancellationToken);

    private async Task<bool> DismissEventChallengeRatingIfVisibleAsync(
        string sourceWorkflow,
        string normalizedText,
        CancellationToken cancellationToken)
    {
        if (!HasAny(normalizedText, "AVALIAR DESAFIO", "QUER CURTIR ESTE DESAFIO"))
        {
            return false;
        }

        var focusedOption = await CaptureStableEventChallengeRatingOptionAsync(cancellationToken);
        if (focusedOption == EventChallengeRatingOption.Unknown)
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "ConfirmarAvaliacaoNoHandoff",
                "A avaliação apareceu, mas nenhuma opção focada foi confirmada em duas capturas consecutivas.");
        }

        for (var movement = 1; movement <= 3 && focusedOption != EventChallengeRatingOption.Cancelar; movement++)
        {
            context.Logger.State(
                sourceWorkflow,
                "FocarCancelarAvaliacao",
                $"Foco atual confirmado em {focusedOption}; enviando Down limitado ({movement}/3).");
            await context.Input.TapAsync(GameKey.Down, cancellationToken, 70);
            await Task.Delay(220, cancellationToken);
            focusedOption = await CaptureStableEventChallengeRatingOptionAsync(cancellationToken);
        }

        if (focusedOption != EventChallengeRatingOption.Cancelar)
        {
            throw await CreateEventHandoffFailureAsync(
                sourceWorkflow,
                "FocarCancelarAvaliacao",
                "Cancelar não ficou focado por OCR e contorno lime após três movimentos limitados.");
        }

        context.Logger.State(
            sourceWorkflow,
            "FecharAvaliacaoNoHandoff",
            "Cancelar confirmado por OCR e contorno lime em duas capturas consecutivas; selecionando com A.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);

        // A mudança sazonal exibida depois de Cancelar pode levar vários
        // minutos mesmo com o processo responsivo. Esta espera é totalmente
        // passiva e continua limitada; nenhuma entrada é autorizada em Unknown.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        var cancelRetryUsed = false;
        var safeSuccessorFrames = 0;
        GameContextKind? safeSuccessorKind = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);
            var state = await context.GameContext.DetectAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(state.Document.Text);
            var ratingVisible = HasAny(normalized, "AVALIAR DESAFIO", "QUER CURTIR ESTE DESAFIO");
            var houseEntranceVisible = HasAny(normalized, "ENTRAR NA CASA");
            var safeKind = state.Kind is GameContextKind.Street or GameContextKind.StreetMenu or
                           GameContextKind.EventMenu;
            if (!ratingVisible && (safeKind || houseEntranceVisible))
            {
                var observedKind = houseEntranceVisible && state.Kind == GameContextKind.Unknown
                    ? GameContextKind.Street
                    : state.Kind;
                if (safeSuccessorKind == observedKind)
                {
                    safeSuccessorFrames++;
                }
                else
                {
                    safeSuccessorKind = observedKind;
                    safeSuccessorFrames = 1;
                }

                if (safeSuccessorFrames >= 2)
                {
                    return true;
                }
            }
            else
            {
                safeSuccessorFrames = 0;
                safeSuccessorKind = null;
                if (ratingVisible && !cancelRetryUsed &&
                    await CaptureStableEventChallengeRatingOptionAsync(cancellationToken) ==
                    EventChallengeRatingOption.Cancelar)
                {
                    context.Logger.Warn(
                        "A avaliação permaneceu com Cancelar focado; repetindo A uma única vez após nova confirmação OCR/CV.");
                    await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
                    cancelRetryUsed = true;
                    deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                }
            }
        }

        throw await CreateEventHandoffFailureAsync(
            sourceWorkflow,
            "FecharAvaliacaoNoHandoff",
            "Cancelar foi selecionado, mas a avaliação não levou a rua/evento/menu seguro em dois frames.");
    }

    private async Task<EventChallengeRatingOption> CaptureStableEventChallengeRatingOptionAsync(
        CancellationToken cancellationToken)
    {
        var previous = EventChallengeRatingOption.Unknown;
        var consecutive = 0;
        for (var observation = 1; observation <= 4; observation++)
        {
            var current = await context.Vision.AnalyzeScreenAsync(
                AnalyzeEventChallengeRating,
                cancellationToken);
            if (current.DialogVisible && current.FocusedOption != EventChallengeRatingOption.Unknown)
            {
                if (previous == current.FocusedOption)
                {
                    consecutive++;
                }
                else
                {
                    previous = current.FocusedOption;
                    consecutive = 1;
                }

                if (consecutive >= 2)
                {
                    return current.FocusedOption;
                }
            }
            else
            {
                previous = EventChallengeRatingOption.Unknown;
                consecutive = 0;
            }

            if (observation < 4)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        return EventChallengeRatingOption.Unknown;
    }

    private static EventChallengeRatingObservation AnalyzeEventChallengeRating(
        Bitmap bitmap,
        OcrDocument document)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        var visibleOptions = document.Lines
            .Select(line => GameVisionService.Normalize(line.Text).Replace(" ", string.Empty, StringComparison.Ordinal))
            .Count(text => EventChallengeRatingOptions.Any(option => text == option.CompactText));
        var dialogVisible = HasAny(normalized, "AVALIAR DESAFIO", "QUER CURTIR ESTE DESAFIO") &&
                            visibleOptions >= 2;
        if (!dialogVisible)
        {
            return new EventChallengeRatingObservation(false, EventChallengeRatingOption.Unknown, 0);
        }

        var focused = EventChallengeRatingOption.Unknown;
        var bestScore = 0d;
        foreach (var option in EventChallengeRatingOptions)
        {
            var textVisible = document.Lines.Any(line =>
            {
                var centerX = line.Center.X / (double)bitmap.Width;
                var centerY = line.Center.Y / (double)bitmap.Height;
                return GameVisionService.Normalize(line.Text).Replace(" ", string.Empty, StringComparison.Ordinal) == option.CompactText &&
                       centerX is >= 0.30 and <= 0.70 &&
                       centerY >= option.Region.Top &&
                       centerY <= option.Region.Bottom;
            });
            var score = textVisible ? LimeHorizontalBorderRatio(bitmap, option.Region) : 0;
            if (score < 0.70 || score <= bestScore)
            {
                continue;
            }

            focused = option.Option;
            bestScore = score;
        }

        return new EventChallengeRatingObservation(true, focused, bestScore);
    }

    private async Task<GameContextResult> DetectContextMenuWithConsensusAsync(
        CancellationToken cancellationToken)
    {
        GameContextResult? last = null;
        GameContextKind? candidateKind = null;
        var consecutive = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await context.GameContext.DetectAsync(cancellationToken);
            if (last.Kind is GameContextKind.EventMenu or GameContextKind.StreetMenu)
            {
                if (candidateKind == last.Kind)
                {
                    consecutive++;
                }
                else
                {
                    candidateKind = last.Kind;
                    consecutive = 1;
                }

                if (consecutive >= 2)
                {
                    return last;
                }
            }
            else
            {
                candidateKind = null;
                consecutive = 0;
            }

            if (attempt < 3)
            {
                await Task.Delay(900, cancellationToken);
            }
        }

        return new GameContextResult(
            GameContextKind.Unknown,
            0,
            "menu observado sem duas confirmações consecutivas",
            last!.Document);
    }

    private async Task<bool> HasStableLimeSelectionAsync(
        RectangleF region,
        double minimumRatio,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await context.Vision.HasLimeSelectionAsync(
                    region,
                    cancellationToken,
                    minimumRatio))
            {
                confirmations++;
            }

            if (attempt < 3)
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        return confirmations >= 2;
    }

    private static bool IsEventExitConfirmation(GameContextResult result)
    {
        var normalized = GameVisionService.Normalize(result.Document.Text);
        return result.Kind == GameContextKind.EventExitConfirmation ||
               normalized.Contains("SAIR DO EVENTO", StringComparison.Ordinal) &&
               normalized.Contains("SIM", StringComparison.Ordinal) &&
               normalized.Contains("NAO", StringComparison.Ordinal);
    }

    private async Task<CalibrationRequiredException> CreateEventHandoffFailureAsync(
        string sourceWorkflow,
        string state,
        string message)
    {
        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, sourceWorkflow, state);
        return new CalibrationRequiredException($"{message} Diagnóstico local: {diagnostic}");
    }

    private async Task LeaveHouseEntranceIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!await context.Vision.ContainsAnyTextAsync(
                ["ENTRAR NA CASA"],
                cancellationToken))
        {
            return;
        }

        context.Logger.State(
            Workflow,
            "SairDaEntradaDaCasa",
            "Painel da casa ainda ativo; avançando brevemente antes de abrir o menu da rua.");
        await context.Input.HoldAsync(GameKey.W, 1_800, cancellationToken);
        await Task.Delay(1_500, cancellationToken);
        if (!await context.Vision.ContainsAnyTextAsync(
                ["ENTRAR NA CASA"],
                cancellationToken))
        {
            return;
        }

        context.Logger.State(
            Workflow,
            "RecuarDaEntradaDaCasa",
            "O painel da casa permaneceu após avançar; recuando de forma limitada para sair do gatilho.");
        await context.Input.HoldAsync(GameKey.S, 3_000, cancellationToken);
        await Task.Delay(2_500, cancellationToken);
        if (!await context.Vision.ContainsAnyTextAsync(
                ["ENTRAR NA CASA"],
                cancellationToken))
        {
            return;
        }

        context.Logger.State(
            Workflow,
            "ManobrarDaEntradaDaCasa",
            "O painel da casa ainda está ativo; fazendo uma única manobra curta para a esquerda antes de parar com segurança.");
        await context.Input.KeyDownAsync(GameKey.A, cancellationToken);
        try
        {
            await context.Input.HoldAsync(GameKey.W, 1_300, cancellationToken);
        }
        finally
        {
            await context.Input.KeyUpAsync(GameKey.A, CancellationToken.None);
        }

        await Task.Delay(1_500, cancellationToken);
        if (await context.Vision.ContainsAnyTextAsync(
                ["ENTRAR NA CASA"],
                cancellationToken))
        {
            context.Logger.Warn(
                "O painel 'Entrar na Casa' ainda aparece após a manobra limitada; " +
                "a próxima sonda Start será única e exigirá menu ou garagem como sucessor positivo.");
        }
    }

    private async Task<bool> WaitForPauseMenuAfterProbeAsync(
        string initialNormalized,
        bool houseRecoveryAlreadyPerformed,
        CancellationToken cancellationToken)
    {
        const int maximumObservations = 16;
        var normalized = initialNormalized;
        var houseRecoveryUsed = houseRecoveryAlreadyPerformed;
        var additionalMenuProbeUsed = false;

        for (var observation = 1; observation <= maximumObservations; observation++)
        {
            if (IsPauseMenuText(normalized) ||
                HasAny(normalized, "MEU HORIZON", "MAPA DO MUNDO", "CENTRAL CRIATIVA", "ONLINE", "SAIR DO JOGO"))
            {
                return false;
            }

            if (IsGarageText(normalized))
            {
                return true;
            }

            if (HasAny(normalized, "ENTRAR NA CASA"))
            {
                if (!houseRecoveryUsed)
                {
                    context.Logger.State(
                        Workflow,
                        "RecuperarEntradaDurantePausa",
                        "O painel da casa apareceu durante a espera do menu; executando a única recuperação física permitida.");
                    await LeaveHouseEntranceIfNeededAsync(cancellationToken);
                    houseRecoveryUsed = true;
                }

                if (additionalMenuProbeUsed)
                {
                    break;
                }

                await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                additionalMenuProbeUsed = true;
                await Task.Delay(1_000, cancellationToken);
            }
            else if (IsPhotoModeText(normalized))
            {
                await ExitPhotoModeIfNeededAsync(cancellationToken);
                await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                await Task.Delay(1_000, cancellationToken);
            }
            else if (observation < maximumObservations)
            {
                await Task.Delay(400, cancellationToken);
            }

            if (observation < maximumObservations)
            {
                var document = await context.Vision.ReadScreenAsync(cancellationToken);
                normalized = GameVisionService.Normalize(document.Text);
            }
        }

        using var diagnosticFrame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(diagnosticFrame.Bitmap, Workflow, "AguardarMenuPausa");
        throw new CalibrationRequiredException(
            "O menu de pausa não foi confirmado após a sonda limitada e as recuperações conhecidas. " +
            $"Nenhum novo comando será enviado. OCR: '{normalized}'. Diagnóstico local: {diagnostic}");
    }

    public async Task ReturnToGarageMenuAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        var attempt = 0;
        while (attempt < maximumAttempts)
        {
            var screen = await context.Vision.ReadScreenAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(screen.Text);
            if (HasAny(normalized, "ALTERACOES NAO SALVAS", "SALVAR E CONTINUAR"))
            {
                context.Logger.State(
                    Workflow,
                    "SalvarAlteracoesPendentes",
                    "Diálogo de alterações não salvas detectado; confirmando 'Salvar e Continuar' com A.");
                await context.Input.TapAsync(GameKey.Enter, cancellationToken);
                await Task.Delay(1_500, cancellationToken);
                attempt++;
                continue;
            }

            if (IsPhotoModeExitConfirmationText(normalized))
            {
                await ConfirmPhotoModeExitAsync(cancellationToken);
                await EnsureGarageAsync(cancellationToken);
                attempt++;
                continue;
            }

            if (IsGarageMainMenuText(normalized))
            {
                return;
            }

            if (IsPauseMenuText(normalized) ||
                IsTravelConfirmationText(normalized) ||
                HasAny(normalized, TravelCardAliases))
            {
                context.Logger.State(
                    Workflow,
                    "RetomarDaPausa",
                    "A saída do submenu terminou no menu de pausa; retomando a navegação confirmada até a garagem.");
                await EnsureGarageAsync(cancellationToken);
                return;
            }

            if (!IsGarageText(normalized))
            {
                // A saída da apresentação do carro volta ao menu inicial por
                // um fade curto. Durante esse intervalo o cabeçalho e a lista
                // ainda estão escuros e o OCR pode ler somente o carro atual.
                // Aguarde passivamente um consenso 2/3: nenhum B/Up é
                // autorizado enquanto a estrutura da garagem não reaparecer
                // de forma estável.
                var stableMenu = await WaitForStableGarageMenuAsync(cancellationToken);
                if (stableMenu == GarageMenuObservationKind.MainMenu)
                {
                    return;
                }

                if (stableMenu != GarageMenuObservationKind.Submenu)
                {
                    using var unsafeFrame = await context.Capture.CaptureAsync(CancellationToken.None);
                    var unsafeDiagnostic = context.Capture.SaveDiagnostic(
                        unsafeFrame.Bitmap,
                        Workflow,
                        "RetornarMenuGaragemEstadoInseguro");
                    var unsafeObserved = screen.Text.ReplaceLineEndings(" ").Trim();
                    if (unsafeObserved.Length > 220)
                    {
                        unsafeObserved = unsafeObserved[..220] + "…";
                    }

                    throw new CalibrationRequiredException(
                        "A tela atual não foi confirmada como submenu da garagem; nenhum B/Up foi enviado. " +
                        $"OCR observado: '{unsafeObserved}'. Diagnóstico: {unsafeDiagnostic}");
                }
            }

            context.Logger.State(
                Workflow,
                "RetornarMenuGaragem",
                $"Submenu aberto; voltando com B ({attempt + 1}/{maximumAttempts}).");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);
            await Task.Delay(300, cancellationToken);
            // Depois de uma apresentação longa a garagem pode reabrir com a
            // lista invisível por inatividade. Um direcional a desperta sem
            // abrir nenhuma opção; os chamadores normalizam o foco em seguida.
            await context.Input.TapAsync(GameKey.Up, cancellationToken, 40, postDelayMs: 110);
            await Task.Delay(180, cancellationToken);
            attempt++;
        }

        var lastScreen = await context.Vision.ReadScreenAsync(CancellationToken.None);
        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "RetornarMenuGaragem");
        var observed = lastScreen.Text.ReplaceLineEndings(" ").Trim();
        if (observed.Length > 260)
        {
            observed = observed[..260] + "…";
        }
        throw new CalibrationRequiredException(
            $"Não foi possível retornar ao menu inicial da garagem. " +
            $"OCR final: '{observed}'. Diagnóstico: {diagnostic}");
    }

    private async Task<GarageMenuObservationKind> WaitForStableGarageMenuAsync(
        CancellationToken cancellationToken)
    {
        const int maximumObservations = 5;
        var recent = new Queue<GarageMenuObservationKind>(3);
        context.Logger.State(
            Workflow,
            "AguardarMenuGaragemEstavel",
            "Estrutura inconclusiva durante uma transição; aguardando passivamente consenso 2/3.");

        for (var observation = 0; observation < maximumObservations; observation++)
        {
            await Task.Delay(250, cancellationToken);
            var screen = await context.Vision.ReadScreenAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(screen.Text);
            var kind = IsGarageMainMenuText(normalized)
                ? GarageMenuObservationKind.MainMenu
                : IsGarageText(normalized)
                    ? GarageMenuObservationKind.Submenu
                    : GarageMenuObservationKind.Unknown;

            recent.Enqueue(kind);
            if (recent.Count > 3)
            {
                _ = recent.Dequeue();
            }

            if (recent.Count == 3)
            {
                if (recent.Count(item => item == GarageMenuObservationKind.MainMenu) >= 2)
                {
                    context.Logger.State(
                        Workflow,
                        "AguardarMenuGaragemEstavel",
                        "Menu principal da garagem confirmado em duas de três releituras passivas.");
                    return GarageMenuObservationKind.MainMenu;
                }

                if (recent.Count(item => item == GarageMenuObservationKind.Submenu) >= 2)
                {
                    context.Logger.State(
                        Workflow,
                        "AguardarMenuGaragemEstavel",
                        "Submenu da garagem confirmado em duas de três releituras passivas.");
                    return GarageMenuObservationKind.Submenu;
                }
            }
        }

        return GarageMenuObservationKind.Unknown;
    }

    public Task OpenBuySellTabAsync(CancellationToken cancellationToken) =>
        OpenGarageTabAsync(
            "AbaComprarEVender",
            "COMPRAR E VENDER",
            ["CONCESSIONÁRIA", "CONCESSIONARIA", "CASA DE LEILÕES", "CASA DE LEILOES"],
            cancellationToken);

    public Task OpenCarsTabAsync(CancellationToken cancellationToken) =>
        OpenGarageTabAsync(
            "AbaCarros",
            "CARROS",
            ["MEUS CARROS", "APRIMORAR E TUNAR"],
            cancellationToken);

    public async Task<int> ReadCreditsAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScaledRegionAsync(
            new RectangleF(0.85f, 0.015f, 0.145f, 0.10f),
            requestedScale: 2,
            cancellationToken);
        var candidates = ExtractFuzzyCreditNumbers(document.Text)
            .Where(value => value <= 999_999_999)
            .ToArray();
        context.Logger.State(
            Workflow,
            "LerCreditos",
            $"OCR do bloco de créditos: '{document.Text.ReplaceLineEndings(" ").Trim()}'; " +
            $"candidatos corrigidos: [{string.Join(", ", candidates)}].");

        var credits = candidates.DefaultIfEmpty(-1).Max();
        if (credits < 0)
        {
            var fallbackDocument = await context.Vision.ReadScaledRegionAsync(
                new RectangleF(0.80f, 0.00f, 0.20f, 0.14f),
                requestedScale: 2,
                cancellationToken);
            var fallbackCandidates = ExtractFuzzyCreditNumbers(fallbackDocument.Text)
                .Where(value => value <= 999_999_999)
                .ToArray();
            context.Logger.State(
                Workflow,
                "LerCreditosFallback",
                $"OCR ampliado do bloco de créditos: '{fallbackDocument.Text.ReplaceLineEndings(" ").Trim()}'; " +
                $"candidatos corrigidos: [{string.Join(", ", fallbackCandidates)}].");
            credits = fallbackCandidates.DefaultIfEmpty(-1).Max();
        }

        if (credits < 0)
        {
            throw new CalibrationRequiredException(
                "O saldo de créditos não produziu nenhum candidato OCR confiável nas duas regiões; " +
                "nenhuma compra será autorizada.");
        }

        context.Logger.State(Workflow, "LerCreditos", $"Créditos detectados: {credits:N0} CR.");
        context.Resources.SetCredits(credits, estimated: false);
        return credits;
    }

    private static IReadOnlyList<int> ExtractFuzzyCreditNumbers(string text)
    {
        // O OCR do HUD também pode trocar um separador por hífen (por exemplo,
        // "3-L280.29S" para 31.280.295). Preserve o fragmento inteiro; o
        // mínimo de três dígitos observados abaixo impede que ícones/letras
        // isolados sejam promovidos a saldo.
        return Regex.Matches(
                text.ToUpperInvariant(),
                @"[0-9A-Z][0-9A-Z.,\-\u2010\u2011\u2012\u2013\u2014\u2015]*")
            .Select(match => match.Value)
            .Where(token => token.Count(char.IsDigit) >= 3)
            .Select(token =>
            {
                var digits = new string(token
                    .Select(character => character switch
                    {
                        >= '0' and <= '9' => character,
                        // Na fonte do contador, o 3 aberto é lido como A
                        // (ex.: 31aog.78g = 31.309.789).
                        'A' => '3',
                        'B' => '8',
                        'O' or 'Q' or 'D' => '0',
                        'I' or 'L' => '1',
                        'Z' => '2',
                        'S' => '5',
                        // O OCR do contador usa 'g' para o algarismo 9 na
                        // fonte estreita do HUD (ex.: 31.30g.78g).
                        'G' => '9',
                        _ => '\0'
                    })
                    .Where(character => character != '\0')
                    .ToArray());
                return int.TryParse(digits, out var value) ? value : -1;
            })
            .Where(value => value >= 0)
            .ToArray();
    }

    private async Task OpenGarageTabAsync(
        string state,
        string expectedTab,
        IReadOnlyCollection<string> uniqueTexts,
        CancellationToken cancellationToken)
    {
        const int maximumMoves = 6;
        for (var moves = 0; moves <= maximumMoves; moves++)
        {
            var tabState = await ConfirmGarageTabStateAsync(
                expectedTab,
                state,
                cancellationToken);
            if (tabState == GarageTabState.Target)
            {
                context.Logger.State(
                    Workflow,
                    state,
                    $"Aba {expectedTab} confirmada em 2/3 pelo cabeçalho ativo ou pelo conteúdo exclusivo.");
                return;
            }

            if (tabState == GarageTabState.Unknown)
            {
                using var unknownFrame = await context.Capture.CaptureAsync(CancellationToken.None);
                var unknownDiagnostic = context.Capture.SaveDiagnostic(
                    unknownFrame.Bitmap,
                    Workflow,
                    $"{state}CabecalhoAmbiguo");
                throw new CalibrationRequiredException(
                    $"O cabeçalho da garagem não confirmou uma aba ativa inequívoca em 2/3 capturas; " +
                    $"nenhum LB adicional foi enviado. Diagnóstico: {unknownDiagnostic}");
            }

            if (moves >= maximumMoves)
            {
                break;
            }

            context.Logger.State(
                Workflow,
                state,
                $"Avançando com um pulso LB preciso de 12 ms ({moves + 1}/{maximumMoves}) e revalidando a aba ativa.");
            await context.Input.HoldPreciselyAsync(GameKey.PageUp, 12, cancellationToken);
            await Task.Delay(320, cancellationToken);
        }

        var lastScreen = await context.Vision.ReadScreenAsync(CancellationToken.None);
        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, state);
        var observed = lastScreen.Text.ReplaceLineEndings(" ").Trim();
        if (observed.Length > 220)
        {
            observed = observed[..220] + "…";
        }

        throw new CalibrationRequiredException(
            $"Não foi possível abrir a aba {expectedTab}, confirmada pelo cabeçalho ativo ou por " +
            $"[{string.Join(" | ", uniqueTexts)}]. " +
            $"OCR final: '{observed}'. Diagnóstico: {diagnostic}");
    }

    private async Task<GarageTabState> ConfirmGarageTabStateAsync(
        string expectedTab,
        string state,
        CancellationToken cancellationToken)
    {
        var expected = 0;
        var content = 0;
        var garageContext = 0;
        var visualConflicts = 0;
        var otherTabs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) => AnalyzeGarageTab(
                    bitmap,
                    document,
                    expectedTab),
                cancellationToken);
            if (string.Equals(observation.ActiveTab, expectedTab, StringComparison.Ordinal))
            {
                expected++;
            }
            else if (observation.ActiveTab is not null)
            {
                otherTabs.TryGetValue(observation.ActiveTab, out var count);
                otherTabs[observation.ActiveTab] = count + 1;
            }

            if (observation.HasExpectedContent)
            {
                content++;
            }

            if (observation.GarageContext)
            {
                garageContext++;
            }

            if (observation.VisualConflict)
            {
                visualConflicts++;
            }

            if (attempt < 2)
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        context.Logger.State(
            Workflow,
            state,
            $"Cabeçalho: {expectedTab}={expected}/3, outras=[{string.Join(", ", otherTabs.Select(pair => $"{pair.Key}={pair.Value}/3"))}], " +
            $"conteúdo exclusivo={content}/3, contexto de garagem={garageContext}/3, conflitos visuais={visualConflicts}/3.");
        if (garageContext >= 2 &&
            (expected >= 2 || content >= 2) &&
            otherTabs.Count == 0 &&
            visualConflicts == 0)
        {
            return GarageTabState.Target;
        }

        return expected == 0 &&
               visualConflicts == 0 &&
               otherTabs.Values.Any(count => count >= 2)
            ? GarageTabState.Other
            : GarageTabState.Unknown;
    }

    private static GarageTabObservation AnalyzeGarageTab(
        Bitmap bitmap,
        OcrDocument document,
        string expectedTab)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        var hasExpectedContent = expectedTab switch
        {
            "CAMPANHA" =>
                normalized.Contains("DIARIO DE COLECAO", StringComparison.Ordinal) &&
                HasAny(normalized, "LISTA DE DETALHES DO FESTIVAL", "CONFIGURACOES"),
            "COMPRAR E VENDER" =>
                normalized.Contains("CONCESSIONARIA", StringComparison.Ordinal) &&
                normalized.Contains("CASA DE LEILOES", StringComparison.Ordinal),
            "CARROS" =>
                normalized.Contains("MEUS CARROS", StringComparison.Ordinal) &&
                HasAny(normalized, "APRIMORAR E TUNAR", "DESIGNS E TINTAS"),
            _ => false
        };
        var knownSubmenu = IsPauseMenuText(normalized) ||
                           HasAny(
                               normalized,
                               "COMPRAR CARRO",
                               "IR PARA FABRICANTE",
                               "PONTOS DISPONIVEIS",
                               "MAESTRIA DE CARRO",
                               "APRIMORAMENTO PERSONALIZADO",
                               "TUNAGEM PERSONALIZADA",
                               "FABRICANTE",
                               "CORES DO FABRICANTE",
                               "ENTRAR NO CARRO",
                               "REMOVER CARRO DA GARAGEM");
        var hasGarageHeaderContext = document.Lines.Any(line =>
        {
            var centerY = (line.Y + line.Height / 2d) / bitmap.Height;
            if (centerY is < 0.135 or > 0.185)
            {
                return false;
            }

            var lineText = GameVisionService.Normalize(line.Text);
            return HasAny(
                lineText,
                "COMPRAR E VENDER",
                "GARAGEM PERSONALIZAVEL",
                "PERSONAGEM");
        });
        var garageContext = !knownSubmenu && (hasGarageHeaderContext || hasExpectedContent);
        var activeTabs = GarageTabs
            .Select(tab =>
            {
                var (darkRatio, limeRatio) = MeasureTabVisual(bitmap, tab.Region);
                return new GarageTabVisual(tab.Name, darkRatio, limeRatio);
            })
            .Where(tab =>
                tab.DarkRatio >= ActiveTabDarkRatio &&
                tab.LimeRatio >= ActiveTabLimeRatio)
            .ToArray();
        var activeTab = garageContext && activeTabs.Length == 1
            ? activeTabs[0].Name
            : null;
        return new GarageTabObservation(
            activeTab,
            garageContext && hasExpectedContent,
            garageContext,
            activeTabs.Length > 1);
    }

    private async Task TapNavigationRepeatedAsync(
        GameKey key,
        int count,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            // Direcionais de normalização não autorizam nenhuma ação por si
            // só. O destino sempre é confirmado por OCR logo depois, então
            // eles podem usar cadência curta sem enfraquecer o fail-closed.
            await context.Input.TapAsync(key, cancellationToken, 65, postDelayMs: 110);
        }
    }

    private async Task OpenMeuHorizonTabAsync(CancellationToken cancellationToken)
    {
        for (var moves = 0; moves <= MaximumPauseTabMoves; moves++)
        {
            var state = await ConfirmPauseTabStateAsync(cancellationToken);
            if (state == PauseTabState.MeuHorizon)
            {
                if (moves == 0)
                {
                    await ReenterMeuHorizonTabAsync(cancellationToken);
                }

                context.Logger.State(
                    Workflow,
                    "AbrirMeuHorizon",
                    $"Aba Meu Horizon confirmada pelo cabeçalho em 2/3 após {moves} movimento(s) LB.");
                return;
            }

            if (state == PauseTabState.Unknown)
            {
                using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
                var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "IdentificarAbaPausa");
                throw new CalibrationRequiredException(
                    "O cabeçalho do menu de pausa não produziu uma aba ativa inequívoca em 2/3 capturas; " +
                    $"nenhum LB adicional foi autorizado. Diagnóstico: {diagnostic}");
            }

            if (moves == MaximumPauseTabMoves)
            {
                break;
            }

            context.Logger.State(
                Workflow,
                "AbrirMeuHorizon",
                $"Outra aba está ativa; avançando um LB ({moves + 1}/{MaximumPauseTabMoves}).");
            await context.Input.TapAsync(GameKey.PageUp, cancellationToken, 60, postDelayMs: 140);
            await Task.Delay(320, cancellationToken);
        }

        using var finalFrame = await context.Capture.CaptureAsync(CancellationToken.None);
        var finalDiagnostic = context.Capture.SaveDiagnostic(finalFrame.Bitmap, Workflow, "AbrirMeuHorizonLimitado");
        throw new CalibrationRequiredException(
            $"A aba Meu Horizon não foi confirmada após {MaximumPauseTabMoves} movimentos LB limitados. " +
            $"Diagnóstico: {finalDiagnostic}");
    }

    private async Task ReenterMeuHorizonTabAsync(CancellationToken cancellationToken)
    {
        context.Logger.State(
            Workflow,
            "NormalizarFocoMeuHorizon",
            "Meu Horizon já estava ativo; fazendo uma única reentrada confirmada RB→LB para não reutilizar o foco de outro cartão.");
        await context.Input.TapAsync(GameKey.PageDown, cancellationToken, 60, postDelayMs: 140);
        await Task.Delay(320, cancellationToken);
        if (await ConfirmPauseTabStateAsync(cancellationToken) != PauseTabState.Other)
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(
                frame.Bitmap,
                Workflow,
                "SairMeuHorizonParaNormalizarFoco");
            throw new CalibrationRequiredException(
                "A aba Meu Horizon já estava ativa, mas um único RB não confirmou outra aba em 2/3; " +
                $"nenhum A foi enviado. Diagnóstico: {diagnostic}");
        }

        await context.Input.TapAsync(GameKey.PageUp, cancellationToken, 60, postDelayMs: 140);
        await Task.Delay(320, cancellationToken);
        if (await ConfirmPauseTabStateAsync(cancellationToken) != PauseTabState.MeuHorizon)
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(
                frame.Bitmap,
                Workflow,
                "RetornarMeuHorizonParaNormalizarFoco");
            throw new CalibrationRequiredException(
                "A reentrada limitada RB→LB não reconfirmou Meu Horizon em 2/3; " +
                $"nenhum cartão foi aberto. Diagnóstico: {diagnostic}");
        }

        context.Logger.State(
            Workflow,
            "FocoMeuHorizonNormalizado",
            "A reentrada RB→LB foi confirmada; o cartão de viagem ainda será validado por texto e contorno antes de A.");
    }

    private async Task<PauseTabState> ConfirmPauseTabStateAsync(CancellationToken cancellationToken)
    {
        var meuHorizon = 0;
        var other = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(AnalyzePauseTab, cancellationToken);
            if (observation.State == PauseTabState.MeuHorizon)
            {
                meuHorizon++;
            }
            else if (observation.State == PauseTabState.Other)
            {
                other++;
            }

            if (attempt < 2)
            {
                await Task.Delay(140, cancellationToken);
            }
        }

        context.Logger.State(
            Workflow,
            "IdentificarAbaPausa",
            $"Cabeçalho: Meu Horizon={meuHorizon}/3, outra aba={other}/3.");
        if (meuHorizon >= 2 && other == 0)
        {
            return PauseTabState.MeuHorizon;
        }

        return other >= 2 && meuHorizon == 0
            ? PauseTabState.Other
            : PauseTabState.Unknown;
    }

    private static PauseTabObservation AnalyzePauseTab(Bitmap bitmap, OcrDocument _)
    {
        // Este analisador só é chamado depois que a estrutura StreetMenu foi
        // confirmada. Os rótulos pequenos do cabeçalho oscilam no Windows OCR;
        // a aba ativa, porém, possui fundo escuro e sublinhado lime em regiões
        // fixas e deve ser a única que satisfaz ambos os sinais no mesmo frame.
        var activeTabs = PauseTabs
            .Select(tab =>
            {
                var (darkRatio, _) = MeasureTabVisual(bitmap, tab.Region);
                var underlineLimeRatio = MeasurePauseTabUnderlineLimeRatio(bitmap, tab.Region);
                return new PauseTabVisual(tab.Name, darkRatio, underlineLimeRatio);
            })
            .Where(tab =>
                tab.DarkRatio >= ActiveTabDarkRatio &&
                tab.UnderlineLimeRatio >= PauseTabUnderlineLimeRatio)
            .ToArray();
        if (activeTabs.Length != 1)
        {
            return new PauseTabObservation(PauseTabState.Unknown, null, 0, 0);
        }

        var active = activeTabs[0];
        return new PauseTabObservation(
            active.Name == "MEU HORIZON" ? PauseTabState.MeuHorizon : PauseTabState.Other,
            active.Name,
            active.DarkRatio,
            active.UnderlineLimeRatio);
    }

    private static double MeasurePauseTabUnderlineLimeRatio(
        Bitmap bitmap,
        RectangleF normalizedRegion)
    {
        var region = ToPixelRegion(bitmap, normalizedRegion);
        var underlineTop = region.Top + (int)Math.Floor(region.Height * PauseTabUnderlineStartRatio);
        var bestRowRatio = 0d;
        for (var y = underlineTop; y < region.Bottom; y++)
        {
            var lime = 0;
            var sampled = 0;
            for (var x = region.Left; x < region.Right; x += 2)
            {
                sampled++;
                if (IsLime(bitmap.GetPixel(x, y)))
                {
                    lime++;
                }
            }

            if (sampled > 0)
            {
                bestRowRatio = Math.Max(bestRowRatio, lime / (double)sampled);
            }
        }

        return bestRowRatio;
    }

    private static (double DarkRatio, double LimeRatio) MeasureTabVisual(
        Bitmap bitmap,
        RectangleF normalizedRegion)
    {
        var region = ToPixelRegion(bitmap, normalizedRegion);
        var dark = 0;
        var lime = 0;
        var sampled = 0;
        for (var y = region.Top; y < region.Bottom; y += 2)
        {
            for (var x = region.Left; x < region.Right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                sampled++;
                if (color.R <= 75 && color.G <= 75 && color.B <= 75)
                {
                    dark++;
                }

                if (IsLime(color))
                {
                    lime++;
                }
            }
        }

        return sampled == 0
            ? (0, 0)
            : (dark / (double)sampled, lime / (double)sampled);
    }

    private async Task WaitForFocusedTravelCardAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var recent = new Queue<TravelCardObservation>(3);
        var bestOutline = 0d;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await context.Vision.AnalyzeScreenAsync(AnalyzeTravelCard, cancellationToken);
            bestOutline = Math.Max(bestOutline, observation.OutlineRatio);
            recent.Enqueue(observation);
            if (recent.Count > 3)
            {
                _ = recent.Dequeue();
            }

            if (recent.Count == 3 && HasStableFocusedTravelCard(recent))
            {
                context.Logger.State(
                    Workflow,
                    "ConfirmarCartaoViagem",
                    $"Texto e contorno lime do cartão de viagem confirmados em 2/3; melhor contorno={bestOutline:P1}.");
                return;
            }

            await Task.Delay(180, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "ConfirmarCartaoViagem");
        throw new CalibrationRequiredException(
            "A aba Meu Horizon foi confirmada, mas o texto estável e o contorno lime do cartão de viagem " +
            $"não coincidiram em 2/3 capturas. Melhor contorno={bestOutline:P1}. Diagnóstico: {diagnostic}");
    }

    private static TravelCardObservation AnalyzeTravelCard(Bitmap bitmap, OcrDocument document)
    {
        TravelCardObservation best = new(false, 0, 0, 0);
        foreach (var line in document.Lines)
        {
            var centerY = line.Center.Y / (double)bitmap.Height;
            if (centerY is < 0.24 or > 0.92)
            {
                continue;
            }

            var normalized = GameVisionService.Normalize(line.Text);
            if (!TravelCardAliases.Any(alias => normalized.Contains(alias, StringComparison.Ordinal)))
            {
                continue;
            }

            var outline = BestTravelCardOutlineRatio(bitmap, line);
            if (!best.TextVisible || outline > best.OutlineRatio)
            {
                best = new TravelCardObservation(
                    true,
                    outline,
                    line.Center.X / (double)bitmap.Width,
                    line.Center.Y / (double)bitmap.Height);
            }
        }

        return best;
    }

    private static bool HasStableFocusedTravelCard(IEnumerable<TravelCardObservation> observations)
    {
        var samples = observations.ToArray();
        if (samples.Length < 3 ||
            !samples[^1].TextVisible ||
            samples[^1].OutlineRatio < TravelCardOutlineRatio)
        {
            return false;
        }

        var confirmed = samples
            .Where(observation =>
                observation.TextVisible &&
                observation.OutlineRatio >= TravelCardOutlineRatio)
            .ToArray();
        for (var first = 0; first < confirmed.Length; first++)
        {
            for (var second = first + 1; second < confirmed.Length; second++)
            {
                if (Math.Abs(confirmed[first].CenterX - confirmed[second].CenterX) <= 0.035 &&
                    Math.Abs(confirmed[first].CenterY - confirmed[second].CenterY) <= 0.035)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double BestTravelCardOutlineRatio(Bitmap bitmap, OcrLine line)
    {
        ReadOnlySpan<float> widths = [0.28f, 0.40f, 0.52f, 0.66f];
        ReadOnlySpan<float> heights = [0.12f, 0.18f, 0.26f, 0.34f];
        ReadOnlySpan<float> verticalOffsets = [-0.12f, -0.06f, 0, 0.06f];
        var centerX = (float)(line.Center.X / (double)bitmap.Width);
        var lineCenterY = (float)(line.Center.Y / (double)bitmap.Height);
        var best = 0d;
        foreach (var width in widths)
        {
            foreach (var height in heights)
            {
                foreach (var offset in verticalOffsets)
                {
                    var centerY = lineCenterY + offset;
                    var region = new RectangleF(
                        Math.Clamp(centerX - width / 2, 0, 1 - width),
                        Math.Clamp(centerY - height / 2, 0, 1 - height),
                        width,
                        height);
                    best = Math.Max(best, LimeHorizontalBorderRatio(bitmap, region));
                }
            }
        }

        return best;
    }

    private static double LimeHorizontalBorderRatio(Bitmap bitmap, RectangleF normalizedRegion)
    {
        var region = ToPixelRegion(bitmap, normalizedRegion);
        var centerY = region.Top + region.Height / 2;
        var bestAbove = 0d;
        var bestBelow = 0d;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var matching = 0;
            var sampled = 0;
            for (var x = region.Left; x < region.Right; x += 2)
            {
                sampled++;
                if (IsLime(bitmap.GetPixel(x, y)))
                {
                    matching++;
                }
            }

            var ratio = sampled == 0 ? 0 : matching / (double)sampled;
            if (y < centerY)
            {
                bestAbove = Math.Max(bestAbove, ratio);
            }
            else
            {
                bestBelow = Math.Max(bestBelow, ratio);
            }
        }

        return Math.Min(bestAbove, bestBelow);
    }

    private static Rectangle ToPixelRegion(Bitmap bitmap, RectangleF normalizedRegion)
    {
        var left = Math.Clamp((int)Math.Round(bitmap.Width * normalizedRegion.Left), 0, bitmap.Width - 1);
        var top = Math.Clamp((int)Math.Round(bitmap.Height * normalizedRegion.Top), 0, bitmap.Height - 1);
        var right = Math.Clamp(
            (int)Math.Round(bitmap.Width * normalizedRegion.Right),
            left + 1,
            bitmap.Width);
        var bottom = Math.Clamp(
            (int)Math.Round(bitmap.Height * normalizedRegion.Bottom),
            top + 1,
            bitmap.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsLime(Color color) =>
        color.R >= 130 &&
        color.G >= 180 &&
        color.B <= 90 &&
        color.G > color.B * 2;

    private async Task<TravelDecisionState> WaitForTravelDecisionAsync(
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        OcrDocument? lastDocument = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastDocument = await context.Vision.ReadScreenAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(lastDocument.Text);
            if (IsTravelConfirmationText(normalized))
            {
                context.Logger.State(
                    Workflow,
                    "ResultadoDaViagem",
                    "Confirmação de viagem reconhecida pelo título e pela pergunta, sem depender do OCR de 'Sim'.");
                return TravelDecisionState.Confirmation;
            }

            if (IsGarageText(normalized))
            {
                context.Logger.State(
                    Workflow,
                    "ResultadoDaViagem",
                    "A garagem abriu diretamente, sem confirmação pendente.");
                return TravelDecisionState.Garage;
            }

            await Task.Delay(300, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "ResultadoDaViagem");
        var observed = lastDocument?.Text.ReplaceLineEndings(" ").Trim();
        if (observed?.Length > 220)
        {
            observed = observed[..220] + "…";
        }

        throw new CalibrationRequiredException(
            "A viagem não abriu uma confirmação reconhecível nem chegou à garagem em oito segundos. " +
            $"OCR observado: '{observed}'. Diagnóstico: {diagnostic}");
    }

    private async Task<bool> IsTravelYesFocusedAsync(CancellationToken cancellationToken)
    {
        var confirmations = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(
                AnalyzeTravelConfirmation,
                cancellationToken);
            if (observation.DialogVisible && observation.YesLimeRatio >= 0.04)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(140, cancellationToken);
            }
        }

        context.Logger.State(
            Workflow,
            "ConfirmarViagem",
            $"Foco em 'Sim' confirmado por CV em {confirmations}/3 capturas.");
        return confirmations >= 2;
    }

    private static TravelConfirmationObservation AnalyzeTravelConfirmation(
        Bitmap bitmap,
        OcrDocument document) =>
        AnalyzeYesConfirmation(bitmap, document, IsTravelConfirmationText);

    private static TravelConfirmationObservation AnalyzePhotoModeExitConfirmation(
        Bitmap bitmap,
        OcrDocument document) =>
        AnalyzeYesConfirmation(bitmap, document, IsPhotoModeExitConfirmationText);

    private static TravelConfirmationObservation AnalyzeYesConfirmation(
        Bitmap bitmap,
        OcrDocument document,
        Func<string, bool> isExpectedDialog)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        var left = Math.Clamp((int)Math.Round(bitmap.Width * TravelYesRegion.Left), 0, bitmap.Width - 1);
        var top = Math.Clamp((int)Math.Round(bitmap.Height * TravelYesRegion.Top), 0, bitmap.Height - 1);
        var right = Math.Clamp(
            (int)Math.Round(bitmap.Width * TravelYesRegion.Right),
            left + 1,
            bitmap.Width);
        var bottom = Math.Clamp(
            (int)Math.Round(bitmap.Height * TravelYesRegion.Bottom),
            top + 1,
            bitmap.Height);
        var matching = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 2)
        {
            for (var x = left; x < right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                sampled++;
                if (color.R >= 130 &&
                    color.G >= 180 &&
                    color.B <= 90 &&
                    color.G > color.B * 2)
                {
                    matching++;
                }
            }
        }

        return new TravelConfirmationObservation(
            isExpectedDialog(normalized),
            sampled == 0 ? 0 : matching / (double)sampled);
    }

    private async Task WaitForGarageConfirmedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        OcrDocument? lastDocument = null;
        var recent = new Queue<bool>(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastDocument = await context.Vision.ReadScreenAsync(cancellationToken);
            var isMainMenu = IsGarageMainMenuText(GameVisionService.Normalize(lastDocument.Text));
            recent.Enqueue(isMainMenu);
            if (recent.Count > 3)
            {
                _ = recent.Dequeue();
            }

            if (isMainMenu && recent.Count == 3 && recent.Count(value => value) >= 2)
            {
                context.Logger.State(
                    Workflow,
                    "AguardarGaragem",
                    "Menu principal da garagem confirmado em duas de três leituras passivas.");
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "AguardarGaragem");
        var observed = lastDocument?.Text.ReplaceLineEndings(" ").Trim();
        if (observed?.Length > 220)
        {
            observed = observed[..220] + "…";
        }

        throw new CalibrationRequiredException(
            "A viagem foi confirmada, mas a estrutura da garagem não apareceu em dois minutos. " +
            $"OCR observado: '{observed}'. Diagnóstico: {diagnostic}");
    }

    private static bool IsGarageText(string normalized)
    {
        // A aba Carros do menu de pausa compartilha textos como "Meus Carros"
        // e "Aprimorar e Tunar" com a garagem. As abas exclusivas do cabeçalho
        // da pausa prevalecem para que esses rótulos nunca autorizem a rota de
        // submenus da casa.
        if (IsPauseMenuText(normalized) ||
            IsTravelConfirmationText(normalized) ||
            HasAny(normalized, TravelCardAliases))
        {
            return false;
        }

        // "Dirigir" é exclusivo do menu inicial da garagem. Exigir que o OCR
        // leia simultaneamente todas as abas tornava a detecção frágil.
        if (IsGarageMainMenuText(normalized))
        {
            return true;
        }

        // Os workflows podem chamar EnsureGarage enquanto ainda estão em um
        // submenu interno da casa. Esses textos não existem no menu da rua e
        // são mais seguros do que inferir o estado apenas pela ausência das
        // abas de pausa. Sem esta lista, a Maestria era confundida com a rua e
        // um Esc desviava a automação para uma categoria diferente.
        if (HasAny(
                normalized,
                "PONTOS DISPONIVEIS",
                "MAESTRIA DE CARRO",
                "APRIMORAMENTO PERSONALIZADO",
                "TUNAGEM PERSONALIZADA",
                "APRIMORAR E TUNAR",
                "MEUS CARROS",
                "COMPRAR CARRO",
                "CORES DO FABRICANTE",
                "IR PARA FABRICANTE",
                "FABRICANTE"))
        {
            return true;
        }

        // A prévia de compra mostra somente o carro, o preço e os comandos do
        // rodapé. Esse conjunto não existe na rua e permite tratá-la como um
        // submenu da concessionária, voltando com B pela rota limitada do
        // navegador. Não aceite o preço isoladamente (ele também aparece em
        // outros contextos).
        var dealershipPricePreview =
            Regex.IsMatch(normalized, @"(?<![0-9])100[.\s]?000(?![0-9])") &&
            normalized.Contains("VOLTAR", StringComparison.Ordinal) &&
            normalized.Contains("MUDAR CAMERA", StringComparison.Ordinal);
        if (dealershipPricePreview)
        {
            return true;
        }

        return false;
    }

    private static bool IsPauseMenuText(string normalized)
    {
        string[] pauseOnlyMarkers =
        [
            "MEU HORIZON",
            "ONLINE",
            "CENTRAL CRIATIVA",
            "LOJA",
            "MAPA DO MUNDO",
            "SAIR DO JOGO"
        ];
        return pauseOnlyMarkers.Count(marker =>
            normalized.Contains(marker, StringComparison.Ordinal)) >= 2;
    }

    private static bool IsTravelConfirmationText(string normalized) =>
        HasAny(normalized, TravelConfirmationTitleAliases) &&
        normalized.Contains("QUER FAZER UMA VIAGEM", StringComparison.Ordinal);

    private static bool IsGarageMainMenuText(string normalized)
    {
        // As abas CAMPANHA / COMPRAR E VENDER / CARROS permanecem visíveis
        // dentro de vários submenus. Um único item também não basta sem
        // rejeitar estados internos: Comprar Carro contém "Concessionária"
        // no rodapé. Rejeite primeiro as telas internas conhecidas e só então
        // aceite um item da lista principal; isso tolera uma linha omitida
        // pelo OCR sem voltar ao falso positivo original.
        if (IsPauseMenuText(normalized))
        {
            return false;
        }

        if (!HasAny(normalized, "CAMPANHA", "COMPRAR E VENDER", "CARROS"))
        {
            return false;
        }

        if (HasAny(
                normalized,
                "COMPRAR CARRO",
                "IR PARA FABRICANTE",
                "PONTOS DISPONIVEIS",
                "MAESTRIA DE CARRO",
                "APRIMORAMENTO PERSONALIZADO",
                "TUNAGEM PERSONALIZADA",
                "FABRICANTE"))
        {
            return false;
        }

        var campaignContent = normalized.Contains("DIARIO DE COLECAO", StringComparison.Ordinal) &&
                              HasAny(normalized, "LISTA DE DETALHES DO FESTIVAL", "CONFIGURACOES");
        var buySellContent = normalized.Contains("CONCESSIONARIA", StringComparison.Ordinal) &&
                             normalized.Contains("CASA DE LEILOES", StringComparison.Ordinal);
        var carsContent = normalized.Contains("MEUS CARROS", StringComparison.Ordinal) &&
                          HasAny(normalized, "APRIMORAR E TUNAR", "DESIGNS E TINTAS");
        return campaignContent ||
               buySellContent ||
               carsContent;
    }

    private static bool HasAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private sealed record PauseTabDefinition(string Name, RectangleF Region);

    private sealed record PauseTabVisual(
        string Name,
        double DarkRatio,
        double UnderlineLimeRatio);

    private sealed record GarageTabDefinition(string Name, RectangleF Region);

    private sealed record GarageTabVisual(string Name, double DarkRatio, double LimeRatio);

    private sealed record GarageTabObservation(
        string? ActiveTab,
        bool HasExpectedContent,
        bool GarageContext,
        bool VisualConflict);

    private sealed record PauseTabObservation(
        PauseTabState State,
        string? ActiveTab,
        double DarkRatio,
        double UnderlineLimeRatio);

    private sealed record TravelCardObservation(
        bool TextVisible,
        double OutlineRatio,
        double CenterX,
        double CenterY);

    private sealed record TravelConfirmationObservation(bool DialogVisible, double YesLimeRatio);

    private sealed record WelcomeContinueObservation(
        bool SignatureVisible,
        bool Focused,
        bool Ready,
        double OutlineRatio,
        string ObservedText);

    private sealed record EventChallengeRatingOptionDefinition(
        EventChallengeRatingOption Option,
        string CompactText,
        RectangleF Region);

    private sealed record EventChallengeRatingObservation(
        bool DialogVisible,
        EventChallengeRatingOption FocusedOption,
        double FocusScore);

    private enum GarageMenuObservationKind
    {
        Unknown,
        MainMenu,
        Submenu
    }

    private enum GarageTabState
    {
        Unknown,
        Other,
        Target
    }

    private enum PauseTabState
    {
        Unknown,
        Other,
        MeuHorizon
    }

    private enum EventChallengeRatingOption
    {
        Unknown,
        Curtir,
        NaoGostei,
        Cancelar
    }

    private enum TravelDecisionState
    {
        Confirmation,
        Garage
    }

    private enum WelcomeContinueState
    {
        Absent,
        Ambiguous,
        Stable
    }
}
