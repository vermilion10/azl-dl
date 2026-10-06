using System.Text.Json;
using System.Text.Json.Serialization;
using AzlDl.App.Theming;

namespace AzlDl.App.Settings;

// User preferences, stored as JSON in the user's application-data folder (azl-dl/settings.json).
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AppStyle Style { get; set; } = AppStyle.ChartRoom;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // Where downloaded and decoded game data lives (the CLI's output folder layout).
    public string DataFolder { get; set; } = DefaultDataFolder;

    // True when the last load found a damaged settings file and fell back to defaults.
    [JsonIgnore]
    public bool WasReset { get; private set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "azl-dl");

    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "azl-dl", "data");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return new AppSettings { WasReset = true };
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
