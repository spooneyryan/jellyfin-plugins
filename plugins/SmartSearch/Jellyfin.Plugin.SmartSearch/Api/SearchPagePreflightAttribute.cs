using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// The checks of the search and lookup endpoints that come before anything of the request is read: a signed-in user who
/// still exists (403 for an API key) and the search page turned on (403 "turned off"). A resource filter, so it runs after
/// authentication (401) and before model binding: a request refused for who sent it is never refused for its body or
/// parameters first.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SearchPagePreflightAttribute : Attribute, IAsyncResourceFilter
{
    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        try
        {
            http.User.RequireUser(http.RequestServices.GetRequiredService<IUserManager>());
            SearchPageGate.Ensure(http.RequestServices.GetRequiredService<SearchPageSwitch>().Enabled);
        }
        catch (PluginException refused)
        {
            // A resource filter's exception never reaches the exception filter: the same body, set here.
            var message = refused.NonAdminMessage is { } forUsers && !http.User.HasAdministratorRole() ? forUsers : refused.Message;
            context.Result = KitProblemDetails.Result(refused.Kind, message, refused.Code, refused.Extensions);
            return;
        }

        await next().ConfigureAwait(false);
    }
}
