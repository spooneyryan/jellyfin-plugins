using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// The indexed movies a caller can't see, and the ones the build left out that they can't see (<see cref="ItemVisibility.Hidden"/>).
/// </summary>
/// <param name="Ids">The movies' ids.</param>
/// <param name="Key">The set's digest, part of a cached result's key.</param>
internal sealed record HiddenItems(IReadOnlySet<Guid> Ids, string Key);
