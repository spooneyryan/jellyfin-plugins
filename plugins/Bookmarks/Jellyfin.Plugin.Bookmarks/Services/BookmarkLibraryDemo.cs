using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's record lifecycle and library reads: the demo's records
/// (<see cref="LibraryDemoRecords"/>) with the kit's verify sweep, re-resolution, archive, export and purge, and read-only
/// checks of <see cref="FreshUserData"/>, <see cref="LibraryAccess"/> and <see cref="Kit.Library.VersionSets"/> against a
/// real library, for <c>scripts/library-demo-probe.mjs</c>. Every call answers 403 "turned off" unless the developer
/// setting <c>EnableLibraryDemo</c> is on. Bookmarks' own identity service uses the same kit services.
/// </summary>
/// <remarks>
/// What it shows of a record is its ids, kind, name, numbers, times and marks, never a path. A read "as a user" takes
/// that user's view, as an admin can read any user's data natively.
/// </remarks>
internal sealed class BookmarkLibraryDemo : IBookmarkLibraryDemo
{
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILibraryManager _library;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userData;
    private readonly LibraryDemoRecords _records;
    private readonly AttachedRecordVerifier _verifier;
    private readonly DetachedRecords _detached;
    private readonly LibraryAccess _access;
    private readonly FreshUserData _fresh;
    private readonly IServerApplicationHost _applicationHost;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkLibraryDemo"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="records">The demo's records.</param>
    /// <param name="verifier">The kit's verify sweep.</param>
    /// <param name="detached">The kit's detached records.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="fresh">The kit's fresh user-data reads.</param>
    /// <param name="applicationHost">The server, for the export's server id.</param>
    /// <param name="timeProvider">The lifecycle's clock.</param>
    public BookmarkLibraryDemo(
        IPluginConfigurationProvider configuration,
        ILibraryManager library,
        IUserManager userManager,
        IUserDataManager userData,
        LibraryDemoRecords records,
        AttachedRecordVerifier verifier,
        DetachedRecords detached,
        LibraryAccess access,
        FreshUserData fresh,
        IServerApplicationHost applicationHost,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _library = library;
        _userManager = userManager;
        _userData = userData;
        _records = records;
        _verifier = verifier;
        _detached = detached;
        _access = access;
        _fresh = fresh;
        _applicationHost = applicationHost;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> DescribeRecords()
    {
        EnsureEnabled();
        var archive = _detached.ArchiveOf(_records).Read();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Store"] = Kit.Diagnostics.KitDiagnostics.DescribeStore(_records.Store),
            ["Now"] = _timeProvider.GetUtcNow().UtcDateTime,
            ["ClockOffsetHonoured"] = DeveloperClock.IsHonoured,
            ["RetentionDays"] = _records.DetachedRetentionDays,
            ["Records"] = _records.Store.Current.Records.Select(Describe).ToList(),
            ["ArchiveFile"] = System.IO.Path.GetFileName(_detached.ArchiveOf(_records).FilePath),
            ["Archived"] = archive.Records.Where(r => !_records.ContainsRecord(r.Id)).Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Id"] = r.Id,
                ["ItemId"] = r.Identity.ItemId,
                ["Kind"] = r.Identity.Kind,
                ["Name"] = r.Identity.Name,
                ["DetachedUtc"] = r.DetachedUtc,
                ["ArchivedUtc"] = r.ArchivedUtc,
                ["Seq"] = r.Seq
            }).ToList(),
            ["ArchiveLastCheckedSeq"] = archive.LastCheckedSeq,
            ["Verifier"] = _verifier.Describe(),
            ["Detached"] = _detached.Describe()
        };
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> AddRecordAsync(Guid itemId, Guid? userId, string? note, bool withoutContainerIds, User? caller, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var owner = RequireUser(userId, caller);
        if (itemId == Guid.Empty)
        {
            throw PluginException.Invalid("An item id is required.");
        }

        // As the record's user: an item they can't see is the same as none (the kit's privacy rule).
        var item = _library.GetItemById<BaseItem>(itemId, owner) ?? throw PluginException.NotFound("No library item the user can see has this id.");
        var identity = ItemIdentity.Capture(item, _library);
        if (withoutContainerIds)
        {
            identity = identity with { SeriesId = null, SeasonId = null, AlbumId = null };
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var record = new LibraryDemoRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            ItemId = item.Id,
            Identity = identity,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            MediaSourceId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            CreatedAt = now,
            AttachedUtc = now
        };
        await _records.Store.UpdateAsync(document => LibraryDemoRecords.WithRecords(document, document.Records.Append(record)), cancellationToken).ConfigureAwait(false);
        return Describe(record);
    }

    /// <inheritdoc />
    public async Task ClearRecordsAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await _records.Store.UpdateAsync(document => document.Records.Count == 0 ? document : document with { Records = [] }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> SweepAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return (await _verifier.SweepAsync(VerifySweepReason.Requested, cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ResolveAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return (await _detached.ResolveDetachedAsync(_records, cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> PurgeAsync(int? olderThanDays, bool includeArchive, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return (await _detached.PurgeAsync(_records, olderThanDays, includeArchive, cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public IActionResult Export(HttpContext context)
    {
        EnsureEnabled();
        var data = _records.Store.ExportData();
        var users = KitUsers.GetAll(_userManager).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);
        var envelope = ExportEnvelope.Create(
            BookmarksKit.Options,
            data,
            ExportUser.For(_records.Store.Current.Records.Select(r => r.UserId), KnownUsers.Empty, users, new HashSet<Guid>()),
            settings: null,
            archive: _detached.ExportArchive(_records),
            serverId: _applicationHost.SystemId,
            exportedUtc: _timeProvider.GetUtcNow().UtcDateTime);
        return envelope.ToFileResult(context, LibraryDemoRecords.StoreName);
    }

    /// <inheritdoc />
    public IDictionary<string, object?> ReadUserData(Guid itemId, Guid? userId, string? read, string? path, User? caller)
    {
        EnsureEnabled();
        var user = RequireUser(userId, caller);
        if (itemId == Guid.Empty || _library.GetItemById<BaseItem>(itemId, user) is null)
        {
            throw PluginException.NotFound("No library item the user can see has this id.");
        }

        var readPath = path switch
        {
            null or "" or "Query" => FreshUserDataReadPath.Query,
            "Repository" => FreshUserDataReadPath.Repository,
            _ => throw PluginException.Invalid("The path must be Query or Repository.")
        };
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ItemId"] = itemId, ["UserId"] = user.Id };
        switch (read)
        {
            case "Cached":
                // Core's cached instance as GetUserData reads it (UserDataManager.cs:239-245), left as it is.
                var cached = _library.GetItemById(itemId)!;
                result["Read"] = "Cached";
                result["HasSnapshot"] = cached.UserData is not null;
                result["Rows"] = cached.UserData?.Count;
                Fill(result, _userData.GetUserData(user, cached));
                break;
            case null or "" or "Fresh":
                var fresh = _fresh.Read(itemId, user, readPath) ?? throw PluginException.Unavailable("UserDataUnavailable: the item couldn't be read from the database.");
                result["Read"] = "Fresh";
                result["Source"] = fresh.Source.ToString();
                result["HadSnapshot"] = fresh.HadSnapshot;
                result["Rows"] = fresh.Rows;
                result["ReadPath"] = _fresh.ReadPath.ToString();
                Fill(result, fresh.Data);
                break;
            default:
                throw PluginException.Invalid("The read must be Fresh or Cached.");
        }

        return result;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> DescribeLibraries(Guid? userId, IReadOnlyList<Guid>? libraryIds, User? caller)
    {
        EnsureEnabled();
        var user = ViewUser(userId, caller);
        var query = _access.CreateQuery(user, libraryIds);
        IDictionary<string, object?>? described = null;
        if (query is not null)
        {
            described = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["UserSet"] = query.User is not null,
                ["TopParentIds"] = query.TopParentIds,
                ["GroupByPresentationUniqueKey"] = query.GroupByPresentationUniqueKey,
                ["HasOwnerId"] = query.HasOwnerId
            };

            // What the factory's query finds: every movie of the libraries, none of them owned (extras, local alternates).
            query.IncludeItemTypes = [BaseItemKind.Movie];
            query.Recursive = true;
            query.IsVirtualItem = false;
            query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
            var movies = _library.GetItemList(query);
            described["Movies"] = movies.Count;
            described["OwnedMovies"] = movies.Count(LibraryAccess.IsOwned);
            described["MovieIds"] = movies.Select(m => m.Id).ToList();
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["UserId"] = user?.Id,
            ["Visible"] = _access.VisibleLibraries(user).Select(l => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Id"] = l.Id,
                ["Name"] = l.Name,
                ["CollectionType"] = l.CollectionType?.ToString(),
                ["PhysicalFolderIds"] = l.PhysicalFolderIds
            }).ToList(),
            ["Resolved"] = _access.ResolveLibraries(libraryIds, user).Select(l => l.Id).ToList(),
            ["Query"] = described
        };
    }

    /// <inheritdoc />
    public IDictionary<string, object?> DescribeVersionSets(IReadOnlyList<Guid> itemIds, Guid? userId, User? caller)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0 || itemIds.Count > 100)
        {
            throw PluginException.Invalid("Give 1 to 100 item ids.");
        }

        var user = ViewUser(userId, caller);
        var lookup = Kit.Library.VersionSets.LibraryLookup(_library);
        var paths = Kit.Library.VersionSets.LibraryPaths(_library);
        var members = itemIds.Where(id => id != Guid.Empty).Select(lookup).Where(m => m is not null).Select(m => m!).ToList();
        var sets = Kit.Library.VersionSets.Group(members, lookup, paths);
        var libraries = _access.ResolveLibraries(null, user);

        // As a scoped search: the matches are the given items the user's libraries hold, and the representatives follow
        // Collapse's eligibility.
        var eligible = _access.EligibleIds(user, libraries, members.Select(m => m.Id).ToList());
        var representatives = Kit.Library.VersionSets.Collapse(
            members.Where(m => eligible.Contains(m.Id)).ToList(),
            ids => _access.EligibleIds(user, libraries, ids),
            lookup,
            paths);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["UserId"] = user?.Id,
            ["Sets"] = sets.Select(s => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["RootId"] = s.RootId,
                ["MemberIds"] = s.MemberIds,
                ["ExternalIds"] = s.ExternalIds,
                ["State"] = s.State.ToString(),
                ["IsOrphaned"] = s.IsOrphaned,
                ["StaleLinkIds"] = s.StaleLinkIds,
                ["StaleLinks"] = s.StaleLinks.Count,
                ["ChainedIds"] = s.ChainedIds,
                ["UnlistedIds"] = s.UnlistedIds,
                ["ListedNotPointingIds"] = s.ListedNotPointingIds,
                ["HasCycle"] = s.HasCycle
            }).ToList(),
            ["Eligible"] = eligible.ToList(),
            ["Representatives"] = representatives.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Id"] = r.Id,
                ["IsRoot"] = r.IsRoot,
                ["RootId"] = r.Set.RootId,
                ["MatchedIds"] = r.MatchedIds
            }).ToList()
        };
    }

    private static void Fill(Dictionary<string, object?> result, MediaBrowser.Controller.Entities.UserItemData? data)
    {
        result["IsFavorite"] = data?.IsFavorite;
        result["Played"] = data?.Played;
        result["PlayCount"] = data?.PlayCount;
        result["PlaybackPositionTicks"] = data?.PlaybackPositionTicks;
        result["Rating"] = data?.Rating;
        result["LastPlayedDate"] = data?.LastPlayedDate;
    }

    private static Dictionary<string, object?> Describe(LibraryDemoRecord record) => new(StringComparer.Ordinal)
    {
        ["Id"] = record.Id,
        ["UserId"] = record.UserId,
        ["ItemId"] = record.ItemId,
        ["Kind"] = record.Identity.Kind,
        ["Name"] = record.Identity.Name,
        ["Season"] = record.Identity.Season,
        ["Episode"] = record.Identity.Episode,
        ["SeriesId"] = record.Identity.SeriesId,
        ["SeasonId"] = record.Identity.SeasonId,
        ["RunTimeTicks"] = record.Identity.RunTimeTicks,
        ["ProviderIds"] = record.Identity.ProviderIds,
        ["Note"] = record.Note,
        ["MediaSourceId"] = record.MediaSourceId,
        ["CreatedAt"] = record.CreatedAt,
        ["AttachedUtc"] = record.AttachedUtc,
        ["DetachedUtc"] = record.DetachedUtc,
        ["DetachedBy"] = record.DetachedBy,
        ["RelinkedUtc"] = record.RelinkedUtc,
        ["RelinkStrength"] = record.RelinkStrength?.ToString(),
        ["LastOutcome"] = record.LastOutcome
    };

    private User RequireUser(Guid? userId, User? caller)
    {
        if (userId is { } id && id != Guid.Empty)
        {
            return _userManager.GetUserById(id) ?? throw PluginException.NotFound("No user has this id.");
        }

        return caller ?? throw PluginException.Invalid("An API key has no user: give a userId.");
    }

    // The user whose view a read takes; null (an API key without a userId) is the whole server.
    private User? ViewUser(Guid? userId, User? caller)
        => userId is { } id && id != Guid.Empty
            ? _userManager.GetUserById(id) ?? throw PluginException.NotFound("No user has this id.")
            : caller;

    private void EnsureEnabled()
    {
        if (!_configuration.GetConfiguration().EnableLibraryDemo)
        {
            throw PluginException.FeatureDisabled("The library demo is turned off (developer setting EnableLibraryDemo).");
        }
    }
}
