using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdrCapture;

/// <summary>A user-configurable global hotkey. Modifier flags match the Win32 <c>RegisterHotKey</c> values.</summary>
internal sealed class HotkeyConfig
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public bool Control { get; set; } = true;
    public bool Shift { get; set; } = true;
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public uint VirtualKey { get; set; } = 0x41; // 'A'
    public string KeyName { get; set; } = "A";

    [JsonIgnore]
    public uint Modifiers =>
        (Control ? ModControl : 0) | (Shift ? ModShift : 0) | (Alt ? ModAlt : 0) | (Win ? ModWin : 0);

    [JsonIgnore]
    public bool IsValid => VirtualKey != 0 && Modifiers != 0;

    public string Describe()
    {
        var parts = new List<string>(5);
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join("+", parts);
    }
}

/// <summary>Persisted user settings stored as JSON under <c>%APPDATA%\HdrCapture\settings.json</c>.</summary>
internal sealed class AppSettings
{
    public HotkeyConfig CaptureHotkey { get; set; } = new();
    public string OutputFormat { get; set; } = "hdrpng";
    public string? SaveDirectory { get; set; }
    /// <summary>File name pattern; <c>{...}</c> segments are DateTime format strings.</summary>
    public string FileNamePattern { get; set; } = "Kirari_{yyyyMMdd_HHmmss}";
    /// <summary>UI theme: "auto" (follow system), "light" or "dark".</summary>
    public string Theme { get; set; } = "auto";
    /// <summary>UI language: "auto" (follow system), "zh" or "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Finish (clipboard) also writes the HDR file to the output directory.</summary>
    public bool SaveFileOnFinish { get; set; }
    /// <summary>When exporting HDR PNG, also write an "_SDR.png" companion.</summary>
    public bool SaveSdrCopy { get; set; }
    public bool HideTrayIcon { get; set; }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kirari", "settings.json");

    // Settings written before the rename to Kirari.
    private static string LegacyConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrCapture", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(ConfigPath) ? ConfigPath : LegacyConfigPath;
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions);
                if (settings is not null)
                {
                    settings.CaptureHotkey ??= new HotkeyConfig();
                    // Legacy values (UltraHDR "jpg" / gain-map "png") normalize to HDR PNG.
                    if (settings.OutputFormat is not ("hdrpng" or "sdrpng" or "sdrjpg"))
                        settings.OutputFormat = "hdrpng";
                    if (settings.Theme is not ("auto" or "light" or "dark"))
                        settings.Theme = "auto";
                    if (settings.Language is not ("auto" or "zh" or "en"))
                        settings.Language = "auto";
                    // Old default pattern migrates to the branded one.
                    if (settings.FileNamePattern == "HDR_{yyyyMMdd_HHmmss}")
                        settings.FileNamePattern = "Kirari_{yyyyMMdd_HHmmss}";
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults rather than blocking startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    public string ResolveOutputDirectory()
    {
        var directory = string.IsNullOrWhiteSpace(SaveDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDR Capture")
            : SaveDirectory!;
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string FileExtension =>
        OutputFormat.Equals("sdrjpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";

    /// <summary>
    /// Expands the file name pattern ({...} = DateTime format) and appends the extension.
    /// SDR outputs use the base name as-is; HDR outputs carry an "_HDR" suffix, so an HDR file
    /// and its optional SDR companion form a natural pair (name_HDR.png / name.png).
    /// </summary>
    public string BuildFileName()
    {
        var pattern = string.IsNullOrWhiteSpace(FileNamePattern) ? "Kirari_{yyyyMMdd_HHmmss}" : FileNamePattern;
        string name;
        try
        {
            name = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{([^}]+)\}",
                match => DateTime.Now.ToString(match.Groups[1].Value));
        }
        catch (FormatException)
        {
            name = $"Kirari_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        var suffix = OutputFormat.Equals("hdrpng", StringComparison.OrdinalIgnoreCase) ? "_HDR" : string.Empty;
        return name + suffix + FileExtension;
    }
}

/// <summary>Start-with-Windows via the per-user Run registry key.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kirari";
    private const string LegacyValueName = "HdrCapture";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string || key?.GetValue(LegacyValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled && Environment.ProcessPath is { } path)
            key.SetValue(ValueName, $"\"{path}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
