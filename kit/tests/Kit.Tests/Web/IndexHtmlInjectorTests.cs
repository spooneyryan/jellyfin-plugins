using System;
using System.Text;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The insert-only transform of the injection, with the optional head fragment.
/// </summary>
public sealed class IndexHtmlInjectorTests
{
    private const string BodyMarker = "data-jfkt-plugin";
    private const string HeadMarker = "data-jfkt-head";
    private const string Tag = "<script data-jfkt-plugin=\"1.0.0.0\" src=\"../Fx/ClientScript?v=1.0.0.0-0123abcd\" defer></script>";
    private const string Fragment = "<style data-jfkt-head>.x{}</style>";

    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact]
    public void InsertsTheTagBeforeTheLastBodyEnd()
    {
        var html = "<html><head></head><body><script>var s = '</body>';</script></body></html>";

        var result = Insert(html, null, out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.Inserted, IndexHtmlHeadOutcome.None), result);
        Assert.Equal("<html><head></head><body><script>var s = '</body>';</script>" + Tag + "</body></html>", output);
    }

    [Theory]
    [InlineData("</BODY>")]
    [InlineData("</Body>")]
    public void BodyEnd_IsFoundInAnyCase(string bodyEnd)
    {
        Insert("<html><body>x" + bodyEnd + "</html>", null, out var output);

        Assert.Equal("<html><body>x" + Tag + bodyEnd + "</html>", output);
    }

    [Theory]
    [InlineData("<script data-jfkt-plugin=\"0.9.0.0\" src=\"old\"></script>")]
    [InlineData("<script DATA-JFKT-PLUGIN src=\"x\"></script>")]
    public void AnExistingMarker_LeavesTheDocumentUnchanged(string existing)
    {
        var bytes = Utf8.GetBytes("<html><head></head><body>" + existing + "</body></html>");

        var result = IndexHtmlInjector.Insert(bytes, BodyMarker, Tag, HeadMarker, null, out var output);

        Assert.Equal(IndexHtmlBodyOutcome.AlreadyPresent, result.Body);
        Assert.False(result.Changed);
        Assert.Same(bytes, output);
    }

    [Fact]
    public void AnotherPluginsMarker_DoesNotCount()
    {
        var result = Insert("<body><script data-jfktx-plugin=\"1\"></script><script data-jf-plugin=\"1\"></script></body>", null, out var output);

        Assert.Equal(IndexHtmlBodyOutcome.Inserted, result.Body);
        Assert.Contains(Tag, output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutBodyEnd_NothingChanges_NotEvenTheHead()
    {
        var bytes = Utf8.GetBytes("(self.webpackChunk=self.webpackChunk||[]).push([[1],{1:function(){}}]);</head>");

        var result = IndexHtmlInjector.Insert(bytes, BodyMarker, Tag, HeadMarker, Fragment, out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.NoBodyTag, IndexHtmlHeadOutcome.None), result);
        Assert.Same(bytes, output);
    }

    [Fact]
    public void InvalidUtf8_Throws_SoTheCallerServesTheOriginalBytes()
    {
        byte[] bytes = [.. Utf8.GetBytes("<html><body>"), 0xC3, 0x28, .. Utf8.GetBytes("</body></html>")];

        Assert.Throws<DecoderFallbackException>(() => IndexHtmlInjector.Insert(bytes, BodyMarker, Tag, HeadMarker, null, out _));
    }

    [Fact]
    public void MultiByteText_AndAByteOrderMark_AreKept()
    {
        var html = "﻿<html><head><title>Größe ✓ 日本</title></head><body>日本</body></html>";

        Insert(html, Fragment, out var output);

        Assert.Equal("﻿<html><head><title>Größe ✓ 日本</title>" + Fragment + "</head><body>日本" + Tag + "</body></html>", output);
    }

    [Fact]
    public void TheSyntheticIndex_GetsTheTagAtTheEnd()
    {
        Insert(WebHostHarness.SyntheticIndexHtml, null, out var output);

        Assert.EndsWith(Tag + "</body></html>", output.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(WebHostHarness.SyntheticIndexHtml.Length + Tag.Length, output.Length);
    }

    [Fact]
    public void HeadFragment_GoesBeforeTheFirstHeadEnd()
    {
        var html = "<html><head><title>t</title></HEAD><body><template></head></template></body></html>";

        var result = Insert(html, Fragment, out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.Inserted, IndexHtmlHeadOutcome.Inserted), result);
        Assert.Equal("<html><head><title>t</title>" + Fragment + "</HEAD><body><template></head></template>" + Tag + "</body></html>", output);
    }

    [Fact]
    public void WithoutHeadEnd_OnlyTheTagIsInserted()
    {
        var result = Insert("<html><body>x</body></html>", Fragment, out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.Inserted, IndexHtmlHeadOutcome.NoHeadTag), result);
        Assert.Equal("<html><body>x" + Tag + "</body></html>", output);
    }

    [Fact]
    public void AnExistingHeadMarker_KeepsTheFragmentOut_ButTheTagStillGoesIn()
    {
        var html = "<html><head><style data-jfkt-head>.old{}</style></head><body></body></html>";

        var result = Insert(html, Fragment, out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.Inserted, IndexHtmlHeadOutcome.AlreadyPresent), result);
        Assert.Equal("<html><head><style data-jfkt-head>.old{}</style></head><body>" + Tag + "</body></html>", output);
    }

    [Fact]
    public void AnExistingTag_DoesNotKeepTheFragmentOut()
    {
        var html = "<html><head></head><body>" + Tag + "</body></html>";

        var result = Insert(html, Fragment, out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.AlreadyPresent, IndexHtmlHeadOutcome.Inserted), result);
        Assert.Equal("<html><head>" + Fragment + "</head><body>" + Tag + "</body></html>", output);
    }

    [Fact]
    public void AFragmentWithoutItsMarker_IsNotInserted()
    {
        var result = Insert("<html><head></head><body></body></html>", "<style>.unmarked{}</style>", out var output);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.Inserted, IndexHtmlHeadOutcome.MissingMarker), result);
        Assert.Equal("<html><head></head><body>" + Tag + "</body></html>", output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoFragment_GivesTheSameBytesAsWithoutTheFeature(string? fragment)
    {
        // Plugins without a fragment produce byte-identical output to the tag-only injection.
        var bytes = Utf8.GetBytes(WebHostHarness.SyntheticIndexHtml);

        IndexHtmlInjector.Insert(bytes, BodyMarker, Tag, HeadMarker, null, out var withoutFeature);
        var result = IndexHtmlInjector.Insert(bytes, BodyMarker, Tag, HeadMarker, fragment, out var output);

        Assert.Equal(IndexHtmlHeadOutcome.None, result.Head);
        Assert.Equal(withoutFeature, output);
    }

    [Fact]
    public void RunningTwice_ChangesNothingTheSecondTime()
    {
        var bytes = Utf8.GetBytes(WebHostHarness.SyntheticIndexHtml);

        IndexHtmlInjector.Insert(bytes, BodyMarker, Tag, HeadMarker, Fragment, out var once);
        var second = IndexHtmlInjector.Insert(once, BodyMarker, Tag, HeadMarker, Fragment, out var twice);

        Assert.Equal(new IndexHtmlInsertResult(IndexHtmlBodyOutcome.AlreadyPresent, IndexHtmlHeadOutcome.AlreadyPresent), second);
        Assert.Same(once, twice);
    }

    private static IndexHtmlInsertResult Insert(string html, string? fragment, out string output)
    {
        var result = IndexHtmlInjector.Insert(Utf8.GetBytes(html), BodyMarker, Tag, HeadMarker, fragment, out var bytes);
        output = Utf8.GetString(bytes);
        return result;
    }
}
