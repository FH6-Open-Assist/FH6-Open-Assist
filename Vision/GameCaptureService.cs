using System.Drawing;
using System.Drawing.Imaging;
using FH6OpenAssist.Core;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Vision;

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
    private const int SessionIdleTimeoutMilliseconds = 2_000;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private WindowsGraphicsCaptureSession? _session;
    private Timer? _sessionIdleTimer;
    private IntPtr _sessionWindow;
    private int _captureReadyWritten;
    private int _disposed;

    public async Task<CapturedFrame> CaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var game = windows.GetRequiredGameWindow();
        if (game.IsMinimized)
        {
            throw new AutomationFaultException(
                "O Forza está minimizado. Restaure a janela; o Windows não produz frames WGC enquanto ela está minimizada.");
        }

        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            EnsureSession(game.Handle);
            var bitmap = await _session!.CaptureClientAsync(
                game.ClientCaptureBounds,
                cancellationToken);

            if (Interlocked.Exchange(ref _captureReadyWritten, 1) == 0)
            {
                logger.Info(
                    "Captura Windows.Graphics.Capture ativa. O Forza pode ficar coberto por outras janelas sem receber foco.");
            }

            ArmSessionIdleRelease();
            return new CapturedFrame(bitmap);
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
            ResetSession();
            throw new AutomationFaultException(
                $"Falha na captura Windows.Graphics.Capture: {exception.Message}");
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async Task ReleaseSessionAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        DisarmSessionIdleRelease();
        await _captureGate.WaitAsync();
        try
        {
            if (!IsDisposed)
            {
                ResetSession();
            }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _captureGate.Wait();
        try
        {
            var idleTimer = Interlocked.Exchange(ref _sessionIdleTimer, null);
            idleTimer?.Dispose();
            ResetSession();
        }
        finally
        {
            // O semaphore permanece válido para um callback do timer que já
            // tenha sido enfileirado. Ele observará IsDisposed e sairá.
            _captureGate.Release();
        }
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
        var session = _session;
        _session = null;
        _sessionWindow = IntPtr.Zero;
        try
        {
            session?.Dispose();
        }
        catch (Exception exception)
        {
            logger.Warn($"Não foi possível liberar completamente a sessão de captura: {exception.Message}");
        }
    }

    private void ArmSessionIdleRelease()
    {
        if (IsDisposed)
        {
            return;
        }

        var timer = _sessionIdleTimer ??= new Timer(
            static state =>
            {
                var service = (GameCaptureService)state!;
                _ = service.ReleaseIdleSessionAsync();
            },
            this,
            Timeout.Infinite,
            Timeout.Infinite);
        timer.Change(SessionIdleTimeoutMilliseconds, Timeout.Infinite);
    }

    private void DisarmSessionIdleRelease()
    {
        try
        {
            _sessionIdleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // O encerramento pode disputar com o callback one-shot do timer.
        }
    }

    private async Task ReleaseIdleSessionAsync()
    {
        try
        {
            await ReleaseSessionAsync();
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // A janela foi encerrada enquanto o callback aguardava o gate.
        }
        catch (Exception exception)
        {
            logger.Warn($"Não foi possível pausar a captura ociosa: {exception.Message}");
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}
