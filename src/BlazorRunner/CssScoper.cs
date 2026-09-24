using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Applies CSS isolation the way the Razor SDK does. A component's <c>.razor.css</c> gets an
/// attribute scope appended to its selectors — <c>.page[b-jqx7awf7db]</c> — and the generated
/// component puts that same attribute on the elements it renders, so the rules only apply inside it.
///
/// The rewriting matches the SDK's output: the scope goes on the last compound selector, or on the
/// last compound *before* <c>::deep</c> (which marks where scoping stops, so a parent can style the
/// markup a child component renders). Nested at-rules such as <c>@media</c> are rewritten too, while
/// <c>@keyframes</c> contents are left alone because their key selectors are not selectors.
/// </summary>
public static class CssScoper
{
    static readonly string[] PseudoElements =
    {
        "::before", "::after", "::first-line", "::first-letter", "::selection", "::marker",
        "::placeholder", "::backdrop", "::file-selector-button", "::slotted", "::part",
    };

    /// <summary>Scopes a stylesheet. <paramref name="attribute"/> is the bracketed scope, such as
    /// <c>[b-jqx7awf7db]</c>.</summary>
    public static string Scope(string css, string attribute)
    {
        var text = css ?? "";
        var output = new StringBuilder(text.Length + 64);
        ScopeBlock(text, attribute, output);
        return output.ToString();
    }

    static void ScopeBlock(string text, string attribute, StringBuilder output)
    {
        var index = 0;

        while (index < text.Length)
        {
            var brace = IndexOfStructural(text, index, '{');
            if (brace < 0)
            {
                output.Append(text, index, text.Length - index);
                return;
            }

            var close = MatchingBrace(text, brace);
            if (close < 0)
            {
                output.Append(text, index, text.Length - index);
                return;
            }

            var prelude = text.Substring(index, brace - index);
            var body = text.Substring(brace + 1, close - brace - 1);
            var trimmed = prelude.TrimStart();

            if (trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                output.Append(prelude).Append('{');

                if (trimmed.IndexOf("keyframes", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    output.Append(body);
                }
                else
                {
                    ScopeBlock(body, attribute, output);
                }

                output.Append('}');
            }
            else
            {
                output.Append(ScopeSelectors(prelude, attribute)).Append('{').Append(body).Append('}');
            }

            index = close + 1;
        }
    }

    static string ScopeSelectors(string prelude, string attribute)
    {
        // Comments are kept out of the way while scoping — otherwise a `::deep` mentioned inside one
        // (in prose, say) is taken for the real thing — and emitted above the rule afterwards.
        var comments = new StringBuilder();
        var code = StripComments(prelude, comments);

        var parts = SplitSelectors(code);
        var scoped = parts.Count == 0
            ? code
            : string.Join(",", parts.Select(part => ScopeSelector(part, attribute)));

        return comments.Length > 0 ? comments + scoped : scoped;
    }

    static string StripComments(string text, StringBuilder comments)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                comments.Append(text, index, stop - index).Append('\n');
                index = stop;
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    static string ScopeSelector(string selector, string attribute)
    {
        var deep = selector.IndexOf("::deep", StringComparison.OrdinalIgnoreCase);

        if (deep >= 0)
        {
            var before = selector.Substring(0, deep);
            var after = selector.Substring(deep + "::deep".Length);
            var core = before.TrimEnd();
            var gap = before.Substring(core.Length);

            return core.Length > 0
                ? core + attribute + gap + after
                : attribute + gap + after;
        }

        var trimmed = selector.TrimEnd();
        var trailing = selector.Substring(trimmed.Length);

        // A pseudo-element comes after the scope, so tuck the scope in front of it.
        foreach (var pseudo in PseudoElements)
        {
            if (trimmed.EndsWith(pseudo, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(0, trimmed.Length - pseudo.Length) + attribute + pseudo + trailing;
            }
        }

        return trimmed + attribute + trailing;
    }

    /// <summary>Splits on the commas that separate selectors, ignoring commas inside parentheses,
    /// brackets, comments and strings (as in <c>:is(a, b)</c>).</summary>
    static List<string> SplitSelectors(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var index = 0;

        while (index < text.Length)
        {
            var c = text[index];

            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                index = SkipString(text, index);
                continue;
            }

            if (c == '(' || c == '[')
            {
                depth++;
            }
            else if (c == ')' || c == ']')
            {
                if (depth > 0) depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text.Substring(start, index - start));
                start = index + 1;
            }

            index++;
        }

        parts.Add(text.Substring(start));
        return parts;
    }

    /// <summary>Finds the next <paramref name="target"/> that is a structural character, i.e. not
    /// inside a comment or a string.</summary>
    static int IndexOfStructural(string text, int start, char target)
    {
        var index = start;

        while (index < text.Length)
        {
            var c = text[index];

            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                index = SkipString(text, index);
                continue;
            }

            if (c == target) return index;
            index++;
        }

        return -1;
    }

    static int SkipString(string text, int start)
    {
        var quote = text[start];
        var index = start + 1;

        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == quote) return index + 1;
            index++;
        }

        return text.Length;
    }

    static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        var index = open;

        while (index < text.Length)
        {
            var c = text[index];

            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                index = SkipString(text, index);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return index;
            }

            index++;
        }

        return -1;
    }
}
