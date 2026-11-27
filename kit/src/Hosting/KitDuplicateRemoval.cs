using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The highest copy's half of <see cref="KitDuplicateGuard"/>: once the server has started, it turns off every lower copy
/// that Jellyfin loaded at this start and tells the server a restart is pending, and removes every lower copy that wasn't
/// loaded. Registered only when there is a lower copy, also when this copy stands down. Never throws.
/// </summary>
/// <remarks>
/// <para>A loaded copy is turned off with <c>IPluginManager.DisablePlugin</c>, which writes <c>Disabled</c> to its
/// <c>meta.json</c>: Jellyfin can't delete a loaded DLL on Windows, and the <c>Deleted</c> or <c>Superseded</c> mark
/// <c>RemovePlugin</c> or an install would leave is set back to <c>Active</c> by the catalog refresh
/// (<c>InstallationManager.GetAvailablePackages</c> rewrites the manifest of every installed version it lists, keeping
/// only <c>Disabled</c>). At the next start the copy isn't loaded, and <c>RemovePlugin</c> deletes its folder, unless its DLL
/// is still in use: a restart from the Dashboard runs in the same process, and a DLL loaded before it stays locked on
/// Windows. Such a copy stays <c>Disabled</c> and is removed at a later start. The plugin's own uninstall hook never runs, and the settings in <c>plugins/configurations</c> and the data folder are never touched;
/// both copies share them.</para>
/// <para>Before acting, it checks again that the folder is still a copy of this plugin: beside this copy's folder, with
/// the plugin's DLL, no <c>meta.json</c> naming another plugin, and listed by Jellyfin with this plugin's id (or, for a
/// folder still without <c>meta.json</c> that Jellyfin created no plugin from, the id Jellyfin made up for it, matched by
/// folder). Each copy is handled on its own: one that fails is logged and the others are still handled.</para>
/// </remarks>
internal sealed class KitDuplicateRemoval : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IServerApplicationHost _applicationHost;
    private readonly KitOptions _options;
    private readonly KitPluginCopy _own;
    private readonly string _assemblyFileName;
    private readonly IReadOnlyList<KitPluginCopy> _copies;
    private int _done;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitDuplicateRemoval"/> class.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="applicationHost">The server, told about the pending restart.</param>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="own">This copy.</param>
    /// <param name="assemblyFileName">The plugin's assembly file name.</param>
    /// <param name="copies">The lower copies to remove.</param>
    public KitDuplicateRemoval(IServiceProvider services, IServerApplicationHost applicationHost, KitOptions options, KitPluginCopy own, string assemblyFileName, IReadOnlyList<KitPluginCopy> copies)
    {
        _services = services;
        _applicationHost = applicationHost;
        _options = options;
        _own = own;
        _assemblyFileName = assemblyFileName;
        _copies = copies;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_services.GetService<IHostApplicationLifetime>() is { } lifetime)
            {
                lifetime.ApplicationStarted.Register(Run);
            }
            else
            {
                Run();
            }
        }
        catch (Exception ex)
        {
            // Never stop the host over the clean-up: at worst the older copy stays until it is uninstalled by hand.
            CreateLogger().LogWarning(ex, "Couldn't schedule the removal of the older copies of {Plugin}", _options.PluginName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Turns off or removes the lower copies; runs once. Internal for tests.
    /// </summary>
    internal void Run()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1)
        {
            return;
        }

        var logger = CreateLogger();
        try
        {
            var manager = _services.GetService<IPluginManager>();
            if (manager is null)
            {
                logger.LogWarning("Couldn't remove the older copies of {Plugin}: the server has no plugin manager", _options.PluginName);
                return;
            }

            var configurations = _services.GetService<IApplicationPaths>()?.PluginConfigurationsPath;
            var plugins = manager.Plugins.ToList();
            var turnedOff = 0;
            foreach (var copy in _copies)
            {
                // One copy that can't be handled doesn't stop the others.
                try
                {
                    turnedOff += Handle(manager, plugins, configurations, copy, logger);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Couldn't remove the older copy of {Plugin} in {Folder}; uninstall it on the Dashboard's Plugins page", _options.PluginName, copy.FolderName);
                }
            }

            // Only a copy that was loaded needs the restart; one removed while not loaded never ran.
            if (turnedOff > 0)
            {
                _applicationHost.NotifyPendingRestart();
            }
        }
        catch (Exception ex)
        {
            // Never throws: a failed clean-up leaves the older copy, which the next start finds again.
            logger.LogWarning(ex, "Couldn't remove the older copies of {Plugin}", _options.PluginName);
        }
    }

    // Returns how many loaded copies were turned off (each needs the restart).
    private int Handle(IPluginManager manager, IReadOnlyList<LocalPlugin> plugins, string? configurations, KitPluginCopy copy, ILogger logger)
    {
        if (!IsStillACopy(copy.Folder, configurations))
        {
            logger.LogWarning("Left the folder {Folder} alone: it is no longer a copy of {Plugin}", copy.FolderName, _options.PluginName);
            return 0;
        }

        var listed = plugins.Where(p => p.Path is { Length: > 0 } path && KitDuplicateGuard.IsSameFolder(path, copy.Folder)).ToList();
        var entries = listed.Where(IsThisPlugin).ToList();
        if (entries.Count == 0)
        {
            if (listed.Count > 0)
            {
                logger.LogWarning("Left the folder {Folder} alone: Jellyfin lists it as another plugin than {Plugin}", copy.FolderName, _options.PluginName);
            }
            else if (!copy.HasManifest && KitDuplicateGuard.ReadStatus(copy.Folder) is null)
            {
                KeepOff(copy, logger);
            }
            else
            {
                logger.LogWarning("Couldn't remove the older copy of {Plugin} in {Folder}: the server doesn't list it", _options.PluginName, copy.FolderName);
            }

            return 0;
        }

        var turnedOff = 0;
        foreach (var entry in entries)
        {
            if (copy.IsLoaded ? TurnOff(manager, copy, entry, logger) : Remove(manager, copy, entry, logger))
            {
                turnedOff++;
            }
        }

        return turnedOff;

        // Jellyfin's entry carries this plugin's id once it has created the plugin from the folder, also for a folder
        // without meta.json. Only an entry it never created a plugin for, in a folder still without a readable meta.json, can
        // be matched by the folder alone: its id is one Jellyfin made up from the folder name.
        bool IsThisPlugin(LocalPlugin p)
            => p.Id == _options.PluginId
                || (p.Instance is null && !copy.HasManifest && KitDuplicateGuard.ReadStatus(copy.Folder) is null);
    }

    // A loaded copy is turned off rather than removed: Jellyfin can't delete a loaded DLL on Windows, and the Deleted or
    // Superseded mark it would leave is set back to Active by its catalog refresh; Disabled is kept. The next start finds
    // the copy turned off, not loaded, and removes it. Returns whether the copy was turned off (a restart is then needed).
    private bool TurnOff(IPluginManager manager, KitPluginCopy copy, LocalPlugin entry, ILogger logger)
    {
        if (!Disable(manager, copy, entry))
        {
            logger.LogWarning("Jellyfin couldn't turn off the older copy of {Plugin} in {Folder}; uninstall it on the Dashboard's Plugins page", _options.PluginName, copy.FolderName);
            return false;
        }

        logger.LogInformation(
            "Turned off the older copy of {Plugin} in {Folder} ({OldVersion}); this copy is {Version} in {OwnFolder}. The older copy is removed at a later start. Restart Jellyfin to finish the update",
            _options.PluginName,
            copy.FolderName,
            copy.Version,
            _own.Version,
            _own.FolderName);
        return true;
    }

    // A copy that isn't loaded is deleted. Its DLL can still be in use: Jellyfin's restart from the Dashboard runs in the
    // same process, and a DLL loaded before it stays locked. Jellyfin would then only mark the copy Deleted, which its
    // catalog refresh sets back to Active, so a locked copy is kept turned off instead and removed at a later start.
    private bool Remove(IPluginManager manager, KitPluginCopy copy, LocalPlugin entry, ILogger logger)
    {
        if (!IsInUse(Path.Combine(copy.Folder, _assemblyFileName)) && manager.RemovePlugin(entry) && !Directory.Exists(copy.Folder))
        {
            logger.LogInformation(
                "Removed the older copy of {Plugin} in {Folder} ({OldVersion}), which wasn't loaded; this copy is {Version} in {OwnFolder}",
                _options.PluginName,
                copy.FolderName,
                copy.Version,
                _own.Version,
                _own.FolderName);
            return false;
        }

        if (Disable(manager, copy, entry))
        {
            logger.LogInformation(
                "The older copy of {Plugin} in {Folder} ({OldVersion}) isn't loaded but its files are still in use, so it stays turned off and is removed at a later start",
                _options.PluginName,
                copy.FolderName,
                copy.Version);
        }
        else
        {
            logger.LogWarning("Jellyfin couldn't remove or turn off the older copy of {Plugin} in {Folder}; uninstall it on the Dashboard's Plugins page", _options.PluginName, copy.FolderName);
        }

        return false;
    }

    // Checked on disk: Jellyfin sets the status in memory even when it can't save the file. Once Disabled is saved, the
    // entry's status is set to Disabled again: Jellyfin 10.11's DisablePlugin then sets it to Restart in memory
    // (PluginManager.ProcessAlternative), and its catalog refresh writes the in-memory status back as Active unless it is
    // Disabled. Jellyfin 12 leaves the status Disabled and keeps the restart in RestartRequired.
    private static bool Disable(IPluginManager manager, KitPluginCopy copy, LocalPlugin entry)
    {
        if (KitDuplicateGuard.ReadStatus(copy.Folder) != PluginStatus.Disabled)
        {
            manager.DisablePlugin(entry);
        }

        if (KitDuplicateGuard.ReadStatus(copy.Folder) != PluginStatus.Disabled)
        {
            return false;
        }

        entry.Manifest.Status = PluginStatus.Disabled;
        return true;
    }

    // Windows can't delete a DLL that a process has loaded, and won't open it for writing either; elsewhere it can.
    private static bool IsInUse(string dll)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(dll))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // A folder Jellyfin dropped after deleting only part of it (a restart from the Dashboard leaves the DLL locked, and
    // Jellyfin's clean-up of older versions deletes the rest, meta.json included). Without a meta.json Jellyfin makes one
    // up at the next start, Active, under an id derived from the folder name, and that id can sort it above the real
    // newest version, which Jellyfin then deletes instead (PluginManager.DiscoverPlugins). A meta.json with this plugin's
    // id and Disabled keeps it below the newest version, and turned off, until a later start can delete it.
    private void KeepOff(KitPluginCopy copy, ILogger logger)
    {
        var dll = Path.Combine(copy.Folder, _assemblyFileName);
        if (IsInUse(dll) || !TryDelete(copy.Folder))
        {
            if (!File.Exists(dll))
            {
                // Deleted in part: without the DLL Jellyfin loads nothing from the folder, and a meta.json would list a missing file.
                logger.LogInformation("Removed most of what was left of the older copy of {Plugin} in {Folder} ({OldVersion}); Jellyfin no longer loads it", _options.PluginName, copy.FolderName, copy.Version);
                return;
            }

            WriteDisabledManifest(copy);
            logger.LogInformation(
                "The older copy of {Plugin} in {Folder} ({OldVersion}) lost its meta.json in a removal Jellyfin couldn't finish; it has been given one that keeps it turned off, and it is removed at a later start",
                _options.PluginName,
                copy.FolderName,
                copy.Version);
            return;
        }

        logger.LogInformation("Removed what was left of the older copy of {Plugin} in {Folder} ({OldVersion})", _options.PluginName, copy.FolderName, copy.Version);
    }

    private void WriteDisabledManifest(KitPluginCopy copy)
    {
        // The name Jellyfin gives a folder without meta.json, so its clean-up of older versions still matches it.
        var name = copy.FolderName;
        var underscore = name.LastIndexOf('_');
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        using var stream = new FileStream(Path.Combine(copy.Folder, KitDuplicateGuard.ManifestFileName), FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("guid", _options.PluginId.ToString());
        writer.WriteString("name", name);
        writer.WriteString("version", copy.Version.ToString());
        writer.WriteString("targetAbi", "0.0.0.1");
        writer.WriteString("status", nameof(PluginStatus.Disabled));
        writer.WriteBoolean("autoUpdate", false);
        writer.WriteStartArray("assemblies");
        writer.WriteStringValue(_assemblyFileName);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(folder);
        }
    }

    private bool IsStillACopy(string folder, string? configurations)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        var ownParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_own.Folder));
        return parent is not null
            && ownParent is not null
            && KitDuplicateGuard.IsSameFolder(parent, ownParent)
            && !KitDuplicateGuard.IsSameFolder(folder, _own.Folder)
            && (configurations is null || !KitDuplicateGuard.IsSameFolder(folder, configurations))
            && File.Exists(Path.Combine(folder, _assemblyFileName))
            && (KitDuplicateGuard.ReadId(folder) is not { } id || id == _options.PluginId);
    }

    private ILogger CreateLogger()
    {
        try
        {
            return _services.GetService<ILoggerFactory>() is { } factory
                ? _options.CreateLogger(factory, KitDuplicateGuard.LoggerComponent)
                : NullLogger.Instance;
        }
        catch (Exception)
        {
            // Logging must not be the reason the clean-up fails.
            return NullLogger.Instance;
        }
    }
}
