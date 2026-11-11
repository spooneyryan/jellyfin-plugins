using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Tests.Boundary.Good;
using Jellyfin.Plugin.Kit.Tests.Boundary.Good.Api.Models;
using Jellyfin.Plugin.Kit.Tests.Boundary.Good.Integration;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The public/internal boundary check on fixture "plugins"
/// (<c>Fixtures/BoundaryFixtures.cs</c>): one that follows every rule, and one for each way of breaking them.
/// </summary>
public sealed class PublicBoundaryChecksTests
{
    private const string Root = "Jellyfin.Plugin.Kit.Tests.Boundary";
    private static readonly Assembly Assembly = typeof(PublicBoundaryChecksTests).Assembly;

    [Fact]
    public void Good_HasNoProblems()
    {
        Assert.Empty(Check("Good"));
    }

    [Fact]
    public void Good_EveryPublicTypeHasItsRole()
    {
        var surface = PublicBoundaryChecks.Describe(Assembly, "Fxb", Root + ".Good");

        Assert.Equal([typeof(FxbController)], surface.OfRole(PublicRole.Controller));
        Assert.Equal([typeof(FxbPlugin), typeof(FxbRegistrator), typeof(FxbTask)], surface.OfRole(PublicRole.JellyfinPart));
        Assert.Equal([typeof(FxbConfiguration)], surface.OfRole(PublicRole.Configuration));
        Assert.Equal([typeof(IFxbItems)], surface.OfRole(PublicRole.ServiceInterface));
        Assert.Equal([typeof(FxbSummariesV1)], surface.OfRole(PublicRole.Facade));

        // Reached from the actions (FxbItemDto, its FxbState and FxbTagDto, the request), the [FromQuery] model, and the
        // unreached enum with the prefix. The classes are in Good.Api.Models; the enums, like Bookmarks', in Good.
        Assert.Equal(
            new[] { typeof(FxbCreateRequest), typeof(FxbItemDto), typeof(FxbOrigin), typeof(FxbSearchQuery), typeof(FxbState), typeof(FxbTagDto) }.Select(t => t.FullName).Order(StringComparer.Ordinal),
            surface.OfRole(PublicRole.Dto).Select(t => t.FullName));
        Assert.All(
            surface.OfRole(PublicRole.Dto).Where(t => !t.IsEnum),
            t => Assert.Equal(Root + ".Good" + PublicBoundaryChecks.ModelsNamespaceSuffix, t.Namespace));
        Assert.Empty(surface.Unclassified);
        Assert.Empty(surface.ReachedOutsideModels);
    }

    [Fact]
    public void TheKitsPublicTypes_AreTheThreeInterfacesAControllerTakes_AndTheTwoTaskBases()
    {
        // Kit types are internal except interfaces a public constructor needs and abstract bases (the post-scan task's
        // and the maintenance task's, which a plugin's public sealed tasks derive from). The kit is compiled into this
        // assembly as into a plugin; a new public kit type is added here on purpose.
        var surface = PublicBoundaryChecks.Describe(Assembly, "Fxb", Root + ".Good");

        Assert.Equal(
            new[] { typeof(IKitDiagnostics), typeof(Kit.Hosting.KitMaintenanceTaskBase), typeof(Kit.Library.KitPostScanTaskBase), typeof(IEmbeddedScript), typeof(IKitPages) }.Select(t => t.FullName).Order(StringComparer.Ordinal),
            surface.KitPublicTypes.Select(t => t.FullName));
        Assert.All(surface.KitPublicTypes, t => Assert.True(t.IsInterface || (t.IsAbstract && !t.IsSealed)));
    }

    [Fact]
    public void APublicRecord_ReturnedByTheService_IsReported()
    {
        var problems = Check("Bad.PublicRecord");

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith(Root + ".Bad.PublicRecord.FxbRecord is public, but it is none", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("IFxbItems.Get (return type) uses " + Root + ".Bad.PublicRecord.FxbRecord (not part of the public surface)", StringComparison.Ordinal)
            && p.Contains("service interfaces use only DTOs", StringComparison.Ordinal));
    }

    [Fact]
    public void APublicRecordAndStore_ReturnedByActionsAndTheService_AreReported()
    {
        // Swashbuckle reaches them, and the record carries the prefix, but they live outside Api.Models, so neither is a DTO.
        var problems = Check("Bad.ReachedRecord");

        Assert.True(problems.Count == 6, string.Join(Environment.NewLine, problems));
        Assert.Equal(
            [Root + ".Bad.ReachedRecord.Model.FxbRecord", Root + ".Bad.ReachedRecord.Storage.FxbStore"],
            PublicBoundaryChecks.Describe(Assembly, "Fxb", Root + ".Bad.ReachedRecord").ReachedOutsideModels.Select(t => t.FullName));
        foreach (var type in new[] { "Model.FxbRecord", "Storage.FxbStore" })
        {
            Assert.Contains(problems, p => p.StartsWith(Root + ".Bad.ReachedRecord." + type + " is public and a controller action reaches it", StringComparison.Ordinal)
                && p.Contains("DTOs live in " + Root + ".Bad.ReachedRecord.Api.Models", StringComparison.Ordinal));
        }

        Assert.Contains(problems, p => p.Contains("FxbReachedRecordController.Get (return type) uses " + Root + ".Bad.ReachedRecord.Model.FxbRecord (not part of the public surface)", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("FxbReachedRecordController.Export (return type) uses " + Root + ".Bad.ReachedRecord.Storage.FxbStore (not part of the public surface)", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("IFxbItems.Get (return type) uses " + Root + ".Bad.ReachedRecord.Model.FxbRecord (not part of the public surface)", StringComparison.Ordinal)
            && p.Contains("service interfaces use only DTOs", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("IFxbItems.Store uses " + Root + ".Bad.ReachedRecord.Storage.FxbStore (not part of the public surface)", StringComparison.Ordinal));
    }

    [Fact]
    public void AKitType_InAServiceInterface_IsReported()
    {
        var problem = Assert.Single(Check("Bad.KitInService"));

        Assert.Contains("IFxbItems.Pages (return type) uses the kit's " + typeof(IKitPages).FullName, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AController_ReadingTheStore_IsReported_ThroughItsStateMachineAndClosure()
    {
        // Every signature is public; only the code (an async state machine and a lambda closure) touches the internals.
        var problems = Check("Bad.ControllerReadsStore");

        Assert.Equal(
            ["FxbMapper", "FxbRecord", "FxbStore"],
            problems.Select(p => p.Split("uses the plugin's internal ", StringSplitOptions.None)[1].Split(';')[0].Split('.').Last()).Order(StringComparer.Ordinal));
        Assert.All(problems, p => Assert.StartsWith(Root + ".Bad.ControllerReadsStore.FxbReadsStoreController's code uses", p, StringComparison.Ordinal));
    }

    [Fact]
    public void AConcreteClass_InAPublicConstructor_IsReported()
    {
        var problems = Check("Bad.ConcreteInConstructor");

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith(Root + ".Bad.ConcreteInConstructor.FxbItems is public, but it is none", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("FxbConcreteController's constructor (items) uses", StringComparison.Ordinal)
            && p.Contains("a public constructor takes the plugin's service interfaces", StringComparison.Ordinal));
    }

    [Fact]
    public void AServiceInterface_InADto_IsReported()
    {
        var problem = Assert.Single(Check("Bad.DtoHoldsService"));

        Assert.Contains("FxbItemDto.Items uses " + Root + ".Bad.DtoHoldsService.IFxbItems (a service interface)", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicTypesNobodyNeeds_AreReported()
    {
        var problems = Check("Bad.Unused");

        Assert.Equal(
            [Root + ".Bad.Unused.FxbHelpers", Root + ".Bad.Unused.IFxbHelper"],
            problems.Select(p => p.Split(" is public", StringSplitOptions.None)[0]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFacade_WithJellyfinTypesOrDtos_IsReported()
    {
        var problems = Check("Bad.Facade");

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("FxbLookupV1.Find(user) uses Jellyfin.Database.Implementations.Entities.User", StringComparison.Ordinal)
            && p.Contains("use BCL types only.", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("FxbLookupV1.Find (return type) uses " + Root + ".Bad.Facade.Api.Models.FxbItemDto (a DTO)", StringComparison.Ordinal));
    }

    [Fact]
    public void AProtectedNestedType_InAnUnsealedController_IsReported()
    {
        var problem = Assert.Single(Check("Bad.ProtectedNested"));

        Assert.Contains("FxbProtectedController.MakePart (return type) exposes", problem, StringComparison.Ordinal);
        Assert.Contains("isn't visible outside the assembly", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodBodyReferences_SeeLocalsCallsAndGenericArguments()
    {
        var method = typeof(FxbItems).GetMethod(nameof(FxbItems.CreateAsync))!;

        var types = MethodBodyReferences.FindTypes(method).ToHashSet();

        Assert.Contains(typeof(FxbRecord), types);
        Assert.Contains(typeof(FxbMapper), types);
        Assert.Contains(typeof(FxbItemDto), types);
        Assert.Contains(typeof(List<FxbRecord>), types);
        Assert.Contains(typeof(System.Threading.Tasks.Task), types);
    }

    [Fact]
    public void Expand_ListsGenericArgumentsAndElementTypes()
    {
        var types = MethodBodyReferences.Expand(typeof(Dictionary<Guid, FxbItemDto[]>)).ToList();

        Assert.Contains(typeof(Guid), types);
        Assert.Contains(typeof(FxbItemDto), types);
        Assert.Contains(typeof(Dictionary<,>), types);
        Assert.DoesNotContain(typeof(FxbItemDto[]), types);
    }

    private static List<string> Check(string name) => PublicBoundaryChecks.Check(Assembly, "Fxb", Root + "." + name).ToList();
}
