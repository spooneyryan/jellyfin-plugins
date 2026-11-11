using Jellyfin.Plugin.LibraryMaintenance.Move;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

public sealed class MoveTitleTests
{
    [Theory]
    [InlineData("Heat", 1995, "Heat (1995)")]
    [InlineData("Heat", null, "Heat")]
    [InlineData("Heat", 0, "Heat")]
    [InlineData(null, null, "")]
    [InlineData(null, 1999, " (1999)")]
    // The doubled year (the 1.0.2 verifier): Jellyfin keeps the year in a name it didn't parse.
    [InlineData("Folder Movie (2008)", 2008, "Folder Movie (2008)")]
    [InlineData("Folder Movie (2008) ", 2008, "Folder Movie (2008) ")]
    [InlineData("Folder Movie (2008)", 2009, "Folder Movie (2008) (2009)")]
    [InlineData("1917", 2019, "1917 (2019)")]
    [InlineData("Movie 2008", 2008, "Movie 2008 (2008)")]
    [InlineData("Movie [2008]", 2008, "Movie [2008] (2008)")]
    public void WithYear_AddsTheYearOnce(string? name, int? year, string expected)
        => Assert.Equal(expected, MoveTitle.WithYear(name, year));
}
