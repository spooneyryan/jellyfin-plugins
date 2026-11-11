using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Storage;

/// <summary>
/// The export envelope, with <c>Archive</c>, written and read back, its checks, and its HTTP forms.
/// </summary>
public sealed class ExportEnvelopeTests
{
    private static readonly Guid Alex = Guid.Parse("5f0c4a1e-0000-4000-8000-000000000001");
    private static readonly Guid Alice = Guid.Parse("ae0c4a1e-0000-4000-8000-000000000004");
    private static readonly DateTime Exported = new(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnEnvelope_RoundTrips_InTheExportShape()
    {
        var envelope = Sample();

        var bytes = envelope.ToUtf8Bytes();
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(bytes));
        var read = ExportEnvelope.Parse(bytes);

        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(
            ["Format", "Plugin", "PluginId", "PluginVersion", "SchemaVersion", "ExportedUtc", "ServerId", "Users", "Settings", "Data", "Archive"],
            root.Select(p => p.Key));
        Assert.Equal("jellyfin-plugin-export/1", (string)root["Format"]!);
        Assert.Equal(TestKit.PluginId.ToString("D"), (string)root["PluginId"]!);
        Assert.Equal("2026-10-01T18:00:00Z", (string)root["ExportedUtc"]!);
        Assert.Equal("5f0c4a1e000040008000000000000001", (string)root["Users"]![0]!["Id"]!);
        Assert.Equal(["Id", "Name"], root["Users"]![0]!.AsObject().Select(p => p.Key));
        Assert.True((bool)root["Users"]![2]!["Deleted"]!);
        Assert.Equal(envelope.Plugin, read.Plugin);
        Assert.Equal(envelope.PluginId, read.PluginId);
        Assert.Equal(envelope.PluginVersion, read.PluginVersion);
        Assert.Equal(3, read.SchemaVersion);
        Assert.Equal(Exported, read.ExportedUtc);
        Assert.Equal(DateTimeKind.Utc, read.ExportedUtc.Kind);
        Assert.Equal("abc123", read.ServerId);
        Assert.Equal([new ExportUser(Alex, "alex"), new ExportUser(Guid.Parse("0a0c4a1e-0000-4000-8000-000000000002"), null), new ExportUser(Alice, "alice", Deleted: true)], read.Users);
        Assert.True(JsonNode.DeepEquals(envelope.Data, read.Data));
        Assert.True(JsonNode.DeepEquals(envelope.Settings, read.Settings));
        Assert.Equal(2, read.Archive.Count);
        Assert.True(JsonNode.DeepEquals(envelope.Archive[1], read.Archive[1]));
        Assert.Equal(bytes, read.ToUtf8Bytes());
    }

    [Fact]
    public void Create_TakesTheIdentityFromTheKitOptions_AndTheSettingsInTheStoreFormat()
    {
        var envelope = ExportEnvelope.Create(
            TestKit.Options,
            new StoreExportData(3, new JsonObject { ["Version"] = 3, ["Notes"] = new JsonArray() }),
            [new ExportUser(Alex, "alex")],
            new SampleSettings { Mode = SampleMode.Fast, Path = null },
            exportedUtc: new DateTime(2026, 10, 1, 18, 0, 0, 999, DateTimeKind.Utc));

        Assert.Equal("Kit Test", envelope.Plugin);
        Assert.Equal(TestKit.PluginId, envelope.PluginId);
        Assert.Equal("0.0.0.0", envelope.PluginVersion);
        Assert.Equal(3, envelope.SchemaVersion);
        Assert.Equal(Exported, envelope.ExportedUtc);
        Assert.Null(envelope.ServerId);
        Assert.Equal("{\"Enabled\":true,\"Mode\":\"Fast\"}", envelope.Settings!.ToJsonString());
        Assert.Empty(envelope.Archive);

        var noId = TestKit.Create();
        Assert.Throws<InvalidOperationException>(() => ExportEnvelope.Create(
            new Jellyfin.Plugin.Kit.Hosting.KitOptions { PluginName = noId.PluginName, RoutePrefix = noId.RoutePrefix, Prefix = noId.Prefix, SchemaPrefix = noId.SchemaPrefix, DataFolder = noId.DataFolder, PluginAssembly = noId.PluginAssembly },
            new StoreExportData(1, new JsonObject()),
            []));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/2\" }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"nope\", \"SchemaVersion\": 1, \"ExportedUtc\": \"2026-10-01T18:00:00Z\", \"Data\": {} }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 0, \"ExportedUtc\": \"2026-10-01T18:00:00Z\", \"Data\": {} }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 1, \"ExportedUtc\": \"soon\", \"Data\": {} }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 1, \"ExportedUtc\": \"2026-10-01T18:00:00Z\" }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 1, \"ExportedUtc\": \"2026-10-01T18:00:00Z\", \"Data\": {}, \"Users\": [ { \"Name\": \"no id\" } ] }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 1, \"ExportedUtc\": \"2026-10-01T18:00:00Z\", \"Data\": {}, \"Users\": [ { \"Id\": \"0f1e2d3c4b5a49688776a5b4c3d2e1f0\", \"Deleted\": \"yes\" } ] }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 1, \"ExportedUtc\": \"2026-10-01T18:00:00Z\", \"Data\": {}, \"Archive\": [ 1 ] }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Plugin\": \"X\", \"PluginId\": \"0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0\", \"SchemaVersion\": 1, \"ExportedUtc\": \"2026-10-01T18:00:00Z\", \"Data\": {}, \"Settings\": [] }")]
    [InlineData("{ \"Format\": \"jellyfin-plugin-export/1\", \"Format\": \"jellyfin-plugin-export/1\" }")]
    public void Parse_RefusesWhatIsntAnExport_With400(string body)
    {
        var ex = Assert.Throws<PluginException>(() => ExportEnvelope.Parse(Encoding.UTF8.GetBytes(body)));

        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public void EnsureFor_RefusesAnotherPluginsExport_AndANewerSchema()
    {
        var envelope = Sample();

        envelope.EnsureFor(TestKit.Options, 3);
        envelope.EnsureFor(TestKit.Options, 4);
        var newer = Assert.Throws<PluginException>(() => envelope.EnsureFor(TestKit.Options, 2));
        Assert.Equal(PluginErrorKind.Conflict, newer.Kind);
        Assert.Contains("schema 3", newer.Message, StringComparison.Ordinal);

        var other = ExportEnvelope.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(envelope.ToUtf8Bytes()).Replace(TestKit.PluginId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)));
        var ex = Assert.Throws<PluginException>(() => other.EnsureFor(TestKit.Options, 3));
        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        Assert.Contains("not Kit Test", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_Caps100MB_AndRefusesAnEmptyBody()
    {
        var bytes = Sample().ToUtf8Bytes();
        var ok = await ExportEnvelope.ReadAsync(Request(bytes, bytes.Length), TestContext.Current.CancellationToken);
        Assert.Equal(3, ok.SchemaVersion);

        var declared = await Assert.ThrowsAsync<PluginException>(() => ExportEnvelope.ReadAsync(Request([], ExportEnvelope.MaxImportBytes + 1), TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.PayloadTooLarge, declared.Kind);

        // Without a Content-Length the count of what is read decides.
        var streamed = await Assert.ThrowsAsync<PluginException>(() => ExportEnvelope.ReadAsync(Request(bytes, null), TestContext.Current.CancellationToken, maxBytes: bytes.Length - 1));
        Assert.Equal(PluginErrorKind.PayloadTooLarge, streamed.Kind);

        var empty = await Assert.ThrowsAsync<PluginException>(() => ExportEnvelope.ReadAsync(Request([], null), TestContext.Current.CancellationToken));
        Assert.Equal(PluginErrorKind.Invalid, empty.Kind);
    }

    [Fact]
    public void ToFileResult_IsAnAttachment_NeverCached()
    {
        var context = new DefaultHttpContext();

        var result = Assert.IsType<FileContentResult>(Sample().ToFileResult(context, "kittest"));

        Assert.Equal("kittest-export-20261001-180000.json", result.FileDownloadName);
        Assert.StartsWith("application/json", result.ContentType, StringComparison.Ordinal);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(3, ExportEnvelope.Parse(result.FileContents).SchemaVersion);
    }

    [Fact]
    public void ExportUsers_TakeTheServersNames_ThenTheLastSeenOnes_AndFlagUsersDeletedHere()
    {
        var viewer = Guid.Parse("0a0c4a1e-0000-4000-8000-000000000002");
        var gone = Guid.Parse("ff0c4a1e-0000-4000-8000-000000000003");
        var known = KnownUsers.Empty.With(Alex, "old name").With(gone, "gone").With(Alice, "alice");

        // alice was deleted here while she held records; a mark for an id the server knows (alex) doesn't count.
        var users = ExportUser.For([Alex, gone, viewer, Alice, Alex, Guid.Empty], known, new System.Collections.Generic.Dictionary<Guid, string> { [Alex] = "alex" }, new System.Collections.Generic.HashSet<Guid> { Alice, Alex });

        Assert.Equal([new ExportUser(viewer, null), new ExportUser(Alex, "alex"), new ExportUser(Alice, "alice", Deleted: true), new ExportUser(gone, "gone")], users);
    }

    private static ExportEnvelope Sample() => new()
    {
        Plugin = "Kit Test",
        PluginId = TestKit.PluginId,
        PluginVersion = "1.1.0.0",
        SchemaVersion = 3,
        ExportedUtc = Exported,
        ServerId = "abc123",
        Users = [new ExportUser(Alex, "alex"), new ExportUser(Guid.Parse("0a0c4a1e-0000-4000-8000-000000000002"), null), new ExportUser(Alice, "alice", Deleted: true)],
        Settings = new JsonObject { ["EnableWebUi"] = true },
        Data = new JsonObject { ["Version"] = 3, ["Notes"] = new JsonArray(new JsonObject { ["Id"] = "00000001-4b1d-4c2e-9a0f-112233445566", ["Text"] = "Amélie, café scène ✂ 日本語" }) },
        Archive = [new JsonObject { ["Id"] = "a" }, new JsonObject { ["Id"] = "b", ["Text"] = "archived" }]
    };

    private static HttpRequest Request(byte[] body, long? length)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = length;
        return context.Request;
    }

    private enum SampleMode
    {
        Accurate,
        Fast
    }

    private sealed class SampleSettings
    {
        public bool Enabled { get; set; } = true;

        public SampleMode Mode { get; set; }

        public string? Path { get; set; }
    }
}
