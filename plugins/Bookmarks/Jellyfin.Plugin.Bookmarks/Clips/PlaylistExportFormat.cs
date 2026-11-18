using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The one picture size and frame rate of a playlist export (since 1.5.0 / 2.3.0), which every video is scaled, padded
/// and timed to, so that differing sizes and frame rates join into one file that plays straight through.
/// </summary>
/// <remarks>
/// <para><b>Size.</b> The largest video's frame (by its number of pixels; the first of equals), made to fit inside
/// 1920×1080, or inside the clip setting <c>MaxVideoHeight</c> by 16:9 when that is lower, keeping its shape, both sides
/// even (x264's 4:2:0). Every video is scaled to fit inside it, keeping its own shape, and padded with black: letterboxed
/// when it is wider, pillarboxed when it is narrower; none is ever cropped or stretched. Without any size known,
/// 1920×1080 (or the cap).</para>
/// <para><b>Frame rate.</b> The rate of most of the running time (each video's rate counted for its length; the
/// higher of equals), as the NTSC fraction for 23.976, 29.97 and 59.94, at most 60; 30 when none is known. Videos of
/// another rate get frames dropped or repeated (ffmpeg's <c>fps</c> filter), as a TV does.</para>
/// </remarks>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="FrameRateNumerator">The frame rate's numerator.</param>
/// <param name="FrameRateDenominator">The frame rate's denominator.</param>
internal sealed record PlaylistExportFormat(int Width, int Height, int FrameRateNumerator, int FrameRateDenominator)
{
    /// <summary>The widest picture: 1920.</summary>
    public const int MaxWidth = 1920;

    /// <summary>The tallest picture: 1080.</summary>
    public const int MaxHeight = 1080;

    /// <summary>The highest frame rate: 60.</summary>
    public const int MaxFrameRate = 60;

    /// <summary>The frame rate without any known: 30.</summary>
    public const int DefaultFrameRate = 30;

    /// <summary>Gets the frame rate as a number.</summary>
    public double FrameRate => (double)FrameRateNumerator / FrameRateDenominator;

    /// <summary>Gets the frame rate as ffmpeg takes it (<c>30000/1001</c>, <c>25</c>).</summary>
    public string FrameRateText => FrameRateDenominator == 1
        ? FrameRateNumerator.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{FrameRateNumerator}/{FrameRateDenominator}");

    /// <summary>
    /// Chooses the format of an export's videos.
    /// </summary>
    /// <param name="items">The videos, in playlist order.</param>
    /// <param name="maxVideoHeight">The clip setting <c>MaxVideoHeight</c> (0: none).</param>
    /// <returns>The format.</returns>
    public static PlaylistExportFormat Choose(IReadOnlyList<PlaylistExportItem> items, int maxVideoHeight)
    {
        ArgumentNullException.ThrowIfNull(items);
        var (width, height) = ChooseSize(items, maxVideoHeight);
        var (numerator, denominator) = ChooseFrameRate(items);
        return new PlaylistExportFormat(width, height, numerator, denominator);
    }

    /// <summary>
    /// Chooses the picture size (<see cref="PlaylistExportFormat"/>'s remarks).
    /// </summary>
    /// <param name="items">The videos.</param>
    /// <param name="maxVideoHeight">The clip setting <c>MaxVideoHeight</c> (0: none).</param>
    /// <returns>The width and height, both even.</returns>
    public static (int Width, int Height) ChooseSize(IReadOnlyList<PlaylistExportItem> items, int maxVideoHeight)
    {
        ArgumentNullException.ThrowIfNull(items);
        var boxHeight = maxVideoHeight > 0 ? Math.Min(MaxHeight, maxVideoHeight) : MaxHeight;
        var boxWidth = boxHeight >= MaxHeight ? MaxWidth : (int)Math.Round(boxHeight * 16.0 / 9);

        PlaylistExportItem? largest = null;
        foreach (var item in items)
        {
            if (item.Width is > 0 && item.Height is > 0
                && (largest is null || (long)item.Width.Value * item.Height.Value > (long)largest.Width!.Value * largest.Height!.Value))
            {
                largest = item;
            }
        }

        if (largest is null)
        {
            return (Even(boxWidth), Even(boxHeight));
        }

        double w = largest.Width!.Value;
        double h = largest.Height!.Value;
        var scale = Math.Min(1, Math.Min(boxWidth / w, boxHeight / h));
        return (Math.Max(2, Even(w * scale)), Math.Max(2, Even(h * scale)));
    }

    /// <summary>
    /// Chooses the frame rate (<see cref="PlaylistExportFormat"/>'s remarks).
    /// </summary>
    /// <param name="items">The videos.</param>
    /// <returns>The frame rate as a fraction.</returns>
    public static (int Numerator, int Denominator) ChooseFrameRate(IReadOnlyList<PlaylistExportItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var seconds = new Dictionary<(int, int), double>();
        foreach (var item in items)
        {
            if (item.FrameRate is not double rate || double.IsNaN(rate) || rate <= 0)
            {
                continue;
            }

            var key = Snap(rate);
            seconds[key] = seconds.GetValueOrDefault(key) + Math.Max(item.Duration.TotalSeconds, 0.001);
        }

        if (seconds.Count == 0)
        {
            return (DefaultFrameRate, 1);
        }

        return seconds
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => (double)pair.Key.Item1 / pair.Key.Item2)
            .First().Key;
    }

    /// <summary>
    /// A frame rate as a fraction ffmpeg takes: the NTSC rates (23.976, 29.97, 59.94) as theirs, a rate within 0.01 of a
    /// whole number as that number, any other to a thousandth, and at most <see cref="MaxFrameRate"/>.
    /// </summary>
    /// <param name="rate">The rate.</param>
    /// <returns>The fraction.</returns>
    public static (int Numerator, int Denominator) Snap(double rate)
    {
        if (rate >= MaxFrameRate - 0.01)
        {
            return (MaxFrameRate, 1);
        }

        foreach (var ntsc in new[] { 24000, 30000, 60000 })
        {
            if (Math.Abs(rate - (ntsc / 1001.0)) < 0.01)
            {
                return (ntsc, 1001);
            }
        }

        var whole = Math.Round(rate);
        if (Math.Abs(rate - whole) < 0.01 && whole >= 1)
        {
            return ((int)whole, 1);
        }

        return ((int)Math.Round(rate * 1000), 1000);
    }

    private static int Even(double value) => (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2;
}
