using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VISOR.Diagnostics;

namespace VISOR.Update
{
    /// <summary>
    /// Checks GitHub Releases for a newer VISOR version. Runs fire-and-forget on
    /// startup and fails silently on any network/parse error so it never blocks or
    /// crashes the app. When a newer version is found it is recorded in
    /// <see cref="AvailableUpdate"/> and announced via <see cref="UpdateAvailable"/>
    /// so the Config window can surface a non-intrusive notification.
    /// </summary>
    public static class UpdateChecker
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/phitz86/VISOR/releases/latest";
        public const string ReleasesPageUrl = "https://github.com/phitz86/VISOR/releases/latest";

        /// <summary>
        /// The newer version found by the last check, or null if none/unknown.
        /// Stored so a Config window opened after the check still sees it.
        /// </summary>
        public static Version? AvailableUpdate { get; private set; }

        /// <summary>
        /// Raised when a newer version becomes available. Lets a Config window that
        /// is already open update itself when the check completes.
        /// </summary>
        public static event Action<Version>? UpdateAvailable;

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
                    AvailableUpdate = latest;
                    UpdateAvailable?.Invoke(latest);
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

        /// <summary>
        /// Opens the GitHub releases page in the user's default browser.
        /// </summary>
        public static void OpenReleasesPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Failed to open releases page", ex);
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
    }
}
