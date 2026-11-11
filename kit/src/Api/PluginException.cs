using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// An expected failure with a message meant for the user. <see cref="PluginExceptionFilterAttribute"/> turns it into a
/// ProblemDetails response; without it Jellyfin's exception middleware would log it as a server error and replace the
/// message with "Error processing request." Plugins may derive a thin subclass of their own.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "Kit types are internal; the controller-level filter maps it before it leaves the plugin.")]
internal class PluginException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginException"/> class for an invalid request.
    /// </summary>
    public PluginException()
        : this(PluginErrorKind.Invalid, "The request is invalid.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginException"/> class for an invalid request.
    /// </summary>
    /// <param name="message">The message shown to the user.</param>
    public PluginException(string message)
        : this(PluginErrorKind.Invalid, message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginException"/> class for an invalid request.
    /// </summary>
    /// <param name="message">The message shown to the user.</param>
    /// <param name="innerException">The underlying error.</param>
    public PluginException(string message, Exception? innerException)
        : this(PluginErrorKind.Invalid, message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginException"/> class.
    /// </summary>
    /// <param name="kind">The error category, which decides the status code.</param>
    /// <param name="message">The message shown to the user.</param>
    public PluginException(PluginErrorKind kind, string message)
        : this(kind, message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginException"/> class.
    /// </summary>
    /// <param name="kind">The error category, which decides the status code.</param>
    /// <param name="message">The message shown to the user.</param>
    /// <param name="innerException">The underlying error.</param>
    public PluginException(PluginErrorKind kind, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>
    /// Gets the error category.
    /// </summary>
    public PluginErrorKind Kind { get; }

    /// <summary>
    /// Gets the message a caller who isn't an administrator is shown instead of <see cref="Exception.Message"/>, or
    /// <c>null</c> for the same one: when the message sends the reader somewhere only an admin can go (the plugin's settings
    /// page), a user is told to ask the admin instead (the JSON store's 503 while its data is read-only).
    /// <see cref="PluginExceptionFilterAttribute"/> picks it by the request's role, for wording only.
    /// </summary>
    public string? NonAdminMessage { get; init; }

    /// <summary>
    /// Gets a stable code a client tells this error apart from others of its kind by, never by its words (a 409 that
    /// offers a reload beside one that doesn't), or <c>null</c> for none: the ProblemDetails' <c>code</c> extension, and
    /// <c>KitApiError.code</c> on the page. Never <c>ParentalControl</c>.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Gets more members of the ProblemDetails body, by name, or <c>null</c> for none: what a client needs beyond the code to
    /// act on the error (the problems of a request, one per field, or a count to word a hint with). Names are camelCase like
    /// <c>code</c>; <c>code</c> and the standard members can't be replaced. Never user data the caller may not see.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

    /// <summary>Creates a 400 error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException Invalid(string message) => new(PluginErrorKind.Invalid, message);

    /// <summary>Creates a 403 error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException Forbidden(string message) => new(PluginErrorKind.Forbidden, message);

    /// <summary>Creates the 403 "turned off" error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException FeatureDisabled(string message) => new(PluginErrorKind.FeatureDisabled, message);

    /// <summary>Creates the 403 "locked" error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException Locked(string message) => new(PluginErrorKind.Locked, message);

    /// <summary>Creates a 404 error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException NotFound(string message) => new(PluginErrorKind.NotFound, message);

    /// <summary>Creates a 409 error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException Conflict(string message) => new(PluginErrorKind.Conflict, message);

    /// <summary>Creates a 413 error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException PayloadTooLarge(string message) => new(PluginErrorKind.PayloadTooLarge, message);

    /// <summary>Creates a 503 error.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <returns>The exception.</returns>
    public static PluginException Unavailable(string message) => new(PluginErrorKind.Unavailable, message);
}
