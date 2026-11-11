using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;

/// <summary>
/// Reads the top-level fields of a jprm <c>build.yaml</c>: plain, quoted, folded (<c>&gt;</c>) and literal (<c>|</c>)
/// scalars, and lists of scalars. That is all the format uses; nested mappings are rejected.
/// </summary>
internal sealed class BuildYaml
{
    private readonly Dictionary<string, string> _scalars;
    private readonly Dictionary<string, IReadOnlyList<string>> _lists;

    private BuildYaml(Dictionary<string, string> scalars, Dictionary<string, IReadOnlyList<string>> lists)
    {
        _scalars = scalars;
        _lists = lists;
    }

    /// <summary>Gets the fields every plugin's build.yaml must set.</summary>
    public static IReadOnlyList<string> RequiredFields { get; } =
    [
        "name", "guid", "version", "targetAbi", "framework", "overview", "description", "category", "owner", "artifacts", "changelog"
    ];

    /// <summary>Gets the names of all top-level fields, in no particular order.</summary>
    public IEnumerable<string> Keys => _scalars.Keys.Concat(_lists.Keys);

    public string Name => Require("name");

    public string Guid => Require("guid");

    public string Version => Require("version");

    public string TargetAbi => Require("targetAbi");

    public string Framework => Require("framework");

    public IReadOnlyList<string> Artifacts => GetList("artifacts");

    public static BuildYaml Load(string path) => Parse(File.ReadAllText(path));

    public static BuildYaml Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsBlankOrComment(line) || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("...", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(line[0]) || line[0] == '-')
            {
                throw new FormatException($"Line {i + 1}: expected a top-level 'key: value', found '{line}'.");
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || (colon + 1 < line.Length && line[colon + 1] != ' '))
            {
                throw new FormatException($"Line {i + 1}: expected 'key: value', found '{line}'.");
            }

            var key = line[..colon].Trim();
            var rest = StripComment(line[(colon + 1)..]).Trim();
            if (scalars.ContainsKey(key) || lists.ContainsKey(key))
            {
                throw new FormatException($"Line {i + 1}: duplicate key '{key}'.");
            }

            i++;
            if (rest.Length > 0 && (rest[0] == '>' || rest[0] == '|'))
            {
                scalars[key] = ReadBlockScalar(lines, ref i, folded: rest[0] == '>', chomp: rest.Length > 1 ? rest[1] : ' ');
            }
            else if (rest.Length > 0)
            {
                scalars[key] = Unquote(rest, i);
            }
            else
            {
                lists[key] = ReadList(lines, ref i, key);
            }
        }

        return new BuildYaml(scalars, lists);
    }

    public string? GetScalar(string key) => _scalars.TryGetValue(key, out var value) ? value : null;

    public string Require(string key)
        => GetScalar(key) is { Length: > 0 } value
            ? value
            : throw new KeyNotFoundException($"build.yaml has no value for '{key}'.");

    public IReadOnlyList<string> GetList(string key) => _lists.TryGetValue(key, out var value) ? value : [];

    public bool Has(string key)
        => (_scalars.TryGetValue(key, out var scalar) && scalar.Length > 0)
            || (_lists.TryGetValue(key, out var list) && list.Count > 0);

    private static IReadOnlyList<string> ReadList(string[] lines, ref int i, string key)
    {
        var items = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsBlankOrComment(line))
            {
                i++;
                continue;
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('-'))
            {
                break;
            }

            var item = StripComment(trimmed[1..]).Trim();
            if (item.Length == 0)
            {
                throw new FormatException($"Line {i + 1}: empty item in '{key}'.");
            }

            if (item.Contains(": ", StringComparison.Ordinal) && item[0] != '"' && item[0] != '\'')
            {
                throw new FormatException($"Line {i + 1}: '{key}' may only hold plain values.");
            }

            items.Add(Unquote(item, i + 1));
            i++;
        }

        return items.Count > 0
            ? items
            : throw new FormatException($"'{key}' has neither a value nor list items.");
    }

    private static string ReadBlockScalar(string[] lines, ref int i, bool folded, char chomp)
    {
        var content = new List<string>();
        int? indent = null;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                content.Add(string.Empty);
                i++;
                continue;
            }

            var lineIndent = line.Length - line.TrimStart().Length;
            if (lineIndent == 0)
            {
                break;
            }

            indent ??= lineIndent;
            content.Add(line[Math.Min(indent.Value, lineIndent)..]);
            i++;
        }

        while (content.Count > 0 && content[^1].Length == 0)
        {
            content.RemoveAt(content.Count - 1);
        }

        var builder = new StringBuilder();
        if (folded)
        {
            // A single line break becomes a space; each blank line becomes one line break.
            var blanks = 0;
            foreach (var line in content)
            {
                if (line.Length == 0)
                {
                    blanks++;
                    continue;
                }

                if (blanks > 0)
                {
                    builder.Append('\n', blanks);
                }
                else if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(line);
                blanks = 0;
            }
        }
        else
        {
            builder.AppendJoin('\n', content);
        }

        var text = builder.ToString();
        return chomp == '-' ? text : text + "\n";
    }

    private static string Unquote(string value, int lineNumber)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (value[0] is '"' or '\'' or '{' or '[' or '&' or '*' or '!')
        {
            throw new FormatException($"Line {lineNumber}: unsupported YAML value '{value}'.");
        }

        return value;
    }

    private static string StripComment(string value)
    {
        var quote = '\0';
        for (var n = 0; n < value.Length; n++)
        {
            var c = value[n];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && (n == 0 || char.IsWhiteSpace(value[n - 1])))
            {
                return value[..n];
            }
        }

        return value;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }
}
