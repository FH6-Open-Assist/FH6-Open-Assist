using System.Drawing;

namespace FH6OpenAssist.Vision;

public enum SpRaceResultKind
{
    Unknown,
    Success,
    Failure
}

public readonly record struct SpRaceResultEvidence(
    string NormalizedText,
    bool RetryVisible,
    bool ExitVisible,
    double RetryGreenRatio,
    double SuccessRetryRedRatio,
    double ExitRedRatio)
{
    public bool SuccessControlsVisible =>
        RetryGreenRatio >= 0.12 &&
        SuccessRetryRedRatio >= 0.12 &&
        ExitRedRatio < 0.05;

    public bool FailureControlsVisible =>
        RetryGreenRatio >= 0.12 &&
        ExitRedRatio >= 0.12 &&
        SuccessRetryRedRatio < 0.05;
}

public readonly record struct SpRaceResultObservation(
    SpRaceResultKind Kind,
    SpRaceResultEvidence Evidence,
    string TitleText,
    string ActionsText);

public static class SpRaceVision
{
    public static readonly RectangleF ResultTitleRegion = new(0.02f, 0.14f, 0.64f, 0.15f);
    public static readonly RectangleF HudRegion = new(0.015f, 0.025f, 0.24f, 0.18f);
    public static readonly RectangleF ResultActionsRegion = new(0.02f, 0.88f, 0.30f, 0.10f);

    private static readonly RectangleF RetryGreenRegion = new(0.041f, 0.918f, 0.016f, 0.028f);
    private static readonly RectangleF SuccessRetryRedRegion = new(0.121f, 0.918f, 0.016f, 0.028f);
    private static readonly RectangleF ExitRedRegion = new(0.162f, 0.918f, 0.016f, 0.028f);

    public static IReadOnlyList<RectangleF> ResultOcrRegions { get; } =
        [ResultTitleRegion, ResultActionsRegion];

    public static SpRaceResultEvidence AnalyzeResultFrame(Bitmap bitmap, OcrDocument document)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        return new SpRaceResultEvidence(
            normalized,
            HasRetryText(normalized),
            normalized.Contains("SAIR", StringComparison.Ordinal),
            ColorFillRatio(bitmap, RetryGreenRegion, IsGreen),
            ColorFillRatio(bitmap, SuccessRetryRedRegion, IsRed),
            ColorFillRatio(bitmap, ExitRedRegion, IsRed));
    }

    public static SpRaceResultKind ClassifyResult(
        SpRaceResultEvidence evidence,
        OcrDocument scaledTitle,
        OcrDocument scaledActions)
    {
        var title = GameVisionService.Normalize(scaledTitle.Text);
        var actions = GameVisionService.Normalize(scaledActions.Text);
        var actionsRetryVisible = HasRetryText(actions);
        var actionsExitVisible = actions.Contains("SAIR", StringComparison.Ordinal);
        var actionsContinueVisible = HasContinueText(actions);
        var compactTitle = title.Replace(" ", string.Empty, StringComparison.Ordinal);
        var failureTitle = compactTitle.Contains("NAOCONCLUIDO", StringComparison.Ordinal) ||
                           compactTitle.Contains("DESAFIONAO", StringComparison.Ordinal);
        var failureControls = actionsRetryVisible &&
                              actionsExitVisible &&
                              !actionsContinueVisible &&
                              evidence.FailureControlsVisible;
        if (failureControls)
        {
            return SpRaceResultKind.Failure;
        }

        // No resultado positivo real, o rodapé é estável mesmo quando o OCR do
        // título branco demora a convergir: A=Continuar e B=Tentar Novamente.
        // A derrota usa A=Tentar Novamente e B=Sair, já classificada acima. O
        // título serve somente como veto/diagnóstico, pois o OCR pode omitir "Não".
        var successControls = actionsContinueVisible && actionsRetryVisible &&
                              evidence.SuccessControlsVisible &&
                              !actionsExitVisible && !evidence.FailureControlsVisible;
        return successControls && !failureTitle
            ? SpRaceResultKind.Success
            : SpRaceResultKind.Unknown;
    }

    public static SpRaceResultObservation AnalyzeResultCheckpoint(
        Bitmap bitmap,
        OcrDocument document,
        IReadOnlyList<OcrDocument> scaledRegions)
    {
        if (scaledRegions.Count != 2)
        {
            throw new ArgumentException(
                "O checkpoint de resultado de SP exige título e ações na mesma captura.",
                nameof(scaledRegions));
        }

        var evidence = AnalyzeResultFrame(bitmap, document);
        var kind = ClassifyResult(evidence, scaledRegions[0], scaledRegions[1]);
        return new SpRaceResultObservation(
            kind,
            evidence,
            GameVisionService.Normalize(scaledRegions[0].Text),
            GameVisionService.Normalize(scaledRegions[1].Text));
    }

    public static bool IsActiveHud(string text)
    {
        var normalized = GameVisionService.Normalize(text);
        return normalized.Contains("TEMPO RESTANTE", StringComparison.Ordinal) &&
               normalized.Contains("ATUAL", StringComparison.Ordinal) &&
               !HasRetryText(normalized) &&
               !normalized.Contains("SAIR", StringComparison.Ordinal) &&
               !normalized.Contains("NAO CONCLUIDO", StringComparison.Ordinal) &&
               !normalized.Contains("DESAFIO CONCLUIDO", StringComparison.Ordinal) &&
               !normalized.Contains("CORRIDA CONCLUIDA", StringComparison.Ordinal) &&
               !normalized.Contains("EVENTO CONCLUIDO", StringComparison.Ordinal);
    }

    public static bool HasRetryText(string normalizedText)
    {
        var compact = normalizedText.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("TENTARNOVAMENTE", StringComparison.Ordinal) ||
               compact.Contains("NOVAMENTE", StringComparison.Ordinal);
    }

    private static bool HasContinueText(string normalizedText)
    {
        var compact = normalizedText.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("CONTINUAR", StringComparison.Ordinal) ||
               compact.Contains("CONFNUAR", StringComparison.Ordinal) ||
               compact.Contains("CONINUAR", StringComparison.Ordinal);
    }

    private static double ColorFillRatio(
        Bitmap bitmap,
        RectangleF normalizedRegion,
        Func<Color, bool> matches)
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

    private static bool IsGreen(Color color) =>
        color.G >= 120 &&
        color.G >= color.R * 1.30 &&
        color.G >= color.B * 1.15;

    private static bool IsRed(Color color) =>
        color.R >= 120 &&
        color.R >= color.G * 1.30 &&
        color.R >= color.B * 1.15;
}
