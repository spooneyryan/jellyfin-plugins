using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
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
/// The admins' clip routes on the wire (admin routes 403 for non-admins, and for admins with the flag off), behind
/// Jellyfin's own <c>RequiresElevation</c> as the kit's test
/// authentication copies it: a member, and an admin demoted since their token was issued (Jellyfin sets the role from the
/// stored user at every request, so the same token now carries the user role), 403 on every route; a disabled user (whose
/// token Jellyfin no longer authenticates) and a request without a token 401; an API key admitted like an admin; with
/// <c>AdminsCanManageAllClips</c> off every route but Summary 403. The list never carries a note, the file route answers as the
/// owner's (an attachment named as the file is, ranges, a held file still served, a missing one 404, no path), the deletes
/// and the capabilities' <c>CanManageAllClips</c>, and what the API explorer says about the routes.
/// </summary>
public sealed class AdminClipsApiTests : IAsyncLifetime
{
    private static readonly Guid AliceId = new("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid BobId = new("ff71b896-f205-45ff-936a-c2393f6091f6");
    private static readonly Guid CarolId = new("c4a7d1e2-0b3f-4c5d-8e9f-a0b1c2d3e4f5");
    private static readonly Guid HeatId = new("ba7ef8f5-0757-f5f6-0cae-61b9b7fd19f5");
    private const string BobsNote = "Bob's private words about the vault";

    private readonly TempDirectory _data = new();
    private readonly User _alice = TestData.CreateUser("alice");
    private readonly User _bob = TestData.CreateUser("bob");
    private readonly User _carol = TestData.CreateUser("carol", canDownload: false);
    private readonly PluginConfiguration _configuration = new();

    public AdminClipsApiTests()
    {
        _alice.Id = AliceId;
        _bob.Id = BobId;
        _carol.Id = CarolId;
        _alice.SetPermission(PermissionKind.IsAdministrator, true);
        _carol.SetPermission(PermissionKind.IsAdministrator, true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ClipFolder => Path.Combine(_data.Path, "bookmarks", "clips");

    private string StoreFile => Path.Combine(_data.Path, "bookmarks", "bookmarks.json");

    // Every admin clip route, {id} for a clip id.
    private static readonly (string Method, string Route)[] RouteList =
    [
        ("GET", "/Bookmarks/Admin/Clips"),
        ("GET", "/Bookmarks/Admin/Clips/Summary"),
        ("GET", "/Bookmarks/Admin/Clips/Status?ids={id}"),
        ("GET", "/Bookmarks/Admin/Clips/{id}/File"),
        ("POST", "/Bookmarks/Admin/Clips/{id}/Cancel"),
        ("DELETE", "/Bookmarks/Admin/Clips/{id}"),
        ("DELETE", "/Bookmarks/Admin/Clips/{id}?onlyIfMissing=true"),
        ("POST", "/Bookmarks/Admin/Clips/BulkDelete")
    ];

    public static TheoryData<string, string> Routes
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (method, route) in RouteList)
            {
                data.Add(method, route);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryAdminRoute_RefusesAMemberAndADemotedAdminsToken403_ADisabledOrSignedOutCaller401_AndChangesNothing(string method, string route)
    {
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);
        var file = host.Services.GetRequiredService<IBookmarkStore>().Get(bobs)!.Clip!.FilePath!;
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);
        var path = route.Replace("{id}", bobs.ToString("N"), StringComparison.Ordinal);

        // A member; and Alice's own id with the user role, as Jellyfin authenticates an admin's token after the admin was demoted.
        foreach (var (caller, userId) in new[] { ("user", BobId), ("user", AliceId) })
        {
            using var refused = await SendRawAsync(host, method, path, caller, userId, Body(route, bobs));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.DoesNotContain(BobsNote, await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // A disabled user's token (Jellyfin's authentication fails it: "User account has been disabled."), and no token.
        foreach (var caller in new[] { "disabled", null })
        {
            using var refused = await SendRawAsync(host, method, path, caller, AliceId, Body(route, bobs));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task AnApiKey_IsAdmittedLikeAnAdmin_WhileTheSettingIsOn_AndWithItOffEveryGatedRouteIs403_TheSummaryStill200()
    {
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);

        var (status, list) = await SendAsync(host, "GET", "/Bookmarks/Admin/Clips", "apikey", Guid.Empty);
        Assert.Equal(HttpStatusCode.OK, status);
        var row = Assert.Single(list.GetProperty("Items").EnumerateArray());
        Assert.Equal(bobs, Id(row.GetProperty("Id")));
        Assert.False(row.GetProperty("ItemAvailable").GetBoolean(), "an API key has no user to open the video as");

        _configuration.AdminsCanManageAllClips = false;
        foreach (var (caller, userId) in new[] { ("apikey", Guid.Empty), ("admin", AliceId) })
        {
            foreach (var (method, route) in RouteList)
            {
                using var response = await SendRawAsync(host, method, route.Replace("{id}", bobs.ToString("N"), StringComparison.Ordinal), caller, userId, Body(route, bobs));
                if (route.EndsWith("/Summary", StringComparison.Ordinal))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    continue;
                }

                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{caller} {method} {route}: {response.StatusCode}");
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                Assert.Equal(AdminClipService.TurnedOffMessage, problem.RootElement.GetProperty("detail").GetString());
            }
        }

        Assert.NotNull(host.Services.GetRequiredService<IBookmarkStore>().Get(bobs)!.Clip);
    }

    [Fact]
    public async Task TheList_OnTheWire_NamesTheOwnerAndThePath_AndNeverTheNote_WhichTheOwnerStillSees()
    {
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);
        var alices = await CreateWithClipAsync(host, AliceId, 120, "Alice's", "Alice's own note");
        await SendAsync(host, "DELETE", $"/Bookmarks/{alices}?deleteClip=false", "user", AliceId);

        var (_, body) = await SendAsync(host, "GET", "/Bookmarks/Admin/Clips?sortBy=Name", "admin", AliceId);

        Assert.Equal(["Items", "TotalRecordCount", "StartIndex"], body.EnumerateObject().Select(p => p.Name));
        var rows = body.GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal([alices, bobs], rows.Select(r => Id(r.GetProperty("Id"))));
        var row = rows[1];
        Assert.Equal(
            ["Id", "UserId", "UserName", "UserDeleted", "BookmarkDeleted", "Origin", "Name", "ItemId", "ItemName", "ItemType", "ItemAvailable", "BookmarkStartTicks", "BookmarkEndTicks", "Clip", "FilePath", "RootId", "CreatedAt", "UpdatedAt"],
            row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("bob", false, "Bob's"), (row.GetProperty("UserName").GetString(), row.GetProperty("UserDeleted").GetBoolean(), row.GetProperty("Name").GetString()));
        Assert.Equal(BobId, Id(row.GetProperty("UserId")));
        Assert.StartsWith(ClipFolder, row.GetProperty("FilePath").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, row.GetProperty("RootId").GetString()!.Length);
        Assert.True(rows[0].GetProperty("BookmarkDeleted").GetBoolean());

        // The note is in no admin answer: not the list, not a search for its words, not a row the cancel answers with.
        Assert.DoesNotContain("Note", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private words", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("own note", body.GetRawText(), StringComparison.Ordinal);
        (_, var search) = await SendAsync(host, "GET", "/Bookmarks/Admin/Clips?searchTerm=vault", "admin", AliceId);
        Assert.Equal(0, search.GetProperty("TotalRecordCount").GetInt32());
        (_, var cancelled) = await SendAsync(host, "POST", $"/Bookmarks/Admin/Clips/{bobs:N}/Cancel", "admin", AliceId);
        Assert.DoesNotContain("private words", cancelled.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("bob", cancelled.GetProperty("UserName").GetString());

        // The owner filter, and the owner's own list, which keeps the note and no path.
        (_, var only) = await SendAsync(host, "GET", $"/Bookmarks/Admin/Clips?userId={BobId:N}", "admin", AliceId);
        Assert.Equal([bobs], only.GetProperty("Items").EnumerateArray().Select(r => Id(r.GetProperty("Id"))));
        (_, var own) = await SendAsync(host, "GET", "/Bookmarks/Clips", "user", BobId);
        var ownRow = Assert.Single(own.GetProperty("Items").EnumerateArray());
        Assert.Equal(BobsNote, ownRow.GetProperty("Note").GetString());
        Assert.False(ownRow.TryGetProperty("FilePath", out _));

        // The totals: per user, by name.
        (_, var summary) = await SendAsync(host, "GET", "/Bookmarks/Admin/Clips/Summary", "admin", AliceId);
        Assert.Equal(["alice", "bob"], summary.GetProperty("Users").EnumerateArray().Select(u => u.GetProperty("UserName").GetString()));
        Assert.Equal(2, summary.GetProperty("Count").GetInt32());
        Assert.DoesNotContain("Bob's", summary.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFile_IsServedAsTheOwnersRouteServesIt_NamedAsTheFileIs_WithRanges_AndAMissingOneIs404WithoutAPath()
    {
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var file = store.Get(bobs)!.Clip!.FilePath!;

        using (var download = await SendRawAsync(host, "GET", $"/Bookmarks/Admin/Clips/{bobs:N}/File", "admin", AliceId))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("video/mp4", download.Content.Headers.ContentType?.MediaType);
            var disposition = download.Content.Headers.ContentDisposition!;
            Assert.Equal("attachment", disposition.DispositionType);
            Assert.Equal(Path.GetFileName(file), disposition.FileNameStar ?? disposition.FileName?.Trim('"'));
            Assert.DoesNotContain(ClipFolder, download.Content.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("clip", await download.Content.ReadAsStringAsync(Ct));
        }

        using (var preview = await SendRawAsync(host, "GET", $"/Bookmarks/Admin/Clips/{bobs:N}/File?download=false", "admin", AliceId, configure: r => r.Headers.Range = new RangeHeaderValue(1, 2)))
        {
            Assert.Equal(HttpStatusCode.PartialContent, preview.StatusCode);
            Assert.Null(preview.Content.Headers.ContentDisposition);
            Assert.Equal("li", await preview.Content.ReadAsStringAsync(Ct));
        }

        // A file held open by a reader that shares reading and writing (a preview elsewhere, Jellyfin playing it) is still
        // served, as the owner's route serves it.
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var held = await SendRawAsync(host, "GET", $"/Bookmarks/Admin/Clips/{bobs:N}/File", "admin", AliceId);
            Assert.Equal(HttpStatusCode.OK, held.StatusCode);
            using var own = await SendRawAsync(host, "GET", $"/Bookmarks/{bobs}/Clip/File", "user", BobId);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        // An admin without Jellyfin's download permission may not download, as on the owner's route.
        using (var noDownload = await SendRawAsync(host, "GET", $"/Bookmarks/Admin/Clips/{bobs:N}/File", "admin", CarolId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noDownload.StatusCode);
        }

        // Gone: 404 with the owner's route's sentence and no path, on both routes.
        File.Delete(file);
        foreach (var (path, caller, userId) in new[] { ($"/Bookmarks/Admin/Clips/{bobs:N}/File", "admin", AliceId), ($"/Bookmarks/{bobs}/Clip/File", "user", BobId) })
        {
            using var missing = await SendRawAsync(host, "GET", path, caller, userId);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var text = await missing.Content.ReadAsStringAsync(Ct);
            using var problem = JsonDocument.Parse(text);
            Assert.Equal(BookmarkManager.NoClipFileMessage, problem.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain(JsonSerializer.Serialize(_data.Path).Trim('"'), text, StringComparison.OrdinalIgnoreCase);
        }

        using var unknown = await SendRawAsync(host, "GET", $"/Bookmarks/Admin/Clips/{Guid.NewGuid():N}/File", "admin", AliceId);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task TheDeletes_OnTheWire_204_TheBulkResult_AndTheirRefusalsAreProblemDetails()
    {
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);
        var kept = await CreateWithClipAsync(host, BobId, 120, "Kept", null);
        await SendAsync(host, "DELETE", $"/Bookmarks/{kept}?deleteClip=false", "user", BobId);
        var gone = await CreateWithClipAsync(host, BobId, 180, "Gone", null);
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        File.Delete(store.Get(gone)!.Clip!.FilePath!);
        var bobsFile = store.Get(bobs)!.Clip!.FilePath!;

        // Forget refuses a file that is there; the delete deletes it.
        using (var there = await SendRawAsync(host, "DELETE", $"/Bookmarks/Admin/Clips/{bobs:N}?onlyIfMissing=true", "admin", AliceId))
        {
            Assert.Equal(HttpStatusCode.Conflict, there.StatusCode);
            using var problem = JsonDocument.Parse(await there.Content.ReadAsStringAsync(Ct));
            Assert.Equal(BookmarkManager.ClipFileIsThereMessage, problem.RootElement.GetProperty("detail").GetString());
        }

        using (var deleted = await SendRawAsync(host, "DELETE", $"/Bookmarks/Admin/Clips/{bobs:N}", "admin", AliceId))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.False(File.Exists(bobsFile));
        Assert.Null(store.Get(bobs)!.Clip);
        (_, var stays) = await SendAsync(host, "GET", $"/Bookmarks/{bobs}", "user", BobId);
        Assert.Equal("Bob's", stays.GetProperty("Name").GetString());

        var unknown = Guid.NewGuid();
        var (status, result) = await SendAsync(host, "POST", "/Bookmarks/Admin/Clips/BulkDelete", "admin", AliceId, $$"""{"Ids":["{{kept}}","{{gone}}","{{unknown}}"]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["FilesDeleted", "RecordsRemoved", "NotFound", "InProgress", "InUse", "FolderUnavailable", "Failed"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal([gone], result.GetProperty("FilesDeleted").EnumerateArray().Select(Id));
        Assert.Equal([kept], result.GetProperty("RecordsRemoved").EnumerateArray().Select(Id));
        Assert.Equal([unknown], result.GetProperty("NotFound").EnumerateArray().Select(Id));
        Assert.Null(store.Get(kept));

        // A path outside the clip folders is the admins' 409, nothing touched.
        var outside = Path.Combine(_data.Path, "elsewhere.mp4");
        await File.WriteAllTextAsync(outside, "not a clip of ours", Ct);
        var stray = await CreateWithClipAsync(host, BobId, 240, "Stray", null);
        await store.UpdateAsync(stray, b => b with { Clip = b.Clip! with { FilePath = outside } }, Ct);
        using (var refused = await SendRawAsync(host, "DELETE", $"/Bookmarks/Admin/Clips/{stray:N}", "admin", AliceId))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal(ClipFileOps.OutsideClipFoldersMessage, problem.RootElement.GetProperty("detail").GetString());
        }

        Assert.Equal("not a clip of ours", await File.ReadAllTextAsync(outside, Ct));

        foreach (var (json, detail) in new[] { ("""{"Ids":[]}""", ClipService.BulkDeleteNoIdsMessage) })
        {
            using var bad = await SendRawAsync(host, "POST", "/Bookmarks/Admin/Clips/BulkDelete", "admin", AliceId, json);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal($$"""{"title":"Bad Request","status":400,"detail":"{{detail}}"}""", await bad.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task ADeleteOfAFileHeldOpen_Is409WithNothingChanged_AndDeletesOnceItIsClosed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete a file open without delete sharing.");
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);
        var file = host.Services.GetRequiredService<IBookmarkStore>().Get(bobs)!.Clip!.FilePath!;
        var before = await File.ReadAllBytesAsync(StoreFile, Ct);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var inUse = await SendRawAsync(host, "DELETE", $"/Bookmarks/Admin/Clips/{bobs:N}", "admin", AliceId);
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
            using var problem = JsonDocument.Parse(await inUse.Content.ReadAsStringAsync(Ct));
            Assert.Equal(ClipFileOps.InUseMessage, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile, Ct));

            var (_, bulk) = await SendAsync(host, "POST", "/Bookmarks/Admin/Clips/BulkDelete", "admin", AliceId, $$"""{"Ids":["{{bobs}}"]}""");
            Assert.Equal([bobs], bulk.GetProperty("InUse").EnumerateArray().Select(Id));
            Assert.True(File.Exists(file));
        }

        using var released = await SendRawAsync(host, "DELETE", $"/Bookmarks/Admin/Clips/{bobs:N}", "admin", AliceId);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task TheCapabilities_OfferTheAllUsersScope_ToAnAdminOnly_WhileTheSettingIsOn()
    {
        await using var host = await StartAsync();

        async Task<bool> CanManageAllClipsAsync(string caller, Guid userId)
        {
            var (_, capabilities) = await SendAsync(host, "GET", "/Bookmarks/Capabilities", caller, userId);
            return capabilities.GetProperty("CanManageAllClips").GetBoolean();
        }

        Assert.True(await CanManageAllClipsAsync("admin", AliceId));
        Assert.False(await CanManageAllClipsAsync("user", BobId));
        Assert.False(await CanManageAllClipsAsync("user", AliceId), "a demoted admin's token");
        _configuration.AdminsCanManageAllClips = false;
        Assert.False(await CanManageAllClipsAsync("admin", AliceId));
    }

    [Fact]
    public async Task AnAdmin_CannotRenameAnotherUsersClip_TheOwnersRoutesAre404_AndTheAdminsHaveNone()
    {
        // No rename or remake of other users' clips. The owner's rename routes find only the
        // caller's own clips (404 for Bob's, asked by an admin), and nothing under Admin/Clips renames.
        await using var host = await StartAsync();
        var bobs = await CreateWithClipAsync(host, BobId, 60, "Bob's", BobsNote);
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        var before = store.Get(bobs);

        (string Method, string Route, string? Body)[] attempts =
        [
            ("POST", $"/Bookmarks/Clips/{bobs:N}/Name", """{"Name":"Alice's now","RenameFile":true}"""),
            ("POST", $"/Bookmarks/Clips/{bobs:N}/RenameFile", null),
            ("POST", $"/Bookmarks/Admin/Clips/{bobs:N}/Name", """{"Name":"Alice's now","RenameFile":true}"""),
            ("POST", $"/Bookmarks/Admin/Clips/{bobs:N}/RenameFile", null)
        ];
        foreach (var (method, route, body) in attempts)
        {
            using var response = await SendRawAsync(host, method, route, "admin", AliceId, body);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{method} {route}: {response.StatusCode}");
        }

        Assert.Equal(before, store.Get(bobs));
        Assert.True(File.Exists(before!.Clip!.FilePath!));
    }

    [Fact]
    public async Task TheApiExplorer_DescribesTheAdminRoutes_TheirParametersAndAnswers()
    {
        await using var host = await StartAsync();
        var descriptions = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Where(d => d.RelativePath?.StartsWith("Bookmarks/Admin/Clips", StringComparison.Ordinal) == true)
            .ToList();

        ApiDescription Find(string method, string route) => Assert.Single(descriptions, d => d.HttpMethod == method && d.RelativePath == route);

        Assert.Equal(7, descriptions.Count);
        var list = Find("GET", "Bookmarks/Admin/Clips");
        Assert.Equal(["SearchTerm", "State", "Mode", "ItemTypes", "IncludeDeleted", "ItemId", "StartIndex", "Limit", "userId", "sortBy"], list.ParameterDescriptions.Select(p => p.Name));
        Assert.All(list.ParameterDescriptions, p => Assert.Equal(BindingSource.Query, p.Source));
        Assert.Contains(list.SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(MediaBrowser.Model.Querying.QueryResult<BookmarkAdminClipItemDto>));
        Assert.Contains(Find("GET", "Bookmarks/Admin/Clips/Summary").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkAdminClipSummaryDto));
        Assert.Contains(Find("GET", "Bookmarks/Admin/Clips/Status").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(IReadOnlyList<BookmarkClipStatusDto>));
        Assert.Equal([200, 403, 404], Find("GET", "Bookmarks/Admin/Clips/{bookmarkId}/File").SupportedResponseTypes.Select(r => r.StatusCode).Order());
        Assert.Contains(Find("POST", "Bookmarks/Admin/Clips/{bookmarkId}/Cancel").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(BookmarkAdminClipItemDto));
        var delete = Find("DELETE", "Bookmarks/Admin/Clips/{bookmarkId}");
        Assert.Equal([204, 403, 404, 409, 503], delete.SupportedResponseTypes.Select(r => r.StatusCode).Order());
        var onlyIfMissing = Assert.Single(delete.ParameterDescriptions, p => p.Name == "onlyIfMissing");
        Assert.Equal((BindingSource.Query, typeof(bool?), false), (onlyIfMissing.Source, onlyIfMissing.Type, onlyIfMissing.IsRequired));
        var bulk = Find("POST", "Bookmarks/Admin/Clips/BulkDelete");
        Assert.Equal(typeof(BookmarkClipBulkDeleteRequest), Assert.Single(bulk.ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Equal([200, 400, 403, 413, 503], bulk.SupportedResponseTypes.Select(r => r.StatusCode).Order());
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    // The bulk delete's body for a route that takes one.
    private static string? Body(string route, Guid id) => route.EndsWith("/BulkDelete", StringComparison.Ordinal) ? $$"""{"Ids":["{{id}}"]}""" : null;

    // Jellyfin's JSON options write GUIDs without dashes ("N"), which JsonElement.GetGuid doesn't read.
    private static Guid Id(JsonElement element) => Guid.Parse(element.GetString()!);

    // A request as a caller: "admin", "user", "apikey", "disabled" (an authentication that fails, as Jellyfin's for a disabled
    // user) or none at all (no token).
    private static Task<HttpResponseMessage> SendRawAsync(WebHostHarness host, string method, string path, string? caller, Guid userId, string? json = null, Action<HttpRequestMessage>? configure = null)
        => host.SendAsync(new HttpMethod(method), path, request =>
        {
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
                request.Headers.Add(JellyfinAuth.UserIdHeader, userId.ToString("N"));
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            configure?.Invoke(request);
        });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(WebHostHarness host, string method, string path, string caller, Guid userId, string? json = null)
    {
        using var response = await SendRawAsync(host, method, path, caller, userId, json);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body.Length == 0 ? "null" : body);
        return (response.StatusCode, document.RootElement.Clone());
    }

    // A bookmark of the user's on Heat with a range of 30 s and a finished clip, its file in the user's clip folder.
    private async Task<Guid> CreateWithClipAsync(WebHostHarness host, Guid userId, int seconds, string name, string? note)
    {
        var body = JsonSerializer.Serialize(new { ItemId = HeatId, StartTicks = seconds * TestData.OneSecond, EndTicks = (seconds + 30) * TestData.OneSecond, Name = name, Note = note });
        var (_, created) = await SendAsync(host, "POST", "/Bookmarks", "user", userId, body);
        var id = Id(created.GetProperty("Id"));
        var store = host.Services.GetRequiredService<IBookmarkStore>();
        await store.AddClipRootAsync(ClipFolder, Ct);
        var folder = Path.Combine(ClipFolder, userId == AliceId ? "alice" : "bob");
        Directory.CreateDirectory(folder);
        if (!File.Exists(ClipRootMarker.PathIn(ClipFolder)))
        {
            await ClipFileSupport.MarkClipFolderAsync(store, ClipFolder);
        }

        var path = Path.Combine(folder, ClipFileNaming.BuildFileName("Heat (1995)", name, seconds * TestData.OneSecond, (seconds + 30) * TestData.OneSecond, BookmarkClipMode.Accurate));
        await File.WriteAllTextAsync(path, "clip", Ct);
        await store.UpdateAsync(
            id,
            b => b with
            {
                Clip = new ClipInfo
                {
                    Status = BookmarkClipStatus.Completed,
                    Mode = BookmarkClipMode.Accurate,
                    StartTicks = b.StartTicks,
                    EndTicks = b.EndTicks!.Value,
                    RequestedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                    FilePath = path,
                    FileSize = 4,
                    FileStartTicks = b.StartTicks,
                    FileEndTicks = b.EndTicks
                }
            },
            Ct);
        return id;
    }

    private Task<WebHostHarness> StartAsync()
    {
        // The plugin's own registrations for everything this touches; Jellyfin's services are mocks, ffmpeg is fake.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Type[] own =
        [
            typeof(KitOptions), typeof(KitAbiStatus), typeof(KitPaths), typeof(KitPageHosts), typeof(ClientHealthRegistry), typeof(KitDiagnostics),
            typeof(IKitDiagnostics), typeof(IKitDiagnosticsSource), typeof(BookmarkPaths), typeof(IBookmarkStore), typeof(BackgroundWriteGate),
            typeof(ClipExtractionService), typeof(IClipExtractionService), typeof(ClipFileOps), typeof(PartialFileJanitor), typeof(IBookmarkManager),
            typeof(IBookmarkQuery), typeof(IBookmarkPreferences), typeof(ClipStorageGuard), typeof(IDriveSpaceProbe),
            typeof(IBookmarkClips), typeof(ClipQueryService), typeof(ClipStateCache), typeof(ClipLibraryLocations), typeof(LibraryAccess),

            // The admins' clip service.
            typeof(IBookmarkAdminClips),
            .. TestData.LibraryServiceTypes
        ];

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUserById(AliceId)).Returns(_alice);
        users.Setup(m => m.GetUserById(BobId)).Returns(_bob);
        users.Setup(m => m.GetUserById(CarolId)).Returns(_carol);
        users.SetupGet(m => m.Users).Returns([_alice, _bob, _carol]);
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

                // Jellyfin's download policy as its UserPermissionHandler decides it (v10.11.5 Jellyfin.Api/Auth/
                // UserPermissionPolicy/UserPermissionHandler.cs): an API key passes, a user with "Allow media downloading" does.
                services.AddAuthorizationCore(options => options.AddPolicy(
                    MediaBrowser.Common.Api.Policies.Download,
                    policy => policy.AddAuthenticationSchemes(JellyfinAuth.Scheme).RequireAuthenticatedUser().RequireAssertion(context =>
                        context.User.IsApiKey() || users.Object.GetUserById(context.User.GetUserId())?.HasPermission(PermissionKind.EnableContentDownloading) == true)));
                services.AddSingleton(applicationHost.Object);
                services.AddSingleton(paths.Object);
                services.AddSingleton(loggers.Object);
                services.AddSingleton(users.Object);
                services.AddSingleton(library.Object);
                services.AddSingleton(TestData.ConfigurationProvider(_configuration));
                services.AddSingleton<IClipPlanner>(new FakeClipPlanner(_data));
                services.AddSingleton<IFfmpegRunner>(new FakeFfmpegRunner());
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
            .WithMvc(typeof(BookmarksController), typeof(BookmarksClipsController), typeof(BookmarksAdminClipsController))
            .StartAsync();
    }
}
