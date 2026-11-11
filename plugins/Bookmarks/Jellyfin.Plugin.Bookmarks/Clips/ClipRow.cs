using System;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One clip of the list: its record, and what it is right now, worked out once, when first asked for, through the request's
/// reading of the clip files.
/// </summary>
internal sealed class ClipRow
{
    private readonly ClipQueryService _service;
    private readonly ClipStateCache.Reading _reading;
    private ClipFacts? _facts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipRow"/> class.
    /// </summary>
    /// <param name="record">The record, with its clip.</param>
    /// <param name="service">The list, which works out the facts.</param>
    /// <param name="reading">The request's reading of the clip files.</param>
    public ClipRow(Bookmark record, ClipQueryService service, ClipStateCache.Reading reading)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(reading);
        if (record.Clip is null)
        {
            throw new ArgumentException("The record has no clip.", nameof(record));
        }

        Record = record;
        _service = service;
        _reading = reading;
    }

    /// <summary>
    /// Gets the record: a live bookmark, or a clip-only tombstone.
    /// </summary>
    public Bookmark Record { get; }

    /// <summary>
    /// Gets the record's clip.
    /// </summary>
    public ClipInfo Clip => Record.Clip!;

    /// <summary>
    /// Gets a value indicating whether a job is waiting or being made.
    /// </summary>
    public bool IsInProgress => Clip.Status is BookmarkClipStatus.Queued or BookmarkClipStatus.Running;

    /// <summary>
    /// Gets a value indicating whether the clip has a file path (the file may be gone).
    /// </summary>
    public bool HasFile => !string.IsNullOrEmpty(Clip.FilePath);

    /// <summary>
    /// Gets a value indicating whether the record is the clip of a deleted bookmark.
    /// </summary>
    public bool IsTombstone => BookmarkRecordRules.IsTombstone(Record);

    /// <summary>
    /// Gets the clip's state when its record alone says it (being made, or failed without a file), so nothing is
    /// asked of the file system; <c>null</c> for a clip with a file, whose state is its file's.
    /// </summary>
    public BookmarkClipState? KnownState => IsInProgress ? BookmarkClipState.InProgress : HasFile ? null : BookmarkClipState.Failed;

    /// <summary>
    /// Gets the clip's state: its record's when that says it, else its file's (through the cache).
    /// </summary>
    public BookmarkClipState State => KnownState ?? Facts.State;

    /// <summary>
    /// Gets what the clip is right now; for a clip with a file this asks the file system, through the cache.
    /// </summary>
    public ClipFacts Facts => _facts ??= _service.Describe(Record, _reading);
}
