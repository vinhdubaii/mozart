using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace MozartBrowser.Services.Update
{
    public class UpdateInfo
    {
        public string Version { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string ReleaseNotesUrl { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks github.com/{owner}/{repo}/releases/latest for a newer version than
    /// the running assembly, and can silently launch the downloaded Inno Setup
    /// installer with /VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS so the
    /// update finishes without extra clicks from the user.
    /// </summary>
    public class UpdateService
    {
        private readonly string _owner;
        private readonly string _repo;
        private readonly HttpClient _http;

        public event EventHandler<UpdateInfo>? UpdateAvailable;

        public UpdateService(string owner, string repo)
        {
            _owner = owner;
            _repo = repo;

            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MozartBrowser", CurrentVersion.ToString()));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
                using var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    return null;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var versionText = tagName.TrimStart('v', 'V');

                if (!Version.TryParse(versionText, out var remoteVersion))
                    return null;

                if (remoteVersion <= CurrentVersion)
                    return null;

                string? downloadUrl = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }

                if (downloadUrl == null)
                    return null;

                var info = new UpdateInfo
                {
                    Version = remoteVersion.ToString(),
                    DownloadUrl = downloadUrl,
                    ReleaseNotesUrl = root.TryGetProperty("html_url", out var htmlUrl)
                        ? htmlUrl.GetString() ?? string.Empty
                        : string.Empty
                };

                UpdateAvailable?.Invoke(this, info);
                return info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Update check failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Downloads the installer to %TEMP% and launches it silently, replacing the running app.</summary>
        public async Task DownloadAndInstallAsync(UpdateInfo info)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"MozartBrowser-Setup-{info.Version}.exe");

            using (var response = await _http.GetAsync(info.DownloadUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempPath);
                await response.Content.CopyToAsync(fs);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });

            System.Windows.Application.Current.Shutdown();
        }
    }
}
