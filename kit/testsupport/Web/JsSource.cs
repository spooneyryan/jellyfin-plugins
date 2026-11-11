using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// A light JavaScript scanner for the web coupling checks: it separates comments, string and
/// template literals and regular-expression literals from the code, well enough for greps over plain ES2019 files. It
/// is not a parser.
/// </summary>
public sealed class JsSource
{
    private JsSource(string code, IReadOnlyList<JsString> strings)
    {
        Code = code;
        Strings = strings;
    }

    /// <summary>
    /// Gets the code with comments removed and every string, template and regex literal replaced by an empty one
    /// (<c>""</c>, <c>``</c>, <c>/(?:)/</c>), newlines kept, so identifiers and operators can be grepped.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the string and template literals, with their line numbers (escapes left as written).
    /// </summary>
    public IReadOnlyList<JsString> Strings { get; }

    /// <summary>
    /// Scans a file.
    /// </summary>
    /// <param name="text">The JavaScript source.</param>
    /// <returns>The scanned source.</returns>
    public static JsSource Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var code = new StringBuilder(text.Length);
        var strings = new List<JsString>();
        var line = 1;
        var i = 0;
        char? lastSignificant = null;
        var lastWord = string.Empty;
        var previousWordChar = false;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                    {
                        line++;
                        code.Append('\n');
                    }

                    i++;
                }

                i += 2;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                var startLine = line;
                var value = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        value.Append(text[i]).Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (text[i] == '\n')
                    {
                        line++;
                        code.Append('\n');
                    }

                    value.Append(text[i]);
                    i++;
                }

                i++;
                strings.Add(new JsString(value.ToString(), startLine));
                code.Append(c).Append(c);
                lastSignificant = c;
                lastWord = string.Empty;
                previousWordChar = false;
                continue;
            }

            if (c == '/' && StartsRegex(lastSignificant, lastWord))
            {
                i++;
                var inClass = false;
                while (i < text.Length && text[i] != '\n' && (inClass || text[i] != '/'))
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                    else if (text[i] == '[')
                    {
                        inClass = true;
                    }
                    else if (text[i] == ']')
                    {
                        inClass = false;
                    }

                    i++;
                }

                i++;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                code.Append("/(?:)/");
                lastSignificant = '/';
                lastWord = string.Empty;
                previousWordChar = false;
                continue;
            }

            if (c == '\n')
            {
                line++;
            }

            code.Append(c);
            var wordChar = char.IsLetterOrDigit(c) || c is '_' or '$';
            if (!char.IsWhiteSpace(c))
            {
                lastSignificant = c;
                lastWord = wordChar ? (previousWordChar ? lastWord : string.Empty) + c : string.Empty;
            }

            previousWordChar = wordChar;
            i++;
        }

        return new JsSource(code.ToString(), strings);
    }

    /// <summary>
    /// Splits a literal into the class-name-like tokens a selector or class list holds (letters, digits, <c>_</c> and
    /// <c>-</c>).
    /// </summary>
    /// <param name="value">The literal.</param>
    /// <returns>The tokens.</returns>
    public static IEnumerable<string> Tokens(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var token = new StringBuilder();
        foreach (var c in value + " ")
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
            {
                token.Append(c);
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }
    }

    // A '/' starts a regex after an operator, an opening bracket, a comma or semicolon, the start of the file, or a keyword
    // such as return; after an identifier, a number or a closing bracket it divides.
    private static bool StartsRegex(char? last, string lastWord)
    {
        if (last is null)
        {
            return true;
        }

        if (lastWord is "return" or "typeof" or "case" or "in" or "of" or "new" or "delete" or "void" or "throw")
        {
            return true;
        }

        return "(,=:[!&|?{};+-*%<>~^".Contains(last.Value, StringComparison.Ordinal);
    }
}

/// <summary>
/// A string or template literal and the line it starts on.
/// </summary>
/// <param name="Value">The literal's content.</param>
/// <param name="Line">The 1-based line.</param>
public sealed record JsString(string Value, int Line);
