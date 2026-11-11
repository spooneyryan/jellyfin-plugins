using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// The checked-in list, per Jellyfin line, of <b>every</b> controller action in Jellyfin.Api, each marked with its
/// <see cref="RuleKind"/>s or reviewed with no rule and a one-line reason, so the table is kept
/// complete. It is what detects new and renamed actions: the manifest test reflects over a <c>Jellyfin.Api.dll</c> of the
/// line and fails on any action missing from the list or any entry that no longer binds, and the running
/// server's actions are compared with it in Diagnostics (<see cref="ServerActions"/>; the <c>RuleTableSelfCheck</c>
/// builds on it).
/// </summary>
/// <remarks>
/// Embedded as <c>ActionManifest.jellyfin-api-&lt;version&gt;.json</c> (<c>Rules/Manifests</c>). Its enums are read
/// strictly: a misspelt rule or lock fails the load, and so the tests, never quietly.
/// </remarks>
internal sealed class ActionManifest
{
    /// <summary>The format the manifest files carry.</summary>
    public const string CurrentFormat = "accessroles-action-manifest/1";

    /// <summary>The embedded resource name prefix of the manifest files.</summary>
    public const string ResourcePrefix = "ActionManifest.";

    private static readonly JsonSerializerOptions _options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, ActionManifestEntry> _byKey;

    private ActionManifest(ManifestFile file)
    {
        Format = file.Format;
        JellyfinTarget = file.JellyfinTarget;
        JellyfinVersion = file.JellyfinVersion;
        Source = file.Source;
        Assembly = file.Assembly;
        Actions = file.Actions;
        _byKey = new Dictionary<string, ActionManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in file.Actions)
        {
            if (!_byKey.TryAdd(entry.Key, entry))
            {
                throw new InvalidDataException($"The action manifest lists {entry.Key} twice.");
            }
        }
    }

    /// <summary>Gets the manifest's format, <see cref="CurrentFormat"/>.</summary>
    public string Format { get; }

    /// <summary>Gets the Jellyfin line the manifest is for (<c>10.11</c>), compared with the build's <see cref="KitBuild.JellyfinTarget"/>.</summary>
    public string JellyfinTarget { get; }

    /// <summary>Gets the Jellyfin version whose Jellyfin.Api was reviewed, for example <c>10.11.5</c>.</summary>
    public string JellyfinVersion { get; }

    /// <summary>Gets where the reviewed assembly came from: the tag and its commit.</summary>
    public string Source { get; }

    /// <summary>Gets the reviewed assembly's name, <c>Jellyfin.Api</c>.</summary>
    public string Assembly { get; }

    /// <summary>Gets every action, by controller and action name.</summary>
    public IReadOnlyList<ActionManifestEntry> Actions { get; }

    /// <summary>
    /// Lists the manifests embedded in the plugin.
    /// </summary>
    /// <returns>The resource names, ordinally.</returns>
    public static IReadOnlyList<string> EmbeddedResources()
        => typeof(ActionManifest).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Loads the manifest of the Jellyfin line this build is for (<see cref="KitBuild.JellyfinTarget"/>).
    /// </summary>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">No embedded manifest is for this line, or one doesn't read.</exception>
    public static ActionManifest ForBuild() => ForTarget(KitBuild.JellyfinTarget);

    /// <summary>
    /// Loads the embedded manifest of a Jellyfin line.
    /// </summary>
    /// <param name="jellyfinTarget">The line, for example <c>10.11</c>.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">No embedded manifest is for the line, or more than one, or one doesn't read.</exception>
    public static ActionManifest ForTarget(string jellyfinTarget)
    {
        var matches = EmbeddedResources().Select(LoadEmbedded).Where(m => string.Equals(m.JellyfinTarget, jellyfinTarget, StringComparison.Ordinal)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException($"The plugin embeds {matches.Count} action manifests for Jellyfin {jellyfinTarget}; it needs exactly one.");
    }

    /// <summary>
    /// Loads one embedded manifest.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The manifest.</returns>
    public static ActionManifest LoadEmbedded(string resourceName)
    {
        using var stream = typeof(ActionManifest).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"The plugin doesn't embed {resourceName}.");
        return Load(stream);
    }

    /// <summary>
    /// Reads a manifest.
    /// </summary>
    /// <param name="utf8Json">The manifest file.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The file isn't a manifest of <see cref="CurrentFormat"/>.</exception>
    public static ActionManifest Load(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        ManifestFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ManifestFile>(utf8Json, _options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The action manifest doesn't read: " + ex.Message, ex);
        }

        if (file is null || !string.Equals(file.Format, CurrentFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The action manifest isn't of the format {CurrentFormat}.");
        }

        return new ActionManifest(file);
    }

    /// <summary>
    /// Finds an action.
    /// </summary>
    /// <param name="controller">The controller's full type name.</param>
    /// <param name="action">MVC's action name.</param>
    /// <returns>The entry, or <c>null</c> when the manifest doesn't list the action.</returns>
    public ActionManifestEntry? Find(string controller, string action)
        => _byKey.GetValueOrDefault(controller + "." + action);

    /// <summary>
    /// Compares the manifest with a set of actions of its assembly (a DLL's, or the running server's).
    /// </summary>
    /// <param name="actual">The actions.</param>
    /// <returns>What is missing, unreviewed or changed.</returns>
    public ActionManifestComparison Compare(IEnumerable<ApiActionShape> actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var byKey = new Dictionary<string, ApiActionShape>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (var shape in actual)
        {
            if (!byKey.TryAdd(shape.Key, shape))
            {
                duplicates.Add(shape.Key);
            }
        }

        var missing = Actions.Where(e => !byKey.ContainsKey(e.Key)).Select(e => e.Key).ToList();
        var unreviewed = byKey.Keys.Where(k => !_byKey.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var changed = new List<string>();
        foreach (var entry in Actions)
        {
            if (byKey.TryGetValue(entry.Key, out var shape) && entry.ToShape().DescribeDifferences(shape) is { Count: > 0 } differences)
            {
                changed.Add(entry.Key + ": " + string.Join("; ", differences));
            }
        }

        return new ActionManifestComparison(byKey.Count, missing, unreviewed, changed, duplicates.Distinct(StringComparer.Ordinal).ToList());
    }

    private sealed record ManifestFile
    {
        public required string Format { get; init; }

        public required string JellyfinTarget { get; init; }

        public required string JellyfinVersion { get; init; }

        public required string Source { get; init; }

        public required string Assembly { get; init; }

        public required IReadOnlyList<ActionManifestEntry> Actions { get; init; }
    }
}
