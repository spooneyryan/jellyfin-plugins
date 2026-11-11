using System;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// <see cref="KitComponent{T}"/> and <see cref="KitComponents"/>: the kit's record owners and maintenance steps are
/// created one by one, so one that can't be created costs only itself, is logged once, and is tried again; a created
/// one is kept.
/// </summary>
public sealed class KitComponentTests
{
    private readonly ListLogger _logger = new();

    private interface IFx
    {
        string Name { get; }
    }

    [Fact]
    public void AComponent_IsCreatedOnce_AndKept()
    {
        var created = 0;
        using var provider = new ServiceCollection()
            .AddSingleton(new KitComponent<IFx>("one", _ =>
            {
                created++;
                return new Fx("one");
            }))
            .BuildServiceProvider();

        var first = Assert.Single(KitComponents.CreateAll<IFx>(provider, _logger));
        var second = Assert.Single(KitComponents.CreateAll<IFx>(provider, _logger));

        Assert.Same(first, second);
        Assert.Equal(1, created);
        Assert.Empty(KitComponents.Problems<IFx>(provider));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void AComponentThatCantBeCreated_CostsOnlyItself_IsLoggedOnce_AndIsTriedAgain()
    {
        var broken = true;
        var attempts = 0;
        using var provider = new ServiceCollection()
            .AddSingleton(new KitComponent<IFx>("first", _ => new Fx("first")))
            .AddSingleton(new KitComponent<IFx>("flaky", _ =>
            {
                attempts++;
                return broken ? throw new InvalidOperationException("no data folder") : new Fx("flaky");
            }))
            .AddSingleton(new KitComponent<IFx>("last", _ => new Fx("last")))
            .BuildServiceProvider();

        Assert.Equal(new[] { "first", "last" }, KitComponents.CreateAll<IFx>(provider, _logger).Select(f => f.Name));
        Assert.Equal(new[] { "first", "last" }, KitComponents.CreateAll<IFx>(provider, _logger).Select(f => f.Name));

        Assert.Equal(2, attempts);
        var problem = Assert.Single(KitComponents.Problems<IFx>(provider));
        Assert.Equal(("flaky", "no data folder"), ((string?)problem["Name"], (string?)problem["Problem"]));
        var error = Assert.Single(_logger.At(LogLevel.Error));
        Assert.Contains("flaky", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Single(_logger.At(LogLevel.Debug));

        broken = false;
        Assert.Equal(new[] { "first", "flaky", "last" }, KitComponents.CreateAll<IFx>(provider, _logger).Select(f => f.Name));
        Assert.Empty(KitComponents.Problems<IFx>(provider));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void AFactoryThatReturnsNothing_IsAFailure()
    {
        var component = new KitComponent<IFx>("empty", _ => null!);
        using var provider = new ServiceCollection().BuildServiceProvider();

        Assert.Null(component.TryCreate(provider, _logger));
        Assert.Contains("empty", component.Problem, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => component.Create(provider));
    }

    [Fact]
    public void TheArguments_AreChecked()
    {
        Assert.Throws<ArgumentException>(() => new KitComponent<IFx>(string.Empty, _ => new Fx("x")));
        Assert.Throws<ArgumentNullException>(() => new KitComponent<IFx>("x", null!));
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<ArgumentNullException>(() => KitComponents.CreateAll<IFx>(provider, null!));
        Assert.Throws<ArgumentNullException>(() => new KitComponent<IFx>("x", _ => new Fx("x")).Create(null!));
    }

    private sealed class Fx(string name) : IFx
    {
        public string Name { get; } = name;
    }
}
