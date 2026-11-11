namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// One missing anchor of a <see cref="ClientHealthReport"/>.
/// </summary>
/// <param name="Anchor">The anchor's declared name.</param>
/// <param name="View">One of the anchor's declared views.</param>
/// <param name="Count">How often the browser found it missing in one page load, 1 to <see cref="ClientHealthReport.MaxCount"/>.</param>
internal sealed record ClientHealthMissing(string Anchor, string View, int Count);
