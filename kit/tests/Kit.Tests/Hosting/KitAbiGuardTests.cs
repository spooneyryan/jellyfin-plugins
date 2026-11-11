using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// KitAbiGuard: 10.11.5 and 10.11.11 accepted by a 10.11 build, 12.1 rejected with nothing
/// registered but KitAbiStatus, and a throwing version check counted as a mismatch.
/// </summary>
public sealed class KitAbiGuardTests
{
    [Theory]
    [InlineData("10.11", "10.11.5", true)]
    [InlineData("10.11", "10.11.11", true)]
    [InlineData("10.11", "10.11.0.0", true)]
    [InlineData("10.11", "10.10.7", false)]
    [InlineData("10.11", "10.12.0", false)]
    [InlineData("10.11", "12.1.0", false)]
    [InlineData("10.11", "11.11.0", false)]
    [InlineData("12", "12.0.0", true)]
    [InlineData("12", "12.1.0", true)]
    [InlineData("12", "12.9.3", true)]
    [InlineData("12", "10.11.5", false)]
    [InlineData("12", "13.0.0", false)]
    [InlineData("", "10.11.5", false)]
    [InlineData("10.x", "10.11.5", false)]
    [InlineData("10.11.5", "10.11.5", false)]
    [InlineData("+10.11", "10.11.5", false)]
    public void Accepts_MatchesTheTargetLine(string target, string server, bool expected)
    {
        Assert.Equal(expected, KitAbiGuard.Accepts(target, Version.Parse(server)));
    }

    [Fact]
    public void ThisBuild_TargetsJellyfin1011()
    {
        // kit/build/Kit.props writes JellyfinTarget into the assembly metadata.
        Assert.Equal("10.11", KitBuild.JellyfinTarget);
        Assert.Equal(System.IO.File.ReadAllText(System.IO.Path.Combine(TestPaths.KitRoot, "VERSION")).Trim(), KitBuild.KitVersion);
    }

    [Theory]
    [InlineData("10.11.5")]
    [InlineData("10.11.11")]
    public void Register_OnA1011Server_ReturnsTrueAndRegistersOnlyTheStatus(string server)
    {
        var services = new ServiceCollection();
        var errors = new List<(string Category, string Message)>();

        var active = KitAbiGuard.Register(services, Host(server), TestKit.Options, (c, m) => errors.Add((c, m)));

        Assert.True(active);
        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.True(status.IsActive);
        Assert.Equal("10.11", status.Target);
        Assert.Equal(server, status.Server);
        Assert.Empty(errors);
    }

    [Fact]
    public void Register_OnA121Server_RegistersNothingButTheStatusAndLogsOneError()
    {
        var services = new ServiceCollection();
        var errors = new List<(string Category, string Message)>();

        var active = KitAbiGuard.Register(services, Host("12.1.0"), TestKit.Options, (c, m) => errors.Add((c, m)));

        Assert.False(active);
        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(KitAbiStatus), descriptor.ServiceType);
        var status = Assert.IsType<KitAbiStatus>(descriptor.ImplementationInstance);
        Assert.False(status.IsActive);
        Assert.Equal("12.1.0", status.Server);

        var (category, message) = Assert.Single(errors);
        Assert.Equal("Jellyfin.Plugin.KitTest.Kit.AbiGuard", category);
        Assert.Equal(
            "Kit Test is built for Jellyfin 10.11, but the server is Jellyfin 12.1.0: the plugin is inactive. Install the Kit Test build for Jellyfin 12.",
            message);
        Assert.Equal(message, status.Message);
    }

    [Fact]
    public void Register_WhenTheVersionCheckThrows_CountsAsAMismatch()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Throws(new MissingMethodException("IApplicationHost", "get_ApplicationVersion"));
        var services = new ServiceCollection();
        var errors = new List<string>();

        var active = KitAbiGuard.Register(services, host.Object, TestKit.Options, (_, m) => errors.Add(m));

        Assert.False(active);
        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.False(status.IsActive);
        Assert.Null(status.Server);
        Assert.Contains("MissingMethodException", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Register_WithoutAServerVersion_CountsAsAMismatch()
    {
        var host = new Mock<IServerApplicationHost>();
        var services = new ServiceCollection();

        Assert.False(KitAbiGuard.Register(services, host.Object, TestKit.Options, (_, _) => { }));
        Assert.False(Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance).IsActive);
    }

    [Fact]
    public void Evaluate_WhenTheTargetCantBeRead_CountsAsAMismatch()
    {
        var status = KitAbiGuard.Evaluate("Kit Test", () => throw new InvalidOperationException("No metadata."), () => new Version(10, 11, 5));

        Assert.False(status.IsActive);
        Assert.Equal("unknown", status.Target);
        Assert.Contains("No metadata.", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_A12BuildOnA1011Server_NamesThe1011Line()
    {
        var status = KitAbiGuard.Evaluate("Kit Test", () => "12", () => new Version(10, 11, 5, 0));

        Assert.False(status.IsActive);
        Assert.Equal("10.11.5", status.Server);
        Assert.EndsWith("Install the Kit Test build for Jellyfin 10.11.", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_NeverThrows()
    {
        // RegisterServices must never throw, even when the logger or the arguments fail.
        Assert.False(KitAbiGuard.Register(new ServiceCollection(), Host("12.1.0"), TestKit.Options, (_, _) => throw new InvalidOperationException()));
        Assert.False(KitAbiGuard.Register(new ServiceCollection(), null!, TestKit.Options, (_, _) => { }));
        Assert.False(KitAbiGuard.Register(null!, Host("10.11.5"), TestKit.Options, (_, _) => { }));
        Assert.False(KitAbiGuard.Register(new ServiceCollection(), Host("10.11.5"), null!, (_, _) => { }));
    }

    [Fact]
    public void Register_WithTheDefaultLogger_DoesNotThrow()
    {
        var services = new ServiceCollection();

        Assert.False(KitAbiGuard.Register(services, Host("12.1.0"), TestKit.Options));
        Assert.Single(services);
    }

    [Fact]
    public void IsActive_ReadsTheRegisteredStatus()
    {
        Assert.True(KitAbiGuard.IsActive(Provider(new KitAbiStatus("K", "10.11", "10.11.5", true, "Active."))));
        Assert.False(KitAbiGuard.IsActive(Provider(new KitAbiStatus("K", "10.11", "12.1.0", false, "Inactive."))));
        Assert.False(KitAbiGuard.IsActive(new ServiceCollection().BuildServiceProvider()));
    }

    [Fact]
    public void Status_Summary_IsTheDiagnosticsShape()
    {
        var summary = new KitAbiStatus("K", "10.11", "12.1.0", false, "Inactive.").ToSummary();

        Assert.Equal(["Target", "Server", "Active"], summary.Keys.ToArray());
        Assert.Equal(false, summary["Active"]);
    }

    private static IServiceProvider Provider(KitAbiStatus status) => new ServiceCollection().AddSingleton(status).BuildServiceProvider();

    private static IServerApplicationHost Host(string version)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        return host.Object;
    }
}
