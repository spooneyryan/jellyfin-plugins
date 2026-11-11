using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Kit.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Diagnostics;

/// <summary>
/// The ClientHealth body's validation:
/// every field is checked, nothing is trusted, and a report that breaks a rule is dropped.
/// </summary>
public sealed class ClientHealthReportTests
{
    private static readonly WebAnchorCatalog Catalog = WebAnchorCatalog.Load(typeof(ClientHealthReportTests).Assembly, typeof(ClientHealthReportTests).Assembly);

    public const string Valid = """
        { "WebVersion": "10.11.5", "Client": "Jellyfin Web", "ClientVersion": "10.11.5", "Layout": "Desktop", "Experimental": false,
          "PluginVersion": "1.0.0.0", "Missing": [ { "Anchor": "fixtureList", "View": "fixture", "Count": 2 } ] }
        """;

    // Versions in other scripts' digits, which .NET's \d matches and the kit's JavaScript \d doesn't: 10.11.5 in
    // Arabic-Indic digits, 10.11.5 with a fullwidth 5, 10.11.5 with Devanagari 1s, and 1.0 in Arabic-Indic digits.
    private const string ArabicIndicVersion = "\U00000661\U00000660.\U00000661\U00000661.\U00000665";
    private const string FullwidthVersion = "10.11.\U0000FF15";
    private const string DevanagariVersion = "10.\U00000967\U00000967.5";
    private const string ArabicIndicPluginVersion = "\U00000661.\U00000660";

    [Fact]
    public void AReportAsTheKitSendsIt_IsAccepted()
    {
        Assert.True(Parse(Valid, out var report, out var reason), reason);

        Assert.Equal("10.11.5", report!.WebVersion);
        Assert.Equal("Jellyfin Web", report.Client);
        Assert.Equal("10.11.5", report.ClientVersion);
        Assert.Equal(ClientLayout.Desktop, report.Layout);
        Assert.False(report.Experimental);
        Assert.Equal("1.0.0.0", report.PluginVersion);
        Assert.Equal([new ClientHealthMissing("fixtureList", "fixture", 2)], report.Missing);
        Assert.Equal("10.11.5", report.WebKey);
        Assert.Equal("web 10.11.5", report.WebText);
    }

    [Fact]
    public void WebVersionNull_WithAValidClient_IsAccepted_AndKeyedOnTheClient()
    {
        // A native app wrapper (window.NativeShell), where appVersion() is the app's own version.
        Assert.True(Parse(Valid.Replace("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": null", StringComparison.Ordinal).Replace("Jellyfin Web", "Jellyfin Media Player", StringComparison.Ordinal).Replace("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"1.12.0\"", StringComparison.Ordinal), out var report, out var reason), reason);

        Assert.Null(report!.WebVersion);
        Assert.Equal("native:Jellyfin Media Player", report.WebKey);
        Assert.Equal("web unknown (Jellyfin Media Player 1.12.0)", report.WebText);
    }

    [Theory]
    [InlineData("{ \"Client\": \"Jellyfin Web\", \"ClientVersion\": \"10.11.5\", \"Layout\": \"Tv\", \"Missing\": [ { \"Anchor\": \"fixtureButton\", \"View\": \"other\" } ] }")]
    [InlineData("{ \"webversion\": \"10.11.5.123\", \"client\": \"Jellyfin Web (Chrome) 1+1_x-y\", \"clientversion\": \"1.2.3\", \"layout\": \"Mobile\", \"experimental\": true, \"missing\": [ { \"anchor\": \"kit.f1Host\", \"view\": \"pages\", \"count\": 1000 } ], \"Unknown\": { \"x\": 1 } }")]
    public void OptionalFields_CaseAndUnknownFields_AreFine(string json)
    {
        Assert.True(Parse(json, out var report, out var reason), reason);
        Assert.Single(report!.Missing);
    }

    [Theory]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"10.11\"", "WebVersion")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"10.11.5-beta\"", "WebVersion")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"<script>\"", "WebVersion")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": 10", "WebVersion")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"1000.11.5\"", "WebVersion")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"" + ArabicIndicVersion + "\"", "WebVersion")]
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"\\u0661\\u0660.\\u0661\\u0661.\\u0665\"", "WebVersion")] // the same, as JSON escapes
    [InlineData("\"WebVersion\": \"10.11.5\"", "\"WebVersion\": \"" + FullwidthVersion + "\"", "WebVersion")]
    [InlineData("\"Client\": \"Jellyfin Web\"", "\"Client\": \"Jellyfin<Web>\"", "Client")]
    [InlineData("\"Client\": \"Jellyfin Web\"", "\"Client\": \"\"", "Client")]
    [InlineData("\"Client\": \"Jellyfin Web\"", "\"Client\": \"Jellyfin Web\\n\"", "Client")]
    [InlineData("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"10.11.5\\n\"", "ClientVersion")]
    [InlineData("\"Client\": \"Jellyfin Web\"", "\"Client\": \"Jellyfin\\nWeb\"", "Client")]
    [InlineData("\"Client\": \"Jellyfin Web\"", "\"Client\": \"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"", "Client")]
    [InlineData("\"Client\": \"Jellyfin Web\", ", "", "Client")]
    [InlineData("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"latest\"", "ClientVersion")]
    [InlineData("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": null", "ClientVersion")]
    [InlineData("\"ClientVersion\": \"10.11.5\"", "\"ClientVersion\": \"" + ArabicIndicVersion + "\"", "ClientVersion")]
    [InlineData("\"Layout\": \"Desktop\"", "\"Layout\": \"desktop\"", "Layout")]
    [InlineData("\"Layout\": \"Desktop\"", "\"Layout\": \"1\"", "Layout")]
    [InlineData("\"Layout\": \"Desktop\"", "\"Layout\": \"Watch\"", "Layout")]
    [InlineData("\"Experimental\": false", "\"Experimental\": \"no\"", "Experimental")]
    [InlineData("\"PluginVersion\": \"1.0.0.0\"", "\"PluginVersion\": \"one\"", "PluginVersion")]
    [InlineData("\"PluginVersion\": \"1.0.0.0\"", "\"PluginVersion\": \"" + ArabicIndicPluginVersion + "\"", "PluginVersion")]
    [InlineData("\"PluginVersion\": \"1.0.0.0\"", "\"PluginVersion\": \"" + DevanagariVersion + "\"", "PluginVersion")]
    [InlineData("\"Anchor\": \"fixtureList\"", "\"Anchor\": \"unknownAnchor\"", "doesn't declare")]
    [InlineData("\"Anchor\": \"fixtureList\"", "\"Anchor\": \"FixtureList\"", "doesn't declare")]
    [InlineData("\"View\": \"fixture\"", "\"View\": \"other\"", "doesn't declare")]
    [InlineData("\"View\": \"fixture\"", "\"View\": 1", "doesn't declare")]
    [InlineData("\"Count\": 2", "\"Count\": 0", "Count")]
    [InlineData("\"Count\": 2", "\"Count\": 1001", "Count")]
    [InlineData("\"Count\": 2", "\"Count\": 1.5", "Count")]
    [InlineData("\"Count\": 2", "\"Count\": \"2\"", "Count")]
    [InlineData("[ { \"Anchor\": \"fixtureList\", \"View\": \"fixture\", \"Count\": 2 } ]", "[]", "Missing")]
    [InlineData("[ { \"Anchor\": \"fixtureList\", \"View\": \"fixture\", \"Count\": 2 } ]", "{}", "Missing")]
    [InlineData("[ { \"Anchor\": \"fixtureList\", \"View\": \"fixture\", \"Count\": 2 } ]", "[ { \"Anchor\": \"fixtureList\", \"View\": \"fixture\" }, { \"Anchor\": \"fixtureList\", \"View\": \"fixture\" } ]", "twice")]
    [InlineData("\"Layout\": \"Desktop\"", "\"Layout\": \"Desktop\", \"layout\": \"Tv\"", "twice")]
    public void AReportThatBreaksARule_IsDropped(string field, string forged, string reasonPart)
    {
        var json = Valid.Replace(field, forged, StringComparison.Ordinal);
        Assert.NotEqual(Valid, json);

        Assert.False(Parse(json, out var report, out var reason));
        Assert.Null(report);
        Assert.Contains(reasonPart, reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{ \"Missing\": [[[[[[[[[[ ]]]]]]]]]] }")]
    public void ABodyThatIsNotAReportObject_IsDropped(string json)
    {
        Assert.False(Parse(json, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void MoreThan32Missing_IsDropped()
    {
        var entries = string.Join(", ", new string[33].Select((_, i) => "{ \"Anchor\": \"fixtureList\", \"View\": \"fixture\", \"Count\": " + (i + 1) + " }"));
        var json = Valid.Replace("[ { \"Anchor\": \"fixtureList\", \"View\": \"fixture\", \"Count\": 2 } ]", "[" + entries + "]", StringComparison.Ordinal);

        Assert.False(Parse(json, out _, out var reason));
        Assert.Contains("1 to 32", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.11.5", true)]
    [InlineData("10.11.5.0", true)]
    [InlineData("1.2.3.12345", true)]
    [InlineData("1.2.3.123456", false)]
    [InlineData("10.11", false)]
    [InlineData(" 10.11.5", false)]
    [InlineData("10.11.5\n", false)]
    [InlineData(ArabicIndicVersion, false)]
    [InlineData(FullwidthVersion, false)]
    [InlineData(DevanagariVersion, false)]
    public void TheVersionPattern_IsTheWebClientsAsciiPattern(string value, bool matches)
    {
        Assert.Equal(matches, ClientHealthReport.IsVersion(value));
    }

    private static bool Parse(string json, out ClientHealthReport? report, out string reason)
        => ClientHealthReport.TryParse(Encoding.UTF8.GetBytes(json), Catalog, out report, out reason);
}
