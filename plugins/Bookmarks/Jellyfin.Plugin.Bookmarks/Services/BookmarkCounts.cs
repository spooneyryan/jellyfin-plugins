using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The Diagnostics entry <c>BookmarkCounts</c>: how many bookmarks each user has, and
/// of which kind, for the admin, who never browses another user's bookmarks. Counts and sizes only: no name, note, title
/// or path of a bookmark reaches it. The kit's own <c>Users</c> entry (the reconciler's unknown users) stays next to it.
/// </summary>
/// <remarks>
/// Computed from memory on every Diagnostics request (the store's index; the clip sizes the clip worker recorded, never a
/// file on disk), plus the size of <c>bookmarks.json</c>, with a warning above <see cref="LargeFileBytes"/> (the
/// whole file is written on every change, which stays quick to about 5 MB).
/// </remarks>
internal static class BookmarkCounts
{
    /// <summary>The size of <c>bookmarks.json</c> above which Diagnostics warns (3 MB).</summary>
    public const long LargeFileBytes = 3L * 1024 * 1024;

    /// <summary>
    /// Describes the counts.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="userManager">The user manager, for the server's user names; <c>null</c> leaves only the last seen ones.</param>
    /// <returns>The entry: <c>Users</c> (one row per user id that has records, most bookmarks first), <c>Total</c>,
    /// <c>FileBytes</c> and <c>Warning</c>.</returns>
    public static IDictionary<string, object?> Describe(IBookmarkStore store, IUserManager? userManager)
    {
        ArgumentNullException.ThrowIfNull(store);
        var server = userManager is null
            ? new Dictionary<Guid, string>()
            : KitUsers.GetAll(userManager).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);
        var known = store.KnownUsers;
        var records = store.GetAll();
        var rows = records
            .GroupBy(b => b.UserId)
            .Select(g => Count(g.Key, g.ToList(), server, known))
            .OrderByDescending(r => r.Bookmarks)
            .ThenBy(r => r.UserName ?? r.UserId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = Count(Guid.Empty, records, server, known);

        long? fileBytes = null;
        try
        {
            var file = new FileInfo(store.Documents.FilePath);
            fileBytes = file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The size is a hint; the store's own entry reports a file it can't read.
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Users"] = rows.Select(r => r.ToDictionary(withUser: true)).ToList(),
            ["Total"] = WithUsers(total.ToDictionary(withUser: false), rows.Count),
            ["FileBytes"] = fileBytes,
            ["Warning"] = fileBytes > LargeFileBytes
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "bookmarks.json is {0:0.0} MB. Every change writes the whole file, which stays quick up to about 5 MB; at this size a change takes longer, and the file keeps growing with the bookmarks.",
                    fileBytes.Value / 1048576.0)
                : null
        };
    }

    private static Dictionary<string, object?> WithUsers(Dictionary<string, object?> total, int users)
    {
        total["Users"] = users;
        return total;
    }

    private static Row Count(Guid userId, IReadOnlyList<Bookmark> records, Dictionary<Guid, string> server, KnownUsers known)
    {
        var live = records.Where(BookmarkRecordRules.IsLive).ToList();
        var files = records.Where(b => !string.IsNullOrEmpty(b.Clip?.FilePath)).ToList();
        return new Row
        {
            UserId = KnownUsers.FormatId(userId),
            UserName = server.TryGetValue(userId, out var name) ? name : known.NameOf(userId),
            Known = server.ContainsKey(userId),
            Bookmarks = live.Count,
            Named = live.Count(b => !string.IsNullOrEmpty(b.Name)),
            Ranges = live.Count(b => b.EndTicks is not null),
            Detached = live.Count(b => b.DetachedUtc is not null),
            Relinked = live.Count(b => b.RelinkedUtc is not null),
            Imported = live.Count(b => b.Origin == BookmarkOrigin.JellyfinEnhanced),
            Clips = files.Count,
            ClipBytes = files.Sum(b => Math.Max(0, b.Clip!.FileSize ?? 0)),
            ClipsBeingMade = live.Count(b => b.Clip?.Status is BookmarkClipStatus.Queued or BookmarkClipStatus.Running),
            Tombstones = records.Count(BookmarkRecordRules.IsTombstone)
        };
    }

    private sealed record Row
    {
        public required string UserId { get; init; }

        public string? UserName { get; init; }

        public bool Known { get; init; }

        public int Bookmarks { get; init; }

        public int Named { get; init; }

        public int Ranges { get; init; }

        public int Detached { get; init; }

        public int Relinked { get; init; }

        public int Imported { get; init; }

        public int Clips { get; init; }

        public long ClipBytes { get; init; }

        public int ClipsBeingMade { get; init; }

        public int Tombstones { get; init; }

        public Dictionary<string, object?> ToDictionary(bool withUser)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (withUser)
            {
                row["UserId"] = UserId;
                row["UserName"] = UserName;
                row["Known"] = Known;
            }

            row["Bookmarks"] = Bookmarks;
            row["Named"] = Named;
            row["Ranges"] = Ranges;
            row["Detached"] = Detached;
            row["Relinked"] = Relinked;
            row["Imported"] = Imported;
            row["Clips"] = Clips;
            row["ClipBytes"] = ClipBytes;
            row["ClipsBeingMade"] = ClipsBeingMade;
            row["Tombstones"] = Tombstones;
            return row;
        }
    }
}
