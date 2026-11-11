using System;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Xunit;
using ItemVisibility = Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups.ItemVisibility;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// <see cref="ItemVisibility"/>'s fast path: only a policy that limits items is checked item by item.
/// </summary>
public sealed class ItemVisibilityTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("rating", true)]
    [InlineData("unrated", true)]
    [InlineData("blocked", true)]
    [InlineData("allowed", true)]
    [InlineData("blank tags", false)]
    [InlineData("folders only", false)]
    public void OnlyAnItemLimit_TakesTheSlowPath(string? limit, bool expected)
    {
        var user = new User("u", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Guid.NewGuid() };
        switch (limit)
        {
            case "rating":
                user.MaxParentalRatingScore = 13;
                break;
            case "unrated":
                user.SetPreference(PreferenceKind.BlockUnratedItems, ["Movie"]);
                break;
            case "blocked":
                user.SetPreference(PreferenceKind.BlockedTags, ["gore"]);
                break;
            case "allowed":
                user.SetPreference(PreferenceKind.AllowedTags, ["family"]);
                break;
            case "blank tags":
                user.SetPreference(PreferenceKind.BlockedTags, [" "]);
                user.SetPreference(PreferenceKind.AllowedTags, [string.Empty]);
                break;
            case "folders only":
                // Library limits are LibraryAccess's, never an item check.
                user.SetPreference(PreferenceKind.BlockedMediaFolders, [Guid.NewGuid().ToString("N")]);
                break;
        }

        Assert.Equal(expected, ItemVisibility.HasItemRestrictions(user));
    }

    [Fact]
    public void AnApiKey_OrAnUnlimitedUser_HasNothingHidden_AndNoQueryIsMade()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var visibility = new ItemVisibility(new LibraryAccess(library.Fake.Object, _ => library.Libraries, library.Fake.IsVisible));
        var snapshot = Snapshot(library);
        var user = new User("u", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

        Assert.Null(visibility.Hidden(snapshot, null, [library.Libraries[0].Id]));
        Assert.Null(visibility.Hidden(snapshot, user, [library.Libraries[0].Id]));
        Assert.Equal(0, visibility.Checks);
        Assert.DoesNotContain(library.Fake.Queries, q => q.User is not null);
    }

    private static MovieIndexSnapshot Snapshot(DuplicatesLibrary library)
    {
        var content = library.Builder().Build(CancellationToken.None);
        var byId = content.Entries.SelectMany(e => e.AllIds.Select(id => (id, e))).DistinctBy(p => p.id).ToDictionary(p => p.id, p => p.e);
        return new MovieIndexSnapshot(1, DateTime.UtcNow, TimeSpan.Zero, false, content.Entries, byId, content.Libraries, content.Stats, new BuildMetrics(0, 0, 0, 0, 0));
    }
}
