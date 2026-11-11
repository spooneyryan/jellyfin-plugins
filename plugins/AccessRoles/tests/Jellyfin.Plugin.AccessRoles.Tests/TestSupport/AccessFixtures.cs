using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// Synthetic libraries, users, roles and assignments for the resolver and change tests (no real user, library or path):
/// five libraries (Movies, Kids, Shows, Horror, Collections), users by number, roles by number.
/// </summary>
internal static class AccessFixtures
{
    public static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public static readonly CatalogLibrary Movies = Library(1, "Movies", "movies");
    public static readonly CatalogLibrary Kids = Library(2, "Kids", "movies");
    public static readonly CatalogLibrary Shows = Library(3, "Shows", "tvshows");
    public static readonly CatalogLibrary Horror = Library(4, "Horror", "movies");
    public static readonly CatalogLibrary Collections = Library(5, "Collections", "boxsets");

    public static LibraryCatalog Catalog { get; } = new([Movies, Kids, Shows, Horror, Collections]);

    public static CatalogLibrary Library(int number, string name, string? type = null, bool enabled = true, DateTime? created = null)
        => new(TestDocuments.LibraryId(number), name, [@"C:\Media\" + name], enabled, type, created);

    public static NativeAccess AllFolders { get; } = new(true, [], true, false, []);

    public static NativeAccess Folders(params CatalogLibrary[] libraries) => new(false, [.. libraries.Select(l => l.Id)], true, false, []);

    public static ServerUser User(int number, string name, bool admin = false, NativeAccess? native = null, bool disabled = false)
        => new(TestDocuments.UserId(number), name, admin, disabled, false, native ?? AllFolders);

    public static AccessRole Role(int number, string name, IEnumerable<AccessRolesLock>? locks = null, AccessRolesRoleLibraryMode mode = AccessRolesRoleLibraryMode.NotManaged, params CatalogLibrary[] refs)
        => new()
        {
            Id = TestDocuments.RoleId(number),
            Name = name,
            Locks = [.. (locks ?? []).Select(LenientEnum<AccessRolesLock>.From)],
            Libraries = new RoleLibraries { Mode = mode, Refs = [.. refs.Select(r => new LibraryReference { Id = r.Id, NameAtSelection = r.Name, Locations = r.Locations })] },
            CreatedUtc = Now.AddDays(-1),
            UpdatedUtc = Now.AddDays(-1)
        };

    public static RoleAssignment Assign(
        ServerUser user,
        IEnumerable<AccessRole>? roles = null,
        AccessRolesUserLibraryMode mode = AccessRolesUserLibraryMode.Inherit,
        IEnumerable<CatalogLibrary>? add = null,
        IEnumerable<CatalogLibrary>? remove = null,
        IReadOnlyDictionary<AccessRolesLock, AccessRolesLockOverride>? overrides = null,
        AccessRolesAssignmentState state = AccessRolesAssignmentState.Managed,
        bool confirmNoLibraries = false)
        => new()
        {
            UserId = user.Id,
            RoleIds = [.. (roles ?? []).Select(r => r.Id)],
            LockOverrides = (overrides ?? ImmutableDictionary<AccessRolesLock, AccessRolesLockOverride>.Empty)
                .ToImmutableDictionary(p => LenientEnum<AccessRolesLock>.From(p.Key), p => LenientEnum<AccessRolesLockOverride>.From(p.Value)),
            Libraries = new AssignmentLibraries
            {
                Mode = mode,
                Add = [.. (add ?? []).Select(l => l.Id)],
                Remove = [.. (remove ?? []).Select(l => l.Id)],
                ConfirmNoLibraries = confirmNoLibraries
            },
            State = state,
            UpdatedUtc = Now.AddHours(-1)
        };

    public static AccessRolesDocument Document(IEnumerable<AccessRole>? roles = null, IEnumerable<RoleAssignment>? assignments = null, IReadOnlyDictionary<Guid, LedgerEntry>? ledger = null)
        => new()
        {
            Roles = [.. roles ?? []],
            Assignments = [.. assignments ?? []],
            Ledger = ledger ?? ImmutableDictionary<Guid, LedgerEntry>.Empty
        };

    public static LedgerEntry Ledger(bool allFolders, params CatalogLibrary[] folders) => new()
    {
        OriginalEnableAllFolders = allFolders,
        OriginalEnabledFolders = [.. folders.Select(f => f.Id)],
        OriginalEnableUserPreferenceAccess = true,
        CapturedUtc = Now.AddDays(-2)
    };

    public static IReadOnlyList<string> Names(IEnumerable<VisibleLibrary> libraries) => [.. libraries.Select(l => l.Library.Name)];

    public static IReadOnlyList<string> Names(IEnumerable<CatalogLibrary> libraries) => [.. libraries.Select(l => l.Name)];
}
