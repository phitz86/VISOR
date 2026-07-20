using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VISOR.Diagnostics;

namespace VISOR.TrackData
{
    /// <summary>One named stretch of a circuit, starting at a LapDistPct fraction.</summary>
    public sealed class TrackSection
    {
        [JsonPropertyName("pct")] public float Pct { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    /// <summary>A full lap's worth of named sections for one track configuration.</summary>
    public sealed class TrackSectionSet
    {
        [JsonPropertyName("track")] public string Track { get; set; } = string.Empty;
        [JsonPropertyName("match")] public string[] Match { get; set; } = Array.Empty<string>();
        [JsonPropertyName("configs")] public string[] Configs { get; set; } = Array.Empty<string>();
        [JsonPropertyName("sections")] public List<TrackSection> Sections { get; set; } = new();
    }

    /// <summary>
    /// Loads Data/TrackSections.json (shipped beside the executable, user-editable) and
    /// resolves the current iRacing track to a section set.
    ///
    /// Matching is deliberately fuzzy: iRacing's machine TrackName slugs change across
    /// rescans ("spa combined" vs "spa 2023 grandprix"), so entries match on substrings of
    /// TrackName + TrackDisplayName instead of exact slugs. Config substrings guard layouts
    /// whose lap geometry differs (Silverstone National must not inherit Grand Prix
    /// percentages); an entry with no configs applies to every layout of the venue.
    /// </summary>
    public static class TrackSectionCatalog
    {
        private static List<TrackSectionSet>? _tracks;
        private static bool _loadAttempted;
        private static readonly object _loadLock = new();

        public static TrackSectionSet? Resolve(string trackName, string trackDisplayName, string trackConfig)
        {
            var tracks = GetTracks();
            if (tracks.Count == 0)
                return null;

            string haystack = ((trackName ?? "") + " " + (trackDisplayName ?? "")).ToLowerInvariant();

            // Config keys match against TrackConfigName plus the TrackName slug: iRacing puts
            // the layout in the config on multi-config venues ("Road Course") but only in the
            // slug on single-config content ("imola gp" with an empty config). The display name
            // stays out of this haystack — it is venue-wide ("Sebring International Raceway"),
            // so including it would let every layout of a venue satisfy the guard.
            string configHaystack = ((trackConfig ?? "") + " " + (trackName ?? "")).ToLowerInvariant();

            TrackSectionSet? configAgnostic = null;
            foreach (var track in tracks)
            {
                if (!track.Match.Any(m => haystack.Contains(m)))
                    continue;

                if (track.Configs.Length == 0)
                    configAgnostic ??= track;
                else if (track.Configs.Any(c => ContainsToken(configHaystack, c)))
                    return track;
            }
            return configAgnostic;
        }

        /// <summary>
        /// Whole-word containment for config keys. Venue matching stays substring-based
        /// (so "rburgring" covers Nürburgring/Nuerburgring/nurburgring), but config keys
        /// must land on word boundaries: "gp" matches "nurburgring gp" yet not
        /// "nurburgring gpshort" — the without-Arena layout must stay unresolved rather
        /// than inherit full Grand Prix percentages. Digits count as word characters, so
        /// "24" matches "24 heures" but not the year in "spa 2024 up".
        /// </summary>
        private static bool ContainsToken(string haystack, string key)
        {
            int start = 0;
            while (true)
            {
                int i = haystack.IndexOf(key, start, StringComparison.Ordinal);
                if (i < 0) return false;
                int end = i + key.Length;
                bool leftOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
                bool rightOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
                if (leftOk && rightOk) return true;
                start = i + 1;
            }
        }

        private static List<TrackSectionSet> GetTracks()
        {
            lock (_loadLock)
            {
                if (!_loadAttempted)
                {
                    _loadAttempted = true;
                    _tracks = Load();
                }
                return _tracks ?? new List<TrackSectionSet>();
            }
        }

        private static List<TrackSectionSet>? Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "TrackSections.json");
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warning($"[TrackSections] Catalog not found at {path}");
                    return null;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("tracks", out var tracksElement))
                {
                    Log.Warning("[TrackSections] Catalog has no 'tracks' array");
                    return null;
                }

                var tracks = tracksElement.Deserialize<List<TrackSectionSet>>() ?? new List<TrackSectionSet>();

                // Normalize once at load: lowercase match keys, sections in lap order.
                foreach (var track in tracks)
                {
                    track.Match = track.Match.Select(m => m.ToLowerInvariant()).ToArray();
                    track.Configs = track.Configs.Select(c => c.ToLowerInvariant()).ToArray();
                    track.Sections.Sort((a, b) => a.Pct.CompareTo(b.Pct));
                }
                tracks.RemoveAll(t => t.Sections.Count == 0 || t.Match.Length == 0);

                Log.Info($"[TrackSections] Loaded {tracks.Count} track entries from catalog");
                return tracks;
            }
            catch (Exception ex)
            {
                Log.Warning($"[TrackSections] Failed to load catalog: {ex.Message}");
                return null;
            }
        }
    }
}
