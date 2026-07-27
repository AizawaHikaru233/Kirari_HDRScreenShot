using System.Windows.Media;

namespace HdrCapture;

internal enum AppThemeMode { Auto, Light, Dark }

/// <summary>UI theme selection: light, dark, or follow the system apps theme.</summary>
internal static class ThemeService
{
    public static AppThemeMode Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => AppThemeMode.Light,
        "dark" => AppThemeMode.Dark,
        _ => AppThemeMode.Auto,
    };

    public static bool IsDark(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => SystemUsesDark(),
    };

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Brushes for the capture overlay's floating chrome (toolbar, popups, panels).</summary>
internal sealed record ChromeTheme(
    bool Dark,
    Brush PanelBg,
    Brush Text,
    Brush SubText,
    Brush Separator,
    Brush ActiveBg,
    Brush SwatchRing)
{
    private static Brush Make(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly ChromeTheme DarkTheme = new(
        Dark: true,
        PanelBg: Make(235, 32, 32, 32),
        Text: Make(255, 255, 255, 255),
        SubText: Make(170, 255, 255, 255),
        Separator: Make(80, 255, 255, 255),
        ActiveBg: Make(90, 60, 160, 255),
        SwatchRing: Make(60, 255, 255, 255));

    private static readonly ChromeTheme LightTheme = new(
        Dark: false,
        PanelBg: Make(242, 250, 250, 252),
        Text: Make(255, 27, 30, 34),
        SubText: Make(255, 95, 102, 112),
        Separator: Make(50, 0, 0, 0),
        ActiveBg: Make(70, 30, 130, 220),
        SwatchRing: Make(70, 0, 0, 0));

    public static ChromeTheme Resolve(bool dark) => dark ? DarkTheme : LightTheme;
}
