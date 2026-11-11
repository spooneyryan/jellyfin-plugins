using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Where the server put each user page at startup (<see cref="PluginPagesRegistrationService"/>), for the Diagnostics tab
/// (<c>Pages:[{Key, Host}]</c>) and the log. The browser decides the host it uses on its own.
/// </summary>
internal sealed class KitPageHosts
{
    private readonly KitOptions _options;
    private readonly ConcurrentDictionary<string, KitPageHostEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="KitPageHosts"/> class.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    public KitPageHosts(KitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Gets one entry per page, in the order of <c>KitOptions.Pages</c>; <see cref="KitPageHost.NotStarted"/> until the
    /// registration service ran.
    /// </summary>
    public IReadOnlyList<KitPageHostEntry> Entries => _options.Pages
        .Select(p => _entries.TryGetValue(p.Key, out var entry)
            ? entry
            : new KitPageHostEntry(p.Key, p.PluginPagesId(_options.RoutePrefix), KitPageHost.NotStarted, "The registration service hasn't run."))
        .ToList();

    /// <summary>
    /// Records a page's host.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Set(KitPageHostEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.Key] = entry;
    }
}
