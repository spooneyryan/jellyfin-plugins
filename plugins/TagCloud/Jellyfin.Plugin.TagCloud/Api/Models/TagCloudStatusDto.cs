using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// The index's state for the admin (<c>GET /TagCloud/Admin/Status</c>): what was read, how, and how long it
/// took.
/// </summary>
public sealed class TagCloudStatusDto
{
    /// <summary>Gets or sets the state: <c>NotBuilt</c>, <c>Building</c>, <c>Ready</c> or <c>Failed</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets how many titles the build in progress has read.</summary>
    public int Done { get; set; }

    /// <summary>Gets or sets how many titles the build in progress reads.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many titles the index holds.</summary>
    public int Titles { get; set; }

    /// <summary>Gets or sets how many words each source has.</summary>
    public IReadOnlyDictionary<string, int> Words { get; set; } = new Dictionary<string, int>();

    /// <summary>Gets or sets when the index was last built in full, or null.</summary>
    public DateTime? BuiltUtc { get; set; }

    /// <summary>Gets or sets how long the last full build took, in milliseconds.</summary>
    public long BuildMilliseconds { get; set; }

    /// <summary>Gets or sets how long the last full build spent reading the people, in milliseconds.</summary>
    public long PeopleMilliseconds { get; set; }

    /// <summary>Gets or sets how the people were read: <c>Database</c> (one grouped read) or <c>PerItem</c> (the fallback).</summary>
    public string PeoplePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the index generation.</summary>
    public long Generation { get; set; }

    /// <summary>Gets or sets how many titles were patched since the last full build.</summary>
    public int Patched { get; set; }

    /// <summary>Gets or sets the last build's error, or null.</summary>
    public string? LastError { get; set; }
}
