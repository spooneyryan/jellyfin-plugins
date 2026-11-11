using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// Turns a <see cref="PluginException"/> thrown by an action into its ProblemDetails response
/// (<see cref="KitProblemDetails"/>) and logs it at Debug, because it is an expected outcome, not a server fault.
/// So too a request body over the action's <c>[RequestSizeLimit]</c>: Kestrel stops
/// reading it with a 413 <see cref="BadHttpRequestException"/> while MVC binds the body, which is a 413 ProblemDetails
/// (<see cref="TooLargeMessage"/>) here instead of Jellyfin's 500 and Error log. A caller who isn't an administrator gets
/// the exception's <see cref="PluginException.NonAdminMessage"/> when it has one. Other exceptions pass through to
/// Jellyfin's middleware. Put it on every plugin controller; it stays controller-level and never becomes a global filter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PluginExceptionFilterAttribute : ExceptionFilterAttribute
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "Api";

    /// <summary>The 413 message of a request body over the action's <c>[RequestSizeLimit]</c>.</summary>
    public const string TooLargeMessage = "The request is too large.";

    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PluginErrorKind kind;
        string message;
        string? code = null;
        IReadOnlyDictionary<string, object?>? extensions = null;
        switch (context.Exception)
        {
            case PluginException exception:
                kind = exception.Kind;
                message = exception.NonAdminMessage is { } forUsers && !context.HttpContext.User.HasAdministratorRole() ? forUsers : exception.Message;
                code = exception.Code;
                extensions = exception.Extensions;
                break;
            case BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }:
                kind = PluginErrorKind.PayloadTooLarge;
                message = TooLargeMessage;
                break;
            default:
                return;
        }

        if (context.HttpContext.RequestServices is { } services && services.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            var category = services.GetService<KitOptions>()?.GetLoggerCategory(LoggerComponent) ?? KitOptions.FallbackLoggerCategory;
            loggerFactory.CreateLogger(category).LogDebug(
                context.Exception,
                "{Method} {Path} ended with an expected {Kind} error",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path.Value,
                kind);
        }

        context.Result = KitProblemDetails.Result(kind, message, code, extensions);
        context.ExceptionHandled = true;
    }
}
