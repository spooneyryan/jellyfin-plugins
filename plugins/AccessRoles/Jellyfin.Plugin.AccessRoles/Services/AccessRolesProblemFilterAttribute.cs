using System;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// Turns an <see cref="AccessRolesException"/> into the kit's ProblemDetails body (<see cref="KitProblemDetails"/>) with its
/// <c>Code</c> and <c>Users</c>, so a client tells <c>NoLibraries</c> from <c>NoFullAdministrator</c>. It runs
/// before the kit's <see cref="PluginExceptionFilterAttribute"/> (a higher order is an inner filter, whose exception
/// handler runs first), which handles every other <see cref="PluginException"/> as it always does.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AccessRolesProblemFilterAttribute : ExceptionFilterAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesProblemFilterAttribute"/> class.
    /// </summary>
    public AccessRolesProblemFilterAttribute()
    {
        Order = 1;
    }

    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Exception is not AccessRolesException exception)
        {
            return;
        }

        if (context.HttpContext.RequestServices?.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            var category = context.HttpContext.RequestServices.GetService<KitOptions>()?.GetLoggerCategory(PluginExceptionFilterAttribute.LoggerComponent) ?? KitOptions.FallbackLoggerCategory;
            loggerFactory.CreateLogger(category).LogDebug(
                "{Method} {Path} ended with an expected {Kind} error {Code}",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path.Value,
                exception.Kind,
                exception.Code);
        }

        var problem = KitProblemDetails.For(exception.Kind, exception.Message);
        problem.Extensions["Code"] = exception.Code;
        if (exception.Users.Count > 0)
        {
            problem.Extensions["Users"] = exception.Users;
        }

        if (exception.Preview is { } preview)
        {
            problem.Extensions["Preview"] = preview;
        }

        context.Result = new KitProblemResult(problem);
        context.ExceptionHandled = true;
    }
}
