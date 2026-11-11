using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Clouds;
using Jellyfin.Plugin.TagCloud.Services;
using Jellyfin.Plugin.TagCloud.Tests.TestSupport;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TagCloud.Tests.Services;

/// <summary>
/// Saved clouds: owners, sharing, names, conflicts, deletes, a deleted user's clouds.
/// </summary>
public sealed class CloudServiceTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly SavedCloudOwner _owner;
    private readonly FakeConfiguration _configuration = new();
    private readonly User _alice = TagCloudFixture.NewUser("alice");
    private readonly User _bob = TagCloudFixture.NewUser("bob");
    private readonly TagCloudCloudService _clouds;

    public CloudServiceTests()
    {
        _owner = new SavedCloudOwner(Path.Combine(_folder.Path, SavedCloudOwner.FileName), Path.Combine(_folder.Path, "snapshots"), NullLogger.Instance);
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_alice.Id)).Returns(_alice);
        users.Setup(u => u.GetUserById(_bob.Id)).Returns(_bob);
        _clouds = new TagCloudCloudService(_owner, users.Object, _configuration);
    }

    [Fact]
    public async Task ACloud_KeepsWhatItCounts_AndANormalLook()
    {
        var saved = await _clouds.CreateAsync(_alice, Request("  Midnight   slashers ", look: new TagCloudLookDto { MaxWords = 260, Palette = "rainbow", SizeMixing = 7, Font = "Comic" }), CancellationToken.None);

        Assert.Equal("Midnight slashers", saved.Name);
        Assert.True(saved.IsMine);
        Assert.Equal("alice", saved.OwnerName);
        Assert.Equal(["slasher"], saved.NarrowKeys);
        Assert.Equal(250, saved.Look.MaxWords);
        Assert.Equal("Rainbow", saved.Look.Palette);
        Assert.Equal(1, saved.Look.SizeMixing);
        Assert.Equal("Theme", saved.Look.Font);
        Assert.Single(_clouds.List(_alice));
    }

    [Fact]
    public async Task OthersSeeOnlySharedClouds_AndOnlyWhileSharingIsAllowed()
    {
        await _clouds.CreateAsync(_alice, Request("Private"), CancellationToken.None);
        var shared = await _clouds.CreateAsync(_alice, Request("Shared", shared: true), CancellationToken.None);

        var bobs = _clouds.List(_bob);
        Assert.Equal(["Shared"], bobs.Select(c => c.Name));
        Assert.False(bobs[0].IsMine);
        Assert.Equal("alice", bobs[0].OwnerName);

        _configuration.Configuration.AllowSharedClouds = false;
        Assert.Empty(_clouds.List(_bob));
        Assert.Equal(2, _clouds.List(_alice).Count);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => _clouds.Get(_bob, shared.Id)).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.CreateAsync(_bob, Request("Mine", shared: true), CancellationToken.None))).Kind);
    }

    [Fact]
    public async Task Names_AreRequired_Short_AndUniqueAmongTheOwnersOwn()
    {
        await _clouds.CreateAsync(_alice, Request("Slashers"), CancellationToken.None);

        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => _clouds.CreateAsync(_alice, Request("SLASHERS"), CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => _clouds.CreateAsync(_alice, Request(" \t "), CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => _clouds.CreateAsync(_alice, Request(new string('x', 81)), CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => _clouds.CreateAsync(_alice, Request("Many", narrow: Enumerable.Range(0, 11).Select(i => "k" + i).ToArray()), CancellationToken.None))).Kind);

        // Bob may use the same name.
        Assert.Equal("Slashers", (await _clouds.CreateAsync(_bob, Request("Slashers"), CancellationToken.None)).Name);
    }

    [Fact]
    public async Task OnlyTheOwnerUpdates_AndAStaleUpdateIsAConflict()
    {
        var saved = await _clouds.CreateAsync(_alice, Request("Shared", shared: true), CancellationToken.None);

        Assert.Equal(PluginErrorKind.Forbidden, (await Assert.ThrowsAsync<PluginException>(() => _clouds.UpdateAsync(_bob, saved.Id, Request("Taken over", shared: true), CancellationToken.None))).Kind);

        var updated = await _clouds.UpdateAsync(_alice, saved.Id, Request("Renamed", shared: true, ifUpdated: saved.UpdatedUtc), CancellationToken.None);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(saved.CreatedUtc, updated.CreatedUtc);

        var stale = Request("Again", shared: true, ifUpdated: saved.UpdatedUtc.AddSeconds(-1));
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => _clouds.UpdateAsync(_alice, saved.Id, stale, CancellationToken.None))).Kind);
    }

    [Fact]
    public async Task ADuplicate_IsAPrivateCopyOfTheCallers_NamedAsACopy()
    {
        var shared = await _clouds.CreateAsync(_alice, Request("Slashers", shared: true), CancellationToken.None);

        var first = await _clouds.DuplicateAsync(_bob, shared.Id, CancellationToken.None);
        var second = await _clouds.DuplicateAsync(_bob, shared.Id, CancellationToken.None);

        Assert.Equal("Slashers (copy)", first.Name);
        Assert.Equal("Slashers (copy 2)", second.Name);
        Assert.True(first.IsMine);
        Assert.False(first.Shared);
        Assert.Equal(shared.NarrowKeys, first.NarrowKeys);
    }

    [Fact]
    public async Task Deletes_AreTheOwners_OrAnAdminsOfASharedCloud()
    {
        var hidden = await _clouds.CreateAsync(_alice, Request("Private"), CancellationToken.None);
        var shared = await _clouds.CreateAsync(_alice, Request("Shared", shared: true), CancellationToken.None);

        Assert.Equal(PluginErrorKind.Forbidden, (await Assert.ThrowsAsync<PluginException>(() => _clouds.DeleteAsync(_bob, shared.Id, isAdmin: false, CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => _clouds.DeleteAsync(_bob, hidden.Id, isAdmin: true, CancellationToken.None))).Kind);

        await _clouds.DeleteAsync(_bob, shared.Id, isAdmin: true, CancellationToken.None);
        await _clouds.DeleteAsync(_alice, hidden.Id, isAdmin: false, CancellationToken.None);
        Assert.Empty(_clouds.List(_alice));
    }

    [Fact]
    public async Task ADeletedUsersPrivateCloudsGo_AndTheirSharedOnesStayAsFormerUsers()
    {
        await _clouds.CreateAsync(_alice, Request("Private"), CancellationToken.None);
        var shared = await _clouds.CreateAsync(_alice, Request("Shared", shared: true), CancellationToken.None);

        Assert.Equal(2, await _owner.HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.KeepAsOrphan, CancellationToken.None));

        var bobs = _clouds.List(_bob);
        Assert.Equal([shared.Id], bobs.Select(c => c.Id));
        Assert.Equal(TagCloudCloudService.FormerUser, bobs[0].OwnerName);
        Assert.False(bobs[0].IsMine);
        Assert.Equal(0, await _owner.HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.KeepAsOrphan, CancellationToken.None));

        Assert.Equal(1, await _owner.HandleUserDeletedAsync(_alice.Id, DeletedUserPolicy.Delete, CancellationToken.None));
        Assert.Empty(_clouds.List(_bob));
    }

    [Fact]
    public async Task WithSharingOff_ASharedCloudCanBeSavedAsItIs_AndStopBeingShared_ButNoneStartsSharing()
    {
        var shared = await _clouds.CreateAsync(_alice, Request("Shared", shared: true), CancellationToken.None);
        var priv = await _clouds.CreateAsync(_alice, Request("Private"), CancellationToken.None);
        _configuration.Configuration.AllowSharedClouds = false;

        // A rename keeps Shared as it was: allowed.
        var renamed = await _clouds.UpdateAsync(_alice, shared.Id, Request("Shared renamed", shared: true, ifUpdated: shared.UpdatedUtc), CancellationToken.None);
        Assert.True(renamed.Shared);
        Assert.Equal("Shared renamed", renamed.Name);

        // Stop sharing: allowed.
        var stopped = await _clouds.UpdateAsync(_alice, shared.Id, Request("Shared renamed", shared: false, ifUpdated: renamed.UpdatedUtc), CancellationToken.None);
        Assert.False(stopped.Shared);

        // Start sharing, an old private cloud or the one just unshared: refused.
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.UpdateAsync(_alice, priv.Id, Request("Private", shared: true), CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.UpdateAsync(_alice, shared.Id, Request("Shared renamed", shared: true), CancellationToken.None))).Kind);
    }

    [Fact]
    public async Task WithThePageOff_EverySavedCloudCallIsFeatureDisabled()
    {
        var mine = await _clouds.CreateAsync(_alice, Request("Mine"), CancellationToken.None);
        _configuration.Configuration.EnableTagCloudPage = false;

        Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.Throws<PluginException>(() => _clouds.List(_alice)).Kind);
        Assert.Equal(TagCloudPageGate.DisabledMessage, Assert.Throws<PluginException>(() => _clouds.List(_alice)).Message);
        Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.Throws<PluginException>(() => _clouds.Get(_alice, mine.Id)).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.CreateAsync(_alice, Request("Other"), CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.UpdateAsync(_alice, mine.Id, Request("Mine 2"), CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.DuplicateAsync(_alice, mine.Id, CancellationToken.None))).Kind);
        Assert.Equal(PluginErrorKind.FeatureDisabled, (await Assert.ThrowsAsync<PluginException>(() => _clouds.DeleteAsync(_alice, mine.Id, isAdmin: true, CancellationToken.None))).Kind);

        _configuration.Configuration.EnableTagCloudPage = true;
        Assert.Single(_clouds.List(_alice));
    }

    [Fact]
    public async Task ARemap_MovesTheClouds()
    {
        await _clouds.CreateAsync(_alice, Request("Mine"), CancellationToken.None);

        Assert.Equal(1, await _owner.RemapUsersAsync(new System.Collections.Generic.Dictionary<Guid, Guid> { [_alice.Id] = _bob.Id }, CancellationToken.None));

        Assert.Equal(["Mine"], _clouds.List(_bob).Select(c => c.Name));
        Assert.Equal(1, _owner.CountRecordsByUser()[_bob.Id]);
    }

    public void Dispose()
    {
        _owner.Dispose();
        _folder.Dispose();
    }

    private static TagCloudSaveRequest Request(string name, bool shared = false, string[]? narrow = null, TagCloudLookDto? look = null, DateTime? ifUpdated = null) => new()
    {
        Name = name,
        Shared = shared,
        Source = TagCloudSource.Tags,
        Scope = TagCloudScope.Movies,
        LibraryIds = [Guid.NewGuid(), Guid.Empty],
        NarrowKeys = narrow ?? ["Slasher", "slasher "],
        Look = look ?? new TagCloudLookDto(),
        IfUpdatedUtc = ifUpdated
    };
}
