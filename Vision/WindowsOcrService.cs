using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FH6OpenAssist.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FH6OpenAssist.Vision;

public sealed record OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public Point Center => new((int)Math.Round(X + Width / 2), (int)Math.Round(Y + Height / 2));
}

public sealed record OcrDocument(string Text, IReadOnlyList<OcrLine> Lines);

public sealed record TextMatch(string RequestedText, OcrLine Line, string NormalizedText);

public sealed class WindowsOcrService
{
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private OcrEngine? _engine;

    public WindowsOcrService(AutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    public async Task<OcrDocument> ReadAsync(
        Bitmap source,
        CancellationToken cancellationToken,
        Rectangle? region = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveRegion = region ?? new Rectangle(0, 0, source.Width, source.Height);
        effectiveRegion.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (effectiveRegion.Width <= 0 || effectiveRegion.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "A região de OCR está fora da imagem.");
        }

        var gateAcquired = false;
        try
        {
            await _recognitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            cancellationToken.ThrowIfCancellationRequested();
            using var softwareBitmap = CreateSoftwareBitmap(source, effectiveRegion, cancellationToken);
            var engine = _engine ??= OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new AutomationFaultException("O OCR nativo do Windows não está disponível.");

            var result = await engine
                .RecognizeAsync(softwareBitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return CreateDocument(result, effectiveRegion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            if (gateAcquired)
            {
                _recognitionGate.Release();
            }
        }
    }

    private static SoftwareBitmap CreateSoftwareBitmap(
        Bitmap source,
        Rectangle region,
        CancellationToken cancellationToken)
    {
        using var image = source.Clone(region, PixelFormat.Format32bppArgb);
        var bitmapRegion = new Rectangle(0, 0, image.Width, image.Height);
        var bitmapData = image.LockBits(
            bitmapRegion,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var rowLength = checked(image.Width * 4);
            var pixels = GC.AllocateUninitializedArray<byte>(checked(rowLength * image.Height));

            for (var row = 0; row < image.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = IntPtr.Add(bitmapData.Scan0, checked(row * bitmapData.Stride));
                Marshal.Copy(sourceRow, pixels, row * rowLength, rowLength);
            }

            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            var buffer = writer.DetachBuffer();
            return SoftwareBitmap.CreateCopyFromBuffer(
                buffer,
                BitmapPixelFormat.Bgra8,
                image.Width,
                image.Height,
                BitmapAlphaMode.Ignore);
        }
        finally
        {
            image.UnlockBits(bitmapData);
        }
    }

    private static OcrDocument CreateDocument(
        OcrResult result,
        Rectangle region,
        CancellationToken cancellationToken)
    {
        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Words.Count == 0)
            {
                continue;
            }

            var left = line.Words.Min(word => word.BoundingRect.X);
            var top = line.Words.Min(word => word.BoundingRect.Y);
            var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
            var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
            lines.Add(new OcrLine(
                line.Text,
                left + region.X,
                top + region.Y,
                right - left,
                bottom - top));
        }

        return new OcrDocument(result.Text, lines);
    }
}
