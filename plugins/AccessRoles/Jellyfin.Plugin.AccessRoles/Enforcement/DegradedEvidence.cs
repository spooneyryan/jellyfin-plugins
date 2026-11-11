using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// Reads the managed users from a copy of the data file leniently: only <c>Assignments[].UserId</c> and
/// <c>State</c>, which need no migration, so a file this build can't read whole (a newer version's, a schema broken
/// elsewhere) still names them; when <c>Assignments</c> can't be read (missing, not a list, or a list none of whose items
/// has a readable <c>UserId</c>: a structure renamed), the ledger's users (those whose access the plugin recorded), else
/// the mirror's. An assignment that isn't <c>Excluded</c> is managed, an unknown state included. Also the users whose
/// native preference access the optional mirror recorded turning off (<c>PreferenceMirror</c>'s keys),
/// so that a copy the store couldn't use still names the flags to put back.
/// </summary>
internal static class DegradedEvidence
{
    /// <summary>The largest file read: anything bigger isn't a data file.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private static readonly JsonDocumentOptions _lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    /// <summary>
    /// Finds the newest readable copy: the file itself, a copy of it moved aside as unreadable, its <c>.bak</c>, then the
    /// daily and pre-operation snapshots and the <c>.v&lt;N&gt;</c> pre-migration copies, newest first.
    /// </summary>
    /// <param name="store">The kit store.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The copy read, relative to the data folder, with its managed users and mirror records; <c>null</c> when none could be read.</returns>
    public static Found? Find(JsonDocumentStore<AccessRolesDocument> store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        var folder = Path.GetDirectoryName(store.FilePath)!;
        var kept = store.ListKeptFiles();
        var candidates = new List<string> { store.FilePath };
        candidates.AddRange(kept.Where(k => k.Kind == StoreKeptFileKind.Corrupt).Select(k => Path.Combine(folder, k.Name)));
        candidates.Add(store.FilePath + ".bak");
        var copies = new List<(DateTime Utc, string Path)>();
        try
        {
            copies.AddRange(SnapshotRotation.List(store.SnapshotDirectory, store.StoreName, ".json").Select(s => (s.Utc, s.Path)));
            copies.AddRange(SnapshotRotation.ListPreOp(store.SnapshotDirectory, store.StoreName, ".json").Select(s => (s.Utc, s.Path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Access Roles couldn't list its snapshots for Degraded mode");
        }

        copies.AddRange(kept.Where(k => k.Kind == StoreKeptFileKind.PreUpgradeCopy).Select(k => (k.Utc, Path.Combine(folder, k.Name))));
        candidates.AddRange(copies.OrderByDescending(c => c.Utc).Select(c => c.Path));

        foreach (var path in candidates)
        {
            if (Read(path, logger) is { } found)
            {
                return found with { Source = Path.GetRelativePath(folder, path) };
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the copies the store moved aside as unreadable at this start, which its status message names (a store restored
    /// from a snapshot, <see cref="StoreStatus.RestoredFromSnapshot"/>, or from its <c>.bak</c>,
    /// <see cref="StoreStatus.RecoveredFromBackup"/>): what they name that the copy used instead may not.
    /// </summary>
    /// <param name="store">The kit store.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>Their managed users and mirror records together, and their names; <c>null</c> when none was moved aside or none could be read.</returns>
    public static Found? MovedAside(JsonDocumentStore<AccessRolesDocument> store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        var message = store.StatusMessage;
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(store.FilePath)!;
        var users = new HashSet<Guid>();
        var mirrored = new HashSet<Guid>();
        var sources = new List<string>();
        foreach (var name in store.ListKeptFiles().Where(k => k.Kind == StoreKeptFileKind.Corrupt && Names(message, k.Name)).Select(k => k.Name).Order(StringComparer.Ordinal))
        {
            if (Read(Path.Combine(folder, name), logger) is { } found)
            {
                users.UnionWith(found.Users);
                mirrored.UnionWith(found.Mirrored);
                sources.Add(name);
            }
        }

        return sources.Count == 0 ? null : new Found(string.Join(", ", sources), users, mirrored);
    }

    /// <summary>
    /// Reads one copy.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>Its managed users (perhaps none) and mirror records, its file name as the source; <c>null</c> when it is missing or names none readably.</returns>
    public static Found? Read(string path, ILogger? logger = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return null;
            }

            return Parse(File.ReadAllBytes(path)) is { } found ? found with { Source = Path.GetFileName(path) } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Access Roles couldn't read {File} for Degraded mode", path);
            return null;
        }
    }

    /// <summary>
    /// Reads the managed users and the mirror's records from a file's bytes.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>What it names (no <see cref="Found.Source"/>), or <c>null</c> when neither the assignments, the ledger nor the mirror's records can be read.</returns>
    public static Found? Parse(ReadOnlyMemory<byte> bytes)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes.Span, documentOptions: _lenient);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject document)
        {
            return null;
        }

        var mirrored = Property(document, "PreferenceMirror") is JsonObject mirror ? Ids(mirror) : null;
        IReadOnlySet<Guid>? users = null;
        if (Property(document, "Assignments") is JsonArray assignments)
        {
            var found = new HashSet<Guid>();
            var readable = 0;
            foreach (var item in assignments)
            {
                if (item is JsonObject assignment && IdOf(Property(assignment, "UserId")) is { } id)
                {
                    readable++;
                    if (!IsExcluded(Property(assignment, "State")))
                    {
                        found.Add(id);
                    }
                }
            }

            // Items none of which has a readable UserId aren't assignments this build knows: the ledger names the users.
            users = readable > 0 || assignments.Count == 0 ? found : null;
        }

        if (users is null && Property(document, "Ledger") is JsonObject ledger)
        {
            users = Ids(ledger);
        }

        users ??= mirrored;
        return users is null ? null : new Found(null, users, mirrored ?? new HashSet<Guid>());
    }

    // Whether a status message names a kept copy: the name not followed by more of a name ("…corrupt-20261101190908" isn't
    // "…corrupt-20261101190908-1").
    private static bool Names(string message, string name)
    {
        for (var at = message.IndexOf(name, StringComparison.Ordinal); at >= 0; at = message.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            var end = at + name.Length;
            var more = end < message.Length && (char.IsLetterOrDigit(message[end]) || (message[end] == '-' && end + 1 < message.Length && char.IsDigit(message[end + 1])));
            if (!more)
            {
                return true;
            }
        }

        return false;
    }

    // A property in any letter case (the kit's format is PascalCase; a hand-edited file may not be).
    private static JsonNode? Property(JsonObject node, string name)
        => node.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static HashSet<Guid> Ids(JsonObject map)
        => map.Select(p => Guid.TryParse(p.Key, out var id) && !id.Equals(Guid.Empty) ? id : (Guid?)null).OfType<Guid>().ToHashSet();

    private static Guid? IdOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && !id.Equals(Guid.Empty) ? id : null;

    private static bool IsExcluded(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text?.Trim(), nameof(AccessRolesAssignmentState.Excluded), StringComparison.OrdinalIgnoreCase);

    /// <summary>What a lenient read found.</summary>
    /// <param name="Source">The copy or copies read, relative to the data folder, or <c>null</c> for bytes.</param>
    /// <param name="Users">The managed users.</param>
    /// <param name="Mirrored">The users whose native preference access the mirror recorded turning off.</param>
    internal sealed record Found(string? Source, IReadOnlySet<Guid> Users, IReadOnlySet<Guid> Mirrored);
}
