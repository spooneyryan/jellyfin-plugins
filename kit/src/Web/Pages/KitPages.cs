using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// The kit's <see cref="IKitPages"/>: the page shells, for the pages of
/// <c>KitOptions.Pages</c>.
/// </summary>
internal sealed class KitPages : IKitPages
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "Pages";

    /// <summary>The content type of a shell.</summary>
    public const string ContentType = "text/html; charset=utf-8";

    /// <summary>The 404 message for a key the plugin never had.</summary>
    public const string UnknownPageMessage = "There is no such page.";

    private readonly KitOptions _options;
    private readonly ILogger _logger;
    private int _settingWarned;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitPages"/> class.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public KitPages(KitOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options;
        _logger = options.CreateLogger(loggerFactory, LoggerComponent);
    }

    /// <inheritdoc />
    public IActionResult Shell(string key) => new ShellResult(this, key);

    /// <summary>
    /// Finds a known page by its key (ordinal).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The page, or <c>null</c> when the plugin never had that key.</returns>
    public KitPage? Find(string? key)
        => key is null ? null : _options.Pages.FirstOrDefault(p => p is not null && string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Reads whether a page is turned on. A setting that can't be read counts as off, with one warning per process.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="services">The request's (or the server's) services.</param>
    /// <returns><c>true</c> when the page is on.</returns>
    public bool IsEnabled(KitPage page, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(page);
        try
        {
            return page.IsEnabled(services);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _settingWarned, 1) == 0)
            {
                _logger.LogWarning(ex, "Reading whether the {Plugin} page {Key} is on failed, so it is treated as turned off (logged once per process)", _options.PluginName, page.Key);
            }

            return false;
        }
    }

    // Evaluated when MVC executes the result, with the request's services (like EnableWebUi).
    private sealed class ShellResult : IActionResult
    {
        private readonly KitPages _pages;
        private readonly string _key;

        public ShellResult(KitPages pages, string key)
        {
            _pages = pages;
            _key = key;
        }

        public Task ExecuteResultAsync(ActionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var page = _pages.Find(_key);
            if (page is null)
            {
                return KitProblemDetails.Result(PluginErrorKind.NotFound, UnknownPageMessage).ExecuteResultAsync(context);
            }

            var html = _pages.IsEnabled(page, context.HttpContext.RequestServices)
                ? KitPageMarkup.Shell(_pages._options, page)
                : KitPageMarkup.TurnedOff(_pages._options, page);
            context.HttpContext.Response.Headers.CacheControl = "no-cache";
            return new ContentResult { Content = html, ContentType = ContentType, StatusCode = StatusCodes.Status200OK }.ExecuteResultAsync(context);
        }
    }
}
