using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Diagnostics;

/// <summary>
/// The server side of the health reports (the abuse limits and the ClientHealth validation): forged reports are dropped,
/// one report per user per ten minutes is accepted, and the Warning is logged once per anchor and web version with the
/// de-duplication set capped at 200.
/// </summary>
public sealed class ClientHealthRegistryTests
{
    private static readonly WebAnchorCatalog Catalog = WebAnchorCatalog.Load(typeof(ClientHealthRegistryTests).Assembly, typeof(ClientHealthRegistryTests).Assembly);
    private static readonly Guid Alice = Guid.Parse("a11ce000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0b00000-0000-0000-0000-000000000002");

    private readonly ListLogger _logger = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AValidReport_IsAccepted_Logged_AndListed()
    {
        var registry = Registry();

        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Alice, Body(ClientHealthReportTests.Valid)));

        var warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Equal("Kit Test web anchor fixtureList is missing on the fixture view (web 10.11.5), as reported by a browser; that feature is off there until the web client or the plugin changes. Diagnostics lists the reports", warning.Message);
        var entry = Assert.Single(registry.Entries);
        Assert.Equal(("fixtureList", "fixture", true, "10.11.5", "Jellyfin Web", ClientLayout.Desktop, "1.0.0.0"), (entry.Anchor, entry.View, entry.Required, entry.WebVersion, entry.Client, entry.Layout, entry.PluginVersion));
        Assert.Equal((2L, 1L, 1), (entry.Count, entry.Reports, entry.Users));
        Assert.Equal(_time.GetUtcNow(), entry.FirstUtc);
        Assert.Equal(1, registry.Counts[ClientHealthOutcome.Accepted]);
    }

    [Theory]
    [InlineData("\"Anchor\": \"fixtureList\"", "\"Anchor\": \"forgedAnchor\"")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"10.11.5; DROP TABLE\"")]
    [InlineData("\"Client\": \"Jellyfin Web\"", "\"Client\": \"<img src=x onerror=alert(1)>\"")]
    [InlineData("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"x\"")]
    [InlineData("\"View\": \"fixture\"", "\"View\": \"details\"")]
    public void AForgedReport_IsDropped_WithoutALogLineOrAnEntry_AndDoesNotUseUpTheRateLimit(string field, string forged)
    {
        var registry = Registry();

        Assert.Equal(ClientHealthOutcome.Invalid, registry.Submit(Alice, Body(ClientHealthReportTests.Valid.Replace(field, forged, StringComparison.Ordinal))));

        Assert.Empty(_logger.At(LogLevel.Warning));
        Assert.Empty(registry.Entries);
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains("Dropped", StringComparison.Ordinal));
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Alice, Body(ClientHealthReportTests.Valid)));
        Assert.Equal(1, registry.Counts[ClientHealthOutcome.Invalid]);
    }

    [Fact]
    public void AFlood_IsCutToOneReportPerUserPerTenMinutes()
    {
        var registry = Registry();

        var outcomes = Enumerable.Range(0, 50).Select(_ => registry.Submit(Alice, Body(ClientHealthReportTests.Valid))).ToList();

        Assert.Equal(ClientHealthOutcome.Accepted, outcomes[0]);
        Assert.All(outcomes.Skip(1), o => Assert.Equal(ClientHealthOutcome.RateLimited, o));
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Bob, Body(ClientHealthReportTests.Valid)));
        _time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(ClientHealthOutcome.RateLimited, registry.Submit(Alice, Body(ClientHealthReportTests.Valid)));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Alice, Body(ClientHealthReportTests.Valid)));

        var entry = Assert.Single(registry.Entries);
        Assert.Equal((6L, 3L, 2), (entry.Count, entry.Reports, entry.Users));
        Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Equal(50, registry.Counts[ClientHealthOutcome.RateLimited]);
        Assert.Equal(3, registry.Counts[ClientHealthOutcome.Accepted]);
    }

    [Fact]
    public void TheWarning_IsLoggedOncePerAnchorAndWebVersion()
    {
        var registry = Registry();

        registry.Submit(Alice, Body(ClientHealthReportTests.Valid));
        registry.Submit(Bob, Body(ClientHealthReportTests.Valid.Replace("\"Layout\": \"Desktop\"", "\"Layout\": \"Tv\"", StringComparison.Ordinal)));
        registry.Submit(Guid.NewGuid(), Body(ClientHealthReportTests.Valid.Replace("10.11.5", "10.11.6", StringComparison.Ordinal)));
        registry.Submit(Guid.NewGuid(), Body(Native("Jellyfin Media Player")));
        registry.Submit(Guid.NewGuid(), Body(Native("Jellyfin Media Player")));
        registry.Submit(Guid.NewGuid(), Body(Native("Jellyfin Android")));

        var messages = _logger.At(LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(4, messages.Count);
        Assert.Contains("(web 10.11.5)", messages[0], StringComparison.Ordinal);
        Assert.Contains("(web 10.11.6)", messages[1], StringComparison.Ordinal);
        Assert.Contains("(web unknown (Jellyfin Media Player 1.12.0))", messages[2], StringComparison.Ordinal);
        Assert.Contains("(web unknown (Jellyfin Android 1.12.0))", messages[3], StringComparison.Ordinal);
        Assert.Equal(4, registry.LoggedKeys);
        Assert.Equal(4, registry.Entries.Count);
    }

    [Fact]
    public void TheDeDuplicationSet_StaysAt200_AndSaysSoOnce_WhileDiagnosticsKeepsTheLatest200()
    {
        var registry = Registry();

        for (var i = 0; i < 250; i++)
        {
            var outcome = registry.Submit(Guid.NewGuid(), Body(WithWebVersion(ClientHealthReportTests.Valid, $"10.{i / 100}.{i % 100}")));
            Assert.Equal(ClientHealthOutcome.Accepted, outcome);
        }

        Assert.Equal(ClientHealthRegistry.MaxLoggedKeys, registry.LoggedKeys);
        Assert.Equal(201, _logger.At(LogLevel.Warning).Count());
        Assert.Contains("further ones are only listed in Diagnostics", _logger.At(LogLevel.Warning).Last().Message, StringComparison.Ordinal);
        Assert.Equal(ClientHealthRegistry.MaxEntries, registry.Entries.Count);
        Assert.Equal(50, registry.Counts[ClientHealthOutcome.Overflow]);
        // The 50 entries updated longest ago made room for the 50 newest; the log stays at its 200.
        Assert.Equal(
            Enumerable.Range(50, 200).Select(i => $"10.{i / 100}.{i % 100}").Order(StringComparer.Ordinal),
            registry.Entries.Select(e => e.WebVersion!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFullList_MakesRoomWithTheEntryUpdatedLongestAgo_WhateverTheClockSays()
    {
        var registry = Registry();
        for (var i = 0; i < ClientHealthRegistry.MaxEntries; i++)
        {
            Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Guid.NewGuid(), Body(WithWebVersion(ClientHealthReportTests.Valid, $"10.0.{i}"))));
        }

        // Every entry has the same time; a second report of the first one makes it the most recently updated.
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Guid.NewGuid(), Body(WithWebVersion(ClientHealthReportTests.Valid, "10.0.0"))));
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Guid.NewGuid(), Body(WithWebVersion(ClientHealthReportTests.Valid, "10.9.9"))));

        var versions = registry.Entries.Select(e => e.WebVersion).ToList();
        Assert.Equal(ClientHealthRegistry.MaxEntries, versions.Count);
        Assert.Contains("10.9.9", versions);
        Assert.Contains("10.0.0", versions);
        Assert.DoesNotContain("10.0.1", versions);
        Assert.Equal(1, registry.Counts[ClientHealthOutcome.Overflow]);
        Assert.Equal((2L, 2), registry.Entries.Where(e => e.WebVersion == "10.0.0").Select(e => (e.Reports, e.Users)).Single());
    }

    [Fact]
    public void ForgedButValidReports_CannotKeepAGenuineProblemOutOfDiagnostics()
    {
        // Regression, a flood: signed-in users post valid reports with invented web versions until the list is full. Each
        // account names at most four web clients, so filling the list takes 50 accounts, and a genuine report still gets
        // in afterwards, in place of the entry updated longest ago.
        var registry = Registry();
        var forgers = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToList();
        for (var round = 0; round < ClientHealthRegistry.MaxWebClientsPerUser + 1; round++)
        {
            for (var f = 0; f < forgers.Count; f++)
            {
                var outcome = registry.Submit(forgers[f], Body(WithWebVersion(ClientHealthReportTests.Valid, $"9.{f}.{round}")));
                Assert.Equal(round < ClientHealthRegistry.MaxWebClientsPerUser ? ClientHealthOutcome.Accepted : ClientHealthOutcome.TooManyWebClients, outcome);
            }

            _time.Advance(ClientHealthRegistry.RateWindow);
        }

        Assert.Equal(ClientHealthRegistry.MaxEntries, registry.Entries.Count);

        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Alice, Body(ClientHealthReportTests.Valid)));

        var genuine = Assert.Single(registry.Entries, e => e.WebVersion == "10.11.5");
        Assert.Equal(("fixtureList", 1), (genuine.Anchor, genuine.Users));
        Assert.Equal("10.11.5", registry.Entries[0].WebVersion);
        Assert.Equal(60 * ClientHealthRegistry.MaxWebClientsPerUser + 1 - ClientHealthRegistry.MaxEntries, registry.Counts[ClientHealthOutcome.Overflow]);
        Assert.Equal(60, registry.Counts[ClientHealthOutcome.TooManyWebClients]);
    }

    [Fact]
    public void OneUser_NamesAtMostFourWebClients_AndADroppedReportDoesNotUseUpTheRateLimit()
    {
        var registry = Registry();
        string[] clients = ["10.11.1", "10.11.2", "10.11.3", "10.11.4"];
        foreach (var version in clients)
        {
            Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Alice, Body(WithWebVersion(ClientHealthReportTests.Valid, version))));
            _time.Advance(ClientHealthRegistry.RateWindow);
        }

        Assert.Equal(ClientHealthOutcome.TooManyWebClients, registry.Submit(Alice, Body(WithWebVersion(ClientHealthReportTests.Valid, "10.11.5"))));
        Assert.Equal(ClientHealthOutcome.TooManyWebClients, registry.Submit(Alice, Body(Native("Jellyfin Media Player"))));
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Alice, Body(WithWebVersion(ClientHealthReportTests.Valid, "10.11.1"))));
        Assert.Equal(ClientHealthOutcome.Accepted, registry.Submit(Bob, Body(WithWebVersion(ClientHealthReportTests.Valid, "10.11.5"))));

        Assert.Equal(["10.11.1", "10.11.2", "10.11.3", "10.11.4", "10.11.5"], registry.Entries.Select(e => e.WebVersion!).Order(StringComparer.Ordinal));
        Assert.Equal(5, _logger.At(LogLevel.Warning).Count());
        Assert.Equal(2, registry.Counts[ClientHealthOutcome.TooManyWebClients]);
        Assert.Contains(_logger.At(LogLevel.Debug), e => e.Message.Contains("at most 4 web clients", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnchorListWithProblems_IsLoggedAsAnError_AndItsBrokenAnchorsAreUnknown()
    {
        var catalog = WebAnchorCatalog.Parse("""{ "anchors": [] }""", """{ "anchors": [ { "name": "fixtureList", "css": [], "required": true, "views": ["fixture"], "note": "n" } ] }""");

        var registry = new ClientHealthRegistry(WebKit.Options(), catalog, WebKit.LoggerFactory(_logger), _time);

        Assert.Single(_logger.At(LogLevel.Error));
        Assert.Equal(ClientHealthOutcome.Invalid, registry.Submit(Alice, Body(ClientHealthReportTests.Valid)));
    }

    [Fact]
    public void TheLoggerCategory_NamesThePlugin()
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger);

        _ = new ClientHealthRegistry(WebKit.Options(), Catalog, factory.Object, _time);

        factory.Verify(f => f.CreateLogger("Kit.Tests.Kit.ClientHealth"), Times.Once);
    }

    private static ReadOnlyMemory<byte> Body(string json) => Encoding.UTF8.GetBytes(json);

    private static string WithWebVersion(string json, string version)
        => json.Replace("\"WebVersion\": \"10.11.5\"", $"\"WebVersion\": \"{version}\"", StringComparison.Ordinal);

    private static string Native(string client)
        => ClientHealthReportTests.Valid
            .Replace("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": null", StringComparison.Ordinal)
            .Replace("Jellyfin Web", client, StringComparison.Ordinal)
            .Replace("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"1.12.0\"", StringComparison.Ordinal);

    private ClientHealthRegistry Registry() => new(WebKit.Options(), Catalog, WebKit.LoggerFactory(_logger), _time);
}
