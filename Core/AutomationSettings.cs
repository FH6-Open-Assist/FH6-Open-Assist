using System.Text.Json;
using System.Text.Json.Serialization;

namespace FH6OpenAssist.Core;

public enum InputMode
{
    Foreground,
    BackgroundExperimental
}

public sealed class AutomationSettings
{
    public string GameProcessName { get; set; } = "forzahorizon6";
    public string GameWindowTitle { get; set; } = "Forza Horizon 6";
    public InputMode InputMode { get; set; } = InputMode.Foreground;
    public int PollIntervalMs { get; set; } = 350;
    public int ActionDelayMs { get; set; } = 450;
    public int ScreenTimeoutSeconds { get; set; } = 35;
    public string DiagnosticsDirectory { get; set; } = "diagnostics";
    public SpSettings Sp { get; set; } = new();
    public SpinSettings Spins { get; set; } = new();
    public SpinSettings RevueltoSpins { get; set; } = new()
    {
        SkillPointsPerCar = 39,
        CreditsPerCar = 365_000
    };
    public CrFarmSettings CrFarm { get; set; } = new();

    [JsonIgnore]
    public string BaseDirectory { get; private set; } = AppPaths.Current.AppDirectory;

    [JsonIgnore]
    public string DataDirectory { get; private set; } = AppPaths.Current.DataDirectory;

    [JsonIgnore]
    public string AssetsDirectory => Path.Combine(BaseDirectory, "Assets");

    [JsonIgnore]
    public string VisionAssetsDirectory => Path.Combine(AssetsDirectory, "Vision");

    [JsonIgnore]
    public string OcrScriptPath => Path.Combine(AssetsDirectory, "windows_ocr_json.ps1");

    [JsonIgnore]
    public string DiagnosticsPath => Path.Combine(DataDirectory, DiagnosticsDirectory);

    [JsonIgnore]
    public string CrPositionModelPath => Path.Combine(VisionAssetsDirectory, CrFarm.PositionModelFileName);

    [JsonIgnore]
    public string CrFarmDatasetPath => Path.Combine(DataDirectory, CrFarm.DatasetDirectory);

    public static AutomationSettings Load() => Load(AppPaths.Current);

    public static AutomationSettings Load(AppPaths paths)
    {
        var options = CreateJsonOptions();
        AutomationSettings settings;

        if (!File.Exists(paths.AutomationSettingsPath))
        {
            settings = new AutomationSettings();
        }
        else
        {
            settings = JsonSerializer.Deserialize<AutomationSettings>(
                           File.ReadAllText(paths.AutomationSettingsPath),
                           options)
                ?? new AutomationSettings();
        }

        settings.BaseDirectory = paths.AppDirectory;
        settings.DataDirectory = paths.DataDirectory;
        Directory.CreateDirectory(settings.DiagnosticsPath);
        return settings;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class SpSettings
{
    public int DirectTarget { get; set; } = 999;
    public int PointsPerRace { get; set; } = 10;
    public string ShareCode { get; set; } = "122697651";
    public int CinematicDelaySeconds { get; set; } = 18;
    public int WRefreshMs { get; set; } = 600;
}

public sealed class SpinSettings
{
    public int SkillPointsPerCar { get; set; } = 21;
    public int CreditsPerCar { get; set; } = 100_000;
    public int PreserveCredits { get; set; } = 0;
}
public sealed class CrFarmSettings
{
    public string PositionModelFileName { get; set; } = "cr-position.onnx";
    public string DatasetDirectory { get; set; } = "ExemplosPosition";
    public int InputWidth { get; set; } = 160;
    public int InputHeight { get; set; } = 96;
    public float CropX { get; set; } = 0.18f;
    public float CropY { get; set; } = 0.18f;
    public float CropWidth { get; set; } = 0.68f;
    public float CropHeight { get; set; } = 0.82f;
    public double ValidThreshold { get; set; } = 0.90;
    public double InvalidThreshold { get; set; } = 0.20;
    public int ConsensusFrames { get; set; } = 3;
    public int FrameIntervalMs { get; set; } = 300;
    public int OutcomeSettleMs { get; set; } = 1_200;
    public int RecoveryDelaySeconds { get; set; } = 15;
    public int MaximumRecoveries { get; set; } = 10;
    public int MinimumSuccessfulCreditGain { get; set; } = 100_000;
    public int MaximumSamplesPerClass { get; set; } = 600;
    public bool CollectSamples { get; set; } = true;
}
