using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// An audit line to write: everything but its number and time, which the log gives it.
/// </summary>
/// <param name="Actor">Who did it.</param>
/// <param name="Action">What happened (<see cref="AuditActions"/>).</param>
/// <param name="TargetUserId">The user it concerns, or <c>null</c>.</param>
/// <param name="TargetRoleId">The role it concerns, or <c>null</c>.</param>
/// <param name="TargetName">The user's or role's name, or <c>null</c>.</param>
/// <param name="Details">The details, or <c>null</c>.</param>
internal sealed record AuditRecord(
    AuditActor Actor,
    string Action,
    Guid? TargetUserId,
    Guid? TargetRoleId,
    string? TargetName,
    IReadOnlyDictionary<string, object?>? Details);
