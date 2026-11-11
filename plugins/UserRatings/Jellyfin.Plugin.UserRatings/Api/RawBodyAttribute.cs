using System;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.UserRatings.Api;

/// <summary>
/// Keeps MVC from reading a form out of a route's body before its action, so the action reads <c>Request.Body</c> as the
/// bytes it is, whatever its <c>Content-Type</c> (found while verifying 0.3.19). Without it, MVC's form value
/// providers read the body first: an import sent as <c>application/x-www-form-urlencoded</c> was a 400 (the whole export one
/// form key, over the form reader's 2 KiB key limit), and one sent as <c>multipart/form-data</c> was read into a form,
/// leaving the import an empty body. The route's other parameters come from its query string.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RawBodyAttribute : Attribute, IResourceFilter
{
    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var factories = context.ValueProviderFactories;
        for (var i = factories.Count - 1; i >= 0; i--)
        {
            if (factories[i] is FormValueProviderFactory or FormFileValueProviderFactory or JQueryFormValueProviderFactory)
            {
                factories.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
