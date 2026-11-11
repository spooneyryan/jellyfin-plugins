using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// Synthetic store records with every field set, numbered so each is distinct (no real user, library or path).
/// </summary>
internal static class TestDocuments
{
    private static readonly DateTime Start = new(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>Gets a user id of the number.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The id.</returns>
    public static Guid UserId(int number) => new(number, 0x0a1c, 0x4e5f, 0x80, 0x71, 0x82, 0x93, 0xa4, 0xb5, 0xc6, 0xd7);

    /// <summary>Gets a role id of the number.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The id.</returns>
    public static Guid RoleId(int number) => new(number, 0x5b0e, 0x4d2a, 0x9e, 0x8d, 0x1f, 0x2a, 0x3b, 0x4c, 0x5d, 0x6e);

    /// <summary>Gets a library id of the number.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The id.</returns>
    public static Guid LibraryId(int number) => new(number, 0x3c1d, 0x4f8e, 0x8e, 0x9f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f);

    /// <summary>
    /// Creates a role with locks, an <c>Only</c> grant and a description.
    /// </summary>
    /// <param name="number">The number.</param>
    /// <returns>The role.</returns>
    public static AccessRole Role(int number) => new()
    {
        Id = RoleId(number),
        Name = "Role " + number + " – Kinder, café ✓",
        Description = number % 2 == 0 ? "Description " + number : null,
        Locks = [AccessRolesLock.Profile, AccessRolesLock.Home, (number % 3) switch { 0 => AccessRolesLock.AdminSurfaces, 1 => AccessRolesLock.UserDirectory, _ => AccessRolesLock.QuickConnect }],
        Libraries = new RoleLibraries
        {
            Mode = number % 4 == 0 ? AccessRolesRoleLibraryMode.AllExcept : AccessRolesRoleLibraryMode.Only,
            Refs = [new LibraryReference { Id = LibraryId(number), NameAtSelection = "Library " + number, Locations = [@"C:\Media\Library " + number], Orphaned = number % 5 == 0 }]
        },
        CreatedUtc = Start.AddMinutes(number),
        UpdatedUtc = Start.AddMinutes(number + 1)
    };

    /// <summary>
    /// Creates an assignment with roles, an override and library lists.
    /// </summary>
    /// <param name="number">The number.</param>
    /// <returns>The assignment.</returns>
    public static RoleAssignment Assignment(int number) => new()
    {
        UserId = UserId(number),
        RoleIds = [RoleId(number / 3), RoleId((number / 3) + 1)],
        LockOverrides = new Dictionary<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>>
        {
            [AccessRolesLock.QuickConnect] = number % 2 == 0 ? AccessRolesLockOverride.Unlock : AccessRolesLockOverride.Lock
        },
        Libraries = new AssignmentLibraries
        {
            Mode = (number % 3) switch { 0 => AccessRolesUserLibraryMode.Inherit, 1 => AccessRolesUserLibraryMode.All, _ => AccessRolesUserLibraryMode.Only },
            Add = [LibraryId(number + 1000)],
            Remove = number % 2 == 0 ? [LibraryId(number + 2000)] : [],
            ConfirmNoLibraries = number % 7 == 0
        },
        State = number % 10 == 9 ? AccessRolesAssignmentState.Excluded : AccessRolesAssignmentState.Managed,
        UpdatedUtc = Start.AddSeconds(number)
    };

    /// <summary>
    /// Creates a known library.
    /// </summary>
    /// <param name="number">The number.</param>
    /// <returns>The library.</returns>
    public static KnownLibrary Library(int number) => new()
    {
        Id = LibraryId(number),
        Name = "Library " + number,
        Locations = [@"C:\Media\Library " + number],
        Enabled = number % 6 != 0,
        CreatedUtc = number % 4 == 0 ? null : Start.AddDays(-1).AddMinutes(number),
        FirstSeenUtc = Start.AddMinutes(number),
        State = number % 5 == 0 ? AccessRolesLibraryState.Orphaned : AccessRolesLibraryState.Active
    };

    /// <summary>
    /// Creates a ledger entry, with a last write for even numbers.
    /// </summary>
    /// <param name="number">The number.</param>
    /// <returns>The entry.</returns>
    public static LedgerEntry Ledger(int number) => new()
    {
        OriginalEnableAllFolders = number % 2 == 1,
        OriginalEnabledFolders = number % 2 == 1 ? [] : [LibraryId(number), LibraryId(number + 1)],
        OriginalEnableUserPreferenceAccess = true,
        CapturedUtc = Start.AddSeconds(number),
        LastWrite = number % 2 == 0
            ? new LedgerWrite { Utc = Start.AddSeconds(number + 1), EnableAllFolders = false, EnabledFolders = [LibraryId(number)], EnableUserPreferenceAccess = number % 4 == 0 ? false : null }
            : null
    };

    /// <summary>
    /// Adds one assignment (the conformance suite's record), with its ledger entry and the user's name, and every third
    /// number a role and a known library.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="number">The number.</param>
    /// <returns>The new document.</returns>
    public static AccessRolesDocument AddRecord(AccessRolesDocument document, int number)
        => WithRecords(document, [number]);

    /// <summary>
    /// Adds the records of several numbers at once.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="numbers">The numbers.</param>
    /// <returns>The new document.</returns>
    public static AccessRolesDocument WithRecords(AccessRolesDocument document, IReadOnlyList<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(document);
        var ledger = document.Ledger.ToDictionary(p => p.Key, p => p.Value);
        var knownUsers = document.KnownUsers;
        foreach (var number in numbers)
        {
            ledger[UserId(number)] = Ledger(number);
            knownUsers = knownUsers.With(UserId(number), "user" + number);
        }

        var thirds = numbers.Where(n => n % 3 == 0).ToList();
        return document with
        {
            Roles = [.. document.Roles, .. thirds.Select(Role)],
            Assignments = [.. document.Assignments, .. numbers.Select(Assignment)],
            DefaultRoleIds = [.. document.DefaultRoleIds, .. thirds.Take(1).Select(RoleId)],
            KnownLibraries = [.. document.KnownLibraries, .. thirds.Select(Library)],
            PendingRenames = [.. document.PendingRenames, .. numbers.Where(n => n % 50 == 1).Select(n => new PendingRename { OldId = LibraryId(n), OldName = "Library " + n, NewName = "Library " + n + " (renamed)", RequestedUtc = Start.AddHours(n) })],
            Ledger = ledger,
            PreferenceMirror = new Dictionary<Guid, MirroredPreference>(document.PreferenceMirror.Concat(numbers.Where(n => n % 4 == 0).Select(n => new KeyValuePair<Guid, MirroredPreference>(UserId(n), new MirroredPreference { WrittenUtc = Start.AddHours(n) })))),
            KnownUsers = knownUsers
        };
    }
}
