using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Operations;

/// <summary>
/// An item in <see cref="LibraryOperationGate"/>'s busy set.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="Owner">The feature holding it.</param>
/// <param name="OperationId">The job or operation holding it.</param>
internal sealed record BusyItem(Guid ItemId, OperationOwner Owner, string OperationId);
