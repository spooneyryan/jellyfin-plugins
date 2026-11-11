namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// What <see cref="IndexHtmlInjector.Insert"/> did with the head fragment.
/// </summary>
internal enum IndexHtmlHeadOutcome
{
    /// <summary>Nothing: there was no fragment (null or empty), or the document has no <c>&lt;/body&gt;</c>.</summary>
    None,

    /// <summary>The fragment was inserted before the first <c>&lt;/head&gt;</c>.</summary>
    Inserted,

    /// <summary>The head marker was already in the document, so the fragment wasn't inserted again.</summary>
    AlreadyPresent,

    /// <summary>The document has no <c>&lt;/head&gt;</c>; only the script tag was inserted.</summary>
    NoHeadTag,

    /// <summary>The fragment doesn't carry the head marker, so it can't be kept idempotent and wasn't inserted.</summary>
    MissingMarker,
}
