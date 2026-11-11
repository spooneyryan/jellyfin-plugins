using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// An enum value as <c>accessroles.json</c> stores it (read with a lenient converter): the
/// member's name, or, for a name this build doesn't know (a newer build wrote it), that name exactly as written, kept so a
/// save never loses it. Code asks <see cref="Value"/> (null when unknown) and decides what an unknown value means: an
/// unknown lock is ignored, an unknown assignment state or library mode makes that user count as managed with every
/// settings section locked and keeps the plugin from writing their native policy.
/// </summary>
/// <typeparam name="TEnum">The enum.</typeparam>
[JsonConverter(typeof(LenientEnumJsonConverterFactory))]
internal readonly struct LenientEnum<TEnum> : IEquatable<LenientEnum<TEnum>>
    where TEnum : struct, Enum
{
    private static readonly string[] _names = Enum.GetNames<TEnum>();

    private readonly string? _name;

    private LenientEnum(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Gets the stored name: the member's own spelling for a known value, otherwise the text as read. The default
    /// instance is the enum's zero member.
    /// </summary>
    public string Name => _name ?? default(TEnum).ToString();

    /// <summary>Gets the value, or <c>null</c> when this build doesn't know the name.</summary>
    public TEnum? Value => _names.Contains(Name, StringComparer.Ordinal) ? Enum.Parse<TEnum>(Name) : null;

    /// <summary>Gets a value indicating whether this build knows the name.</summary>
    public bool IsKnown => Value.HasValue;

    /// <summary>
    /// Converts a known value.
    /// </summary>
    /// <param name="value">The value.</param>
    public static implicit operator LenientEnum<TEnum>(TEnum value) => From(value);

    /// <summary>
    /// Compares two values by stored name.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> when they are the same name.</returns>
    public static bool operator ==(LenientEnum<TEnum> left, LenientEnum<TEnum> right) => left.Equals(right);

    /// <summary>
    /// Compares two values by stored name.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> when they are different names.</returns>
    public static bool operator !=(LenientEnum<TEnum> left, LenientEnum<TEnum> right) => !left.Equals(right);

    /// <summary>
    /// Wraps a known value.
    /// </summary>
    /// <param name="value">The value; it must be a defined member.</param>
    /// <returns>The stored value.</returns>
    public static LenientEnum<TEnum> From(TEnum value)
    {
        var name = Enum.GetName(value) ?? throw new ArgumentOutOfRangeException(nameof(value), value, $"{value} is not a member of {typeof(TEnum).Name}.");
        return new LenientEnum<TEnum>(name);
    }

    /// <summary>
    /// Reads a stored name: a member's name in any letter case becomes that member (spelled as declared); anything else is
    /// kept as written, unknown. Numbers are names like any other text, never members.
    /// </summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The value.</returns>
    public static LenientEnum<TEnum> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var member = _names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        return new LenientEnum<TEnum>(member ?? text);
    }

    /// <summary>
    /// Gets a value indicating whether this is the given member.
    /// </summary>
    /// <param name="value">The member.</param>
    /// <returns><c>true</c> when the stored name is the member's.</returns>
    public bool Is(TEnum value) => Value is { } known && known.Equals(value);

    /// <inheritdoc />
    public bool Equals(LenientEnum<TEnum> other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LenientEnum<TEnum> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;
}
