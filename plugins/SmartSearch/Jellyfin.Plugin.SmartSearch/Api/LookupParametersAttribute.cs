using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// A lookup's query parameters that don't bind (<c>limit=abc</c>, a library id that isn't a GUID) answer 400
/// <c>InvalidRequest</c> in the kit's ProblemDetails shape, with <c>errors</c>, as every other refusal of these endpoints
/// does, instead of MVC's validation problem. It runs before <c>[ApiController]</c>'s own model-state filter (-2000).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LookupParametersAttribute : Attribute, IActionFilter, IOrderedFilter
{
    /// <inheritdoc />
    public int Order => -3000;

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ModelState.IsValid)
        {
            return;
        }

        var names = context.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var errors = names.Select(n => new ProblemError(n, QueryErrorCodes.InvalidValue, "This parameter's value isn't valid.")).ToList();
        context.Result = KitProblemDetails.Result(
            PluginErrorKind.Invalid,
            "The request's parameters aren't valid: " + string.Join(", ", names) + ".",
            SmartSearchRequestBinder.InvalidRequestCode,
            new Dictionary<string, object?> { ["errors"] = errors });
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
