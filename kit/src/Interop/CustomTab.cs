namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// One tab of the Custom Tabs plugin's settings (<c>Tabs/TabConfig</c>).
/// </summary>
/// <param name="Title">The tab's title.</param>
/// <param name="ContentHtml">The HTML the tab shows.</param>
internal sealed record CustomTab(string Title, string ContentHtml);
