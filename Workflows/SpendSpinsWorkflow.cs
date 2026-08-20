using System.Text.RegularExpressions;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed class SpendSpinsWorkflow : IMacroWorkflow
{
    private const string Workflow = "GastarWheelspins";
    private const int PollDelayMs = 750;

    private static readonly string[] SafeDuplicateActions =
    [
        "ADICIONAR À GARAGEM",
        "ADICIONAR A GARAGEM",
        "MANTER CARRO",
        "FICAR COM O CARRO",
        "GUARDAR NA GARAGEM"
    ];

    private static readonly string[] PrizeActions =
    [
        "COLETAR PRÊMIO",
        "COLETAR PREMIO",
        "RESGATAR PRÊMIO",
        "RESGATAR PREMIO",
        "CONTINUAR",
        "ACEITAR"
    ];

    public MacroKind Kind => MacroKind.GastarWheelspins;

    public async Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        var spinsStarted = 0;
        try
        {
            context.Logger.State(
                Workflow,
                "Preparar",
                "Fluxo conservador: Super Wheelspins primeiro, uma leitura OCR por ciclo e nenhuma confirmação sem estado visual conhecido.");

            var screen = await LocateEntryAsync(context, cancellationToken);
            if (IsSpinSession(screen.Kind))
            {
                var resumed = await DrainOpenSessionAsync(context, screen, cancellationToken);
                spinsStarted += resumed.SpinsStarted;
                screen = await ReturnToHubAsync(context, resumed.Screen, cancellationToken);
            }

            screen = await EnsureHubAsync(context, screen, cancellationToken);
            foreach (var spinType in new[] { SpinType.Super, SpinType.Standard })
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var opened = await TryOpenSpinAsync(context, screen, spinType, cancellationToken);
                    if (opened is null)
                    {
                        break;
                    }

                    var drained = await DrainOpenSessionAsync(context, opened, cancellationToken);
                    spinsStarted += drained.SpinsStarted;
                    screen = await ReturnToHubAsync(context, drained.Screen, cancellationToken);
                    if (drained.Exhausted)
                    {
                        break;
                    }
                }
            }

            context.Logger.State(
                Workflow,
                spinsStarted == 0 ? "SaldoZero" : "Concluido",
                spinsStarted == 0
                    ? "Nenhum Wheelspin disponível foi confirmado; encerrando sem iniciar giro."
                    : $"{spinsStarted} giro(s) iniciado(s). Os saldos confirmados chegaram a zero.");
        }
        finally
        {
            // O coordenador também possui uma barreira final. Mantemos esta
            // liberação local para cobrir cancelamento em qualquer espera OCR.
            await context.Input.ReleaseAllAsync();
        }
    }

    private static async Task<Screen> LocateEntryAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var screen = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
        if (screen.Kind is ScreenKind.Hub or ScreenKind.PauseMenu || IsSpinSession(screen.Kind))
        {
            return screen.Kind == ScreenKind.PauseMenu
                ? await OpenMyHorizonAsync(context, screen, cancellationToken)
                : screen;
        }

        var gameContext = context.GameContext.Classify(screen.Document);
        if (gameContext.Kind == GameContextKind.ControllerDisconnected)
        {
            context.Logger.State(
                Workflow,
                "ReconectarControle",
                "Controle virtual já validado; confirmando o aviso do jogo uma única vez.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken, 120);
            await Task.Delay(1_200, cancellationToken);
            screen = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
            gameContext = context.GameContext.Classify(screen.Document);
        }

        if (gameContext.Kind is GameContextKind.Street or GameContextKind.Unknown)
        {
            context.Logger.State(
                Workflow,
                "AbrirMenu",
                gameContext.Kind == GameContextKind.Street
                    ? "Rua confirmada; abrindo o menu e validando a transição por OCR."
                    : "Tela sem texto suficiente; fazendo uma única sonda segura por Menu e validando o resultado por OCR.");
            await context.Input.TapAsync(GameKey.Menu, cancellationToken);
            screen = await WaitAfterActionAsync(
                context,
                screen,
                candidate => candidate.Kind is ScreenKind.PauseMenu or ScreenKind.Hub,
                "AbrirMenu",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            return screen.Kind == ScreenKind.Hub
                ? screen
                : await OpenMyHorizonAsync(context, screen, cancellationToken);
        }

        throw await CalibrationFailureAsync(
            context,
            "TelaInicial",
            gameContext.Kind switch
            {
                GameContextKind.Garage => "Saia da garagem e deixe o carro na rua antes de iniciar este BOT.",
                GameContextKind.Event or GameContextKind.EventMenu => "Saia do evento antes de gastar Wheelspins.",
                _ => "Abra o menu de pausa ou uma tela de Wheelspin antes de iniciar; o contexto atual não foi reconhecido com segurança."
            },
            screen);
    }

    private static async Task<Screen> EnsureHubAsync(
        AutomationContext context,
        Screen screen,
        CancellationToken cancellationToken)
    {
        if (screen.Kind == ScreenKind.Hub)
        {
            return screen;
        }

        if (screen.Kind == ScreenKind.PauseMenu)
        {
            return await OpenMyHorizonAsync(context, screen, cancellationToken);
        }

        throw await CalibrationFailureAsync(
            context,
            "MeuHorizon",
            "Não foi possível confirmar a página Meu Horizon antes de escolher um Wheelspin.",
            screen);
    }

    private static async Task<Screen> OpenMyHorizonAsync(
        AutomationContext context,
        Screen screen,
        CancellationToken cancellationToken)
    {
        var line = FindLine(screen.Document, ["MEU HORIZON"])
            ?? throw await CalibrationFailureAsync(
                context,
                "MeuHorizon",
                "O menu foi detectado, mas o texto 'Meu Horizon' não ficou legível.",
                screen);

        context.Logger.State(Workflow, "MeuHorizon", "Abrindo a aba pelo texto detectado; não haverá fallback por sequência fixa.");
        await context.Input.ClickClientAsync(line.Center.X, line.Center.Y, cancellationToken);
        return await WaitAfterActionAsync(
            context,
            screen,
            candidate => candidate.Kind == ScreenKind.Hub,
            "MeuHorizon",
            TimeSpan.FromSeconds(25),
            cancellationToken);
    }

    private static async Task<Screen?> TryOpenSpinAsync(
        AutomationContext context,
        Screen hub,
        SpinType spinType,
        CancellationToken cancellationToken)
    {
        var tile = FindSpinLine(hub.Document, spinType);
        if (tile is null)
        {
            context.Logger.State(Workflow, "Indisponivel", $"Cartão {DisplayName(spinType)} não detectado; seguindo sem enviar entrada.");
            return null;
        }

        if (ReadCountNearTile(hub.Document, tile) == 0)
        {
            await Task.Delay(600, cancellationToken);
            var confirmation = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
            var confirmedTile = FindSpinLine(confirmation.Document, spinType);
            if (confirmation.Kind == ScreenKind.Hub && confirmedTile is not null &&
                ReadCountNearTile(confirmation.Document, confirmedTile) == 0)
            {
                context.Logger.State(Workflow, "SaldoZero", $"{DisplayName(spinType)} confirmado em zero por duas leituras; nenhum clique enviado.");
                return null;
            }
        }

        context.Logger.State(Workflow, "Abrir", $"Abrindo {DisplayName(spinType)} pelo rótulo OCR.");
        await context.Input.ClickClientAsync(tile.Center.X, tile.Center.Y, cancellationToken);

        // Se o cartão estiver desabilitado, a tela não muda. Isso é tratado
        // como indisponibilidade segura, nunca com Enter de tentativa.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(18);
        var departed = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
            departed |= HasDeparted(hub, candidate);
            if (departed && IsSpinSession(candidate.Kind))
            {
                return candidate;
            }

            await Task.Delay(PollDelayMs, cancellationToken);
        }

        if (!departed)
        {
            context.Logger.State(Workflow, "SaldoZero", $"O cartão {DisplayName(spinType)} permaneceu desabilitado; nenhum giro foi iniciado.");
            return null;
        }

        throw await CalibrationFailureAsync(
            context,
            "AbrirWheelspin",
            $"A tela mudou após abrir {DisplayName(spinType)}, mas nenhum estado seguro ficou reconhecível.",
            Analyze(await context.Vision.ReadScreenAsync(CancellationToken.None)));
    }

    private static async Task<DrainResult> DrainOpenSessionAsync(
        AutomationContext context,
        Screen screen,
        CancellationToken cancellationToken)
    {
        var spinsStarted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            screen = await ResolvePrizeActionsAsync(context, screen, cancellationToken);

            if (screen.Kind == ScreenKind.Hub)
            {
                return new DrainResult(screen, spinsStarted, Exhausted: false);
            }

            if (screen.Kind == ScreenKind.Zero)
            {
                await Task.Delay(600, cancellationToken);
                var confirmation = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
                if (confirmation.Kind != ScreenKind.Zero)
                {
                    throw await CalibrationFailureAsync(context, "ConfirmarZero", "O saldo zero não se repetiu em duas leituras; nenhuma entrada foi enviada.", confirmation);
                }

                context.Logger.State(Workflow, "SaldoZero", "Saldo zero confirmado por duas leituras OCR.");
                return new DrainResult(confirmation, spinsStarted, Exhausted: true);
            }

            if (screen.Kind != ScreenKind.Ready)
            {
                throw await CalibrationFailureAsync(context, "EstadoDoGiro", "A tela não oferece uma ação de giro reconhecida.", screen);
            }

            await Task.Delay(600, cancellationToken);
            var readyConfirmation = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
            if (readyConfirmation.Kind == ScreenKind.Zero)
            {
                screen = readyConfirmation;
                continue;
            }

            if (readyConfirmation.Kind != ScreenKind.Ready || readyConfirmation.Type != screen.Type)
            {
                throw await CalibrationFailureAsync(context, "ConfirmarGiro", "A opção Girar não permaneceu estável em duas leituras; o giro não foi iniciado.", readyConfirmation);
            }

            context.Logger.State(Workflow, "Girar", $"{DisplayName(screen.Type ?? SpinType.Standard)} confirmado em duas leituras; iniciando exatamente um giro.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            spinsStarted++;
            screen = await WaitAfterActionAsync(
                context,
                readyConfirmation,
                candidate => candidate.Kind is ScreenKind.Prize or ScreenKind.Duplicate or ScreenKind.Ready or ScreenKind.Zero or ScreenKind.Hub or ScreenKind.Purchase,
                "ResultadoDoGiro",
                TimeSpan.FromMinutes(2),
                cancellationToken);
        }
    }

    private static async Task<Screen> ResolvePrizeActionsAsync(
        AutomationContext context,
        Screen screen,
        CancellationToken cancellationToken)
    {
        for (var action = 0; action < 12; action++)
        {
            if (screen.Kind == ScreenKind.Purchase)
            {
                throw await CalibrationFailureAsync(
                    context,
                    "CompraBloqueada",
                    "O jogo ofereceu comprar outro Wheelspin. O BOT não gasta créditos nem confirma essa tela.",
                    screen);
            }

            string[] expectedActions;
            if (screen.Kind == ScreenKind.Duplicate)
            {
                expectedActions = SafeDuplicateActions;
            }
            else if (screen.Kind == ScreenKind.Prize)
            {
                expectedActions = PrizeActions;
            }
            else
            {
                return screen;
            }

            var line = FindLine(screen.Document, expectedActions);
            if (line is null)
            {
                var message = screen.Kind == ScreenKind.Duplicate
                    ? "Carro duplicado detectado, mas não há opção segura para mantê-lo. Vender ou presentear não será escolhido automaticamente."
                    : "Prêmio detectado sem ação textual segura para continuar.";
                throw await CalibrationFailureAsync(context, "ResolverPremio", message, screen);
            }

            context.Logger.State(
                Workflow,
                screen.Kind == ScreenKind.Duplicate ? "ManterDuplicado" : "ColetarPremio",
                $"Executando somente a ação confirmada por OCR: '{line.Text}'.");
            var before = screen;
            await context.Input.ClickClientAsync(line.Center.X, line.Center.Y, cancellationToken);
            screen = await WaitAfterActionAsync(
                context,
                before,
                candidate => candidate.Kind is ScreenKind.Prize or ScreenKind.Duplicate or ScreenKind.Ready or ScreenKind.Zero or ScreenKind.Hub or ScreenKind.Purchase,
                "ConfirmarPremio",
                TimeSpan.FromSeconds(40),
                cancellationToken);
        }

        throw await CalibrationFailureAsync(context, "ResolverPremio", "Muitas telas de prêmio consecutivas; parando para evitar repetição cega.", screen);
    }

    private static async Task<Screen> ReturnToHubAsync(
        AutomationContext context,
        Screen screen,
        CancellationToken cancellationToken)
    {
        if (screen.Kind == ScreenKind.Hub)
        {
            return screen;
        }

        if (screen.Kind != ScreenKind.Zero)
        {
            throw await CalibrationFailureAsync(context, "VoltarMeuHorizon", "O BOT só volta após confirmar saldo zero.", screen);
        }

        context.Logger.State(Workflow, "VoltarMeuHorizon", "Saldo zero confirmado; voltando uma única tela e validando o destino.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        var result = await WaitAfterActionAsync(
            context,
            screen,
            candidate => candidate.Kind is ScreenKind.Hub or ScreenKind.PauseMenu,
            "VoltarMeuHorizon",
            TimeSpan.FromSeconds(25),
            cancellationToken);
        return result.Kind == ScreenKind.Hub
            ? result
            : await OpenMyHorizonAsync(context, result, cancellationToken);
    }

    private static async Task<Screen> WaitAfterActionAsync(
        AutomationContext context,
        Screen before,
        Func<Screen, bool> accept,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var departed = false;
        Screen? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = Analyze(await context.Vision.ReadScreenAsync(cancellationToken));
            departed |= HasDeparted(before, last);
            if (departed && accept(last))
            {
                return last;
            }

            await Task.Delay(PollDelayMs, cancellationToken);
        }

        throw await CalibrationFailureAsync(
            context,
            state,
            "A ação foi enviada, mas a transição visual esperada não foi confirmada; nenhum fallback será tentado.",
            last ?? before);
    }

    private static Screen Analyze(OcrDocument document)
    {
        var normalized = Canonical(document.Text);
        SpinType? type = normalized.Contains("SUPER WHEELSPIN", StringComparison.Ordinal)
            ? SpinType.Super
            : normalized.Contains("WHEELSPIN", StringComparison.Ordinal)
                ? SpinType.Standard
                : null;

        if (HasAny(normalized, "COMPRAR WHEELSPIN", "COMPRAR OUTRO WHEELSPIN", "USAR CRÉDITOS", "USAR CREDITOS"))
        {
            return new Screen(ScreenKind.Purchase, type, document, normalized);
        }

        var hasSafeDuplicate = FindLine(document, SafeDuplicateActions) is not null;
        if (hasSafeDuplicate || HasAny(normalized, "VENDER CARRO", "PRESENTEAR CARRO", "CARRO DUPLICADO"))
        {
            return new Screen(ScreenKind.Duplicate, type, document, normalized);
        }

        if (IsZeroText(normalized))
        {
            return new Screen(ScreenKind.Zero, type, document, normalized);
        }

        if (type is not null && FindLine(document, ["GIRAR", "INICIAR WHEELSPIN"]) is not null)
        {
            return new Screen(ScreenKind.Ready, type, document, normalized);
        }

        if (FindLine(document, PrizeActions) is not null &&
            HasAny(normalized, "PRÊMIO", "PREMIO", "WHEELSPIN", "CARRO", "CR"))
        {
            return new Screen(ScreenKind.Prize, type, document, normalized);
        }

        var hasSpinTile = FindSpinLine(document, SpinType.Super) is not null ||
                          FindSpinLine(document, SpinType.Standard) is not null;
        if (normalized.Contains("MEU HORIZON", StringComparison.Ordinal) && hasSpinTile)
        {
            return new Screen(ScreenKind.Hub, null, document, normalized);
        }

        if (normalized.Contains("MEU HORIZON", StringComparison.Ordinal) &&
            HasAny(normalized, "CAMPANHA", "ONLINE", "MAPA DO MUNDO", "CONFIGURAÇÕES", "CONFIGURACOES"))
        {
            return new Screen(ScreenKind.PauseMenu, null, document, normalized);
        }

        return new Screen(ScreenKind.Unknown, type, document, normalized);
    }

    private static OcrLine? FindSpinLine(OcrDocument document, SpinType spinType) =>
        document.Lines
            .Select(line => (Line: line, Text: Canonical(line.Text)))
            .Where(item => spinType == SpinType.Super
                ? item.Text.Contains("SUPER WHEELSPIN", StringComparison.Ordinal)
                : item.Text.Contains("WHEELSPIN", StringComparison.Ordinal) &&
                  !item.Text.Contains("SUPER WHEELSPIN", StringComparison.Ordinal))
            .Where(item => !HasAny(item.Text, "GIRAR", "NENHUM", "SEM WHEELSPIN", "COMPRAR", "PRÊMIO", "PREMIO"))
            .OrderBy(item => item.Text.Length)
            .Select(item => item.Line)
            .FirstOrDefault();

    private static OcrLine? FindLine(OcrDocument document, IReadOnlyCollection<string> expectedTexts)
    {
        foreach (var expected in expectedTexts.Select(Canonical))
        {
            var line = document.Lines.FirstOrDefault(candidate =>
            {
                var text = Canonical(candidate.Text);
                return text.Contains(expected, StringComparison.Ordinal) &&
                       !text.Contains("SALVAR E CONTINUAR", StringComparison.Ordinal);
            });
            if (line is not null)
            {
                return line;
            }
        }

        return null;
    }

    private static int? ReadCountNearTile(OcrDocument document, OcrLine tile)
    {
        var sameLine = GameVisionService.ExtractNumbers(tile.Text).Where(value => value <= 999).ToArray();
        if (sameLine.Length > 0)
        {
            return sameLine[^1];
        }

        return document.Lines
            .Select(line => new
            {
                Line = line,
                Text = Canonical(line.Text),
                Distance = Math.Abs(line.Center.X - tile.Center.X) + Math.Abs(line.Center.Y - tile.Center.Y) * 2
            })
            .Where(item => Regex.IsMatch(item.Text, @"^(?:X )?\d{1,3}$", RegexOptions.CultureInvariant))
            .Where(item => Math.Abs(item.Line.Center.X - tile.Center.X) <= Math.Max(220, tile.Width * 1.5))
            .Where(item => Math.Abs(item.Line.Center.Y - tile.Center.Y) <= Math.Max(90, tile.Height * 5))
            .OrderBy(item => item.Distance)
            .Select(item => GameVisionService.ExtractNumbers(item.Text).FirstOrDefault())
            .Cast<int?>()
            .FirstOrDefault();
    }

    private static bool IsZeroText(string normalized) =>
        HasAny(
            normalized,
            "NENHUM WHEELSPIN",
            "SEM WHEELSPINS",
            "SEM WHEELSPIN",
            "NÃO HÁ WHEELSPINS",
            "NAO HA WHEELSPINS",
            "VOCÊ NÃO TEM WHEELSPINS",
            "VOCE NAO TEM WHEELSPINS",
            "GANHE MAIS WHEELSPINS") ||
        Regex.IsMatch(normalized, @"\b(?:SUPER )?WHEELSPINS? (?:DISPONIVEL |DISPONIVEIS )?0\b", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(normalized, @"\b0 (?:SUPER )?WHEELSPINS?\b", RegexOptions.CultureInvariant);

    private static bool HasDeparted(Screen before, Screen after)
    {
        if (after.Kind != before.Kind)
        {
            return true;
        }

        if (string.Equals(before.Normalized, after.Normalized, StringComparison.Ordinal))
        {
            return false;
        }

        var beforeTokens = before.Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var afterTokens = after.Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (beforeTokens.Count == 0 || afterTokens.Count == 0)
        {
            return true;
        }

        var union = beforeTokens.Union(afterTokens).Count();
        var intersection = beforeTokens.Intersect(afterTokens).Count();
        return union == 0 || intersection / (double)union < 0.68;
    }

    private static async Task<CalibrationRequiredException> CalibrationFailureAsync(
        AutomationContext context,
        string state,
        string message,
        Screen screen)
    {
        var diagnostic = "não disponível";
        try
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, state);
        }
        catch (Exception exception)
        {
            context.Logger.Warn($"Não foi possível salvar diagnóstico de {state}: {exception.Message}");
        }

        var observed = screen.Document.Text.ReplaceLineEndings(" ").Trim();
        if (observed.Length > 240)
        {
            observed = observed[..240] + "…";
        }

        return new CalibrationRequiredException($"{message} OCR observado: '{observed}'. Diagnóstico: {diagnostic}");
    }

    private static bool IsSpinSession(ScreenKind kind) =>
        kind is ScreenKind.Ready or ScreenKind.Zero or ScreenKind.Prize or ScreenKind.Duplicate or ScreenKind.Purchase;

    private static bool HasAny(string text, params string[] values) =>
        values.Select(Canonical).Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string Canonical(string value) =>
        GameVisionService.Normalize(value)
            .Replace("WHEEL SPIN", "WHEELSPIN", StringComparison.Ordinal)
            .Replace("SUPERWHEELSPIN", "SUPER WHEELSPIN", StringComparison.Ordinal)
            .Replace("SUPER SORTEIOS", "SUPER WHEELSPINS", StringComparison.Ordinal)
            .Replace("SUPER SORTEIO", "SUPER WHEELSPIN", StringComparison.Ordinal)
            .Replace("SUPERSORTEIOS", "SUPER WHEELSPINS", StringComparison.Ordinal)
            .Replace("SUPERSORTEIO", "SUPER WHEELSPIN", StringComparison.Ordinal)
            .Replace("SORTEIOS", "WHEELSPINS", StringComparison.Ordinal)
            .Replace("SORTEIO", "WHEELSPIN", StringComparison.Ordinal);

    private static string DisplayName(SpinType spinType) => spinType == SpinType.Super
        ? "Super Wheelspin"
        : "Wheelspin";

    private enum SpinType
    {
        Standard,
        Super
    }

    private enum ScreenKind
    {
        Unknown,
        PauseMenu,
        Hub,
        Ready,
        Zero,
        Prize,
        Duplicate,
        Purchase
    }

    private sealed record Screen(
        ScreenKind Kind,
        SpinType? Type,
        OcrDocument Document,
        string Normalized);

    private sealed record DrainResult(
        Screen Screen,
        int SpinsStarted,
        bool Exhausted);
}
