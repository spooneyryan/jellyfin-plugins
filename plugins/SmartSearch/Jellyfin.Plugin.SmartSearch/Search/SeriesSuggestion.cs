using System;

namespace Jellyfin.Plugin.SmartSearch.Search;

/// <summary>A series the picker offers, with the library the user sees it in.</summary>
/// <param name="Series">The series.</param>
/// <param name="LibraryId">The library.</param>
internal sealed record SeriesSuggestion(IndexedSeries Series, Guid LibraryId);
