using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.AccessRoles.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AccessRoles.Hosting;

/// <summary>
/// The emergency stop in effect at the server's start: the file
/// <c>data/accessroles/SAFE-MODE</c> exists, or <c>&lt;EnforcementEnabled&gt;false&lt;/EnforcementEnabled&gt;</c> is in
/// <c>plugins/configurations/Jellyfin.Plugin.AccessRoles.xml</c>. The plugin is then inert: it registers nothing but its admin
/// page (the kit's paths and the embedded scripts, so the page loads and says which of the two is in effect), and every
/// other action of its controllers answers 503 with this as the ProblemDetails' <c>inert</c> extension
/// (<see cref="Api.AccessRolesInertGateAttribute"/>). Nothing filters a request, writes a policy or changes the web client.
/// </summary>
/// <remarks>
/// Read before any service exists, from the server's <see cref="IApplicationPaths"/> instance in the service collection
/// (10.11.5's <c>ApplicationHost.RegisterServices</c> adds it before the plugins' registrators run); the XML is read for
/// that one element only. A file that can't be read counts as not stopping anything (the plugin runs and enforces: fail
/// closed). While the server runs, the same two switches pause enforcement as before (the filter, the sync and the head
/// read them per request); only a start makes the plugin inert.
/// </remarks>
/// <param name="SafeMode">Whether the SAFE-MODE file exists.</param>
/// <param name="EnforcementDisabled">Whether the configuration file has <c>EnforcementEnabled</c> false.</param>
/// <param name="SafeModeFile">The SAFE-MODE file's path.</param>
/// <param name="ConfigurationFile">The configuration file's path.</param>
internal sealed record AccessRolesInert(bool SafeMode, bool EnforcementDisabled, string SafeModeFile, string ConfigurationFile)
{
    /// <summary>The plugin configuration's file name (Jellyfin's <c>BasePlugin</c>: the assembly's name, <c>.xml</c>).</summary>
    public const string ConfigurationFileName = "Jellyfin.Plugin.AccessRoles.xml";

    /// <summary>Gets the message of the 503 and the log.</summary>
    public string Message => "Access Roles is inert (the emergency stop: "
        + string.Join(" and ", new[] { SafeMode ? "the SAFE-MODE file exists" : null, EnforcementDisabled ? "EnforcementEnabled is false in its configuration file" : null }.Where(s => s is not null))
        + "): it registered nothing but its admin page at the server's start, so nothing is enforced, no library access is written and the web client is unchanged.";

    /// <summary>
    /// Finds whether the emergency stop is in effect. Never throws.
    /// </summary>
    /// <param name="services">The server's service collection, as the registrator gets it.</param>
    /// <returns>The stop, or <c>null</c> when the plugin runs (or the paths aren't known).</returns>
    public static AccessRolesInert? Detect(IServiceCollection services)
    {
        try
        {
            if (services?.LastOrDefault(d => d.ServiceType == typeof(IApplicationPaths))?.ImplementationInstance is not IApplicationPaths paths)
            {
                return null;
            }

            return Detect(paths.DataPath, paths.PluginConfigurationsPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds whether the emergency stop is in effect for a data folder and a configurations folder. Never throws.
    /// </summary>
    /// <param name="dataPath">The server's data folder (<c>IApplicationPaths.DataPath</c>).</param>
    /// <param name="configurationsPath">The plugin configurations' folder.</param>
    /// <returns>The stop, or <c>null</c>.</returns>
    public static AccessRolesInert? Detect(string? dataPath, string? configurationsPath)
    {
        var safeModeFile = string.IsNullOrEmpty(dataPath) ? string.Empty : Path.Combine(dataPath, AccessRolesKit.Options.DataFolder, AccessRolesPaths.SafeModeFileName);
        var configurationFile = string.IsNullOrEmpty(configurationsPath) ? string.Empty : Path.Combine(configurationsPath, ConfigurationFileName);
        var safeMode = safeModeFile.Length > 0 && Exists(safeModeFile);
        var disabled = configurationFile.Length > 0 && EnforcementDisabledIn(configurationFile);
        return safeMode || disabled ? new AccessRolesInert(safeMode, disabled, safeModeFile, configurationFile) : null;
    }

    /// <summary>
    /// Describes it for the 503's <c>inert</c> extension and the admin page.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToSummary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["SafeMode"] = SafeMode,
        ["EnforcementDisabled"] = EnforcementDisabled,
        ["SafeModeFile"] = SafeModeFile,
        ["ConfigurationFile"] = ConfigurationFile
    };

    private static bool Exists(string file)
    {
        try
        {
            return File.Exists(file);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // <EnforcementEnabled>false</EnforcementEnabled> directly under the root element, as XmlSerializer writes it.
    private static bool EnforcementDisabledIn(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            var root = XDocument.Load(file).Root;
            var value = root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "EnforcementEnabled", StringComparison.Ordinal))?.Value;
            return value is not null && string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
