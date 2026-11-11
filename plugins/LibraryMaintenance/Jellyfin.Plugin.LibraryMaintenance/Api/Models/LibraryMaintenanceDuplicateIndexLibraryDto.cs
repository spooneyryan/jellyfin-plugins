using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A library the movie index read (<c>GET /Index</c>).
/// </summary>
public sealed class LibraryMaintenanceDuplicateIndexLibraryDto
{
    /// <summary>
    /// Gets or sets the library id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's kind: <c>movies</c>, or <c>mixed</c> for a library without a content type.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movies indexed from it; a movie in two libraries counts in both.
    /// </summary>
    public int Movies { get; set; }
}
