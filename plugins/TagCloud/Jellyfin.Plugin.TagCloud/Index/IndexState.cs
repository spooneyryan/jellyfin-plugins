using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TagCloud.Api.Models;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// The index while it is built or patched: titles by ordinal, and for each source the words with the titles
/// carrying them and the casings they're written in. Not thread-safe: <see cref="TagCloudIndex"/> holds its lock around
/// every use. <see cref="ToSnapshot"/> publishes what readers see.
/// </summary>
/// <remarks>
/// Words are keyed by their trimmed text in lower case, so "Horror" and "horror" are one word (as
/// <c>StringComparer.OrdinalIgnoreCase</c> compares them); the word shows the casing most of its titles use. A removed title
/// keeps its ordinal as a gap until the next full build, so ordinals never move under a cached visibility mask.
/// </remarks>
internal sealed class IndexState
{
    private readonly List<TitleRecord?> _titles = [];
    private readonly Dictionary<Guid, int> _ordinals = [];
    private readonly Dictionary<TagCloudSource, Dictionary<string, WordState>> _words = [];
    private readonly Dictionary<int, Dictionary<TagCloudSource, string[]>> _keysByTitle = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexState"/> class.
    /// </summary>
    /// <param name="sources">The sources indexed; words of others are ignored.</param>
    public IndexState(IEnumerable<TagCloudSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources.Distinct())
        {
            _words[source] = new Dictionary<string, WordState>(StringComparer.Ordinal);
        }
    }

    /// <summary>Gets the sources indexed.</summary>
    public IReadOnlyCollection<TagCloudSource> Sources => _words.Keys;

    /// <summary>Gets how many titles the state holds.</summary>
    public int Count => _ordinals.Count;

    /// <summary>
    /// Gets the key a word is counted under: its trimmed text in lower case, or null for none.
    /// </summary>
    /// <param name="text">The word.</param>
    /// <returns>The key, or null for an empty word.</returns>
    public static string? KeyOf(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Adds a title, or replaces what is known of one already there (its ordinal stays).
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>Its ordinal.</returns>
    public int Upsert(TitleRecord title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (_ordinals.TryGetValue(title.Id, out var ordinal))
        {
            _titles[ordinal] = title;
            return ordinal;
        }

        ordinal = _titles.Count;
        _titles.Add(title);
        _ordinals[title.Id] = ordinal;
        return ordinal;
    }

    /// <summary>
    /// Sets the words a title carries in one source, replacing the ones it had. Ignored for a source not indexed.
    /// </summary>
    /// <param name="ordinal">The title's ordinal.</param>
    /// <param name="source">The source.</param>
    /// <param name="texts">The words, in any case, duplicates and blanks allowed.</param>
    public void SetWords(int ordinal, TagCloudSource source, IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (!_words.TryGetValue(source, out var words))
        {
            return;
        }

        ClearWords(ordinal, source, words);
        var keys = new List<string>();
        foreach (var text in texts)
        {
            var key = KeyOf(text);
            if (key is null || keys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            keys.Add(key);
            if (!words.TryGetValue(key, out var word))
            {
                words[key] = word = new WordState();
            }

            word.Add(ordinal, text!.Trim());
        }

        if (keys.Count > 0)
        {
            if (!_keysByTitle.TryGetValue(ordinal, out var bySource))
            {
                _keysByTitle[ordinal] = bySource = [];
            }

            bySource[source] = [.. keys];
        }
        else if (_keysByTitle.TryGetValue(ordinal, out var bySource))
        {
            bySource.Remove(source);
        }
    }

    /// <summary>
    /// Removes a title and every word it carried; its ordinal stays a gap.
    /// </summary>
    /// <param name="id">The title's item id.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(Guid id)
    {
        if (!_ordinals.Remove(id, out var ordinal))
        {
            return false;
        }

        foreach (var (source, words) in _words)
        {
            ClearWords(ordinal, source, words);
        }

        _keysByTitle.Remove(ordinal);
        _titles[ordinal] = null;
        return true;
    }

    /// <summary>
    /// Gets whether the state holds a title.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <returns>Whether it does.</returns>
    public bool Contains(Guid id) => _ordinals.ContainsKey(id);

    /// <summary>
    /// Publishes the state as an immutable snapshot.
    /// </summary>
    /// <param name="generation">The snapshot's generation.</param>
    /// <param name="builtUtc">When the index was last built in full.</param>
    /// <returns>The snapshot.</returns>
    public TagCloudIndexSnapshot ToSnapshot(long generation, DateTime? builtUtc)
    {
        var words = _words.ToDictionary(
            source => source.Key,
            source => (IReadOnlyDictionary<string, IndexedWord>)source.Value
                .Where(word => word.Value.Titles.Count > 0)
                .ToDictionary(word => word.Key, word => new IndexedWord(word.Key, word.Value.Text, word.Value.SortedTitles()), StringComparer.Ordinal));
        return new TagCloudIndexSnapshot(generation, builtUtc, _titles.ToArray(), new Dictionary<Guid, int>(_ordinals), words);
    }

    private void ClearWords(int ordinal, TagCloudSource source, Dictionary<string, WordState> words)
    {
        if (!_keysByTitle.TryGetValue(ordinal, out var bySource) || !bySource.TryGetValue(source, out var keys))
        {
            return;
        }

        foreach (var key in keys)
        {
            if (words.TryGetValue(key, out var word) && word.Remove(ordinal) && word.Titles.Count == 0)
            {
                words.Remove(key);
            }
        }

        bySource.Remove(source);
    }

    /// <summary>One word while the index is built: the titles carrying it and the casings each wrote it in.</summary>
    private sealed class WordState
    {
        private readonly Dictionary<int, string> _casings = [];

        public IReadOnlyCollection<int> Titles => _casings.Keys;

        /// <summary>Gets the casing most titles use; ties go to the first in ordinal order.</summary>
        public string Text => _casings
            .GroupBy(c => c.Value, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(c => c.Key))
            .First().Key;

        public void Add(int ordinal, string text) => _casings[ordinal] = text;

        public bool Remove(int ordinal) => _casings.Remove(ordinal);

        public int[] SortedTitles()
        {
            var titles = _casings.Keys.ToArray();
            Array.Sort(titles);
            return titles;
        }
    }
}
