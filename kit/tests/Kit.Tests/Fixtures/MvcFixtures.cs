using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kit.Tests.Fixtures;

/// <summary>Throws or returns every error kind, as a plugin controller would (with Bookmarks' [Produces] JSON).</summary>
[ApiController]
[Authorize]
[Route("Fx/Errors")]
[Produces("application/json")]
[PluginExceptionFilter]
public sealed class FxErrorsController : ControllerBase
{
    [HttpGet("Throw/{kind}")]
    public ActionResult<string> Throw([FromRoute] string kind)
        => throw new PluginException(Enum.Parse<PluginErrorKind>(kind), "Detail for " + kind + ".");

    [HttpGet("Result/{kind}")]
    public ActionResult<string> Result([FromRoute] string kind)
        => KitProblemDetails.Result(Enum.Parse<PluginErrorKind>(kind), "Detail for " + kind + ".");

    [HttpGet("Derived")]
    public ActionResult<string> Derived() => throw new FxDerivedException("A thin subclass.");

    [HttpGet("Coded")]
    public ActionResult<string> Coded() => throw new PluginException(PluginErrorKind.Conflict, "Saved since.") { Code = "FxChanged" };

    [HttpGet("Extended")]
    public ActionResult<string> Extended() => throw new PluginException(PluginErrorKind.Invalid, "Too broad.")
    {
        Code = "FxBroad",
        Extensions = new System.Collections.Generic.Dictionary<string, object?> { ["candidateCount"] = 12, ["kinds"] = new[] { "Movie" } }
    };

    [HttpGet("Ok")]
    public ActionResult<string> Fine() => "fine";

    /// <summary>Echoes a JSON body of at most 64 bytes, as a plugin's POST with <c>[RequestSizeLimit]</c> takes one.</summary>
    [HttpPost("Body")]
    [RequestSizeLimit(64)]
    public ActionResult<string> Body([FromBody] FxBody body) => body?.Text ?? string.Empty;

    /// <summary>What Kestrel throws while MVC reads a body over the action's limit (the TestServer enforces none).</summary>
    [HttpGet("TooLarge")]
    public ActionResult<string> TooLarge() => throw new BadHttpRequestException("Request body too large. The max request body size is 64 bytes.", StatusCodes.Status413PayloadTooLarge);

    /// <summary>Another of Kestrel's bad-request errors, which isn't the filter's to answer.</summary>
    [HttpGet("BadRequest")]
    public ActionResult<string> KestrelBadRequest() => throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest);
}

/// <summary>A small request body.</summary>
public sealed class FxBody
{
    public string? Text { get; set; }
}

/// <summary>Counts constructions, to prove the gate runs first.</summary>
[ApiController]
[Authorize]
[Route("Fx/Gate")]
[KitAbiGate]
[PluginExceptionFilter]
public sealed class FxGateController : ControllerBase
{
    public FxGateController(FxConstructionCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        Interlocked.Increment(ref counter.Count);
    }

    [HttpGet("Ping")]
    public ActionResult<string> Ping() => "pong";

    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult<string> ClientScript() => "script";
}

/// <summary>Compares CurrentUser.IsElevatedAsync with an [Authorize(Policy = RequiresElevation)] endpoint.</summary>
[ApiController]
[Authorize]
[Route("Fx/Elevation")]
public sealed class FxElevationController : ControllerBase
{
    [HttpGet("Check")]
    public async Task<ActionResult<bool>> Check() => await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);

    [HttpGet("Admin")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Admin() => NoContent();
}

/// <summary>Constructions of <see cref="FxGateController"/>.</summary>
public sealed class FxConstructionCounter
{
    [SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Interlocked needs a field.")]
    public int Count;
}

/// <summary>A plugin's thin subclass of PluginException, like BookmarkException.</summary>
[SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Test fixture.")]
internal sealed class FxDerivedException : PluginException
{
    public FxDerivedException(string message)
        : base(PluginErrorKind.Conflict, message)
    {
    }
}

/// <summary>
/// Vetoes RequiresElevation (the policy with the role requirement) for a request carrying <c>X-Veto: 1</c>, with the
/// HttpContext as resource, the way an endpoint-aware handler could veto it for an administrator.
/// Other policies of the request pass.
/// </summary>
public sealed class FxVetoHandler : IAuthorizationHandler
{
    public const string Header = "X-Veto";

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Resource is HttpContext http
            && http.Request.Headers[Header] == "1"
            && context.Requirements.OfType<ClaimsAuthorizationRequirement>().Any(r => r.ClaimType == ClaimTypes.Role))
        {
            context.Fail(new AuthorizationFailureReason(this, "Vetoed by the test handler."));
        }

        return Task.CompletedTask;
    }
}
