using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using VISOR.Diagnostics;

namespace VISOR.Update
{
    /// <summary>
    /// Checks GitHub Releases for a newer VISOR version and notifies the user.
    /// Runs fire-and-forget on startup and fails silently on any network/parse
    /// error so it never blocks or crashes the app.
    /// </summary>
    public static class UpdateChecker
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/phitz86/VISOR/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/phitz86/VISOR/releases/latest";

        public static async Task CheckForUpdatesAsync()
        {
            try
            {
                Version? latest = await GetLatestReleaseVersionAsync();
                if (latest == null)
                    return;

                Version? current = Assembly.GetExecutingAssembly().GetName().Version;
                if (current == null)
                    return;

                latest = Normalize(latest);
                current = Normalize(current);

                if (latest > current)
                {
                    Log.Info($"Update available: {latest} (current {current})");
                    Application.Current?.Dispatcher.Invoke(() => PromptUpdate(current, latest));
                }
                else
                {
                    Log.Info($"VISOR is up to date (current {current}, latest {latest})");
                }
            }
            catch (Exception ex)
            {
                // Update checks are best-effort; a failure should never surface to the user.
                Log.Warning($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static async Task<Version?> GetLatestReleaseVersionAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub's API requires a User-Agent header.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VISOR-UpdateChecker");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await client.GetStringAsync(LatestReleaseApiUrl);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagElement))
                return null;

            return ParseVersion(tagElement.GetString());
        }

        /// <summary>
        /// Extracts a Version from a release tag such as "v0.9.13", "0.9.13.0",
        /// or "v1.0.0-rc1". Returns null if no version-like substring is found.
        /// </summary>
        private static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            Match match = Regex.Match(tag, @"\d+(\.\d+){1,3}");
            if (!match.Success)
                return null;

            return Version.TryParse(match.Value, out Version? version) ? version : null;
        }

        /// <summary>
        /// Pads a Version to four components so comparisons between tags like
        /// "0.9.13" and "0.9.13.0" behave intuitively (unset components are -1).
        /// </summary>
        private static Version Normalize(Version v)
        {
            return new Version(
                v.Major < 0 ? 0 : v.Major,
                v.Minor < 0 ? 0 : v.Minor,
                v.Build < 0 ? 0 : v.Build,
                v.Revision < 0 ? 0 : v.Revision);
        }

        private static void PromptUpdate(Version current, Version latest)
        {
            var result = MessageBox.Show(
                $"A new version of VISOR is available.\n\n" +
                $"Installed: {current}\nLatest: {latest}\n\n" +
                $"Open the download page?",
                "VISOR Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Failed to open releases page", ex);
            }
        }
    }
}
