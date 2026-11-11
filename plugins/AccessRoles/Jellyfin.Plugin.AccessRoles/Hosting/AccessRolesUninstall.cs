using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AccessRoles.Hosting;

/// <summary>
/// What uninstalling does (<c>BasePlugin.OnUninstalling</c>): 10.11.5's
/// <c>InstallationManager.UninstallPlugin</c> calls <see cref="Plugin.OnUninstalling"/> before it removes the plugin and asks for
/// a restart, while the plugin's services still run. The optional mirror stops for good (no run turns a flag off again until
/// the restart) and every <c>EnableUserPreferenceAccess</c> it turned off is written back on, audited
/// <c>PreferenceAccessRestored</c> with <c>ReleasedFor</c> <c>Uninstalling</c>. Native library access stays as last synced
/// (fail closed); the Overview's <em>Release all locks</em> restores it before an uninstall.
/// </summary>
/// <remarks>
/// Never throws, and waits at most <see cref="Timeout"/> (the uninstall is a request an admin waits on). When the plugin
/// started inert (the emergency stop) or its services aren't there, nothing is released: the README's recovery puts a flag
/// back with an API key.
/// </remarks>
internal static class AccessRolesUninstall
{
    /// <summary>The longest the uninstall waits for the release.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stops the mirror and releases its flags.
    /// </summary>
    /// <param name="services">The server's services; none: nothing is done.</param>
    /// <param name="timeout">The longest it waits.</param>
    /// <returns>What the release wrote; <see langword="null"/> when nothing was released or it didn't finish in time.</returns>
    public static IReadOnlyList<MirrorResult>? Run(IServiceProvider? services, TimeSpan timeout)
    {
        ILogger logger = NullLogger.Instance;
        try
        {
            if (services is null)
            {
                return null;
            }

            if (services.GetService<ILoggerFactory>() is { } factory)
            {
                logger = AccessRolesKit.Options.CreateLogger(factory, "Uninstall");
            }

            if (services.GetService<AccessRolesInert>() is { } inert)
            {
                logger.LogWarning("Access Roles is being uninstalled while inert ({Stop}): no preference access it turned off is put back; the README's recovery does it with an API key", inert.Message);
                return null;
            }

            if (services.GetService<PreferenceMirrorSync>() is not { } mirror)
            {
                return null;
            }

            mirror.StopForUninstall();
            var release = Task.Run(() => mirror.ReleaseAsync(AuditActor.System, null, PreferenceMirrorSync.Uninstalling, CancellationToken.None));
            if (!release.Wait(timeout))
            {
                logger.LogError("Access Roles is being uninstalled, but putting back the preference access it turned off didn't finish in {Seconds} s; the README's recovery does it with an API key", timeout.TotalSeconds);
                return null;
            }

            var results = release.Result;
            var failed = results.Count(r => r.Action == MirrorAction.Failed);
            if (failed > 0)
            {
                logger.LogError("Access Roles is being uninstalled: the preference access of {Failed} of {Count} users couldn't be put back; the README's recovery does it with an API key", failed, results.Count);
            }
            else if (results.Count > 0)
            {
                logger.LogWarning("Access Roles is being uninstalled: it put back the preference access of {Count} users", results.Count);
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Access Roles is being uninstalled, but putting back the preference access it turned off failed; the README's recovery does it with an API key");
            return null;
        }
    }
}
