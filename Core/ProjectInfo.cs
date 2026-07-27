using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HdrCapture;

internal readonly record struct UpdateCheckResult(bool IsUpdateAvailable, string LatestVersion, Uri ReleaseUrl);

internal static class ProjectInfo
{
    public const string GitHubRepository = "AizawaHikaru233/Kirari_HDRScreenShot";
    public static readonly Uri GitHubUrl = new($"https://github.com/{GitHubRepository}");
    public static readonly Uri LatestReleaseApiUrl = new($"https://api.github.com/repos/{GitHubRepository}/releases/latest");

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd($"Kirari/{CurrentVersionText}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("The latest GitHub release has no tag.");
        var htmlUrl = root.GetProperty("html_url").GetString() ?? GitHubUrl.AbsoluteUri;
        var latest = ParseReleaseVersion(tag);
        return new UpdateCheckResult(latest.CompareTo(CurrentVersion) > 0, tag, new Uri(htmlUrl));
    }

    public static void OpenGitHub() => OpenUrl(GitHubUrl);

    public static void OpenUrl(Uri url) => Process.Start(new ProcessStartInfo
    {
        FileName = url.AbsoluteUri,
        UseShellExecute = true,
    });

    internal static Version ParseReleaseVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        if (!Version.TryParse(value, out var version))
            throw new InvalidOperationException($"GitHub release tag '{tag}' is not a version.");
        return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(8) };
}
