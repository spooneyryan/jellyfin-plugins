using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Every plugin uses the kit's ABI guard and API filters: its registrator registers nothing but the kit's KitAbiStatus on
/// a Jellyfin line it isn't built for, never throws, and every controller carries [KitAbiGate] and
/// [PluginExceptionFilter]. Every background service derives from the kit's KitBackgroundService, and every post-scan task
/// is a sealed KitPostScanTaskBase that flushes the kit's library-change monitor its registrator adds. A plugin's
/// scheduled maintenance is one public sealed KitMaintenanceTaskBase, inert on Jellyfin 12.1, whose registrator adds the
/// kit's maintenance, and a plugin with item-linked record owners registers the verify sweep; owners and steps are
/// registered only as the kit's KitComponent, which the kit creates one by one, and so are library-change consumers and
/// user data owners. Kit types are internal, so they are matched by full name.
/// </summary>
public sealed class KitAdoptionTests
{
    private const string AbiStatusType = "Jellyfin.Plugin.Kit.Hosting.KitAbiStatus";
    private const string KitBackgroundServiceType = "Jellyfin.Plugin.Kit.Hosting.KitBackgroundService";
    private const string KitPostScanTaskType = "Jellyfin.Plugin.Kit.Library.KitPostScanTaskBase";
    private const string MonitorType = "Jellyfin.Plugin.Kit.Library.LibraryChangeMonitor";
    private const string ConsumerType = "Jellyfin.Plugin.Kit.Library.ILibraryChangeConsumer";
    private const string UserDataOwnerType = "Jellyfin.Plugin.Kit.Users.IUserDataOwner";
    private const string ReconcilerType = "Jellyfin.Plugin.Kit.Users.UserReconciler";
    private const string KitMaintenanceTaskType = "Jellyfin.Plugin.Kit.Hosting.KitMaintenanceTaskBase";
    private const string MaintenanceType = "Jellyfin.Plugin.Kit.Hosting.KitMaintenance";
    private const string MaintenanceStepType = "Jellyfin.Plugin.Kit.Hosting.IKitMaintenanceStep";
    private const string VerifierType = "Jellyfin.Plugin.Kit.Library.AttachedRecordVerifier";
    private const string OwnerType = "Jellyfin.Plugin.Kit.Library.IAttachedRecordOwner";
    private const string DetachedOwnerType = "Jellyfin.Plugin.Kit.Library.IDetachedRecordOwner";
    private const string DetachedRecordsType = "Jellyfin.Plugin.Kit.Library.DetachedRecords";
    private const string ComponentType = "Jellyfin.Plugin.Kit.Hosting.KitComponent`1";

    private static readonly string RepositoryRoot = PluginIdentityChecks.FindRepositoryRoot();

    public static TheoryData<string> Plugins => new(PluginIdentityChecks.FindPluginFolders(RepositoryRoot));

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Registrator_OnTheOtherLine_RegistersOnlyTheAbiStatus(string plugin)
    {
        var assembly = LoadAssembly(plugin);
        foreach (var registrator in FindRegistrators(assembly))
        {
            var services = Register(registrator, JellyfinLine.OtherLineVersion);

            var descriptor = Assert.Single(services);
            Assert.Equal(AbiStatusType, descriptor.ServiceType.FullName);
            Assert.Same(assembly, descriptor.ServiceType.Assembly);
            Assert.False(IsActive(descriptor));
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Registrator_OnItsLine_IsActive(string plugin)
    {
        var registrators = FindRegistrators(LoadAssembly(plugin));

        Assert.NotEmpty(registrators);
        foreach (var registrator in registrators)
        {
            var services = Register(registrator, JellyfinLine.ServerVersion);

            Assert.True(IsActive(Assert.Single(services, d => d.ServiceType.FullName == AbiStatusType)));
            Assert.True(services.Count > 1);
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Registrator_OnItsLine_AddsItsWebUiThroughTheKitMiddleware(string plugin)
    {
        // The only startup filter is the kit's index.html injection, compiled into the plugin. A plugin with
        // no web client (no Web/js; Library Maintenance 1.0 has only its dashboard page) adds no startup filter.
        var assembly = LoadAssembly(plugin);
        var hasWebClient = assembly.GetManifestResourceNames().Any(n => n.StartsWith("Web.js.", StringComparison.Ordinal));
        foreach (var registrator in FindRegistrators(assembly))
        {
            var filters = Register(registrator, JellyfinLine.ServerVersion).Where(d => d.ServiceType == typeof(IStartupFilter)).ToList();
            if (!hasWebClient)
            {
                Assert.Empty(filters);
                continue;
            }

            var filter = Assert.Single(filters);
            Assert.Equal("Jellyfin.Plugin.Kit.Web.IndexHtmlInjectionStartupFilter", filter.ImplementationType?.FullName);
            Assert.Same(assembly, filter.ImplementationType!.Assembly);
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Registrator_OnItsLine_ServesItsScriptThroughTheKit(string plugin)
    {
        // The script the tag loads is the kit's EmbeddedScript of kit.js and the plugin's Web/js files.
        var assembly = LoadAssembly(plugin);
        foreach (var registrator in FindRegistrators(assembly))
        {
            var script = Assert.Single(Register(registrator, JellyfinLine.ServerVersion), d => d.ServiceType.FullName == "Jellyfin.Plugin.Kit.Web.IEmbeddedScript");

            Assert.Same(assembly, script.ServiceType.Assembly);
            Assert.Contains("Kit.kit.js", assembly.GetManifestResourceNames());
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void PageShells_AreTheKits_WithItsPluginPagesRegistration(string plugin)
    {
        // A plugin with user pages serves GET /<Route>/Pages/{key} through the kit's IKitPages, never its
        // own shells, and registers the kit's Plugin Pages registration service (which never writes Plugin Pages' config.json).
        var assembly = LoadAssembly(plugin);
        var pageControllers = ControllerDiscovery.FindControllers(assembly)
            .Where(c => c.GetConstructors().Any(k => k.GetParameters().Any(p => p.ParameterType.FullName == "Jellyfin.Plugin.Kit.Web.Pages.IKitPages")))
            .ToList();
        foreach (var registrator in FindRegistrators(assembly))
        {
            var services = Register(registrator, JellyfinLine.ServerVersion);
            var shells = services.Where(d => d.ServiceType.FullName == "Jellyfin.Plugin.Kit.Web.Pages.IKitPages").ToList();
            var registration = services.Where(d => d.ImplementationType?.FullName == "Jellyfin.Plugin.Kit.Web.Pages.PluginPagesRegistrationService").ToList();

            Assert.Equal(pageControllers.Count > 0, shells.Count == 1);
            Assert.Equal(pageControllers.Count > 0, registration.Count == 1);
            Assert.All(shells, d => Assert.Same(assembly, d.ServiceType.Assembly));
        }

        foreach (var controller in pageControllers)
        {
            var route = controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false).Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>().Single().Template;
            Assert.EndsWith("/Pages", route, StringComparison.Ordinal);
            Assert.NotEmpty(controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true));
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void WebHealth_GoesThroughTheKitsDiagnostics(string plugin)
    {
        // A plugin with a web client serves POST /<Route>/Diagnostics/ClientHealth (user) and
        // GET /<Route>/Admin/Diagnostics (RequiresElevation) from the kit's IKitDiagnostics, which its registrator adds, so
        // the ClientScript's health reports have somewhere to go and every field is validated by the kit.
        var assembly = LoadAssembly(plugin);
        if (!assembly.GetManifestResourceNames().Any(n => n.StartsWith("Web.js.", StringComparison.Ordinal)))
        {
            return;
        }

        var actions = ControllerDiscovery.FindControllers(assembly)
            .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                    .SelectMany(http => http.HttpMethods.Select(verb => (Controller: controller, Method: method, Verb: verb, Path: Combine(controller, http.Template))))))
            .ToList();
        var route = Assert.Single(actions, a => a.Verb == "GET" && a.Path.EndsWith("/ClientScript", StringComparison.Ordinal)).Path[..^"/ClientScript".Length];

        var health = Assert.Single(actions, a => a.Verb == "POST" && a.Path == route + "/Diagnostics/ClientHealth");
        var diagnostics = Assert.Single(actions, a => a.Verb == "GET" && a.Path == route + "/Admin/Diagnostics");

        foreach (var action in new[] { health, diagnostics })
        {
            Assert.Contains(action.Controller.GetConstructors().SelectMany(k => k.GetParameters()), p => p.ParameterType.FullName == "Jellyfin.Plugin.Kit.Diagnostics.IKitDiagnostics");
        }

        Assert.Contains(
            diagnostics.Controller.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(true).Concat(diagnostics.Method.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(true)),
            a => a.Policy == "RequiresElevation");
        Assert.NotEmpty(health.Method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute>());
        foreach (var registrator in FindRegistrators(assembly))
        {
            var registered = Assert.Single(Register(registrator, JellyfinLine.ServerVersion), d => d.ServiceType.FullName == "Jellyfin.Plugin.Kit.Diagnostics.IKitDiagnostics");
            Assert.Same(assembly, registered.ServiceType.Assembly);
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Plugins_DoNotUseFileTransformation(string plugin)
    {
        // Middleware only, no File Transformation fallback path; the kit keeps nothing of the Bookmarks bridge.
        var offenders = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "plugins", plugin), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("Jellyfin.Plugin.FileTransformation", StringComparison.Ordinal)
                    || text.Contains("RegisterTransformation", StringComparison.Ordinal)
                    || text.Contains("bb1813eb-f116-45bf-9cc0-e277011634b9", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Controllers_CarryTheAbiGateAndTheExceptionFilter(string plugin)
    {
        foreach (var controller in ControllerDiscovery.FindControllers(LoadAssembly(plugin)))
        {
            var attributes = controller.GetCustomAttributes(inherit: true).Select(a => a.GetType().FullName).ToList();

            Assert.Contains("Jellyfin.Plugin.Kit.Api.KitAbiGateAttribute", attributes);
            Assert.Contains("Jellyfin.Plugin.Kit.Api.PluginExceptionFilterAttribute", attributes);
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void BackgroundServices_DeriveFromTheKitsBackgroundService(string plugin)
    {
        // Jellyfin keeps .NET's StopHost, so an exception escaping a plugin's ExecuteAsync would stop
        // the server; the kit's base waits on the startup gate and catches everything.
        var assembly = LoadAssembly(plugin);
        var offenders = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(BackgroundService).IsAssignableFrom(t) && !DerivesFrom(t, KitBackgroundServiceType, assembly))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0, "Derive these from the kit's KitBackgroundService: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public async Task PostScanTasks_AreSealedKitPostScanTasks_WithTheMonitorRegistered_AndInertOnTheOtherLine(string plugin)
    {
        // Jellyfin creates each post-scan task once, with its root service provider, and runs it inside every
        // scan; the kit's base flushes the monitor as a full pass, and returns at once when the build is inert.
        var assembly = LoadAssembly(plugin);
        var tasks = assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILibraryPostScanTask).IsAssignableFrom(t)).ToList();
        foreach (var task in tasks)
        {
            Assert.True(task.IsPublic && task.IsSealed, task.FullName + " is public and sealed.");
            Assert.True(DerivesFrom(task, KitPostScanTaskType, assembly), task.FullName + " derives from the kit's KitPostScanTaskBase.");
            var constructor = Assert.Single(task.GetConstructors());
            Assert.Equal([typeof(IServiceProvider)], constructor.GetParameters().Select(p => p.ParameterType));

            var inert = Register(FindRegistrators(assembly).Single(), JellyfinLine.OtherLineVersion);
            await using var provider = inert.BuildServiceProvider();
            await ((ILibraryPostScanTask)constructor.Invoke([provider])).Run(new Progress<double>(), CancellationToken.None);
        }

        foreach (var registrator in FindRegistrators(assembly))
        {
            var services = Register(registrator, JellyfinLine.ServerVersion);
            var monitor = services.Count(d => d.ServiceType.FullName == MonitorType);
            var consumers = services.Count(d => IsComponentOf(d.ServiceType, ConsumerType));

            // The monitor creates each consumer on its own through its KitComponent; one registered as the interface
            // itself would never get a change.
            Assert.DoesNotContain(services, d => d.ServiceType.FullName == ConsumerType);

            // A monitor or a consumer without a post-scan task never gets the full pass; a post-scan task without the monitor
            // flushes nothing.
            Assert.Equal(tasks.Count > 0 ? 1 : 0, monitor);
            Assert.True(consumers == 0 || tasks.Count == 1, "A plugin with library-change consumers declares one post-scan task.");
            Assert.True(tasks.Count <= 1, "One post-scan task per plugin.");
        }
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public async Task MaintenanceTasks_AreOneSealedKitMaintenanceTask_WithTheMaintenanceRegistered_AndInertOnTheOtherLine(string plugin)
    {
        // One daily <Plugin>: Maintenance per plugin, a public sealed class on the kit's base, which Jellyfin
        // creates through GetExports with its service provider and which returns at once for an inert build.
        var assembly = LoadAssembly(plugin);
        var tasks = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(MediaBrowser.Model.Tasks.IScheduledTask).IsAssignableFrom(t) && DerivesFrom(t, KitMaintenanceTaskType, assembly))
            .ToList();
        Assert.True(tasks.Count <= 1, "One maintenance task per plugin.");
        foreach (var task in tasks)
        {
            Assert.True(task.IsPublic && task.IsSealed, task.FullName + " is public and sealed.");
            var constructor = Assert.Single(task.GetConstructors());
            Assert.Equal([typeof(IServiceProvider)], constructor.GetParameters().Select(p => p.ParameterType));

            var inert = Register(FindRegistrators(assembly).Single(), JellyfinLine.OtherLineVersion);
            await using var provider = inert.BuildServiceProvider();
            var instance = (MediaBrowser.Model.Tasks.IScheduledTask)constructor.Invoke([provider]);
            await instance.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            Assert.EndsWith(": Maintenance", instance.Name, StringComparison.Ordinal);
            Assert.Single(instance.GetDefaultTriggers());
        }

        foreach (var registrator in FindRegistrators(assembly))
        {
            var services = Register(registrator, JellyfinLine.ServerVersion);
            var maintenance = services.Count(d => d.ServiceType.FullName == MaintenanceType);
            var steps = services.Count(d => IsComponentOf(d.ServiceType, MaintenanceStepType));

            // The kit creates owners and steps one by one through its KitComponent registrations, so one
            // that can't be created costs only itself; one registered as the interface itself would never be seen.
            Assert.DoesNotContain(services, d => d.ServiceType.FullName is MaintenanceStepType or OwnerType or DetachedOwnerType);

            // The same for the owners of user data: the reconciler and the deleted-user consumer see only components.
            Assert.DoesNotContain(services, d => d.ServiceType.FullName == UserDataOwnerType);
            var userDataOwners = services.Count(d => IsComponentOf(d.ServiceType, UserDataOwnerType));
            Assert.True(userDataOwners == 0 || services.Any(d => d.ServiceType.FullName == ReconcilerType), "A plugin with IUserDataOwners registers AddKitUsers.");

            // Steps without the maintenance, or the maintenance without the task, never run.
            Assert.Equal(tasks.Count, maintenance);
            Assert.True(steps == 0 || tasks.Count == 1, "A plugin with maintenance steps declares the maintenance task.");

            // Item-linked records are checked by the verify sweep; detached ones need the kit's detached records.
            var owners = services.Count(d => IsComponentOf(d.ServiceType, OwnerType));
            var detachedOwners = services.Count(d => IsComponentOf(d.ServiceType, DetachedOwnerType));
            Assert.True(owners == 0 || services.Any(d => d.ServiceType.FullName == VerifierType), "A plugin with IAttachedRecordOwners registers AddKitVerifier.");
            Assert.True(detachedOwners == 0 || services.Any(d => d.ServiceType.FullName == DetachedRecordsType), "A plugin with IDetachedRecordOwners registers AddKitDetachedRecords.");
            Assert.True(owners + detachedOwners == 0 || tasks.Count == 1, "A plugin with item-linked records declares the maintenance task (the daily sweep).");
        }
    }

    private static bool IsComponentOf(Type serviceType, string extensionPoint)
        => serviceType.IsGenericType
            && serviceType.GetGenericTypeDefinition().FullName == ComponentType
            && serviceType.GetGenericArguments()[0].FullName == extensionPoint;

    private static bool DerivesFrom(Type type, string baseFullName, Assembly assembly)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName == baseFullName && current.Assembly == assembly)
            {
                return true;
            }
        }

        return false;
    }

    private static ServiceCollection Register(Type registrator, string version)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        var services = new ServiceCollection();
        ((IPluginServiceRegistrator)Activator.CreateInstance(registrator)!).RegisterServices(services, host.Object);
        return services;
    }

    private static string Combine(Type controller, string? template)
    {
        var prefix = controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false).Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>().SingleOrDefault()?.Template ?? string.Empty;
        return string.IsNullOrEmpty(template) ? prefix : prefix + "/" + template;
    }

    private static bool IsActive(ServiceDescriptor descriptor)
        => (bool)descriptor.ServiceType.GetProperty("IsActive")!.GetValue(descriptor.ImplementationInstance)!;

    private static Type[] FindRegistrators(Assembly assembly)
        => assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPluginServiceRegistrator).IsAssignableFrom(t)).ToArray();

    private static Assembly LoadAssembly(string plugin)
    {
        var yaml = BuildYaml.Load(Path.Combine(RepositoryRoot, "plugins", plugin, JellyfinLine.BuildYamlName));
        var dll = yaml.Artifacts.First(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        return Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(dll)));
    }
}
