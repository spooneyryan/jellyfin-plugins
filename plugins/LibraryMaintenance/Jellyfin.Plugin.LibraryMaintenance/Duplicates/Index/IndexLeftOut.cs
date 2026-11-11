using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// A movie a build left out (no name, or under an excluded folder), kept so a caller's <c>GET /Index</c> counts only the
/// ones they can see: in their libraries, and not hidden from them by a parental rating or tags (the 1.0 verifier).
/// </summary>
/// <param name="Id">The movie's id.</param>
/// <param name="LibraryIds">The libraries it is in.</param>
internal sealed record IndexLeftOut(Guid Id, IReadOnlyList<Guid> LibraryIds);
