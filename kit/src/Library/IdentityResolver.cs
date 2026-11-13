using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Finds the library item an <see cref="IdentitySnapshot"/> belongs to now, for a consumer
/// that re-resolves a detached record or imports one.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> (1) <c>GetItemById</c> of the snapshot's id (cache-backed), only when every parent up the item's
/// chain is there too (<see cref="LibraryPresence"/>: on 10.11.5 a deleted music artist folder's albums and tracks stay
/// in the item cache); (2) the move journal: the chain of Committed lines from that id committed after the record was
/// attached (<see cref="MoveJournal.Chain"/>), its end by id (with the same parent check), else the last line's
/// <c>NewPath</c> by path, else, within <see cref="MoveJournal.PendingWindow"/> of the last line, a pending relink to
/// the end id; (3) <c>FindByPath</c> of the snapshot's path; (4) provider ids: items of the same kind sharing one of
/// the kind's strong ids (<see cref="ItemIdentity.StrongProviders"/>), and for episodes and seasons the items with the
/// same numbers under a series that shares one of the series' ids; (5) only when nothing Strong was found, the same
/// kind, name and year (for episodes and seasons, the series name and the numbers), as Weak suggestions. Steps 1–3 are
/// Exact and end the search.</para>
/// <para><b>Paths.</b> A blank path is never looked up: 10.11.5's <c>FindByPath</c> puts no path filter on its query for
/// one and answers with the newest item of the whole library. The item <c>FindByPath</c> returns (the newest at that path)
/// counts only when it has the expected kind: the snapshot's, or for the journal's <c>NewPath</c> the line's <c>Kind</c>
/// when given. A folder or an item of another kind at that path is ignored, and without an expected kind the step finds
/// nothing.</para>
/// <para><b>Decision.</b> Exact always relinks. A single Strong candidate relinks when the consumer's
/// <see cref="RelinkPolicy"/> allows Strong relinks and its validator accepts it; several Strong candidates, a policy
/// that allows Exact only, or a failed validation give <see cref="ResolveOutcome.Ambiguous"/>. A candidate that shares
/// one strong id but has a different value for another (a wrong scrape, or another film) is only Weak. Weak candidates are
/// never applied.</para>
/// <para><b>Conflicting ids</b>. With <see cref="RelinkPolicy.RefuseConflictingExact"/>, an Exact match
/// whose strong ids contradict the snapshot's (another film at the same path, which gets the same id back) is not applied:
/// the search goes on, so a single Strong candidate elsewhere (the film itself, moved) still relinks, and when nothing
/// Strong is found the result is <see cref="ResolveOutcome.Ambiguous"/> (<see cref="AmbiguousReason.ConflictingProviderIds"/>)
/// with that item listed as a Weak candidate. An item without strong ids yet (just added, not scraped) contradicts
/// nothing. Without the flag (Bookmarks, the policies of 1.1) Exact always relinks, as before.</para>
/// <para><b>Whose record.</b> Candidates are searched server-wide, without a user. A consumer whose records belong to a
/// user (Bookmarks, User Ratings' own ratings) passes <c>isVisible</c> (<see cref="VisibleTo"/> for the owner): an item
/// it rejects is treated as not found at every step, so the result never relinks to, lists or counts an item the owner
/// can't see (the kit's privacy rule: a user's record holds only ids they stored while they had access). A pending
/// relink has no item to check yet; see <see cref="ResolveResult.IsPending"/>.</para>
/// <para>Virtual items (missing episodes) are never candidates. Nothing here writes; the consumer applies the result.
/// <c>GetItemById(Guid.Empty)</c> throws in 10.11.5, so an empty id skips steps 1 and 2.</para>
/// </remarks>
internal sealed class IdentityResolver
{
    /// <summary>The most Strong candidates a result lists.</summary>
    public const int MaxStrongCandidates = 20;

    /// <summary>The most Weak candidates a result lists (User Ratings shows up to 5 candidates).</summary>
    public const int MaxWeakCandidates = 5;

    // The most items one provider-id or name query returns; more than enough to tell one candidate from several.
    private const int QueryLimit = 50;

    private readonly ILibraryManager _library;
    private readonly MoveJournalReader _moves;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityResolver"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="moves">The move-journal reader.</param>
    /// <param name="timeProvider">The clock for the pending window; the system clock by default.</param>
    public IdentityResolver(ILibraryManager library, MoveJournalReader moves, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(moves);

        _library = library;
        _moves = moves;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the visibility check of a record's owner for <see cref="Resolve"/>: an item counts only when the library still
    /// has it and every parent up its chain (<see cref="LibraryPresence"/>) and
    /// <c>GetItemById&lt;BaseItem&gt;(id, owner)</c> finds it, which checks the owner's libraries and parental settings.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="owner">The user the record belongs to.</param>
    /// <returns>The check.</returns>
    public static Func<BaseItem, bool> VisibleTo(ILibraryManager library, User owner)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(owner);
        return item => LibraryPresence.IsInLibrary(library, item) && library.GetItemById<BaseItem>(item.Id, owner) is not null;
    }

    /// <summary>
    /// Resolves a snapshot against the library and the move journal.
    /// </summary>
    /// <param name="snapshot">The record's snapshot.</param>
    /// <param name="policy">The consumer's relink policy.</param>
    /// <param name="journal">The move journal already read (<see cref="MoveJournalReader.Read"/>), for a batch of
    /// records; null reads the current one.</param>
    /// <param name="attachedUtc">When the record got <see cref="IdentitySnapshot.ItemId"/>: when it was written on the item
    /// or relinked to it, or, when a journal line moved it there, that line's <c>Utc</c>. Journal lines committed at or
    /// before it are not followed, because a path used again gives an old id again (<see cref="MoveJournal.Chain"/>). Null
    /// follows the whole journal, for records that never stored the time.</param>
    /// <param name="isVisible">For a record that belongs to a user, whether that user can see an item
    /// (<see cref="VisibleTo"/>); null for server-wide records.</param>
    /// <returns>The result.</returns>
    public ResolveResult Resolve(
        IdentitySnapshot snapshot,
        RelinkPolicy policy,
        MoveJournal? journal = null,
        DateTime? attachedUtc = null,
        Func<BaseItem, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        journal ??= _moves.Read();
        bool Visible(BaseItem item) => isVisible is null || isVisible(item);

        // An Exact match whose strong ids contradict the snapshot's, when the policy refuses it: the first one is kept to
        // list, and the search goes on.
        ResolveCandidate? refused = null;
        bool Accept(BaseItem item, MatchSource source)
        {
            if (!policy.RefuseConflictingExact || !ConflictsWith(snapshot, item))
            {
                return true;
            }

            refused ??= new ResolveCandidate(item, MatchStrength.Weak, source, null);
            return false;
        }

        // 1. The same id: the item is still there, and every parent up its chain (not a cache ghost).
        var hasId = snapshot.ItemId != Guid.Empty;
        var same = hasId ? LibraryPresence.Find(_library, snapshot.ItemId) : null;
        if (same is not null && Visible(same) && Accept(same, MatchSource.Id))
        {
            return Exact(same, MatchSource.Id, null);
        }

        // 2. The move journal (Committed lines only, chained in Seq order, from the time the record was attached).
        MoveChain? chain = null;
        if (hasId && journal.Status == MoveJournalStatus.Ok)
        {
            var followed = journal.Chain(snapshot.ItemId, afterUtc: attachedUtc);
            if (followed.Last is { } last)
            {
                chain = followed;
                var moved = followed.EndId == snapshot.ItemId ? same : LibraryPresence.Find(_library, followed.EndId);
                if (moved is not null && Visible(moved) && Accept(moved, MatchSource.Journal))
                {
                    return Exact(moved, MatchSource.Journal, chain);
                }

                var atNewPath = FindByPath(last.NewPath, last.Kind ?? snapshot.Kind);
                if (atNewPath is not null && Visible(atNewPath) && Accept(atNewPath, MatchSource.Journal))
                {
                    return Exact(atNewPath, MatchSource.Journal, chain);
                }

                // Pending only while the library has nothing at the end of the chain, not when it has an item there that
                // the owner can't see.
                if (moved is null && atNewPath is null && _timeProvider.GetUtcNow().UtcDateTime - last.Utc < MoveJournal.PendingWindow)
                {
                    return new ResolveResult
                    {
                        Outcome = ResolveOutcome.Relink,
                        Strength = MatchStrength.Exact,
                        Source = MatchSource.Journal,
                        ItemId = followed.EndId,
                        IsPending = true,
                        Chain = chain
                    };
                }
            }
        }

        // 3. The same path.
        if (FindByPath(snapshot.Path, snapshot.Kind) is { } atPath && Visible(atPath) && Accept(atPath, MatchSource.Path))
        {
            return Exact(atPath, MatchSource.Path, chain);
        }

        // 4. Provider ids, and 5. names.
        return Decide(snapshot, policy, FindCandidates(snapshot, isVisible), chain, refused);
    }

    /// <summary>
    /// Whether an item's strong ids contradict a snapshot's (<see cref="RelinkPolicy.RefuseConflictingExact"/>): both have
    /// a strong id of the same provider, and none of those they both have agrees.
    /// </summary>
    /// <param name="snapshot">The record's snapshot.</param>
    /// <param name="item">The item.</param>
    /// <returns>Whether they contradict.</returns>
    internal static bool ConflictsWith(IdentitySnapshot snapshot, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(item);
        var own = ItemIdentity.StrongIds(snapshot);
        return own.Count > 0 && Compare(own, item.ProviderIds) == MatchStrength.None && Contradicts(own, item.ProviderIds);
    }

    /// <summary>
    /// Compares an item's provider ids with a snapshot's strong ids, as the provider-id step does: Strong when it shares one
    /// and contradicts none, Weak when it shares one but has another value for another, None when it shares none (or the
    /// snapshot has none). For a consumer that judges an item it found itself (User Ratings' held ratings).
    /// </summary>
    /// <param name="snapshot">The record's snapshot.</param>
    /// <param name="item">The item.</param>
    /// <returns>The strength.</returns>
    internal static MatchStrength CompareIds(IdentitySnapshot snapshot, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(item);
        var own = ItemIdentity.StrongIds(snapshot);
        return own.Count == 0 ? MatchStrength.None : Compare(own, item.ProviderIds);
    }

    private static ResolveResult Exact(BaseItem item, MatchSource source, MoveChain? chain) => new()
    {
        Outcome = ResolveOutcome.Relink,
        Strength = MatchStrength.Exact,
        Source = source,
        ItemId = item.Id,
        Item = item,
        Chain = chain
    };

    private static ResolveResult Decide(IdentitySnapshot snapshot, RelinkPolicy policy, List<ResolveCandidate> found, MoveChain? chain, ResolveCandidate? refused = null)
    {
        var strong = found.Where(c => c.Strength == MatchStrength.Strong)
            .Select(c => c with { PassesValidation = policy.Accepts(snapshot, c.Item) })
            .ToList();
        var weak = found.Where(c => c.Strength == MatchStrength.Weak).Take(MaxWeakCandidates).ToList();
        IReadOnlyList<ResolveCandidate> listed = [.. strong.Take(MaxStrongCandidates), .. weak];

        if (strong.Count == 0 && refused is not null)
        {
            // An Exact match refused for its conflicting ids, and nothing Strong elsewhere: left for the admin.
            return new ResolveResult
            {
                Outcome = ResolveOutcome.Ambiguous,
                Strength = MatchStrength.Weak,
                Source = refused.Source,
                Reason = AmbiguousReason.ConflictingProviderIds,
                Candidates = [refused, .. weak.Where(w => w.ItemId != refused.ItemId).Take(MaxWeakCandidates - 1)],
                Chain = chain
            };
        }

        if (strong.Count == 0)
        {
            return new ResolveResult
            {
                Outcome = ResolveOutcome.Unmatched,
                Strength = weak.Count > 0 ? MatchStrength.Weak : MatchStrength.None,
                Source = weak.Count > 0 ? weak[0].Source : MatchSource.None,
                Candidates = listed,
                Chain = chain
            };
        }

        var single = strong[0];
        var reason = strong.Count > 1 ? AmbiguousReason.SeveralStrongCandidates
            : !policy.AllowStrongRelink ? AmbiguousReason.StrongRelinkNotAllowed
            : single.PassesValidation != true ? AmbiguousReason.FailedValidation
            : AmbiguousReason.None;
        if (reason != AmbiguousReason.None)
        {
            return new ResolveResult
            {
                Outcome = ResolveOutcome.Ambiguous,
                Strength = MatchStrength.Strong,
                Source = single.Source,
                Reason = reason,
                Candidates = listed,
                Chain = chain
            };
        }

        return new ResolveResult
        {
            Outcome = ResolveOutcome.Relink,
            Strength = MatchStrength.Strong,
            Source = single.Source,
            ItemId = single.ItemId,
            Item = single.Item,
            Candidates = listed,
            Chain = chain
        };
    }

    /// <summary>
    /// Compares a candidate's provider ids with the ids it was searched by: Strong when it shares one and contradicts
    /// none, Weak when it shares one but has another value for another, None when it shares none.
    /// </summary>
    private static MatchStrength Compare(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string>? actual)
    {
        if (actual is null)
        {
            return MatchStrength.None;
        }

        bool shared = false, contradicts = false;
        foreach (var (provider, value) in expected)
        {
            if (ItemIdentity.TryGet(actual, provider, out var other))
            {
                if (string.Equals(value, other, StringComparison.OrdinalIgnoreCase))
                {
                    shared = true;
                }
                else
                {
                    contradicts = true;
                }
            }
        }

        return !shared ? MatchStrength.None : contradicts ? MatchStrength.Weak : MatchStrength.Strong;
    }

    private static bool Contradicts(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string>? actual)
        => actual is not null
            && expected.Any(e => ItemIdentity.TryGet(actual, e.Key, out var other) && !string.Equals(e.Value, other, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseKind(string kind, out BaseItemKind value)
    {
        value = default;
        return kind.Length > 0 && char.IsLetter(kind[0]) && Enum.TryParse(kind, ignoreCase: false, out value) && Enum.IsDefined(value);
    }

    private static InternalItemsQuery NewQuery(BaseItemKind kind) => KitItemQueries.IncludeEveryVersion(new()
    {
        IncludeItemTypes = [kind],
        Recursive = true,
        IsVirtualItem = false,
        GroupByPresentationUniqueKey = false,
        Limit = QueryLimit,
        OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.DateCreated, SortOrder.Ascending)],
        DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false }
    });

    /// <summary>
    /// Finds the item at a path, provided it has the expected kind. A blank path is never looked up, and without an
    /// expected kind nothing counts.
    /// </summary>
    private BaseItem? FindByPath(string? path, string? kind)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        return KitItemQueries.FindAnyByPath(_library, path, null) is { } item && string.Equals(ItemIdentity.KindOf(item), kind, StringComparison.Ordinal)
            ? item
            : null;
    }

    private List<ResolveCandidate> FindCandidates(IdentitySnapshot snapshot, Func<BaseItem, bool>? isVisible)
    {
        var found = new List<ResolveCandidate>();
        if (!TryParseKind(snapshot.Kind, out var kind))
        {
            return found;
        }

        // An item the owner can't see is never listed or counted, and is asked about once.
        var seen = new HashSet<Guid> { snapshot.ItemId };
        void Add(BaseItem item, MatchStrength strength, MatchSource source)
        {
            if (strength != MatchStrength.None && seen.Add(item.Id) && (isVisible is null || isVisible(item)))
            {
                found.Add(new ResolveCandidate(item, strength, source, null));
            }
        }

        // The item's own strong ids.
        var ownIds = ItemIdentity.StrongIds(snapshot);
        if (ownIds.Count > 0)
        {
            foreach (var item in ByProviderIds(kind, ownIds))
            {
                Add(item, Compare(ownIds, item.ProviderIds), MatchSource.ProviderIds);
            }
        }

        // Episodes and seasons: the series' strong ids plus the numbers.
        var seriesIds = ItemIdentity.StrongSeriesIds(snapshot);
        if (seriesIds.Count > 0 && HasNumbers(kind, snapshot))
        {
            foreach (var series in ByProviderIds(BaseItemKind.Series, seriesIds))
            {
                var seriesStrength = Compare(seriesIds, series.ProviderIds);
                if (seriesStrength == MatchStrength.None)
                {
                    continue;
                }

                foreach (var child in ByNumbers(kind, series.Id, snapshot))
                {
                    var strength = seriesStrength == MatchStrength.Strong && Contradicts(ownIds, child.ProviderIds) ? MatchStrength.Weak : seriesStrength;
                    Add(child, strength, MatchSource.SeriesNumbers);
                }
            }
        }

        // Only when nothing Strong was found: the same name (and year, or numbers), as suggestions.
        if (!found.Any(c => c.Strength == MatchStrength.Strong))
        {
            foreach (var item in ByName(kind, snapshot))
            {
                Add(item, MatchStrength.Weak, MatchSource.Name);
            }
        }

        return found;
    }

    private static bool HasNumbers(BaseItemKind kind, IdentitySnapshot snapshot)
        => (kind == BaseItemKind.Episode && snapshot.Season is not null && snapshot.Episode is not null)
            || (kind == BaseItemKind.Season && snapshot.Season is not null);

    private IReadOnlyList<BaseItem> ByProviderIds(BaseItemKind kind, IReadOnlyDictionary<string, string> ids)
    {
        var query = NewQuery(kind);

        // Assigned, never initialised in place: the property is null until set.
        query.HasAnyProviderId = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase);
        return _library.GetItemList(query);
    }

    private IReadOnlyList<BaseItem> ByNumbers(BaseItemKind kind, Guid seriesId, IdentitySnapshot snapshot)
    {
        var query = NewQuery(kind);
        query.AncestorIds = [seriesId];
        if (kind == BaseItemKind.Episode)
        {
            query.ParentIndexNumber = snapshot.Season;
            query.IndexNumber = snapshot.Episode;
        }
        else
        {
            query.IndexNumber = snapshot.Season;
        }

        return _library.GetItemList(query);
    }

    private IEnumerable<BaseItem> ByName(BaseItemKind kind, IdentitySnapshot snapshot)
    {
        if (kind is BaseItemKind.Episode or BaseItemKind.Season)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SeriesName) || !HasNumbers(kind, snapshot))
            {
                return [];
            }

            var seriesQuery = NewQuery(BaseItemKind.Series);
            seriesQuery.Name = snapshot.SeriesName;
            seriesQuery.Limit = MaxWeakCandidates;
            return _library.GetItemList(seriesQuery).SelectMany(series => ByNumbers(kind, series.Id, snapshot)).Take(MaxWeakCandidates + 1).ToList();
        }

        if (string.IsNullOrWhiteSpace(snapshot.Name))
        {
            return [];
        }

        var query = NewQuery(kind);
        query.Name = snapshot.Name;
        query.Years = snapshot.Year is { } year ? [year] : [];
        query.Limit = MaxWeakCandidates + 1;
        return _library.GetItemList(query);
    }
}
