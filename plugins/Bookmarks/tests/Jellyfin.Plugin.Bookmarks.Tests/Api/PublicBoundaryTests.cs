using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Library;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Bookmarks.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The public/internal boundary: only controllers, the parts Jellyfin creates, the
/// configuration, the service interfaces the controllers take and DTOs are public; the service interfaces use only DTOs,
/// Jellyfin's and the BCL's types; controllers get DTOs from the services and never touch a record, the store or the
/// mapper; records, the store, the mapper and every implementation are internal.
/// </summary>
public sealed class PublicBoundaryTests
{
    private static readonly System.Reflection.Assembly Assembly = typeof(Plugin).Assembly;

    [Fact]
    public void Boundary_HasNoProblems()
    {
        var problems = PublicBoundaryChecks.Check(Assembly, BookmarksKit.Options.SchemaPrefix);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PublicSurface_IsThePinnedSet()
    {
        // A new public type fails here until it is added on purpose, with its role. In 1.1.0 IBookmarkJeImport joined the
        // service interfaces with the Jellyfin Enhanced import's endpoint, its request, report and enum the DTOs; the
        // post-scan task came with the kit's library-change monitor, the maintenance task with its record lifecycle;
        // IBookmarkRecordsAdmin (the admin actions on records of users and videos that are gone) came with
        // BookmarkApplyPolicyRequest, and the kit's move hook, Integration/ItemMovedHookV1, the first facade;
        // IBookmarkQuery and IBookmarkPreferences with the My Bookmarks page's lists, the bulk delete, the preferences and the
        // capabilities' shortcuts and pages (the query filter is an action's parameter model; the sort, filter and type enums
        // live in Api.Models, since only the API has them); IBookmarkAdminTools, the settings page's export, import,
        // snapshots, maintenance run and shortcut check (plain dictionaries, like the Diagnostics body, so no new DTO). In
        // 1.2.0 IBookmarkClips with the My Clips page's controller, its list, summary, status and bulk delete (the clip list's
        // filter an action's parameter model, the state and sort enums in Api.Models); the second facade,
        // Integration/ClipRootsV1 (the clip folders for Library Maintenance's Duplicates); IBookmarkAdminClips with the
        // admins' clip controller, its clip rows (no note), the per-user totals and their rows (the list's filter, the bulk
        // delete's request and result and the
        // status rows are the user's).
        var surface = PublicBoundaryChecks.Describe(Assembly, BookmarksKit.Options.SchemaPrefix, "Jellyfin.Plugin.Bookmarks");

        AssertTypes(
            [
                typeof(BookmarksAdminClipsController), typeof(BookmarksAdminController), typeof(BookmarksClipsController), typeof(BookmarksController), typeof(BookmarksDiagnosticsController),
                typeof(BookmarksIdentityDemoController), typeof(BookmarksLibraryChangeDemoController), typeof(BookmarksLibraryDemoController), typeof(BookmarksPagesController),
                typeof(BookmarksUserDataDemoController)
            ],
            surface.OfRole(PublicRole.Controller));
        AssertTypes([typeof(BookmarksPostScanTask), typeof(BookmarksMaintenanceTask), typeof(Plugin), typeof(PluginServiceRegistrator)], surface.OfRole(PublicRole.JellyfinPart));
        AssertTypes([typeof(PluginConfiguration)], surface.OfRole(PublicRole.Configuration));
        AssertTypes(
            [
                typeof(IBookmarkIdentityDemo), typeof(IBookmarkLibraryChangeDemo), typeof(IBookmarkLibraryDemo), typeof(IBookmarkManager), typeof(IBookmarkPreferences),
                typeof(IBookmarkQuery), typeof(IBookmarkRecordsAdmin), typeof(IBookmarkUserDataDemo), typeof(Jellyfin.Plugin.Bookmarks.Import.IBookmarkJeImport),
                typeof(IBookmarkAdminTools), typeof(IBookmarkClips), typeof(IBookmarkAdminClips)
            ],
            surface.OfRole(PublicRole.ServiceInterface));
        AssertTypes(
            [
                typeof(BookmarkApplyPolicyRequest), typeof(BookmarkCapabilitiesDto), typeof(BookmarkClipDto), typeof(BookmarkClipRequest), typeof(BookmarkCreateRequest), typeof(BookmarkDto),
                typeof(BookmarkRenameRequest), typeof(BookmarkUpdateRequest), typeof(BookmarkClipMode), typeof(BookmarkClipStatus), typeof(BookmarkOrigin),
                typeof(BookmarkStoreReadOnlyReason), typeof(BookmarkAvailability), typeof(BookmarkBulkDeleteRequest), typeof(BookmarkBulkDeleteResultDto),
                typeof(BookmarkGroupDto), typeof(BookmarkGroupSortBy), typeof(BookmarkItemType), typeof(BookmarkPagesDto), typeof(BookmarkPreferencesDto),
                typeof(BookmarkQueryFilter), typeof(BookmarkShortcutsDto), typeof(BookmarkSortBy), typeof(BookmarkJeImportRequest), typeof(BookmarkJeImportReportDto),
                typeof(BookmarkJeImportUserDto), typeof(BookmarkJeImportSampleDto), typeof(BookmarkJeImportOutcome), typeof(BookmarkJeChecklistItemDto),
                typeof(BookmarkClipItemDto), typeof(BookmarkClipQueryFilter), typeof(BookmarkClipSortBy), typeof(BookmarkClipState), typeof(BookmarkClipSummaryDto),
                typeof(BookmarkClipStatusDto), typeof(BookmarkClipBulkDeleteRequest), typeof(BookmarkClipBulkDeleteResultDto),
                typeof(BookmarkClipRenameRequest), typeof(BookmarkClipRenameFileRequest), typeof(BookmarkClipFolderCheckRequest),
                typeof(BookmarkAdminClipItemDto), typeof(BookmarkAdminClipSummaryDto), typeof(BookmarkAdminClipUserDto)
            ],
            surface.OfRole(PublicRole.Dto));
        // The facades: 1.1.0's move hook, and 1.2.0's clip folders for Library Maintenance's Duplicates.
        AssertTypes(
            [typeof(Jellyfin.Plugin.Bookmarks.Integration.ClipRootsV1), typeof(Jellyfin.Plugin.Bookmarks.Integration.ItemMovedHookV1)],
            surface.OfRole(PublicRole.Facade));
        Assert.Empty(surface.Unclassified);

        // The DTO classes are in Api.Models, where the check looks for them; the enums, shared with the records, in Model.
        Assert.Empty(surface.ReachedOutsideModels);

        // The kit's public types are the interfaces the controllers take and the abstract bases of the post-scan and
        // maintenance tasks.
        AssertTypes([typeof(IKitDiagnostics), typeof(KitMaintenanceTaskBase), typeof(KitPostScanTaskBase), typeof(IEmbeddedScript), typeof(IKitPages)], surface.KitPublicTypes);
    }

    [Fact]
    public void RecordsStoreMapperAndImplementations_AreInternal()
    {
        // The internal list: the records (they hold the kit's internal IdentitySnapshot), the store, the mapper and
        // every service implementation; and the exceptions, which the kit's filter maps inside the plugin.
        Type[] internals =
        [
            typeof(Bookmark), typeof(ClipInfo), typeof(BookmarkStoreDocument), typeof(BookmarkImportLedgerEntry), typeof(BookmarkUserPreferences),
            typeof(PendingFileDelete), typeof(IBookmarkStore), typeof(BookmarkStore), typeof(BookmarkStoreMigration1To2), typeof(BookmarkDtoMapper),
            typeof(BookmarkManager), typeof(BookmarkRecordRules), typeof(BookmarkNames), typeof(BookmarkIdentityDemo), typeof(BookmarkUserDataDemo),
            typeof(IClipExtractionService), typeof(ClipExtractionService), typeof(IClipPlanner), typeof(ClipPlanner), typeof(BookmarkException),
            typeof(ClipExtractionException), typeof(IPluginConfigurationProvider), typeof(BookmarkPaths), typeof(BookmarksKit), typeof(BackgroundWriteGate),
            typeof(BackgroundWriter),

            // The clip file operations, their guard and results, the janitor, and the import check.
            typeof(ClipFileOps), typeof(ClipFileGuard), typeof(ClipPathCheck), typeof(ClipPathVerdict), typeof(ClipFileOutcome), typeof(ClipFileResult),
            typeof(IClipFileSystem), typeof(PhysicalClipFileSystem), typeof(PartialFileJanitor), typeof(PendingFileDeleteRetry), typeof(BookmarkImportAdapter),

            // The clip file names, the clip folder listing and the janitor's results.
            typeof(ClipNamePattern), typeof(ClipFolderFile), typeof(StalePartialCleanup), typeof(JanitorRun),

            // The developer library change demo, the kit's monitor and its consumer interface.
            typeof(BookmarkLibraryChangeDemo), typeof(LibraryChangeMonitor), typeof(ILibraryChangeConsumer),

            // The developer library demo, its records and the clock, and the kit's record lifecycle and library reads.
            typeof(BookmarkLibraryDemo), typeof(LibraryDemoRecords), typeof(LibraryDemoRecord), typeof(LibraryDemoDocument), typeof(DeveloperClock),
            typeof(AttachedRecordVerifier), typeof(IAttachedRecordOwner), typeof(IDetachedRecordOwner), typeof(DetachedRecords), typeof(DetachedArchive),
            typeof(LibraryAccess), typeof(FreshUserData), typeof(VersionSets), typeof(KitMaintenance), typeof(IKitMaintenanceStep),

            // The identity service, the store's user data owner, the admin actions and their results, the maintenance
            // steps, and the kit's users services.
            typeof(BookmarkIdentityService), typeof(IdentityPassResult), typeof(BookmarkUserDataOwner), typeof(BookmarkRecordsAdmin),
            typeof(Jellyfin.Plugin.Bookmarks.Tasks.BookmarkMaintenanceSteps), typeof(Jellyfin.Plugin.Kit.Users.UserReconciler), typeof(Jellyfin.Plugin.Kit.Users.IUserDataOwner),

            // The My Bookmarks page's lists, the preferences, and the kit's query-string binders.
            typeof(BookmarkQueryService), typeof(BookmarkPreferencesService), typeof(Jellyfin.Plugin.Kit.Api.CommaSeparatedQueryAttribute),
            typeof(Jellyfin.Plugin.Kit.Api.StrictEnumQueryAttribute),

            // The Jellyfin Enhanced import, its plan and the files it read, the store's run entries, and the kit's reader.
            typeof(Jellyfin.Plugin.Bookmarks.Import.JellyfinEnhancedBookmarkImporter), typeof(Jellyfin.Plugin.Bookmarks.Import.JeImportPlanner),
            typeof(Jellyfin.Plugin.Bookmarks.Import.JeImportPlan), typeof(Jellyfin.Plugin.Bookmarks.Import.JeImportSource),
            typeof(Jellyfin.Plugin.Bookmarks.Import.JeEntryResolution), typeof(BookmarkImportRun), typeof(Jellyfin.Plugin.Kit.Interop.JellyfinEnhancedReader),

            // The My Clips page's service, its list and rows, the clip files' kept states, the libraries' folders, the
            // file-first bulk delete the two bulk deletes share, and the guard's read-only calls.
            typeof(ClipService), typeof(ClipQueryService), typeof(ClipRow), typeof(ClipFacts), typeof(ClipPage), typeof(ClipStateCache),
            typeof(ClipLibraryLocations), typeof(ClipLibraryLocation), typeof(ClipFileBatchDelete), typeof(ClipFileBatchOutcome), typeof(IClipPathProbe),

            // The missing-clip sweep and its result, and the facade's source (the facade itself is public); the clip folder
            // markers, their states and the confirmation of a folder.
            typeof(MissingClipSweep), typeof(MissingClipSweepResult), typeof(Jellyfin.Plugin.Bookmarks.Integration.ClipRootsFacadeService),
            typeof(ClipRootMarker), typeof(ClipRootState), typeof(ClipRootMarking), typeof(ClipFolderLeftOut), typeof(ClipFolderClips), typeof(ClipFolderConfirmation),

            // The admins' clip service, the single delete it shares with the manager, the library as a caller, and the
            // name look-up that failed.
            typeof(AdminClipService), typeof(ClipFileSingleDelete), typeof(ClipViewer), typeof(ClipFolderUnreachableException)
        ];

        Assert.All(internals, type => Assert.False(type.IsVisible, type.FullName + " is public."));
    }

    [Fact]
    public void BookmarksClipsController_TakesOnlyPublicInterfaces_AndItsActionsAnswerWithDtos()
    {
        // The My Clips page's controller receives DTOs from IBookmarkClips, as BookmarksController does from the
        // manager, and never touches a record (Boundary_HasNoProblems reads its code).
        var constructor = Assert.Single(typeof(BookmarksClipsController).GetConstructors());
        Assert.Equal([typeof(IBookmarkClips), typeof(MediaBrowser.Controller.Library.IUserManager)], constructor.GetParameters().Select(p => p.ParameterType));

        var answered = ControllerDiscovery.FindActions(typeof(BookmarksClipsController))
            .SelectMany(action => MethodBodyReferences.Expand(action.ReturnType))
            .Where(type => type.Assembly == Assembly)
            .Distinct()
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [typeof(BookmarkClipBulkDeleteResultDto), typeof(BookmarkClipItemDto), typeof(BookmarkClipStatusDto), typeof(BookmarkClipSummaryDto)],
            answered);
    }

    [Fact]
    public void BookmarksAdminClipsController_TakesOnlyPublicInterfaces_AnswersWithDtosWithoutANote_AndRequiresElevation()
    {
        // The admins' clip controller receives DTOs from IBookmarkAdminClips; the admin clip row has no Note member at
        // all (never a note in an admin answer), and every action is behind RequiresElevation (on the class).
        var constructor = Assert.Single(typeof(BookmarksAdminClipsController).GetConstructors());
        Assert.Equal([typeof(IBookmarkAdminClips)], constructor.GetParameters().Select(p => p.ParameterType));

        var answered = ControllerDiscovery.FindActions(typeof(BookmarksAdminClipsController))
            .SelectMany(action => MethodBodyReferences.Expand(action.ReturnType))
            .Where(type => type.Assembly == Assembly)
            .Distinct()
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [typeof(BookmarkAdminClipItemDto), typeof(BookmarkAdminClipSummaryDto), typeof(BookmarkClipBulkDeleteResultDto), typeof(BookmarkClipStatusDto)],
            answered);
        Assert.Null(typeof(BookmarkAdminClipItemDto).GetProperty("Note"));
        Assert.DoesNotContain(typeof(BookmarkAdminClipItemDto).GetProperties(), p => p.PropertyType == typeof(BookmarkClipItemDto));
        var authorize = Assert.Single(typeof(BookmarksAdminClipsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, authorize.Policy);
        Assert.All(
            ControllerDiscovery.FindActions(typeof(BookmarksAdminClipsController)),
            action => Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true)));
    }

    [Fact]
    public void BookmarksController_TakesOnlyPublicInterfaces_AndItsActionsAnswerWithDtos()
    {
        // The API controller receives DTOs from IBookmarkManager: every action answers with a DTO (or a file, a script, or
        // no content), never a record.
        var constructor = Assert.Single(typeof(BookmarksController).GetConstructors());
        Assert.All(constructor.GetParameters(), p => Assert.True(p.ParameterType.IsInterface && p.ParameterType.IsVisible, p.Name));

        var answered = ControllerDiscovery.FindActions(typeof(BookmarksController))
            .SelectMany(action => MethodBodyReferences.Expand(action.ReturnType))
            .Where(type => type.Assembly == Assembly)
            .Distinct()
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [
                typeof(BookmarkBulkDeleteResultDto), typeof(BookmarkCapabilitiesDto), typeof(BookmarkDto), typeof(BookmarkGroupDto),
                typeof(BookmarkPreferencesDto)
            ],
            answered);
    }

    private static void AssertTypes(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(t => t.FullName).Order(StringComparer.Ordinal), actual.Select(t => t.FullName));
}
