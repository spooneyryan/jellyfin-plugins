namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// How a cloud looks: every choice of the settings panel. Names that aren't known fall back to
/// the defaults and numbers are held to their ranges when a cloud is saved.
/// </summary>
public sealed class TagCloudLookDto
{
    /// <summary>Gets or sets how many words to show, a multiple of 25.</summary>
    public int MaxWords { get; set; } = 100;

    /// <summary>Gets or sets the arrangement: <c>Packed</c> (Cloud) or <c>Flow</c> (Rows).</summary>
    public string Arrangement { get; set; } = "Packed";

    /// <summary>Gets or sets the Rows order: <c>Centered</c>, <c>Shuffled</c>, <c>Alphabetical</c> or <c>ByWeight</c>.</summary>
    public string Order { get; set; } = "Centered";

    /// <summary>Gets or sets the Cloud shape: <c>Wide</c>, <c>Landscape</c>, <c>Square</c> or <c>Tall</c>.</summary>
    public string Shape { get; set; } = "Wide";

    /// <summary>Gets or sets the arrangement's seed: New arrangement adds one.</summary>
    public int Seed { get; set; }

    /// <summary>Gets or sets how freely sizes mix, 0 (the heaviest in the middle) to 1 (every size everywhere).</summary>
    public double SizeMixing { get; set; }

    /// <summary>Gets or sets the space between words, in em (0–2).</summary>
    public double WordSpacing { get; set; } = 0.5;

    /// <summary>Gets or sets the space between lines, in em (0–2).</summary>
    public double LineSpacing { get; set; } = 0.25;

    /// <summary>Gets or sets the letter case: <c>AsIs</c>, <c>Upper</c> or <c>Lower</c>.</summary>
    public string TextCase { get; set; } = "AsIs";

    /// <summary>
    /// Gets or sets the font: <c>Theme</c>, <c>Impact</c>, <c>Heavy</c>, <c>Condensed</c>, <c>Serif</c>,
    /// <c>Typewriter</c> or <c>Handwritten</c>.
    /// </summary>
    public string Font { get; set; } = "Theme";

    /// <summary>Gets or sets the smallest word's size, in rem (0.4–2).</summary>
    public double MinFontSize { get; set; } = 0.85;

    /// <summary>Gets or sets the largest word's size, in rem (1.5–8).</summary>
    public double MaxFontSize { get; set; } = 4;

    /// <summary>
    /// Gets or sets the size scale: <c>SquareRoot</c> (Balanced), <c>Linear</c> (Dramatic) or <c>Logarithmic</c> (Even).
    /// </summary>
    public string Scale { get; set; } = "SquareRoot";

    /// <summary>
    /// Gets or sets the palette: <c>Theme</c>, <c>Accent</c>, <c>Ink</c>, <c>Rainbow</c>, <c>Sunset</c>, <c>Citrus</c>,
    /// <c>Ocean</c>, <c>Forest</c>, <c>Neon</c>, <c>Pastel</c> or <c>Classic</c>.
    /// </summary>
    public string Palette { get; set; } = "Theme";

    /// <summary>Gets or sets how the palette's colours are handed out: <c>Word</c> or <c>Weight</c>.</summary>
    public string ColorBy { get; set; } = "Word";

    /// <summary>
    /// Gets or sets the backdrop: <c>Theme</c>, <c>Black</c>, <c>Midnight</c>, <c>White</c>, <c>Paper</c> or <c>Slate</c>.
    /// </summary>
    public string Background { get; set; } = "Theme";
}
