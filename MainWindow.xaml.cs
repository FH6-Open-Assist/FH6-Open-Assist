using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ForzaFarm.Core;
using ForzaFarm.Vision;
using ForzaFarm.Windows;
using ForzaFarm.Workflows;

namespace ForzaFarm;

public partial class MainWindow : Window
{
    private readonly AutomationSettings _settings;
    private readonly AutomationLogger _logger;
    private readonly GameInputService _input;
    private readonly GameCaptureService _capture;
    private readonly ResourceTracker _resources;
    private readonly AutomationCoordinator _coordinator;
    private GlobalHotkeyService? _hotkeys;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AutomationSettings.Load();
        _logger = new AutomationLogger(_settings.BaseDirectory);
        var gameWindow = new GameWindowService(_settings, _logger);
        _input = new GameInputService(gameWindow, _settings, _logger);
        _capture = new GameCaptureService(gameWindow, _settings, _logger);
        var ocr = new WindowsOcrService(_settings);
        var vision = new GameVisionService(_capture, ocr, _input, _settings, _logger);
        _resources = new ResourceTracker();
        var context = new AutomationContext
        {
            Settings = _settings,
            Logger = _logger,
            GameWindow = gameWindow,
            Input = _input,
            Capture = _capture,
            Vision = vision,
            Resources = _resources,
            RunNestedAsync = (_, _) => Task.CompletedTask
        };
        IMacroWorkflow[] workflows =
        [
            new SpFarmWorkflow(),
            new FastMoneyWorkflow(),
            new SpinFarmWorkflow(),
            new SpendSpinsWorkflow()
        ];
        _coordinator = new AutomationCoordinator(workflows, context, _logger);

        _logger.LineWritten += AppendLog;
        _resources.Changed += Resources_Changed;
        _coordinator.StateChanged += Coordinator_StateChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SpMacro.IsChecked = true;
        ForegroundMode.IsChecked = _settings.InputMode == InputMode.Foreground;
        BackgroundMode.IsChecked = _settings.InputMode == InputMode.BackgroundExperimental;
        ApplyInputModeDescription(_settings.InputMode);
        _logger.Info(
            $"FH6 Open Assist iniciado em {InputModeLabel(_settings.InputMode)}. Ative um BOT; F8 executa/pausa e F9 encerra.");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _hotkeys = new GlobalHotkeyService(this);
            _hotkeys.ToggleRequested += () => Dispatcher.BeginInvoke(ToggleMacro);
            _hotkeys.EndRequested += () => Dispatcher.BeginInvoke(EndMacro);
            _logger.Info("Hotkeys globais F8 e F9 registradas.");
        }
        catch (Exception exception)
        {
            _logger.Error($"Hotkeys globais indisponíveis: {exception.Message}");
        }
    }

    private void Macro_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<MacroKind>(tag, out var kind))
        {
            return;
        }

        _coordinator.Select(kind);
    }

    private void ActivateBotButton_Click(object sender, RoutedEventArgs e) => _coordinator.ArmSelected();

    private void InstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InstructionsWindow { Owner = this };
        _ = dialog.ShowDialog();
    }

    private void InputMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_input is null || sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<InputMode>(tag, out var mode))
        {
            return;
        }

        _settings.InputMode = mode;
        _input.SetMode(mode);
        ApplyInputModeDescription(mode);
        _logger.Info($"Modo de execução alterado para {InputModeLabel(mode)}.");
    }

    private async void ToggleMacro()
    {
        try
        {
            if (_coordinator.State == MacroRunState.Executando)
            {
                await _coordinator.StopAsync();
                return;
            }

            await _coordinator.StartSelectedAsync();
        }
        catch (Exception exception)
        {
            _logger.Error($"Não foi possível alternar a execução: {exception.Message}");
        }
    }

    private async void EndMacro()
    {
        try
        {
            await _coordinator.EndAsync();
        }
        catch (Exception exception)
        {
            _logger.Error($"Falha ao encerrar: {exception.Message}");
        }
    }

    private void Coordinator_StateChanged(MacroRunState state, MacroKind? kind, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = StateLabel(state);
            StatusDot.Fill = StateBrush(state);
            DetailText.Text = message;
            ActivateBotButton.IsEnabled = state is not (MacroRunState.Executando or MacroRunState.Parando);
            ActivateBotButton.Content = state is MacroRunState.Armado or MacroRunState.Executando or MacroRunState.Parando
                ? "BOT ATIVO"
                : "Ativar BOT";
            ForegroundMode.IsEnabled = state is not (MacroRunState.Executando or MacroRunState.Parando);
            BackgroundMode.IsEnabled = state is not (MacroRunState.Executando or MacroRunState.Parando);
        });
    }

    private void AppendLog(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (LogTextBox.Text.Length > 80_000)
            {
                LogTextBox.Text = LogTextBox.Text[^50_000..];
            }

            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void Resources_Changed(ResourceSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SkillPointsText.Text = FormatResource(snapshot.SkillPoints, snapshot.SkillPointsEstimated);
            CreditsText.Text = FormatResource(snapshot.Credits, snapshot.CreditsEstimated);
        });
    }

    private static string FormatResource(long? value, bool estimated) =>
        value is null ? "—" : $"{(estimated ? "≈ " : string.Empty)}{value.Value:N0}";

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _hotkeys?.Dispose();
        _resources.Changed -= Resources_Changed;
        await _coordinator.DisposeAsync();
        _input.Dispose();
        _capture.Dispose();
        _logger.Dispose();
    }

    private Brush StateBrush(MacroRunState state) => state switch
    {
        MacroRunState.Executando => (Brush)FindResource("AccentBrush"),
        MacroRunState.Parando or MacroRunState.Falhou => (Brush)FindResource("DangerBrush"),
        MacroRunState.CalibracaoNecessaria => new SolidColorBrush(Color.FromRgb(255, 195, 90)),
        MacroRunState.Armado => new SolidColorBrush(Color.FromRgb(83, 166, 255)),
        _ => new SolidColorBrush(Color.FromRgb(137, 145, 157))
    };

    private static string StateLabel(MacroRunState state) => state switch
    {
        MacroRunState.Parado => "Parado",
        MacroRunState.Armado => "Armado",
        MacroRunState.Executando => "Executando",
        MacroRunState.Parando => "Parando",
        MacroRunState.Falhou => "Falha segura",
        MacroRunState.CalibracaoNecessaria => "Calibração necessária",
        _ => state.ToString()
    };

    private void ApplyInputModeDescription(InputMode mode)
    {
        InputModeDescription.Text = mode == InputMode.Foreground
            ? "Traz o Forza para frente quando necessário. É o modo recomendado e confiável."
            : "Não muda o foco: captura WGC + controle Xbox virtual. A janela pode ficar coberta, mas não minimizada; alguns campos de texto ainda estão em calibração.";
    }

    private static string InputModeLabel(InputMode mode) => mode == InputMode.Foreground
        ? "primeiro plano"
        : "segundo plano experimental";
}
