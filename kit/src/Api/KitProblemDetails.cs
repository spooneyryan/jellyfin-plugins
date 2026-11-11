using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// Builds the ProblemDetails body for a <see cref="PluginErrorKind"/>. <see cref="PluginExceptionFilterAttribute"/>
/// uses it for thrown <see cref="PluginException"/>s; code that must set a result instead of throwing (a filter's
/// <c>context.Result</c>, where an exception would reach Jellyfin's middleware as a 500) uses <see cref="Result"/>, so
/// both paths send the same body.
/// </summary>
/// <remarks>
/// The body is <c>title</c> (the reason phrase), <c>status</c>, <c>detail</c> (the message) and, only for the two
/// 403 kinds the client words differently, <c>type</c>: exactly what Bookmarks 1.0 sent, served as
/// <c>application/problem+json</c>. An error with a <see cref="PluginException.Code"/> adds it as <c>code</c>. No kit error ever carries Jellyfin's <c>ParentalControl</c> error code, which makes
/// jellyfin-web sign the user out.
/// </remarks>
internal static class KitProblemDetails
{
    /// <summary>The ProblemDetails type of <see cref="PluginErrorKind.FeatureDisabled"/>.</summary>
    public const string FeatureDisabledType = "urn:jellyfin-plugin-kit:feature-disabled";

    /// <summary>The ProblemDetails type of <see cref="PluginErrorKind.Locked"/>.</summary>
    public const string LockedType = "urn:jellyfin-plugin-kit:locked";

    // The members a body has of its own (RFC 9457's, any case) and the kit's code: an extension never replaces them.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) { "type", "title", "status", "detail", "instance", "code" };

    /// <summary>
    /// Gets the HTTP status code of an error kind.
    /// </summary>
    /// <param name="kind">The error kind.</param>
    /// <returns>The status code.</returns>
    public static int GetStatusCode(PluginErrorKind kind) => kind switch
    {
        PluginErrorKind.Invalid => StatusCodes.Status400BadRequest,
        PluginErrorKind.Forbidden or PluginErrorKind.FeatureDisabled or PluginErrorKind.Locked => StatusCodes.Status403Forbidden,
        PluginErrorKind.NotFound => StatusCodes.Status404NotFound,
        PluginErrorKind.Conflict => StatusCodes.Status409Conflict,
        PluginErrorKind.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        PluginErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown error kind.")
    };

    /// <summary>
    /// Gets the ProblemDetails type of an error kind, or <c>null</c> for the kinds that carry none.
    /// </summary>
    /// <param name="kind">The error kind.</param>
    /// <returns>The type URI.</returns>
    public static string? GetProblemType(PluginErrorKind kind) => kind switch
    {
        PluginErrorKind.FeatureDisabled => FeatureDisabledType,
        PluginErrorKind.Locked => LockedType,
        _ => null
    };

    /// <summary>
    /// Builds the ProblemDetails body of an error.
    /// </summary>
    /// <param name="kind">The error kind.</param>
    /// <param name="detail">The message shown to the user.</param>
    /// <param name="code">The error's stable code (<see cref="PluginException.Code"/>), or <c>null</c> for none.</param>
    /// <param name="extensions">More members (<see cref="PluginException.Extensions"/>); a name the body already has
    /// (<c>code</c>, <c>status</c>, …) is left out.</param>
    /// <returns>The body.</returns>
    public static ProblemDetails For(PluginErrorKind kind, string detail, string? code = null, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var status = GetStatusCode(kind);
        var body = new ProblemDetails
        {
            Type = GetProblemType(kind),
            Title = ReasonPhrases.GetReasonPhrase(status),
            Status = status,
            Detail = detail
        };
        if (code is not null)
        {
            body.Extensions["code"] = code;
        }

        foreach (var (name, value) in extensions ?? new Dictionary<string, object?>())
        {
            if (!ReservedNames.Contains(name) && !body.Extensions.ContainsKey(name))
            {
                body.Extensions[name] = value;
            }
        }

        return body;
    }

    /// <summary>
    /// Builds the action result of an error, for code that sets a result instead of throwing.
    /// </summary>
    /// <param name="kind">The error kind.</param>
    /// <param name="detail">The message shown to the user.</param>
    /// <param name="code">The error's stable code, or <c>null</c> for none.</param>
    /// <param name="extensions">More members of the body, or <c>null</c>.</param>
    /// <returns>The result, served as <c>application/problem+json</c> (<see cref="KitProblemResult"/>).</returns>
    public static KitProblemResult Result(PluginErrorKind kind, string detail, string? code = null, IReadOnlyDictionary<string, object?>? extensions = null)
        => new(For(kind, detail, code, extensions));
}
