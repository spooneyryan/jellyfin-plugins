// Controllers for JellyfinApiMetadataTests: each MVC rule the manifest reader copies, as a case MVC itself is asked about
// (the real application model in a test host). They are never served; their bodies only satisfy the compiler.
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules.ReaderFixtures;

/// <summary>Tokens, the Async suffix, [ActionName], [NonAction] and methods that are no actions.</summary>
[Route("Fixture/[controller]")]
public class TokensController : ControllerBase
{
    [HttpGet("[action]/{id}")]
    public IActionResult GetThing(Guid id) => Ok(id);

    [HttpGet("Later")]
    public Task<IActionResult> LoadLaterAsync() => Task.FromResult<IActionResult>(Ok());

    [ActionName("Renamed")]
    [HttpPost("[action]")]
    public IActionResult OriginalName() => Ok();

    [NonAction]
    public IActionResult NotAnAction() => Ok();

    public static IActionResult StaticMethod() => new OkResult();

    public IActionResult GenericMethod<T>() => Ok(typeof(T).Name);

    public override string ToString() => "tokens";
}

/// <summary>MVC's selectors: route-defining and silent attributes together, absolute templates, AcceptVerbs, no attributes.</summary>
[Route("Fixture")]
public class MixedController : ControllerBase
{
    [HttpGet("a")]
    [HttpHead("a", Name = "FixtureHeadA")]
    public IActionResult GetAndHead() => Ok();

    [HttpGet]
    [Route("r")]
    public IActionResult RouteWithSilentGet() => Ok();

    [HttpGet]
    [HttpPost("p")]
    public IActionResult SilentAndDefining() => Ok();

    [AcceptVerbs("GET", "post", Route = "verbs")]
    public IActionResult Verbs() => Ok();

    [HttpGet("/absolute/x")]
    public IActionResult Absolute() => Ok();

    [HttpGet("~/tilde/y/")]
    public IActionResult Tilde() => Ok();

    [HttpGet("")]
    public IActionResult EmptyTemplate() => Ok();

    [HttpPut(Name = "FixturePutNamed")]
    public IActionResult NamedWithoutTemplate() => Ok();

    [HttpDelete("d", Order = 2)]
    [HttpPatch]
    public IActionResult OrderedAndSilent() => Ok();

    public IActionResult NoAttributes() => Ok();
}

/// <summary>Policies on the class and the action, and [AllowAnonymous] on an action.</summary>
[Authorize]
[Route("Fixture/Auth")]
public class AuthController : ControllerBase
{
    [HttpGet("elevated")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Elevated() => Ok();

    [HttpGet("open")]
    [AllowAnonymous]
    public IActionResult Open() => Ok();

    [HttpGet("plain")]
    public IActionResult Plain() => Ok();
}

/// <summary>[AllowAnonymous] on the class, no [Authorize] anywhere.</summary>
[AllowAnonymous]
[Route("Fixture/Open")]
public class OpenController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>A base like Jellyfin's <c>BaseJellyfinApiController</c>: public, a controller itself, its route used by subclasses.</summary>
[Route("Fixture/[controller]")]
[Authorize(Policy = "FixtureBase")]
public class FixtureApiBase : ControllerBase
{
    protected IActionResult Helper() => Ok();
}

/// <summary>Inherits the base's route and policy.</summary>
public class DerivedController : FixtureApiBase
{
    [HttpGet("z")]
    public IActionResult Z() => Helper();
}

/// <summary>Overrides the base's route with its own.</summary>
[Route("Fixture/Own")]
public class OwnRouteController : FixtureApiBase
{
    [HttpGet("w")]
    public IActionResult W() => Ok();
}

/// <summary>A base whose public actions a subclass inherits (no overrides: the reader refuses those, see the reader).</summary>
[Route("Fixture/Parent")]
public class ParentController : ControllerBase
{
    [HttpGet("inherited")]
    public IActionResult Inherited() => Ok();

    [HttpGet("hidden")]
    public IActionResult HiddenLater() => Ok();
}

/// <summary>Inherits one action and hides another with <c>new</c>; the parent's route is its own.</summary>
public class ChildController : ParentController
{
    [HttpGet("new")]
    public new IActionResult HiddenLater() => Ok();

    [HttpGet("own")]
    public IActionResult OwnAction() => Ok();
}

/// <summary>Scope parameters, by parameter name and by the binding name.</summary>
[Route("Fixture/Scope")]
public class ScopeController : ControllerBase
{
    [HttpGet]
    public IActionResult Scoped([FromQuery] Guid? parentId, [FromQuery(Name = "SeriesId")] Guid? series, Guid seasonId, [FromRoute(Name = "other")] Guid? parentIdNot, Guid unrelated) => Ok();
}

/// <summary>Every kind of parameter MVC lists for an action: body, services, a cancellation token, names from attributes.</summary>
[Route("Fixture/Parameters")]
public class ParametersController : ControllerBase
{
    [HttpPost]
    public IActionResult Every(
        [FromBody] FixtureBody body,
        [FromServices] IServiceProvider services,
        [FromHeader(Name = "X-Fixture")] string? header,
        [ModelBinder(Name = "values")] string[] list,
        int? count,
        System.Threading.CancellationToken cancellationToken) => Ok();
}

/// <summary>[ApiController], as Jellyfin's controllers have it: binding sources inferred, the names unchanged.</summary>
[ApiController]
[Route("Fixture/ApiBehavior")]
public class ApiBehaviorController : ControllerBase
{
    [HttpPost("{id}")]
    public IActionResult Inferred(Guid id, FixtureBody body, int? count) => Ok();
}

/// <summary>A request body for the fixtures.</summary>
public sealed class FixtureBody
{
    /// <summary>Gets or sets a value.</summary>
    public string? Value { get; set; }
}

/// <summary>Dispose of IDisposable is no action.</summary>
[Route("Fixture/Disposing")]
public sealed class DisposingController : ControllerBase, IDisposable
{
    [HttpGet]
    public IActionResult Get() => Ok();

    public void Dispose()
    {
    }
}

/// <summary>A controller by derivation alone: MVC's ControllerBase carries [Controller].</summary>
[Route("Fixture/NoSuffix")]
public class NoSuffixButDerived : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>A controller by name alone (a POCO), with a conventional action: no attribute route anywhere.</summary>
public class PocoController
{
    public string Hello() => "hello";
}

/// <summary>Not a controller: abstract.</summary>
public abstract class AbstractController : ControllerBase
{
    [HttpGet("Fixture/Abstract")]
    public IActionResult Get() => Ok();
}

/// <summary>Not a controller: generic.</summary>
/// <typeparam name="T">Anything.</typeparam>
public class GenericController<T> : ControllerBase
{
    [HttpGet("Fixture/Generic")]
    public IActionResult Get() => Ok(typeof(T).Name);
}

/// <summary>Not a controller: [NonController].</summary>
[NonController]
public class HiddenController : ControllerBase
{
    [HttpGet("Fixture/Hidden")]
    public IActionResult Get() => Ok();
}

/// <summary>Not a controller: [NonController] inherited.</summary>
public class HiddenChildController : HiddenController
{
}

/// <summary>Not a controller: not public.</summary>
internal sealed class InternalController : ControllerBase
{
    [HttpGet("Fixture/Internal")]
    public IActionResult Get() => Ok();
}
