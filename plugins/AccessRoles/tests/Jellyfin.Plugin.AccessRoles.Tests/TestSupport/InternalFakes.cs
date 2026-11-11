using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Configuration;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// The plugin's own internal services' doubles, written out instead of Moq's (since 1.0.1): Moq
/// proxies an internal interface only when the assembly lets <c>DynamicProxyGenAssembly2</c> see its internals, and the
/// plugin's DLL no longer does (<c>Jellyfin.Plugin.AccessRoles.csproj</c>). Each answers as Moq's loose mock answered:
/// what the test gives it, else a default.
/// </summary>
internal sealed class FakeConfigurationProvider : IPluginConfigurationProvider
{
    private readonly Func<PluginConfiguration> _get;
    private readonly Action<PluginConfiguration>? _save;

    public FakeConfigurationProvider(Func<PluginConfiguration> get, Action<PluginConfiguration>? save = null)
    {
        _get = get;
        _save = save;
    }

    public event EventHandler? Changed;

    public PluginConfiguration GetConfiguration() => _get();

    public void Save(PluginConfiguration configuration) => _save?.Invoke(configuration);

    /// <summary>Raises <see cref="Changed"/>, as the plugin instance's <c>ConfigurationChanged</c> does.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>The server's users as the test gives them (<see cref="IServerUsers"/>).</summary>
internal sealed class FakeServerUsers : IServerUsers
{
    private readonly Func<Guid, ServerUser?> _find;
    private readonly Func<IReadOnlyList<ServerUser>>? _all;

    public FakeServerUsers(Func<Guid, ServerUser?> find, Func<IReadOnlyList<ServerUser>>? all = null)
    {
        _find = find;
        _all = all;
    }

    public IReadOnlyList<ServerUser> GetAll() => _all?.Invoke() ?? [];

    public ServerUser? Find(Guid id) => _find(id);
}

/// <summary>The library catalog as the test gives it (<see cref="ILibraryCatalogSource"/>); no name has an id.</summary>
internal sealed class FakeCatalogSource : ILibraryCatalogSource
{
    private readonly Func<LibraryCatalog> _read;

    public FakeCatalogSource(Func<LibraryCatalog> read)
    {
        _read = read;
    }

    public LibraryCatalog Read() => _read();

    public Guid IdOf(string name) => Guid.Empty;
}
