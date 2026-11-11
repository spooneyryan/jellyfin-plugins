using System.Text.Json.Serialization;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// One rule of an <see cref="ActionManifestEntry"/>.
/// </summary>
internal sealed record ActionManifestRule
{
    /// <summary>Gets the rule's kind.</summary>
    public required RuleKind Kind { get; init; }

    /// <summary>Gets the lock the rule depends on, for <see cref="RuleKind.LockedReject"/>; otherwise <c>null</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AccessRolesLock? Lock { get; init; }

    /// <inheritdoc />
    public override string ToString() => Lock is { } locked ? $"{Kind}({locked})" : Kind.ToString();
}
