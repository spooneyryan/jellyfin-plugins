using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The users' own naming preferences through
/// <see cref="IBookmarkPreferences"/>, and the capabilities 1.1 adds (the limits, the shortcut, the player and
/// page settings, and the preferences with the server's defaults).
/// </summary>
public sealed class BookmarkPreferencesTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly PluginConfiguration _configuration = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob", canDownload: false);
    private readonly IBookmarkPreferences _preferences;
    private readonly IBookmarkManager _manager;

    public BookmarkPreferencesTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _preferences = new BookmarkPreferencesService(_store, NullLogger<BookmarkPreferencesService>.Instance);
        _manager = new BookmarkManager(
            _store,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IClipExtractionService>(),
            Mock.Of<IClipPlanner>(),
            ClipFileSupport.Ops(_store),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(_configuration),
            NullLogger<BookmarkManager>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preferences_AreEachUsersOwn_AndSettingTheDefaultsRemovesTheEntry()
    {
        var none = _preferences.Get(_alice);
        Assert.Null(none.AskForName);
        Assert.False(none.PauseWhileNaming);

        await _preferences.SetAsync(_alice, new BookmarkPreferencesDto { AskForName = true, PauseWhileNaming = true }, Ct);

        var set = _preferences.Get(_alice);
        Assert.True(set.AskForName);
        Assert.True(set.PauseWhileNaming);
        Assert.Null(_preferences.Get(_bob).AskForName);
        Assert.True(_store.Documents.Current.UserPreferences.ContainsKey(_alice.Id));
        Assert.False(_store.Documents.Current.UserPreferences.ContainsKey(_bob.Id));

        // Replace semantics: AskForName back to the server's default, the pause off: the defaults are no entry at all.
        await _preferences.SetAsync(_alice, new BookmarkPreferencesDto { AskForName = false }, Ct);
        Assert.False(_preferences.Get(_alice).AskForName);
        Assert.False(_preferences.Get(_alice).PauseWhileNaming);
        await _preferences.SetAsync(_alice, new BookmarkPreferencesDto(), Ct);
        Assert.Empty(_store.Documents.Current.UserPreferences);
        Assert.Null(_preferences.Get(_alice).AskForName);
    }

    [Fact]
    public async Task ALaterVersionsPreferences_StayWhenThisVersionSetsOrClearsItsOwn()
    {
        // Unknown fields survive a round trip, also when this version goes back to the defaults.
        var file = _directory.Combine("later", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, $$$"""{"Version":2,"UserPreferences":{"{{{_alice.Id:N}}}":{"AskForName":true,"ShowChapters":"always"}},"Bookmarks":[]}""", Ct);
        using var store = BookmarkStore.Create(file, NullLogger.Instance);
        var preferences = new BookmarkPreferencesService(store, NullLogger<BookmarkPreferencesService>.Instance);

        Assert.True(preferences.Get(_alice).AskForName);
        await preferences.SetAsync(_alice, new BookmarkPreferencesDto { PauseWhileNaming = true }, Ct);
        Assert.Contains("\"ShowChapters\": \"always\"", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);

        await preferences.SetAsync(_alice, new BookmarkPreferencesDto(), Ct);
        var stored = store.GetPreferences(_alice.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.AskForName);
        Assert.False(stored.PauseWhileNaming);
        Assert.Equal("always", stored.Extra!["ShowChapters"].GetString());
        Assert.Null(preferences.Get(_alice).AskForName);
    }

    [Fact]
    public async Task ReadOnlyData_Is503_AndNothingIsWritten()
    {
        var file = _directory.Combine("newer", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """{"Version":3,"Bookmarks":[]}""", Ct);
        using var store = BookmarkStore.Create(file, NullLogger.Instance);
        var preferences = new BookmarkPreferencesService(store, NullLogger<BookmarkPreferencesService>.Instance);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => preferences.SetAsync(_alice, new BookmarkPreferencesDto { AskForName = true }, Ct));

        Assert.Equal(PluginErrorKind.Unavailable, error.Kind);
        Assert.Equal("""{"Version":3,"Bookmarks":[]}""", await File.ReadAllTextAsync(file, Ct));
        Assert.Null(preferences.Get(_alice).AskForName);
    }

    [Fact]
    public async Task Capabilities_CarryTheUsersPreferences_WithTheServersDefault()
    {
        var byDefault = _manager.GetCapabilities(_alice, isAdmin: false);
        Assert.False(byDefault.AskForName);
        Assert.True(byDefault.AskForNameIsDefault);
        Assert.False(byDefault.AskForNameDefault);
        Assert.False(byDefault.PauseWhileNaming);

        _configuration.AskForNameDefault = true;
        Assert.True(_manager.GetCapabilities(_alice, isAdmin: false).AskForName);
        Assert.True(_manager.GetCapabilities(_alice, isAdmin: false).AskForNameIsDefault);
        Assert.True(_manager.GetCapabilities(_alice, isAdmin: false).AskForNameDefault);

        await _preferences.SetAsync(_alice, new BookmarkPreferencesDto { AskForName = false, PauseWhileNaming = true }, Ct);
        var chosen = _manager.GetCapabilities(_alice, isAdmin: false);
        Assert.False(chosen.AskForName);
        Assert.False(chosen.AskForNameIsDefault);
        Assert.True(chosen.PauseWhileNaming);

        // The server's default is still named next to the user's own choice (the Player options dialog
        // shows it in "Use the server's default (ask)").
        Assert.True(chosen.AskForNameDefault);

        // Another user still follows the server's default.
        Assert.True(_manager.GetCapabilities(_bob, isAdmin: false).AskForName);
        Assert.True(_manager.GetCapabilities(_bob, isAdmin: false).AskForNameIsDefault);
        Assert.True(_manager.GetCapabilities(_bob, isAdmin: false).AskForNameDefault);
    }

    [Fact]
    public void Capabilities_CarryTheLimits_TheShortcut_ThePlayerAndPageSettings()
    {
        var capabilities = _manager.GetCapabilities(_alice, isAdmin: true);

        Assert.Equal(100, capabilities.MaxNameLength);
        Assert.Equal(500, capabilities.MaxNoteLength);
        Assert.Equal("Shift+B", capabilities.Shortcuts.BookmarkNow);
        // A manager without the kit's shortcut policy reports none (the test below has one).
        Assert.Empty(capabilities.ReservedShortcuts);
        Assert.True(capabilities.ShowPlayerButtons);
        Assert.True(capabilities.SuggestChapterNames);

        // The My Bookmarks page is there since 1.1.0, on while EnableBookmarksPage is; the My Clips page since 1.2.0, on
        // while EnableClipsPage is (Pages.Clips; false in 1.1.x, which had no such page).
        Assert.True(capabilities.Pages.Bookmarks);
        Assert.True(capabilities.Pages.Clips);
        Assert.True(BookmarksKit.HasPage(BookmarksKit.BookmarksPage));
        Assert.True(BookmarksKit.HasPage(BookmarksKit.ClipsPage));
        Assert.True(BookmarksKit.HasPage(BookmarksKit.DemoPageA));
        Assert.False(BookmarksKit.HasPage("nope"));

        _configuration.BookmarkNowShortcut = "  Ctrl+Shift+K ";
        _configuration.ShowPlayerButtons = false;
        _configuration.SuggestChapterNames = false;
        _configuration.EnableBookmarksPage = false;
        var changed = _manager.GetCapabilities(_alice, isAdmin: true);
        Assert.Equal("Ctrl+Shift+K", changed.Shortcuts.BookmarkNow);
        Assert.False(changed.ShowPlayerButtons);
        Assert.False(changed.SuggestChapterNames);
        Assert.False(changed.Pages.Bookmarks);
        Assert.True(changed.Pages.Clips, "each page has its own setting");
        _configuration.EnableBookmarksPage = true;
        _configuration.EnableClipsPage = false;
        var clipsOff = _manager.GetCapabilities(_alice, isAdmin: false);
        Assert.True(clipsOff.Pages.Bookmarks);
        Assert.False(clipsOff.Pages.Clips, "read on every request, so the delete dialogs' default follows at once");
        _configuration.EnableClipsPage = true;

        // Empty turns the shortcut off, and so does a setting the XML left out.
        _configuration.BookmarkNowShortcut = string.Empty;
        Assert.Equal(string.Empty, _manager.GetCapabilities(_alice, isAdmin: true).Shortcuts.BookmarkNow);
        _configuration.BookmarkNowShortcut = null!;
        Assert.Equal(string.Empty, _manager.GetCapabilities(_alice, isAdmin: true).Shortcuts.BookmarkNow);
    }

    [Fact]
    public void Capabilities_ListTheReservedShortcuts_FromTheKitsPolicy()
    {
        // The kit's shortcut policy over a plugin configurations folder with JE's settings: jellyfin-web's player
        // keys, JE's configured keys and the caller's own override, read again on every request; another user's own
        // override is never in a capabilities response.
        using var configurations = new TempDirectory();
        File.WriteAllText(
            Path.Combine(configurations.Path, "Jellyfin.Plugin.JellyfinEnhanced.xml"),
            "<PluginConfiguration><Shortcuts><Shortcut><Name>BookmarkCurrentTime</Name><Key>B</Key></Shortcut></Shortcuts></PluginConfiguration>");
        var paths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(configurations.Path);
        var reader = new Jellyfin.Plugin.Kit.Interop.JellyfinEnhancedReader(paths.Object, null, new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(), NullLogger.Instance);
        IBookmarkManager manager = new BookmarkManager(
            _store,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IClipExtractionService>(),
            Mock.Of<IClipPlanner>(),
            ClipFileSupport.Ops(_store),
            ClipFileSupport.Guard(_store),
            TestData.ConfigurationProvider(_configuration),
            NullLogger<BookmarkManager>.Instance,
            new Jellyfin.Plugin.Kit.Interop.ShortcutPolicy(reader));

        var reserved = manager.GetCapabilities(_alice, isAdmin: false).ReservedShortcuts;

        Assert.Contains("K", reserved);
        Assert.Contains("B", reserved);
        Assert.Contains("/", reserved);
        Assert.DoesNotContain("Shift+B", reserved);
        Assert.Equal(reserved.Count, new HashSet<string>(reserved, StringComparer.Ordinal).Count);

        // Bob's own JE shortcut joins Bob's list at the next request after the memo (half a second) lapses, and never
        // Alice's, admin or not: what another user bound is their data.
        var userFolder = Path.Combine(configurations.Path, "Jellyfin.Plugin.JellyfinEnhanced", _bob.Id.ToString("N"));
        Directory.CreateDirectory(userFolder);
        File.WriteAllText(Path.Combine(userFolder, "shortcuts.json"), "{\"Shortcuts\": [{\"Name\": \"BookmarkCurrentTime\", \"Key\": \"Shift+Alt+B\"}]}");
        reader.Invalidate();
        Assert.Contains("Shift+Alt+B", manager.GetCapabilities(_bob, isAdmin: false).ReservedShortcuts);
        Assert.DoesNotContain("Shift+Alt+B", manager.GetCapabilities(_alice, isAdmin: false).ReservedShortcuts);
        Assert.DoesNotContain("Shift+Alt+B", manager.GetCapabilities(_alice, isAdmin: true).ReservedShortcuts);
        Assert.Contains("B", manager.GetCapabilities(_alice, isAdmin: false).ReservedShortcuts);
    }

    [Fact]
    public void Settings_Defaults_AreTheShippedOnes()
    {
        var configuration = new PluginConfiguration();
        Assert.True(configuration.EnableBookmarksPage);
        Assert.Equal("Shift+B", configuration.BookmarkNowShortcut);
        Assert.True(configuration.ShowPlayerButtons);
        Assert.False(configuration.AskForNameDefault);
        Assert.True(configuration.SuggestChapterNames);

        // Jellyfin keeps the settings with XmlSerializer: a 1.0 or early 1.1 settings file without them reads the defaults.
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("""<?xml version="1.0" encoding="utf-8"?><PluginConfiguration><EnableClips>true</EnableClips></PluginConfiguration>""");
        var read = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal("Shift+B", read.BookmarkNowShortcut);
        Assert.True(read.EnableBookmarksPage);
        Assert.True(read.ShowPlayerButtons);
    }

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }
}
