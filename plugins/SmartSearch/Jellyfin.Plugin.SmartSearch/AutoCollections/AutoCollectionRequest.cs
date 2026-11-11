using System;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>A requested run of one definition.</summary>
/// <param name="DefinitionId">The definition's id.</param>
/// <param name="Reason">What starts it.</param>
/// <param name="Kind">What it does.</param>
internal sealed record AutoCollectionRequest(Guid DefinitionId, SmartSearchAutoCollectionRunReason Reason, AutoCollectionRequestKind Kind = AutoCollectionRequestKind.Run);
