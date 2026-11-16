using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TagCloud.Api.Models;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// The clouds as one user sees them: every count is over only the titles that user can see.
/// </summary>
public interface ITagCloudQuery
{
    /// <summary>
    /// Gets what this server's Tag Cloud offers.
    /// </summary>
    /// <param name="isAdmin">Whether the caller is an administrator.</param>
    /// <returns>The capabilities.</returns>
    TagCloudCapabilitiesDto GetCapabilities(bool isAdmin);

    /// <summary>
    /// Gets the libraries the user can open that hold titles in the scope, by name.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The libraries.</returns>
    IReadOnlyList<TagCloudLibraryDto> GetLibraries(User user, TagCloudScope scope);

    /// <summary>
    /// Gets a cloud's words as the user sees them.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="source">What to count.</param>
    /// <param name="scope">Which titles.</param>
    /// <param name="libraryIds">The libraries, or null or empty for every library the user can open.</param>
    /// <param name="narrow">Word keys the titles must all carry, or null.</param>
    /// <param name="limit">The most words, or null for 100.</param>
    /// <returns>The words.</returns>
    TagCloudWordsDto GetWords(User user, TagCloudSource source, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, IReadOnlyList<string>? narrow, int? limit);

    /// <summary>
    /// Gets a page of one word's titles as the user sees them.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="source">What the word is.</param>
    /// <param name="scope">Which titles.</param>
    /// <param name="libraryIds">The libraries, or null or empty for every library the user can open.</param>
    /// <param name="narrow">Word keys the titles must also carry, or null.</param>
    /// <param name="key">The word's key.</param>
    /// <param name="order">How to order them.</param>
    /// <param name="seed">The shuffle's seed for <see cref="TagCloudTitleOrder.Random"/>.</param>
    /// <param name="startIndex">How many to skip (0 or more).</param>
    /// <param name="limit">The most to return (1–200).</param>
    /// <param name="sort">What to sort by before the page is cut, or null for <paramref name="order"/>; never with
    /// <see cref="TagCloudTitleOrder.Random"/>.</param>
    /// <param name="descending">Whether the sort runs the other way round.</param>
    /// <param name="query">Text the name or the original title must hold, ignoring case and accents (at most 100
    /// characters), or null.</param>
    /// <param name="minResolution">The least sharp movie kept.</param>
    /// <param name="unwatched">Whether only the titles the user hasn't watched are kept.</param>
    /// <returns>The page.</returns>
    TagCloudTitlesDto GetTitles(User user, TagCloudSource source, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, IReadOnlyList<string>? narrow, string key, TagCloudTitleOrder order, int seed, int startIndex, int limit, TagCloudTitleSort? sort = null, bool descending = false, string? query = null, TagCloudMinResolution minResolution = TagCloudMinResolution.Any, bool unwatched = false);
}
