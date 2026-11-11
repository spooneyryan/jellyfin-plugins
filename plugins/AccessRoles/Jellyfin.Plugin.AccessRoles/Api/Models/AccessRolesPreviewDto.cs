using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of <c>POST /AccessRoles/Admin/Preview</c>: what a proposed change does, per user it
/// affects, and the refusals saving it would meet.
/// </summary>
public sealed class AccessRolesPreviewDto
{
    /// <summary>Gets or sets the users the change does anything for, by name.</summary>
    public IReadOnlyList<AccessRolesPreviewUserDto> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets the users it takes out of library management without an <c>onUnmanage</c> choice (a save is a 400
    /// <c>UnmanageChoiceRequired</c>).
    /// </summary>
    public IReadOnlyList<AccessRolesUserRefDto> UnmanageChoiceRequired { get; set; } = [];

    /// <summary>Gets or sets the users it would leave with no libraries by accident (a save is a 409 <c>NoLibraries</c>).</summary>
    public IReadOnlyList<AccessRolesUserRefDto> NoLibraries { get; set; } = [];

    /// <summary>Gets or sets the users <c>Restore</c> can't restore, having no ledger entry (a save is a 409 <c>NoLedgerEntry</c>).</summary>
    public IReadOnlyList<AccessRolesUserRefDto> NoLedgerEntry { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether it leaves no enabled administrator with every library (a save needs
    /// <c>confirmNoFullAdmin=true</c>, else 409 <c>NoFullAdministrator</c>).
    /// </summary>
    public bool NoFullAdministrator { get; set; }

    /// <summary>
    /// Gets or sets the roles it deletes that are default roles of new users, in the stored default roles or the
    /// proposal's (a <c>DELETE</c> is a 409 <c>DefaultRole</c> until the default roles leave them out).
    /// </summary>
    public IReadOnlyList<AccessRolesRoleRefDto> DefaultRole { get; set; } = [];

    /// <summary>
    /// Gets or sets what this change does, as one token: the save sends it back as
    /// <c>?ifPreview=</c>, and a save whose own change does anything else (a role it adds, reassigns to or names was edited
    /// since, a library went, a user's access changed) writes nothing and answers 409 <c>Changed</c>. A save without it (an
    /// older client) is not checked.
    /// </summary>
    public string PreviewToken { get; set; } = string.Empty;
}
