using System.Drawing;
using System.Drawing.Imaging;
using ForzaFarm.Core;
using ForzaFarm.Windows;

namespace ForzaFarm.Vision;

public sealed class CapturedFrame(Bitmap bitmap) : IDisposable
{
    public Bitmap Bitmap { get; } = bitmap;
    public DateTimeOffset CapturedAt { get; } = DateTimeOffset.Now;

    public void Dispose() => Bitmap.Dispose();
}

public sealed class GameCaptureService(
    GameWindowService windows,
    AutomationSettings settings,
    AutomationLogger logger) : IDisposable
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private WindowsGraphicsCaptureSession? _session;
    private IntPtr _sessionWindow;
    private int _captureReadyWritten;
    private bool _disposed;

    public async Task<CapturedFrame> CaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var game = windows.GetRequiredGameWindow();
        if (game.IsMinimized)
        {
            throw new AutomationFaultException(
                "O Forza está minimizado. Restaure a janela; o Windows não produz frames WGC enquanto ela está minimizada.");
        }

        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            EnsureSession(game.Handle);
            var bitmap = await _session!.CaptureClientAsync(
                game.ClientCaptureBounds,
                cancellationToken);

            if (Interlocked.Exchange(ref _captureReadyWritten, 1) == 0)
            {
                logger.Info(
                    "Captura Windows.Graphics.Capture ativa. O Forza pode ficar coberto por outras janelas sem receber foco.");
            }

            return new CapturedFrame(bitmap);
        }
        catch (AutomationFaultException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ResetSession();
            throw new AutomationFaultException(
                $"Falha na captura Windows.Graphics.Capture: {exception.Message}");
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public string SaveDiagnostic(Bitmap bitmap, string workflow, string state)
    {
        Directory.CreateDirectory(settings.DiagnosticsPath);
        var safeWorkflow = Sanitize(workflow);
        var safeState = Sanitize(state);
        var path = Path.Combine(
            settings.DiagnosticsPath,
            $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{safeWorkflow}-{safeState}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetSession();
        _captureGate.Dispose();
    }

    private void EnsureSession(IntPtr windowHandle)
    {
        if (_session is not null && _sessionWindow == windowHandle)
        {
            return;
        }

        ResetSession();
        _session = new WindowsGraphicsCaptureSession(windowHandle);
        _sessionWindow = windowHandle;
    }

    private void ResetSession()
    {
        _session?.Dispose();
        _session = null;
        _sessionWindow = IntPtr.Zero;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }
}
