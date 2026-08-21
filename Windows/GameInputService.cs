using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FH6OpenAssist.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace FH6OpenAssist.Windows;

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
    S = 0x53,
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
        GameKey.S => 0x53,
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
    private readonly record struct PressedInput(
        InputMode Mode,
        IntPtr WindowHandle,
        uint ProcessId);

    private readonly GameWindowService _windows;
    private readonly AutomationSettings _settings;
    private readonly AutomationLogger _logger;
    private readonly object _lifecycleSync = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private readonly ConcurrentDictionary<GameKey, PressedInput> _pressedKeys = new();
    private bool _analogAcceleratorActive;
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

    public async Task TapAsync(
        GameKey key,
        CancellationToken cancellationToken,
        int holdMs = 65,
        int? postDelayMs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdMs);
        if (postDelayMs is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(postDelayMs));
        }

        await KeyDownAsync(key, cancellationToken);
        try
        {
            await Task.Delay(holdMs, cancellationToken);
        }
        finally
        {
            await KeyUpAsync(key, CancellationToken.None);
        }

        await Task.Delay(postDelayMs ?? _settings.ActionDelayMs, cancellationToken);
    }

    public async Task HoldAsync(
        GameKey key,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdMilliseconds);

        await KeyDownAsync(key, cancellationToken);
        try
        {
            await MonitorHeldInputAsync(key, holdMilliseconds, cancellationToken);
        }
        finally
        {
            await KeyUpAsync(key, CancellationToken.None);
        }
    }

    public async Task HoldPreciselyAsync(
        GameKey key,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdMilliseconds);

        await KeyDownAsync(key, cancellationToken);
        try
        {
            MonitorHeldInputPrecisely(key, holdMilliseconds, cancellationToken);
        }
        finally
        {
            await KeyUpAsync(key, CancellationToken.None);
        }
    }

    public Task DelayPreciselyAsync(
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);
        WaitPrecisely(
            delayMilliseconds,
            cancellationToken,
            healthCheck: null);
        return Task.CompletedTask;
    }

    public async Task PulseAcceleratorAsync(
        double strength,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdMilliseconds);
        if (strength is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strength),
                "A intensidade do acelerador deve estar entre 0 (exclusivo) e 1.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mode = GetModeForOperation();
        var game = GetUsableWindow(mode);
        _ = GetWindowThreadProcessId(game.Handle, out var processId);
        if (processId == 0)
        {
            throw new AutomationFaultException(
                "Não foi possível confirmar o processo do Forza antes do pulso analógico.");
        }

        EnsureBackgroundInputAvailable();
        var expectedInput = new PressedInput(mode, game.Handle, processId);
        var triggerValue = (byte)Math.Clamp(
            (int)Math.Round(strength * byte.MaxValue),
            1,
            byte.MaxValue);

        ExecuteControllerOperation(
            controller =>
            {
                controller.SetSliderValue(Xbox360Slider.RightTrigger, triggerValue);
                _analogAcceleratorActive = true;
            },
            $"aplicar acelerador analógico a {strength:P0}");
        try
        {
            await Task.Run(
                () => WaitPrecisely(
                    holdMilliseconds,
                    cancellationToken,
                    () => EnsureInputTarget(expectedInput, "acelerador analógico")),
                cancellationToken);
        }
        finally
        {
            try
            {
                ExecuteControllerOperation(
                    controller => controller.SetSliderValue(Xbox360Slider.RightTrigger, 0),
                    "soltar acelerador analógico",
                    allowWhileDisposing: true);
            }
            finally
            {
                lock (_lifecycleSync)
                {
                    _analogAcceleratorActive = false;
                }
            }
        }
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
        _ = GetWindowThreadProcessId(game.Handle, out var processId);
        if (processId == 0)
        {
            throw new AutomationFaultException(
                "Não foi possível confirmar o processo da janela do Forza antes de enviar a entrada.");
        }

        var pressedInput = new PressedInput(mode, game.Handle, processId);

        if (mode == InputMode.Foreground)
        {
            lock (_lifecycleSync)
            {
                ThrowIfDisposedOrDisposingLocked();
                keybd_event(GameKeyTranslator.ToForegroundVirtualKey(key), 0, 0, UIntPtr.Zero);
                _pressedKeys[key] = pressedInput;
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

                _pressedKeys[key] = pressedInput;
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
                else if (key == GameKey.S)
                {
                    controller.SetSliderValue(Xbox360Slider.LeftTrigger, byte.MaxValue);
                }
                else if (key == GameKey.A)
                {
                    controller.SetAxisValue(Xbox360Axis.LeftThumbX, short.MinValue);
                }
                else
                {
                    controller.SetButtonState(MapButton(key), true);
                }

                _pressedKeys[key] = pressedInput;
            },
            $"pressionar {key}");
        return Task.CompletedTask;
    }

    private async Task MonitorHeldInputAsync(
        GameKey key,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!_pressedKeys.TryGetValue(key, out var pressedInput))
        {
            throw new AutomationFaultException(
                $"A tecla {key} foi liberada antes de iniciar a sustentação segura.");
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureHeldInputTarget(key, pressedInput);

            var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var remainingMilliseconds = holdMilliseconds - elapsedMilliseconds;
            if (remainingMilliseconds <= 0)
            {
                break;
            }

            var slice = Math.Max(
                1,
                (int)Math.Ceiling(Math.Min(
                    HoldHealthCheckIntervalMilliseconds,
                    remainingMilliseconds)));
            await Task.Delay(slice, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureHeldInputTarget(key, pressedInput);
    }

    private void MonitorHeldInputPrecisely(
        GameKey key,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!_pressedKeys.TryGetValue(key, out var pressedInput))
        {
            throw new AutomationFaultException(
                $"A tecla {key} foi liberada antes de iniciar a sustentação precisa.");
        }

        WaitPrecisely(
            holdMilliseconds,
            cancellationToken,
            () => EnsureHeldInputTarget(key, pressedInput));
    }

    private static void WaitPrecisely(
        int milliseconds,
        CancellationToken cancellationToken,
        Action? healthCheck)
    {
        if (milliseconds <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            healthCheck?.Invoke();
            return;
        }

        var timer = CreateWaitableTimerEx(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        var deadline = Stopwatch.GetTimestamp() +
                       (long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1_000);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                healthCheck?.Invoke();

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return;
                }

                var remainingMilliseconds = remainingTicks * 1_000d / Stopwatch.Frequency;
                var sliceMilliseconds = Math.Min(
                    HoldHealthCheckIntervalMilliseconds,
                    remainingMilliseconds);
                if (timer != IntPtr.Zero && WaitWithHighResolutionTimer(timer, sliceMilliseconds))
                {
                    continue;
                }

                var fallbackDeadline = Math.Min(
                    deadline,
                    Stopwatch.GetTimestamp() +
                    (long)Math.Ceiling(
                        sliceMilliseconds * Stopwatch.Frequency / 1_000d));
                WaitWithMonotonicFallback(fallbackDeadline, cancellationToken);
            }
        }
        finally
        {
            if (timer != IntPtr.Zero)
            {
                _ = CancelWaitableTimer(timer);
                _ = CloseHandle(timer);
            }
        }
    }

    private static bool WaitWithHighResolutionTimer(
        IntPtr timer,
        double milliseconds)
    {
        var dueTime = -(long)Math.Max(
            1,
            Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond));
        if (!SetWaitableTimer(
                timer,
                ref dueTime,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                false))
        {
            return false;
        }

        var timeoutMilliseconds = (uint)Math.Clamp(
            Math.Ceiling(milliseconds) + 50,
            1,
            HoldHealthCheckIntervalMilliseconds + 50);
        return WaitForSingleObject(timer, timeoutMilliseconds) == WaitObject0;
    }

    private static void WaitWithMonotonicFallback(
        long deadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = remainingTicks * 1_000d / Stopwatch.Frequency;
            if (remainingMilliseconds > 1)
            {
                var coarseWait = Math.Max(1, (int)Math.Floor(remainingMilliseconds - 0.5));
                _ = cancellationToken.WaitHandle.WaitOne(coarseWait);
                continue;
            }

            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(16);
            }

            return;
        }
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

            mode = _pressedKeys.TryGetValue(key, out var pressedInput)
                ? pressedInput.Mode
                : _mode;
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
                        else if (key == GameKey.S)
                        {
                            controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
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

        var releaseAnalogAccelerator = false;
        lock (_lifecycleSync)
        {
            releaseAnalogAccelerator = _analogAcceleratorActive && _controller is not null;
        }

        if (releaseAnalogAccelerator)
        {
            try
            {
                ExecuteControllerOperation(
                    controller => controller.SetSliderValue(Xbox360Slider.RightTrigger, 0),
                    "liberar acelerador analógico",
                    allowWhileDisposing: true);
            }
            catch (Exception exception)
            {
                _logger.Warn($"Não foi possível liberar o acelerador analógico: {exception.Message}");
            }
            finally
            {
                lock (_lifecycleSync)
                {
                    _analogAcceleratorActive = false;
                }
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
            for (var attempt = 0; attempt < 5 && GetForegroundWindow() != game.Handle; attempt++)
            {
                _ = ShowWindowAsync(game.Handle, ShowNormal);
                _ = BringWindowToTop(game.Handle);
                _ = SetForegroundWindow(game.Handle);
                Thread.Sleep(35);
            }

            if (GetForegroundWindow() != game.Handle)
            {
                throw new AutomationFaultException(
                    "O Windows não concedeu foco real ao Forza. A entrada foi bloqueada para não atingir outro aplicativo.");
            }
        }

        return game;
    }

    private void EnsureHeldInputTarget(
        GameKey key,
        PressedInput expectedInput)
    {
        if (!_pressedKeys.TryGetValue(key, out var pressedInput) || pressedInput != expectedInput)
        {
            throw new AutomationFaultException(
                $"A sustentação de {key} foi interrompida porque a entrada deixou de estar ativa.");
        }

        EnsureInputTarget(expectedInput, key.ToString());
    }

    private static void EnsureInputTarget(
        PressedInput expectedInput,
        string inputName)
    {
        if (!IsWindow(expectedInput.WindowHandle) ||
            !IsWindowVisible(expectedInput.WindowHandle) ||
            IsIconic(expectedInput.WindowHandle))
        {
            throw new AutomationFaultException(
                $"A sustentação de {inputName} foi interrompida porque a janela do Forza foi fechada, ocultada ou minimizada.");
        }

        _ = GetWindowThreadProcessId(expectedInput.WindowHandle, out var currentProcessId);
        if (currentProcessId == 0 || currentProcessId != expectedInput.ProcessId)
        {
            throw new AutomationFaultException(
                $"A sustentação de {inputName} foi interrompida porque o processo do Forza não é mais o mesmo.");
        }

        if (expectedInput.Mode == InputMode.Foreground && GetForegroundWindow() != expectedInput.WindowHandle)
        {
            throw new AutomationFaultException(
                $"A sustentação de {inputName} foi interrompida porque o Forza perdeu o foco.");
        }
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
    private const int HoldHealthCheckIntervalMilliseconds = 100;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0x00000000;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr completionRoutineArgument,
        bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CancelWaitableTimer(IntPtr timer);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
