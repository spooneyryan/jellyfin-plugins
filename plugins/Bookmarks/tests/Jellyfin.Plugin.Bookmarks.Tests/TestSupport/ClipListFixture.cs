using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// The My Clips list's tests (its query and derived fields): a store whose recorded clip folder is a
/// temporary folder, the clip files' states kept by a <see cref="ClipStateCache"/> on a clock that moves only when told,
/// and records with clips as the clip worker and the deletes leave them (<see cref="ClipSpec"/>), their files written for
/// real, named as the planner names them, under the owner's folder.
/// </summary>
internal sealed class ClipListFixture : IDisposable
{
    /// <summary>The time the records start from.</summary>
    public static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private int _made;

    public ClipListFixture(IClipFileSystem? fileSystem = null)
    {
        Root = Directory.Combine("clips");
        System.IO.Directory.CreateDirectory(Root);

        // As the clip worker leaves a clip folder: with its marker file (ClipRootMarker).
        ClipFileSupport.MarkClipFolder(Root);
        Store = ClipFileSupport.StoreWithClipRoots(Directory.Combine("data", "bookmarks.json"), Root);
        Clock = new ManualTimeProvider(new DateTimeOffset(T0));
        States = new ClipStateCache(Store, fileSystem ?? PhysicalClipFileSystem.Instance, Clock);
        Query = new ClipQueryService(Store, States);
    }

    public TempDirectory Directory { get; } = new();

    /// <summary>Gets the recorded clip folder.</summary>
    public string Root { get; }

    public BookmarkStore Store { get; }

    public ManualTimeProvider Clock { get; }

    public ClipStateCache States { get; }

    public ClipQueryService Query { get; }

    /// <summary>
    /// Makes a record with a clip and adds it to the store.
    /// </summary>
    public async Task<Bookmark> AddAsync(Guid userId, ClipSpec? spec = null, CancellationToken cancellationToken = default)
    {
        var record = Make(userId, spec ?? new ClipSpec());
        await Store.AddAsync(record, cancellationToken);
        return record;
    }

    /// <summary>
    /// Adds many records in one write.
    /// </summary>
    public Task AddAllAsync(IEnumerable<Bookmark> records, CancellationToken cancellationToken = default)
    {
        var list = records.ToList();
        return Store.UpdateDocumentAsync(d => d with { Bookmarks = [.. d.Bookmarks, .. list] }, cancellationToken);
    }

    /// <summary>
    /// Makes a record with a clip, as the store would hold it, and writes its file when <see cref="ClipSpec.OnDisk"/>.
    /// </summary>
    public Bookmark Make(Guid userId, ClipSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var made = Interlocked.Increment(ref _made);
        var created = spec.Created ?? T0.AddMinutes(made);
        var item = spec.ItemId ?? Guid.NewGuid();
        var start = spec.Start * TestData.OneSecond;
        var end = spec.End * TestData.OneSecond;
        // As the planner names a clip since 1.2.0 (BookmarkNames.ClipFileLabel): the bookmark's name, never its note.
        var label = string.IsNullOrWhiteSpace(spec.Name) ? null : spec.Name;
        var folder = spec.Folder ?? Path.Combine(Root, spec.UserFolder);
        string? path = null;
        if (spec.HasFile)
        {
            // As the planner names a clip: " (2)" when the name is taken, by a file or by another record of the fixture's.
            var fileName = spec.FileName ?? ClipFileNaming.BuildFileName(spec.ItemName, label, start, end, spec.Mode);
            lock (_paths)
            {
                path = spec.FileName is not null
                    ? Path.Combine(folder, fileName)
                    : ClipFileNaming.MakeUnique(folder, fileName, p => _paths.Contains(p) || File.Exists(p));
                _paths.Add(path);
            }
        }

        if (path is not null && spec.OnDisk)
        {
            System.IO.Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, new byte[spec.BytesOnDisk]);
            File.SetLastWriteTimeUtc(path, spec.Written ?? created.AddSeconds(20));
        }

        var finished = spec.Status is BookmarkClipStatus.Completed or BookmarkClipStatus.Failed or BookmarkClipStatus.Cancelled;
        var clip = new ClipInfo
        {
            Status = spec.Status,
            Mode = spec.JobMode ?? spec.Mode,
            StartTicks = start,
            EndTicks = end,
            RequestedAt = spec.Requested ?? created,
            CompletedAt = spec.NoCompletedAt ? null : spec.Completed ?? (finished ? created.AddSeconds(30) : null),
            Error = spec.Status == BookmarkClipStatus.Failed ? "ffmpeg failed" : null,
            FilePath = path,
            FileSize = path is null ? null : spec.Size,
            FileStartTicks = path is null || spec.NoFileRange ? null : (spec.FileStart ?? spec.Start) * TestData.OneSecond,
            FileEndTicks = path is null || spec.NoFileRange ? null : (spec.FileEnd ?? spec.End) * TestData.OneSecond,
            MissingSinceUtc = spec.MissingSince,
        };

        var record = new Bookmark
        {
            Id = spec.Id ?? Guid.NewGuid(),
            UserId = userId,
            ItemId = spec.Origin == BookmarkOrigin.Adopted ? Guid.Empty : item,
            MediaSourceId = spec.MediaSourceId,
            AudioStreamIndex = spec.AudioStreamIndex,
            StartTicks = (spec.BookmarkStart ?? spec.Start) * TestData.OneSecond,
            EndTicks = (spec.BookmarkEnd ?? spec.End) * TestData.OneSecond,
            Name = spec.Name,
            Note = spec.Note,
            ItemName = spec.ItemName,
            Identity = new IdentitySnapshot
            {
                ItemId = spec.Origin == BookmarkOrigin.Adopted ? Guid.Empty : item,
                Kind = spec.Kind,
                Name = spec.ItemName,
                Path = @"C:\media\" + spec.ItemName + ".mkv"
            },
            Origin = spec.Origin,
            AttachedUtc = created,
            DetachedUtc = spec.Detached,
            CreatedAt = created,
            UpdatedAt = created,
            Clip = spec.NoClip ? null : clip
        };

        return spec.Tombstone ? BookmarkRecordRules.ToTombstone(record, created.AddMinutes(1)) ?? throw new InvalidOperationException("A tombstone needs a clip file.") : record;
    }

    /// <summary>
    /// The ids of a page's clips, in order.
    /// </summary>
    public static Guid[] Ids(ClipPage page) => page.Rows.Select(r => r.Record.Id).ToArray();

    public void Dispose()
    {
        Store.Dispose();
        Directory.Dispose();
    }
}

/// <summary>
/// A record with a clip for <see cref="ClipListFixture.Make"/>: by default a completed Accurate clip of 1:00–1:30 on a movie
/// "Heat (1995)", its file (1,000 bytes recorded) on disk in the clip folder under the user's folder.
/// </summary>
internal sealed record ClipSpec
{
    public Guid? Id { get; init; }

    public string? Name { get; init; }

    public string? Note { get; init; }

    public string ItemName { get; init; } = "Heat (1995)";

    public string Kind { get; init; } = "Movie";

    public Guid? ItemId { get; init; }

    public long Start { get; init; } = 60;

    public long End { get; init; } = 90;

    /// <summary>Gets the bookmark's own start, when it moved since the clip (<c>Range changed</c>).</summary>
    public long? BookmarkStart { get; init; }

    public long? BookmarkEnd { get; init; }

    public BookmarkClipStatus Status { get; init; } = BookmarkClipStatus.Completed;

    /// <summary>Gets the file's mode (its extension).</summary>
    public BookmarkClipMode Mode { get; init; } = BookmarkClipMode.Accurate;

    /// <summary>Gets the most recent job's mode, when it isn't the file's (a remake in the other mode).</summary>
    public BookmarkClipMode? JobMode { get; init; }

    public bool HasFile { get; init; } = true;

    public bool OnDisk { get; init; } = true;

    public int BytesOnDisk { get; init; } = 4;

    public long? Size { get; init; } = 1000;

    public bool Tombstone { get; init; }

    public bool NoClip { get; init; }

    public DateTime? Created { get; init; }

    public DateTime? Requested { get; init; }

    public DateTime? Completed { get; init; }

    public bool NoCompletedAt { get; init; }

    /// <summary>Gets the file's last write time; by default 20 s after the record was made.</summary>
    public DateTime? Written { get; init; }

    public long? FileStart { get; init; }

    public long? FileEnd { get; init; }

    public bool NoFileRange { get; init; }

    public string? FileName { get; init; }

    /// <summary>Gets the folder the file is in; by default the user's folder in the clip folder.</summary>
    public string? Folder { get; init; }

    public string UserFolder { get; init; } = "alice";

    public DateTime? Detached { get; init; }

    public BookmarkOrigin Origin { get; init; } = BookmarkOrigin.Native;

    public string? MediaSourceId { get; init; }

    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets since when the missing-clip sweep has marked the file missing, or <c>null</c>.</summary>
    public DateTime? MissingSince { get; init; }
}
