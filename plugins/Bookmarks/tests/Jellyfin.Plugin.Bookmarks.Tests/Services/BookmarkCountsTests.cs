using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// Diagnostics' <c>BookmarkCounts</c>: each user's bookmarks counted by kind, with
/// their clip files' sizes, a user the server doesn't know by the last seen name, the totals, the file's size and the
/// warning above 3 MB; never a bookmark's name, note, video or path.
/// </summary>
public sealed class BookmarkCountsTests : IDisposable
{
    private static readonly DateTime Created = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");

    public BookmarkCountsTests()
    {
        _alice.Id = Guid.NewGuid();
        _bob.Id = Guid.NewGuid();
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task EachUsersBookmarks_AreCountedByKind_WithTheirClipFiles_AndTheTotals()
    {
        var gone = Guid.NewGuid();
        await _store.AddAsync(Bookmark(_alice.Id) with { Name = "Bank job", Note = "Secret note", EndTicks = 900_000_000 }, Ct, _alice);
        await _store.AddAsync(Bookmark(_alice.Id) with { Name = "Tears in rain", Origin = BookmarkOrigin.JellyfinEnhanced, DetachedUtc = Created }, Ct, _alice);
        await _store.AddAsync(Bookmark(_alice.Id) with { RelinkedUtc = Created, Clip = Clip(BookmarkClipStatus.Completed, 3_000_000) }, Ct, _alice);
        await _store.AddAsync(Bookmark(_alice.Id) with { EndTicks = 900_000_000, Clip = Clip(BookmarkClipStatus.Running, null) }, Ct, _alice);
        await _store.AddAsync(Bookmark(_alice.Id) with { DeletedUtc = Created, Clip = Clip(BookmarkClipStatus.Completed, 1_000_000) }, Ct, _alice);
        await _store.AddAsync(Bookmark(_bob.Id), Ct, _bob);
        await _store.AddAsync(Bookmark(gone) with { Name = "Of someone gone" }, Ct);
        await _store.UpdateDocumentAsync(d => d with { KnownUsers = d.KnownUsers.With(gone, "carol") }, Ct);

        var users = new Mock<IUserManager>();
        users.SetupUsers([_alice, _bob]);
        var entry = BookmarkCounts.Describe(_store, users.Object);
        var json = JsonSerializer.SerializeToElement(entry);

        var rows = json.GetProperty("Users").EnumerateArray().ToList();
        Assert.Equal(["alice", "bob", "carol"], rows.Select(r => r.GetProperty("UserName").GetString()));
        var alice = rows[0];
        Assert.Equal(_alice.Id.ToString("N"), alice.GetProperty("UserId").GetString());
        Assert.True(alice.GetProperty("Known").GetBoolean());
        Assert.Equal(4, alice.GetProperty("Bookmarks").GetInt32());
        Assert.Equal(2, alice.GetProperty("Named").GetInt32());
        Assert.Equal(2, alice.GetProperty("Ranges").GetInt32());
        Assert.Equal(1, alice.GetProperty("Detached").GetInt32());
        Assert.Equal(1, alice.GetProperty("Relinked").GetInt32());
        Assert.Equal(1, alice.GetProperty("Imported").GetInt32());
        Assert.Equal(2, alice.GetProperty("Clips").GetInt32());
        Assert.Equal(4_000_000, alice.GetProperty("ClipBytes").GetInt64());
        Assert.Equal(1, alice.GetProperty("ClipsBeingMade").GetInt32());
        Assert.Equal(1, alice.GetProperty("Tombstones").GetInt32());
        Assert.False(rows[2].GetProperty("Known").GetBoolean());

        var total = json.GetProperty("Total");
        Assert.Equal(3, total.GetProperty("Users").GetInt32());
        Assert.Equal(6, total.GetProperty("Bookmarks").GetInt32());
        Assert.Equal(4_000_000, total.GetProperty("ClipBytes").GetInt64());
        Assert.False(total.TryGetProperty("UserName", out _));
        Assert.True(json.GetProperty("FileBytes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("Warning").ValueKind);

        // Counts and sizes only: no name, note, video or path of a bookmark.
        var text = json.GetRawText();
        foreach (var secret in new[] { "Bank job", "Secret note", "Tears in rain", "Heat", "clip.mkv", "Of someone gone" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABookmarkFileOver3MB_IsWarnedAbout_AndAnEmptyStoreCountsNothing()
    {
        var empty = JsonSerializer.SerializeToElement(BookmarkCounts.Describe(_store, null));
        Assert.Empty(empty.GetProperty("Users").EnumerateArray());
        Assert.Equal(0, empty.GetProperty("Total").GetProperty("Bookmarks").GetInt32());
        Assert.Equal(0, empty.GetProperty("FileBytes").GetInt64());

        // 7,000 bookmarks with full notes, written at once: well over 3 MB.
        var many = Enumerable.Range(0, 7000).Select(_ => Bookmark(_alice.Id) with { Note = new string('n', 500) }).ToList();
        await _store.UpdateDocumentAsync(d => BookmarkStore.WithBookmarks(d, many), Ct);

        var json = JsonSerializer.SerializeToElement(BookmarkCounts.Describe(_store, null));
        Assert.True(json.GetProperty("FileBytes").GetInt64() > BookmarkCounts.LargeFileBytes);
        Assert.Matches(@"^bookmarks\.json is \d+\.\d MB\. Every change writes the whole file, which stays quick up to about 5 MB", json.GetProperty("Warning").GetString());
        Assert.Equal(7000, json.GetProperty("Total").GetProperty("Bookmarks").GetInt32());
        Assert.Null(json.GetProperty("Users")[0].GetProperty("UserName").GetString());
    }

    private static Bookmark Bookmark(Guid userId)
    {
        var item = Guid.NewGuid();
        return new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ItemId = item,
            StartTicks = 600_000_000,
            ItemName = "Heat (1995)",
            Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "Heat" },
            AttachedUtc = Created,
            CreatedAt = Created,
            UpdatedAt = Created
        };
    }

    private static ClipInfo Clip(BookmarkClipStatus status, long? size) => new()
    {
        Status = status,
        Mode = BookmarkClipMode.Fast,
        StartTicks = 600_000_000,
        EndTicks = 900_000_000,
        RequestedAt = Created,
        FilePath = size is null ? null : @"C:\clips\alice\clip.mkv",
        FileSize = size
    };
}
