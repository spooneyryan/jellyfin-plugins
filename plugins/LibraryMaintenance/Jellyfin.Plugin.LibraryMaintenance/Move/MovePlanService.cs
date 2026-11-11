using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// The planning side of Move (<c>MovePlanService</c>): an item's source, and a plan for one item and one
/// destination (eligibility, unit planning, preflight), kept 15 minutes under an id with a hash of what it moves and where,
/// so a job started from a plan that no longer matches the disk is refused as stale. Nothing here moves a file, and nothing
/// here writes to the disk: the destination's write check is an access check.
/// </summary>
internal sealed class MovePlanService
{
    /// <summary>The most plans kept at once; the oldest goes first.</summary>
    public const int MaxPlans = 64;

    /// <summary>How long a plan is kept.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private static readonly IReadOnlyList<string> _regenerated =
    [
        "Media info and chapters (read again from the files)",
        "Chapter images",
        "Intro Skipper segments (analysed again)",
        "Playback Reporting history keeps the old id (its reports still show the names)",
        "Jellyfin Enhanced hidden-content entries (not verified)",
    ];

    private readonly MoveEligibility _eligibility;
    private readonly LibraryLocationCatalog _catalog;
    private readonly DestinationResolver _resolver;
    private readonly UnitPlanner _planner;
    private readonly MovePreflight _preflight;
    private readonly MoveDestinations _destinations;
    private readonly MovePreservedSummary _preserved;
    private readonly IVolumeInfoProvider _volumes;
    private readonly MoveFeature _feature;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, StoredPlan> _plans = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MovePlanService"/> class.
    /// </summary>
    /// <param name="eligibility">The item checks.</param>
    /// <param name="catalog">The libraries' locations.</param>
    /// <param name="resolver">The destination resolver.</param>
    /// <param name="planner">The unit planner.</param>
    /// <param name="preflight">The preflight checks.</param>
    /// <param name="destinations">Where items can go.</param>
    /// <param name="preserved">The "Kept" summary.</param>
    /// <param name="volumes">The volumes.</param>
    /// <param name="feature">The settings.</param>
    /// <param name="time">The clock; the system's by default.</param>
    public MovePlanService(
        MoveEligibility eligibility,
        LibraryLocationCatalog catalog,
        DestinationResolver resolver,
        UnitPlanner planner,
        MovePreflight preflight,
        MoveDestinations destinations,
        MovePreservedSummary preserved,
        IVolumeInfoProvider volumes,
        MoveFeature feature,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(preserved);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(feature);
        _eligibility = eligibility;
        _catalog = catalog;
        _resolver = resolver;
        _planner = planner;
        _preflight = preflight;
        _destinations = destinations;
        _preserved = preserved;
        _volumes = volumes;
        _feature = feature;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the hash of a planned unit (<c>PlanHash</c>): SHA-256 over the method, the volume ids, the source and
    /// target paths, every rename, every entry with its size and time, the folders created and every item's old and
    /// predicted id, in that order.
    /// </summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The hash, lowercase hex.</returns>
    public static string Hash(MoveUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var text = new StringBuilder("jflm-move-plan/1\n");
        void Line(params object?[] parts) => text.Append(string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)))).Append('\n');
        Line(unit.Kind, unit.Method, unit.SourceVolumeId, unit.DestinationVolumeId);
        Line(unit.SourceRoot, unit.TargetDirectory, unit.TargetRoot, unit.WrapFolderName);
        foreach (var rename in unit.Renames)
        {
            Line("rename", rename.From, rename.To, rename.IsDirectory);
        }

        foreach (var entry in unit.Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            Line("entry", entry.RelativePath, entry.IsDirectory, entry.Length, entry.LastWriteUtc.Ticks);
        }

        foreach (var created in unit.CreatedDirectories)
        {
            Line("create", created);
        }

        foreach (var item in unit.Items)
        {
            Line("item", item.Role, item.OldId.ToString("N"), item.PredictedId.ToString("N"), item.OldPath, item.NewPath);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Gets an item that can move, or the error the routes answer.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The eligible item.</returns>
    /// <exception cref="PluginException">400 for no id, 404 for an item the caller can't see, 409 with the code for one
    /// that can't move.</exception>
    public EligibilityResult RequireEligible(Guid itemId, User? user)
    {
        if (itemId == Guid.Empty)
        {
            throw PluginException.Invalid("Give the item id.");
        }

        var result = _eligibility.Check(itemId, user);
        if (!result.Found)
        {
            throw PluginException.NotFound(result.Reason ?? "The item isn't there.");
        }

        if (!result.Eligible)
        {
            throw new PluginException(PluginErrorKind.Conflict, result.Reason ?? "This item can't move.") { Code = result.Code };
        }

        return result;
    }

    /// <summary>
    /// Gets what moves with an item (<c>GET /Move/Source</c>).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The source.</returns>
    public LibraryMaintenanceMoveSourceDto GetSource(Guid itemId, User? user)
    {
        var source = RequireEligible(itemId, user);
        var item = source.Item!;
        var library = source.Library!;
        var unit = PlanInPlace(source);

        // Eligibility refuses an item reached through a link; the planner walked again, and a link found since is
        // refused here too: what is below it was never listed.
        if (unit.IsLinked(unit.SourceRoot))
        {
            var link = unit.Blockers.First(b => b.Code == MoveErrorCodes.ReparsePoint);
            throw new PluginException(PluginErrorKind.Conflict, link.Message) { Code = MoveErrorCodes.ReparsePoint };
        }

        var asked = source.OwnerItemId is null ? null : unit.Items.FirstOrDefault(i => i.OldId == itemId);
        return new LibraryMaintenanceMoveSourceDto
        {
            Item = new LibraryMaintenanceMoveItemDto { Id = item.Id, Name = item.Name ?? string.Empty, Year = item.ProductionYear, Kind = item.GetBaseItemKind().ToString() },
            Library = new LibraryMaintenanceMoveLibraryDto { Id = library.Id, Name = library.Name, CollectionType = library.CollectionTypeName },
            LocationPath = library.Locations[source.LocationIndex],
            Unit = new LibraryMaintenanceMoveSourceUnitDto
            {
                Kind = unit.Kind.ToString(),
                Root = unit.SourceRoot,
                Files = unit.Entries.Where(e => !e.IsDirectory).Select(e => new LibraryMaintenanceMoveFileDto { Rel = e.RelativePath, Bytes = e.Length }).ToList(),
                Dirs = unit.Entries.Where(e => e.IsDirectory).Select(e => e.RelativePath).ToList(),
                TotalBytes = unit.TotalBytes,
                Primary = unit.PrimaryItem.OldPath,
                Versions = Paths(unit, UnitItemRole.Version),
                Parts = Paths(unit, UnitItemRole.Part),
                Extras = Paths(unit, UnitItemRole.Extra),
            },
            Retargeted = source.OwnerItemId is null
                ? null
                : "This " + (asked?.Role == UnitItemRole.Part ? "part" : "version") + " belongs to " + Title(item) + "; "
                    + (unit.Kind == MoveUnitKind.Folder ? "the whole folder moves." : "its files move together."),
        };
    }

    /// <summary>
    /// Gets the libraries an item can move to (<c>GET /Move/Destinations</c>).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<LibraryMaintenanceMoveDestinationLibraryDto> GetDestinations(Guid itemId, User? user)
        => _destinations.GetDestinations(RequireEligible(itemId, user), user);

    /// <summary>
    /// Plans a move (<c>POST /Move/Plan</c>) and keeps the plan.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels the lock probe's waits.</param>
    /// <returns>The plan, with its warnings and blockers.</returns>
    /// <exception cref="PluginException">400 for a bad request or <c>move.destination-invalid</c>, 404 for an item the
    /// caller can't see, 409 for one that can't move, 503 for a destination that isn't reachable.</exception>
    public async Task<LibraryMaintenanceMovePlanDto> PlanAsync(LibraryMaintenanceMovePlanRequestDto? request, User? user, CancellationToken cancellationToken)
    {
        var ids = request?.ItemIds ?? [];
        if (ids.Count != 1)
        {
            throw PluginException.Invalid("Give exactly one item id; Move plans one item at a time in this version.");
        }

        var destination = request!.Destination ?? throw PluginException.Invalid("Give the destination.");
        var source = RequireEligible(ids[0], user);
        var library = _catalog.FindLibrary(destination.LibraryId, user)
            ?? throw DestinationInvalid("There is no such library, or you can't open it.");
        if (!_destinations.IsCompatible(source.Library!, library))
        {
            throw DestinationInvalid(_feature.Settings.AllowCrossLibraryMoves
                ? "This item can't move to " + library.Name + ", a library of another type."
                : "Moves between libraries are turned off in Library Maintenance's settings.");
        }

        var resolved = _resolver.Resolve(library, destination.LocationIndex, destination.RelativePath, destination.NewFolderName);
        var unit = _planner.Plan(new UnitPlanRequest(source.Item!, source.Library!, source.LocationIndex, resolved, destination.Wrap, destination.WrapFolderName, _feature.Settings));
        var planId = Guid.NewGuid().ToString("N");
        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, planId, null, cancellationToken).ConfigureAwait(false);

        var plan = new StoredPlan(planId, Hash(unit), _time.GetUtcNow().UtcDateTime + Lifetime, user?.Id, unit);
        Keep(plan);
        return Dto(plan);
    }

    /// <summary>
    /// Gets a kept plan that hasn't expired.
    /// </summary>
    /// <param name="planId">The plan id.</param>
    /// <returns>The plan, or null.</returns>
    public StoredPlan? FindPlan(string? planId)
        => planId is not null && _plans.TryGetValue(planId, out var plan) && plan.ExpiresUtc > _time.GetUtcNow().UtcDateTime ? plan : null;

    private static PluginException DestinationInvalid(string message) => new(PluginErrorKind.Invalid, message) { Code = MoveErrorCodes.DestinationInvalid };

    private static string Title(BaseItem item) => MoveTitle.WithYear(item.Name, item.ProductionYear);

    private static List<string> Paths(MoveUnit unit, UnitItemRole role) => unit.Items.Where(i => i.Role == role).Select(i => i.OldPath).ToList();

    private static LibraryMaintenanceMoveIssueDto Issue(PlanIssue issue) => new() { Code = issue.Code, Message = issue.Message, Paths = issue.Paths };

    // The unit as it stands: planned against the folder it is already in (the planner's same-folder blocker is not shown).
    private MoveUnit PlanInPlace(EligibilityResult source)
    {
        var item = source.Item!;
        var library = source.Library!;
        var location = library.Locations[source.LocationIndex];
        var root = source.UnitKind == MoveUnitKind.Folder ? PathRules.TrimEnd(item.ContainingFolderPath) : PathRules.TrimEnd(Path.GetDirectoryName(item.Path) ?? location);
        var parent = source.UnitKind == MoveUnitKind.Folder ? Path.GetDirectoryName(root) ?? location : root;
        var relative = PathRules.SamePath(parent, location) || !PathRules.IsUnder(parent, location) ? string.Empty : Path.GetRelativePath(location, parent);
        var segments = relative.Length == 0 ? [] : relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var here = new ResolvedDestination(library, source.LocationIndex, location, segments, null);
        return _planner.Plan(new UnitPlanRequest(item, library, source.LocationIndex, here, false, null, _feature.Settings));
    }

    private void Keep(StoredPlan plan)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var expired in _plans.Values.Where(p => p.ExpiresUtc <= now).ToList())
        {
            _plans.TryRemove(expired.PlanId, out _);
        }

        while (_plans.Count >= MaxPlans && _plans.Values.OrderBy(p => p.ExpiresUtc).FirstOrDefault() is { } oldest)
        {
            _plans.TryRemove(oldest.PlanId, out _);
        }

        _plans[plan.PlanId] = plan;
    }

    private LibraryMaintenanceMovePlanDto Dto(StoredPlan plan)
    {
        var unit = plan.Unit;
        var target = unit.IsLinked(unit.Destination.ExistingDirectory) ? null : _volumes.GetVolume(unit.Destination.ExistingDirectory);
        return new LibraryMaintenanceMovePlanDto
        {
            PlanId = plan.PlanId,
            PlanHash = plan.PlanHash,
            ExpiresUtc = plan.ExpiresUtc,
            Method = unit.Method.ToString(),
            Units =
            [
                new LibraryMaintenanceMovePlanUnitDto
                {
                    ItemId = unit.Primary.Id,
                    Title = Title(unit.Primary),
                    Kind = unit.Kind.ToString(),
                    From = unit.SourceRoot,
                    To = unit.TargetRoot,
                    Files = unit.FileCount,
                    Bytes = unit.TotalBytes,
                    WrapFolderName = unit.WrapFolderName,
                    CreatedFolders = unit.CreatedDirectories,
                    PredictedIds = unit.Items.Select(i => new LibraryMaintenanceMovePlanItemDto { Role = i.Role.ToString(), OldId = i.OldId, NewId = i.PredictedId, From = i.OldPath, To = i.NewPath }).ToList(),
                },
            ],
            Volumes = target is null ? [] : [new LibraryMaintenanceMovePlanVolumeDto { Root = target.MountPoint, FreeBytes = target.FreeBytes, RequiredBytes = unit.Method == MoveMethod.Rename ? 0 : unit.TotalBytes }],
            EstimatedSeconds = null,
            Preserved = _preserved.Summarize(unit),
            Regenerated = _regenerated,
            Warnings = unit.Warnings.Select(Issue).ToList(),
            Blockers = unit.Blockers.Select(Issue).ToList(),
        };
    }
}
