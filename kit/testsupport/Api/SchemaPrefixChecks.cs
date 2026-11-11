using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// The schema-prefix test. Swashbuckle 6.2.3, as shipped with Jellyfin 10.11.5, uses a
/// type's short name as its schema id, and one duplicate id anywhere on the server makes <c>/api-docs/openapi.json</c>
/// return 500 for everyone. So every type of the plugin that Swashbuckle can reach from a controller action must start
/// with the plugin's DTO prefix, and no reached id may already be used by Jellyfin or an installed plugin.
/// </summary>
/// <remarks>
/// <para>What is reached: action parameters (request bodies included) and return types, the types named in
/// <c>[ProducesResponseType]</c> and <c>[Produces]</c> on the action or controller whatever their accessibility, the
/// public properties of every reached type of the plugin, generic arguments, array elements, nested types and enums.
/// <c>[FromQuery]</c>, <c>[FromRoute]</c>, <c>[FromHeader]</c> and <c>[FromForm]</c> models are expanded into their
/// properties (Swashbuckle documents them as parameters or an inline form); <c>[FromServices]</c> parameters and
/// <c>[ApiExplorerSettings(IgnoreApi = true)]</c> actions are skipped, as Swashbuckle skips them.</para>
/// <para>The walk stops at types of other assemblies (Jellyfin, ASP.NET Core, the BCL): they are the same Type objects
/// core uses and share its schemas. Their generic arguments are still walked, and a server generic of ours
/// (<c>QueryResult&lt;BookmarkDto&gt;</c> is <c>BookmarkDtoQueryResult</c>) is checked against the foreign list only.</para>
/// <para>A nested type's schema id is its <b>own</b> name (<c>Outer+Part</c> is <c>Part</c>), so it needs the prefix itself.</para>
/// </remarks>
internal static class SchemaPrefixChecks
{
    /// <summary>The pinned foreign-id list in <c>kit/testdata</c>.</summary>
    public const string ForeignIdsFile = "openapi-schema-ids.txt";

    private static readonly Lazy<IReadOnlySet<string>> _foreignSchemaIds =
        new(() => ControllerDiscovery.ReadTestDataList(ForeignIdsFile).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// Gets the schema ids already published by Jellyfin 10.11.5 and a reference set of community plugins
    /// (<c>kit/testdata/openapi-schema-ids.txt</c>).
    /// </summary>
    public static IReadOnlySet<string> ForeignSchemaIds => _foreignSchemaIds.Value;

    /// <summary>
    /// Runs the check over every controller of the plugin assembly against the pinned foreign-id list.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <param name="schemaPrefix">The plugin's DTO prefix (<c>KitOptions.SchemaPrefix</c>).</param>
    /// <param name="knownUnprefixed">Schema ids of the plugin allowed to miss the prefix for now, each with a TODO naming
    /// the task that renames it. A listed id that is no longer reached unprefixed is reported, so the list only shrinks.</param>
    /// <returns>The problems; empty when the plugin passes.</returns>
    public static IReadOnlyList<string> Check(Assembly assembly, string schemaPrefix, IEnumerable<string>? knownUnprefixed = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Check(ControllerDiscovery.FindControllers(assembly), assembly, schemaPrefix, ForeignSchemaIds, knownUnprefixed);
    }

    /// <summary>
    /// Runs the check over the given controllers.
    /// </summary>
    /// <param name="controllers">The controller types.</param>
    /// <param name="ownAssembly">The plugin assembly; only its types need the prefix.</param>
    /// <param name="schemaPrefix">The plugin's DTO prefix.</param>
    /// <param name="foreignIds">Schema ids that must never be reached.</param>
    /// <param name="knownUnprefixed">Schema ids allowed to miss the prefix for now.</param>
    /// <returns>The problems; empty when the controllers pass.</returns>
    public static IReadOnlyList<string> Check(
        IEnumerable<Type> controllers,
        Assembly ownAssembly,
        string schemaPrefix,
        IReadOnlySet<string> foreignIds,
        IEnumerable<string>? knownUnprefixed = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaPrefix);
        ArgumentNullException.ThrowIfNull(foreignIds);

        var exempt = (knownUnprefixed ?? []).ToHashSet(StringComparer.Ordinal);
        var reached = FindReachedSchemas(controllers, ownAssembly);
        var problems = new List<string>();

        foreach (var schema in reached)
        {
            if (schema.IsOwnType && !schema.PrefixedName.StartsWith(schemaPrefix, StringComparison.Ordinal) && !exempt.Contains(schema.SchemaId))
            {
                var nested = schema.Type.IsNested
                    ? $" It is nested in {schema.Type.DeclaringType!.Name}, but a nested type's schema id is its own name."
                    : string.Empty;
                problems.Add(
                    $"Schema '{schema.SchemaId}' ({schema.Type.FullName}), reached from {schema.Path}, doesn't start with '{schemaPrefix}'. "
                    + $"Every schema-visible type of the plugin carries its DTO prefix.{nested}");
            }

            if (foreignIds.Contains(schema.SchemaId))
            {
                problems.Add(
                    $"Schema '{schema.SchemaId}' ({schema.Type.FullName}), reached from {schema.Path}, is already published by Jellyfin or an "
                    + $"installed plugin ({ForeignIdsFile}); a second type with that id breaks /api-docs/openapi.json for the whole server.");
            }
        }

        problems.AddRange(reached
            .GroupBy(schema => schema.SchemaId, StringComparer.Ordinal)
            .Where(group => group.Select(schema => schema.Type).Distinct().Count() > 1)
            .Select(group => $"Schema id '{group.Key}' belongs to several types ({string.Join(", ", group.Select(s => s.Type.FullName).Distinct())}); "
                + "Swashbuckle refuses the second one."));

        var unprefixed = reached
            .Where(schema => schema.IsOwnType && !schema.PrefixedName.StartsWith(schemaPrefix, StringComparison.Ordinal))
            .Select(schema => schema.SchemaId)
            .ToHashSet(StringComparer.Ordinal);
        problems.AddRange(exempt
            .Where(id => !unprefixed.Contains(id))
            .Order(StringComparer.Ordinal)
            .Select(id => $"'{id}' is listed as a known unprefixed schema id, but no unprefixed type of the plugin has that id any more; "
                + "remove it from the list."));

        return problems;
    }

    /// <summary>
    /// Lists every schema Swashbuckle creates for the controllers' own types, and every server generic built from them.
    /// </summary>
    /// <param name="controllers">The controller types.</param>
    /// <param name="ownAssembly">The plugin assembly.</param>
    /// <returns>The schemas in the order they were reached, one per type.</returns>
    public static IReadOnlyList<ReachedSchema> FindReachedSchemas(IEnumerable<Type> controllers, Assembly ownAssembly)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentNullException.ThrowIfNull(ownAssembly);

        var walker = new Walker(ownAssembly);
        foreach (var controller in controllers.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (IsHiddenFromApi(controller))
            {
                continue;
            }

            var controllerResponseTypes = GetResponseTypes(controller);
            foreach (var action in ControllerDiscovery.FindActions(controller))
            {
                if (IsHiddenFromApi(action))
                {
                    continue;
                }

                var name = ControllerDiscovery.Describe(controller, action);
                foreach (var parameter in action.GetParameters())
                {
                    walker.VisitParameter(parameter, $"{name}({parameter.Name})");
                }

                walker.Visit(action.ReturnType, $"{name} (return type)");
                foreach (var type in GetResponseTypes(action).Concat(controllerResponseTypes))
                {
                    walker.Visit(type, $"{name} ([ProducesResponseType]/[Produces])");
                }
            }
        }

        return walker.Reached;
    }

    /// <summary>
    /// Gets the schema id Swashbuckle 6.2.3 gives a type (<c>SchemaGeneratorOptions.DefaultSchemaIdSelector</c>):
    /// the short name, and for a constructed generic the argument ids followed by the definition's name.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The schema id.</returns>
    public static string GetSchemaId(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsConstructedGenericType)
        {
            return type.Name.Replace("[]", "Array", StringComparison.Ordinal);
        }

        var prefix = string.Concat(type.GetGenericArguments().Select(GetSchemaId));
        return prefix + type.Name.Split('`')[0];
    }

    private static bool IsHiddenFromApi(MemberInfo member)
        => member.GetCustomAttributes(inherit: true).OfType<IApiDescriptionVisibilityProvider>().Any(provider => provider.IgnoreApi);

    // [ProducesResponseType(typeof(T), ...)], [ProducesResponseType<T>], [Produces(typeof(T))]: every IApiResponseMetadataProvider's Type.
    private static IEnumerable<Type> GetResponseTypes(MemberInfo member)
        => member.GetCustomAttributes(inherit: true)
            .OfType<IApiResponseMetadataProvider>()
            .Select(provider => provider.GetType().GetProperty("Type", typeof(Type))?.GetValue(provider) as Type)
            .OfType<Type>()
            .Where(type => type != typeof(void));

    /// <summary>
    /// A schema the walk reached.
    /// </summary>
    /// <param name="Type">The type.</param>
    /// <param name="SchemaId">Its Swashbuckle schema id.</param>
    /// <param name="PrefixedName">The name that must carry the prefix: the type's own name (without generic arity).</param>
    /// <param name="IsOwnType">Whether the type (or, for a generic, its definition) is the plugin's.</param>
    /// <param name="Path">How it was reached, for messages.</param>
    internal sealed record ReachedSchema(Type Type, string SchemaId, string PrefixedName, bool IsOwnType, string Path);

    private sealed class Walker
    {
        private static readonly HashSet<Type> _wrappers =
        [
            typeof(Task<>), typeof(ValueTask<>), typeof(ActionResult<>), typeof(Nullable<>), typeof(IAsyncEnumerable<>)
        ];

        private readonly Assembly _own;
        private readonly HashSet<Type> _visited = [];
        private readonly List<ReachedSchema> _reached = [];

        public Walker(Assembly own)
        {
            _own = own;
        }

        public IReadOnlyList<ReachedSchema> Reached => _reached;

        public void VisitParameter(ParameterInfo parameter, string path)
        {
            var attributes = parameter.GetCustomAttributes(inherit: true);
            if (attributes.OfType<IFromServiceMetadata>().Any()
                || attributes.Any(a => a.GetType().Name == "FromKeyedServicesAttribute"))
            {
                return;
            }

            var source = attributes.OfType<IBindingSourceMetadata>().Select(m => m.BindingSource).FirstOrDefault(s => s is not null);
            if (source == BindingSource.Services || source == BindingSource.Special)
            {
                return;
            }

            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            var expanded = source == BindingSource.Query || source == BindingSource.Path || source == BindingSource.Header
                || source == BindingSource.Form || source == BindingSource.FormFile;
            if (expanded && type.Assembly == _own && !type.IsEnum && (type.IsClass || type.IsValueType))
            {
                // Documented as separate parameters or an inline form, not as a schema of the model itself.
                WalkMembers(type, path);
                return;
            }

            Visit(parameter.ParameterType, path);
        }

        public void Visit(Type type, string path)
        {
            while (type.HasElementType)
            {
                type = type.GetElementType()!;
            }

            if (type.IsGenericParameter || !_visited.Add(type))
            {
                return;
            }

            if (type.IsConstructedGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition.Assembly == _own)
                {
                    Record(type, definition.Name.Split('`')[0], isOwn: true, path);
                    WalkMembers(type, path);
                }
                else if (!_wrappers.Contains(definition) && !typeof(IEnumerable).IsAssignableFrom(type) && InvolvesOwn(type))
                {
                    Record(type, GetSchemaId(type), isOwn: false, path);
                }

                foreach (var argument in type.GetGenericArguments())
                {
                    Visit(argument, path);
                }

                return;
            }

            if (type.Assembly != _own)
            {
                return;
            }

            Record(type, type.Name, isOwn: true, path);
            if (!type.IsEnum)
            {
                WalkMembers(type, path);
            }
        }

        private void WalkMembers(Type type, string path)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var property in type.GetProperties(All))
            {
                var included = property.IsDefined(typeof(JsonIncludeAttribute), inherit: true);
                if (property.GetIndexParameters().Length > 0
                    || (property.GetMethod?.IsPublic != true && !included)
                    || IsAlwaysIgnored(property))
                {
                    continue;
                }

                Visit(property.PropertyType, $"{path} -> {type.Name}.{property.Name}");
            }

            foreach (var field in type.GetFields(All).Where(f => f.IsDefined(typeof(JsonIncludeAttribute), inherit: true) && !IsAlwaysIgnored(f)))
            {
                Visit(field.FieldType, $"{path} -> {type.Name}.{field.Name}");
            }
        }

        private static bool IsAlwaysIgnored(MemberInfo member)
            => member.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is { Condition: JsonIgnoreCondition.Always };

        private bool InvolvesOwn(Type type)
            => type.Assembly == _own
                || (type.HasElementType && InvolvesOwn(type.GetElementType()!))
                || (type.IsConstructedGenericType && type.GetGenericArguments().Any(InvolvesOwn));

        private void Record(Type type, string prefixedName, bool isOwn, string path)
            => _reached.Add(new ReachedSchema(type, GetSchemaId(type), prefixedName, isOwn, path));
    }
}
