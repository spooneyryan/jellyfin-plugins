using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// Bookmarks' JSON on the wire keeps every property 1.0 sent: renaming the DTOs and enums for the schema
/// prefix (<c>ClipDto</c>, <c>ClipStatus</c>, <c>ClipMode</c>, <c>CreateBookmarkRequest</c>, <c>UpdateBookmarkRequest</c>
/// and <c>CreateClipRequest</c> became <see cref="BookmarkClipDto"/>, <see cref="BookmarkClipStatus"/>,
/// <see cref="BookmarkClipMode"/>, <see cref="BookmarkCreateRequest"/>, <see cref="BookmarkUpdateRequest"/> and
/// <see cref="BookmarkClipRequest"/>) changes only Swagger schema ids. <c>Fixtures/api-wire-1.0.json</c> was written by
/// this test from those types just before the renames: that is Bookmarks 1.0.0's JSON (the 1.0.0 release, tag
/// <c>bookmarks/v1.0.0</c>, where the other DTOs, both enums and <see cref="BookmarkDtoMapper"/> were
/// the same) plus the two 1.1 capabilities added by then, <c>IsAdmin</c> and <c>StoreReadOnly</c>, listed in
/// <see cref="InSnapshotSince10"/>. Every sample is compared as text, through Jellyfin's own serializer options (both
/// JSON profiles) and through MVC with the JSON options Jellyfin's <c>AddJellyfinApi</c> sets.
/// </summary>
public sealed class WireFormatTests
{
    /// <summary>The snapshot, embedded from <c>Fixtures/</c>.</summary>
    public const string SnapshotFixture = "api-wire-1.0.json";

    private static readonly Guid UserId = Guid.Parse("0f8b6a52-3c1d-4e7f-9a2b-6c5d4e3f2a10");
    private static readonly Guid BookmarkId = Guid.Parse("5f0c6a4e-2b1d-4c8e-9a7f-3e2d1c0b9a88");
    private static readonly Guid ItemId = Guid.Parse("a1b2c3d4-e5f6-4789-abcd-ef0123456789");

    // What the 1.0 web client and API users send (Web/js, scripts/bookmarks-api-probe.mjs): GUIDs with dashes, every field.
    private const string CreateBody = """{"ItemId":"a1b2c3d4-e5f6-4789-abcd-ef0123456789","StartTicks":1200000000,"EndTicks":1260000000,"Note":"Bank job \"take 2\"\nAmélie, café scène ✂ 日本語","MediaSourceId":"a1b2c3d4e5f64789abcdef0123456789","AudioStreamIndex":2}""";
    private const string UpdateBody = """{"StartTicks":1210000000,"EndTicks":null,"Note":"moved"}""";
    private const string ClipBody = """{"Mode":"Fast"}""";

    /// <summary>
    /// Properties the snapshot holds that Bookmarks 1.0.0 never sent: two of 1.1's capabilities, added before the
    /// snapshot was taken (<c>IsAdmin</c> and <c>StoreReadOnly</c>). The snapshot pins them like the
    /// 1.0 properties, so they never go in <see cref="AddedSinceSnapshot"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> InSnapshotSince10 = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["BookmarkCapabilitiesDto"] = ["IsAdmin", "StoreReadOnly"],
    };

    /// <summary>
    /// Properties added to a DTO after the snapshot, removed from the actual JSON before it is compared, so that every
    /// property the snapshot holds (every one a 1.0 client knows among them) keeps its name, type and value.
    /// <c>BookmarkDto.Name</c> came with schema 2, which moves short 1.0 notes into it, and
    /// <c>BookmarkCapabilitiesDto.StoreReadOnlyReason</c> with the read-only store (left out while the data is writable,
    /// so a writable server's capabilities are the snapshot's); 1.1.0 also added the requests' <c>Name</c> and the
    /// update's <c>IfUpdatedAt</c> (null in a 1.0 body, so the 1.0 bodies bind and write back as they did), the
    /// bookmark's <c>Detached</c> and <c>Relinked</c> (false for every bookmark 1.0 could have made), and the other 1.1
    /// fields, except the two already in the snapshot (<see cref="InSnapshotSince10"/>): the bookmark's <c>Year</c>,
    /// <c>SeriesName</c>, <c>SeasonNumber</c> and <c>EpisodeNumber</c> (from its identity snapshot), <c>ItemAvailable</c>
    /// (left out except in the My Bookmarks page's queries) and <c>Origin</c>, and the capabilities' limits, shortcuts,
    /// naming preferences and pages, the capabilities' <c>AskForNameDefault</c> (the server's default next to the
    /// caller's resolved <c>AskForName</c>) and <c>JellyfinEnhancedImportPending</c> (an admin's hint, false otherwise);
    /// 1.2.0 the capabilities' <c>CanDownloadClips</c> (the clip file route's permission, for the My Clips page's Preview
    /// and Download) and <c>CanManageAllClips</c> (the My Clips page's "All users" scope).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> AddedSinceSnapshot = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["BookmarkDto"] = ["Name", "Detached", "Relinked", "Year", "SeriesName", "SeasonNumber", "EpisodeNumber", "ItemAvailable", "Origin"],
        ["BookmarkClipDto"] = [],
        ["BookmarkCapabilitiesDto"] =
        [
            "StoreReadOnlyReason", "MaxNameLength", "MaxNoteLength", "Shortcuts", "ReservedShortcuts", "ShowPlayerButtons", "AskForName",
            "AskForNameIsDefault", "AskForNameDefault", "PauseWhileNaming", "SuggestChapterNames", "Pages", "JellyfinEnhancedImportPending",
            "CanDownloadClips", "CanManageAllClips"
        ],
        ["BookmarkCreateRequest"] = ["Name"],
        ["BookmarkUpdateRequest"] = ["Name", "IfUpdatedAt"],
    };

    private readonly User _user = TestData.CreateUser("alice");

    public static TheoryData<string> Profiles => new() { "pascalCase", "camelCase" };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Serializer_WritesAndReadsThe10Json(string profile)
    {
        var options = Options(profile);
        var actual = Samples(options);
        var expected = Snapshot()[profile]!.AsObject();

        var problems = new List<string>();
        foreach (var (name, json) in actual)
        {
            var wanted = expected[name]?.ToJsonString();
            var got = Strip(name, json, options).ToJsonString();
            if (!string.Equals(wanted, got, StringComparison.Ordinal))
            {
                problems.Add($"{profile}.{name}:\n  snapshot:  {wanted}\n  now:       {got}");
            }
        }

        problems.AddRange(expected.Select(p => p.Key).Except(actual.Keys).Select(key => $"{profile}.{key} is in the snapshot but no longer produced"));
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join('\n', problems) + "\nThe actual samples are in " + WriteActual());
        }
    }

    [Theory]
    [InlineData("pascalCase", """{"ClipsEnabled":true,"CanCreateClips":true,"MaxClipDurationSeconds":300,"DefaultClipMode":"Fast"}""")]
    [InlineData("camelCase", """{"clipsEnabled":true,"canCreateClips":true,"maxClipDurationSeconds":300,"defaultClipMode":"Fast"}""")]
    public void SnapshotCapabilities_Are10sPlusTheTwoAddedFields(string profile, string capabilities10)
    {
        // capabilities10 is what Bookmarks 1.0.0's own BookmarkCapabilitiesDto and ClipMode (the 1.0.0 release,
        // tag bookmarks/v1.0.0) write for these values through Jellyfin 10.11.5's JsonDefaults: its four properties, in this order. The
        // snapshot is that and InSnapshotSince10, nothing else; its other samples are 1.0.0's as they are.
        var snapshot = Snapshot()[profile]!["capabilities"]!.DeepClone().AsObject();
        foreach (var property in InSnapshotSince10["BookmarkCapabilitiesDto"])
        {
            Assert.True(snapshot.Remove(Name(property, Options(profile))), $"{profile}.capabilities has no {property}");
        }

        Assert.Equal(capabilities10, snapshot.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void AddedSinceSnapshot_ListsNothingTheSnapshotHolds(string profile)
    {
        // Listing a property the snapshot holds would strip it from the actual JSON and fail the comparison without saying
        // why; IsAdmin and StoreReadOnly, 1.1 fields, are the ones to watch for (InSnapshotSince10).
        var options = Options(profile);
        var pinned = new List<string>();
        foreach (var (sample, json) in Snapshot()[profile]!.AsObject())
        {
            foreach (var (obj, dto) in DtoObjects(sample, json!, options))
            {
                pinned.AddRange(AddedSinceSnapshot[dto].Where(p => obj.ContainsKey(Name(p, options))).Select(p => $"{profile}.{sample}: {dto}.{p}"));
            }
        }

        Assert.True(
            pinned.Count == 0,
            "In AddedSinceSnapshot, but the snapshot already holds them and keeps comparing them (IsAdmin and StoreReadOnly were added before it was taken): "
                + string.Join(", ", pinned));
    }

    [Fact]
    public void Enums_KeepTheir10NamesAndNumbers()
    {
        // The store (JsonStoreFormat) and the plugin configuration write enum names, and Jellyfin's converters read the
        // numbers too; so the members' names and values are part of the format as well.
        Assert.Equal(["Queued", "Running", "Completed", "Failed", "Cancelled"], Enum.GetNames<BookmarkClipStatus>());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<BookmarkClipStatus>().Select(v => (int)v));
        Assert.Equal(["Accurate", "Fast"], Enum.GetNames<BookmarkClipMode>());
        Assert.Equal([0, 1], Enum.GetValues<BookmarkClipMode>().Select(v => (int)v));
    }

    [Theory]
    [InlineData("pascalCase", "\"StoreReadOnly\":true,\"StoreReadOnlyReason\":\"MigrationFailed\",")]
    [InlineData("camelCase", "\"storeReadOnly\":true,\"storeReadOnlyReason\":\"MigrationFailed\",")]
    public void Capabilities_OfReadOnlyData_NameTheReason_AndWritableDataLeavesItOut(string profile, string readOnlyPart)
    {
        // The client words its banner by the reason; a writable server leaves it out, so it sends the
        // snapshot's capabilities and only the 1.1 fields added since, which a 1.0 client ignores.
        var options = Options(profile);
        var readOnly = Capabilities(isAdmin: false);
        readOnly.StoreReadOnly = true;
        readOnly.StoreReadOnlyReason = BookmarkStoreReadOnlyReason.MigrationFailed;

        Assert.Contains(readOnlyPart, JsonSerializer.Serialize(readOnly, options), StringComparison.Ordinal);
        var writable = JsonNode.Parse(JsonSerializer.Serialize(Capabilities(isAdmin: true), options))!;
        Assert.Null(writable[Name("StoreReadOnlyReason", options)]);
        Assert.Equal(Snapshot()[profile]!["capabilities"]!.ToJsonString(), Strip("capabilities", writable, options).ToJsonString());
        // Values are only added, at the end (DataMissing in 1.1.2, DataUnreadable in 1.2), so the numbers stay.
        Assert.Equal(["NewerVersion", "MigrationFailed", "DataMissing", "DataUnreadable"], Enum.GetNames<BookmarkStoreReadOnlyReason>());
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<BookmarkStoreReadOnlyReason>().Select(v => (int)v));
    }

    [Fact]
    public void SettingsXml_NamesTheClipModeByItsValueOnly()
    {
        // Jellyfin keeps plugin settings with XmlSerializer (plugins/configurations/Jellyfin.Plugin.Bookmarks.xml), which
        // never writes an enum's type name: a settings file of 1.0 reads the same, and this version writes it the same.
        const string Settings10 = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <EnableClips>true</EnableClips>
              <DefaultClipMode>Fast</DefaultClipMode>
              <MaxClipDurationSeconds>600</MaxClipDurationSeconds>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var reader = new StringReader(Settings10);
        Assert.Equal(BookmarkClipMode.Fast, Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader)).DefaultClipMode);

        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { DefaultClipMode = BookmarkClipMode.Fast });
        Assert.Contains("<DefaultClipMode>Fast</DefaultClipMode>", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(BookmarkClipMode), writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mvc_AnswersAndBindsThe10Json()
    {
        var created = default(BookmarkCreateRequest);
        var updated = default(BookmarkUpdateRequest);
        var modes = new List<BookmarkClipMode?>();
        var manager = new Mock<IBookmarkManager>();
        manager.Setup(m => m.GetBookmark(_user, BookmarkId)).Returns(RunningClipDto());
        manager.Setup(m => m.GetBookmarks(_user, ItemId)).Returns([RunningClipDto(), BookmarkDtoMapper.ToDto(FailedClip(), null), BookmarkDtoMapper.ToDto(Minimal(), null)]);
        manager.Setup(m => m.GetCapabilities(_user, It.IsAny<bool>())).Returns((User _, bool isAdmin) => Capabilities(isAdmin));
        manager.Setup(m => m.CreateBookmarkAsync(_user, It.IsAny<BookmarkCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((User _, BookmarkCreateRequest r, CancellationToken _) => created = r)
            .ReturnsAsync(RunningClipDto());
        manager.Setup(m => m.UpdateBookmarkAsync(_user, BookmarkId, It.IsAny<BookmarkUpdateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((User _, Guid _, BookmarkUpdateRequest r, CancellationToken _) => updated = r)
            .ReturnsAsync(BookmarkDtoMapper.ToDto(Minimal(), null));
        manager.Setup(m => m.StartClipAsync(_user, BookmarkId, It.IsAny<BookmarkClipMode?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((User _, Guid _, BookmarkClipMode? mode, bool _, CancellationToken _) => modes.Add(mode))
            .ReturnsAsync(RunningClipDto());

        await using var host = await StartAsync(manager.Object);
        var snapshot = Snapshot()["pascalCase"]!;
        var options = JsonDefaults.PascalCaseOptions;

        var (status, body) = await SendAsync(host, HttpMethod.Get, $"/Bookmarks/{BookmarkId}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(snapshot["bookmark"]!.ToJsonString(), Strip("bookmark", JsonNode.Parse(body)!, options).ToJsonString());

        (status, body) = await SendAsync(host, HttpMethod.Get, $"/Bookmarks?itemId={ItemId}");
        Assert.Equal(HttpStatusCode.OK, status);
        var list = JsonNode.Parse(body)!.AsArray();
        Assert.Equal(
            [snapshot["bookmark"]!.ToJsonString(), snapshot["bookmarkFailedClip"]!.ToJsonString(), snapshot["bookmarkMinimal"]!.ToJsonString()],
            list.Select(item => Strip("bookmark", item!.DeepClone(), options).ToJsonString()));

        (status, body) = await SendAsync(host, HttpMethod.Get, "/Bookmarks/Capabilities");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(snapshot["capabilities"]!.ToJsonString(), Strip("capabilities", JsonNode.Parse(body)!, options).ToJsonString());

        (status, body) = await SendAsync(host, HttpMethod.Post, "/Bookmarks", CreateBody);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(snapshot["bookmark"]!.ToJsonString(), Strip("bookmark", JsonNode.Parse(body)!, options).ToJsonString());
        Assert.Equal(snapshot["createRequest"]!.ToJsonString(), JsonSerializer.Serialize(created, options));

        (status, _) = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{BookmarkId}", UpdateBody);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(snapshot["updateRequest"]!.ToJsonString(), JsonSerializer.Serialize(updated, options));

        // 1.0's update body has neither Name nor IfUpdatedAt, which is how the manager tells it and keeps the name; the
        // flag is no part of the JSON.
        Assert.False(updated!.NameSent);
        Assert.Null(updated.IfUpdatedAt);

        foreach (var clipBody in new[] { ClipBody, """{"Mode":1}""", """{"Mode":"Accurate"}""", "{}", string.Empty })
        {
            (status, body) = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{BookmarkId}/Clip", clipBody);
            Assert.True(status == HttpStatusCode.Accepted, $"{clipBody}: {status} {body}");
        }

        Assert.Equal([BookmarkClipMode.Fast, BookmarkClipMode.Fast, BookmarkClipMode.Accurate, null, null], modes);
    }

    [Fact]
    public async Task Mvc_Binds11Bodies_AndTheUpdatedAtItSends_ComesBackAsTheSameToken()
    {
        // The requests' Name and IfUpdatedAt through MVC with Jellyfin's JSON options. The token is the UpdatedAt
        // the API sent, as text: Jellyfin's JsonDateTimeConverter writes every tick (".0000000Z" when the milliseconds are
        // 0, the shortest round trip otherwise) and reads it back to the same UTC instant, so IsUnchangedSince matches it; a
        // JavaScript Date's millisecond form matches too, and a later millisecond doesn't.
        var created = default(BookmarkCreateRequest);
        var updated = default(BookmarkUpdateRequest);
        var renamed = new List<BookmarkRenameRequest>();
        var record = RunningClip() with { UpdatedAt = new DateTime(2026, 9, 29, 11, 0, 5, 250, DateTimeKind.Utc).AddTicks(4321) };
        var manager = new Mock<IBookmarkManager>();
        manager.Setup(m => m.GetBookmark(_user, BookmarkId)).Returns(() => BookmarkDtoMapper.ToDto(record, null));
        manager.Setup(m => m.CreateBookmarkAsync(_user, It.IsAny<BookmarkCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((User _, BookmarkCreateRequest r, CancellationToken _) => created = r)
            .ReturnsAsync(() => BookmarkDtoMapper.ToDto(record, null));
        manager.Setup(m => m.UpdateBookmarkAsync(_user, BookmarkId, It.IsAny<BookmarkUpdateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((User _, Guid _, BookmarkUpdateRequest r, CancellationToken _) => updated = r)
            .ReturnsAsync(() => BookmarkDtoMapper.ToDto(record, null));
        manager.Setup(m => m.RenameBookmarkAsync(_user, BookmarkId, It.IsAny<BookmarkRenameRequest>(), It.IsAny<CancellationToken>()))
            .Callback((User _, Guid _, BookmarkRenameRequest r, CancellationToken _) => renamed.Add(r))
            .ReturnsAsync(() => BookmarkDtoMapper.ToDto(record, null));
        await using var host = await StartAsync(manager.Object);

        var (status, body) = await SendAsync(host, HttpMethod.Get, $"/Bookmarks/{BookmarkId}");
        Assert.Equal(HttpStatusCode.OK, status);
        var token = JsonNode.Parse(body)!["UpdatedAt"]!.GetValue<string>();
        Assert.Equal("2026-09-29T11:00:05.2504321Z", token);

        (status, body) = await SendAsync(host, HttpMethod.Post, "/Bookmarks", $$"""{"ItemId":"{{ItemId}}","StartTicks":0,"Name":"Bank job"}""");
        Assert.True(status == HttpStatusCode.Created, body);
        Assert.Equal("Bank job", created!.Name);

        (status, body) = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{BookmarkId}", $$"""{"StartTicks":0,"Name":"Vault","Note":null,"IfUpdatedAt":"{{token}}"}""");
        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal("Vault", updated!.Name);
        Assert.True(updated.NameSent);
        Assert.Equal(record.UpdatedAt.Ticks, updated.IfUpdatedAt!.Value.ToUniversalTime().Ticks);
        Assert.True(BookmarkRecordRules.IsUnchangedSince(record, updated.IfUpdatedAt));

        // A Name that is there but null (in any case of its property name) is sent: it clears the name, 1.0's body or not.
        foreach (var nullName in new[] { """{"StartTicks":0,"Name":null}""", """{"startTicks":0,"name":null}""" })
        {
            (status, body) = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{BookmarkId}", nullName);
            Assert.True(status == HttpStatusCode.OK, body);
            Assert.Null(updated.Name);
            Assert.True(updated.NameSent, nullName);
            Assert.Null(updated.IfUpdatedAt);
        }

        foreach (var given in new[] { token, "2026-09-29T11:00:05.250Z", "2026-09-29T11:00:05.251Z", "2026-09-29T13:00:05.2504321+02:00" })
        {
            (status, body) = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{BookmarkId}/Name", $$"""{"name":"Vault","ifUpdatedAt":"{{given}}"}""");
            Assert.True(status == HttpStatusCode.OK, body);
        }

        (status, body) = await SendAsync(host, HttpMethod.Post, $"/Bookmarks/{BookmarkId}/Name", """{"Name":null}""");
        Assert.True(status == HttpStatusCode.OK, body);

        Assert.Equal(["Vault", "Vault", "Vault", "Vault", null], renamed.Select(r => r.Name));
        Assert.Equal([true, true, false, true, true], renamed.Select(r => BookmarkRecordRules.IsUnchangedSince(record, r.IfUpdatedAt)));
        Assert.Null(renamed[^1].IfUpdatedAt);
    }

    // Every sample of the snapshot, serialized with the given options.
    private static SortedDictionary<string, JsonNode> Samples(JsonSerializerOptions options)
    {
        JsonNode Serialize<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, options))!;
        JsonNode Bound<T>(string body) => Serialize(JsonSerializer.Deserialize<T>(body, MvcOptions(options)));

        return new SortedDictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["bookmark"] = Serialize(RunningClipDto()),
            ["bookmarkFailedClip"] = Serialize(BookmarkDtoMapper.ToDto(FailedClip(), null)),
            ["bookmarkMinimal"] = Serialize(BookmarkDtoMapper.ToDto(Minimal(), null)),
            ["capabilities"] = Serialize(Capabilities(isAdmin: true)),
            ["createRequest"] = Bound<BookmarkCreateRequest>(CreateBody),
            ["updateRequest"] = Bound<BookmarkUpdateRequest>(UpdateBody),
            ["clipRequest"] = Bound<BookmarkClipRequest>(ClipBody),
            ["clipStatus"] = new JsonArray([.. Enum.GetValues<BookmarkClipStatus>().Select(v => (JsonNode?)Serialize(v))]),
            ["clipMode"] = new JsonArray([.. Enum.GetValues<BookmarkClipMode>().Select(v => (JsonNode?)Serialize(v))]),
        };
    }

    // How MVC reads a body: the web defaults (case-insensitive names) with the options Jellyfin copies in.
    private static JsonSerializerOptions MvcOptions(JsonSerializerOptions jellyfin)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        CopyJellyfinOptions(jellyfin, options);
        return options;
    }

    // Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs (v10.11.5) AddJellyfinApi, AddJsonOptions.
    private static void CopyJellyfinOptions(JsonSerializerOptions from, JsonSerializerOptions to)
    {
        to.ReadCommentHandling = from.ReadCommentHandling;
        to.WriteIndented = from.WriteIndented;
        to.DefaultIgnoreCondition = from.DefaultIgnoreCondition;
        to.NumberHandling = from.NumberHandling;
        to.Converters.Clear();
        foreach (var converter in from.Converters)
        {
            to.Converters.Add(converter);
        }

        to.PropertyNamingPolicy = from.PropertyNamingPolicy;
    }

    // Removes the properties added after the snapshot (AddedSinceSnapshot) from an actual sample.
    private static JsonNode Strip(string sample, JsonNode json, JsonSerializerOptions options)
    {
        foreach (var (obj, dto) in DtoObjects(sample, json, options).ToList())
        {
            foreach (var property in AddedSinceSnapshot[dto])
            {
                obj.Remove(Name(property, options));
            }
        }

        return json;
    }

    // The DTO objects in a sample: a bookmark and its clip, or the capabilities.
    private static IEnumerable<(JsonObject Json, string Dto)> DtoObjects(string sample, JsonNode json, JsonSerializerOptions options)
    {
        if (sample.StartsWith("bookmark", StringComparison.Ordinal))
        {
            yield return (json.AsObject(), "BookmarkDto");
            if (json[Name("Clip", options)] is JsonObject clip)
            {
                yield return (clip, "BookmarkClipDto");
            }
        }
        else if (sample == "capabilities")
        {
            yield return (json.AsObject(), "BookmarkCapabilitiesDto");
        }
        else if (sample == "createRequest")
        {
            yield return (json.AsObject(), "BookmarkCreateRequest");
        }
        else if (sample == "updateRequest")
        {
            yield return (json.AsObject(), "BookmarkUpdateRequest");
        }
    }

    private static string Name(string property, JsonSerializerOptions options) => options.PropertyNamingPolicy?.ConvertName(property) ?? property;

    private static JsonSerializerOptions Options(string profile) => profile == "pascalCase" ? JsonDefaults.PascalCaseOptions : JsonDefaults.CamelCaseOptions;

    private static JsonObject Snapshot()
    {
        var text = Encoding.UTF8.GetString(StoreFixtures.Read(SnapshotFixture));
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string WriteActual()
    {
        var root = new JsonObject
        {
            ["comment"] = Snapshot()["comment"]?.DeepClone(),
            ["pascalCase"] = new JsonObject(Samples(JsonDefaults.PascalCaseOptions).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["camelCase"] = new JsonObject(Samples(JsonDefaults.CamelCaseOptions).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        };
        var file = Path.Combine(AppContext.BaseDirectory, "api-wire-1.0.actual.json");
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return file;
    }

    // The running clip's bookmark as the manager returns it, with its job's progress.
    private static BookmarkDto RunningClipDto() => BookmarkDtoMapper.ToDto(RunningClip(), 0.45678);

    // A range whose Accurate clip is running, while an older clip file of another range is on disk (so it is outdated).
    // Schema 2 fields (a name, the identity with its admin-only path) leave the 1.0 properties as they were.
    private static Bookmark RunningClip() => new()
    {
        Id = BookmarkId,
        UserId = UserId,
        ItemId = ItemId,
        MediaSourceId = "a1b2c3d4e5f64789abcdef0123456789",
        AudioStreamIndex = 2,
        StartTicks = 1_200_000_000,
        EndTicks = 1_260_000_000,
        Name = "Bank job",
        Note = "Bank job \"take 2\"\nAmélie, café scène ✂ 日本語",
        ItemName = "Heat (1995)",
        Identity = new IdentitySnapshot
        {
            ItemId = ItemId,
            Kind = "Movie",
            Name = "Heat",
            Year = 1995,
            RunTimeTicks = 102_000_000_000,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "949" },
            Path = @"Q:\Movies\Heat (1995)\Heat (1995).mkv"
        },
        AttachedUtc = new DateTime(2026, 9, 29, 10, 15, 30, 123, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 9, 29, 10, 15, 30, 123, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 9, 29, 11, 0, 0, DateTimeKind.Utc),
        Clip = new ClipInfo
        {
            Status = BookmarkClipStatus.Running,
            Mode = BookmarkClipMode.Accurate,
            StartTicks = 1_200_000_000,
            EndTicks = 1_260_000_000,
            RequestedAt = new DateTime(2026, 9, 29, 11, 0, 5, 250, DateTimeKind.Utc).AddTicks(4321),
            FilePath = @"C:\clips\alice\Heat (1995) - Bank job [00.01.50-00.01.56].mkv",
            FileSize = 325_642,
            FileStartTicks = 1_100_000_000,
            FileEndTicks = 1_160_000_000
        }
    };

    // A range whose Fast clip failed; no file.
    private static Bookmark FailedClip() => new()
    {
        Id = Guid.Parse("7c1e2d3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f"),
        UserId = UserId,
        ItemId = ItemId,
        StartTicks = 600_000_000,
        EndTicks = 660_000_000,
        ItemName = "Heat (1995)",
        Identity = new IdentitySnapshot { ItemId = ItemId, Kind = "Movie", Name = "Heat" },
        CreatedAt = new DateTime(2026, 9, 28, 20, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 9, 28, 20, 0, 0, DateTimeKind.Utc),
        Clip = new ClipInfo
        {
            Status = BookmarkClipStatus.Failed,
            Mode = BookmarkClipMode.Fast,
            StartTicks = 600_000_000,
            EndTicks = 660_000_000,
            RequestedAt = new DateTime(2026, 9, 28, 20, 1, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 9, 28, 20, 1, 2, 500, DateTimeKind.Utc),
            Error = "ffmpeg failed: No space left on device"
        }
    };

    // A position with nothing optional: no range, name, note, media source, item type (an empty identity) or clip.
    private static Bookmark Minimal() => new()
    {
        Id = Guid.Parse("11111111-2222-4333-8444-555555555555"),
        UserId = UserId,
        ItemId = ItemId,
        StartTicks = 0,
        ItemName = "Heat (1995)",
        CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };

    private static BookmarkCapabilitiesDto Capabilities(bool isAdmin) => new()
    {
        ClipsEnabled = true,
        CanCreateClips = true,
        MaxClipDurationSeconds = 300,
        DefaultClipMode = BookmarkClipMode.Fast,
        IsAdmin = isAdmin,
        StoreReadOnly = false
    };

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(WebHostHarness host, HttpMethod method, string path, string? json = null)
    {
        using var response = await host.SendAsync(method, path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "admin");
            request.Headers.Add(JellyfinAuth.UserIdHeader, UserIdHeaderValue);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private string UserIdHeaderValue => _user.Id.ToString("N");

    private Task<WebHostHarness> StartAsync(IBookmarkManager manager)
    {
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(_user.Id)).Returns(_user);
        return new WebHostHarness()
            .WithServices(services =>
            {
                services.AddSingleton(BookmarksKit.Options);
                services.AddSingleton(new KitAbiStatus(BookmarksKit.Options.PluginName, JellyfinLine.Target, JellyfinLine.ServerVersion, isActive: true, "Active."));
                services.AddSingleton(manager);
                services.AddSingleton(Mock.Of<IBookmarkQuery>());
                services.AddSingleton(Mock.Of<IBookmarkPreferences>());
                services.AddSingleton(users.Object);
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => CopyJellyfinOptions(JsonDefaults.PascalCaseOptions, o.JsonSerializerOptions));
            })
            .WithMvc(typeof(BookmarksController))
            .StartAsync();
    }
}
