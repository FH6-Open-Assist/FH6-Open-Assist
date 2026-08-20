using System.Text.RegularExpressions;
using FH6OpenAssist.Core;

namespace FH6OpenAssist.Vision;

public enum GameContextKind
{
    Unknown,
    Street,
    Garage,
    Event,
    EventPreRaceMenu,
    WorldMap,
    StreetMenu,
    EventMenu,
    CarPositionResetConfirmation,
    EventExitConfirmation,
    ControllerDisconnected
}

public sealed record GameContextResult(
    GameContextKind Kind,
    double Confidence,
    string Evidence,
    OcrDocument Document);

public sealed class GameContextDetector(
    GameVisionService vision,
    AutomationLogger logger)
{
    private readonly ClassicalGameStateDetector _classical = new();

    public async Task<GameContextResult> DetectAsync(CancellationToken cancellationToken)
    {
        var result = await vision.AnalyzeScreenAsync(
            (frame, document) => Combine(Classify(document), _classical.Analyze(frame)),
            cancellationToken);
        logger.State(
            "Contexto",
            "Detectar",
            $"{result.Kind} ({result.Confidence:P0}): {result.Evidence}");
        return result;
    }

    private static GameContextResult Combine(
        GameContextResult ocr,
        ClassicalGameStateResult classical)
    {
        var normalizedText = GameVisionService.Normalize(ocr.Document.Text);
        if ((classical.Kind is ClassicalGameStateKind.ConfirmationDialog or
             ClassicalGameStateKind.ControllerDisconnected) &&
            (normalizedText.Contains("REDEFINIR POSICAO DO CARRO", StringComparison.Ordinal) ||
             normalizedText.Contains("REINICIAR POSICAO DO CARRO", StringComparison.Ordinal)))
        {
            return new GameContextResult(
                GameContextKind.CarPositionResetConfirmation,
                0.995,
                $"texto Redefinir Posição do Carro + visão clássica ({classical.Evidence}, " +
                $"{classical.Elapsed.TotalMilliseconds:F1} ms)",
                ocr.Document);
        }

        if (classical.Kind == ClassicalGameStateKind.ConfirmationDialog)
        {
            if (ocr.Kind == GameContextKind.EventMenu &&
                normalizedText.Contains("SAIR DO EVENTO", StringComparison.Ordinal))
            {
                return new GameContextResult(
                    GameContextKind.EventExitConfirmation,
                    0.995,
                    $"texto Sair do Evento + visão clássica ({classical.Evidence}, " +
                    $"{classical.Elapsed.TotalMilliseconds:F1} ms)",
                    ocr.Document);
            }

            return ocr with
            {
                Evidence = $"{ocr.Evidence}; modal clássico não autorizado sem texto específico de confirmação ({classical.Evidence})"
            };
        }

        var classicalKind = classical.Kind switch
        {
            ClassicalGameStateKind.StreetMenu => GameContextKind.StreetMenu,
            ClassicalGameStateKind.EventMenu => GameContextKind.EventMenu,
            ClassicalGameStateKind.EventPreRaceMenu => GameContextKind.EventPreRaceMenu,
            ClassicalGameStateKind.ControllerDisconnected => GameContextKind.ControllerDisconnected,
            _ => GameContextKind.Unknown
        };

        if (classicalKind == GameContextKind.Unknown)
        {
            return ocr;
        }

        if (ocr.Kind == classicalKind)
        {
            return ocr with
            {
                Confidence = Math.Max(ocr.Confidence, 0.995),
                Evidence = $"{ocr.Evidence}; confirmado por visão clássica ({classical.Evidence}, {classical.Elapsed.TotalMilliseconds:F1} ms)"
            };
        }

        if (ocr.Kind == GameContextKind.Unknown)
        {
            // O molde visual do diálogo central é compartilhado por avisos do
            // jogo. Ele confirma o texto de desconexão, mas sozinho não pode
            // autorizar Enter nem inferir qual aviso está aberto.
            if (classicalKind == GameContextKind.ControllerDisconnected)
            {
                return ocr with
                {
                    Evidence = $"{ocr.Evidence}; diálogo central reconhecido, mas o OCR não confirmou que é desconexão ({classical.Evidence})"
                };
            }

            return new GameContextResult(
                classicalKind,
                classical.Confidence,
                $"fallback de visão clássica: {classical.Evidence} ({classical.Elapsed.TotalMilliseconds:F1} ms)",
                ocr.Document);
        }

        // Durante transições, cores e texto podem pertencer a frames lógicos
        // distintos. Nunca escolha um lado do conflito para enviar entrada.
        return new GameContextResult(
            GameContextKind.Unknown,
            0,
            $"conflito entre OCR={ocr.Kind} ({ocr.Evidence}) e visão clássica={classicalKind} ({classical.Evidence})",
            ocr.Document);
    }

    public GameContextResult Classify(OcrDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalizedText = GameVisionService.Normalize(document.Text);
        var normalizedLines = document.Lines
            .Select(line => new NormalizedLine(GameVisionService.Normalize(line.Text), line))
            .Where(line => line.Text.Length > 0)
            .ToArray();

        if (normalizedText.Length == 0 && normalizedLines.Length > 0)
        {
            normalizedText = GameVisionService.Normalize(
                string.Join(" ", normalizedLines.Select(line => line.Text)));
        }

        if (Contains(normalizedText, "CONTROLE DESCONECTADO") &&
            ContainsAny(normalizedText, "RECONECTE UM CONTROLE", "RECONECTE O CONTROLE"))
        {
            return Result(
                GameContextKind.ControllerDisconnected,
                0.99,
                "diálogo Controle Desconectado confirmado por título e instrução",
                document);
        }

        if (Contains(normalizedText, "FECHAR MAPA"))
        {
            return Result(
                GameContextKind.WorldMap,
                0.98,
                "mapa-múndi confirmado pelo comando exclusivo Fechar Mapa",
                document);
        }

        var hasWorldMap = Contains(normalizedText, "MAPA DO MUNDO") ||
                          HasStackedLabel(normalizedLines, "MAPA", "DO MUNDO");
        var hasRestartEvent = Contains(normalizedText, "REINICIAR EVENTO") ||
                              HasStackedLabel(normalizedLines, "REINICIAR", "EVENTO");
        var hasExitEvent = Contains(normalizedText, "SAIR DO EVENTO") ||
                           HasStackedLabel(normalizedLines, "SAIR DO", "EVENTO");

        if (hasRestartEvent || hasExitEvent)
        {
            var labels = new List<string>(2);
            if (hasRestartEvent)
            {
                labels.Add("Reiniciar Evento");
            }

            if (hasExitEvent)
            {
                labels.Add("Sair do Evento");
            }

            var confidence = hasRestartEvent && hasExitEvent
                ? 0.99
                : hasWorldMap
                    ? 0.97
                    : 0.94;
            return Result(
                GameContextKind.EventMenu,
                confidence,
                $"rótulo exclusivo detectado: {string.Join(" + ", labels)}",
                document);
        }

        var preRaceIndicators = MatchedPhrases(
            normalizedText,
            "INICIAR EVENTO DE CORRIDA",
            "DIFICULDADE E CONFIGURACOES",
            "TUNAR CARRO",
            "GRID DE LARGADA",
            "SAIR DA CORRIDA");
        if (preRaceIndicators.Count >= 3)
        {
            return Result(
                GameContextKind.EventPreRaceMenu,
                preRaceIndicators.Count >= 4 ? 0.99 : 0.96,
                $"menu pré-corrida: {Join(preRaceIndicators)}",
                document);
        }

        var menuStructure = MatchedPhrases(
            normalizedText,
            "O QUE VEM A SEGUIR",
            "CONFIGURACOES",
            "SAIR DO JOGO",
            "MEU HORIZON",
            "CAMPANHA",
            "ONLINE");
        var streetMenuIndicators = MatchedPhrases(
            normalizedText,
            "DIARIO DE COLECAO",
            "FESTIVAL PLAYLIST",
            "FESTA DE MASCOTES",
            "HORIZON AVENTURA",
            "HORIZON ADVENTURE");

        if (hasWorldMap && menuStructure.Count >= 2 && streetMenuIndicators.Count > 0)
        {
            var confidence = menuStructure.Count >= 4 ? 0.97 : 0.93;
            return Result(
                GameContextKind.StreetMenu,
                confidence,
                $"Mapa do Mundo, estrutura do menu ({Join(menuStructure)}) e indicador de rua ({Join(streetMenuIndicators)}); sem rótulo de evento",
                document);
        }

        var garageIndicators = MatchedPhrases(
            normalizedText,
            "MEUS CARROS",
            "APRIMORAR E TUNAR",
            "CONCESSIONARIA",
            "CASA DE LEILOES",
            "MAESTRIA DE CARRO",
            "PONTOS DISPONIVEIS",
            "IR PARA FABRICANTE",
            "COMPRAR CARRO",
            "APRIMORAMENTO PERSONALIZADO",
            "TUNAGEM PERSONALIZADA",
            "DESIGNS E TINTAS");
        var hasGarageShell = Contains(normalizedText, "CARROS") &&
                             (Contains(normalizedText, "CAMPANHA") ||
                              Contains(normalizedText, "COMPRAR E VENDER") ||
                              Contains(normalizedText, "DIRIGIR"));
        var hasStrongGarageIndicator = ContainsAny(
            normalizedText,
            "MAESTRIA DE CARRO",
            "IR PARA FABRICANTE",
            "COMPRAR CARRO",
            "APRIMORAMENTO PERSONALIZADO",
            "TUNAGEM PERSONALIZADA");

        if (garageIndicators.Count >= 2 ||
            garageIndicators.Count == 1 && (hasGarageShell || hasStrongGarageIndicator))
        {
            var confidence = garageIndicators.Count >= 2
                ? 0.97
                : hasStrongGarageIndicator
                    ? 0.93
                    : 0.89;
            return Result(
                GameContextKind.Garage,
                confidence,
                $"indicadores exclusivos da garagem: {Join(garageIndicators)}",
                document);
        }

        var hasProgress = Contains(normalizedText, "PROGRESSO");
        var hasPosition = Contains(normalizedText, "POSICAO") || HasRacePosition(document.Text);
        var hasTime = Contains(normalizedText, "TEMPO");
        var hasFinish = Contains(normalizedText, "FIM");
        var eventHudIndicators = new List<string>(3);
        if (hasProgress)
        {
            eventHudIndicators.Add("Progresso");
        }

        if (hasPosition)
        {
            eventHudIndicators.Add("Posição");
        }

        if (hasTime)
        {
            eventHudIndicators.Add("Tempo");
        }

        if (eventHudIndicators.Count >= 2 ||
            hasTime && hasFinish && (hasProgress || hasPosition))
        {
            var confidence = eventHudIndicators.Count == 3 ? 0.98 : 0.94;
            return Result(
                GameContextKind.Event,
                confidence,
                $"HUD de evento: {string.Join(" + ", eventHudIndicators)}",
                document);
        }

        var streetIndicators = MatchedPhrases(
            normalizedText,
            "ENTRAR NA CASA",
            "CASA EM TOQUIO",
            "HORIZON ARCADE",
            "DESTINO DEFINIDO",
            "ROTA DEFINIDA");
        var hasRaceHudEvidence = hasProgress || hasPosition || hasTime;
        if (streetIndicators.Count > 0 && !hasRaceHudEvidence)
        {
            return Result(
                GameContextKind.Street,
                streetIndicators.Count >= 2 ? 0.97 : 0.92,
                $"indicador exclusivo da rua: {Join(streetIndicators)}; sem HUD de evento",
                document);
        }

        var unknownEvidence = normalizedText.Length == 0
            ? "OCR sem texto suficiente"
            : $"evidência insuficiente; menu={menuStructure.Count}, pré-corrida={preRaceIndicators.Count}, " +
              $"garagem={garageIndicators.Count}, HUD de evento={eventHudIndicators.Count}, rua={streetIndicators.Count}";
        return Result(GameContextKind.Unknown, 0.0, unknownEvidence, document);
    }

    private static GameContextResult Result(
        GameContextKind kind,
        double confidence,
        string evidence,
        OcrDocument document) =>
        new(kind, Math.Clamp(confidence, 0.0, 1.0), evidence, document);

    private static bool Contains(string normalizedText, string phrase) =>
        normalizedText.Contains(phrase, StringComparison.Ordinal);

    private static bool ContainsAny(string normalizedText, params string[] phrases) =>
        phrases.Any(phrase => Contains(normalizedText, phrase));

    private static IReadOnlyList<string> MatchedPhrases(
        string normalizedText,
        params string[] phrases) =>
        phrases
            .Where(phrase => Contains(normalizedText, phrase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool HasStackedLabel(
        IReadOnlyList<NormalizedLine> lines,
        string upperFragment,
        string lowerFragment)
    {
        foreach (var upper in lines.Where(line => Contains(line.Text, upperFragment)))
        {
            foreach (var lower in lines.Where(line => Contains(line.Text, lowerFragment)))
            {
                if (ReferenceEquals(upper.Source, lower.Source))
                {
                    continue;
                }

                var verticalGap = lower.Source.Y - (upper.Source.Y + upper.Source.Height);
                var maximumGap = Math.Max(18.0, Math.Max(upper.Source.Height, lower.Source.Height) * 2.5);
                var centerDelta = Math.Abs(upper.Source.Center.X - lower.Source.Center.X);
                var maximumCenterDelta = Math.Max(
                    45.0,
                    Math.Max(upper.Source.Width, lower.Source.Width) * 0.55);

                if (verticalGap >= -4.0 && verticalGap <= maximumGap && centerDelta <= maximumCenterDelta)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasRacePosition(string rawText) =>
        Regex.IsMatch(rawText, @"\b\d{1,3}\s*[/|]\s*\d{1,3}\b", RegexOptions.CultureInvariant);

    private static string Join(IReadOnlyCollection<string> values) =>
        string.Join(", ", values.Select(ToDisplayText));

    private static string ToDisplayText(string value) => value switch
    {
        "CONFIGURACOES" => "Configurações",
        "DIARIO DE COLECAO" => "Diário de Coleção",
        "CONCESSIONARIA" => "Concessionária",
        "POSICAO" => "Posição",
        _ => value
    };

    private sealed record NormalizedLine(string Text, OcrLine Source);
}
