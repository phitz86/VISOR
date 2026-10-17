using System.Collections.Generic;
using System.Linq;
using VISOR.TrackData;
using Xunit;

namespace VISOR.Tests
{
    public class TrackSectionCatalogTests
    {
        private static List<TrackSectionSet> Parse(string tracksJson) =>
            TrackSectionCatalog.Parse("{ \"tracks\": [" + tracksJson + "] }")!;

        private const string Venue = """
            { "track": "Venue", "match": ["venue"], "sections": [ { "pct": 0.0, "name": "Start" } ] }
            """;

        // --- The shipped catalog (Data/TrackSections.json, copied beside the test binaries) ---

        [Theory]
        [InlineData("roadatlanta full", "Road Atlanta", "Full Course", "Road Atlanta", 11)]
        [InlineData("silverstone 2019 gp", "Silverstone Circuit", "Arena Grand Prix", "Silverstone", 20)]
        public void ShippedCatalog_ResolvesKnownLayouts(string slug, string display, string config,
            string expectedTrack, int expectedSections)
        {
            var set = TrackSectionCatalog.Resolve(slug, display, config);

            Assert.NotNull(set);
            Assert.Equal(expectedTrack, set!.Track);
            Assert.Equal(expectedSections, set.Sections.Count);
        }

        [Fact]
        public void ShippedCatalog_LeavesUncataloguedLayoutsUnresolved()
        {
            // Oulton Park Fosters has no section data; it must not borrow another layout's.
            Assert.Null(TrackSectionCatalog.Resolve("oulton fosters", "Oulton Park Circuit", "Fosters"));
        }

        [Fact]
        public void ShippedCatalog_EveryEntrySurvivesLoading()
        {
            string json = System.IO.File.ReadAllText(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "Data", "TrackSections.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            int inFile = doc.RootElement.GetProperty("tracks").GetArrayLength();

            var tracks = TrackSectionCatalog.Parse(json)!;

            Assert.Equal(inFile, tracks.Count);
            Assert.All(tracks, t =>
            {
                Assert.NotEmpty(t.Match);
                Assert.Equal(0f, t.Sections[0].Pct);
                Assert.Equal(t.Sections.OrderBy(s => s.Pct).Select(s => s.Pct), t.Sections.Select(s => s.Pct));
            });
        }

        // --- Loading rules (B6) ---

        [Fact]
        public void Parse_SkipsMalformedEntries_AndKeepsTheRest()
        {
            var tracks = Parse(Venue + """
                , { "track": "Broken", "match": "not-an-array", "sections": [] }
                , { "track": "NoSections", "match": ["nosections"], "sections": [] }
                , { "track": "NoKeys", "match": [], "sections": [ { "pct": 0.0, "name": "Start" } ] }
                """);

            Assert.Equal(new[] { "Venue" }, tracks.Select(t => t.Track));
        }

        [Fact]
        public void Parse_DropsBlankKeys_AndUnusableSections()
        {
            var tracks = Parse("""
                { "track": "Venue", "match": ["", "  ", "=", "VENUE"], "configs": ["", "GP"],
                  "sections": [
                    { "pct": 0.5, "name": "Back" },
                    { "pct": 0.0, "name": "Start" },
                    { "pct": 1.5, "name": "Off the lap" },
                    { "pct": 0.7, "name": "  " }
                  ] }
                """);

            var t = Assert.Single(tracks);
            Assert.Equal(new[] { "venue" }, t.Match);
            Assert.Equal(new[] { "gp" }, t.Configs);
            Assert.Equal(new[] { "Start", "Back" }, t.Sections.Select(s => s.Name));
        }

        [Fact]
        public void Parse_ReturnsNull_WithoutATracksArray()
        {
            Assert.Null(TrackSectionCatalog.Parse("{ \"something\": [] }"));
        }

        // --- Resolve rules ---

        [Fact]
        public void Resolve_PrefersAConfigSpecificEntry_OverAVenueWideOne()
        {
            var tracks = Parse("""
                { "track": "Venue (any)", "match": ["venue"], "sections": [ { "pct": 0.0, "name": "A" } ] },
                { "track": "Venue GP", "match": ["venue"], "configs": ["gp"], "sections": [ { "pct": 0.0, "name": "B" } ] }
                """);

            Assert.Equal("Venue GP", TrackSectionCatalog.Resolve(tracks, "venue gp", "Venue", "")!.Track);
            Assert.Equal("Venue (any)", TrackSectionCatalog.Resolve(tracks, "venue national", "Venue", "National")!.Track);
        }

        [Fact]
        public void Resolve_ConfigKeysMatchWholeWordsOnly()
        {
            var tracks = Parse("""
                { "track": "Venue GP", "match": ["venue"], "configs": ["gp"], "sections": [ { "pct": 0.0, "name": "A" } ] }
                """);

            Assert.NotNull(TrackSectionCatalog.Resolve(tracks, "venue gp", "Venue", ""));
            // "gpshort" is a different layout: it must not inherit the GP percentages.
            Assert.Null(TrackSectionCatalog.Resolve(tracks, "venue gpshort", "Venue", ""));
        }

        [Fact]
        public void Resolve_ExactSlugKeys_DoNotMatchLongerSlugs()
        {
            var tracks = Parse("""
                { "track": "Laguna Seca", "match": ["=lagunaseca"], "sections": [ { "pct": 0.0, "name": "A" } ] }
                """);

            Assert.NotNull(TrackSectionCatalog.Resolve(tracks, "lagunaseca", "WeatherTech Raceway Laguna Seca", ""));
            Assert.Null(TrackSectionCatalog.Resolve(tracks, "lagunaseca school", "WeatherTech Raceway Laguna Seca", ""));
        }

        [Fact]
        public void Resolve_VenueKeysMatchTheDisplayNameToo()
        {
            var tracks = Parse(Venue);

            Assert.NotNull(TrackSectionCatalog.Resolve(tracks, "vn 2024", "The Venue Circuit", ""));
            Assert.Null(TrackSectionCatalog.Resolve(tracks, "elsewhere", "Another Circuit", ""));
        }
    }
}
