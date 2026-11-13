using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Index;
using Jellyfin.Plugin.TagCloud.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Index;

/// <summary>
/// The index: what it holds, how words are keyed and shown, a full build and a patch.
/// </summary>
public sealed class IndexTests
{
    [Fact]
    public void Words_AreKeyedIgnoringCase_AndShowTheCasingMostTitlesUse()
    {
        var state = new IndexState([TagCloudSource.Tags]);
        var a = state.Upsert(new TitleRecord(Guid.NewGuid(), "A", "a", 2000, BaseItemKind.Movie));
        var b = state.Upsert(new TitleRecord(Guid.NewGuid(), "B", "b", 2001, BaseItemKind.Movie));
        var c = state.Upsert(new TitleRecord(Guid.NewGuid(), "C", "c", 2002, BaseItemKind.Movie));
        state.SetWords(a, TagCloudSource.Tags, ["Space", " space ", "", null]);
        state.SetWords(b, TagCloudSource.Tags, ["space"]);
        state.SetWords(c, TagCloudSource.Tags, ["space", "Horror"]);

        var words = state.ToSnapshot(1, null).Words[TagCloudSource.Tags];

        Assert.Equal(["horror", "space"], words.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("space", words["space"].Text);
        Assert.Equal([a, b, c], words["space"].Ordinals);
        Assert.Equal("Horror", words["horror"].Text);
    }

    [Fact]
    public void Replacing_AndRemoving_ATitle_TakeItsWordsAway_AndKeepItsOrdinalAGap()
    {
        var state = new IndexState([TagCloudSource.Tags, TagCloudSource.Genres]);
        var id = Guid.NewGuid();
        var a = state.Upsert(new TitleRecord(id, "A", "a", null, BaseItemKind.Movie));
        var b = state.Upsert(new TitleRecord(Guid.NewGuid(), "B", "b", null, BaseItemKind.Series));
        state.SetWords(a, TagCloudSource.Tags, ["heist", "noir"]);
        state.SetWords(b, TagCloudSource.Tags, ["noir"]);
        state.SetWords(a, TagCloudSource.Genres, ["Crime"]);
        state.SetWords(a, TagCloudSource.Tags, ["noir"]);

        var replaced = state.ToSnapshot(1, null);
        Assert.False(replaced.Words[TagCloudSource.Tags].ContainsKey("heist"));
        Assert.Equal([a, b], replaced.Words[TagCloudSource.Tags]["noir"].Ordinals);

        Assert.True(state.Remove(id));
        Assert.False(state.Remove(id));
        var removed = state.ToSnapshot(2, null);
        Assert.Equal([b], removed.Words[TagCloudSource.Tags]["noir"].Ordinals);
        Assert.False(removed.Words[TagCloudSource.Genres].ContainsKey("crime"));
        Assert.Null(removed.Titles[a]);
        Assert.Equal(1, removed.TitleCount);
    }

    [Fact]
    public void WordsOfASourceNotIndexed_AreIgnored()
    {
        var state = new IndexState([TagCloudSource.Genres]);
        state.SetWords(state.Upsert(new TitleRecord(Guid.NewGuid(), "A", "a", null, BaseItemKind.Movie)), TagCloudSource.Tags, ["heist"]);

        Assert.False(state.ToSnapshot(1, null).Words.ContainsKey(TagCloudSource.Tags));
    }

    [Fact]
    public async Task ABuild_HoldsAMergedVersion_AsAPatchDoes()
    {
        // Jellyfin 12 hides a merged (linked) alternate version from queries unless asked: a build still reads it, so a
        // rebuild and a patch count the same titles, as on 10.11.
        var fixture = new TagCloudFixture();
        var cut = fixture.Movie("Alien (Director's Cut)", 1979, ["space"], ["Horror"], []);
        cut.SetPrimaryVersion(fixture.Alien.Id);

        await fixture.Index.BuildAsync(CancellationToken.None);

        Assert.Contains("Alien (Director's Cut)", fixture.Index.Current!.Titles.OfType<TitleRecord>().Select(t => t.Name));
        Assert.Same(cut, fixture.Titles.Read(cut.Id));
    }

    [Fact]
    public async Task ABuild_HoldsMoviesAndSeries_ButNotEpisodesExtrasOrVirtualItems_WithTheirWordsAndCredits()
    {
        var fixture = new TagCloudFixture();

        await fixture.Index.BuildAsync(CancellationToken.None);
        var snapshot = fixture.Index.Current!;

        Assert.Equal(["Alien", "Aliens", "Heat", "Scarface", "The Wire"], snapshot.Titles.OfType<TitleRecord>().Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["baltimore", "creature", "gangster", "heist", "horror", "los angeles, california", "police", "sequel", "space"], snapshot.Words[TagCloudSource.Tags].Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, snapshot.Words[TagCloudSource.Actors]["sigourney weaver"].Ordinals.Count);
        Assert.Equal("James Cameron", snapshot.Words[TagCloudSource.Writers]["james cameron"].Text);
        Assert.False(snapshot.Words[TagCloudSource.Actors].ContainsKey("someone"));
        Assert.Equal(PeopleReader.PerItemPath, fixture.Index.Describe().PeoplePath);
        Assert.Equal("Ready", fixture.Index.Describe().State);
    }

    [Fact]
    public async Task APatch_ReadsAChangedTitleAgain_AndDropsARemovedOne()
    {
        var fixture = new TagCloudFixture();
        await fixture.Index.BuildAsync(CancellationToken.None);
        var before = fixture.Index.Current!;

        fixture.Heat.Tags = ["heist", "bank robbery"];
        fixture.People[fixture.Heat.Id].Add(new MediaBrowser.Controller.Entities.PersonInfo { Name = "Robert De Niro", Type = PersonKind.Actor });
        var count = await fixture.Index.PatchAsync([fixture.Heat.Id], [fixture.Scarface.Id], CancellationToken.None);
        var after = fixture.Index.Current!;

        Assert.Equal(2, count);
        Assert.True(after.Generation > before.Generation);
        Assert.True(after.Words[TagCloudSource.Tags].ContainsKey("bank robbery"));
        Assert.False(after.Words[TagCloudSource.Tags].ContainsKey("los angeles, california"));
        Assert.False(after.Words[TagCloudSource.Tags].ContainsKey("gangster"));
        Assert.True(after.Words[TagCloudSource.Actors].ContainsKey("robert de niro"));
        Assert.Single(after.Words[TagCloudSource.Actors]["al pacino"].Ordinals);
        Assert.Equal(2, fixture.Index.Describe().Patched);
    }

    [Fact]
    public async Task ASourceTurnedOff_IsntIndexed()
    {
        var fixture = new TagCloudFixture();
        fixture.ConfigurationProvider.Configuration.EnableActors = false;

        await fixture.Index.BuildAsync(CancellationToken.None);

        Assert.False(fixture.Index.Current!.Words.ContainsKey(TagCloudSource.Actors));
        Assert.True(fixture.Index.Current.Words.ContainsKey(TagCloudSource.Directors));
    }

    [Fact]
    public async Task TheConsumer_PatchesSmallBatches_AndAsksForARebuildPastTheThreshold()
    {
        var fixture = new TagCloudFixture();
        await fixture.Index.BuildAsync(CancellationToken.None);
        fixture.ConfigurationProvider.Configuration.FullRebuildAfterChanges = 1;
        var consumer = new TagCloudLibraryConsumer(fixture.Index, fixture.ConfigurationProvider);
        Assert.Equal(new[] { BaseItemKind.Movie, BaseItemKind.Series }.Order(), consumer.Filter.Kinds!.Order());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var asked = fixture.Index.WaitForRebuildRequestAsync(timeout.Token);
        Assert.False(asked.IsCompleted);
        fixture.Index.RequestRebuild();
        await asked;
    }
}
