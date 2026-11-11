using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// A page registration for Plugin Pages: the four fields of its <c>PluginPage</c>. Serialised in camel
/// case for Plugin Pages 3.x's <c>PluginInterface.RegisterPage(JObject)</c>.
/// </summary>
/// <param name="Id">Unique Id, <c>Jellyfin.Plugin.&lt;Name&gt;.&lt;Page&gt;Page</c>; Plugin Pages ignores duplicates and
/// renders it as the link's <c>data-itemid</c>.</param>
/// <param name="Url">Root-relative shell URL without BaseUrl, <c>&amp;</c>, <c>?</c> or <c>#</c>.</param>
/// <param name="DisplayText">Plain-text link label (Plugin Pages 2.4.7 puts it into <c>innerHTML</c> unescaped).</param>
/// <param name="Icon">Material icon class.</param>
internal sealed partial record PluginPageRegistration(string Id, string Url, string DisplayText, string Icon)
{
    /// <summary>
    /// Checks the fields against what Plugin Pages 2.4.7 handles safely: the link is built as
    /// <c>href="#/userpluginsettings.html?pageUrl=${Url}"</c> without encoding and read back with
    /// <c>URLSearchParams</c>, the host fetches any absolute <c>pageUrl</c> with the user's token, and the label and icon
    /// are interpolated into <c>innerHTML</c>.
    /// </summary>
    /// <returns>The problems; empty when the registration is safe.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Id is null || !IdPattern().IsMatch(Id))
        {
            problems.Add($"Id '{Id}' must look like Jellyfin.Plugin.<Name>.<Page>Page.");
        }

        // Root-relative path only: never "//host/..." or "http://...", which Plugin Pages would fetch with the user's token.
        if (Url is null || !UrlPattern().IsMatch(Url))
        {
            problems.Add($"Url '{Url}' must be a root-relative path such as /Bookmarks/Pages/clips, without '&', '?', '#' or a host.");
        }

        if (DisplayText is null || !DisplayTextPattern().IsMatch(DisplayText))
        {
            problems.Add($"DisplayText '{DisplayText}' must be plain text of 1 to 60 characters without < > & \" ' or `.");
        }

        if (Icon is null || !IconPattern().IsMatch(Icon))
        {
            problems.Add($"Icon '{Icon}' must be a Material icon class such as video_library.");
        }

        return problems;
    }

    [GeneratedRegex("^Jellyfin\\.Plugin\\.[A-Z][A-Za-z0-9]*\\.[A-Z][A-Za-z0-9]*Page$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^/[A-Za-z0-9][A-Za-z0-9_-]*(/[A-Za-z0-9][A-Za-z0-9_.-]*)*$")]
    private static partial Regex UrlPattern();

    [GeneratedRegex("^[^<>&\"'`\\p{Cc}]{1,60}$")]
    private static partial Regex DisplayTextPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex IconPattern();
}
