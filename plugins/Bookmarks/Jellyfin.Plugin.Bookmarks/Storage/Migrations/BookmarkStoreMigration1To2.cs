using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Bookmarks.Storage.Migrations;

/// <summary>
/// Upgrades <c>bookmarks.json</c> from schema 1 (Bookmarks 1.0) to schema 2, inside the kit store's load: the
/// store has already copied the file to <c>bookmarks.json.v1-&lt;yyyyMMddHHmmss&gt;</c>, and it saves the result at once.
/// </summary>
/// <remarks>
/// <para>For each record: a short single-line note becomes the name; <c>ItemType</c>, <c>ItemName</c> and
/// <c>ItemProviderIds</c> become the <c>Identity</c> snapshot; <c>Origin</c> is <c>Native</c>; <c>AttachedUtc</c> is the
/// record's <c>CreatedAt</c> (1.0 never relinked). For the document: <c>ClipRoots</c> from the default and configured clip
/// folders and every clip file's folder outside them, empty preferences, ledger and pending deletes, no move-journal
/// position, and <c>KnownUsers</c> for the user ids this server knows.</para>
/// <para>Written for real 1.0 data (1.0.0 may be installed on a server in use before 1.1.0): every shape 1.0.0 writes, and
/// the kit build of 1.0 with unknown fields it kept. Fields schema 2 defines are removed from 1.0 records first, so a
/// hand-added one can't clash. Reading the server's users or settings never fails the upgrade: a user the server can't
/// name is left out of <c>KnownUsers</c> (the reconciler lists it), a setting that can't be read leaves the configured
/// folder out. Anything else unexpected throws, and the kit then leaves the file untouched and the store read-only
/// (<c>ReadOnlyMigrationFailed</c>).</para>
/// <para>The kit also runs this step on an older export's data (<c>JsonDocumentStore.ReadData</c>), where the clip files,
/// and so the <c>ClipRoots</c> seeded from them, are the export's and can point anywhere. An import keeps the target's own
/// <c>ClipRoots</c> and never takes the migrated export's.</para>
/// </remarks>
internal sealed class BookmarkStoreMigration1To2 : IJsonMigration
{
    // Fields schema 2 adds to a record and to its clip.
    private static readonly string[] _recordFieldsV2 = ["Name", "Identity", "Origin", "AttachedUtc", "DetachedUtc", "RelinkedUtc", "RelinkStrength", "DeletedUtc"];
    private static readonly string[] _clipFieldsV2 = ["MissingSinceUtc", "EstimatedBytes"];
    private static readonly string[] _documentFieldsV2 = ["ClipRoots", "UserPreferences", "ImportLedger", "KnownUsers", "MoveJournalId", "LastMoveSeq", "PendingFileDeletes"];

    private readonly string _defaultClipRoot;
    private readonly Func<string?> _configuredClipRoot;
    private readonly Func<Guid, string?> _userName;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkStoreMigration1To2"/> class.
    /// </summary>
    /// <param name="defaultClipRoot">The folder clips go to without a configured one (<see cref="BookmarkPaths.DefaultClipsDirectory"/>).</param>
    /// <param name="configuredClipRoot">Reads the configured clip folder (<c>ClipOutputPath</c>); blank for none.</param>
    /// <param name="userName">Finds the name of a user this server knows, else <c>null</c>.</param>
    /// <param name="logger">The logger.</param>
    public BookmarkStoreMigration1To2(string defaultClipRoot, Func<string?> configuredClipRoot, Func<Guid, string?> userName, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(defaultClipRoot);
        ArgumentNullException.ThrowIfNull(configuredClipRoot);
        ArgumentNullException.ThrowIfNull(userName);
        _defaultClipRoot = defaultClipRoot;
        _configuredClipRoot = configuredClipRoot;
        _userName = userName;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public int FromVersion => 1;

    /// <summary>
    /// Creates the step with the plugin's paths, settings and the server's users, as the plugin runs it.
    /// </summary>
    /// <param name="paths">The plugin's paths.</param>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="userManager">Gets the server's user manager, only when a 1.0 file is upgraded (the store is created
    /// early, with the hosted clip service, and the upgrade runs on its first use).</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The step.</returns>
    public static BookmarkStoreMigration1To2 Create(BookmarkPaths paths, IPluginConfigurationProvider configuration, Func<IUserManager> userManager, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(userManager);
        return new BookmarkStoreMigration1To2(
            paths.DefaultClipsDirectory,
            () => configuration.GetConfiguration().ClipOutputPath,
            id => KitUsers.Find(userManager(), id)?.Username,
            logger);
    }

    /// <inheritdoc />
    public void Apply(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<JsonObject> records = root["Bookmarks"] switch
        {
            null => [],
            JsonArray array => array.OfType<JsonObject>().ToList(),
            _ => throw new InvalidDataException("Bookmarks isn't a list.")
        };

        var named = 0;
        var userIds = new List<Guid>();
        var clipFiles = new List<string>();
        foreach (var record in records)
        {
            foreach (var field in _recordFieldsV2)
            {
                record.Remove(field);
            }

            if (MoveNoteToName(record))
            {
                named++;
            }

            record["Identity"] = BuildIdentity(record);
            record["Origin"] = nameof(BookmarkOrigin.Native);
            if (record["CreatedAt"] is JsonValue created)
            {
                record["AttachedUtc"] = created.DeepClone();
            }

            if (ReadGuid(record["UserId"]) is { } userId && !userId.Equals(Guid.Empty))
            {
                userIds.Add(userId);
            }

            if (record["Clip"] is JsonObject clip)
            {
                foreach (var field in _clipFieldsV2)
                {
                    clip.Remove(field);
                }

                if (ReadString(clip["FilePath"]) is { } filePath)
                {
                    clipFiles.Add(filePath);
                }
            }
        }

        foreach (var field in _documentFieldsV2)
        {
            root.Remove(field);
        }

        var clipRoots = SeedClipRoots(clipFiles);
        var knownUsers = SeedKnownUsers(userIds);

        // Document fields in schema 2's order; the store writes them in the document's declared order anyway.
        root["ClipRoots"] = new JsonArray(clipRoots.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        root["UserPreferences"] = new JsonObject();
        root["ImportLedger"] = new JsonObject();
        root["KnownUsers"] = JsonSerializer.SerializeToNode(knownUsers, JsonStoreFormat.Options);
        root["LastMoveSeq"] = 0;
        root["PendingFileDeletes"] = new JsonArray();

        _logger.LogInformation(
            "Upgrading the bookmark data to schema 2: {Records} bookmarks, {Named} notes became names, {ClipRoots} clip folders, {KnownUsers} of {Users} users named",
            records.Count,
            named,
            clipRoots.Count,
            knownUsers.Count,
            userIds.Distinct().Count());
    }

    // A note that is non-blank, at most 100 characters once trimmed and on one line becomes the name, put through the
    // name rules; anything else stays the note and the bookmark is unnamed.
    private static bool MoveNoteToName(JsonObject record)
    {
        if (ReadString(record["Note"]) is not { } note)
        {
            return false;
        }

        var trimmed = note.Trim();
        if (trimmed.Length == 0 || trimmed.Length > BookmarkNames.MaxNameLength || trimmed.Contains('\r', StringComparison.Ordinal) || trimmed.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        if (!BookmarkNames.TryNormalizeName(trimmed, out var name, out _) || name is null)
        {
            return false;
        }

        record["Name"] = name;
        record.Remove("Note");
        return true;
    }

    // { ItemId, Kind: ItemType ?? "Video", Name: ItemName, ProviderIds: ItemProviderIds ?? {} }; ItemType and
    // ItemProviderIds are removed. Name holds the display name until the identity backfill replaces it.
    private static JsonObject BuildIdentity(JsonObject record)
    {
        var kind = ReadString(record["ItemType"]);
        var providerIds = new JsonObject();
        if (record["ItemProviderIds"] is JsonObject ids)
        {
            foreach (var (provider, value) in ids)
            {
                // 1.0 wrote strings; a hand-edited number is kept as its text, anything else is left out.
                if (value is JsonValue scalar && scalar.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                {
                    providerIds[provider] = scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : scalar.ToJsonString();
                }
            }
        }

        record.Remove("ItemType");
        record.Remove("ItemProviderIds");

        var identity = new JsonObject();
        if (record["ItemId"] is JsonValue itemId)
        {
            identity["ItemId"] = itemId.DeepClone();
        }

        identity["Kind"] = string.IsNullOrWhiteSpace(kind) ? "Video" : kind.Trim();
        identity["Name"] = ReadString(record["ItemName"]) ?? string.Empty;
        identity["ProviderIds"] = providerIds;
        return identity;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static Guid? ReadGuid(JsonNode? node) => Guid.TryParse(ReadString(node), out var id) ? id : null;

    // The default folder, the configured one if set, then the folder of every clip file outside those (the
    // shortest first, so a folder under another one adds nothing), distinct, at most MaxClipRoots.
    private List<string> SeedClipRoots(IEnumerable<string> clipFiles)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (ClipRootPaths.TryNormalize(path, out var root) && !roots.Contains(root, ClipRootPaths.Comparer))
            {
                roots.Add(root);
            }
        }

        Add(_defaultClipRoot);
        Add(ReadConfiguredClipRoot());

        var folders = clipFiles
            .Select(file => ClipRootPaths.TryNormalize(file, out var path) ? Path.GetDirectoryName(path) : null)
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Select(folder => folder!)
            .Distinct(ClipRootPaths.Comparer)
            .OrderBy(folder => folder.Length)
            .ThenBy(folder => folder, StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!ClipRootPaths.IsUnderAny(folder, roots))
            {
                Add(folder);
            }
        }

        if (roots.Count > BookmarkStoreDocument.MaxClipRoots)
        {
            _logger.LogWarning(
                "Clip files lie in {Count} folders; only the first {Max} are recorded as clip folders, so clips in the others can't be deleted or renamed through Bookmarks",
                roots.Count,
                BookmarkStoreDocument.MaxClipRoots);
            roots.RemoveRange(BookmarkStoreDocument.MaxClipRoots, roots.Count - BookmarkStoreDocument.MaxClipRoots);
        }

        return roots;
    }

    private string? ReadConfiguredClipRoot()
    {
        try
        {
            return _configuredClipRoot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read the configured clip folder; only the default clip folder and the clip files' folders are recorded");
            return null;
        }
    }

    private KnownUsers SeedKnownUsers(IEnumerable<Guid> userIds)
    {
        var names = new List<KeyValuePair<Guid, string>>();
        foreach (var id in userIds.Distinct())
        {
            try
            {
                if (_userName(id) is { } name)
                {
                    names.Add(new KeyValuePair<Guid, string>(id, name));
                }
            }
            catch (Exception ex)
            {
                // The reconciler names it later, or lists it as unknown.
                _logger.LogWarning(ex, "Couldn't look up user {UserId} while upgrading the bookmark data", id);
            }
        }

        return KnownUsers.From(names);
    }
}
