using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Bookmarks.Clips;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// The library's side of the clip artwork for tests: the images of the videos it knows, the clip files that are library
/// items, and every refresh asked for. It can throw.
/// </summary>
internal sealed class FakeClipArtworkSource : IClipArtworkSource
{
    private readonly ConcurrentQueue<string> _refreshed = new();
    private int _lookups;

    /// <summary>Gets the videos' images, by item id.</summary>
    public ConcurrentDictionary<Guid, ClipArtworkImages> Items { get; } = new();

    /// <summary>Gets the clip files that are library items (compared without regard to case).</summary>
    public ISet<string> LibraryClips { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets what a look-up throws, if anything.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets every clip file a refresh was queued for, in order.</summary>
    public IReadOnlyList<string> Refreshed => _refreshed.ToList();

    /// <summary>Gets how many look-ups were made.</summary>
    public int Lookups => _lookups;

    public ClipArtworkImages? Find(Guid itemId)
    {
        Interlocked.Increment(ref _lookups);
        return Failure is { } failure ? throw failure : Items.TryGetValue(itemId, out var images) ? images : null;
    }

    public bool RefreshClip(string clipPath)
    {
        if (!LibraryClips.Contains(clipPath))
        {
            return false;
        }

        _refreshed.Enqueue(clipPath);
        return true;
    }
}
