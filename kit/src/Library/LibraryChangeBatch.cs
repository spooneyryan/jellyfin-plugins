using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What a <see cref="ILibraryChangeConsumer"/> receives (<c>{Added, Updated, Removed, RemovedFolders, Kinds, FullPass,
/// DuringPostScan}</c>): the changes its filter accepted since its previous batch, each item once per list.
/// </summary>
/// <remarks>
/// <para>Consumers handle a batch in the order <b>Removed → verify sweep (when <see cref="RemovedFolders"/> isn't empty) →
/// Added/Updated</b>, so a rename seen in one batch (the old item removed, the new one added) detaches and relinks in the
/// same batch.</para>
/// <para>A <see cref="FullPass"/> batch comes from the plugin's post-scan task; it may be empty, and it takes every change
/// waiting until then. <see cref="DuringPostScan"/> means it is dispatched inside the scan, while
/// <c>ILibraryManager.IsScanRunning</c> is still true: a consumer or health guard must not take the running scan as a
/// reason to defer or as "unhealthy" then.</para>
/// <para><b>The full pass is not the end of the scan.</b> Jellyfin 10.11.5 runs the plugins' post-scan tasks before its
/// own (the plugins' assemblies come first in <c>ApplicationHost.GetComposablePartAssemblies</c>,
/// <c>ApplicationHost.cs:822-826</c>, and <c>LibraryManager.cs:263, 1220-1265</c> keep that order), so the full pass
/// covers the scan's validation of the library folders, not what core's post-scan tasks do after it: by-name music
/// artists, genres, music genres and studios created and dead ones deleted (a removed by-name <c>MusicArtist</c> is a
/// folder, so it is in <see cref="RemovedFolders"/>), box sets filled from the library's collection setting, items
/// whose parent is gone deleted (<c>CleanDatabaseScheduledTask</c>), and <c>UpdateInheritedValues()</c>. Those changes
/// come in the next batch, <see cref="AfterFullPass"/>, which goes out as soon as the scan has ended, without waiting
/// for the quiet period.</para>
/// </remarks>
internal sealed class LibraryChangeBatch
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeBatch"/> class.
    /// </summary>
    /// <param name="added">The items added.</param>
    /// <param name="updated">The items updated (and not added in this batch).</param>
    /// <param name="removed">The items removed.</param>
    /// <param name="removedFolders">The removed items that were folders, whatever the consumer's kinds.</param>
    /// <param name="userData">The user data saved (opt-in).</param>
    /// <param name="fullPass">Whether this is the full pass of the plugin's post-scan task.</param>
    /// <param name="duringPostScan">Whether it is dispatched inside the scan, by the post-scan task.</param>
    /// <param name="afterFullPass">Whether it holds changes raised after a scan's full pass while that scan still ran; never with <paramref name="fullPass"/>.</param>
    public LibraryChangeBatch(
        IReadOnlyList<ItemChange> added,
        IReadOnlyList<ItemChange> updated,
        IReadOnlyList<ItemChange> removed,
        IReadOnlyList<ItemChange> removedFolders,
        IReadOnlyList<ItemChange> userData,
        bool fullPass,
        bool duringPostScan,
        bool afterFullPass = false)
    {
        if (fullPass && afterFullPass)
        {
            throw new ArgumentException("A full pass doesn't come after a full pass.", nameof(afterFullPass));
        }

        Added = added ?? throw new ArgumentNullException(nameof(added));
        Updated = updated ?? throw new ArgumentNullException(nameof(updated));
        Removed = removed ?? throw new ArgumentNullException(nameof(removed));
        RemovedFolders = removedFolders ?? throw new ArgumentNullException(nameof(removedFolders));
        UserData = userData ?? throw new ArgumentNullException(nameof(userData));
        FullPass = fullPass;
        DuringPostScan = duringPostScan;
        AfterFullPass = afterFullPass;
        Kinds = added.Concat(updated).Concat(removed).Concat(removedFolders).Concat(userData)
            .Where(change => change.ItemKind is not null)
            .Select(change => change.ItemKind!.Value)
            .ToHashSet();
        var times = added.Concat(updated).Concat(removed).Concat(removedFolders).Concat(userData).Select(change => change.Utc).ToList();
        FirstUtc = times.Count > 0 ? times.Min() : null;
        LastUtc = times.Count > 0 ? times.Max() : null;
    }

    /// <summary>Gets the items added.</summary>
    public IReadOnlyList<ItemChange> Added { get; }

    /// <summary>Gets the items updated that weren't added in this batch.</summary>
    public IReadOnlyList<ItemChange> Updated { get; }

    /// <summary>
    /// Gets the items removed. A folder's removal is one change: its descendants went with it without an event of their own
    /// (<c>LibraryManager.cs:409-466</c>), so records on them are found through container ids and the verify sweep.
    /// </summary>
    public IReadOnlyList<ItemChange> Removed { get; }

    /// <summary>
    /// Gets every removed item that was a folder (series, season, album, music artist, box set, photo album, folder,
    /// library folder...), also those whose kind the consumer's filter doesn't list: container ids don't cover every folder
    /// (a music artist's albums store no artist), so a consumer runs its verify sweep when this isn't empty.
    /// </summary>
    public IReadOnlyList<ItemChange> RemovedFolders { get; }

    /// <summary>Gets the user data saved, one change per item and user (only with <see cref="LibraryChangeFilter.IncludeUserData"/>).</summary>
    public IReadOnlyList<ItemChange> UserData { get; }

    /// <summary>Gets the kinds of the items in the batch.</summary>
    public IReadOnlySet<BaseItemKind> Kinds { get; }

    /// <summary>
    /// Gets a value indicating whether this is the full pass of a library scan: the plugin's post-scan task, after the scan
    /// validated the library folders and before Jellyfin's own post-scan tasks (see <see cref="AfterFullPass"/>).
    /// </summary>
    public bool FullPass { get; }

    /// <summary>Gets a value indicating whether the batch is dispatched inside the scan (the post-scan task), while <c>IsScanRunning</c> is true.</summary>
    public bool DuringPostScan { get; }

    /// <summary>
    /// Gets a value indicating whether the batch holds changes raised after a scan's full pass while that scan still ran:
    /// what Jellyfin's own post-scan tasks did (by-name music artists, genres, music genres, studios, box sets, items whose
    /// parent is gone). It goes out as soon as the scan has ended, without waiting for the consumer's quiet period, with
    /// <see cref="FullPass"/> and <see cref="DuringPostScan"/> false; changes made after the scan may be in it too.
    /// </summary>
    public bool AfterFullPass { get; }

    /// <summary>Gets when the earliest change of the batch was seen; null for an empty batch.</summary>
    public DateTimeOffset? FirstUtc { get; }

    /// <summary>Gets when the latest change of the batch was seen; null for an empty batch.</summary>
    public DateTimeOffset? LastUtc { get; }

    /// <summary>Gets the number of changes (a removed folder counted once, in <see cref="Removed"/> or in <see cref="RemovedFolders"/>).</summary>
    public int Count => Added.Count + Updated.Count + UserData.Count + Removed.Select(c => c.ItemId).Union(RemovedFolders.Select(c => c.ItemId)).Count();

    /// <summary>Gets a value indicating whether the batch holds no change.</summary>
    public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Removed.Count == 0 && RemovedFolders.Count == 0 && UserData.Count == 0;
}
