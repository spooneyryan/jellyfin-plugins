using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// Access Roles' global filter over the whole action manifest: every action
/// classified; an administrator and an API key never refused, whatever their locks; each settings section's actions
/// refused or merged for a locked non-admin and passed otherwise; the merges' pass-through on an invalid or missing body;
/// the usersettings document's id and client variants; Jellyfin Enhanced's saves; fail closed when a rule can't decide and
/// when the store can't be trusted; the counters; and the library sync's and user policy hooks, unchanged inside it.
/// </summary>
public sealed class AccessRulesActionFilterTests : IDisposable
{
    private const string Users = "Jellyfin.Api.Controllers.UserController";
    private const string DisplayPreferencesController = "Jellyfin.Api.Controllers.DisplayPreferencesController";
    private const string Je = ActionRuleTable.JellyfinEnhancedController;

    private readonly FilterHarness _h = new();
    private readonly User _kid;
    private readonly User _open;
    private readonly User _admin;

    public AccessRulesActionFilterTests()
    {
        _kid = _h.AddUser("kid");
        _open = _h.AddUser("open");
        _admin = _h.AddUser("admin");
    }

    public static TheoryData<AccessRolesLock> ServerEnforcedSections() => [.. SettingsSections.ServerEnforced];

    [Fact]
    public void TheFilter_IsAtTheGlobalOrder()
    {
        Assert.Equal(-10000, _h.Filter.Order);
        Assert.Equal(AccessRulesActionFilter.FilterOrder, ((Microsoft.AspNetCore.Mvc.Filters.IOrderedFilter)_h.Filter).Order);
    }

    [Fact]
    public async Task AnAdministratorAndAnApiKey_AreNeverRefused_OnAnyActionOfTheManifest_WithEveryLock()
    {
        await _h.LockAsync(FilterHarness.EveryLock, _admin.Id);
        foreach (var action in _h.Manifest.Actions)
        {
            var asAdmin = await _h.RunAsync(action.Controller, action.Action, FilterHarness.Administrator(_admin.Id));
            Assert.True(asAdmin.Ran && asAdmin.Context.Result is null, action.Key + " was refused for an administrator.");
            var asKey = await _h.RunAsync(action.Controller, action.Action, FilterHarness.ApiKey());
            Assert.True(asKey.Ran && asKey.Context.Result is null, action.Key + " was refused for an API key.");
        }

        // With bodies too: the merges leave an administrator's post as it came.
        var posted = FilterHarness.Posted();
        var run = await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.Administrator(_admin.Id), new() { ["userConfig"] = posted });
        Assert.True(run.Ran);
        Assert.Equal("posted-receiver", posted.CastReceiverId);
        Assert.Equal(0L, (long)((IDictionary<string, long>)_h.Counter.Describe()["Totals"]!).Values.Sum());
    }

    [Fact]
    public async Task ANonAdminWithEveryLock_PassesEveryActionWithoutARule_AndMeetsTheHardeningRulesAsReviewed()
    {
        // An action with no rule is one lookup and goes on; 1.1's Strict rules are classified, not applied.
        // The admin-surface and server-wide rules, sent without arguments: the AdminSurfaces rejects, the configuration reads (AllowList, no key) and
        // the restart are refused; the others have nothing to hide and go on. No action is refused for being outside a rule.
        await _h.LockAsync(FilterHarness.EveryLock, _kid.Id);
        var passed = 0;
        var refused = new List<string>();
        foreach (var action in _h.Manifest.Actions.Where(a => !a.Rules.Any(r => ActionRuleTable.Owner(r) == RuleOwner.Settings)))
        {
            var run = await _h.RunAsync(action.Controller, action.Action, FilterHarness.NonAdmin(_kid.Id));
            if (run.Ran && run.Context.Result is null)
            {
                passed++;
            }
            else
            {
                Assert.True(run.Locked, action.Key + " was answered without a refusal.");
                refused.Add(action.Key["Jellyfin.Api.Controllers.".Length..]);
            }
        }

        var expected = _h.Manifest.Actions
            .Where(a => a.Rules.Any(r => (r.Kind == RuleKind.LockedReject && r.Lock == AccessRolesLock.AdminSurfaces) || r.Kind is RuleKind.ConfigurationReadGuard or RuleKind.RestartBlock))
            .Select(a => a.Key["Jellyfin.Api.Controllers.".Length..]);
        Assert.Equal(expected.Order(StringComparer.Ordinal), refused.Order(StringComparer.Ordinal));
        // Jellyfin 12.4 has five actions fewer than 10.11.5 (385, not 390).
        Assert.Equal(((JellyfinLine.Is12 ? 385 : 390) - 14 - 29, 29), (passed, refused.Count));

        // A plugin's action outside the manifest, and an action the manifest doesn't know: on.
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "Save", "Jellyfin.Plugin.Other", "POST", "Other/Save"), FilterHarness.NonAdmin(_kid.Id))).Ran);
        Assert.True((await _h.RunAsync(Users, "SomeNewAction", FilterHarness.NonAdmin(_kid.Id))).Ran);
    }

    [Theory]
    [MemberData(nameof(ServerEnforcedSections))]
    public async Task EachSection_ItsRejectsRefuseALockedNonAdmin_AndPassAnUnlockedOne_AndAnAdministrator(AccessRolesLock section)
    {
        await _h.LockAsync([section], _kid.Id, _admin.Id);
        var rejects = _h.Manifest.Actions.Where(a => a.Rules.Any(r => r.Kind == RuleKind.LockedReject && r.Lock == section)).ToList();
        foreach (var action in rejects)
        {
            var locked = await _h.RunAsync(action.Controller, action.Action, FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id });
            Assert.True(locked.Locked, action.Key + " passed a locked non-admin.");
            Assert.Equal(AccessRulesActionFilter.LockedMessage(section), locked.Problem!.Detail);
            Assert.Contains(AccessRulesActionFilter.Title(section), locked.Problem.Detail, StringComparison.Ordinal);
            Assert.True((await _h.RunAsync(action.Controller, action.Action, FilterHarness.NonAdmin(_open.Id))).Ran, action.Key + " refused an unlocked non-admin.");
            Assert.True((await _h.RunAsync(action.Controller, action.Action, FilterHarness.Administrator(_admin.Id))).Ran, action.Key + " refused an administrator.");
        }

        // Every other settings reject passes the user locked in this section only.
        foreach (var action in _h.Manifest.Actions.Where(a => a.Rules.Any(r => r.Kind == RuleKind.LockedReject && r.Lock != section && ActionRuleTable.Owner(r) == RuleOwner.Settings)))
        {
            Assert.True((await _h.RunAsync(action.Controller, action.Action, FilterHarness.NonAdmin(_kid.Id))).Ran, action.Key + " was refused under another section's lock.");
        }

        var expected = section switch
        {
            AccessRolesLock.Profile => 8,
            AccessRolesLock.QuickConnect => 1,
            _ => 0
        };
        Assert.Equal(expected, rejects.Count);
    }

    [Theory]
    [MemberData(nameof(ServerEnforcedSections))]
    public async Task EachSection_TheConfigurationMerge_KeepsItsFieldsForALockedNonAdmin_OnBothRoutes_AndNothingElse(AccessRolesLock section)
    {
        await _h.LockAsync([section], _kid.Id, _admin.Id);
        var fields = SettingsSections.ConfigurationFields.GetValueOrDefault(section) ?? [];
        foreach (var action in new[] { "UpdateUserConfiguration", "UpdateUserConfigurationLegacy" })
        {
            var posted = FilterHarness.Posted();
            var run = await _h.RunAsync(Users, action, FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id, ["userConfig"] = posted });
            Assert.True(run.Ran);
            foreach (var field in UserConfigurationMerger.KnownFields)
            {
                var value = Value(posted, field);
                Assert.Equal(fields.Contains(field) ? Value(FilterHarness.Stored(), field) : Value(FilterHarness.Posted(), field), value);
            }

            // Unlocked and administrator: the post as it came.
            var open = FilterHarness.Posted();
            Assert.True((await _h.RunAsync(Users, action, FilterHarness.NonAdmin(_open.Id), new() { ["userId"] = _open.Id, ["userConfig"] = open })).Ran);
            var admin = FilterHarness.Posted();
            Assert.True((await _h.RunAsync(Users, action, FilterHarness.Administrator(_admin.Id), new() { ["userId"] = _admin.Id, ["userConfig"] = admin })).Ran);
            Assert.All(UserConfigurationMerger.KnownFields, f => Assert.Equal(Value(FilterHarness.Posted(), f), Value(open, f)));
            Assert.All(UserConfigurationMerger.KnownFields, f => Assert.Equal(Value(FilterHarness.Posted(), f), Value(admin, f)));
        }

        // UpdateUser's configuration, both routes, the same; the name unchanged.
        foreach (var action in new[] { "UpdateUser", "UpdateUserLegacy" })
        {
            var dto = new UserDto { Name = _kid.Username, Configuration = FilterHarness.Posted() };
            Assert.True((await _h.RunAsync(Users, action, FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id, ["updateUser"] = dto })).Ran);
            Assert.All(UserConfigurationMerger.KnownFields, f => Assert.Equal(fields.Contains(f) ? Value(FilterHarness.Stored(), f) : Value(FilterHarness.Posted(), f), Value(dto.Configuration, f)));
        }
    }

    [Theory]
    [MemberData(nameof(ServerEnforcedSections))]
    public async Task EachSection_TheUserSettingsMerge_KeepsItsKeysForALockedNonAdmin_AndNothingElse(AccessRolesLock section)
    {
        await _h.LockAsync([section], _kid.Id, _admin.Id);
        foreach (var user in new[] { _kid, _open, _admin })
        {
            _h.StoreUserSettings(user.Id);
        }

        var kid = PostedUserSettings();
        Assert.True((await SaveUserSettingsAsync(FilterHarness.NonAdmin(_kid.Id), _kid.Id, "usersettings", "emby", kid)).Ran);
        var open = PostedUserSettings();
        Assert.True((await SaveUserSettingsAsync(FilterHarness.NonAdmin(_open.Id), _open.Id, "usersettings", "emby", open)).Ran);
        var admin = PostedUserSettings();
        Assert.True((await SaveUserSettingsAsync(FilterHarness.Administrator(_admin.Id), _admin.Id, "usersettings", "emby", admin)).Ran);

        var stored = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["homesection0"] = "resume", ["homesection1"] = "latestmedia", ["tvhome"] = "stored-tvhome", ["landing-11111111111111111111111111111111"] = "movies",
            ["skipBackLength"] = "5000", ["skipForwardLength"] = "15000", ["dashboardTheme"] = "dark", ["useEpisodeImagesInNextUpAndResume"] = "true"
        };
        var expected = new Dictionary<string, string?>(PostedUserSettings().CustomPrefs, StringComparer.Ordinal);
        if (SettingsSections.DisplayPreferenceKeys.ContainsKey(section))
        {
            foreach (var key in expected.Keys.Where(k => SettingsSections.SectionOfKey(k) == section).ToList())
            {
                expected.Remove(key);
            }

            foreach (var (key, value) in stored.Where(kv => SettingsSections.SectionOfKey(kv.Key) == section))
            {
                expected[key] = value;
            }
        }

        Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), kid.CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(PostedUserSettings().CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal), open.CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(PostedUserSettings().CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal), admin.CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task UpdateUser_UnderTheProfileLock_RefusesANameChange_ACaseOnlyOneToo_AndMergesWithTheSameName()
    {
        await _h.LockAsync([AccessRolesLock.Profile], _kid.Id);
        foreach (var name in new[] { "kid2", "KID", "Kid", string.Empty })
        {
            var run = await _h.RunAsync(Users, "UpdateUser", FilterHarness.NonAdmin(_kid.Id), new() { ["updateUser"] = new UserDto { Name = name, Configuration = FilterHarness.Posted() } });
            Assert.True(run.Locked, name);
            Assert.Equal(AccessRulesActionFilter.NameLockedMessage, run.Problem!.Detail);
        }

        var same = new UserDto { Name = "kid", Configuration = FilterHarness.Posted() };
        Assert.True((await _h.RunAsync(Users, "UpdateUserLegacy", FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id, ["updateUser"] = same })).Ran);
        Assert.True(same.Configuration.EnableLocalPassword);

        // Locked elsewhere only: a rename goes on (the merge keeps that section's fields).
        await _h.LockAsync([AccessRolesLock.Home], _kid.Id);
        var renamed = new UserDto { Name = "kid2", Configuration = FilterHarness.Posted() };
        Assert.True((await _h.RunAsync(Users, "UpdateUser", FilterHarness.NonAdmin(_kid.Id), new() { ["updateUser"] = renamed })).Ran);
        Assert.Equal(FilterHarness.Stored().OrderedViews, renamed.Configuration.OrderedViews);
        Assert.False(renamed.Configuration.EnableLocalPassword);
    }

    [Fact]
    public async Task AnInvalidOrMissingBody_PassesTheMergesUntouched_ButTheRejectsStillRefuse()
    {
        await _h.LockAsync(FilterHarness.EveryLock, _kid.Id);
        var posted = FilterHarness.Posted();

        // ModelState invalid with the argument present (it bound but failed validation), and absent.
        Assert.True((await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userConfig"] = posted }, invalidModel: true)).Ran);
        Assert.Equal("posted-receiver", posted.CastReceiverId);
        Assert.True((await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), [], invalidModel: true)).Ran);
        Assert.True((await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userConfig"] = null })).Ran);
        Assert.True((await _h.RunAsync(Users, "UpdateUser", FilterHarness.NonAdmin(_kid.Id), new() { ["updateUser"] = new UserDto { Name = "other" } }, invalidModel: true)).Ran);
        Assert.True((await _h.RunAsync(Users, "UpdateUser", FilterHarness.NonAdmin(_kid.Id), new() { ["updateUser"] = new UserDto { Name = "kid", Configuration = null } })).Ran);
        Assert.True((await _h.RunAsync(DisplayPreferencesController, "UpdateDisplayPreferences", FilterHarness.NonAdmin(_kid.Id), new() { ["displayPreferencesId"] = "usersettings", ["client"] = "emby" }, invalidModel: true)).Ran);

        // A reject refuses whatever the body.
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id), [], invalidModel: true)).Locked);
    }

    [Fact]
    public async Task AnotherUsersSettings_PassToTheCore_WhichRefusesANonAdmin_AndAreNotRead()
    {
        await _h.LockAsync(FilterHarness.EveryLock, _kid.Id);
        var posted = FilterHarness.Posted();

        Assert.True((await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _open.Id, ["userConfig"] = posted })).Ran);
        Assert.Equal("posted-receiver", posted.CastReceiverId);
        Assert.True((await SaveUserSettingsAsync(FilterHarness.NonAdmin(_kid.Id), _open.Id, "usersettings", "emby", PostedUserSettings())).Ran);
        _h.DisplayManager.Verify(m => m.GetDisplayPreferences(_open.Id, It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        // The rejects refuse whoever the target is.
        Assert.True((await _h.RunAsync(Users, "UpdateUserPasswordLegacy", FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _open.Id })).Locked);
    }

    [Theory]
    [InlineData("usersettings", "emby")]
    [InlineData("3ce5b65de116d73165d1efc4a30ec35c", "emby")]
    [InlineData("3ce5b65d-e116-d731-65d1-efc4a30ec35c", "emby")]
    [InlineData("{3ce5b65d-e116-d731-65d1-efc4a30ec35c}", "emby")]
    [InlineData("(3ce5b65d-e116-d731-65d1-efc4a30ec35c)", "emby")]
    [InlineData("3CE5B65D-E116-D731-65D1-EFC4A30EC35C", "emby")]
    [InlineData("usersettings", "EMBY")]
    [InlineData("usersettings", "Emby")]
    public async Task TheUserSettingsDocument_IsMergedUnderEveryIdFormAndClientCase(string id, string client)
    {
        await _h.LockAsync([AccessRolesLock.Home, AccessRolesLock.Playback], _kid.Id);
        _h.StoreUserSettings(_kid.Id, client);
        var posted = PostedUserSettings();
        posted.CustomPrefs["HOMESECTION0"] = "livetv";
        posted.CustomPrefs["homesection12"] = "nextup";
        posted.CustomPrefs["SKIPBACKLENGTH"] = "1";
        posted.CustomPrefs.Remove("skipForwardLength");

        Assert.True((await SaveUserSettingsAsync(FilterHarness.NonAdmin(_kid.Id), null, id, client, posted)).Ran);

        Assert.DoesNotContain(posted.CustomPrefs.Keys, k => k is "HOMESECTION0" or "homesection12" or "homesection2" or "SKIPBACKLENGTH" or "TVHOME");
        Assert.Equal(("resume", "latestmedia", "stored-tvhome", "5000", "15000"), (posted.CustomPrefs["homesection0"], posted.CustomPrefs["homesection1"], posted.CustomPrefs["tvhome"], posted.CustomPrefs["skipBackLength"], posted.CustomPrefs["skipForwardLength"]));
        Assert.Equal("movies", posted.CustomPrefs["landing-11111111111111111111111111111111"]);
        Assert.False(posted.CustomPrefs.ContainsKey("landing-22222222222222222222222222222222"));

        // Unlocked keys as posted.
        Assert.Equal(("light", "false", "False"), (posted.CustomPrefs["dashboardTheme"], posted.CustomPrefs["useEpisodeImagesInNextUpAndResume"], posted.CustomPrefs["enableNextVideoInfoOverlay"]));
    }

    [Theory]
    [InlineData("homeview", "emby")]
    [InlineData("8d9ba0fe-2a1c-4f9e-8a4b-1f0d6f4a6c55", "emby")]
    [InlineData("usersettings", "android")]
    public async Task OtherDocumentsAndClients_PassUnmerged(string id, string client)
    {
        await _h.LockAsync(FilterHarness.EveryLock, _kid.Id);
        var posted = PostedUserSettings();

        Assert.True((await SaveUserSettingsAsync(FilterHarness.NonAdmin(_kid.Id), null, id, client, posted)).Ran);

        Assert.Equal(PostedUserSettings().CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal), posted.CustomPrefs.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        _h.DisplayManager.Verify(m => m.GetDisplayPreferences(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task JellyfinEnhancedsSettingsSaves_AreRefusedUnderEnhancedPanel_TheBookmarkOnesAndReadsPass()
    {
        await _h.LockAsync([AccessRolesLock.EnhancedPanel], _kid.Id, _admin.Id);
        var saves = new[] { "settings.json", "shortcuts.json", "elsewhere.json", "hidden-content.json", "spoilerblur.json", "a-new-one.json" };
        foreach (var file in saves)
        {
            var descriptor = FilterHarness.Descriptor(Je, "Save" + file, "Jellyfin.Plugin.JellyfinEnhanced", "POST", "JellyfinEnhanced/user-settings/{userId}/" + file);
            var run = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id.ToString("N") });
            Assert.True(run.Locked, file);
            Assert.Equal(AccessRulesActionFilter.LockedMessage(AccessRolesLock.EnhancedPanel), run.Problem!.Detail);
            Assert.True((await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_open.Id), new() { ["userId"] = _open.Id.ToString("N") })).Ran, file);
            Assert.True((await _h.RunAsync(descriptor, FilterHarness.Administrator(_admin.Id), new() { ["userId"] = _admin.Id.ToString("N") })).Ran, file);

            // JE writes the user its route names: an unlocked non-admin writing the locked user's file is refused too.
            Assert.True((await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_open.Id), new() { ["userId"] = _kid.Id.ToString("N") })).Locked, file);
        }

        foreach (var (method, template) in new[] { ("POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json"), ("POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json/add"), ("GET", "JellyfinEnhanced/user-settings/{userId}/settings.json"), ("POST", "JellyfinEnhanced/reviews/{tmdbId}") })
        {
            var descriptor = FilterHarness.Descriptor(Je, "Other", "Jellyfin.Plugin.JellyfinEnhanced", method, template);
            Assert.True((await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id.ToString("N") })).Ran, template);
        }

        // The same route on another plugin's controller is no JE save.
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "Save", "Jellyfin.Plugin.Other", "POST", "JellyfinEnhanced/user-settings/{userId}/settings.json"), FilterHarness.NonAdmin(_kid.Id))).Ran);
    }

    [Fact]
    public async Task JellyfinEnhancedsSpoilerGuardPreferences_AreRefusedUnderEnhancedPanel_TheHideAndDismissRoutesPass()
    {
        // JE 12.14's POST spoiler-blur/user-prefs writes the caller's own spoilerblur.json (its Prefs),
        // the file the locked SaveUserSpoilerBlur writes (read from the 12.14.0.0 DLL's IL); continue-watching/hide,
        // next-up/hide and whats-new/dismiss are user-undoable UI state and stay open.
        await _h.LockAsync([AccessRolesLock.EnhancedPanel], _kid.Id);
        foreach (var template in new[] { "JellyfinEnhanced/spoiler-blur/user-prefs", "jellyfinenhanced/Spoiler-Blur/User-Prefs" })
        {
            var prefs = FilterHarness.Descriptor(Je, "SetSpoilerBlurUserPrefs", "Jellyfin.Plugin.JellyfinEnhanced", "POST", template);
            var run = await _h.RunAsync(prefs, FilterHarness.NonAdmin(_kid.Id));
            Assert.True(run.Locked, template);
            Assert.Equal(AccessRulesActionFilter.LockedMessage(AccessRolesLock.EnhancedPanel), run.Problem!.Detail);
            Assert.True((await _h.RunAsync(prefs, FilterHarness.NonAdmin(_open.Id))).Ran, template);
            Assert.True((await _h.RunAsync(prefs, FilterHarness.Administrator(_admin.Id))).Ran, template);
        }

        foreach (var (action, method, template) in new[]
        {
            ("HideFromContinueWatching", "POST", "JellyfinEnhanced/continue-watching/hide/{itemId}"),
            ("HideFromNextUp", "POST", "JellyfinEnhanced/next-up/hide/{itemId}"),
            ("DismissWhatsNew", "POST", "JellyfinEnhanced/whats-new/dismiss"),
            ("GetSpoilerBlurUserPrefs", "GET", "JellyfinEnhanced/spoiler-blur/user-prefs")
        })
        {
            Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Je, action, "Jellyfin.Plugin.JellyfinEnhanced", method, template), FilterHarness.NonAdmin(_kid.Id))).Ran, template);
        }
    }

    [Fact]
    public async Task AnActionWhoseRulesCantBeRead_IsRefusedForANonAdmin_FailClosed_AndPassesForAdministratorsApiKeysAndAnonymousCallers()
    {
        // The rule table's lookup throws, so the action's rules may be settings rules.
        var descriptor = FilterHarness.Unreadable(Users, "UpdateUserConfiguration");
        Assert.ThrowsAny<Exception>(() => _h.Table.For(descriptor));

        var run = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_open.Id), new() { ["userConfig"] = FilterHarness.Posted() });

        Assert.True(run.Locked);
        Assert.False(run.Ran);
        Assert.Equal(AccessRulesActionFilter.UnavailableMessage, run.Problem!.Detail);
        Assert.Equal(1L, _h.Status.FilterErrors);
        Assert.Equal(1L, ((IDictionary<string, long>)_h.Counter.Describe()["Totals"]!)[BlockCounter.FailedClosed]);
        Assert.True((await _h.RunAsync(descriptor, FilterHarness.Administrator(_admin.Id))).Ran);
        Assert.True((await _h.RunAsync(descriptor, FilterHarness.ApiKey())).Ran);
        Assert.True((await _h.RunAsync(descriptor, FilterHarness.Anonymous())).Ran);

        // The hooks still run for an administrator, found by their own lookup: DeleteUser's per-user lock is held around it.
        IDisposable? during = null;
        var delete = FilterHarness.Unreadable(Users, "DeleteUser");
        Assert.True((await _h.RunAsync(delete, FilterHarness.Administrator(_admin.Id), new() { ["userId"] = _kid.Id }, inside: async () => during = await _h.Sync.Locks.TryAcquireAsync(_kid.Id, TimeSpan.Zero, TestContext.Current.CancellationToken))).Ran);
        Assert.Null(during);

        // With the settings locks off nothing is refused.
        _h.Sync.Configuration.EnforcementEnabled = false;
        Assert.True((await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_open.Id))).Ran);
        Assert.Equal(1L, _h.Status.FilterErrors);
    }

    [Fact]
    public async Task TheGlobalFilter_HandsTheUserPolicyFilterTheHookTheRuleTableFound()
    {
        // An action is classified once per request. A user policy filter whose own manifest has no hook
        // row still takes DeleteUser's per-user lock when the global filter runs it, since the table's hook comes along.
        var userPolicy = _h.NewUserPolicy(WithoutHooks(_h.Manifest));
        Assert.Empty(userPolicy.WatchedActions);
        var filter = _h.Build(_h.UserManager.Object, userPolicy: userPolicy);
        Assert.Equal(RuleKind.PerUserLock, _h.Table.For(FilterHarness.Descriptor(Users, "DeleteUser")).UserPolicyHook);

        IDisposable? during = null;
        var run = await _h.RunAsync(FilterHarness.Descriptor(Users, "DeleteUser"), FilterHarness.Administrator(_admin.Id), new() { ["userId"] = _kid.Id }, filter: filter, inside: async () => during = await _h.Sync.Locks.TryAcquireAsync(_kid.Id, TimeSpan.Zero, TestContext.Current.CancellationToken));

        Assert.True(run.Ran);
        Assert.Null(during);
        using var after = await _h.Sync.Locks.TryAcquireAsync(_kid.Id, TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task AMergeThatKeptLockedValues_NamesTheSections_InItsResponse_AndOneThatKeptNothingDoesnt()
    {
        // jellyfin-web shows its own "saved" for the 204, so the web guard tells the user from this header.
        await _h.LockAsync([AccessRolesLock.Home, AccessRolesLock.Playback], _kid.Id);

        var changed = await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id, ["userConfig"] = FilterHarness.Posted() });
        var same = await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userId"] = _kid.Id, ["userConfig"] = FilterHarness.Stored() });
        var open = await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_open.Id), new() { ["userId"] = _open.Id, ["userConfig"] = FilterHarness.Posted() });

        Assert.True(changed.Ran && same.Ran && open.Ran);
        Assert.Equal("Home,Playback", changed.Context.HttpContext.Response.Headers[AccessRulesActionFilter.KeptHeader].ToString());
        Assert.False(same.Context.HttpContext.Response.Headers.ContainsKey(AccessRulesActionFilter.KeptHeader));
        Assert.False(open.Context.HttpContext.Response.Headers.ContainsKey(AccessRulesActionFilter.KeptHeader));
    }

    [Fact]
    public async Task AMergedSaveThatFailed_NamesNothingAsKept()
    {
        // The header is set after the action, only for a save that went through (no exception, 2xx).
        await _h.LockAsync([AccessRolesLock.Home], _kid.Id);
        var arguments = () => new Dictionary<string, object?> { ["userId"] = _kid.Id, ["userConfig"] = FilterHarness.Posted() };
        var descriptor = FilterHarness.Descriptor(Users, "UpdateUserConfiguration");

        var threw = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_kid.Id), arguments(), exception: new InvalidOperationException("the save failed"));
        var failed = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_kid.Id), arguments(), result: new StatusCodeResult(500));
        var notFound = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_kid.Id), arguments(), result: new NotFoundResult());
        var ok = await _h.RunAsync(descriptor, FilterHarness.NonAdmin(_kid.Id), arguments(), result: new OkResult());

        Assert.True(threw.Ran && failed.Ran && notFound.Ran && ok.Ran);
        Assert.False(threw.Context.HttpContext.Response.Headers.ContainsKey(AccessRulesActionFilter.KeptHeader));
        Assert.False(failed.Context.HttpContext.Response.Headers.ContainsKey(AccessRulesActionFilter.KeptHeader));
        Assert.False(notFound.Context.HttpContext.Response.Headers.ContainsKey(AccessRulesActionFilter.KeptHeader));
        Assert.Equal("Home", ok.Context.HttpContext.Response.Headers[AccessRulesActionFilter.KeptHeader].ToString());
    }

    [Fact]
    public async Task TheDisplayPagesUnchangedSave_SaysNothing_ForAUserWhoNeverStoredItsKeys()
    {
        // Through the filter: a Kid-preset user (Display (account) locked) whose usersettings
        // document holds neither useEpisodeImagesInNextUpAndResume nor a theme (the harness's fresh document) saves the
        // Display page unchanged, which posts "false" and the web's default theme: no header. A real change says so.
        await _h.LockAsync([AccessRolesLock.DisplayAccount, AccessRolesLock.Home], _kid.Id);
        DisplayPreferencesDto Posted(string images) => new()
        {
            CustomPrefs = new Dictionary<string, string?> { ["useEpisodeImagesInNextUpAndResume"] = images, ["dashboardTheme"] = "dark", ["tvhome"] = null, ["skipBackLength"] = "10000", ["skipForwardLength"] = "30000" }
        };

        var unchanged = await SaveUserSettingsAsync(FilterHarness.NonAdmin(_kid.Id), null, "usersettings", "emby", Posted("false"));
        var changed = await SaveUserSettingsAsync(FilterHarness.NonAdmin(_kid.Id), null, "usersettings", "emby", Posted("true"));

        Assert.True(unchanged.Ran && changed.Ran);
        Assert.False(unchanged.Context.HttpContext.Response.Headers.ContainsKey(AccessRulesActionFilter.KeptHeader));
        Assert.Equal("DisplayAccount", changed.Context.HttpContext.Response.Headers[AccessRulesActionFilter.KeptHeader].ToString());
    }

    [Fact]
    public async Task TheRefusal_IsTheKitsLockedProblemDetails_NeverParentalControl()
    {
        await _h.LockAsync([AccessRolesLock.QuickConnect], _kid.Id);

        var run = await _h.RunAsync("Jellyfin.Api.Controllers.QuickConnectController", "AuthorizeQuickConnect", FilterHarness.NonAdmin(_kid.Id), new() { ["code"] = "123456" });

        var result = Assert.IsType<Jellyfin.Plugin.Kit.Api.KitProblemResult>(run.Context.Result);
        Assert.Equal(403, result.StatusCode);
        var problem = result.Value;
        Assert.Equal(("urn:jellyfin-plugin-kit:locked", "Forbidden", 403), (problem.Type, problem.Title, problem.Status));
        Assert.Equal("The Quick Connect settings are managed by your server administrator, so this change isn't allowed.", problem.Detail);
        Assert.DoesNotContain("ParentalControl", JsonSerializer.Serialize(problem), StringComparison.Ordinal);
        Assert.False(run.Ran);
    }

    [Fact]
    public async Task ExcludedAndUnassignedUsers_AndAnonymousCallers_MeetNoLock()
    {
        var role = await _h.LockAsync(FilterHarness.EveryLock, _kid.Id);
        await _h.Sync.SaveAsync(d => d with { Assignments = [.. d.Assignments.Select(a => a with { State = AccessRolesAssignmentState.Excluded })] });

        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Ran);
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_open.Id))).Ran);
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.Anonymous())).Ran);
        Assert.NotNull(role);
    }

    [Fact]
    public async Task AValueANewerBuildWrote_LocksEverySettingsSection()
    {
        // An unknown state makes the user managed with every settings section locked.
        await _h.LockAsync([], _kid.Id);
        await _h.Sync.SaveAsync(d => d with { Assignments = [.. d.Assignments.Select(a => a with { State = LenientEnum<AccessRolesAssignmentState>.Parse("Paused") })] });

        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Locked);
        Assert.True((await _h.RunAsync("Jellyfin.Api.Controllers.QuickConnectController", "AuthorizeQuickConnect", FilterHarness.NonAdmin(_kid.Id))).Locked);
    }

    [Fact]
    public async Task EnforcementOff_OrTheSafeModeFile_LetsEverythingThrough()
    {
        await _h.LockAsync(FilterHarness.EveryLock, _kid.Id);
        _h.Sync.Configuration.EnforcementEnabled = false;
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Ran);

        _h.Sync.Configuration.EnforcementEnabled = true;
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Sync.Paths.SafeModeFile)!);
        await File.WriteAllTextAsync(_h.Sync.Paths.SafeModeFile, string.Empty, TestContext.Current.CancellationToken);
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Ran);

        File.Delete(_h.Sync.Paths.SafeModeFile);
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Locked);
    }

    [Fact]
    public async Task ARuleThatThrows_RefusesThatRequest_FailClosed_CountedAndShown()
    {
        await _h.LockAsync([AccessRolesLock.Home], _kid.Id);
        var throwing = new Mock<IUserManager>();
        throwing.Setup(m => m.GetUserById(It.IsAny<Guid>())).Throws(new InvalidOperationException("The user manager is gone."));
        var filter = _h.Build(throwing.Object);

        var run = await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserConfiguration"), FilterHarness.NonAdmin(_kid.Id), new() { ["userConfig"] = FilterHarness.Posted() }, filter: filter);

        Assert.True(run.Locked);
        Assert.Equal(AccessRulesActionFilter.UnavailableMessage, run.Problem!.Detail);
        Assert.Equal(1L, _h.Status.FilterErrors);
        Assert.Equal(1L, ((IDictionary<string, long>)_h.Counter.Describe()["Totals"]!)[BlockCounter.FailedClosed]);

        // An administrator never meets the rule, so never its failure; an unlocked user's merge reads nothing.
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserConfiguration"), FilterHarness.Administrator(_admin.Id), new() { ["userConfig"] = FilterHarness.Posted() }, filter: filter)).Ran);
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserConfiguration"), FilterHarness.NonAdmin(_open.Id), new() { ["userConfig"] = FilterHarness.Posted() }, filter: filter)).Ran);
    }

    [Fact]
    public async Task AStoreThatCantBeRead_RefusesTheSettingsActions_FailClosed()
    {
        // The file is held open without sharing, so the store can't read it (it throws a 503 at every use until it can).
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.Combine("accessroles.json"), "{\"Version\": 1}", TestContext.Current.CancellationToken);
        using var held = new FileStream(directory.Combine("accessroles.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var store = AccessRolesStore.Create(directory.Combine("accessroles.json"), NullLogger.Instance);
        var filter = _h.Build(_h.UserManager.Object, new EffectiveRulesCache(store));

        var run = await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserPassword"), FilterHarness.NonAdmin(_open.Id), filter: filter);
        var merge = await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserConfiguration"), FilterHarness.NonAdmin(_open.Id), new() { ["userConfig"] = FilterHarness.Posted() }, filter: filter);
        var other = await _h.RunAsync(FilterHarness.Descriptor(Users, "GetCurrentUser"), FilterHarness.NonAdmin(_open.Id), filter: filter);
        var admin = await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserPassword"), FilterHarness.Administrator(_admin.Id), filter: filter);

        // Since 0.9.7 the UserDirectory lock needs the caller's locks as well: it fails closed too.
        var userList = await _h.RunAsync(FilterHarness.Descriptor(Users, "GetUsers"), FilterHarness.NonAdmin(_open.Id), filter: filter);

        Assert.True(run.Locked);
        Assert.Equal(AccessRulesActionFilter.UnavailableMessage, run.Problem!.Detail);
        Assert.True(merge.Locked);
        Assert.True(other.Ran);
        Assert.True(admin.Ran);
        Assert.True(userList.Locked);
        Assert.Equal(ServerHardening.UnavailableMessage, userList.Problem!.Detail);
    }

    [Theory]
    [InlineData("{ not json", null, "ReadOnlyDataUnreadable", true)]
    [InlineData("   ", null, "RecoveredEmpty", false)]
    [InlineData("{\"Version\": 2, \"Roles\": [], \"Assignments\": []}", null, "ReadOnlyNewerVersion", false)]
    [InlineData(null, "{\"Version\": 2, \"Roles\": [{\"Name\": \"Newer\"}]}", "ReadOnlyDataMissing", true)]
    public async Task AStoreThatCantBeTrusted_LocksEveryServerEnforcedSection_ForEveryNonAdmin_WhenNothingListsTheManagedUsers(string? content, string? snapshot, string status, bool locked)
    {
        // RecoveredEmpty, or read-only with no assignment. Since 0.9.13 (Degraded mode, DegradedModeTests) the
        // users the newest readable copy lists are locked; with no copy readable a read-only store locks every non-admin
        // (fail closed), and a store recovered empty with no evidence, or a readable newer file listing nobody, locks nobody.
        using var directory = new TempDirectory();
        if (content is not null)
        {
            await File.WriteAllTextAsync(directory.Combine("accessroles.json"), content, TestContext.Current.CancellationToken);
        }

        if (snapshot is not null)
        {
            Directory.CreateDirectory(directory.Combine("snapshots"));
            await File.WriteAllTextAsync(directory.Combine(Path.Combine("snapshots", "accessroles-20261018-031000.json")), snapshot, TestContext.Current.CancellationToken);
        }

        using var store = AccessRolesStore.Create(directory.Combine("accessroles.json"), NullLogger.Instance);
        var cache = new EffectiveRulesCache(store);
        var filter = _h.Build(_h.UserManager.Object, cache);

        Assert.Equal(status, store.Status.ToString());
        Assert.Equal(locked ? status : null, cache.Current().DegradedBecause);
        Assert.Equal(locked, (await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserPassword"), FilterHarness.NonAdmin(_open.Id), filter: filter)).Locked);
        var posted = FilterHarness.Posted();
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserConfiguration"), FilterHarness.NonAdmin(_open.Id), new() { ["userConfig"] = posted }, filter: filter)).Ran);
        if (locked)
        {
            Assert.All(UserConfigurationMerger.KnownFields, f => Assert.Equal(Value(FilterHarness.Stored(), f), Value(posted, f)));
        }

        Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserPassword"), FilterHarness.Administrator(_admin.Id), filter: filter)).Ran);
        Assert.Equal(cache.Current().DegradedBecause, _h.Status.Describe()["DegradedBecause"]);
    }

    [Fact]
    public async Task ANewerVersionsFile_WithAssignments_IsReadAsItIs()
    {
        // A schema 1 file as this build writes it, with a role and an assignment, then marked as a newer version's.
        using var directory = new TempDirectory();
        var file = directory.Combine("accessroles.json");
        using (var writer = AccessRolesStore.Create(file, NullLogger.Instance))
        {
            var role = Role(1, "Kid", [AccessRolesLock.Profile]);
            await writer.UpdateAsync(d => d with { Roles = [role], Assignments = [new RoleAssignment { UserId = _kid.Id, RoleIds = [role.Id], State = AccessRolesAssignmentState.Managed, UpdatedUtc = Now }] }, TestContext.Current.CancellationToken);
        }

        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!;
        json["Version"] = 2;
        await File.WriteAllTextAsync(file, json.ToJsonString(), TestContext.Current.CancellationToken);
        using var store = AccessRolesStore.Create(file, NullLogger.Instance);
        Assert.Equal(Jellyfin.Plugin.Kit.Storage.StoreStatus.ReadOnlyNewerVersion, store.Status);
        var cache = new EffectiveRulesCache(store);
        var filter = _h.Build(_h.UserManager.Object, cache);

        Assert.Null(cache.Current().DegradedBecause);
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserPassword"), FilterHarness.NonAdmin(_kid.Id), filter: filter)).Locked);
        Assert.True((await _h.RunAsync(FilterHarness.Descriptor(Users, "UpdateUserPassword"), FilterHarness.NonAdmin(_open.Id), filter: filter)).Ran);
    }

    [Fact]
    public async Task TheLockSnapshot_IsBuiltOnFirstUse_AndRebuiltWhenTheStoreChanges()
    {
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Ran);
        var builds = _h.Cache.Builds;
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Ran);
        Assert.Equal(builds, _h.Cache.Builds);

        await _h.LockAsync([AccessRolesLock.Profile], _kid.Id);
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Locked);
        Assert.Equal(builds + 1, _h.Cache.Builds);

        await _h.UnlockAllAsync();
        Assert.True((await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id))).Ran);
    }

    [Fact]
    public async Task RefusalsAndReverts_AreCountedPerUserRuleActionAndOutcome_AndAuditedOncePerWindow()
    {
        await _h.LockAsync([AccessRolesLock.Profile, AccessRolesLock.Home], _kid.Id);
        for (var i = 0; i < 3; i++)
        {
            await _h.RunAsync(Users, "UpdateUserPassword", FilterHarness.NonAdmin(_kid.Id));
        }

        await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userConfig"] = FilterHarness.Posted() });
        await _h.RunAsync(Users, "UpdateUserConfiguration", FilterHarness.NonAdmin(_kid.Id), new() { ["userConfig"] = FilterHarness.Stored() });

        var totals = (IDictionary<string, long>)_h.Counter.Describe()["Totals"]!;
        // One merge reverted two sections' fields: one count each.
        Assert.Equal((3L, 2L, 0L), (totals[BlockCounter.Refused], totals[BlockCounter.Reverted], totals[BlockCounter.FailedClosed]));
        Assert.Empty(_h.Counter.Take(all: false, _ => null));

        var lines = _h.Counter.Take(all: true, id => id.Equals(_kid.Id) ? "kid" : null);
        Assert.Equal(3, lines.Count);
        var refused = Assert.Single(lines, l => (string)l.Details!["Endpoint"]! == "UserController.UpdateUserPassword");
        Assert.Equal((AuditActions.RequestsBlocked, _kid.Id, "kid", "Settings.Profile", BlockCounter.Refused, 3), (refused.Action, refused.TargetUserId!.Value, refused.TargetName, refused.Details!["Rule"], refused.Details["Outcome"], refused.Details["Count"]));
        Assert.Contains(lines, l => (string)l.Details!["Rule"]! == "Settings.Home" && (string)l.Details["Outcome"]! == BlockCounter.Reverted && (int)l.Details["Count"]! == 1);
        Assert.Contains(lines, l => (string)l.Details!["Rule"]! == "Settings.Profile" && (string)l.Details["Outcome"]! == BlockCounter.Reverted && (string)l.Details["Endpoint"]! == "UserController.UpdateUserConfiguration");
        Assert.Empty(_h.Counter.Take(all: true, _ => null));
    }

    [Fact]
    public async Task AnAdminsPolicySave_IsStillPreMerged_InsideTheGlobalFilter()
    {
        // The user policy hook, unchanged: the save carries the roles' result, in one write.
        var kid = _h.Sync.Server.Add("managed", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.Sync.SaveAsync(d => d with
        {
            Roles = [role],
            Assignments = [Assign(SyncHarness.Read(kid), [role])],
            Ledger = new Dictionary<Guid, LedgerEntry> { [kid.Id] = new LedgerEntry { OriginalEnableAllFolders = true, OriginalEnableUserPreferenceAccess = true, CapturedUtc = _h.Sync.Now } }
        });
        Assert.True(Assert.Single((await _h.Sync.RunAsync()).Users).Wrote);
        var page = _h.Sync.Server.Load(kid.Id);
        page.EnabledFolders = [Movies.Id, Shows.Id];

        var context = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            new ActionContext(new Microsoft.AspNetCore.Http.DefaultHttpContext { User = FilterHarness.Administrator(_admin.Id) }, new Microsoft.AspNetCore.Routing.RouteData(), FilterHarness.Descriptor(Users, "UpdateUserPolicy")),
            [],
            new Dictionary<string, object?> { ["userId"] = kid.Id, ["newPolicy"] = page },
            new object());
        await _h.Filter.OnActionExecutionAsync(context, async () =>
        {
            await _h.Sync.Server.Manager.Object.UpdatePolicyAsync(kid.Id, page);
            return new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(context, [], new object()) { Result = new NoContentResult() };
        });

        Assert.Equal([Kids.Id], _h.Sync.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Single(_h.Sync.Lines(AuditActions.ManualEditOverridden));
    }

    [Fact]
    public async Task ALibraryRename_IsStillRecorded_InsideTheGlobalFilter()
    {
        // The library structure hook, unchanged.
        var run = await _h.RunAsync(FilterHarness.Descriptor(LibraryStructureFilter.Controller, "RenameVirtualFolder"), FilterHarness.Administrator(_admin.Id), new() { ["name"] = "Kids", ["newName"] = "kids", ["refreshLibrary"] = true });

        Assert.True(run.Ran);
        var rename = Assert.Single(_h.Sync.Store.Current.PendingRenames);
        Assert.Equal((Kids.Id, "Kids", "kids"), (rename.OldId, rename.OldName, rename.NewName));
    }

    public void Dispose() => _h.Dispose();

    // The build's manifest with every user policy hook row taken out.
    private static ActionManifest WithoutHooks(ActionManifest manifest)
    {
        using var stream = typeof(ActionManifest).Assembly.GetManifestResourceStream(Assert.Single(ActionManifest.EmbeddedResources()))!;
        var json = System.Text.Json.Nodes.JsonNode.Parse(stream)!;
        foreach (var action in json["Actions"]!.AsArray())
        {
            var rules = action!["Rules"]!.AsArray();
            foreach (var rule in rules.Where(r => r!["Kind"]!.GetValue<string>() is "UserPolicyPreMerge" or "PerUserLock" or "SignInHook").ToList())
            {
                rules.Remove(rule);
            }
        }

        Assert.Equal(manifest.Actions.Count, json["Actions"]!.AsArray().Count);
        return ActionManifest.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    private static object? Value(UserConfiguration configuration, string field)
    {
        var value = typeof(UserConfiguration).GetProperty(field)!.GetValue(configuration);
        return value is Guid[] ids ? string.Join(",", ids) : value;
    }

    // A usersettings post with a value of every section's keys that differs from the stored document's.
    private static DisplayPreferencesDto PostedUserSettings() => new()
    {
        CustomPrefs = new Dictionary<string, string?>
        {
            ["homesection0"] = "nextup", ["homesection1"] = "none", ["homesection2"] = "livetv", ["tvhome"] = "posted-tvhome",
            ["landing-22222222222222222222222222222222"] = "folders", ["skipBackLength"] = "20000", ["skipForwardLength"] = "40000",
            ["dashboardTheme"] = "light", ["useEpisodeImagesInNextUpAndResume"] = "false", ["enableNextVideoInfoOverlay"] = "False", ["chromecastVersion"] = "stable"
        }
    };

    private Task<FilterHarness.FilterRun> SaveUserSettingsAsync(System.Security.Claims.ClaimsPrincipal caller, Guid? userId, string id, string client, DisplayPreferencesDto posted)
    {
        var arguments = new Dictionary<string, object?> { ["displayPreferencesId"] = id, ["client"] = client, ["displayPreferences"] = posted };
        if (userId is { } target)
        {
            arguments["userId"] = target;
        }

        return _h.RunAsync(DisplayPreferencesController, "UpdateDisplayPreferences", caller, arguments);
    }
}
