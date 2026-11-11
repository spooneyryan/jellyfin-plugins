using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Integration;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Storage.Migrations;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The public/internal boundary (the kit's rule and its plugin-side twin), and the SQLite isolation rule on the
/// plugin's own code: only the controllers, the parts Jellyfin creates and the
/// configuration are public in 0.3.0, and no type of the plugin's own names a SQLite type, the controllers' actions above
/// all.
/// </summary>
public sealed class PublicBoundaryTests
{
    private static readonly Assembly Assembly = typeof(Plugin).Assembly;

    [Fact]
    public void Boundary_HasNoProblems()
    {
        var problems = PublicBoundaryChecks.Check(Assembly, UserRatingsKit.Options.SchemaPrefix);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PublicSurface_IsThePinnedSet()
    {
        // A new public type fails here until it is added on purpose, with its role. The store added the maintenance task; the
        // user API the controller's service interface and its DTOs (with the requests and the sort); the lifecycle the
        // post-scan task, Library Maintenance's hook and the admin endpoints' service interface; Detach now its request and
        // answer; moderation its list entry, answer, log row, state and reason; the Settings tab its settings, view, user,
        // report and change.
        var surface = PublicBoundaryChecks.Describe(Assembly, UserRatingsKit.Options.SchemaPrefix, "Jellyfin.Plugin.UserRatings");

        AssertTypes([typeof(UserRatingsAdminController), typeof(UserRatingsController), typeof(UserRatingsDiagnosticsController)], surface.OfRole(PublicRole.Controller));
        AssertTypes([typeof(Plugin), typeof(PluginServiceRegistrator), typeof(RatingsMaintenanceTask), typeof(RatingsPostScanTask)], surface.OfRole(PublicRole.JellyfinPart));
        AssertTypes([typeof(PluginConfiguration)], surface.OfRole(PublicRole.Configuration));
        AssertTypes([typeof(IUserRatingsAdminApi), typeof(IUserRatingsApi)], surface.OfRole(PublicRole.ServiceInterface));
        AssertTypes(
            [
                typeof(UserRatingsAdminEntryDto), typeof(UserRatingsApplyPolicyRequest), typeof(UserRatingsCapabilitiesDto), typeof(UserRatingsDetachedCandidateDto), typeof(UserRatingsDetachedDto), typeof(UserRatingsDisplayDto), typeof(UserRatingsHeldDetachDto), typeof(UserRatingsHeldDetachRequest), typeof(UserRatingsItemCanDto), typeof(UserRatingsItemDto),
                typeof(UserRatingsItemSummaryDto), typeof(UserRatingsJeImportRequest), typeof(UserRatingsJobDto), typeof(UserRatingsMineDto), typeof(UserRatingsModerationDto), typeof(UserRatingsModerationLogDto),
                typeof(UserRatingsModerationReason), typeof(UserRatingsModerationState), typeof(UserRatingsOtherCopyDto), typeof(UserRatingsOwnRowDto),
                typeof(UserRatingsPostersDto), typeof(UserRatingsRatingRequest), typeof(UserRatingsRelinkRequest), typeof(UserRatingsReviewDto), typeof(UserRatingsReviewRequest),
                typeof(UserRatingsReviewSort), typeof(UserRatingsSettingsChangeDto), typeof(UserRatingsSettingsDto), typeof(UserRatingsSettingsReportDto),
                typeof(UserRatingsSettingsUserDto), typeof(UserRatingsSettingsViewDto)
            ],
            surface.OfRole(PublicRole.Dto));
        AssertTypes([typeof(ItemMovedHookV1)], surface.OfRole(PublicRole.Facade));
        Assert.Empty(surface.Unclassified);
        Assert.Empty(surface.ReachedOutsideModels);

        // The kit's public types, compiled in whole: the interfaces controllers take and the abstract task bases.
        AssertTypes([typeof(IEmbeddedScript), typeof(IKitDiagnostics), typeof(IKitPages), typeof(KitMaintenanceTaskBase), typeof(KitPostScanTaskBase)], surface.KitPublicTypes);
    }

    [Fact]
    public void TheServices_AreInternal()
    {
        Type[] internals =
        [
            typeof(UserRatingsKit), typeof(IPluginConfigurationProvider), typeof(PluginConfigurationProvider),
            typeof(IRatingsStore), typeof(SqliteRatingsStore), typeof(RatingsSql), typeof(RatingsSchema), typeof(M001Initial), typeof(RatingValues),
            typeof(RatingRecord), typeof(SummaryRow), typeof(TombstoneRecord), typeof(ModerationLogRecord), typeof(ImportLedgerRecord), typeof(RatingsStoreCounts),
            typeof(ProviderIdsJson), typeof(RatingsRowWarnings),
            typeof(RatingsSettings), typeof(RatingsSettingsReader), typeof(WhoCanRate), typeof(DetailButtonMode), typeof(PosterBadgeContent), typeof(PosterBadgePosition),
            typeof(StarValue), typeof(ReviewText), typeof(IRatingsWriter), typeof(SqliteRatingsWriter), typeof(RatingsChangedEventArgs),
            typeof(RatingSummary), typeof(VisibilityPolicy), typeof(RatingAuthor), typeof(AuthorKind), typeof(SummaryCache), typeof(ItemAccess), typeof(PlayedCheck),
            typeof(CopySiblings), typeof(CopySibling), typeof(RatingsService), typeof(RatingWrite), typeof(ItemRatings), typeof(ItemCapabilities),
            typeof(UserRatingsApi), typeof(UserRatingsAdminApi), typeof(M002AttachedUtc), typeof(RatingsLibraryConsumer), typeof(LibraryAvailability),
            typeof(RatingsSettingsService), typeof(RatingsSettingsFields), typeof(SettingField), typeof(ModerationLogRetention)
        ];

        Assert.All(internals, type => Assert.False(type.IsVisible, type.FullName + " is public."));
    }

    [Fact]
    public void ThePluginsOwnTypes_NameNoSqliteType_ButTheStoresSqlAndItsNoInliningCores()
    {
        // The kit README's "SQLite" isolation rule: a method that names a type from an assembly the server
        // can't bind fails when the JIT compiles it, before any catch of its own. So the controllers, the registrator, the
        // configuration and every service name no SQLite type; the store's SQL lives in RatingsSql (SQLite types in its
        // signatures), called only from SqliteRatingsStore's NoInlining core methods (and their lambdas), which the store's
        // public members call inside the guard that answers the database's 503, and from SqliteRatingsWriter (the unit of
        // work's operations on the open transaction), whose every member is NoInlining and which only the store's write core
        // makes, inside the kit's write.
        var own = Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && ns.StartsWith("Jellyfin.Plugin.UserRatings", StringComparison.Ordinal) && !t.Name.StartsWith('<'))
            .ToList();
        var sqlOwners = new[] { typeof(RatingsSql), typeof(SqliteRatingsStore), typeof(SqliteRatingsWriter) };
        var offenders = own
            .Except(sqlOwners)
            .SelectMany(MethodBodyReferences.DeclaredMethods)
            .Where(NamesSqlite)
            .Select(m => m.DeclaringType!.FullName + "." + m.Name)
            .ToList();
        Assert.Empty(offenders);
        Assert.Contains(typeof(UserRatingsController), own);

        // The store: no SQLite type in a constructor, a field or a member that isn't a NoInlining core (or one of its lambdas).
        var methods = MethodBodyReferences.DeclaredMethods(typeof(SqliteRatingsStore)).ToList();
        var cores = methods.Where(m => !IsGenerated(m) && IsNoInlining(m)).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("WriteCore", cores);
        Assert.Contains("GetSummaryRowsCore", cores);
        var storeOffenders = methods.Where(m => NamesSqlite(m) && !cores.Contains(Owner(m))).Select(m => m.DeclaringType!.Name + "." + m.Name).ToList();
        Assert.Empty(storeOffenders);
        Assert.All(typeof(SqliteRatingsStore).GetConstructors(), c => Assert.DoesNotContain(MethodBodyReferences.FindTypes(c), IsSqlite));
        Assert.DoesNotContain(
            typeof(SqliteRatingsStore).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            f => MethodBodyReferences.Expand(f.FieldType).Any(IsSqlite));
        Assert.All(typeof(IRatingsStore).GetMethods(), m => Assert.DoesNotContain(Signature(m), IsSqlite));
        Assert.All(typeof(IRatingsWriter).GetMethods(), m => Assert.DoesNotContain(Signature(m), IsSqlite));

        // Only the store's cores and the writer call RatingsSql.
        var callingMethods = own.Except([typeof(RatingsSql)])
            .SelectMany(MethodBodyReferences.DeclaredMethods)
            .Where(m => MethodBodyReferences.FindTypes(m).Contains(typeof(RatingsSql)))
            .ToList();
        var callers = callingMethods.Where(m => !IsWriter(m)).Select(m => Owner(m)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(callers);
        Assert.Subset(cores, callers);
        Assert.Contains(callingMethods, IsWriter);
        Assert.All(callingMethods.Where(m => !IsWriter(m)), m => Assert.True(m.DeclaringType == typeof(SqliteRatingsStore) || m.DeclaringType!.DeclaringType == typeof(SqliteRatingsStore), m.DeclaringType!.FullName + "." + m.Name));

        // The writer: every member NoInlining, and only the store's write core makes one.
        var writer = typeof(SqliteRatingsWriter).GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .OfType<MethodBase>()
            .Where(m => !IsGenerated(m) && !(m is MethodInfo { IsSpecialName: true } && m.Name.StartsWith("get_", StringComparison.Ordinal)) && !m.Name.Equals("RequireMember", StringComparison.Ordinal) && !m.Name.Equals("Touch", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(writer);
        Assert.All(writer, m => Assert.True(IsNoInlining(m), "SqliteRatingsWriter." + m.Name + " isn't NoInlining."));
        var makers = own.SelectMany(MethodBodyReferences.DeclaredMethods)
            .Where(m => m.DeclaringType != typeof(SqliteRatingsWriter) && MethodBodyReferences.FindTypes(m).Contains(typeof(SqliteRatingsWriter)))
            .Select(m => Owner(m))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["WriteCore"], makers);

        // Not vacuous: RatingsSql and the cores do name SQLite types.
        Assert.Contains(methods, m => cores.Contains(Owner(m)) && NamesSqlite(m));
        Assert.Contains(MethodBodyReferences.DeclaredMethods(typeof(RatingsSql)), NamesSqlite);
    }

    private static bool IsWriter(MethodBase method) => method.DeclaringType == typeof(SqliteRatingsWriter) || method.DeclaringType!.DeclaringType == typeof(SqliteRatingsWriter);

    private static bool NamesSqlite(MethodBase method) => MethodBodyReferences.FindTypes(method).Any(IsSqlite) || Signature(method).Any(IsSqlite);

    private static bool IsNoInlining(MethodBase method) => (method.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0;

    private static bool IsGenerated(MethodBase method) => method.Name.StartsWith('<') || method.DeclaringType!.Name.StartsWith('<');

    // The member a compiler-generated method belongs to: "<UpsertCore>b__0" in a display class.
    private static string Owner(MethodBase method)
    {
        if (!IsGenerated(method))
        {
            return method.Name;
        }

        var name = method.Name.StartsWith('<') ? method.Name : method.DeclaringType!.Name;
        var end = name.IndexOf('>', StringComparison.Ordinal);
        return end > 1 ? name[1..end] : name;
    }

    private static bool IsSqlite(Type type)
        => type.Namespace is { } ns && (ns.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || ns.StartsWith("SQLitePCL", StringComparison.Ordinal));

    private static IEnumerable<Type> Signature(MethodBase method)
        => method.GetParameters().Select(p => p.ParameterType).Concat(method is MethodInfo info ? [info.ReturnType] : []).SelectMany(MethodBodyReferences.Expand);

    private static void AssertTypes(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(t => t.FullName).Order(StringComparer.Ordinal), actual.Select(t => t.FullName));
}
