using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// What Jellyfin Enhanced 12.8's <c>ScriptInjectionStartupFilter</c> does, read from its IL (JavaScript
/// Injector 4.0 does the same): on GET <c>…/web/index.html</c>, <c>…/web/</c> or <c>…/web</c> (any case, any BaseUrl) it
/// strips only <c>Accept-Encoding</c>, <c>Range</c> and <c>If-Range</c>, not <c>If-None-Match</c> or
/// <c>If-Modified-Since</c>, buffers the body and, for a 200 <c>text/html</c> response, inserts its tag before the last
/// <c>&lt;/body&gt;</c> unless <see cref="Needle"/> is present, then sets <c>text/html;charset=utf-8</c> and the length
/// and removes <c>ETag</c>, <c>Last-Modified</c> and <c>Accept-Ranges</c>. Used in both nesting orders with ours.
/// </summary>
public sealed class JellyfinEnhancedLikeStartupFilter : IStartupFilter
{
    /// <summary>What marks Jellyfin Enhanced's tag, and what it checks before inserting it.</summary>
    public const string Needle = "/JellyfinEnhanced/script";

    /// <summary>The tag it inserts.</summary>
    public const string Tag = "<script plugin=\"Jellyfin Enhanced\" version=\"12.8.0.0-test\" src=\"../JellyfinEnhanced/script?v=12.8.0.0-test\" defer></script>\n";

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(async (HttpContext context, RequestDelegate inner) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var isIndex = path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
                if (!HttpMethods.IsGet(context.Request.Method) || !isIndex)
                {
                    await inner(context).ConfigureAwait(false);
                    return;
                }

                context.Request.Headers.Remove("Accept-Encoding");
                context.Request.Headers.Remove("Range");
                context.Request.Headers.Remove("If-Range");
                var original = context.Response.Body;
                using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await inner(context).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Body = original;
                }

                var bytes = buffer.ToArray();
                if (context.Response.StatusCode == StatusCodes.Status200OK
                    && (context.Response.ContentType ?? string.Empty).Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    var html = Encoding.UTF8.GetString(bytes);
                    var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (end >= 0 && !html.Contains(Needle, StringComparison.Ordinal))
                    {
                        bytes = Encoding.UTF8.GetBytes(html.Insert(end, Tag));
                    }

                    context.Response.ContentType = "text/html;charset=utf-8";
                    context.Response.ContentLength = bytes.Length;
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    context.Response.Headers.Remove("Accept-Ranges");
                }

                if (bytes.Length > 0)
                {
                    await original.WriteAsync(bytes).ConfigureAwait(false);
                }
            });
            next(app);
        };
    }
}
