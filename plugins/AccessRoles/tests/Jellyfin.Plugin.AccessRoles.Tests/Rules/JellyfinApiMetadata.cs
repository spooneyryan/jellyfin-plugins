using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Jellyfin.Plugin.AccessRoles.Rules;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// The controller actions of a <c>Jellyfin.Api.dll</c>, read from its metadata alone with System.Reflection.Metadata
/// (Jellyfin.Api has no NuGet package to reference): the file's bytes are read into memory and parsed; the assembly is never loaded, so none of its
/// code runs and none of its dependencies is needed. MVC's rules are applied as ASP.NET Core 9 applies them
/// (<c>ControllerFeatureProvider.IsController</c>, <c>DefaultApplicationModelProvider.IsAction</c> and
/// <c>CreateSelectors</c>, <c>AttributeRouteModel.CombineAttributeRouteModel</c> and <c>ReplaceTokens</c>,
/// <c>MvcOptions.SuppressAsyncSuffixInActionNames</c>, which Jellyfin leaves on), so each <see cref="ApiActionShape"/> is
/// what the running server's action descriptors give (<see cref="ServerActions"/>; the sandbox check compares the two).
/// </summary>
internal static class JellyfinApiMetadata
{
    private const string ControllerSuffix = "Controller";
    private const string AsyncSuffix = "Async";
    private const string MvcNamespace = "Microsoft.AspNetCore.Mvc";
    private const string AuthorizeAttribute = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
    private const string AllowAnonymousAttribute = "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute";
    private const string RouteAttribute = MvcNamespace + ".RouteAttribute";
    private const string AcceptVerbsAttribute = MvcNamespace + ".AcceptVerbsAttribute";
    private const string ActionNameAttribute = MvcNamespace + ".ActionNameAttribute";
    private const string NonActionAttribute = MvcNamespace + ".NonActionAttribute";
    private const string NonControllerAttribute = MvcNamespace + ".NonControllerAttribute";
    private const string ControllerAttribute = MvcNamespace + ".ControllerAttribute";
    private const string BindAttribute = MvcNamespace + ".BindAttribute";

    // ControllerBase carries [Controller] (inherited), so every public concrete class deriving from it is a controller.
    private static readonly HashSet<string> ControllerBases = new(StringComparer.Ordinal) { MvcNamespace + ".ControllerBase", MvcNamespace + ".Controller" };

    private static readonly Dictionary<string, string> HttpMethodAttributes = new(StringComparer.Ordinal)
    {
        [MvcNamespace + ".HttpGetAttribute"] = "GET",
        [MvcNamespace + ".HttpPostAttribute"] = "POST",
        [MvcNamespace + ".HttpPutAttribute"] = "PUT",
        [MvcNamespace + ".HttpDeleteAttribute"] = "DELETE",
        [MvcNamespace + ".HttpPatchAttribute"] = "PATCH",
        [MvcNamespace + ".HttpHeadAttribute"] = "HEAD",
        [MvcNamespace + ".HttpOptionsAttribute"] = "OPTIONS",
    };

    // Attributes that give a parameter the name MVC binds it from.
    private static readonly HashSet<string> BindingNameAttributes = new(StringComparer.Ordinal)
    {
        MvcNamespace + ".FromQueryAttribute",
        MvcNamespace + ".FromRouteAttribute",
        MvcNamespace + ".FromFormAttribute",
        MvcNamespace + ".FromHeaderAttribute",
        MvcNamespace + ".ModelBinderAttribute",
    };

    /// <summary>
    /// Reads a <c>Jellyfin.Api.dll</c>. The file is opened for reading only and shared with everyone (a running server
    /// keeps it mapped), and closed before anything is parsed.
    /// </summary>
    /// <param name="path">The assembly.</param>
    /// <returns>Its identity and actions.</returns>
    public static JellyfinApiAssembly Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        return Read(bytes);
    }

    /// <summary>
    /// Reads an assembly's bytes.
    /// </summary>
    /// <param name="bytes">The assembly file.</param>
    /// <returns>Its identity and actions.</returns>
    public static JellyfinApiAssembly Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var pe = new PEReader(ImmutableArray.Create(bytes));
        var md = pe.GetMetadataReader();
        var definition = md.GetAssemblyDefinition();
        var reader = new Reader(md);

        var actions = new List<ApiActionShape>();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (reader.IsController(handle, type))
            {
                actions.AddRange(reader.ReadActions(handle, type));
            }
        }

        return new JellyfinApiAssembly(
            md.GetString(definition.Name),
            definition.Version,
            reader.ReadAssemblyString(definition.GetCustomAttributes(), "System.Reflection.AssemblyInformationalVersionAttribute"),
            md.GetGuid(md.GetModuleDefinition().Mvid),
            actions.OrderBy(a => a.Controller, StringComparer.Ordinal).ThenBy(a => a.Action, StringComparer.Ordinal).ToList());
    }

    private sealed class Reader
    {
        private readonly MetadataReader _md;
        private readonly AttributeValueProvider _provider = new();

        public Reader(MetadataReader md)
        {
            _md = md;
        }

        public bool IsController(TypeDefinitionHandle handle, TypeDefinition type)
        {
            // Public top-level classes only (nested ones are never public in MVC's sense), concrete, non-generic.
            var attributes = type.Attributes;
            if ((attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public
                || (attributes & TypeAttributes.Interface) != 0
                || (attributes & TypeAttributes.Abstract) != 0
                || type.GetGenericParameters().Count > 0
                || IsValueTypeOrEnum(type))
            {
                return false;
            }

            if (Hierarchy(handle).Any(t => HasAttribute(t.GetCustomAttributes(), NonControllerAttribute)))
            {
                return false;
            }

            var name = _md.GetString(type.Name);
            return name.EndsWith(ControllerSuffix, StringComparison.OrdinalIgnoreCase)
                || Hierarchy(handle).Any(t => HasAttribute(t.GetCustomAttributes(), ControllerAttribute))
                || ExternalBase(handle) is { } external && ControllerBases.Contains(external);
        }

        public IEnumerable<ApiActionShape> ReadActions(TypeDefinitionHandle controllerHandle, TypeDefinition controller)
        {
            var controllerFullName = Names.Of(_md, controllerHandle);
            var controllerName = _md.GetString(controller.Name);
            if (controllerName.EndsWith(ControllerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                controllerName = controllerName[..^ControllerSuffix.Length];
            }

            var controllerRoutes = ControllerRoutes(controllerHandle);
            var classAttributes = Hierarchy(controllerHandle).SelectMany(t => Decode(t.GetCustomAttributes())).ToList();
            var classPolicies = Policies(classAttributes).ToList();
            var classAnonymous = classAttributes.Any(a => a.Type == AllowAnonymousAttribute);

            // Every public method of the controller and of its bases here, as Type.GetMethods() gives MVC: a base method that
            // a derived one hides with "new" is listed too (MVC then has two actions of one name; the manifest reports them
            // as duplicates). Overrides, which GetMethods() would collapse, are refused by IsAction.
            var actions = new List<ApiActionShape>();
            foreach (var typeHandle in HierarchyHandles(controllerHandle))
            {
                var type = _md.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = _md.GetMethodDefinition(methodHandle);
                    var name = _md.GetString(method.Name);
                    if (!IsAction(typeHandle, method, name))
                    {
                        continue;
                    }

                    var attributes = Decode(method.GetCustomAttributes()).ToList();
                    if (attributes.Any(a => a.Type == NonActionAttribute))
                    {
                        continue;
                    }

                    var actionName = attributes.FirstOrDefault(a => a.Type == ActionNameAttribute)?.Arguments.FirstOrDefault() as string
                        ?? (name.EndsWith(AsyncSuffix, StringComparison.Ordinal) ? name[..^AsyncSuffix.Length] : name);

                    var routes = new List<string>();
                    foreach (var selector in Selectors(attributes))
                    {
                        foreach (var template in Combine(controllerRoutes, selector.Template))
                        {
                            routes.Add(ApiActionShape.FormatRoute(selector.Methods, template is null ? null : ReplaceTokens(template, controllerName, actionName)));
                        }
                    }

                    actions.Add(new ApiActionShape(
                        controllerFullName,
                        actionName,
                        routes,
                        classPolicies.Concat(Policies(attributes)),
                        classAnonymous || attributes.Any(a => a.Type == AllowAnonymousAttribute),
                        Parameters(controllerFullName + "." + name, method)));
                }
            }

            return actions;
        }

        public string? ReadAssemblyString(CustomAttributeHandleCollection attributes, string attributeType)
            => Decode(attributes).FirstOrDefault(a => a.Type == attributeType)?.Arguments.FirstOrDefault() as string;

        // DefaultApplicationModelProvider.IsAction, for a method declared in the controller or one of its bases here.
        private bool IsAction(TypeDefinitionHandle declaringType, MethodDefinition method, string name)
        {
            var attributes = method.Attributes;
            if ((attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public
                || (attributes & MethodAttributes.Static) != 0
                || (attributes & MethodAttributes.Abstract) != 0
                || (attributes & MethodAttributes.SpecialName) != 0
                || method.GetGenericParameters().Count > 0)
            {
                return false;
            }

            // object's virtual methods overridden (ToString, Equals, GetHashCode) and IDisposable.Dispose are no actions.
            var parameters = method.DecodeSignature(_provider.Signatures, genericContext: null).ParameterTypes.Length;
            var isOverride = (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0;
            if (isOverride && (name, parameters) is ("ToString", 0) or ("GetHashCode", 0) or ("Equals", 1) or ("Finalize", 0))
            {
                return false;
            }

            if (name == "Dispose" && parameters == 0 && Implements(declaringType, "System.IDisposable"))
            {
                return false;
            }

            // An override takes its base method's inherited attributes (routes, policies, [NonAction]: ControllerBase's
            // virtual methods carry [NonAction]), which another assembly's metadata would have to show; Jellyfin.Api has no
            // overriding actions, so the reader refuses one rather than guess.
            return isOverride
                ? throw new NotSupportedException($"{Names.Of(_md, declaringType)}.{name} overrides a base method; teach JellyfinApiMetadata MVC's attribute inheritance for overrides.")
                : true;
        }

        // CreateSelectors: one selector per route-defining attribute; the silent ones together, unless a [Route] takes them.
        private List<Selector> Selectors(List<DecodedAttribute> attributes)
        {
            var providers = attributes.Where(a => a.IsRouteProvider).ToList();
            var defining = providers.Where(p => !p.IsSilentRoute).ToList();
            var silent = providers.Count > defining.Count;
            if (defining.Any(p => !p.IsHttpMethodProvider))
            {
                silent = false;
            }

            var selectors = new List<Selector>();
            if (defining.Count == 0 && !silent)
            {
                selectors.Add(new Selector(null, attributes.SelectMany(a => a.HttpMethods).ToList()));
                return selectors;
            }

            foreach (var provider in defining)
            {
                // A route provider that is also an HTTP method provider keeps only its own methods; a [Route] takes every
                // silent method provider's.
                var methods = provider.IsHttpMethodProvider
                    ? provider.HttpMethods
                    : attributes.Where(a => !defining.Contains(a)).SelectMany(a => a.HttpMethods).ToList();
                selectors.Add(new Selector(provider.Template, methods));
            }

            if (silent)
            {
                selectors.Add(new Selector(null, attributes.Where(a => !defining.Contains(a)).SelectMany(a => a.HttpMethods).ToList()));
            }

            return selectors;
        }

        // ActionAttributeRouteModel.GetAttributeRoutes and AttributeRouteModel.CombineAttributeRouteModel: an absolute action
        // template (/ or ~/) ignores the controller's routes; otherwise each controller route is combined with it. A null
        // combination is a conventionally routed action (no attribute route), which Jellyfin has none of.
        private static IEnumerable<string?> Combine(IReadOnlyList<string?> controllerRoutes, string? actionTemplate)
        {
            if (IsOverride(actionTemplate) || controllerRoutes.Count == 0)
            {
                return [Clean(CombineCore(null, actionTemplate))];
            }

            return controllerRoutes.Select(c => Clean(CombineCore(c, actionTemplate))).ToList();
        }

        private static bool IsOverride(string? template)
            => template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal));

        private static bool IsEmptyLeft(string? template) => template is null or "" or "/" or "~/";

        private static string? CombineCore(string? prefix, string? template)
        {
            if (IsOverride(template))
            {
                return template;
            }

            if (IsEmptyLeft(prefix))
            {
                return template;
            }

            if (IsEmptyLeft(template))
            {
                return prefix;
            }

            return prefix!.EndsWith('/') ? prefix + template : prefix + "/" + template;
        }

        private static string? Clean(string? result)
        {
            if (result is null || result == "//")
            {
                return result;
            }

            var start = result.StartsWith('/') ? 1 : result.StartsWith("~/", StringComparison.Ordinal) ? 2 : 0;
            if (start == result.Length)
            {
                return string.Empty;
            }

            var length = result.Length - start - (result.EndsWith('/') ? 1 : 0);
            return result.Substring(start, length);
        }

        // AttributeRouteModel.ReplaceTokens for [controller] and [action] ([[ and ]] escape a bracket).
        private static string ReplaceTokens(string template, string controller, string action)
        {
            if (!template.Contains('[', StringComparison.Ordinal))
            {
                return template;
            }

            var result = new System.Text.StringBuilder(template.Length);
            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if ((c is '[' or ']') && i + 1 < template.Length && template[i + 1] == c)
                {
                    result.Append(c);
                    i++;
                    continue;
                }

                if (c == '[')
                {
                    var end = template.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        throw new InvalidOperationException($"The route template '{template}' has an unclosed token.");
                    }

                    var token = template[(i + 1)..end];
                    result.Append(token.ToUpperInvariant() switch
                    {
                        "CONTROLLER" => controller,
                        "ACTION" => action,
                        _ => throw new InvalidOperationException($"The route template '{template}' uses the token [{token}], which the reader doesn't know.")
                    });
                    i = end;
                    continue;
                }

                result.Append(c);
            }

            return result.ToString();
        }

        // The routes of the nearest class in the hierarchy that declares any (MVC lets a derived class override the base's).
        private List<string?> ControllerRoutes(TypeDefinitionHandle controller)
        {
            foreach (var type in Hierarchy(controller))
            {
                var routes = Decode(type.GetCustomAttributes()).Where(a => a.IsRouteProvider).ToList();
                if (routes.Count > 0)
                {
                    return routes.Select(r => r.Template).ToList();
                }
            }

            return [];
        }

        private static IEnumerable<string> Policies(IEnumerable<DecodedAttribute> attributes)
            => attributes.Where(a => a.Type == AuthorizeAttribute).Select(a => a.Named.TryGetValue("Policy", out var p) && p is string policy && policy.Length > 0 ? policy : ApiActionShape.DefaultPolicy);

        // Every parameter of the method, as MVC's ControllerParameterDescriptor has it: its name (the key of the action's
        // arguments) and the name of the first binding attribute that names one (BindingInfo.GetBindingInfo).
        private List<string> Parameters(string method, MethodDefinition definition)
        {
            var count = definition.DecodeSignature(_provider.Signatures, genericContext: null).ParameterTypes.Length;
            var parameters = new List<string>();
            foreach (var handle in definition.GetParameters())
            {
                var parameter = _md.GetParameter(handle);
                if (parameter.SequenceNumber == 0)
                {
                    continue;
                }

                var name = _md.GetString(parameter.Name);
                if (name.Length == 0)
                {
                    throw new NotSupportedException($"{method} has a parameter without a name, which MVC can't bind by name.");
                }

                var bindingName = Decode(parameter.GetCustomAttributes())
                    .Where(a => BindingNameAttributes.Contains(a.Type))
                    .Select(a => a.Named.TryGetValue("Name", out var n) ? n as string : null)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));
                parameters.Add(ApiActionShape.FormatParameter(name, bindingName));
            }

            // The compiler writes a row for every named parameter; a method with fewer rows than its signature's parameters
            // would have parameters MVC sees and this reader doesn't.
            return parameters.Count == count
                ? parameters
                : throw new NotSupportedException($"{method} has {count} parameters but {parameters.Count} parameter rows.");
        }

        private IEnumerable<TypeDefinition> Hierarchy(TypeDefinitionHandle handle) => HierarchyHandles(handle).Select(_md.GetTypeDefinition);

        // The type and its base types defined in this assembly, most derived first.
        private IEnumerable<TypeDefinitionHandle> HierarchyHandles(TypeDefinitionHandle handle)
        {
            var current = handle;
            for (var depth = 0; depth < 64; depth++)
            {
                yield return current;
                var baseType = _md.GetTypeDefinition(current).BaseType;
                if (baseType.Kind != HandleKind.TypeDefinition)
                {
                    yield break;
                }

                current = (TypeDefinitionHandle)baseType;
            }

            throw new InvalidOperationException("The type hierarchy is deeper than 64 types; the assembly isn't readable this way.");
        }

        // The first base type defined in another assembly, by full name (null at System.Object or a generic base).
        private string? ExternalBase(TypeDefinitionHandle handle)
        {
            var last = HierarchyHandles(handle).Last();
            var baseType = _md.GetTypeDefinition(last).BaseType;
            return baseType.Kind == HandleKind.TypeReference ? Names.Of(_md, (TypeReferenceHandle)baseType) : null;
        }

        private bool IsValueTypeOrEnum(TypeDefinition type)
            => type.BaseType.Kind == HandleKind.TypeReference
                && Names.Of(_md, (TypeReferenceHandle)type.BaseType) is "System.ValueType" or "System.Enum";

        private bool Implements(TypeDefinitionHandle type, string interfaceName)
            => HierarchyHandles(type).Any(t => _md.GetTypeDefinition(t).GetInterfaceImplementations()
                .Select(i => _md.GetInterfaceImplementation(i).Interface)
                .Any(i => i.Kind == HandleKind.TypeReference && Names.Of(_md, (TypeReferenceHandle)i) == interfaceName));

        private bool HasAttribute(CustomAttributeHandleCollection attributes, string type)
            => attributes.Any(h => Names.AttributeType(_md, _md.GetCustomAttribute(h)) == type);

        // Decodes the attributes the reader needs (routing, authorization, binding names); the others are listed by type only.
        private IEnumerable<DecodedAttribute> Decode(CustomAttributeHandleCollection attributes)
        {
            foreach (var handle in attributes)
            {
                var attribute = _md.GetCustomAttribute(handle);
                var type = Names.AttributeType(_md, attribute);
                if (Names.AttributeTypeDefinition(_md, attribute) is { IsNil: false } own && ExternalBase(own) is { } ownBase && IsRoutingAuthorizationOrBinding(ownBase))
                {
                    // An attribute type of this assembly deriving from a routing, authorization or binding attribute (as 10.8's
                    // DLNA HttpSubscribe did): its methods, policy or name are set in its constructor's code, which metadata
                    // can't show.
                    throw new NotSupportedException($"The attribute {type} derives from {ownBase}; teach JellyfinApiMetadata what it means for routes, policies and parameter names.");
                }

                if (!IsRelevant(type))
                {
                    yield return new DecodedAttribute(type, [], new Dictionary<string, object?>(StringComparer.Ordinal));
                    continue;
                }

                var value = attribute.DecodeValue(_provider);
                yield return new DecodedAttribute(
                    type,
                    value.FixedArguments.Select(a => Unwrap(a.Value)).ToList(),
                    value.NamedArguments.Where(n => n.Name is not null).ToDictionary(n => n.Name!, n => Unwrap(n.Value), StringComparer.Ordinal));
            }
        }

        private static bool IsRoutingAuthorizationOrBinding(string type)
            => HttpMethodAttributes.ContainsKey(type) || BindingNameAttributes.Contains(type)
                || type is RouteAttribute or AcceptVerbsAttribute or AuthorizeAttribute or AllowAnonymousAttribute or BindAttribute
                    or "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute";

        private static bool IsRelevant(string type)
        {
            if (HttpMethodAttributes.ContainsKey(type) || BindingNameAttributes.Contains(type)
                || type is RouteAttribute or AcceptVerbsAttribute or AuthorizeAttribute or AllowAnonymousAttribute or ActionNameAttribute
                    or "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                return true;
            }

            // A routing or authorization attribute the reader doesn't know would change routes or policies unseen; [Bind]'s
            // Prefix would name a parameter's binding as [FromQuery(Name)] does.
            if ((type.StartsWith(MvcNamespace + ".Http", StringComparison.Ordinal) && type.EndsWith("Attribute", StringComparison.Ordinal))
                || type.StartsWith("Microsoft.AspNetCore.Authorization.", StringComparison.Ordinal)
                || type == BindAttribute)
            {
                throw new NotSupportedException($"The attribute {type} isn't one the manifest reader knows; teach JellyfinApiMetadata what it means for routes, policies and parameter names.");
            }

            return false;
        }

        // Arrays come back as collections of typed arguments.
        private static object? Unwrap(object? value)
            => value is ImmutableArray<CustomAttributeTypedArgument<string>> array ? array.Select(a => Unwrap(a.Value)).ToList() : value;
    }

    private sealed record Selector(string? Template, IReadOnlyList<string> Methods);

    private sealed record DecodedAttribute(string Type, IReadOnlyList<object?> Arguments, IReadOnlyDictionary<string, object?> Named)
    {
        public bool IsHttpMethodProvider => HttpMethodAttributes.ContainsKey(Type) || Type == AcceptVerbsAttribute;

        public bool IsRouteProvider => IsHttpMethodProvider || Type == RouteAttribute;

        public string? Template => Type switch
        {
            RouteAttribute => Arguments.FirstOrDefault() as string,
            AcceptVerbsAttribute => Named.TryGetValue("Route", out var route) ? route as string : null,
            _ => Arguments.FirstOrDefault() as string
        };

        // IRouteTemplateProvider: Template, Order and Name all unset (HttpMethodAttribute keeps Order nullable).
        public bool IsSilentRoute => Template is null && !Named.ContainsKey("Order") && !(Named.TryGetValue("Name", out var name) && name is not null);

        public IReadOnlyList<string> HttpMethods => Type switch
        {
            AcceptVerbsAttribute => Arguments.SelectMany(a => a switch
            {
                string one => [one],
                IEnumerable<object?> many => many.OfType<string>(),
                _ => []
            }).Select(m => m.ToUpperInvariant()).ToList(),
            _ when HttpMethodAttributes.TryGetValue(Type, out var method) => [method],
            _ => []
        };
    }

    private static class Names
    {
        public static string Of(MetadataReader md, TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            var name = md.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return Of(md, declaring) + "+" + name;
            }

            var ns = md.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public static string Of(MetadataReader md, TypeReferenceHandle handle)
        {
            var type = md.GetTypeReference(handle);
            var name = md.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return Of(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
            }

            var ns = md.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public static TypeDefinitionHandle AttributeTypeDefinition(MetadataReader md, CustomAttribute attribute)
            => attribute.Constructor.Kind switch
            {
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                HandleKind.MemberReference when md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeDefinition } parent
                    => (TypeDefinitionHandle)parent,
                _ => default
            };

        public static string AttributeType(MetadataReader md, CustomAttribute attribute)
        {
            EntityHandle parent = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default
            };

            return parent.Kind switch
            {
                HandleKind.TypeReference => Of(md, (TypeReferenceHandle)parent),
                HandleKind.TypeDefinition => Of(md, (TypeDefinitionHandle)parent),
                HandleKind.TypeSpecification => "(generic attribute)",
                _ => "(unknown)"
            };
        }
    }

    // Attribute values as names and plain values; enum-typed arguments only occur in attributes the reader skips.
    private sealed class AttributeValueProvider : ICustomAttributeTypeProvider<string>
    {
        public SignatureProvider Signatures { get; } = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Names.Of(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Names.Of(reader, handle);

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type)
            => throw new NotSupportedException($"An enum argument of type {type} in a routing or authorization attribute; the reader doesn't decode enums.");

        public bool IsSystemType(string type) => type == "System.Type";
    }

    // Only the parameter count of a method signature is used.
    private sealed class SignatureProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Names.Of(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Names.Of(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}

/// <summary>
/// A <c>Jellyfin.Api.dll</c> as <see cref="JellyfinApiMetadata"/> read it.
/// </summary>
/// <param name="Name">The assembly name.</param>
/// <param name="Version">The assembly version.</param>
/// <param name="InformationalVersion">The informational version (Jellyfin writes its source commit there), or <c>null</c>.</param>
/// <param name="Mvid">The module version id.</param>
/// <param name="Actions">The controller actions, by controller and action name.</param>
internal sealed record JellyfinApiAssembly(string Name, Version Version, string? InformationalVersion, Guid Mvid, IReadOnlyList<ApiActionShape> Actions);
