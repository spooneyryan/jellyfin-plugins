using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="JellyfinEnhancedReader"/> over a plugin configurations folder holding the
/// <c>je</c> sandbox fixture's XML files and the synthetic data of <c>kit/testdata/je-data</c>: read-only, tolerant of
/// missing, malformed, locked and oversized files, with JE's state from the plugin manager and Plugin Pages' list from its
/// manager in memory.
/// </summary>
public sealed class JellyfinEnhancedReaderTests : IDisposable
{
    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();
    private static readonly string Fixture = Path.Combine(Root, "kit", "testdata", "je-sandbox", "plugins", "configurations");
    private static readonly string Data = Path.Combine(Root, "kit", "testdata", "je-data");
    private static readonly string PrivateData = Path.Combine(Root, "private", "testdata", "je-data");
    private static readonly Guid User1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid User2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TheFixtureXml_Reads_FlagsAndShortcuts_InFileOrder()
    {
        CopyFixtureXml();

        var settings = Reader().ReadSettings();

        Assert.True(settings.Exists);
        Assert.Null(settings.Problem);
        Assert.True(settings.Flag("BookmarksEnabled"));
        Assert.True(settings.Flag("BookmarksUsePluginPages"));
        Assert.True(settings.Flag("BookmarksAutoCreateCustomTab"));
        Assert.True(settings.Flag("ShowUserReviews"));
        Assert.False(settings.Flag("ShowUserRatingOnPosters"));
        Assert.False(settings.Flag("DisableAllShortcuts"));
        Assert.Equal(20, settings.Shortcuts.Count);
        Assert.Equal("JumpToPercentage", settings.Shortcuts[0].Name);
        Assert.Equal("0-9", settings.Shortcuts[0].Key);
        var b = Assert.Single(settings.Shortcuts, s => s.Name == "BookmarkCurrentTime");
        Assert.Equal("B", b.Key);
        Assert.Equal("Bookmark current time", b.Label);
        Assert.Equal("Player", b.Category);
    }

    [Fact]
    public void The1211Xml_Reads_TheTenFlags_AndTheTwentyShortcuts_WithTheirLabels()
    {
        // kit/testdata/je-data/Jellyfin.Plugin.JellyfinEnhanced.xml: a hand-written stand-in for JE 12.11's settings file
        // (XmlSerializer's root and namespaces, the settings the kit reads, made-up Example* elements the reader skips),
        // with ShowUserReviews and the mirror flipped on.
        var reader = Reader();
        File.Copy(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.xml"), reader.SettingsFile);

        var settings = reader.ReadSettings();

        Assert.Null(settings.Problem);
        Assert.Equal(JellyfinEnhancedDefaults.Flags.Keys.Except(Only1214).Order(StringComparer.Ordinal), settings.Flags.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(settings.Set);
        Assert.True(settings.Flag("ShowUserReviews"));
        Assert.True(settings.Flag("MirrorReviewRatingsToJellyfin"));
        Assert.False(settings.Flag("ShowUserRatingOnPosters"));
        Assert.True(settings.Flag("BookmarksEnabled"));
        Assert.False(settings.Flag("BookmarksUsePluginPages"));
        Assert.False(settings.Flag("DisableAllShortcuts"));
        Assert.Equal(JellyfinEnhancedDefaults.Shortcuts, settings.Shortcuts);

        // The reviews checklist reads the mirror from the file (it read only the flags of JellyfinEnhancedDefaults.Flags,
        // which lacked 12.11's, so the item was always "Off." before the review).
        var snapshot = new JellyfinEnhancedSnapshot(new JePluginState(true, true, "12.11.0.0", "Active"), settings, [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);
        var reviews = JeSwitchOff.Describe(JeFeature.Reviews, snapshot);
        Assert.True(reviews.FeatureOn);
        var mirror = reviews.Checklist.Single(i => i.Key == "MirrorReviewRatingsToJellyfin");
        Assert.False(mirror.Done);
        Assert.StartsWith("Still on. While it is on, Jellyfin Enhanced writes each review's stars", mirror.Detail, StringComparison.Ordinal);
        Assert.Contains(ShortcutPolicy.JellyfinEnhanced(snapshot), r => r.Combo == "B" && !r.IsDefault);

        // The stand-in: every element is a setting the kit reads, the shortcut list or a made-up Example* filler.
        var root = XDocument.Load(reader.SettingsFile).Root!;
        Assert.Equal("PluginConfiguration", root.Name.LocalName);
        Assert.All(root.Elements().Select(e => e.Name.LocalName), n => Assert.True(JellyfinEnhancedDefaults.Flags.ContainsKey(n) || n == "Shortcuts" || n.StartsWith("Example", StringComparison.Ordinal), n));
        Assert.Contains(root.Elements(), e => e.Name.LocalName.StartsWith("Example", StringComparison.Ordinal));
        Assert.DoesNotMatch("[0-9a-fA-F]{32}", File.ReadAllText(reader.SettingsFile));
    }

    [Fact]
    public void The1212Xml_Reads_TheSameTenFlags_AndTwentyShortcuts_AndKeeps1211sElementsInOrder()
    {
        // kit/testdata/je-data/Jellyfin.Plugin.JellyfinEnhanced.12.12.xml: the hand-written stand-in for JE 12.12's
        // settings file: the 12.11 stand-in's elements in their order and made-up ExampleAdded* ones standing for the
        // settings 12.12 added, nothing renamed, moved or removed, so the reader is the same for both.
        var reader = Reader();
        File.Copy(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.12.12.xml"), reader.SettingsFile);

        var settings = reader.ReadSettings();

        Assert.Null(settings.Problem);
        Assert.Equal(JellyfinEnhancedDefaults.Flags.Keys.Except(Only1214).Order(StringComparer.Ordinal), settings.Flags.Keys.Order(StringComparer.Ordinal));
        Assert.True(settings.Flag("ShowUserReviews"));
        Assert.True(settings.Flag("MirrorReviewRatingsToJellyfin"));
        Assert.True(settings.Flag("BookmarksEnabled"));
        Assert.False(settings.Flag("BookmarksUsePluginPages"));
        Assert.False(settings.Flag("BookmarksUseCustomTabs"));
        Assert.False(settings.Flag("BookmarksAutoCreateCustomTab"));
        Assert.False(settings.Flag("DisableAllShortcuts"));
        Assert.Equal(JellyfinEnhancedDefaults.Shortcuts, settings.Shortcuts);

        var root = XDocument.Load(reader.SettingsFile).Root!;
        var names1212 = root.Elements().Select(e => e.Name.LocalName).ToList();
        var names1211 = XDocument.Load(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.xml")).Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(names1211, names1212.Where(names1211.Contains));
        Assert.Equal(new[] { "ExampleAddedSettingA", "ExampleAddedSettingB", "ExampleAddedSettingC", "ExampleAddedSettingD" }, names1212.Except(names1211));
        Assert.Equal(settings.Flags, ReadFile(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.xml")).Flags);

        // On 12.12 the checklist words the key step for its Keyboard tab's grid; the reviews checklist reads the mirror.
        var snapshot = new JellyfinEnhancedSnapshot(new JePluginState(true, true, "12.12.0.0", "Active"), settings, [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);
        Assert.Equal(JeSwitchOff.ShortcutAdminGridText, JeSwitchOff.Describe(JeFeature.Bookmarks, snapshot).Checklist.Single(i => i.Key == "ShortcutAdmin").Text);
        Assert.False(JeSwitchOff.Describe(JeFeature.Reviews, snapshot).Checklist.Single(i => i.Key == "MirrorReviewRatingsToJellyfin").Done);
        Assert.Contains(ShortcutPolicy.JellyfinEnhanced(snapshot), r => r.Combo == "B" && !r.IsDefault);
        Assert.DoesNotMatch("[0-9a-fA-F]{32}", File.ReadAllText(reader.SettingsFile));
    }

    // The settings files JE 12.11 and 12.12 write for a fresh install (every property of their PluginConfiguration in
    // declaration order with the constructor's defaults, generated from the DLLs, sanitised, ShowUserReviews and the
    // mirror flipped on) are not part of this repository; these two run only where a copy is in the optional local folder
    // private/testdata/je-data (gitignored), and check the stand-ins above read the same.
    [Fact]
    public void TheWhole1211SettingsFile_Reads_TheTenFlags_AndTheTwentyShortcuts()
    {
        var file = Path.Combine(PrivateData, "Jellyfin.Plugin.JellyfinEnhanced.xml");
        Assert.SkipUnless(File.Exists(file), "The whole JE settings files aren't in this checkout (the optional local folder private/testdata/je-data).");

        var settings = ReadFile(file);

        Assert.Null(settings.Problem);
        Assert.Equal(JellyfinEnhancedDefaults.Flags.Keys.Except(Only1214).Order(StringComparer.Ordinal), settings.Flags.Keys.Order(StringComparer.Ordinal));
        Assert.True(settings.Flag("ShowUserReviews"));
        Assert.True(settings.Flag("MirrorReviewRatingsToJellyfin"));
        Assert.False(settings.Flag("ShowUserRatingOnPosters"));
        Assert.True(settings.Flag("BookmarksEnabled"));
        Assert.False(settings.Flag("BookmarksUsePluginPages"));
        Assert.False(settings.Flag("DisableAllShortcuts"));
        Assert.Equal(JellyfinEnhancedDefaults.Shortcuts, settings.Shortcuts);
        var snapshot = new JellyfinEnhancedSnapshot(new JePluginState(true, true, "12.11.0.0", "Active"), settings, [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);
        Assert.False(JeSwitchOff.Describe(JeFeature.Reviews, snapshot).Checklist.Single(i => i.Key == "MirrorReviewRatingsToJellyfin").Done);

        // The stand-in in kit/testdata reads the same.
        var standIn = ReadFile(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.xml"));
        Assert.Equal(settings.Flags, standIn.Flags);
        Assert.Equal(settings.Shortcuts, standIn.Shortcuts);

        // Sanitised: nothing in it points JE at a service or identifies an install.
        var root = XDocument.Load(file).Root!;
        var endpoints = root.Elements().Where(e => Regex.IsMatch(e.Name.LocalName, "(ApiKey|Urls?|UrlMappings|Instances|Secret|InstallId|TMDB_API_KEY|ImageUrl)$")).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.Empty(e.Value));
        Assert.DoesNotMatch("[0-9a-fA-F]{32}", File.ReadAllText(file));
        Assert.Equal(332, root.Elements().Count());
    }

    [Fact]
    public void TheWhole1212SettingsFile_Reads_TheSameFlags_AndKeeps1211sElementsInOrder()
    {
        var file = Path.Combine(PrivateData, "Jellyfin.Plugin.JellyfinEnhanced.12.12.xml");
        Assert.SkipUnless(File.Exists(file), "The whole JE settings files aren't in this checkout (the optional local folder private/testdata/je-data).");

        var settings = ReadFile(file);

        Assert.Null(settings.Problem);
        Assert.Equal(JellyfinEnhancedDefaults.Flags.Keys.Except(Only1214).Order(StringComparer.Ordinal), settings.Flags.Keys.Order(StringComparer.Ordinal));
        Assert.True(settings.Flag("ShowUserReviews"));
        Assert.True(settings.Flag("MirrorReviewRatingsToJellyfin"));
        Assert.True(settings.Flag("BookmarksEnabled"));
        Assert.False(settings.Flag("BookmarksUsePluginPages"));
        Assert.False(settings.Flag("BookmarksUseCustomTabs"));
        Assert.False(settings.Flag("BookmarksAutoCreateCustomTab"));
        Assert.False(settings.Flag("DisableAllShortcuts"));
        Assert.Equal(JellyfinEnhancedDefaults.Shortcuts, settings.Shortcuts);
        Assert.Equal(settings.Flags, ReadFile(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.12.12.xml")).Flags);

        // 19 new settings (Catch Up among them, off by default), nothing renamed, moved or removed.
        var root = XDocument.Load(file).Root!;
        var names1212 = root.Elements().Select(e => e.Name.LocalName).ToList();
        var names1211 = XDocument.Load(Path.Combine(PrivateData, "Jellyfin.Plugin.JellyfinEnhanced.xml")).Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(351, names1212.Count);
        Assert.Equal(332, names1211.Count);
        Assert.Equal(names1211, names1212.Where(names1211.Contains));
        Assert.Equal("false", root.Element("CatchUpEnabled")!.Value);
        Assert.Equal("false", root.Element("CatchUpUsePluginPages")!.Value);

        // Sanitised like the 12.11 file.
        var endpoints = root.Elements().Where(e => Regex.IsMatch(e.Name.LocalName, "(ApiKey|Urls?|UrlMappings|Instances|Secret|InstallId|TMDB_API_KEY|ImageUrl)$")).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.Empty(e.Value));
        Assert.Empty(root.Element("AnalyticsLastPayloadJson")!.Value);
        Assert.DoesNotMatch("[0-9a-fA-F]{32}", File.ReadAllText(file));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AUserFolderListing_ThatFails_IsSaidInTheSnapshot_NotReadAsNoUsers()
    {
        // A data folder that is there but can't be listed used to read as "no user has own shortcuts", which ticked the
        // users' item and offered Use B. Denying the listing needs a Windows ACL.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Denying a folder listing needs a Windows ACL.");
        var reader = Reader();
        Directory.CreateDirectory(reader.DataFolder);
        var folder = new DirectoryInfo(reader.DataFolder);
        var security = folder.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        security.AddAccessRule(deny);
        folder.SetAccessControl(security);
        try
        {
            var users = reader.ReadUserShortcuts(out var problem);
            Assert.Empty(users);
            Assert.StartsWith("Jellyfin Enhanced's data folder couldn't be listed: ", problem, StringComparison.Ordinal);
            Assert.Empty(reader.ListUsersWithBookmarks(out var listProblem));
            Assert.Equal(problem, listProblem);
            var snapshot = reader.Read();
            Assert.Equal(problem, snapshot.UsersProblem);
            Assert.Empty(snapshot.Users);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            folder.SetAccessControl(security);
        }

        // Listable again (and the memo lapsed): no problem.
        reader.Invalidate();
        Assert.Null(reader.Read().UsersProblem);
    }

    [Fact]
    public void ASettingLeftOut_ReadsJesDefault_AndTheLastOfARepeatedElement_Wins()
    {
        File.WriteAllText(
            Reader().SettingsFile,
            "<?xml version=\"1.0\"?><PluginConfiguration><ShowUserReviews>true</ShowUserReviews><ShowUserReviews>false</ShowUserReviews><BookmarksEnabled>maybe</BookmarksEnabled>"
            + "<Shortcuts><Shortcut><Name>BookmarkCurrentTime</Name><Key></Key></Shortcut><Shortcut><Name>Later</Name></Shortcut></Shortcuts></PluginConfiguration>");

        var settings = Reader().ReadSettings();

        Assert.False(settings.Flag("ShowUserReviews"));
        Assert.True(settings.Flag("BookmarksEnabled"), "text that isn't a boolean reads the default");
        Assert.False(settings.Flags.ContainsKey("BookmarksEnabled"));
        Assert.False(settings.Flag("BookmarksUsePluginPages"));
        Assert.Equal([("BookmarkCurrentTime", ""), ("Later", null)], settings.Shortcuts.Select(s => (s.Name, s.Key)));
    }

    [Fact]
    public void MissingAndMalformedFiles_FailSoft()
    {
        var reader = Reader();
        Assert.Same(JeSettings.Missing, reader.ReadSettings());
        Assert.Equal(JeDataFormat.Missing, reader.ReadReviews().Format);
        Assert.Equal(JeDataFormat.Missing, reader.ReadBookmarks(User1).Format);
        Assert.Empty(reader.ReadUserShortcuts());
        Assert.Empty(reader.ListUsersWithBookmarks());
        var tabs = reader.ReadCustomTabs();
        Assert.False(tabs.Installed);
        Assert.Empty(tabs.Tabs);
        Assert.Null(tabs.Problem);

        File.WriteAllText(reader.SettingsFile, "<PluginConfiguration><ShowUserReviews>true</ShowUserReviews>");
        File.WriteAllText(reader.CustomTabsFile, "not xml at all");
        var broken = reader.ReadSettings();
        Assert.True(broken.Exists);
        Assert.StartsWith("Jellyfin.Plugin.JellyfinEnhanced.xml isn't well-formed XML", broken.Problem, StringComparison.Ordinal);
        Assert.True(broken.Flag("BookmarksEnabled"), "the defaults while the file can't be read");
        Assert.StartsWith("Jellyfin.Plugin.CustomTabs.xml isn't well-formed XML", reader.ReadCustomTabs().Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ADtd_IsRefused_NotResolved()
    {
        var reader = Reader();
        File.WriteAllText(reader.SettingsFile, "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///C:/Windows/win.ini\">]><PluginConfiguration><ShowUserReviews>&e;</ShowUserReviews></PluginConfiguration>");

        var settings = reader.ReadSettings();

        Assert.NotNull(settings.Problem);
        Assert.Empty(settings.Flags);
    }

    [Fact]
    public void TheDataFolder_GivesEachUsersFiles_AndTheReviews()
    {
        CopyData();
        var reader = Reader();

        Assert.Equal([User1, User2], reader.ListUsersWithBookmarks());
        var bookmarks = reader.ReadBookmarks(User1);
        Assert.Equal(JeDataFormat.Bookmarks1, bookmarks.Format);
        Assert.Equal(3, bookmarks.Entries.Count);
        Assert.Equal(4, reader.ReadBookmarks(User2).Invalid.Count);
        Assert.Equal(4, reader.ReadReviews().Entries.Count);

        var shortcuts = Assert.Single(reader.ReadUserShortcuts());
        Assert.Equal(User1, shortcuts.UserId);
        Assert.Null(shortcuts.Problem);
        Assert.Equal("Shift+Alt+B", shortcuts.Shortcuts[0].Key);
    }

    [Fact]
    public void UserFolders_InAnyGuidSpelling_AreRead_OthersAreSkipped()
    {
        var reader = Reader();
        var upper = Path.Combine(reader.DataFolder, User1.ToString("N").ToUpperInvariant());
        var dashed = Path.Combine(reader.DataFolder, User2.ToString("D"));
        Directory.CreateDirectory(upper);
        Directory.CreateDirectory(dashed);
        Directory.CreateDirectory(Path.Combine(reader.DataFolder, "cdn-cache"));
        Directory.CreateDirectory(Path.Combine(reader.DataFolder, Guid.Empty.ToString("N")));
        File.Copy(Path.Combine(Data, "00000000000000000000000000000001", "bookmark.json"), Path.Combine(upper, "bookmark.json"));
        File.WriteAllText(Path.Combine(dashed, "shortcuts.json"), "{\"Shortcuts\": [{\"Name\": \"BookmarkCurrentTime\", \"Key\": \"\"}]}");
        File.WriteAllText(Path.Combine(reader.DataFolder, "cdn-cache", "shortcuts.json"), "{}");
        File.WriteAllText(Path.Combine(reader.DataFolder, Guid.Empty.ToString("N"), "shortcuts.json"), "{}");

        Assert.Equal([User1], reader.ListUsersWithBookmarks());
        Assert.Equal(JeDataFormat.Bookmarks1, reader.ReadBookmarks(User1).Format);
        var user = Assert.Single(reader.ReadUserShortcuts());
        Assert.Equal(User2, user.UserId);
        Assert.Equal(string.Empty, user.Shortcuts[0].Key);
    }

    [Fact]
    public void AUserWithTwoFolderSpellings_IsListedOnce_AndReadFromTheNFolder_ElseTheFirstSpellingWithTheFile()
    {
        // JE 12.8+ renames other spellings to the N form at its start, but leaves one when the N folder is already
        // there; reading both imported the user's bookmarks twice.
        var reader = Reader();
        var own = Path.Combine(reader.DataFolder, User1.ToString("N"));
        var dashed = Path.Combine(reader.DataFolder, User1.ToString("D"));
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(dashed);
        File.Copy(Path.Combine(Data, "00000000000000000000000000000001", "bookmark.json"), Path.Combine(own, "bookmark.json"));
        File.WriteAllText(Path.Combine(dashed, "bookmark.json"), "{\"Bookmarks\": {\"Bm_old\": {\"ItemId\": \"11111111111111111111111111111111\", \"Timestamp\": 1}}}");
        File.WriteAllText(Path.Combine(own, "shortcuts.json"), "{\"Shortcuts\": [{\"Name\": \"BookmarkCurrentTime\", \"Key\": \"Shift+B\"}]}");
        File.WriteAllText(Path.Combine(dashed, "shortcuts.json"), "{\"Shortcuts\": [{\"Name\": \"BookmarkCurrentTime\", \"Key\": \"B\"}]}");
        Directory.CreateDirectory(Path.Combine(reader.DataFolder, User2.ToString("D")));
        File.WriteAllText(Path.Combine(reader.DataFolder, User2.ToString("D"), "bookmark.json"), "{\"Bookmarks\": {}}");

        // The D folder sorts first, yet the N folder's files are the user's, as JE reads them.
        Assert.Equal([User1, User2], reader.ListUsersWithBookmarks());
        Assert.Equal(3, reader.ReadBookmarks(User1).Entries.Count);
        var shortcuts = Assert.Single(reader.ReadUserShortcuts());
        Assert.Equal((User1, "Shift+B"), (shortcuts.UserId, shortcuts.Shortcuts[0].Key));

        // Without a file in the N folder, the older spelling's is the user's (listed once still).
        File.Delete(Path.Combine(own, "bookmark.json"));
        Assert.Equal([User1, User2], reader.ListUsersWithBookmarks());
        Assert.Equal("Bm_old", Assert.Single(reader.ReadBookmarks(User1).Entries).Key);
        File.Delete(Path.Combine(dashed, "bookmark.json"));
        Assert.Equal([User2], reader.ListUsersWithBookmarks());
        Assert.Equal(JeDataFormat.Missing, reader.ReadBookmarks(User1).Format);
    }

    [Fact]
    public void AnOversizedFile_AndAFileBeingWritten_AreReportedNotThrown()
    {
        var reader = Reader();
        Directory.CreateDirectory(reader.DataFolder);
        var reviews = Path.Combine(reader.DataFolder, "reviews.json");
        using (var big = new FileStream(reviews, FileMode.Create))
        {
            big.SetLength(JellyfinEnhancedFiles.MaxBytes + 1);
        }

        var tooBig = reader.ReadReviews();
        Assert.Equal(JeDataFormat.Unreadable, tooBig.Format);
        Assert.Contains("over the 20 MB", tooBig.Problem, StringComparison.Ordinal);

        // JE writes the whole file; a reader meanwhile sees a cut-off document (and shares the file, so JE's write goes on).
        File.WriteAllText(reviews, "{\"Reviews\": {\"a\": {\"TmdbId\": \"1\", \"Rating\":");
        using (var writer = new FileStream(reviews, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var partial = reader.ReadReviews();
            Assert.Equal(JeDataFormat.Unreadable, partial.Format);
            Assert.StartsWith("Not valid JSON (the file may be being written; try again)", partial.Problem, StringComparison.Ordinal);
            Assert.True(writer.CanWrite);
        }

        using (new FileStream(reviews, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = reader.ReadReviews();
            Assert.Equal(JeDataFormat.Unreadable, locked.Format);
            Assert.Contains("couldn't be read", locked.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePluginManager_SaysWhetherJeAndCustomTabsRun_ElseTheFilesDo()
    {
        CopyFixtureXml();
        var manager = new Mock<IPluginManager>();
        var je = new LocalPlugin("C:\\plugins\\Jellyfin Enhanced_12.11.0.0", true, new PluginManifest { Id = JellyfinEnhancedFiles.PluginId, Name = "Jellyfin Enhanced", Version = "12.11.0.0", Status = PluginStatus.Active })
        {
            Instance = Mock.Of<IPlugin>()
        };
        var older = new LocalPlugin("C:\\plugins\\Jellyfin Enhanced_12.8.0.0", true, new PluginManifest { Id = JellyfinEnhancedFiles.PluginId, Name = "Jellyfin Enhanced", Version = "12.8.0.0", Status = PluginStatus.Superseded });
        var tabs = new LocalPlugin("C:\\plugins\\Custom Tabs_0.2.6.0", true, new PluginManifest { Id = JellyfinEnhancedFiles.CustomTabsPluginId, Name = "Custom Tabs", Version = "0.2.6.0", Status = PluginStatus.Restart });
        manager.SetupGet(m => m.Plugins).Returns([older, je, tabs]);

        var reader = Reader(manager.Object);
        var state = reader.ReadPluginState();
        Assert.True(state.Installed);
        Assert.True(state.Active);
        Assert.Equal("12.11.0.0", state.Version);
        Assert.Equal("Active", state.Status);

        var customTabs = reader.ReadCustomTabs();
        Assert.True(customTabs.Installed);
        Assert.False(customTabs.Active, "a pending restart isn't running");
        var tab = Assert.Single(customTabs.Tabs);
        Assert.Equal(("Bookmarks", "<div class=\"sections bookmarks\"></div>"), (tab.Title, tab.ContentHtml));

        // A plugin that was deleted doesn't count; without a manager the settings file decides.
        manager.SetupGet(m => m.Plugins).Returns([new LocalPlugin("C:\\plugins\\x", true, new PluginManifest { Id = JellyfinEnhancedFiles.PluginId, Status = PluginStatus.Deleted })]);
        Assert.False(Reader(manager.Object).ReadPluginState().Installed);
        var byFile = Reader().ReadPluginState();
        Assert.True(byFile.Installed);
        Assert.True(byFile.Active);
        Assert.Equal("Unknown", byFile.Status);
    }

    [Fact]
    public void Read_TakesEverythingAtOnce_AndKeepsItForHalfASecond()
    {
        CopyFixtureXml();
        CopyData();
        using var pages = FakePluginPages.Version24();
        var pagesManager = pages.CreateManager();
        var services = new ServiceCollection();
        services.AddSingleton(pages.ManagerInterface, pagesManager);
        using var provider = services.BuildServiceProvider();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var reader = new JellyfinEnhancedReader(Paths(), null, provider, NullLogger.Instance, clock)
        {
            // Another test class's fake may be loaded at the same time; this one's own assembly is the one to list.
            PluginPagesLister = services => PluginPagesBridge.TryListPages(services, [pages.Assembly])
        };
        PluginPagesBridge.TryRegister(provider, new("Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage", "/JellyfinEnhanced/bookmarksPage", "Bookmarks", "bookmark"), NullLogger.Instance, [pages.Assembly]);

        var first = reader.Read();

        Assert.True(first.Plugin.Installed);
        Assert.True(first.Settings.Flag("BookmarksEnabled"));
        Assert.Single(first.Users);
        Assert.Single(first.CustomTabs.Tabs);
        // The fake's collectible load context is among AssemblyLoadContext.All, so the bridge finds it as it finds the
        // real Plugin Pages, and its DI manager lists the page registered above.
        Assert.True(first.PluginPages.Installed);
        Assert.Contains(first.PluginPages.Pages, p => p.Id == "Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage" && p.DisplayText == "Bookmarks");

        File.Delete(reader.SettingsFile);
        Assert.Same(first, reader.Read());
        clock.Advance(TimeSpan.FromMilliseconds(600));
        var second = reader.Read();
        Assert.NotSame(first, second);
        Assert.False(second.Settings.Exists);

        reader.Invalidate();
        Assert.NotSame(second, reader.Read());
    }

    [Fact]
    public void The1214ReviewSettings_AreRead_AndTheTranslationServiceOnlyForWhetherItIsSet()
    {
        // JE 12.14's review elements, as the je sandbox's file has them, with the translation service set:
        // the reader says it is set, never what it is, and neither the checklist nor the snapshot holds the value.
        var reader = Reader();
        File.WriteAllText(reader.SettingsFile, """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <ShowReviews>true</ShowReviews>
              <ShowUserReviews>true</ShowUserReviews>
              <ShowAnilistReviews>true</ShowAnilistReviews>
              <ReviewTranslationEnabled>false</ReviewTranslationEnabled>
              <ReviewTranslationUrl>https://translate.example.invalid/x</ReviewTranslationUrl>
              <ReviewTranslationApiKey>   </ReviewTranslationApiKey>
              <ReviewsExpandedByDefault>true</ReviewsExpandedByDefault>
              <HideReviewsFromHiddenUsers>false</HideReviewsFromHiddenUsers>
              <HideReviewsFromDisabledUsers>true</HideReviewsFromDisabledUsers>
              <MirrorReviewRatingsToJellyfin>false</MirrorReviewRatingsToJellyfin>
              <ActivityFeedShowReviewed>false</ActivityFeedShowReviewed>
              <SpoilerStripReviews>false</SpoilerStripReviews>
            </PluginConfiguration>
            """);

        var settings = reader.ReadSettings();

        Assert.Null(settings.Problem);
        Assert.True(settings.Flag("ShowReviews"));
        Assert.True(settings.Flag("ShowAnilistReviews"));
        Assert.False(settings.Flag("ReviewTranslationEnabled"));
        Assert.True(settings.Flag("ReviewsExpandedByDefault"));
        Assert.False(settings.Flag("HideReviewsFromHiddenUsers"));
        Assert.True(settings.Flag("HideReviewsFromDisabledUsers"));
        Assert.False(settings.Flag("ActivityFeedShowReviewed"));
        Assert.False(settings.Flag("SpoilerStripReviews"));
        Assert.True(settings.IsSet("ReviewTranslationUrl"));
        Assert.False(settings.IsSet("ReviewTranslationApiKey"), "blanks are not set");
        Assert.Equal(["ReviewTranslationUrl"], settings.Set);

        var snapshot = new JellyfinEnhancedSnapshot(new JePluginState(true, true, "12.14.0.0", "Active"), settings, [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);
        var reviews = JeSwitchOff.Describe(JeFeature.Reviews, snapshot);
        var translation = reviews.Checklist.Single(i => i.Key == "ReviewTranslationEnabled");
        Assert.False(translation.Done);
        Assert.Equal("Still to do: a service URL is set. Turn translation off and clear both fields in Jellyfin Enhanced's settings.", translation.Detail);
        Assert.DoesNotContain(reviews.Checklist, i => (i.Text + i.Detail).Contains("example.invalid", StringComparison.Ordinal));
        Assert.DoesNotContain("example.invalid", System.Text.Json.JsonSerializer.Serialize(snapshot.Settings), StringComparison.Ordinal);

        // Without the 12.14 elements (an older JE), their defaults: off, and nothing set.
        File.WriteAllText(reader.SettingsFile, "<PluginConfiguration><ShowUserReviews>true</ShowUserReviews></PluginConfiguration>");
        var older = reader.ReadSettings();
        Assert.False(older.Flag("ShowAnilistReviews"));
        Assert.False(older.Flag("ReviewTranslationEnabled"));
        Assert.True(older.Flag("HideReviewsFromHiddenUsers"));
        Assert.True(older.Flag("ActivityFeedShowReviewed"));
        Assert.Empty(older.Set);
    }

    // A file that can't be read says its name and a plain reason, never its folder (the system's message holds the full
    // path); the system's message goes to the log.
    [Fact]
    public void AFileThatCantBeRead_SaysItsNameAndAPlainReason_NeverItsPath()
    {
        var reader = Reader();
        Directory.CreateDirectory(reader.DataFolder);
        var reviews = Path.Combine(reader.DataFolder, JellyfinEnhancedFiles.ReviewsFile);
        File.WriteAllText(reviews, "{\"Reviews\": {}}");
        File.WriteAllText(reader.SettingsFile, "<PluginConfiguration />");
        using (new FileStream(reviews, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (new FileStream(reader.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var file = reader.ReadReviews();
            Assert.Equal(JeDataFormat.Unreadable, file.Format);
            Assert.Equal("reviews.json couldn't be read: it's in use or couldn't be opened (the server log has the details).", file.Problem);
            var settings = reader.ReadSettings();
            Assert.Equal("Jellyfin.Plugin.JellyfinEnhanced.xml couldn't be read: it's in use or couldn't be opened (the server log has the details).", settings.Problem);
        }

        Assert.Equal("access to it was denied (the server log has the details).", JellyfinEnhancedReader.PlainReason(new UnauthorizedAccessException("Access to the path 'C:\\secret\\x' is denied.")));
        File.WriteAllText(reader.SettingsFile, "<PluginConfiguration>\n  <ShowUserReviews>true</ShowUserReviews>\n  <Oops");
        Assert.Equal("Jellyfin.Plugin.JellyfinEnhanced.xml isn't well-formed XML at line 3, position 8 (the file may be being written; try again).", reader.ReadSettings().Problem);
        Assert.DoesNotContain(reader.ConfigurationsFolder, reader.ReadSettings().Problem!, StringComparison.OrdinalIgnoreCase);
    }

    // An unreadable settings file through the reader: a settings file cut off while JE writes it, with ShowUserReviews
    // really on, is a problem (never JE's defaults as its values), and the reviews checklist says Unknown for every item.
    [Fact]
    public void AHalfWrittenSettingsFile_IsAProblem_AndTheReviewsChecklistIsUnknown()
    {
        var reader = Reader();
        var whole = File.ReadAllText(Path.Combine(Data, "Jellyfin.Plugin.JellyfinEnhanced.xml"));
        Assert.Contains("<ShowUserReviews>true</ShowUserReviews>", whole, StringComparison.Ordinal);
        File.WriteAllText(reader.SettingsFile, whole[..(whole.IndexOf("<ShowUserReviews>true</ShowUserReviews>", StringComparison.Ordinal) + 60)]);

        var settings = reader.ReadSettings();

        Assert.True(settings.Exists);
        Assert.StartsWith("Jellyfin.Plugin.JellyfinEnhanced.xml isn't well-formed XML", settings.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(reader.ConfigurationsFolder, settings.Problem!, StringComparison.OrdinalIgnoreCase);
        var reviews = JeSwitchOff.Describe(JeFeature.Reviews, new JellyfinEnhancedSnapshot(new JePluginState(true, true, "12.14.0.0", "Active"), settings, [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled));
        Assert.All(reviews.Checklist, i => Assert.True(i.Unknown && !i.Done, i.Key));
        Assert.False(reviews.Complete);
    }

    // The flags JE 12.14 added, which the 12.11 and 12.12 files haven't (they read as their defaults).
    private static readonly string[] Only1214 = ["ShowAnilistReviews", "ReviewTranslationEnabled"];

    private static JeSettings ReadFile(string file)
    {
        using var folder = new TempDirectory();
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(folder.Path);
        var reader = new JellyfinEnhancedReader(paths.Object, null, new ServiceCollection().BuildServiceProvider(), NullLogger.Instance);
        File.Copy(file, reader.SettingsFile);
        return reader.ReadSettings();
    }

    private JellyfinEnhancedReader Reader(IPluginManager? pluginManager = null)
        => new(Paths(), pluginManager, new ServiceCollection().BuildServiceProvider(), NullLogger.Instance);

    private IApplicationPaths Paths()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_folder.Path);
        return paths.Object;
    }

    private void CopyFixtureXml()
    {
        File.Copy(Path.Combine(Fixture, "Jellyfin.Plugin.JellyfinEnhanced.xml"), Path.Combine(_folder.Path, "Jellyfin.Plugin.JellyfinEnhanced.xml"), overwrite: true);
        File.Copy(Path.Combine(Fixture, "Jellyfin.Plugin.CustomTabs.xml"), Path.Combine(_folder.Path, "Jellyfin.Plugin.CustomTabs.xml"), overwrite: true);
    }

    private void CopyData()
    {
        var target = Path.Combine(_folder.Path, JellyfinEnhancedFiles.ConfigurationName);
        foreach (var file in Directory.EnumerateFiles(Data, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Data, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
