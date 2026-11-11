using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.SmartSearch.Query.Planning;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// One evaluated search in the <see cref="ResultCache"/>: for the Residual path the ordered ids, for the Fast path the plan
/// (its pages are SQL's, a page at a time) and the count once SQL has counted.
/// </summary>
internal sealed class CachedResult
{
    /// <summary>Gets or sets the result's token (set by the cache).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets the user it was evaluated for.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the query's canonical hash.</summary>
    public required string QueryHash { get; init; }

    /// <summary>Gets the now-bucket it was evaluated in (<see cref="ResultCache.Bucket"/>).</summary>
    public required long Bucket { get; init; }

    /// <summary>Gets the user's access it was evaluated with (<see cref="ResultCache.Fingerprint"/>).</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Gets the plan's path.</summary>
    public required QueryPlanPath Path { get; init; }

    /// <summary>Gets the time relative rules counted from.</summary>
    public required DateTime NowUtc { get; init; }

    /// <summary>Gets the libraries the plan searched (the page hydrator checks them again).</summary>
    public required IReadOnlyList<Guid> LibraryIds { get; init; }

    /// <summary>Gets the Residual path's ordered, limited ids.</summary>
    public IReadOnlyList<Guid> OrderedIds { get; init; } = [];

    /// <summary>Gets the Fast path's plan; its candidate query is paged under <see cref="Gate"/>.</summary>
    public QueryPlan? Plan { get; init; }

    /// <summary>Gets or sets the total: the Residual path's result count, or the Fast path's SQL count once read.</summary>
    public int? Count { get; set; }

    /// <summary>Gets the candidates the Residual path checked.</summary>
    public int CandidateCount { get; init; }

    /// <summary>Gets a value indicating whether the result reads the user's data (a per-user rule or sort): the user's
    /// saved user data ends it.</summary>
    public bool ReadsUserData { get; init; }

    /// <summary>Gets or sets the item generation it was stored in.</summary>
    internal long Generation { get; set; }

    /// <summary>Gets or sets the user's generation it was stored in.</summary>
    internal long UserGeneration { get; set; }

    /// <summary>Gets or sets the user's user-data generation it was stored in.</summary>
    internal long UserDataGeneration { get; set; }

    /// <summary>Gets or sets when it was last used.</summary>
    internal DateTime LastUsedUtc { get; set; }

    /// <summary>Gets or sets the cache's use counter when it was last used (the least recently used go first; two uses
    /// in one clock tick are still ordered).</summary>
    internal long LastUse { get; set; }

    private Lock Gate { get; } = new();

    /// <summary>Runs an action under the entry's lock: a Fast page sets the plan's paging and reads its count under it.</summary>
    /// <typeparam name="T">The action's result.</typeparam>
    /// <param name="action">The action.</param>
    /// <returns>Its result.</returns>
    public T Locked<T>(Func<CachedResult, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (Gate)
        {
            return action(this);
        }
    }
}
