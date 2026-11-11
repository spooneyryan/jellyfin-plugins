using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A consumer's rule for relinking on a provider-id match. Exact matches (id, move journal, path) are always
/// applied; a single Strong candidate only when <paramref name="AllowStrongRelink"/> is true and
/// <paramref name="Validate"/> accepts it. Otherwise the result is <see cref="ResolveOutcome.Ambiguous"/>.
/// </summary>
/// <remarks>
/// The plugins' policies: Bookmarks <see cref="RuntimeWithin"/> (its records hold positions in one file, and the other
/// copy of a film may be another cut; with <c>requireStoredRuntime</c> for its own records and without it for Jellyfin
/// Enhanced imports, per record through <see cref="IDetachedRecordOwner.GetRelinkPolicy"/>); User Ratings and Smart
/// Search's auto-collections <see cref="Default"/> (a rating or pin names a title); Library Maintenance's ignore marks
/// <see cref="ExactOnly"/> (duplicate copies share provider ids by definition, so the single Strong candidate is the
/// surviving copy).
/// </remarks>
/// <param name="AllowStrongRelink">Whether a single Strong candidate may be applied; false means Exact only.</param>
/// <param name="Validate">Checks the single Strong candidate against the snapshot; null accepts it.</param>
internal sealed record RelinkPolicy(bool AllowStrongRelink = true, Func<IdentitySnapshot, BaseItem, bool>? Validate = null)
{
    /// <summary>The default tolerance of <see cref="RuntimeWithin"/>: 2 % of the stored runtime.</summary>
    public const double DefaultRuntimeTolerance = 0.02;

    /// <summary>The default cap of <see cref="RuntimeWithin"/>: 60 seconds.</summary>
    public static readonly TimeSpan DefaultRuntimeCap = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the default policy: a single Strong candidate is applied without a check.
    /// </summary>
    public static RelinkPolicy Default { get; } = new();

    /// <summary>
    /// Gets the policy that applies Exact matches only; a single Strong candidate is only suggested.
    /// </summary>
    public static RelinkPolicy ExactOnly { get; } = new(AllowStrongRelink: false);

    /// <summary>
    /// Gets a value indicating whether an Exact match (the same id, the journal's end, the same path) whose strong ids
    /// contradict the snapshot's is refused: another film put at a removed film's path gets the same id
    /// back, and a record that names a title must not move onto it. The resolver goes on searching, and leaves the record
    /// <see cref="ResolveOutcome.Ambiguous"/> (<see cref="AmbiguousReason.ConflictingProviderIds"/>) when nothing Strong
    /// is found. False by default, so the policies of 1.1 (Bookmarks) behave as before; User Ratings sets it.
    /// </summary>
    public bool RefuseConflictingExact { get; init; }

    /// <summary>
    /// Creates the policy that applies a single Strong candidate only when its runtime is within
    /// <paramref name="tolerance"/> of the stored one, and at most <paramref name="cap"/> away (Bookmarks: 2 %, at most
    /// 60 s). See <see cref="RuntimeMatches"/> for records without a stored runtime.
    /// </summary>
    /// <param name="tolerance">The allowed difference as a fraction of the stored runtime; 0.02 by default.</param>
    /// <param name="cap">The largest allowed difference; 60 s by default.</param>
    /// <param name="requireStoredRuntime">Whether a record without a stored runtime fails the check (its Strong
    /// candidate is then only suggested: <see cref="AmbiguousReason.FailedValidation"/>) instead of passing it. False
    /// by default, for records that never had a file of their own (a JE import that never attached); true for records
    /// made on one file whose runtime isn't known (a Bookmarks 1.0 bookmark whose video was gone before 1.1 looked it
    /// up).</param>
    /// <returns>The policy.</returns>
    public static RelinkPolicy RuntimeWithin(double tolerance = DefaultRuntimeTolerance, TimeSpan? cap = null, bool requireStoredRuntime = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        var limit = cap ?? DefaultRuntimeCap;
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero, nameof(cap));
        return new RelinkPolicy(
            true,
            (snapshot, item) => (!requireStoredRuntime || snapshot.RunTimeTicks is > 0) && RuntimeMatches(snapshot.RunTimeTicks, item.RunTimeTicks, tolerance, limit));
    }

    /// <summary>
    /// Whether a candidate's runtime is close enough to the stored one: at most <paramref name="tolerance"/> times the
    /// stored runtime and at most <paramref name="cap"/> apart. Without a stored runtime (a JE import that never attached)
    /// any candidate passes; <see cref="RuntimeWithin"/> with <c>requireStoredRuntime</c> fails it first. A candidate whose
    /// runtime isn't known yet fails, because the scan may still be probing it: the record is re-resolved when the item is
    /// updated.
    /// </summary>
    /// <param name="stored">The snapshot's runtime in ticks.</param>
    /// <param name="candidate">The candidate's runtime in ticks.</param>
    /// <param name="tolerance">The allowed difference as a fraction of <paramref name="stored"/>.</param>
    /// <param name="cap">The largest allowed difference.</param>
    /// <returns>Whether the runtimes match.</returns>
    public static bool RuntimeMatches(long? stored, long? candidate, double tolerance, TimeSpan cap)
    {
        if (stored is not > 0)
        {
            return true;
        }

        if (candidate is not > 0)
        {
            return false;
        }

        var allowed = Math.Min(stored.Value * tolerance, cap.Ticks);
        return Math.Abs(candidate.Value - stored.Value) <= allowed;
    }

    /// <summary>
    /// Runs <see cref="Validate"/> on a Strong candidate; true without a validator.
    /// </summary>
    /// <param name="snapshot">The record's snapshot.</param>
    /// <param name="candidate">The candidate.</param>
    /// <returns>Whether the candidate passes.</returns>
    public bool Accepts(IdentitySnapshot snapshot, BaseItem candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);
        return Validate?.Invoke(snapshot, candidate) ?? true;
    }
}
