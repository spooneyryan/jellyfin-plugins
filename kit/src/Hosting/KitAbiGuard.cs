using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The ABI guard, the first statement of every plugin's <c>RegisterServices</c>.
/// </summary>
/// <remarks>
/// <para>Jellyfin's loader has no ABI ceiling: 12.1 marks a plugin supported whenever the server is at least its
/// <c>targetAbi</c>, so a 10.11 build would load Active on 12 and fail only when it calls a changed API.
/// The guard compares <c>IApplicationHost.ApplicationVersion</c> with the compiled <see cref="KitBuild.JellyfinTarget"/>:
/// a <c>10.11</c> build accepts any 10.11.x server, a <c>12</c> build any 12.x server.</para>
/// <para>On a mismatch, or when the check itself fails, it logs one Error and registers nothing but
/// <see cref="KitAbiStatus"/>: no startup filter, hosted service, event consumer or store. What Jellyfin finds without DI
/// is covered elsewhere: controllers carry <see cref="Api.KitAbiGateAttribute"/>, and scheduled tasks check the status.</para>
/// </remarks>
internal static class KitAbiGuard
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "AbiGuard";

    /// <summary>
    /// Evaluates the guard and registers <see cref="KitAbiStatus"/>. Never throws.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <param name="applicationHost">The server.</param>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="logError">Where the one Error goes (category, message); defaults to <see cref="KitStartupLog.Error"/>.</param>
    /// <returns><c>true</c> when the plugin may register the rest of its services; <c>false</c> when it must return at once.</returns>
    public static bool Register(
        IServiceCollection services,
        IServerApplicationHost applicationHost,
        KitOptions options,
        Action<string, string>? logError = null)
    {
        try
        {
            var status = Evaluate(options.PluginName, () => KitBuild.JellyfinTarget, () => ReadServerVersion(applicationHost));
            services.AddSingleton(status);
            if (!status.IsActive)
            {
                (logError ?? LogWithStartupLog)(options.GetLoggerCategory(LoggerComponent), status.Message);
            }

            return status.IsActive;
        }
        catch (Exception)
        {
            // Only reachable if the service collection or the logger threw; stay inert rather than half registered.
            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the guard let the plugin run. For what Jellyfin creates without the plugin's
    /// registrations (scheduled tasks, post-scan tasks): they return at once when this is <c>false</c>. Without a
    /// registered <see cref="KitAbiStatus"/> the answer is <c>false</c>.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <returns><c>true</c> when the plugin is active.</returns>
    public static bool IsActive(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<KitAbiStatus>() is { IsActive: true };
    }

    /// <summary>
    /// Decides whether a build for <paramref name="target"/> runs on the server. A failing check counts as a mismatch.
    /// </summary>
    /// <param name="pluginName">The plugin's display name, for the message.</param>
    /// <param name="target">Reads the compiled target (<see cref="KitBuild.JellyfinTarget"/>).</param>
    /// <param name="serverVersion">Reads the server's version.</param>
    /// <returns>The status.</returns>
    public static KitAbiStatus Evaluate(string pluginName, Func<string> target, Func<Version?> serverVersion)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(serverVersion);

        string? targetText = null;
        string? serverText = null;
        try
        {
            targetText = target();
            var server = serverVersion() ?? throw new InvalidOperationException("The server reported no version.");
            serverText = Format(server);
            return Accepts(targetText, server)
                ? new KitAbiStatus(pluginName, targetText, serverText, true, $"{pluginName} is built for Jellyfin {targetText}; the server is Jellyfin {serverText}.")
                : new KitAbiStatus(
                    pluginName,
                    targetText,
                    serverText,
                    false,
                    $"{pluginName} is built for Jellyfin {targetText}, but the server is Jellyfin {serverText}: the plugin is inactive. "
                    + $"Install the {pluginName} build for Jellyfin {GetLine(server)}.");
        }
        catch (Exception ex)
        {
            return new KitAbiStatus(
                pluginName,
                targetText ?? "unknown",
                serverText,
                false,
                $"{pluginName} couldn't check the Jellyfin version ({ex.GetType().Name}: {ex.Message}): the plugin is inactive.");
        }
    }

    /// <summary>
    /// Gets a value indicating whether a build for <paramref name="target"/> runs on <paramref name="server"/>:
    /// <c>10.11</c> accepts 10.11.x, <c>12</c> accepts 12.x. Anything else is refused.
    /// </summary>
    /// <param name="target">The compiled target.</param>
    /// <param name="server">The server's version.</param>
    /// <returns><c>true</c> when the build may run.</returns>
    public static bool Accepts(string target, Version server)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(server);

        var parts = target.Split('.');
        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || server.Major != major)
        {
            return false;
        }

        return parts.Length == 1
            || (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && server.Minor == minor);
    }

    // The Jellyfin line a build must target to run on this server: 10.x lines are major.minor, later ones major only.
    private static string GetLine(Version server)
        => server.Major == 10
            ? string.Create(CultureInfo.InvariantCulture, $"{server.Major}.{server.Minor}")
            : server.Major.ToString(CultureInfo.InvariantCulture);

    private static string Format(Version version) => version.Build >= 0 ? version.ToString(3) : version.ToString();

    // Kept out of Register so that a changed IApplicationHost member fails inside Evaluate's try (it throws when this
    // method is compiled, which happens on its first call) instead of when Register is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Version? ReadServerVersion(IServerApplicationHost applicationHost) => applicationHost.ApplicationVersion;

    private static void LogWithStartupLog(string category, string message) => KitStartupLog.Error(category, message);
}
