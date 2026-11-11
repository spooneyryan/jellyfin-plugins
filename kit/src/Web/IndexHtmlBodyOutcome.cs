namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// What <see cref="IndexHtmlInjector.Insert"/> did with the script tag.
/// </summary>
internal enum IndexHtmlBodyOutcome
{
    /// <summary>The tag was inserted before the last <c>&lt;/body&gt;</c>.</summary>
    Inserted,

    /// <summary>The marker attribute was already in the document, so the tag wasn't inserted again.</summary>
    AlreadyPresent,

    /// <summary>The document has no <c>&lt;/body&gt;</c>; it is returned unchanged, head fragment included.</summary>
    NoBodyTag,
}
