using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>The saved queries a write replaced, removed or added.</summary>
internal sealed class SavedQueriesChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueriesChangedEventArgs"/> class.
    /// </summary>
    /// <param name="queryIds">The queries' ids.</param>
    public SavedQueriesChangedEventArgs(IReadOnlyList<Guid> queryIds)
    {
        QueryIds = queryIds;
    }

    /// <summary>Gets the ids of the queries replaced, removed or added.</summary>
    public IReadOnlyList<Guid> QueryIds { get; }
}
