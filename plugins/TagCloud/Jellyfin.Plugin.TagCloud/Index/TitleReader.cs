using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.TagCloud.Api.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// Reads the titles the index holds and their tags, genres and studios: one <c>GetItemList</c> of
/// every movie and series, without a user (server-wide work), whose items carry those three as columns of their row
/// (<c>v10.11.5:Jellyfin.Server.Implementations/Item/BaseItemRepository.cs:837-871</c>).
/// </summary>
internal sealed class TitleReader
{
    /// <summary>The item kinds the index holds: TV counts series, never episodes.</summary>
    public static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Series];

    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleReader"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public TitleReader(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>
    /// Gets the item kinds of a scope.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The kinds.</returns>
    public static BaseItemKind[] KindsOf(TagCloudScope scope) => scope switch
    {
        TagCloudScope.Movies => [BaseItemKind.Movie],
        TagCloudScope.Shows => [BaseItemKind.Series],
        _ => Kinds
    };

    /// <summary>
    /// Gets the query options that read no images and no user data, which nothing here shows.
    /// </summary>
    /// <returns>The options.</returns>
    public static DtoOptions NoImagesOrUserData() => new(false) { EnableImages = false, EnableUserData = false };

    /// <summary>
    /// Gets whether an item is one the index holds: a movie or series of the library, not virtual and not owned by another
    /// item (an extra or a local alternate version, <see cref="LibraryAccess.IsOwned"/>).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it is a title.</returns>
    public static bool IsTitle(BaseItem? item)
        => item is Movie or MediaBrowser.Controller.Entities.TV.Series && !item.IsVirtualItem && !LibraryAccess.IsOwned(item);

    /// <summary>
    /// Gets a title's record.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The record.</returns>
    public static TitleRecord ToRecord(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var name = item.Name ?? string.Empty;
        return new TitleRecord(item.Id, name, string.IsNullOrEmpty(item.SortName) ? name : item.SortName, item.ProductionYear, item is Movie ? BaseItemKind.Movie : BaseItemKind.Series);
    }

    /// <summary>
    /// Gets the words a title carries in one of the sources its own row holds.
    /// </summary>
    /// <param name="item">The title.</param>
    /// <param name="source">Tags, Genres or Studios.</param>
    /// <returns>The words; none for a people source.</returns>
    public static IReadOnlyList<string> WordsOf(BaseItem item, TagCloudSource source)
    {
        ArgumentNullException.ThrowIfNull(item);
        return source switch
        {
            TagCloudSource.Tags => item.Tags ?? [],
            TagCloudSource.Genres => item.Genres ?? [],
            TagCloudSource.Studios => item.Studios ?? [],
            _ => []
        };
    }

    /// <summary>
    /// Reads every title of every library.
    /// </summary>
    /// <returns>The titles.</returns>
    public IReadOnlyList<BaseItem> ReadAll()
        => _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = Kinds,
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = NoImagesOrUserData()
        }).Where(IsTitle).ToList();

    /// <summary>
    /// Reads one title again, for a patch.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <returns>The title, or null when it is gone or isn't one.</returns>
    public BaseItem? Read(Guid id)
    {
        var item = id == Guid.Empty ? null : _library.GetItemById(id);
        return IsTitle(item) ? item : null;
    }
}
