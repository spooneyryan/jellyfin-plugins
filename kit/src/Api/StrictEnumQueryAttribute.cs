using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// Makes the enum values of a controller's query string strict (400 for a bad value): a value of an enum-typed
/// query parameter, or of an enum-typed property of a <c>[FromQuery]</c> model, that names no member of the enum (in any
/// case) and isn't one of its numbers (with <see cref="NamesOnly"/>: that names no member) answers 400 with MVC's own
/// validation ProblemDetails (<see cref="ApiBehaviorOptions.InvalidModelStateResponseFactory"/>), and the action never runs.
/// </summary>
/// <remarks>
/// <para>Why: Jellyfin 10.11.5 inserts its <c>NullableEnumModelBinderProvider</c> in front of MVC's binders
/// (<c>Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:134</c>). Its binder converts a nullable enum's value
/// with the enum's <c>TypeConverter</c> and, when that fails, leaves the parameter unbound, so it stays <c>null</c> with
/// only a Debug line; a number binds even when no member has it. A wrong <c>sortBy</c> would silently sort the default way
/// (found in a Bookmarks sandbox run). A non-nullable enum is MVC's, which already refuses both.</para>
/// <para>What it checks: every action parameter bound from the query whose type is an enum or a nullable enum, under its
/// binder model name; and every public property of that kind of a class-typed <c>[FromQuery]</c> parameter, under each name
/// MVC may bind it from. A model without a name of its own binds without a prefix (<c>shade</c>), unless a key of the query
/// starts with the parameter's name (<c>filter.limit</c>), which switches the whole model to that prefix
/// (<c>filter.shade</c>) for the request; both are checked, so a bad value is a 400 either way. A model named explicitly
/// (<c>[FromQuery(Name = "f")]</c>) always binds under that name (<c>f.shade</c>), and one named <c>""</c> never binds under
/// a prefix. Lists of an enum are <see cref="CommaSeparatedQueryAttribute"/>'s, whose binder reads the prefixed name too, and
/// a property or parameter with its own binder is left to it. An empty value counts as absent, as MVC reads it. Put it on
/// the controller; it stays controller-level.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class StrictEnumQueryAttribute : Attribute, IActionFilter, IOrderedFilter
{
    private static readonly ConcurrentDictionary<ActionDescriptor, IReadOnlyList<(string Name, Type EnumType)>> _checked = new();

    /// <summary>
    /// Gets the filter's order: after <c>[ApiController]</c>'s model-state filter (-2000), whose answer comes first, and before
    /// filters of the default order.
    /// </summary>
    public int Order => -1000;

    /// <summary>
    /// Gets or sets a value indicating whether only a member's name counts: a number is then a 400 too, even one a member has
    /// (User Ratings' <c>sort</c>, where <c>sort=1</c> sorted Oldest). Off by default, where a member's
    /// number is accepted as before.
    /// </summary>
    public bool NamesOnly { get; set; }

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = context.HttpContext.Request.Query;
        foreach (var (name, enumType) in _checked.GetOrAdd(context.ActionDescriptor, FindQueryEnums))
        {
            if (!query.TryGetValue(name, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) && !IsMember(enumType, value, NamesOnly))
                {
                    context.ModelState.TryAddModelError(name, $"The value '{value}' is not one of {string.Join(", ", Enum.GetNames(enumType))}.");
                }
            }
        }

        if (!context.ModelState.IsValid && context.Result is null)
        {
            var behavior = context.HttpContext.RequestServices.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;
            context.Result = behavior.InvalidModelStateResponseFactory(context);
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /// <summary>
    /// Whether a query-string value is a member of the enum: its name in any case, or its number. Names joined by commas are
    /// one value only for a <c>[Flags]</c> enum (<see cref="Enum.TryParse(Type, string, bool, out object)"/> would combine
    /// <c>Dark,Light</c> of any enum into a member's number).
    /// </summary>
    /// <param name="enumType">The enum.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether it names a member.</returns>
    internal static bool IsMember(Type enumType, string value)
        => (!value.Contains(',', StringComparison.Ordinal) || enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
            && Enum.TryParse(enumType, value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(enumType, parsed!);

    /// <summary>
    /// Whether a query-string value is a member of the enum, as <see cref="IsMember(Type, string)"/>; with
    /// <paramref name="namesOnly"/>, only by name (in any case; each of a <c>[Flags]</c> enum's comma-separated names), never
    /// by number.
    /// </summary>
    /// <param name="enumType">The enum.</param>
    /// <param name="value">The value.</param>
    /// <param name="namesOnly">Whether a number is refused.</param>
    /// <returns>Whether it names a member.</returns>
    internal static bool IsMember(Type enumType, string value, bool namesOnly)
    {
        if (!IsMember(enumType, value))
        {
            return false;
        }

        if (!namesOnly)
        {
            return true;
        }

        var names = Enum.GetNames(enumType);
        return value.Split(',').All(part => names.Contains(part.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    private static Type? EnumOf(Type type) => (Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } e ? e : null;

    private static bool HasOwnBinder(ICustomAttributeProvider member)
        => member.GetCustomAttributes(inherit: true).OfType<IBinderTypeProviderMetadata>().Any(m => m.BinderType is not null);

    // The name MVC binds a property under inside its model: the first name an attribute gives it (as MVC's BindingInfo reads
    // IModelNameProvider), else the property's own.
    private static string NameOf(PropertyInfo property)
        => property.GetCustomAttributes(inherit: true).OfType<IModelNameProvider>().Select(p => p.Name).FirstOrDefault(n => n is not null) ?? property.Name;

    // The prefixes MVC may bind a [FromQuery] model's properties under (its ParameterBinder): an explicit name always, else
    // none, or the parameter's name when a key of the query starts with it.
    private static IEnumerable<string> PrefixesOf(ParameterDescriptor parameter)
        => parameter.BindingInfo?.BinderModelName is { } explicitName ? [explicitName] : [string.Empty, parameter.Name];

    private static IReadOnlyList<(string Name, Type EnumType)> FindQueryEnums(ActionDescriptor action)
    {
        var found = new List<(string Name, Type EnumType)>();
        foreach (var parameter in action.Parameters)
        {
            if (parameter.BindingInfo?.BindingSource != BindingSource.Query || parameter.BindingInfo.BinderType is not null)
            {
                continue;
            }

            if (EnumOf(parameter.ParameterType) is { } enumType)
            {
                found.Add((parameter.BindingInfo.BinderModelName ?? parameter.Name, enumType));
            }
            else if (parameter.ParameterType.IsClass && parameter.ParameterType != typeof(string))
            {
                foreach (var property in parameter.ParameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.SetMethod is { IsPublic: true } && !HasOwnBinder(property) && EnumOf(property.PropertyType) is { } propertyEnum)
                    {
                        found.AddRange(PrefixesOf(parameter).Select(prefix => (ModelNames.CreatePropertyModelName(prefix, NameOf(property)), propertyEnum)));
                    }
                }
            }
        }

        // One check per key and enum (query keys match ignoring case), so a value is refused once.
        return found.DistinctBy(f => (f.Name.ToUpperInvariant(), f.EnumType)).ToList();
    }
}
