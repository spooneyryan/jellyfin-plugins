using System;
using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.UserRatings.Configuration;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests;

/// <summary>
/// The plugin's and the kit's settings with their defaults (the Settings tab renders them), and their round trip
/// through the XML file Jellyfin writes with <see cref="XmlSerializer"/>.
/// </summary>
public sealed class ConfigurationTests
{
    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var c = new PluginConfiguration();

        // The kit's common fields.
        Assert.True(c.EnableWebUi);
        Assert.True(c.ReportWebAnchorProblems);
        Assert.True(c.RegisterPluginPages);
        Assert.Equal("FallbackOnly", c.PageHostMode);
        Assert.True(c.ShowFallbackNav);
        Assert.Equal(7, c.SnapshotsToKeep);
        Assert.Equal(90, c.DetachedRetentionDays);
        Assert.Equal("Delete", c.DeletedUserPolicy);
        Assert.True(c.ShowJellyfinEnhancedBanner);

        // Reviewers, who and what.
        Assert.False(c.HideReviewsFromHiddenUsers);
        Assert.True(c.HideReviewsFromDisabledUsers);
        Assert.True(c.ShowReviewerAvatars);
        Assert.True(c.EnableRatings);
        Assert.True(c.EnableReviews);
        Assert.Equal("AllUsers", c.WhoCanRate);
        Assert.Empty(c.RaterUserIds);
        Assert.Equal(["Movie", "Series", "Season", "Episode", "Video", "MusicVideo", "MusicAlbum", "Book", "AudioBook"], c.RateableItemTypes);
        Assert.False(c.RequirePlayedToRate);
        Assert.Equal(5000, c.MaxReviewLength);
        Assert.True(c.AllowSpoilerFlag);

        // Details page, posters, pages, integration, moderation.
        Assert.True(c.ShowAverageChip);
        Assert.True(c.ShowHistogram);
        Assert.Equal(1, c.MinRatingsForAverage);
        Assert.True(c.SectionExpandedByDefault);
        Assert.Equal("Auto", c.ShowDetailButton);
        Assert.Equal(20, c.ReviewsPageSize);
        Assert.False(c.ShowOnPosters);
        Assert.Equal("Average", c.PosterBadgeContent);
        Assert.Equal("top-start", c.PosterBadgePosition);
        Assert.True(c.EnableTopRatedPage);
        Assert.False(c.MirrorToUserData);
        Assert.Equal(365, c.ModerationLogDays);
    }

    [Fact]
    public void EverySetting_RoundTripsThroughJellyfinsXmlFile()
    {
        var rater = Guid.Parse("0f8b7c3e-1a2b-4c5d-9e8f-001122334455");
        var original = new PluginConfiguration
        {
            EnableWebUi = false,
            WhoCanRate = "SelectedUsers",
            RaterUserIds = [rater],
            RateableItemTypes = ["Movie", "Episode"],
            MaxReviewLength = 2000,
            ShowDetailButton = "Always",
            PosterBadgePosition = "bottom-end",
            DeletedUserPolicy = "Anonymize"
        };

        var copy = RoundTrip(original);

        Assert.False(copy.EnableWebUi);
        Assert.Equal("SelectedUsers", copy.WhoCanRate);
        Assert.Equal([rater], copy.RaterUserIds);
        Assert.Equal(["Movie", "Episode"], copy.RateableItemTypes);
        Assert.Equal(2000, copy.MaxReviewLength);
        Assert.Equal("Always", copy.ShowDetailButton);
        Assert.Equal("bottom-end", copy.PosterBadgePosition);
        Assert.Equal("Anonymize", copy.DeletedUserPolicy);
    }

    [Fact]
    public void AValueANewerBuildWrote_StillLoads()
    {
        // Names are strings (PluginConfiguration's remarks), so a WhoCanRate a later version adds never stops the file loading.
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <WhoCanRate>Moderators</WhoCanRate>
              <ShowDetailButton>Sometimes</ShowDetailButton>
              <SomeFutureSetting>true</SomeFutureSetting>
            </PluginConfiguration>
            """;

        var loaded = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(Xml))!;

        Assert.Equal("Moderators", loaded.WhoCanRate);
        Assert.Equal("Sometimes", loaded.ShowDetailButton);
        Assert.Equal(9, loaded.RateableItemTypes.Length);
    }

    [Fact]
    public void ModerationSnapshotDays_AValueEarlierBuildsWrote_LoadsIgnored_AndTheNextSaveDropsIt()
    {
        // The setting went in 0.3.16; any value of it, even one that isn't a number, loads.
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <MaxReviewLength>1200</MaxReviewLength>
              <ModerationSnapshotDays>thirty</ModerationSnapshotDays>
              <ModerationLogDays>90</ModerationLogDays>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var loaded = (PluginConfiguration)serializer.Deserialize(new StringReader(Xml))!;

        Assert.Equal((1200, 90), (loaded.MaxReviewLength, loaded.ModerationLogDays));
        Assert.Null(typeof(PluginConfiguration).GetProperty("ModerationSnapshotDays"));
        using var writer = new StringWriter();
        serializer.Serialize(writer, loaded);
        Assert.DoesNotContain("ModerationSnapshotDays", writer.ToString(), StringComparison.Ordinal);
    }

    private static PluginConfiguration RoundTrip(PluginConfiguration configuration)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, configuration);
        return (PluginConfiguration)serializer.Deserialize(new StringReader(writer.ToString()))!;
    }
}
