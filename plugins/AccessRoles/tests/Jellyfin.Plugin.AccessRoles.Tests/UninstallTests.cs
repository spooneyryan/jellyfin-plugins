using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Hosting;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests;

/// <summary>
/// Uninstalling (<c>BasePlugin.OnUninstalling</c>): the plugin the server creates as 10.11.5's
/// <c>PluginManager</c> does (<c>ActivatorUtilities</c>) has the server's services, and its <c>OnUninstalling</c> stops the
/// mirror and puts back every preference access it turned off; inert, without services or failing, it does nothing and never
/// throws.
/// </summary>
public sealed class UninstallTests : IDisposable
{
    private static readonly AccessRolesLock[] Five = [AccessRolesLock.Profile, AccessRolesLock.DisplayAccount, AccessRolesLock.Home, AccessRolesLock.Playback, AccessRolesLock.Subtitles];

    private readonly SyncHarness _sync = new();
    private readonly TempDirectory _directory = new();

    [Fact]
    public async Task ThePluginAsTheServerCreatesIt_PutsTheMirrorsFlagsBackWhenUninstalled_AndNoRunTurnsThemOffAgain()
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        var other = _sync.Server.Add("other");
        await MirrorAsync(kid, 100);
        await MirrorAsync(other, 101);
        await _sync.RunAsync();
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        using var services = Services(s => s.AddSingleton(_sync.Mirror));
        var plugin = (Plugin)ActivatorUtilities.CreateInstance(services, typeof(Plugin));

        plugin.OnUninstalling();
        await _sync.RunAsync();

        Assert.True(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.True(_sync.Server.Stored(other.Id).EnableUserPreferenceAccess);
        Assert.Empty(_sync.Store.Current.PreferenceMirror);
        Assert.Equal(PreferenceMirrorSync.Uninstalling, _sync.Mirror.PauseReason());
        var lines = _sync.Lines(AuditActions.PreferenceAccessRestored);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("Uninstalling", SyncHarness.Detail(l, "ReleasedFor")));
        Assert.Empty(_sync.Lines(AuditActions.PreferenceAccessMirrored).Skip(2));
    }

    // The negative controls: inert (the emergency stop at the start), no services (the plugin made with two arguments), the
    // kit's inert build (no plugin services), a release that throws.
    [Theory]
    [InlineData("inert")]
    [InlineData("none")]
    [InlineData("noServices")]
    [InlineData("throws")]
    public async Task WithoutTheMirror_OrWhenTheReleaseFails_UninstallingWritesNothing_AndNeverThrows(string variant)
    {
        _sync.Configuration.MirrorPreferenceLockToPolicy = true;
        var kid = _sync.Server.Add("kid");
        await MirrorAsync(kid, 100);
        await _sync.RunAsync();
        var writes = _sync.Server.Writes.Count;
        using var services = Services(s =>
        {
            if (variant == "inert")
            {
                s.AddSingleton(new AccessRolesInert(true, false, "/srv/jellyfin/data/accessroles/SAFE-MODE", "/srv/jellyfin/plugins/configurations/Jellyfin.Plugin.AccessRoles.xml"));
                s.AddSingleton(_sync.Mirror);
            }
            else if (variant == "throws")
            {
                s.AddSingleton(_ => (PreferenceMirrorSync)Throw());
            }
        });
        var plugin = variant == "noServices"
            ? new Plugin(services.GetRequiredService<IApplicationPaths>(), services.GetRequiredService<IXmlSerializer>())
            : (Plugin)ActivatorUtilities.CreateInstance(services, typeof(Plugin));

        plugin.OnUninstalling();

        Assert.Equal(writes, _sync.Server.Writes.Count);
        Assert.False(_sync.Server.Stored(kid.Id).EnableUserPreferenceAccess);
        Assert.Single(_sync.Store.Current.PreferenceMirror);
        Assert.Null(_sync.Mirror.PauseReason());
    }

    [Fact]
    public void TheWaitIsBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), AccessRolesUninstall.Timeout);
        Assert.Null(AccessRolesUninstall.Run(null, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _sync.Dispose();
        _directory.Dispose();
    }

    private static object Throw() => throw new InvalidOperationException("The mirror can't be made.");

    private ServiceProvider Services(Action<IServiceCollection> configure)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetReturnsDefault(_directory.Path);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(paths.Object);
        services.AddSingleton(Mock.Of<IXmlSerializer>());
        configure(services);
        return services.BuildServiceProvider();
    }

    private async Task MirrorAsync(User user, int number)
    {
        var role = Role(number, "Role " + number, Five);
        await _sync.SaveAsync(d => d with
        {
            Roles = [.. d.Roles, role],
            Assignments = [.. d.Assignments, Assign(SyncHarness.Read(user), [role])]
        });
    }
}
