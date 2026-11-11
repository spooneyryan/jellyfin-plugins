using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// The folder a loose file is wrapped into: <c>Title (Year)</c>, the year
/// once (<see cref="MoveTitle"/>), with <c>": "</c> as <c>" - "</c>, then Jellyfin's <c>IFileSystem.GetValidFilename</c>
/// (the remaining invalid characters become spaces), whitespace collapsed, leading spaces and trailing dots and spaces
/// trimmed. <c>PathRules</c> still checks the result, and whatever the admin types instead.
/// </summary>
internal static partial class WrapName
{
    /// <summary>
    /// Cleans a title and year into a folder name.
    /// </summary>
    /// <param name="title">The item's name.</param>
    /// <param name="year">Its production year, or null.</param>
    /// <param name="validFilename">Jellyfin's <c>IFileSystem.GetValidFilename</c>.</param>
    /// <returns>The name; empty when nothing is left.</returns>
    public static string Clean(string? title, int? year, Func<string, string> validFilename)
    {
        ArgumentNullException.ThrowIfNull(validFilename);
        // The year once: a title Jellyfin didn't parse keeps it ("Folder Movie (2008)").
        var name = MoveTitle.WithYear((title ?? string.Empty).Replace(": ", " - ", StringComparison.Ordinal), year);
        name = Whitespace().Replace(validFilename(name), " ");
        return name.TrimStart(' ').TrimEnd('.', ' ');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
