using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests;

/// <summary>
/// <see cref="RatingsSettings"/>, the one reader of the name-valued settings: the documented defaults
/// read as themselves, names in any case, and a value this version doesn't know (or a blank one) gets a safe fallback,
/// listed and logged once. Names are strings because 10.11.5's <c>LoadConfiguration</c> replaces the settings file with
/// defaults on any failure to deserialize it.
/// </summary>
public sealed class RatingsSettingsTests
{
    [Fact]
    public void TheDefaults_ReadAsTheDocumentedOnes_WithNoProblem()
    {
        var settings = RatingsSettings.From(new PluginConfiguration());

        Assert.Empty(settings.Problems);
        Assert.Equal(WhoCanRate.AllUsers, settings.WhoCanRate);
        Assert.Empty(settings.RaterUserIds);
        Assert.Equal(RatingsSettings.DefaultRateableItemTypes.Order(), settings.RateableItemTypes.Order());
        Assert.DoesNotContain(BaseItemKind.Audio, settings.RateableItemTypes);
        Assert.DoesNotContain(BaseItemKind.BoxSet, settings.RateableItemTypes);
        Assert.Equal(DetailButtonMode.Auto, settings.ShowDetailButton);
        Assert.Equal(PosterBadgeContent.Average, settings.PosterBadgeContent);
        Assert.Equal(PosterBadgePosition.TopStart, settings.PosterBadgePosition);
        Assert.Equal(DeletedUserPolicy.Delete, settings.DeletedUserPolicy);
        Assert.Equal((5000, 1, 20), (settings.MaxReviewLength, settings.MinRatingsForAverage, settings.ReviewsPageSize));
        Assert.True(settings.EnableRatings && settings.EnableReviews && settings.AllowSpoilerFlag && settings.HideReviewsFromDisabledUsers);
        Assert.False(settings.HideReviewsFromHiddenUsers || settings.RequirePlayedToRate);
        Assert.True(settings.MayRate(Guid.NewGuid()));
    }

    [Fact]
    public void Names_AreReadInAnyCase_AndWithSurroundingSpaces()
    {
        var rater = Guid.NewGuid();
        var settings = RatingsSettings.From(new PluginConfiguration
        {
            WhoCanRate = " selectedusers ",
            RaterUserIds = [rater, Guid.Empty],
            RateableItemTypes = ["movie", " Episode", "MUSICALBUM"],
            ShowDetailButton = "always",
            PosterBadgeContent = "BOTH",
            PosterBadgePosition = "Bottom-End",
            DeletedUserPolicy = "anonymize"
        });

        Assert.Empty(settings.Problems);
        Assert.Equal(WhoCanRate.SelectedUsers, settings.WhoCanRate);
        Assert.Equal([rater], settings.RaterUserIds);
        Assert.True(settings.MayRate(rater));
        Assert.False(settings.MayRate(Guid.NewGuid()));
        Assert.Equal(new[] { BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.MusicAlbum }.Order(), settings.RateableItemTypes.Order());
        Assert.Equal(DetailButtonMode.Always, settings.ShowDetailButton);
        Assert.Equal(PosterBadgeContent.Both, settings.PosterBadgeContent);
        Assert.Equal(PosterBadgePosition.BottomEnd, settings.PosterBadgePosition);
        Assert.Equal(DeletedUserPolicy.Anonymize, settings.DeletedUserPolicy);
    }

    [Theory]
    [InlineData("Purge")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0")]
    public void AnUnknownOrBlankDeletedUserPolicy_IsKeepAsOrphan_NeverDelete(string? value)
    {
        // It used to fall back to Delete, which can't be undone.
        var settings = RatingsSettings.From(new PluginConfiguration { DeletedUserPolicy = value! });

        Assert.Equal(DeletedUserPolicy.KeepAsOrphan, settings.DeletedUserPolicy);
        Assert.Contains(settings.Problems, p => p.StartsWith("DeletedUserPolicy", StringComparison.Ordinal) && p.Contains("deletes nothing", StringComparison.Ordinal));
        Assert.Equal(DeletedUserPolicy.KeepAsOrphan, UserRatingsKit.Options.DeletedUserPolicy(Services(new PluginConfiguration { DeletedUserPolicy = value! })));
    }

    [Fact]
    public void TheKitsDeletedUserPolicy_IsTheConfiguredOne()
    {
        Assert.Equal(DeletedUserPolicy.Delete, UserRatingsKit.Options.DeletedUserPolicy(Services(new PluginConfiguration())));
        Assert.Equal(DeletedUserPolicy.Anonymize, UserRatingsKit.Options.DeletedUserPolicy(Services(new PluginConfiguration { DeletedUserPolicy = "Anonymize" })));
    }

    [Fact]
    public void UnknownNames_GetTheirSafeFallbacks_AndAreListed()
    {
        var settings = RatingsSettings.From(new PluginConfiguration
        {
            WhoCanRate = "Moderators",
            ShowDetailButton = "Sometimes",
            PosterBadgeContent = "Stars",
            PosterBadgePosition = "middle",
            RateableItemTypes = ["Movie", "Hologram", " ", "7"]
        });

        // Nobody gains the right to rate from a value this version can't read; the display settings keep their defaults.
        Assert.Equal(WhoCanRate.SelectedUsers, settings.WhoCanRate);
        Assert.False(settings.MayRate(Guid.NewGuid()));
        Assert.Equal(DetailButtonMode.Auto, settings.ShowDetailButton);
        Assert.Equal(PosterBadgeContent.Average, settings.PosterBadgeContent);
        Assert.Equal(PosterBadgePosition.TopStart, settings.PosterBadgePosition);
        Assert.Equal([BaseItemKind.Movie], settings.RateableItemTypes);
        Assert.Equal(7, settings.Problems.Count);
        Assert.Contains(settings.Problems, p => p.Contains("WhoCanRate 'Moderators'", StringComparison.Ordinal) && p.Contains("SelectedUsers is used", StringComparison.Ordinal));
        Assert.Contains(settings.Problems, p => p.Contains("'Hologram'", StringComparison.Ordinal));
    }

    [Fact]
    public void NoKnownRateableKind_IsTheDefaultList()
    {
        var settings = RatingsSettings.From(new PluginConfiguration { RateableItemTypes = ["Hologram"] });

        Assert.Equal(RatingsSettings.DefaultRateableItemTypes.Order(), settings.RateableItemTypes.Order());
        Assert.Contains(settings.Problems, p => p.Contains("default list", StringComparison.Ordinal));
        Assert.Equal(RatingsSettings.DefaultRateableItemTypes.Order(), RatingsSettings.From(new PluginConfiguration { RateableItemTypes = [] }).RateableItemTypes.Order());
    }

    [Theory]
    [InlineData(100, 1, 1, 2000, 1, 5)]
    [InlineData(99999, 50, 500, 20000, 10, 50)]
    [InlineData(2000, 10, 5, 2000, 10, 5)]
    public void Numbers_AreHeldToTheirRanges(int maxLength, int minRatings, int pageSize, int expectedMax, int expectedMin, int expectedPage)
    {
        var settings = RatingsSettings.From(new PluginConfiguration { MaxReviewLength = maxLength, MinRatingsForAverage = minRatings, ReviewsPageSize = pageSize });

        Assert.Equal((expectedMax, expectedMin, expectedPage), (settings.MaxReviewLength, settings.MinRatingsForAverage, settings.ReviewsPageSize));
    }

    [Fact]
    public void EveryNumberAndNameOfTheTab_ReadFromTheConfiguration_IsHeldToTheTabsRange_AndNamedInProblems()
    {
        // A bypass: POST /Plugins/{id}/Configuration saves any value. SnapshotsToKeep
        // was taken above 365, and it and DetachedRetentionDays were held with no problem named; PageHostMode wasn't read.
        var numbers = RatingsSettingsFields.Fields.Where(f => f.Max > 0).ToList();
        Assert.Equal(
            ["MaxReviewLength", "MinRatingsForAverage", "ReviewsPageSize", "SnapshotsToKeep", "DetachedRetentionDays", "ModerationLogDays"],
            numbers.Select(f => f.Name));
        foreach (var field in numbers)
        {
            foreach (var (value, held) in new[] { (field.Max + 1, field.Max), (field.Min - 1, field.Min), (int.MaxValue, field.Max), (int.MinValue, field.Min) })
            {
                var configuration = new PluginConfiguration();
                typeof(PluginConfiguration).GetProperty(field.Name)!.SetValue(configuration, value);
                var settings = RatingsSettings.From(configuration);
                Assert.Equal(held, (int)typeof(RatingsSettings).GetProperty(field.Name)!.GetValue(settings)!);
                Assert.Equal(field.Name + " " + value + " is outside " + field.Min + " to " + field.Max + "; " + held + " is used", Assert.Single(settings.Problems));
            }
        }

        foreach (var field in RatingsSettingsFields.Fields.Where(f => f.Names is not null))
        {
            var configuration = new PluginConfiguration();
            typeof(PluginConfiguration).GetProperty(field.Name)!.SetValue(configuration, "Bogus");
            Assert.StartsWith(field.Name + " 'Bogus' isn't a value this version knows", Assert.Single(RatingsSettings.From(configuration).Problems), StringComparison.Ordinal);
        }

        Assert.Equal(365, RatingsSettings.From(new PluginConfiguration { SnapshotsToKeep = 100000 }).SnapshotsToKeep);
        Assert.Equal(0, RatingsSettings.From(new PluginConfiguration { DetachedRetentionDays = -9 }).DetachedRetentionDays);
        Assert.Empty(RatingsSettings.From(new PluginConfiguration { PageHostMode = " auto " }).Problems);
    }

    [Fact]
    public void TheMaintenancesSnapshotsToKeep_IsTheHeldValue()
    {
        var configuration = new PluginConfiguration { SnapshotsToKeep = 100000 };
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(() => configuration);
        using var services = new ServiceCollection().AddSingleton(provider.Object).AddSingleton<RatingsSettingsReader>().BuildServiceProvider();

        Assert.Equal(365, PluginServiceRegistrator.SnapshotsToKeep(services));
        configuration = new PluginConfiguration { SnapshotsToKeep = -3 };
        Assert.Equal(0, PluginServiceRegistrator.SnapshotsToKeep(services));
        configuration = new PluginConfiguration();
        Assert.Equal(7, PluginServiceRegistrator.SnapshotsToKeep(services));
    }

    [Fact]
    public void TheReader_ReadsTheCurrentConfigurationEveryTime_AndLogsEachProblemOnce()
    {
        var configuration = new PluginConfiguration { WhoCanRate = "Moderators" };
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(() => configuration);
        var logger = new ListLogger<RatingsSettingsReader>();
        var reader = new RatingsSettingsReader(provider.Object, logger);

        Assert.Equal(WhoCanRate.SelectedUsers, reader.Read().WhoCanRate);
        Assert.Equal(WhoCanRate.SelectedUsers, reader.Read().WhoCanRate);
        Assert.Single(logger.At(LogLevel.Warning));

        configuration = new PluginConfiguration { WhoCanRate = "AllUsers" };
        Assert.Equal(WhoCanRate.AllUsers, reader.Read().WhoCanRate);
        Assert.Single(logger.At(LogLevel.Warning));
    }

    private static ServiceProvider Services(PluginConfiguration configuration)
    {
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(configuration);
        return new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();
    }
}
