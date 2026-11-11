using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// One item a unit moves, with its new path and predicted id (<c>Items</c>).
/// </summary>
/// <param name="Role">Its part in the unit.</param>
/// <param name="OldId">Its id now.</param>
/// <param name="Type">The type its id is made from (<c>GetNewItemId(path, type)</c>).</param>
/// <param name="OldPath">Its path now.</param>
/// <param name="NewPath">Its path after the move.</param>
/// <param name="PredictedId">The id Jellyfin will give it there.</param>
internal sealed record UnitItem(UnitItemRole Role, Guid OldId, Type Type, string OldPath, string NewPath, Guid PredictedId);
