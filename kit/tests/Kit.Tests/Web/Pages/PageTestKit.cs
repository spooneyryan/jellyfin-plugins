using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Web.Pages;

namespace Jellyfin.Plugin.Kit.Tests.Web.Pages;

/// <summary>
/// Kit options of a pretend plugin with user pages ("Kit Test", route <c>Fx</c>, prefix <c>jfkt</c>): <c>alpha</c> and
/// <c>beta</c>, whose on/off state and settings the tests change through <see cref="Settings"/>.
/// </summary>
internal sealed class PageTestKit
{
    public PageTestKit(string pluginName = "Kit Test")
    {
        Options = new KitOptions
        {
            PluginName = pluginName,
            RoutePrefix = "Fx",
            Prefix = "jfkt",
            SchemaPrefix = "Fx",
            DataFolder = "kittest",
            PluginAssembly = typeof(PageTestKit).Assembly,
            Pages =
            [
                new KitPage("alpha", "Alpha", "video_library", "AlphaPage") { IsEnabled = _ => Settings.Enabled.Contains("alpha") },
                new KitPage("beta", "Beta", "collections_bookmark", "BetaPage") { IsEnabled = _ => Settings.ThrowOnEnabled ? throw new InvalidOperationException("broken setting") : Settings.Enabled.Contains("beta") },
            ],
            PageHostMode = _ => Settings.ThrowOnMode ? throw new InvalidOperationException("broken setting") : Settings.Mode,
            RegisterPluginPages = _ => Settings.Register,
            ShowFallbackNav = _ => Settings.Nav,
        };
    }

    public PageSettings Settings { get; } = new();

    public KitOptions Options { get; }

    public KitPages CreatePages(Jellyfin.Plugin.Kit.TestSupport.Logging.ListLogger? logger = null)
        => new(Options, WebKit.LoggerFactory(logger ?? new Jellyfin.Plugin.Kit.TestSupport.Logging.ListLogger()));

    internal sealed class PageSettings
    {
        public HashSet<string> Enabled { get; } = new(StringComparer.Ordinal) { "alpha", "beta" };

        public string? Mode { get; set; }

        public bool Register { get; set; } = true;

        public bool Nav { get; set; } = true;

        public bool ThrowOnEnabled { get; set; }

        public bool ThrowOnMode { get; set; }
    }
}
