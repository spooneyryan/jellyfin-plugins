using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// A script from <see cref="EmbeddedScript"/>: <c>text/javascript; charset=utf-8</c>, <c>Cache-Control: no-cache</c> and an
/// <c>ETag</c>, so browsers revalidate on every load and get a 304 until the plugin is rebuilt. The body
/// is served by a <see cref="FileContentResult"/>, which answers conditional requests.
/// </summary>
internal sealed class KitScriptResult : IActionResult
{
    private readonly byte[] _body;
    private readonly EntityTagHeaderValue _entityTag;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitScriptResult"/> class.
    /// </summary>
    /// <param name="body">The script's UTF-8 bytes.</param>
    /// <param name="entityTag">The entity tag.</param>
    public KitScriptResult(byte[] body, EntityTagHeaderValue entityTag)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(entityTag);
        _body = body;
        _entityTag = entityTag;
    }

    /// <summary>
    /// Gets the number of bytes of the script.
    /// </summary>
    public int Length => _body.Length;

    /// <inheritdoc />
    public Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Response.Headers.CacheControl = "no-cache";
        return new FileContentResult(_body, EmbeddedScript.ContentType) { EntityTag = _entityTag }.ExecuteResultAsync(context);
    }
}
