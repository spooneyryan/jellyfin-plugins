using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The Data tab's actions and the Diagnostics tab's: export, import, the snapshots and their
/// restore, <i>Remap by name</i> and <i>Run maintenance now</i>.
/// </summary>
/// <remarks>
/// <para><b>An import or a restore is a write like any other</b>: the export's or the snapshot's document is combined with
/// the stored one (<see cref="AccessRolesDataImport"/>) inside the store's writer, through the same analysis, refusals
/// (the leaving-management choice, <c>NoLedgerEntry</c>, <c>NoLibraries</c>, <c>NoFullAdministrator</c>, the
/// <c>LockoutGuard</c>), ledger, preview token and audit lines. A dry run (the default) answers what it would
/// do for each user; the save is checked first against the stored document with nothing written (its refusals and
/// <c>ifPreview</c>), so a refused save leaves no snapshot behind, then takes the pre-operation snapshot, then saves.</para>
/// <para><b>A restore holds the library sync</b>: the hold file is written before the save, failing closed, so no
/// run writes the restored access before an admin has seen the sync's dry run and confirmed it (the Overview's box, as
/// after a restore at load).</para>
/// </remarks>
internal sealed partial class AccessRolesAdminService
{
    private const string ExportFileStem = "accessroles";
    private const string ExportFormatPrefix = "jellyfin-plugin-export/";
    private const string SourceExport = "Export";
    private const string SourceSnapshot = "Snapshot";

    /// <inheritdoc />
    public IActionResult ExportData(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var support = Support();

        // The data first: it refuses (503) a file whose upgrade failed, and passes on a newer version's file as it is.
        var data = _store.Documents.ExportData();
        var document = _store.Current;
        var stored = document.Assignments.Select(a => a.UserId)
            .Concat(document.Ledger.Keys)
            .Concat(document.PreferenceMirror.Keys)
            .Distinct()
            .ToList();
        var envelope = ExportEnvelope.Create(
            AccessRolesKit.Options,
            data,
            ExportUser.For(stored, document.KnownUsers, ServerUserNames(), _reconciler().DeletedUserIds),
            support.Configuration(),
            archive: null,
            support.ServerId,
            _timeProvider.GetUtcNow().UtcDateTime);
        _logger.LogInformation(
            "An admin exported the Access Roles data: {Roles} roles, {Assignments} assignments, {Ledger} ledger entries, {Mirror} mirror records",
            document.Roles.Count,
            document.Assignments.Count,
            document.Ledger.Count,
            document.PreferenceMirror.Count);
        return envelope.ToFileResult(context, ExportFileStem);
    }

    /// <inheritdoc />
    public async Task<AccessRolesDataImportDto> ImportDataAsync(HttpRequest request, string? mode, string? mapUsersBy, AccessRolesWriteOptions options, ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var support = Support();
        var parsed = ImportRequest.Parse(mode, options.DryRun, mapUsersBy);
        var cancellationToken = request.HttpContext.RequestAborted;
        var envelope = await ReadEnvelopeAsync(request, cancellationToken).ConfigureAwait(false);
        AccessRolesDocument incoming;
        try
        {
            RequireUniqueNames(envelope.Data);
            incoming = _store.Documents.ReadData(envelope.Data, envelope.SchemaVersion);
        }
        catch (ArgumentException ex)
        {
            // System.Text.Json's JsonObject refuses a name given twice when it is first read: inside Data, here.
            throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.InvalidData, "The export's data couldn't be read: " + ex.Message);
        }
        catch (PluginException ex) when (ex is not AccessRolesException && ex.Kind == PluginErrorKind.Invalid)
        {
            throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.InvalidData, ex.Message);
        }

        AccessRolesDataImport.EnsureWithinLimits(incoming, envelope.Users.Count);

        // Users mapped by name never get a deleted user's access: while the list of those users can't be read, refused, a dry
        // run too (fail closed, as Bookmarks).
        var reconciler = _reconciler();
        if (parsed.MapUsersBy == MapUsersBy.Name)
        {
            reconciler.ThrowIfDeletedUsersUnknown("An import that maps users by name");
        }

        var users = ImportUserMap.Build(AccessRolesDataImport.CountRecordsByUser(incoming), envelope.Users, ServerUserNames(), reconciler.DeletedUserIds, parsed.MapUsersBy);
        var sameServer = !string.IsNullOrWhiteSpace(envelope.ServerId) && string.Equals(envelope.ServerId, support.ServerId, StringComparison.OrdinalIgnoreCase);
        var source = new DataSource(SourceExport, envelope.PluginVersion, envelope.ExportedUtc, envelope.SchemaVersion, sameServer, envelope.Settings is not null);
        return await CombineAsync(incoming, parsed.Mode, users, source, options, caller, restore: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetSnapshots()
    {
        var support = Support();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Snapshots"] = _store.Documents.ListSnapshots(SnapshotDirectory()).Select(s => s.ToDictionary()).ToList(),
            ["KeepSnapshotsBeforeUtc"] = _store.Documents.KeepSnapshotsBeforeUtc,
            ["SnapshotsToKeep"] = Math.Max(0, support.Configuration().SnapshotsToKeep),
            ["StoreReadOnly"] = _store.IsReadOnly,
            ["SyncHeld"] = RestoreHold.IsHeld(support.Paths, _store)
        };
    }

    /// <inheritdoc />
    public async Task<AccessRolesDataImportDto> RestoreSnapshotAsync(string? name, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Support();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw PluginException.Invalid("name is required: one of the names GET /AccessRoles/Admin/Snapshots lists.");
        }

        var directory = SnapshotDirectory();
        var snapshot = SnapshotRotation.List(directory, _store.Documents.StoreName, ".json")
            .Concat(SnapshotRotation.ListPreOp(directory, _store.Documents.StoreName, ".json"))
            .FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PluginException.NotFound("No snapshot of the Access Roles data has that name.");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(snapshot.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Access Roles couldn't read the snapshot {Snapshot}", snapshot.Name);
            throw new PluginException(PluginErrorKind.Unavailable, "The snapshot couldn't be read. The server log has the details.", ex);
        }

        AccessRolesDocument document;
        int version;
        try
        {
            var utf8 = JsonStoreFormat.SkipByteOrderMark(bytes);
            version = JsonStoreFormat.ReadVersion(utf8) ?? 1;
            ThrowIfNewer(version, "The snapshot " + snapshot.Name);
            var root = JsonNode.Parse(utf8) as JsonObject ?? throw PluginException.Conflict("The snapshot " + snapshot.Name + " isn't a JSON object.");
            RequireUniqueNames(root);
            document = _store.Documents.ReadData(root, version, "The snapshot " + snapshot.Name);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new AccessRolesException(PluginErrorKind.Conflict, AccessRolesException.InvalidData, "The snapshot " + snapshot.Name + " couldn't be read: " + ex.Message);
        }

        // The snapshot's users by id, the names it knew for them: a user deleted since is not on this server, so nothing comes
        // back for them.
        var known = document.KnownUsers;
        var exportUsers = AccessRolesDataImport.CountRecordsByUser(document).Keys.Select(id => new ExportUser(id, known.NameOf(id))).ToList();
        var users = ImportUserMap.Build(AccessRolesDataImport.CountRecordsByUser(document), exportUsers, ServerUserNames(), _reconciler().DeletedUserIds, MapUsersBy.Id);
        var source = new DataSource(SourceSnapshot, snapshot.Name, snapshot.Utc, version, SameServer: true, Settings: false);
        return await CombineAsync(document, ImportMode.Replace, users, source, options, caller, snapshot.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IDictionary<string, object?> RunMaintenance()
    {
        var tasks = Support().Tasks() ?? throw new PluginException(PluginErrorKind.Unavailable, "The server's scheduled tasks aren't available right now.");
        tasks.QueueScheduledTask<AccessRolesMaintenanceTask>();
        _logger.LogInformation("An admin queued the Access Roles maintenance to run now");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Queued"] = true,
            ["Task"] = AccessRolesKit.Options.PluginName + ": Maintenance"
        };
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, string? ifPlan, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(by) && !string.Equals(by.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
        {
            throw PluginException.Invalid("by must be Name.");
        }

        // The kit's reconciler calls the store's user-data owner, whose UsersRemapped line names this caller. A remap that
        // writes is the one its dry run showed (the kit's PlanToken as ifPlan).
        using (AuditActorScope.Begin(Actor(caller)))
        {
            return (await _reconciler().RemapByNameAsync(dryRun ?? true, ifPlan, cancellationToken).ConfigureAwait(false)).ToDictionary();
        }
    }

    private static void ThrowIfNewer(int schemaVersion, string what)
    {
        if (schemaVersion > AccessRolesDocument.CurrentVersion)
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                AccessRolesException.NewerSchemaVersion,
                what + " was written by a newer Access Roles version (schema " + schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "; this version reads up to " + AccessRolesDocument.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "). Install that version to import it.");
        }
    }

    // The body read once with the kit's limit, then checked as an export of this plugin before anything else: each refusal
    // has its own code, so the page can say which file it is.
    private static async Task<ExportEnvelope> ReadEnvelopeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > ExportEnvelope.MaxImportBytes)
        {
            throw PluginException.PayloadTooLarge(ExportEnvelope.TooLargeMessage);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > ExportEnvelope.MaxImportBytes)
                {
                    throw PluginException.PayloadTooLarge(ExportEnvelope.TooLargeMessage);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new PluginException(PluginErrorKind.PayloadTooLarge, ExportEnvelope.TooLargeMessage, ex);
        }

        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        string? format = null;
        try
        {
            if (bytes.Length > 0 && JsonNode.Parse(JsonStoreFormat.SkipByteOrderMark(bytes.Span)) is JsonObject root
                && root[nameof(ExportEnvelope.Format)] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                format = text;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            format = null;
        }

        if (format is null || !format.StartsWith(ExportFormatPrefix, StringComparison.Ordinal))
        {
            throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.NotAnExport, "This file isn't an export of a Jellyfin plugin's data: choose a file the Data tab's Export made.");
        }

        if (!string.Equals(format, ExportEnvelope.FormatV1, StringComparison.Ordinal))
        {
            throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.ExportFormatNotSupported, "This export's format (" + format + ") is newer than this version reads (" + ExportEnvelope.FormatV1 + "). Install the Access Roles version that wrote it to import it.");
        }

        ExportEnvelope envelope;
        try
        {
            envelope = ExportEnvelope.Parse(bytes.Span);
        }
        catch (ArgumentException ex)
        {
            // A name given twice in an object the kit reads past its root (Users, Settings, Data).
            throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.InvalidData, "The export couldn't be read: " + ex.Message);
        }

        if (!envelope.PluginId.Equals(AccessRolesKit.Options.PluginId))
        {
            throw new AccessRolesException(PluginErrorKind.Invalid, AccessRolesException.NotThisPlugin, "This is an export of " + envelope.Plugin + "'s data, not Access Roles'.");
        }

        ThrowIfNewer(envelope.SchemaVersion, "The export");
        return envelope;
    }

    // Every object of the data read once, so a name given twice anywhere in it is refused (JsonObject throws when it first
    // reads its names), never taken as its last value by the deserializer; linear in the data, as deep as the parser lets it.
    private static void RequireUniqueNames(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (_, child) in value)
                {
                    RequireUniqueNames(child);
                }

                break;
            case JsonArray list:
                foreach (var child in list)
                {
                    RequireUniqueNames(child);
                }

                break;
        }
    }

    private static Dictionary<string, object?> Dictionary(params (string Key, object? Value)[] pairs)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            result[key] = value;
        }

        return result;
    }

    private static string PreviewLine(AccessRolesDocument document, IEnumerable<Guid> ledgerAdded)
        => AccessRolesTokens.Hash(JsonSerializer.Serialize(
            new
            {
                document.Roles,
                document.Assignments,
                document.DefaultRoleIds,
                Ledger = ledgerAdded.Select(AccessRolesTokens.Key).Order(StringComparer.Ordinal).ToList()
            },
            JsonStoreFormat.Options));

    private static List<AuditRecord> RoleLines(AuditActor actor, AccessRolesDocument before, AccessRolesDocument after, ChangeReport report, LibraryCatalog catalog, string source)
    {
        var lines = new List<AuditRecord>();
        var beforeById = new Dictionary<Guid, AccessRole>();
        foreach (var role in before.Roles)
        {
            beforeById.TryAdd(role.Id, role);
        }

        var afterIds = after.Roles.Select(r => r.Id).ToHashSet();
        foreach (var role in after.Roles)
        {
            var existing = beforeById.GetValueOrDefault(role.Id);
            if (existing is null)
            {
                lines.Add(new AuditRecord(actor, AuditActions.RoleCreated, null, role.Id, role.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Preset"] = null,
                    ["Imported"] = true,
                    ["Source"] = source,
                    ["Role"] = AccessRolesAuditDetails.Role(role)
                }));
            }
            else if (!SameRole(existing, role))
            {
                lines.Add(new AuditRecord(actor, AuditActions.RoleUpdated, null, role.Id, role.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Source"] = source,
                    ["Before"] = AccessRolesAuditDetails.Role(existing),
                    ["After"] = AccessRolesAuditDetails.Role(role),
                    ["Affected"] = AccessRolesAuditDetails.Affected(report, catalog)
                }));
            }
        }

        foreach (var role in before.Roles.Where(r => !afterIds.Contains(r.Id)))
        {
            lines.Add(new AuditRecord(actor, AuditActions.RoleDeleted, null, role.Id, role.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Source"] = source,
                ["Role"] = AccessRolesAuditDetails.Role(role),
                ["Members"] = before.Assignments.Where(a => a.RoleIds.Contains(role.Id)).Select(a => AccessRolesAuditDetails.Id(a.UserId)).ToList(),
                ["ReassignedTo"] = null,
                ["ReassignedToName"] = null
            }));
        }

        if (!before.DefaultRoleIds.SequenceEqual(after.DefaultRoleIds))
        {
            lines.Add(new AuditRecord(actor, AuditActions.DefaultRolesChanged, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Source"] = source,
                ["Before"] = AccessRolesAuditDetails.Ids(before.DefaultRoleIds),
                ["After"] = AccessRolesAuditDetails.Ids(after.DefaultRoleIds),
                ["RoleNames"] = after.DefaultRoleIds.ToDictionary(AccessRolesAuditDetails.Id, id => (object?)after.Roles.First(r => r.Id.Equals(id)).Name, StringComparer.Ordinal)
            }));
        }

        return lines;
    }

    private AccessRolesDataSupport Support()
        => _data ?? throw new PluginException(PluginErrorKind.Unavailable, "Access Roles' Data tab isn't available in this process.");

    private string SnapshotDirectory() => Support().Paths.SnapshotsDirectory;

    private Dictionary<Guid, string> ServerUserNames()
    {
        var names = new Dictionary<Guid, string>();
        foreach (var user in _users.GetAll())
        {
            names[user.Id] = user.Name;
        }

        return names;
    }

    // One import or restore. Restores run one at a time (since 1.0.1): a second one while a restore
    // is between its start and the end of its save answers 409 RestoreInProgress, before anything is checked or written. The
    // mark is taken without waiting, before the store's writer, so it takes part in no lock order (a dry run takes none).
    private async Task<AccessRolesDataImportDto> CombineAsync(
        AccessRolesDocument incoming,
        ImportMode mode,
        ImportUserMap users,
        DataSource source,
        AccessRolesWriteOptions options,
        ClaimsPrincipal caller,
        string? restore,
        CancellationToken cancellationToken)
    {
        if (restore is null || options.DryRun)
        {
            return await CombineOnceAsync(incoming, mode, users, source, options, caller, restore, cancellationToken).ConfigureAwait(false);
        }

        if (!_store.TryBeginRestore())
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                AccessRolesException.RestoreInProgress,
                "Another restore of a snapshot is in progress, so this one wasn't started: nothing was changed. When it is done, look at the Data tab again and restore again if you still want to.");
        }

        try
        {
            return await CombineOnceAsync(incoming, mode, users, source, options, caller, restore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _store.EndRestore();
        }
    }

    // One import or restore: the dry run, or the check, the hold (a restore), the snapshot and the save.
    private async Task<AccessRolesDataImportDto> CombineOnceAsync(
        AccessRolesDocument incoming,
        ImportMode mode,
        ImportUserMap users,
        DataSource source,
        AccessRolesWriteOptions options,
        ClaimsPrincipal caller,
        string? restore,
        CancellationToken cancellationToken)
    {
        var support = Support();
        var actor = Actor(caller);
        AccessRolesDataImport.Combined? combined = null;
        AccessRolesDataImport.LibraryMap? libraries = null;
        string? safety = null;

        Proposal Propose(WriteContext context)
        {
            var map = AccessRolesDataImport.MapLibraries(context.Document, incoming, context.Catalog);
            var result = AccessRolesDataImport.Combine(context.Document, incoming, mode, users, map, source.SameServer, checkRoles: source.Kind == SourceExport);
            libraries = map;
            combined = result;
            var ledgerAdded = result.Document.Ledger.Keys.Where(id => !context.Document.Ledger.ContainsKey(id)).ToList();
            return new Proposal(result.Document, source.Kind == SourceSnapshot ? "Restore" : "Import")
            {
                PreviewLines = new Dictionary<string, string>(StringComparer.Ordinal) { [source.Kind] = PreviewLine(result.Document, ledgerAdded) },
                Lines = report =>
                [
                    .. RoleLines(context.Actor, context.Document, result.Document, report, context.Catalog, source.Kind),
                    new AuditRecord(context.Actor, AuditActions.ImportApplied, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Source"] = source.Kind,
                        ["SourceName"] = source.Name,
                        ["SourceUtc"] = source.Utc,
                        ["SchemaVersion"] = source.SchemaVersion,
                        ["SameServer"] = source.SameServer,
                        ["Mode"] = mode.ToString(),
                        ["MapUsersBy"] = users.By.ToString(),
                        ["Roles"] = result.Roles.ToDictionary(),
                        ["Assignments"] = result.Assignments.ToDictionary(),
                        ["LedgerAdded"] = AccessRolesAuditDetails.Ids(ledgerAdded),
                        ["UsersMapped"] = users.Mapped.Count,
                        ["UsersUnknown"] = users.Unknown.Select(u => u.ToDictionary()).ToList(),
                        ["LibrariesUnmatched"] = map.Unmatched.Values.Select(Unmatched).ToList(),
                        ["Skipped"] = result.Skipped.Count,
                        ["Users"] = report.Affected.Count,
                        ["SafetySnapshot"] = safety is null ? null : Path.GetFileName(safety)
                    })
                ]
            };
        }

        // The dry run, or the save's check against the stored document with nothing written: ifPreview first, then the
        // refusals, as the save's own evaluation orders them; a refused save leaves no snapshot and no hold behind.
        var check = await WriteAsync(new AccessRolesWriteOptions { DryRun = true, OnUnmanage = options.OnUnmanage, ConfirmNoFullAdmin = options.ConfirmNoFullAdmin }, actor, Propose, refuse: false, cancellationToken).ConfigureAwait(false);
        var unchanged = ReferenceEquals(combined!.Document, check.Stored);
        if (!options.DryRun && !string.IsNullOrWhiteSpace(options.IfPreview))
        {
            RequirePreview(check.Report, options.IfPreview);
        }

        if (options.DryRun)
        {
            try
            {
                Refuse(check.Report, options);
            }
            catch (AccessRolesException refused) when (refused.Preview is null)
            {
                throw refused.WithPreview(AccessRolesMapper.Preview(check.Report));
            }

            return Answer(check, combined, libraries!, users, source, mode, unchanged, safety: null, held: false);
        }

        Refuse(check.Report, options);
        if (unchanged)
        {
            // Nothing to save: no snapshot, no hold, no audit line.
            var nothing = Answer(check, combined, libraries!, users, source, mode, unchanged: true, safety: null, held: false);
            nothing.DryRun = false;
            return nothing;
        }

        _store.Documents.ThrowIfReadOnlyForAdmin();

        // The hold is written before the save takes the store's writer (program order: Begin returns first), so a sync run or
        // a sign-in that checks it under the writer, or after reading the document, never writes the restored access.
        // A failure takes the hold back only when this restore certainly saved nothing:
        // a cancelled request, or a write that failed once the store had the document, keeps it. It takes back only its own
        // hold (since 1.0.1: the file's owner is this restore's), putting back an earlier restore's hold it replaced.
        var hold = restore is null ? null : RestoreHold.Begin(support.Paths, _store, _timeProvider.GetUtcNow().UtcDateTime, restore);
        var progress = new SaveProgress();
        WriteResult saved;
        try
        {
            try
            {
                safety = await _store.Documents.PreOpSnapshotAsync(SnapshotDirectory(), restore is null ? SnapshotOperations.Import : SnapshotOperations.Restore, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Access Roles couldn't write the snapshot before an {Operation}", restore is null ? "import" : "restore");
                throw new PluginException(PluginErrorKind.Unavailable, "The snapshot of the data before the " + (restore is null ? "import" : "restore") + " couldn't be written, so nothing was changed. The server log has the details.", ex);
            }

            saved = await WriteAsync(options, actor, Propose, refuse: true, cancellationToken, progress: progress).ConfigureAwait(false);
        }
        catch
        {
            if (hold is not null && !progress.MayHaveSaved)
            {
                RestoreHold.Cancel(support.Paths, _store, hold, _logger);
            }

            throw;
        }

        _logger.LogWarning(
            "An admin {Action} the Access Roles data ({Mode}): {Affected} users' access changes, {Lines} audit lines; the state before was saved as {Safety}",
            restore is null ? "imported" : "restored a snapshot of",
            mode,
            saved.Report.Affected.Count,
            saved.AuditSeq.Count,
            Path.GetFileName(safety));
        return Answer(saved, combined!, libraries!, users, source, mode, unchanged: false, safety, held: restore is not null && RestoreHold.IsHeld(support.Paths, _store));
    }

    private static Dictionary<string, object?> Unmatched(AccessRolesDataImport.UnmatchedLibrary library) => Dictionary(
        ("ExportId", AccessRolesAuditDetails.Id(library.ExportId)),
        ("Name", library.Name),
        ("Locations", library.Locations),
        ("Reason", library.Reason));

    private AccessRolesDataImportDto Answer(
        WriteResult result,
        AccessRolesDataImport.Combined combined,
        AccessRolesDataImport.LibraryMap libraries,
        ImportUserMap users,
        DataSource source,
        ImportMode mode,
        bool unchanged,
        string? safety,
        bool held)
    {
        var before = result.Stored ?? _store.Current;
        var notes = new List<string>();
        if (source.Settings)
        {
            notes.Add("The export holds the settings for reference; an import never applies them (the Settings tab changes them).");
        }

        if (!source.SameServer && combined.Ledger.Incoming > 0)
        {
            notes.Add("The export is from another server, so its ledger (each user's access before Access Roles managed them) isn't imported: this server records its own.");
        }

        if (libraries.Unmatched.Count > 0)
        {
            notes.Add("The libraries listed under Unmatched aren't on this server, or more than one library matches: nothing here grants them. Grant the right ones on the Roles tab.");
        }

        if (users.Unknown.Count > 0)
        {
            notes.Add("The users listed under Unknown aren't on this server, or were deleted: nothing is written for them.");
        }

        if (source.Kind == SourceSnapshot && !unchanged)
        {
            notes.Add(held
                ? "The library sync is held after the restore: nothing is written to users' library access until you see the sync's dry run and confirm it on the Overview."
                : "After the restore the library sync is held until you see its dry run and confirm it on the Overview.");
        }

        if (result.AuditNote is { } auditNote)
        {
            notes.Add(auditNote);
        }

        return new AccessRolesDataImportDto
        {
            DryRun = result.DryRun,
            Mode = mode.ToString(),
            MapUsersBy = users.By.ToString(),
            Source = source.Kind,
            SourceName = source.Name,
            SourceUtc = source.Utc,
            SchemaVersion = source.SchemaVersion,
            SameServer = source.SameServer,
            Users = Dictionary(
                ("Mapped", users.Mapped.Select(m => m.ToDictionary()).ToList()),
                ("Unknown", users.Unknown.Select(u => u.ToDictionary()).ToList())),
            Libraries = Dictionary(
                ("Mapped", libraries.Mapped.Values.Select(m => Dictionary(
                    ("ExportId", AccessRolesAuditDetails.Id(m.ExportId)),
                    ("Id", AccessRolesAuditDetails.Id(m.Id)),
                    ("Name", m.Name),
                    ("By", m.By))).ToList()),
                ("Unmatched", libraries.Unmatched.Values.Select(Unmatched).ToList())),
            Records = Dictionary(
                ("Roles", combined.Roles.ToDictionary()),
                ("Assignments", combined.Assignments.ToDictionary()),
                ("DefaultRoles", Dictionary(("Before", AccessRolesAuditDetails.Ids(before.DefaultRoleIds)), ("After", AccessRolesAuditDetails.Ids(combined.Document.DefaultRoleIds)))),
                ("Ledger", Dictionary(("Incoming", combined.Ledger.Incoming), ("Added", combined.Ledger.Added), ("SameServer", combined.Ledger.SameServer))),
                ("MirrorRecords", combined.MirrorRecords)),
            Skipped = [.. combined.Skipped.Select(s => Dictionary(
                ("Kind", s.Kind),
                ("Id", AccessRolesAuditDetails.Id(s.Id)),
                ("Name", s.Name),
                ("Reason", s.Reason),
                ("Of", s.Of),
                ("Libraries", s.Libraries?.Select(Unmatched).ToList()),
                ("Detail", s.Detail)))],
            Preview = AccessRolesMapper.Preview(result.Report),
            NoChange = unchanged,
            SafetySnapshot = safety is null ? null : Path.GetFileName(safety),
            SyncHeld = held,
            Notes = notes,
            AuditSeq = result.AuditSeq
        };
    }

    /// <summary>Where an import's document comes from.</summary>
    /// <param name="Kind"><c>Export</c> or <c>Snapshot</c>.</param>
    /// <param name="Name">The plugin version that wrote the export, or the snapshot's name.</param>
    /// <param name="Utc">When it was written.</param>
    /// <param name="SchemaVersion">The schema version it was written at.</param>
    /// <param name="SameServer">Whether it is this server's.</param>
    /// <param name="Settings">Whether it holds the settings (never applied).</param>
    private sealed record DataSource(string Kind, string? Name, DateTime Utc, int SchemaVersion, bool SameServer, bool Settings);
}
