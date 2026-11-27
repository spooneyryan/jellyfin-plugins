using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// One folder under Jellyfin's plugins folder that holds a copy of this plugin's assembly, as <see cref="KitDuplicateGuard"/>
/// read it from disk without loading anything.
/// </summary>
internal sealed class KitPluginCopy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KitPluginCopy"/> class.
    /// </summary>
    /// <param name="folder">The folder's full path.</param>
    /// <param name="version">The copy's version: its <c>meta.json</c> version, else its assembly's.</param>
    /// <param name="hasManifest">Whether the folder has a <c>meta.json</c> that Jellyfin can read.</param>
    /// <param name="status">The <c>meta.json</c> status (<see cref="PluginStatus.Active"/> without one).</param>
    /// <param name="isSupported">Whether the server is at least the <c>meta.json</c> <c>targetAbi</c>.</param>
    /// <param name="hasGuard">Whether the copy's assembly has the duplicate guard (it then stands down by itself).</param>
    /// <param name="jellyfinTarget">The Jellyfin line the copy is built for, or <c>null</c> when its assembly doesn't say.</param>
    public KitPluginCopy(string folder, Version version, bool hasManifest, PluginStatus status, bool isSupported, bool hasGuard, string? jellyfinTarget)
    {
        Folder = folder;
        Version = version;
        HasManifest = hasManifest;
        Status = status;
        IsSupported = isSupported;
        HasGuard = hasGuard;
        JellyfinTarget = jellyfinTarget;
    }

    /// <summary>Gets the folder's full path.</summary>
    public string Folder { get; }

    /// <summary>Gets the folder's name, for messages.</summary>
    public string FolderName => System.IO.Path.GetFileName(Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>Gets the copy's version: its <c>meta.json</c> version, else its assembly's.</summary>
    public Version Version { get; }

    /// <summary>Gets a value indicating whether the folder has a <c>meta.json</c> that Jellyfin can read.</summary>
    public bool HasManifest { get; }

    /// <summary>Gets the <c>meta.json</c> status; <see cref="PluginStatus.Active"/> without one, as Jellyfin assumes.</summary>
    public PluginStatus Status { get; }

    /// <summary>Gets a value indicating whether the server is at least the copy's <c>targetAbi</c>.</summary>
    public bool IsSupported { get; }

    /// <summary>Gets a value indicating whether Jellyfin counts the copy as enabled: supported and Active or Restart.</summary>
    public bool IsEnabledAndSupported => IsSupported && Status >= PluginStatus.Active;

    /// <summary>Gets a value indicating whether the copy's assembly has the duplicate guard.</summary>
    public bool HasGuard { get; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin loads this copy at this start (<see cref="KitDuplicateGuard"/> works it out
    /// from its status and the other copies); a copy that isn't loaded is only cleaned up.
    /// </summary>
    public bool IsLoaded { get; set; }

    /// <summary>Gets the Jellyfin line the copy is built for (<c>10.11</c>, <c>12</c>), or <c>null</c> when it doesn't say.</summary>
    public string? JellyfinTarget { get; }

    /// <summary>
    /// Gets a value indicating whether this copy runs on a server of <paramref name="server"/>'s version: its ABI guard
    /// accepts the server, or it carries no target to check.
    /// </summary>
    /// <param name="server">The server's version.</param>
    /// <returns><c>true</c> when the copy would be active.</returns>
    public bool RunsOn(Version server) => JellyfinTarget is null || KitAbiGuard.Accepts(JellyfinTarget, server);

    /// <summary>
    /// Compares two copies the way every copy of the guard orders them: by version, then by folder name.
    /// </summary>
    /// <param name="other">The other copy.</param>
    /// <returns>Less than zero when this copy is the lower one.</returns>
    public int CompareTo(KitPluginCopy other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var byVersion = Version.CompareTo(other.Version);
        if (byVersion != 0)
        {
            return byVersion;
        }

        var byName = string.Compare(FolderName, other.FolderName, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(FolderName, other.FolderName);
    }

    /// <summary>Gets the copy as "'folder' (version)", for messages.</summary>
    /// <returns>The text.</returns>
    public string Describe() => $"'{FolderName}' ({Version})";
}
