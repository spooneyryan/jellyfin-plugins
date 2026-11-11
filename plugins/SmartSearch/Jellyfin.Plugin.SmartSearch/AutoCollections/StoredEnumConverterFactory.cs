using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Creates the converters of <see cref="StoredEnum{TEnum}"/>.</summary>
internal sealed class StoredEnumConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(StoredEnum<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(typeof(StoredEnumConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }
}
