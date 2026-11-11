using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Hosting;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The emergency stop at the server's start: the SAFE-MODE file or
/// <c>EnforcementEnabled</c> false make the plugin inert. Only the admin page's script answers; every other action of its
/// controllers answers 503 with the stop as the ProblemDetails' <c>inert</c> extension, before any service is needed.
/// </summary>
public sealed class InertApiTests
{
    private static readonly (string Method, string Path)[] Actions =
    [
        ("GET", "/AccessRoles/Admin/Diagnostics"),
        ("GET", "/AccessRoles/Admin/Roles"),
        ("GET", "/AccessRoles/Admin/Users"),
        ("GET", "/AccessRoles/Admin/Libraries"),
        ("GET", "/AccessRoles/Admin/Settings"),
        ("GET", "/AccessRoles/Admin/Audit"),
        ("POST", "/AccessRoles/Admin/Degraded/Resolve?dryRun=true"),
        ("GET", "/AccessRoles/Admin/Export"),
        ("POST", "/AccessRoles/Admin/Import"),
        ("GET", "/AccessRoles/Admin/Snapshots"),
        ("POST", "/AccessRoles/Admin/Snapshots/Restore?name=x"),
        ("POST", "/AccessRoles/Admin/Maintenance/Run"),
        ("GET", "/AccessRoles/Me"),
        ("GET", "/AccessRoles/ClientScript")
    ];

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WhenTheStopIsInEffectAtTheStart_OnlyTheAdminScriptAnswers_AndEveryOtherAction503sNamingIt(bool safeMode, bool disabled)
    {
        await using var api = await AccessRolesApi.StartAsync(a => WriteStop(a, safeMode, disabled));

        using var script = await api.SendAsync(HttpMethod.Get, "/AccessRoles/AdminScript", caller: null);
        Assert.Equal(200, (int)script.StatusCode);
        foreach (var (method, path) in Actions)
        {
            var (status, problem) = await api.JsonAsync(new HttpMethod(method), path);

            Assert.True(status == 503, method + " " + path + " answered " + status);
            var inert = problem!["inert"]!;
            Assert.Equal((safeMode, disabled), ((bool)inert["SafeMode"]!, (bool)inert["EnforcementDisabled"]!));
            Assert.Equal(Path.Combine(api.DataPath, "accessroles", AccessRolesPaths.SafeModeFileName), (string?)inert["SafeModeFile"]);
            Assert.Equal(Path.Combine(api.ConfigurationsPath, AccessRolesInert.ConfigurationFileName), (string?)inert["ConfigurationFile"]);
            Assert.StartsWith("Access Roles is inert", (string?)problem["detail"], StringComparison.Ordinal);
        }

        api.VerifyNoUserWritten();
        Assert.False(File.Exists(Path.Combine(api.DataPath, "accessroles", "accessroles.json")), "Nothing creates the data file.");
    }

    // The negative control: the same harness without the stop (EnforcementEnabled true, an unreadable configuration file)
    // answers every action as usual.
    [Theory]
    [InlineData("<PluginConfiguration><EnforcementEnabled>true</EnforcementEnabled></PluginConfiguration>")]
    [InlineData("<PluginConfiguration><EnforcementEnabled>false</EnforcementEnabled>")]
    [InlineData(null)]
    public async Task WithoutTheStop_EveryActionAnswersAsUsual(string? configuration)
    {
        await using var api = await AccessRolesApi.StartAsync(a =>
        {
            if (configuration is not null)
            {
                Directory.CreateDirectory(a.ConfigurationsPath);
                File.WriteAllText(Path.Combine(a.ConfigurationsPath, AccessRolesInert.ConfigurationFileName), configuration);
            }
        });

        foreach (var (method, path) in new[] { ("GET", "/AccessRoles/Admin/Diagnostics"), ("GET", "/AccessRoles/Admin/Roles"), ("GET", "/AccessRoles/Admin/Settings") })
        {
            var (status, _) = await api.JsonAsync(new HttpMethod(method), path);
            Assert.True(status == 200, method + " " + path + " answered " + status);
        }
    }

    private static void WriteStop(AccessRolesApi api, bool safeMode, bool disabled)
    {
        if (safeMode)
        {
            Directory.CreateDirectory(Path.Combine(api.DataPath, "accessroles"));
            File.WriteAllText(Path.Combine(api.DataPath, "accessroles", AccessRolesPaths.SafeModeFileName), string.Empty);
        }

        if (disabled)
        {
            Directory.CreateDirectory(api.ConfigurationsPath);
            File.WriteAllText(
                Path.Combine(api.ConfigurationsPath, AccessRolesInert.ConfigurationFileName),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n  <EnforcementEnabled>false</EnforcementEnabled>\n  <LibrarySyncEnabled>true</LibrarySyncEnabled>\n</PluginConfiguration>");
        }
    }
}
