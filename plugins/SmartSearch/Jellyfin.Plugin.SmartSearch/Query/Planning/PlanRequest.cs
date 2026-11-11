using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// What the planner plans: a query the validator accepted, and whose access applies.
/// </summary>
/// <param name="Query">The validated, migrated query.</param>
/// <param name="User">For <see cref="PlanPurpose.Search"/> the searching user; for <see cref="PlanPurpose.EvaluateAll"/> the
/// evaluation user, or <c>null</c> for server-wide work. Their libraries, parental limits, blocked tags and unrated items
/// apply, and their user data is what per-user rules read.</param>
/// <param name="Purpose">What the plan is for.</param>
/// <param name="AllowedLibraries">The library ids the plan may read on top of the user's access (an auto-collection's
/// creator's libraries), or <c>null</c> for no further limit.</param>
/// <param name="RulesInMemory">Whether every rule stays in the residual, even those the candidate query enforces, so that
/// the plan's test decides on any candidate of its scope (<see cref="QueryPlan.CreateScopeQuery"/>), not only on those the
/// candidate query returns: an auto-collection pass whose scope's items are already loaded. The candidate query, the kinds
/// and everything else are the same as without.</param>
/// <param name="ScopeOnly">Whether to push down the scope alone (kinds, libraries, access, missing items) and check every
/// rule in memory: the Residual path whatever the query. An administrator's check of the pushdown against the database
/// (<c>ForceResidual</c> on <c>/Search</c>).</param>
internal sealed record PlanRequest(SmartSearchQueryDefinition Query, User? User, PlanPurpose Purpose = PlanPurpose.Search, IReadOnlyCollection<Guid>? AllowedLibraries = null, bool RulesInMemory = false, bool ScopeOnly = false);
