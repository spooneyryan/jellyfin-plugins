using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Rules;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// The manifest reader against MVC itself: the fixture controllers of <c>ReaderFixtures.cs</c>, one per rule
/// the reader copies from ASP.NET Core, read from this test assembly's metadata by <see cref="JellyfinApiMetadata"/> and
/// built into action descriptors by MVC's real application model (controller discovery, actions, selectors, routes,
/// names), which <see cref="ServerActions"/> turns into the same shapes the running server's Diagnostics compares. Both
/// readers must give the same actions, so neither can drift from MVC unseen.
/// </summary>
public sealed class JellyfinApiMetadataTests
{
    private const string FixtureNamespace = "Jellyfin.Plugin.AccessRoles.Tests.Rules.ReaderFixtures.";

    private static readonly Lazy<IReadOnlyList<ApiActionShape>> FromMetadata = new(() =>
        JellyfinApiMetadata.Read(typeof(JellyfinApiMetadataTests).Assembly.Location).Actions.Where(IsFixture).ToList());

    private static readonly Lazy<IReadOnlyList<ApiActionShape>> FromMvc = new(ReadThroughMvc);

    [Fact]
    public void TheMetadataReader_AndMvc_FindTheSameActions()
    {
        Assert.Equal(FromMvc.Value.Select(a => a.Key), FromMetadata.Value.Select(a => a.Key));
    }

    [Fact]
    public void TheMetadataReader_AndMvc_AgreeOnEveryActionsShape()
    {
        Assert.Equal(FromMvc.Value.Select(Describe).Order(StringComparer.Ordinal), FromMetadata.Value.Select(Describe).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AMethodHiddenWithNew_IsTwoActionsOfOneName_AsMvcHasThem()
    {
        // Type.GetMethods() lists the base method a derived "new" method hides, so MVC builds both; the manifest can't tell
        // two actions of one name apart and reports them (ActionManifest.Compare's Duplicates).
        Assert.Equal(2, FromMvc.Value.Count(a => a.Key.EndsWith(".ChildController.HiddenLater", StringComparison.Ordinal)));
        Assert.Equal(2, FromMetadata.Value.Count(a => a.Key.EndsWith(".ChildController.HiddenLater", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheFixtures_CoverWhatTheyClaim()
    {
        // Guards the two tests above against agreeing on nothing.
        var actions = FromMetadata.Value
            .Where(a => !a.Key.EndsWith(".ChildController.HiddenLater", StringComparison.Ordinal))
            .ToDictionary(a => a.Key[FixtureNamespace.Length..], StringComparer.Ordinal);
        Assert.Equal(
            ["GET Fixture/Parent/hidden", "GET Fixture/Parent/new"],
            FromMetadata.Value.Where(a => a.Key.EndsWith(".ChildController.HiddenLater", StringComparison.Ordinal)).SelectMany(a => a.Routes).Order(StringComparer.Ordinal));

        Assert.Equal(["GET Fixture/Tokens/GetThing/{id}"], actions["TokensController.GetThing"].Routes);
        Assert.Equal(["GET Fixture/Tokens/Later"], actions["TokensController.LoadLater"].Routes);
        Assert.Equal(["POST Fixture/Tokens/Renamed"], actions["TokensController.Renamed"].Routes);
        Assert.Equal(["GET Fixture/a", "HEAD Fixture/a"], actions["MixedController.GetAndHead"].Routes);
        Assert.Equal(["GET Fixture/r"], actions["MixedController.RouteWithSilentGet"].Routes);
        Assert.Equal(["GET Fixture", "POST Fixture/p"], actions["MixedController.SilentAndDefining"].Routes);
        Assert.Equal(["GET,POST Fixture/verbs"], actions["MixedController.Verbs"].Routes);
        Assert.Equal(["GET absolute/x"], actions["MixedController.Absolute"].Routes);
        Assert.Equal(["GET tilde/y"], actions["MixedController.Tilde"].Routes);
        Assert.Equal(["PUT Fixture"], actions["MixedController.NamedWithoutTemplate"].Routes);
        Assert.Equal(["DELETE Fixture/d", "PATCH Fixture"], actions["MixedController.OrderedAndSilent"].Routes);
        Assert.Equal(["* Fixture"], actions["MixedController.NoAttributes"].Routes);
        Assert.Equal(["Default", "RequiresElevation"], actions["AuthController.Elevated"].Policies);
        Assert.True(actions["AuthController.Open"].AllowAnonymous);
        Assert.False(actions["AuthController.Plain"].IsAnonymous);
        Assert.True(actions["OpenController.Get"].IsAnonymous);
        Assert.Equal(["GET Fixture/Derived/z"], actions["DerivedController.Z"].Routes);
        Assert.Equal(["FixtureBase"], actions["DerivedController.Z"].Policies);
        Assert.Equal(["GET Fixture/Own/w"], actions["OwnRouteController.W"].Routes);
        Assert.Equal(["GET Fixture/Parent/inherited"], actions["ChildController.Inherited"].Routes);
        Assert.Equal(["GET Fixture/Parent/own"], actions["ChildController.OwnAction"].Routes);
        Assert.Equal(["SeriesId", "parentId", "seasonId"], actions["ScopeController.Scoped"].ScopeParameters);
        Assert.Equal(["parentId", "parentIdNot=other", "seasonId", "series=SeriesId", "unrelated"], actions["ScopeController.Scoped"].Parameters);
        Assert.Equal(
            ["body", "cancellationToken", "count", "header=X-Fixture", "list=values", "services"],
            actions["ParametersController.Every"].Parameters);
        Assert.Equal(["body", "count", "id"], actions["ApiBehaviorController.Inferred"].Parameters);
        Assert.Empty(actions["MixedController.NoAttributes"].Parameters);
        Assert.Equal(["* "], actions["PocoController.Hello"].Routes);
        Assert.Contains("NoSuffixButDerived.Get", actions.Keys);
        Assert.Contains("DisposingController.Get", actions.Keys);

        // No actions: methods MVC skips, and types that aren't controllers.
        Assert.DoesNotContain(actions.Keys, k => k.EndsWith(".NotAnAction", StringComparison.Ordinal) || k.EndsWith(".StaticMethod", StringComparison.Ordinal)
            || k.EndsWith(".GenericMethod", StringComparison.Ordinal) || k.EndsWith(".ToString", StringComparison.Ordinal) || k.EndsWith(".Dispose", StringComparison.Ordinal));
        Assert.DoesNotContain(actions.Keys, k => k.StartsWith("Abstract", StringComparison.Ordinal) || k.StartsWith("Generic", StringComparison.Ordinal)
            || k.StartsWith("Hidden", StringComparison.Ordinal) || k.StartsWith("Internal", StringComparison.Ordinal) || k.StartsWith("FixtureApiBase", StringComparison.Ordinal));
    }

    private static bool IsFixture(ApiActionShape action) => action.Controller.StartsWith(FixtureNamespace, StringComparison.Ordinal);

    private static string Describe(ApiActionShape action)
        => $"{action.Key} | {string.Join("; ", action.Routes)} | {string.Join(",", action.Policies)} | anonymous {action.AllowAnonymous} | {string.Join(",", action.Parameters)}";

    // MVC's own discovery and application model over this assembly: the default ControllerFeatureProvider, nothing hosted.
    private static IReadOnlyList<ApiActionShape> ReadThroughMvc()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            manager.ApplicationParts.Clear();
            manager.ApplicationParts.Add(new AssemblyPart(typeof(JellyfinApiMetadataTests).Assembly));
        });
        using var provider = services.BuildServiceProvider();
        return ServerActions.Read(provider.GetRequiredService<IActionDescriptorCollectionProvider>(), typeof(JellyfinApiMetadataTests).Assembly.GetName().Name!)
            .Where(IsFixture)
            .ToList();
    }
}
