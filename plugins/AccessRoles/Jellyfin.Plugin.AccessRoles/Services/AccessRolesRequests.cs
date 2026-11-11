using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// Checks the roles API's requests and turns them into store records. Every id is
/// resolved: roles against the store, libraries against the catalog (<c>GetVirtualFolders()</c>), and a
/// reference a stored record already has to a library that is gone is kept as it is.
/// </summary>
internal static class AccessRolesRequests
{
    /// <summary>The longest role name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest role description.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// Builds a role from a request: a new one (<paramref name="existing"/> <c>null</c>) or a replacement of a stored one,
    /// whose unknown lock names and fields a newer build wrote are kept, and its unknown library mode (with its references)
    /// while the request sets no mode.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="existing">The stored role, or <c>null</c> for a new one.</param>
    /// <param name="id">The role's id.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The role.</returns>
    public static AccessRole BuildRole(AccessRolesRoleRequest request, AccessRole? existing, Guid id, LibraryCatalog catalog, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        if (existing is not null && request.Preset is not null)
        {
            throw PluginException.Invalid("A preset is for a new role; send the role's locks and libraries instead.");
        }

        var preset = request.Preset;
        if (preset is { } p && !Enum.IsDefined(p))
        {
            throw PluginException.Invalid("Preset must be Kid, Guest or Standard.");
        }

        var name = Name(request.Name);
        var description = Description(request.Description);
        var locks = request.Locks ?? (preset is { } fromPreset ? AccessPresets.Locks(fromPreset) : []);
        foreach (var value in locks)
        {
            if (!Enum.IsDefined(value))
            {
                throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "{0} is no lock.", (int)value));
            }
        }

        var keptLocks = existing?.Locks.Where(l => !l.IsKnown) ?? [];

        // A library mode a newer build wrote stays, with its references, unless the request sets a mode (the store's rule:
        // unknown values are kept); this build can't check references it doesn't know the meaning of.
        if (request.Libraries?.Mode is null && existing?.Libraries.Mode is { IsKnown: false } keptMode)
        {
            var keptRefs = request.Libraries?.Refs is { } given
                ? [.. given.Where(r => r is not null).Select(r => r.Id).Distinct().Select(libraryId => Reference(libraryId, existing, catalog))]
                : existing.Libraries.Refs;
            return new AccessRole
            {
                Id = id,
                Name = name,
                Description = description,
                Locks = [.. AccessPresets.AllLocks.Where(locks.Contains).Select(LenientEnum<AccessRolesLock>.From), .. keptLocks],
                Libraries = existing.Libraries with { Mode = keptMode, Refs = keptRefs },
                CreatedUtc = existing.CreatedUtc,
                UpdatedUtc = AccessRolesTokens.Next(existing.UpdatedUtc, now),
                Extra = existing.Extra
            };
        }

        var mode = request.Libraries?.Mode ?? (preset is { } presetMode ? AccessPresets.LibraryMode(presetMode) : AccessRolesRoleLibraryMode.NotManaged);
        if (!Enum.IsDefined(mode))
        {
            throw PluginException.Invalid("Libraries.Mode must be NotManaged, All, AllExcept or Only.");
        }

        var ids = (request.Libraries?.Refs ?? []).Where(r => r is not null).Select(r => r.Id).Distinct().ToList();
        switch (mode)
        {
            case AccessRolesRoleLibraryMode.NotManaged or AccessRolesRoleLibraryMode.All when ids.Count > 0:
                throw PluginException.Invalid("A role that grants every library, or doesn't manage libraries, lists no library: leave Refs empty.");
            case AccessRolesRoleLibraryMode.Only when ids.Count == 0:
                throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.EmptyOnlyRole, "Pick at least one library: an Only… role with nothing selected can't be saved.");
            case AccessRolesRoleLibraryMode.AllExcept when ids.Count == 0:
                throw PluginException.Invalid("All except… needs at least one library to leave out; a role for every library is All.");
        }

        var refs = ids.Select(libraryId => Reference(libraryId, existing, catalog)).ToList();
        var role = new AccessRole
        {
            Id = id,
            Name = name,
            Description = description,
            Locks = [.. AccessPresets.AllLocks.Where(locks.Contains).Select(LenientEnum<AccessRolesLock>.From), .. keptLocks],
            Libraries = new RoleLibraries { Mode = mode, Refs = refs, Extra = existing?.Libraries.Extra },
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = AccessRolesTokens.Next(existing?.UpdatedUtc, now),
            Extra = existing?.Extra
        };
        return role;
    }

    /// <summary>
    /// Checks a role an import brings as <see cref="BuildRole"/> checks a request: its name
    /// (1 to 100 characters after trimming, no control characters) and description, and its library mode's references
    /// (<c>NotManaged</c> and <c>All</c> list none, <c>AllExcept</c> and <c>Only</c> at least one; a mode a newer build wrote
    /// isn't checked, as the roles API keeps it). Its name and description come back trimmed, as the API stores them.
    /// </summary>
    /// <param name="role">The file's role.</param>
    /// <param name="reason">Why it is refused (<see cref="AccessRolesDataImport.InvalidName"/>, …), else <c>null</c>.</param>
    /// <param name="detail">The roles API's message for it, else <c>null</c>.</param>
    /// <returns>The role, normalised, or <c>null</c> when the roles API would refuse it.</returns>
    public static AccessRole? CheckImported(AccessRole role, out string? reason, out string? detail)
    {
        ArgumentNullException.ThrowIfNull(role);
        reason = null;
        detail = null;
        string name;
        string? description;
        try
        {
            name = Name(role.Name);
        }
        catch (PluginException ex)
        {
            (reason, detail) = (AccessRolesDataImport.InvalidName, ex.Message);
            return null;
        }

        try
        {
            description = Description(role.Description);
        }
        catch (PluginException ex)
        {
            (reason, detail) = (AccessRolesDataImport.InvalidDescription, ex.Message);
            return null;
        }

        var libraries = role.Libraries;
        var count = libraries?.Refs?.Count ?? 0;
        if (libraries is null || libraries.Refs is null)
        {
            (reason, detail) = (AccessRolesDataImport.InvalidLibraries, "The role has no Libraries.");
            return null;
        }

        if ((libraries.Mode.Is(AccessRolesRoleLibraryMode.NotManaged) || libraries.Mode.Is(AccessRolesRoleLibraryMode.All)) && count > 0)
        {
            (reason, detail) = (AccessRolesDataImport.InvalidLibraries, "A role that grants every library, or doesn't manage libraries, lists no library: leave Refs empty.");
            return null;
        }

        if (libraries.Mode.Is(AccessRolesRoleLibraryMode.AllExcept) && count == 0)
        {
            (reason, detail) = (AccessRolesDataImport.InvalidLibraries, "All except… needs at least one library to leave out; a role for every library is All.");
            return null;
        }

        if (libraries.Mode.Is(AccessRolesRoleLibraryMode.Only) && count == 0)
        {
            (reason, detail) = (AccessRolesException.EmptyOnlyRole, "Pick at least one library: an Only… role with nothing selected can't be saved.");
            return null;
        }

        return string.Equals(name, role.Name, StringComparison.Ordinal) && string.Equals(description, role.Description, StringComparison.Ordinal)
            ? role
            : role with { Name = name, Description = description };
    }

    /// <summary>
    /// Refuses a role whose name another role has, ignoring letter case (409 <c>DuplicateName</c>).
    /// </summary>
    /// <param name="roles">The roles the role joins.</param>
    /// <param name="role">The role.</param>
    public static void EnsureUniqueName(IEnumerable<AccessRole> roles, AccessRole role)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(role);
        if (roles.Any(r => !r.Id.Equals(role.Id) && string.Equals(r.Name, role.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AccessRolesException(PluginErrorKind.Conflict, AccessRolesException.DuplicateName, "Another role is already called \"" + role.Name + "\".");
        }
    }

    /// <summary>
    /// Builds a user's assignment from a request, or <c>null</c> when it says nothing (no role, no override, <c>Inherit</c>
    /// with empty lists, <c>Managed</c>): the user is then not managed. Unknown overrides and fields a newer build wrote on
    /// the stored assignment are kept, and so are its unknown <c>State</c> and <c>Libraries.Mode</c> while the request leaves
    /// them out; a request that sets one replaces it.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="userId">The user's id.</param>
    /// <param name="existing">The stored assignment, or <c>null</c>.</param>
    /// <param name="document">The document whose roles the request's role ids must name.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The assignment, or <c>null</c>.</returns>
    public static RoleAssignment? BuildAssignment(AccessRolesUserRequest request, Guid userId, RoleAssignment? existing, AccessRolesDocument document, LibraryCatalog catalog, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var roleIds = RoleIds(request.RoleIds, document);
        var overrides = ImmutableDictionary.CreateBuilder<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>>();
        foreach (var (key, value) in request.LockOverrides ?? ImmutableDictionary<string, AccessRolesLockOverride>.Empty)
        {
            if (string.IsNullOrWhiteSpace(key) || !Enum.TryParse<AccessRolesLock>(key.Trim(), ignoreCase: true, out var name) || !Enum.IsDefined(name) || char.IsDigit(key.Trim()[0]))
            {
                throw PluginException.Invalid("LockOverrides: \"" + key + "\" is no lock.");
            }

            if (!Enum.IsDefined(value))
            {
                throw PluginException.Invalid("LockOverrides: an override is Inherit, Lock or Unlock.");
            }

            if (value != AccessRolesLockOverride.Inherit)
            {
                overrides[name] = value;
            }
        }

        foreach (var kept in existing?.LockOverrides.Where(o => !o.Key.IsKnown || !o.Value.IsKnown) ?? [])
        {
            overrides.TryAdd(kept.Key, kept.Value);
        }

        // A mode or state a newer build wrote stays unless the request sets one (the store's rule: unknown values are
        // kept); a request that names one replaces it, as the admin chose.
        LenientEnum<AccessRolesUserLibraryMode> mode;
        if (request.Libraries?.Mode is { } requestedMode)
        {
            if (!Enum.IsDefined(requestedMode))
            {
                throw PluginException.Invalid("Libraries.Mode must be Inherit, All or Only.");
            }

            mode = requestedMode;
        }
        else
        {
            mode = existing?.Libraries.Mode is { IsKnown: false } keptMode ? keptMode : AccessRolesUserLibraryMode.Inherit;
        }

        var add = LibraryIds(request.Libraries?.Add, existing?.Libraries.Add, catalog, "Add");
        var remove = LibraryIds(request.Libraries?.Remove, existing?.Libraries.Remove, catalog, "Remove");
        if (add.Intersect(remove).Any())
        {
            throw PluginException.Invalid("A library can't be both added and removed for a user.");
        }

        LenientEnum<AccessRolesAssignmentState> state;
        if (request.State is { } requestedState)
        {
            if (!Enum.IsDefined(requestedState) || requestedState == AccessRolesAssignmentState.CannotManage)
            {
                throw PluginException.Invalid("State must be Managed or Excluded; CannotManage is the server's to set.");
            }

            state = requestedState;
        }
        else
        {
            state = existing?.State is { IsKnown: false } keptState ? keptState : AccessRolesAssignmentState.Managed;
        }

        var confirm = request.Libraries?.ConfirmNoLibraries == true && mode.Is(AccessRolesUserLibraryMode.Only) && add.Count == 0;
        if (roleIds.Count == 0 && overrides.Count == 0 && mode.Is(AccessRolesUserLibraryMode.Inherit) && add.Count == 0 && remove.Count == 0
            && state.Is(AccessRolesAssignmentState.Managed) && existing?.Extra is null && existing?.Libraries.Extra is null)
        {
            return null;
        }

        return new RoleAssignment
        {
            UserId = userId,
            RoleIds = roleIds,
            LockOverrides = overrides.ToImmutable(),
            Libraries = new AssignmentLibraries { Mode = mode, Add = add, Remove = remove, ConfirmNoLibraries = confirm, Extra = existing?.Libraries.Extra },
            State = state,
            UpdatedUtc = AccessRolesTokens.Next(existing?.UpdatedUtc, now),
            ConfirmedNoFullAdminUtc = existing?.ConfirmedNoFullAdminUtc,
            Extra = existing?.Extra
        };
    }

    /// <summary>
    /// Gives an assignment other roles, or removes it when it says nothing afterwards.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="roleIds">The new roles.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The assignment, or <c>null</c>.</returns>
    public static RoleAssignment? WithRoles(RoleAssignment assignment, IReadOnlyList<Guid> roleIds, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(roleIds);
        var changed = assignment with { RoleIds = roleIds, UpdatedUtc = AccessRolesTokens.Next(assignment.UpdatedUtc, now) };
        return IsEmpty(changed) ? null : changed;
    }

    /// <summary>
    /// Gets whether an assignment says nothing: no role, no override, <c>Inherit</c> with empty lists, <c>Managed</c>, and no
    /// field a newer build wrote.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <returns><c>true</c> when the user would be unmanaged.</returns>
    public static bool IsEmpty(RoleAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return assignment.RoleIds.Count == 0 && assignment.LockOverrides.Count == 0 && assignment.Libraries.Mode.Is(AccessRolesUserLibraryMode.Inherit)
            && assignment.Libraries.Add.Count == 0 && assignment.Libraries.Remove.Count == 0 && assignment.State.Is(AccessRolesAssignmentState.Managed)
            && assignment.Extra is null && assignment.Libraries.Extra is null;
    }

    /// <summary>
    /// Checks role ids against the document's roles (400 for one that doesn't exist).
    /// </summary>
    /// <param name="ids">The ids, or <c>null</c>.</param>
    /// <param name="document">The document.</param>
    /// <returns>The ids, without repeats, in their order.</returns>
    public static IReadOnlyList<Guid> RoleIds(IReadOnlyList<Guid>? ids, AccessRolesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var distinct = (ids ?? []).Distinct().ToList();
        foreach (var id in distinct)
        {
            if (!document.Roles.Any(r => r.Id.Equals(id)))
            {
                throw PluginException.Invalid("Role " + id.ToString("N", CultureInfo.InvariantCulture) + " doesn't exist.");
            }
        }

        return distinct;
    }

    private static LibraryReference Reference(Guid id, AccessRole? existing, LibraryCatalog catalog)
    {
        if (catalog.Find(id) is { } library)
        {
            return new LibraryReference { Id = id, NameAtSelection = library.Name, Locations = library.Locations };
        }

        // A library the server no longer lists: only a reference the role already has stays (kept until it is purged).
        return existing?.Libraries.Refs.FirstOrDefault(r => r.Id.Equals(id))
            ?? throw PluginException.Invalid("Library " + id.ToString("N", CultureInfo.InvariantCulture) + " doesn't exist.");
    }

    private static List<Guid> LibraryIds(IReadOnlyList<Guid>? ids, IReadOnlyList<Guid>? stored, LibraryCatalog catalog, string field)
    {
        var distinct = (ids ?? []).Distinct().ToList();
        foreach (var id in distinct)
        {
            if (!catalog.Contains(id) && stored?.Contains(id) != true)
            {
                throw PluginException.Invalid(field + ": library " + id.ToString("N", CultureInfo.InvariantCulture) + " doesn't exist.");
            }
        }

        return distinct;
    }

    private static string Name(string? name)
    {
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw PluginException.Invalid("The role needs a name.");
        }

        if (text.Length > MaxNameLength)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "A role's name is at most {0} characters.", MaxNameLength));
        }

        if (text.Any(char.IsControl))
        {
            throw PluginException.Invalid("A role's name can't contain control characters.");
        }

        return text;
    }

    private static string? Description(string? description)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxDescriptionLength)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "A role's description is at most {0} characters.", MaxDescriptionLength));
        }

        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            throw PluginException.Invalid("A role's description can't contain control characters.");
        }

        return text;
    }
}
