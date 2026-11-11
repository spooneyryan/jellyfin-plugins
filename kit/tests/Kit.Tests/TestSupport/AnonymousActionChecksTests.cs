using System;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The anonymous-action test.
/// </summary>
public sealed class AnonymousActionChecksTests
{
    [Fact]
    public void Check_AllowsOnlyGetClientScriptAndAdminScript()
    {
        Assert.Empty(AnonymousActionChecks.Check([typeof(FxGoodController), typeof(FxAdminController)]));
    }

    [Fact]
    public void Check_FlagsActionsWithoutAuthorize()
    {
        var problem = Assert.Single(AnonymousActionChecks.Check([typeof(FxOpenController)]));

        Assert.Contains("FxOpenController.Status", problem, StringComparison.Ordinal);
        Assert.Contains("/Fx/Open/Status", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FlagsAllowAnonymousUnderAClassLevelAuthorize()
    {
        var problems = AnonymousActionChecks.Check([typeof(FxLeakyController)]);

        // AllowAnonymous wins over [Authorize]; a POST ClientScript and a ClientScript-named data route are not the script.
        Assert.Equal(["FxLeakyController.Data", "FxLeakyController.PostScript"], problems.Select(p => p.Split(' ')[0]).Order());
    }

    [Fact]
    public void Check_SeesAuthorizeOnABaseClass()
    {
        Assert.Empty(AnonymousActionChecks.Check([typeof(FxDerivedController)]));
        Assert.True(AnonymousActionChecks.IsAnonymous(typeof(FxOpenController), typeof(FxOpenController).GetMethod(nameof(FxOpenController.Status))!));
    }

    [Fact]
    public void Check_FlagsAnActionProtectedOnlyByAnonymousLanAccessPolicy()
    {
        var problem = Assert.Single(AnonymousActionChecks.Check([typeof(FxLanController)]));

        Assert.StartsWith("FxLanController.Mine (/Fx/Lan/Mine)", problem, StringComparison.Ordinal);
        Assert.Contains(Policies.AnonymousLanAccessPolicy, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FlagsEveryPolicyThatAdmitsCallersWithoutAToken()
    {
        var problems = AnonymousActionChecks.Check([typeof(FxAnonymousPoliciesController)]);

        Assert.Equal(
            [
                "FxAnonymousPoliciesController.FirstTimeSetupOrDefault",
                "FxAnonymousPoliciesController.FirstTimeSetupOrElevated",
                "FxAnonymousPoliciesController.FirstTimeSetupOrIgnoreParentalControl",
                "FxAnonymousPoliciesController.LocalAccessOrRequiresElevation",
                "FxAnonymousPoliciesController.MixedCase",
                "FxAnonymousPoliciesController.TwoAnonymousPolicies",
            ],
            problems.Select(p => p.Split(' ')[0]).Order(StringComparer.Ordinal));
        Assert.All(
            AnonymousActionChecks.AnonymousPolicies,
            policy => Assert.Contains(problems, p => p.Contains($"({policy})", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Check_AcceptsAnonymousPoliciesCombinedWithAUserRequirement()
    {
        // A class-level [Authorize] adds the default policy, roles need a signed-in user, and the other policies need a user.
        Assert.Empty(AnonymousActionChecks.Check([typeof(FxLanUnderAuthorizeController), typeof(FxUserPoliciesController)]));

        var problem = Assert.Single(AnonymousActionChecks.Check([typeof(FxLanClassController)]));
        Assert.StartsWith("FxLanClassController.Inherited ", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_AllowsClientScriptUnderAnAnonymousPolicy()
    {
        Assert.Empty(AnonymousActionChecks.Check([typeof(FxLanScriptController)]));
    }

    [Fact]
    public void AuthorizationOptions_MatchPolicyNamesCaseInsensitively()
    {
        // The check matches policy names as ASP.NET Core resolves them.
        var options = new AuthorizationOptions();
        options.AddPolicy(Policies.AnonymousLanAccessPolicy, policy => policy.RequireAssertion(_ => true));

        Assert.NotNull(options.GetPolicy(Policies.AnonymousLanAccessPolicy.ToUpperInvariant()));
    }

    [ApiController]
    [Authorize]
    [Route("Fx")]
    public sealed class FxGoodController : ControllerBase
    {
        [HttpGet("Items")]
        public ActionResult<string> Items() => "items";

        [HttpGet("ClientScript")]
        [AllowAnonymous]
        public ActionResult<string> ClientScript() => "script";
    }

    [ApiController]
    [Route("Fx/Admin")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public sealed class FxAdminController : ControllerBase
    {
        [HttpGet("Diagnostics")]
        public ActionResult<string> Diagnostics() => "diag";

        [HttpGet("/Fx/AdminScript")]
        [AllowAnonymous]
        public ActionResult<string> AdminScript() => "module";
    }

    [ApiController]
    [Route("Fx/Open")]
    public sealed class FxOpenController : ControllerBase
    {
        [HttpGet("Status")]
        public ActionResult<string> Status() => "open";
    }

    [ApiController]
    [Authorize]
    [Route("Fx/Leaky")]
    public sealed class FxLeakyController : ControllerBase
    {
        [HttpGet("Data")]
        [AllowAnonymous]
        public ActionResult<string> Data() => "data";

        [HttpPost("ClientScript")]
        [AllowAnonymous]
        public ActionResult<string> PostScript() => "posted";

        [HttpGet("Safe")]
        public ActionResult<string> Safe() => "safe";
    }

    [ApiController]
    [Route("Fx/Lan")]
    public sealed class FxLanController : ControllerBase
    {
        [Authorize(Policy = Policies.AnonymousLanAccessPolicy)]
        [HttpGet("Mine")]
        public ActionResult<string> Mine() => "user data";
    }

    [ApiController]
    [Route("Fx/AnonymousPolicies")]
    public sealed class FxAnonymousPoliciesController : ControllerBase
    {
        [HttpGet("LocalAccessOrRequiresElevation")]
        [Authorize(Policy = Policies.LocalAccessOrRequiresElevation)]
        public ActionResult<string> LocalAccessOrRequiresElevation() => "a";

        [HttpGet("FirstTimeSetupOrDefault")]
        [Authorize(Policy = Policies.FirstTimeSetupOrDefault)]
        public ActionResult<string> FirstTimeSetupOrDefault() => "b";

        [HttpGet("FirstTimeSetupOrElevated")]
        [Authorize(Policy = Policies.FirstTimeSetupOrElevated)]
        public ActionResult<string> FirstTimeSetupOrElevated() => "c";

        [HttpGet("FirstTimeSetupOrIgnoreParentalControl")]
        [Authorize(Policy = Policies.FirstTimeSetupOrIgnoreParentalControl)]
        public ActionResult<string> FirstTimeSetupOrIgnoreParentalControl() => "d";

        [HttpGet("MixedCase")]
        [Authorize(Policy = "anonymouslanaccesspolicy")]
        public ActionResult<string> MixedCase() => "e";

        [HttpGet("TwoAnonymousPolicies")]
        [Authorize(Policy = Policies.FirstTimeSetupOrDefault)]
        [Authorize(Policy = Policies.AnonymousLanAccessPolicy)]
        public ActionResult<string> TwoAnonymousPolicies() => "f";
    }

    [ApiController]
    [Route("Fx/LanClass")]
    [Authorize(Policy = Policies.AnonymousLanAccessPolicy)]
    public sealed class FxLanClassController : ControllerBase
    {
        [HttpGet("Inherited")]
        public ActionResult<string> Inherited() => "a";

        [HttpGet("WithRoles")]
        [Authorize(Policy = Policies.AnonymousLanAccessPolicy, Roles = "Administrator")]
        public ActionResult<string> WithRoles() => "b";

        [HttpGet("WithDefault")]
        [Authorize]
        public ActionResult<string> WithDefault() => "c";

        [HttpGet("WithElevation")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult<string> WithElevation() => "d";
    }

    [ApiController]
    [Route("Fx/LanUnderAuthorize")]
    [Authorize]
    public sealed class FxLanUnderAuthorizeController : ControllerBase
    {
        [Authorize(Policy = Policies.AnonymousLanAccessPolicy)]
        [HttpGet("Mine")]
        public ActionResult<string> Mine() => "user data";
    }

    [ApiController]
    [Route("Fx/UserPolicies")]
    public sealed class FxUserPoliciesController : ControllerBase
    {
        [HttpGet("Download")]
        [Authorize(Policy = Policies.Download)]
        public ActionResult<string> Download() => "a";

        [HttpGet("IgnoreParentalControl")]
        [Authorize(Policy = Policies.IgnoreParentalControl)]
        public ActionResult<string> IgnoreParentalControl() => "b";

        [HttpGet("Roles")]
        [Authorize(Roles = "Administrator")]
        public ActionResult<string> Roles() => "c";
    }

    [ApiController]
    [Route("Fx/LanScript")]
    [Authorize(Policy = Policies.AnonymousLanAccessPolicy)]
    public sealed class FxLanScriptController : ControllerBase
    {
        [HttpGet("ClientScript")]
        public ActionResult<string> ClientScript() => "script";
    }

    [Authorize]
    public abstract class FxAuthorizedBase : ControllerBase
    {
    }

    [ApiController]
    [Route("Fx/Derived")]
    public sealed class FxDerivedController : FxAuthorizedBase
    {
        [HttpGet]
        public ActionResult<string> Get() => "derived";
    }
}
