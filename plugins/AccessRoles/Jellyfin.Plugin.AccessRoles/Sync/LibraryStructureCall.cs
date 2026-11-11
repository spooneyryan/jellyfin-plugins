using System;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// A library structure call the filter saw succeed, with what it recorded before the action ran.
/// </summary>
/// <param name="Kind">The call.</param>
/// <param name="Name">The library's name the call gave (<c>name</c>), if any.</param>
/// <param name="NewName">A rename's new name (<c>newName</c>).</param>
/// <param name="LibraryId">The library's id before the call, from its name (<c>GetNewItemId</c>), for a removal or a rename.</param>
internal sealed record LibraryStructureCall(LibraryStructureCallKind Kind, string? Name, string? NewName, Guid? LibraryId);
