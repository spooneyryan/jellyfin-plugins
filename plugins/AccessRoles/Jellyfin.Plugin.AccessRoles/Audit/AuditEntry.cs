using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// One line of <c>audit/audit-&lt;yyyyMM&gt;.jsonl</c>: <c>{"V":1,"Seq":1042,"Utc":…,"Actor":{…},"Action":…,
/// "TargetUserId":…,"TargetName":…,"Details":{…}}</c>, plus <c>TargetRoleId</c> for role actions.
/// </summary>
internal sealed record AuditEntry
{
    /// <summary>The line format's version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the line format's version.</summary>
    public int V { get; init; } = CurrentVersion;

    /// <summary>Gets the line's number: one more than the line before, across files.</summary>
    public long Seq { get; init; }

    /// <summary>Gets when it happened, UTC.</summary>
    public DateTime Utc { get; init; }

    /// <summary>Gets who did it.</summary>
    public AuditActor Actor { get; init; } = AuditActor.System;

    /// <summary>Gets what happened (<see cref="AuditActions"/>).</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Gets the user it concerns, or <c>null</c>.</summary>
    public Guid? TargetUserId { get; init; }

    /// <summary>Gets the role it concerns, or <c>null</c>.</summary>
    public Guid? TargetRoleId { get; init; }

    /// <summary>Gets the user's or role's name, or <c>null</c>.</summary>
    public string? TargetName { get; init; }

    /// <summary>
    /// Gets the details, or <c>null</c>: written from plain values, read back as <c>JsonElement</c>s.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}
