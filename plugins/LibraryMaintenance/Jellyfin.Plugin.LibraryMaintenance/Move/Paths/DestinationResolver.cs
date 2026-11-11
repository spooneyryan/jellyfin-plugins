using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// Turns a client's <c>{LibraryId, LocationIndex, RelativePath, NewFolderName}</c> into a folder:
/// never an arbitrary path. The location comes from the library's configuration, and must
/// be a local path that exists and isn't a junction or symbolic link (a device, UNC or mapped-drive location is never
/// opened); each name of the relative path is checked (<see cref="PathRules"/>), must exist as a folder, is spelled as on
/// disk, may not be a junction or symbolic link and may not hold a <c>.ignore</c> file, nor be an extras folder's name
/// where Jellyfin ignores one; the new folder's name is checked the same way and may not exist yet; the full path must stay
/// in the location.
/// </summary>
internal sealed class DestinationResolver
{
    private readonly Func<string, bool>? _isMappedNetworkDrive;
    private readonly Func<IEnumerable<string>>? _allLocations;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationResolver"/> class.
    /// </summary>
    /// <param name="isMappedNetworkDrive">Whether a drive root is mapped to a share; by default Windows is asked.</param>
    /// <param name="allLocations">Every library's locations, whoever may open them (whether a location is inside another);
    /// none by default.</param>
    public DestinationResolver(Func<string, bool>? isMappedNetworkDrive = null, Func<IEnumerable<string>>? allLocations = null)
    {
        _isMappedNetworkDrive = isMappedNetworkDrive;
        _allLocations = allLocations;
    }

    /// <summary>
    /// Gets why a location can't be opened, or null when it can: a device, UNC or mapped-drive path, or one that isn't fully
    /// qualified. Asks nothing of the disk but whether a drive letter is mapped.
    /// </summary>
    /// <param name="location">The location as configured.</param>
    /// <returns>The reason's short code (<c>device-path</c>, <c>network-location</c>, <c>not-a-local-path</c>), or null.</returns>
    public string? LocationProblem(string location) => PathRules.Classify(location, _isMappedNetworkDrive) switch
    {
        LocationKind.Local => null,
        LocationKind.Device => "device-path",
        LocationKind.Network or LocationKind.MappedNetworkDrive => "network-location",
        _ => "not-a-local-path"
    };

    /// <summary>
    /// Gets a location the caller chose: its index in range, a local path (<see cref="LocationProblem"/>), not a link, and
    /// there.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="locationIndex">The location's index.</param>
    /// <returns>The location as configured.</returns>
    /// <exception cref="PluginException">400 <c>move.destination-invalid</c> for an index out of range or a location that is
    /// never opened; 400 <c>move.reparse-point</c> for a junction or symbolic link; 503 <c>move.not-writable</c> for one that
    /// isn't there.</exception>
    public string ResolveLocation(CatalogLibrary library, int locationIndex)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (locationIndex < 0 || locationIndex >= library.Locations.Count)
        {
            throw Invalid("The library " + library.Name + " has no folder number " + locationIndex + ".");
        }

        var location = library.Locations[locationIndex];
        if (LocationProblem(location) is { } problem)
        {
            throw Invalid(problem == "network-location"
                ? location + " is a network folder; Move works with local drives only."
                : location + " isn't a local folder path; Move doesn't open it.");
        }

        // A location that is a junction or symbolic link is refused, as a link below it is (v1 fails closed),
        // before anything in it is read: its .ignore file is looked for in it.
        // One whose attributes can't be read isn't reachable: nothing in it is read either.
        var link = LinkGuard.Find(location, location);
        if (link is { Unreadable: false })
        {
            throw new PluginException(PluginErrorKind.Invalid, link.Message) { Code = MoveErrorCodes.ReparsePoint };
        }

        if (link is not null || !System.IO.Directory.Exists(location))
        {
            throw new PluginException(PluginErrorKind.Unavailable, location + " is not reachable.") { Code = MoveErrorCodes.NotWritable };
        }

        if (FindIgnoreFile(location) is { } ignore)
        {
            throw Invalid(IgnoreFileMessage(Path.GetDirectoryName(ignore)!));
        }

        return location;
    }

    /// <summary>
    /// Resolves a destination.
    /// </summary>
    /// <param name="library">The destination library (already checked to be one the caller can open).</param>
    /// <param name="locationIndex">The location's index.</param>
    /// <param name="relativePath">Existing folders below the location, either slash; empty or null for the location.</param>
    /// <param name="newFolderName">A new folder to create there, or null.</param>
    /// <returns>The destination.</returns>
    /// <exception cref="PluginException">400 <c>move.destination-invalid</c> (or <c>move.reparse-point</c>) for a destination
    /// that isn't allowed; 503 <c>move.not-writable</c> for a location that isn't there.</exception>
    public ResolvedDestination Resolve(CatalogLibrary library, int locationIndex, string? relativePath, string? newFolderName)
    {
        var location = ResolveLocation(library, locationIndex);
        if (PathRules.SplitRelativePath(relativePath, out var names) is { } pathProblem)
        {
            throw Invalid(pathProblem);
        }

        var nested = IsNested(location);
        var segments = new List<string>(names.Count);
        var current = location;
        foreach (var name in names)
        {
            var entry = FindEntry(current, name);
            if (entry is null)
            {
                throw Invalid("The folder " + Path.Join(current, name) + " doesn't exist; choose an existing folder, or a new folder's name.");
            }

            if (entry.Value.IsAlias)
            {
                throw Invalid(AliasMessage(Path.Join(current, name)) + "; choose the folder by its own name.");
            }

            var (onDisk, attributes, _) = entry.Value;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw Invalid(Path.Join(current, onDisk) + " is a file, not a folder.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PluginException(PluginErrorKind.Invalid, Path.Join(current, onDisk) + " is a junction or a symbolic link; Move doesn't follow links.") { Code = MoveErrorCodes.ReparsePoint };
            }

            if (PathRules.IsIgnoredExtrasFolder(onDisk, segments.Count == 0 && !nested))
            {
                throw ExtrasFolder(Path.Join(current, onDisk));
            }

            current = Path.Join(current, onDisk);
            if (File.Exists(Path.Join(current, ".ignore")))
            {
                throw Invalid(IgnoreFileMessage(current));
            }

            segments.Add(onDisk);
        }

        string? newName = null;
        if (newFolderName is not null)
        {
            newName = newFolderName.Trim();
            if (CheckNewName(newName) is { } nameProblem)
            {
                throw Invalid(nameProblem);
            }

            if (PathRules.IsIgnoredExtrasFolder(newName, segments.Count == 0 && !nested))
            {
                throw ExtrasFolder(Path.Join(current, newName));
            }

            if (FindEntry(current, newName) is { } existing)
            {
                throw Invalid(existing.IsAlias
                    ? AliasMessage(Path.Join(current, newName)) + "; choose another name."
                    : Path.Join(current, existing.Name) + " exists already; open it instead of making it.");
            }
        }

        var resolved = new ResolvedDestination(library, locationIndex, location, segments, newName) { LocationIsNested = nested };

        // Defence in depth: whatever the names were, the folder is in the location.
        if (!PathRules.IsSameOrUnder(Path.GetFullPath(resolved.Directory), location))
        {
            throw Invalid("The folder isn't inside " + location + ".");
        }

        return resolved;
    }

    /// <summary>
    /// Gets why a name can't be a folder Move creates (a new folder, a wrap folder), or null:
    /// <see cref="PathRules.CheckCreatedName"/> and Jellyfin's ignored names.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The reason, or null.</returns>
    public static string? CheckNewName(string name)
        => PathRules.CheckCreatedName(name) ?? (PathRules.IsIgnoredName(name) ? "Jellyfin ignores folders named \"" + name + "\", so media moved there would leave the library." : null);

    /// <summary>
    /// Finds the <c>.ignore</c> file 10.11.5 would read for a folder: in it or the nearest folder above it, up to the drive
    /// root (<c>DotIgnoreIgnoreRule.FindIgnoreFile</c>, <c>v10.11.5:Emby.Server.Implementations/Library/DotIgnoreIgnoreRule.cs:18-30</c>).
    /// Move doesn't read its rules: any one refuses, as an empty one ignores everything below it.
    /// </summary>
    /// <param name="folder">A local folder.</param>
    /// <returns>The file's path, or null.</returns>
    public static string? FindIgnoreFile(string folder)
    {
        try
        {
            for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
            {
                var path = Path.Join(current.FullName, ".ignore");
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Finds a folder's entry by name, ignoring case (Windows' rule), with its on-disk spelling and attributes; links are
    /// read, never followed. A listing has long names only, so when none matches but Windows opens a path by the name
    /// (<see cref="Path.Exists(string)"/>, which reads the entry's own attributes), it is another entry's 8.3 short name
    /// (or another alias Windows resolves): <see cref="FolderEntry.IsAlias"/>.
    /// </summary>
    /// <param name="directory">The folder to look in.</param>
    /// <param name="name">The name (no wildcards: <see cref="PathRules.CheckName"/> refuses them).</param>
    /// <returns>The entry, or null when the name is free.</returns>
    public static FolderEntry? FindEntry(string directory, string name)
    {
        try
        {
            var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple, AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = true };
            var found = new DirectoryInfo(directory).EnumerateFileSystemInfos(name, options).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return new FolderEntry(found.Name, found.Attributes, false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not listed: the name is free only if Windows opens nothing by it either.
        }

        return Path.Exists(Path.Join(directory, name)) ? new FolderEntry(name, 0, true) : null;
    }

    /// <summary>
    /// Gets the message for a name that exists already under another name (<see cref="FolderEntry.IsAlias"/>).
    /// </summary>
    /// <param name="path">The path by that name.</param>
    /// <returns>The message.</returns>
    public static string AliasMessage(string path)
        => path + " exists already under another name (\"" + Path.GetFileName(path) + "\" is another name, an 8.3 short name, of a folder or file there)";

    /// <summary>
    /// Gets the message for a folder that holds a <c>.ignore</c> file, the location's own, one above it or a path segment's.
    /// Move doesn't read its rules, so it doesn't say what is ignored: possibly everything.
    /// </summary>
    /// <param name="folder">The folder that holds the file.</param>
    /// <returns>The message.</returns>
    public static string IgnoreFileMessage(string folder)
        => folder + " has a .ignore file; Jellyfin may ignore some or all of what is below it, so Move doesn't use folders under it.";

    /// <summary>
    /// Gets the message for an extras folder's name where Jellyfin ignores it (<see cref="PathRules.IsIgnoredExtrasFolder"/>).
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <returns>The message.</returns>
    public static string ExtrasFolderMessage(string path)
        => "Jellyfin ignores a folder named \"" + Path.GetFileName(path) + "\" inside another folder (it reads it as that folder's extras), so media moved into " + path + " would leave the library.";

    /// <summary>
    /// Gets whether a location is inside another location of any library (so isn't top-level to Jellyfin).
    /// </summary>
    /// <param name="location">The location.</param>
    /// <returns>True when nested.</returns>
    public bool IsNested(string location)
        => _allLocations?.Invoke().Any(other => !string.IsNullOrEmpty(other) && PathRules.IsUnder(location, other)) ?? false;

    private static PluginException ExtrasFolder(string path) => new(PluginErrorKind.Invalid, ExtrasFolderMessage(path)) { Code = MoveErrorCodes.ExtrasFolderName };

    private static PluginException Invalid(string message) => new(PluginErrorKind.Invalid, message) { Code = MoveErrorCodes.DestinationInvalid };
}
