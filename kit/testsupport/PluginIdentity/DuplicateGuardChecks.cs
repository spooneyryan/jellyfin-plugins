using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;

/// <summary>
/// Checks a plugin's use of the kit's duplicate guard with a copy of its real assembly in a plugins folder: beside an
/// older copy without the guard, the registrator registers only the kit's status (standing down) and the removal of the
/// older copy, and the plugin class shows no pages, never touches the settings file and refuses to save; alone, it
/// registers as usual.
/// </summary>
public static class DuplicateGuardChecks
{
    /// <summary>
    /// Asserts that a copy of the plugin stands down beside an older copy installed under another folder name.
    /// </summary>
    /// <param name="pluginType">The plugin's <c>Plugin</c> class.</param>
    /// <param name="pluginId">The plugin id.</param>
    /// <param name="oldFolderName">The older copy's folder, for example <c>Bookmarks_0.9.0.0</c>.</param>
    /// <param name="newFolderName">This copy's folder, for example <c>Bookmarks And Clips_2.5.2.0</c>.</param>
    public static void StandsDownBesideAnOlderCopy(Type pluginType, Guid pluginId, string oldFolderName, string newFolderName)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        using var copies = new PluginCopies();
        var dll = Path.GetFileName(pluginType.Assembly.Location);
        copies.AddBuild(oldFolderName, dll, pluginId, new Version(0, 9, 0, 0));
        var copy = copies.LoadCopy(pluginType.Assembly, newFolderName, pluginId);

        var services = PluginCopies.Register(copy, JellyfinLine.ServerVersion);

        var (isActive, isDuplicate, message) = PluginCopies.ReadStatus(services);
        Assert.False(isActive);
        Assert.True(isDuplicate);
        Assert.Contains($"'{oldFolderName}' (0.9.0.0)", message, StringComparison.Ordinal);
        Assert.Contains($"'{newFolderName}'", message, StringComparison.Ordinal);
        Assert.Equal(2, services.Count);
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.True(PluginCopies.IsStandingDown(copy));

        // What Jellyfin creates without the registrations: the plugin class.
        var plugin = CreatePlugin(copy, pluginType.FullName!, copies.PluginsPath);
        Assert.Empty(((IHasWebPages)plugin).GetPages());
        var configuration = ((IHasPluginConfiguration)plugin).Configuration;
        Assert.NotNull(configuration);
        var save = plugin.GetType().GetMethod("SaveConfiguration", [configuration.GetType()])!;
        var refused = Assert.Throws<TargetInvocationException>(() => save.Invoke(plugin, [configuration]));
        Assert.IsType<InvalidOperationException>(refused.InnerException);
        Assert.Empty(Directory.EnumerateFiles(copies.PluginsPath, "*.xml", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Asserts that a copy of the plugin alone in its plugins folder registers as usual.
    /// </summary>
    /// <param name="pluginType">The plugin's <c>Plugin</c> class.</param>
    /// <param name="pluginId">The plugin id.</param>
    /// <param name="folderName">The copy's folder.</param>
    public static void RegistersAlone(Type pluginType, Guid pluginId, string folderName)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        using var copies = new PluginCopies();
        var copy = copies.LoadCopy(pluginType.Assembly, folderName, pluginId);

        var services = PluginCopies.Register(copy, JellyfinLine.ServerVersion);

        var (isActive, isDuplicate, _) = PluginCopies.ReadStatus(services);
        Assert.True(isActive);
        Assert.False(isDuplicate);
        Assert.True(services.Count > 2);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null && IsRemoval(d));
        Assert.False(PluginCopies.IsStandingDown(copy));
        Assert.NotEmpty(((IHasWebPages)CreatePlugin(copy, pluginType.FullName!, copies.PluginsPath)).GetPages());
    }

    // The removal is registered through a factory, so it shows as a factory descriptor whose lambda is the kit's.
    private static bool IsRemoval(Microsoft.Extensions.DependencyInjection.ServiceDescriptor descriptor)
        => descriptor.ImplementationFactory!.Method.DeclaringType?.FullName?.StartsWith("Jellyfin.Plugin.Kit.Hosting.KitAbiGuard", StringComparison.Ordinal) == true;

    private static object CreatePlugin(Assembly copy, string typeName, string pluginsPath)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(pluginsPath);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(pluginsPath, "configurations"));
        paths.SetupGet(p => p.DataPath).Returns(Path.Combine(pluginsPath, "..", "data"));
        var serializer = new Mock<IXmlSerializer>(MockBehavior.Strict);
        var type = copy.GetType(typeName, throwOnError: true)!;
        var constructor = type.GetConstructor([typeof(IApplicationPaths), typeof(IXmlSerializer)])!;
        return constructor.Invoke([paths.Object, serializer.Object]);
    }
}
