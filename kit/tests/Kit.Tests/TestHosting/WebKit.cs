using System;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.Kit.Tests.TestHosting;

/// <summary>
/// Kit options with a real assembly (Kit.Tests, for its version and MVID) and the injection filter built from them, for
/// the web tests.
/// </summary>
internal static class WebKit
{
    public static KitOptions Options(
        Func<IServiceProvider, bool>? webUiEnabled = null,
        Func<HttpContext, string?>? headFragment = null,
        string prefix = "jfkt",
        string routePrefix = "Fx",
        string pluginName = "Kit Test")
        => new()
        {
            PluginName = pluginName,
            RoutePrefix = routePrefix,
            Prefix = prefix,
            SchemaPrefix = routePrefix,
            DataFolder = "kittest",
            PluginAssembly = typeof(WebKit).Assembly,
            WebUiEnabled = webUiEnabled ?? (static _ => true),
            HeadFragment = headFragment
        };

    /// <summary>
    /// The script tag of <paramref name="options"/>, spelled out as the kit's injection writes it.
    /// </summary>
    public static string ExpectedTag(KitOptions options)
    {
        var assembly = options.PluginAssembly;
        var version = assembly.GetName().Version!.ToString();
        var mvid = assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
        return $"<script data-{options.Prefix}-plugin=\"{version}\" src=\"../{options.RoutePrefix}/ClientScript?v={version}-{mvid}\" defer></script>";
    }

    public static Func<IServiceProvider, IStartupFilter> Filter(KitOptions options, ListLogger? logger = null)
        => services => new IndexHtmlInjectionStartupFilter(options, services.GetRequiredService<IServerConfigurationManager>(), LoggerFactory(logger ?? new ListLogger()));

    public static ILoggerFactory LoggerFactory(ListLogger logger)
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(logger);
        return factory.Object;
    }
}
