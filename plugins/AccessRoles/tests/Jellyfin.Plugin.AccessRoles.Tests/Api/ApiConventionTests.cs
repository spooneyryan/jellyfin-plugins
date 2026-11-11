using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Api;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The kit's API checks on Access Roles: the DTO prefix
/// (<c>AccessRoles</c>), controller names and routes under <c>/AccessRoles</c>, and no anonymous action but the script.
/// </summary>
public sealed class ApiConventionTests
{
    [Fact]
    public void KitOptions_AreValid_AndAreThePluginsIdentity()
    {
        var options = AccessRolesKit.Options;

        Assert.Empty(options.Validate());
        Assert.Same(typeof(Plugin).Assembly, options.PluginAssembly);
        Assert.Equal(
            ("Access Roles", "AccessRoles", "jfar", "AccessRoles", "accessroles", new Guid("7cbc1019-49a5-4b97-8aa2-279de946ef88")),
            (options.PluginName, options.RoutePrefix, options.Prefix, options.SchemaPrefix, options.DataFolder, options.PluginId));
        Assert.Empty(options.Pages);
        Assert.NotNull(options.HeadFragment);
    }

    [Fact]
    public void SchemaIds_CarryTheAccessRolesPrefix_AndAreNotForeign()
    {
        AssertNoProblems(SchemaPrefixChecks.Check(typeof(Plugin).Assembly, AccessRolesKit.Options.SchemaPrefix));
    }

    [Fact]
    public void SchemaIds_AreThePinnedSet()
    {
        // The roles API brings the first schemas, every one prefixed, the library sync its run's
        // report, and the import its proposal; Diagnostics and the kit's user reports answer dictionaries. A new schema fails here until it is added on
        // purpose.
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal(
            [
                "AccessRolesApplyPolicyRequest", "AccessRolesAssignmentDto", "AccessRolesAssignmentLibrariesDto", "AccessRolesAssignmentState", "AccessRolesAuditActorDto", "AccessRolesAuditEntryDto",
                "AccessRolesAuditEntryDtoQueryResult", "AccessRolesBulkRequest", "AccessRolesChangeDto", "AccessRolesDashboardRowDto", "AccessRolesDataImportDto",
                "AccessRolesDefaultRolesRequest", "AccessRolesDegradedDto", "AccessRolesEffectiveDto", "AccessRolesEffectiveLibrariesDto", "AccessRolesEffectiveLibraryDto",
                "AccessRolesImportAssignmentDto", "AccessRolesImportDto", "AccessRolesImportRoleDto", "AccessRolesImportWarningDto",
                "AccessRolesLeavingDto", "AccessRolesLibraryDto", "AccessRolesLibraryNameDto", "AccessRolesLibraryRefDto", "AccessRolesLibraryRefRequest",
                "AccessRolesLibraryState", "AccessRolesLibraryUserDto", "AccessRolesLock", "AccessRolesLockOverride", "AccessRolesLockStateDto", "AccessRolesMeDto", "AccessRolesNativeFlagsDto",
                "AccessRolesNativeLibrariesDto", "AccessRolesPendingReleaseDto", "AccessRolesPendingWriteDto", "AccessRolesPreset", "AccessRolesPreviewDto", "AccessRolesPreviewRequest",
                "AccessRolesPreviewUserDto", "AccessRolesReleaseLocksDto", "AccessRolesReleaseLocksRequest", "AccessRolesReleasedUserDto", "AccessRolesRoleDto", "AccessRolesRoleLibrariesDto", "AccessRolesRoleLibrariesRequest",
                "AccessRolesRoleLibraryMode", "AccessRolesRoleRefDto", "AccessRolesRoleRequest", "AccessRolesSettingChangeDto", "AccessRolesSettingsChangeDto", "AccessRolesSettingsDto",
                "AccessRolesSyncDto", "AccessRolesSyncLibraryDto",
                "AccessRolesSyncUserDto", "AccessRolesUnmanageChoice", "AccessRolesUnmanageRequest",
                "AccessRolesUserDto", "AccessRolesUserLibrariesRequest", "AccessRolesUserLibraryMode", "AccessRolesUserRefDto", "AccessRolesUserRequest",
                "AccessRolesUserState", "AccessRolesUserTokenRequest"
            ],
            SchemaPrefixChecks.FindReachedSchemas(ControllerDiscovery.FindControllers(assembly), assembly).Select(s => s.SchemaId).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ControllerNames_StartWithTheRoutePrefix_AndAreNotCoreNames()
    {
        AssertNoProblems(ControllerNameChecks.CheckNames(typeof(Plugin).Assembly, AccessRolesKit.Options.RoutePrefix));
    }

    [Fact]
    public void Routes_AreUnderTheRoutePrefix()
    {
        AssertNoProblems(ControllerNameChecks.CheckRoutes(typeof(Plugin).Assembly, AccessRolesKit.Options.RoutePrefix));
    }

    [Fact]
    public void OnlyTheClientScript_IsAnonymous()
    {
        AssertNoProblems(AnonymousActionChecks.Check(typeof(Plugin).Assembly));
    }

    [Fact]
    public void Checks_SeeEveryController_AndEveryAction()
    {
        // Guards the checks above against passing vacuously.
        Assert.Equal(
            [typeof(AccessRolesAdminController), typeof(AccessRolesController), typeof(AccessRolesDiagnosticsController), typeof(AccessRolesRolesController), typeof(AccessRolesUsersController)],
            ControllerDiscovery.FindControllers(typeof(Plugin).Assembly).OrderBy(t => t.Name, StringComparer.Ordinal));
        Assert.Equal(
            [
                "AccessRolesAdminController.ApplyAccessRolesDeletedUserPolicy", "AccessRolesAdminController.ExportAccessRolesAudit", "AccessRolesAdminController.ExportAccessRolesData",
                "AccessRolesAdminController.GetAccessRolesAudit", "AccessRolesAdminController.GetAccessRolesDiagnostics",
                "AccessRolesAdminController.GetAccessRolesLibraries", "AccessRolesAdminController.GetAccessRolesSettings", "AccessRolesAdminController.GetAccessRolesSnapshots", "AccessRolesAdminController.GrantAccessRolesLibraryAgain",
                "AccessRolesAdminController.ImportAccessRolesData", "AccessRolesAdminController.ImportExistingAccessRoles", "AccessRolesAdminController.PreviewAccessRoles", "AccessRolesAdminController.PurgeAccessRolesLibrary",
                "AccessRolesAdminController.ReleaseAccessRolesLocks", "AccessRolesAdminController.RemapAccessRolesUsers", "AccessRolesAdminController.ResolveAccessRolesDegraded", "AccessRolesAdminController.RestoreAccessRolesSnapshot", "AccessRolesAdminController.RunAccessRolesMaintenance", "AccessRolesAdminController.SyncAccessRolesLibraries", "AccessRolesAdminController.UpdateAccessRolesSettings",
                "AccessRolesController.GetAccessRolesAdminScript", "AccessRolesController.GetAccessRolesClientScript", "AccessRolesController.GetAccessRolesMe",
                "AccessRolesDiagnosticsController.PostAccessRolesClientHealth", "AccessRolesRolesController.CreateAccessRolesRole",
                "AccessRolesRolesController.DeleteAccessRolesRole", "AccessRolesRolesController.GetAccessRolesRole",
                "AccessRolesRolesController.GetAccessRolesRoles", "AccessRolesRolesController.SetAccessRolesDefaultRoles",
                "AccessRolesRolesController.UpdateAccessRolesRole", "AccessRolesUsersController.BulkAccessRolesUsers",
                "AccessRolesUsersController.GetAccessRolesEffective", "AccessRolesUsersController.GetAccessRolesUsers",
                "AccessRolesUsersController.UnmanageAccessRolesUser", "AccessRolesUsersController.UpdateAccessRolesUser"
            ],
            ControllerDiscovery.FindControllers(typeof(Plugin).Assembly).SelectMany(c => ControllerDiscovery.FindActions(c).Select(a => ControllerDiscovery.Describe(c, a))).Order(StringComparer.Ordinal));
        Assert.True(AnonymousActionChecks.IsAnonymous(typeof(AccessRolesController), typeof(AccessRolesController).GetMethod(nameof(AccessRolesController.GetAccessRolesClientScript))!));
        Assert.True(AnonymousActionChecks.IsAnonymous(typeof(AccessRolesController), typeof(AccessRolesController).GetMethod(nameof(AccessRolesController.GetAccessRolesAdminScript))!));
        Assert.False(AnonymousActionChecks.IsAnonymous(typeof(AccessRolesAdminController), typeof(AccessRolesAdminController).GetMethod(nameof(AccessRolesAdminController.GetAccessRolesDiagnostics))!));
    }

    [Fact]
    public void EveryRolesApiAction_RequiresElevation_AndCarriesTheProblemFilter()
    {
        // Every admin endpoint is RequiresElevation (administrators and API keys, the break-glass path), and
        // its refusals carry their Code (AccessRolesProblemFilter) next to the kit's filter.
        foreach (var controller in new[] { typeof(AccessRolesAdminController), typeof(AccessRolesRolesController), typeof(AccessRolesUsersController) })
        {
            var authorize = Assert.Single(controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
            Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, authorize.Policy);
            Assert.Single(controller.GetCustomAttributes(typeof(Jellyfin.Plugin.AccessRoles.Services.AccessRolesProblemFilterAttribute), inherit: true));
            Assert.All(ControllerDiscovery.FindActions(controller), a => Assert.False(AnonymousActionChecks.IsAnonymous(controller, a)));
            Assert.All(ControllerDiscovery.FindActions(controller), a => Assert.Empty(a.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)));
        }
    }

    private static void AssertNoProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }
}
