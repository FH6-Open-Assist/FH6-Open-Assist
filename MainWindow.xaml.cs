using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;
using FH6OpenAssist.Workflows;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace FH6OpenAssist;

public sealed partial class MainWindow : Window
{
    private const int MaximumVisibleLogCharacters = 32_000;
    private const int RetainedVisibleLogCharacters = 24_000;
    private readonly AppPaths _paths = AppPaths.Current;
    private readonly UserPreferences _preferences;
    private readonly AutomationSettings _settings;
    private readonly AutomationLogger _logger;
    private readonly GameInputService _input;
    private readonly GameCaptureService _capture;
    private readonly CrPositionClassifier _crPosition;
    private readonly ResourceTracker _resources;
    private readonly SessionTelemetry _telemetry;
    private readonly AutomationCoordinator _coordinator;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _usingMica;
    private bool _loaded;
    private bool _updatingControls;
    private bool _isInitializingSelection;
    private bool _reduceMotion;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _logScrollPending;
    private ScrollViewer? _logScrollViewer;
    private MacroKind _selectedMacro = MacroKind.FarmarSp;
    private InputMode _selectedInputMode = InputMode.Foreground;
    private static readonly Dictionary<string, ImageSource> GrayscaleImageCache = new();
    private readonly Dictionary<MacroKind, BotCardVisuals> _botCards = new();
    private CancellationTokenSource? _botAnimationCancellation;
    private MacroRunState _currentState = MacroRunState.Parado;

    private sealed record BotCardVisuals(
        MacroKind Kind,
        RadioButton Button,
        FrameworkElement ImageHost,
        Image GrayImage,
        Image ColorImage,
        FrameworkElement TextOverlay,
        FrameworkElement Glint);

    private const string ViGEmOfficialUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";
    private const string SupportPixKey = "48bf874c-3e3d-48d1-89eb-4cd11b679167";

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
        var gameContext = new GameContextDetector(vision, _logger);
        _crPosition = new CrPositionClassifier(_settings, _logger);
        var crFarmSamples = new CrFarmSampleCollector(_settings, _logger);
        _resources = new ResourceTracker();
        _telemetry = new SessionTelemetry();
        var context = new AutomationContext
        {
            Settings = _settings,
            Logger = _logger,
            GameWindow = gameWindow,
            Input = _input,
            Capture = _capture,
            Vision = vision,
            GameContext = gameContext,
            CrPosition = _crPosition,
            CrFarmSamples = crFarmSamples,
            Resources = _resources,
            Telemetry = _telemetry,
            RunNestedAsync = (_, _) => Task.FromException(
                new AutomationFaultException("O encadeamento de workflows ainda não foi configurado."))
        };
        IMacroWorkflow[] workflows =
        [
            new SpFarmWorkflow(),
            new FastMoneyWorkflow(),
            new SpinFarmWorkflow(SpinFarmProfile.MadMike),
            new SpinFarmWorkflow(SpinFarmProfile.Revuelto),
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
        _hotkeys.AppWindow.Closing += MainWindow_Closing;
    }

    private async void MainContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _reduceMotion = !new UISettings().AnimationsEnabled;
        await InitializeBotCardsAsync();
        if (_shutdownStarted)
        {
            return;
        }

        ApplyBotVisualState(_selectedMacro);

        _updatingControls = true;
        _isInitializingSelection = true;
        SpMacro.IsChecked = true;
        _coordinator.Select(_selectedMacro);
        ThemeComboBox.SelectedIndex = (int)_preferences.Theme;
        var backgroundAvailable = UpdateBackgroundInputStatus();
        if (_settings.InputMode == InputMode.BackgroundExperimental && !backgroundAvailable)
        {
            _settings.InputMode = InputMode.Foreground;
            _preferences.InputMode = InputMode.Foreground;
            _selectedInputMode = InputMode.Foreground;
            _input.SetMode(InputMode.Foreground);
            SavePreferences();
            _logger.Warn("Preferência de segundo plano revertida para primeiro plano porque o ViGEm não respondeu.");
        }

        ForegroundMode.IsChecked = _settings.InputMode == InputMode.Foreground;
        BackgroundMode.IsChecked = _settings.InputMode == InputMode.BackgroundExperimental;
        _selectedInputMode = _settings.InputMode;
        _updatingControls = false;
        _isInitializingSelection = false;
        InitializeDashboard();
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
            $"FH6 Open Assist iniciado em {InputModeLabel(_settings.InputMode)}. Ative um BOT; F8 inicia/interrompe e F9 encerra.");
        _logger.Info(_usingMica
            ? "Plano de fundo Mica ativado."
            : "Plano de fundo sólido compatível ativado.");
        _logger.Info($"Tema efetivo: {MainContent.ActualTheme}.");
    }

    private void MainContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 1100;
        PageLayout.Padding = new Thickness(isNarrow ? 16 : 24);

        Grid.SetRow(HeaderResourcesPanel, isNarrow ? 1 : 0);
        Grid.SetColumn(HeaderResourcesPanel, isNarrow ? 0 : 1);
        HeaderResourcesPanel.Margin = isNarrow
            ? new Thickness(0, 16, 0, 0)
            : new Thickness(16, 0, 0, 0);
        ContextBotBadgesPanel.Orientation = e.NewSize.Width < 780
            ? Orientation.Vertical
            : Orientation.Horizontal;

        MainLeftColumn.Width = isNarrow
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(380);
        MainGapColumn.Width = new GridLength(isNarrow ? 0 : 18);
        MainRightColumn.Width = isNarrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        MainTopRow.Height = GridLength.Auto;
        MainGapRow.Height = new GridLength(isNarrow ? 16 : 0);
        MainBottomRow.Height = isNarrow ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(AssistantPanel, isNarrow ? 2 : 0);
        Grid.SetColumn(AssistantPanel, isNarrow ? 0 : 2);
    }

    private async Task InitializeBotCardsAsync()
    {
        if (_botCards.Count > 0)
        {
            return;
        }

        _botCards[MacroKind.FarmarSp] = new BotCardVisuals(
            MacroKind.FarmarSp,
            SpMacro,
            SpBotImageFrame,
            SpMacroImageGray,
            SpMacroImageColor,
            SpMacroImageOverlay,
            SpMacroImageGlint);

        _botCards[MacroKind.Farmar200kMin] = new BotCardVisuals(
            MacroKind.Farmar200kMin,
            CrMacro,
            CrBotImageFrame,
            CrMacroImageGray,
            CrMacroImageColor,
            CrMacroImageOverlay,
            CrMacroImageGlint);

        _botCards[MacroKind.FarmarWheelspins] = new BotCardVisuals(
            MacroKind.FarmarWheelspins,
            WheelspinMacro,
            WheelspinBotImageFrame,
            WheelspinMacroImageGray,
            WheelspinMacroImageColor,
            WheelspinMacroImageOverlay,
            WheelspinMacroImageGlint);

        _botCards[MacroKind.FarmarWheelspinsRevuelto] = new BotCardVisuals(
            MacroKind.FarmarWheelspinsRevuelto,
            RevueltoSpinMacro,
            RevueltoSpinBotImageFrame,
            RevueltoSpinMacroImageGray,
            RevueltoSpinMacroImageColor,
            RevueltoSpinMacroImageOverlay,
            RevueltoSpinMacroImageGlint);

        _botCards[MacroKind.GastarWheelspins] = new BotCardVisuals(
            MacroKind.GastarWheelspins,
            SpendWheelspinMacro,
            SpendWheelspinBotImageFrame,
            SpendWheelspinMacroImageGray,
            SpendWheelspinMacroImageColor,
            SpendWheelspinMacroImageOverlay,
            SpendWheelspinMacroImageGlint);

        try
        {
            SpMacroImageGray.Source = await CreateGrayscaleImageSourceAsync("ms-appx:///Assets/UI/Skill_Points.png");
            CrMacroImageGray.Source = await CreateGrayscaleImageSourceAsync("ms-appx:///Assets/UI/CR_Icon.png");
            WheelspinMacroImageGray.Source = await CreateGrayscaleImageSourceAsync("ms-appx:///Assets/UI/WheelSpin.jpg");
            RevueltoSpinMacroImageGray.Source = await CreateGrayscaleImageSourceAsync("ms-appx:///Assets/UI/WheelSpin.jpg");
            SpendWheelspinMacroImageGray.Source = await CreateGrayscaleImageSourceAsync("ms-appx:///Assets/UI/WheelSpin.jpg");
        }
        catch (Exception exception)
        {
            _logger.Warn($"Não foi possível carregar o efeito de escala de cinza dos cards: {exception.Message}");
        }
    }

    private static Task<ImageSource> CreateGrayscaleImageSourceAsync(string assetPath)
    {
        if (GrayscaleImageCache.TryGetValue(assetPath, out var cached))
        {
            return Task.FromResult(cached);
        }

        return LoadAndGrayscaleAssetAsync(assetPath);
    }

    private static async Task<ImageSource> LoadAndGrayscaleAssetAsync(string assetPath)
    {
        const string appPrefix = "ms-appx:///";
        if (!assetPath.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("O asset deve usar o prefixo ms-appx:///.", nameof(assetPath));
        }

        var relativePath = assetPath[appPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(applicationDirectory, relativePath));
        if (!fullPath.StartsWith(applicationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O asset calculado ficou fora da pasta da aplicação.");
        }

        var file = await StorageFile.GetFileFromPathAsync(fullPath);
        using var sourceStream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = data.DetachPixelData();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = pixels[i + 3];
            var gray = (byte)Math.Clamp((0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
            pixels[i] = gray;
            pixels[i + 1] = gray;
            pixels[i + 2] = gray;
            pixels[i + 3] = a;
        }

        var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels);
        await encoder.FlushAsync();

        output.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(output);
        GrayscaleImageCache[assetPath] = image;
        return image;
    }

    private void ApplyBotVisualState(MacroKind selectedKind)
    {
        foreach (var card in _botCards.Values)
        {
            var isSelected = card.Kind == selectedKind;
            var colorVisual = ElementCompositionPreview.GetElementVisual(card.ColorImage);
            colorVisual.Opacity = isSelected ? 1f : 0f;
            var overlayVisual = ElementCompositionPreview.GetElementVisual(card.TextOverlay);
            overlayVisual.Opacity = isSelected ? 0.42f : 1f;
            SetGlintOpacity(card, 0f);
        }
    }

    private static void SetGlintOpacity(BotCardVisuals card, float opacity)
    {
        var glintVisual = ElementCompositionPreview.GetElementVisual(card.Glint);
        glintVisual.Opacity = opacity;
    }

    private async Task AnimateMacroSelectionTransitionAsync(MacroKind previousKind, MacroKind nextKind)
    {
        if (!_botCards.TryGetValue(previousKind, out var previousCard) || !_botCards.TryGetValue(nextKind, out var nextCard))
        {
            return;
        }

        if (_botAnimationCancellation is not null)
        {
            _botAnimationCancellation.Cancel();
            _botAnimationCancellation.Dispose();
        }

        _botAnimationCancellation = new CancellationTokenSource();
        var token = _botAnimationCancellation.Token;

        if (_reduceMotion)
        {
            ElementCompositionPreview.GetElementVisual(previousCard.ColorImage).Opacity = 0f;
            ElementCompositionPreview.GetElementVisual(previousCard.TextOverlay).Opacity = 1f;
            ElementCompositionPreview.GetElementVisual(nextCard.ColorImage).Opacity = 1f;
            ElementCompositionPreview.GetElementVisual(nextCard.TextOverlay).Opacity = 0.42f;
            SetGlintOpacity(nextCard, 0f);
            return;
        }

        await Task.WhenAll(
            AnimateElementOpacityAsync(previousCard.ColorImage, 0f, TimeSpan.FromMilliseconds(300), token),
            AnimateElementOpacityAsync(previousCard.TextOverlay, 1f, TimeSpan.FromMilliseconds(300), token),
            AnimateElementOpacityAsync(nextCard.ColorImage, 0.5f, TimeSpan.FromMilliseconds(650), token),
            AnimateElementOpacityAsync(nextCard.TextOverlay, 0.42f, TimeSpan.FromMilliseconds(650), token),
            AnimateGlintPassAsync(nextCard, TimeSpan.FromMilliseconds(650), token));
        if (token.IsCancellationRequested)
        {
            return;
        }

        await Task.Delay(100, token);
        if (token.IsCancellationRequested)
        {
            return;
        }

        await Task.WhenAll(
            AnimateElementOpacityAsync(nextCard.ColorImage, 1f, TimeSpan.FromMilliseconds(650), token),
            AnimateGlintPassAsync(nextCard, TimeSpan.FromMilliseconds(650), token));
    }

    private static async Task AnimateElementOpacityAsync(FrameworkElement element, float targetOpacity, TimeSpan duration, CancellationToken token)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(Visual.Opacity));

        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = duration;
        animation.InsertKeyFrame(0f, (float)visual.Opacity);
        animation.InsertKeyFrame(
            1f,
            targetOpacity,
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f)));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var tcs = new TaskCompletionSource();
        batch.Completed += (_, _) =>
        {
            tcs.TrySetResult();
        };

        visual.StartAnimation(nameof(Visual.Opacity), animation);
        batch.End();

        await tcs.Task;
        if (!token.IsCancellationRequested)
        {
            visual.Opacity = targetOpacity;
            return;
        }

        visual.Opacity = targetOpacity;
    }

    private async Task AnimateGlintPassAsync(BotCardVisuals card, TimeSpan duration, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(card.Glint);
        var compositor = visual.Compositor;
        var hostWidth = (float)card.ImageHost.ActualWidth;
        var hostHeight = (float)card.ImageHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return;
        }

        var glintWidth = (float)card.Glint.ActualWidth;
        var glintHeight = (float)card.Glint.ActualHeight;
        visual.Offset = new Vector3(-glintWidth, -hostHeight, 0);
        visual.Opacity = 0f;

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.Duration = duration;
        move.InsertKeyFrame(0f, new Vector3(-glintWidth, -hostHeight, 0));
        move.InsertKeyFrame(
            1f,
            new Vector3(hostWidth, hostHeight, 0),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f)));

        var glow = compositor.CreateScalarKeyFrameAnimation();
        glow.Duration = move.Duration;
        glow.InsertKeyFrame(0f, 0f);
        glow.InsertKeyFrame(0.32f, 0.46f);
        glow.InsertKeyFrame(0.72f, 0.32f);
        glow.InsertKeyFrame(1f, 0f);

        visual.StopAnimation(nameof(Visual.Offset));
        visual.StopAnimation(nameof(Visual.Opacity));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var tcs = new TaskCompletionSource();
        batch.Completed += (_, _) =>
        {
            tcs.TrySetResult();
            SetGlintOpacity(card, 0f);
        };

        visual.StartAnimation(nameof(Visual.Offset), move);
        visual.StartAnimation(nameof(Visual.Opacity), glow);
        batch.End();
        await tcs.Task;
    }

    private async void Macro_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializingSelection || _updatingControls)
        {
            return;
        }

        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<MacroKind>(tag, out var kind))
        {
            return;
        }

        if (_currentState is MacroRunState.Executando or MacroRunState.Parando)
        {
            _updatingControls = true;
            if (_botCards.TryGetValue(_selectedMacro, out var selectedCard))
            {
                selectedCard.Button.IsChecked = true;
            }

            _updatingControls = false;
            return;
        }

        if (kind == _selectedMacro)
        {
            return;
        }

        var previous = _selectedMacro;
        if (!_coordinator.Select(kind))
        {
            _updatingControls = true;
            if (_botCards.TryGetValue(previous, out var selectedCard))
            {
                selectedCard.Button.IsChecked = true;
            }

            _updatingControls = false;
            return;
        }

        if (_telemetry.Snapshot.Bot is { } sessionBot && sessionBot != kind)
        {
            _telemetry.Reset();
        }

        _selectedMacro = kind;
        UpdateBotContext(kind);
        await AnimateMacroSelectionTransitionAsync(previous, kind);
    }

    private void ActivateBotButton_Click(object sender, RoutedEventArgs e) => _coordinator.ArmSelected();

    private void CopySupportPixButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(SupportPixKey);
        Clipboard.SetContent(package);
        _logger.Info("Chave PIX de apoio copiada para a área de transferência.");
    }

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
            _selectedInputMode = InputMode.Foreground;
            _updatingControls = false;
            ViGEmInfoBar.IsOpen = true;
            return;
        }

        _selectedInputMode = mode;
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
                _selectedInputMode = InputMode.Foreground;
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
            ActivateBotButton.IsEnabled = state is not (
                MacroRunState.Armado or MacroRunState.Executando or MacroRunState.Parando);
            ActivateBotButton.Content = state switch
            {
                MacroRunState.Armado => "BOT armado",
                MacroRunState.Executando => "Em execução",
                MacroRunState.Parando => "Interrompendo…",
                _ => "Ativar BOT"
            };
            SetBotSelectionAvailability(state is not (MacroRunState.Executando or MacroRunState.Parando));
            UpdateInputModeControlsAvailability();
            RenderSessionSnapshot(_telemetry.Snapshot);
        });
    }

    private void AppendLog(string line)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var updatedText = LogTextBox.Text + line + Environment.NewLine;
            if (updatedText.Length > MaximumVisibleLogCharacters)
            {
                var start = Math.Max(0, updatedText.Length - RetainedVisibleLogCharacters);
                var nextLine = updatedText.IndexOf('\n', start);
                if (nextLine >= 0 && nextLine + 1 < updatedText.Length)
                {
                    start = nextLine + 1;
                }

                updatedText = updatedText[start..];
            }

            LogTextBox.Text = updatedText;
            QueueLogScrollToEnd();
        });
    }

    private void QueueLogScrollToEnd()
    {
        if (_logScrollPending)
        {
            return;
        }

        _logScrollPending = true;
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _logScrollPending = false;
                LogTextBox.Select(LogTextBox.Text.Length, 0);
                LogTextBox.UpdateLayout();
                _logScrollViewer ??= FindDescendantScrollViewer(LogTextBox);
                _logScrollViewer?.ChangeView(
                    horizontalOffset: null,
                    verticalOffset: _logScrollViewer.ScrollableHeight,
                    zoomFactor: null,
                    disableAnimation: true);
            }))
        {
            _logScrollPending = false;
        }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var descendant = FindDescendantScrollViewer(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void Resources_Changed(ResourceSnapshot snapshot)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            SkillPointsText.Text = FormatResource(snapshot.SkillPoints, snapshot.SkillPointsEstimated);
            CreditsText.Text = FormatResource(snapshot.Credits, snapshot.CreditsEstimated);
            AutomationProperties.SetName(
                SkillPointsText,
                $"Pontos de habilidade detectados: {SkillPointsText.Text}");
            AutomationProperties.SetName(
                CreditsText,
                $"Créditos detectados: {CreditsText.Text}");
        });
    }

    private static string FormatResource(long? value, bool estimated) =>
        value is null ? "—" : $"{(estimated ? "≈ " : string.Empty)}{value.Value:N0}";

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogTextBox.Text = string.Empty;

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete)
        {
            return;
        }

        args.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        ShutdownDashboard();
        _botAnimationCancellation?.Cancel();
        _botAnimationCancellation?.Dispose();
        MainContent.ActualThemeChanged -= MainContent_ActualThemeChanged;
        _hotkeys.Dispose();
        _resources.Changed -= Resources_Changed;
        try
        {
            await _coordinator.DisposeAsync();
        }
        catch (Exception exception)
        {
            _logger.Error($"Falha ao liberar a automação durante o fechamento: {exception.Message}");
        }
        finally
        {
            _input.BackgroundInputAvailabilityChanged -= Input_BackgroundInputAvailabilityChanged;
            _input.Dispose();
            _capture.Dispose();
            _crPosition.Dispose();
            _logger.Dispose();
        }

        _shutdownComplete = true;
        sender.Closing -= MainWindow_Closing;
        Close();
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
        UpdateBotContext(_selectedMacro);
        RenderSessionSnapshot(_telemetry.Snapshot);
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
            ? "Recomendado. Traz o Forza para frente; os demais BOTs usam teclado e mouse nativos, mas o Farm de CR também usa o controle virtual no ajuste analógico."
            : "Mantém o foco atual e usa captura WGC + controle Xbox virtual validado. O jogo não pode ficar minimizado.";
    }

    private void SetInputMode(InputMode mode)
    {
        _input.SetMode(mode);
        _settings.InputMode = mode;
        _preferences.InputMode = mode;
        _selectedInputMode = mode;
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
            : "ViGEm não conectado · BOTs que exigem controle virtual estão indisponíveis";
    }

    private void UpdateInputModeControlsAvailability()
    {
        var changingState = _currentState is MacroRunState.Executando or MacroRunState.Parando;
        ForegroundMode.IsEnabled = !changingState;
        BackgroundMode.IsEnabled = !changingState && _input.IsBackgroundInputAvailable;
    }

    private static FrameworkElement CreateInstructions(MacroKind kind)
    {
        var definition = BotCatalog.Get(kind);

        var content = new StackPanel { Spacing = 10, MaxWidth = 540 };
        content.Children.Add(new TextBlock
        {
            Text = definition.Description,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Tela inicial: {definition.StartContext}",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var requirement in definition.Requirements)
        {
            content.Children.Add(new TextBlock
            {
                Text = requirement.Kind switch
                {
                    BotRequirementKind.Automated => $"• Automático: {requirement.Text}",
                    BotRequirementKind.Required => $"• Pré-requisito: {requirement.Text}",
                    _ => $"• Aviso: {requirement.Text}"
                },
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (definition.RequiresViGEm)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Este BOT exige uma conexão ViGEmBus válida mesmo em primeiro plano.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = "Depois, clique em Ativar BOT. Use F8 para iniciar/interromper e F9 para encerrar.",
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

    private static string MacroDisplayName(MacroKind kind) => BotCatalog.Get(kind).Name;

    private static string InputModeLabel(InputMode mode) => mode == InputMode.Foreground
        ? "primeiro plano"
        : "segundo plano experimental";
}

