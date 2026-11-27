using System;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests;

/// <summary>
/// The kit's duplicate guard in this plugin: a copy installed under the plugin's current name beside one under its old
/// name ("Bookmarks") stands down for that start, and a copy alone registers as usual.
/// </summary>
public sealed class DuplicateGuardTests
{
    private static readonly Guid Id = Guid.Parse(Plugin.PluginId);

    [Fact]
    public void Registrator_BesideACopyUnderTheOldName_StandsDown()
        => DuplicateGuardChecks.StandsDownBesideAnOlderCopy(typeof(Plugin), Id, "Bookmarks_0.9.0.0", "Bookmarks And Clips_" + JellyfinLine.VersionOf(typeof(Plugin)));

    [Fact]
    public void Registrator_Alone_RegistersAsUsual()
        => DuplicateGuardChecks.RegistersAlone(typeof(Plugin), Id, "Bookmarks And Clips_" + JellyfinLine.VersionOf(typeof(Plugin)));
}