using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// A library the index read (<c>movies</c> and mixed libraries).
/// </summary>
/// <param name="Id">The library id.</param>
/// <param name="Name">The library name.</param>
/// <param name="Kind"><c>movies</c>, or <c>mixed</c> for a library without a content type.</param>
/// <param name="Movies">The movies indexed from it (a movie in two libraries counts in both).</param>
internal sealed record IndexLibrary(Guid Id, string Name, string Kind, int Movies);
