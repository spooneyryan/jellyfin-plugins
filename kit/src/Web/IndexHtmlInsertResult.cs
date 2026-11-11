namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// The result of <see cref="IndexHtmlInjector.Insert"/>.
/// </summary>
/// <param name="Body">What happened to the script tag.</param>
/// <param name="Head">What happened to the head fragment.</param>
internal readonly record struct IndexHtmlInsertResult(IndexHtmlBodyOutcome Body, IndexHtmlHeadOutcome Head)
{
    /// <summary>
    /// Gets a value indicating whether the document changed.
    /// </summary>
    public bool Changed => Body == IndexHtmlBodyOutcome.Inserted || Head == IndexHtmlHeadOutcome.Inserted;
}
