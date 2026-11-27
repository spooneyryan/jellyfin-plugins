using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// What <see cref="KitDuplicateGuard"/> decided for this copy of the plugin: whether it stands down for this start, and
/// which older copies it asks Jellyfin to remove.
/// </summary>
internal sealed class KitDuplicateVerdict
{
    /// <summary>The verdict when no other copy was found.</summary>
    public static readonly KitDuplicateVerdict None = new(null, [], false, [], string.Empty);

    /// <summary>
    /// Initializes a new instance of the <see cref="KitDuplicateVerdict"/> class.
    /// </summary>
    /// <param name="own">This copy, or <c>null</c> when nothing was read.</param>
    /// <param name="siblings">The other copies Jellyfin loads.</param>
    /// <param name="standsDown">Whether this copy stays inert for this start.</param>
    /// <param name="toRemove">The lower copies this copy asks Jellyfin to remove once the server has started.</param>
    /// <param name="message">The sentence for the log and the status; empty without siblings.</param>
    public KitDuplicateVerdict(KitPluginCopy? own, IReadOnlyList<KitPluginCopy> siblings, bool standsDown, IReadOnlyList<KitPluginCopy> toRemove, string message)
    {
        Own = own;
        Siblings = siblings;
        StandsDown = standsDown;
        ToRemove = toRemove;
        Message = message;
    }

    /// <summary>Gets this copy, or <c>null</c> when nothing was read.</summary>
    public KitPluginCopy? Own { get; }

    /// <summary>Gets the other copies of the plugin that Jellyfin loads beside this one.</summary>
    public IReadOnlyList<KitPluginCopy> Siblings { get; }

    /// <summary>Gets a value indicating whether this copy stays inert for this start.</summary>
    public bool StandsDown { get; }

    /// <summary>Gets the lower copies this copy asks Jellyfin to remove once the server has started.</summary>
    public IReadOnlyList<KitPluginCopy> ToRemove { get; }

    /// <summary>Gets the sentence for the log and the status; empty without siblings.</summary>
    public string Message { get; }

    /// <summary>Gets a value indicating whether another copy was found.</summary>
    public bool HasSiblings => Siblings.Count > 0;
}
