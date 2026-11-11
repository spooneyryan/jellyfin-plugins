namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The defaults and limits of the search and lookup requests.</summary>
internal static class SmartSearchRequestLimits
{
    /// <summary>The page size of a search when none is given.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page of a search.</summary>
    public const int MaxLimit = 200;

    /// <summary>The largest request body: a 64 KB query and room for the page's fields around it.</summary>
    public const int MaxBodyBytes = (64 * 1024) + 4096;

    /// <summary>
    /// The largest body that saves a query: a 64 KB query, a name of 100 and a description of 500 characters (each at most
    /// 6 bytes as a JSON escape), and room for the other fields.
    /// </summary>
    public const int MaxSaveBodyBytes = (64 * 1024) + 8192;

    /// <summary>The number of suggestions a lookup returns when none is given.</summary>
    public const int DefaultLookupLimit = 20;

    /// <summary>The most suggestions a lookup returns.</summary>
    public const int MaxLookupLimit = 100;

    /// <summary>The shortest text the series picker searches for.</summary>
    public const int MinSeriesPrefix = 2;

    /// <summary>The longest text a lookup searches for.</summary>
    public const int MaxPrefix = 200;
}
