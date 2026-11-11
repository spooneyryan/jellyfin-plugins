using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// What a change to the roles and assignments does, user by user, and the refusals a save would
/// meet.
/// </summary>
internal sealed record ChangeReport
{
    /// <summary>Gets every user of the server, by name.</summary>
    public IReadOnlyList<UserChange> Users { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the change leaves no enabled administrator with every library
    /// (<c>LockoutGuard</c>): a save needs <c>confirmNoFullAdmin=true</c>.
    /// </summary>
    public bool NoFullAdministrator { get; init; }

    /// <summary>Gets the users the change does anything for.</summary>
    public IReadOnlyList<UserChange> Affected => [.. Users.Where(u => u.Affected)];

    /// <summary>Gets the users who leave library management without an <c>onUnmanage</c> choice (400 on a save).</summary>
    public IReadOnlyList<UserChange> ChoiceRequired => [.. Users.Where(u => u.Leaving is { Choice: null })];

    /// <summary>Gets the users <c>Restore</c> can't restore: no ledger entry (409 on a save).</summary>
    public IReadOnlyList<UserChange> NoLedgerEntry => [.. Users.Where(u => u.Leaving is { NoLedgerEntry: true })];

    /// <summary>Gets the users the change would leave with no libraries by accident (409 on a save).</summary>
    public IReadOnlyList<UserChange> NoLibraries => [.. Users.Where(u => u.NoLibrariesRefused)];

    /// <summary>
    /// Gets the lines a write adds to its preview's token for what it changes besides users' access: the default roles and
    /// their definitions (without them, a change of no existing user's access was the token of nothing).
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtraLines { get; init; } = new Dictionary<string, string>();
}
