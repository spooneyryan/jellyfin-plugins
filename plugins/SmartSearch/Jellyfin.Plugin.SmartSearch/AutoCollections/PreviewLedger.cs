using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The recent previews, by hash, so a save can tell how far the members moved since the preview it names. Each is of
/// one definition (or of a new one): a save of another definition doesn't find it. Kept in memory, a few dozen for half
/// an hour; a hash it doesn't know (an old preview, or one from before a restart) means the caller previews again.
/// </summary>
internal sealed class PreviewLedger
{
    /// <summary>The most previews kept.</summary>
    public const int MaxEntries = 64;

    /// <summary>How long a preview is kept.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly LinkedList<Entry> _entries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewLedger"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public PreviewLedger(TimeProvider time) => _time = time;

    /// <summary>Records a preview.</summary>
    /// <param name="hash">Its hash.</param>
    /// <param name="ids">Its members, in order.</param>
    /// <param name="wouldRemove">How many current members it would remove.</param>
    /// <param name="definitionId">The definition previewed, or <c>null</c> for a new one.</param>
    public void Record(string hash, IReadOnlyList<Guid> ids, int wouldRemove, Guid? definitionId)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            Drop(hash, definitionId);
            _entries.AddFirst(new Entry(hash, ids, wouldRemove, definitionId, now));
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveLast();
            }
        }
    }

    /// <summary>Finds a recent preview of a definition.</summary>
    /// <param name="hash">Its hash.</param>
    /// <param name="definitionId">The definition, or <c>null</c> for a new one.</param>
    /// <returns>Its members and removals, or <c>null</c>.</returns>
    public (IReadOnlyList<Guid> Ids, int WouldRemove)? Find(string? hash, Guid? definitionId)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var oldest = _time.GetUtcNow() - Lifetime;
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => string.Equals(e.Hash, hash, StringComparison.Ordinal) && Nullable.Equals(e.DefinitionId, definitionId));
            return entry is null || entry.Utc < oldest ? null : (entry.Ids, entry.WouldRemove);
        }
    }

    /// <summary>
    /// Tells whether members match an accepted preview: the same hash, or a recent preview within 5% of them (at least
    /// one item either way), counting items added and items gone.
    /// </summary>
    /// <param name="accepted">The accepted preview's hash.</param>
    /// <param name="ids">The members now.</param>
    /// <param name="definitionId">The definition, or <c>null</c> for a new one.</param>
    /// <returns>Whether they match.</returns>
    public bool Matches(string? accepted, IReadOnlyList<Guid> ids, Guid? definitionId)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (string.Equals(accepted, MembershipHash.Of(ids), StringComparison.Ordinal))
        {
            return true;
        }

        if (Find(accepted, definitionId) is not { } preview)
        {
            return false;
        }

        var then = preview.Ids.ToHashSet();
        var now = ids.ToHashSet();
        var moved = then.Count(i => !now.Contains(i)) + now.Count(i => !then.Contains(i));
        return moved <= Math.Max(1, (int)Math.Ceiling(then.Count * 0.05));
    }

    private void Drop(string hash, Guid? definitionId)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.Hash, hash, StringComparison.Ordinal) && Nullable.Equals(node.Value.DefinitionId, definitionId))
            {
                _entries.Remove(node);
                return;
            }
        }
    }

    private sealed record Entry(string Hash, IReadOnlyList<Guid> Ids, int WouldRemove, Guid? DefinitionId, DateTimeOffset Utc);
}
