namespace HdrCapture;

/// <summary>
/// Minimal bilingual string helper. Chinese is the primary language; English is used when the
/// user picks it explicitly or (in auto mode) when the system UI culture is not Chinese.
/// </summary>
internal static class L
{
    private static bool _english;

    public static void Apply(string? language) => _english = language switch
    {
        "en" => true,
        "zh" => false,
        _ => !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
    };

    public static string T(string chinese, string english) => _english ? english : chinese;
}
