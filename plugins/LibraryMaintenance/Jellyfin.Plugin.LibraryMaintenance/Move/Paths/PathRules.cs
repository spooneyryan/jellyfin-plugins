using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Jellyfin.Plugin.LibraryMaintenance.Settings;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// What kind of path a library location is, by its form. Only <see cref="Local"/> locations are ever opened.
/// </summary>
internal enum LocationKind
{
    /// <summary>A fully qualified path on a local drive letter.</summary>
    Local,

    /// <summary>A Windows device or NT path (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>, either slash, or any path whose second
    /// character is <c>?</c>).</summary>
    Device,

    /// <summary>A UNC path (<c>\\server\share</c>, either slash).</summary>
    Network,

    /// <summary>A drive letter Windows maps to a network share.</summary>
    MappedNetworkDrive,

    /// <summary>Anything else: relative, drive-relative (<c>C:foo</c>), empty.</summary>
    NotFullyQualified
}

/// <summary>
/// Move's path rules: the names a client may send, the names Jellyfin
/// ignores (its 10.11.5 <c>IgnorePatterns</c>, copied and pinned by a test), and the forms of path that are never opened.
/// Pure: the disk is read by <see cref="DestinationResolver"/>.
/// </summary>
/// <remarks>
/// <para>The location test is separator-aware and ignores case: the path equals the location, or starts with the location
/// plus one separator (<c>Q:\Video\Movies</c> holds <c>Q:\Video\Movies\a</c>, not <c>Q:\Video\Movies2\a</c>). Never
/// <c>IFileSystem.ContainsSubPath</c>, a substring test.</para>
/// <para>The forms never opened, from Library Maintenance's 1.0.x releases: a device path (<c>\\?\UNC\host\share</c> opens a
/// share with NTLM, <c>\??\</c> goes straight to the object manager), a UNC path and a drive mapped to a share (nothing
/// connects to another host while a plan is made), and, from the 1.0.1 verifier, any path whose second character is
/// <c>?</c> (<c>\?\…</c>, <c>/?/…</c>).</para>
/// </remarks>
internal static class PathRules
{
    /// <summary>The longest name of one folder or file (NTFS).</summary>
    public const int MaxSegmentLength = 255;

    /// <summary>The longest relative path a client may send.</summary>
    public const int MaxRelativePathLength = 1024;

    /// <summary>
    /// Jellyfin 10.11.5's ignore patterns (<c>v10.11.5:Emby.Server.Implementations/Library/IgnorePatterns.cs:14-100</c>),
    /// copied because the class isn't in the NuGet packages; <c>PathRulesTests</c> pins the list.
    /// </summary>
    public static readonly IReadOnlyList<string> IgnorePatterns =
    [
        "**/small.jpg",
        "**/albumart.jpg",
        "**/sample.?",
        "**/sample.??",
        "**/sample.???",
        "**/sample.????",
        "**/sample.?????",
        "**/*.sample.?",
        "**/*.sample.??",
        "**/*.sample.???",
        "**/*.sample.????",
        "**/*.sample.?????",
        "**/sample/*",
        "**/metadata/**",
        "**/metadata",
        "**/ps3_update/**",
        "**/ps3_update",
        "**/ps3_vprm/**",
        "**/ps3_vprm",
        "**/extrafanart/**",
        "**/extrafanart",
        "**/extrathumbs/**",
        "**/extrathumbs",
        "**/.actors/**",
        "**/.actors",
        "**/.wd_tv/**",
        "**/.wd_tv",
        "**/lost+found/**",
        "**/lost+found",
        "**/subs/**",
        "**/subs",
        "**/*.trickplay",
        "**/*.trickplay/**",
        "**/TempRec/**",
        "**/TempRec",
        "**/TempSBE/**",
        "**/TempSBE",
        "**/eaDir/**",
        "**/eaDir",
        "**/@eaDir/**",
        "**/@eaDir",
        "**/#recycle/**",
        "**/#recycle",
        "**/@Recycle/**",
        "**/@Recycle",
        "**/.@__thumb/**",
        "**/.@__thumb",
        "**/$RECYCLE.BIN/**",
        "**/$RECYCLE.BIN",
        "**/System Volume Information/**",
        "**/System Volume Information",
        "**/.grab/**",
        "**/.grab",
        "**/.*",
        "**/thumbs.db",
        "**/*.bts",
        "**/*.sync",
        "**/.zfs/**",
        "**/.zfs",
    ];

    /// <summary>
    /// 10.11.5's extras folder names (<c>NamingOptions.AllExtrasTypesFolderNames</c>: <c>trailers</c>, <c>extras</c>,
    /// <c>featurettes</c>…; <c>v10.11.5:Emby.Naming/Common/NamingOptions.cs:484-570, 697-699</c>), ignoring case.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ExtrasFolderNames = new HashSet<string>(new NamingOptions().AllExtrasTypesFolderNames.Keys, StringComparer.OrdinalIgnoreCase);

    // The device names Win32 maps whatever the folder. Only the whole name: "Con.Air.1997" is a movie folder,
    // and Windows 11 maps a name with an extension only for NUL.
    private static readonly string[] _reservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    // The device names Windows 10 also maps with an extension or trailing spaces ("CON.x", "CON .x", "COM1.txt"):
    // refused for the names Move creates (a new folder, a wrap folder), never for existing folders.
    private static readonly string[] _deviceStems =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    // Each pattern as a glob over one name: "**/x", "**/x/**" and "**/x/*" all ignore a folder named x (the last two what is
    // inside it, which is where a move would put its files).
    private static readonly string[] _nameGlobs = IgnorePatterns
        .Select(p => p.StartsWith("**/", StringComparison.Ordinal) ? p[3..] : p)
        .Select(p => p.EndsWith("/**", StringComparison.Ordinal) ? p[..^3] : p.EndsWith("/*", StringComparison.Ordinal) ? p[..^2] : p)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Gets what kind of path a library location is, by its form; only a mapped drive asks Windows (which knows its
    /// mappings without connecting).
    /// </summary>
    /// <param name="path">The location as configured.</param>
    /// <param name="isMappedNetworkDrive">Whether a drive root is mapped to a share; by default <see cref="IsMappedNetworkDrive"/>.</param>
    /// <returns>The kind.</returns>
    public static LocationKind Classify(string? path, Func<string, bool>? isMappedNetworkDrive = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return LocationKind.NotFullyQualified;
        }

        var trimmed = path.Trim();
        if (IsDevicePath(trimmed))
        {
            return LocationKind.Device;
        }

        if (SettingsRules.IsNetworkPath(trimmed))
        {
            return LocationKind.Network;
        }

        if (trimmed.Length < 3 || !char.IsAsciiLetter(trimmed[0]) || trimmed[1] != ':' || !IsSlash(trimmed[2]))
        {
            return LocationKind.NotFullyQualified;
        }

        return (isMappedNetworkDrive ?? IsMappedNetworkDrive)(trimmed[..3]) ? LocationKind.MappedNetworkDrive : LocationKind.Local;
    }

    /// <summary>
    /// Gets whether a path is a device or NT path: Library Maintenance's 1.0.x rule (<see cref="SettingsRules.IsDevicePath"/>:
    /// <c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>, either slash) and any path whose second character is <c>?</c> (the 1.0.1
    /// verifier's finding: <c>\?\UNC\…</c>, <c>/?/…</c>).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>True for a device path.</returns>
    public static bool IsDevicePath(string? path)
        => path is not null && ((path.Length >= 2 && path[1] == '?') || SettingsRules.IsDevicePath(path));

    /// <summary>
    /// Gets whether a drive root (<c>Q:\</c>) is mapped to a network share, asked of Windows without connecting.
    /// </summary>
    /// <param name="root">The drive root.</param>
    /// <returns>True for a network drive; false off Windows or for anything else.</returns>
    public static bool IsMappedNetworkDrive(string root)
    {
        if (!OperatingSystem.IsWindows() || root is null || root.Length < 2 || root[1] != ':')
        {
            return false;
        }

        try
        {
            return new DriveInfo(root[..2] + "\\").DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets why a name can't be a folder (or file) name a move creates or a client names, or null when it can:
    /// empty, <c>.</c> or <c>..</c>, a separator, <c>:</c> (alternate data streams), <c>&lt; &gt; " | ? *</c>, a control
    /// character, a leading space, a trailing dot or space, a reserved device name, longer than 255 characters.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The reason, or null.</returns>
    public static string? CheckName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "A folder name is empty.";
        }

        if (name is "." or "..")
        {
            return "\"" + name + "\" isn't a folder name.";
        }

        if (name.Length > MaxSegmentLength)
        {
            return "A folder name is longer than " + MaxSegmentLength + " characters.";
        }

        foreach (var c in name)
        {
            if (c < ' ' || c is '\\' or '/' or ':' or '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(c))
            {
                return "\"" + Shorten(name) + "\" has a character Windows doesn't allow in a name.";
            }
        }

        if (name[0] == ' ' || name[^1] is '.' or ' ')
        {
            return "\"" + Shorten(name) + "\" starts with a space or ends with a dot or a space.";
        }

        if (_reservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return "\"" + name + "\" is a name Windows reserves for a device.";
        }

        return null;
    }

    /// <summary>
    /// Gets why a name can't be a folder Move creates (a new folder, a wrap folder), or null: <see cref="CheckName"/>, and a
    /// device name before the first dot, trailing spaces trimmed (<c>NUL.txt</c>, <c>CON .x</c>, <c>COM1.Extras</c>), which
    /// Windows 10 maps to a device. An existing folder keeps the whole-name rule (<c>Con.Air.1997</c> is a movie folder).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The reason, or null.</returns>
    public static string? CheckCreatedName(string? name)
    {
        if (CheckName(name) is { } problem)
        {
            return problem;
        }

        var dot = name!.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return _deviceStems.Contains(stem, StringComparer.OrdinalIgnoreCase)
            ? "\"" + Shorten(name) + "\" starts with " + stem + ", a name Windows reserves for a device."
            : null;
    }

    /// <summary>
    /// Gets whether Jellyfin's library scan ignores a folder or file of this name (its 10.11.5 <see cref="IgnorePatterns"/>,
    /// case-insensitive, as Jellyfin's glob options say), so media moved there would vanish from the library.
    /// </summary>
    /// <param name="name">One folder or file name.</param>
    /// <returns>True when Jellyfin ignores it.</returns>
    public static bool IsIgnoredName(string name)
        => !string.IsNullOrEmpty(name) && _nameGlobs.Any(glob => Glob(glob, name));

    /// <summary>
    /// Gets whether Jellyfin's scan ignores a folder of this name where it would be (
    /// <c>CoreResolutionIgnoreRule.ShouldIgnore</c>, <c>v10.11.5:Emby.Server.Implementations/Library/CoreResolutionIgnoreRule.cs:45-62</c>):
    /// a folder named like an extras folder is ignored, ignoring case, unless its parent is a top-level library folder
    /// (<c>IsTopParent</c>: a location's own entries; a location inside another location is an ordinary folder there).
    /// </summary>
    /// <param name="name">The folder's name.</param>
    /// <param name="inTopLevelLocation">Whether the folder is directly in a location that isn't inside another one.</param>
    /// <returns>True when Jellyfin ignores it, and with it what is moved into it.</returns>
    public static bool IsIgnoredExtrasFolder(string name, bool inTopLevelLocation)
        => !inTopLevelLocation && !string.IsNullOrEmpty(name) && ExtrasFolderNames.Contains(name);

    /// <summary>
    /// Splits a client's relative path into names, checking each (<see cref="CheckName"/>, <see cref="IsIgnoredName"/>):
    /// either slash separates, and one trailing slash is dropped (<c>Movies\</c>); an empty path is
    /// the location itself; a rooted or drive-relative path, an empty name (<c>a\\b</c>, <c>a\\</c>), <c>.</c> and
    /// <c>..</c> are refused.
    /// </summary>
    /// <param name="relativePath">The relative path, or null.</param>
    /// <param name="segments">The names, in order.</param>
    /// <returns>Why the path is refused, or null.</returns>
    public static string? SplitRelativePath(string? relativePath, out IReadOnlyList<string> segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        if (relativePath.Length > MaxRelativePathLength)
        {
            return "The folder path is longer than " + MaxRelativePathLength + " characters.";
        }

        // One trailing separator after a name is dropped (Movies\); two (Movies\\) or a lone one still leave an empty name.
        if (relativePath.Length > 1 && relativePath[^1] is '\\' or '/' && relativePath[^2] is not ('\\' or '/'))
        {
            relativePath = relativePath[..^1];
        }

        var parts = relativePath.Split('\\', '/');
        var names = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (CheckName(part) is { } problem)
            {
                return problem;
            }

            if (IsIgnoredName(part))
            {
                return "Jellyfin ignores folders named \"" + Shorten(part) + "\", so media moved there would leave the library.";
            }

            names.Add(part);
        }

        segments = names;
        return null;
    }

    /// <summary>
    /// Gets whether a path is a location or under it, separator-aware and ignoring case.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <param name="location">The location.</param>
    /// <returns>True when <paramref name="path"/> equals <paramref name="location"/> or is below it.</returns>
    public static bool IsSameOrUnder(string path, string location)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(location);
        // Either slash, both without their trailing separators (a drive root's too, "C:"), so the next character must be one.
        var root = location.Replace('/', '\\').TrimEnd('\\');
        var full = path.Replace('/', '\\').TrimEnd('\\');
        return root.Length > 0
            && (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || (full.Length > root.Length + 1 && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full[root.Length] == '\\' && full[root.Length + 1] != '\\'));
    }

    /// <summary>
    /// Gets whether a path is strictly below a folder (<see cref="IsSameOrUnder"/> and not equal).
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>True when below.</returns>
    public static bool IsUnder(string path, string folder) => IsSameOrUnder(path, folder) && !SamePath(path, folder);

    /// <summary>
    /// Gets whether two paths name the same place, ignoring case and a trailing separator.
    /// </summary>
    /// <param name="a">One path.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when equal.</returns>
    public static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes trailing separators, keeping a drive root's (<c>C:\</c>).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The path without them.</returns>
    public static string TrimEnd(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    private static bool IsSlash(char c) => c is '\\' or '/';

    private static string Shorten(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "…");

    // A glob over one name: '*' any run, '?' one character, case-insensitive.
    private static bool Glob(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
