using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Which library changes a <see cref="ILibraryChangeConsumer"/> receives, and how they are batched.
/// <see cref="LibraryChangeMonitor"/> reads it once, when it is created.
/// </summary>
/// <remarks>
/// <para>The defaults are the kit's: items of the library only (<c>SourceType == Library</c>: no channel or Live TV
/// items), the kinds the consumer names, a 60 s quiet period and a 10 min maximum wait, no user data, the removed
/// item's identity captured in the handler. <see cref="Ignore"/> leaves out items of the plugin's own, such as the box
/// sets Smart Search's auto-collections create (a feedback loop otherwise).</para>
/// <para>The filter runs inside Jellyfin's event handlers, so it must be cheap (O(1)) and never throw; the monitor catches
/// what it throws, and that consumer misses that event (the others still get it).</para>
/// </remarks>
internal sealed class LibraryChangeFilter
{
    /// <summary>The default quiet period: a batch goes out when no change came for this long.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(60);

    /// <summary>The default maximum wait: a batch goes out at the latest this long after its first change.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(10);

    /// <summary>The longest quiet period or maximum wait the monitor accepts; longer ones are cut to it.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets the kinds of items the consumer cares about; null for every kind. Removed folders reach
    /// <see cref="LibraryChangeBatch.RemovedFolders"/> whatever their kind.
    /// </summary>
    public IReadOnlySet<BaseItemKind>? Kinds { get; init; }

    /// <summary>Gets a value indicating whether only the library's own items count (<c>SourceType == Library</c>, the default).</summary>
    public bool LibraryItemsOnly { get; init; } = true;

    /// <summary>
    /// Gets a predicate that leaves an item out (true) for a kind of change, after the other rules: the plugin's own box
    /// sets, say, or folders except the updates of the box sets a rule names. It also applies to removed folders.
    /// </summary>
    public Func<LibraryChangeKind, BaseItem, bool>? Ignore { get; init; }

    /// <summary>
    /// Gets a value indicating whether the consumer also receives <c>IUserDataManager.UserDataSaved</c>, apart from
    /// <c>PlaybackStart</c> and <c>PlaybackProgress</c> saves (off by default).
    /// </summary>
    public bool IncludeUserData { get; init; }

    /// <summary>Gets a value indicating whether a removed item's <see cref="IdentitySnapshot"/> is captured in the handler (the default).</summary>
    public bool CaptureIdentity { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether each change carries the item's top parent id (<see cref="ItemChange.TopParentId"/>:
    /// its library path's folder), found by walking up its parents through the library manager's cache in the handler (off
    /// by default).
    /// </summary>
    public bool CaptureTopParent { get; init; }

    /// <summary>Gets how long no change must come before a batch goes out (60 s by default).</summary>
    public TimeSpan QuietPeriod { get; init; } = DefaultQuietPeriod;

    /// <summary>Gets how long after its first change a batch goes out at the latest, however busy the library (10 min by default).</summary>
    public TimeSpan MaxWait { get; init; } = DefaultMaxWait;

    /// <summary>
    /// Gets a value indicating whether the filter passes a change of an item: the source rule, the kinds and <see cref="Ignore"/>.
    /// </summary>
    /// <param name="kind">The change.</param>
    /// <param name="item">The item.</param>
    /// <param name="itemKind">The item's kind, when <see cref="BaseItemKind"/> names it.</param>
    /// <returns><c>true</c> when the consumer receives it.</returns>
    public bool Accepts(LibraryChangeKind kind, BaseItem item, BaseItemKind? itemKind)
        => AcceptsSource(item)
            && (Kinds is null || (itemKind is { } known && Kinds.Contains(known)))
            && (Ignore is null || !Ignore(kind, item));

    /// <summary>
    /// Gets a value indicating whether a removed folder reaches <see cref="LibraryChangeBatch.RemovedFolders"/>: the source
    /// rule and <see cref="Ignore"/>, whatever its kind.
    /// </summary>
    /// <param name="item">The removed item.</param>
    /// <param name="wasFolder">Whether it was a folder (<see cref="WasFolder"/>).</param>
    /// <returns><c>true</c> when the consumer receives it.</returns>
    public bool AcceptsRemovedFolder(BaseItem item, bool wasFolder)
        => wasFolder && AcceptsSource(item) && (Ignore is null || !Ignore(LibraryChangeKind.Removed, item));

    /// <summary>
    /// Gets a value indicating whether a removed item was a folder. <c>LibraryManager.DeleteItem</c> cuts the item's
    /// parent link before it raises <c>ItemRemoved</c>
    /// (<c>v10.11.5:Emby.Server.Implementations/Library/LibraryManager.cs:451, 466</c>), and a
    /// <see cref="MusicArtist"/> is a folder only while it has a parent (<c>MusicArtist.cs:30-33</c>: one without is an
    /// artist by name), so every removed music artist would say <c>IsFolder == false</c>. A music artist whose removal
    /// names a parent was a folder of the library (its albums store no artist); an artist by name, which Jellyfin's
    /// artists post-scan task deletes, has none and isn't one.
    /// </summary>
    /// <param name="item">The removed item.</param>
    /// <param name="eventParent">The parent the event names.</param>
    /// <returns><c>true</c> when it was a folder.</returns>
    public static bool WasFolder(BaseItem item, BaseItem? eventParent)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsFolder || (item is MusicArtist && eventParent is not null);
    }

    /// <summary>
    /// Gets a value indicating whether a user data save counts: not <c>PlaybackStart</c> or <c>PlaybackProgress</c>.
    /// </summary>
    /// <param name="reason">Why the data was saved.</param>
    /// <returns><c>true</c> when it counts.</returns>
    public static bool CountsUserData(UserDataSaveReason reason)
        => reason is not (UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress);

    /// <summary>
    /// The quiet period and maximum wait the monitor uses: negative ones are 0, long ones <see cref="MaxDelay"/>, and the
    /// maximum wait at least the quiet period.
    /// </summary>
    /// <returns>The two delays.</returns>
    public (TimeSpan QuietPeriod, TimeSpan MaxWait) EffectiveDelays()
    {
        var quiet = Clamp(QuietPeriod);
        var max = Clamp(MaxWait);
        return (quiet, max < quiet ? quiet : max);
    }

    private static TimeSpan Clamp(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelay ? MaxDelay : value;

    private bool AcceptsSource(BaseItem item) => !LibraryItemsOnly || item.SourceType == SourceType.Library;
}
