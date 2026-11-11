using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// The service behind the Move routes (<see cref="IMoveMedia"/>).
/// </summary>
internal sealed class MoveMedia : IMoveMedia
{
    /// <summary>The most ids one Eligibility request may ask about.</summary>
    public const int MaxEligibilityIds = 100;

    /// <summary>
    /// The reason for an id the server couldn't answer for: a fixed sentence, since an exception's message can name a path.
    /// </summary>
    public const string UnreadableReason = "The server couldn't read this item, so it can't be moved; the server's log has the details.";

    private readonly MoveFeature _feature;
    private readonly MoveEligibility _eligibility;
    private readonly MovePlanService _plans;
    private readonly MoveDestinations _destinations;
    private readonly ILogger<MoveMedia> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveMedia"/> class.
    /// </summary>
    /// <param name="feature">Whether Move answers.</param>
    /// <param name="eligibility">The item checks.</param>
    /// <param name="plans">The plan service.</param>
    /// <param name="destinations">Where items can go.</param>
    /// <param name="logger">The logger.</param>
    public MoveMedia(MoveFeature feature, MoveEligibility eligibility, MovePlanService plans, MoveDestinations destinations, ILogger<MoveMedia> logger)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(logger);
        _feature = feature;
        _eligibility = eligibility;
        _plans = plans;
        _destinations = destinations;
        _logger = logger;
    }

    /// <inheritdoc />
    public LibraryMaintenanceCapabilitiesDto GetCapabilities(bool isAdmin)
    {
        if (!isAdmin)
        {
            return new LibraryMaintenanceCapabilitiesDto();
        }

        var settings = _feature.Settings;
        return new LibraryMaintenanceCapabilitiesDto
        {
            IsAdmin = true,
            MoveEnabled = settings.Enabled,
            MoveSupported = _feature.IsSupportedOs,
            CanPlanMove = _feature.CanPlan,
            CanMove = _feature.CanMove,
            ShowMoveInItemMenu = _feature.CanPlan && settings.ShowInItemMenu,
            ShowMoveDetailPageButton = _feature.CanPlan && settings.ShowDetailPageButton,
            ShowMoveInTvLayout = _feature.CanPlan && settings.ShowInTvLayout
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryMaintenanceMoveEligibilityDto> GetEligibility(string? ids, User? user)
    {
        var parts = (ids ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw PluginException.Invalid("Give the item ids, comma-separated.");
        }

        if (parts.Length > MaxEligibilityIds)
        {
            throw PluginException.Invalid("Ask about " + MaxEligibilityIds + " items at most.");
        }

        var parsed = new List<Guid>(parts.Length);
        foreach (var part in parts)
        {
            if (!Guid.TryParse(part, CultureInfo.InvariantCulture, out var id) || id == Guid.Empty)
            {
                throw PluginException.Invalid("\"" + (part.Length > 40 ? part[..40] + "…" : part) + "\" isn't an item id.");
            }

            parsed.Add(id);
        }

        return parsed.Select(id => Dto(Check(id, user))).ToList();
    }

    /// <inheritdoc />
    public LibraryMaintenanceMoveSourceDto GetSource(Guid itemId, User? user) => _plans.GetSource(itemId, user);

    /// <inheritdoc />
    public IReadOnlyList<LibraryMaintenanceMoveDestinationLibraryDto> GetDestinations(Guid itemId, User? user) => _plans.GetDestinations(itemId, user);

    /// <inheritdoc />
    public LibraryMaintenanceMoveFoldersDto GetFolders(Guid libraryId, int locationIndex, string? relativePath, string? filter, int startIndex, int limit, Guid? itemId, User? user)
        => _destinations.GetFolders(libraryId, locationIndex, relativePath, filter, startIndex, limit, itemId is { } id ? _plans.RequireEligible(id, user) : null, user);

    /// <inheritdoc />
    public Task<LibraryMaintenanceMovePlanDto> PlanAsync(LibraryMaintenanceMovePlanRequestDto? request, User? user, CancellationToken cancellationToken)
        => _plans.PlanAsync(request, user, cancellationToken);

    private static LibraryMaintenanceMoveEligibilityDto Dto(EligibilityResult result) => new()
    {
        ItemId = result.ItemId,
        Eligible = result.Eligible,
        Code = result.Code,
        Reason = result.Reason,
        UnitKind = result.UnitKind?.ToString(),
        OwnerItemId = result.Found ? result.OwnerItemId : null
    };

    // One id the server can't answer for (a row it can't load, a folder it can't read in the link check) is that id's
    // answer, not the whole list's 500: not eligible, with a fixed reason; the log has the exception.
    private EligibilityResult Check(Guid id, User? user)
    {
        try
        {
            return _eligibility.Check(id, user);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(e, "Library Maintenance couldn't check whether item {ItemId} can be moved; it is listed as not eligible", id);
            return new EligibilityResult(id, MoveErrorCodes.NotEligible, UnreadableReason);
        }
    }
}
