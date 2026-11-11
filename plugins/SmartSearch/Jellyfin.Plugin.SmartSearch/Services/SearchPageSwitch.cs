using System;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>The setting <c>EnableSearchPage</c>, read on each request, for the checks that run before an action.</summary>
internal sealed class SearchPageSwitch
{
    private readonly Func<bool> _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchPageSwitch"/> class.
    /// </summary>
    /// <param name="enabled">Reads the setting.</param>
    public SearchPageSwitch(Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
    }

    /// <summary>Gets a value indicating whether the search page is on.</summary>
    public bool Enabled => _enabled();
}
