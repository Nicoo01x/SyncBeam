using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SyncBeam.App;

/// <summary>
/// Checks for updates from GitHub Releases.
/// </summary>
public sealed class UpdateChecker
{
    private const string GitHubApiUrl = "https://api.github.com/repos/Nicoo01x/SyncBeam/releases/latest";
    private const string CurrentVersion = "3.0.6";

    private static readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "SyncBeam-UpdateChecker" },
            { "Accept", "application/vnd.github.v3+json" }
        },
        Timeout = TimeSpan.FromSeconds(10)
    };

    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    /// <summary>
    /// Check for updates asynchronously.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);

            if (release == null || string.IsNullOrEmpty(release.TagName))
                return;

            // Parse version from tag (e.g., "v3.1.0" -> "3.1.0")
            var latestVersion = release.TagName.TrimStart('v', 'V');

            if (IsNewerVersion(latestVersion, CurrentVersion))
            {
                // Find the installer asset
                var installerAsset = release.Assets?.FirstOrDefault(a =>
                    a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs
                {
                    CurrentVersion = CurrentVersion,
                    LatestVersion = latestVersion,
                    ReleaseNotes = release.Body ?? "",
                    DownloadUrl = installerAsset?.BrowserDownloadUrl ?? release.HtmlUrl ?? "",
                    ReleaseUrl = release.HtmlUrl ?? ""
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Error checking for updates: {ex.Message}");
        }
    }

    /// <summary>
    /// Compare two version strings.
    /// </summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();
            var currentParts = current.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Max(latestParts.Length, currentParts.Length); i++)
            {
                var latestPart = i < latestParts.Length ? latestParts[i] : 0;
                var currentPart = i < currentParts.Length ? currentParts[i] : 0;

                if (latestPart > currentPart)
                    return true;
                if (latestPart < currentPart)
                    return false;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

public class UpdateAvailableEventArgs : EventArgs
{
    public required string CurrentVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required string ReleaseNotes { get; init; }
    public required string DownloadUrl { get; init; }
    public required string ReleaseUrl { get; init; }
}

// GitHub API response models
public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
