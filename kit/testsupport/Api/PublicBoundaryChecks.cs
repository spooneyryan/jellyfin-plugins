using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// The public/internal boundary test (the kit's code rules for its own types, applied to the plugin's own types as
/// well). ASP.NET Core activates controllers, and Jellyfin creates tasks and the other parts it discovers, only
/// through public constructors, so what they take must be public; everything else stays internal, and records never
/// cross the boundary: the services map them to DTOs.
/// </summary>
/// <remarks>
/// <para>Every public type of the plugin's own namespaces (<see cref="PublicSurface"/>) must have one of these roles:
/// a <b>controller</b>; a <b>Jellyfin part</b> (a public concrete class Jellyfin 10.11.5 discovers and creates itself:
/// the plugin, its service registrator, scheduled and post-scan tasks, providers, resolvers; <see cref="DiscoveredInterfaces"/>);
/// the plugin's <b>configuration</b>; a <b>service interface</b> that a controller's or part's public constructor takes;
/// a <b>DTO</b> (a class or struct in <c>&lt;Root&gt;.Api.Models</c> (<see cref="ModelsNamespaceSuffix"/>) that Swashbuckle
/// reaches from a controller action or that is an action's parameter model, or an enum that is reached or carries the
/// schema prefix); or an <b>Integration facade</b> (<c>public static class
/// &lt;Root&gt;.Integration.&lt;Name&gt;V&lt;n&gt;</c>). Anything else public is reported: make it internal. A public
/// record or store that an action returns is reached like a DTO, and carries the prefix when its name starts like the
/// plugin's, so the namespace is what tells them apart: a reached class or struct outside <c>Api.Models</c> is reported
/// as no DTO.</para>
/// <para>Signatures (public, and protected in unsealed types, members declared by each of those types): nothing exposes a
/// type that isn't visible outside the assembly; service interfaces and DTOs use only DTOs and other assemblies' public
/// types (the BCL, ASP.NET Core, Jellyfin's <c>User</c>…), never a record, a store or a kit type; controllers and parts
/// take service interfaces and the kit's public interfaces in their constructors and use DTOs, the configuration and
/// other assemblies' types elsewhere; facades use BCL types only.</para>
/// <para>Code: a controller's methods (with their async state machines, iterators and closures) never touch an internal
/// type of the plugin: no record, store, mapper or service implementation, even through
/// <c>HttpContext.RequestServices</c>. The kit's internal helpers (<c>CurrentUser</c>, <c>PluginException</c>) are fine.
/// Parts may resolve internal services lazily from an <see cref="IServiceProvider"/>, so their code isn't
/// checked.</para>
/// <para>The kit, compiled into the plugin (<see cref="KitNamespace"/>), has public types only where a plugin's public
/// constructor needs them: interfaces and abstract bases.</para>
/// </remarks>
internal static partial class PublicBoundaryChecks
{
    /// <summary>The kit's root namespace; its types are compiled into every plugin assembly.</summary>
    public const string KitNamespace = "Jellyfin.Plugin.Kit";

    /// <summary>Where a plugin's DTO classes and structs live, under its root namespace (<c>Jellyfin.Plugin.Bookmarks.Api.Models</c>).</summary>
    public const string ModelsNamespaceSuffix = ".Api.Models";

    /// <summary>
    /// The Jellyfin 10.11.5 interfaces whose public concrete implementations the server discovers in plugin assemblies and
    /// creates itself (<c>ApplicationHost.GetExports</c>, <c>PluginManager</c>, <c>CoreAppHost</c>; types from
    /// <c>GetExportedTypes()</c>, so only public ones).
    /// </summary>
    public static readonly IReadOnlyList<Type> DiscoveredInterfaces =
    [
        typeof(IPlugin), typeof(IPluginServiceRegistrator), typeof(IScheduledTask), typeof(ILibraryPostScanTask),
        typeof(IConfigurationFactory), typeof(IResolverIgnoreRule), typeof(IItemResolver), typeof(IIntroProvider),
        typeof(IBaseItemComparer), typeof(IImageProvider), typeof(IMetadataService), typeof(IMetadataProvider),
        typeof(IMetadataSaver), typeof(IExternalId), typeof(IExternalUrlProvider), typeof(IMediaSourceProvider),
        typeof(ILyricProvider), typeof(ILyricParser),
    ];

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private enum Slot
    {
        Constructor,
        Member,
        Base,
        ServiceMember,
        DtoMember,
        FacadeMember,
    }

    /// <summary>
    /// Runs the check over the plugin assembly, whose own namespaces are under its assembly name
    /// (<c>Jellyfin.Plugin.Bookmarks</c>).
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <param name="schemaPrefix">The plugin's DTO prefix (<c>KitOptions.SchemaPrefix</c>); public enums carrying it are DTOs.</param>
    /// <returns>The problems; empty when the plugin passes.</returns>
    public static IReadOnlyList<string> Check(Assembly assembly, string schemaPrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Check(assembly, schemaPrefix, assembly.GetName().Name!);
    }

    /// <summary>
    /// Runs the check over the types of an assembly under a root namespace.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="schemaPrefix">The DTO prefix.</param>
    /// <param name="rootNamespace">The plugin's root namespace; types outside it are other code (the kit, the compiler's).</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> Check(Assembly assembly, string schemaPrefix, string rootNamespace)
    {
        var surface = Describe(assembly, schemaPrefix, rootNamespace);
        var problems = new List<string>();

        problems.AddRange(surface.Unclassified.Select(type => surface.ReachedOutsideModels.Contains(type)
            ? $"{type.FullName} is public and a controller action reaches it, but it is no DTO: DTOs live in "
                + $"{surface.RootNamespace}{ModelsNamespaceSuffix}. A record or a store stays internal and the services map it to a "
                + "DTO; a DTO moves there."
            : $"{type.FullName} is public, but it is none of the plugin's public surface (a controller, a part Jellyfin creates, "
                + "the configuration, a service interface a public constructor takes, a DTO, an Integration facade); make it internal."));

        problems.AddRange(surface.KitPublicTypes
            .Where(type => !type.IsInterface && !(type.IsAbstract && !type.IsSealed))
            .Select(type => $"The kit's {type.FullName} is public; kit types are internal, except interfaces a plugin's public "
                + "constructor needs and abstract bases."));

        foreach (var (type, role) in surface.Roles.OrderBy(pair => pair.Key.FullName, StringComparer.Ordinal))
        {
            foreach (var (description, slotType, slot) in Slots(type, role))
            {
                problems.AddRange(CheckSlot(surface, type, description, slotType, slot));
            }
        }

        foreach (var controller in surface.OfRole(PublicRole.Controller))
        {
            problems.AddRange(CheckControllerCode(surface, controller));
        }

        return problems;
    }

    /// <summary>
    /// Finds the public types of an assembly's own namespaces and their roles.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <param name="schemaPrefix">The DTO prefix.</param>
    /// <param name="rootNamespace">The plugin's root namespace.</param>
    /// <returns>The surface.</returns>
    public static PublicSurface Describe(Assembly assembly, string schemaPrefix, string rootNamespace)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(schemaPrefix);
        ArgumentException.ThrowIfNullOrEmpty(rootNamespace);

        var types = assembly.GetTypes();
        var publicOwn = types.Where(type => type.IsVisible && IsInNamespace(type, rootNamespace)).ToList();
        var roles = new Dictionary<Type, PublicRole>();

        var controllers = ControllerDiscovery.FindControllers(assembly).Where(type => IsInNamespace(type, rootNamespace)).ToList();
        foreach (var controller in controllers)
        {
            roles[controller] = PublicRole.Controller;
        }

        foreach (var type in publicOwn.Where(type => !roles.ContainsKey(type) && type is { IsClass: true, IsAbstract: false }))
        {
            if (DiscoveredInterfaces.Any(contract => contract.IsAssignableFrom(type)))
            {
                roles[type] = PublicRole.JellyfinPart;
            }
            else if (typeof(BasePluginConfiguration).IsAssignableFrom(type))
            {
                roles[type] = PublicRole.Configuration;
            }
        }

        foreach (var type in publicOwn.Where(type => !roles.ContainsKey(type) && type is { IsAbstract: true, IsSealed: true }
            && type.Namespace!.EndsWith(".Integration", StringComparison.Ordinal) && FacadeName().IsMatch(type.Name)))
        {
            roles[type] = PublicRole.Facade;
        }

        var takers = roles.Where(pair => pair.Value is PublicRole.Controller or PublicRole.JellyfinPart).Select(pair => pair.Key).ToList();
        foreach (var parameter in takers.SelectMany(type => type.GetConstructors()).SelectMany(constructor => constructor.GetParameters()))
        {
            foreach (var type in MethodBodyReferences.Expand(parameter.ParameterType))
            {
                if (type.IsInterface && type.IsVisible && IsInNamespace(type, rootNamespace) && !roles.ContainsKey(type))
                {
                    roles[type] = PublicRole.ServiceInterface;
                }
            }
        }

        // What an action reaches is a DTO only where DTOs live: a class or struct in <Root>.Api.Models, or an enum anywhere
        // (an enum holds no data, and the records share theirs with the DTOs). A record or a store left public and returned
        // by an action is reached too, and it carries the prefix when its name starts like the plugin's (Bookmarks'
        // Bookmark), so neither the walk nor the prefix can tell it from a DTO; its namespace does.
        var modelsNamespace = rootNamespace + ModelsNamespaceSuffix;
        var reached = SchemaPrefixChecks.FindReachedSchemas(controllers, assembly)
            .Select(schema => schema.Type.IsConstructedGenericType ? schema.Type.GetGenericTypeDefinition() : schema.Type)
            .Concat(controllers
                .SelectMany(ControllerDiscovery.FindActions)
                .SelectMany(action => action.GetParameters().Select(p => p.ParameterType).Append(action.ReturnType))
                .SelectMany(MethodBodyReferences.Expand))
            .Where(type => type.IsVisible && !type.IsInterface && type.Assembly == assembly && IsInNamespace(type, rootNamespace) && !roles.ContainsKey(type))
            .Distinct()
            .ToList();
        var dtos = reached
            .Where(type => type.IsEnum || IsInNamespace(type, modelsNamespace))
            .Concat(publicOwn.Where(type => type.IsEnum && type.Name.StartsWith(schemaPrefix, StringComparison.Ordinal)));
        foreach (var type in dtos.Where(type => !roles.ContainsKey(type)))
        {
            roles[type] = PublicRole.Dto;
        }

        return new PublicSurface(
            rootNamespace,
            assembly,
            roles,
            publicOwn.Where(type => !roles.ContainsKey(type)).OrderBy(type => type.FullName, StringComparer.Ordinal).ToList(),
            types.Where(type => type.IsVisible && IsKitType(type, rootNamespace)).OrderBy(type => type.FullName, StringComparer.Ordinal).ToList())
        {
            ReachedOutsideModels = reached.Where(type => !roles.ContainsKey(type)).OrderBy(type => type.FullName, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>
    /// Gets a value indicating whether a type's namespace is the root namespace or under it.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="rootNamespace">The root namespace.</param>
    /// <returns><c>true</c> when the type is in it.</returns>
    public static bool IsInNamespace(Type type, string rootNamespace)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Namespace is { } name
            && (string.Equals(name, rootNamespace, StringComparison.Ordinal) || name.StartsWith(rootNamespace + ".", StringComparison.Ordinal));
    }

    private static bool IsKitType(Type type, string rootNamespace)
        => IsInNamespace(type, KitNamespace)
            && !IsInNamespace(type, rootNamespace)
            && !IsInNamespace(type, KitNamespace + ".Tests")
            && !IsInNamespace(type, KitNamespace + ".TestSupport");

    private static IEnumerable<(string Description, Type Type, Slot Slot)> Slots(Type type, PublicRole role)
    {
        var memberSlot = role switch
        {
            PublicRole.ServiceInterface => Slot.ServiceMember,
            PublicRole.Dto => Slot.DtoMember,
            PublicRole.Facade => Slot.FacadeMember,
            _ => Slot.Member,
        };
        var exposesProtected = !type.IsSealed;

        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
        {
            yield return ($"{type.Name}'s base type", baseType, role is PublicRole.Dto ? Slot.DtoMember : Slot.Base);
        }

        foreach (var contract in type.GetInterfaces())
        {
            yield return ($"{type.Name}'s interface", contract, memberSlot == Slot.Member ? Slot.Base : memberSlot);
        }

        if (type.IsEnum)
        {
            yield break;
        }

        foreach (var constructor in type.GetConstructors(DeclaredMembers).Where(c => IsExposed(c, exposesProtected)))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return ($"{type.Name}'s constructor ({parameter.Name})", parameter.ParameterType, memberSlot == Slot.Member ? Slot.Constructor : memberSlot);
            }
        }

        foreach (var method in type.GetMethods(DeclaredMembers).Where(m => IsExposed(m, exposesProtected) && !m.IsSpecialName))
        {
            yield return ($"{type.Name}.{method.Name} (return type)", method.ReturnType, memberSlot);
            foreach (var parameter in method.GetParameters())
            {
                yield return ($"{type.Name}.{method.Name}({parameter.Name})", parameter.ParameterType, memberSlot);
            }
        }

        foreach (var property in type.GetProperties(DeclaredMembers)
            .Where(p => p.GetAccessors(nonPublic: true).Any(accessor => IsExposed(accessor, exposesProtected))))
        {
            yield return ($"{type.Name}.{property.Name}", property.PropertyType, memberSlot);
            foreach (var parameter in property.GetIndexParameters())
            {
                yield return ($"{type.Name}.{property.Name}[{parameter.Name}]", parameter.ParameterType, memberSlot);
            }
        }

        foreach (var field in type.GetFields(DeclaredMembers).Where(f => f.IsPublic || (exposesProtected && (f.IsFamily || f.IsFamilyOrAssembly))))
        {
            yield return ($"{type.Name}.{field.Name}", field.FieldType, memberSlot);
        }

        foreach (var @event in type.GetEvents(DeclaredMembers).Where(e => e.AddMethod is { } add && IsExposed(add, exposesProtected)))
        {
            yield return ($"{type.Name}.{@event.Name}", @event.EventHandlerType!, memberSlot);
        }
    }

    private static bool IsExposed(MethodBase method, bool exposesProtected)
        => method.IsPublic || (exposesProtected && (method.IsFamily || method.IsFamilyOrAssembly));

    private static IEnumerable<string> CheckSlot(PublicSurface surface, Type owner, string description, Type slotType, Slot slot)
    {
        foreach (var type in MethodBodyReferences.Expand(slotType).Distinct())
        {
            if (type == owner)
            {
                continue;
            }

            if (!type.IsVisible)
            {
                yield return $"{owner.FullName}: {description} exposes {type.FullName}, which isn't visible outside the assembly "
                    + "(only public types cross the boundary).";
                continue;
            }

            if (IsInNamespace(type, surface.RootNamespace) && type.Assembly == surface.Assembly)
            {
                var role = surface.Roles.TryGetValue(type, out var found) ? found : (PublicRole?)null;
                if (role is null || !Allowed(slot).Contains(role.Value))
                {
                    var what = role is { } known ? "a " + Describe(known) : "not part of the public surface";
                    yield return $"{owner.FullName}: {description} uses {type.FullName} ({what}); {Rule(slot)}.";
                }

                continue;
            }

            if (type.Assembly == surface.Assembly && IsKitType(type, surface.RootNamespace))
            {
                if (slot is not (Slot.Constructor or Slot.Base))
                {
                    yield return $"{owner.FullName}: {description} uses the kit's {type.FullName}; kit types stay behind the plugin's own "
                        + "service interfaces and DTOs, and only a controller's or part's constructor takes the kit's public interfaces.";
                }

                continue;
            }

            if (slot == Slot.FacadeMember && !IsBaseClassLibrary(type))
            {
                yield return $"{owner.FullName}: {description} uses {type.FullName} from {type.Assembly.GetName().Name}; "
                    + "Integration facades are called from other plugins' load contexts and use BCL types only.";
            }
        }
    }

    private static IEnumerable<string> CheckControllerCode(PublicSurface surface, Type controller)
    {
        var used = MethodBodyReferences.DeclaredMethods(controller)
            .SelectMany(method => MethodBodyReferences.FindTypes(method).Select(type => (Type: type, Where: method.DeclaringType!)))
            .Concat(MethodBodyReferences.DeclaredFields(controller)
                .SelectMany(field => MethodBodyReferences.Expand(field.FieldType).Select(type => (Type: type, Where: field.DeclaringType!))));

        return used
            .Where(use => use.Type.Assembly == surface.Assembly
                && IsInNamespace(use.Type, surface.RootNamespace)
                && !use.Type.IsVisible
                && !IsNestedIn(use.Type, controller))
            .Select(use => use.Type)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => $"{controller.FullName}'s code uses the plugin's internal {type.FullName}; controllers get DTOs from the "
                + "public service interfaces, whose implementations map records to DTOs.");
    }

    private static bool IsNestedIn(Type type, Type outer)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current == outer)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBaseClassLibrary(Type type)
    {
        var name = type.Assembly.GetName().Name ?? string.Empty;
        return type.Assembly == typeof(object).Assembly
            || string.Equals(name, "netstandard", StringComparison.Ordinal)
            || name.StartsWith("System.", StringComparison.Ordinal);
    }

    private static PublicRole[] Allowed(Slot slot) => slot switch
    {
        Slot.Constructor => [PublicRole.ServiceInterface],
        Slot.Base => [PublicRole.Configuration],
        Slot.Member => [PublicRole.Dto, PublicRole.Configuration],
        Slot.ServiceMember or Slot.DtoMember => [PublicRole.Dto],
        _ => [],
    };

    private static string Rule(Slot slot) => slot switch
    {
        Slot.Constructor => "a public constructor takes the plugin's service interfaces, never a concrete or data type",
        Slot.Base => "a public type derives only from other assemblies' types, the kit's public bases or the plugin's configuration",
        Slot.Member => "controllers and parts use DTOs and the configuration in their public members",
        Slot.ServiceMember => "service interfaces use only DTOs and other assemblies' public types, so records stay internal and the services map them",
        Slot.DtoMember => "DTOs hold only DTOs and other assemblies' public types",
        _ => "Integration facades use BCL types only",
    };

    private static string Describe(PublicRole role) => role switch
    {
        PublicRole.Controller => "controller",
        PublicRole.JellyfinPart => "part Jellyfin creates",
        PublicRole.Configuration => "configuration",
        PublicRole.ServiceInterface => "service interface",
        PublicRole.Dto => "DTO",
        _ => "facade",
    };

    [GeneratedRegex(@"V[0-9]+$")]
    private static partial Regex FacadeName();
}

/// <summary>
/// The role of a public type of a plugin (<see cref="PublicBoundaryChecks"/>).
/// </summary>
internal enum PublicRole
{
    /// <summary>An MVC controller.</summary>
    Controller,

    /// <summary>A public concrete class Jellyfin discovers and creates: the plugin, its registrator, a task, a provider.</summary>
    JellyfinPart,

    /// <summary>The plugin's <c>BasePluginConfiguration</c>.</summary>
    Configuration,

    /// <summary>An interface a controller's or part's public constructor takes.</summary>
    ServiceInterface,

    /// <summary>
    /// A class or struct in <c>&lt;Root&gt;.Api.Models</c> that Swashbuckle reaches from an action or that is an action's
    /// parameter model; or an enum that is reached or carries the schema prefix.
    /// </summary>
    Dto,

    /// <summary>A <c>public static class &lt;Root&gt;.Integration.&lt;Name&gt;V&lt;n&gt;</c>.</summary>
    Facade,
}

/// <summary>
/// The public types of a plugin's own namespaces, with their roles (<see cref="PublicBoundaryChecks.Describe"/>).
/// </summary>
/// <param name="RootNamespace">The plugin's root namespace.</param>
/// <param name="Assembly">The plugin assembly.</param>
/// <param name="Roles">Each public type with a role.</param>
/// <param name="Unclassified">Public types without a role, which the check reports.</param>
/// <param name="KitPublicTypes">The kit's public types compiled into the assembly.</param>
internal sealed record PublicSurface(
    string RootNamespace,
    Assembly Assembly,
    IReadOnlyDictionary<Type, PublicRole> Roles,
    IReadOnlyList<Type> Unclassified,
    IReadOnlyList<Type> KitPublicTypes)
{
    /// <summary>
    /// Gets the public classes and structs a controller action reaches outside <c>&lt;Root&gt;.Api.Models</c>: no DTOs, so
    /// among <see cref="Unclassified"/> (a record or a store left public, or a DTO in the wrong place).
    /// </summary>
    public IReadOnlyList<Type> ReachedOutsideModels { get; init; } = [];

    /// <summary>
    /// Lists the types with a role, by full name.
    /// </summary>
    /// <param name="role">The role.</param>
    /// <returns>The types.</returns>
    public IReadOnlyList<Type> OfRole(PublicRole role)
        => Roles.Where(pair => pair.Value == role).Select(pair => pair.Key).OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
}
