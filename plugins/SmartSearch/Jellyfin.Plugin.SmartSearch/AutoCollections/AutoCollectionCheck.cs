using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The outcome of <see cref="AutoCollectionChecks"/>.</summary>
/// <param name="Status"><see cref="SmartSearchAutoCollectionStatus.Ok"/>, or the status that stops the run.</param>
/// <param name="Reason">The machine-readable reason (<see cref="AutoCollectionReasons"/>), or <c>null</c>.</param>
/// <param name="Messages">The reasons in plain English.</param>
/// <param name="Query">The query to run, when <see cref="Status"/> is <c>Ok</c>.</param>
/// <param name="QueryHash">Its canonical hash.</param>
/// <param name="UsesPerUserFields">Whether the query reads watch state.</param>
/// <param name="Creator">The creator, when they exist.</param>
/// <param name="EvaluationUser">The evaluation user, when the query reads watch state.</param>
/// <param name="ClearEvaluationUser">Whether the definition names an evaluation user it no longer needs (a run clears it).</param>
internal sealed record AutoCollectionCheck(
    SmartSearchAutoCollectionStatus Status,
    string? Reason,
    IReadOnlyList<string> Messages,
    SmartSearchQueryDefinition? Query,
    string? QueryHash,
    bool UsesPerUserFields,
    User? Creator,
    User? EvaluationUser,
    bool ClearEvaluationUser)
{
    /// <summary>Gets a value indicating whether the definition may run.</summary>
    public bool Ok => Status == SmartSearchAutoCollectionStatus.Ok;
}
