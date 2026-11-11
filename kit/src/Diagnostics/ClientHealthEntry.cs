using System;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// The accepted reports of one anchor on one view from one web version, for the Diagnostics tab.
/// </summary>
/// <param name="Anchor">The anchor.</param>
/// <param name="View">The view.</param>
/// <param name="Required">Whether the anchor is required.</param>
/// <param name="WebVersion">The jellyfin-web version, or <c>null</c> for a native app wrapper.</param>
/// <param name="Client">The last report's client name.</param>
/// <param name="ClientVersion">The last report's client version.</param>
/// <param name="Layout">The last report's layout.</param>
/// <param name="Experimental">Whether the last report came from the experimental layout.</param>
/// <param name="PluginVersion">The last report's plugin version.</param>
/// <param name="Count">How often browsers found the anchor missing, summed over the reports.</param>
/// <param name="Reports">How many reports were accepted.</param>
/// <param name="Users">How many different users reported it (at most <see cref="ClientHealthRegistry.MaxUsersPerEntry"/>).</param>
/// <param name="FirstUtc">When the first report arrived.</param>
/// <param name="LastUtc">When the last report arrived.</param>
internal sealed record ClientHealthEntry(
    string Anchor,
    string View,
    bool Required,
    string? WebVersion,
    string Client,
    string ClientVersion,
    ClientLayout Layout,
    bool Experimental,
    string? PluginVersion,
    long Count,
    long Reports,
    int Users,
    DateTimeOffset FirstUtc,
    DateTimeOffset LastUtc);
