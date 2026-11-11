namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// One item of the switch-off checklist (<see cref="JeSwitchOff"/>).
/// </summary>
/// <param name="Key">The item's key: the JE setting's name, <c>ShortcutAdmin</c>, <c>ShortcutUsers</c>, <c>CustomTab</c> or <c>PluginPagesLink</c>.</param>
/// <param name="Text">What to do.</param>
/// <param name="Done">Whether the reader sees it done.</param>
/// <param name="Detail">What was found, and what stays until a restart.</param>
/// <param name="NeedsRestart">Whether a restart is still needed for it (the link), or what ticked it isn't durable: a
/// restart undoes it (a cleared key comes back at JE's next start), or a switch does (JE's Disable Keyboard Shortcuts
/// frees every key only while it stays on). "Use B" is never offered on such a tick.</param>
internal sealed record JeChecklistItem(string Key, string Text, bool Done, string? Detail, bool NeedsRestart)
{
    /// <summary>Gets a value indicating whether the item is optional (it doesn't keep the checklist incomplete).</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// Gets a value indicating whether the setting couldn't be read (JE's settings file is there but half written or
    /// malformed): never done, and the checklist isn't complete while one is (User Ratings' reviews checklist).
    /// </summary>
    public bool Unknown { get; init; }
}
