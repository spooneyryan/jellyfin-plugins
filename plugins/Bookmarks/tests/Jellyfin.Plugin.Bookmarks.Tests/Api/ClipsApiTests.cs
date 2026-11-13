using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Api;

/// <summary>
/// The My Clips page's endpoints on the wire: <c>GET /Bookmarks/Clips</c> as Jellyfin's
/// <c>QueryResult</c> with Jellyfin's JSON options, only the caller's clips (a deleted bookmark's kept clip among them, another
/// user's never), each with its state and the file's name but no path; the query string's values (lists comma-separated or
/// repeated, the endpoint's own sorts, a 400 for anything else); <c>Summary</c>, <c>Status</c> (ids as Jellyfin's clients
/// write them) and <c>BulkDelete</c>; a bookmark's own JSON unchanged; API keys refused; and what the API explorer, which
/// Jellyfin's Swashbuckle reads, says about the new actions. The plugin's own registrations behind Jellyfin's web pipeline;
/// Jellyfin's services are mocks.
/// </summary>
public sealed class ClipsApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid BobId = new("ff71b896-f205-45ff-936a-c2393f6091f6");
    private static readonly Guid HeatId = new("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");

    public ClipsApiTests()
    {
        _alice.Id = AliceId;
        _bob.Id = BobId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ClipFolder => Path.Combine(_data.Path, "bookmarks", "clips");

    [Fact]
    public async Task Clips_AreJellyfinsQueryResult_OfTheCallersClipsOnly_WithTheirStates_AndNoPath()
    {
        await using var host = await StartAsync();
        var ready = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var kept = await CreateWithClipAsync(host, AliceId, 120, "Kept");
        await SendAsync(host, "DELETE", $"/Bookmarks/{kept}?deleteClip=false", AliceId);
        var failed = await CreateWithClipAsync(host, AliceId, 180, "Failed", status: BookmarkClipStatus.Failed, file: false);
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's");

        var (status, body) = await SendAsync(host, "GET", "/Bookmarks/Clips", AliceId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Items", "TotalRecordCount", "StartIndex"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, body.GetProperty("TotalRecordCount").GetInt32());
        var rows = body.GetProperty("Items").EnumerateArray().ToDictionary(r => Id(r.GetProperty("Id")));
        Assert.Equal(new[] { ready, kept, failed }.Order(), rows.Keys.Order());
        Assert.DoesNotContain(bobs, rows.Keys);

        var row = rows[ready];
        Assert.Equal(
            ["Id", "BookmarkDeleted", "Origin", "Name", "ItemId", "ItemName", "ItemType", "ItemAvailable", "BookmarkStartTicks", "BookmarkEndTicks", "Clip", "CreatedAt", "UpdatedAt"],
            row.EnumerateObject().Select(p => p.Name));
        Assert.False(row.GetProperty("BookmarkDeleted").GetBoolean());
        Assert.True(row.GetProperty("ItemAvailable").GetBoolean());
        Assert.Equal("Native", row.GetProperty("Origin").GetString());
        var clip = row.GetProperty("Clip");
        Assert.Equal("Ready", clip.GetProperty("State").GetString());
        Assert.Equal("Completed", clip.GetProperty("Status").GetString());
        Assert.Equal("mp4", clip.GetProperty("Container").GetString());
        Assert.True(clip.GetProperty("FileExists").GetBoolean());
        Assert.True(clip.GetProperty("RootAvailable").GetBoolean());
        Assert.False(clip.GetProperty("InLibrary").GetBoolean());
        Assert.False(clip.GetProperty("IsNameOutdated").GetBoolean());
        Assert.False(clip.GetProperty("LastJobFailed").GetBoolean());
        Assert.Equal(30 * TestData.OneSecond, clip.GetProperty("DurationTicks").GetInt64());
        Assert.Equal("Heat (1995) - Bank job [00.01.00-00.01.30].mp4", clip.GetProperty("FileName").GetString());
        Assert.True(clip.TryGetProperty("MadeAt", out _));
        Assert.False(clip.TryGetProperty("LibraryName", out _));

        Assert.True(rows[kept].GetProperty("BookmarkDeleted").GetBoolean());
        Assert.Equal("Ready", rows[kept].GetProperty("Clip").GetProperty("State").GetString());
        Assert.Equal("Failed", rows[failed].GetProperty("Clip").GetProperty("State").GetString());
        Assert.False(rows[failed].GetProperty("Clip").TryGetProperty("RootAvailable", out _));

        // No server path: neither the clip folder nor the video's.
        Assert.DoesNotContain(JsonSerializer.Serialize(_data.Path).Trim('"'), body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\\media", body.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // A bookmark's own JSON is 1.1's: its clip carries none of the clip list's facts.
        (_, var one) = await SendAsync(host, "GET", $"/Bookmarks/{ready}", AliceId);
        Assert.Equal(
            ["Status", "Mode", "StartTicks", "EndTicks", "IsOutdated", "FileName", "FileSize", "RequestedAt", "CompletedAt"],
            one.GetProperty("Clip").EnumerateObject().Select(p => p.Name));

        // Bob sees his own only.
        (_, var bobsList) = await SendAsync(host, "GET", "/Bookmarks/Clips", BobId);
        Assert.Equal([bobs], bobsList.GetProperty("Items").EnumerateArray().Select(r => Id(r.GetProperty("Id"))));
    }

    [Fact]
    public async Task TheFilters_BindFromTheQueryString_ListsCommaSeparatedOrRepeated_AndAnyOtherValueIs400()
    {
        await using var host = await StartAsync();
        var ready = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var fast = await CreateWithClipAsync(host, AliceId, 120, "Fast one", mode: BookmarkClipMode.Fast);
        var failed = await CreateWithClipAsync(host, AliceId, 180, "Failed", status: BookmarkClipStatus.Failed, file: false);
        var kept = await CreateWithClipAsync(host, AliceId, 240, "Kept");
        await SendAsync(host, "DELETE", $"/Bookmarks/{kept}?deleteClip=false", AliceId);

        async Task<Guid[]> ListedAsync(string query)
        {
            var (status, body) = await SendAsync(host, "GET", "/Bookmarks/Clips?" + query, AliceId);
            Assert.True(status == HttpStatusCode.OK, query);
            return body.GetProperty("Items").EnumerateArray().Select(r => Id(r.GetProperty("Id"))).Order().ToArray();
        }

        Assert.Equal(new[] { failed }, await ListedAsync("state=Failed"));
        Assert.Equal(new[] { ready, fast, failed, kept }.Order(), await ListedAsync("state=Ready,Failed"));
        Assert.Equal(new[] { ready, fast, failed, kept }.Order(), await ListedAsync("state=ready&state=FAILED"));
        Assert.Equal(new[] { fast }, await ListedAsync("mode=Fast"));
        Assert.Equal(new[] { ready, fast, failed, kept }.Order(), await ListedAsync("itemTypes=Movie,Other"));
        Assert.Empty(await ListedAsync("itemTypes=Episode"));
        Assert.Equal(new[] { ready, fast, failed }.Order(), await ListedAsync("includeDeleted=false"));
        Assert.Equal(new[] { kept }, await ListedAsync("searchTerm=KEPT"));
        Assert.Equal(new[] { ready, fast, failed, kept }.Order(), await ListedAsync($"itemId={HeatId:N}&sortBy=Oldest&startIndex=0&limit=200"));
        foreach (var sort in Enum.GetNames<BookmarkClipSortBy>())
        {
            Assert.Equal(4, (await ListedAsync("sortBy=" + sort)).Length);
        }

        // MVC's own validation answer for a value that is no member ([ApiController], the kit's StrictEnumQuery and
        // CommaSeparatedQuery); the kit's ProblemDetails for a value out of range.
        foreach (var query in new[]
        {
            "state=Gone", "state=Ready,Gone", "mode=Slow", "sortBy=Count", "sortBy=UpdatedAt", "sortBy=99", "itemTypes=Show",
            "includeDeleted=perhaps", "itemId=not-a-guid", "filter.mode=Slow", "filter.state=Ready,Gone"
        })
        {
            using var response = await SendRawAsync(host, "GET", "/Bookmarks/Clips?" + query, AliceId);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, query);
            Assert.NotEmpty((await ValidationProblemAsync(response)).GetProperty("errors").EnumerateObject());
        }

        foreach (var (query, detail) in new[]
        {
            ("limit=201", "The limit must be between 1 and 200."),
            ("limit=0", "The limit must be between 1 and 200."),
            ("startIndex=-1", "The start index can't be negative."),
            ("searchTerm=" + new string('x', 101), "Search terms can be at most 100 characters long.")
        })
        {
            using var response = await SendRawAsync(host, "GET", "/Bookmarks/Clips?" + query, AliceId);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, query);
            Assert.Equal($$"""{"title":"Bad Request","status":400,"detail":"{{detail}}"}""", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task SummaryAndStatus_OnTheWire_AndStatusTakesTheIdsAsJellyfinsClientsWriteThem()
    {
        await using var host = await StartAsync();
        var ready = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var queued = await CreateWithClipAsync(host, AliceId, 120, "Queued", status: BookmarkClipStatus.Queued, file: false);
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's");

        var (_, summary) = await SendAsync(host, "GET", "/Bookmarks/Clips/Summary", AliceId);
        Assert.Equal("""{"Count":2,"TotalBytes":4,"InProgress":1,"Failed":0,"Missing":0,"FolderUnavailable":0}""", summary.GetRawText());

        foreach (var query in new[] { $"ids={ready:N},{bobs:N},{queued:N}", $"ids={ready}&ids={bobs}&ids={queued}", $"ids={ready:N}, {queued:N},,{bobs:N}" })
        {
            var (status, body) = await SendAsync(host, "GET", "/Bookmarks/Clips/Status?" + query, AliceId);
            Assert.Equal(HttpStatusCode.OK, status);
            var states = body.EnumerateArray().ToList();
            Assert.Equal([ready, queued], states.Select(s => Id(s.GetProperty("Id"))));
            Assert.Equal(["Id", "Status", "State", "FileName", "FileSize"], states[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal("Ready", states[0].GetProperty("State").GetString());
            Assert.Equal("InProgress", states[1].GetProperty("State").GetString());
            Assert.Equal("Queued", states[1].GetProperty("Status").GetString());
        }

        using (var bad = await SendRawAsync(host, "GET", $"/Bookmarks/Clips/Status?ids={ready:N},nope", AliceId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var problem = await ValidationProblemAsync(bad);
            Assert.Equal(["The value 'nope' is not a GUID."], problem.GetProperty("errors").GetProperty("ids").EnumerateArray().Select(e => e.GetString()));
        }

        foreach (var (query, detail) in new[]
        {
            (string.Empty, ClipService.StatusNoIdsMessage),
            ("?ids=", ClipService.StatusNoIdsMessage),
            ("?ids=" + string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.NewGuid().ToString("N"))), ClipService.StatusTooManyMessage)
        })
        {
            using var response = await SendRawAsync(host, "GET", "/Bookmarks/Clips/Status" + query, AliceId);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal($$"""{"title":"Bad Request","status":400,"detail":"{{detail}}"}""", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task BulkDelete_AnswersForEachId_TheFileFirst_AndAnotherUsersClipIsNotFound()
    {
        await using var host = await StartAsync();
        var ready = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var kept = await CreateWithClipAsync(host, AliceId, 120, "Kept");
        await SendAsync(host, "DELETE", $"/Bookmarks/{kept}?deleteClip=false", AliceId);
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var readyFile = store.Get(ready)!.Clip!.FilePath!;
        var keptFile = store.Get(kept)!.Clip!.FilePath!;
        var bobsFile = store.Get(bobs)!.Clip!.FilePath!;
        var unknown = Guid.NewGuid();

        var (status, result) = await SendAsync(host, "POST", "/Bookmarks/Clips/BulkDelete", AliceId, $$"""{"Ids":["{{kept}}","{{bobs}}","{{ready}}","{{unknown}}"]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["FilesDeleted", "RecordsRemoved", "NotFound", "InProgress", "InUse", "FolderUnavailable", "Failed"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal([ready], result.GetProperty("FilesDeleted").EnumerateArray().Select(Id));
        Assert.Equal([kept], result.GetProperty("RecordsRemoved").EnumerateArray().Select(Id));
        Assert.Equal([bobs, unknown], result.GetProperty("NotFound").EnumerateArray().Select(Id));
        Assert.False(File.Exists(readyFile));
        Assert.False(File.Exists(keptFile));
        Assert.True(File.Exists(bobsFile));

        // The live bookmark stays, without its clip; the kept clip is gone with what was left of its bookmark.
        (_, var stays) = await SendAsync(host, "GET", $"/Bookmarks/{ready}", AliceId);
        Assert.False(stays.TryGetProperty("Clip", out _));
        using (var gone = await SendRawAsync(host, "GET", $"/Bookmarks/{kept}/Clip/File", AliceId))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        Assert.Equal(0, (await SendAsync(host, "GET", "/Bookmarks/Clips", AliceId)).Body.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, (await SendAsync(host, "GET", "/Bookmarks/Clips", BobId)).Body.GetProperty("TotalRecordCount").GetInt32());

        using var none = await SendRawAsync(host, "POST", "/Bookmarks/Clips/BulkDelete", AliceId, """{"Ids":[]}""");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal($$"""{"title":"Bad Request","status":400,"detail":"{{ClipService.BulkDeleteNoIdsMessage}}"}""", await none.Content.ReadAsStringAsync(Ct));
        using var noBody = await SendRawAsync(host, "POST", "/Bookmarks/Clips/BulkDelete", AliceId, string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
    }

    [Fact]
    public async Task ARemakeOfAClipWhoseFileWentMissing_ReadsReadyAtOnce_AndForgetNeverDeletesItsFile()
    {
        // Found in testing: a Fast clip made, its file moved aside (Missing), a remake started; the first poll
        // after it was done said "Status: Completed, State: Missing" with the new file on disk (the remake takes the name
        // that is free again), the list and the summary agreed, and the page's Forget (DELETE /Bookmarks/{id}/Clip) deleted
        // the new file. The real worker, cache and file system here, the system clock, polls as the page makes them.
        var runner = new FakeFfmpegRunner();
        await using var host = await StartAsync(runner);
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var body = JsonSerializer.Serialize(new { ItemId = HeatId, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond, Name = "Bank job" });
        var id = Id((await SendAsync(host, "POST", "/Bookmarks", AliceId, body)).Body.GetProperty("Id"));
        await SendAsync(host, "POST", $"/Bookmarks/{id}/Clip", AliceId, """{"Mode":"Fast"}""");
        var first = await CompletedAsync(store, id, null);
        var path = first.FilePath!;
        File.Delete(path);
        Assert.Equal(("Completed", "Missing"), await StatusAsync(host, id));

        runner.HoldUntilReleased();
        await SendAsync(host, "POST", $"/Bookmarks/{id}/Clip", AliceId, """{"Mode":"Fast"}""");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(("Running", "InProgress"), await StatusAsync(host, id));
        Assert.Equal(("Running", "InProgress"), await StatusAsync(host, id));
        runner.Release();
        var second = await CompletedAsync(store, id, first.RequestedAt);

        Assert.Equal(path, second.FilePath);
        Assert.True(File.Exists(path));
        Assert.Equal(("Completed", "Ready"), await StatusAsync(host, id));
        var row = Assert.Single((await SendAsync(host, "GET", "/Bookmarks/Clips", AliceId)).Body.GetProperty("Items").EnumerateArray());
        Assert.Equal("Ready", row.GetProperty("Clip").GetProperty("State").GetString());
        Assert.Equal(0, (await SendAsync(host, "GET", "/Bookmarks/Clips/Summary", AliceId)).Body.GetProperty("Missing").GetInt32());

        // The page's Forget and Dismiss ask for onlyIfMissing: a clip file that is there is never deleted (409).
        using (var forget = await SendRawAsync(host, "DELETE", $"/Bookmarks/{id}/Clip?onlyIfMissing=true", AliceId))
        {
            Assert.Equal(HttpStatusCode.Conflict, forget.StatusCode);
            Assert.Equal($$"""{"title":"Conflict","status":409,"detail":"{{BookmarkManager.ClipFileIsThereMessage}}"}""", await forget.Content.ReadAsStringAsync(Ct));
        }

        Assert.True(File.Exists(path));
        Assert.Equal(second, store.Get(id)!.Clip);

        // Gone by hand, it is forgotten, and the bookmark stays without its clip; a value that isn't a boolean is a 400.
        File.Delete(path);
        var (status, forgotten) = await SendAsync(host, "DELETE", $"/Bookmarks/{id}/Clip?onlyIfMissing=true", AliceId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(forgotten.TryGetProperty("Clip", out _));
        using var bad = await SendRawAsync(host, "DELETE", $"/Bookmarks/{id}/Clip?onlyIfMissing=perhaps", AliceId);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task BulkDelete_LeavesAClipBeingMade_AsItIs_InProgress()
    {
        // A clip being made isn't deleted; its job runs on, and the clip it makes is the bookmark's.
        var runner = new FakeFfmpegRunner();
        await using var host = await StartAsync(runner);
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var body = JsonSerializer.Serialize(new { ItemId = HeatId, StartTicks = 60 * TestData.OneSecond, EndTicks = 90 * TestData.OneSecond, Name = "Being made" });
        var id = Id((await SendAsync(host, "POST", "/Bookmarks", AliceId, body)).Body.GetProperty("Id"));
        runner.HoldUntilReleased();
        await SendAsync(host, "POST", $"/Bookmarks/{id}/Clip", AliceId, """{"Mode":"Fast"}""");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var (_, result) = await SendAsync(host, "POST", "/Bookmarks/Clips/BulkDelete", AliceId, $$"""{"Ids":["{{id}}"]}""");

        Assert.Equal([id], result.GetProperty("InProgress").EnumerateArray().Select(Id));
        Assert.Empty(result.GetProperty("FilesDeleted").EnumerateArray());
        Assert.Equal(BookmarkClipStatus.Running, store.Get(id)!.Clip!.Status);
        runner.Release();
        var done = await CompletedAsync(store, id, null);
        Assert.True(File.Exists(done.FilePath));
        Assert.Equal(("Completed", "Ready"), await StatusAsync(host, id));
    }

    [Theory]
    [InlineData("GET", "/Bookmarks/Clips", null)]
    [InlineData("GET", "/Bookmarks/Clips/Summary", null)]
    [InlineData("GET", "/Bookmarks/Clips/Status?ids=9fa5c6778cf74a94bff0c82a60581ccf", null)]
    [InlineData("POST", "/Bookmarks/Clips/BulkDelete", """{"Ids":["9fa5c677-8cf7-4a94-bff0-c82a60581ccf"]}""")]
    [InlineData("POST", "/Bookmarks/Clips/9fa5c6778cf74a94bff0c82a60581ccf/Name", """{"Name":"x","RenameFile":true}""")]
    [InlineData("POST", "/Bookmarks/Clips/9fa5c6778cf74a94bff0c82a60581ccf/RenameFile", null)]
    public async Task ApiKeys_Get403_WithTheBookmarksMessage(string method, string path, string? body)
    {
        await using var host = await StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "apikey");
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(BookmarksController.NoUserMessage, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiExplorer_DescribesEveryFilterAsAQueryParameter_AndTheAnswers()
    {
        // What Jellyfin's Swashbuckle documents in /api-docs/openapi.json comes from ASP.NET Core's API explorer.
        await using var host = await StartAsync();
        var descriptions = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .ToList();

        ApiDescription Find(string method, string route) => Assert.Single(descriptions, d => d.HttpMethod == method && d.RelativePath == route);

        var list = Find("GET", "Bookmarks/Clips");
        Assert.Equal(["SearchTerm", "State", "Mode", "ItemTypes", "IncludeDeleted", "ItemId", "StartIndex", "Limit", "sortBy"], list.ParameterDescriptions.Select(p => p.Name));
        Assert.All(list.ParameterDescriptions, p => Assert.Equal(BindingSource.Query, p.Source));
        Assert.Equal(typeof(IReadOnlyList<BookmarkClipState>), list.ParameterDescriptions.Single(p => p.Name == "State").Type);
        Assert.Equal(typeof(IReadOnlyList<BookmarkItemType>), list.ParameterDescriptions.Single(p => p.Name == "ItemTypes").Type);
        // The enum's model type, which Swashbuckle builds the schema from (its Type is string on ASP.NET Core 9).
        Assert.Equal(typeof(BookmarkClipSortBy?), list.ParameterDescriptions.Single(p => p.Name == "sortBy").ModelMetadata?.ModelType);
        Assert.Contains(list.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(MediaBrowser.Model.Querying.QueryResult<BookmarkClipItemDto>));

        var statusIds = Assert.Single(Find("GET", "Bookmarks/Clips/Status").ParameterDescriptions);
        Assert.Equal(("ids", BindingSource.Query, typeof(IReadOnlyList<Guid>)), (statusIds.Name, statusIds.Source, statusIds.Type));
        Assert.Contains(Find("GET", "Bookmarks/Clips/Status").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(IReadOnlyList<BookmarkClipStatusDto>));
        Assert.Contains(Find("GET", "Bookmarks/Clips/Summary").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkClipSummaryDto));
        var bulk = Find("POST", "Bookmarks/Clips/BulkDelete");
        Assert.Equal(typeof(BookmarkClipBulkDeleteRequest), Assert.Single(bulk.ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Contains(bulk.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkClipBulkDeleteResultDto));
        Assert.Equal([200, 400, 413, 503], bulk.SupportedResponseTypes.Select(r => r.StatusCode).Order());

        // The renames.
        var rename = Find("POST", "Bookmarks/Clips/{bookmarkId}/Name");
        Assert.Equal(typeof(BookmarkClipRenameRequest), Assert.Single(rename.ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Contains(rename.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkClipItemDto));
        Assert.Equal([200, 400, 404, 409, 413, 503], rename.SupportedResponseTypes.Select(r => r.StatusCode).Order());
        // Rename file's optional body, its IfUpdatedAt (a request without one still works).
        var renameFile = Find("POST", "Bookmarks/Clips/{bookmarkId}/RenameFile");
        var renameFileBody = Assert.Single(renameFile.ParameterDescriptions, p => p.Source == BindingSource.Body);
        Assert.Equal((typeof(BookmarkClipRenameFileRequest), false), (renameFileBody.Type, renameFileBody.IsRequired));
        Assert.Contains(renameFile.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkClipItemDto));
        Assert.Equal([200, 400, 404, 409, 413, 503], renameFile.SupportedResponseTypes.Select(r => r.StatusCode).Order());

        // The single delete's Forget: an optional query parameter of the bookmark route.
        var deleteClip = Find("DELETE", "Bookmarks/{bookmarkId}/Clip");
        var onlyIfMissing = Assert.Single(deleteClip.ParameterDescriptions, p => p.Name == "onlyIfMissing");
        Assert.Equal((BindingSource.Query, typeof(bool?), false), (onlyIfMissing.Source, onlyIfMissing.Type, onlyIfMissing.IsRequired));
    }

    [Fact]
    public async Task ARename_OnTheWire_IsTheClipAsTheListMapsIt_TheNameAndTheFileInOneChange_AndItsRefusalsAreProblemDetails()
    {
        await using var host = await StartAsync();
        var id = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var before = store.Get(id)!;

        var (status, body) = await SendAsync(host, "POST", $"/Bookmarks/Clips/{id:N}/Name", AliceId, $$"""{"Name":"Vault","RenameFile":true,"IfUpdatedAt":"{{before.UpdatedAt:O}}"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id, Id(body.GetProperty("Id")));
        Assert.Equal("Vault", body.GetProperty("Name").GetString());
        var clip = body.GetProperty("Clip");
        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", clip.GetProperty("FileName").GetString());
        Assert.Equal("Ready", clip.GetProperty("State").GetString());
        Assert.False(clip.GetProperty("IsNameOutdated").GetBoolean());
        Assert.DoesNotContain(JsonSerializer.Serialize(_data.Path).Trim('"'), body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(ClipFolder, "alice", "Heat (1995) - Vault [00.01.00-00.01.30].mp4")));
        Assert.False(File.Exists(before.Clip!.FilePath));

        // A name over 100 characters is 400; another user's clip, or none, 404; a stale IfUpdatedAt 409: each ProblemDetails.
        foreach (var (path, json, code, detail) in new (string, string, int, string)[]
        {
            ($"/Bookmarks/Clips/{id:N}/Name", JsonSerializer.Serialize(new { Name = new string('n', 101), RenameFile = true }), 400, BookmarkNames.TooLongMessage),
            ($"/Bookmarks/Clips/{bobs:N}/Name", """{"Name":"Mine","RenameFile":true}""", 404, ClipFileRenamer.NotFoundMessage),
            ($"/Bookmarks/Clips/{bobs:N}/RenameFile", "", 404, ClipFileRenamer.NotFoundMessage),
            ($"/Bookmarks/Clips/{id:N}/Name", JsonSerializer.Serialize(new { Name = "Late", IfUpdatedAt = before.UpdatedAt }), 409, BookmarkManager.ChangedElsewhereMessage)
        })
        {
            using var refused = await SendRawAsync(host, "POST", path, AliceId, json.Length == 0 ? null : json);
            Assert.Equal(code, (int)refused.StatusCode);
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
        }

        Assert.Equal("Vault", store.Get(id)!.Name);
        Assert.Equal("Bob's", store.Get(bobs)!.Name);

        // Renamed elsewhere (the bookmark's own rename keeps the file's name): Name changed, then Rename file.
        await SendAsync(host, "POST", $"/Bookmarks/{id}/Name", AliceId, """{"Name":"Strongroom"}""");
        (_, var list) = await SendAsync(host, "GET", "/Bookmarks/Clips", AliceId);
        Assert.True(list.GetProperty("Items").EnumerateArray().Single(r => Id(r.GetProperty("Id")) == id).GetProperty("Clip").GetProperty("IsNameOutdated").GetBoolean());
        (_, var file) = await SendAsync(host, "POST", $"/Bookmarks/Clips/{id}/RenameFile", AliceId);
        Assert.Equal("Heat (1995) - Strongroom [00.01.00-00.01.30].mp4", file.GetProperty("Clip").GetProperty("FileName").GetString());
        Assert.False(file.GetProperty("Clip").GetProperty("IsNameOutdated").GetBoolean());
    }

    [Fact]
    public async Task ARenameOfAFileHeldOpen_Is409WithNothingChanged_AndRenamesOnceItIsClosed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to rename a file open without delete sharing.");
        await using var host = await StartAsync();
        var id = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var path = store.Get(id)!.Clip!.FilePath!;
        var storeFile = Path.Combine(_data.Path, "bookmarks", "bookmarks.json");
        var bytes = await File.ReadAllBytesAsync(storeFile, Ct);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var inUse = await SendRawAsync(host, "POST", $"/Bookmarks/Clips/{id:N}/Name", AliceId, """{"Name":"Vault","RenameFile":true}""");
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
            using var problem = JsonDocument.Parse(await inUse.Content.ReadAsStringAsync(Ct));
            Assert.Equal(ClipFileOps.InUseMessage, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(storeFile, Ct));
            Assert.Equal("Bank job", store.Get(id)!.Name);
        }

        (_, var released) = await SendAsync(host, "POST", $"/Bookmarks/Clips/{id:N}/Name", AliceId, """{"Name":"Vault","RenameFile":true}""");
        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", released.GetProperty("Clip").GetProperty("FileName").GetString());
    }

    [Fact]
    public async Task ARemakeWhileARenameIsntFinished_Is503_AndTheNextRequestThatNeedsItFinishesIt()
    {
        await using var host = await StartAsync();
        var id = await CreateWithClipAsync(host, AliceId, 60, "Bank job");
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var path = store.Get(id)!.Clip!.FilePath!;

        // A rename that isn't finished (its record's write failed, and so did the move back) whose clip folder is away now: a
        // remake would delete the file its record names and leave the renamed one no clip's, so it is 503 until the rename is
        // finished.
        var renamed = Path.Combine(ClipFolder, "alice", "Heat (1995) - Moved [00.01.00-00.01.30].mp4");
        host.Services.GetRequiredService<ClipFileOps>().Journal!.Add(new ClipRenameEntry
        {
            BookmarkId = id,
            From = path,
            To = renamed,
            File = PhysicalClipFileSystem.ReadFileIdentity(path),
            UpdatedAt = store.Get(id)!.UpdatedAt,
            StartedUtc = DateTime.UtcNow
        });
        File.Move(path, renamed);
        Directory.Move(ClipFolder, ClipFolder + "-away");
        try
        {
            using var remake = await SendRawAsync(host, "POST", $"/Bookmarks/{id}/Clip", AliceId, """{"Mode":"Fast"}""");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, remake.StatusCode);
            using var problem = JsonDocument.Parse(await remake.Content.ReadAsStringAsync(Ct));
            Assert.Equal(ClipFileOps.RenameUnfinishedMessage, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(path, store.Get(id)!.Clip!.FilePath);
        }
        finally
        {
            Directory.Move(ClipFolder + "-away", ClipFolder);
        }

        // Back: the next request that needs it finishes the rename first.
        (_, var renamedNow) = await SendAsync(host, "POST", $"/Bookmarks/Clips/{id:N}/RenameFile", AliceId);
        // (finished: the record named the moved file, whose name no longer says the clip's name, so the file got it back).
        Assert.Equal("Heat (1995) - Bank job [00.01.00-00.01.30].mp4", renamedNow.GetProperty("Clip").GetProperty("FileName").GetString());
        Assert.Equal(path, store.Get(id)!.Clip!.FilePath);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(renamed));
        Assert.Empty(Directory.GetFiles(Path.Combine(_data.Path, "bookmarks"), ClipRenameJournal.FilePrefix + "*"));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> ValidationProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var problem = document.RootElement.Clone();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", problem.GetProperty("title").GetString());
        return problem;
    }

    // Jellyfin's JSON options write GUIDs without dashes ("N"), which JsonElement.GetGuid doesn't read.
    private static Guid Id(JsonElement element) => Guid.Parse(element.GetString()!);

    private static Task<HttpResponseMessage> SendRawAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
        => host.SendAsync(new HttpMethod(method), path, request =>
        {
            request.Headers.Add(JellyfinAuth.CallerHeader, "user");
            request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(WebHostHarness host, string method, string path, Guid userId, string? json = null)
    {
        using var response = await SendRawAsync(host, method, path, userId, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body.Length == 0 ? "null" : body);
        return (response.StatusCode, document.RootElement.Clone());
    }

    // One clip's Status and State as the page's poll reads them (Alice's).
    private static async Task<(string? Status, string? State)> StatusAsync(WebHostHarness host, Guid id)
    {
        var (_, body) = await SendAsync(host, "GET", $"/Bookmarks/Clips/Status?ids={id:N}", AliceId);
        var clip = Assert.Single(body.EnumerateArray());
        return (clip.GetProperty("Status").GetString(), clip.GetProperty("State").GetString());
    }

    // The bookmark's clip once its job (another than the one asked for at `previous`) has completed.
    private static async Task<ClipInfo> CompletedAsync(IBookmarkStore store, Guid id, DateTime? previous)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (store.Get(id)?.Clip is not { Status: BookmarkClipStatus.Completed } clip || clip.RequestedAt == previous)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the clip.");
            await Task.Delay(20, Ct);
        }

        return store.Get(id)!.Clip!;
    }

    // A bookmark of the user's on Heat with a range of 30 s and a clip as the worker leaves it: its file in the user's clip
    // folder (named as the planner names it), unless the job had none.
    private async Task<Guid> CreateWithClipAsync(
        WebHostHarness host,
        Guid userId,
        int seconds,
        string name,
        BookmarkClipStatus status = BookmarkClipStatus.Completed,
        BookmarkClipMode mode = BookmarkClipMode.Accurate,
        bool file = true)
    {
        var body = JsonSerializer.Serialize(new { ItemId = HeatId, StartTicks = seconds * TestData.OneSecond, EndTicks = (seconds + 30) * TestData.OneSecond, Name = name });
        var (_, created) = await SendAsync(host, "POST", "/Bookmarks", userId, body);
        var id = Id(created.GetProperty("Id"));
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddClipRootAsync(ClipFolder, Ct);
        string? path = null;
        if (file)
        {
            var folder = Path.Combine(ClipFolder, userId == AliceId ? "alice" : "bob");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, ClipFileNaming.BuildFileName("Heat (1995)", name, seconds * TestData.OneSecond, (seconds + 30) * TestData.OneSecond, mode));
            await File.WriteAllTextAsync(path, "clip", Ct);
        }

        await store.UpdateAsync(
            id,
            b => b with
            {
                Clip = new ClipInfo
                {
                    Status = status,
                    Mode = mode,
                    StartTicks = b.StartTicks,
                    EndTicks = b.EndTicks!.Value,
                    RequestedAt = DateTime.UtcNow,
                    CompletedAt = status is BookmarkClipStatus.Queued or BookmarkClipStatus.Running ? null : DateTime.UtcNow,
                    Error = status == BookmarkClipStatus.Failed ? "ffmpeg failed" : null,
                    FilePath = path,
                    FileSize = path is null ? null : 4,
                    FileStartTicks = path is null ? null : b.StartTicks,
                    FileEndTicks = path is null ? null : b.EndTicks
                }
            },
            Ct);
        return id;
    }

    private Task<WebHostHarness> StartAsync(FakeFfmpegRunner? runner = null)
    {
        // The plugin's own registrations for everything this touches; Jellyfin's services are mocks, ffmpeg is fake.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(ClipExtractionService), typeof(IClipExtractionService), typeof(ClipFileOps), typeof(PartialFileJanitor), typeof(IBookmarkManager),
            typeof(IBookmarkQuery), typeof(IBookmarkPreferences), typeof(ClipStorageGuard), typeof(IDriveSpaceProbe),

            // The My Clips page's service, its list, the two caches and the kit's LibraryAccess (a library's name).
            typeof(IBookmarkClips), typeof(ClipQueryService), typeof(ClipStateCache), typeof(ClipLibraryLocations), typeof(LibraryAccess),
            .. TestData.LibraryServiceTypes
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(AliceId)).Returns(_alice);
        users.Setup(m => m.GetUserById(BobId)).Returns(_bob);
        var heat = new MediaBrowser.Controller.Entities.Movies.Movie { Id = HeatId, Name = "Heat", ProductionYear = 1995, RunTimeTicks = 3000 * TestData.OneSecond, Path = @"C:\media\Heat (1995).mkv" };
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<User?>()))
            .Returns((Guid id, User? user) => user is not null && id == HeatId ? heat : null);
        library.Setup(m => m.GetVirtualFolders()).Returns([]);

        return new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => own.Contains(d.ServiceType)
                    || (d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)))
                {
                    services.Add(descriptor);
                }

                // Jellyfin's download policy (the clip file route's) needs Jellyfin.Api; any signed-in user stands in here.
                services.AddAuthorizationCore(options => options.AddPolicy(
                    MediaBrowser.Common.Api.Policies.Download,
                    policy => policy.AddAuthenticationSchemes(JellyfinAuth.Scheme).RequireAuthenticatedUser()));
                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(users.Object);
                services.AddSingleton(library.Object);
                services.AddSingleton(TestData.ConfigurationProvider(new PluginConfiguration()));
                services.AddSingleton<IClipPlanner>(new FakeClipPlanner(_data));
                services.AddSingleton<IFfmpegRunner>(runner ?? new FakeFfmpegRunner());

                // The clips made through the route here are free-space checked by a probe that finds room.
                services.AddSingleton<IDriveSpaceProbe>(new FakeDriveSpaceProbe());
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                {
                    var jellyfin = JsonDefaults.PascalCaseOptions;
                    o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                    o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                    o.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jellyfin.Converters)
                    {
                        o.JsonSerializerOptions.Converters.Add(converter);
                    }
                });
            })
            .WithMvc(typeof(BookmarksController), typeof(BookmarksClipsController))
            .StartAsync();
    }
}
