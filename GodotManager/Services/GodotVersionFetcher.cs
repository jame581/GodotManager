using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotManager.Services;

internal sealed record GodotRelease(
    string Version,
    bool IsStable,
    bool HasStandard,
    bool HasDotNet,
    DateTimeOffset PublishedAt);

internal sealed class GodotVersionFetcher
{
    private readonly HttpClient _httpClient;
    private const string GitHubApiUrl = "https://api.github.com/repos/godotengine/godot/releases";

    public GodotVersionFetcher(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("godman", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<GodotRelease>> FetchReleasesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var releases = new List<GodotRelease>();
            var page = 1;
            const int perPage = 100;

            while (page <= 3) // Fetch up to 300 releases
            {
                var url = $"{GitHubApiUrl}?per_page={perPage}&page={page}";
                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var githubReleases = JsonSerializer.Deserialize<List<GitHubRelease>>(json);

                if (githubReleases == null || githubReleases.Count == 0)
                {
                    break;
                }

                foreach (var release in githubReleases)
                {
                    var godotRelease = ParseRelease(release);
                    if (godotRelease != null)
                    {
                        releases.Add(godotRelease);
                    }
                }

                if (githubReleases.Count < perPage)
                {
                    break; // Last page
                }

                page++;
            }

            return releases.OrderByDescending(r => r.PublishedAt).ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch releases from GitHub: {ex.Message}", ex);
        }
    }

    private static GodotRelease? ParseRelease(GitHubRelease release)
    {
        if (release.Draft || string.IsNullOrWhiteSpace(release.TagName))
        {
            return null;
        }

        // Parse version from tag (e.g., "4.3-stable", "3.5.3-stable", "4.2-beta1")
        var tag = release.TagName;
        var isStable = tag.Contains("-stable");

        // Extract version number
        var versionPart = tag.Replace("-stable", "")
                             .Replace("-beta", "")
                             .Replace("-alpha", "")
                             .Replace("-rc", "")
                             .Replace("-dev", "");

        // Check if assets indicate Standard and/or .NET availability
        var hasStandard = release.Assets?.Any(a =>
            a.Name.Contains("win64.exe.zip") ||
            a.Name.Contains("linux.x86_64.zip") ||
            a.Name.Contains("linux_x86_64.zip")) ?? false;

        var hasDotNet = release.Assets?.Any(a =>
            a.Name.Contains("mono") ||
            a.Name.Contains("dotnet")) ?? false;

        // Only include if we have actual downloadable assets
        if (!hasStandard && !hasDotNet)
        {
            return null;
        }

        return new GodotRelease(
            versionPart,
            isStable,
            hasStandard,
            hasDotNet,
            release.PublishedAt);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
