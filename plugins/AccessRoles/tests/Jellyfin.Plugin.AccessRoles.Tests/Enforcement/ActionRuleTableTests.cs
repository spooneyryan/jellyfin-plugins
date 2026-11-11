using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// The rule table (<c>ActionRuleTable</c>): every manifest action and rule classified by
/// the part that applies it, the arguments the settings filter reads among the actions' parameters, Jellyfin Enhanced's
/// saves matched by controller and route, and one lookup per descriptor.
/// </summary>
public sealed class ActionRuleTableTests
{
    private const string LibraryStructureFilterName = "Jellyfin.Api.Controllers.LibraryStructureController";

    private readonly ActionRuleTable _table = new(ActionManifest.ForBuild());

    [Fact]
    public void EveryManifestActionAndRule_IsClassified_TheCountsAsReviewed()
    {
        var manifest = _table.Manifest;
        var rules = manifest.Actions.SelectMany(a => a.Rules).ToList();
        Assert.All(rules, r => Assert.True(Enum.IsDefined(ActionRuleTable.Owner(r))));

        var summary = _table.Summary();
        Assert.Equal((390, 280), ((int)summary["Actions"]!, (int)summary["NoRule"]!));
        var counts = Enum.GetValues<RuleOwner>().ToDictionary(o => o, o => (IDictionary<string, object?>)summary[o.ToString()]!);
        Assert.Equal((14, 14), ((int)counts[RuleOwner.Settings]["Actions"]!, (int)counts[RuleOwner.Settings]["Rules"]!));
        Assert.Equal((14, 14), ((int)counts[RuleOwner.LibraryHooks]["Actions"]!, (int)counts[RuleOwner.LibraryHooks]["Rules"]!));
        Assert.Equal((73, 73), ((int)counts[RuleOwner.ServerHardening]["Actions"]!, (int)counts[RuleOwner.ServerHardening]["Rules"]!));
        Assert.Equal((11, 11), ((int)counts[RuleOwner.Strict]["Actions"]!, (int)counts[RuleOwner.Strict]["Rules"]!));
        Assert.Equal(rules.Count, counts.Values.Sum(c => (int)c["Rules"]!));
    }

    [Fact]
    public void TheSettingsRules_AreTheProfileAndQuickConnectRejectsAndTheThreeMerges_AdminSurfacesIsTheHardenings()
    {
        var settings = _table.Manifest.Actions.Where(a => a.Rules.Any(r => ActionRuleTable.Owner(r) == RuleOwner.Settings)).ToList();

        Assert.Equal(
            [
                "AuthorizeQuickConnect", "DeleteUserImage", "DeleteUserImageByIndexLegacy", "DeleteUserImageLegacy", "PostUserImage", "PostUserImageByIndexLegacy",
                "PostUserImageLegacy", "UpdateDisplayPreferences", "UpdateUser", "UpdateUserConfiguration", "UpdateUserConfigurationLegacy", "UpdateUserLegacy",
                "UpdateUserPassword", "UpdateUserPasswordLegacy"
            ],
            settings.Select(a => a.Action).Order(StringComparer.Ordinal));
        Assert.All(
            _table.Manifest.Actions.SelectMany(a => a.Rules).Where(r => r.Kind == RuleKind.LockedReject && r.Lock == AccessRolesLock.AdminSurfaces),
            r => Assert.Equal(RuleOwner.ServerHardening, ActionRuleTable.Owner(r)));
        Assert.Equal(RuleOwner.LibraryHooks, ActionRuleTable.Owner(new ActionManifestRule { Kind = RuleKind.PerUserLock }));
        Assert.Equal(RuleOwner.Strict, ActionRuleTable.Owner(new ActionManifestRule { Kind = RuleKind.StrictStream }));
    }

    [Fact]
    public void TheArgumentsTheFilterReads_AreParametersOfTheirActions()
    {
        // A Jellyfin rename of one of these shows as a changed action in the manifest test and the self-check; here the
        // filter's names are held to the manifest's.
        var reads = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["UserController.UpdateUser"] = [AccessRulesActionFilter.UpdateUserArgument, AccessRulesActionFilter.UserIdArgument],
            ["UserController.UpdateUserLegacy"] = [AccessRulesActionFilter.UpdateUserArgument, AccessRulesActionFilter.UserIdArgument],
            ["UserController.UpdateUserConfiguration"] = [AccessRulesActionFilter.UserConfigArgument, AccessRulesActionFilter.UserIdArgument],
            ["UserController.UpdateUserConfigurationLegacy"] = [AccessRulesActionFilter.UserConfigArgument, AccessRulesActionFilter.UserIdArgument],
            ["DisplayPreferencesController.UpdateDisplayPreferences"] =
                [AccessRulesActionFilter.DisplayPreferencesArgument, AccessRulesActionFilter.DisplayPreferencesIdArgument, AccessRulesActionFilter.ClientArgument, AccessRulesActionFilter.UserIdArgument]
        };
        foreach (var (key, arguments) in reads)
        {
            var entry = _table.Manifest.Actions.Single(a => a.Key == "Jellyfin.Api.Controllers." + key);
            Assert.All(arguments, a => Assert.Contains(a, entry.Parameters));
        }
    }

    [Fact]
    public void JellyfinEnhancedsSaves_AreMatchedByControllerMethodAndRoute()
    {
        const string Je = ActionRuleTable.JellyfinEnhancedController;
        Assert.True(Is(Je, "POST", "JellyfinEnhanced/user-settings/{userId}/settings.json"));
        Assert.True(Is(Je, "POST", "jellyfinenhanced/USER-SETTINGS/{userId}/hidden-content.json"));
        Assert.True(Is(Je, "POST", "user-settings/{id}/elsewhere.json"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/user-settings/{userId}/bookmark.json"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/user-settings/{userId}/Bookmark.json/add"));
        Assert.False(Is(Je, "GET", "JellyfinEnhanced/user-settings/{userId}/settings.json"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/user-settings/settings.json"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/a/b/user-settings/{userId}/settings.json"));
        Assert.False(Is("Jellyfin.Plugin.Other.OtherController", "POST", "JellyfinEnhanced/user-settings/{userId}/settings.json"));

        // JE 12.14's Spoiler Guard preferences write the same spoilerblur.json; the hide and dismiss
        // routes, user-undoable UI state, and the per-item Spoiler Guard routes aren't settings saves of the table.
        Assert.True(Is(Je, "POST", "JellyfinEnhanced/spoiler-blur/user-prefs"));
        Assert.True(Is(Je, "POST", "JELLYFINENHANCED/spoiler-blur/USER-PREFS"));
        Assert.True(Is(Je, "POST", "spoiler-blur/user-prefs"));
        Assert.False(Is(Je, "GET", "JellyfinEnhanced/spoiler-blur/user-prefs"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/spoiler-blur/user-prefs/x"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/spoiler-blur/series/{seriesId}"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/continue-watching/hide/{itemId}"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/next-up/hide/{itemId}"));
        Assert.False(Is(Je, "POST", "JellyfinEnhanced/whats-new/dismiss"));
        Assert.False(Is("Jellyfin.Plugin.Other.OtherController", "POST", "JellyfinEnhanced/spoiler-blur/user-prefs"));

        var rules = _table.For(FilterHarness.Descriptor(Je, "SaveUserSettingsSettings", "Jellyfin.Plugin.JellyfinEnhanced", "POST", "JellyfinEnhanced/user-settings/{userId}/settings.json"));
        Assert.True(rules.JellyfinEnhanced);
        Assert.Equal("LockedReject(EnhancedPanel)", Assert.Single(rules.Settings).ToString());
        Assert.Equal("JellyfinEnhancedController.SaveUserSettingsSettings", rules.Endpoint);
    }

    [Fact]
    public void JellyfinEnhancedsWrites_AreEveryMethodButTheReadingOnes_OnEveryControllerOfItsAssembly()
    {
        // JE 12.14 writes with DELETE too (RemoveUserBookmark, the Hidden Content and Spoiler
        // Guard undos, DeleteReview), so a settings save under user-settings/{userId}/ is any method but GET, HEAD and
        // OPTIONS, or an action that takes any method; the bookmark routes stay open whatever the method.
        const string Je = ActionRuleTable.JellyfinEnhancedController;
        foreach (var method in new[] { "PUT", "PATCH", "DELETE", null })
        {
            Assert.True(Is(Je, method, "JellyfinEnhanced/user-settings/{userId}/settings.json"), method ?? "any method");
            Assert.True(Is(Je, method, "JellyfinEnhanced/spoiler-blur/user-prefs"), method ?? "any method");
            Assert.False(Is(Je, method, "JellyfinEnhanced/user-settings/{userId}/bookmark.json/{bookmarkId}"), method ?? "any method");
        }

        foreach (var method in new[] { "GET", "HEAD", "OPTIONS" })
        {
            Assert.False(Is(Je, method, "JellyfinEnhanced/user-settings/{userId}/settings.json"), method);
        }

        // Another controller of JE's assembly (12.14's CatchUpController) is JE's too; another plugin's isn't.
        var catchUp = FilterHarness.Descriptor("Jellyfin.Plugin.JellyfinEnhanced.Controllers.CatchUpController", "PostEvent", "Jellyfin.Plugin.JellyfinEnhanced", "POST", "JellyfinEnhanced/catchup/event");
        Assert.True(ActionRuleTable.IsJellyfinEnhancedWrite(catchUp));
        Assert.False(ActionRuleTable.IsJellyfinEnhancedSettingsSave(catchUp));
        Assert.Equal("CatchUpController.PostEvent", ActionRuleTable.JellyfinEnhancedName(catchUp));
        Assert.False(ActionRuleTable.IsJellyfinEnhancedWrite(FilterHarness.Descriptor("Jellyfin.Plugin.Other.OtherController", "Save", "Jellyfin.Plugin.Other", "DELETE", "JellyfinEnhanced/user-settings/{userId}/settings.json")));
        Assert.Equal(["DELETE"], ActionRuleTable.Methods(FilterHarness.Descriptor(Je, "Save", "Jellyfin.Plugin.JellyfinEnhanced", "delete", "x")));
    }

    [Fact]
    public void TheRulesOfADescriptor_AreResolvedOnce()
    {
        var descriptor = FilterHarness.Descriptor("Jellyfin.Api.Controllers.UserController", "UpdateUserPassword");

        var first = _table.For(descriptor);

        Assert.Same(first, _table.For(descriptor));
        Assert.Equal(("UserController.UpdateUserPassword", true), (first.Endpoint, first.IsJellyfinApi));
        Assert.Equal("LockedReject(Profile)", Assert.Single(first.Settings).ToString());
        Assert.True(_table.For(FilterHarness.Descriptor(LibraryStructureFilterName, "AddVirtualFolder")).StructureHook);
        Assert.Empty(_table.For(FilterHarness.Descriptor("Jellyfin.Api.Controllers.ConfigurationController", "GetNamedConfiguration")).Settings);
    }

    [Fact]
    public void TheTablesUserPolicyHook_IsTheOneTheUserPolicyFilterFinds_ForEveryManifestAction()
    {
        // The global filter hands the table's hook to the user policy filter; they must agree.
        var watched = UserPolicyFilter.Actions(_table.Manifest);
        foreach (var action in _table.Manifest.Actions)
        {
            RuleKind? expected = watched.TryGetValue((action.Controller, action.Action), out var kind) ? kind : null;
            Assert.Equal(expected, _table.For(action.Controller, action.Action).UserPolicyHook);
            Assert.Equal(expected, _table.For(FilterHarness.Descriptor(action.Controller, action.Action)).UserPolicyHook);
        }

        Assert.Equal(7, watched.Count);
    }

    // A JE controller lives in JE's assembly; another plugin's controller in its own.
    private static bool Is(string controller, string? method, string template)
        => ActionRuleTable.IsJellyfinEnhancedSettingsSave(FilterHarness.Descriptor(controller, "Save", controller.StartsWith("Jellyfin.Plugin.JellyfinEnhanced.", StringComparison.Ordinal) ? "Jellyfin.Plugin.JellyfinEnhanced" : "Jellyfin.Plugin.Other", method, template));
}
