using System.Text.Json;
using System.Text.Json.Serialization;

namespace FH6OpenAssist.Core;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class UserPreferences
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public InputMode InputMode { get; set; } = InputMode.Foreground;

    public static UserPreferences Load(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.UserPreferencesPath))
            {
                return new UserPreferences();
            }

            return JsonSerializer.Deserialize<UserPreferences>(
                       File.ReadAllText(paths.UserPreferencesPath),
                       CreateJsonOptions())
                   ?? new UserPreferences();
        }
        catch (JsonException)
        {
            return new UserPreferences();
        }
        catch (IOException)
        {
            return new UserPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new UserPreferences();
        }
    }

    public void Save(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temporaryPath = paths.UserPreferencesPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, CreateJsonOptions()));
        File.Move(temporaryPath, paths.UserPreferencesPath, overwrite: true);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
