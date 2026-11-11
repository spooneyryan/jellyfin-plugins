using System;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Rules;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// The library leak rows a review of the manifest added and the two the later review of its noted actions added, for the
/// bypass matrix, whose library leak rows a sandbox probe runs (not included in this repository): each row says what the matrix
/// sends as a viewer who may open one library, with an item of another, what the rule must answer, and what 10.11.5 does natively (the review's run, or its
/// source). The probe runs the rows its fixture can hold (the reports, the viewing report, the sessions of a second user,
/// the play command's ids); the trickplay playlist needs tiles and the guide a tuner, which the sandbox lacks, so those two
/// are the unit tests' (<c>ServerHardeningTests</c>). The tests pin that each row names a route of an action that carries
/// the row's rule, and that every action of these rule kinds has a row, so the matrix can't miss one.
/// </summary>
public sealed class BypassMatrixLeakRowsTests
{
    private const string Ns = "Jellyfin.Api.Controllers.";

    private static readonly string[] ReviewKinds =
    [
        nameof(RuleKind.HardenSessionReport), nameof(RuleKind.HardenSessions), nameof(RuleKind.HardenItem), nameof(RuleKind.HardenLibrarySeries), nameof(RuleKind.HardenPlayCommand)
    ];

    // The route as the manifest writes it, the action, the rule, the request and its answer with the rule, the native answer.
    private static readonly (string Route, string Action, string Kind, string Request, string Native)[] RowList =
    [
        ("POST Sessions/Playing", "PlaystateController.ReportPlaybackStart", nameof(RuleKind.HardenSessionReport),
            "body {ItemId: <hidden movie>}: 204, and GET /Sessions as the viewer shows no NowPlayingItem",
            "204, and GET /Sessions shows the movie's DTO with its name and Path (the review's run on 10.11.5)"),
        ("POST Sessions/Playing", "PlaystateController.ReportPlaybackStart", nameof(RuleKind.HardenSessionReport),
            "body {ItemId: <visible>, NowPlayingQueue: [<visible>, <hidden movie>]}: 204, NowPlayingQueueFullItems without the hidden movie",
            "the queue's full DTOs, the hidden movie's too (SessionManager L457-467)"),
        ("POST Sessions/Playing/Progress", "PlaystateController.ReportPlaybackProgress", nameof(RuleKind.HardenSessionReport),
            "body {ItemId: <hidden movie>}: 204, no NowPlayingItem", "as ReportPlaybackStart (L885)"),
        ("POST Sessions/Playing/Stopped", "PlaystateController.ReportPlaybackStopped", nameof(RuleKind.HardenSessionReport),
            "body {ItemId: <hidden movie>}: 204, no play state written for the movie", "the movie resolved and its play state written (L1029)"),
        ("POST PlayingItems/{itemId}", "PlaystateController.OnPlaybackStart", nameof(RuleKind.HardenSessionReport),
            "itemId <hidden movie>: 204, no NowPlayingItem", "as ReportPlaybackStart"),
        ("POST Users/{userId}/PlayingItems/{itemId}", "PlaystateController.OnPlaybackStartLegacy", nameof(RuleKind.HardenSessionReport),
            "the viewer's id, itemId <hidden movie>: 204, no NowPlayingItem", "as ReportPlaybackStart"),
        ("POST PlayingItems/{itemId}/Progress", "PlaystateController.OnPlaybackProgress", nameof(RuleKind.HardenSessionReport),
            "itemId <hidden movie>: 204, no NowPlayingItem", "as ReportPlaybackStart"),
        ("POST Users/{userId}/PlayingItems/{itemId}/Progress", "PlaystateController.OnPlaybackProgressLegacy", nameof(RuleKind.HardenSessionReport),
            "the viewer's id, itemId <hidden movie>: 204, no NowPlayingItem", "as ReportPlaybackStart"),
        ("DELETE PlayingItems/{itemId}", "PlaystateController.OnPlaybackStopped", nameof(RuleKind.HardenSessionReport),
            "itemId <hidden movie>: 204, no play state written", "as ReportPlaybackStopped"),
        ("DELETE Users/{userId}/PlayingItems/{itemId}", "PlaystateController.OnPlaybackStoppedLegacy", nameof(RuleKind.HardenSessionReport),
            "the viewer's id, itemId <hidden movie>: 204, no play state written", "as ReportPlaybackStopped"),
        ("POST Sessions/Viewing", "SessionController.ReportViewing", nameof(RuleKind.HardenSessionReport),
            "?itemId=<hidden movie> (N and D forms): 204, and GET /Sessions shows no NowViewingItem",
            "204, and GET /Sessions shows the movie's DTO as NowViewingItem (SessionManager L1854-1862)"),
        ("GET Sessions", "SessionController.GetSessions", nameof(RuleKind.HardenSessions),
            "the viewer with EnableRemoteControlOfOtherUsers, ?controllableByUserId=<viewer>, while an admin's session plays the hidden movie: that session without NowPlayingItem (an unrestricted viewer's shows it)",
            "the admin's NowPlayingItem, the hidden movie's DTO (SessionManager L1974-2010)"),
        ("GET Videos/{itemId}/Trickplay/{width}/tiles.m3u8", "TrickplayController.GetTrickplayHlsPlaylist", nameof(RuleKind.HardenItem),
            "itemId <hidden movie with tiles>, and a visible itemId with ?mediaSourceId=<hidden movie>: 404 both",
            "200, the tiles playlist (TrickplayController L54-67)"),
        ("GET LiveTv/Programs", "LiveTvController.GetLiveTvPrograms", nameof(RuleKind.HardenLibrarySeries),
            "?librarySeriesId=<hidden series> with a guide holding a program of that name: an empty result",
            "the programs named like the hidden series (LiveTvController L626-635)"),
        ("POST LiveTv/Programs", "LiveTvController.GetPrograms", nameof(RuleKind.HardenLibrarySeries),
            "body {LibrarySeriesId: <hidden series>}: an empty result", "as GET (L681-690)"),
        ("POST Sessions/{sessionId}/Playing", "SessionController.Play", nameof(RuleKind.HardenPlayCommand),
            "?itemIds=<hidden folder> to the viewer's own other session: 204 and no command sent; ?itemIds=<hidden>,<visible>: only the visible one",
            "the folder's children resolved and sent to the session (SessionManager L1382-1433)"),
    ];

    /// <summary>Gets the rows for the theory.</summary>
    public static TheoryData<string, string, string, string, string> Rows
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string>();
            foreach (var row in RowList)
            {
                data.Add(row.Route, row.Action, row.Kind, row.Request, row.Native);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EachRow_NamesARouteOfAnActionWithItsRule(string route, string action, string kind, string request, string native)
    {
        var separator = action.IndexOf('.', StringComparison.Ordinal);
        var entry = ActionManifest.ForBuild().Find(Ns + action[..separator], action[(separator + 1)..]);

        Assert.NotNull(entry);
        Assert.Contains(route, entry.Routes);
        Assert.Contains(entry.Rules, r => r.Kind == Enum.Parse<RuleKind>(kind));
        Assert.False(string.IsNullOrWhiteSpace(request) || string.IsNullOrWhiteSpace(native));
    }

    [Fact]
    public void TheRows_CoverEveryActionOfTheReviewsKinds()
    {
        var kinds = ReviewKinds.Select(Enum.Parse<RuleKind>).ToHashSet();

        Assert.Equal(
            ActionManifest.ForBuild().Actions.Where(a => a.Rules.Any(r => kinds.Contains(r.Kind))).Select(a => a.Key[Ns.Length..]).Order(StringComparer.Ordinal),
            RowList.Select(r => r.Action).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }
}
