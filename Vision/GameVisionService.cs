using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ForzaFarm.Core;
using ForzaFarm.Windows;

namespace ForzaFarm.Vision;

public sealed class GameVisionService(
    GameCaptureService capture,
    WindowsOcrService ocr,
    GameInputService input,
    AutomationSettings settings,
    AutomationLogger logger)
{
    public async Task<OcrDocument> ReadScreenAsync(CancellationToken cancellationToken)
    {
        using var frame = await capture.CaptureAsync(cancellationToken);
        return await ocr.ReadAsync(frame.Bitmap, cancellationToken);
    }

    public async Task<OcrDocument> ReadRegionAsync(
        RectangleF normalizedRegion,
        CancellationToken cancellationToken)
    {
        using var frame = await capture.CaptureAsync(cancellationToken);
        var region = ToPixels(frame.Bitmap, normalizedRegion);
        return await ocr.ReadAsync(frame.Bitmap, cancellationToken, region);
    }

    public async Task<TextMatch?> TryFindAnyTextAsync(
        IReadOnlyCollection<string> expectedTexts,
        CancellationToken cancellationToken)
    {
        var document = await ReadScreenAsync(cancellationToken);
        return FindAny(document, expectedTexts);
    }

    public async Task<TextMatch> WaitForAnyTextAsync(
        string workflow,
        string state,
        IReadOnlyCollection<string> expectedTexts,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(settings.ScreenTimeoutSeconds));
        OcrDocument? lastDocument = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastDocument = await ReadScreenAsync(cancellationToken);
            var match = FindAny(lastDocument, expectedTexts);
            if (match is not null)
            {
                logger.State(workflow, state, $"Texto detectado: '{match.Line.Text}'.");
                return match;
            }

            await Task.Delay(settings.PollIntervalMs, cancellationToken);
        }

        using var frame = await capture.CaptureAsync(CancellationToken.None);
        var path = capture.SaveDiagnostic(frame.Bitmap, workflow, state);
        var observed = lastDocument?.Text.ReplaceLineEndings(" ").Trim();
        if (observed?.Length > 220)
        {
            observed = observed[..220] + "…";
        }
        throw new CalibrationRequiredException(
            $"Não encontrei [{string.Join(" | ", expectedTexts)}] em '{state}'. " +
            $"OCR observado: '{observed}'. Diagnóstico: {path}");
    }

    public async Task<TextMatch> ClickAnyTextAsync(
        string workflow,
        string state,
        IReadOnlyCollection<string> expectedTexts,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var match = await WaitForAnyTextAsync(
            workflow,
            state,
            expectedTexts,
            cancellationToken,
            timeout);
        logger.State(workflow, state, $"Clique em '{match.Line.Text}' no ponto {match.Line.Center.X},{match.Line.Center.Y}.");
        await input.ClickClientAsync(match.Line.Center.X, match.Line.Center.Y, cancellationToken);
        return match;
    }

    public async Task<TextMatch> ClickAndConfirmAnyTextAsync(
        string workflow,
        string state,
        IReadOnlyCollection<string> expectedTexts,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var match = await ClickAnyTextAsync(
            workflow,
            state,
            expectedTexts,
            cancellationToken,
            timeout);
        logger.State(workflow, state, "Confirmando a opção focada com Enter.");
        await input.TapAsync(GameKey.Enter, cancellationToken);
        return match;
    }

    public async Task<TextMatch> ClickAndAdvanceAsync(
        string workflow,
        string state,
        IReadOnlyCollection<string> expectedTexts,
        IReadOnlyCollection<string> successorTexts,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var clicked = await ClickAnyTextAsync(
            workflow,
            state,
            expectedTexts,
            cancellationToken,
            timeout);

        var clickDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1_400);
        while (DateTime.UtcNow < clickDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await ReadScreenAsync(cancellationToken);
            var successor = FindAny(document, successorTexts);
            if (successor is not null)
            {
                logger.State(workflow, state, $"O clique avançou para '{successor.Line.Text}'; Enter não é necessário.");
                return clicked;
            }

            await Task.Delay(250, cancellationToken);
        }

        logger.State(workflow, state, "O clique apenas focou a opção; confirmando com Enter.");
        await input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await WaitForAnyTextAsync(
            workflow,
            $"{state}Confirmado",
            successorTexts,
            cancellationToken,
            timeout);
        return clicked;
    }

    public async Task<bool> ContainsAnyTextAsync(
        IReadOnlyCollection<string> expectedTexts,
        CancellationToken cancellationToken)
    {
        var match = await TryFindAnyTextAsync(expectedTexts, cancellationToken);
        return match is not null;
    }

    public async Task<int> ReadLargestNumberAsync(
        RectangleF region,
        int maximum,
        string workflow,
        string state,
        CancellationToken cancellationToken)
    {
        var document = await ReadRegionAsync(region, cancellationToken);
        var numbers = ExtractNumbers(document.Text)
            .Where(number => number >= 0 && number <= maximum)
            .ToArray();
        logger.State(
            workflow,
            state,
            $"OCR numérico da ROI: '{document.Text.Replace("\r", " ").Replace("\n", " ").Trim()}'; candidatos: [{string.Join(", ", numbers)}].");
        if (numbers.Length == 0)
        {
            using var frame = await capture.CaptureAsync(CancellationToken.None);
            var path = capture.SaveDiagnostic(frame.Bitmap, workflow, state);
            throw new CalibrationRequiredException(
                $"Não foi possível ler um número em '{state}'. OCR: '{document.Text}'. Diagnóstico: {path}");
        }

        return numbers.Max();
    }

    public async Task<int> ReadYellowNumberAsync(
        RectangleF normalizedRegion,
        int maximum,
        string workflow,
        string state,
        CancellationToken cancellationToken)
    {
        using var frame = await capture.CaptureAsync(cancellationToken);
        var region = ToPixels(frame.Bitmap, normalizedRegion);
        const int scale = 4;
        const int padding = 24;
        using var processed = new Bitmap(
            region.Width * scale + padding * 2,
            region.Height * scale + padding * 2);
        using (var graphics = Graphics.FromImage(processed))
        {
            graphics.Clear(Color.White);
            using var font = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString("SP", font, Brushes.Black, 8, 8);
        }

        // O saldo é amarelo. Transforme somente os seus pixels em preto sobre
        // branco e amplie 4x; isso evita que o OCR confunda a estrela amarela
        // ao lado com um zero. Os últimos 7 px da ROI pertencem ao ícone.
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width - 7; x++)
            {
                var color = frame.Bitmap.GetPixel(region.Left + x, region.Top + y);
                var isYellow = color.R >= 170 && color.G >= 140 && color.B <= 110 &&
                               color.R - color.B >= 80 && color.G - color.B >= 55;
                if (!isYellow)
                {
                    continue;
                }

                for (var dy = 0; dy < scale; dy++)
                {
                    for (var dx = 0; dx < scale; dx++)
                    {
                        processed.SetPixel(padding + x * scale + dx, padding + y * scale + dy, Color.Black);
                    }
                }
            }
        }

        var document = await ocr.ReadAsync(processed, cancellationToken);
        var numbers = ExtractNumbers(document.Text)
            .Where(number => number >= 0 && number <= maximum)
            .ToArray();
        logger.State(
            workflow,
            state,
            $"OCR amarelo: '{document.Text.Replace("\r", " ").Replace("\n", " ").Trim()}'; candidatos: [{string.Join(", ", numbers)}].");
        if (numbers.Length == 0)
        {
            var path = capture.SaveDiagnostic(frame.Bitmap, workflow, state);
            var processedPath = capture.SaveDiagnostic(processed, workflow, $"{state}-Amarelo");
            throw new CalibrationRequiredException(
                $"Não foi possível ler o saldo amarelo em '{state}'. OCR: '{document.Text}'. " +
                $"Diagnósticos: {path}; processado: {processedPath}");
        }

        return numbers.Max();
    }

    public async Task<bool> HasMagentaMarkerAsync(
        RectangleF normalizedRegion,
        CancellationToken cancellationToken,
        double minimumRatio = 0.002)
    {
        using var frame = await capture.CaptureAsync(cancellationToken);
        var region = ToPixels(frame.Bitmap, normalizedRegion);
        var matching = 0;
        var sampled = 0;
        for (var y = region.Top; y < region.Bottom; y += 3)
        {
            for (var x = region.Left; x < region.Right; x += 3)
            {
                var color = frame.Bitmap.GetPixel(x, y);
                sampled++;
                if (color.R >= 190 && color.G <= 100 && color.B >= 80 && color.R >= color.B)
                {
                    matching++;
                }
            }
        }

        return sampled > 0 && matching / (double)sampled >= minimumRatio;
    }

    public async Task<bool> HasLimeSelectionAsync(
        RectangleF normalizedRegion,
        CancellationToken cancellationToken,
        double minimumRatio = 0.004)
    {
        using var frame = await capture.CaptureAsync(cancellationToken);
        var region = ToPixels(frame.Bitmap, normalizedRegion);
        var matching = 0;
        var sampled = 0;
        for (var y = region.Top; y < region.Bottom; y += 2)
        {
            for (var x = region.Left; x < region.Right; x += 2)
            {
                var color = frame.Bitmap.GetPixel(x, y);
                sampled++;
                if (color.R >= 130 && color.G >= 180 && color.B <= 90 && color.G > color.B * 2)
                {
                    matching++;
                }
            }
        }

        return sampled > 0 && matching / (double)sampled >= minimumRatio;
    }

    public async Task<int> FindLimeSelectionAsync(
        IReadOnlyList<RectangleF> normalizedRegions,
        CancellationToken cancellationToken,
        double minimumRatio = 0.004)
    {
        using var frame = await capture.CaptureAsync(cancellationToken);
        for (var index = 0; index < normalizedRegions.Count; index++)
        {
            var region = ToPixels(frame.Bitmap, normalizedRegions[index]);
            var matching = 0;
            var sampled = 0;
            for (var y = region.Top; y < region.Bottom; y += 2)
            {
                for (var x = region.Left; x < region.Right; x += 2)
                {
                    var color = frame.Bitmap.GetPixel(x, y);
                    sampled++;
                    if (color.R >= 130 && color.G >= 180 && color.B <= 90 && color.G > color.B * 2)
                    {
                        matching++;
                    }
                }
            }

            if (sampled > 0 && matching / (double)sampled >= minimumRatio)
            {
                return index;
            }
        }

        return -1;
    }

    public static IReadOnlyList<int> ExtractNumbers(string text)
    {
        return Regex.Matches(text, @"\d+(?:[.,]\d+)*")
            .Select(match => Regex.Replace(match.Value, @"\D", string.Empty))
            .Where(value => value.Length > 0)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : -1)
            .Where(value => value >= 0)
            .ToArray();
    }

    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static TextMatch? FindAny(OcrDocument document, IReadOnlyCollection<string> expectedTexts)
    {
        var normalizedExpected = expectedTexts
            .Select(text => (Original: text, Normalized: Normalize(text)))
            .Where(item => item.Normalized.Length > 0)
            .OrderByDescending(item => item.Normalized.Length)
            .ToArray();

        foreach (var line in document.Lines)
        {
            var normalizedLine = Normalize(line.Text);
            foreach (var expected in normalizedExpected)
            {
                if (normalizedLine.Contains(expected.Normalized, StringComparison.Ordinal))
                {
                    return new TextMatch(expected.Original, line, normalizedLine);
                }
            }
        }

        return null;
    }

    private static Rectangle ToPixels(Bitmap bitmap, RectangleF normalized)
    {
        var x = Math.Clamp((int)Math.Round(bitmap.Width * normalized.X), 0, bitmap.Width - 1);
        var y = Math.Clamp((int)Math.Round(bitmap.Height * normalized.Y), 0, bitmap.Height - 1);
        var width = Math.Clamp((int)Math.Round(bitmap.Width * normalized.Width), 1, bitmap.Width - x);
        var height = Math.Clamp((int)Math.Round(bitmap.Height * normalized.Height), 1, bitmap.Height - y);
        return new Rectangle(x, y, width, height);
    }
}
