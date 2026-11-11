using System;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// Answers 503 ProblemDetails before the controller is constructed when <see cref="KitAbiGuard"/> found this build inert
/// on the running Jellyfin version. Jellyfin discovers controllers without DI, so they would otherwise
/// run, and fail on services the guard didn't register. Put it on every plugin controller.
/// </summary>
/// <remarks>
/// A resource filter runs after authorisation and before model binding and controller construction. Without a
/// registered <see cref="KitAbiStatus"/> (the plugin's registrator never ran) the gate also answers 503: nothing of the
/// plugin is set up then.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class KitAbiGateAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    /// <summary>The message when no <see cref="KitAbiStatus"/> is registered.</summary>
    public const string NotRegisteredMessage = "The plugin didn't register its services; it is inactive.";

    /// <inheritdoc />
    public int Order => int.MinValue;

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
