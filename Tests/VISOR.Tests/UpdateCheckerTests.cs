using System;
using VISOR.Update;
using Xunit;

namespace VISOR.Tests
{
    public class UpdateCheckerTests
    {
        [Theory]
        [InlineData("v1.2.1", "1.2.1.0")]
        [InlineData("1.2.1.0", "1.2.1.0")]
        [InlineData("v0.9.13", "0.9.13.0")]
        [InlineData("v1.0.0-rc1", "1.0.0.0")]
        [InlineData("VISOR v1.3", "1.3.0.0")]
        public void ParseVersion_ReadsTheVersionInATag(string tag, string expected)
        {
            Version? parsed = UpdateChecker.ParseVersion(tag);

            Assert.NotNull(parsed);
            Assert.Equal(Version.Parse(expected), UpdateChecker.Normalize(parsed!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0983b")]      // a real early beta tag: no dotted version in it
        [InlineData("latest")]
        public void ParseVersion_ReturnsNull_WithoutADottedVersion(string? tag)
        {
            Assert.Null(UpdateChecker.ParseVersion(tag));
        }

        [Fact]
        public void Normalize_MakesThreeAndFourPartVersionsCompareEqual()
        {
            var three = UpdateChecker.Normalize(new Version(1, 2, 1));
            var four = UpdateChecker.Normalize(new Version(1, 2, 1, 0));

            Assert.Equal(four, three);
            Assert.False(three > four);
        }

        [Fact]
        public void Normalize_OrdersReleasesAsExpected()
        {
            var installed = UpdateChecker.Normalize(new Version(1, 2, 1, 0));

            Assert.True(UpdateChecker.Normalize(new Version(1, 3)) > installed);
            Assert.True(UpdateChecker.Normalize(new Version(1, 2, 2)) > installed);
            Assert.False(UpdateChecker.Normalize(new Version(1, 0, 0)) > installed);
        }
    }
}
