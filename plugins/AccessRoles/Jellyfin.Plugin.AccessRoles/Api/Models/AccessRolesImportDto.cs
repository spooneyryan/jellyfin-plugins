using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of <c>POST /AccessRoles/Admin/ImportExisting</c> (<c>{ProposedRoles[], Assignments[],
/// EffectiveChanges[], Warnings[]}</c>): the roles the import proposes (one per distinct library access among
/// the non-admin users, named after its libraries, with no locks), who gets which, what that does to each of them (which
/// must be nothing), and the warnings.
/// </summary>
public sealed class AccessRolesImportDto
{
    /// <summary>Gets or sets a value indicating whether nothing was saved (<c>dryRun=true</c>).</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the proposed roles, by name; saved ones carry their id.</summary>
    public IReadOnlyList<AccessRolesImportRoleDto> ProposedRoles { get; set; } = [];

    /// <summary>Gets or sets who gets which role, by user name.</summary>
    public IReadOnlyList<AccessRolesImportAssignmentDto> Assignments { get; set; } = [];

    /// <summary>
    /// Gets or sets what the import does to each user it assigns (the preview's rows): no library added or removed, and no
    /// policy write, unless a warning says why.
    /// </summary>
    public IReadOnlyList<AccessRolesPreviewUserDto> EffectiveChanges { get; set; } = [];

    /// <summary>Gets or sets how many imported users keep exactly their access with no policy write ("no change for 7 users").</summary>
    public int NoChange { get; set; }

    /// <summary>Gets or sets the warnings: users not imported and why, and what the admin should know about the others.</summary>
    public IReadOnlyList<AccessRolesImportWarningDto> Warnings { get; set; } = [];

    /// <summary>Gets or sets the numbers of the audit lines the import wrote (none for a dry run).</summary>
    public IReadOnlyList<long> AuditSeq { get; set; } = [];

    /// <summary>
    /// Gets or sets what the import does, as one token (<see cref="AccessRolesPreviewDto.PreviewToken"/>):
    /// the import sends its dry run's back as <c>?ifPreview=</c>, and an import that would do anything else writes nothing
    /// (409 <c>Changed</c>).
    /// </summary>
    public string PreviewToken { get; set; } = string.Empty;
}
