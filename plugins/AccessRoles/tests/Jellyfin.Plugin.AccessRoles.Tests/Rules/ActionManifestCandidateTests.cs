using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Rules;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// The maintainer's tool for the manifest (<c>plugins/AccessRoles/DEVELOPMENT.md</c>, "The action manifest"): with
/// <c>ACCESSROLES_MANIFEST_CANDIDATE</c> set to a file path, writes the candidate manifest for the located
/// <c>Jellyfin.Api.dll</c> there (every action of the DLL, the current rules and reviews kept, new actions and changed ones
/// marked <c>UNREVIEWED</c>), for a review and a copy over <c>Rules/Manifests/jellyfin-api-&lt;version&gt;.json</c>. Without
/// the variable that test does nothing; the others check the candidate itself.
/// </summary>
public sealed class ActionManifestCandidateTests
{
    /// <summary>The environment variable naming the candidate's file.</summary>
    public const string Variable = "ACCESSROLES_MANIFEST_CANDIDATE";

    private const string Ns = "Jellyfin.Api.Controllers.";

    [Fact]
    public void WritesTheCandidate_WhenAsked()
    {
        var output = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(output), $"{Variable} is not set; nothing to write.");
        var dll = JellyfinApiDll.Locate();
        Assert.SkipWhen(dll is null, $"No Jellyfin.Api.dll: set {JellyfinApiDll.Variable}.");

        var current = ActionManifest.ForBuild();
        var assembly = JellyfinApiMetadata.Read(dll!);
        var text = ActionManifestWriter.Write(current, ActionManifestWriter.Candidate(current, assembly.Actions));
        File.WriteAllText(output!, text);

        Assert.True(File.Exists(output));
    }

    [Fact]
    public void TheCandidate_OfTheReviewedShapes_IsTheManifest()
    {
        var current = ActionManifest.ForBuild();

        var candidate = ActionManifestWriter.Candidate(current, current.Actions.Select(a => a.ToShape()));

        Assert.Equal(ActionManifestWriter.Write(current, current.Actions), ActionManifestWriter.Write(current, candidate));
    }

    [Fact]
    public void TheCandidate_MarksAKnownActionWhosePolicyChanged_Unreviewed_KeepingItsRulesAndReview()
    {
        // Regression: an action that lost RequiresElevation would keep "RequiresElevation: administrators … only"
        // and pass every test if the candidate copied its review as it was.
        var current = ActionManifest.ForBuild();
        var policyKey = Ns + "UserController.UpdateUserPolicy";
        var renamedKey = Ns + "UserController.UpdateUserConfiguration";
        ApiActionShape Changed(ApiActionShape s)
            => s.Key == policyKey ? new ApiActionShape(s.Controller, s.Action, s.Routes, [ApiActionShape.DefaultPolicy], s.AllowAnonymous, s.Parameters)
                : s.Key == renamedKey ? new ApiActionShape(s.Controller, s.Action, s.Routes, s.Policies, s.AllowAnonymous, ["configuration", "userId"])
                : s;

        var candidate = ActionManifestWriter.Candidate(current, current.Actions.Select(a => Changed(a.ToShape()))).ToDictionary(e => e.Key, StringComparer.Ordinal);

        var policy = candidate[policyKey];
        var reviewed = current.Find(Ns + "UserController", "UpdateUserPolicy")!;
        Assert.Equal($"UNREVIEWED (changed: policies: reviewed [RequiresElevation], now [Default]) {reviewed.Review}", policy.Review);
        Assert.Equal(reviewed.Rules, policy.Rules);
        Assert.Equal([ApiActionShape.DefaultPolicy], policy.Policies);
        var renamed = candidate[renamedKey];
        Assert.StartsWith("UNREVIEWED (changed: parameters: reviewed [userConfig; userId], now [configuration; userId]) The posted configuration", renamed.Review, StringComparison.Ordinal);
        Assert.Equal(["configuration", "userId"], renamed.Parameters);

        // Everything else is as reviewed, and the manifest test refuses the two (EveryAction_IsReviewed's test).
        Assert.Equal(
            [renamedKey, policyKey],
            candidate.Values.Where(e => e.Review.Contains(ActionManifestWriter.UnreviewedMarker, StringComparison.Ordinal)).Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.All(current.Actions.Where(a => a.Key != policyKey && a.Key != renamedKey), a => Assert.Equal(a.Review, candidate[a.Key].Review));
    }

    [Fact]
    public void TheCandidate_MarksANewAction_Unreviewed_WithoutRules()
    {
        var current = ActionManifest.ForBuild();
        var added = new ApiActionShape(Ns + "UserController", "AddedLater", ["GET Users/Later"], [ApiActionShape.DefaultPolicy], allowAnonymous: false, ["userId"]);

        var candidate = ActionManifestWriter.Candidate(current, current.Actions.Select(a => a.ToShape()).Append(added));

        var entry = Assert.Single(candidate, e => e.Key == added.Key);
        Assert.Equal(ActionManifestWriter.UnreviewedMarker, entry.Review);
        Assert.Empty(entry.Rules);
        Assert.Equal(["userId"], entry.Parameters);
        Assert.Equal(current.Actions.Count + 1, candidate.Count);
    }
}
