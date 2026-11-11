using System;
using System.Globalization;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.UserRatings.Domain;

/// <summary>
/// A rating's stars: 0.5 to 5 in half-star steps, stored as half-star units
/// <see cref="Stars2"/> 1 to 10 (7 = 3.5 stars). There is no zero-star rating: "unrated" is the absence of a value.
/// </summary>
internal readonly record struct StarValue
{
    /// <summary>The 400's message for stars that aren't a half step from 0.5 to 5.</summary>
    public const string InvalidMessage = "Stars must be between 0.5 and 5 in steps of 0.5.";

    private StarValue(int stars2) => Stars2 = stars2;

    /// <summary>Gets the stars in half-star units, 1 to 10.</summary>
    public int Stars2 { get; }

    /// <summary>Gets the stars, 0.5 to 5.</summary>
    public decimal Stars => Stars2 / 2m;

    /// <summary>
    /// Reads stars the API was sent (<c>{"Stars": 3.5}</c>).
    /// </summary>
    /// <param name="stars">The stars.</param>
    /// <returns>The value.</returns>
    /// <exception cref="PluginException">400 (<see cref="InvalidMessage"/>): 0, 0.25, 3.3, 5.5 and the like.</exception>
    public static StarValue FromStars(decimal stars)
    {
        var doubled = stars * 2;
        if (doubled != decimal.Truncate(doubled) || doubled < 1 || doubled > 10)
        {
            throw PluginException.Invalid(InvalidMessage);
        }

        return new StarValue((int)doubled);
    }

    /// <summary>
    /// Reads stars given as a floating-point number; every half step is exact in binary, so nothing near one passes.
    /// </summary>
    /// <param name="stars">The stars.</param>
    /// <returns>The value.</returns>
    /// <exception cref="PluginException">400: not a half step from 0.5 to 5, NaN or an infinity.</exception>
    public static StarValue FromStars(double stars)
        => TryFromStars(stars, out var value) ? value : throw PluginException.Invalid(InvalidMessage);

    /// <summary>
    /// Reads stars given as a floating-point number.
    /// </summary>
    /// <param name="stars">The stars.</param>
    /// <param name="value">The value, when they are a half step from 0.5 to 5.</param>
    /// <returns>Whether they are.</returns>
    public static bool TryFromStars(double stars, out StarValue value)
    {
        value = default;
        var doubled = stars * 2;
        if (!double.IsFinite(doubled) || doubled != Math.Floor(doubled) || doubled < 1 || doubled > 10)
        {
            return false;
        }

        value = new StarValue((int)doubled);
        return true;
    }

    /// <summary>
    /// Makes a value from stored half-star units.
    /// </summary>
    /// <param name="stars2">The half-star units, 1 to 10.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1 to 10.</exception>
    public static StarValue FromStars2(int stars2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stars2, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stars2, 10);
        return new StarValue(stars2);
    }

    /// <summary>
    /// Reads Jellyfin's native <c>UserData.Rating</c> (0 to 10): <c>clamp(round(r), 1, 10)</c> half stars, rounding
    /// halves away from zero; 0 (and anything that rounds to it, or no value) is no rating.
    /// </summary>
    /// <param name="rating">The native rating.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    public static StarValue? FromUserDataRating(double? rating)
    {
        if (rating is not { } r || !double.IsFinite(r))
        {
            return null;
        }

        var rounded = Math.Round(r, MidpointRounding.AwayFromZero);
        return rounded <= 0 ? null : new StarValue((int)Math.Min(rounded, 10));
    }

    /// <summary>
    /// Gets the value written into <c>UserData.Rating</c> by the mirror (1.1): the half-star units as a number of 10.
    /// </summary>
    /// <returns>1 to 10.</returns>
    public double ToUserDataRating() => Stars2;

    /// <inheritdoc />
    public override string ToString() => Stars.ToString("0.0", CultureInfo.InvariantCulture);
}
