using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Registers the plugin's enabled user pages with Plugin Pages when the host starts, in <c>Auto</c> and
/// <c>PluginPagesOnly</c> mode (the pattern the SmartLists plugin uses). In
/// <c>FallbackOnly</c> mode (the default) or with <c>RegisterPluginPages</c> off it registers nothing.
/// </summary>
/// <remarks>
/// Verified in a sandbox: <see cref="IHostedService.StartAsync"/> runs after every plugin is constructed (after Plugin
/// Pages read its <c>config.json</c>) and before Plugin Pages' own startup task, so <c>/PluginPages/User</c> lists the
/// pages at once, and no file is written. Plugin Pages 2.4.x can't remove a page, so a change takes effect at the next
/// restart. It never throws: a failure leaves the pages on their fallback routes, with a warning.
/// </remarks>
internal sealed class PluginPagesRegistrationService : IHostedService
{
    private readonly KitOptions _options;
    private readonly IServiceProvider _services;
    private readonly KitPages _pages;
    private readonly KitPageHosts _hosts;
    private readonly ILogger _logger;
    private readonly Func<IEnumerable<Assembly>>? _assemblies;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPagesRegistrationService"/> class.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="services">The server's service provider (for Plugin Pages 2.4.x's DI manager and the settings).</param>
    /// <param name="pages">The page shells, for the enabled state.</param>
    /// <param name="hosts">Where the chosen hosts are recorded.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public PluginPagesRegistrationService(KitOptions options, IServiceProvider services, KitPages pages, KitPageHosts hosts, ILoggerFactory loggerFactory)
        : this(options, services, pages, hosts, loggerFactory, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPagesRegistrationService"/> class that looks for Plugin Pages in
    /// the given assemblies (tests).
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="services">The server's service provider.</param>
    /// <param name="pages">The page shells.</param>
    /// <param name="hosts">Where the chosen hosts are recorded.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="assemblies">Where to look for Plugin Pages; the loaded assemblies when <c>null</c>.</param>
    internal PluginPagesRegistrationService(
        KitOptions options,
        IServiceProvider services,
        KitPages pages,
        KitPageHosts hosts,
        ILoggerFactory loggerFactory,
        Func<IEnumerable<Assembly>>? assemblies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options;
        _services = services;
        _pages = pages;
        _hosts = hosts;
        _logger = options.CreateLogger(loggerFactory, KitPages.LoggerComponent);
        _assemblies = assemblies;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Register();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registering the {Plugin} pages with Plugin Pages failed, so they use their fallback routes", _options.PluginName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Register()
    {
        if (_options.Pages.Count == 0)
        {
            return;
        }

        var mode = ReadMode();
        var register = ReadRegisterSetting();
        var skipped = new List<string>();
        foreach (var page in _options.Pages)
        {
            var id = page.PluginPagesId(_options.RoutePrefix);
            if (!_pages.IsEnabled(page, _services))
            {
                _hosts.Set(new KitPageHostEntry(page.Key, id, KitPageHost.Disabled, "Turned off at startup."));
                continue;
            }

            if (mode == PageHostMode.FallbackOnly || !register)
            {
                _hosts.Set(new KitPageHostEntry(page.Key, id, KitPageHost.Fallback, $"PageHostMode={mode}, RegisterPluginPages={register}."));
                skipped.Add(page.Key);
                continue;
            }

            var result = PluginPagesBridge.TryRegister(
                _services,
                new PluginPageRegistration(id, page.ShellUrl(_options.RoutePrefix), page.Title, page.Icon),
                _logger,
                _assemblies?.Invoke());
            _hosts.Set(new KitPageHostEntry(page.Key, id, result.Registered ? KitPageHost.PluginPages : KitPageHost.Fallback, result.Api + ": " + result.Detail));
            if (result.Registered)
            {
                _logger.LogInformation("{Plugin}: registered page {Id} ({Url}) with Plugin Pages through {Api}; {Detail}", _options.PluginName, id, page.ShellUrl(_options.RoutePrefix), result.Api, result.Detail);
            }
            else
            {
                _logger.LogInformation("{Plugin}: page {Id} is not registered with Plugin Pages ({Api}: {Detail}); it uses #/{Prefix}-{Key}", _options.PluginName, id, result.Api, result.Detail, _options.Prefix, page.Key);
            }
        }

        if (skipped.Count > 0)
        {
            _logger.LogInformation(
                "{Plugin}: {Count} page(s) not registered with Plugin Pages (PageHostMode={Mode}, RegisterPluginPages={Register}); they use their #/{Prefix}-<key> routes",
                _options.PluginName,
                skipped.Count,
                mode,
                register,
                _options.Prefix);
        }
    }

    private PageHostMode ReadMode()
    {
        string? value;
        try
        {
            value = _options.PageHostMode(_services);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the {Plugin} PageHostMode setting failed, so FallbackOnly is used", _options.PluginName);
            return PageHostMode.FallbackOnly;
        }

        if (!PageHostModes.TryParse(value, out var mode))
        {
            _logger.LogWarning("The {Plugin} PageHostMode setting {Value} isn't FallbackOnly, Auto or PluginPagesOnly, so FallbackOnly is used", _options.PluginName, value);
        }

        return mode;
    }

    private bool ReadRegisterSetting()
    {
        try
        {
            return _options.RegisterPluginPages(_services);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the {Plugin} RegisterPluginPages setting failed, so no page is registered with Plugin Pages", _options.PluginName);
            return false;
        }
    }
}
