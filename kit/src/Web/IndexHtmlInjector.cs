using System;
using System.Text;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// The insert-only <c>index.html</c> transform: the script tag before the last <c>&lt;/body&gt;</c> and
/// the optional head fragment before the first <c>&lt;/head&gt;</c>, each only when its marker attribute is absent,
/// both case-insensitive. Nothing is ever removed or rewritten.
/// </summary>
internal static class IndexHtmlInjector
{
    private const string BodyEnd = "</body>";
    private const string HeadEnd = "</head>";

    // Invalid UTF-8 throws instead of being replaced, so a document we can't read is never re-encoded (the caller serves
    // the original bytes). No preamble is emitted: a BOM in the input is kept as the character U+FEFF.
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Inserts the script tag and the head fragment.
    /// </summary>
    /// <param name="input">The UTF-8 response body.</param>
    /// <param name="bodyMarker">The script tag's marker attribute, for example <c>data-jfbm-plugin</c>.</param>
    /// <param name="bodyTag">The complete script tag.</param>
    /// <param name="headMarker">The head fragment's marker attribute, for example <c>data-jfbm-head</c>.</param>
    /// <param name="headFragment">The head fragment; <c>null</c> or empty for none.</param>
    /// <param name="output">The new body, or <paramref name="input"/> itself when nothing changed.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="DecoderFallbackException">The input isn't valid UTF-8.</exception>
    public static IndexHtmlInsertResult Insert(byte[] input, string bodyMarker, string bodyTag, string headMarker, string? headFragment, out byte[] output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(bodyMarker);
        ArgumentException.ThrowIfNullOrEmpty(bodyTag);
        ArgumentException.ThrowIfNullOrEmpty(headMarker);

        output = input;
        var html = _strictUtf8.GetString(input);
        var bodyEnd = html.LastIndexOf(BodyEnd, StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0)
        {
            return new(IndexHtmlBodyOutcome.NoBodyTag, IndexHtmlHeadOutcome.None);
        }

        var body = IndexHtmlBodyOutcome.AlreadyPresent;
        if (!html.Contains(bodyMarker, StringComparison.OrdinalIgnoreCase))
        {
            html = html.Insert(bodyEnd, bodyTag);
            body = IndexHtmlBodyOutcome.Inserted;
        }

        var head = IndexHtmlHeadOutcome.None;
        if (!string.IsNullOrEmpty(headFragment))
        {
            if (!headFragment.Contains(headMarker, StringComparison.OrdinalIgnoreCase))
            {
                head = IndexHtmlHeadOutcome.MissingMarker;
            }
            else if (html.Contains(headMarker, StringComparison.OrdinalIgnoreCase))
            {
                head = IndexHtmlHeadOutcome.AlreadyPresent;
            }
            else
            {
                var headEnd = html.IndexOf(HeadEnd, StringComparison.OrdinalIgnoreCase);
                if (headEnd < 0)
                {
                    head = IndexHtmlHeadOutcome.NoHeadTag;
                }
                else
                {
                    html = html.Insert(headEnd, headFragment);
                    head = IndexHtmlHeadOutcome.Inserted;
                }
            }
        }

        var result = new IndexHtmlInsertResult(body, head);
        if (result.Changed)
        {
            output = _strictUtf8.GetBytes(html);
        }

        return result;
    }
}
