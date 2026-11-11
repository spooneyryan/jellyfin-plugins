using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The import's plan (<see cref="AccessRolesImport.Plan"/>).
/// </summary>
/// <param name="Roles">The roles with the users who get them (only roles with members).</param>
/// <param name="Warnings">The warnings.</param>
/// <param name="Utc">When it was planned, UTC.</param>
internal sealed record ImportPlan(IReadOnlyList<(AccessRole Role, List<ServerUser> Members)> Roles, IReadOnlyList<AccessRolesImportWarningDto> Warnings, DateTime Utc);
