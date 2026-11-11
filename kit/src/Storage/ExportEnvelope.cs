using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The export file of every plugin, needed because Jellyfin 10.11's backup leaves plugin data and
/// settings out:
/// <code>
/// { "Format": "jellyfin-plugin-export/1", "Plugin": "Bookmarks", "PluginId": "d0ee1b2a-…", "PluginVersion": "1.1.0.0",
///   "SchemaVersion": 2, "ExportedUtc": "2026-10-01T18:00:00Z", "ServerId": "&lt;N&gt;",
///   "Users": [ { "Id": "&lt;N&gt;", "Name": "alex" }, { "Id": "&lt;N&gt;", "Name": "alice", "Deleted": true } ],
///   "Settings": { … }, "Data": { … }, "Archive": [ … ] }
/// </code>
/// <c>Data</c> is the store document in its file format at <c>SchemaVersion</c>, <c>Archive</c> the archived records,
/// <c>Settings</c> the plugin configuration for reference (an import never applies it). <c>Deleted</c> marks a user
/// deleted on the exporting server, whose records an import never gives to anyone by name. Served as an attachment with
/// <c>Cache-Control: no-store</c>; read back from a plain <c>application/json</c> body of at most 100 MB, never
/// <c>IFormFile</c>.
/// </summary>
internal sealed record ExportEnvelope
{
    /// <summary>The <c>Format</c> of version 1.</summary>
    public const string FormatV1 = "jellyfin-plugin-export/1";

    /// <summary>The largest import body: 100 MB.</summary>
    public const long MaxImportBytes = 100L * 1024 * 1024;

    /// <summary>The 413 message.</summary>
    public const string TooLargeMessage = "An import file is at most 100 MB.";

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Gets the format marker, <see cref="FormatV1"/>.</summary>
    public string Format { get; init; } = FormatV1;

    /// <summary>Gets the exporting plugin's name.</summary>
    public required string Plugin { get; init; }

    /// <summary>Gets the exporting plugin's id; an import checks it.</summary>
    public required Guid PluginId { get; init; }

    /// <summary>Gets the exporting plugin's version.</summary>
    public required string PluginVersion { get; init; }

    /// <summary>Gets the schema version of <see cref="Data"/>.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets when the export was made.</summary>
    public required DateTime ExportedUtc { get; init; }

    /// <summary>Gets the exporting server's id, or <c>null</c>.</summary>
    public string? ServerId { get; init; }

    /// <summary>Gets the users the data holds records of, with their names.</summary>
    public IReadOnlyList<ExportUser> Users { get; init; } = [];

    /// <summary>Gets the plugin configuration as JSON, or <c>null</c>.</summary>
    public JsonObject? Settings { get; init; }

    /// <summary>Gets the store document.</summary>
    public required JsonObject Data { get; init; }

    /// <summary>Gets the archived records, in the records' shape at <see cref="SchemaVersion"/>.</summary>
    public IReadOnlyList<JsonObject> Archive { get; init; } = [];

    /// <summary>
    /// Creates the export of a plugin's data.
    /// </summary>
    /// <param name="options">The plugin's kit options (name, id, assembly version).</param>
    /// <param name="data">The store's data and its schema version (<c>JsonDocumentStore.ExportData</c>).</param>
    /// <param name="users">The users of the data (<see cref="ExportUser.For"/>).</param>
    /// <param name="settings">The plugin configuration, serialized in the store format; <c>null</c> for none.</param>
    /// <param name="archive">The archived records; none by default.</param>
    /// <param name="serverId">The server's id (<c>IServerApplicationHost.SystemId</c>).</param>
    /// <param name="exportedUtc">The export time; now by default.</param>
    /// <returns>The envelope.</returns>
    public static ExportEnvelope Create(
        KitOptions options,
        StoreExportData data,
        IEnumerable<ExportUser> users,
        object? settings = null,
        IEnumerable<JsonObject>? archive = null,
        string? serverId = null,
        DateTime? exportedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(users);
        if (options.PluginId.Equals(Guid.Empty))
        {
            throw new InvalidOperationException("KitOptions.PluginId must be set to export data.");
        }

        return new ExportEnvelope
        {
            Plugin = options.PluginName,
            PluginId = options.PluginId,
            PluginVersion = IndexHtmlMarkers.Version(options.PluginAssembly),
            SchemaVersion = data.SchemaVersion,
            ExportedUtc = Truncate(exportedUtc ?? DateTime.UtcNow),
            ServerId = string.IsNullOrWhiteSpace(serverId) ? null : serverId,
            Users = users.ToList(),
            Settings = settings is null ? null : JsonSerializer.SerializeToNode(settings, settings.GetType(), JsonStoreFormat.Options) as JsonObject,
            Data = data.Data,
            Archive = archive?.ToList() ?? []
        };
    }

    /// <summary>
    /// Reads an export from a request body: at most <paramref name="maxBytes"/>, as JSON.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="maxBytes">The limit; 100 MB by default.</param>
    /// <returns>The envelope.</returns>
    /// <exception cref="PluginException">413 over the limit; 400 not an export.</exception>
    public static async Task<ExportEnvelope> ReadAsync(HttpRequest request, CancellationToken cancellationToken, long maxBytes = MaxImportBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentLength > maxBytes)
        {
            throw PluginException.PayloadTooLarge(TooLargeMessage);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw PluginException.PayloadTooLarge(TooLargeMessage);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new PluginException(PluginErrorKind.PayloadTooLarge, TooLargeMessage, ex);
        }

        if (buffer.Length == 0)
        {
            throw PluginException.Invalid("The body is empty; send the export file as application/json.");
        }

        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>
    /// Parses an export file.
    /// </summary>
    /// <param name="utf8">The file's bytes; a byte order mark is skipped.</param>
    /// <returns>The envelope.</returns>
    /// <exception cref="PluginException">400: not JSON, or not an export of this format.</exception>
    public static ExportEnvelope Parse(ReadOnlySpan<byte> utf8)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(JsonStoreFormat.SkipByteOrderMark(utf8)) as JsonObject
                ?? throw PluginException.Invalid("The body isn't an export: it is not a JSON object.");

            // JsonObject builds its property table lazily and refuses a name given twice; do it here.
            _ = root.Count;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new PluginException(PluginErrorKind.Invalid, "The body isn't valid JSON: " + ex.Message, ex);
        }

        if (!string.Equals(String(root, nameof(Format), required: false), FormatV1, StringComparison.Ordinal))
        {
            throw PluginException.Invalid($"The body isn't an export of the format {FormatV1}.");
        }

        var pluginId = String(root, nameof(PluginId), required: true);
        if (!Guid.TryParse(pluginId, out var id))
        {
            throw PluginException.Invalid("The export's PluginId isn't a GUID.");
        }

        if (root[nameof(SchemaVersion)] is not JsonValue schema || !schema.TryGetValue<int>(out var schemaVersion) || schemaVersion < 1)
        {
            throw PluginException.Invalid("The export's SchemaVersion must be a whole number of 1 or more.");
        }

        if (root[nameof(ExportedUtc)] is not JsonValue exported || !exported.TryGetValue<DateTime>(out var exportedUtc))
        {
            throw PluginException.Invalid("The export's ExportedUtc must be a date and time.");
        }

        var data = root[nameof(Data)] as JsonObject ?? throw PluginException.Invalid("The export has no Data object.");
        return new ExportEnvelope
        {
            Plugin = String(root, nameof(Plugin), required: true)!,
            PluginId = id,
            PluginVersion = String(root, nameof(PluginVersion), required: false) ?? string.Empty,
            SchemaVersion = schemaVersion,
            ExportedUtc = exportedUtc.Kind == DateTimeKind.Utc ? exportedUtc : exportedUtc.ToUniversalTime(),
            ServerId = String(root, nameof(ServerId), required: false),
            Users = ReadUsers(root[nameof(Users)]),
            Settings = root[nameof(Settings)] switch
            {
                null => null,
                JsonObject settings => (JsonObject)settings.DeepClone(),
                _ => throw PluginException.Invalid("The export's Settings must be an object.")
            },
            Data = (JsonObject)data.DeepClone(),
            Archive = ReadArchive(root[nameof(Archive)])
        };
    }

    /// <summary>
    /// Checks that the export is this plugin's and that this version can read its data.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="currentSchemaVersion">The schema version the store reads up to.</param>
    /// <exception cref="PluginException">400 another plugin's export; 409 a newer schema version.</exception>
    public void EnsureFor(KitOptions options, int currentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!PluginId.Equals(options.PluginId))
        {
            throw PluginException.Invalid($"This export is from {Plugin} ({PluginId:D}), not {options.PluginName}.");
        }

        if (SchemaVersion > currentSchemaVersion)
        {
            throw PluginException.Conflict(string.Create(
                CultureInfo.InvariantCulture,
                $"The export was written by a newer {options.PluginName} version (schema {SchemaVersion}; this version reads up to {currentSchemaVersion}). Install that version to import it."));
        }
    }

    /// <summary>
    /// Gets the envelope as JSON, in the export's property order: ids of users in the "N" format Jellyfin writes, the
    /// plugin id in the "D" format.
    /// </summary>
    /// <returns>A new JSON object.</returns>
    public JsonObject ToJson()
    {
        var users = new JsonArray();
        foreach (var user in Users)
        {
            var entry = new JsonObject { ["Id"] = KnownUsers.FormatId(user.Id) };
            if (user.Name is not null)
            {
                entry["Name"] = user.Name;
            }

            if (user.Deleted)
            {
                entry["Deleted"] = true;
            }

            users.Add(entry);
        }

        var root = new JsonObject
        {
            [nameof(Format)] = Format,
            [nameof(Plugin)] = Plugin,
            [nameof(PluginId)] = PluginId.ToString("D", CultureInfo.InvariantCulture),
            [nameof(PluginVersion)] = PluginVersion,
            [nameof(SchemaVersion)] = SchemaVersion,
            [nameof(ExportedUtc)] = ExportedUtc,
        };
        if (ServerId is not null)
        {
            root[nameof(ServerId)] = ServerId;
        }

        root[nameof(Users)] = users;
        if (Settings is not null)
        {
            root[nameof(Settings)] = Settings.DeepClone();
        }

        root[nameof(Data)] = Data.DeepClone();
        root[nameof(Archive)] = new JsonArray(Archive.Select(r => (JsonNode?)r.DeepClone()).ToArray());
        return root;
    }

    /// <summary>
    /// Serializes the envelope: indented UTF-8 without a byte order mark.
    /// </summary>
    /// <returns>The file's bytes.</returns>
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(ToJson(), JsonStoreFormat.Options);

    /// <summary>
    /// Answers <c>GET /&lt;Route&gt;/Admin/Export</c>: the file as an attachment named
    /// <c>&lt;stem&gt;-export-&lt;yyyyMMdd-HHmmss&gt;.json</c>, with <c>Cache-Control: no-store</c> (it holds users' names and
    /// notes).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="fileStem">The file name's start, for example the data folder <c>bookmarks</c>.</param>
    /// <returns>The result.</returns>
    public IActionResult ToFileResult(HttpContext context, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);
        context.Response.Headers.CacheControl = "no-store";
        return new FileContentResult(ToUtf8Bytes(), "application/json; charset=utf-8")
        {
            FileDownloadName = fileStem + "-export-" + ExportedUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".json"
        };
    }

    private static DateTime Truncate(DateTime utc)
    {
        var value = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private static string? String(JsonObject root, string name, bool required)
    {
        switch (root[name])
        {
            case null when !required:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            default:
                throw PluginException.Invalid($"The export's {name} must be a string.");
        }
    }

    private static List<ExportUser> ReadUsers(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw PluginException.Invalid("The export's Users must be an array.");
        }

        var users = new List<ExportUser>();
        foreach (var entry in array)
        {
            if (entry is not JsonObject user
                || user["Id"] is not JsonValue idValue
                || !idValue.TryGetValue<string>(out var idText)
                || !Guid.TryParse(idText, out var id))
            {
                throw PluginException.Invalid("Every entry of the export's Users needs an Id that is a GUID.");
            }

            string? name = null;
            if (user["Name"] is JsonValue nameValue && !nameValue.TryGetValue(out name))
            {
                throw PluginException.Invalid("A Name in the export's Users must be a string.");
            }

            var deleted = false;
            if (user["Deleted"] is { } deletedNode && (deletedNode is not JsonValue deletedValue || !deletedValue.TryGetValue(out deleted)))
            {
                throw PluginException.Invalid("A Deleted in the export's Users must be true or false.");
            }

            users.Add(new ExportUser(id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), deleted));
        }

        return users;
    }

    private static List<JsonObject> ReadArchive(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Any(r => r is not JsonObject))
        {
            throw PluginException.Invalid("The export's Archive must be an array of records.");
        }

        return array.Select(r => (JsonObject)r!.DeepClone()).ToList();
    }
}
