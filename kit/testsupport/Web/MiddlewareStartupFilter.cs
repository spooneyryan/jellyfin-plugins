using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// A startup filter that adds one inline middleware at its position among the plugin filters of a
/// <see cref="WebHostHarness"/>: to record what reaches a position, or to stand in for another plugin's response.
/// </summary>
public sealed class MiddlewareStartupFilter : IStartupFilter
{
    private readonly Func<HttpContext, RequestDelegate, Task> _middleware;

    /// <summary>
    /// Initializes a new instance of the <see cref="MiddlewareStartupFilter"/> class.
    /// </summary>
    /// <param name="middleware">The middleware; it calls the next delegate or answers itself.</param>
    public MiddlewareStartupFilter(Func<HttpContext, RequestDelegate, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middleware = middleware;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(_middleware);
            next(app);
        };
    }
}
