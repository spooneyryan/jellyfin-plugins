namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// What a cloud counts: the words of the titles in scope, each sized by how many titles carry it.
/// </summary>
public enum TagCloudSource
{
    /// <summary>The titles' tags.</summary>
    Tags,

    /// <summary>The titles' genres.</summary>
    Genres,

    /// <summary>The titles' studios, by name.</summary>
    Studios,

    /// <summary>The people credited as actors.</summary>
    Actors,

    /// <summary>The people credited as directors.</summary>
    Directors,

    /// <summary>The people credited as writers.</summary>
    Writers
}
