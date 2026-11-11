using System;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// An expected failure of a bookmark operation, with a message meant for the user. A thin subclass of the kit's
/// <see cref="PluginException"/>: the kit's <see cref="PluginExceptionFilterAttribute"/> turns it into ProblemDetails.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "Its base, the kit's PluginException, is internal; the filter maps it inside the plugin.")]
internal sealed class BookmarkException : PluginException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkException"/> class.
    /// </summary>
    public BookmarkException()
        : this(PluginErrorKind.Invalid, "The bookmark request is invalid.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public BookmarkException(string message)
        : this(PluginErrorKind.Invalid, message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public BookmarkException(string message, Exception innerException)
        : base(PluginErrorKind.Invalid, message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkException"/> class.
    /// </summary>
    /// <param name="kind">The error category.</param>
    /// <param name="message">The error message.</param>
    public BookmarkException(PluginErrorKind kind, string message)
        : base(kind, message)
    {
    }

    /// <summary>
    /// Creates a "not found" error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The exception.</returns>
    public static new BookmarkException NotFound(string message) => new(PluginErrorKind.NotFound, message);

    /// <summary>
    /// Creates an "invalid request" error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The exception.</returns>
    public static new BookmarkException Invalid(string message) => new(PluginErrorKind.Invalid, message);

    /// <summary>
    /// Creates a "forbidden" error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The exception.</returns>
    public static new BookmarkException Forbidden(string message) => new(PluginErrorKind.Forbidden, message);

    /// <summary>
    /// Creates a "conflict" error (409), such as an edit whose <c>IfUpdatedAt</c> is out of date.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The exception.</returns>
    public static new BookmarkException Conflict(string message) => new(PluginErrorKind.Conflict, message);
}
