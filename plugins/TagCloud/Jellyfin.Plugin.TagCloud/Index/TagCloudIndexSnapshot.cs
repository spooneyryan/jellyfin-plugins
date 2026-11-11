using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TagCloud.Api.Models;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// An immutable view of the index: every title by ordinal, and for each source its words with the
/// ordinals of the titles carrying them. Requests read the snapshot they started with; a rebuild or a patch publishes a
/// new one.
/// </summary>
internal sealed class TagCloudIndexSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudIndexSnapshot"/> class.
    /// </summary>
    /// <param name="generation">The generation; every change publishes a higher one.</param>
    /// <param name="builtUtc">When the index was last built in full.</param>
    /// <param name="titles">The titles by ordinal; null where a title was removed since.</param>
    /// <param name="ordinals">Each title's ordinal by item id.</param>
    /// <param name="words">Each indexed source's words by key.</param>
    public TagCloudIndexSnapshot(
        long generation,
        DateTime? builtUtc,
        IReadOnlyList<TitleRecord?> titles,
        IReadOnlyDictionary<Guid, int> ordinals,
        IReadOnlyDictionary<TagCloudSource, IReadOnlyDictionary<string, IndexedWord>> words)
    {
        Generation = generation;
        BuiltUtc = builtUtc;
        Titles = titles;
        Ordinals = ordinals;
        Words = words;
    }

    /// <summary>Gets the generation.</summary>
    public long Generation { get; }

    /// <summary>Gets when the index was last built in full.</summary>
    public DateTime? BuiltUtc { get; }

    /// <summary>Gets the titles by ordinal; null where a title was removed since the last full build.</summary>
    public IReadOnlyList<TitleRecord?> Titles { get; }

    /// <summary>Gets each title's ordinal by item id.</summary>
    public IReadOnlyDictionary<Guid, int> Ordinals { get; }

    /// <summary>Gets each indexed source's words by key. A source turned off when the index was built isn't there.</summary>
    public IReadOnlyDictionary<TagCloudSource, IReadOnlyDictionary<string, IndexedWord>> Words { get; }

    /// <summary>Gets how many titles the index holds.</summary>
    public int TitleCount => Ordinals.Count;

    /// <summary>
    /// Gets how many words each indexed source has.
    /// </summary>
    /// <returns>The counts by source name.</returns>
    public IReadOnlyDictionary<string, int> CountWords()
        => Words.OrderBy(w => w.Key).ToDictionary(w => w.Key.ToString(), w => w.Value.Count, StringComparer.Ordinal);
}
