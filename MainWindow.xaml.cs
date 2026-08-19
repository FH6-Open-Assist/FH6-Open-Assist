using System.ComponentModel;
using System.Diagnostics;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;
using FH6OpenAssist.Workflows;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace FH6OpenAssist;

public sealed partial class MainWindow : Window
{
    private readonly AppPaths _paths = AppPaths.Current;
    private readonly UserPreferences _preferences;
    private readonly AutomationSettings _settings;
    private readonly AutomationLogger _logger;
    private readonly GameInputService _input;
    private readonly GameCaptureService _capture;
    private readonly ResourceTracker _resources;
    private readonly AutomationCoordinator _coordinator;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _usingMica;
    private bool _loaded;
    private bool _updatingControls;
    private MacroRunState _currentState = MacroRunState.Parado;

    private const string ViGEmOfficialUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

    public MainWindow(UserPreferences preferences)
    {
        _preferences = preferences;
        InitializeComponent();

        MainContent.RequestedTheme = ToElementTheme(_preferences.Theme);
        _usingMica = ConfigureBackdrop();
        _dispatcherQueue = DispatcherQueue;
        _settings = AutomationSettings.Load(_paths);
        _settings.InputMode = preferences.InputMode;
        _logger = new AutomationLogger(_paths.DataDirectory);
        var gameWindow = new GameWindowService(_settings, _logger);
        _input = new GameInputService(gameWindow, _settings, _logger);
        _input.BackgroundInputAvailabilityChanged += Input_BackgroundInputAvailabilityChanged;
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

        _hotkeys = new GlobalHotkeyService(this, minimumWidthDips: 720, minimumHeightDips: 620);
        _hotkeys.AppWindow.Resize(new SizeInt32(1180, 780));
        _hotkeys.ToggleRequested += () => _dispatcherQueue.TryEnqueue(ToggleMacro);
        _hotkeys.EndRequested += () => _dispatcherQueue.TryEnqueue(EndMacro);
        Closed += MainWindow_Closed;
    }

    private void MainContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _updatingControls = true;
        SpMacro.IsChecked = true;
        ThemeComboBox.SelectedIndex = (int)_preferences.Theme;
        var backgroundAvailable = UpdateBackgroundInputStatus();
        if (_settings.InputMode == InputMode.BackgroundExperimental && !backgroundAvailable)
        {
            _settings.InputMode = InputMode.Foreground;
            _preferences.InputMode = InputMode.Foreground;
            _input.SetMode(InputMode.Foreground);
            SavePreferences();
            _logger.Warn("Preferência de segundo plano revertida para primeiro plano porque o ViGEm não respondeu.");
        }

        ForegroundMode.IsChecked = _settings.InputMode == InputMode.Foreground;
        BackgroundMode.IsChecked = _settings.InputMode == InputMode.BackgroundExperimental;
        _updatingControls = false;
        ApplyInputModeDescription(_settings.InputMode);
        UpdateInputModeControlsAvailability();
        MainContent.ActualThemeChanged += MainContent_ActualThemeChanged;
        if (_hotkeys.HotkeysRegistered)
        {
            _logger.Info("Hotkeys globais F8 e F9 registradas.");
        }
        else
        {
            _logger.Error($"Hotkeys globais indisponíveis: {_hotkeys.RegistrationError}");
        }

        _logger.Info(
            $"FH6 Open Assist iniciado em {InputModeLabel(_settings.InputMode)}. Ative um BOT; F8 executa/pausa e F9 encerra.");
        _logger.Info(_usingMica
            ? "Plano de fundo Mica ativado."
            : "Plano de fundo sólido compatível ativado.");
        _logger.Info($"Tema efetivo: {MainContent.ActualTheme}.");
    }

    private void MainContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 1000;
        PageLayout.Padding = new Thickness(isNarrow ? 16 : 24);

        Grid.SetRow(HeaderResourcesPanel, isNarrow ? 1 : 0);
        Grid.SetColumn(HeaderResourcesPanel, isNarrow ? 0 : 1);
        HeaderResourcesPanel.Margin = isNarrow
            ? new Thickness(0, 16, 0, 0)
            : new Thickness(16, 0, 0, 0);

        MainLeftColumn.Width = isNarrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(380);
        MainGapColumn.Width = new GridLength(isNarrow ? 0 : 18);
        MainRightColumn.Width = isNarrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        MainTopRow.Height = GridLength.Auto;
        MainGapRow.Height = new GridLength(isNarrow ? 16 : 0);
        MainBottomRow.Height = new GridLength(isNarrow ? 480 : 0);

        Grid.SetRow(LogPanel, isNarrow ? 2 : 0);
        Grid.SetColumn(LogPanel, isNarrow ? 0 : 2);
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

    private async void InstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        var kind = _coordinator.SelectedMacro ?? MacroKind.FarmarSp;
        var dialog = new ContentDialog
        {
            Title = $"Antes de ativar · {MacroDisplayName(kind)}",
            Content = CreateInstructions(kind),
            PrimaryButtonText = "Entendi",
            XamlRoot = MainContent.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void InputMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updatingControls)
        {
            return;
        }

        if (sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<InputMode>(tag, out var mode))
        {
            return;
        }

        if (mode == InputMode.BackgroundExperimental && !_input.IsBackgroundInputAvailable)
        {
            _updatingControls = true;
            ForegroundMode.IsChecked = true;
            BackgroundMode.IsChecked = false;
            _updatingControls = false;
            ViGEmInfoBar.IsOpen = true;
            return;
        }

        SetInputMode(mode);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingControls ||
            ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<ThemePreference>(tag, out var theme))
        {
            return;
        }

        _preferences.Theme = theme;
        MainContent.RequestedTheme = ToElementTheme(theme);
        SavePreferences();
        _logger.Info($"Tema alterado para {ThemeLabel(theme)}.");
    }

    private void RetryViGEmButton_Click(object sender, RoutedEventArgs e)
    {
        var available = UpdateBackgroundInputStatus();
        UpdateInputModeControlsAvailability();
        if (available)
        {
            _logger.Info("O segundo plano foi liberado após nova validação do ViGEm.");
        }
    }

    private void Input_BackgroundInputAvailabilityChanged(bool available, string? error)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            UpdateBackgroundInputStatusVisuals(available);
            if (!available && _settings.InputMode == InputMode.BackgroundExperimental)
            {
                try
                {
                    _input.SetMode(InputMode.Foreground);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _settings.InputMode = InputMode.Foreground;
                _preferences.InputMode = InputMode.Foreground;
                _updatingControls = true;
                ForegroundMode.IsChecked = true;
                BackgroundMode.IsChecked = false;
                _updatingControls = false;
                ApplyInputModeDescription(InputMode.Foreground);
                SavePreferences();
                _logger.Warn(
                    "O segundo plano perdeu a conexão ViGEm e foi revertido para primeiro plano. " +
                    $"Detalhe: {error}");
            }

            UpdateInputModeControlsAvailability();
        });
    }

    private void ViGEmOfficialLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ViGEmOfficialUrl) { UseShellExecute = true });
            _logger.Info("Página oficial do ViGEmBus aberta por solicitação do usuário.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _logger.Error($"Não foi possível abrir a página oficial do ViGEmBus: {exception.Message}");
        }
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            _currentState = state;
            StatusText.Text = StateLabel(state);
            StatusDot.Fill = StateBrush(state);
            DetailText.Text = message;
            ActivateBotButton.IsEnabled = state is not (MacroRunState.Executando or MacroRunState.Parando);
            ActivateBotButton.Content = state is MacroRunState.Armado or MacroRunState.Executando or MacroRunState.Parando
                ? "BOT ATIVO"
                : "Ativar BOT";
            UpdateInputModeControlsAvailability();
        });
    }

    private void AppendLog(string line)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (LogTextBox.Text.Length > 80_000)
            {
                LogTextBox.Text = LogTextBox.Text[^50_000..];
            }

            LogTextBox.Text += line + Environment.NewLine;
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
        });
    }

    private void Resources_Changed(ResourceSnapshot snapshot)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            SkillPointsText.Text = FormatResource(snapshot.SkillPoints, snapshot.SkillPointsEstimated);
            CreditsText.Text = FormatResource(snapshot.Credits, snapshot.CreditsEstimated);
        });
    }

    private static string FormatResource(long? value, bool estimated) =>
        value is null ? "—" : $"{(estimated ? "≈ " : string.Empty)}{value.Value:N0}";

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogTextBox.Text = string.Empty;

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        MainContent.ActualThemeChanged -= MainContent_ActualThemeChanged;
        _hotkeys.Dispose();
        _resources.Changed -= Resources_Changed;
        await _coordinator.DisposeAsync();
        _input.BackgroundInputAvailabilityChanged -= Input_BackgroundInputAvailabilityChanged;
        _input.Dispose();
        _capture.Dispose();
        _logger.Dispose();
    }

    private void SavePreferences()
    {
        try
        {
            _preferences.Save(_paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Não foi possível salvar as preferências: {exception.Message}");
        }
    }

    private bool ConfigureBackdrop()
    {
        if (!new AccessibilitySettings().HighContrast && MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            MainContent.Background = null;
            return true;
        }

        SystemBackdrop = null;
        MainContent.Background = ThemeBrush("WindowBackgroundBrush");
        return false;
    }

    private void MainContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        _usingMica = ConfigureBackdrop();
        StatusDot.Fill = StateBrush(_currentState);
        UpdateBackgroundInputStatusVisuals(_input.IsBackgroundInputAvailable);
    }

    private Brush StateBrush(MacroRunState state)
    {
        var resourceKey = state switch
        {
            MacroRunState.Executando => "StatusRunningBrush",
            MacroRunState.Parando or MacroRunState.Falhou => "StatusFailureBrush",
            MacroRunState.CalibracaoNecessaria => "StatusWarningBrush",
            MacroRunState.Armado => "StatusArmedBrush",
            _ => "StatusIdleBrush"
        };

        return ThemeBrush(resourceKey);
    }

    private Brush ThemeBrush(string resourceKey)
    {
        var themeKey = new AccessibilitySettings().HighContrast
            ? "HighContrast"
            : MainContent.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var resources = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[themeKey];
        return (Brush)resources[resourceKey];
    }

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
            ? "Recomendado. Traz o Forza para frente e usa somente teclado e mouse nativos do Windows."
            : "Mantém o foco atual e usa captura WGC + controle Xbox virtual validado. O jogo não pode ficar minimizado.";
    }

    private void SetInputMode(InputMode mode)
    {
        _input.SetMode(mode);
        _settings.InputMode = mode;
        _preferences.InputMode = mode;
        ApplyInputModeDescription(mode);
        SavePreferences();
        _logger.Info($"Modo de execução alterado para {InputModeLabel(mode)}.");
    }

    private bool UpdateBackgroundInputStatus()
    {
        var available = _input.TryEnableBackgroundInput();
        UpdateBackgroundInputStatusVisuals(available);
        return available;
    }

    private void UpdateBackgroundInputStatusVisuals(bool available)
    {
        ViGEmInfoBar.IsOpen = !available;
        ViGEmOfficialLink.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        ViGEmStatusDot.Fill = ThemeBrush(available ? "StatusRunningBrush" : "StatusWarningBrush");
        ViGEmStatusText.Text = available
            ? "ViGEm conectado · segundo plano disponível"
            : "ViGEm não conectado · use primeiro plano ou tente novamente";
    }

    private void UpdateInputModeControlsAvailability()
    {
        var changingState = _currentState is MacroRunState.Executando or MacroRunState.Parando;
        ForegroundMode.IsEnabled = !changingState;
        BackgroundMode.IsEnabled = !changingState && _input.IsBackgroundInputAvailable;
    }

    private static FrameworkElement CreateInstructions(MacroKind kind)
    {
        var (summary, steps) = kind switch
        {
            MacroKind.FarmarSp => (
                "Prepare o Subaru Impreza 22B-STI Version antes de armar o BOT.",
                new[]
                {
                    "Selecione o Subaru Impreza 22B-STI Version, de preferência com a árvore de habilidades desbloqueada.",
                    "Ative todas as assistências.",
                    "Vá para a rua; não inicie dentro da garagem."
                }),
            MacroKind.Farmar200kMin => (
                "Prepare o Nissan S-Cargo S1 800 sem tunagem antes de armar o BOT.",
                new[]
                {
                    "Desative todas as assistências.",
                    "Defina a dificuldade como Imbatível.",
                    "Vá para a rua; não inicie dentro da garagem."
                }),
            MacroKind.FarmarWheelspins => (
                "Confirme os recursos e abra a tela inicial correta.",
                new[]
                {
                    "A conta precisa ser VIP.",
                    "Tenha mais de 100.000 CR e mais de 30 SP.",
                    "Fique na garagem, no menu Campanha."
                }),
            _ => (
                "Prepare o jogo conforme a calibração do BOT.",
                new[] { "Confirme a tela inicial antes de ativar." })
        };

        var content = new StackPanel { Spacing = 10, MaxWidth = 540 };
        content.Children.Add(new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        foreach (var step in steps)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"• {step}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = "Depois, clique em Ativar BOT. Use F8 para iniciar/pausar e F9 para encerrar.",
            TextWrapping = TextWrapping.Wrap
        });
        return content;
    }

    private static ElementTheme ToElementTheme(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => Microsoft.UI.Xaml.ElementTheme.Light,
        ThemePreference.Dark => Microsoft.UI.Xaml.ElementTheme.Dark,
        _ => Microsoft.UI.Xaml.ElementTheme.Default
    };

    private static string ThemeLabel(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => "claro",
        ThemePreference.Dark => "escuro",
        _ => "sistema"
    };

    private static string MacroDisplayName(MacroKind kind) => kind switch
    {
        MacroKind.FarmarSp => "Skill Points",
        MacroKind.Farmar200kMin => "Farm de CR",
        MacroKind.FarmarWheelspins => "WheelSpin Mad Mike",
        MacroKind.GastarWheelspins => "Gastar Wheelspins",
        _ => kind.ToString()
    };

    private static string InputModeLabel(InputMode mode) => mode == InputMode.Foreground
        ? "primeiro plano"
        : "segundo plano experimental";
}

