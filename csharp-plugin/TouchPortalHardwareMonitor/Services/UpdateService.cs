using System.Net.Http;
using System.Text.Json;
using TouchPortalHardwareMonitor.Models;

namespace TouchPortalHardwareMonitor.Services;

/// <summary>
/// Checks for a newer plugin release. Uses the GitHub Releases API (the
/// canonical source of releases) rather than package.json on main, which tracks
/// the in-development version and drifts ahead of what's actually released.
/// </summary>
public sealed class UpdateService
{
    // /releases/latest excludes drafts and pre-releases, so this is the latest
    // stable release tag (e.g. "v2.2.1").
    private const string LatestReleaseApi =
        "https://api.github.com/repos/spdermn02/TouchPortal-HardwareMonitor/releases/latest";

    public string? LastError { get; private set; }

    /// <summary>
    /// Returns the newer version (e.g. "2.2.2") if the latest release is newer
    /// than <paramref name="currentVersion"/>; otherwise null.
    /// </summary>
    public async Task<string?> CheckForUpdateAsync(string currentVersion, Action<string>? log = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub requires a User-Agent; without it the API returns 403.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TouchPortalHardwareMonitor");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(LatestReleaseApi);
            var release = JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubRelease);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return null;
            }

            var latest = ParseVersion(release.TagName);
            var current = ParseVersion(currentVersion);
            if (latest == null || current == null)
            {
                return null;
            }

            log?.Invoke($"[Update] current={current} latest={latest} (tag {release.TagName})");
            return latest > current ? release.TagName.TrimStart('v', 'V') : null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log?.Invoke($"[Update] check failed: {ex.Message}");
            return null;
        }
    }

    // Parse a version like "v2.2.1" or "2.0.0-alpha-1" into a comparable Version.
    private static Version? ParseVersion(string raw)
    {
        var s = raw.TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash]; // drop pre-release suffix
        return Version.TryParse(s, out var v) ? v : null;
    }
}
