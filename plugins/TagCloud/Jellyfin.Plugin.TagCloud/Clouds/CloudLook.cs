using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Configuration;

namespace Jellyfin.Plugin.TagCloud.Clouds;

/// <summary>
/// How a saved cloud looks, as stored: every choice of the settings panel, with every name
/// one the page knows and every number in its range (<see cref="Normalize"/>).
/// </summary>
internal sealed record CloudLook
{
    /// <summary>The arrangements: Cloud (packed) and Rows.</summary>
    public static readonly string[] Arrangements = ["Packed", "Flow"];

    /// <summary>The Rows orders.</summary>
    public static readonly string[] Orders = ["Centered", "Shuffled", "Alphabetical", "ByWeight"];

    /// <summary>The Cloud shapes.</summary>
    public static readonly string[] Shapes = ["Wide", "Landscape", "Square", "Tall"];

    /// <summary>The letter cases.</summary>
    public static readonly string[] TextCases = ["AsIs", "Upper", "Lower"];

    /// <summary>The fonts.</summary>
    public static readonly string[] Fonts = ["Theme", "Impact", "Heavy", "Condensed", "Serif", "Typewriter", "Handwritten"];

    /// <summary>The size scales.</summary>
    public static readonly string[] Scales = ["SquareRoot", "Linear", "Logarithmic"];

    /// <summary>The palettes.</summary>
    public static readonly string[] Palettes = ["Theme", "Accent", "Ink", "Rainbow", "Sunset", "Citrus", "Ocean", "Forest", "Neon", "Pastel", "Classic"];

    /// <summary>The colour rules.</summary>
    public static readonly string[] ColorBys = ["Word", "Weight"];

    /// <summary>The backdrops.</summary>
    public static readonly string[] Backgrounds = ["Theme", "Black", "Midnight", "White", "Paper", "Slate"];

    /// <summary>Gets how many words to show.</summary>
    public int MaxWords { get; init; } = 100;

    /// <summary>Gets the arrangement.</summary>
    public string Arrangement { get; init; } = "Packed";

    /// <summary>Gets the Rows order.</summary>
    public string Order { get; init; } = "Centered";

    /// <summary>Gets the Cloud shape.</summary>
    public string Shape { get; init; } = "Wide";

    /// <summary>Gets the arrangement's seed.</summary>
    public int Seed { get; init; }

    /// <summary>Gets how freely sizes mix (0–1).</summary>
    public double SizeMixing { get; init; }

    /// <summary>Gets the space between words, in em.</summary>
    public double WordSpacing { get; init; } = 0.5;

    /// <summary>Gets the space between lines, in em.</summary>
    public double LineSpacing { get; init; } = 0.25;

    /// <summary>Gets the letter case.</summary>
    public string TextCase { get; init; } = "AsIs";

    /// <summary>Gets the font.</summary>
    public string Font { get; init; } = "Theme";

    /// <summary>Gets the smallest word's size, in rem.</summary>
    public double MinFontSize { get; init; } = 0.85;

    /// <summary>Gets the largest word's size, in rem.</summary>
    public double MaxFontSize { get; init; } = 4;

    /// <summary>Gets the size scale.</summary>
    public string Scale { get; init; } = "SquareRoot";

    /// <summary>Gets the palette.</summary>
    public string Palette { get; init; } = "Theme";

    /// <summary>Gets the colour rule.</summary>
    public string ColorBy { get; init; } = "Word";

    /// <summary>Gets the backdrop.</summary>
    public string Background { get; init; } = "Theme";

    /// <summary>Gets fields a newer version wrote, kept as they are.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Gets a look with every name known and every number in range; unknown names take the default.
    /// </summary>
    /// <param name="look">The look a client sent, or null for the defaults.</param>
    /// <returns>The stored look.</returns>
    public static CloudLook Normalize(TagCloudLookDto? look)
    {
        var defaults = new CloudLook();
        if (look is null)
        {
            return defaults;
        }

        var words = Math.Clamp(look.MaxWords, PluginConfiguration.WordStep, PluginConfiguration.HighestWordsLimit);
        var smallest = Range(look.MinFontSize, 0.4, 2, defaults.MinFontSize);
        return new CloudLook
        {
            MaxWords = words - (words % PluginConfiguration.WordStep),
            Arrangement = Known(look.Arrangement, Arrangements),
            Order = Known(look.Order, Orders),
            Shape = Known(look.Shape, Shapes),
            Seed = look.Seed,
            SizeMixing = Range(look.SizeMixing, 0, 1, defaults.SizeMixing),
            WordSpacing = Range(look.WordSpacing, 0, 2, defaults.WordSpacing),
            LineSpacing = Range(look.LineSpacing, 0, 2, defaults.LineSpacing),
            TextCase = Known(look.TextCase, TextCases),
            Font = Known(look.Font, Fonts),
            MinFontSize = smallest,
            MaxFontSize = Math.Max(smallest, Range(look.MaxFontSize, 1.5, 8, defaults.MaxFontSize)),
            Scale = Known(look.Scale, Scales),
            Palette = Known(look.Palette, Palettes),
            ColorBy = Known(look.ColorBy, ColorBys),
            Background = Known(look.Background, Backgrounds)
        };
    }

    /// <summary>
    /// Gets the look as the API shows it.
    /// </summary>
    /// <returns>The DTO.</returns>
    public TagCloudLookDto ToDto() => new()
    {
        MaxWords = MaxWords,
        Arrangement = Arrangement,
        Order = Order,
        Shape = Shape,
        Seed = Seed,
        SizeMixing = SizeMixing,
        WordSpacing = WordSpacing,
        LineSpacing = LineSpacing,
        TextCase = TextCase,
        Font = Font,
        MinFontSize = MinFontSize,
        MaxFontSize = MaxFontSize,
        Scale = Scale,
        Palette = Palette,
        ColorBy = ColorBy,
        Background = Background
    };

    // The known name in its own spelling, whatever the case sent; the first (the default) for anything else.
    private static string Known(string? value, string[] names)
        => names.FirstOrDefault(name => string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? names[0];

    private static double Range(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Round(Math.Clamp(value, min, max), 3) : fallback;
}
