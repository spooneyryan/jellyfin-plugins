using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// One jellyfin-web anchor: a selector we use to find, observe or insert into the web
/// client's DOM, declared in the plugin's <c>Web/anchors.json</c> or the kit's <c>kit/web/anchors.json</c>.
/// </summary>
/// <param name="Name">The name the code and the health reports use: <c>detailButtons</c>, or <c>kit.f1Host</c> for
/// the kit's own.</param>
/// <param name="Css">The primary selector, then the fallbacks, tried in order.</param>
/// <param name="Required">Whether the feature can't work without it; <c>Test-WebAnchors.ps1</c> fails when a required
/// anchor is missing from the bundle, and warns for the others.</param>
/// <param name="Views">The views the anchor is expected on; a health report must name one of them.</param>
/// <param name="Note">Where it comes from (the bundle chunk or jellyfin-web source file) and what happens without
/// it.</param>
internal sealed record WebAnchor(string Name, IReadOnlyList<string> Css, bool Required, IReadOnlyList<string> Views, string Note);
