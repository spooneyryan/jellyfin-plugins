using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The Settings tab's endpoints: <c>GET Settings</c> as this build reads the settings, and
/// <c>PUT Settings</c> through the write pattern of the roles API (a dry run with its <c>PreviewToken</c>, the save checked
/// against it), the secret-carrying allow-list keys' confirmation, the values checked, audited
/// <c>SettingsChanged</c>.
/// </summary>
public sealed class SettingsApiTests
{
    private const string SettingsPath = "/AccessRoles/Admin/Settings";

    [Fact]
    public async Task Settings_AreForAdministrators_AndReadAsThisBuildReadsThem()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        api.Configuration.DriftPolicy = "reportonly";
        api.Configuration.ReconcileIntervalMinutes = 600;
        api.Configuration.ConfigurationReadAllowList = ["Encoding", "encoding", " "];
        api.Configuration.FirstLoadMode = "Sometimes";

        using var user = await api.SendAsync(HttpMethod.Get, SettingsPath, caller: "user");
        using var anonymous = await api.SendAsync(HttpMethod.Get, SettingsPath, caller: null);
        var (status, settings) = await api.JsonAsync(HttpMethod.Get, SettingsPath);

        Assert.Equal((403, 401, 200), ((int)user.StatusCode, (int)anonymous.StatusCode, status));
        Assert.Equal(("ReportOnly", 60, "HideUntilKnown"), ((string?)settings!["DriftPolicy"], (int)settings["ReconcileIntervalMinutes"]!, (string?)settings["FirstLoadMode"]));
        Assert.Equal(["encoding"], settings["ConfigurationReadAllowList"]!.AsArray().Select(k => (string?)k));
        Assert.Equal(("Standard", "AllowList", "Toast"), ((string?)settings["LibraryHardening"], (string?)settings["NonAdminConfigurationReads"], (string?)settings["BlockedNavigationNotice"]));
        Assert.True((bool)settings["EnforcementEnabled"]!);
        Assert.False((bool)settings["MirrorPreferenceLockToPolicy"]!);
    }

    [Fact]
    public async Task ADryRun_SaysWhatChanges_AndSavesNothing_ThenTheSaveWithItsToken_SavesOnlyThat_Audited()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        api.Configuration.TrimPublicUsers = true;
        api.Configuration.BlockedNavigationNotice = "Toast";
        api.Configuration.LibraryHardening = "standard ";
        var body = new { DriftPolicy = "ReportOnly", ReconcileIntervalMinutes = 15, EnforcementEnabled = false, TrimPublicUsers = true };

        var (dryStatus, dry) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: body);
        var savesAfterDry = api.Saves;
        var linesAfterDry = api.Audit.Read(0, 10, null, AuditActions.SettingsChanged, null).TotalRecordCount;
        var admin = api.UserManager.Object.Users.Single(u => u.Username == "admin").Id;
        var (status, saved) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?ifPreview=" + (string?)dry!["PreviewToken"], body: body, userId: admin);

        Assert.Equal((200, 200), (dryStatus, status));
        Assert.True((bool)dry["DryRun"]!);
        Assert.Equal(
            [("EnforcementEnabled", "true", "false"), ("ReconcileIntervalMinutes", "5", "15"), ("DriftPolicy", "Reapply", "ReportOnly")],
            dry["Changes"]!.AsArray().Select(c => ((string)c!["Name"]!, (string)c["Before"]!, (string)c["After"]!)));
        Assert.Contains(dry["Warnings"]!.AsArray(), w => ((string)w!).StartsWith("Nothing is enforced", StringComparison.Ordinal));
        Assert.Equal((0, 0), (savesAfterDry, linesAfterDry));

        Assert.False((bool)saved!["DryRun"]!);
        Assert.Equal(1, api.Saves);
        Assert.Equal(("ReportOnly", 15, false), (api.Configuration.DriftPolicy, api.Configuration.ReconcileIntervalMinutes, api.Configuration.EnforcementEnabled));
        Assert.Equal("standard ", api.Configuration.LibraryHardening);
        var line = Assert.Single(api.Audit.Read(0, 10, null, AuditActions.SettingsChanged, null).Items);
        Assert.Equal((AuditActor.AdminKind, admin, "admin"), (line.Actor.Kind, line.Actor.UserId, line.Actor.Name));
        Assert.Contains("ReconcileIntervalMinutes", line.Details!["Changes"]!.ToString(), StringComparison.Ordinal);

        // The same body again changes nothing: no save, no line.
        var (_, again) = await api.JsonAsync(HttpMethod.Put, SettingsPath, body: body);
        Assert.Empty(again!["Changes"]!.AsArray());
        Assert.Equal(1, api.Saves);
        Assert.Single(api.Audit.Read(0, 10, null, AuditActions.SettingsChanged, null).Items);
    }

    [Fact]
    public async Task ASaveWhoseDryRunIsStale_Is409NamingTheSettingChangedMeanwhile_AndSavesNothing()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var body = new { DriftPolicy = "ReportOnly", ReconcileIntervalMinutes = 15 };
        var (_, dry) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: body);
        api.Configuration.ReconcileIntervalMinutes = 10;

        var (status, problem) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?ifPreview=" + (string?)dry!["PreviewToken"], body: body);

        Assert.Equal(409, status);
        Assert.Equal(AccessRolesException.Changed, (string?)problem!["Code"]);
        Assert.Contains("(ReconcileIntervalMinutes)", (string?)problem["detail"], StringComparison.Ordinal);
        Assert.Equal(("Reapply", 10, 0), (api.Configuration.DriftPolicy, api.Configuration.ReconcileIntervalMinutes, api.Saves));
        Assert.Empty(api.Audit.Read(0, 10, null, AuditActions.SettingsChanged, null).Items);

        // The negative control: the dry run seen now saves.
        var (_, now) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: body);
        var (ok, _) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?ifPreview=" + (string?)now!["PreviewToken"], body: body);
        Assert.Equal(200, ok);
        Assert.Equal(("ReportOnly", 15), (api.Configuration.DriftPolicy, api.Configuration.ReconcileIntervalMinutes));
    }

    [Fact]
    public async Task ASecretCarryingKey_AddedToTheAllowList_NeedsConfirmation()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        var body = new { ConfigurationReadAllowList = new[] { "encoding", "NETWORK", "branding", "network" } };

        var (_, dry) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: body);
        var (refused, problem) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?ifPreview=" + (string?)dry!["PreviewToken"], body: body);
        var keysAfterRefusal = api.Configuration.ConfigurationReadAllowList;
        var (status, saved) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?confirmSecretKeys=true&ifPreview=" + (string?)dry["PreviewToken"], body: body);
        var (_, kept) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: body);

        Assert.Equal(["network"], dry["SecretKeysAdded"]!.AsArray().Select(k => (string?)k));
        Assert.Equal(["encoding", "network", "branding"], dry["Settings"]!["ConfigurationReadAllowList"]!.AsArray().Select(k => (string?)k));
        Assert.Equal((400, AccessRolesException.SecretKeys), (refused, (string?)problem!["Code"]));
        Assert.Equal(["encoding"], keysAfterRefusal);
        Assert.Equal(200, status);
        Assert.Equal(["encoding", "network", "branding"], api.Configuration.ConfigurationReadAllowList);
        Assert.Equal("encoding, network, branding", (string?)saved!["Changes"]![0]!["After"]);

        // A key already on the list isn't "added" again.
        Assert.Empty(kept!["SecretKeysAdded"]!.AsArray());
    }

    [Theory]
    [InlineData("""{"ReconcileIntervalMinutes":0}""", "ReconcileIntervalMinutes must be 1 to 60.")]
    [InlineData("""{"SnapshotsToKeep":101}""", "SnapshotsToKeep must be 0 to 100.")]
    [InlineData("""{"AuditRetentionMonths":-1}""", "AuditRetentionMonths must be 0 to 120.")]
    [InlineData("""{"DriftPolicy":"Sometimes"}""", "DriftPolicy must be Reapply or ReportOnly.")]
    [InlineData("""{"LibraryHardening":"Strict"}""", "LibraryHardening Strict arrives in 1.1: choose Off or Standard.")]
    [InlineData("""{"FirstLoadMode":"Never"}""", "FirstLoadMode must be HideUntilKnown or ShowUntilKnown.")]
    [InlineData("""{"BlockedNavigationNotice":"Loud"}""", "BlockedNavigationNotice must be Toast or Silent.")]
    [InlineData("""{"NonAdminConfigurationReads":"Off"}""", "NonAdminConfigurationReads must be AllowList or Redact.")]
    [InlineData("""{"ConfigurationReadAllowList":["../etc"]}""", "\"../etc\" isn't a configuration key: letters, digits, '.', '_' and '-', at most 64.")]
    [InlineData("""{"DriftPolicy":"ReportOnly","DriftPolicyy":"Reapply","MirrorPreferenceLockToPolicyy":true}""", "No such setting: DriftPolicyy, MirrorPreferenceLockToPolicyy (the names are as GET Settings answers them). Nothing was saved.")]
    [InlineData("""{"Reconcile":5}""", "No such setting: Reconcile (the names are as GET Settings answers them). Nothing was saved.")]
    public async Task AValueOutOfItsRange_Is400_AndNothingIsSaved(string json, string message)
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();

        var (dry, dryProblem) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: System.Text.Json.Nodes.JsonNode.Parse(json));
        var (status, _) = await api.JsonAsync(HttpMethod.Put, SettingsPath, body: System.Text.Json.Nodes.JsonNode.Parse(json));

        Assert.Equal((400, 400), (dry, status));
        Assert.Equal(message, (string?)dryProblem!["detail"]);
        Assert.Equal(0, api.Saves);
    }

    [Fact]
    public async Task AStoredStrict_StaysAndIsReadAsItIs_AndOffOrStandardReplaceIt()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        api.Configuration.LibraryHardening = "Strict";

        var (_, read) = await api.JsonAsync(HttpMethod.Get, SettingsPath);
        var (same, _) = await api.JsonAsync(HttpMethod.Put, SettingsPath, body: new { LibraryHardening = "strict" });
        var (_, off) = await api.JsonAsync(HttpMethod.Put, SettingsPath, body: new { LibraryHardening = "Off" });

        Assert.Equal("Strict", (string?)read!["LibraryHardening"]);
        Assert.Equal(200, same);
        Assert.Equal(("LibraryHardening", "Strict", "Off"), ((string?)off!["Changes"]![0]!["Name"], (string?)off["Changes"]![0]!["Before"], (string?)off["Changes"]![0]!["After"]));
        Assert.Equal("Off", api.Configuration.LibraryHardening);
        Assert.Contains(off["Warnings"]!.AsArray(), w => ((string)w!).StartsWith("Library leak hardening is off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheServerWideBox_ShowsTheFirstLoadRule()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();
        api.Configuration.FirstLoadMode = "ShowUntilKnown";

        var (_, diagnostics) = await api.JsonAsync(HttpMethod.Get, "/AccessRoles/Admin/Diagnostics");

        Assert.Equal("ShowUntilKnown", (string?)diagnostics!["Enforcement"]!["ServerWide"]!["FirstLoadMode"]);
    }

    [Fact]
    public async Task ShowUntilKnown_IsWarnedAbout_AndHideUntilKnownIsNot()
    {
        // The dry run of ShowUntilKnown said nothing of the locked entries a new browser shows.
        await using var api = await RolesApiTests.StartWithUsersAsync();

        var (_, show) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: new { FirstLoadMode = "ShowUntilKnown" });
        api.Configuration.FirstLoadMode = "ShowUntilKnown";
        var (_, hide) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: new { FirstLoadMode = "HideUntilKnown" });

        var warning = Assert.Single(show!["Warnings"]!.AsArray());
        Assert.StartsWith("In a browser that doesn't know a managed user's locks yet", (string?)warning, StringComparison.Ordinal);
        Assert.Empty(hide!["Warnings"]!.AsArray());
    }

    [Fact]
    public async Task GetSettings_AnswersNoUnknownKeys()
    {
        await using var api = await RolesApiTests.StartWithUsersAsync();

        var (_, settings) = await api.JsonAsync(HttpMethod.Get, SettingsPath);
        var (_, dry) = await api.JsonAsync(HttpMethod.Put, SettingsPath + "?dryRun=true", body: new { DriftPolicy = "ReportOnly" });

        Assert.DoesNotContain("UnknownKeys", settings!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("UnknownKeys", dry!["Settings"]!.AsObject().Select(p => p.Key));
    }
}
