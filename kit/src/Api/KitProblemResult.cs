using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// The result of a kit error (<see cref="KitProblemDetails.Result"/>): a ProblemDetails body served as
/// <c>application/problem+json</c>, whether an exception filter, a resource filter or an action produced it.
/// </summary>
/// <remarks>
/// It isn't an <see cref="ObjectResult"/> on purpose: <c>[Produces("application/json")]</c> is a result filter that rewrites
/// the content types of every <see cref="ObjectResult"/> an action returns, which would serve a returned error as
/// <c>application/json</c> while the same error thrown (whose result skips result filters) is
/// <c>application/problem+json</c>. It executes a plain <see cref="ObjectResult"/> with no content types, exactly what
/// Bookmarks 1.0's exception filter did, so the response is byte-for-byte the same.
/// </remarks>
internal sealed class KitProblemResult : ActionResult, IStatusCodeActionResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KitProblemResult"/> class.
    /// </summary>
    /// <param name="value">The body.</param>
    public KitProblemResult(ProblemDetails value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>
    /// Gets the body.
    /// </summary>
    public ProblemDetails Value { get; }

    /// <inheritdoc />
    public int? StatusCode => Value.Status;

    /// <inheritdoc />
    public override Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ObjectResult(Value) { StatusCode = Value.Status }.ExecuteResultAsync(context);
    }
}
