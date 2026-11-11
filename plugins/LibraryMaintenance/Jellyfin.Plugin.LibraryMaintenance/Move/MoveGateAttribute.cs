using System;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// Answers every Move route with 403 <c>FeatureDisabled</c> while <c>MoveEnabled</c> is off and with 503
/// <c>move.unsupported-os</c> on a host that isn't Windows, through <see cref="MoveFeature.RequireAvailable"/>.
/// A resource filter: it runs after authorisation (so a non-admin gets the policy's plain 403 first) and after the kit's
/// <see cref="KitAbiGateAttribute"/>, and before model binding, so a turned-off feature never reads a request body.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MoveGateAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    /// <inheritdoc />
    public int Order => int.MinValue + 1;

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var feature = context.HttpContext.RequestServices.GetRequiredService<MoveFeature>();
        try
        {
            feature.RequireAvailable();
        }
        catch (PluginException e)
        {
            // A filter sets the result: an exception here would reach Jellyfin's middleware as a 500.
            context.Result = KitProblemDetails.Result(e.Kind, e.Message, e.Code);
        }
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
