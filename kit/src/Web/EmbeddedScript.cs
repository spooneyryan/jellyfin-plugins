using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// Builds and serves the plugin's web client scripts, each once per process.
/// </summary>
/// <remarks>
/// <para><b>ClientScript</b>: one self-contained IIFE, in this order: a header with <c>KIT_BUILD</c> (plugin, prefix,
/// route, version, kit version, MVID) and the double-load guard <c>window.__jfkit[prefix]</c>, then <c>KIT_CSS</c>
/// (<c>kit/web/kit.css</c> with <c>__PFX__</c> replaced by the prefix, followed by the plugin's <c>Web/css/*.css</c> in name
/// order), <c>KIT_ANCHOR_DECLARATIONS</c> (the kit's and the plugin's jellyfin-web anchors, <c>kit/web/anchors.json</c>
/// and <c>Web/anchors.json</c>), <c>kit/web/kit.js</c>, the plugin's kit instance <c>kit</c>
/// (registered in <c>window.__jfkit[prefix]</c>), the plugin's <c>Web/js/*.js</c> in name order, and a footer. The files
/// share the IIFE's scope, so the names <c>KIT_BUILD</c>, <c>KIT_CSS</c>, <c>KIT_ANCHOR_DECLARATIONS</c>,
/// <c>KIT_NATIVE_CLASSES</c>, <c>KIT_ANCHORS</c>, <c>KitApiError</c>, <c>createKit</c> and <c>kit</c> are taken. A
/// developer build with <c>-p:KitRenameAnchors=&lt;names&gt;</c> renames those anchors' selectors
/// and logs a warning when it builds the script.</para>
/// <para><b>AdminScript</b> (optional): an ES module of the same header without the guard, <c>kit.js</c> with the admin
/// kit instance (<c>&lt;prefix&gt;-admin-styles</c>, <c>kit.css</c> plus <c>Web/admin/css/*.css</c>), the plugin's
/// <c>Web/admin/js/*.js</c> in name order, which must declare <c>mount(view, params)</c>, and <c>export { mount };</c>.
/// A plugin without <c>Web/admin/js</c> files has none.</para>
/// <para>The files are embedded by <c>kit/build/Kit.props</c> as <c>Kit.kit.js</c>, <c>Kit.kit.css</c> and
/// <c>Web.js.&lt;file&gt;</c>, <c>Web.css.&lt;file&gt;</c>, <c>Web.admin.js.&lt;file&gt;</c>, <c>Web.admin.css.&lt;file&gt;</c>.
/// Both scripts are served as <c>text/javascript; charset=utf-8</c> with <c>Cache-Control: no-cache</c> and the assembly
/// MVID as <c>ETag</c>. A build failure (a missing kit resource, a file that isn't valid UTF-8) is logged once
/// and answers 503, never an exception from the action.</para>
/// </remarks>
internal sealed class EmbeddedScript : IEmbeddedScript
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "WebScript";

    /// <summary>The content type of both scripts.</summary>
    public const string ContentType = "text/javascript; charset=utf-8";

    /// <summary>The resource name of <c>kit/web/kit.js</c>.</summary>
    public const string KitScriptResource = "Kit.kit.js";

    /// <summary>The resource name of <c>kit/web/kit.css</c>.</summary>
    public const string KitCssResource = "Kit.kit.css";

    /// <summary>The resource name prefix of the plugin's <c>Web/js/*.js</c> files.</summary>
    public const string ScriptFolder = "Web.js.";

    /// <summary>The resource name prefix of the plugin's <c>Web/css/*.css</c> files.</summary>
    public const string CssFolder = "Web.css.";

    /// <summary>The resource name prefix of the plugin's <c>Web/admin/js/*.js</c> files.</summary>
    public const string AdminScriptFolder = "Web.admin.js.";

    /// <summary>The resource name prefix of the plugin's <c>Web/admin/css/*.css</c> files.</summary>
    public const string AdminCssFolder = "Web.admin.css.";

    /// <summary>The token in <c>kit.css</c> that becomes the plugin's prefix.</summary>
    public const string PrefixToken = "__PFX__";

    /// <summary>The script variable holding the anchor declarations (<see cref="WebAnchorCatalog.ToClientJson"/>).</summary>
    public const string AnchorsVariable = "KIT_ANCHOR_DECLARATIONS";

    /// <summary>The function the plugin's admin files declare and the AdminScript module exports.</summary>
    public const string AdminExport = "mount";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly KitOptions _options;
    private readonly Assembly _kitAssembly;
    private readonly ILogger _logger;
    private readonly Lazy<byte[]> _clientScript;
    private readonly Lazy<byte[]?> _adminScript;
    private int _clientFailureLogged;
    private int _adminFailureLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedScript"/> class. Nothing is built until the first request.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public EmbeddedScript(KitOptions options, ILoggerFactory loggerFactory)
        : this(options, options?.PluginAssembly!, loggerFactory)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedScript"/> class with the kit's resources read from another
    /// assembly (tests). Internal, so dependency injection only sees the public constructor.
    /// </summary>
    /// <param name="options">The plugin's kit options; the plugin's own files are read from its assembly.</param>
    /// <param name="kitAssembly">The assembly holding <c>Kit.kit.js</c> and <c>Kit.kit.css</c>.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    internal EmbeddedScript(KitOptions options, Assembly kitAssembly, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kitAssembly);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _kitAssembly = kitAssembly;
        _logger = options.CreateLogger(loggerFactory, LoggerComponent);
        ETag = new EntityTagHeaderValue("\"" + options.PluginAssembly.ManifestModule.ModuleVersionId.ToString("N", CultureInfo.InvariantCulture) + "\"");
        _clientScript = new Lazy<byte[]>(BuildClientScriptOnce, LazyThreadSafetyMode.ExecutionAndPublication);
        _adminScript = new Lazy<byte[]?>(
            () => BuildAdminScript(_options, _kitAssembly) is { } text ? _strictUtf8.GetBytes(text) : null,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the entity tag of both scripts: the plugin assembly's MVID, which changes with every build.
    /// </summary>
    public EntityTagHeaderValue ETag { get; }

    /// <summary>
    /// Gets the ClientScript's text.
    /// </summary>
    /// <exception cref="InvalidOperationException">The script couldn't be built.</exception>
    public string ClientScriptText => _strictUtf8.GetString(_clientScript.Value);

    /// <summary>
    /// Gets the AdminScript module's text, or <c>null</c> when the plugin has no <c>Web/admin/js</c> files.
    /// </summary>
    /// <exception cref="InvalidOperationException">The module couldn't be built.</exception>
    public string? AdminScriptText => _adminScript.Value is { } bytes ? _strictUtf8.GetString(bytes) : null;

    /// <inheritdoc />
    public IActionResult ClientScript()
    {
        try
        {
            return new KitScriptResult(_clientScript.Value, ETag);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DecoderFallbackException or EncoderFallbackException or IOException)
        {
            if (Interlocked.Exchange(ref _clientFailureLogged, 1) == 0)
            {
                _logger.LogError(ex, "The {Plugin} web client script couldn't be built; its web UI won't load", _options.PluginName);
            }

            return KitProblemDetails.Result(PluginErrorKind.Unavailable, "The web client script couldn't be built. The server log has the details.");
        }
    }

    /// <inheritdoc />
    public IActionResult AdminScript()
    {
        try
        {
            return _adminScript.Value is { } bytes
                ? new KitScriptResult(bytes, ETag)
                : KitProblemDetails.Result(PluginErrorKind.NotFound, "This plugin has no dashboard script.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DecoderFallbackException or EncoderFallbackException or IOException)
        {
            if (Interlocked.Exchange(ref _adminFailureLogged, 1) == 0)
            {
                _logger.LogError(ex, "The {Plugin} dashboard script couldn't be built; its dashboard page won't load", _options.PluginName);
            }

            return KitProblemDetails.Result(PluginErrorKind.Unavailable, "The dashboard script couldn't be built. The server log has the details.");
        }
    }

    /// <summary>
    /// Builds the ClientScript.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="kitAssembly">The assembly holding the kit's resources.</param>
    /// <returns>The script.</returns>
    /// <exception cref="InvalidOperationException">A kit resource is missing or a file isn't valid UTF-8.</exception>
    public static string BuildClientScript(KitOptions options, Assembly kitAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kitAssembly);

        var files = ResourceNames(options.PluginAssembly, ScriptFolder, ".js");
        var script = new StringBuilder();
        script.Append("/* ").Append(Comment(options.PluginName)).Append(' ').Append(IndexHtmlMarkers.Version(options.PluginAssembly))
            .Append(" web client, kit ").Append(Comment(KitVersion())).Append(": built by the kit's EmbeddedScript from KIT_BUILD, KIT_CSS, kit/web/kit.js and ")
            .Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append(" plugin file(s) of Web/js. */\n");
        script.Append("(function () {\n'use strict';\n");
        script.Append("var KIT_BUILD = ").Append(BuildJson(options)).Append(";\n");
        script.Append("window.__jfkit = window.__jfkit || {};\n");
        script.Append("if (window.__jfkit[KIT_BUILD.prefix]) {\n    return;\n}\n");
        script.Append("var KIT_CSS = ").Append(JsonSerializer.Serialize(BuildCss(options, kitAssembly, CssFolder))).Append(";\n");
        script.Append("var ").Append(AnchorsVariable).Append(" = ").Append(BuildAnchors(options, kitAssembly)).Append(";\n");
        AppendFile(script, "kit/web/kit.js", ReadResource(kitAssembly, KitScriptResource));
        script.Append("\n// ---- the plugin's kit\n");
        script.Append("var kit = createKit(KIT_BUILD, KIT_CSS, { anchors: ").Append(AnchorsVariable).Append(" });\n");
        script.Append("window.__jfkit[KIT_BUILD.prefix] = { plugin: KIT_BUILD.plugin, version: KIT_BUILD.version, kit: KIT_BUILD.kit, health: kit.health, timing: kit.timing, instance: kit };\n");
        foreach (var name in files)
        {
            AppendFile(script, "Web/js/" + name[ScriptFolder.Length..], ReadResource(options.PluginAssembly, name));
        }

        script.Append("\n// ---- end\n})();\n");
        return script.ToString();
    }

    /// <summary>
    /// Builds the AdminScript ES module, or returns <c>null</c> when the plugin has no <c>Web/admin/js</c>
    /// files.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="kitAssembly">The assembly holding the kit's resources.</param>
    /// <returns>The module, or <c>null</c>.</returns>
    /// <exception cref="InvalidOperationException">A kit resource is missing or a file isn't valid UTF-8.</exception>
    public static string? BuildAdminScript(KitOptions options, Assembly kitAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kitAssembly);

        var files = ResourceNames(options.PluginAssembly, AdminScriptFolder, ".js");
        if (files.Count == 0)
        {
            return null;
        }

        var script = new StringBuilder();
        script.Append("/* ").Append(Comment(options.PluginName)).Append(' ').Append(IndexHtmlMarkers.Version(options.PluginAssembly))
            .Append(" dashboard module, kit ").Append(Comment(KitVersion())).Append(": built by the kit's EmbeddedScript from KIT_BUILD, KIT_CSS, kit/web/kit.js and ")
            .Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append(" plugin file(s) of Web/admin/js. */\n");
        script.Append("const KIT_BUILD = ").Append(BuildJson(options)).Append(";\n");
        script.Append("const KIT_CSS = ").Append(JsonSerializer.Serialize(BuildCss(options, kitAssembly, AdminCssFolder))).Append(";\n");
        script.Append("const ").Append(AnchorsVariable).Append(" = ").Append(BuildAnchors(options, kitAssembly)).Append(";\n");
        AppendFile(script, "kit/web/kit.js", ReadResource(kitAssembly, KitScriptResource));
        script.Append("\n// ---- the plugin's dashboard kit\n");
        script.Append("const kit = createKit(KIT_BUILD, KIT_CSS, { styleId: KIT_BUILD.prefix + '-admin-styles', anchors: ").Append(AnchorsVariable).Append(" });\n");
        foreach (var name in files)
        {
            AppendFile(script, "Web/admin/js/" + name[AdminScriptFolder.Length..], ReadResource(options.PluginAssembly, name));
        }

        script.Append("\n// ---- end\nexport { ").Append(AdminExport).Append(" };\n");
        return script.ToString();
    }

    /// <summary>
    /// Builds <c>KIT_CSS</c>: <c>kit.css</c> for the plugin's prefix, followed by the plugin's CSS files of one folder in
    /// name order.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="kitAssembly">The assembly holding <c>Kit.kit.css</c>.</param>
    /// <param name="folder"><see cref="CssFolder"/> or <see cref="AdminCssFolder"/>.</param>
    /// <returns>The style sheet.</returns>
    public static string BuildCss(KitOptions options, Assembly kitAssembly, string folder)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kitAssembly);

        var css = new StringBuilder(ReadResource(kitAssembly, KitCssResource).Replace(PrefixToken, options.Prefix, StringComparison.Ordinal));
        foreach (var name in ResourceNames(options.PluginAssembly, folder, ".css"))
        {
            css.Append("\n/* ").Append(folder.Replace('.', '/')).Append(Comment(name[folder.Length..])).Append(" */\n").Append(ReadResource(options.PluginAssembly, name));
        }

        return css.ToString();
    }

    /// <summary>
    /// Gets the names of the plugin's embedded web files in one folder, in name order (ordinal).
    /// </summary>
    /// <param name="assembly">The plugin's assembly.</param>
    /// <param name="folder">The resource name prefix, for example <see cref="ScriptFolder"/>.</param>
    /// <param name="extension">The file extension, for example <c>.js</c>.</param>
    /// <returns>The resource names.</returns>
    public static IReadOnlyList<string> ResourceNames(Assembly assembly, string folder, string extension)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(folder, StringComparison.Ordinal) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Builds <c>KIT_ANCHOR_DECLARATIONS</c>: the kit's anchors, then the plugin's, with the
    /// anchors a developer build's <c>-p:KitRenameAnchors</c> names renamed so they match nothing.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="kitAssembly">The assembly holding <c>Kit.anchors.json</c>.</param>
    /// <returns>A JSON array.</returns>
    /// <exception cref="InvalidOperationException">The kit's anchors are missing.</exception>
    public static string BuildAnchors(KitOptions options, Assembly kitAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kitAssembly);

        if (!kitAssembly.GetManifestResourceNames().Contains(WebAnchorCatalog.KitResource, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"The embedded resource '{WebAnchorCatalog.KitResource}' is missing from {kitAssembly.GetName().Name}; kit/build/Kit.props embeds it.");
        }

        return WebAnchorCatalog.Load(options.PluginAssembly, kitAssembly).ToClientJson(WebAnchorCatalog.RenamedAnchors(options.PluginAssembly));
    }

    private byte[] BuildClientScriptOnce()
    {
        var bytes = _strictUtf8.GetBytes(BuildClientScript(_options, _kitAssembly));
        var renamed = WebAnchorCatalog.RenamedAnchors(_options.PluginAssembly);
        if (renamed.Count > 0)
        {
            _logger.LogWarning(
                "This {Plugin} build renames the web anchors {Anchors} (-p:KitRenameAnchors): a developer build that simulates missing jellyfin-web anchors. Never install it on a real server",
                _options.PluginName,
                string.Join(", ", renamed));
        }

        return bytes;
    }

    private static string BuildJson(KitOptions options)
        => JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plugin"] = options.PluginName,
            ["prefix"] = options.Prefix,
            ["route"] = options.RoutePrefix,
            ["version"] = IndexHtmlMarkers.Version(options.PluginAssembly),
            ["kit"] = KitVersion(),
            ["jellyfin"] = JellyfinTarget(),
            ["mvid"] = IndexHtmlMarkers.CacheBuster(options.PluginAssembly)
        });

    private static string JellyfinTarget()
    {
        try
        {
            return KitBuild.JellyfinTarget;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string KitVersion()
    {
        try
        {
            return KitBuild.KitVersion;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static void AppendFile(StringBuilder script, string label, string text)
    {
        script.Append("\n// ---- ").Append(label).Append('\n').Append(text);
        if (!text.EndsWith('\n'))
        {
            script.Append('\n');
        }
    }

    // A value placed inside a /* */ comment.
    private static string Comment(string value) => value.Replace("*/", "* /", StringComparison.Ordinal);

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing from {assembly.GetName().Name}; kit/build/Kit.props embeds it.");
        using var reader = new StreamReader(stream, _strictUtf8, detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException($"The embedded resource '{name}' isn't valid UTF-8.", ex);
        }
    }
}
