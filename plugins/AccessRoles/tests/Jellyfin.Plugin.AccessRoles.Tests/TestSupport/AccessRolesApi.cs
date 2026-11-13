using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// Access Roles' admin API over HTTP in the kit's harness: every service the plugin's registrator adds, with
/// Jellyfin's JSON options, a fake library catalog, and a mocked user manager over synthetic <see cref="User"/>s whose
/// policy writes are recorded, so a test can prove none happened (the roles API writes no user's policy).
/// </summary>
internal sealed class AccessRolesApi : IAsyncDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly List<User> _users = [];
    private WebHostHarness? _web;

    private AccessRolesApi()
    {
        UserManager = new Mock<IUserManager>(MockBehavior.Loose);
        UserManager.SetupUsers(() => _users.ToList());
        UserManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _users.FirstOrDefault(u => u.Id.Equals(id)));

        // The library sync reads the users' DTOs and writes their policies: the policy as 10.11.5 builds it, and a
        // write that sets the library fields and the preference flag (Release all locks) on the user (the round trip itself is PolicyWriterTests').
        UserManager.Setup(m => m.GetUserDto(It.IsAny<User>(), It.IsAny<string?>())).Returns((User user, string? _) => new MediaBrowser.Model.Dto.UserDto { Id = user.Id, Name = user.Username, Policy = FakeJellyfinUsers.PolicyOf(user) });
        UserManager.Setup(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>())).Returns((Guid id, UserPolicy policy) =>
        {
            var user = _users.Single(u => u.Id.Equals(id));
            user.SetPermission(PermissionKind.EnableAllFolders, policy.EnableAllFolders);
            user.SetPreference(PreferenceKind.EnabledFolders, policy.EnabledFolders);
            user.EnableUserPreferenceAccess = policy.EnableUserPreferenceAccess;
            return Task.CompletedTask;
        });
    }

    public Mock<IUserManager> UserManager { get; }

    /// <summary>Gets the server's id, which an export carries and an import compares (only this server's file brings ledger entries).</summary>
    public string SystemId { get; } = "5e7a1c0f2b3d4e6f8091a2b3c4d5e6f7";

    public List<CatalogLibrary> Libraries { get; } = [.. AccessFixtures.Catalog.Libraries];

    public PluginConfiguration Configuration { get; set; } = new();

    /// <summary>Gets how many times the settings were saved (the Settings tab's <c>PUT Settings</c>).</summary>
    public int Saves { get; private set; }

    public string DataPath => _directory.Path;

    public WebHostHarness Web => _web ?? throw new InvalidOperationException("Start first.");

    public AccessRolesStore Store => Web.Services.GetRequiredService<AccessRolesStore>();

    public AccessRolesAuditLog Audit => Web.Services.GetRequiredService<AccessRolesAuditLog>();

    /// <summary>Gets the plugin configurations' folder of the server's paths (empty: nothing in it unless a test writes it).</summary>
    public string ConfigurationsPath => System.IO.Path.Combine(_directory.Path, "plugins", "configurations");

    public static Task<AccessRolesApi> StartAsync() => StartAsync(null);

    /// <summary>
    /// Starts the harness after <paramref name="beforeStart"/> prepared its folders (the emergency stop's files): the
    /// registrator reads the server's paths as at a real start.
    /// </summary>
    /// <param name="beforeStart">Writes files before the registrator runs.</param>
    /// <returns>The started harness.</returns>
    public static async Task<AccessRolesApi> StartAsync(Action<AccessRolesApi>? beforeStart)
    {
        var api = new AccessRolesApi();
        beforeStart?.Invoke(api);
        await api.StartHostAsync().ConfigureAwait(false);
        return api;
    }

    public User AddUser(string name, bool admin = false, bool allFolders = true, IEnumerable<CatalogLibrary>? folders = null, bool disabled = false)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        user.SetPermission(PermissionKind.EnableAllFolders, allFolders);
        user.SetPreference(PreferenceKind.EnabledFolders, (folders ?? []).Select(f => f.Id).ToArray());
        _users.Add(user);
        return user;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? caller = "admin", object? body = null, Guid? userId = null)
    {
        return await Web.SendAsync(method, path, request =>
        {
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
            }

            if (userId is { } id)
            {
                request.Headers.Add(JellyfinAuth.UserIdHeader, id.ToString("N"));
            }

            if (body is not null)
            {
                request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body, JsonDefaults.PascalCaseOptions), Encoding.UTF8, "application/json");
            }
        }).ConfigureAwait(false);
    }

    public async Task<(int Status, JsonNode? Json)> JsonAsync(HttpMethod method, string path, string? caller = "admin", object? body = null, Guid? userId = null)
    {
        using var response = await SendAsync(method, path, caller, body, userId).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>
    /// Asserts that nothing wrote a user: no policy, configuration or user update reached the user manager.
    /// </summary>
    public void VerifyNoUserWritten()
    {
        UserManager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
        UserManager.Verify(m => m.UpdateUserAsync(It.IsAny<User>()), Times.Never);
        UserManager.Verify(m => m.UpdateConfigurationAsync(It.IsAny<Guid>(), It.IsAny<MediaBrowser.Model.Configuration.UserConfiguration>()), Times.Never);
    }

    public async ValueTask DisposeAsync()
    {
        if (_web is not null)
        {
            await _web.DisposeAsync().ConfigureAwait(false);
        }

        _directory.Dispose();
    }

    private async Task StartHostAsync()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        host.SetupGet(h => h.SystemId).Returns(SystemId);
        // The server's paths are in the collection before the registrator runs, as 10.11.5's ApplicationHost adds them (the
        // emergency stop is read from them).
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(ConfigurationsPath);
        var registered = new ServiceCollection();
        registered.AddSingleton(paths.Object);
        new PluginServiceRegistrator().RegisterServices(registered, host.Object);
        var provider = new FakeConfigurationProvider(() => Configuration, c =>
        {
            Saves++;
            Configuration = c;
        });
        var catalog = new FakeCatalogSource(() => new LibraryCatalog(Libraries));

        _web = await new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider) && d.ServiceType != typeof(Microsoft.Extensions.Hosting.IHostedService)))
                {
                    services.Add(descriptor);
                }

                services.AddLogging();
                services.AddSingleton<IPluginConfigurationProvider>(provider);
                services.AddSingleton(paths.Object);
                services.AddSingleton(host.Object);
                services.AddSingleton(UserManager.Object);
                services.AddSingleton(Mock.Of<ILibraryManager>());
                services.AddSingleton(Mock.Of<IServerApplicationPaths>());
                services.AddSingleton<ILibraryCatalogSource>(catalog);
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                {
                    var jellyfin = JsonDefaults.PascalCaseOptions;
                    o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                    o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                    o.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jellyfin.Converters)
                    {
                        o.JsonSerializerOptions.Converters.Add(converter);
                    }
                });
            })
            .WithMvc(typeof(AccessRolesController), typeof(AccessRolesAdminController), typeof(AccessRolesDiagnosticsController), typeof(AccessRolesRolesController), typeof(AccessRolesUsersController))
            .StartAsync()
            .ConfigureAwait(false);
    }
}
