using System;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Domain;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Domain;

/// <summary>
/// <see cref="StarValue"/>: every half step round-trips through stars, half-star units and
/// <c>UserData.Rating</c>; anything else is the 400.
/// </summary>
public sealed class StarValueTests
{
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 2)]
    [InlineData(1.5, 3)]
    [InlineData(2.0, 4)]
    [InlineData(2.5, 5)]
    [InlineData(3.0, 6)]
    [InlineData(3.5, 7)]
    [InlineData(4.0, 8)]
    [InlineData(4.5, 9)]
    [InlineData(5.0, 10)]
    public void EveryHalfStep_RoundTrips(double stars, int stars2)
    {
        var fromDecimal = StarValue.FromStars((decimal)stars);
        var fromDouble = StarValue.FromStars(stars);

        Assert.Equal(stars2, fromDecimal.Stars2);
        Assert.Equal(fromDecimal, fromDouble);
        Assert.Equal((decimal)stars, fromDecimal.Stars);
        Assert.Equal(fromDecimal, StarValue.FromStars2(stars2));
        Assert.Equal(stars2, fromDecimal.ToUserDataRating());
        Assert.Equal(fromDecimal, StarValue.FromUserDataRating(fromDecimal.ToUserDataRating()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(0.4)]
    [InlineData(3.25)]
    [InlineData(3.3)]
    [InlineData(5.5)]
    [InlineData(-0.5)]
    [InlineData(10)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.Epsilon)]
    [InlineData(3.5000000001)]
    public void AnythingElse_IsThe400(double stars)
    {
        var refused = Assert.Throws<PluginException>(() => StarValue.FromStars(stars));
        Assert.Equal((PluginErrorKind.Invalid, StarValue.InvalidMessage), (refused.Kind, refused.Message));
        Assert.False(StarValue.TryFromStars(stars, out _));
        if (double.IsFinite(stars) && Math.Abs(stars) < 1e6)
        {
            Assert.Throws<PluginException>(() => StarValue.FromStars((decimal)stars));
        }
    }

    [Fact]
    public void ADecimalJustOffAHalfStep_IsRefused()
    {
        Assert.Throws<PluginException>(() => StarValue.FromStars(3.4999999999999999999999m));
        Assert.Throws<PluginException>(() => StarValue.FromStars(0.0m));
        Assert.Equal(7, StarValue.FromStars(3.50m).Stars2);
    }

    [Fact]
    public void StoredUnitsOutsideOneToTen_AreAFault()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StarValue.FromStars2(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StarValue.FromStars2(11));
    }

    [Theory]
    [InlineData(7.3, 7)]
    [InlineData(10.0, 10)]
    [InlineData(1.0, 1)]
    [InlineData(0.5, 1)]
    [InlineData(6.5, 7)]
    [InlineData(9.6, 10)]
    public void FromUserDataRating_RoundsToHalfStars(double rating, int stars2)
        => Assert.Equal(stars2, StarValue.FromUserDataRating(rating)!.Value.Stars2);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(null)]
    public void FromUserDataRating_ZeroOrNothing_IsNoRating(double? rating)
        => Assert.Null(StarValue.FromUserDataRating(rating));

    [Fact]
    public void ARatingAboveTen_IsClampedToFiveStars()
        => Assert.Equal(10, StarValue.FromUserDataRating(12.4)!.Value.Stars2);

    [Fact]
    public void ToString_IsTheStarsWithOneDecimal()
    {
        Assert.Equal("3.5", StarValue.FromStars2(7).ToString());
        Assert.Equal("5.0", StarValue.FromStars2(10).ToString());
    }
}
