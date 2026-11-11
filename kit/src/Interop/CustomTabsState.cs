using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// The Custom Tabs plugin and its tabs, read from <c>Jellyfin.Plugin.CustomTabs.xml</c> (read-only).
/// </summary>
/// <param name="Installed">Whether Custom Tabs is known to the server.</param>
/// <param name="Active">Whether it runs in this process.</param>
/// <param name="Tabs">Its tabs; empty without a settings file.</param>
/// <param name="Problem">Why the settings couldn't be read, or null.</param>
internal sealed record CustomTabsState(bool Installed, bool Active, IReadOnlyList<CustomTab> Tabs, string? Problem);
