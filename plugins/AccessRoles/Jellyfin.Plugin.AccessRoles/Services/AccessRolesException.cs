using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// An expected refusal of the roles API that carries a code (400 <c>EmptyOnlyRole</c>, 400
/// <c>UnmanageChoiceRequired</c>, 409 <c>NoLibraries</c>, …) and the users it concerns. <see cref="AccessRolesProblemFilterAttribute"/>
/// adds both to the kit's ProblemDetails body as <c>Code</c> and <c>Users</c>, and a refused dry run's <c>Preview</c>.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Every refusal has a kind and a code.")]
[SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Plugin types are internal; the controller-level filter maps it.")]
internal sealed class AccessRolesException : PluginException
{
    /// <summary>An <c>Only…</c> role with no library (400).</summary>
    public const string EmptyOnlyRole = "EmptyOnlyRole";

    /// <summary>A change that takes users out of library management without an <c>onUnmanage</c> choice (400).</summary>
    public const string UnmanageChoiceRequired = "UnmanageChoiceRequired";

    /// <summary>A change that would leave users with no libraries by accident (409).</summary>
    public const string NoLibraries = "NoLibraries";

    /// <summary>
    /// <c>Restore</c> for users without a ledger entry (409, <c>Unmanage</c> and the other ways of leaving management).
    /// </summary>
    public const string NoLedgerEntry = "NoLedgerEntry";

    /// <summary>A change that leaves no enabled administrator with every library, unconfirmed (409, the lockout guard).</summary>
    public const string NoFullAdministrator = "NoFullAdministrator";

    /// <summary>A role name another role has, ignoring letter case (409).</summary>
    public const string DuplicateName = "DuplicateName";

    /// <summary>A role that is a default role of new users can't be deleted (409).</summary>
    public const string DefaultRole = "DefaultRole";

    /// <summary>The record changed since the request's <c>IfUpdatedAt</c> (409).</summary>
    public const string Changed = "Changed";

    /// <summary><i>Import current setup</i> while Access Roles already manages users (409, <c>ImportExisting</c>).</summary>
    public const string AlreadyManaging = "AlreadyManaging";

    /// <summary>
    /// <c>Sync?confirmRestore=true</c> before the sync's dry run was seen since it was held after a restore (409),
    /// or once a restore began since that dry run, or while one is in progress (1.0.1).
    /// </summary>
    public const string DryRunRequired = "DryRunRequired";

    /// <summary>
    /// A settings save that adds a configuration key whose type carries secrets to the allow-list, unconfirmed
    /// (400).
    /// </summary>
    public const string SecretKeys = "SecretKeys";

    /// <summary>An import whose body isn't an export of the kit's envelope at all (400).</summary>
    public const string NotAnExport = "NotAnExport";

    /// <summary>
    /// An import of the kit's envelope in a format version this build doesn't read (<c>jellyfin-plugin-export/2</c>, say;
    /// 400).
    /// </summary>
    public const string ExportFormatNotSupported = "ExportFormatNotSupported";

    /// <summary>An import of another plugin's export (400).</summary>
    public const string NotThisPlugin = "NotThisPlugin";

    /// <summary>
    /// An import or snapshot restore of data written at a newer schema version than this build reads (409): install that
    /// version to import it.
    /// </summary>
    public const string NewerSchemaVersion = "NewerSchemaVersion";

    /// <summary>
    /// An import with more records than Access Roles takes (400): more than 1,000 roles, 10,000
    /// assignments, ledger entries, mirror records or users, 1,000 libraries, or 100,000 ids in its records.
    /// </summary>
    public const string TooManyRecords = "TooManyRecords";

    /// <summary>
    /// An export (400) or a snapshot (409) whose data can't be read: a name given twice in one of its objects, or a value of
    /// the wrong type.
    /// </summary>
    public const string InvalidData = "InvalidData";

    /// <summary>
    /// <c>Sync?confirmRestore=true</c> without <c>ifPlan</c> while the sync is held after a restore (400; since 0.9.16):
    /// the confirmation names the plan the admin saw, the dry run's <c>PlanToken</c>.
    /// </summary>
    public const string IfPlanRequired = "IfPlanRequired";

    /// <summary>
    /// A <i>Restore snapshot</i> while another restore is in progress (409; since 1.0.1): restores run one
    /// at a time, and nothing was changed.
    /// </summary>
    public const string RestoreInProgress = "RestoreInProgress";

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesException"/> class.
    /// </summary>
    /// <param name="kind">The error kind, which decides the status code.</param>
    /// <param name="code">The code.</param>
    /// <param name="message">The message shown to the admin.</param>
    /// <param name="users">The users it concerns.</param>
    public AccessRolesException(PluginErrorKind kind, string code, string message, IReadOnlyList<AccessRolesUserRefDto>? users = null)
        : base(kind, message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
        Users = users ?? [];
    }

    /// <summary>
    /// Gets the code. It hides the kit's <see cref="PluginException.Code"/>, which stays <c>null</c>: the
    /// <see cref="AccessRolesProblemFilterAttribute"/> writes this one as the <c>Code</c> extension, as before the kit had it.
    /// </summary>
    public new string Code { get; }

    /// <summary>Gets the users it concerns.</summary>
    public IReadOnlyList<AccessRolesUserRefDto> Users { get; }

    /// <summary>
    /// Gets, for a refused dry run, the preview of what the change would do: the admin page shows it with
    /// the refusal, from the very request it would save.
    /// </summary>
    public AccessRolesPreviewDto? Preview { get; init; }

    /// <summary>
    /// Gets the same refusal with the preview of what the change would do.
    /// </summary>
    /// <param name="preview">The preview.</param>
    /// <returns>The refusal.</returns>
    public AccessRolesException WithPreview(AccessRolesPreviewDto preview) => new(Kind, Code, Message, Users) { Preview = preview };
}
