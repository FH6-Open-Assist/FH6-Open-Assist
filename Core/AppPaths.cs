namespace FH6OpenAssist.Core;

public sealed class AppPaths
{
    public const string PortableMarkerFileName = "portable.marker";

    public static AppPaths Current { get; } = Create();

    private AppPaths(string appDirectory, string dataDirectory, bool isPortable)
    {
        AppDirectory = appDirectory;
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    public string AppDirectory { get; }

    public string AssetsDirectory => Path.Combine(AppDirectory, "Assets");

    public string AutomationSettingsPath => Path.Combine(AssetsDirectory, "automation.json");

    public string DataDirectory { get; }

    public string UserPreferencesPath => Path.Combine(DataDirectory, "user-preferences.json");

    public bool IsPortable { get; }

    private static AppPaths Create()
    {
        var appDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var portableMarkerPath = Path.Combine(appDirectory, PortableMarkerFileName);
        var isPortable = File.Exists(portableMarkerPath);
        var dataDirectory = isPortable
            ? appDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FH6 Open Assist");

        return new AppPaths(appDirectory, dataDirectory, isPortable);
    }
}
