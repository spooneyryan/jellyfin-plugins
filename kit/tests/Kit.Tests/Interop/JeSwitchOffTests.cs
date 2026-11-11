using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Interop;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="JeSwitchOff"/>: the banner
/// while JE's feature is on, each checklist item ticked as the snapshot shows the change, what stays until a restart or
/// isn't durable, and "Use B" offered only once <c>B</c> is durably free: JE's <c>BookmarkCurrentTime</c> off or another
/// key for the server default in a way the next start keeps, no user's own list setting <c>B</c>, every list readable.
/// </summary>
public sealed class JeSwitchOffTests
{
    private static readonly JePluginState Active = new(true, true, "12.11.0.0", "Active");

    // JE 12.12, whose Keyboard tab is a grid of keys recorded by a click and a key press (no Key field, no Disable button).
    private static readonly JePluginState Active1212 = new(true, true, "12.12.0.0", "Active");

    // The je sandbox fixture: everything on, B, the Custom Tab and the Plugin Pages link.
    private static JellyfinEnhancedSnapshot FixtureOn(IReadOnlyList<JeUserShortcuts>? users = null) => new(
        Active,
        Settings(bookmarks: true, pluginPages: true, customTabs: true, autoTab: true, key: "B"),
        users ?? [],
        new CustomTabsState(true, true, [new CustomTab("Bookmarks", "<div class=\"sections bookmarks\"></div>")], null),
        new PluginPagesState(true, [new PluginPagesEntry("Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage", "/JellyfinEnhanced/bookmarksPage", "Bookmarks")], null));

    // The fixture with JE's Bookmarks switched off and its key as given; the tab and the link still there.
    private static JellyfinEnhancedSnapshot SwitchedOff(string key, IReadOnlyList<JeUserShortcuts>? users = null, bool disableAll = false)
        => FixtureOn(users) with { Settings = Settings(bookmarks: false, pluginPages: false, customTabs: false, autoTab: false, key: key, disableAll: disableAll) };

    [Fact]
    public void WithEverythingOn_TheBannerShows_NothingIsTicked_AndUseBIsNotOffered()
    {
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, FixtureOn());

        Assert.Equal(JeFeature.Bookmarks, state.Feature);
        Assert.True(state.JeInstalled);
        Assert.True(state.FeatureOn);
        Assert.Equal(JeSwitchOff.BookmarksBanner, state.Banner);
        Assert.False(state.Complete);
        Assert.False(state.CanUseB);
        Assert.Null(state.UseBNote);
        // JE's four boxes in the order JE 12.11's Pages tab lets them be unticked (as checked in a sandbox): the one it
        // hides first, the one that greys out the others last.
        Assert.Equal(
            ["BookmarksAutoCreateCustomTab", "BookmarksUseCustomTabs", "BookmarksUsePluginPages", "BookmarksEnabled", "CustomTab", "PluginPagesLink", "ShortcutAdmin", "ShortcutUsers"],
            state.Checklist.Select(i => i.Key));
        Assert.Equal([false, false, false, false, false, false, false, true], state.Checklist.Select(i => i.Done));
        Assert.Equal(
            [
                "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Add the Custom Tabs entry for me off, first",
                "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Use Custom Tabs for Bookmarks off",
                "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Use Plugin Pages for Bookmarks Library off",
                "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: Enable Bookmarks Feature off, after the three above (it greys them out), then Save Settings"
            ],
            state.Checklist.Take(4).Select(i => i.Text));
        Assert.Equal("Still on. Untick it first: Jellyfin Enhanced hides it as soon as Use Custom Tabs for Bookmarks or Enable Bookmarks Feature is off, and then saves it as it is.", Item(state, "BookmarksAutoCreateCustomTab").Detail);
        Assert.Equal("Still on.", Item(state, "BookmarksUseCustomTabs").Detail);
        Assert.Equal("Still on. Its Plugin Pages link stays until the restart below.", Item(state, "BookmarksUsePluginPages").Detail);

        // The shortcut, the way JE 12.11's Keyboard tab does it: never "Shortcuts", never its Disable button.
        var admin = Item(state, "ShortcutAdmin");
        Assert.Equal(JeSwitchOff.ShortcutAdminText, admin.Text);
        Assert.Equal(
            "Last, the server default: on Jellyfin Enhanced's settings page, Keyboard tab, choose Bookmark Current Time in the list of shortcuts, type Disabled into the Key field and click Add (or type Disabled over the key in its own row under Current Overrides), then Save Settings; never its Disable button, never a blank key",
            admin.Text);
        Assert.Equal("Still \"B\". " + JeSwitchOff.ShortcutAdminStillB, admin.Detail);
        Assert.Contains("Type Disabled as its key as above; its Disable button saves a blank key, which Jellyfin Enhanced turns back into B at its next start.", admin.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Shortcuts:", admin.Text, StringComparison.Ordinal);
        Assert.Contains("set to Disabled or another key", Item(state, "ShortcutUsers").Text, StringComparison.Ordinal);
        Assert.Contains("Still there: \"Bookmarks\"", Item(state, "CustomTab").Detail, StringComparison.Ordinal);
        Assert.True(Item(state, "PluginPagesLink").NeedsRestart);
        Assert.Contains("restart Jellyfin, and once more", Item(state, "PluginPagesLink").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OnJe1212_TheKeyStep_IsWordedForItsKeyGrid_AndTheOtherItemsAsOn1211()
    {
        // JE 12.12 rebuilt its Keyboard tab: a grid where a key is changed by clicking it and pressing a combination. It
        // takes no typed text, so Disabled can't be set there; Delete on a key and the block button next to it save the
        // blank key that JE's next start turns back into B (BackfillMissingDefaultShortcuts, the same IL as 12.11's).
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, FixtureOn() with { Plugin = Active1212 });

        var admin = Item(state, "ShortcutAdmin");
        Assert.Equal(JeSwitchOff.ShortcutAdminGridText, admin.Text);
        // The grid has each row's key at its left end and its label at its right end, rows close together, so the item says
        // where the key is: a neighbouring row's key (Reset Playback Speed's R, Open Episode Preview's P) would move that
        // shortcut instead (since Bookmarks 1.1.1).
        Assert.Equal(
            "Last, the server default: on Jellyfin Enhanced's settings page, Keyboard tab, click the key (B) at the left end of the Bookmark Current Time row, press another key combination, such as Shift+X, then Save Settings; never Delete or the ⊘ button beside the key (Disable for every user), never a blank key (Backspace puts B back)",
            admin.Text);
        Assert.DoesNotContain("Disabled", admin.Text, StringComparison.Ordinal);
        Assert.Equal("Still \"B\". " + JeSwitchOff.ShortcutAdminGridStillB, admin.Detail);
        Assert.Contains("Give it another key as above; Delete and the ⊘ button save a blank key", admin.Detail, StringComparison.Ordinal);
        Assert.Contains("its B key stays until the shortcut has another key (the last step)", Item(state, "BookmarksEnabled").Detail, StringComparison.Ordinal);

        // JE 12.12's Pages tab is 12.11's (the same four boxes, greying and hiding): those items, the Custom Tab, the link
        // and the users' item read the same.
        var on1211 = JeSwitchOff.Describe(JeFeature.Bookmarks, FixtureOn());
        Assert.Equal(on1211.Checklist.Select(i => i.Key), state.Checklist.Select(i => i.Key));
        Assert.Equal(on1211.Checklist.Take(6).Select(i => i.Text), state.Checklist.Take(6).Select(i => i.Text));
        Assert.Equal(Item(on1211, "ShortcutUsers").Text, Item(state, "ShortcutUsers").Text);
        Assert.Equal(Item(on1211, "PluginPagesLink").Detail, Item(state, "PluginPagesLink").Detail);
        Assert.Contains("12.12 also when its settings are saved", Item(state, "PluginPagesLink").Detail, StringComparison.Ordinal);

        // A cleared key: the grid shows it as "Disabled" in a dimmed row, which isn't durable; the notes say another key.
        var cleared = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff(string.Empty) with { Plugin = Active1212 });
        var clearedAdmin = Item(cleared, "ShortcutAdmin");
        Assert.True(clearedAdmin.Done);
        Assert.True(clearedAdmin.NeedsRestart);
        Assert.Equal("Cleared, but only until the next restart: " + JeSwitchOff.ClearedKeyGridNote + " " + JeSwitchOff.ClearedKeyGridHow, clearedAdmin.Detail);
        Assert.Contains("shows its key as \"Disabled\" at the left end of the dimmed Bookmark Current Time row, which looks done and isn't: click that key, press another key combination, such as Shift+X, then Save Settings.", clearedAdmin.Detail, StringComparison.Ordinal);
        Assert.False(cleared.CanUseB);
        Assert.Equal(JeSwitchOff.UseBWithheld + JeSwitchOff.ClearedKeyGridNote, cleared.UseBNote);

        // Disable Keyboard Shortcuts alone, on 12.12.
        var all = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("B", disableAll: true) with { Plugin = Active1212 });
        Assert.Equal("Off only while Disable Keyboard Shortcuts is on (its own key is \"B\"). " + JeSwitchOff.DisabledAllGridNote, Item(all, "ShortcutAdmin").Detail);
        Assert.Equal(JeSwitchOff.UseBWithheld + JeSwitchOff.DisabledAllGridNote, all.UseBNote);

        // The grid's way, another combination: ticked for good, Use B offered. And Disabled, typed on 12.11 (or in the XML),
        // still frees B for good on 12.12, whose page keeps an override it can't make.
        foreach (var key in new[] { "Shift+X", "Disabled" })
        {
            var durable = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff(key) with { Plugin = Active1212 });
            Assert.True(Item(durable, "ShortcutAdmin").Done, key);
            Assert.False(Item(durable, "ShortcutAdmin").NeedsRestart, key);
            Assert.True(durable.CanUseB, key);
            Assert.Null(durable.UseBNote);
        }
    }

    [Theory]
    [InlineData("12.11.0.0", false)]
    [InlineData("12.8.0.0", false)]
    [InlineData("12.12.0.0", true)]
    [InlineData("12.13.1.0", true)]
    [InlineData("13.0", true)]
    [InlineData(null, true)]
    [InlineData("Unknown", true)]
    public void TheKeyGrid_Is1212sAndLaters_AndAnUnknownVersions(string? version, bool grid)
    {
        // An unknown version gets the newest page's words: the one a new install of JE brings.
        Assert.Equal(grid, JeSwitchOff.HasShortcutGrid(version));
    }

    [Fact]
    public void ALowerCaseB_TakesB_SinceJe1212ReadsALetterInEitherCase()
    {
        // JE 12.12's canonicalCombo upper-cases a one-letter key, so "b" (typed into 12.11's Key field, or the XML) acts
        // on B there; counted on 12.11 too, so neither the checklist nor Use B lets it through across the update.
        Assert.True(JeSwitchOff.TakesB("b"));
        Assert.False(JeSwitchOff.TakesB("Shift+b"));
        Assert.False(JeSwitchOff.TakesB("Ctrl+B"));

        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("b"));
        Assert.False(Item(state, "ShortcutAdmin").Done);
        Assert.StartsWith("Still \"b\". ", Item(state, "ShortcutAdmin").Detail, StringComparison.Ordinal);
        Assert.False(state.CanUseB);

        var alice = Guid.NewGuid();
        var users = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled", [new(alice, [new("BookmarkCurrentTime", "b")], null)]), _ => "alice");
        Assert.False(Item(users, "ShortcutUsers").Done);
        Assert.StartsWith("Still set by: alice (\"b\").", Item(users, "ShortcutUsers").Detail, StringComparison.Ordinal);
        Assert.False(users.CanUseB);
    }

    [Fact]
    public void TheTogglesTick_AsTheXmlShowsThem_TheLinkStaysUntilARestart()
    {
        // After JE's settings page saved with Bookmarks off (and removed its own Custom Tab), before the restart.
        var snapshot = SwitchedOff("B") with
        {
            CustomTabs = new CustomTabsState(true, true, [new CustomTab("Requests", "<div class=\"sections requests\"></div>")], null)
        };

        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, snapshot);

        Assert.False(state.FeatureOn);
        Assert.Null(state.Banner);
        Assert.Equal([true, true, true, true, true, false, false, true], state.Checklist.Select(i => i.Done));
        Assert.False(state.Complete);
        Assert.False(state.CanUseB, "B is still JE's key");
        Assert.Null(state.UseBNote);

        // After the restart: the link is gone.
        var restarted = snapshot with { PluginPages = new PluginPagesState(true, [], null) };
        Assert.True(Item(JeSwitchOff.Describe(JeFeature.Bookmarks, restarted), "PluginPagesLink").Done);
    }

    [Fact]
    public void BoxesJe1211SavesAsTheyAre_SayHowToFreeThem()
    {
        // JE 12.11's Pages tab greys out Use Plugin Pages and Use Custom Tabs while Enable Bookmarks Feature is off, hides
        // Add the Custom Tabs entry for me while either of those two is off, and its Save reads every box as it is: an
        // admin who unticked Enable Bookmarks Feature first saved the other three on (as checked in a sandbox).
        var stuck = FixtureOn() with { Settings = Settings(bookmarks: false, pluginPages: true, customTabs: true, autoTab: true, key: "B") };

        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, stuck);

        Assert.Equal([false, false, false, true], state.Checklist.Take(4).Select(i => i.Done));
        Assert.Equal(
            "Still on. Jellyfin Enhanced hides this box while Use Custom Tabs for Bookmarks or Enable Bookmarks Feature is off, and saves it as it is: tick those two again, then untick the boxes in this checklist's order and Save Settings.",
            Item(state, "BookmarksAutoCreateCustomTab").Detail);
        Assert.Equal(
            "Still on. Jellyfin Enhanced greys Use Custom Tabs for Bookmarks out while Enable Bookmarks Feature is off, and saves it as it is: tick Enable Bookmarks Feature again, then untick the boxes in this checklist's order and Save Settings.",
            Item(state, "BookmarksUseCustomTabs").Detail);
        Assert.Equal(
            "Still on. Its Plugin Pages link stays until the restart below. Jellyfin Enhanced greys Use Plugin Pages for Bookmarks Library out while Enable Bookmarks Feature is off, and saves it as it is: tick Enable Bookmarks Feature again, then untick the boxes in this checklist's order and Save Settings.",
            Item(state, "BookmarksUsePluginPages").Detail);
        Assert.Equal("Off.", Item(state, "BookmarksEnabled").Detail);

        // Use Custom Tabs off alone hides only the auto-create box.
        var tabsOff = FixtureOn() with { Settings = Settings(bookmarks: true, pluginPages: true, customTabs: false, autoTab: true, key: "B") };
        var hidden = JeSwitchOff.Describe(JeFeature.Bookmarks, tabsOff);
        Assert.StartsWith("Still on. Jellyfin Enhanced hides this box while ", Item(hidden, "BookmarksAutoCreateCustomTab").Detail, StringComparison.Ordinal);
        Assert.Equal("Still on. Its Plugin Pages link stays until the restart below.", Item(hidden, "BookmarksUsePluginPages").Detail);
    }

    [Fact]
    public void AClearedAdminKey_Ticks_ButIsFragile_AndUseBIsWithheldWithTheNote()
    {
        // JE's own "Disable" writes Key: "", which its next start drops and restores to B: B is free now, not for good.
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff(string.Empty));

        var admin = Item(state, "ShortcutAdmin");
        Assert.True(admin.Done);
        Assert.True(admin.NeedsRestart);
        Assert.StartsWith("Cleared, but only until the next restart: ", admin.Detail, StringComparison.Ordinal);
        // JE 12.11 shows the cleared key's row with a grey "Disabled" placeholder: the detail says to type the word.
        Assert.EndsWith(JeSwitchOff.ClearedKeyNote + " " + JeSwitchOff.ClearedKeyHow, admin.Detail, StringComparison.Ordinal);
        Assert.Contains("type the word Disabled into that field, then Save Settings", admin.Detail, StringComparison.Ordinal);
        Assert.True(Item(state, "ShortcutUsers").Done);
        Assert.False(Item(state, "ShortcutUsers").NeedsRestart);
        Assert.False(state.CanUseB, "not durable");
        Assert.Equal(JeSwitchOff.UseBWithheld + JeSwitchOff.ClearedKeyNote, state.UseBNote);
    }

    [Fact]
    public void AnUnpressableAdminKey_IsOffForGood_AndUseBIsOffered()
    {
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled"));

        Assert.True(Item(state, "ShortcutAdmin").Done);
        Assert.False(Item(state, "ShortcutAdmin").NeedsRestart);
        Assert.Equal("Off: its key is \"Disabled\", which no key press produces.", Item(state, "ShortcutAdmin").Detail);
        Assert.True(state.CanUseB);
        Assert.Null(state.UseBNote);
    }

    [Theory]
    [InlineData("Shift+X")]
    [InlineData("X")]
    [InlineData("Shift+B")]
    public void AnotherAdminKey_FreesB_ForGood(string key)
    {
        // JE's listener compares the whole combination it builds with the configured text, so any key but B leaves B
        // alone (the checklist's own advice, "another key", must count as done).
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff(key));

        var admin = Item(state, "ShortcutAdmin");
        Assert.True(admin.Done);
        Assert.False(admin.NeedsRestart);
        Assert.Equal($"Moved: its key is \"{key}\", so B does nothing in Jellyfin Enhanced.", admin.Detail);
        Assert.True(state.CanUseB);
        Assert.Null(state.UseBNote);
        Assert.False(JeSwitchOff.TakesB(key));
        Assert.True(JeSwitchOff.TakesB("B"));
    }

    [Fact]
    public void AUsersOwnKey_HoldsUseBBack_OnlyWhenItIsB_NamedWhenItCan_UntilTheyClearOrMoveIt()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();
        var dave = Guid.NewGuid();
        var erin = Guid.NewGuid();
        var users = new List<JeUserShortcuts>
        {
            new(alice, [new("BookmarkCurrentTime", "B")], null),
            new(bob, [new("BookmarkCurrentTime", string.Empty)], null),
            new(carol, [], "shortcuts.json couldn't be read: locked"),
            new(dave, [new("BookmarkCurrentTime", "X")], null),
            new(erin, [new("BookmarkCurrentTime", "Shift+B"), new("OpenSearch", "B")], null)
        };

        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled", users), id => id == alice ? "alice" : null);

        var item = Item(state, "ShortcutUsers");
        Assert.False(item.Done);
        Assert.False(item.NeedsRestart);
        Assert.Equal($"Still set by: alice (\"B\"), {carol:N} (file unreadable: shortcuts.json couldn't be read: locked). Each sets it to Disabled or another key in Jellyfin Enhanced's settings panel (Shortcuts), on their own account.", item.Detail);
        Assert.False(state.CanUseB);
        // The unreadable file is why it can't be told.
        Assert.Equal(JeSwitchOff.UseBWithheld + JeSwitchOff.UsersUnreadableNote, state.UseBNote);

        // Only a user who set B holds it: the item then says so itself.
        var onlyAlice = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled", [users[0]]));
        Assert.False(Item(onlyAlice, "ShortcutUsers").Done);
        Assert.False(onlyAlice.CanUseB);
        Assert.Null(onlyAlice.UseBNote);

        // Cleared, moved or unrelated overrides hold nothing.
        var done = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled", [users[1], users[3], users[4]]));
        Assert.True(Item(done, "ShortcutUsers").Done);
        Assert.Equal("No user's own list sets it.", Item(done, "ShortcutUsers").Detail);
        Assert.True(done.CanUseB);
        Assert.Null(done.UseBNote);
    }

    [Fact]
    public void AUsersListing_ThatFailed_KeepsTheUsersItemUnticked_AndUseBWithheld()
    {
        // The reader couldn't list JE's data folder: the empty user list says nothing.
        const string problem = "Jellyfin Enhanced's data folder couldn't be listed: Access to the path is denied.";
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled") with { UsersProblem = problem });

        var item = Item(state, "ShortcutUsers");
        Assert.False(item.Done);
        Assert.Equal("The users' own Jellyfin Enhanced shortcuts couldn't be listed, so this can't be checked: " + problem, item.Detail);
        Assert.False(state.CanUseB);
        Assert.Equal(JeSwitchOff.UseBWithheld + problem, state.UseBNote);
        Assert.False(state.Complete);
    }

    [Fact]
    public void DisableAllShortcuts_TicksBothShortcutItems_ButNotDurably_SoUseBIsWithheld()
    {
        // JE registers no key listener at all while the switch is on (events.js), so B is free whatever the keys say,
        // and comes back as soon as the switch is unticked.
        var alice = Guid.NewGuid();
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("B", [new(alice, [new("BookmarkCurrentTime", "B")], null)], disableAll: true), _ => "alice");

        var admin = Item(state, "ShortcutAdmin");
        Assert.True(admin.Done);
        Assert.True(admin.NeedsRestart);
        Assert.Equal("Off only while Disable Keyboard Shortcuts is on (its own key is \"B\"). " + JeSwitchOff.DisabledAllNote, admin.Detail);
        var users = Item(state, "ShortcutUsers");
        Assert.True(users.Done);
        Assert.True(users.NeedsRestart);
        Assert.Equal("Off while Disable Keyboard Shortcuts is on in Jellyfin Enhanced's settings, whatever the users' own lists say. Once it is unticked, still set by: alice (\"B\"). Each sets it to Disabled or another key in Jellyfin Enhanced's settings panel (Shortcuts), on their own account.", users.Detail);
        Assert.False(state.CanUseB);
        Assert.Equal(JeSwitchOff.UseBWithheld + JeSwitchOff.DisabledAllNote, state.UseBNote);

        // The switch on and the key cleared: both reasons, the switch's note first, and the cleared key named.
        var cleared = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff(string.Empty, disableAll: true));
        Assert.True(Item(cleared, "ShortcutAdmin").NeedsRestart);
        Assert.StartsWith("Off only while Disable Keyboard Shortcuts is on (its own key is \"\", cleared, which the next start restores to B). ", Item(cleared, "ShortcutAdmin").Detail, StringComparison.Ordinal);
        Assert.False(cleared.CanUseB);

        // The switch on next to a durable key: the key is what frees B, so Use B is offered.
        var durable = JeSwitchOff.Describe(JeFeature.Bookmarks, SwitchedOff("Disabled", disableAll: true));
        Assert.True(Item(durable, "ShortcutAdmin").Done);
        Assert.False(Item(durable, "ShortcutAdmin").NeedsRestart);
        Assert.Equal("Off: its key is \"Disabled\", which no key press produces. Disable Keyboard Shortcuts is on as well.", Item(durable, "ShortcutAdmin").Detail);
        Assert.True(Item(durable, "ShortcutUsers").Done);
        Assert.False(Item(durable, "ShortcutUsers").NeedsRestart);
        Assert.True(durable.CanUseB);
        Assert.Null(durable.UseBNote);
    }

    [Fact]
    public void WithoutJe_EverythingIsDone_AndUseBIsOffered()
    {
        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, JellyfinEnhancedSnapshot.None);

        Assert.False(state.JeInstalled);
        Assert.False(state.FeatureOn);
        Assert.Null(state.Banner);
        Assert.True(state.Complete);
        Assert.True(state.CanUseB);
        Assert.Null(state.UseBNote);
        Assert.All(state.Checklist, i => Assert.True(i.Done));
        Assert.All(state.Checklist, i => Assert.False(i.NeedsRestart));
        Assert.Equal("Jellyfin Enhanced isn't installed.", state.Checklist[0].Detail);
        Assert.Equal("Custom Tabs isn't installed.", Item(state, "CustomTab").Detail);
        Assert.Equal("Plugin Pages isn't installed.", Item(state, "PluginPagesLink").Detail);
    }

    [Fact]
    public void AnUnreadableFile_IsSaidInTheItem_AndKeepsItUnticked()
    {
        // A tab list or a page list that couldn't be read holds nothing, which says nothing: unticked, with the reason
        // (the items used to be ticked on an unreadable source).
        var snapshot = SwitchedOff("Disabled") with
        {
            CustomTabs = new CustomTabsState(true, true, [], "Jellyfin.Plugin.CustomTabs.xml isn't well-formed XML"),
            PluginPages = new PluginPagesState(true, [], "Plugin Pages (Jellyfin.Plugin.PluginPages 9.0.0.0) has an unexpected interface, so its list can't be read.")
        };

        var state = JeSwitchOff.Describe(JeFeature.Bookmarks, snapshot);

        Assert.False(Item(state, "CustomTab").Done);
        Assert.StartsWith("Custom Tabs' settings couldn't be read, so this can't be checked: ", Item(state, "CustomTab").Detail, StringComparison.Ordinal);
        Assert.False(Item(state, "PluginPagesLink").Done);
        Assert.False(Item(state, "PluginPagesLink").NeedsRestart, "no restart can be advised while the list can't be read");
        Assert.StartsWith("Plugin Pages' list couldn't be read, so this can't be checked: ", Item(state, "PluginPagesLink").Detail, StringComparison.Ordinal);
        Assert.False(state.Complete);
        Assert.True(state.CanUseB, "the shortcut items alone decide Use B");
    }

    [Fact]
    public void Reviews_HaveTheirOwnBanner_AndAnItemForEveryReviewSettingOfJe1214()
    {
        // JE's defaults but ShowUserReviews on (in User Ratings' switch-off order): four required (only user reviews to
        // do: the activity feed is off by default, so its reviewed list is done), two optional, five for
        // information only.
        var on = new JellyfinEnhancedSnapshot(Active, new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal) { ["ShowUserReviews"] = true }, [], null), [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);

        var state = JeSwitchOff.Describe(JeFeature.Reviews, on);

        Assert.True(state.FeatureOn);
        Assert.Equal(JeSwitchOff.ReviewsBanner, state.Banner);
        Assert.Equal(
            ["ShowUserReviews", "ShowReviews", "ShowAnilistReviews", "ReviewTranslationEnabled", "ReviewsExpandedByDefault", "MirrorReviewRatingsToJellyfin", "HideReviewsFromHiddenUsers", "HideReviewsFromDisabledUsers", "ActivityFeedShowReviewed", "SpoilerStripReviews", "ShowUserRatingOnPosters"],
            state.Checklist.Select(i => i.Key));
        Assert.Equal([false, true, true, false, true, false, true, true, false, true, true], state.Checklist.Select(i => i.Optional));
        Assert.Equal([false, true, true, true, true, true, true, true, true, true, true], state.Checklist.Select(i => i.Done));
        Assert.All(state.Checklist, i => Assert.False(i.Unknown));
        Assert.Equal("On.", Item(state, "HideReviewsFromHiddenUsers").Detail);
        Assert.Equal("On.", Item(state, "SpoilerStripReviews").Detail);
        Assert.Equal("Off.", Item(state, "ShowAnilistReviews").Detail);
        Assert.Equal("Off, and no service URL or API key is set.", Item(state, "ReviewTranslationEnabled").Detail);
        Assert.Contains("D-pad", Item(state, "ShowReviews").Text + Item(state, "ShowReviews").Detail, StringComparison.Ordinal);
        Assert.False(state.Complete);
        Assert.False(state.CanUseB);

        // User reviews and the feed off, the mirror on: not complete until the mirror is off too.
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal) { ["ShowUserReviews"] = false, ["ActivityFeedShowReviewed"] = false, ["MirrorReviewRatingsToJellyfin"] = true, ["ShowReviews"] = true };
        var mirrorOn = JeSwitchOff.Describe(JeFeature.Reviews, on with { Settings = new JeSettings(true, flags, [], null) });
        Assert.Null(mirrorOn.Banner);
        Assert.False(Item(mirrorOn, "MirrorReviewRatingsToJellyfin").Done);
        Assert.False(mirrorOn.Complete, "the mirror must be off after the switch");
        Assert.StartsWith("Still on. While it is on, Jellyfin Enhanced draws its TMDB review section", Item(mirrorOn, "ShowReviews").Detail, StringComparison.Ordinal);

        flags["MirrorReviewRatingsToJellyfin"] = false;
        var done = JeSwitchOff.Describe(JeFeature.Reviews, on with { Settings = new JeSettings(true, flags, [], null) });
        Assert.True(done.Complete, "the TMDB section may stay on");

        // The translation service: a key alone keeps it to do.
        var keyed = JeSwitchOff.Describe(JeFeature.Reviews, on with { Settings = new JeSettings(true, flags, [], null) { Set = new HashSet<string>(StringComparer.Ordinal) { "ReviewTranslationApiKey" } } });
        Assert.False(keyed.Complete);
        Assert.Equal("Still to do: an API key is set. Turn translation off and clear both fields in Jellyfin Enhanced's settings.", Item(keyed, "ReviewTranslationEnabled").Detail);
    }

    // JE's own defaults (ActivityFeedEnabled false, ActivityFeedShowReviewed true) with everything else switched over
    // are complete: JE's activity script returns at once while the feed is off, so it shows no reviews. The feed on
    // with its reviewed list on is still to do; either one off is done.
    [Fact]
    public void Reviews_JesDefaultActivityFeed_IsOff_SoTheChecklistCompletes_AndTheFeedOnWithReviewedOn_IsToDo()
    {
        var off = new JellyfinEnhancedSnapshot(Active, new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal) { ["ShowUserReviews"] = false }, [], null), [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);

        var state = JeSwitchOff.Describe(JeFeature.Reviews, off);

        Assert.True(state.Complete);
        Assert.Null(state.Banner);
        var feed = Item(state, "ActivityFeedShowReviewed");
        Assert.True(feed.Done);
        Assert.False(feed.Optional);
        Assert.Equal("The activity feed is off, so it shows no reviews. Show recently reviewed is still on: turn it off before turning the feed on.", feed.Detail);
        Assert.Contains("ActivityFeedEnabled", feed.Text, StringComparison.Ordinal);

        JeOverlapState With(bool feedOn, bool reviewedOn) => JeSwitchOff.Describe(JeFeature.Reviews, off with
        {
            Settings = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal) { ["ShowUserReviews"] = false, ["ActivityFeedEnabled"] = feedOn, ["ActivityFeedShowReviewed"] = reviewedOn }, [], null)
        });
        var on = With(feedOn: true, reviewedOn: true);
        Assert.False(Item(on, "ActivityFeedShowReviewed").Done);
        Assert.Equal("Still on, and the activity feed is on.", Item(on, "ActivityFeedShowReviewed").Detail);
        Assert.False(on.Complete);
        Assert.True(With(feedOn: true, reviewedOn: false).Complete);
        Assert.Equal("Off.", Item(With(feedOn: true, reviewedOn: false), "ActivityFeedShowReviewed").Detail);
        Assert.Equal("The activity feed is off, and Show recently reviewed is off too.", Item(With(feedOn: false, reviewedOn: false), "ActivityFeedShowReviewed").Detail);
    }

    // JE's settings file there but unreadable (half written, malformed) used to read as JE's defaults, so user reviews
    // showed off and done with no banner. Every item is Unknown now, never done, the checklist isn't complete, and the
    // banner says the settings couldn't be read.
    [Fact]
    public void Reviews_WhileJesSettingsCantBeRead_AreUnknown_NeverDone_NotComplete_WithTheirOwnBanner()
    {
        var broken = new JellyfinEnhancedSnapshot(Active, new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [], "Jellyfin.Plugin.JellyfinEnhanced.xml isn't well-formed XML (the file may be being written; try again)."), [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);

        var state = JeSwitchOff.Describe(JeFeature.Reviews, broken);

        Assert.Equal(11, state.Checklist.Count);
        Assert.All(state.Checklist, i =>
        {
            Assert.True(i.Unknown, i.Key);
            Assert.False(i.Done, i.Key);
            Assert.Equal(JeSwitchOff.SettingsUnreadable, i.Detail);
        });
        Assert.False(state.Complete);
        Assert.False(state.FeatureOn);
        Assert.Equal(JeSwitchOff.ReviewsUnknownBanner, state.Banner);

        // Bookmarks' checklist is as before (the Unknown state is the reviews checklist's).
        Assert.All(JeSwitchOff.Describe(JeFeature.Bookmarks, broken).Checklist, i => Assert.False(i.Unknown));
    }

    [Fact]
    public void NoFeature_IsNone()
    {
        var state = JeSwitchOff.Describe(JeFeature.None, FixtureOn());

        Assert.Same(JeOverlapState.None, state);
        Assert.Empty(state.Checklist);
        Assert.True(state.Complete);
    }

    private static JeSettings Settings(bool bookmarks, bool pluginPages, bool customTabs, bool autoTab, string key, bool disableAll = false) => new(
        true,
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["BookmarksEnabled"] = bookmarks,
            ["BookmarksUsePluginPages"] = pluginPages,
            ["BookmarksUseCustomTabs"] = customTabs,
            ["BookmarksAutoCreateCustomTab"] = autoTab,
            ["DisableAllShortcuts"] = disableAll
        },
        [new("BookmarkCurrentTime", key)],
        null);

    // The item of a key: the shortcut items come last, so tests look items up by key.
    private static JeChecklistItem Item(JeOverlapState state, string key) => state.Checklist.Single(i => i.Key == key);
}
