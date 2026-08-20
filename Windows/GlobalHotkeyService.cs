using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FH6OpenAssist.Windows;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int ToggleHotkeyId = 0x5001;
    private const int EndHotkeyId = 0x5002;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;
    private static readonly UIntPtr SubclassId = new(0xF6A0);

    private readonly IntPtr _handle;
    private readonly int _minimumWidthDips;
    private readonly int _minimumHeightDips;
    private readonly SubclassProcedure _subclassProcedure;
    private bool _disposed;

    public GlobalHotkeyService(Window window, int minimumWidthDips, int minimumHeightDips)
    {
        _minimumWidthDips = minimumWidthDips;
        _minimumHeightDips = minimumHeightDips;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Não foi possível obter o identificador da janela WinUI.");
        }

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle);
        AppWindow = AppWindow.GetFromWindowId(windowId);
        _subclassProcedure = WindowProcedure;

        if (!SetWindowSubclass(_handle, _subclassProcedure, SubclassId, UIntPtr.Zero))
        {
            throw new InvalidOperationException("Não foi possível integrar as mensagens da janela WinUI.");
        }

        if (!RegisterHotKey(_handle, ToggleHotkeyId, ModNoRepeat, VkF8))
        {
            RegistrationError = "Não foi possível registrar a tecla global F8.";
            return;
        }

        if (!RegisterHotKey(_handle, EndHotkeyId, ModNoRepeat, VkF9))
        {
            _ = UnregisterHotKey(_handle, ToggleHotkeyId);
            RegistrationError = "Não foi possível registrar a tecla global F9.";
            return;
        }

        HotkeysRegistered = true;
    }

    public AppWindow AppWindow { get; }

    public IntPtr Handle => _handle;

    public bool HotkeysRegistered { get; }

    public string? RegistrationError { get; }

    public event Action? ToggleRequested;

    public event Action? EndRequested;

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        switch (message)
        {
            case WmHotkey:
                if (wParam.ToInt32() == ToggleHotkeyId)
                {
                    ToggleRequested?.Invoke();
                    return IntPtr.Zero;
                }

                if (wParam.ToInt32() == EndHotkeyId)
                {
                    EndRequested?.Invoke();
                    return IntPtr.Zero;
                }

                break;

            case WmGetMinMaxInfo:
                ApplyMinimumSize(windowHandle, lParam);
                break;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ApplyMinimumSize(IntPtr windowHandle, IntPtr lParam)
    {
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var dpi = GetDpiForWindow(windowHandle);
        var scale = (dpi == 0 ? 96u : dpi) / 96d;
        minMaxInfo.MinimumTrackSize.X = (int)Math.Ceiling(_minimumWidthDips * scale);
        minMaxInfo.MinimumTrackSize.Y = (int)Math.Ceiling(_minimumHeightDips * scale);
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = UnregisterHotKey(_handle, ToggleHotkeyId);
        _ = UnregisterHotKey(_handle, EndHotkeyId);
        _ = RemoveWindowSubclass(_handle, _subclassProcedure, SubclassId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaximumSize;
        public Point MaximumPosition;
        public Point MinimumTrackSize;
        public Point MaximumTrackSize;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure subclassProcedure,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure subclassProcedure,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
