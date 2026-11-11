using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// Binds a list of enum values or GUIDs from the query string in either form clients use: one comma-separated
/// value, as Jellyfin's own endpoints take lists and <c>ApiClient.getUrl</c> writes an array (<c>itemTypes=Movie,Episode</c>,
/// <c>ids=a,b</c>), or the key repeated (<c>itemTypes=Movie&amp;itemTypes=Episode</c>). Enum names match ignoring case and
/// numbers of members are taken too; a GUID is read in any of .NET's formats (Jellyfin writes its ids without dashes).
/// Unlike Jellyfin's <c>CommaDelimitedCollectionModelBinder</c>, which drops a value it can't read, a value that names no
/// member, or isn't a GUID, is a model error, so the request is a 400 (<c>[ApiController]</c>) instead of a filter that
/// silently matches more or less.
/// </summary>
/// <remarks>
/// Put it on a property of a <c>[FromQuery]</c> model, or on a parameter, typed as an array or a generic list of an enum or
/// of <see cref="Guid"/> (<c>IReadOnlyList&lt;T&gt;</c> gets an array). The binding source stays the query, so ASP.NET Core's
/// API explorer, and so Jellyfin's OpenAPI document, list it as a query parameter of the element's array type. Left out,
/// the list is unbound (<c>null</c>). GUID lists since Bookmarks' clips (<c>GET /Bookmarks/Clips/Status?ids=</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
internal sealed class CommaSeparatedQueryAttribute : Attribute, IBinderTypeProviderMetadata
{
    /// <inheritdoc />
    public Type BinderType => typeof(CommaSeparatedListBinder);

    /// <inheritdoc />
    public BindingSource BindingSource => BindingSource.Query;

    /// <summary>
    /// The binder of <see cref="CommaSeparatedQueryAttribute"/>, for properties and parameters whose type is an array or a
    /// generic list of an enum or of <see cref="Guid"/>.
    /// </summary>
    internal sealed class CommaSeparatedListBinder : IModelBinder
    {
        /// <inheritdoc />
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ArgumentNullException.ThrowIfNull(bindingContext);

            var elementType = bindingContext.ModelType.IsArray
                ? bindingContext.ModelType.GetElementType()
                : bindingContext.ModelType.GenericTypeArguments.FirstOrDefault();
            if (elementType is null || !(elementType.IsEnum || elementType == typeof(Guid)))
            {
                throw new InvalidOperationException($"{nameof(CommaSeparatedQueryAttribute)} binds lists of an enum or of GUIDs, not {bindingContext.ModelType}.");
            }

            var values = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (values == ValueProviderResult.None)
            {
                // Not in the query: left unbound, so the property keeps null (no filter).
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, values);
            var parts = values.Values
                .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
            var result = Array.CreateInstance(elementType, parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                if (!TryParse(elementType, parts[i], out var value))
                {
                    bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, Refusal(elementType, parts[i]));
                    bindingContext.Result = ModelBindingResult.Failed();
                    return Task.CompletedTask;
                }

                result.SetValue(value, i);
            }

            bindingContext.Result = ModelBindingResult.Success(result);
            return Task.CompletedTask;
        }

        private static bool TryParse(Type elementType, string text, out object? value)
        {
            if (elementType == typeof(Guid))
            {
                var parsed = Guid.TryParse(text, CultureInfo.InvariantCulture, out var id);
                value = id;
                return parsed;
            }

            return Enum.TryParse(elementType, text, ignoreCase: true, out value) && Enum.IsDefined(elementType, value!);
        }

        private static string Refusal(Type elementType, string text)
            => elementType == typeof(Guid)
                ? $"The value '{text}' is not a GUID."
                : $"The value '{text}' is not one of {string.Join(", ", Enum.GetNames(elementType))}.";
    }
}
