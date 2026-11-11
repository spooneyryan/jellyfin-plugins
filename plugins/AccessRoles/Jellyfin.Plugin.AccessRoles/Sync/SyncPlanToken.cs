using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Services;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What a library sync run writes, as one token (for the admin's <i>Revert</i> and the confirmed sync): the users it writes
/// (or releases), the library access it writes for each and the native access it writes over. A dry run
/// answers it as <c>PlanToken</c>, and a real run given it as <c>ifPlan</c> writes nothing when its own plan is another
/// (409 <c>Changed</c>), so the admin's confirmation covers exactly the writes the dry run listed, over what it showed.
/// </summary>
internal static class SyncPlanToken
{
    /// <summary>
    /// Gets the library fields a planned action writes, or <c>null</c> for one that writes nothing.
    /// </summary>
    /// <param name="action">The planned action (or its result).</param>
    /// <returns>The fields.</returns>
    public static LibraryFields? Written(SyncUserResult action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            SyncActionKind.Write or SyncActionKind.Release => action.Target,
            SyncActionKind.Drift => action.DriftDecision?.Write,
            _ => null
        };
    }

    /// <summary>
    /// Gets the token of a plan (the token of its <see cref="Lines"/>, remembered so a stale one can name who differs).
    /// </summary>
    /// <param name="plan">The planned actions (a dry run's report, or a run's plan before it writes).</param>
    /// <param name="scope">The one user the run is for, or <c>null</c> for everyone.</param>
    /// <returns>The token.</returns>
    public static string Of(IEnumerable<SyncUserResult> plan, Guid? scope = null) => AccessRolesTokens.Of(Lines(plan, scope));

    /// <summary>
    /// Gets a plan's lines: for each user it writes (or releases), the action, the library access written and the user's
    /// native access when the run read it.
    /// </summary>
    /// <param name="plan">The planned actions.</param>
    /// <param name="scope">
    /// The one user the run is for, or <c>null</c>: a run for one user has a line of its scope too (key <c>scope</c>, never a
    /// user id), so its token is never a token of a run for everyone or for another user.
    /// </param>
    /// <returns>The lines, by user id (<c>N</c> format).</returns>
    public static Dictionary<string, string> Lines(IEnumerable<SyncUserResult> plan, Guid? scope = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        if (scope is { } user)
        {
            lines["scope"] = "user:" + AccessRolesTokens.Key(user);
        }

        foreach (var action in plan)
        {
            if (Written(action) is not { } fields)
            {
                continue;
            }

            // The user's native access as the run read it too: a native change between the dry
            // run and the write (another administrator's edit on the stock page) is another plan, though the roles' result
            // is the same, so Revert, Revert for everyone and the hold's Resume never write over access the admin didn't see.
            lines[AccessRolesTokens.Key(action.User.Id)] = action.Kind + ":" + Access(fields) + "|native:" + Access(action.Native);
        }

        return lines;
    }

    /// <summary>
    /// Gets the users a plan writes otherwise than a confirmed token's plan did (so a 409 names only
    /// them): a user it writes now that the confirmed plan didn't, one it no longer writes, one it writes other access for or
    /// over another native access.
    /// </summary>
    /// <param name="confirmed">The confirmed token.</param>
    /// <param name="plan">The plan now.</param>
    /// <param name="scope">The one user the run is for, or <c>null</c>.</param>
    /// <returns>The users, or <c>null</c> when the confirmed plan isn't remembered.</returns>
    public static IReadOnlySet<Guid>? Differing(string confirmed, IEnumerable<SyncUserResult> plan, Guid? scope = null)
        => AccessRolesTokens.Differing(confirmed, Lines(plan, scope)) is { } keys
            ? keys.Select(key => Guid.TryParseExact(key, "N", out var id) ? id : Guid.Empty).Where(id => !id.Equals(Guid.Empty)).ToHashSet()
            : null;

    private static string Access(LibraryFields fields) => fields.EnableAllFolders
        ? "all"
        : string.Join(',', fields.EnabledFolders.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
}
