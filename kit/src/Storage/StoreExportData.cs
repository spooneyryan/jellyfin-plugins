using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// A store's data for an export's <c>Data</c>, with the schema version it is written at (<c>SchemaVersion</c>), from
/// <c>JsonDocumentStore.ExportData</c>, so the two always agree.
/// </summary>
/// <param name="SchemaVersion">The schema version of <paramref name="Data"/>.</param>
/// <param name="Data">The document as JSON, in the file's format.</param>
internal sealed record StoreExportData(int SchemaVersion, JsonObject Data);
