using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ForzaFarm.Windows;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int ToggleHotkeyId = 0x5001;
    private const int EndHotkeyId = 0x5002;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;

    private readonly IntPtr _handle;
    private readonly HwndSource _source;

    public event Action? ToggleRequested;
    public event Action? EndRequested;

    public GlobalHotkeyService(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("Não foi possível acessar a janela WPF.");
        _source.AddHook(WindowProcedure);

        if (!RegisterHotKey(_handle, ToggleHotkeyId, ModNoRepeat, VkF8))
        {
            throw new InvalidOperationException("Não foi possível registrar a tecla global F8.");
        }

        if (!RegisterHotKey(_handle, EndHotkeyId, ModNoRepeat, VkF9))
        {
            _ = UnregisterHotKey(_handle, ToggleHotkeyId);
            throw new InvalidOperationException("Não foi possível registrar a tecla global F9.");
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (wParam.ToInt32())
        {
            case ToggleHotkeyId:
                ToggleRequested?.Invoke();
                break;
            case EndHotkeyId:
                EndRequested?.Invoke();
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WindowProcedure);
        _ = UnregisterHotKey(_handle, ToggleHotkeyId);
        _ = UnregisterHotKey(_handle, EndHotkeyId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
