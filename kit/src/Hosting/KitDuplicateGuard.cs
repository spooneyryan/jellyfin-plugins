using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The duplicate guard: notices another copy of this plugin in a second folder under Jellyfin's plugins folder and
/// decides which copy runs for this start. <c>KitAbiGuard.Register</c> runs it, so every plugin has it.
/// </summary>
/// <remarks>
/// <para>Jellyfin installs a catalog package into <c>plugins/&lt;name&gt;_&lt;version&gt;</c> and, at start-up, removes
/// older versions of a plugin by its name only (<c>PluginManager.DiscoverPlugins</c>). After a plugin's name changed, the
/// copy installed under the old name stays Active beside the new one, and Jellyfin loads both: two assemblies with the same
/// name and id, each in its own load context. Both would register their services and read their settings, and Jellyfin's
/// XML serializer cache is keyed by the settings type's full name, so the second copy fails with an
/// <c>InvalidCastException</c> between the two copies' types, and the server doesn't start.</para>
/// <para>The guard reads the other folders from disk at registration, without loading anything. A <em>sibling</em> is a
/// folder beside this one with a DLL of this plugin's file name that Jellyfin loads: its <c>meta.json</c> is missing, or has
/// this plugin's id, a status Jellyfin loads and a <c>targetAbi</c> the server meets. Copies are ordered by version (the
/// <c>meta.json</c> version, else the assembly's), then by folder name. A copy with a higher sibling that runs on this
/// server stands down. The highest copy cleans up every lower copy once the server has started
/// (<see cref="KitDuplicateRemoval"/>: a loaded one is turned off and removed at a later start, one that isn't loaded is
/// removed at once), and stands down itself when a loaded lower copy is a build without this guard, which runs regardless. Standing down is the ABI guard's inert mode: only <see cref="KitAbiStatus"/> is registered (and the
/// removal), controllers drop out of routing (<see cref="Api.KitAbiGateAttribute"/>) and tasks return at once.</para>
/// <para>Other copies find the guard by this type's full name in the assembly's metadata, so the name
/// <c>Jellyfin.Plugin.Kit.Hosting.KitDuplicateGuard</c> must never change.</para>
/// </remarks>
internal static class KitDuplicateGuard
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "DuplicateGuard";

    /// <summary>The namespace of the type other copies look for.</summary>
    public const string MarkerNamespace = "Jellyfin.Plugin.Kit.Hosting";

    /// <summary>The name of the type other copies look for.</summary>
    public const string MarkerName = nameof(KitDuplicateGuard);

    /// <summary>The file Jellyfin keeps a plugin's manifest in.</summary>
    public const string ManifestFileName = "meta.json";

    // The version Jellyfin gives a manifest whose version or targetAbi doesn't parse (PluginManager._minimumVersion).
    private static readonly Version MinimumVersion = new(0, 0, 0, 1);

    private static volatile bool _isStandingDown;

    /// <summary>
    /// Gets a value indicating whether this copy of the plugin stands down because another copy of it was loaded too. The
    /// plugin's <c>Plugin</c> class reads it: a copy that stands down never loads or saves its settings, since Jellyfin's
    /// settings serializer is shared with the other copy.
    /// </summary>
    public static bool IsStandingDown => _isStandingDown;

    /// <summary>Records that this copy stands down; <see cref="KitAbiGuard.RegisterAt"/> calls it for the real assembly only.</summary>
    internal static void MarkStandingDown() => _isStandingDown = true;

    /// <summary>
    /// Throws when this copy stands down (<see cref="IsStandingDown"/>): for the plugin's <c>SaveConfiguration</c>, so a
    /// save through the Dashboard fails visibly instead of writing the settings file with the other copy's serializer.
    /// </summary>
    /// <param name="pluginName">The plugin's display name, for the message.</param>
    /// <exception cref="InvalidOperationException">This copy stands down.</exception>
    public static void ThrowIfStandingDown(string pluginName)
    {
        if (_isStandingDown)
        {
            throw new InvalidOperationException(
                $"This copy of {pluginName} is inactive because another copy of it was loaded too, so it can't save settings. Restart Jellyfin to finish the update, then try again.");
        }
    }

    /// <summary>
    /// Reads the plugins folder and decides for the copy at <paramref name="assemblyPath"/>. Throws on an unreadable plugins
    /// folder; the caller treats that as no sibling.
    /// </summary>
    /// <param name="pluginName">The plugin's display name, for the message.</param>
    /// <param name="assemblyPath">This copy's assembly file.</param>
    /// <param name="pluginId">The plugin's id.</param>
    /// <param name="server">The server's version.</param>
    /// <param name="ownRunsOnServer">Whether this copy's ABI guard accepted the server.</param>
    /// <param name="jellyfin12Rules">Whether to apply Jellyfin 12's rule for superseded copies (else 10.11's).</param>
    /// <param name="loadedByServer">Whether Jellyfin loaded the copy in a folder, from its plugin manager; without it the
    /// guard works it out from each copy's status, which can't see what Jellyfin removed or deleted in part at this start.</param>
    /// <returns>The verdict.</returns>
    public static KitDuplicateVerdict Inspect(string pluginName, string assemblyPath, Guid pluginId, Version server, bool ownRunsOnServer, bool jellyfin12Rules, Func<string, bool>? loadedByServer = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        ArgumentNullException.ThrowIfNull(server);

        var fullPath = Path.GetFullPath(assemblyPath);
        var ownFolder = Path.GetDirectoryName(fullPath);
        var parent = ownFolder is null ? null : Path.GetDirectoryName(ownFolder);
        if (ownFolder is null || parent is null)
        {
            return KitDuplicateVerdict.None;
        }

        var fileName = Path.GetFileName(fullPath);
        var own = ReadCopy(ownFolder, fileName, pluginId, server, isOwn: true);
        if (own is null)
        {
            return KitDuplicateVerdict.None;
        }

        var copies = Directory.EnumerateDirectories(parent)
            .Where(folder => !SameFolder(folder, ownFolder))
            .Select(folder => ReadCopy(folder, fileName, pluginId, server, isOwn: false))
            .OfType<KitPluginCopy>()
            .ToList();
        var all = copies.Append(own).ToList();
        foreach (var copy in copies)
        {
            copy.IsLoaded = loadedByServer is null ? Loads(copy, own, all, jellyfin12Rules) : loadedByServer(copy.Folder);
        }

        own.IsLoaded = true;
        var siblings = copies.Where(copy => copy.IsLoaded).ToList();
        if (!ownRunsOnServer)
        {
            // Inert anyway (the ABI guard's message stands): nothing is removed in favour of a build that can't run here.
            return new KitDuplicateVerdict(own, siblings, siblings.Count > 0, [], string.Empty);
        }

        var higher = siblings.Where(s => s.CompareTo(own) > 0 && s.RunsOn(server)).ToList();
        if (higher.Count > 0)
        {
            var top = higher.Aggregate((a, b) => a.CompareTo(b) >= 0 ? a : b);
            return new KitDuplicateVerdict(
                own,
                siblings,
                true,
                [],
                $"{pluginName} {own.Version} in the plugin folder '{own.FolderName}' is inactive for this start: a newer copy of it, "
                + $"{top.Version} in '{top.FolderName}', was loaded too. The newer copy turns this one off. Restart Jellyfin to finish the update.");
        }

        // Every lower copy is cleaned up, loaded or not: Jellyfin's catalog refresh sets a superseded copy Active again
        // (PluginManager.PopulateManifest keeps only Disabled), so one left behind would load at a later start. A copy marked
        // Deleted is Jellyfin's to delete.
        var order = Comparer<KitPluginCopy>.Create((a, b) => a.CompareTo(b));
        var toRemove = copies.Where(c => c.CompareTo(own) < 0 && c.Status != PluginStatus.Deleted).OrderByDescending(c => c, order).ToList();
        var lower = toRemove.Where(c => c.IsLoaded).ToList();
        if (lower.Count == 0)
        {
            // No other loaded copy below this one (at most higher ones built for another Jellyfin line, which stay inert).
            return new KitDuplicateVerdict(own, siblings, false, toRemove, string.Empty);
        }

        var list = Join(lower.Select(s => s.Describe()).ToList());
        var one = lower.Count == 1;
        if (lower.Any(s => !s.HasGuard))
        {
            return new KitDuplicateVerdict(
                own,
                siblings,
                true,
                toRemove,
                $"{pluginName} {own.Version} in the plugin folder '{own.FolderName}' is inactive for this start: "
                + (one ? $"an older copy of it, {list}, was loaded too and runs instead. This copy turns the older one off once the server has started" : $"older copies of it, {list}, were loaded too and run instead. This copy turns the older ones off once the server has started")
                + "; the log says whether that worked. Then restart Jellyfin to finish the update.");
        }

        return new KitDuplicateVerdict(
            own,
            siblings,
            false,
            toRemove,
            $"{pluginName} {own.Version} in the plugin folder '{own.FolderName}' found "
            + (one ? $"an older copy of itself, {list}, which stands down for this start. This copy turns it off once the server has started" : $"older copies of itself, {list}, which stand down for this start. This copy turns them off once the server has started")
            + "; the log says whether that worked. Then restart Jellyfin to finish the update.");
    }

    /// <summary>
    /// Reads one folder: <c>null</c> when it holds no DLL of <paramref name="assemblyFileName"/>, or its <c>meta.json</c>
    /// names another plugin or lists assemblies without it.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="assemblyFileName">The plugin's assembly file name.</param>
    /// <param name="pluginId">The plugin's id.</param>
    /// <param name="server">The server's version.</param>
    /// <param name="isOwn">Whether this is the running copy's own folder (it has the guard and its target by definition).</param>
    /// <returns>The copy, or <c>null</c>.</returns>
    internal static KitPluginCopy? ReadCopy(string folder, string assemblyFileName, Guid pluginId, Version server, bool isOwn)
    {
        var dll = Path.Combine(folder, assemblyFileName);
        if (!File.Exists(dll))
        {
            return null;
        }

        var manifest = ReadManifest(folder);
        if (manifest is not null)
        {
            if (manifest.Id != pluginId)
            {
                return null;
            }

            if (manifest.Assemblies.Count > 0 && !manifest.Assemblies.Any(a => SameFile(Path.Combine(folder, a), dll)))
            {
                return null;
            }
        }

        var assembly = ReadAssembly(dll);
        var version = manifest?.Version ?? assembly.Version ?? new Version(0, 0, 0, 0);
        var supported = manifest is null || server >= manifest.TargetAbi;
        return new KitPluginCopy(
            Path.GetFullPath(folder),
            version,
            manifest is not null,
            manifest?.Status ?? PluginStatus.Active,
            supported,
            isOwn || assembly.HasGuard,
            isOwn ? SafeTarget() : assembly.JellyfinTarget);
    }

    /// <summary>
    /// Gets a value indicating whether the assembly at <paramref name="dllPath"/> defines the duplicate guard, read with
    /// <see cref="MetadataReader"/> without loading it. Any failure counts as no guard.
    /// </summary>
    /// <param name="dllPath">The DLL.</param>
    /// <returns><c>true</c> when it has the guard.</returns>
    internal static bool HasGuard(string dllPath) => ReadAssembly(dllPath).HasGuard;

    /// <summary>Reads the status in a folder's <c>meta.json</c> as it is on disk.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The status, or <c>null</c> when the folder has no <c>meta.json</c> Jellyfin can read.</returns>
    internal static PluginStatus? ReadStatus(string folder) => ReadManifest(folder)?.Status;

    /// <summary>Reads the plugin id in a folder's <c>meta.json</c> as it is on disk.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The id (empty when the file has none), or <c>null</c> when the folder has no <c>meta.json</c> Jellyfin can read.</returns>
    internal static Guid? ReadId(string folder) => ReadManifest(folder)?.Id;

    // Whether Jellyfin loads the copy (LocalPlugin.IsEnabledAndSupported, after PluginManager.UpdatePluginSupersededStatus).
    // A superseded copy loads again when nothing replaces it: on Jellyfin 12 a higher enabled or disabled copy of the same
    // id, on 10.11 any other enabled version of it. Only folders with a meta.json carry the plugin's id there.
    private static bool Loads(KitPluginCopy copy, KitPluginCopy own, IReadOnlyList<KitPluginCopy> all, bool jellyfin12Rules)
    {
        if (!copy.IsSupported)
        {
            return false;
        }

        if (copy.Status >= PluginStatus.Active)
        {
            return true;
        }

        if (copy.Status != PluginStatus.Superseded)
        {
            return false;
        }

        bool Enabled(KitPluginCopy p) => ReferenceEquals(p, own) || p.IsEnabledAndSupported;
        var others = all.Where(p => !ReferenceEquals(p, copy) && p.HasManifest).ToList();
        return jellyfin12Rules
            ? !others.Any(p => p.Version > copy.Version && (Enabled(p) || p.Status == PluginStatus.Disabled))
            : !others.Any(p => Enabled(p) && p.Version != copy.Version);
    }

    private static string? SafeTarget()
    {
        try
        {
            return KitBuild.JellyfinTarget;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Manifest? ReadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // The property names of Jellyfin's PluginManifest; a value it couldn't read makes it ignore the file.
            var id = Guid.Empty;
            if (root.TryGetProperty("guid", out var guid) && (guid.ValueKind != JsonValueKind.String || !Guid.TryParse(guid.GetString(), out id)))
            {
                return null;
            }

            var status = PluginStatus.Active;
            if (root.TryGetProperty("status", out var statusValue) && !TryReadStatus(statusValue, out status))
            {
                return null;
            }

            var version = root.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String
                && Version.TryParse(versionValue.GetString(), out var parsed) ? parsed : MinimumVersion;
            var targetAbi = root.TryGetProperty("targetAbi", out var abiValue) && abiValue.ValueKind == JsonValueKind.String
                && Version.TryParse(abiValue.GetString(), out var abi) ? abi : MinimumVersion;
            var assemblies = root.TryGetProperty("assemblies", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            return new Manifest(id, status, version, targetAbi, assemblies);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Jellyfin treats an unreadable meta.json as missing: the folder loads as Active.
            return null;
        }
    }

    private static bool TryReadStatus(JsonElement value, out PluginStatus status)
    {
        status = PluginStatus.Active;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            status = (PluginStatus)number;
            return true;
        }

        return value.ValueKind == JsonValueKind.String
            && Enum.TryParse(value.GetString(), ignoreCase: true, out status)
            && !int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static AssemblyFacts ReadAssembly(string dllPath)
    {
        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return AssemblyFacts.Unreadable;
            }

            var reader = pe.GetMetadataReader();
            var version = reader.IsAssembly ? reader.GetAssemblyDefinition().Version : null;
            var hasGuard = reader.TypeDefinitions
                .Select(reader.GetTypeDefinition)
                .Any(t => reader.StringComparer.Equals(t.Name, MarkerName) && reader.StringComparer.Equals(t.Namespace, MarkerNamespace));
            return new AssemblyFacts(version, hasGuard, reader.IsAssembly ? ReadTarget(reader) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return AssemblyFacts.Unreadable;
        }
    }

    // The [assembly: AssemblyMetadata("Jellyfin.Plugin.Kit.JellyfinTarget", "...")] value Kit.props compiles in.
    private static string? ReadTarget(MetadataReader reader)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference
                || !reader.StringComparer.Equals(reader.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name, "AssemblyMetadataAttribute"))
            {
                continue;
            }

            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 1)
            {
                continue;
            }

            var key = blob.ReadSerializedString();
            var value = blob.ReadSerializedString();
            if (string.Equals(key, KitBuild.JellyfinTargetKey, StringComparison.Ordinal) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Join(IReadOnlyList<string> items)
        => items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static bool SameFolder(string a, string b) => SameFile(a, b);

    private static bool SameFile(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Compares two folder paths the way the file system does.</summary>
    /// <param name="a">One folder.</param>
    /// <param name="b">The other.</param>
    /// <returns><c>true</c> when they are the same folder.</returns>
    internal static bool IsSameFolder(string a, string b) => SameFolder(a, b);

    private sealed record Manifest(Guid Id, PluginStatus Status, Version Version, Version TargetAbi, IReadOnlyList<string> Assemblies);

    private sealed record AssemblyFacts(Version? Version, bool HasGuard, string? JellyfinTarget)
    {
        public static readonly AssemblyFacts Unreadable = new(null, false, null);
    }
}
