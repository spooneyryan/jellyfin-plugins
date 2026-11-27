using System;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// Answers 503 ProblemDetails before the controller is constructed when <see cref="KitAbiGuard"/> found this build inert
/// on the running Jellyfin version. Jellyfin discovers controllers without DI, so they would otherwise
/// run, and fail on services the guard didn't register. Put it on every plugin controller.
/// </summary>
/// <remarks>
/// <para>A resource filter runs after authorisation and before model binding and controller construction. Without a
/// registered <see cref="KitAbiStatus"/> (the plugin's registrator never ran) the gate also answers 503: nothing of the
/// plugin is set up then.</para>
/// <para>It is also an action constraint. When <see cref="KitDuplicateGuard"/> made this copy stand down beside another
/// copy of the plugin (<see cref="KitAbiStatus.IsDuplicate"/>), both copies' controllers have the same routes, and two
/// matching endpoints would make every request ambiguous (a 500 for both). The constraint then refuses this copy's
/// actions, so routing picks the other copy's. In every other case it accepts, and the filter answers as above.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class KitAbiGateAttribute : Attribute, IResourceFilter, IOrderedFilter, IActionConstraint
{
    /// <summary>The message when no <see cref="KitAbiStatus"/> is registered.</summary>
    public const string NotRegisteredMessage = "The plugin didn't register its services; it is inactive.";

    /// <inheritdoc cref="IOrderedFilter.Order" />
    public int Order => int.MinValue;

    /// <inheritdoc />
    public bool Accept(ActionConstraintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RouteContext.HttpContext.RequestServices?.GetService<KitAbiStatus>() is not { IsDuplicate: true };
    }

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var status = context.HttpContext.RequestServices?.GetService<KitAbiStatus>();
        if (status is { IsActive: true })
        {
            return;
        }

        var result = KitProblemDetails.Result(PluginErrorKind.Unavailable, status?.Message ?? NotRegisteredMessage);
        if (status is not null)
        {
            result.Value.Extensions["abi"] = status.ToSummary();
        }

        context.Result = result;
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
