using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// Adds the plugin's script tag, and its optional head fragment, to jellyfin-web's <c>index.html</c> at request time.
/// One instance per plugin, registered by
/// <see cref="KitWebServiceCollectionExtensions.AddIndexHtmlInjection"/>.
/// </summary>
/// <remarks>
/// <para>Plugin <see cref="IStartupFilter"/>s wrap Jellyfin's whole <c>Startup.Configure</c>, so this middleware sits
/// outside <c>UseBaseUrlRedirection</c> and <c>Map(BaseUrl)</c> and sees the full path. They run in
/// plugin-name order, the first registered outermost, and all of them outside File Transformation, whose tags are
/// therefore already in the body buffered here. Nothing here assumes the position: no other plugin's tag is
/// read and no request header is assumed present.</para>
/// <para>Only GET <c>…/web/</c> and <c>…/web/index.html</c> under the BaseUrl are touched, also behind
/// Jellyfin's legacy <c>/emby</c> or <c>/mediabrowser</c> prefix, which its <c>UsePathTrim</c> removes: conditional,
/// range and compression headers are stripped from the request (step 4), the response is buffered up to 1 MB (step 5),
/// and a 200 <c>text/html</c> response without <c>Content-Encoding</c> gets the insert-only transform (step 6) and loses
/// <c>ETag</c>, <c>Last-Modified</c> and <c>Accept-Ranges</c>, with <c>Cache-Control: no-cache</c> and the new
/// <c>Content-Length</c> (step 7). Anything else, HEAD included, passes through unbuffered. Any failure of our own code
/// serves the original bytes and logs one warning per process (step 8); exceptions from the rest of the pipeline
/// propagate after the body stream is restored.</para>
/// </remarks>
internal sealed class IndexHtmlInjectionStartupFilter : IStartupFilter
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "WebInjection";

    /// <summary>The largest response that is buffered and transformed; a larger one is served unchanged.</summary>
    public const int MaxBufferedBytes = 1024 * 1024;

    private const string WebDefault = "/web/";
    private const string WebIndex = "/web/index.html";

    // Jellyfin's UsePathTrim (LegacyEmbyRouteRewriteMiddleware, inside Map(BaseUrl)) removes one of these from the start
    // of the path, so the web client is also served at .../emby/web/ and .../mediabrowser/web/index.html.
    private const string EmbyPrefix = "/emby";
    private const string MediaBrowserPrefix = "/mediabrowser";

    // If-None-Match and If-Modified-Since avoid a 304 that would keep a browser on an old copy (JE and JSI strip
    // neither); If-Range and Range a partial body; Accept-Encoding the inner UseResponseCompression.
    private static readonly string[] _strippedRequestHeaders =
    [
        HeaderNames.IfNoneMatch,
        HeaderNames.IfModifiedSince,
        HeaderNames.IfRange,
        HeaderNames.Range,
        HeaderNames.AcceptEncoding,
    ];

    private readonly KitOptions _options;
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger _logger;
    private readonly string? _scriptTag;
    private readonly string _bodyMarker;
    private readonly string _headMarker;
    private int _transformWarned;
    private int _overflowWarned;
    private int _settingsWarned;
    private int _fragmentWarned;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexHtmlInjectionStartupFilter"/> class. Never throws: the host
    /// builds its pipeline with it, and a throw would stop the server.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="configuration">The server configuration, for the BaseUrl.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public IndexHtmlInjectionStartupFilter(KitOptions options, IServerConfigurationManager configuration, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _configuration = configuration;
        _logger = options.CreateLogger(loggerFactory, LoggerComponent);
        _bodyMarker = IndexHtmlMarkers.Plugin(options.Prefix);
        _headMarker = IndexHtmlMarkers.Head(options.Prefix);
        try
        {
            _scriptTag = IndexHtmlMarkers.ScriptTag(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The {Plugin} script tag couldn't be built, so its web UI isn't added to index.html", options.PluginName);
        }
    }

    /// <summary>
    /// Gets the script tag this filter inserts, or <c>null</c> when it couldn't be built (the filter then does nothing).
    /// </summary>
    public string? ScriptTag => _scriptTag;

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private static bool IsHtml(string? contentType)
        => contentType is not null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithIndexSuffix(string path)
        => path.EndsWith(WebDefault, StringComparison.OrdinalIgnoreCase) || path.EndsWith(WebIndex, StringComparison.OrdinalIgnoreCase);

    // As v10.11.5 LegacyEmbyRouteRewriteMiddleware does: at most one prefix, "/emby" first, any case.
    private static string TrimLegacyPrefix(string path)
    {
        if (path.StartsWith(EmbyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path[EmbyPrefix.Length..];
        }

        return path.StartsWith(MediaBrowserPrefix, StringComparison.OrdinalIgnoreCase) ? path[MediaBrowserPrefix.Length..] : path;
    }

    private async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (_scriptTag is null || !HttpMethods.IsGet(context.Request.Method) || !IsIndexPath(context.Request.Path) || !IsWebUiEnabled(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        foreach (var header in _strippedRequestHeaders)
        {
            context.Request.Headers.Remove(header);
        }

        var response = context.Response;
        var originalBody = response.Body;
        using var buffer = new CappedBufferStream(originalBody, MaxBufferedBytes);
        response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = originalBody;
        }

        if (buffer.Overflowed)
        {
            // Already written through, untransformed, with the inner pipeline's own headers.
            if (Interlocked.Exchange(ref _overflowWarned, 1) == 0)
            {
                _logger.LogWarning(
                    "index.html is larger than {Limit} bytes, so it was served without the {Plugin} script tag (logged once per process)",
                    MaxBufferedBytes,
                    _options.PluginName);
            }

            return;
        }

        var bytes = buffer.ToArray();
        if (response.StatusCode == StatusCodes.Status200OK && IsHtml(response.ContentType) && !response.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            bytes = Transform(context, bytes, _scriptTag);
            response.Headers.Remove(HeaderNames.ETag);
            response.Headers.Remove(HeaderNames.LastModified);
            response.Headers.Remove(HeaderNames.AcceptRanges);
            response.Headers.CacheControl = "no-cache";
            response.ContentLength = bytes.Length;
        }

        if (bytes.Length > 0)
        {
            await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private bool IsIndexPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || !EndsWithIndexSuffix(value))
        {
            // Every request passes through here (API, images, streams), so the cheap suffix test comes first.
            return false;
        }

        string baseUrl;
        try
        {
            baseUrl = _configuration.GetNetworkConfiguration().BaseUrl?.TrimEnd('/') ?? string.Empty;
        }
        catch (Exception ex)
        {
            WarnSettings(ex, "the server's BaseUrl");
            return false;
        }

        if (baseUrl.Length > 0)
        {
            if (!value.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                // Outside the BaseUrl: Jellyfin redirects it, and the web client is never served there.
                return false;
            }

            value = value[baseUrl.Length..];
        }

        // The web client is served behind the legacy prefixes too, and File Transformation's tags reach it there.
        value = TrimLegacyPrefix(value);

        // "/web" without the slash is a redirect to "/web/", and stays untouched.
        return value.Equals(WebDefault, StringComparison.OrdinalIgnoreCase) || value.Equals(WebIndex, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsWebUiEnabled(HttpContext context)
    {
        try
        {
            return _options.WebUiEnabled(context.RequestServices);
        }
        catch (Exception ex)
        {
            // Leave index.html alone when the plugin's own setting can't be read.
            WarnSettings(ex, "the EnableWebUi setting");
            return false;
        }
    }

    private byte[] Transform(HttpContext context, byte[] bytes, string scriptTag)
    {
        try
        {
            var fragment = _options.HeadFragment?.Invoke(context);
            var result = IndexHtmlInjector.Insert(bytes, _bodyMarker, scriptTag, _headMarker, fragment, out var output);
            if (result.Head == IndexHtmlHeadOutcome.MissingMarker && Interlocked.Exchange(ref _fragmentWarned, 1) == 0)
            {
                _logger.LogWarning(
                    "The {Plugin} head fragment has no {Marker} attribute, so it isn't inserted into index.html (logged once per process)",
                    _options.PluginName,
                    _headMarker);
            }

            return output;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _transformWarned, 1) == 0)
            {
                _logger.LogWarning(
                    ex,
                    "Adding the {Plugin} web UI to index.html failed, so the original index.html was served (logged once per process)",
                    _options.PluginName);
            }

            return bytes;
        }
    }

    private void WarnSettings(Exception exception, string what)
    {
        if (Interlocked.Exchange(ref _settingsWarned, 1) == 0)
        {
            _logger.LogWarning(
                exception,
                "Reading {Setting} failed, so index.html was served without the {Plugin} web UI (logged once per process)",
                what,
                _options.PluginName);
        }
    }
}
