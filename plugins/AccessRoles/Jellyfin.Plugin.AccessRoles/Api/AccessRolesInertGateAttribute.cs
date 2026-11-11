using System;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Hosting;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// Answers every action of Access Roles' controllers with 503 while the plugin is inert (the emergency stop at the
/// server's start, <see cref="AccessRolesInert"/>), before the controller is built, except the actions
/// marked <see cref="AllowWhileInertAttribute"/> (the admin page's script). The ProblemDetails carries the stop as its
/// <c>inert</c> extension, so the page says which of the two is in effect. Runs right after the kit's ABI gate.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal sealed class AccessRolesInertGateAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    /// <inheritdoc />
    public int Order => int.MinValue + 1;

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.RequestServices?.GetService<AccessRolesInert>() is not { } inert
            || context.ActionDescriptor.EndpointMetadata.OfType<AllowWhileInertAttribute>().Any())
        {
            return;
        }

        var result = KitProblemDetails.Result(PluginErrorKind.Unavailable, inert.Message);
        result.Value.Extensions["inert"] = inert.ToSummary();
        context.Result = result;
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
