using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// A file set's members, by the names 10.11.5 reads as a video's own, ignoring case:
/// <list type="bullet">
/// <item><description>subtitles, external audio and any other companion file: the stem and a dot, then anything
/// (<c>.srt</c>, <c>.en.forced.srt</c>, <c>.mka</c>, a VobSub <c>.idx</c>; <c>MediaInfoResolver.GetExternalFiles</c>,
/// <c>v10.11.5:MediaBrowser.Providers/MediaInfo/MediaInfoResolver.cs:235-241</c>, whose delimiter is <c>.</c>);</description></item>
/// <item><description>an <c>.nfo</c>: exactly <c>&lt;stem&gt;.nfo</c> (<c>Path.ChangeExtension(path, ".nfo")</c>,
/// <c>MovieNfoSaver.GetMovieSavePaths</c> and <c>EpisodeNfoProvider</c>);</description></item>
/// <item><description>an image: exactly <c>&lt;stem&gt;.&lt;ext&gt;</c> or <c>&lt;stem&gt;-&lt;name&gt;.&lt;ext&gt;</c> with one of
/// <c>LocalImageProvider</c>'s names for a video in a mixed folder (<c>poster</c>, <c>fanart</c>, <c>fanart-2</c>,
/// <c>thumb</c>…; <c>v10.11.5:MediaBrowser.LocalMetadata/Images/LocalImageProvider.cs:55-62, 185-345, 472-493</c>, and an
/// episode's <c>-thumb</c>, <c>EpisodeLocalImageProvider.cs:55-83</c>);</description></item>
/// <item><description>an extra of the item: the stem and a dot or an extras suffix (<c>-trailer</c>…);</description></item>
/// <item><description>one folder only, the stem's own <c>&lt;stem&gt;.trickplay</c>, where 10.11.5 saves trickplay with the
/// media (<c>v10.11.5:Emby.Server.Implementations/Library/PathManager.cs:66-73</c>); any other folder named like a stem
/// is never a member (it can be another movie's own folder, <c>Alien.Resurrection.1997</c> for <c>Alien.mkv</c>).</description></item>
/// <item><description>for an episode, its images in the <c>metadata</c> folder beside it (<see cref="MatchEpisodeImages"/>);
/// the folder itself isn't a member, as other episodes' images share it.</description></item>
/// </list>
/// Each file goes to the video whose stem is the <b>longest</b> that matches it, of every video in the folder: a file of
/// <c>Movie.2003.Extended.Cut.mkv</c> (<c>.en.srt</c>, <c>.nfo</c>, <c>-poster.jpg</c>) is never a member of
/// <c>Movie.2003.mkv</c>. A matched video that isn't one of the item's own is left behind (never another item's
/// file).
/// </summary>
internal static class SidecarMatcher
{
    /// <summary>The trickplay folder's extension (<c>Path.ChangeExtension(fileName, ".trickplay")</c>, PathManager.cs:71).</summary>
    public const string TrickplayExtension = ".trickplay";

    /// <summary>
    /// The folder beside an episode where 10.11.5 also reads its images: exactly <c>metadata</c>, compared
    /// <see cref="StringComparison.Ordinal"/> (<c>EpisodeLocalImageProvider.cs:45</c>), so <c>Metadata</c> isn't read.
    /// </summary>
    public const string EpisodeMetadataFolder = "metadata";

    // LocalImageProvider's names after "<stem>-" for a video in a mixed folder (and an episode's "thumb"), each also numbered
    // as Jellyfin reads them: fanart-1…, background-1…, art-1… and backdrop1… (PopulateBackdrops, up to 20).
    private static readonly string[] _imageNames =
    [
        "poster", "folder", "cover", "default", "movie", "logo", "clearlogo", "clearart", "disc", "cdart", "discart", "banner",
        "landscape", "thumb", "fanart", "background", "art", "backdrop",
    ];

    private static readonly string[] _numberedImageNames = ["fanart-", "background-", "art-", "backdrop"];

    /// <summary>
    /// Matches one folder's entries.
    /// </summary>
    /// <param name="entries">The folder's entries, links included (read, never followed).</param>
    /// <param name="stems">The file names without extension of the unit's videos (primary, parts, versions).</param>
    /// <param name="ownPaths">The paths of the unit's own videos, which are members whatever their name.</param>
    /// <param name="extraPaths">The paths of the item's extras: members when named after a stem.</param>
    /// <param name="naming">Jellyfin's naming options (video extensions, extras suffixes).</param>
    /// <returns>The members (the unit's own videos and its extras included), the matched videos left behind, and the
    /// folders named like a stem that aren't its trickplay folder.</returns>
    public static SidecarMatch Match(
        IEnumerable<FileSystemInfo> entries,
        IReadOnlyCollection<string> stems,
        IReadOnlySet<string> ownPaths,
        IReadOnlySet<string> extraPaths,
        NamingOptions naming)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(stems);
        ArgumentNullException.ThrowIfNull(ownPaths);
        ArgumentNullException.ThrowIfNull(extraPaths);
        ArgumentNullException.ThrowIfNull(naming);

        var list = entries.ToList();
        var suffixes = naming.VideoExtraRules.Where(r => r.RuleType == Emby.Naming.Video.ExtraRuleType.Suffix).Select(r => r.Token).ToArray();
        var members = new List<FileSystemInfo>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The unit's own videos and its extras named after a stem; their stems own sidecars (an extra's .srt moves with it).
        var unitStems = new HashSet<string>(stems.Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            if (ownPaths.Contains(entry.FullName) || (extraPaths.Contains(entry.FullName) && stems.Any(stem => IsExtraName(entry.Name, stem, suffixes))))
            {
                members.Add(entry);
                taken.Add(entry.FullName);
                if (!entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    unitStems.Add(Path.GetFileNameWithoutExtension(entry.Name));
                }
            }
        }

        // Every video in the folder competes for the other files; the longest stem that matches owns one.
        var candidates = list
            .Where(e => !e.Attributes.HasFlag(FileAttributes.Directory) && IsVideo(e.Name, naming))
            .Select(e => Path.GetFileNameWithoutExtension(e.Name))
            .Concat(unitStems)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var leftBehind = new List<string>();
        var otherFolders = new List<string>();
        foreach (var entry in list)
        {
            if (taken.Contains(entry.FullName))
            {
                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                if (unitStems.Any(stem => IsTrickplayFolder(entry.Name, stem)))
                {
                    members.Add(entry);
                }
                else if (Owner(candidates, stem => IsNamedAfter(entry.Name, stem)) is { } owner && unitStems.Contains(owner))
                {
                    otherFolders.Add(entry.FullName);
                }

                continue;
            }

            if (IsVideo(entry.Name, naming))
            {
                if (stems.Any(stem => IsNamedAfter(entry.Name, stem)))
                {
                    leftBehind.Add(entry.FullName);
                }

                continue;
            }

            if (Owner(candidates, stem => IsSidecarName(entry.Name, stem)) is { } sidecarOwner && unitStems.Contains(sidecarOwner))
            {
                members.Add(entry);
            }
        }

        return new SidecarMatch(members, leftBehind, otherFolders);
    }

    /// <summary>
    /// Matches an episode's images in the <see cref="EpisodeMetadataFolder"/> beside it:
    /// files named <c>&lt;stem&gt;.&lt;ext&gt;</c> or <c>&lt;stem&gt;-thumb.&lt;ext&gt;</c> with an image extension, ignoring
    /// case, as 10.11.5 reads them there (<c>v10.11.5:MediaBrowser.LocalMetadata/Images/EpisodeLocalImageProvider.cs:45-50,
    /// 55-83</c>). Each goes to the longest stem it matches of the episode folder's videos, as in <see cref="Match"/>:
    /// <c>Ep 1-thumb.jpg</c> is <c>Ep 1-thumb.mkv</c>'s when that video is there.
    /// </summary>
    /// <param name="entries">The metadata folder's entries (read, never followed).</param>
    /// <param name="stem">The episode's file name without extension.</param>
    /// <param name="folderStems">The file names without extension of every video in the episode's folder.</param>
    /// <returns>The episode's images there.</returns>
    public static IReadOnlyList<FileSystemInfo> MatchEpisodeImages(IEnumerable<FileSystemInfo> entries, string stem, IEnumerable<string> folderStems)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(folderStems);
        var candidates = folderStems.Append(stem).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return entries
            .Where(e => !e.Attributes.HasFlag(FileAttributes.Directory)
                && Owner(candidates, s => IsEpisodeImageName(e.Name, s)) is { } owner
                && string.Equals(owner, stem, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Gets whether a file in an episode's <see cref="EpisodeMetadataFolder"/> is one of its images: exactly
    /// <c>&lt;stem&gt;.&lt;ext&gt;</c> or <c>&lt;stem&gt;-thumb.&lt;ext&gt;</c>, ignoring case, with one of
    /// <c>BaseItem.SupportedImageExtensions</c>.
    /// </summary>
    /// <param name="name">The file's name.</param>
    /// <param name="stem">The episode's file name without extension.</param>
    /// <returns>True for one of its images.</returns>
    public static bool IsEpisodeImageName(string name, string stem)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stem);
        if (stem.Length == 0 || !BaseItem.SupportedImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var bare = Path.GetFileNameWithoutExtension(name);
        return string.Equals(bare, stem, StringComparison.OrdinalIgnoreCase) || string.Equals(bare, stem + "-thumb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets whether a folder is a stem's trickplay folder, <c>&lt;stem&gt;.trickplay</c> ignoring case.
    /// </summary>
    /// <param name="name">The folder's name.</param>
    /// <param name="stem">The video's file name without extension.</param>
    /// <returns>True for its trickplay folder.</returns>
    public static bool IsTrickplayFolder(string name, string stem)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stem);
        return stem.Length > 0 && string.Equals(name, stem + TrickplayExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets whether a file (not a video, not a folder) is named as a sidecar of a stem: an <c>.nfo</c> or an image in
    /// Jellyfin's exact forms, anything else the stem and a dot (see the class).
    /// </summary>
    /// <param name="name">The file's name.</param>
    /// <param name="stem">The video's file name without extension.</param>
    /// <returns>True for a sidecar's name.</returns>
    public static bool IsSidecarName(string name, string stem)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stem);
        if (stem.Length == 0 || name.Length <= stem.Length + 1 || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[stem.Length..];
        var extension = Path.GetExtension(name);
        if (string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rest, extension, StringComparison.OrdinalIgnoreCase);
        }

        if (BaseItem.SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return string.Equals(rest, extension, StringComparison.OrdinalIgnoreCase)
                || (rest[0] == '-' && rest.Length > extension.Length + 1 && IsImageName(rest[1..^extension.Length], stem));
        }

        return rest[0] == '.';
    }

    /// <summary>
    /// Gets whether one of the item's extras is named after a stem: the stem and a dot, or an extras suffix
    /// (<c>-trailer</c>, <c>.sample</c>…).
    /// </summary>
    /// <param name="name">The extra's file name.</param>
    /// <param name="stem">The video's file name without extension.</param>
    /// <param name="extraSuffixes">Jellyfin's extras suffixes.</param>
    /// <returns>True when named after it.</returns>
    public static bool IsExtraName(string name, string stem, IReadOnlyCollection<string> extraSuffixes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(extraSuffixes);
        if (stem.Length == 0 || name.Length <= stem.Length + 1 || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[stem.Length..];
        return rest[0] == '.' || extraSuffixes.Any(s => rest.StartsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets whether a file name is a video Jellyfin would read (<c>NamingOptions.VideoFileExtensions</c>).
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="naming">Jellyfin's naming options.</param>
    /// <returns>True for a video.</returns>
    public static bool IsVideo(string name, NamingOptions naming)
    {
        ArgumentNullException.ThrowIfNull(naming);
        var extension = Path.GetExtension(name);
        return extension.Length > 1 && naming.VideoFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    // The longest stem a test accepts, or null.
    private static string? Owner(IEnumerable<string> candidates, Func<string, bool> matches)
        => candidates.Where(matches).OrderByDescending(s => s.Length).FirstOrDefault();

    // "poster", "fanart-2", "backdrop3" and the mixed-folder form "<stem>-fanart" (LocalImageProvider.cs:316-330).
    private static bool IsImageName(string word, string stem)
    {
        if (_imageNames.Contains(word, StringComparer.OrdinalIgnoreCase) || string.Equals(word, stem + "-fanart", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in _numberedImageNames)
        {
            if (word.Length > prefix.Length
                && word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(word.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number is >= 1 and <= 20)
            {
                return true;
            }
        }

        return false;
    }

    // The stem and a dot (Alien.Resurrection.1997 for Alien): a folder or video worth a warning when it stays.
    private static bool IsNamedAfter(string name, string stem)
        => stem.Length > 0 && name.Length > stem.Length + 1 && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && name[stem.Length] == '.';
}
