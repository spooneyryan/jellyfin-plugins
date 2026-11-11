using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The configuration reads (<c>NonAdminConfigurationReads</c>): which named keys a non-admin may read, and
/// the copy of a configuration object with its secrets stripped, chosen by the object's runtime type (never the key
/// string). The server's live configuration objects are <b>never modified</b> (checked on 10.11.5: stripping
/// <c>ObjectResult.Value</c> in place blanked the live <c>NetworkConfiguration</c> with one non-admin read): the copy is
/// made by serialising the object with the MVC JSON options and reading the bytes back into a new object of the same type,
/// and it is used only when it serialises to the very same bytes (so the response is the native one but for the stripped
/// fields); otherwise the read is refused (fail closed).
/// </summary>
internal sealed class ConfigurationReadGuard
{
    // Runtime type full name -> the property paths blanked on the copy ("*" = every element; a list of texts is emptied):
    // secrets, and the server's filesystem paths and devices. A type with no paths is known to carry none; any other type
    // is unknown (refused in Redact mode).
    private static readonly Dictionary<string, string[][]> _stripped = new(StringComparer.Ordinal)
    {
        // v10.11.5:MediaBrowser.Common/Net/NetworkConfiguration.cs:79; and where the certificate is (no filesystem path of
        // the server's).
        ["MediaBrowser.Common.Net.NetworkConfiguration"] = [["CertificatePassword"], ["CertificatePath"]],

        // M3U URLs often carry the provider's user name and password (TunerHostInfo.cs:20,40); the listing providers'
        // accounts and XMLTV URLs (ListingsProviderInfo.cs:26-36); the recording folders, the post-processor and the media
        // folders Live TV created (LiveTvOptions.cs:20-40).
        ["MediaBrowser.Model.LiveTv.LiveTvOptions"] =
        [
            ["TunerHosts", "*", "Url"], ["TunerHosts", "*", "Source"],
            ["ListingProviders", "*", "Username"], ["ListingProviders", "*", "Password"], ["ListingProviders", "*", "Path"],
            ["RecordingPath"], ["MovieRecordingPath"], ["SeriesRecordingPath"], ["RecordingPostProcessor"], ["MediaLocationsCreated"]
        ],
        ["MediaBrowser.Model.Providers.SubtitleOptions"] = [["OpenSubtitlesUsername"], ["OpenSubtitlesPasswordHash"]],
        ["Jellyfin.Database.Implementations.DbConfiguration.DatabaseConfigurationOptions"] = [["CustomProviderOptions", "ConnectionString"]],

        // The server's paths and devices (EncodingOptions.cs:72,77,132-147): the encoding key is on the default allow-list
        // because jellyfin-web's video player reads it, but only EnableFallbackFont (htmlVideoPlayer's renderSsaAss; the
        // fonts come from GET /FallbackFont/Fonts), so a non-admin never gets where FFmpeg, the transcodes and the fonts
        // are or which render devices the server has.
        ["MediaBrowser.Model.Configuration.EncodingOptions"] =
        [
            ["EncoderAppPath"], ["EncoderAppPathDisplay"], ["TranscodingTempPath"], ["FallbackFontPath"], ["VaapiDevice"], ["QsvDevice"]
        ],

        // The server's own paths (BaseApplicationConfiguration.CachePath,
        // ServerConfiguration.MetadataPath and PathSubstitutions' From and To, v10.11.5): GET /System/Configuration in Redact
        // mode (AllowList refuses it, as the server configuration has no key of its own to allow-list).
        ["MediaBrowser.Model.Configuration.ServerConfiguration"] =
        [
            ["CachePath"], ["MetadataPath"], ["PathSubstitutions", "*", "From"], ["PathSubstitutions", "*", "To"]
        ],

        // Where the uploaded splash screen is on disk (BrandingOptions.SplashscreenLocation, v10.11.5): the
        // login page gets it through GET /Branding/Splashscreen, never this path.
        ["MediaBrowser.Model.Branding.BrandingOptions"] = [["SplashscreenLocation"]],

        // The other configuration stores of 10.11.5 (BaseConfigurationManager's stores).
        ["MediaBrowser.Model.Configuration.MetadataConfiguration"] = [],
        ["MediaBrowser.Model.Configuration.XbmcMetadataOptions"] = []
    };

    private readonly Func<JsonSerializerOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationReadGuard"/> class.
    /// </summary>
    /// <param name="options">The JSON options MVC writes responses with (read at each copy, so a late MVC setup counts).</param>
    public ConfigurationReadGuard(Func<JsonSerializerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Gets the configuration types with fields stripped on the copy: secrets, and the server's filesystem paths
    /// and devices (the encoding options', the server configuration's, Live TV's, the certificate's and the splash
    /// screen's).
    /// </summary>
    public static IReadOnlyCollection<string> TypesWithStrippedFields { get; } = [.. _stripped.Where(s => s.Value.Length > 0).Select(s => s.Key)];

    /// <summary>
    /// Gets every configuration type the guard knows.
    /// </summary>
    public static IReadOnlyCollection<string> KnownTypes { get; } = [.. _stripped.Keys];

    /// <summary>
    /// Normalises a configuration key as the allow-list compares it: <c>ToLowerInvariant()</c>, because the core resolves
    /// keys case-insensitively and caches <c>NETWORK</c> as a second object (checked on 10.11.5).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The normalised key.</returns>
    public static string Normalize(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets whether the guard knows a configuration object's runtime type.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns><c>true</c> for a known type.</returns>
    public static bool IsKnown(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.FullName is { } name && _stripped.ContainsKey(name);
    }

    /// <summary>
    /// Copies a configuration object of a known type and strips its secrets (and the encoding options' server paths) on the
    /// copy.
    /// </summary>
    /// <param name="value">The live configuration object; never changed.</param>
    /// <param name="stripped">Receives the property paths that held a value and were blanked.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="InvalidOperationException">The type isn't known, or its copy doesn't serialise like the original.</exception>
    public object Copy(object value, out IReadOnlyList<string> stripped)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = value.GetType();
        if (type.FullName is not { } name || !_stripped.TryGetValue(name, out var paths))
        {
            throw new InvalidOperationException($"Access Roles doesn't know the configuration type {type.FullName}.");
        }

        var options = _options();
        var original = JsonSerializer.SerializeToUtf8Bytes(value, type, options);
        var copy = JsonSerializer.Deserialize(original, type, options) ?? throw new InvalidOperationException($"The copy of a {type.Name} is empty.");
        if (!original.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(copy, type, options)))
        {
            throw new InvalidOperationException($"A copy of a {type.Name} doesn't serialise like the original.");
        }

        var blanked = new List<string>();
        foreach (var path in paths)
        {
            Blank(copy, path, 0, string.Empty, blanked);
        }

        stripped = blanked;
        return copy;
    }

    private static void Blank(object target, string[] path, int index, string trail, List<string> blanked)
    {
        var segment = path[index];
        if (segment == "*")
        {
            if (target is IEnumerable items and not string)
            {
                var i = 0;
                foreach (var element in items)
                {
                    if (element is not null)
                    {
                        Blank(element, path, index + 1, trail + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", blanked);
                    }

                    i++;
                }
            }

            return;
        }

        var property = target.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return;
        }

        var here = trail.Length == 0 ? segment : trail + "." + segment;
        if (index == path.Length - 1)
        {
            if (property.PropertyType == typeof(string) && property.CanWrite && property.GetValue(target) is string { Length: > 0 })
            {
                property.SetValue(target, string.Empty);
                blanked.Add(here);
            }
            else if (property.PropertyType == typeof(string[]) && property.CanWrite && property.GetValue(target) is string[] { Length: > 0 })
            {
                property.SetValue(target, Array.Empty<string>());
                blanked.Add(here);
            }

            return;
        }

        if (property.GetValue(target) is { } child)
        {
            Blank(child, path, index + 1, here, blanked);
        }
    }
}
