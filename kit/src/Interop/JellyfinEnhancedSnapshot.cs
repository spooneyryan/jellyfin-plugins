using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// What <see cref="JellyfinEnhancedReader.Read"/> found at one moment: JE's state and settings, its users' personal
/// shortcuts, Custom Tabs' tabs and Plugin Pages' list. Read again for every request that needs it (JE's keys are
/// re-read on every capabilities request), so a change shows at once.
/// </summary>
/// <param name="Plugin">Whether JE is installed and running.</param>
/// <param name="Settings">JE's settings.</param>
/// <param name="Users">The users with personal JE shortcuts; empty when their folders couldn't be listed (<see cref="UsersProblem"/>).</param>
/// <param name="CustomTabs">The Custom Tabs plugin and its tabs.</param>
/// <param name="PluginPages">Plugin Pages' list.</param>
internal sealed record JellyfinEnhancedSnapshot(
    JePluginState Plugin,
    JeSettings Settings,
    IReadOnlyList<JeUserShortcuts> Users,
    CustomTabsState CustomTabs,
    PluginPagesState PluginPages)
{
    /// <summary>
    /// Gets why JE's data folder couldn't be listed for the users' own shortcuts, or <c>null</c>. While it is set,
    /// <see cref="Users"/> says nothing about the users: the checklist treats it like an unreadable user file (failing
    /// soft must not fail open).
    /// </summary>
    public string? UsersProblem { get; init; }

    /// <summary>Gets a snapshot of a server without JE, Custom Tabs or Plugin Pages.</summary>
    public static JellyfinEnhancedSnapshot None { get; } = new(
        JePluginState.NotInstalled,
        JeSettings.Missing,
        [],
        new CustomTabsState(false, false, [], null),
        PluginPagesState.NotInstalled);
}
