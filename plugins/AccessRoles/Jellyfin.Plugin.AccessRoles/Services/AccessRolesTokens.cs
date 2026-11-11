using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The <c>IfUpdatedAt</c> tokens of roles and assignments (the kit's rule, which Bookmarks' records follow too): the exact
/// <c>UpdatedUtc</c>, or the same time cut to the millisecond (what a JavaScript <c>Date</c> keeps), and every change moves
/// <c>UpdatedUtc</c> to a later millisecond, so no earlier token matches it. And the hashed tokens of what a write covers
/// too: a role's members (<see cref="Members"/>), a dry run's effect (<see cref="Preview"/>) and the library
/// sync's plan (<c>SyncPlanToken</c>), each a hash of keyed lines (<see cref="Of"/>) whose lines are remembered for a while,
/// so a 409 on a stale token can name what differs (<see cref="Differing"/>).
/// </summary>
internal static class AccessRolesTokens
{
    /// <summary>The most tokens whose lines are remembered (the oldest forgotten first).</summary>
    public const int RememberedTokens = 512;

    /// <summary>The preview line that says no enabled administrator would see every library.</summary>
    public const string NoFullAdministratorKey = "NoFullAdministrator";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _remembered = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _rememberedOrder = new();

    /// <summary>
    /// Gets whether a client's token matches a stored time.
    /// </summary>
    /// <param name="stored">The record's <c>UpdatedUtc</c>.</param>
    /// <param name="given">The client's token.</param>
    /// <returns><c>true</c> when the record is unchanged since the client read it.</returns>
    public static bool Matches(DateTime stored, DateTime given)
    {
        var a = ToUtc(given).Ticks;
        var b = ToUtc(stored).Ticks;
        return a == b || (a % TimeSpan.TicksPerMillisecond == 0 && a == b - (b % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>
    /// Gets whether a client's token matches a record that may not exist: no token for no record, the record's time for one.
    /// </summary>
    /// <param name="stored">The record's <c>UpdatedUtc</c>, or <c>null</c> without a record.</param>
    /// <param name="given">The client's token, or <c>null</c>.</param>
    /// <returns><c>true</c> when the client saw what is stored.</returns>
    public static bool MatchesOrNone(DateTime? stored, DateTime? given)
        => stored is { } s ? given is { } g && Matches(s, g) : given is null;

    /// <summary>
    /// Gets the <c>UpdatedUtc</c> of a change: now, but always in a later millisecond than the previous value.
    /// </summary>
    /// <param name="previous">The record's current <c>UpdatedUtc</c>, or <c>null</c> for a new record.</param>
    /// <param name="now">The time of the change, UTC.</param>
    /// <returns>The new value, UTC.</returns>
    public static DateTime Next(DateTime? previous, DateTime now)
    {
        var after = ToUtc(now).Ticks;
        if (previous is not { } before)
        {
            return new DateTime(after, DateTimeKind.Utc);
        }

        var ticks = ToUtc(before).Ticks;
        var earliest = ticks - (ticks % TimeSpan.TicksPerMillisecond) + TimeSpan.TicksPerMillisecond;
        return new DateTime(Math.Max(after, earliest), DateTimeKind.Utc);
    }

    /// <summary>
    /// Gets a role's members and their assignments as one token (<c>MembersToken</c>, <c>ifMembers</c>):
    /// it changes when a member is added or removed, or a member's assignment changes (every change moves its
    /// <c>UpdatedUtc</c>). The token of the members' lines (<see cref="MembersLines"/>).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="roleId">The role.</param>
    /// <returns>The token.</returns>
    public static string Members(AccessRolesDocument document, Guid roleId) => Of(MembersLines(document, roleId));

    /// <summary>
    /// Gets a role's members' lines: each member's id and their assignment's <c>UpdatedUtc</c> in ticks.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="roleId">The role.</param>
    /// <returns>The lines, by member id (<c>N</c> format).</returns>
    public static Dictionary<string, string> MembersLines(AccessRolesDocument document, Guid roleId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in document.Assignments.Where(a => a.RoleIds.Contains(roleId)))
        {
            lines[Key(assignment.UserId)] = ToUtc(assignment.UpdatedUtc).Ticks.ToString(CultureInfo.InvariantCulture);
        }

        return lines;
    }

    /// <summary>
    /// Gets whether a client's members token is the role's now.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="roleId">The role.</param>
    /// <param name="given">The client's token.</param>
    /// <returns><c>true</c> when the members are as the client read them.</returns>
    public static bool MembersMatch(AccessRolesDocument document, Guid roleId, string given)
        => Same(Members(document, roleId), given);

    /// <summary>
    /// Gets what a dry run does as one token (<c>PreviewToken</c>, <c>ifPreview</c>): for each
    /// user the change affects, their state, whether their libraries are managed and written, the library access they end
    /// with, their locked sections and the leave-management choice; and whether no enabled administrator would see every
    /// library. A save given it writes nothing when its own change does anything else (a role it adds, reassigns to or
    /// names was edited, a library went, a user's access changed): 409 <c>Changed</c>.
    /// </summary>
    /// <param name="report">The change's report.</param>
    /// <returns>The token.</returns>
    public static string Preview(ChangeReport report) => Of(PreviewLines(report));

    /// <summary>
    /// Gets a change's preview lines (<see cref="Preview"/>).
    /// </summary>
    /// <param name="report">The change's report.</param>
    /// <returns>The lines, by user id (<c>N</c> format), and <c>NoFullAdministrator</c> when it is so.</returns>
    public static Dictionary<string, string> PreviewLines(ChangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in report.Affected)
        {
            var after = change.After;
            var access = change.AfterEnableAllFolders
                ? "all"
                : string.Join(',', change.AfterEnabledFolders.Select(Key).Distinct().Order(StringComparer.Ordinal));
            var locks = string.Join(',', after.Locked.Select(l => l.ToString()).Order(StringComparer.Ordinal));
            lines[Key(change.User.Id)] = string.Join(
                ';',
                after.State.ToString(),
                after.Libraries.Managed ? "managed" : "-",
                after.Libraries.Writes ? "writes" : "-",
                access,
                locks,
                change.Leaving?.Choice?.ToString() ?? "-");
        }

        if (report.NoFullAdministrator)
        {
            lines[NoFullAdministratorKey] = "1";
        }

        foreach (var extra in report.ExtraLines)
        {
            lines[extra.Key] = extra.Value;
        }

        return lines;
    }

    /// <summary>
    /// Gets a default roles change's own preview lines: the role ids in the order given (key
    /// <c>DefaultRoles</c>), and the definition of each, hashed (key <c>DefaultRole:</c> and its id), so a dry run of other
    /// default roles, or a role among them edited since, is another token. No key is a user id.
    /// </summary>
    /// <param name="document">The document whose roles the ids name.</param>
    /// <param name="ids">The default role ids the change sets.</param>
    /// <returns>The lines.</returns>
    public static Dictionary<string, string> DefaultRolesLines(AccessRolesDocument document, IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ids);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal) { ["DefaultRoles"] = string.Join(',', ids.Select(Key)) };
        foreach (var id in ids)
        {
            if (document.Roles.FirstOrDefault(r => r.Id.Equals(id)) is { } role)
            {
                lines["DefaultRole:" + Key(id)] = Hash(JsonSerializer.Serialize(role));
            }
        }

        return lines;
    }

    /// <summary>
    /// Gets the token of keyed lines (the lines <c>key:value</c> sorted, joined and hashed: <see cref="Hash"/>), and
    /// remembers them for <see cref="Differing"/>.
    /// </summary>
    /// <param name="lines">The lines, by key.</param>
    /// <returns>The token.</returns>
    public static string Of(IReadOnlyDictionary<string, string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var token = Hash(string.Join('\n', lines.Select(p => p.Key + ":" + p.Value).Order(StringComparer.Ordinal)));
        if (_remembered.TryAdd(token, new Dictionary<string, string>(lines, StringComparer.Ordinal)))
        {
            _rememberedOrder.Enqueue(token);
            while (_rememberedOrder.Count > RememberedTokens && _rememberedOrder.TryDequeue(out var oldest))
            {
                _remembered.TryRemove(oldest, out _);
            }
        }

        return token;
    }

    /// <summary>
    /// Gets the keys whose lines differ between a token given back and the lines now: added, gone or changed. A 409 names
    /// those users only, not everyone the change concerns.
    /// </summary>
    /// <param name="given">The token the client sent.</param>
    /// <param name="now">The lines now.</param>
    /// <returns>The keys, or <c>null</c> when the token's lines aren't remembered (another process made it, or too many
    /// tokens were made since).</returns>
    public static IReadOnlySet<string>? Differing(string? given, IReadOnlyDictionary<string, string> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (string.IsNullOrWhiteSpace(given) || !_remembered.TryGetValue(given.Trim().ToLowerInvariant(), out var then))
        {
            return null;
        }

        return then.Keys.Concat(now.Keys)
            .Where(key => !(then.TryGetValue(key, out var a) && now.TryGetValue(key, out var b) && string.Equals(a, b, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets whether a client's token is a token now: the same, ignoring case and surrounding spaces.
    /// </summary>
    /// <param name="token">The token now.</param>
    /// <param name="given">The client's token.</param>
    /// <returns><c>true</c> when they are the same.</returns>
    public static bool Same(string token, string? given) => string.Equals(token, given?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets an id as a line's key.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>It in <c>N</c> format.</returns>
    public static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets a text as a token: the first 16 bytes of its UTF-8 SHA-256, as lowercase hex.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The token.</returns>
    public static string Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
