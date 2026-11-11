using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// A cloud's words as the caller sees them, heaviest first.
/// </summary>
public sealed class TagCloudWordsDto
{
    /// <summary>Gets or sets the words, at most the limit asked for, heaviest first, ties by text.</summary>
    public IReadOnlyList<TagCloudWordDto> Words { get; set; } = [];

    /// <summary>Gets or sets how many words there are in all (the summary's "8,244 tags").</summary>
    public int WordCount { get; set; }

    /// <summary>
    /// Gets or sets how many titles were counted: the caller's titles in scope and libraries, or with narrowing words the
    /// ones carrying every one of them.
    /// </summary>
    public int TitleCount { get; set; }

    /// <summary>Gets or sets the narrowing keys applied: those asked for that the caller's titles carry.</summary>
    public IReadOnlyList<string> Narrow { get; set; } = [];

    /// <summary>Gets or sets the index generation the counts come from; it changes whenever the library does.</summary>
    public long Generation { get; set; }

    /// <summary>Gets or sets when the index was last built in full, or null.</summary>
    public DateTime? IndexedUtc { get; set; }
}
