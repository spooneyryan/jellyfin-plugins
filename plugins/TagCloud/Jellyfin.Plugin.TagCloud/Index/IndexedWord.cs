using System.Collections.Generic;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// One word of a source in an index snapshot: its key, the text shown, and the titles carrying it.
/// </summary>
internal sealed class IndexedWord
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IndexedWord"/> class.
    /// </summary>
    /// <param name="key">The key (the text in lower case).</param>
    /// <param name="text">The text shown: the casing most titles use.</param>
    /// <param name="ordinals">The ordinals of the titles carrying it, ascending.</param>
    public IndexedWord(string key, string text, IReadOnlyList<int> ordinals)
    {
        Key = key;
        Text = text;
        Ordinals = ordinals;
    }

    /// <summary>Gets the key.</summary>
    public string Key { get; }

    /// <summary>Gets the text shown.</summary>
    public string Text { get; }

    /// <summary>Gets the ordinals of the titles carrying the word, ascending.</summary>
    public IReadOnlyList<int> Ordinals { get; }
}
