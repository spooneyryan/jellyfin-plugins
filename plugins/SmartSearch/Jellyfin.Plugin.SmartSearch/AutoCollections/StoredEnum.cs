using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// An enum value of the auto-collection files, read leniently: a name this version doesn't know becomes the enum's
/// <c>Unknown</c> member (0) instead of failing the whole file, and keeps its text, which is written back as it was. A
/// newer Smart Search's value (a new sort, a new membership mode) therefore survives an older build's writes, and the
/// older build sees <see cref="IsUnknown"/> and leaves that definition alone.
/// </summary>
/// <typeparam name="TEnum">The enum; its member 0 must be <c>Unknown</c>.</typeparam>
[JsonConverter(typeof(StoredEnumConverterFactory))]
internal readonly record struct StoredEnum<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StoredEnum{TEnum}"/> struct.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="raw">The text read when the value is unknown, else <c>null</c>.</param>
    public StoredEnum(TEnum value, string? raw = null)
    {
        Value = value;
        Raw = raw;
    }

    /// <summary>Gets the value; <c>Unknown</c> (0) for a name this version doesn't know.</summary>
    public TEnum Value { get; }

    /// <summary>Gets the text that was read for an unknown value, written back unchanged.</summary>
    public string? Raw { get; }

    /// <summary>Gets a value indicating whether the value isn't one this version knows.</summary>
    public bool IsUnknown => Convert.ToInt64(Value, CultureInfo.InvariantCulture) == 0;

    /// <summary>Wraps a known value.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator StoredEnum<TEnum>(TEnum value) => new(value);

    /// <summary>Wraps a known value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The stored value.</returns>
    public static StoredEnum<TEnum> FromTEnum(TEnum value) => new(value);

    /// <inheritdoc />
    public override string ToString() => IsUnknown && Raw is not null ? Raw : Value.ToString();
}
