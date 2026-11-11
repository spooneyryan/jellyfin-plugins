using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// What a Jellyfin Enhanced import is asked to do: <c>Copies</c>, <c>StoreUnmatched</c>,
/// <c>RecreatePurged</c> and the <c>UserMap</c> of JE user ids this server doesn't know.
/// </summary>
/// <param name="DryRun">Whether nothing is written.</param>
/// <param name="Copies"><see cref="First"/> (one copy per review) or <see cref="All"/> (a rating on every copy).</param>
/// <param name="StoreUnmatched">Whether a review whose item isn't in the library is stored detached (it reattaches when the item arrives).</param>
/// <param name="RecreatePurged">Whether a review an admin purged after an earlier import is made again.</param>
/// <param name="UserMap">JE's user id ("N" form) to a user of this server.</param>
internal sealed record JeImportRequest(bool DryRun, string Copies, bool StoreUnmatched, bool RecreatePurged, IReadOnlyDictionary<string, Guid> UserMap)
{
    /// <summary>One copy per review: the one the reviewer can see, then the oldest, then the lowest path.</summary>
    public const string First = "First";

    /// <summary>A rating on every copy that matches.</summary>
    public const string All = "All";

    /// <summary>The most entries a <c>UserMap</c> may hold.</summary>
    public const int MaxUserMap = 1000;

    /// <summary>
    /// Reads the request.
    /// </summary>
    /// <param name="dryRun">The query's dryRun (true when left out).</param>
    /// <param name="body">The body; null for the defaults.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 a bad <c>Copies</c> or <c>UserMap</c>.</exception>
    public static JeImportRequest Parse(bool? dryRun, UserRatingsJeImportRequest? body)
    {
        var copies = body?.Copies?.Trim() switch
        {
            null or "" => First,
            var c when string.Equals(c, First, StringComparison.OrdinalIgnoreCase) => First,
            var c when string.Equals(c, All, StringComparison.OrdinalIgnoreCase) => All,
            _ => throw PluginException.Invalid("Copies must be First or All.")
        };
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (body?.UserMap is { } given)
        {
            if (given.Count > MaxUserMap)
            {
                throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"UserMap can hold at most {MaxUserMap} users."));
            }

            foreach (var (from, to) in given)
            {
                if (string.IsNullOrWhiteSpace(to))
                {
                    continue;
                }

                if (!Guid.TryParse(from?.Trim(), out var jeUser) || !Guid.TryParse(to.Trim(), out var localUser) || jeUser == Guid.Empty || localUser == Guid.Empty)
                {
                    throw PluginException.Invalid("UserMap maps a Jellyfin Enhanced user id to a user id of this server; both must be user ids.");
                }

                map[jeUser.ToString("N", CultureInfo.InvariantCulture)] = localUser;
            }
        }

        return new JeImportRequest(dryRun ?? true, copies, body?.StoreUnmatched ?? true, body?.RecreatePurged ?? false, map);
    }
}
