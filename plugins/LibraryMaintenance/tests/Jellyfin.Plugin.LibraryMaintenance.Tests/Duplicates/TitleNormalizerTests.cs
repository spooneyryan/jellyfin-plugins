using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The title normalisation: the rule table N1 to N10 (its rows and the counterexamples of Jellyfin's own
/// name parsing), the keys that must and must not meet, and the guards (length cap, regex timeouts, never an empty key).
/// </summary>
public sealed class TitleNormalizerTests
{
    // The option sets the rows run with; "default" is the 1.0 settings.
    private const string Default = "default";
    private const string Roman = "roman";
    private const string Spaces = "spaces";
    private const string Release = "release";
    private const string NoArticles = "noarticles";
    private const string NoEditions = "noeditions";
    private const string FanEdit = "fanedit";
    private const string French = "fr";
    private const string Italian = "it";
    private const string German = "de";
    private const string Spanish = "es";
    private const string Dutch = "nl";

    [Theory]

    // The minimum rows.
    [InlineData(Default, "Test Pattern (2020)", "test pattern")]
    [InlineData(Default, "Amélie", "amelie")]
    [InlineData(Default, "Amélie", "amelie")]
    [InlineData(Default, "Matrix, The", "matrix")]
    [InlineData(Default, "The Matrix", "matrix")]
    [InlineData(Default, "Matrix, The (1999)", "matrix")]
    [InlineData(Default, "Schindler's List", "schindlers list")]
    [InlineData(Default, "Schindler’s List", "schindlers list")]
    [InlineData(Default, "Schindler´s List", "schindlers list")]
    [InlineData(Default, "Schindlers List", "schindlers list")]
    [InlineData(Roman, "Rocky II", "rocky 2")]
    [InlineData(Default, "Movie (2020) [tmdbid-603]", "movie")]
    [InlineData(Default, "The Matrix [tmdbid=603]", "matrix")]
    [InlineData(Default, "Blade Runner [imdbid-tt0083658]", "blade runner")]
    [InlineData(Default, "Blade Runner {tmdb-78}", "blade runner")]
    [InlineData(Default, "Blade Runner {imdb-tt0083658} (1982)", "blade runner")]
    [InlineData(Default, "Blade Runner {edition-Director's Cut}", "blade runner")]
    [InlineData(Default, "Blade Runner - The Final Cut", "blade runner")]
    [InlineData(Default, "Blade Runner: The Final Cut", "blade runner")]
    [InlineData(Default, "Blade Runner – The Final Cut", "blade runner")]
    [InlineData(Default, "Fast & Furious", "fast and furious")]
    [InlineData(Default, "Fast and Furious", "fast and furious")]
    [InlineData(Default, "curse.of.chucky", "curse of chucky")]

    // Jellyfin's name parsing strips these years; ours never strips an unbracketed one.
    [InlineData(Default, "Blade Runner 2049", "blade runner 2049")]
    [InlineData(Default, "Death Race 2000", "death race 2000")]
    [InlineData(Default, "Space: 1999", "space 1999")]
    [InlineData(Default, "2001: A Space Odyssey", "2001 a space odyssey")]
    [InlineData(Default, "1917", "1917")]
    [InlineData(Default, "1917 (2019)", "1917")]
    [InlineData(Default, "Se7en", "se7en")]
    [InlineData(Default, "(2020)", "2020")]
    [InlineData(Default, "Movie (1080p)", "movie 1080p")]
    [InlineData(Default, "Movie [2020]", "movie")]

    // N1: diacritics on Latin letters, the folded letters, compatibility forms; other scripts keep their marks.
    [InlineData(Default, "Æon Flux", "aeon flux")]
    [InlineData(Default, "Straße", "strasse")]
    [InlineData(Default, "Łódź", "lodz")]
    [InlineData(Default, "Øresund", "oresund")]
    [InlineData(Default, "Œuvre", "oeuvre")]
    [InlineData(Default, "Đorđe", "dorde")]
    [InlineData(Default, "İstanbul", "istanbul")]
    [InlineData(Default, "ＴＥＳＴ　ＰＡＴＴＥＲＮ", "test pattern")]
    [InlineData(Default, "Ｓｅ７ｅｎ", "se7en")]
    [InlineData(Default, "千と千尋の神隠し", "千と千尋の神隠し")]
    [InlineData(Default, "がんばれ", "がんばれ")]
    [InlineData(Default, "Брат", "брат")]
    [InlineData(Default, "Иван Грозный", "иван грозный")]
    [InlineData(Default, "Ζορμπάς", "ζορμπασ")]

    // N1: Greek's tonos and final sigma, Cyrillic ё and Turkish ı fold; й keeps its breve.
    [InlineData(Default, "Ζορμπας", "ζορμπασ")]
    [InlineData(Default, "ΖΟΡΜΠΑΣ", "ζορμπασ")]
    [InlineData(Default, "Ἀθῆναι", "ἀθῆναι")]
    [InlineData(Default, "Ёлки", "елки")]
    [InlineData(Default, "ЁЛКИ", "елки")]
    [InlineData(Default, "Kızım", "kizim")]

    // N1: characters a title carries without showing them are removed first (the review's repros).
    [InlineData(Default, "Ali​ens", "aliens")]
    [InlineData(Default, "Ali­ens", "aliens")]
    [InlineData(Default, "Ali‍ens", "aliens")]
    [InlineData(Default, "Ali⁠ens", "aliens")]
    [InlineData(Default, "Ali‌ens", "aliens")]
    [InlineData(Default, "﻿Aliens", "aliens")]
    [InlineData(Default, "‫Aliens‬", "aliens")]
    [InlineData(Default, "Movie (2020)‏", "movie")]
    [InlineData(Default, "Matrix, The‎", "matrix")]
    [InlineData(Default, "‎The Matrix", "matrix")]
    [InlineData(Default, "Aliens - Special Edition‎", "aliens")]
    [InlineData(Default, "Movie ❤️", "movie")]
    [InlineData(Default, "Movie ❤⃝", "movie")]
    [InlineData(Default, "Movie 1️⃣", "movie 1")]
    [InlineData(Default, "Movie\U000E0100", "movie")]

    // N4: only a whole segment after a separator or in trailing brackets; up to three; the year before or after it.
    [InlineData(Default, "The Final Cut", "final cut")]
    [InlineData(Default, "Mission: Impossible - Fallout", "mission impossible fallout")]
    [InlineData(Default, "Mission: Impossible", "mission impossible")]
    [InlineData(Default, "Aliens (Special Edition)", "aliens")]
    [InlineData(Default, "Apocalypse Now Redux", "apocalypse now redux")]
    [InlineData(Default, "Apocalypse Now: Redux", "apocalypse now")]
    [InlineData(Default, "Extended", "extended")]
    [InlineData(Default, "Movie (2020) (Extended)", "movie")]
    [InlineData(Default, "Movie (Extended) (2020)", "movie")]
    [InlineData(Default, "Movie - Extended Edition (2020)", "movie")]
    [InlineData(Default, "Movie (2020) - Unrated Cut", "movie")]
    [InlineData(Default, "Movie - Extended (Remastered)", "movie")]
    [InlineData(Default, "Movie (2020) [tmdbid-603] {edition-Extended}", "movie")]
    [InlineData(Default, "Star Wars: Episode IV - A New Hope - Special Edition", "star wars episode iv a new hope")]
    [InlineData(Default, "Evil Dead II - 25th Anniversary Edition", "evil dead ii 25th anniversary edition")]
    [InlineData(Default, "Terminator 2: Judgment Day - Extended Special Edition", "terminator 2 judgment day extended special edition")]
    [InlineData(Default, "Amadeus (Director's Cut Bonus Disc)", "amadeus directors cut bonus disc")]
    [InlineData(NoEditions, "Blade Runner - The Final Cut", "blade runner the final cut")]
    [InlineData(FanEdit, "Movie - Fan Edit", "movie")]
    [InlineData(FanEdit, "Movie - Extended", "movie extended")]

    // N5 and N6.
    [InlineData(Default, "L.A. Confidential", "la confidential")]
    [InlineData(Default, "L.A Confidential", "la confidential")]
    [InlineData(Default, "LA Confidential", "la confidential")]
    [InlineData(Default, "S.W.A.T.", "swat")]
    [InlineData(Default, "E.T. the Extra-Terrestrial", "et the extra terrestrial")]
    [InlineData(Default, "Romeo + Juliet", "romeo juliet")]
    [InlineData(Default, "Romeo & Juliet", "romeo and juliet")]
    [InlineData(Default, "Spider-Man", "spider man")]
    [InlineData(Default, "Spiderman", "spiderman")]
    [InlineData(Default, "Face/Off", "face off")]
    [InlineData(Default, "WALL·E", "wall e")]
    [InlineData(Default, "M*A*S*H", "m a s h")]
    [InlineData(Default, "Mother!", "mother")]
    [InlineData(Default, "  The \t  Matrix  ", "matrix")]
    [InlineData(Default, "!!!", "!!!")]

    // N7: one leading article, never the last word, never mid-title; N7b off by default.
    [InlineData(Default, "A", "a")]
    [InlineData(Default, "The", "the")]
    [InlineData(Default, "A Beautiful Mind", "beautiful mind")]
    [InlineData(Default, "An American Werewolf in London", "american werewolf in london")]
    [InlineData(Default, "Les Misérables", "les miserables")]
    [InlineData(Default, "L'Avventura", "lavventura")]
    [InlineData(NoArticles, "The Matrix", "the matrix")]
    [InlineData(NoArticles, "Matrix, The", "matrix the")]
    [InlineData(French, "Les Misérables", "miserables")]
    [InlineData(French, "Misérables, Les", "miserables")]
    [InlineData(French, "La Haine", "haine")]
    [InlineData(French, "La La Land", "la land")]
    [InlineData(French, "Le", "le")]
    [InlineData(French, "The Matrix", "matrix")]
    [InlineData(Italian, "L'Avventura", "avventura")]
    [InlineData(Italian, "Avventura, L'", "avventura")]
    [InlineData(Italian, "Il Postino", "postino")]
    [InlineData(German, "Das Boot", "boot")]
    [InlineData(Spanish, "El Laberinto del Fauno", "laberinto del fauno")]
    [InlineData(Dutch, "De Lift", "lift")]

    // N8: II to XX, standalone, never I.
    [InlineData(Default, "Rocky II", "rocky ii")]
    [InlineData(Roman, "Rocky 2", "rocky 2")]
    [InlineData(Roman, "Rocky V", "rocky 5")]
    [InlineData(Roman, "Mission: Impossible III", "mission impossible 3")]
    [InlineData(Roman, "I, Robot", "i robot")]
    [InlineData(Roman, "Rocky XXI", "rocky xxi")]
    [InlineData(Roman, "Civilization", "civilization")]

    // N9.
    [InlineData(Spaces, "Spider-Man", "spiderman")]
    [InlineData(Spaces, "Spiderman", "spiderman")]
    [InlineData(Spaces, "Face/Off", "faceoff")]
    [InlineData(Spaces, "The Matrix", "matrix")]

    // N10, for release names only.
    [InlineData(Release, "curse.of.chucky.2013.unrated.1080p", "curse of chucky")]
    [InlineData(Release, "Blade.Runner.2049.1080p.BluRay.x264", "blade runner 2049")]
    [InlineData(Release, "Blade Runner 2049 1080p", "blade runner 2049")]
    [InlineData(Release, "The.Matrix.1999.REMASTERED.2160p.WEB-DL", "matrix")]
    [InlineData(Release, "1917.2019.1080p", "1917")]
    [InlineData(Release, "1917.1080p", "1917")]
    [InlineData(Release, "1080p", "1080p")]
    [InlineData(Release, "Movie.2013.Blu-ray", "movie")]
    [InlineData(Release, "S.W.A.T.2003.720p", "swat")]
    [InlineData(Release, "A.I.Artificial.Intelligence.2001.DVDRip", "ai artificial intelligence")]
    [InlineData(Release, "Movie.2099.1080p", "movie 2099")]

    // N10 strips edition phrases only after the year, and never down to an article; one word is a title
    // ("Alien.1979.Extended" is Alien). "Death.Race.2000" loses its 2000, the window's inherent limit.
    [InlineData(Release, "The.Final.Cut.2004.1080p", "final cut")]
    [InlineData(Release, "Step.Up.3D.2010.720p", "step up 3d")]
    [InlineData(Release, "Apocalypse.Now.Redux.2001.1080p", "apocalypse now redux")]
    [InlineData(Release, "Uncut.Gems.2019.1080p", "uncut gems")]
    [InlineData(Release, "Movie.Unrated.2013.1080p", "movie unrated")]
    [InlineData(Release, "Extended.2019.1080p", "extended")]
    [InlineData(Release, "Alien.1979.1080p", "alien")]
    [InlineData(Release, "Alien.1979.Extended.1080p", "alien")]
    [InlineData(Release, "Alien.1979.Extended.1080p.BluRay", "alien")]
    [InlineData(Release, "Se7en.1995.Remastered.1080p.BluRay", "se7en")]
    [InlineData(Release, "Movie.2013.Extended.Cut.1080p", "movie")]
    [InlineData(Release, "Death.Race.2000.1080p", "death race")]
    [InlineData(Release, "The.2012.Extended.1080p", "2012 extended")]
    [InlineData(Release, "The.2004.1080p", "2004")]
    [InlineData(Release, "The.Movie.2004.Extended.Cut.1080p", "movie")]
    public void Normalize_GivesTheKey(string profile, string title, string expected)
    {
        var result = TitleNormalizer.Normalize(title, Options(profile), isReleaseName: profile == Release);

        Assert.Equal(expected, result.Key);
    }

    [Theory]
    [InlineData("Test Pattern (2020)", null, 2020)]
    [InlineData("Movie (2020) [tmdbid-603]", null, 2020)]
    [InlineData("Blade Runner {edition-Director's Cut}", "directors cut", null)]
    [InlineData("Blade Runner {edition-}", null, null)]
    [InlineData("Blade Runner - The Final Cut", "the final cut", null)]
    [InlineData("Movie (2020) (Extended)", "extended", 2020)]
    [InlineData("Movie - Extended (Remastered)", "extended remastered", null)]
    [InlineData("Movie (2020) [tmdbid-603] {edition-Extended} - Remastered", "extended", 2020)]
    [InlineData("Blade Runner 2049", null, null)]
    [InlineData("1917", null, null)]
    [InlineData("(2020)", null, null)]
    [InlineData("Movie (1799)", null, null)]
    [InlineData("Movie (2020)‏", null, 2020)]
    [InlineData("Aliens - Special Edition‎", "special edition", null)]
    public void Normalize_KeepsTheEditionAndTheBracketYear(string title, string? edition, int? year)
    {
        var result = TitleNormalizer.Normalize(title);

        Assert.Equal(edition, result.Edition);
        Assert.Equal(year, result.BracketYear);
    }

    [Theory]
    [InlineData("curse.of.chucky.2013.unrated.1080p", "unrated", 2013)]
    [InlineData("The.Matrix.1999.REMASTERED.2160p.WEB-DL", "remastered", 1999)]
    [InlineData("Big.Movie.2013.Extended.Cut.1080p", "extended cut", 2013)]
    [InlineData("Blade.Runner.2049.1080p.BluRay.x264", null, null)]
    [InlineData("1917.2019.1080p", null, 2019)]
    [InlineData("The.Final.Cut.2004.1080p", null, 2004)]
    [InlineData("Step.Up.3D.2010.720p", null, 2010)]
    [InlineData("Movie.Unrated.2013.1080p", null, 2013)]
    [InlineData("Alien.1979.Extended.1080p", "extended", 1979)]
    [InlineData("Se7en.1995.Remastered.1080p.BluRay", "remastered", 1995)]
    [InlineData("Death.Race.2000.1080p", null, 2000)]
    [InlineData("The.2004.1080p", null, null)]
    public void Normalize_TakesAReleaseNamesEditionAndYear(string title, string? edition, int? year)
    {
        var result = TitleNormalizer.Normalize(title, Options(Release), isReleaseName: true);

        Assert.Equal(edition, result.Edition);
        Assert.Equal(year, result.BracketYear);
        Assert.True(result.AppliedRules.HasFlag(TitleRules.N10));
    }

    [Fact]
    public void N10_IsOnlyForReleaseNames_AndOnlyWhenOn()
    {
        const string Title = "curse.of.chucky.2013.unrated.1080p";

        // The caller says the name isn't a release name (the item has ids, or a Name of its own).
        Assert.Equal("curse of chucky 2013 unrated 1080p", TitleNormalizer.Normalize(Title, Options(Release), isReleaseName: false).Key);

        // The option is off by default.
        Assert.Equal("curse of chucky 2013 unrated 1080p", TitleNormalizer.Normalize(Title, NormalizationOptions.Default, isReleaseName: true).Key);

        // Without a year window, no year is taken, and so no edition phrase after it either.
        var noWindow = new NormalizationOptions { ReleaseNameCleanup = true };
        var result = TitleNormalizer.Normalize(Title, noWindow, isReleaseName: true);
        Assert.Equal("curse of chucky 2013 unrated", result.Key);
        Assert.Null(result.BracketYear);
        Assert.Null(result.Edition);

        // A bracketed year (N3) is the year; the tokens before it stay.
        Assert.Equal("movie unrated", TitleNormalizer.Normalize("Movie.Unrated (2013)", Options(Release), isReleaseName: true).Key);
        var bracketed = TitleNormalizer.Normalize("Big.Movie.2012.Unrated (2013)", Options(Release), isReleaseName: true);
        Assert.Equal("big movie 2012 unrated", bracketed.Key);
        Assert.Equal(2013, bracketed.BracketYear);
    }

    [Theory]
    [InlineData(Default, "The Matrix", "Matrix, The")]
    [InlineData(Default, "The Matrix", "Matrix (1999)")]
    [InlineData(Default, "L.A. Confidential", "LA Confidential")]
    [InlineData(Default, "Schindler's List", "Schindlers List")]
    [InlineData(Default, "Fast & Furious", "Fast and Furious")]
    [InlineData(Default, "Amélie", "Amelie")]
    [InlineData(Default, "Blade Runner - The Final Cut", "Blade Runner [tmdbid-78]")]
    [InlineData(Default, "Mission: Impossible", "Mission Impossible")]
    [InlineData(Roman, "Rocky II", "Rocky 2")]
    [InlineData(Spaces, "Spider-Man", "Spiderman")]
    [InlineData(Release, "Blade.Runner.1982.1080p.BluRay", "Blade Runner")]
    [InlineData(Default, "Ζορμπάς", "Ζορμπας")]
    [InlineData(Default, "Ёлки", "Елки")]
    [InlineData(Default, "Kızım", "Kizim")]
    [InlineData(Default, "Ali​ens", "Aliens")]
    [InlineData(Default, "Movie (2020)‏", "Movie")]
    [InlineData(Default, "Movie ❤️", "Movie ❤")]
    public void TheseTitles_Meet(string profile, string first, string second)
        => Assert.Equal(Key(profile, first), Key(profile, second));

    [Theory]
    [InlineData(Default, "Romeo + Juliet", "Romeo & Juliet")]
    [InlineData(Default, "Spider-Man", "Spiderman")]
    [InlineData(Default, "Blade Runner", "Blade Runner 2049")]
    [InlineData(Default, "Death Race", "Death Race 2000")]
    [InlineData(Default, "The Final Cut", "Blade Runner - The Final Cut")]
    [InlineData(Default, "Mission: Impossible - Fallout", "Mission: Impossible")]
    [InlineData(Default, "Rocky II", "Rocky 2")]
    [InlineData(Default, "Иван Грозный", "Иван Грозныи")]
    [InlineData(Default, "がんばれ", "かんばれ")]
    [InlineData(Release, "Blade.Runner.2049.1080p", "Blade Runner")]
    [InlineData(Release, "The.Final.Cut.2004.1080p", "The")]
    public void TheseTitles_DoNotMeet(string profile, string first, string second)
        => Assert.NotEqual(Key(profile, first), Key(profile, second));

    [Theory]
    [InlineData(Default, "Amélie", "N1")]
    [InlineData(Default, "movie [tmdbid-603]", "N2")]
    [InlineData(Default, "movie (2020)", "N3")]
    [InlineData(Default, "movie - extended", "N4")]
    [InlineData(Default, "l.a. confidential", "N5")]
    [InlineData(Default, "schindler's list", "N6")]
    [InlineData(Default, "the matrix", "N7")]
    [InlineData(Default, "matrix, the", "N7")]
    [InlineData(French, "la haine", "N7b")]
    [InlineData(Italian, "l'avventura", "N7b")]
    [InlineData(Roman, "rocky ii", "N8")]
    [InlineData(Spaces, "spider man", "N9")]
    [InlineData(Release, "movie.2013.1080p", "N10")]
    public void AppliedRules_NameTheRuleThatChangedTheTitle(string profile, string title, string rule)
    {
        var expected = Enum.Parse<TitleRules>(rule);
        var result = TitleNormalizer.Normalize(title, Options(profile), isReleaseName: profile == Release);

        Assert.True(result.AppliedRules.HasFlag(expected), $"{title}: {result.AppliedRules}");
    }

    [Fact]
    public void AppliedRules_AreNoneForAKeyAlreadyNormal()
    {
        var result = TitleNormalizer.Normalize("matrix");

        Assert.Equal(TitleRules.None, result.AppliedRules);
        Assert.Equal("matrix", result.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t　\n")]
    public void Normalize_OfNothing_IsAnEmptyKey(string? title)
    {
        var result = TitleNormalizer.Normalize(title);

        Assert.Equal(string.Empty, result.Key);
        Assert.Null(result.Edition);
        Assert.Null(result.BracketYear);
        Assert.Equal(TitleRules.None, result.AppliedRules);
    }

    [Theory]
    [InlineData("​")]
    [InlineData("﻿⁠ ‎")]
    [InlineData("️")]
    public void Normalize_OfInvisibleCharactersAlone_IsAnEmptyKey(string title)
    {
        var result = TitleNormalizer.Normalize(title);

        Assert.Equal(string.Empty, result.Key);
        Assert.Null(result.Edition);
        Assert.Null(result.BracketYear);
    }

    [Theory]
    [InlineData(0x115F)] // Hangul choseong filler (a letter by category, blank on screen, which N1 drops)
    [InlineData(0x3164)] // Hangul filler
    [InlineData(0xFFA0)] // halfwidth Hangul filler
    [InlineData(0x034F)] // combining grapheme joiner, a mark on a Latin letter
    [InlineData(0x180B)] // Mongolian free variation selector one
    [InlineData(0x17B4)] // Khmer vowel inherent AQ
    [InlineData(0x2066)] // left-to-right isolate
    [InlineData(0x180E)] // Mongolian vowel separator
    [InlineData(0xE0001)] // language tag
    [InlineData(0x061C)] // Arabic letter mark
    public void Normalize_DropsADefaultIgnorableCharacter_InsideATitle(int codePoint)
    {
        Assert.Equal("aliens", TitleNormalizer.Normalize("Ali" + char.ConvertFromUtf32(codePoint) + "ens").Key);
        if (codePoint is 0x115F or 0x3164 or 0xFFA0)
        {
            Assert.Equal(string.Empty, TitleNormalizer.Normalize(char.ConvertFromUtf32(codePoint) + " " + char.ConvertFromUtf32(codePoint)).Key);
        }
    }

    [Fact]
    public void Normalize_KeepsTheJungseongFiller_PartOfAnOldHangulSyllable()
        => Assert.NotEqual(string.Empty, TitleNormalizer.Normalize("ᄀᅠ").Key);

    [Theory]
    [InlineData("[tmdbid-603]")]
    [InlineData("(2020)")]
    [InlineData("- Extended")]
    [InlineData("The")]
    [InlineData("...")]
    [InlineData("&")]
    public void Normalize_NeverLeavesAnEmptyKey(string title)
    {
        foreach (var profile in new[] { Default, Roman, Spaces, Release, French })
        {
            Assert.NotEqual(string.Empty, TitleNormalizer.Normalize(title, Options(profile), isReleaseName: true).Key);
        }
    }

    [Fact]
    public void Normalize_IsStable_OnItsOwnKeys()
    {
        // A key normalises to itself, so a key written once (a mark's key, an export) still meets its title.
        string[] titles = ["Test Pattern (2020)", "Matrix, The", "Blade Runner - The Final Cut", "L.A. Confidential", "千と千尋の神隠し", "Иван Грозный", "Se7en", "Ζορμπάς", "Ёлки", "Ali​ens", "Movie ❤️"];

        foreach (var title in titles)
        {
            var key = TitleNormalizer.Normalize(title).Key;
            Assert.Equal(key, TitleNormalizer.Normalize(key).Key);
        }
    }

    [Fact]
    public void ALoneSurrogate_DoesNotThrow()
    {
        var result = TitleNormalizer.Normalize("Movie \uD800 Two");

        Assert.Equal("movie two", result.Key);
    }

    [Fact]
    public void ALongTitle_IsCutAtTheCap_Quickly()
    {
        // Pathological input for N5 and N7's expressions: a long run of dotted letters, and commas.
        string[] titles =
        [
            string.Concat(Enumerable.Repeat("a.", 200_000)) + "!",
            string.Concat(Enumerable.Repeat(", ", 200_000)) + "the",
            new string('(', 200_000) + "2020)",
            "Movie" + new string(' ', 100_000) + "x",
        ];

        foreach (var title in titles)
        {
            var clock = Stopwatch.StartNew();
            var result = TitleNormalizer.Normalize(title, Options(Release), isReleaseName: true);
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms");
            Assert.True(result.AppliedRules.HasFlag(TitleRules.Truncated), result.AppliedRules.ToString());
            Assert.True(result.Key.Length <= TitleNormalizer.MaxTitleLength, result.Key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void ATitleAtTheCap_IsNotTruncated_AndACutNeverSplitsASurrogatePair()
    {
        var atCap = new string('x', TitleNormalizer.MaxTitleLength);
        Assert.False(TitleNormalizer.Normalize(atCap).AppliedRules.HasFlag(TitleRules.Truncated));

        // A U+1F600 whose high half is the cap's last character.
        var split = new string('x', TitleNormalizer.MaxTitleLength - 1) + "\U0001F600" + "tail";
        var result = TitleNormalizer.Normalize(split);
        Assert.True(result.AppliedRules.HasFlag(TitleRules.Truncated));
        Assert.Equal(new string('x', TitleNormalizer.MaxTitleLength - 1), result.Key);
    }

    [Fact]
    public void EveryExpression_HasTheTimeout()
    {
        var expressions = typeof(TitleNormalizer)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0)
            .Select(m => (m.Name, Regex: (Regex)m.Invoke(null, null)!))
            .ToList();
        var fields = typeof(TitleNormalizer)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(Regex))
            .ToList();

        Assert.Equal(6, expressions.Count);
        Assert.Empty(fields);
        Assert.All(expressions, e => Assert.Equal(TimeSpan.FromMilliseconds(TitleNormalizer.MatchTimeoutMilliseconds), e.Regex.MatchTimeout));
        Assert.Equal(100, TitleNormalizer.MatchTimeoutMilliseconds);
    }

    [Fact]
    public void ATimeout_GivesTheFallbackKey_NotAnException()
    {
        var result = TitleNormalizer.Normalize(
            "  The   Matrix, Ünd (1999)  ",
            NormalizationOptions.Default,
            isReleaseName: false,
            (_, _, _, _) => throw new RegexMatchTimeoutException("x", "y", TimeSpan.FromMilliseconds(100)));

        Assert.Equal("the matrix, und (1999)", result.Key);
        Assert.Null(result.Edition);
        Assert.Null(result.BracketYear);
        Assert.Equal(TitleRules.N1 | TitleRules.Fallback, result.AppliedRules);
    }

    [Fact]
    public void ATimeout_OnALongTitle_KeepsTheTruncatedFlag()
    {
        var result = TitleNormalizer.Normalize(
            new string('x', TitleNormalizer.MaxTitleLength + 10),
            null,
            isReleaseName: false,
            (_, _, _, _) => throw new RegexMatchTimeoutException());

        Assert.Equal(TitleRules.N1 | TitleRules.Fallback | TitleRules.Truncated, result.AppliedRules);
        Assert.Equal(TitleNormalizer.MaxTitleLength, result.Key.Length);
    }

    [Fact]
    public void OtherExceptions_AreNotSwallowed()
        => Assert.Throws<InvalidOperationException>(() => TitleNormalizer.Normalize("x", null, false, (_, _, _, _) => throw new InvalidOperationException()));

    [Fact]
    public void TheEditionPhrases_AreTheDocumentedList_AndTheSettingsDefault()
    {
        Assert.Equal(24, NormalizationOptions.DefaultEditionPhrases.Count);
        Assert.Equal(NormalizationOptions.DefaultEditionPhrases, new PluginConfiguration().EditionPhrases);

        // The configuration's default is a copy: a change to the settings never changes the defaults.
        var configuration = new PluginConfiguration();
        configuration.EditionPhrases[0] = "changed";
        Assert.Equal("director's cut", NormalizationOptions.DefaultEditionPhrases[0]);
        Assert.Equal("director's cut", new PluginConfiguration().EditionPhrases[0]);
    }

    [Fact]
    public void TheOptions_TolerateNullLists_AndUnknownLanguages()
    {
        var options = new NormalizationOptions { EditionPhrases = null!, ArticleLanguages = null! };
        Assert.Equal("the matrix extended", TitleNormalizer.Normalize("The Matrix - Extended", options).Key);

        var unknown = new NormalizationOptions { ArticleLanguages = ["Xx", "", "en"] };
        Assert.Equal("matrix", TitleNormalizer.Normalize("The Matrix", unknown).Key);
    }

    [Fact]
    public void TheVersion_IsOne()
        => Assert.Equal(1, TitleNormalizer.NormalizerVersion);

    [Theory]
    [InlineData(2001, null, 1999, 2001, "ProductionYear")]
    [InlineData(null, "2002-05-01", 1999, 2002, "PremiereDate")]
    [InlineData(null, null, 1999, 1999, "Title")]
    [InlineData(0, "0001-01-01", null, null, "Unknown")]
    [InlineData(null, null, null, null, "Unknown")]
    [InlineData(2049, null, null, 2049, "ProductionYear")]
    [InlineData(-5, null, 2020, 2020, "Title")]
    public void TheYear_IsProductionYear_ThenPremiere_ThenTheTitles(int? production, string? premiere, int? bracket, int? year, string source)
    {
        var resolved = TitleYear.Resolve(production, premiere is null ? null : DateTime.Parse(premiere, System.Globalization.CultureInfo.InvariantCulture), bracket);

        Assert.Equal(year, resolved.Year);
        Assert.Equal(Enum.Parse<YearSource>(source), resolved.Source);
    }

    [Fact]
    public void TheTitleAndYearKey_MarksAnUnknownYear()
    {
        Assert.Equal("matrix|1999", new TitleYear(1999, YearSource.ProductionYear).KeyFor("matrix"));
        Assert.Equal("matrix|?", TitleYear.Unknown.KeyFor("matrix"));
    }

    [Fact]
    public void TheNormaliser_NamesNoJellyfinType()
    {
        // The normaliser is pure; nothing of the server in its code or its signatures.
        Type[] types = [typeof(TitleNormalizer), typeof(NormalizationOptions), typeof(NormalizedTitle), typeof(TitleRules), typeof(TitleYear), typeof(YearSource)];
        var named = types
            .SelectMany(t => MethodBodyReferences.DeclaredMethods(t)
                .SelectMany(m => MethodBodyReferences.FindTypes(m)
                    .Concat(m is MethodInfo info ? MethodBodyReferences.Expand(info.ReturnType) : [])
                    .Concat(m.GetParameters().SelectMany(p => MethodBodyReferences.Expand(p.ParameterType))))
                .Concat(MethodBodyReferences.DeclaredFields(t).SelectMany(f => MethodBodyReferences.Expand(f.FieldType))))
            .Select(t => t.Namespace ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("System.Text.RegularExpressions", named);
        Assert.DoesNotContain(named, n => n.StartsWith("MediaBrowser", StringComparison.Ordinal) || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || n.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) || (n.StartsWith("Jellyfin", StringComparison.Ordinal) && !string.Equals(n, typeof(TitleNormalizer).Namespace, StringComparison.Ordinal)));

        // And the files name none either.
        var folder = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "plugins", "LibraryMaintenance", "Jellyfin.Plugin.LibraryMaintenance", "Duplicates", "Normalization");
        var files = Directory.GetFiles(folder, "*.cs");
        Assert.Equal(4, files.Length);
        Assert.All(files, file => Assert.DoesNotMatch(@"(?m)^\s*using\s+(?!System(\.|;))", File.ReadAllText(file)));
    }

    private static string Key(string profile, string title)
        => TitleNormalizer.Normalize(title, Options(profile), isReleaseName: profile == Release).Key;

    private static NormalizationOptions Options(string profile) => profile switch
    {
        Default => NormalizationOptions.Default,
        Roman => new() { RomanNumerals = true },
        Spaces => new() { IgnoreSpaces = true },
        Release => new() { ReleaseNameCleanup = true, ReleaseNameMaxYear = 2027 },
        NoArticles => new() { ArticleLanguages = [] },
        NoEditions => new() { StripEditionSuffixes = false },
        FanEdit => new() { EditionPhrases = ["Fan Edit"] },
        French => new() { ArticleLanguages = ["En", "Fr"] },
        Italian => new() { ArticleLanguages = ["En", "It"] },
        German => new() { ArticleLanguages = ["En", "De"] },
        Spanish => new() { ArticleLanguages = ["En", "Es"] },
        Dutch => new() { ArticleLanguages = ["En", "Nl"] },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };
}
