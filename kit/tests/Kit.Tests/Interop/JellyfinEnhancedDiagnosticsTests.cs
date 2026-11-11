using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="JellyfinEnhancedDiagnostics"/> and <see cref="KitJellyfinEnhancedServiceCollectionExtensions.AddKitJellyfinEnhanced"/>:
/// the Diagnostics entry <c>JellyfinEnhanced</c> a plugin's settings page renders the banner, the
/// shortcut warnings and the checklist from, serialisable as JSON.
/// </summary>
public sealed class JellyfinEnhancedDiagnosticsTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "testdata", "je-sandbox", "plugins", "configurations");

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TheEntry_CarriesStateSettingsShortcutsOverlapAndOurChecks()
    {
        File.Copy(Path.Combine(Fixture, "Jellyfin.Plugin.JellyfinEnhanced.xml"), Path.Combine(_folder.Path, "Jellyfin.Plugin.JellyfinEnhanced.xml"));
        File.Copy(Path.Combine(Fixture, "Jellyfin.Plugin.CustomTabs.xml"), Path.Combine(_folder.Path, "Jellyfin.Plugin.CustomTabs.xml"));
        var alice = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_folder.Path, "Jellyfin.Plugin.JellyfinEnhanced", alice.ToString("N")));
        File.WriteAllText(Path.Combine(_folder.Path, "Jellyfin.Plugin.JellyfinEnhanced", alice.ToString("N"), "shortcuts.json"), "{\"Shortcuts\": [{\"Name\": \"BookmarkCurrentTime\", \"Key\": \"Shift+Alt+B\"}]}");
        var userManager = new Mock<IUserManager>();
        userManager.Setup(m => m.GetUserById(alice)).Returns(new Jellyfin.Database.Implementations.Entities.User("alice", "Default", "Default"));
        using var provider = Provider(JeFeature.Bookmarks, [new("BookmarkNow", "Bookmark this moment", "b")], userManager.Object);

        var body = provider.GetRequiredService<IKitDiagnostics>().Describe();
        var json = JsonSerializer.SerializeToElement(body);
        var entry = json.GetProperty("JellyfinEnhanced");

        Assert.True(entry.GetProperty("Installed").GetBoolean());
        Assert.True(entry.GetProperty("SettingsFile").GetBoolean());
        Assert.True(entry.GetProperty("Settings").GetProperty("BookmarksEnabled").GetBoolean());
        Assert.Equal("B", entry.GetProperty("Shortcuts").GetProperty("BookmarkCurrentTime").GetString());
        var user = Assert.Single(entry.GetProperty("UserShortcuts").EnumerateArray());
        Assert.Equal(alice.ToString("N"), user.GetProperty("UserId").GetString());
        Assert.Equal("alice", user.GetProperty("Name").GetString());
        Assert.Equal("Shift+Alt+B", user.GetProperty("Overrides").GetProperty("BookmarkCurrentTime").GetString());
        var tab = Assert.Single(entry.GetProperty("CustomTabs").GetProperty("Tabs").EnumerateArray());
        Assert.True(tab.GetProperty("JellyfinEnhancedBookmarks").GetBoolean());
        // Plugin Pages isn't installed in the test process, unless another test class's fake assembly is loaded at the same
        // time (then its manager isn't in this provider, which the entry reports as a Plugin Pages problem); the bridge's
        // own tests cover the list.
        Assert.Equal(JsonValueKind.Array, entry.GetProperty("PluginPages").GetProperty("Pages").ValueKind);
        Assert.DoesNotContain(entry.GetProperty("Problems").EnumerateArray(), p => !p.GetString()!.StartsWith("Plugin Pages: ", StringComparison.Ordinal));

        var overlap = entry.GetProperty("Overlap");
        Assert.Equal("Bookmarks", overlap.GetProperty("Feature").GetString());
        Assert.True(overlap.GetProperty("On").GetBoolean());
        Assert.Equal(JeSwitchOff.BookmarksBanner, overlap.GetProperty("Banner").GetString());
        Assert.False(overlap.GetProperty("CanUseB").GetBoolean());
        Assert.Equal(8, overlap.GetProperty("Checklist").GetArrayLength());
        // Alice's own Shift+Alt+B isn't B (JE compares whole combinations), so she holds nothing.
        var users = overlap.GetProperty("Checklist").EnumerateArray().Single(i => i.GetProperty("Key").GetString() == "ShortcutUsers");
        Assert.True(users.GetProperty("Done").GetBoolean());
        Assert.Equal("No user's own list sets it.", users.GetProperty("Detail").GetString());
        Assert.Equal(JsonValueKind.Null, overlap.GetProperty("UseBNote").ValueKind);

        var ours = Assert.Single(entry.GetProperty("OurShortcuts").EnumerateArray());
        Assert.Equal("BookmarkNow", ours.GetProperty("Name").GetString());
        Assert.Equal("B", ours.GetProperty("Combo").GetString());
        Assert.True(ours.GetProperty("Reserved").GetBoolean());
        Assert.Equal("B is taken by Jellyfin Enhanced's Bookmark Current Time, so Bookmark this moment won't work with it. Choose another key.", ours.GetProperty("Warning").GetString());
        Assert.Contains("B", entry.GetProperty("ReservedCombos").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("Shift+Alt+B", entry.GetProperty("ReservedCombos").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void WithoutJe_AndNoFeature_TheEntryIsQuiet()
    {
        using var provider = Provider(JeFeature.None, [], null);

        var body = provider.GetRequiredService<IKitDiagnostics>().Describe();
        var entry = JsonSerializer.SerializeToElement(body).GetProperty("JellyfinEnhanced");

        Assert.False(entry.GetProperty("Installed").GetBoolean());
        Assert.Equal("None", entry.GetProperty("Overlap").GetProperty("Feature").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("Overlap").GetProperty("Banner").ValueKind);
        Assert.Empty(entry.GetProperty("Overlap").GetProperty("Checklist").EnumerateArray());
        Assert.Empty(entry.GetProperty("OurShortcuts").EnumerateArray());
        Assert.Contains("K", entry.GetProperty("ReservedCombos").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void AddKitJellyfinEnhanced_RegistersTheReaderThePolicyAndTheSource_Once()
    {
        using var provider = Provider(JeFeature.Reviews, [], null);

        Assert.NotNull(provider.GetRequiredService<JellyfinEnhancedReader>());
        Assert.NotNull(provider.GetRequiredService<ShortcutPolicy>());
        Assert.Same(provider.GetRequiredService<JellyfinEnhancedReader>(), provider.GetRequiredService<JellyfinEnhancedReader>());
        Assert.Single(provider.GetServices<IKitDiagnosticsSource>(), s => s.Key == "JellyfinEnhanced");
        Assert.Equal(JeFeature.Reviews, provider.GetRequiredService<JellyfinEnhancedDiagnostics>().Overlap().Feature);
    }

    [Fact]
    public void WithTheBannerSettingOff_TheBannerIsLeftOut_AndSaysSo_WhileTheChecklistStays()
    {
        // The common setting ShowJellyfinEnhancedBanner: the overlap banner is the plugin's to hide; the checklist and the
        // shortcut checks are not.
        File.Copy(Path.Combine(Fixture, "Jellyfin.Plugin.JellyfinEnhanced.xml"), Path.Combine(_folder.Path, "Jellyfin.Plugin.JellyfinEnhanced.xml"));
        var show = false;
        using var provider = Provider(JeFeature.Bookmarks, [new("BookmarkNow", "Bookmark this moment", "B")], null, _ => show);

        var hidden = JsonSerializer.SerializeToElement(provider.GetRequiredService<IKitDiagnostics>().Describe()).GetProperty("JellyfinEnhanced");
        Assert.True(hidden.GetProperty("Overlap").GetProperty("On").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("Overlap").GetProperty("Banner").ValueKind);
        Assert.True(hidden.GetProperty("Overlap").GetProperty("BannerHidden").GetBoolean());
        Assert.Equal(8, hidden.GetProperty("Overlap").GetProperty("Checklist").GetArrayLength());
        Assert.True(Assert.Single(hidden.GetProperty("OurShortcuts").EnumerateArray()).GetProperty("Reserved").GetBoolean());

        // Read on every request: turned on again, the banner is back.
        show = true;
        var shown = JsonSerializer.SerializeToElement(provider.GetRequiredService<IKitDiagnostics>().Describe()).GetProperty("JellyfinEnhanced");
        Assert.Equal(JeSwitchOff.BookmarksBanner, shown.GetProperty("Overlap").GetProperty("Banner").GetString());
        Assert.False(shown.GetProperty("Overlap").GetProperty("BannerHidden").GetBoolean());

        // A setting that can't be read shows the banner, as by default.
        using var failing = Provider(JeFeature.Bookmarks, [], null, _ => throw new InvalidOperationException("no settings"));
        var fallback = JsonSerializer.SerializeToElement(failing.GetRequiredService<IKitDiagnostics>().Describe()).GetProperty("JellyfinEnhanced");
        Assert.Equal(JeSwitchOff.BookmarksBanner, fallback.GetProperty("Overlap").GetProperty("Banner").GetString());
    }

    [Fact]
    public void CheckShortcut_ChecksAValueBeforeItIsSaved_AsTheSavedSettingIsChecked()
    {
        // Bookmarks' settings page: its shortcut field asks for the check of what the admin types; the answer has the shape
        // of an OurShortcuts entry, with the normalised combination the page saves.
        File.Copy(Path.Combine(Fixture, "Jellyfin.Plugin.JellyfinEnhanced.xml"), Path.Combine(_folder.Path, "Jellyfin.Plugin.JellyfinEnhanced.xml"));
        using var provider = Provider(JeFeature.Bookmarks, [new("BookmarkNow", "Bookmark this moment", "Shift+B")], null);
        var diagnostics = provider.GetRequiredService<JellyfinEnhancedDiagnostics>();

        var free = JsonSerializer.SerializeToElement(diagnostics.CheckShortcut("BookmarkNow", "shift+b"));
        Assert.Equal("BookmarkNow", free.GetProperty("Name").GetString());
        Assert.Equal("Bookmark this moment", free.GetProperty("Label").GetString());
        Assert.Equal("Shift+B", free.GetProperty("Combo").GetString());
        Assert.True(free.GetProperty("Valid").GetBoolean());
        Assert.False(free.GetProperty("Reserved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, free.GetProperty("Warning").ValueKind);

        var player = JsonSerializer.SerializeToElement(diagnostics.CheckShortcut("BookmarkNow", "m"));
        Assert.Equal("M", player.GetProperty("Combo").GetString());
        Assert.True(player.GetProperty("Reserved").GetBoolean());
        Assert.Equal("M is taken by jellyfin-web's video player, so Bookmark this moment won't work with it. Choose another key.", player.GetProperty("Warning").GetString());
        Assert.Equal("JellyfinWeb", Assert.Single(player.GetProperty("Reservations").EnumerateArray()).GetProperty("Owner").GetString());

        var je = JsonSerializer.SerializeToElement(diagnostics.CheckShortcut("BookmarkNow", "B"));
        Assert.Equal("B is taken by Jellyfin Enhanced's Bookmark Current Time, so Bookmark this moment won't work with it. Choose another key.", je.GetProperty("Warning").GetString());

        var invalid = JsonSerializer.SerializeToElement(diagnostics.CheckShortcut("BookmarkNow", "Shift+"));
        Assert.False(invalid.GetProperty("Valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("Combo").ValueKind);
        Assert.Equal("\"Shift+\" isn't a key combination; use a key with optional modifiers, such as Shift+B.", invalid.GetProperty("Warning").GetString());

        var off = JsonSerializer.SerializeToElement(diagnostics.CheckShortcut("BookmarkNow", "  "));
        Assert.Equal(string.Empty, off.GetProperty("Combo").GetString());
        Assert.True(off.GetProperty("Valid").GetBoolean());
        Assert.False(off.GetProperty("Reserved").GetBoolean());

        Assert.Null(diagnostics.CheckShortcut("NoSuchShortcut", "B"));
    }

    private ServiceProvider Provider(JeFeature feature, IReadOnlyList<JeShortcutSetting> shortcuts, IUserManager? userManager, Func<IServiceProvider, bool>? showBanner = null)
    {
        var options = new KitOptions
        {
            PluginName = "Kit Test",
            RoutePrefix = "KitTest",
            Prefix = "jfkt",
            SchemaPrefix = "KitTest",
            DataFolder = "kittest",
            PluginAssembly = typeof(JellyfinEnhancedDiagnosticsTests).Assembly,
            PluginId = Guid.NewGuid(),
            JellyfinEnhanced = new JeInteropOptions { Overlap = feature, Shortcuts = _ => shortcuts, ShowBanner = showBanner ?? (_ => true) }
        };
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_folder.Path);
        var services = new ServiceCollection();
        services.AddSingleton(paths.Object);
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", isActive: true, "ok"));
        if (userManager is not null)
        {
            services.AddSingleton(userManager);
        }

        services.AddKit(options);
        services.AddKitDiagnostics();
        services.AddKitJellyfinEnhanced();
        services.AddKitJellyfinEnhanced();
        return services.BuildServiceProvider();
    }
}
