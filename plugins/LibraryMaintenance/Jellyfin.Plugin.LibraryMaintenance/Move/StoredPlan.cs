using System;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// A plan kept for its 15 minutes, for <c>POST /Move/Jobs</c> to take by id and hash.
/// </summary>
/// <param name="PlanId">The plan id.</param>
/// <param name="PlanHash">Its hash.</param>
/// <param name="ExpiresUtc">When it expires.</param>
/// <param name="UserId">The admin who asked for it, or null for an API key.</param>
/// <param name="Unit">The planned unit.</param>
internal sealed record StoredPlan(string PlanId, string PlanHash, DateTime ExpiresUtc, Guid? UserId, MoveUnit Unit);
