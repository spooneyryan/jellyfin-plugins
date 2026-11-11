using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// Creates the JSON converter of each <see cref="LenientEnum{TEnum}"/>.
/// </summary>
internal sealed class LenientEnumJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert is not null && typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(LenientEnum<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(typeof(LenientEnumJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }
}
