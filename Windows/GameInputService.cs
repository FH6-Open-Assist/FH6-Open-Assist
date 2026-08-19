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

public static class GameKeyTranslator
{
    public static byte ToForegroundVirtualKey(GameKey key) => key switch
    {
        GameKey.Backspace => 0x08,
        GameKey.Enter => 0x0D,
        GameKey.Shift => 0x10,
        GameKey.Escape => 0x1B,
        GameKey.Menu => 0x1B,
        GameKey.Space => 0x20,
        GameKey.PageUp => 0x21,
        GameKey.PageDown => 0x22,
        GameKey.End => 0x23,
        GameKey.Left => 0x25,
        GameKey.Up => 0x26,
        GameKey.Right => 0x27,
        GameKey.Down => 0x28,
        GameKey.D0 => 0x30,
        GameKey.D1 => 0x31,
        GameKey.D2 => 0x32,
        GameKey.D3 => 0x33,
        GameKey.D4 => 0x34,
        GameKey.D5 => 0x35,
        GameKey.D6 => 0x36,
        GameKey.D7 => 0x37,
        GameKey.D8 => 0x38,
        GameKey.D9 => 0x39,
        GameKey.A => 0x41,
        GameKey.C => 0x43,
        GameKey.W => 0x57,
        GameKey.X => 0x58,
        GameKey.Y => 0x59,
        GameKey.NumPad0 => 0x60,
        GameKey.NumPad1 => 0x61,
        GameKey.NumPad2 => 0x62,
        GameKey.NumPad3 => 0x63,
        GameKey.NumPad4 => 0x64,
        GameKey.NumPad5 => 0x65,
        GameKey.NumPad6 => 0x66,
        GameKey.NumPad7 => 0x67,
        GameKey.NumPad8 => 0x68,
        GameKey.NumPad9 => 0x69,
        _ => throw new CalibrationRequiredException(
            $"A tecla {key} ainda não possui tradução nativa revisada para o primeiro plano.")
    };
}

public sealed class GameInputService : IDisposable
{
    private readonly GameWindowService _windows;
    private readonly AutomationSettings _settings;
    private readonly AutomationLogger _logger;
    private readonly object _lifecycleSync = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private readonly ConcurrentDictionary<GameKey, InputMode> _pressedKeys = new();
    private string? _backgroundInputError;
    private InputMode _mode;
    private bool _disposing;
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

        _logger.Info($"Entrada nativa de teclado e mouse pronta. Modo inicial: {ModeLabel(_mode)}.");
    }

    public bool IsBackgroundInputAvailable
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _controller is not null && !_disposing && !_disposed;
            }
        }
    }

    public string? BackgroundInputError
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _backgroundInputError;
            }
        }
    }

    public event Action<bool, string?>? BackgroundInputAvailabilityChanged;

    public bool TryEnableBackgroundInput()
    {
        string? error = null;
        var connected = false;
        lock (_lifecycleSync)
        {
            ThrowIfDisposedOrDisposingLocked();
            if (_controller is not null)
            {
                _backgroundInputError = null;
                return true;
            }

            ViGEmClient? client = null;
            IXbox360Controller? controller = null;
            try
            {
                client = new ViGEmClient();
                controller = client.CreateXbox360Controller();
                controller.Connect();
                _client = client;
                _controller = controller;
                _backgroundInputError = null;
                connected = true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                _backgroundInputError = error;
                SafeDisconnectAndDispose(controller, client);
            }
        }

        if (connected)
        {
            _logger.Info("ViGEm validado: controle Xbox 360 virtual conectado com sucesso.");
        }
        else
        {
            _logger.Warn(
                "ViGEm indisponível. O primeiro plano continua funcionando com teclado/mouse nativos. " +
                $"Detalhe: {error}");
        }

        BackgroundInputAvailabilityChanged?.Invoke(connected, error);
        return connected;
    }

    public bool ValidateBackgroundInput() => TryEnableBackgroundInput();

    public async Task TapAsync(GameKey key, CancellationToken cancellationToken, int holdMs = 65)
    {
        await KeyDownAsync(key, cancellationToken);
        await Task.Delay(holdMs, cancellationToken);
        await KeyUpAsync(key, CancellationToken.None);
        await Task.Delay(_settings.ActionDelayMs, cancellationToken);
    }

    public async Task TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        var mode = GetModeForOperation();
        if (mode == InputMode.BackgroundExperimental)
        {
            EnsureBackgroundInputAvailable();
            var game = GetUsableWindow(mode);
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

        _ = GetUsableWindow(mode);
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
        lock (_lifecycleSync)
        {
            ThrowIfDisposedOrDisposingLocked();
            if (mode == InputMode.BackgroundExperimental && _controller is null)
            {
                throw new AutomationFaultException(
                    "O segundo plano exige uma conexão funcional com o controle virtual ViGEm.");
            }

            _mode = mode;
        }
    }

    public Task KeyDownAsync(GameKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = GetModeForOperation();
        var game = GetUsableWindow(mode);

        if (mode == InputMode.Foreground)
        {
            lock (_lifecycleSync)
            {
                ThrowIfDisposedOrDisposingLocked();
                keybd_event(GameKeyTranslator.ToForegroundVirtualKey(key), 0, 0, UIntPtr.Zero);
                _pressedKeys[key] = mode;
            }

            return Task.CompletedTask;
        }

        EnsureBackgroundInputAvailable();
        if (TryGetNumpadCharacter(key, out var character))
        {
            lock (_lifecycleSync)
            {
                ThrowIfDisposedOrDisposingLocked();
                if (_controller is null)
                {
                    throw new AutomationFaultException(
                        "A conexão com o controle virtual ViGEm não está disponível para o segundo plano.");
                }

                if (!PostMessage(game.Handle, WmChar, new IntPtr(character), IntPtr.Zero))
                {
                    throw new AutomationFaultException(
                        $"O Windows recusou o caractere '{character}' enviado ao Forza em segundo plano.");
                }

                _pressedKeys[key] = mode;
            }

            return Task.CompletedTask;
        }

        ExecuteControllerOperation(
            controller =>
            {
                if (key == GameKey.W)
                {
                    controller.SetSliderValue(Xbox360Slider.RightTrigger, byte.MaxValue);
                }
                else if (key == GameKey.A)
                {
                    controller.SetAxisValue(Xbox360Axis.LeftThumbX, short.MinValue);
                }
                else
                {
                    controller.SetButtonState(MapButton(key), true);
                }

                _pressedKeys[key] = mode;
            },
            $"pressionar {key}");
        return Task.CompletedTask;
    }

    public Task KeyUpAsync(GameKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InputMode mode;
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                _pressedKeys.TryRemove(key, out _);
                return Task.CompletedTask;
            }

            mode = _pressedKeys.TryGetValue(key, out var pressedMode) ? pressedMode : _mode;
        }

        try
        {
            if (mode == InputMode.Foreground)
            {
                lock (_lifecycleSync)
                {
                    if (!_disposed)
                    {
                        keybd_event(
                            GameKeyTranslator.ToForegroundVirtualKey(key),
                            0,
                            KeyEventKeyUp,
                            UIntPtr.Zero);
                    }
                }
            }
            else if (TryGetNumpadCharacter(key, out _))
            {
                // O caractere em segundo plano é enviado de forma atômica por WM_CHAR.
            }
            else
            {
                ExecuteControllerOperation(
                    controller =>
                    {
                        if (key == GameKey.W)
                        {
                            controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                        }
                        else if (key == GameKey.A)
                        {
                            controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                        }
                        else
                        {
                            controller.SetButtonState(MapButton(key), false);
                        }
                    },
                    $"soltar {key}",
                    allowWhileDisposing: true);
            }
        }
        finally
        {
            _pressedKeys.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public async Task ClickClientAsync(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = GetModeForOperation();
        if (mode == InputMode.BackgroundExperimental)
        {
            EnsureBackgroundInputAvailable();
        }

        var game = GetUsableWindow(mode);
        x = Math.Clamp(x, 0, game.ClientBounds.Width - 1);
        y = Math.Clamp(y, 0, game.ClientBounds.Height - 1);

        if (mode == InputMode.Foreground)
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
        var mode = GetModeForOperation();
        var game = GetUsableWindow(mode);
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
        lock (_lifecycleSync)
        {
            if (_disposed || _disposing)
            {
                return;
            }

            _disposing = true;
        }

        ReleaseAllAsync().GetAwaiter().GetResult();
        lock (_lifecycleSync)
        {
            InvalidateBackgroundInputLocked(error: null);
            _pressedKeys.Clear();
            _disposed = true;
            _disposing = false;
        }
    }

    private GameWindowInfo GetUsableWindow(InputMode mode)
    {
        var game = _windows.GetRequiredGameWindow();
        if (game.IsMinimized)
        {
            throw new AutomationFaultException(
                "O Forza está minimizado. Restaure a janela; a captura WGC precisa que ele continue renderizando.");
        }

        if (mode == InputMode.Foreground)
        {
            _ = ShowWindowAsync(game.Handle, ShowNormal);
            _ = BringWindowToTop(game.Handle);
            _ = SetForegroundWindow(game.Handle);
        }

        return game;
    }

    private InputMode GetModeForOperation()
    {
        lock (_lifecycleSync)
        {
            ThrowIfDisposedOrDisposingLocked();
            return _mode;
        }
    }

    private void EnsureBackgroundInputAvailable()
    {
        lock (_lifecycleSync)
        {
            ThrowIfDisposedOrDisposingLocked();
            if (_controller is null)
            {
                throw new AutomationFaultException(
                    "A conexão com o controle virtual ViGEm não está disponível para o segundo plano. " +
                    "Tente habilitar o modo novamente.");
            }
        }
    }

    private void ExecuteControllerOperation(
        Action<IXbox360Controller> operation,
        string operationName,
        bool allowWhileDisposing = false)
    {
        Exception? failure = null;
        lock (_lifecycleSync)
        {
            if (_disposed || (_disposing && !allowWhileDisposing))
            {
                throw new ObjectDisposedException(nameof(GameInputService));
            }

            if (_controller is null)
            {
                throw new AutomationFaultException(
                    "A conexão com o controle virtual ViGEm não está disponível para o segundo plano. " +
                    "Tente habilitar o modo novamente.");
            }

            try
            {
                operation(_controller);
            }
            catch (Exception exception)
            {
                failure = exception;
                InvalidateBackgroundInputLocked(exception.Message);
            }
        }

        if (failure is null)
        {
            return;
        }

        _logger.Warn(
            $"A operação ViGEm '{operationName}' falhou e a conexão foi invalidada: {failure.Message}");
        BackgroundInputAvailabilityChanged?.Invoke(false, failure.Message);
        throw new AutomationFaultException(
            "A conexão com o controle virtual falhou durante a execução. " +
            "O segundo plano foi desabilitado; tente habilitá-lo novamente.");
    }

    private void InvalidateBackgroundInputLocked(string? error)
    {
        var controller = _controller;
        var client = _client;
        _controller = null;
        _client = null;
        _backgroundInputError = error;
        SafeDisconnectAndDispose(controller, client);
    }

    private static void SafeDisconnectAndDispose(
        IXbox360Controller? controller,
        ViGEmClient? client)
    {
        try
        {
            controller?.Disconnect();
        }
        catch
        {
            // A instância já está inválida; a referência será descartada abaixo.
        }

        try
        {
            client?.Dispose();
        }
        catch
        {
            // O descarte é best-effort após falha do barramento.
        }
    }

    private void ThrowIfDisposedOrDisposingLocked()
    {
        if (_disposed || _disposing)
        {
            throw new ObjectDisposedException(nameof(GameInputService));
        }
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
