using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Compare;

/// <summary>
/// <c>GET …/Groups/{groupId}/Compare</c>: one group's copies side by side, from what Jellyfin's library already holds.
/// </summary>
/// <remarks>
/// <para><b>Which group</b>: the one <see cref="IDuplicateGroups.GetGroupAsync"/> finds for the caller, so the same
/// libraries, parental limits and 404s apply, and every copy is one of the caller's entries or a version of one.</para>
/// <para><b>The copies</b>: each entry's main version, then the other versions in the caller's libraries
/// (<c>LinkedVersionIds</c>), then the main version's local alternates (versions in its folder named after it); an item
/// no longer in the library is left out. At most <see cref="MaxCopies"/>.</para>
/// <para><b>What is read</b>: the items, their media streams as Jellyfin stored them at the last scan
/// (<see cref="IMediaSourceManager.GetMediaStreams(Guid)"/>: the database, never the file, so an offline copy still shows
/// its tracks), their extras, and every user's watch data for them (counts only). Each file is checked for being there
/// (exists, never opened; a device path is never checked), all within <see cref="FileCheckTimeout"/>. Nothing is
/// written: no item, no user data, no file.</para>
/// </remarks>
internal sealed partial class DuplicateComparison : IDuplicateComparison
{
    /// <summary>The most copies a comparison shows.</summary>
    public const int MaxCopies = 24;

    /// <summary>The most extras listed by name per copy (all are counted).</summary>
    public const int MaxFeatureItems = 50;

    /// <summary>The most users whose watch data is counted; above it, <c>Played</c> is null.</summary>
    public const int MaxUsersCounted = 1000;

    private readonly IDuplicateGroups _groups;
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IUserDataManager _userData;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateComparison"/> class.
    /// </summary>
    /// <param name="groups">The duplicate groups.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="mediaSources">The media source manager, for the stored streams.</param>
    /// <param name="userData">The user data manager, for the play counts.</param>
    /// <param name="users">The user manager.</param>
    public DuplicateComparison(IDuplicateGroups groups, ILibraryManager library, IMediaSourceManager mediaSources, IUserDataManager userData, IUserManager users)
    {
        _groups = groups;
        _library = library;
        _mediaSources = mediaSources;
        _userData = userData;
        _users = users;
    }

    /// <summary>Gets or sets how long the file checks may take together; a file not answered by then is <c>Unknown</c>.</summary>
    internal TimeSpan FileCheckTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets or sets the check that a file (false) or a disc folder (true) is there; tests replace it.</summary>
    internal Func<string, bool, bool> Exists { get; set; } = static (path, isFolder) => isFolder ? Directory.Exists(path) : File.Exists(path);

    /// <inheritdoc />
    public async Task<LibraryMaintenanceDuplicateCompareDto?> CompareAsync(
        string groupId,
        string? mode,
        string? unknownYear,
        string? libraryIds,
        User? user,
        CancellationToken cancellationToken)
    {
        var group = await _groups.GetGroupAsync(groupId, mode, unknownYear, libraryIds, user, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var copies = Copies(group, out var truncated);
        var states = await FileStatesAsync(copies.Select(c => c.Item), cancellationToken).ConfigureAwait(false);
        var users = Users();
        var dtos = copies.Select(c => Copy(c.Item, c.EntryId, c.Role, states.GetValueOrDefault(c.Item.Id, "Unknown"), users)).ToList();
        return new LibraryMaintenanceDuplicateCompareDto
        {
            GroupId = group.GroupId,
            DisplayTitle = group.DisplayTitle,
            SuggestedKeepId = group.SuggestedKeepId,
            Copies = dtos,
            Truncated = truncated,
            Best = Best(dtos),
        };
    }

    /// <summary>
    /// The details the copies are ranked on, the highest best, in the order the page shows them. A copy with no value
    /// (unknown, or no stream data at all for the track counts) takes no part.
    /// </summary>
    /// <param name="copies">The copies.</param>
    /// <returns>Each detail the copies differ in, with the copies that have its best value.</returns>
    internal static List<LibraryMaintenanceDuplicateCompareBestDto> Best(IReadOnlyList<LibraryMaintenanceDuplicateCompareCopyDto> copies)
    {
        (string Field, Func<LibraryMaintenanceDuplicateCompareCopyDto, double?> Score)[] fields =
        [
            ("Resolution", c => c.Video is { Width: > 0, Height: > 0 } v ? (double)v.Width!.Value * v.Height!.Value : null),
            ("Size", c => c.SizeBytes is > 0 and var s ? s : null),
            ("Bitrate", c => c.TotalBitrate is > 0 and var b ? b : null),
            ("VideoBitrate", c => c.Video?.BitRate is > 0 and var b ? b : null),
            ("BitDepth", c => c.Video?.BitDepth is > 0 and var d ? d : null),
            ("DynamicRange", c => c.Video?.VideoRange switch { "HDR" => 1, "SDR" => 0, _ => null }),
            ("AudioTracks", c => HasStreams(c) ? c.Audio.Count : null),
            ("AudioChannels", c => c.Audio.Count > 0 ? c.Audio.Max(a => a.Channels ?? 0) is > 0 and var n ? n : null : null),
            ("SubtitleTracks", c => HasStreams(c) ? c.Subtitles.Count : null),
            ("SpecialFeatures", c => c.SpecialFeatures.Count),
        ];

        var best = new List<LibraryMaintenanceDuplicateCompareBestDto>();
        foreach (var (field, score) in fields)
        {
            var scores = copies.Select(c => (c.ItemId, Score: score(c))).Where(s => s.Score is not null).ToList();
            if (scores.Select(s => s.Score).Distinct().Count() < 2)
            {
                continue;
            }

            var top = scores.Max(s => s.Score);
            best.Add(new LibraryMaintenanceDuplicateCompareBestDto { Field = field, ItemIds = scores.Where(s => s.Score == top).Select(s => s.ItemId).ToList() });
        }

        return best;
    }

    /// <summary>Gets whether a track's title or comment says it is a commentary, in English and five other languages.</summary>
    /// <param name="stream">The track.</param>
    /// <returns>True for a commentary.</returns>
    internal static bool IsCommentary(MediaStream stream)
        => stream.Type == MediaStreamType.Audio && (CommentaryWords().IsMatch(stream.Title ?? string.Empty) || CommentaryWords().IsMatch(stream.Comment ?? string.Empty));

    /// <summary>The folder part of a path, with either slash, without the trailing one; empty for a bare name.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The folder.</returns>
    internal static string FolderOf(string? path)
    {
        var trimmed = TrimSlashes(path ?? string.Empty);
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? string.Empty : trimmed[..cut];
    }

    /// <summary>The last part of a path, with either slash.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The name.</returns>
    internal static string NameOf(string? path)
    {
        var trimmed = TrimSlashes(path ?? string.Empty);
        return trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    private static string TrimSlashes(string path) => path.Length > 3 ? path.TrimEnd('\\', '/') : path;

    private static bool HasStreams(LibraryMaintenanceDuplicateCompareCopyDto copy) => copy.Video is not null || copy.Audio.Count > 0 || copy.Subtitles.Count > 0;

    // Where an extra is, from the copy's folder (a disc folder is its own): beside it, in a folder in it, or elsewhere.
    private static (string Location, string? Folder) Place(string baseFolder, string? extraPath)
    {
        if (string.IsNullOrEmpty(extraPath) || string.IsNullOrEmpty(baseFolder))
        {
            return ("Elsewhere", null);
        }

        var folder = FolderOf(extraPath);
        if (string.Equals(Slashes(folder), Slashes(baseFolder), StringComparison.OrdinalIgnoreCase))
        {
            return ("BesideFile", null);
        }

        var prefix = Slashes(baseFolder) + "/";
        return Slashes(folder).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? ("ExtrasFolder", folder[prefix.Length..])
            : ("Elsewhere", null);
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static bool IsDisc(BaseItem item) => item is Video { VideoType: VideoType.Dvd or VideoType.BluRay };

    [GeneratedRegex("comment|kommentar|comentario|commento|coment[áa]rio", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommentaryWords();

    // Each entry's main version, its other versions in the caller's libraries, its local alternates; once each.
    private List<(BaseItem Item, Guid EntryId, string Role)> Copies(LibraryMaintenanceDuplicateGroupDto group, out bool truncated)
    {
        var copies = new List<(BaseItem Item, Guid EntryId, string Role)>();
        var seen = new HashSet<Guid>();
        void Add(Guid id, Guid entryId, string role)
        {
            if (id != Guid.Empty && seen.Add(id) && _library.GetItemById(id) is { } item)
            {
                copies.Add((item, entryId, role));
            }
        }

        foreach (var entry in group.Entries)
        {
            Add(entry.ItemId, entry.ItemId, "Main");
            foreach (var id in entry.LinkedVersionIds)
            {
                Add(id, entry.ItemId, "Version");
            }

            if (_library.GetItemById(entry.ItemId) is Video main)
            {
                foreach (var id in LocalAlternateIds(main))
                {
                    Add(id, entry.ItemId, "LocalAlternate");
                }
            }
        }

        truncated = copies.Count > MaxCopies;
        return truncated ? copies.Take(MaxCopies).ToList() : copies;
    }

    private IEnumerable<Guid> LocalAlternateIds(Video video)
#if JELLYFIN_12
        => _library.GetLocalAlternateVersionIds(video) ?? [];
#else
        => (video.LocalAlternateVersions ?? []).Where(p => !string.IsNullOrEmpty(p)).Select(p => _library.GetNewItemId(p, typeof(Video)));
#endif

    // The item's extras: on 10.11 its ExtraIds; on 12, which keeps no list, the items it owns that are extras (as the move
    // planner reads them). Theme songs and videos aren't special features.
    private List<BaseItem> Extras(BaseItem item)
    {
#if JELLYFIN_12
        var extras = _library.GetItemList(new InternalItemsQuery { OwnerIds = [item.Id], IncludeOwnedItems = true }) ?? [];
#else
        var extras = (item.ExtraIds ?? []).Where(id => id != Guid.Empty).Select(id => _library.GetItemById(id)).OfType<BaseItem>().ToList();
#endif
        return extras.Where(e => e.ExtraType is not null and not ExtraType.ThemeSong and not ExtraType.ThemeVideo).ToList();
    }

    // Every user through the kit (10.11.9 turned IUserManager.Users into a method).
    private List<User> Users() => KitUsers.GetAll(_users).Take(MaxUsersCounted + 1).ToList();

    // Whether each copy's file is there: only asked whether it exists, all together within FileCheckTimeout. A stream's
    // address is not a file; a device path (\\?\…, \\.\…) is never checked.
    private async Task<Dictionary<Guid, string>> FileStatesAsync(IEnumerable<BaseItem> items, CancellationToken cancellationToken)
    {
        var states = new Dictionary<Guid, string>();
        var checks = new Dictionary<Guid, Task<bool>>();
        var exists = Exists;
        foreach (var item in items)
        {
            var path = item.Path ?? string.Empty;
            if (path.Contains("://", StringComparison.Ordinal))
            {
                states[item.Id] = "NotAFile";
            }
            else if (path.Length == 0 || SettingsRules.IsDevicePath(path.Trim()))
            {
                states[item.Id] = "Unknown";
            }
            else
            {
                var isFolder = IsDisc(item);
                checks[item.Id] = Task.Run(() => exists(path, isFolder), cancellationToken);
            }
        }

        if (checks.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(checks.Values), Task.Delay(FileCheckTimeout, cancellationToken)).ConfigureAwait(false);
        }

        foreach (var (id, check) in checks)
        {
            states[id] = !check.IsCompletedSuccessfully ? "Unknown" : await check.ConfigureAwait(false) ? "Present" : "Missing";
        }

        return states;
    }

    private LibraryMaintenanceDuplicateCompareCopyDto Copy(BaseItem item, Guid entryId, string role, string fileState, List<User> users)
    {
        var streams = _mediaSources.GetMediaStreams(item.Id) ?? [];
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
        var path = item.Path ?? string.Empty;
        var asVideo = item as Video;
        return new LibraryMaintenanceDuplicateCompareCopyDto
        {
            ItemId = item.Id,
            EntryItemId = entryId,
            Role = role,
            Name = item.Name ?? string.Empty,
            Path = path,
            FileName = NameOf(path),
            Folder = FolderOf(path),
            FileState = fileState,
            SizeBytes = item.Size is > 0 ? item.Size : null,
            Container = string.IsNullOrEmpty(item.Container) ? null : item.Container,
            VideoType = asVideo?.VideoType.ToString() ?? string.Empty,
            RunTimeTicks = item.RunTimeTicks is > 0 ? item.RunTimeTicks : null,
            TotalBitrate = item.TotalBitrate is > 0 ? item.TotalBitrate : null,
            PartCount = (asVideo?.AdditionalParts?.Length ?? 0) + 1,
            Video = video is null ? null : new LibraryMaintenanceDuplicateCompareVideoDto
            {
                Width = video.Width,
                Height = video.Height,
                ResolutionLabel = DuplicateGroupsService.ResolutionLabel(video.Width),
                Codec = video.Codec,
                Profile = video.Profile,
                BitDepth = video.BitDepth,
                VideoRange = video.VideoRange.ToString(),
                VideoRangeType = video.VideoRangeType.ToString(),
                BitRate = video.BitRate,
                FrameRate = video.AverageFrameRate is > 0 ? video.AverageFrameRate : video.RealFrameRate is > 0 ? video.RealFrameRate : null,
                IsInterlaced = video.IsInterlaced,
            },
            Audio = audio.Select(a => new LibraryMaintenanceDuplicateCompareAudioDto
            {
                Index = a.Index,
                Language = Blank(a.Language),
                Codec = Blank(a.Codec),
                Channels = a.Channels,
                ChannelLayout = Blank(a.ChannelLayout),
                Title = Blank(a.Title),
                IsCommentary = IsCommentary(a),
                IsDefault = a.IsDefault,
                IsExternal = a.IsExternal,
            }).ToList(),
            Subtitles = subtitles.Select(s => new LibraryMaintenanceDuplicateCompareSubtitleDto
            {
                Index = s.Index,
                Language = Blank(s.Language),
                Codec = Blank(s.Codec),
                Title = Blank(s.Title),
                IsForced = s.IsForced,
                IsDefault = s.IsDefault,
                IsHearingImpaired = s.IsHearingImpaired,
                IsExternal = s.IsExternal,
                FileName = s.IsExternal && !string.IsNullOrEmpty(s.Path) ? NameOf(s.Path) : null,
            }).ToList(),
            SpecialFeatures = Features(item, audio.Count(IsCommentary)),
            DateCreated = item.DateCreated,
            Played = Played(item, users),
        };
    }

    private LibraryMaintenanceDuplicateCompareFeaturesDto Features(BaseItem item, int commentaries)
    {
        var baseFolder = IsDisc(item) ? TrimSlashes(item.Path ?? string.Empty) : FolderOf(item.Path);
        var extras = Extras(item)
            .Select(e =>
            {
                var (location, folder) = Place(baseFolder, e.Path);
                return new LibraryMaintenanceDuplicateCompareFeatureDto
                {
                    Name = e.Name ?? string.Empty,
                    FileName = string.IsNullOrEmpty(e.Path) ? null : NameOf(e.Path),
                    Kind = e.ExtraType?.ToString() ?? "Unknown",
                    Location = location,
                    Folder = folder,
                    RunTimeTicks = e.RunTimeTicks is > 0 ? e.RunTimeTicks : null,
                };
            })
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byKind = extras.GroupBy(e => e.Kind, StringComparer.Ordinal)
            .Select(g => new LibraryMaintenanceDuplicateCompareCountDto { Name = g.Key, Count = g.Count() })
            .ToList();
        var byLocation = extras.GroupBy(e => (e.Location, Folder: e.Folder ?? string.Empty))
            .Select(g => new LibraryMaintenanceDuplicateCompareCountDto { Name = g.Key.Location, Folder = g.Key.Folder.Length == 0 ? null : g.Key.Folder, Count = g.Count() })
            .ToList();
        if (commentaries > 0)
        {
            byKind.Add(new LibraryMaintenanceDuplicateCompareCountDto { Name = "Commentary", Count = commentaries });
            byLocation.Add(new LibraryMaintenanceDuplicateCompareCountDto { Name = "AudioTrack", Count = commentaries });
        }

        return new LibraryMaintenanceDuplicateCompareFeaturesDto
        {
            Count = extras.Count + commentaries,
            ByKind = byKind.OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal).ToList(),
            ByLocation = byLocation.OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).ToList(),
            Items = extras.Take(MaxFeatureItems).ToList(),
        };
    }

    // Every user's watch data for the copy, counted; read only (GetUserData makes no row for a user with none).
    private LibraryMaintenanceDuplicateComparePlayedDto? Played(BaseItem item, List<User> users)
    {
        if (users.Count > MaxUsersCounted)
        {
            return null;
        }

        var played = new LibraryMaintenanceDuplicateComparePlayedDto();
        foreach (var user in users)
        {
            if (_userData.GetUserData(user, item) is not { } data)
            {
                continue;
            }

            played.UsersPlayed += data.Played ? 1 : 0;
            played.UsersInProgress += data.PlaybackPositionTicks > 0 ? 1 : 0;
            played.PlayCount += Math.Max(data.PlayCount, 0);
        }

        return played;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
