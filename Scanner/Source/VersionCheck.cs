using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WrongWayScanner
{
    public sealed class ComponentVersion
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("current")]
        public string CurrentVersion { get; set; } = "";

        [JsonPropertyName("latest")]
        public string LatestVersion { get; set; } = "";

        [JsonPropertyName("updateAvailable")]
        public bool UpdateAvailable { get; set; }

        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = "";

        [JsonPropertyName("releaseNotes")]
        public string ReleaseNotes { get; set; } = "";

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = "";

        public override string ToString() => 
            $"{Name} {CurrentVersion}{(UpdateAvailable ? $" → {LatestVersion}" : "")} " +
            $"({ReleaseDate})";
    }

    public sealed class VersionCheckResult
    {
        [JsonPropertyName("suiteVersion")]
        public string SuiteVersion { get; set; } = "";

        [JsonPropertyName("lastChecked")]
        public DateTime LastChecked { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("components")]
        public List<ComponentVersion> Components { get; set; } = new();

        [JsonPropertyName("updateCount")]
        public int UpdateCount { get; set; }

        [JsonIgnore]
        public bool HasUpdates => UpdateCount > 0;

        public ComponentVersion? FindComponent(string name) =>
            Components.Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class VersionManager
    {
        private const string GitHubApiBaseUrl = "https://api.github.com/repos/dynapetey/Wrongway-Tuning-suite";
        private const string VersionCacheFile = ".version-cache.json";
        private const int CacheDurationMinutes = 60;

        private readonly string _suiteVersion;
        private readonly string _cacheFilePath;
        private readonly HttpClient _httpClient;

        public VersionManager(string? suiteVersion = null)
        {
            _suiteVersion = suiteVersion ?? typeof(VersionManager).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            _cacheFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WrongWay",
                VersionCacheFile
            );
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "WrongWay-Suite");
        }

        public async Task<VersionCheckResult> CheckForUpdatesAsync()
        {
            if (IsVersionCacheValid())
            {
                return LoadVersionCache();
            }

            var result = new VersionCheckResult
            {
                SuiteVersion = _suiteVersion,
                LastChecked = DateTime.UtcNow
            };

            try
            {
                result.Components.Add(await GetScannerVersionAsync());
                result.Components.Add(await GetPcmVersionAsync());
                result.Components.Add(await GetLiveUsbVersionAsync());
                result.Components.Add(await GetPresetsVersionAsync());

                result.UpdateCount = CountUpdates(result.Components);
                SaveVersionCache(result);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Version check failed: {ex.Message}");
            }

            return result;
        }

        private async Task<ComponentVersion> GetScannerVersionAsync()
        {
            var releases = await FetchReleasesAsync("Scanner");
            if (releases.Length > 0)
            {
                var latest = releases[0];
                return new ComponentVersion
                {
                    Name = "Scanner",
                    CurrentVersion = _suiteVersion,
                    LatestVersion = ExtractVersion(ReleaseValue(latest, "tag_name") ?? _suiteVersion),
                    UpdateAvailable = IsNewerVersion(_suiteVersion, ExtractVersion(ReleaseValue(latest, "tag_name") ?? _suiteVersion)),
                    ReleaseDate = ReleaseValue(latest, "published_at") ?? DateTime.UtcNow.ToString("O"),
                    ReleaseNotes = ReleaseValue(latest, "body") ?? "No release notes",
                    DownloadUrl = ReleaseValue(latest, "html_url") ?? ""
                };
            }

            return new ComponentVersion { Name = "Scanner", CurrentVersion = _suiteVersion };
        }

        private async Task<ComponentVersion> GetPcmVersionAsync()
        {
            var releases = await FetchReleasesAsync("PCM");
            if (releases.Length > 0)
            {
                var latest = releases[0];
                return new ComponentVersion
                {
                    Name = "PCM Hammer",
                    CurrentVersion = "2.0.0",
                    LatestVersion = ExtractVersion(ReleaseValue(latest, "tag_name") ?? "v2.0.0"),
                    UpdateAvailable = IsNewerVersion("2.0.0", ExtractVersion(ReleaseValue(latest, "tag_name") ?? "v2.0.0")),
                    ReleaseDate = ReleaseValue(latest, "published_at") ?? DateTime.UtcNow.ToString("O"),
                    ReleaseNotes = ReleaseValue(latest, "body") ?? "No release notes",
                    DownloadUrl = ReleaseValue(latest, "html_url") ?? ""
                };
            }

            return new ComponentVersion { Name = "PCM Hammer", CurrentVersion = "2.0.0" };
        }

        private async Task<ComponentVersion> GetLiveUsbVersionAsync()
        {
            var releases = await FetchReleasesAsync("live-usb-creator");
            if (releases.Length > 0)
            {
                var latest = releases[0];
                return new ComponentVersion
                {
                    Name = "Live USB Creator",
                    CurrentVersion = "0.4.8",
                    LatestVersion = ExtractVersion(ReleaseValue(latest, "tag_name") ?? "v0.4.8"),
                    UpdateAvailable = IsNewerVersion("0.4.8", ExtractVersion(ReleaseValue(latest, "tag_name") ?? "v0.4.8")),
                    ReleaseDate = ReleaseValue(latest, "published_at") ?? DateTime.UtcNow.ToString("O"),
                    ReleaseNotes = ReleaseValue(latest, "body") ?? "No release notes",
                    DownloadUrl = ReleaseValue(latest, "html_url") ?? ""
                };
            }

            return new ComponentVersion { Name = "Live USB Creator", CurrentVersion = "0.4.8" };
        }

        private Task<ComponentVersion> GetPresetsVersionAsync()
        {
            return Task.FromResult(new ComponentVersion
            {
                Name = "Tuning Presets",
                CurrentVersion = "1.0.0",
                LatestVersion = "1.0.0",
                UpdateAvailable = false,
                ReleaseDate = DateTime.UtcNow.ToString("O"),
                ReleaseNotes = "Gen 3 P59 TuningHistogramPresets module (VE, MAF, Knock, Spark, RAF, IDC, TCC)",
                DownloadUrl = $"{GitHubApiBaseUrl}/releases/latest"
            });
        }

        private async Task<JsonElement[]> FetchReleasesAsync(string releaseTag)
        {
            try
            {
                var url = $"{GitHubApiBaseUrl}/releases";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var releases = new List<JsonElement>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var tagName = element.GetProperty("tag_name").GetString() ?? "";
                    if (tagName.Contains(releaseTag, StringComparison.OrdinalIgnoreCase))
                    {
                        releases.Add(element.Clone());
                    }
                }

                return releases.ToArray();
            }
            catch
            {
                return Array.Empty<JsonElement>();
            }
        }

        private static string? ReleaseValue(JsonElement release, string name) =>
            release.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() : null;

        private static string ExtractVersion(string versionString)
        {
            return versionString.TrimStart('v', 'V');
        }

        private static bool IsNewerVersion(string current, string latest)
        {
            if (!Version.TryParse(current, out var currentVer) ||
                !Version.TryParse(latest, out var latestVer))
            {
                return false;
            }

            return latestVer > currentVer;
        }

        private static int CountUpdates(IEnumerable<ComponentVersion> components)
        {
            int count = 0;
            foreach (var component in components)
            {
                if (component.UpdateAvailable)
                    count++;
            }
            return count;
        }

        private bool IsVersionCacheValid()
        {
            if (!File.Exists(_cacheFilePath))
                return false;

            try
            {
                var cache = LoadVersionCache();
                var elapsed = DateTime.UtcNow - cache.LastChecked;
                return cache.SuiteVersion == _suiteVersion && elapsed.TotalMinutes >= 0 && elapsed.TotalMinutes < CacheDurationMinutes;
            }
            catch
            {
                return false;
            }
        }

        private VersionCheckResult LoadVersionCache()
        {
            var json = File.ReadAllText(_cacheFilePath);
            return JsonSerializer.Deserialize<VersionCheckResult>(json) ??
                   new VersionCheckResult { SuiteVersion = _suiteVersion };
        }

        private void SaveVersionCache(VersionCheckResult result)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(result, options);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Failed to cache version info: {ex.Message}");
            }
        }

        public void ClearCache()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                    File.Delete(_cacheFilePath);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Failed to clear version cache: {ex.Message}");
            }
        }
    }
}
