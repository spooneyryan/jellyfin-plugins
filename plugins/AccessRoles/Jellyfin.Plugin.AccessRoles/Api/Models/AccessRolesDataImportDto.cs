using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The answer of <c>POST /AccessRoles/Admin/Import</c> and <c>POST /AccessRoles/Admin/Snapshots/Restore</c>:
/// what the export's or the snapshot's roles and assignments do here, user by user (<see cref="Preview"/>, whose
/// <c>PreviewToken</c> the save sends back as <c>ifPreview</c>), the users and libraries found here and those that aren't,
/// the records added, updated and removed, and what was left out and why.
/// </summary>
public sealed class AccessRolesDataImportDto
{
    /// <summary>Gets or sets a value indicating whether nothing was saved (<c>dryRun=true</c>, the default).</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets <c>Merge</c> or <c>Replace</c> (a restore is always <c>Replace</c>).</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets how the users were matched: <c>Id</c> or <c>Name</c> (a restore: <c>Id</c>).</summary>
    public string MapUsersBy { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>Export</c> (an import's file) or <c>Snapshot</c> (a restore).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the snapshot's name (a restore) or the plugin version that wrote the export.</summary>
    public string? SourceName { get; set; }

    /// <summary>Gets or sets when the export or the snapshot was written, UTC.</summary>
    public DateTime? SourceUtc { get; set; }

    /// <summary>Gets or sets the schema version the data was written at.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the data is this server's own (a snapshot, or an export with this server's
    /// id): only then may its ledger fill in a managed user's missing original access.
    /// </summary>
    public bool SameServer { get; set; }

    /// <summary>
    /// Gets or sets the users: <c>Mapped</c> (<c>{Id, NewId, Name, Records}</c>) and <c>Unknown</c> (<c>{Id, Name, Reason,
    /// Records}</c>, left out: nothing is written for them).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Users { get; set; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets or sets the libraries the data names: <c>Mapped</c> (<c>{ExportId, Id, Name, By}</c>, <c>By</c> one of
    /// <c>Id</c>, <c>Locations</c>, <c>Name</c>) and <c>Unmatched</c> (<c>{ExportId, Name, Locations, Reason}</c>, left out,
    /// never guessed).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Libraries { get; set; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets or sets the records: <c>Roles</c> and <c>Assignments</c> (<c>{Added, Updated, Unchanged, KeptNewer,
    /// Removed}</c>), <c>DefaultRoles</c> (before and after), <c>Ledger</c> (<c>{Incoming, Added, SameServer}</c>) and
    /// <c>MirrorRecords</c> (in the data; this server's own are kept).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Records { get; set; } = new Dictionary<string, object?>();

    /// <summary>Gets or sets what was left out: <c>{Kind, Id, Name, Reason, Of}</c>.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Skipped { get; set; } = [];

    /// <summary>
    /// Gets or sets what the change does to each user whose effective access changes, and the refusals it meets, with the
    /// <c>PreviewToken</c> (<see cref="AccessRolesPreviewDto"/>).
    /// </summary>
    public AccessRolesPreviewDto Preview { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the data changes nothing here.</summary>
    public bool NoChange { get; set; }

    /// <summary>Gets or sets the snapshot taken of the data before the save (none for a dry run).</summary>
    public string? SafetySnapshot { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the library sync is held after this restore until an admin has seen its dry
    /// run and confirmed it (<c>POST Sync?dryRun=true</c>, then <c>confirmRestore=true</c> with <c>ifPlan</c>).
    /// </summary>
    public bool SyncHeld { get; set; }

    /// <summary>Gets or sets what the admin should know (the settings never applied, say).</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];

    /// <summary>Gets or sets the numbers of the audit lines written (none for a dry run).</summary>
    public IReadOnlyList<long> AuditSeq { get; set; } = [];
}
