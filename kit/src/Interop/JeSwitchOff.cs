using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// The overlap banner and the switch-off checklist of a Jellyfin Enhanced feature one of our plugins replaces, computed
/// from a <see cref="JellyfinEnhancedSnapshot"/>: each item is ticked as the reader detects the change, with and without
/// a restart (what stays until one, or isn't durable, is said in its detail), and "Use B for bookmarks" is offered only
/// once <c>B</c> is durably free in JE: its <c>BookmarkCurrentTime</c> is off or another key for the server default in
/// a way its next start keeps, no user's own list sets <c>B</c>, and every user's list could be read. Bookmarks' items
/// are in the order of its switch-over steps (since Bookmarks 1.1): JE's four toggles (then saving JE's settings), the
/// Custom Tab, the restart for the Plugin Pages link, and last the shortcut, "set to Disabled or another key, not
/// blank", for the server default and every user. The items say what an admin does on JE's own settings page: its Pages
/// tab with the four boxes' labels, in the order that page lets them be unticked (Add the Custom Tabs entry for me
/// first, which it hides once Use Custom Tabs for Bookmarks or Enable Bookmarks Feature is off; Enable Bookmarks
/// Feature last, which greys out the others; a hidden or greyed-out box is saved as it is; the same in 12.11 and
/// 12.12), and its Keyboard tab, which JE 12.12 rebuilt (<see cref="HasShortcutGrid"/>): on 12.11, Bookmark Current
/// Time chosen in the list, <c>Disabled</c> typed into Key, Add, Save Settings, never its Disable button; on 12.12, a
/// grid where a key is recorded by clicking it and pressing a combination, so the key at the left end of the Bookmark
/// Current Time row is given another combination (<c>Disabled</c> can't be typed there), never Delete or the block
/// button; both of those save the blank key JE turns back into <c>B</c>. The plugin shows the checklist once its import
/// has run (Bookmarks: an applied JE import, not a dry run); the kit computes it whenever asked.
/// </summary>
internal static class JeSwitchOff
{
    /// <summary>The banner while JE's Bookmarks is on.</summary>
    public const string BookmarksBanner = "Jellyfin Enhanced's Bookmarks is on. Import its bookmarks, then turn it off to avoid two bookmark systems.";

    /// <summary>The banner while JE's User Reviews is on.</summary>
    public const string ReviewsBanner = "Jellyfin Enhanced's User Reviews is on. Import its data, then turn it off to avoid two review panels.";

    /// <summary>
    /// The banner while JE's settings file is there but can't be read (half written while JE saves it, or malformed): whether
    /// its User Reviews is on can't be told.
    /// </summary>
    public const string ReviewsUnknownBanner = "Couldn't read Jellyfin Enhanced's settings, so whether its User Reviews is on can't be told. Try again in a moment; if it stays, open Jellyfin Enhanced's settings page and save them again.";

    /// <summary>The detail of every reviews checklist item while JE's settings can't be read (<see cref="JeChecklistItem.Unknown"/>).</summary>
    public const string SettingsUnreadable = "Couldn't read Jellyfin Enhanced's settings, so this can't be checked.";

    /// <summary>
    /// The note next to the admin shortcut item, and under the withheld "Use B", while JE's key is only cleared: JE 12.8 to
    /// 12.12 drop an entry without a key at every start and put the default back (<see cref="JeShortcuts"/>). This is
    /// 12.11's (its Keyboard tab can take the text <c>Disabled</c>); <see cref="ClearedKeyGridNote"/> is 12.12's.
    /// </summary>
    public const string ClearedKeyNote = "Jellyfin Enhanced restores B at its next start (12.8 to 12.12 drop a shortcut without a key and put its default back); set it to Disabled or another key instead.";

    /// <summary>The note while JE's key is only cleared, for JE 12.12's Keyboard tab, which records keys and takes no text.</summary>
    public const string ClearedKeyGridNote = "Jellyfin Enhanced restores B at its next start (12.8 to 12.12 drop a shortcut without a key and put its default back); give it another key instead.";

    /// <summary>
    /// The note while <c>B</c> is free only because JE's Disable Keyboard Shortcuts is on (its key listener isn't registered
    /// at all then, <c>events.js</c>): unticking the switch brings every key back, the bookmark one included. 12.11's;
    /// <see cref="DisabledAllGridNote"/> is 12.12's.
    /// </summary>
    public const string DisabledAllNote = "B is free only while Disable Keyboard Shortcuts is on in Jellyfin Enhanced's settings; it comes back as soon as that is unticked. To keep it off for good, set Bookmark Current Time to Disabled or another key as well.";

    /// <summary>The note while <c>B</c> is free only because of Disable Keyboard Shortcuts, for JE 12.12's Keyboard tab.</summary>
    public const string DisabledAllGridNote = "B is free only while Disable Keyboard Shortcuts is on in Jellyfin Enhanced's settings; it comes back as soon as that is unticked. To keep it off for good, give Bookmark Current Time another key as well.";

    /// <summary>The note while a user's own JE shortcuts couldn't be read, so nobody can tell whether B is free for everyone.</summary>
    public const string UsersUnreadableNote = "A user's own Jellyfin Enhanced shortcuts couldn't be read (see the checklist), so the server can't tell whether B is free for everyone.";

    /// <summary>What the withheld "Use B" says first.</summary>
    public const string UseBWithheld = "Use B for bookmarks isn't offered yet: ";

    /// <summary>
    /// The admin shortcut item, the way JE 12.11's settings page does it (the steps in Bookmarks' DEVELOPMENT.md,
    /// "Jellyfin Enhanced: moving its bookmarks over"): Bookmark Current Time chosen in the Add Override list (it has
    /// no row of its own while its key is the default <c>B</c>), the word typed into Key, Add, Save Settings; or the
    /// word typed over the key in its own row under Current Overrides. JE's Disable button saves a blank key, as does a
    /// row left empty, and JE (12.8 to 12.12) turns that back into <c>B</c> at the next start.
    /// <see cref="ShortcutAdminGridText"/> is 12.12's.
    /// </summary>
    public const string ShortcutAdminText = "Last, the server default: on Jellyfin Enhanced's settings page, Keyboard tab, choose Bookmark Current Time in the list of shortcuts, type Disabled into the Key field and click Add (or type Disabled over the key in its own row under Current Overrides), then Save Settings; never its Disable button, never a blank key";

    /// <summary>
    /// The admin shortcut item on JE 12.12's Keyboard tab, a grid of every shortcut's key: a click on the key (<c>B</c>) at
    /// the left end of the Bookmark Current Time row ("Press a key..."), another combination pressed, Save Settings. Each row
    /// has its key and ⊘ button at its left end and its label at its right end, with the rows close together, so the item
    /// names the key's place: a click on a neighbouring row's key (Reset Playback Speed's <c>R</c> above, Open Episode
    /// Preview's <c>P</c> below) would silently move that shortcut instead (Bookmarks 1.1.1, after a review of the JE 12.12
    /// re-check). The grid takes no typed text, so <c>Disabled</c> can't be set there; Delete on a key, and the ⊘ (block)
    /// button beside it, save a blank key, which JE turns back into <c>B</c> at its next start; Backspace on the clicked key
    /// puts its default (<c>B</c>) back. Any combination but <c>B</c> frees <c>B</c> for good: JE compares whole
    /// combinations.
    /// </summary>
    public const string ShortcutAdminGridText = "Last, the server default: on Jellyfin Enhanced's settings page, Keyboard tab, click the key (B) at the left end of the Bookmark Current Time row, press another key combination, such as Shift+X, then Save Settings; never Delete or the ⊘ button beside the key (Disable for every user), never a blank key (Backspace puts B back)";

    /// <summary>What the admin item's detail adds while <c>B</c> is still JE's (12.11's Keyboard tab).</summary>
    public const string ShortcutAdminStillB = "Turning Bookmarks off doesn't free the key: Jellyfin Enhanced's player handler still takes it. Type Disabled as its key as above; its Disable button saves a blank key, which Jellyfin Enhanced turns back into B at its next start.";

    /// <summary>What the admin item's detail adds while <c>B</c> is still JE's (12.12's Keyboard tab).</summary>
    public const string ShortcutAdminGridStillB = "Turning Bookmarks off doesn't free the key: Jellyfin Enhanced's player handler still takes it. Give it another key as above; Delete and the ⊘ button save a blank key, which Jellyfin Enhanced turns back into B at its next start.";

    /// <summary>
    /// What the admin item's detail adds while JE's key is only cleared: JE 12.11's row for it shows a grey "Disabled"
    /// placeholder over the empty field, which looks done and isn't.
    /// </summary>
    public const string ClearedKeyHow = "Its row under Current Overrides on Jellyfin Enhanced's Keyboard tab shows a grey \"Disabled\" placeholder over an empty key: type the word Disabled into that field, then Save Settings.";

    /// <summary>
    /// What the admin item's detail adds while JE's key is only cleared, on JE 12.12's Keyboard tab: the key reads
    /// "Disabled" at the left end of the dimmed Bookmark Current Time row, which looks done and isn't (where the key is, as
    /// in <see cref="ShortcutAdminGridText"/>).
    /// </summary>
    public const string ClearedKeyGridHow = "Jellyfin Enhanced's Keyboard tab shows its key as \"Disabled\" at the left end of the dimmed Bookmark Current Time row, which looks done and isn't: click that key, press another key combination, such as Shift+X, then Save Settings.";

    // JE's settings page (12.11 and 12.12 alike): the Pages tab's Bookmarks section and its four boxes' labels.
    private const string PagesTab = "Jellyfin Enhanced's settings page, Pages tab, Bookmarks: ";
    private const string EnableLabel = "Enable Bookmarks Feature";
    private const string CustomTabsLabel = "Use Custom Tabs for Bookmarks";

    private const string BCombo = "B";

    /// <summary>The first Jellyfin Enhanced version whose Keyboard tab is the grid of keys (12.12; <see cref="HasShortcutGrid"/>).</summary>
    public static readonly Version ShortcutGridSince = new(12, 12);

    /// <summary>
    /// Describes the overlap of a feature and its checklist.
    /// </summary>
    /// <param name="feature">The JE feature the plugin replaces.</param>
    /// <param name="snapshot">What the reader found.</param>
    /// <param name="userName">Names a user for the detail lines; <c>null</c> when it has no name (its id is shown).</param>
    /// <returns>The state; <see cref="JeOverlapState.None"/> for <see cref="JeFeature.None"/>.</returns>
    public static JeOverlapState Describe(JeFeature feature, JellyfinEnhancedSnapshot snapshot, Func<Guid, string?>? userName = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return feature switch
        {
            JeFeature.Bookmarks => Bookmarks(snapshot, userName ?? (static _ => null)),
            JeFeature.Reviews => Reviews(snapshot),
            _ => JeOverlapState.None
        };
    }

    /// <summary>
    /// Gets a value indicating whether a configured JE key makes its action act on plain <c>B</c>: JE's listener compares
    /// the whole combination it builds with the configured key (<c>events.js</c>, <c>switch (combo)</c>), so only <c>B</c>
    /// takes <c>B</c>, and in JE 12.12, which reads a single letter in either case, <c>b</c> too (counted for 12.11 as well,
    /// so a check holds across the update); <c>""</c>, <c>Disabled</c>, <c>Shift+B</c> or <c>X</c> all leave <c>B</c> free.
    /// </summary>
    /// <param name="key">The key as JE's settings or a user's file hold it.</param>
    /// <returns>Whether a press of <c>B</c> triggers the action.</returns>
    public static bool TakesB(string? key) => ShortcutCombo.JellyfinEnhancedCombos(key).Contains(BCombo, StringComparer.Ordinal);

    /// <summary>
    /// Gets a value indicating whether a Jellyfin Enhanced version's settings page has 12.12's Keyboard tab, a grid of
    /// every shortcut's key, each changed by a click and a key press, instead of 12.11's list with a Key field, Add and
    /// Disable: 12.12 and later, and a version that isn't known (the newest page is the one a new install brings).
    /// </summary>
    /// <param name="version">JE's version, as the plugin manager reports the loaded one (<see cref="JePluginState.Version"/>).</param>
    /// <returns>Whether the checklist words the key step for the grid.</returns>
    public static bool HasShortcutGrid(string? version)
        => !System.Version.TryParse(version, out var parsed) || parsed >= ShortcutGridSince;

    private static JeOverlapState Bookmarks(JellyfinEnhancedSnapshot snapshot, Func<Guid, string?> userName)
    {
        var installed = snapshot.Plugin.Installed;
        var settings = snapshot.Settings;
        var items = new List<JeChecklistItem>();

        // The words of the key step follow JE's Keyboard tab: 12.11's list with a Key field, or 12.12's grid of keys, which
        // takes no typed text (the version the server has loaded; a pending update's page comes with the restart).
        var grid = HasShortcutGrid(snapshot.Plugin.Version);
        var adminText = grid ? ShortcutAdminGridText : ShortcutAdminText;
        var stillB = grid ? ShortcutAdminGridStillB : ShortcutAdminStillB;
        var clearedNote = grid ? ClearedKeyGridNote : ClearedKeyNote;
        var clearedHow = grid ? ClearedKeyGridHow : ClearedKeyHow;
        var disabledAllNote = grid ? DisabledAllGridNote : DisabledAllNote;

        // Bookmarks' switch-over order: JE's four toggles, then saving JE's settings; the Custom Tab; the restart (the
        // Plugin Pages link); and last the shortcut, for the server default and every user's own list, so a restart can't
        // undo it unnoticed (a cleared key comes back as B at JE's next start).
        // JE's four toggles: read from its XML, which JE writes at every save (and at every start). In the order JE 12.11's
        // Pages tab lets them be unticked: it hides "Add the Custom Tabs entry for me" while
        // Use Custom Tabs or Enable Bookmarks Feature is off, greys out Use Plugin Pages and Use Custom Tabs while Enable
        // Bookmarks Feature is off, and saves a hidden or greyed-out box as it is. A box already stuck that way says how to
        // free it.
        var featureOn = installed && settings.Flag("BookmarksEnabled");
        var greyedOut = installed && !settings.Flag("BookmarksEnabled");
        var autoTabHidden = greyedOut || (installed && !settings.Flag("BookmarksUseCustomTabs"));
        items.Add(Flag(
            "BookmarksAutoCreateCustomTab",
            PagesTab + "Add the Custom Tabs entry for me off, first",
            installed,
            settings,
            autoTabHidden
                ? $"Jellyfin Enhanced hides this box while {CustomTabsLabel} or {EnableLabel} is off, and saves it as it is: tick those two again, then untick the boxes in this checklist's order and Save Settings."
                : $"Untick it first: Jellyfin Enhanced hides it as soon as {CustomTabsLabel} or {EnableLabel} is off, and then saves it as it is."));
        items.Add(Flag("BookmarksUseCustomTabs", PagesTab + CustomTabsLabel + " off", installed, settings, greyedOut ? GreyedOut(CustomTabsLabel) : null));
        items.Add(Flag(
            "BookmarksUsePluginPages",
            PagesTab + "Use Plugin Pages for Bookmarks Library off",
            installed,
            settings,
            "Its Plugin Pages link stays until the restart below." + (greyedOut ? " " + GreyedOut("Use Plugin Pages for Bookmarks Library") : string.Empty)));
        items.Add(Flag(
            "BookmarksEnabled",
            PagesTab + EnableLabel + " off, after the three above (it greys them out), then Save Settings",
            installed,
            settings,
            "Its OSD bookmark button and its bookmark dialog go at the next page load; its B key stays until the shortcut " + (grid ? "has another key" : "is set to Disabled") + " (the last step). Saving Jellyfin Enhanced's settings with Bookmarks off removes the Custom Tab it created itself; a tab it doesn't own is deleted by hand (below)."));

        // The Custom Tab JE renders into: its own, removed by JE's settings page, or any other with the same content. A tab
        // list that couldn't be read holds no tab, which says nothing: unticked until it can be read.
        var tabs = snapshot.CustomTabs;
        var jeTabs = tabs.Tabs.Where(t => string.Equals(t.ContentHtml.Trim(), JellyfinEnhancedFiles.BookmarksTabHtml, StringComparison.Ordinal)).ToList();
        items.Add(new JeChecklistItem(
            "CustomTab",
            "Custom Tabs settings: the \"Bookmarks\" tab Jellyfin Enhanced renders into deleted, if it is still there",
            tabs.Problem is null && jeTabs.Count == 0,
            tabs.Problem is not null ? "Custom Tabs' settings couldn't be read, so this can't be checked: " + tabs.Problem
                : !tabs.Installed ? "Custom Tabs isn't installed."
                : jeTabs.Count == 0 ? "No tab holds <div class=\"sections bookmarks\"></div>."
                : "Still there: " + string.Join(", ", jeTabs.Select(t => $"\"{t.Title}\"")) + ". Delete it in Custom Tabs' settings (the tab's content is <div class=\"sections bookmarks\"></div>).",
            false));

        // Plugin Pages' list in memory: Plugin Pages 2.4 reads its config.json only when it starts, and JE adds or removes its
        // entry there in its constructor (12.12 also whenever its settings are saved, and with Plugin Pages 3.0 at once), so
        // the link stays until a restart, and on 12.11 until a second one when JE's constructor ran after Plugin Pages'.
        // A list that couldn't be read says nothing: unticked.
        var pages = snapshot.PluginPages;
        var jePages = pages.Pages.Where(p => string.Equals(p.Id, JellyfinEnhancedFiles.BookmarksPageId, StringComparison.Ordinal)).ToList();
        var linkGone = pages.Problem is null && jePages.Count == 0;
        items.Add(new JeChecklistItem(
            "PluginPagesLink",
            "Restart Jellyfin: Jellyfin Enhanced's \"Bookmarks\" link gone from Plugin Pages (restart once more if it is still there)",
            linkGone,
            pages.Problem is not null ? "Plugin Pages' list couldn't be read, so this can't be checked: " + pages.Problem
                : !pages.Installed ? "Plugin Pages isn't installed."
                : linkGone ? "Plugin Pages doesn't list it."
                : "Still listed as \"" + (jePages[0].DisplayText ?? "Bookmarks") + "\". Plugin Pages reads its list only when the server starts, and Jellyfin Enhanced takes its link out of that list when the server starts (12.12 also when its settings are saved): restart Jellyfin, and once more if the link is still there.",
            pages.Problem is null && !linkGone));

        // Last, the shortcut: what frees B is the admin's effective key being anything but B (off, or another key: JE
        // compares the whole combination), and durably so only when the key JE's next start restores is anything but B as
        // well (a cleared key comes back as B). Disable Keyboard Shortcuts frees every key, but only while it stays on.
        var disabledAll = installed && settings.Flag("DisableAllShortcuts");
        var adminKey = JeShortcuts.EffectiveKeys(settings).GetValueOrDefault(JellyfinEnhancedFiles.BookmarkShortcut, string.Empty);
        var adminKeyLater = JeShortcuts.KeysAfterRestart(settings).GetValueOrDefault(JellyfinEnhancedFiles.BookmarkShortcut, string.Empty);
        var keyFreesB = !TakesB(adminKey);
        var keyDurablyFreesB = keyFreesB && !TakesB(adminKeyLater);
        var adminFree = !installed || disabledAll || keyFreesB;
        var adminFragile = installed && adminFree && !keyDurablyFreesB;
        items.Add(new JeChecklistItem(
            "ShortcutAdmin",
            adminText,
            adminFree,
            !installed ? "Jellyfin Enhanced isn't installed."
                : !adminFree ? $"Still \"{adminKey}\". " + stillB
                : keyDurablyFreesB ? AdminKeyDetail(adminKey, disabledAll)
                : disabledAll ? $"Off only while Disable Keyboard Shortcuts is on (its own key is \"{adminKey}\"{(keyFreesB ? ", cleared, which the next start restores to B" : string.Empty)}). " + disabledAllNote
                : "Cleared, but only until the next restart: " + clearedNote + " " + clearedHow,
            adminFragile));

        // Every user's own list: a user whose override is B keeps it until they clear or move it in JE's own settings
        // panel (a user's "" is never normalised, so it lasts). A file that can't be read, or a folder listing that
        // failed, says nothing, so it holds "Use B" back like a user who set B (failing soft must not fail open).
        var holders = new List<string>();
        var unreadable = false;
        foreach (var user in snapshot.Users)
        {
            if (user.Problem is not null)
            {
                holders.Add($"{Name(user.UserId, userName)} (file unreadable: {user.Problem})");
                unreadable = true;
                continue;
            }

            if (JeShortcuts.UserOverrides(user).TryGetValue(JellyfinEnhancedFiles.BookmarkShortcut, out var key) && TakesB(key))
            {
                holders.Add($"{Name(user.UserId, userName)} (\"{key}\")");
            }
        }

        var usersProblem = snapshot.UsersProblem;
        var usersKnownFree = holders.Count == 0 && usersProblem is null;
        var usersFree = !installed || disabledAll || usersKnownFree;
        var usersFragile = installed && usersFree && !usersKnownFree;
        var stillSetBy = holders.Count == 0 ? null : string.Join(", ", holders) + ". Each sets it to Disabled or another key in Jellyfin Enhanced's settings panel (Shortcuts), on their own account.";
        const string notListed = "users' own Jellyfin Enhanced shortcuts couldn't be listed, so this can't be checked: ";
        items.Add(new JeChecklistItem(
            "ShortcutUsers",
            "Bookmark Current Time set to Disabled or another key in every user's own Jellyfin Enhanced shortcuts (a blank key lasts there too)",
            usersFree,
            !installed ? "Jellyfin Enhanced isn't installed."
                : disabledAll ? "Off while Disable Keyboard Shortcuts is on in Jellyfin Enhanced's settings, whatever the users' own lists say."
                    + (usersProblem is not null ? " Once it is unticked, the " + notListed + usersProblem : string.Empty)
                    + (stillSetBy is not null ? " Once it is unticked, still set by: " + stillSetBy : string.Empty)
                : usersProblem is not null ? "The " + notListed + usersProblem
                : stillSetBy is not null ? "Still set by: " + stillSetBy
                : snapshot.Users.Count == 0 ? "No user has personal Jellyfin Enhanced shortcuts."
                : "No user's own list sets it.",
            usersFragile));

        var complete = items.All(i => i.Done);
        var canUseB = adminFree && !adminFragile && usersFree && !usersFragile;
        // Why the button is withheld when the items don't say it themselves: both ticked but not durably (a cleared key,
        // or only Disable Keyboard Shortcuts), or the users' item unticked only because a list couldn't be read.
        string? useBNote = null;
        if (!canUseB && adminFree && usersFree)
        {
            useBNote = UseBWithheld + (disabledAll ? disabledAllNote : clearedNote);
        }
        else if (!canUseB && adminFree && (usersProblem is not null || unreadable))
        {
            useBNote = UseBWithheld + (usersProblem ?? UsersUnreadableNote);
        }

        return new JeOverlapState(
            JeFeature.Bookmarks,
            installed,
            snapshot.Plugin.Active,
            featureOn,
            featureOn ? BookmarksBanner : null,
            items,
            complete,
            canUseB,
            useBNote);
    }

    private static string AdminKeyDetail(string adminKey, bool disabledAll)
    {
        var detail = JeShortcuts.IsOff(adminKey)
            ? $"Off: its key is \"{adminKey}\", which no key press produces."
            : $"Moved: its key is \"{adminKey}\", so B does nothing in Jellyfin Enhanced.";
        return disabledAll ? detail + " Disable Keyboard Shortcuts is on as well." : detail;
    }

    // Every review setting of JE 12.14, in the order of User Ratings' switch-over steps, each with its value as JE's
    // XML has it. Required: user reviews, the translation service and the mirror off, and the activity feed's reviewed
    // list off (done too while the whole feed is off: JE's activity script returns at once without
    // ActivityFeedEnabled). Optional: the TMDB section (it may stay, but traps the TV D-pad while on) and the poster
    // rating. For information only, always ticked: AniList reviews, expanded by default, the two hiding rules (read for
    // User Ratings' "Use the same hiding rules") and JE's spoiler stripping. The plugin never changes any of them: the
    // admin does, in JE. While JE's settings file is there but can't be read, every item is Unknown, never done, and the
    // checklist isn't complete (a half-written file read as JE's defaults once showed user reviews off).
    private static JeOverlapState Reviews(JellyfinEnhancedSnapshot snapshot)
    {
        var installed = snapshot.Plugin.Installed;
        var settings = snapshot.Settings;
        var unknown = installed && settings.Problem is not null;
        var featureOn = installed && !unknown && settings.Flag("ShowUserReviews");
        var translationOn = installed && (settings.Flag("ReviewTranslationEnabled") || settings.IsSet("ReviewTranslationUrl") || settings.IsSet("ReviewTranslationApiKey"));
        var feedOn = settings.Flag("ActivityFeedEnabled");
        var reviewedOn = settings.Flag("ActivityFeedShowReviewed");
        var items = new List<JeChecklistItem>
        {
            Flag(
                "ShowUserReviews",
                "Jellyfin Enhanced settings, Reviews: Enable User Written Reviews (ShowUserReviews) off: this ends the two review systems",
                installed,
                settings,
                "Its review panel and average chip go at the next page load; no restart is needed."),
            Flag(
                "ShowReviews",
                "Jellyfin Enhanced settings, Reviews: the TMDB reviews section (ShowReviews) may stay on (optional), but while it is on it traps the TV remote's D-pad in the details page's buttons row",
                installed,
                settings,
                "While it is on, Jellyfin Enhanced draws its TMDB review section on the details page, which traps the TV remote's D-pad in the buttons row; turning it off ends the trap.") with { Optional = true },
            Info(
                "ShowAnilistReviews",
                "Jellyfin Enhanced settings, Reviews: AniList reviews (ShowAnilistReviews, 12.14): reviews Jellyfin Enhanced fetches from AniList, nothing to import; on or off as you like",
                installed,
                settings),
            new JeChecklistItem(
                "ReviewTranslationEnabled",
                "Jellyfin Enhanced settings, Reviews: review translation (ReviewTranslationEnabled, 12.14) off, and its service URL (ReviewTranslationUrl) and API key (ReviewTranslationApiKey) cleared: Jellyfin Enhanced sends review text to that outside service",
                !translationOn,
                !installed ? "Jellyfin Enhanced isn't installed." : TranslationDetail(settings),
                false),
            Info(
                "ReviewsExpandedByDefault",
                "Jellyfin Enhanced settings, Reviews: reviews expanded by default (ReviewsExpandedByDefault): Jellyfin Enhanced's own section only, no effect on User Ratings'",
                installed,
                settings),
            Flag(
                "MirrorReviewRatingsToJellyfin",
                "Jellyfin Enhanced settings, Reviews: Mirror review ratings to Jellyfin (MirrorReviewRatingsToJellyfin, 12.11 and later) off, so it writes no more stars into Jellyfin's own user ratings",
                installed,
                settings,
                "While it is on, Jellyfin Enhanced writes each review's stars into Jellyfin's own user rating of the item; off by default. The values it wrote stay in Jellyfin's user data, and User Ratings' import never counts them as a second rating of a review."),
            Info(
                "HideReviewsFromHiddenUsers",
                "Jellyfin Enhanced settings, Reviews: hide reviews from hidden users (HideReviewsFromHiddenUsers): read for User Ratings' \"Use the same hiding rules\"",
                installed,
                settings),
            Info(
                "HideReviewsFromDisabledUsers",
                "Jellyfin Enhanced settings, Reviews: hide reviews from disabled users (HideReviewsFromDisabledUsers): read for User Ratings' \"Use the same hiding rules\"",
                installed,
                settings),
            new JeChecklistItem(
                "ActivityFeedShowReviewed",
                "Jellyfin Enhanced settings, Activity feed: Show recently reviewed (ActivityFeedShowReviewed) off, or the whole activity feed (ActivityFeedEnabled) off: its feed would show only Jellyfin Enhanced's old reviews",
                !installed || !feedOn || !reviewedOn,
                !installed ? "Jellyfin Enhanced isn't installed."
                    : !feedOn ? (reviewedOn ? "The activity feed is off, so it shows no reviews. Show recently reviewed is still on: turn it off before turning the feed on." : "The activity feed is off, and Show recently reviewed is off too.")
                    : reviewedOn ? "Still on, and the activity feed is on." : "Off.",
                false),
            Info(
                "SpoilerStripReviews",
                "Jellyfin Enhanced settings: strip spoilers from reviews (SpoilerStripReviews): Jellyfin Enhanced's own section only; User Ratings' spoiler flag is each review's own",
                installed,
                settings),
            Flag("ShowUserRatingOnPosters", "Jellyfin Enhanced settings, Reviews: Show user rating on posters (ShowUserRatingOnPosters) off (optional)", installed, settings, null) with { Optional = true }
        };

        if (unknown)
        {
            items = items.Select(i => i with { Done = false, Unknown = true, Detail = SettingsUnreadable }).ToList();
        }

        return new JeOverlapState(
            JeFeature.Reviews,
            installed,
            snapshot.Plugin.Active,
            featureOn,
            unknown ? ReviewsUnknownBanner : featureOn ? ReviewsBanner : null,
            items,
            !unknown && items.All(i => i.Done || i.Optional),
            false,
            null);
    }

    // A setting for information only: always ticked, optional, its value in the detail.
    private static JeChecklistItem Info(string name, string text, bool installed, JeSettings settings)
        => new(name, text, true, !installed ? "Jellyfin Enhanced isn't installed." : settings.Flag(name) ? "On." : "Off.", false) { Optional = true };

    // The translation item's detail: which of the three are on or set, never the URL or the key themselves.
    private static string TranslationDetail(JeSettings settings)
    {
        var parts = new List<string>();
        if (settings.Flag("ReviewTranslationEnabled"))
        {
            parts.Add("translation is on");
        }

        if (settings.IsSet("ReviewTranslationUrl"))
        {
            parts.Add("a service URL is set");
        }

        if (settings.IsSet("ReviewTranslationApiKey"))
        {
            parts.Add("an API key is set");
        }

        return parts.Count == 0
            ? "Off, and no service URL or API key is set."
            : "Still to do: " + string.Join(", ", parts) + ". Turn translation off and clear both fields in Jellyfin Enhanced's settings.";
    }

    private static JeChecklistItem Flag(string name, string text, bool installed, JeSettings settings, string? whileOn)
    {
        var on = installed && settings.Flag(name);
        return new JeChecklistItem(
            name,
            text,
            !on,
            !installed ? "Jellyfin Enhanced isn't installed." : on ? ("Still on." + (whileOn is null ? string.Empty : " " + whileOn)) : "Off.",
            false);
    }

    // A box JE 12.11's Pages tab greys out while Enable Bookmarks Feature is off, and saves as it is.
    private static string GreyedOut(string label)
        => $"Jellyfin Enhanced greys {label} out while {EnableLabel} is off, and saves it as it is: tick {EnableLabel} again, then untick the boxes in this checklist's order and Save Settings.";

    private static string Name(Guid userId, Func<Guid, string?> userName)
    {
        var name = userName(userId);
        return string.IsNullOrWhiteSpace(name) ? userId.ToString("N", CultureInfo.InvariantCulture) : name;
    }
}
