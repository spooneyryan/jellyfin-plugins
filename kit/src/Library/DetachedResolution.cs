namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A detached record and what <see cref="IdentityResolver"/> found for it with its owner's policy.
/// </summary>
/// <param name="Record">The record as the owner listed it (or as the archive holds it).</param>
/// <param name="Result">The resolver's answer: relink (<see cref="ResolveResult.ShouldRelink"/>), ambiguous or unmatched.</param>
internal sealed record DetachedResolution(DetachedRecord Record, ResolveResult Result);
