using System;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>An item whose stored <c>CleanName</c> isn't today's clean form of its <c>Name</c>.</summary>
/// <param name="Id">The item's id.</param>
/// <param name="Name">The item's name.</param>
/// <param name="CleanName">The stored clean name SQL compares with.</param>
internal sealed record StaleName(Guid Id, string? Name, string? CleanName);
