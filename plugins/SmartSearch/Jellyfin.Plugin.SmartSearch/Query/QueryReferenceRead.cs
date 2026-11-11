using System;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>A reference value as <see cref="QueryReference.Read"/> read it.</summary>
/// <param name="Valid">Whether it is a reference.</param>
/// <param name="Id">The id.</param>
/// <param name="Name">The name, trimmed, or <c>null</c>.</param>
/// <param name="Path">Where the problem is, relative to the value (<c>""</c>, <c>".Id"</c>, <c>".Name"</c> or the stray property).</param>
/// <param name="Message">What is wrong.</param>
internal readonly record struct QueryReferenceRead(bool Valid, Guid Id, string? Name, string Path, string Message)
{
    /// <summary>Creates a read that failed.</summary>
    /// <param name="path">Where the problem is.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The read.</returns>
    public static QueryReferenceRead Problem(string path, string message) => new(false, Guid.Empty, null, path, message);
}
