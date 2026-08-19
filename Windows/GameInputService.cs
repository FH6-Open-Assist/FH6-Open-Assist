using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ForzaFarm.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ForzaFarm.Windows;

public enum GameKey : ushort
{
    Backspace = 0x08,
    Enter = 0x0D,
    Shift = 0x10,
    Escape = 0x1B,
    Menu = 0x5D,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    D0 = 0x30,
    D1 = 0x31,
    D2 = 0x32,
    D3 = 0x33,
    D4 = 0x34,
    D5 = 0x35,
    D6 = 0x36,
    D7 = 0x37,
    D8 = 0x38,
    D9 = 0x39,
    NumPad0 = 0x60,
    NumPad1 = 0x61,
    NumPad2 = 0x62,
    NumPad3 = 0x63,
    NumPad4 = 0x64,
    NumPad5 = 0x65,
    NumPad6 = 0x66,
    NumPad7 = 0x67,
    NumPad8 = 0x68,
    NumPad9 = 0x69,
    A = 0x41,
    C = 0x43,
    W = 0x57,
    X = 0x58,
    Y = 0x59
}

public sealed class GameInputService : IDisposable
{
    private readonly GameWindowService _windows;
    private readonly AutomationSettings _settings;
    private readonly AutomationLogger _logger;
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _controller;
    private readonly ConcurrentDictionary<GameKey, byte> _pressedKeys = new();
    private InputMode _mode;
    private bool _disposed;

    public GameInputService(
        GameWindowService windows,
        AutomationSettings settings,
        AutomationLogger logger)
    {
        _windows = windows;
        _settings = settings;
        _logger = logger;
        _mode = settings.InputMode;

        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.Connect();
            _logger.Info(
                $"Controle Xbox 360 virtual conectado. Modo inicial: {ModeLabel(_mode)}.");
        }
        catch (Exception exception)
        {
            throw new AutomationFaultException(
                "Não foi possível criar o controle Xbox 360 virtual. " +
                $"Confirme a instalação do ViGEmBus 1.22. Detalhe: {exception.Message}");
        }
    }

    public async Task TapAsync(GameKey key, CancellationToken cancellationToken, int holdMs = 65)
    {
        await KeyDownAsync(key, cancellationToken);
        await Task.Delay(holdMs, cancellationToken);
        await KeyUpAsync(key, CancellationToken.None);
        await Task.Delay(_settings.ActionDelayMs, cancellationToken);
    }

    public async Task TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        if (_mode == InputMode.BackgroundExperimental)
        {
            var game = GetUsableWindow();
            _logger.State(
                "Entrada",
                "DigitarSegundoPlano",
                $"Enviando {text.Length} caracteres ao campo ativo do Forza por WM_CHAR, sem alterar o foco.");

            foreach (var character in text)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PostMessage(game.Handle, WmChar, new IntPtr(character), IntPtr.Zero))
                {
                    throw new AutomationFaultException(
                        $"O Windows recusou o caractere '{character}' enviado ao Forza em segundo plano.");
                }

                await Task.Delay(55, cancellationToken);
            }

            return;
        }

        _ = GetUsableWindow();
        await Task.Delay(180, cancellationToken);
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCharacter(character);
            await Task.Delay(35, cancellationToken);
        }
    }

    public void SetMode(InputMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mode = mode;
    }

    public Task KeyDownAsync(GameKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var game = GetUsableWindow();

        if (TryGetNumpadCharacter(key, out var character))
        {
            if (_mode == InputMode.Foreground)
            {
                keybd_event((byte)key, 0, 0, UIntPtr.Zero);
            }
            else if (!PostMessage(game.Handle, WmChar, new IntPtr(character), IntPtr.Zero))
            {
                throw new AutomationFaultException(
                    $"O Windows recusou o caractere '{character}' enviado ao Forza em segundo plano.");
            }

            _pressedKeys[key] = 0;
            return Task.CompletedTask;
        }

        if (key == GameKey.W)
        {
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, byte.MaxValue);
        }
        else if (key == GameKey.A)
        {
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, short.MinValue);
        }
        else
        {
            _controller.SetButtonState(MapButton(key), true);
        }

        _pressedKeys[key] = 0;
        return Task.CompletedTask;
    }

    public Task KeyUpAsync(GameKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            _pressedKeys.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        if (TryGetNumpadCharacter(key, out _))
        {
            if (_mode == InputMode.Foreground)
            {
                keybd_event((byte)key, 0, KeyEventKeyUp, UIntPtr.Zero);
            }
        }
        else if (key == GameKey.W)
        {
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);
        }
        else if (key == GameKey.A)
        {
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
        }
        else
        {
            _controller.SetButtonState(MapButton(key), false);
        }

        _pressedKeys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async Task ClickClientAsync(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = GetUsableWindow();
        x = Math.Clamp(x, 0, game.ClientBounds.Width - 1);
        y = Math.Clamp(y, 0, game.ClientBounds.Height - 1);

        if (_mode == InputMode.Foreground)
        {
            var point = new NativePoint { X = x, Y = y };
            if (!ClientToScreen(game.Handle, ref point))
            {
                throw new AutomationFaultException("Não foi possível converter o clique para a tela do Forza.");
            }

            _ = SetCursorPos(point.X, point.Y);
            await Task.Delay(80, cancellationToken);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(_settings.ActionDelayMs, cancellationToken);
            return;
        }

        var coordinates = (y << 16) | (x & 0xFFFF);
        _ = PostMessage(game.Handle, WmMouseMove, IntPtr.Zero, new IntPtr(coordinates));
        _ = PostMessage(game.Handle, WmLButtonDown, new IntPtr(MkLButton), new IntPtr(coordinates));
        _ = PostMessage(game.Handle, WmLButtonUp, IntPtr.Zero, new IntPtr(coordinates));
        await Task.Delay(_settings.ActionDelayMs, cancellationToken);
    }

    public Task ClickNormalizedAsync(double x, double y, CancellationToken cancellationToken)
    {
        var game = GetUsableWindow();
        return ClickClientAsync(
            (int)Math.Round(game.ClientBounds.Width * x),
            (int)Math.Round(game.ClientBounds.Height * y),
            cancellationToken);
    }

    public async Task ReleaseAllAsync()
    {
        foreach (var key in _pressedKeys.Keys.ToArray())
        {
            try
            {
                await KeyUpAsync(key, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.Warn($"Não foi possível soltar {key}: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseAllAsync().GetAwaiter().GetResult();
        _controller.Disconnect();
        _client.Dispose();
        _disposed = true;
    }

    private GameWindowInfo GetUsableWindow()
    {
        var game = _windows.GetRequiredGameWindow();
        if (game.IsMinimized)
        {
            throw new AutomationFaultException(
                "O Forza está minimizado. Restaure a janela; a captura WGC precisa que ele continue renderizando.");
        }

        if (_mode == InputMode.Foreground)
        {
            _ = ShowWindowAsync(game.Handle, ShowNormal);
            _ = BringWindowToTop(game.Handle);
            _ = SetForegroundWindow(game.Handle);
        }

        return game;
    }

    private static void SendCharacter(char character)
    {
        var encoded = VkKeyScan(character);
        if (encoded == -1)
        {
            throw new CalibrationRequiredException($"O caractere '{character}' não pode ser digitado pelo layout atual.");
        }

        var virtualKey = (byte)(encoded & 0xFF);
        var modifiers = (byte)((encoded >> 8) & 0xFF);
        if ((modifiers & 1) != 0) keybd_event(VirtualKeyShift, 0, 0, UIntPtr.Zero);
        if ((modifiers & 2) != 0) keybd_event(VirtualKeyControl, 0, 0, UIntPtr.Zero);
        if ((modifiers & 4) != 0) keybd_event(VirtualKeyMenu, 0, 0, UIntPtr.Zero);

        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);

        if ((modifiers & 4) != 0) keybd_event(VirtualKeyMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
        if ((modifiers & 2) != 0) keybd_event(VirtualKeyControl, 0, KeyEventKeyUp, UIntPtr.Zero);
        if ((modifiers & 1) != 0) keybd_event(VirtualKeyShift, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static string ModeLabel(InputMode mode) => mode == InputMode.Foreground
        ? "primeiro plano"
        : "segundo plano experimental";

    private static bool TryGetNumpadCharacter(GameKey key, out char character)
    {
        var value = (ushort)key;
        if (value is >= (ushort)GameKey.NumPad0 and <= (ushort)GameKey.NumPad9)
        {
            character = (char)('0' + value - (ushort)GameKey.NumPad0);
            return true;
        }

        character = default;
        return false;
    }

    private static Xbox360Button MapButton(GameKey key) => key switch
    {
        GameKey.Enter => Xbox360Button.A,
        GameKey.Escape => Xbox360Button.B,
        GameKey.Menu => Xbox360Button.Start,
        GameKey.Up => Xbox360Button.Up,
        GameKey.Down => Xbox360Button.Down,
        GameKey.Left => Xbox360Button.Left,
        GameKey.Right => Xbox360Button.Right,
        GameKey.Y => Xbox360Button.Y,
        GameKey.X => Xbox360Button.X,
        GameKey.Backspace => Xbox360Button.Back,
        GameKey.Space => Xbox360Button.A,
        GameKey.PageUp => Xbox360Button.LeftShoulder,
        GameKey.PageDown => Xbox360Button.RightShoulder,
        GameKey.Shift => Xbox360Button.LeftShoulder,
        _ => throw new CalibrationRequiredException(
            $"A tecla {key} ainda não possui equivalente seguro no controle Xbox virtual.")
    };

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmChar = 0x0102;
    private const int MkLButton = 0x0001;
    private const int ShowNormal = 9;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const byte VirtualKeyShift = 0x10;
    private const byte VirtualKeyControl = 0x11;
    private const byte VirtualKeyMenu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char character);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
