namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>Who reserves a key combination.</summary>
internal enum ShortcutOwner
{
    /// <summary>jellyfin-web's video player (its OSD keys).</summary>
    JellyfinWeb,

    /// <summary>Jellyfin Enhanced's configured (or default) shortcuts.</summary>
    JellyfinEnhanced
}
