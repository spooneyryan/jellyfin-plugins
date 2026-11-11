using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Works out a definition's members: the query evaluated in the server's context, then its pins and excluded items.
/// </summary>
/// <remarks>
/// <para><b>Whose view.</b> No searching user: viewers are filtered by the server when they browse the collection. The
/// query reads only the libraries its creator can see (read again at every run, so narrowing the creator's access narrows
/// the collection), never a boxsets library; with an evaluation user (a query that reads watch state), that user's
/// access applies on top, their parental limits included. Ratings are read as the evaluation user, else the creator.</para>
/// <para><b>Order.</b> The query's result in its own order, without the excluded items, then cut to its <c>Limit</c> (so
/// "the top 10" with one item excluded still has 10: the 11th moves up); the pins that aren't in it follow, in the order
/// they were pinned, when they still exist inside the creator's libraries (and the evaluation user's) and are of a kind a
/// collection holds (a movie, show, episode, music video or video; never a collection or a season). Excluding wins over
/// pinning. Past the cap the whole run is refused (<see cref="AutoCollectionReasons.TooManyMembers"/>); a collection is
/// never cut short silently.</para>
/// </remarks>
internal sealed class MembershipPlanner
{
    private readonly ISmartSearchEngine _engine;
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="MembershipPlanner"/> class.
    /// </summary>
    /// <param name="engine">The query engine.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager (the kinds of pinned items).</param>
    public MembershipPlanner(ISmartSearchEngine engine, LibraryAccess access, ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(library);
        _engine = engine;
        _access = access;
        _library = library;
    }

    /// <summary>Works out a definition's members.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="check">Its passed check: the query, the creator and the evaluation user.</param>
    /// <param name="purpose">The engine slot: the worker's, or a preview's.</param>
    /// <param name="nowUtc">The pass's time (relative dates), or <c>null</c> for now.</param>
    /// <param name="maxMembers">The cap on members.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <param name="itemCache">The candidates already loaded in this pass, or <c>null</c>.</param>
    /// <returns>The members.</returns>
    /// <exception cref="SmartSearchRefusedException">The engine refused the query (too broad, a field unavailable, busy, timed out).</exception>
    public async Task<DesiredMembership> PlanAsync(AutoCollectionDefinition definition, AutoCollectionCheck check, EvaluationPurpose purpose, DateTime? nowUtc, int maxMembers, CancellationToken cancellationToken, PassItemCache? itemCache = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(check);
        if (!check.Ok || check.Query is null || check.Creator is null)
        {
            throw new ArgumentException("Only a definition that passed its check is planned.", nameof(check));
        }

        var creator = check.Creator;
        var evaluationUser = check.EvaluationUser;
        var creatorLibraries = Usable(_access.VisibleLibraries(creator));
        var context = new EvaluationContext(
            purpose,
            evaluationUser,
            creatorLibraries.Select(l => l.Id).ToList(),
            evaluationUser?.Id ?? creator.Id,
            nowUtc,
            ItemCache: itemCache);
        var excluded = Active(definition.ManualEdits.Excluded).ToHashSet();

        // The limit applies after the excluded items are out: the engine returns the whole ordered result when some are.
        var limit = check.Query.Limit;
        var query = excluded.Count > 0 && limit is not null ? WithoutLimit(check.Query) : check.Query;
        var evaluation = await _engine.EvaluateAllAsync(query, context, cancellationToken).ConfigureAwait(false);

        var ids = new List<Guid>(Math.Min(evaluation.OrderedIds.Count, limit ?? int.MaxValue));
        var seen = new HashSet<Guid>();
        var excludedCount = 0;
        foreach (var id in evaluation.OrderedIds)
        {
            if (ids.Count >= limit)
            {
                break;
            }

            if (excluded.Contains(id))
            {
                excludedCount++;
            }
            else if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        var queryCount = ids.Count + excludedCount;
        var pins = Active(definition.ManualEdits.Pinned).Where(id => !excluded.Contains(id) && !seen.Contains(id)).Distinct().ToList();
        var pinsAdded = 0;
        if (pins.Count > 0)
        {
            var pinLibraries = evaluationUser is null
                ? creatorLibraries
                : creatorLibraries.Where(l => _access.VisibleLibraries(evaluationUser).Any(v => v.Id.Equals(l.Id))).ToList();
            var eligible = _access.EligibleIds(evaluationUser, pinLibraries, pins);
            foreach (var pin in pins.Where(id => eligible.Contains(id) && IsMemberKind(id)))
            {
                ids.Add(pin);
                pinsAdded++;
            }
        }

        var error = ids.Count > maxMembers ? AutoCollectionReasons.TooManyMembers : null;
        return new DesiredMembership(ids, queryCount, evaluation.CandidateCount, pinsAdded, pins.Count - pinsAdded, excludedCount, evaluation.Plan.Libraries.Select(l => l.Id).ToList(), error, evaluation)
        {
            SourceLibraryIds = creatorLibraries.Select(l => l.Id).ToList()
        };
    }

    // The query as it is, without its limit (a shallow copy: the parts are only read).
    private static SmartSearchQueryDefinition WithoutLimit(SmartSearchQueryDefinition query) => new()
    {
        Version = query.Version,
        Scope = query.Scope,
        Where = query.Where,
        Sort = query.Sort,
        Limit = null,
        Extra = query.Extra
    };

    // Whether a pin is of a kind a collection holds: a library can hold others (a legacy collection folder in a movies
    // library, a season), which the query's own kinds never return.
    private bool IsMemberKind(Guid id)
    {
        try
        {
            return _library.GetItemById(id) is { } item && AutoCollectionChecks.MemberKinds.Contains(item.GetBaseItemKind());
        }
        catch (InvalidOperationException)
        {
            // A row the server can't make an item of (an unknown type): not a member.
            return false;
        }
    }

    // The libraries a collection may draw from: never a boxsets library (collections hold no collections).
    private static List<CollectionFolder> Usable(IEnumerable<CollectionFolder> libraries)
        => libraries.Where(l => l.CollectionType != CollectionType.boxsets).ToList();

    // The entries in force: those that weren't detached.
    private static IEnumerable<Guid> Active(IEnumerable<AutoCollectionItemEntry>? entries)
        => (entries ?? []).Where(e => e.DetachedUtc is null && !e.ItemId.Equals(Guid.Empty)).Select(e => e.ItemId);
}
