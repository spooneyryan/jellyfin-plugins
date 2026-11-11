using System;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.UserRatings.Api;

/// <summary>
/// <c>Cache-Control: no-store</c> on an answer that holds members' reviews or the moderation log,
/// so no browser or proxy keeps a review a member deleted or a moderator removed: the admin's <c>Reviews</c> and
/// <c>ModerationLog</c>, a removal's report, and the member's <c>Items</c> answers and <c>Mine/Inaccessible</c>. Set before
/// the action runs, so an error's answer has it too.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class NoStoreAttribute : ActionFilterAttribute
{
    /// <summary>The header's value.</summary>
    public const string Value = "no-store";

    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Response.Headers.CacheControl = Value;
        base.OnActionExecuting(context);
    }
}
