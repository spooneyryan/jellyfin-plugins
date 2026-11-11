using System;
using System.Collections;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Settings;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// Move's settings: their defaults, how the planner reads them, the build rule behind <c>CanMove</c>, the error
/// codes, and the Settings tab's saves keeping every Move field.
/// </summary>
public sealed class MoveSettingsTests
{
    [Fact]
    public void Defaults_AreThePlans()
    {
        var c = new PluginConfiguration();
        Assert.True(c.MoveEnabled);
        Assert.True(c.ShowInItemMenu);
        Assert.False(c.ShowDetailPageButton);
        Assert.False(c.ShowInTvLayout);
        Assert.Equal(new[] { "Movie", "Episode", "MusicVideo" }, c.AllowedItemKinds);
        Assert.True(c.AllowCrossLibraryMoves);
        Assert.True(c.AllowCrossVolumeMoves);
        Assert.Equal(2048, c.FreeSpaceMarginMb);
        Assert.Equal("SizeAndSha256", c.VerifyCopies);
        Assert.Equal("AllEditableFields", c.MetadataCarryOver);
        Assert.True(c.RestoreDateAdded);
        Assert.True(c.PreStageInternalData);
        Assert.True(c.RewriteCollectionsAndPlaylists);
        Assert.True(c.CallRelinkHooks);
        Assert.Equal("MatchDestination", c.LooseFileFolderMode);
        Assert.True(c.RemoveEmptySourceFolders);
        Assert.Equal(120, c.WaitForScanMinutes);
        Assert.Equal(24, c.AwaitLibraryHours);
        Assert.Equal(5, c.SourceDeleteRetries);
        Assert.Equal(20, c.MaxQueuedJobs);
        Assert.Equal(90, c.JobHistoryDays);

        var s = MoveSettings.From(c);
        Assert.True(s.AllowedKinds.SetEquals([BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo]));
        Assert.Equal(LooseFileFolderMode.MatchDestination, s.LooseFileFolderMode);
    }

    [Fact]
    public void From_ReadsNamesIgnoringCase_AndTakesTheDefaultForWhatItDoesntKnow()
    {
        var s = MoveSettings.From(new PluginConfiguration
        {
            AllowedItemKinds = [" movie ", "Video", "Series", "5", "MUSICVIDEO", null!],
            LooseFileFolderMode = "always",
            MetadataCarryOver = "lockedfieldsonly"
        });
        Assert.True(s.AllowedKinds.SetEquals([BaseItemKind.Movie, BaseItemKind.MusicVideo]));
        Assert.Equal(LooseFileFolderMode.Always, s.LooseFileFolderMode);
        Assert.Equal("LockedFieldsOnly", s.MetadataCarryOver);

        var unknown = MoveSettings.From(new PluginConfiguration { AllowedItemKinds = null!, LooseFileFolderMode = "1", MetadataCarryOver = "Everything" });
        Assert.Empty(unknown.AllowedKinds);
        Assert.Equal(LooseFileFolderMode.MatchDestination, unknown.LooseFileFolderMode);
        Assert.Equal("AllEditableFields", unknown.MetadataCarryOver);
        Assert.Equal(LooseFileFolderMode.Never, MoveSettings.From(new PluginConfiguration { LooseFileFolderMode = "Never" }).LooseFileFolderMode);
    }

    [Fact]
    public void Copy_KeepsEveryField()
    {
        // The Settings tab's save (SettingsRules.Apply) starts from Copy, so a field Copy forgets is reset by every save of
        // the tab. Every property gets a value that differs from its default here.
        var source = new PluginConfiguration();
        foreach (var property in typeof(PluginConfiguration).GetProperties().Where(p => p.CanWrite && p.DeclaringType == typeof(PluginConfiguration)))
        {
            property.SetValue(source, Different(property.PropertyType, property.GetValue(source)));
        }

        var copy = SettingsRules.Copy(source);
        foreach (var property in typeof(PluginConfiguration).GetProperties().Where(p => p.CanWrite && p.DeclaringType == typeof(PluginConfiguration)))
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(copy);
            if (expected is IEnumerable list && expected is not string)
            {
                Assert.True(list.Cast<object>().SequenceEqual(((IEnumerable)actual!).Cast<object>()), property.Name);
                Assert.NotSame(expected, actual);
            }
            else
            {
                Assert.True(Equals(expected, actual), property.Name);
            }
        }
    }

    [Fact]
    public void CanMove_NeedsTheJobSide_AndIsNeverThePlanOnlyBuilds()
    {
        Assert.True(MoveFeature.IsMoveBuild(hasJobs: true, previewOnly: false));
        Assert.False(MoveFeature.IsMoveBuild(hasJobs: true, previewOnly: true));
        Assert.False(MoveFeature.IsMoveBuild(hasJobs: false, previewOnly: false));
        Assert.False(MoveFeature.IsMoveBuild(hasJobs: false, previewOnly: true));

        // This round's builds plan only: no job side yet.
        var feature = new MoveFeature(new TestConfiguration(new PluginConfiguration()), () => true);
        Assert.True(feature.CanPlan);
        Assert.False(feature.CanMove);
    }

    [Fact]
    public void ErrorCodes_AreThePlansAndDistinct()
    {
        Assert.Equal(MoveErrorCodes.All.Count, MoveErrorCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(MoveErrorCodes.All, code => Assert.StartsWith("move.", code, StringComparison.Ordinal));
        foreach (var code in new[] { "move.not-eligible", "move.destination-invalid", "move.collision", "move.into-movie-folder", "move.episode-needs-series", "move.foreign-media", "move.reparse-point", "move.playing", "move.in-use", "move.cross-volume-later", "move.not-writable", "move.item-busy", "move.unsupported-os", "move.scan-running", "move.new-series", "move.episode-other-series", "move.episode-other-season", "move.sidecar-left-behind" })
        {
            Assert.Contains(code, MoveErrorCodes.All);
        }
    }

    private static object? Different(Type type, object? value) => value switch
    {
        bool b => !b,
        int i => i + 7,
        string s => s + "-x",
        Guid[] => new[] { Guid.NewGuid() },
        string[] a => a.Append("other").ToArray(),
        _ => throw new InvalidOperationException("Give the test a different value for " + type.Name)
    };
}
