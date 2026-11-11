using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The answer of <c>POST /&lt;Route&gt;/Admin/Import</c> (a plugin maps it to its <c>&lt;P&gt;ImportReportDto</c> or
/// returns <see cref="ToDictionary"/>).
/// </summary>
internal sealed record ImportReport
{
    /// <summary>Gets a value indicating whether nothing was written.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Gets the mode.</summary>
    public required ImportMode Mode { get; init; }

    /// <summary>Gets how users were matched.</summary>
    public required MapUsersBy MapUsersBy { get; init; }

    /// <summary>Gets the exporting plugin's version.</summary>
    public required string SourcePluginVersion { get; init; }

    /// <summary>Gets the exporting server's id, or <c>null</c>.</summary>
    public string? SourceServerId { get; init; }

    /// <summary>Gets when the export was made.</summary>
    public required DateTime ExportedUtc { get; init; }

    /// <summary>Gets the export's schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the version the data was upgraded from, or <c>null</c> when it was current.</summary>
    public int? MigratedFrom { get; init; }

    /// <summary>Gets the export's users found on this server.</summary>
    public required IReadOnlyList<ImportUserMapping> MappedUsers { get; init; }

    /// <summary>Gets the export's users not found, whose records were skipped.</summary>
    public required IReadOnlyList<ImportUnknownUser> UnknownUsers { get; init; }

    /// <summary>Gets what happened to the records.</summary>
    public required ImportCounts Records { get; init; }

    /// <summary>Gets what happened to the archived records.</summary>
    public required ArchiveImportResult Archive { get; init; }

    /// <summary>Gets a value indicating whether the export carried the plugin's settings (an import never applies them).</summary>
    public bool SettingsIncluded { get; init; }

    /// <summary>Gets the pre-operation snapshot taken before the import wrote, or <c>null</c>.</summary>
    public string? SafetySnapshot { get; init; }

    /// <summary>Gets the plugin's notes.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Describes the report with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["DryRun"] = DryRun,
        ["Mode"] = Mode.ToString(),
        ["MapUsersBy"] = MapUsersBy.ToString(),
        ["SourcePluginVersion"] = SourcePluginVersion,
        ["SourceServerId"] = SourceServerId,
        ["ExportedUtc"] = ExportedUtc,
        ["SchemaVersion"] = SchemaVersion,
        ["MigratedFrom"] = MigratedFrom,
        ["Users"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Mapped"] = MappedUsers.Select(u => u.ToDictionary()).ToList(),
            ["Unknown"] = UnknownUsers.Select(u => u.ToDictionary()).ToList()
        },
        ["Records"] = Records.ToDictionary(),
        ["Archive"] = Archive.ToDictionary(),
        ["Settings"] = SettingsIncluded ? "Included in the export for reference; an import never applies settings." : "Not in the export.",
        ["SafetySnapshot"] = SafetySnapshot,
        ["Notes"] = Notes
    };
}
