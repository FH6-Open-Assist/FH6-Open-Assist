using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using ForzaFarm.Core;

namespace ForzaFarm.Vision;

public sealed record OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public Point Center => new((int)Math.Round(X + Width / 2), (int)Math.Round(Y + Height / 2));
}

public sealed record OcrDocument(string Text, IReadOnlyList<OcrLine> Lines);

public sealed record TextMatch(string RequestedText, OcrLine Line, string NormalizedText);

public sealed class WindowsOcrService(AutomationSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<OcrDocument> ReadAsync(
        Bitmap source,
        CancellationToken cancellationToken,
        Rectangle? region = null)
    {
        var effectiveRegion = region ?? new Rectangle(0, 0, source.Width, source.Height);
        effectiveRegion.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (effectiveRegion.Width <= 0 || effectiveRegion.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "A região de OCR está fora da imagem.");
        }

        using var image = source.Clone(effectiveRegion, PixelFormat.Format24bppRgb);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"forza-farm-ocr-{Guid.NewGuid():N}.png");
        image.Save(temporaryPath, ImageFormat.Png);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(settings.OcrScriptPath);
            startInfo.ArgumentList.Add("-Path");
            startInfo.ArgumentList.Add(temporaryPath);

            using var process = Process.Start(startInfo)
                ?? throw new AutomationFaultException("Não foi possível iniciar o OCR do Windows.");
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (process.ExitCode != 0)
            {
                throw new AutomationFaultException(
                    $"OCR do Windows falhou ({process.ExitCode}): {standardError.Trim()}");
            }

            var payload = JsonSerializer.Deserialize<OcrPayload>(standardOutput.Trim(), JsonOptions)
                ?? new OcrPayload();
            var lines = payload.Lines
                .Select(line => new OcrLine(
                    line.Text ?? string.Empty,
                    line.X + effectiveRegion.X,
                    line.Y + effectiveRegion.Y,
                    line.Width,
                    line.Height))
                .ToArray();
            return new OcrDocument(payload.Text ?? string.Empty, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AutomationFaultException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AutomationFaultException($"Falha ao interpretar o OCR: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // O arquivo temporário será removido pelo Windows posteriormente.
            }
        }
    }

    private sealed class OcrPayload
    {
        public string? Text { get; set; }
        public List<OcrLinePayload> Lines { get; set; } = [];
    }

    private sealed class OcrLinePayload
    {
        public string? Text { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
