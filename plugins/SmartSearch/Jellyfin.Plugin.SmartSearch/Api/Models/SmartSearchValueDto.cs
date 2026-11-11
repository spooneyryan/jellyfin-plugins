namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>A value of a field, with how many of the items the signed-in user can see carry it.</summary>
public sealed class SmartSearchValueDto
{
    /// <summary>Gets or sets the value, as stored.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets how many items the user can see carry it, in the libraries asked for.</summary>
    public int Count { get; set; }
}
