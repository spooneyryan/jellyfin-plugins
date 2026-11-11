namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Whether Jellyfin Enhanced is installed and running in this server process, from Jellyfin's plugin manager.
/// </summary>
/// <param name="Installed">Whether a JE plugin folder is known to the server (in any state but deleted).</param>
/// <param name="Active">Whether JE is loaded in this process, so its script, keys and settings are in effect.</param>
/// <param name="Version">The loaded version, else the newest installed one.</param>
/// <param name="Status">The loaded (or newest) version's status (<c>Active</c>, <c>Disabled</c>, <c>Restart</c>...).</param>
internal sealed record JePluginState(bool Installed, bool Active, string? Version, string? Status)
{
    /// <summary>Gets the state when JE isn't installed.</summary>
    public static JePluginState NotInstalled { get; } = new(false, false, null, null);
}
