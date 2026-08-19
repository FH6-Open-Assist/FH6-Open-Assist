using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ForzaFarm.Core;

namespace ForzaFarm.Windows;

public sealed record GameWindowInfo(
    IntPtr Handle,
    System.Drawing.Rectangle ClientBounds,
    System.Drawing.Rectangle ClientCaptureBounds,
    bool IsMinimized);

public sealed class GameWindowService(AutomationSettings settings, AutomationLogger logger)
{
    public async Task<GameWindowInfo> WaitForGameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = TryGetGameWindow();
            if (window is not null)
            {
                return window;
            }

            logger.Warn("Forza Horizon 6 não foi encontrado; aguardando o processo...");
            await Task.Delay(1_000, cancellationToken);
        }
    }

    public GameWindowInfo GetRequiredGameWindow()
    {
        return TryGetGameWindow()
            ?? throw new AutomationFaultException("A janela do Forza Horizon 6 não foi encontrada.");
    }

    public GameWindowInfo? TryGetGameWindow()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            var process = Process.GetProcessesByName(settings.GameProcessName)
                .FirstOrDefault(candidate => candidate.MainWindowHandle != IntPtr.Zero);
            handle = process?.MainWindowHandle ?? IntPtr.Zero;
        }
        catch
        {
            // A enumeração por título abaixo é o fallback.
        }

        if (handle == IntPtr.Zero)
        {
            EnumWindows((candidate, _) =>
            {
                if (!IsWindowVisible(candidate))
                {
                    return true;
                }

                var length = GetWindowTextLength(candidate);
                if (length == 0)
                {
                    return true;
                }

                var title = new StringBuilder(length + 1);
                _ = GetWindowText(candidate, title, title.Capacity);
                if (!title.ToString().Contains(settings.GameWindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                handle = candidate;
                return false;
            }, IntPtr.Zero);
        }

        if (handle == IntPtr.Zero || !TryGetClientBounds(handle, out var bounds))
        {
            return null;
        }

        var captureBounds = GetClientCaptureBounds(handle, bounds);
        return new GameWindowInfo(handle, bounds, captureBounds, IsIconic(handle));
    }

    private static bool TryGetClientBounds(IntPtr handle, out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        if (!GetClientRect(handle, out var clientRect))
        {
            return false;
        }

        var origin = new Point { X = clientRect.Left, Y = clientRect.Top };
        if (!ClientToScreen(handle, ref origin))
        {
            return false;
        }

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new System.Drawing.Rectangle(origin.X, origin.Y, width, height);
        return true;
    }

    private static System.Drawing.Rectangle GetClientCaptureBounds(
        IntPtr handle,
        System.Drawing.Rectangle clientBounds)
    {
        if (DwmGetWindowAttribute(
                handle,
                DwmwaExtendedFrameBounds,
                out var frame,
                Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(handle, out frame))
        {
            return new System.Drawing.Rectangle(0, 0, clientBounds.Width, clientBounds.Height);
        }

        return new System.Drawing.Rectangle(
            Math.Max(0, clientBounds.Left - frame.Left),
            Math.Max(0, clientBounds.Top - frame.Top),
            clientBounds.Width,
            clientBounds.Height);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hWnd,
        int attribute,
        out Rect value,
        int valueSize);

    private const int DwmwaExtendedFrameBounds = 9;
}
