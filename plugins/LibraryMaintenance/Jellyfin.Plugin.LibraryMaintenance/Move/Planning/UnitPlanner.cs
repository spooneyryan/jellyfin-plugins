using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// Plans one unit: its kind, manifest, items with new paths and predicted ids, where it lands
/// (with a wrap folder for a loose file when asked or when <see cref="LooseFileFolderMode"/> says so), the method, and the
/// destination guards. It reads the disk and the library and writes nothing.
/// </summary>
/// <remarks>
/// <para><b>Kind.</b> A folder unit for a DVD or Blu-ray folder and for a non-episode video that isn't in a mixed folder
/// (Jellyfin's own delete unit, <c>Video.GetDeletePaths</c>); its root is <c>ContainingFolderPath</c>, which may not be a
/// library location. A file set otherwise: the file and its parts and versions with their sidecars
/// (<see cref="SidecarMatcher"/>).</para>
/// <para><b>Guards</b> (blockers): already in that folder; into the unit itself; an existing target (ignoring case, an empty
/// folder too, as <c>MoveFileExW</c> never merges); into another movie's own folder or a folder below one (a file set there
/// would also make it mixed and break its <c>poster.jpg</c> and <c>movie.nfo</c>); foreign media, links; for an episode, a
/// destination outside any series; another volume (<c>move.cross-volume-later</c> in v1).</para>
/// </remarks>
internal sealed class UnitPlanner
{
    /// <summary>How many subfolders <c>MatchDestination</c> looks into for an own-folder movie.</summary>
    public const int OwnFolderProbeLimit = 200;

    private readonly ILibraryManager _library;
    private readonly IVolumeInfoProvider _volumes;
    private readonly Func<string, string> _validFilename;
    private readonly NamingOptions _naming;
    private readonly EpisodeDestinationGuard _episodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitPlanner"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="volumes">The volume provider.</param>
    /// <param name="validFilename">Jellyfin's <c>IFileSystem.GetValidFilename</c>, for wrap names.</param>
    /// <param name="naming">Jellyfin's naming options; by default a new set.</param>
    public UnitPlanner(ILibraryManager library, IVolumeInfoProvider volumes, Func<string, string> validFilename, NamingOptions? naming = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(validFilename);
        _library = library;
        _volumes = volumes;
        _validFilename = validFilename;
        _naming = naming ?? new NamingOptions();
        _episodes = new EpisodeDestinationGuard(library, _naming);
    }

    /// <summary>
    /// Gets the most entries a walk reads (<see cref="ForeignMediaGuard.MaxEntries"/>; tests set fewer).
    /// </summary>
    public int WalkLimit { get; init; } = ForeignMediaGuard.MaxEntries;

    /// <summary>
    /// Plans a unit.
    /// </summary>
    /// <param name="request">The item and destination.</param>
    /// <returns>The unit, with its warnings and blockers.</returns>
    public MoveUnit Plan(UnitPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = request.Item;
        var isFolderUnit = item.VideoType is VideoType.Dvd or VideoType.BluRay || (item is not Episode && !item.IsInMixedFolder);
        return isFolderUnit ? PlanFolder(request) : PlanFileSet(request);
    }

    /// <summary>
    /// Gets the own-folder movie or music video whose folder a directory is (the folder picker's "Movie folder" badge).
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <returns>The item, or null.</returns>
    public Video? FindOwnFolderMovie(string directory) => OwnFolderMovieIn(directory, null);

    private static string Rebase(string path, string from, string to)
        => PathRules.SamePath(path, from) ? to : Path.Join(to, Path.GetRelativePath(from, path));

    private static UnitEntry Entry(FileSystemInfo info, string relative)
    {
        var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
        return new UnitEntry(relative, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc);
    }

    // The source location the unit is in (empty when the request names none: the link walk then starts at the drive root).
    private static string SourceLocation(UnitPlanRequest request)
        => request.SourceLocationIndex >= 0 && request.SourceLocationIndex < request.SourceLibrary.Locations.Count ? request.SourceLibrary.Locations[request.SourceLocationIndex] : string.Empty;

    // Records links (never followed after this) under one move.reparse-point blocker.
    private static void BlockLinks(MoveUnit unit, string message, IReadOnlyList<string> links)
    {
        if (links.Count == 0)
        {
            return;
        }

        unit.Links.AddRange(links);
        unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ReparsePoint, message, links));
    }

    private MoveUnit PlanFolder(UnitPlanRequest request)
    {
        var item = request.Item;
        var destination = request.Destination;
        var root = PathRules.TrimEnd(item.ContainingFolderPath);
        var name = Path.GetFileName(root);
        var target = PathRules.TrimEnd(destination.Directory);
        var targetRoot = Path.Join(target, name);

        var (ownIds, ownPaths, members) = OwnItems(item);
        var extras = Extras(item);
        foreach (var extra in extras)
        {
            ownIds.Add(extra.Id);
            ownPaths.Add(extra.Path);
        }

        // The location and every folder down to the root, both included, are read for links before anything else is:
        // below a link nothing is listed, opened, probed or asked for its volume. A root that is
        // a library folder, of any library, or holds one is never walked either.
        var link = LinkGuard.Find(SourceLocation(request), root);
        var locations = AllLocations(request.SourceLibrary);
        var isLocation = locations.Any(l => PathRules.SamePath(l, root)) || string.IsNullOrEmpty(name);
        var nestedLocations = isLocation ? [] : locations.Where(l => PathRules.IsUnder(l, root)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tree = link is not null || isLocation || nestedLocations.Count > 0 || !Directory.Exists(root) ? new UnitTree([], [], [], false) : ForeignMediaGuard.Walk(root, _library, ownIds, ownPaths, _naming, WalkLimit);

        var items = new List<UnitItem>();
        foreach (var (role, path, type, id) in members)
        {
            if (PathRules.IsSameOrUnder(path, root))
            {
                var newPath = Rebase(path, root, targetRoot);
                items.Add(new UnitItem(role, id, type, path, newPath, _library.GetNewItemId(newPath, type)));
            }
        }

        foreach (var extra in extras.Where(e => PathRules.IsUnder(e.Path, root)))
        {
            var newPath = Rebase(extra.Path, root, targetRoot);
            items.Add(new UnitItem(UnitItemRole.Extra, extra.Id, extra.GetType(), extra.Path, newPath, _library.GetNewItemId(newPath, extra.GetType())));
        }

        var unit = new MoveUnit
        {
            Primary = item,
            Kind = MoveUnitKind.Folder,
            SourceLibrary = request.SourceLibrary,
            SourceLocationIndex = request.SourceLocationIndex,
            SourceRoot = root,
            Destination = destination,
            TargetDirectory = target,
            TargetRoot = targetRoot,
            CreatedDirectories = destination.NewFolderName is null ? [] : [target],
            Renames = [new UnitRename(root, targetRoot, true)],
            Entries = tree.Entries,
            Items = items,
        };

        if (link is not null)
        {
            BlockLinks(unit, link.Message, [link.Path]);
            return unit;
        }

        if (isLocation)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.NotEligible, root + " is a library folder itself, so it can't move as this item's folder.", [root]));
            return unit;
        }

        if (nestedLocations.Count > 0)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ForeignMedia, root + " holds a library folder, so another library's media would move with it.", nestedLocations));
            return unit;
        }

        if (!Directory.Exists(root))
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.NotEligible, "The folder " + root + " isn't there any more.", [root]));
            return unit;
        }

        BlockLinks(unit, "The folder holds a junction or symbolic link; Move doesn't move or follow links.", tree.ReparsePoints);

        if (tree.TooLarge)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ForeignMedia, root + " holds more than " + WalkLimit + " files and folders, which isn't one movie's folder.", [root]));
        }

        if (tree.Foreign.Count > 0)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ForeignMedia, "The folder holds media of other items, which would move with it.", tree.Foreign));
        }

        // A version or part outside the folder wouldn't move with it, as a file set's missing member.
        var outside = members.Where(m => !PathRules.IsSameOrUnder(m.Path, root)).Select(m => m.Path).ToList();
        if (outside.Count > 0)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.NotEligible, "Some of the item's versions or parts aren't in its folder " + root + ", so they wouldn't move with it.", outside));
        }

        if (PathRules.SamePath(target, Path.GetDirectoryName(root)))
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.DestinationInvalid, "The folder is already in " + target + "."));
        }
        else if (PathRules.IsSameOrUnder(target, root))
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.DestinationInvalid, "A folder can't move into itself."));
        }
        else if (destination.NewFolderName is null && DestinationResolver.FindEntry(target, name) is { } existing)
        {
            var path = Path.Join(target, existing.Name);
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.Collision, existing.IsAlias ? DestinationResolver.AliasMessage(path) + "; Move never merges folders." : path + " exists already; Move never merges folders.", [path]));
            if (existing.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                BlockLinks(unit, path + " is a junction or symbolic link; Move doesn't follow links.", [path]);
            }
        }

        if (PathRules.IsIgnoredExtrasFolder(name, destination.IsTopLevelLocation(target)))
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ExtrasFolderName, DestinationResolver.ExtrasFolderMessage(targetRoot), [targetRoot]));
        }

        if (item is Episode episode)
        {
            _episodes.Check(episode, destination, target, unit);
        }
        else
        {
            CheckMovieFolders(unit, destination.NewFolderName is null ? target : destination.ExistingDirectory);
        }

        Finish(unit);
        return unit;
    }

    private MoveUnit PlanFileSet(UnitPlanRequest request)
    {
        var item = request.Item;
        var destination = request.Destination;
        var directory = PathRules.TrimEnd(Path.GetDirectoryName(item.Path) ?? string.Empty);
        var (ownIds, ownPaths, members) = OwnItems(item);
        var extras = Extras(item);
        var extraPaths = extras.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in extras)
        {
            ownIds.Add(extra.Id);
        }

        // The location and every folder down to the file's, both included, before the folder is listed:
        // a file set in or below a link is blocked, and nothing below the link is read.
        if (LinkGuard.Find(SourceLocation(request), directory) is { } link)
        {
            var linkedTarget = PathRules.TrimEnd(destination.Directory);
            var linked = new MoveUnit
            {
                Primary = item,
                Kind = MoveUnitKind.FileSet,
                SourceLibrary = request.SourceLibrary,
                SourceLocationIndex = request.SourceLocationIndex,
                SourceRoot = directory,
                Destination = destination,
                TargetDirectory = linkedTarget,
                TargetRoot = linkedTarget,
                CreatedDirectories = destination.NewFolderName is null ? [] : [linkedTarget],
                Items = members.Select(m => new UnitItem(m.Role, m.Id, m.Type, m.Path, Path.Join(linkedTarget, Path.GetFileName(m.Path)), _library.GetNewItemId(Path.Join(linkedTarget, Path.GetFileName(m.Path)), m.Type))).ToList(),
            };
            BlockLinks(linked, link.Message, [link.Path]);
            return linked;
        }

        var listing = Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ForeignMediaGuard.Listing).ToList() : [];
        var stems = members.Where(m => PathRules.SamePath(Path.GetDirectoryName(m.Path), directory)).Select(m => Path.GetFileNameWithoutExtension(m.Path)).ToList();
        var (matched, leftBehind, otherFolders) = SidecarMatcher.Match(listing, stems, ownPaths, extraPaths, _naming);

        // Where it lands: the destination, or a wrap folder in it (movies and music videos only).
        var target = PathRules.TrimEnd(destination.Directory);
        var created = new List<string>();
        if (destination.NewFolderName is not null)
        {
            created.Add(target);
        }

        string? wrapName = null;
        string? wrapLink = null;
        PlanIssue? wrapProblem = null;
        if (item is not Episode && ShouldWrap(request))
        {
            wrapName = request.WrapFolderName?.Trim() is { Length: > 0 } typed ? typed : WrapName.Clean(item.Name, item.ProductionYear, _validFilename);
            if (wrapName.Length == 0)
            {
                wrapName = WrapName.Clean(Path.GetFileNameWithoutExtension(item.Path), null, _validFilename);
            }

            if (DestinationResolver.CheckNewName(wrapName) is { } problem)
            {
                wrapProblem = new PlanIssue(MoveErrorCodes.DestinationInvalid, problem);
            }
            else if (PathRules.IsIgnoredExtrasFolder(wrapName, destination.IsTopLevelLocation(target)))
            {
                wrapProblem = new PlanIssue(MoveErrorCodes.ExtrasFolderName, DestinationResolver.ExtrasFolderMessage(Path.Join(target, wrapName)) + " Choose another folder name.");
            }
            else if ((destination.NewFolderName is null ? DestinationResolver.FindEntry(target, wrapName) : null) is not { } existing)
            {
                target = Path.Join(target, wrapName);
                created.Add(target);
            }
            else if (existing.IsAlias)
            {
                // Another entry's 8.3 short name (FOLDER~1): neither made nor used, and
                // nothing by it is read (it may name another movie's folder, or a link). The plan stays in the folder.
                var path = Path.Join(target, wrapName);
                wrapProblem = new PlanIssue(MoveErrorCodes.Collision, DestinationResolver.AliasMessage(path) + "; choose another folder name.", [path]);
            }
            else
            {
                // An existing folder of that name is used as it is (each file renamed into it); a file is a collision, and a
                // link is never followed (it may point at another drive or a share): nothing below it is read or probed.
                wrapName = existing.Name;
                var path = Path.Join(target, existing.Name);
                if (!existing.Attributes.HasFlag(FileAttributes.Directory))
                {
                    wrapProblem = new PlanIssue(MoveErrorCodes.Collision, path + " is a file, so the folder can't be made.", [path]);
                }

                if (existing.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    wrapLink = path;
                }

                target = path;
            }
        }

        var entries = new List<UnitEntry>();
        var renames = new List<UnitRename>();
        var links = new List<string>();
        var foreign = new List<string>();
        var tooLarge = new List<string>();
        foreach (var member in matched)
        {
            entries.Add(Entry(member, member.Name));
            renames.Add(new UnitRename(member.FullName, Path.Join(target, member.Name), member.Attributes.HasFlag(FileAttributes.Directory)));
            if (member.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add(member.FullName);
            }
            else if (member.Attributes.HasFlag(FileAttributes.Directory))
            {
                // A trickplay folder (the only folder member): walked like a folder unit, so nothing of another item's moves.
                var tree = ForeignMediaGuard.Walk(member.FullName, _library, ownIds, ownPaths.Concat(extraPaths).ToHashSet(StringComparer.OrdinalIgnoreCase), _naming, WalkLimit);
                entries.AddRange(tree.Entries.Select(e => e with { RelativePath = Path.Join(member.Name, e.RelativePath) }));
                links.AddRange(tree.ReparsePoints);
                foreign.AddRange(tree.Foreign);
                if (tree.TooLarge)
                {
                    tooLarge.Add(member.FullName);
                }
            }
        }

        // An episode's images in the metadata folder beside it move into the target's.
        var images = item is Episode && !PathRules.SamePath(target, directory)
            ? PlanEpisodeImages(listing, Path.GetFileNameWithoutExtension(item.Path), target, created.Any(c => PathRules.SamePath(c, target)))
            : new EpisodeImages(string.Empty);
        foreach (var image in images.Moving)
        {
            entries.Add(Entry(image, Path.Join(SidecarMatcher.EpisodeMetadataFolder, image.Name)));
            renames.Add(new UnitRename(image.FullName, Path.Join(images.Folder, image.Name), false));
            if (image.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add(image.FullName);
            }
        }

        if (images.Create)
        {
            created.Add(images.Folder);
        }

        var matchedPaths = matched.Select(m => m.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<UnitItem>();
        foreach (var (role, path, type, id) in members.Where(m => matchedPaths.Contains(m.Path)))
        {
            var newPath = Path.Join(target, Path.GetFileName(path));
            items.Add(new UnitItem(role, id, type, path, newPath, _library.GetNewItemId(newPath, type)));
        }

        foreach (var extra in extras.Where(e => matchedPaths.Contains(e.Path)))
        {
            var newPath = Path.Join(target, Path.GetFileName(extra.Path));
            items.Add(new UnitItem(UnitItemRole.Extra, extra.Id, extra.GetType(), extra.Path, newPath, _library.GetNewItemId(newPath, extra.GetType())));
        }

        var unit = new MoveUnit
        {
            Primary = item,
            Kind = MoveUnitKind.FileSet,
            SourceLibrary = request.SourceLibrary,
            SourceLocationIndex = request.SourceLocationIndex,
            SourceRoot = directory,
            Destination = destination,
            TargetDirectory = target,
            TargetRoot = target,
            WrapFolderName = wrapName,
            CreatedDirectories = created,
            Renames = renames,
            Entries = entries,
            Items = items,
        };

        var missing = members.Where(m => !matchedPaths.Contains(m.Path)).Select(m => m.Path).ToList();
        if (missing.Count > 0)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.NotEligible, "Some of the item's files aren't next to it, or aren't there any more.", missing));
        }

        if (wrapProblem is not null)
        {
            unit.Blockers.Add(wrapProblem);
        }

        if (wrapLink is not null)
        {
            BlockLinks(unit, wrapLink + " is a junction or symbolic link; Move doesn't follow links. Choose another folder.", [wrapLink]);
        }

        BlockLinks(unit, "A file that would move is a junction or symbolic link, or holds one; Move doesn't move or follow links.", links);

        foreach (var issue in images.Blockers)
        {
            if (issue.Code == MoveErrorCodes.ReparsePoint)
            {
                unit.Links.AddRange(issue.Paths ?? []);
            }

            unit.Blockers.Add(issue);
        }

        unit.Warnings.AddRange(images.Warnings);

        foreach (var folder in tooLarge)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ForeignMedia, folder + " holds more than " + WalkLimit + " files and folders, which isn't one video's trickplay folder.", [folder]));
        }

        if (foreign.Count > 0)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ForeignMedia, "The trickplay folder holds media of other items, which would move with it.", foreign));
        }

        if (leftBehind.Count > 0)
        {
            unit.Warnings.Add(new PlanIssue(MoveErrorCodes.SidecarLeftBehind, "Videos named like this one stay where they are; they aren't part of this item.", leftBehind));
        }

        if (otherFolders.Count > 0)
        {
            unit.Warnings.Add(new PlanIssue(MoveErrorCodes.SidecarLeftBehind, "Folders named like this file stay where they are; only its .trickplay folder moves with it.", otherFolders));
        }

        var strayExtras = extras.Where(e => !matchedPaths.Contains(e.Path)).Select(e => e.Path).ToList();
        if (strayExtras.Count > 0)
        {
            unit.Warnings.Add(new PlanIssue(MoveErrorCodes.SidecarLeftBehind, "Extras in shared folders stay where they are.", strayExtras));
        }

        if (PathRules.SamePath(target, directory))
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.DestinationInvalid, "The file is already in " + target + "."));
        }
        else if (!created.Any(c => PathRules.SamePath(c, target)) && !unit.IsLinked(target))
        {
            // A member's name taken there, as a long name or as another entry's 8.3 short name, is a collision.
            var found = matched.Select(m => DestinationResolver.FindEntry(target, m.Name)).OfType<FolderEntry>().ToList();
            var collisions = found.Select(e => Path.Join(target, e.Name)).ToList();
            if (collisions.Count > 0)
            {
                var one = found[0].IsAlias ? DestinationResolver.AliasMessage(collisions[0]) + "; Move never overwrites." : collisions[0] + " exists already; Move never overwrites.";
                unit.Blockers.Add(new PlanIssue(MoveErrorCodes.Collision, collisions.Count == 1 ? one : collisions.Count + " files of the same names exist there already; Move never overwrites.", collisions));
            }

            BlockLinks(unit, "A file or folder of the same name there is a junction or symbolic link; Move doesn't follow links.", found.Where(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint)).Select(e => Path.Join(target, e.Name)).ToList());
        }

        if (item is Episode episode)
        {
            _episodes.Check(episode, destination, target, unit);
        }
        else
        {
            CheckMovieFolders(unit, created.Count > 0 || unit.IsLinked(target) ? destination.ExistingDirectory : target);
        }

        Finish(unit);
        return unit;
    }

    // An episode's images in the metadata folder beside it (EpisodeLocalImageProvider.cs:45-50):
    // <stem>.<ext> and <stem>-thumb.<ext> there move into the target's metadata folder, made when it is missing. The
    // folder is read only when it is a real folder; a link there, beside the episode or at the target, is never listed.
    private EpisodeImages PlanEpisodeImages(List<FileSystemInfo> listing, string stem, string target, bool targetIsNew)
    {
        var folder = Path.Join(target, SidecarMatcher.EpisodeMetadataFolder);
        var plan = new EpisodeImages(folder);
        var source = listing.FirstOrDefault(e => e.Attributes.HasFlag(FileAttributes.Directory) && string.Equals(e.Name, SidecarMatcher.EpisodeMetadataFolder, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return plan;
        }

        // Jellyfin reads the folder only by that exact name; one of another case holds nothing it reads as the episode's.
        var exact = string.Equals(source.Name, SidecarMatcher.EpisodeMetadataFolder, StringComparison.Ordinal);
        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (exact)
            {
                plan.Blockers.Add(new PlanIssue(MoveErrorCodes.ReparsePoint, source.FullName + " is a junction or symbolic link; Move doesn't look below links, so it can't tell whether this episode's images are in it.", [source.FullName]));
            }

            return plan;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(source.FullName).EnumerateFileSystemInfos("*", ForeignMediaGuard.Listing).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (exact)
            {
                plan.Blockers.Add(new PlanIssue(MoveErrorCodes.NotEligible, "Move can't read " + source.FullName + ", where this episode's images may be.", [source.FullName]));
            }

            return plan;
        }

        var stems = listing.Where(e => !e.Attributes.HasFlag(FileAttributes.Directory) && SidecarMatcher.IsVideo(e.Name, _naming)).Select(e => Path.GetFileNameWithoutExtension(e.Name));
        var images = SidecarMatcher.MatchEpisodeImages(entries, stem, stems);
        if (images.Count == 0)
        {
            return plan;
        }

        if (!exact)
        {
            plan.Warnings.Add(new PlanIssue(MoveErrorCodes.SidecarLeftBehind, "Jellyfin reads an episode's images from a folder named \"metadata\", in lower case, so it doesn't read these in " + source.FullName + "; they stay where they are.", images.Select(i => i.FullName).ToList()));
            return plan;
        }

        if (targetIsNew)
        {
            plan.Moving.AddRange(images);
            plan.Create = true;
            return plan;
        }

        var existing = DestinationResolver.FindEntry(target, SidecarMatcher.EpisodeMetadataFolder);
        if (existing is null)
        {
            plan.Moving.AddRange(images);
            plan.Create = true;
        }
        else if (existing.Value.IsAlias)
        {
            plan.Moving.AddRange(images);
            plan.Blockers.Add(new PlanIssue(MoveErrorCodes.Collision, DestinationResolver.AliasMessage(folder) + "; Move never overwrites.", [folder]));
        }
        else if (existing.Value.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var path = Path.Join(target, existing.Value.Name);
            plan.Moving.AddRange(images);
            plan.Blockers.Add(new PlanIssue(MoveErrorCodes.ReparsePoint, path + " is a junction or symbolic link; Move doesn't follow links, so it doesn't put this episode's images there.", [path]));
        }
        else if (!existing.Value.Attributes.HasFlag(FileAttributes.Directory))
        {
            var path = Path.Join(target, existing.Value.Name);
            plan.Moving.AddRange(images);
            plan.Blockers.Add(new PlanIssue(MoveErrorCodes.Collision, path + " is a file, so the folder for this episode's images can't be made.", [path]));
        }
        else if (!string.Equals(existing.Value.Name, SidecarMatcher.EpisodeMetadataFolder, StringComparison.Ordinal))
        {
            // "Metadata" there: Jellyfin wouldn't read the images in it, and "metadata" can't be made beside it.
            var path = Path.Join(target, existing.Value.Name);
            plan.Warnings.Add(new PlanIssue(MoveErrorCodes.SidecarLeftBehind, "This episode's images in " + source.FullName + " stay where they are: " + path + " is named in another case, and Jellyfin reads an episode's images only from a folder named \"metadata\".", images.Select(i => i.FullName).ToList()));
        }
        else
        {
            plan.Moving.AddRange(images);
            var found = images.Select(i => DestinationResolver.FindEntry(folder, i.Name)).OfType<FolderEntry>().ToList();
            var collisions = found.Select(e => Path.Join(folder, e.Name)).ToList();
            if (collisions.Count > 0)
            {
                var one = found[0].IsAlias ? DestinationResolver.AliasMessage(collisions[0]) + "; Move never overwrites." : collisions[0] + " exists already; Move never overwrites.";
                plan.Blockers.Add(new PlanIssue(MoveErrorCodes.Collision, collisions.Count == 1 ? one : collisions.Count + " of this episode's images exist there already; Move never overwrites.", collisions));
            }

            var linked = found.Where(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint)).Select(e => Path.Join(folder, e.Name)).ToList();
            if (linked.Count > 0)
            {
                plan.Blockers.Add(new PlanIssue(MoveErrorCodes.ReparsePoint, "A file of the same name there is a junction or symbolic link; Move doesn't follow links.", linked));
            }
        }

        return plan;
    }

    // MatchDestination: wrap only when the destination holds no loose videos and at least one
    // subfolder of an own-folder movie. A new destination folder is empty, so the file goes into it as it is.
    private bool ShouldWrap(UnitPlanRequest request)
    {
        if (request.Wrap is { } chosen)
        {
            return chosen;
        }

        switch (request.Settings.LooseFileFolderMode)
        {
            case LooseFileFolderMode.Never:
                return false;
            case LooseFileFolderMode.Always:
                return true;
        }

        var destination = request.Destination;
        if (destination.NewFolderName is not null || !Directory.Exists(destination.Directory))
        {
            return false;
        }

        var listing = new DirectoryInfo(destination.Directory).EnumerateFileSystemInfos("*", ForeignMediaGuard.Listing).ToList();
        if (listing.Any(e => !e.Attributes.HasFlag(FileAttributes.Directory) && SidecarMatcher.IsVideo(e.Name, _naming)))
        {
            return false;
        }

        return listing
            .Where(e => e.Attributes.HasFlag(FileAttributes.Directory) && !e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Take(OwnFolderProbeLimit)
            .Any(e => OwnFolderMovieIn(e.FullName, null) is not null);
    }

    // Blocks a move into another movie's own folder, or a folder below one, up to the location (exclusive).
    private void CheckMovieFolders(MoveUnit unit, string existingTarget)
    {
        var ids = unit.Items.Select(i => i.OldId).ToHashSet();
        var location = unit.Destination.LocationPath;
        for (var dir = existingTarget; PathRules.IsUnder(dir, location); dir = Path.GetDirectoryName(dir)!)
        {
            if (OwnFolderMovieIn(dir, ids) is not { } movie)
            {
                continue;
            }

            var name = MoveTitle.WithYear(movie.Name, movie.ProductionYear);
            unit.Blockers.Add(new PlanIssue(
                MoveErrorCodes.IntoMovieFolder,
                unit.Kind == MoveUnitKind.FileSet && PathRules.SamePath(dir, unit.TargetDirectory)
                    ? dir + " is the own folder of " + name + "; another movie's files there would make it a mixed folder and break its poster and .nfo."
                    : dir + " is the own folder of " + name + "; choose a folder that isn't inside another movie's folder.",
                [dir]));
            return;
        }
    }

    // The own-folder movie (or music video) whose folder this is: a DVD or Blu-ray item at the folder's path, or a video
    // directly inside that maps to a non-episode video not in a mixed folder.
    private Video? OwnFolderMovieIn(string directory, IReadOnlySet<Guid>? except)
    {
        static bool IsOwnFolder(BaseItem? item, IReadOnlySet<Guid>? except)
            => item is Video video && video is not Episode && !video.IsInMixedFolder && video.ExtraType is null && (except is null || !except.Contains(video.Id));

        if (KitItemQueries.FindAnyByPath(_library, directory, false) is var folderItem && IsOwnFolder(folderItem, except))
        {
            return (Video)folderItem!;
        }

        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", ForeignMediaGuard.Listing))
            {
                if (SidecarMatcher.IsVideo(file.Name, _naming) && KitItemQueries.FindAnyByPath(_library, file.FullName, false) is var found && IsOwnFolder(found, except))
                {
                    return (Video)found!;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // The volumes and method (v1 renames on one volume only). A link's volume is never asked: GetVolumePathNameW
    // follows it, to another drive or a share; its move.reparse-point blocker says why the plan can't run.
    private void Finish(MoveUnit unit)
    {
        if (unit.IsLinked(unit.SourceRoot) || unit.IsLinked(unit.TargetDirectory))
        {
            return;
        }

        var source = _volumes.GetVolume(unit.SourceRoot);
        var destination = _volumes.GetVolume(unit.TargetDirectory);
        if (source is null || destination is null)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.NotWritable, "Move can't tell which drive " + (source is null ? unit.SourceRoot : unit.TargetDirectory) + " is on."));
            return;
        }

        var same = string.Equals(source.VolumeId, destination.VolumeId, StringComparison.OrdinalIgnoreCase);
        unit.SourceVolumeId = source.VolumeId;
        unit.DestinationVolumeId = destination.VolumeId;
        unit.Method = same ? MoveMethod.Rename : MoveMethod.CopyVerifyDelete;
        if (!same)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.CrossVolumeLater, "Moving between drives comes with series and season moves, in a later version; for now, choose a folder on the same drive."));
        }
    }

    // The primary, its versions and its parts: ids (on 10.11 versions and parts are typeof(Video), Video.cs:362-370; on 12
    // a version takes its primary's type, LibraryManager.ResolveAlternateVersion, and parts stay Video), paths (ignoring
    // case) and the members to plan.
    private (HashSet<Guid> Ids, HashSet<string> Paths, List<(UnitItemRole Role, string Path, Type Type, Guid Id)> Members) OwnItems(Video item)
    {
        var members = new List<(UnitItemRole Role, string Path, Type Type, Guid Id)> { (UnitItemRole.Primary, item.Path, item.GetType(), item.Id) };
        foreach (var path in item.LocalAlternateVersions ?? [])
        {
#if JELLYFIN_12
            var versionType = item.GetType();
#else
            var versionType = typeof(Video);
#endif
            members.Add((UnitItemRole.Version, path, versionType, _library.GetNewItemId(path, versionType)));
        }

        foreach (var path in item.AdditionalParts ?? [])
        {
            members.Add((UnitItemRole.Part, path, typeof(Video), _library.GetNewItemId(path, typeof(Video))));
        }

        members = members.Where(m => !string.IsNullOrEmpty(m.Path)).DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return (members.Select(m => m.Id).ToHashSet(), members.Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase), members);
    }

    // Every library's locations (not just those the caller can open) and the source library's own.
    private List<string> AllLocations(CatalogLibrary source)
    {
        var all = (_library.GetVirtualFolders() ?? []).SelectMany(f => f?.Locations ?? []).Where(l => !string.IsNullOrEmpty(l));
        return all.Concat(source.Locations).ToList();
    }

    // The item's extras: on 10.11 its ExtraIds; on 12, which keeps no list, the items it owns that are extras (the owned
    // items also include its local versions and parts, without an ExtraType).
    private List<BaseItem> Extras(BaseItem item)
#if JELLYFIN_12
        => _library.GetItemList(new InternalItemsQuery { OwnerIds = [item.Id], IncludeOwnedItems = true }).Where(e => e.ExtraType is not null && !string.IsNullOrEmpty(e.Path)).ToList();
#else
        => (item.ExtraIds ?? []).Where(id => id != Guid.Empty).Select(id => _library.GetItemById(id)).OfType<BaseItem>().Where(e => !string.IsNullOrEmpty(e.Path)).ToList();
#endif

    // An episode's images in its metadata folder (PlanEpisodeImages): those that move, the target's metadata folder, whether
    // the commit makes it, and the issues for the unit.
    private sealed class EpisodeImages(string folder)
    {
        public string Folder { get; } = folder;

        public List<FileSystemInfo> Moving { get; } = [];

        public bool Create { get; set; }

        public List<PlanIssue> Blockers { get; } = [];

        public List<PlanIssue> Warnings { get; } = [];
    }
}
