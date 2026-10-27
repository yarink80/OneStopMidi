using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneStopMidi.App.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON. Missing or corrupt files yield defaults.</summary>
public sealed class SettingsStore
{
    public static string DefaultFolder => Common.AppPaths.DataFolder;

    public static string DefaultPath => Path.Combine(DefaultFolder, "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    /// <summary>Set when the last <see cref="Load"/> found a file it could not read (it was backed up).</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>True when the last <see cref="Load"/> found no settings file (first run).</summary>
    public bool WasMissing { get; private set; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? DefaultPath);
    }

    public AppSettings Load()
    {
        LastLoadError = null;
        WasMissing = false;
        if (!File.Exists(FilePath))
        {
            WasMissing = true;
            return new AppSettings().Normalize();
        }
        try
        {
            var json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (s is null) throw new JsonException("Settings file is empty.");
            return s.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            LastLoadError = ex.Message;
            try
            {
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch
            {
                // best effort
            }
            return new AppSettings().Normalize();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
