using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// What Jellyfin 10.11.5 does to a nullable enum bound from a request (its <c>NullableEnumModelBinderProvider</c>, which
/// <c>AddJellyfinApi</c> inserts in front of MVC's binders, <c>Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:134</c>;
/// <c>Jellyfin.Api/ModelBinders/NullableEnumModelBinder.cs</c>), written anew for the tests: the first value is converted with
/// the enum's <c>TypeConverter</c>; a value it can't convert leaves the model unbound (<c>null</c>, no model error), and a number
/// no member has binds as it is. <see cref="WebHostHarness.WithMvc(System.Type[])"/> puts it first, as Jellyfin does, so a
/// plugin's tests meet the lenient binding a sandbox meets (a wrong <c>sortBy</c> of Bookmarks was a 400 in the tests
/// and ignored by Jellyfin; the kit's <c>StrictEnumQueryAttribute</c> makes it a 400 again).
/// </summary>
internal sealed class JellyfinNullableEnumBinder : IModelBinder
{
    /// <inheritdoc />
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var values = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (values.Length == 0)
        {
            return Task.CompletedTask;
        }

        var enumType = Nullable.GetUnderlyingType(bindingContext.ModelType) ?? bindingContext.ModelType;
        try
        {
            bindingContext.Result = ModelBindingResult.Success(TypeDescriptor.GetConverter(enumType).ConvertFromString(values.FirstValue!));
        }
        catch (FormatException)
        {
            // Jellyfin logs this at Debug and leaves the value unbound.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The provider: this binder for every nullable enum, whatever else the parameter or property declares.
    /// </summary>
    internal sealed class Provider : IModelBinderProvider
    {
        /// <inheritdoc />
        public IModelBinder? GetBinder(ModelBinderProviderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Nullable.GetUnderlyingType(context.Metadata.ModelType) is { IsEnum: true } ? new JellyfinNullableEnumBinder() : null;
        }
    }
}
