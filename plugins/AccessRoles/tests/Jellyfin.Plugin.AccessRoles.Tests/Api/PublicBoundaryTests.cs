using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Hosting;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Api;

/// <summary>
/// The public/internal boundary (the kit's and the plugin's): the controllers, the parts Jellyfin
/// creates, the configuration, the roles API's two service interfaces and its DTOs (with the enums they show) are public
/// since 0.9.2; the store, its records, the resolver, the audit log, the manifest and every service implementation are
/// internal.
/// </summary>
public sealed class PublicBoundaryTests
{
    private static readonly System.Reflection.Assembly Assembly = typeof(Plugin).Assembly;

    [Fact]
    public void Boundary_HasNoProblems()
    {
        var problems = PublicBoundaryChecks.Check(Assembly, AccessRolesKit.Options.SchemaPrefix);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PublicSurface_IsThePinnedSet()
    {
        // A new public type fails here until it is added on purpose, with its role.
        var surface = PublicBoundaryChecks.Describe(Assembly, AccessRolesKit.Options.SchemaPrefix, "Jellyfin.Plugin.AccessRoles");

        AssertTypes(
            [typeof(AccessRolesAdminController), typeof(AccessRolesController), typeof(AccessRolesDiagnosticsController), typeof(AccessRolesRolesController), typeof(AccessRolesUsersController)],
            surface.OfRole(PublicRole.Controller));
        AssertTypes([typeof(AccessRolesMaintenanceTask), typeof(Plugin), typeof(PluginServiceRegistrator)], surface.OfRole(PublicRole.JellyfinPart));
        AssertTypes([typeof(PluginConfiguration)], surface.OfRole(PublicRole.Configuration));
        AssertTypes([typeof(IAccessRolesAdmin), typeof(IAccessRolesAudit), typeof(IAccessRolesMe), typeof(IAccessRolesRecovery), typeof(IAccessRolesSettings)], surface.OfRole(PublicRole.ServiceInterface));
        AssertTypes(
            [
                typeof(AccessRolesApplyPolicyRequest), typeof(AccessRolesAssignmentDto), typeof(AccessRolesAssignmentLibrariesDto), typeof(AccessRolesAuditActorDto), typeof(AccessRolesAuditEntryDto), typeof(AccessRolesBulkRequest),
                typeof(AccessRolesChangeDto), typeof(AccessRolesDashboardRowDto), typeof(AccessRolesDataImportDto), typeof(AccessRolesDefaultRolesRequest), typeof(AccessRolesDegradedDto), typeof(AccessRolesEffectiveDto),
                typeof(AccessRolesEffectiveLibrariesDto), typeof(AccessRolesEffectiveLibraryDto),
                typeof(AccessRolesImportAssignmentDto), typeof(AccessRolesImportDto), typeof(AccessRolesImportRoleDto), typeof(AccessRolesImportWarningDto),
                typeof(AccessRolesLeavingDto), typeof(AccessRolesLibraryDto), typeof(AccessRolesLibraryUserDto),
                typeof(AccessRolesLibraryNameDto), typeof(AccessRolesLibraryRefDto), typeof(AccessRolesLibraryRefRequest), typeof(AccessRolesLockStateDto), typeof(AccessRolesMeDto),
                typeof(AccessRolesNativeFlagsDto), typeof(AccessRolesNativeLibrariesDto), typeof(AccessRolesPendingReleaseDto), typeof(AccessRolesPendingWriteDto),
                typeof(AccessRolesPreviewDto), typeof(AccessRolesPreviewRequest), typeof(AccessRolesPreviewUserDto), typeof(AccessRolesReleasedUserDto), typeof(AccessRolesReleaseLocksDto), typeof(AccessRolesReleaseLocksRequest), typeof(AccessRolesRoleDto), typeof(AccessRolesRoleLibrariesDto),
                typeof(AccessRolesRoleLibrariesRequest), typeof(AccessRolesRoleRefDto), typeof(AccessRolesRoleRequest),
                typeof(AccessRolesSettingChangeDto), typeof(AccessRolesSettingsChangeDto), typeof(AccessRolesSettingsDto), typeof(AccessRolesSettingsOptions), typeof(AccessRolesSyncDto),
                typeof(AccessRolesSyncLibraryDto), typeof(AccessRolesSyncOptions), typeof(AccessRolesSyncUserDto), typeof(AccessRolesUnmanageRequest),
                typeof(AccessRolesUserDto), typeof(AccessRolesUserLibrariesRequest), typeof(AccessRolesUserRefDto), typeof(AccessRolesUserRequest), typeof(AccessRolesUserTokenRequest),
                typeof(AccessRolesWriteOptions),
                typeof(AccessRolesAssignmentState), typeof(AccessRolesLibraryState), typeof(AccessRolesLock), typeof(AccessRolesLockOverride),
                typeof(AccessRolesPreset), typeof(AccessRolesRoleLibraryMode), typeof(AccessRolesUnmanageChoice), typeof(AccessRolesUserLibraryMode),
                typeof(AccessRolesUserState)
            ],
            surface.OfRole(PublicRole.Dto));
        AssertTypes([], surface.OfRole(PublicRole.Facade));
        Assert.Empty(surface.Unclassified);
        Assert.Empty(surface.ReachedOutsideModels);

        // The kit's public types, compiled in whole: the interfaces controllers take and the abstract task bases.
        AssertTypes([typeof(IEmbeddedScript), typeof(IKitDiagnostics), typeof(IKitPages), typeof(KitMaintenanceTaskBase), typeof(KitPostScanTaskBase)], surface.KitPublicTypes);
    }

    [Fact]
    public void TheStoreRecordsResolverAuditAndServices_AreInternal()
    {
        Type[] internals =
        [
            typeof(AccessRolesKit), typeof(IPluginConfigurationProvider), typeof(PluginConfigurationProvider), typeof(AccessRolesStartup), typeof(AccessRolesDiagnostics),
            typeof(AccessRolesStore), typeof(AccessRolesPaths), typeof(AccessRolesDocument), typeof(AccessRole), typeof(RoleLibraries), typeof(LibraryReference),
            typeof(RoleAssignment), typeof(AssignmentLibraries), typeof(KnownLibrary), typeof(PendingRename), typeof(LedgerEntry), typeof(LedgerWrite), typeof(LedgerRelease),
            typeof(LedgerAccess), typeof(AuditActorScope), typeof(LenientEnum<AccessRolesLock>), typeof(LenientEnumJsonConverterFactory), typeof(UserIdMapJsonConverter<LedgerEntry>), typeof(StoreValues),
            typeof(RuleKind), typeof(ApiActionShape), typeof(ActionManifest), typeof(ActionManifestEntry), typeof(ActionManifestRule),
            typeof(ActionManifestComparison), typeof(ServerActions),
            typeof(EffectiveRulesResolver), typeof(EffectiveAccess), typeof(LibraryDecision), typeof(LockDecision), typeof(VisibleLibrary), typeof(HiddenLibrary),
            typeof(ChangeAnalysis), typeof(ChangeReport), typeof(UserChange), typeof(LeavingResult), typeof(AccessPresets), typeof(AccessSources),
            typeof(CatalogLibrary), typeof(LibraryCatalog), typeof(ILibraryCatalogSource), typeof(VirtualFolderCatalogSource), typeof(NativeAccess),
            typeof(ServerUser), typeof(IServerUsers), typeof(JellyfinServerUsers),
            typeof(AccessRolesAuditLog), typeof(AuditEntry), typeof(AuditRecord), typeof(AuditActor), typeof(AuditActions), typeof(AuditPage), typeof(AuditGuidConverter),
            typeof(AccessRolesAdminService), typeof(AccessRolesSettingsService), typeof(AccessRolesRequests), typeof(AccessRolesMapper), typeof(AccessRolesAuditDetails), typeof(AccessRolesTokens),
            typeof(AccessRolesException), typeof(AccessRolesProblemFilterAttribute), typeof(AccessRolesUserDataOwner),
            typeof(LibraryFields), typeof(LibraryLifecycle), typeof(LifecycleEvent), typeof(LifecycleEventKind), typeof(LifecycleResult), typeof(LockoutGuard),
            typeof(PolicyWriter), typeof(PolicyFields), typeof(UserPolicyLocks), typeof(PolicyWriteResult), typeof(PolicyWriteOutcome), typeof(PendingVerification),
            typeof(VerificationResult), typeof(VerificationOutcome), typeof(ICommittedUsers), typeof(CommittedUser), typeof(DatabaseCommittedUsers), typeof(DriftDecision),
            typeof(LibrarySync), typeof(LibrarySyncService), typeof(LibrarySyncStatus), typeof(LibrarySyncAudit), typeof(LibraryStructureFilter),
            typeof(LibraryStructureCall), typeof(LibraryStructureCallKind), typeof(SyncReport), typeof(SyncRequest), typeof(SyncUserResult),
            typeof(SyncActionKind), typeof(SyncTrigger), typeof(SyncReasons), typeof(SyncPlanToken),
            typeof(ActionRuleTable), typeof(ActionRules), typeof(RuleOwner), typeof(AccessRulesActionFilter), typeof(EffectiveRulesCache), typeof(LockSnapshot),
            typeof(UserConfigurationMerger), typeof(DisplayPreferencesMerger), typeof(SettingsSections), typeof(BlockCounter), typeof(BlockCounterService),
            typeof(EnforcementStatus), typeof(RuleTableSelfCheck), typeof(AccessRolesMe)
        ];

        Assert.All(internals, type => Assert.False(type.IsVisible, type.FullName + " is public."));
    }

    private static void AssertTypes(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(t => t.FullName).Order(StringComparer.Ordinal), actual.Select(t => t.FullName));
}
