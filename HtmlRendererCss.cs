using System.Globalization;
using System.Text;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;

namespace PhantomHaze;

// The style engine of the HTMLRenderer and HazeGate protocols. HTML Renderer only understands CSS 2 (and a few extras),
// while websites are written for modern browsers. Instead of handing it the page's stylesheets, PhantomHaze works the
// cascade out itself, the way a browser does - media queries, @supports, @layer, nesting, CSS variables, !important,
// every selector AngleSharp can match - and gives each element the result as an inline style written in the CSS that HTML
// Renderer can draw (px, colours as #rrggbb/rgba, corner-radius, background-gradient...). The page markup itself is
// written by PageMarkupWriter (HtmlRendererEngine.cs), which also approximates flex and grid layouts with tables.
// Nothing in this file uses Windows Forms, so it can be tested on its own.

/// <summary>Where a stylesheet comes from; later origins win ties.</summary>
internal enum CssOrigin
{
    /// <summary>PhantomHaze's own browser defaults (the display of HTML5 elements and similar).</summary>
    UserAgent,

    /// <summary>The page's own stylesheets, &lt;style&gt; elements and style attributes.</summary>
    Author,

    /// <summary>Extensions' web.css rules: added after the page's own styles, like a stylesheet appended to the page.</summary>
    Extension,
}

/// <summary>One declaration: a lower-case property name (custom properties keep their case) and its raw value.</summary>
internal sealed class CssDeclarationItem
{
    public CssDeclarationItem(string name, string value, bool important)
    {
        Name = name;
        Value = value;
        Important = important;
    }

    public string Name { get; }
    public string Value { get; }
    public bool Important { get; }
}

/// <summary>A style rule after media queries, @supports, @layer and nesting were worked out.</summary>
internal sealed class CssStyleRuleItem
{
    public CssStyleRuleItem(string selectorText, List<CssDeclarationItem> normal, List<CssDeclarationItem> important, int order, int layerRank, CssOrigin origin)
    {
        SelectorText = selectorText;
        Normal = normal;
        Important = important;
        Order = order;
        LayerRank = layerRank;
        Origin = origin;
    }

    public string SelectorText { get; }
    public List<CssDeclarationItem> Normal { get; }
    public List<CssDeclarationItem> Important { get; }
    public int Order { get; }

    /// <summary>The cascade layer (@layer): earlier layers lose to later ones and to styles outside layers.</summary>
    public int LayerRank { get; }

    public CssOrigin Origin { get; }
}

/// <summary>A stylesheet's text and where its relative addresses point to.</summary>
internal sealed class CssSheetSource
{
    public CssSheetSource(string text, Uri? baseUri, string? media, CssOrigin origin)
    {
        Text = text ?? string.Empty;
        BaseUri = baseUri;
        Media = media;
        Origin = origin;
    }

    public string Text { get; }
    public Uri? BaseUri { get; }

    /// <summary>The media attribute of the &lt;link&gt; or &lt;style&gt; element (null: all media).</summary>
    public string? Media { get; }

    public CssOrigin Origin { get; }
}

/// <summary>What media queries are evaluated against: the Web View page area, in CSS pixels.</summary>
internal sealed class CssMediaContext
{
    public double WidthPx { get; init; } = 1024;
    public double HeightPx { get; init; } = 768;
    public double DevicePixelRatio { get; init; } = 1;
    public bool ScriptingEnabled { get; init; } = true;
    public bool PrefersDark { get; init; }
}

// ---- Parsing ---------------------------------------------------------------------------------------------------------

/// <summary>A rule of a stylesheet before it is interpreted. Blocks are ranges of the stylesheet text.</summary>
internal abstract class CssNode
{
    protected CssNode(string css, string prelude, int blockStart, int blockEnd)
    {
        Css = css;
        Prelude = prelude;
        BlockStart = blockStart;
        BlockEnd = blockEnd;
    }

    public string Css { get; }
    public string Prelude { get; }

    /// <summary>The first character inside the block's braces, or -1 for a statement without a block.</summary>
    public int BlockStart { get; }

    /// <summary>The closing brace of the block (or the end of the text when it was never closed).</summary>
    public int BlockEnd { get; }

    public bool HasBlock => BlockStart >= 0;
}

internal sealed class CssQualifiedNode : CssNode
{
    public CssQualifiedNode(string css, string prelude, int blockStart, int blockEnd)
        : base(css, prelude, blockStart, blockEnd)
    {
    }
}

internal sealed class CssAtNode : CssNode
{
    public CssAtNode(string css, string name, string prelude, int blockStart, int blockEnd)
        : base(css, prelude, blockStart, blockEnd)
    {
        Name = name;
    }

    /// <summary>The at-keyword without '@', lower-case and without a vendor prefix ("media", "supports", "keyframes").</summary>
    public string Name { get; }
}

/// <summary>A tolerant CSS parser (CSS Syntax Level 3 at the level of rules and declarations). Invalid parts are skipped.</summary>
internal static class CssSheetParser
{
    private const int MaximumNesting = 24;

    public static List<CssNode> Parse(string css) => ParseNodes(css, 0, css.Length, 0);

    public static List<CssNode> ParseNodes(string css, int start, int end, int depth)
    {
        var nodes = new List<CssNode>();
        int i = start;
        while (i < end)
        {
            i = SkipWhitespaceAndComments(css, i, end);
            if (i >= end)
            {
                break;
            }

            char character = css[i];
            if (character is '}' or ';')
            {
                i++;
                continue;
            }

            // HTML comment tokens are allowed at the top level of a stylesheet and ignored.
            if (string.CompareOrdinal(css, i, "<!--", 0, 4) == 0)
            {
                i += 4;
                continue;
            }

            if (string.CompareOrdinal(css, i, "-->", 0, 3) == 0)
            {
                i += 3;
                continue;
            }

            if (character == '@')
            {
                int nameEnd = ReadIdentifierEnd(css, i + 1, end);
                string name = NormalizeAtName(css[(i + 1)..nameEnd]);
                int stop = FindPreludeEnd(css, nameEnd, end);
                string prelude = css[nameEnd..Math.Min(stop, end)].Trim();
                if (stop < end && css[stop] == '{')
                {
                    int close = FindMatchingBrace(css, stop, end);
                    if (depth < MaximumNesting)
                    {
                        nodes.Add(new CssAtNode(css, name, prelude, stop + 1, close));
                    }

                    i = close + 1;
                }
                else
                {
                    nodes.Add(new CssAtNode(css, name, prelude, -1, -1));
                    i = stop + 1;
                }

                continue;
            }

            int preludeEnd = FindPreludeEnd(css, i, end);
            if (preludeEnd >= end)
            {
                break;
            }

            if (css[preludeEnd] == ';')
            {
                // A declaration (or garbage) outside a rule.
                i = preludeEnd + 1;
                continue;
            }

            int blockEnd = FindMatchingBrace(css, preludeEnd, end);
            if (depth < MaximumNesting)
            {
                nodes.Add(new CssQualifiedNode(css, StripComments(css[i..preludeEnd]).Trim(), preludeEnd + 1, blockEnd));
            }

            i = blockEnd + 1;
        }

        return nodes;
    }

    /// <summary>Reads the declarations of a block and the rules nested in it (CSS nesting).</summary>
    public static void ParseBlock(string css, int start, int end, List<CssDeclarationItem> declarations, List<CssNode>? nested, int depth)
    {
        int i = start;
        end = Math.Min(end, css.Length);
        while (i < end)
        {
            i = SkipWhitespaceAndComments(css, i, end);
            if (i >= end)
            {
                break;
            }

            if (css[i] == ';')
            {
                i++;
                continue;
            }

            if (css[i] == '@')
            {
                int nameEnd = ReadIdentifierEnd(css, i + 1, end);
                string name = NormalizeAtName(css[(i + 1)..nameEnd]);
                int stop = FindPreludeEnd(css, nameEnd, end);
                string prelude = css[nameEnd..Math.Min(stop, end)].Trim();
                if (stop < end && css[stop] == '{')
                {
                    int close = FindMatchingBrace(css, stop, end);
                    if (nested is not null && depth < MaximumNesting)
                    {
                        nested.Add(new CssAtNode(css, name, prelude, stop + 1, close));
                    }

                    i = close + 1;
                }
                else
                {
                    i = stop + 1;
                }

                continue;
            }

            int segmentEnd = FindPreludeEnd(css, i, end);
            if (segmentEnd < end && css[segmentEnd] == '{')
            {
                int close = FindMatchingBrace(css, segmentEnd, end);
                if (nested is not null && depth < MaximumNesting)
                {
                    nested.Add(new CssQualifiedNode(css, StripComments(css[i..segmentEnd]).Trim(), segmentEnd + 1, close));
                }

                i = close + 1;
                continue;
            }

            int declarationEnd = Math.Min(segmentEnd, end);
            ParseDeclaration(css, i, declarationEnd, declarations);
            i = declarationEnd + 1;
        }
    }

    /// <summary>The declarations of a style attribute.</summary>
    public static List<CssDeclarationItem> ParseDeclarationList(string text)
    {
        var declarations = new List<CssDeclarationItem>();
        ParseBlock(text, 0, text.Length, declarations, nested: null, depth: 0);
        return declarations;
    }

    private static void ParseDeclaration(string css, int start, int end, List<CssDeclarationItem> declarations)
    {
        int colon = -1;
        for (int i = start; i < end; i++)
        {
            if (css[i] == ':')
            {
                colon = i;
                break;
            }

            if (css[i] is '{' or '}' or '(' or '"' or '\'')
            {
                return;
            }
        }

        if (colon <= start)
        {
            return;
        }

        string name = StripComments(css[start..colon]).Trim();
        if (name.Length == 0 || name.Length > 120)
        {
            return;
        }

        bool custom = name.StartsWith("--", StringComparison.Ordinal);
        if (!custom)
        {
            name = name.ToLowerInvariant();
            // A *property or _property hack from old IE versions.
            if (name[0] is '*' or '_')
            {
                return;
            }
        }

        string value = custom ? css[(colon + 1)..end] : StripComments(css[(colon + 1)..end]);
        value = value.Trim();
        bool important = false;
        int bang = FindImportant(value);
        if (bang >= 0)
        {
            important = true;
            value = value[..bang].TrimEnd();
        }

        if (value.Length == 0 && !custom)
        {
            return;
        }

        declarations.Add(new CssDeclarationItem(name, value, important));
    }

    private static int FindImportant(string value)
    {
        int bang = value.LastIndexOf('!');
        if (bang < 0)
        {
            return -1;
        }

        string rest = value[(bang + 1)..].Trim();
        return rest.Equals("important", StringComparison.OrdinalIgnoreCase) ? bang : -1;
    }

    private static string NormalizeAtName(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.StartsWith('-'))
        {
            // @-webkit-keyframes, @-moz-document, @-ms-viewport...
            int dash = lower.IndexOf('-', 1);
            if (dash > 0)
            {
                lower = lower[(dash + 1)..];
            }
        }

        return lower;
    }

    public static int SkipWhitespaceAndComments(string css, int i, int end)
    {
        while (i < end)
        {
            char character = css[i];
            if (char.IsWhiteSpace(character))
            {
                i++;
                continue;
            }

            if (character == '/' && i + 1 < end && css[i + 1] == '*')
            {
                int close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 || close >= end ? end : close + 2;
                continue;
            }

            break;
        }

        return i;
    }

    private static int ReadIdentifierEnd(string css, int i, int end)
    {
        while (i < end && (char.IsLetterOrDigit(css[i]) || css[i] is '-' or '_'))
        {
            i++;
        }

        return i;
    }

    /// <summary>The '{' or ';' that ends a prelude (outside strings, comments, brackets and parentheses), or <paramref name="end"/>.</summary>
    public static int FindPreludeEnd(string css, int i, int end)
    {
        int parentheses = 0;
        int brackets = 0;
        while (i < end)
        {
            char character = css[i];
            switch (character)
            {
                case '\\':
                    i += 2;
                    continue;
                case '"':
                case '\'':
                    i = SkipString(css, i, end);
                    continue;
                case '/' when i + 1 < end && css[i + 1] == '*':
                {
                    int close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 || close >= end ? end : close + 2;
                    continue;
                }
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses = Math.Max(0, parentheses - 1);
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets = Math.Max(0, brackets - 1);
                    break;
                case '{':
                    return i;
                case ';' when parentheses == 0 && brackets == 0:
                    return i;
                case '}' when parentheses == 0 && brackets == 0:
                    return i;
            }

            i++;
        }

        return end;
    }

    /// <summary>The '}' closing the block that opens at <paramref name="open"/> (or <paramref name="end"/> when it is never closed).</summary>
    public static int FindMatchingBrace(string css, int open, int end)
    {
        int depth = 0;
        int i = open;
        while (i < end)
        {
            char character = css[i];
            switch (character)
            {
                case '\\':
                    i += 2;
                    continue;
                case '"':
                case '\'':
                    i = SkipString(css, i, end);
                    continue;
                case '/' when i + 1 < end && css[i + 1] == '*':
                {
                    int close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 || close >= end ? end : close + 2;
                    continue;
                }
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }

            i++;
        }

        return end;
    }

    public static int SkipString(string css, int i, int end)
    {
        char quote = css[i];
        i++;
        while (i < end)
        {
            char character = css[i];
            if (character == '\\')
            {
                i += 2;
                continue;
            }

            if (character == quote || character == '\n')
            {
                return i + 1;
            }

            i++;
        }

        return end;
    }

    public static string StripComments(string text)
    {
        int comment = text.IndexOf("/*", StringComparison.Ordinal);
        if (comment < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char character = text[i];
            if (character is '"' or '\'')
            {
                int stringEnd = SkipString(text, i, text.Length);
                builder.Append(text, i, stringEnd - i);
                i = stringEnd;
                continue;
            }

            if (character == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Splits at top-level occurrences of <paramref name="separator"/> (outside strings, brackets and parentheses).</summary>
    public static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        int parentheses = 0;
        int brackets = 0;
        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char character = text[i];
            switch (character)
            {
                case '\\':
                    i += 2;
                    continue;
                case '"':
                case '\'':
                    i = SkipString(text, i, text.Length);
                    continue;
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses = Math.Max(0, parentheses - 1);
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets = Math.Max(0, brackets - 1);
                    break;
                default:
                    if (character == separator && parentheses == 0 && brackets == 0)
                    {
                        parts.Add(text[start..i]);
                        start = i + 1;
                    }

                    break;
            }

            i++;
        }

        parts.Add(text[start..Math.Min(text.Length, Math.Max(start, text.Length))]);
        return parts;
    }

    /// <summary>Splits a value into its space-separated components (functions, strings and brackets stay whole).</summary>
    public static List<string> SplitComponents(string value)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = -1;
        int i = 0;
        while (i < value.Length)
        {
            char character = value[i];
            if (character is '"' or '\'')
            {
                if (start < 0)
                {
                    start = i;
                }

                i = SkipString(value, i, value.Length);
                continue;
            }

            if (character == '\\')
            {
                if (start < 0)
                {
                    start = i;
                }

                i += 2;
                continue;
            }

            if (character is '(' or '[')
            {
                if (start < 0)
                {
                    start = i;
                }

                depth++;
            }
            else if (character is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && (char.IsWhiteSpace(character) || character == ','))
            {
                if (start >= 0)
                {
                    parts.Add(value[start..i]);
                    start = -1;
                }

                if (character == ',')
                {
                    parts.Add(",");
                }

                i++;
                continue;
            }
            else if (start < 0)
            {
                start = i;
            }

            i++;
        }

        if (start >= 0)
        {
            parts.Add(value[start..]);
        }

        return parts;
    }
}

// ---- Imports ---------------------------------------------------------------------------------------------------------

/// <summary>A parsed stylesheet with the stylesheets its @import rules brought in.</summary>
internal sealed class ParsedCssSheet
{
    public ParsedCssSheet(CssSheetSource source, List<CssNode> nodes)
    {
        Source = source;
        Nodes = nodes;
    }

    public CssSheetSource Source { get; }
    public List<CssNode> Nodes { get; }
    public Dictionary<CssAtNode, ParsedCssSheet> Imports { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>Limits how much @import can pull in for one page.</summary>
internal sealed class CssImportBudget
{
    public int Remaining { get; set; } = 30;
}

internal static class CssSheetLoader
{
    private const int MaximumImportDepth = 4;

    // Stylesheets that only declare web fonts (@font-face), which HTML Renderer cannot use anyway.
    private static readonly string[] FontOnlyHosts = ["fonts.googleapis.com", "use.typekit.net", "fonts.bunny.net", "use.fontawesome.com"];

    /// <summary>Parses a stylesheet and downloads the stylesheets it imports (in parallel, within the budget).</summary>
    public static async Task<ParsedCssSheet> LoadAsync(
        CssSheetSource source,
        Func<Uri, CancellationToken, Task<string?>>? fetch,
        CssImportBudget budget,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        var sheet = new ParsedCssSheet(source, CssSheetParser.Parse(source.Text));
        if (fetch is null || depth >= MaximumImportDepth || source.BaseUri is null)
        {
            return sheet;
        }

        var downloads = new List<(CssAtNode Node, Task<ParsedCssSheet?> Sheet)>();
        foreach (CssNode node in sheet.Nodes)
        {
            if (node is not CssAtNode { Name: "import" } import || budget.Remaining <= 0)
            {
                continue;
            }

            (string? address, string? media) = ParseImportPrelude(import.Prelude);
            if (address is null ||
                !Uri.TryCreate(source.BaseUri, address, out Uri? target) ||
                !(target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) ||
                FontOnlyHosts.Any(host => target.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            budget.Remaining--;
            downloads.Add((import, LoadImportAsync(target, media, source.Origin, fetch, budget, depth, cancellationToken)));
        }

        foreach ((CssAtNode node, Task<ParsedCssSheet?> task) in downloads)
        {
            ParsedCssSheet? imported = await task.ConfigureAwait(false);
            if (imported is not null)
            {
                sheet.Imports[node] = imported;
            }
        }

        return sheet;
    }

    private static async Task<ParsedCssSheet?> LoadImportAsync(
        Uri target,
        string? media,
        CssOrigin origin,
        Func<Uri, CancellationToken, Task<string?>> fetch,
        CssImportBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        try
        {
            string? text = await fetch(target, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return null;
            }

            return await LoadAsync(new CssSheetSource(text, target, media, origin), fetch, budget, cancellationToken, depth + 1).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The address and media list of an @import prelude (url("a.css") screen, "a.css" layer(x), ...).</summary>
    public static (string? Address, string? Media) ParseImportPrelude(string prelude)
    {
        string text = prelude.Trim();
        string? address = null;
        int rest;
        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = text.IndexOf(')');
            if (close < 0)
            {
                return (null, null);
            }

            address = text[4..close].Trim().Trim('"', '\'').Trim();
            rest = close + 1;
        }
        else if (text.Length > 0 && text[0] is '"' or '\'')
        {
            int close = CssSheetParser.SkipString(text, 0, text.Length);
            address = CssValues.UnescapeString(text[1..Math.Max(1, close - 1)]);
            rest = close;
        }
        else
        {
            return (null, null);
        }

        string media = text[Math.Min(rest, text.Length)..].Trim();
        // layer(...) and supports(...) come before the media list.
        while (media.StartsWith("layer", StringComparison.OrdinalIgnoreCase) || media.StartsWith("supports(", StringComparison.OrdinalIgnoreCase))
        {
            int open = media.IndexOf('(');
            if (media.StartsWith("layer", StringComparison.OrdinalIgnoreCase) && (open < 0 || open > 5))
            {
                media = media[5..].Trim();
                continue;
            }

            int close = FindClosingParenthesis(media, open);
            if (close < 0)
            {
                break;
            }

            if (media.StartsWith("supports(", StringComparison.OrdinalIgnoreCase) && !CssSupportsEvaluator.Matches(media[(open + 1)..close]))
            {
                return (null, null);
            }

            media = media[(close + 1)..].Trim();
        }

        return (address, media.Length == 0 ? null : media);
    }

    private static int FindClosingParenthesis(string text, int open)
    {
        if (open < 0)
        {
            return -1;
        }

        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }
}

// ---- Turning stylesheets into a list of style rules ------------------------------------------------------------------

/// <summary>Collects the style rules that apply, in cascade order, from parsed stylesheets.</summary>
internal sealed class CssRuleCollector
{
    // Styles outside any @layer beat every layer.
    public const int UnlayeredRank = int.MaxValue;

    private readonly CssMediaContext _media;
    private readonly Dictionary<string, int> _layers = new(StringComparer.OrdinalIgnoreCase);
    private int _order;
    private int _anonymousLayers;

    public CssRuleCollector(CssMediaContext media)
    {
        _media = media;
    }

    public List<CssStyleRuleItem> Rules { get; } = new();

    /// <summary>Stops collecting once a page declares this many rules (pages with more are drawn with the first ones).</summary>
    public int MaximumRules { get; init; } = 60_000;

    public void AddSheet(ParsedCssSheet sheet)
    {
        if (!CssMediaQueryEvaluator.Matches(sheet.Source.Media, _media))
        {
            return;
        }

        AddNodes(sheet, sheet.Nodes, parentSelectors: null, UnlayeredRank, string.Empty);
    }

    private void AddNodes(ParsedCssSheet sheet, List<CssNode> nodes, List<string>? parentSelectors, int layerRank, string layerPath)
    {
        foreach (CssNode node in nodes)
        {
            if (Rules.Count >= MaximumRules)
            {
                return;
            }

            switch (node)
            {
                case CssQualifiedNode rule:
                    AddStyleRule(sheet, rule, parentSelectors, layerRank, layerPath);
                    break;
                case CssAtNode at:
                    AddAtRule(sheet, at, parentSelectors, layerRank, layerPath);
                    break;
            }
        }
    }

    private void AddStyleRule(ParsedCssSheet sheet, CssQualifiedNode rule, List<string>? parentSelectors, int layerRank, string layerPath)
    {
        List<string> selectors = ResolveSelectors(rule.Prelude, parentSelectors);
        if (selectors.Count == 0)
        {
            return;
        }

        var declarations = new List<CssDeclarationItem>();
        var nested = new List<CssNode>();
        CssSheetParser.ParseBlock(rule.Css, rule.BlockStart, rule.BlockEnd, declarations, nested, 0);
        AddDeclarations(sheet, string.Join(", ", selectors), declarations, layerRank);
        if (nested.Count > 0)
        {
            AddNodes(sheet, nested, selectors, layerRank, layerPath);
        }
    }

    private void AddDeclarations(ParsedCssSheet sheet, string selectorText, List<CssDeclarationItem> declarations, int layerRank)
    {
        if (declarations.Count == 0)
        {
            return;
        }

        var normal = new List<CssDeclarationItem>(declarations.Count);
        var important = new List<CssDeclarationItem>();
        foreach (CssDeclarationItem declaration in declarations)
        {
            CssDeclarationItem resolved = sheet.Source.BaseUri is Uri baseUri && declaration.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)
                ? new CssDeclarationItem(declaration.Name, CssValues.MakeUrlsAbsolute(declaration.Value, baseUri), declaration.Important)
                : declaration;
            (resolved.Important ? important : normal).Add(resolved);
        }

        Rules.Add(new CssStyleRuleItem(selectorText, normal, important, _order++, layerRank, sheet.Source.Origin));
    }

    private void AddAtRule(ParsedCssSheet sheet, CssAtNode at, List<string>? parentSelectors, int layerRank, string layerPath)
    {
        switch (at.Name)
        {
            case "import":
                if (parentSelectors is null && sheet.Imports.TryGetValue(at, out ParsedCssSheet? imported) &&
                    CssMediaQueryEvaluator.Matches(imported.Source.Media, _media))
                {
                    int importLayer = layerRank;
                    if (at.Prelude.Contains("layer", StringComparison.OrdinalIgnoreCase))
                    {
                        importLayer = GetLayerRank(layerPath, ExtractImportLayerName(at.Prelude));
                    }

                    AddNodes(imported, imported.Nodes, null, importLayer, layerPath);
                }

                return;
            case "media":
                if (at.HasBlock && CssMediaQueryEvaluator.Matches(at.Prelude, _media))
                {
                    AddBlockContents(sheet, at, parentSelectors, layerRank, layerPath);
                }

                return;
            case "supports":
                if (at.HasBlock && CssSupportsEvaluator.Matches(at.Prelude))
                {
                    AddBlockContents(sheet, at, parentSelectors, layerRank, layerPath);
                }

                return;
            case "container":
                if (at.HasBlock && CssMediaQueryEvaluator.MatchesContainer(at.Prelude, _media))
                {
                    AddBlockContents(sheet, at, parentSelectors, layerRank, layerPath);
                }

                return;
            case "layer":
            {
                if (!at.HasBlock)
                {
                    // "@layer base, components;" only fixes the order of the layers.
                    foreach (string name in at.Prelude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        GetLayerRank(layerPath, name);
                    }

                    return;
                }

                string layerName = at.Prelude.Trim();
                if (layerName.Length == 0)
                {
                    layerName = "\u0001anonymous" + (_anonymousLayers++).ToString(CultureInfo.InvariantCulture);
                }

                int rank = GetLayerRank(layerPath, layerName);
                string path = layerPath.Length == 0 ? layerName : layerPath + "." + layerName;
                AddBlockContents(sheet, at, parentSelectors, rank, path);
                return;
            }
            case "scope":
                if (at.HasBlock)
                {
                    AddBlockContents(sheet, at, parentSelectors, layerRank, layerPath);
                }

                return;
            default:
                // @font-face, @keyframes, @page, @property, @counter-style, @namespace, @charset, @font-feature-values,
                // @starting-style, @view-transition, @-moz-document...: nothing HTML Renderer can use.
                return;
        }
    }

    private void AddBlockContents(ParsedCssSheet sheet, CssAtNode at, List<string>? parentSelectors, int layerRank, string layerPath)
    {
        if (parentSelectors is null)
        {
            AddNodes(sheet, CssSheetParser.ParseNodes(at.Css, at.BlockStart, at.BlockEnd, 1), null, layerRank, layerPath);
            return;
        }

        // Inside a style rule (nesting): the block holds declarations for the parent selector and nested rules.
        var declarations = new List<CssDeclarationItem>();
        var nested = new List<CssNode>();
        CssSheetParser.ParseBlock(at.Css, at.BlockStart, at.BlockEnd, declarations, nested, 1);
        AddDeclarations(sheet, string.Join(", ", parentSelectors), declarations, layerRank);
        if (nested.Count > 0)
        {
            AddNodes(sheet, nested, parentSelectors, layerRank, layerPath);
        }
    }

    private int GetLayerRank(string parentPath, string name)
    {
        string key = parentPath.Length == 0 ? name : parentPath + "." + name;
        if (!_layers.TryGetValue(key, out int rank))
        {
            rank = _layers.Count;
            _layers[key] = rank;
        }

        return rank;
    }

    private static string ExtractImportLayerName(string prelude)
    {
        int index = prelude.IndexOf("layer(", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "\u0001import";
        }

        int close = prelude.IndexOf(')', index);
        return close < 0 ? "\u0001import" : prelude[(index + 6)..close].Trim();
    }

    /// <summary>The selectors of a rule; nested rules are combined with their parent (CSS nesting, "&amp;").</summary>
    private static List<string> ResolveSelectors(string prelude, List<string>? parentSelectors)
    {
        var result = new List<string>();
        foreach (string part in CssSheetParser.SplitTopLevel(prelude, ','))
        {
            string selector = part.Trim();
            if (selector.Length == 0)
            {
                continue;
            }

            if (parentSelectors is null)
            {
                result.Add(selector);
                continue;
            }

            string parent = parentSelectors.Count == 1 ? parentSelectors[0] : ":is(" + string.Join(", ", parentSelectors) + ")";
            // "&:hover" and ".x &" use the parent where the '&' is; anything else ("> a", ".b") is relative to it.
            result.Add(selector.Contains('&')
                ? selector.Replace("&", parent, StringComparison.Ordinal)
                : parent + " " + selector);
        }

        return result;
    }
}

// ---- Media queries and @supports --------------------------------------------------------------------------------------

internal static class CssMediaQueryEvaluator
{
    /// <summary>True when the media query list (null or empty: all media) matches the Web View page.</summary>
    public static bool Matches(string? mediaList, CssMediaContext context)
    {
        if (string.IsNullOrWhiteSpace(mediaList))
        {
            return true;
        }

        foreach (string query in CssSheetParser.SplitTopLevel(mediaList, ','))
        {
            try
            {
                if (MatchesQuery(query.Trim(), context))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                // An invalid query matches nothing.
            }
        }

        return false;
    }

    /// <summary>Container queries are approximated with the page width (containers are usually about as wide).</summary>
    public static bool MatchesContainer(string prelude, CssMediaContext context)
    {
        string condition = prelude.Trim();
        int open = condition.IndexOf('(');
        if (open > 0 && !condition.StartsWith("not", StringComparison.OrdinalIgnoreCase) && !condition.StartsWith("style", StringComparison.OrdinalIgnoreCase))
        {
            // A container name comes first: "@container sidebar (min-width: 400px)".
            condition = condition[open..];
        }

        if (condition.StartsWith("style(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return condition.Length == 0 || Matches(condition, context);
    }

    private static bool MatchesQuery(string query, CssMediaContext context)
    {
        if (query.Length == 0)
        {
            return false;
        }

        var tokens = new MediaTokenizer(query);
        bool negate = false;
        string? first = tokens.PeekIdentifier();
        if (first is not null && first.Equals("not", StringComparison.OrdinalIgnoreCase) && tokens.PeekAfterIdentifierIsIdentifier())
        {
            tokens.ReadIdentifier();
            negate = true;
            first = tokens.PeekIdentifier();
        }
        else if (first is not null && first.Equals("only", StringComparison.OrdinalIgnoreCase))
        {
            tokens.ReadIdentifier();
            first = tokens.PeekIdentifier();
        }

        bool result;
        if (first is not null && !first.Equals("not", StringComparison.OrdinalIgnoreCase))
        {
            tokens.ReadIdentifier();
            result = MatchesMediaType(first);
            while (!tokens.AtEnd)
            {
                string? keyword = tokens.ReadIdentifier();
                if (keyword is null || !keyword.Equals("and", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                bool next = EvaluateConditionInParens(tokens, context);
                result = result && next;
            }
        }
        else
        {
            result = EvaluateCondition(tokens, context);
            if (!tokens.AtEnd)
            {
                return false;
            }
        }

        return negate ? !result : result;
    }

    private static bool MatchesMediaType(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "all" or "screen" => true,
            _ => false,
        };
    }

    private static bool EvaluateCondition(MediaTokenizer tokens, CssMediaContext context)
    {
        string? keyword = tokens.PeekIdentifier();
        if (keyword is not null && keyword.Equals("not", StringComparison.OrdinalIgnoreCase))
        {
            tokens.ReadIdentifier();
            return !EvaluateConditionInParens(tokens, context);
        }

        bool result = EvaluateConditionInParens(tokens, context);
        string? combinator = null;
        while (!tokens.AtEnd && tokens.PeekIdentifier() is string next &&
               (next.Equals("and", StringComparison.OrdinalIgnoreCase) || next.Equals("or", StringComparison.OrdinalIgnoreCase)))
        {
            tokens.ReadIdentifier();
            combinator ??= next.ToLowerInvariant();
            bool value = EvaluateConditionInParens(tokens, context);
            result = combinator == "and" ? result && value : result || value;
        }

        return result;
    }

    private static bool EvaluateConditionInParens(MediaTokenizer tokens, CssMediaContext context)
    {
        string? group = tokens.ReadParenthesized();
        if (group is null)
        {
            throw new FormatException("A media condition was expected.");
        }

        string inner = group.Trim();
        if (inner.StartsWith('(') || inner.StartsWith("not ", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("not(", StringComparison.OrdinalIgnoreCase))
        {
            var nested = new MediaTokenizer(inner);
            bool value = EvaluateCondition(nested, context);
            return nested.AtEnd && value;
        }

        return EvaluateFeature(inner, context);
    }

    private static bool EvaluateFeature(string feature, CssMediaContext context)
    {
        // Range syntax: (width >= 600px), (400px < width <= 700px).
        int comparison = feature.IndexOfAny(new[] { '<', '>', '=' });
        if (comparison >= 0)
        {
            return EvaluateRange(feature, context);
        }

        int colon = feature.IndexOf(':');
        string name = (colon < 0 ? feature : feature[..colon]).Trim().ToLowerInvariant();
        string? value = colon < 0 ? null : feature[(colon + 1)..].Trim().ToLowerInvariant();

        string prefix = string.Empty;
        if (name.StartsWith("min-", StringComparison.Ordinal))
        {
            prefix = "min";
            name = name[4..];
        }
        else if (name.StartsWith("max-", StringComparison.Ordinal))
        {
            prefix = "max";
            name = name[4..];
        }
        else if (name.StartsWith("-webkit-min-", StringComparison.Ordinal))
        {
            prefix = "min";
            name = name[12..];
        }
        else if (name.StartsWith("-webkit-max-", StringComparison.Ordinal))
        {
            prefix = "max";
            name = name[12..];
        }
        else if (name.StartsWith("-webkit-", StringComparison.Ordinal) || name.StartsWith("-moz-", StringComparison.Ordinal))
        {
            name = name[(name.IndexOf('-', 1) + 1)..];
        }

        double? actual = GetNumericFeature(name, context);
        if (actual is double number)
        {
            if (value is null)
            {
                return number != 0;
            }

            double? expected = ParseFeatureValue(name, value);
            if (expected is null)
            {
                return false;
            }

            return prefix switch
            {
                "min" => number >= expected.Value - 0.0001,
                "max" => number <= expected.Value + 0.0001,
                _ => Math.Abs(number - expected.Value) < 0.0001,
            };
        }

        string? keyword = GetKeywordFeature(name, context);
        if (keyword is null)
        {
            return false;
        }

        if (value is null)
        {
            return keyword is not ("none" or "no-preference" or "0");
        }

        return keyword.Split('|').Contains(value);
    }

    private static bool EvaluateRange(string feature, CssMediaContext context)
    {
        // Tokens: value op name [op value].
        var parts = new List<string>();
        var operators = new List<string>();
        int i = 0;
        var current = new StringBuilder();
        while (i < feature.Length)
        {
            char character = feature[i];
            if (character is '<' or '>' or '=')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                string op = character.ToString();
                if (i + 1 < feature.Length && feature[i + 1] == '=')
                {
                    op += "=";
                    i++;
                }

                operators.Add(op);
                i++;
                continue;
            }

            current.Append(character);
            i++;
        }

        parts.Add(current.ToString().Trim());
        if (parts.Count < 2 || parts.Count != operators.Count + 1)
        {
            return false;
        }

        int nameIndex = parts.FindIndex(part => part.Length > 0 && char.IsLetter(part[0]) && GetNumericFeature(part.ToLowerInvariant(), context) is not null);
        if (nameIndex < 0)
        {
            return false;
        }

        string name = parts[nameIndex].ToLowerInvariant();
        double actual = GetNumericFeature(name, context)!.Value;
        for (int index = 0; index < operators.Count; index++)
        {
            string left = parts[index];
            string right = parts[index + 1];
            double leftValue = index == nameIndex ? actual : ParseFeatureValue(name, left.ToLowerInvariant()) ?? double.NaN;
            double rightValue = index + 1 == nameIndex ? actual : ParseFeatureValue(name, right.ToLowerInvariant()) ?? double.NaN;
            if (double.IsNaN(leftValue) || double.IsNaN(rightValue))
            {
                return false;
            }

            bool ok = operators[index] switch
            {
                "<" => leftValue < rightValue,
                "<=" => leftValue <= rightValue + 0.0001,
                ">" => leftValue > rightValue,
                ">=" => leftValue >= rightValue - 0.0001,
                "=" => Math.Abs(leftValue - rightValue) < 0.0001,
                _ => false,
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static double? GetNumericFeature(string name, CssMediaContext context)
    {
        return name switch
        {
            "width" or "device-width" or "inline-size" => context.WidthPx,
            "height" or "device-height" or "block-size" => context.HeightPx,
            "aspect-ratio" or "device-aspect-ratio" => context.HeightPx <= 0 ? 1 : context.WidthPx / context.HeightPx,
            "resolution" => context.DevicePixelRatio,
            "device-pixel-ratio" => context.DevicePixelRatio,
            "color" => 8,
            "color-index" => 0,
            "monochrome" => 0,
            "grid" => 0,
            _ => null,
        };
    }

    private static double? ParseFeatureValue(string name, string value)
    {
        value = value.Trim();
        if (name is "aspect-ratio" or "device-aspect-ratio")
        {
            string[] ratio = value.Split('/', StringSplitOptions.TrimEntries);
            if (ratio.Length == 2 &&
                double.TryParse(ratio[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(ratio[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b != 0)
            {
                return a / b;
            }

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double single) ? single : null;
        }

        if (name is "resolution" or "device-pixel-ratio")
        {
            if (CssValues.TryParseNumberWithUnit(value, out double number, out string unit))
            {
                return unit switch
                {
                    "" or "x" or "dppx" => number,
                    "dpi" => number / 96.0,
                    "dpcm" => number * 2.54 / 96.0,
                    _ => null,
                };
            }

            return null;
        }

        if (CssValues.TryParseNumberWithUnit(value, out double length, out string lengthUnit))
        {
            return lengthUnit switch
            {
                "" => length,
                "px" => length,
                "em" or "rem" => length * 16,
                "pt" => length * 96 / 72,
                "pc" => length * 16,
                "in" => length * 96,
                "cm" => length * 96 / 2.54,
                "mm" => length * 96 / 25.4,
                "q" => length * 96 / 101.6,
                _ => null,
            };
        }

        return null;
    }

    private static string? GetKeywordFeature(string name, CssMediaContext context)
    {
        return name switch
        {
            "orientation" => context.HeightPx > context.WidthPx ? "portrait" : "landscape",
            "prefers-color-scheme" => context.PrefersDark ? "dark" : "light",
            "prefers-reduced-motion" or "prefers-reduced-transparency" or "prefers-reduced-data" or "prefers-contrast" => "no-preference",
            "forced-colors" or "inverted-colors" or "high-contrast" => "none",
            "hover" or "any-hover" => "hover",
            "pointer" or "any-pointer" => "fine",
            "scripting" => context.ScriptingEnabled ? "enabled" : "none",
            "update" => "fast",
            "overflow-block" or "overflow-inline" => "scroll",
            "display-mode" => "browser",
            "color-gamut" => "srgb",
            "dynamic-range" or "video-dynamic-range" => "standard",
            "transform-3d" => "0",
            _ => null,
        };
    }

    /// <summary>A minimal tokenizer for media queries: identifiers and parenthesized groups.</summary>
    private sealed class MediaTokenizer
    {
        private readonly string _text;
        private int _position;

        public MediaTokenizer(string text)
        {
            _text = text;
        }

        public bool AtEnd
        {
            get
            {
                SkipWhitespace();
                return _position >= _text.Length;
            }
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        public string? PeekIdentifier()
        {
            SkipWhitespace();
            int end = _position;
            while (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] is '-' or '_'))
            {
                end++;
            }

            return end > _position ? _text[_position..end] : null;
        }

        public bool PeekAfterIdentifierIsIdentifier()
        {
            int saved = _position;
            ReadIdentifier();
            string? next = PeekIdentifier();
            _position = saved;
            return next is not null;
        }

        public string? ReadIdentifier()
        {
            string? identifier = PeekIdentifier();
            if (identifier is not null)
            {
                _position += identifier.Length;
            }

            return identifier;
        }

        public string? ReadParenthesized()
        {
            SkipWhitespace();
            if (_position >= _text.Length || _text[_position] != '(')
            {
                return null;
            }

            int depth = 0;
            int start = _position;
            for (int i = _position; i < _text.Length; i++)
            {
                if (_text[i] == '(')
                {
                    depth++;
                }
                else if (_text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        _position = i + 1;
                        return _text[(start + 1)..i];
                    }
                }
            }

            return null;
        }
    }
}

internal static class CssSupportsEvaluator
{
    // Features PhantomHaze's layout does not have. Pages usually put their enhanced layout for them in @supports blocks
    // (with a simpler fallback outside), so reporting them as unsupported gives the better result.
    private static readonly HashSet<string> UnsupportedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "grid", "grid-template", "grid-template-columns", "grid-template-rows", "grid-template-areas", "grid-area",
        "aspect-ratio", "backdrop-filter", "-webkit-backdrop-filter", "container-type", "container", "contain",
        "content-visibility", "mask", "-webkit-mask", "mask-image", "-webkit-mask-image", "scroll-snap-type",
        "scroll-timeline", "animation-timeline", "view-transition-name", "anchor-name", "position-anchor",
    };

    public static bool Matches(string condition)
    {
        try
        {
            return Evaluate(condition.Trim());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static bool Evaluate(string condition)
    {
        if (condition.StartsWith("not", StringComparison.OrdinalIgnoreCase) && condition.Length > 3 && (char.IsWhiteSpace(condition[3]) || condition[3] == '('))
        {
            return !Evaluate(condition[3..].Trim());
        }

        List<(string Group, string? Combinator)> groups = SplitGroups(condition);
        if (groups.Count == 0)
        {
            return false;
        }

        bool result = EvaluateGroup(groups[0].Group);
        for (int i = 1; i < groups.Count; i++)
        {
            bool value = EvaluateGroup(groups[i].Group);
            result = string.Equals(groups[i].Combinator, "or", StringComparison.OrdinalIgnoreCase) ? result || value : result && value;
        }

        return result;
    }

    private static bool EvaluateGroup(string group)
    {
        string text = group.Trim();
        if (text.StartsWith("selector(", StringComparison.OrdinalIgnoreCase))
        {
            string selector = text[9..^1];
            return new CssSelectorParser().ParseSelector(selector) is not null;
        }

        if (text.StartsWith("font-tech(", StringComparison.OrdinalIgnoreCase) || text.StartsWith("font-format(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            text = text[1..^1].Trim();
        }

        if (text.StartsWith('(') || text.StartsWith("not", StringComparison.OrdinalIgnoreCase))
        {
            return Evaluate(text);
        }

        int colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        string property = text[..colon].Trim();
        string value = text[(colon + 1)..].Trim().ToLowerInvariant();
        if (UnsupportedProperties.Contains(property))
        {
            return false;
        }

        if (property.Equals("display", StringComparison.OrdinalIgnoreCase))
        {
            return value is not ("grid" or "inline-grid" or "subgrid" or "ruby" or "math");
        }

        if (property.Equals("position", StringComparison.OrdinalIgnoreCase))
        {
            return value is not ("sticky" or "-webkit-sticky");
        }

        return true;
    }

    private static List<(string Group, string? Combinator)> SplitGroups(string condition)
    {
        var groups = new List<(string, string?)>();
        int i = 0;
        string? combinator = null;
        while (i < condition.Length)
        {
            while (i < condition.Length && char.IsWhiteSpace(condition[i]))
            {
                i++;
            }

            if (i >= condition.Length)
            {
                break;
            }

            int start = i;
            if (condition[i] == '(' || (i + 1 < condition.Length && char.IsLetter(condition[i]) && condition.IndexOf('(', i) is int open && open > i &&
                                        condition[i..open].All(character => char.IsLetter(character) || character == '-')))
            {
                int depth = 0;
                while (i < condition.Length)
                {
                    if (condition[i] == '(')
                    {
                        depth++;
                    }
                    else if (condition[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }

                    i++;
                }

                groups.Add((condition[start..i], combinator));
                continue;
            }

            int wordEnd = i;
            while (wordEnd < condition.Length && char.IsLetter(condition[wordEnd]))
            {
                wordEnd++;
            }

            combinator = condition[i..wordEnd];
            i = Math.Max(wordEnd, i + 1);
        }

        return groups;
    }
}

// ---- Values ----------------------------------------------------------------------------------------------------------

/// <summary>An sRGB colour with alpha (0-1).</summary>
internal readonly record struct CssColor(byte R, byte G, byte B, double A)
{
    public static readonly CssColor Black = new(0, 0, 0, 1);
    public static readonly CssColor Transparent = new(0, 0, 0, 0);

    /// <summary>The colour as HTML Renderer understands it: #rrggbb, rgba(...) or transparent.</summary>
    public string ToCss()
    {
        if (A <= 0.004)
        {
            return "transparent";
        }

        if (A >= 0.996)
        {
            return string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"rgba({R}, {G}, {B}, {Math.Round(A, 3)})");
    }
}

internal static class CssValues
{
    private static readonly Dictionary<string, uint> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aliceblue"] = 0xF0F8FF, ["antiquewhite"] = 0xFAEBD7, ["aqua"] = 0x00FFFF, ["aquamarine"] = 0x7FFFD4, ["azure"] = 0xF0FFFF,
        ["beige"] = 0xF5F5DC, ["bisque"] = 0xFFE4C4, ["black"] = 0x000000, ["blanchedalmond"] = 0xFFEBCD, ["blue"] = 0x0000FF,
        ["blueviolet"] = 0x8A2BE2, ["brown"] = 0xA52A2A, ["burlywood"] = 0xDEB887, ["cadetblue"] = 0x5F9EA0, ["chartreuse"] = 0x7FFF00,
        ["chocolate"] = 0xD2691E, ["coral"] = 0xFF7F50, ["cornflowerblue"] = 0x6495ED, ["cornsilk"] = 0xFFF8DC, ["crimson"] = 0xDC143C,
        ["cyan"] = 0x00FFFF, ["darkblue"] = 0x00008B, ["darkcyan"] = 0x008B8B, ["darkgoldenrod"] = 0xB8860B, ["darkgray"] = 0xA9A9A9,
        ["darkgreen"] = 0x006400, ["darkgrey"] = 0xA9A9A9, ["darkkhaki"] = 0xBDB76B, ["darkmagenta"] = 0x8B008B, ["darkolivegreen"] = 0x556B2F,
        ["darkorange"] = 0xFF8C00, ["darkorchid"] = 0x9932CC, ["darkred"] = 0x8B0000, ["darksalmon"] = 0xE9967A, ["darkseagreen"] = 0x8FBC8F,
        ["darkslateblue"] = 0x483D8B, ["darkslategray"] = 0x2F4F4F, ["darkslategrey"] = 0x2F4F4F, ["darkturquoise"] = 0x00CED1, ["darkviolet"] = 0x9400D3,
        ["deeppink"] = 0xFF1493, ["deepskyblue"] = 0x00BFFF, ["dimgray"] = 0x696969, ["dimgrey"] = 0x696969, ["dodgerblue"] = 0x1E90FF,
        ["firebrick"] = 0xB22222, ["floralwhite"] = 0xFFFAF0, ["forestgreen"] = 0x228B22, ["fuchsia"] = 0xFF00FF, ["gainsboro"] = 0xDCDCDC,
        ["ghostwhite"] = 0xF8F8FF, ["gold"] = 0xFFD700, ["goldenrod"] = 0xDAA520, ["gray"] = 0x808080, ["green"] = 0x008000,
        ["greenyellow"] = 0xADFF2F, ["grey"] = 0x808080, ["honeydew"] = 0xF0FFF0, ["hotpink"] = 0xFF69B4, ["indianred"] = 0xCD5C5C,
        ["indigo"] = 0x4B0082, ["ivory"] = 0xFFFFF0, ["khaki"] = 0xF0E68C, ["lavender"] = 0xE6E6FA, ["lavenderblush"] = 0xFFF0F5,
        ["lawngreen"] = 0x7CFC00, ["lemonchiffon"] = 0xFFFACD, ["lightblue"] = 0xADD8E6, ["lightcoral"] = 0xF08080, ["lightcyan"] = 0xE0FFFF,
        ["lightgoldenrodyellow"] = 0xFAFAD2, ["lightgray"] = 0xD3D3D3, ["lightgreen"] = 0x90EE90, ["lightgrey"] = 0xD3D3D3, ["lightpink"] = 0xFFB6C1,
        ["lightsalmon"] = 0xFFA07A, ["lightseagreen"] = 0x20B2AA, ["lightskyblue"] = 0x87CEFA, ["lightslategray"] = 0x778899, ["lightslategrey"] = 0x778899,
        ["lightsteelblue"] = 0xB0C4DE, ["lightyellow"] = 0xFFFFE0, ["lime"] = 0x00FF00, ["limegreen"] = 0x32CD32, ["linen"] = 0xFAF0E6,
        ["magenta"] = 0xFF00FF, ["maroon"] = 0x800000, ["mediumaquamarine"] = 0x66CDAA, ["mediumblue"] = 0x0000CD, ["mediumorchid"] = 0xBA55D3,
        ["mediumpurple"] = 0x9370DB, ["mediumseagreen"] = 0x3CB371, ["mediumslateblue"] = 0x7B68EE, ["mediumspringgreen"] = 0x00FA9A, ["mediumturquoise"] = 0x48D1CC,
        ["mediumvioletred"] = 0xC71585, ["midnightblue"] = 0x191970, ["mintcream"] = 0xF5FFFA, ["mistyrose"] = 0xFFE4E1, ["moccasin"] = 0xFFE4B5,
        ["navajowhite"] = 0xFFDEAD, ["navy"] = 0x000080, ["oldlace"] = 0xFDF5E6, ["olive"] = 0x808000, ["olivedrab"] = 0x6B8E23,
        ["orange"] = 0xFFA500, ["orangered"] = 0xFF4500, ["orchid"] = 0xDA70D6, ["palegoldenrod"] = 0xEEE8AA, ["palegreen"] = 0x98FB98,
        ["paleturquoise"] = 0xAFEEEE, ["palevioletred"] = 0xDB7093, ["papayawhip"] = 0xFFEFD5, ["peachpuff"] = 0xFFDAB9, ["peru"] = 0xCD853F,
        ["pink"] = 0xFFC0CB, ["plum"] = 0xDDA0DD, ["powderblue"] = 0xB0E0E6, ["purple"] = 0x800080, ["rebeccapurple"] = 0x663399,
        ["red"] = 0xFF0000, ["rosybrown"] = 0xBC8F8F, ["royalblue"] = 0x4169E1, ["saddlebrown"] = 0x8B4513, ["salmon"] = 0xFA8072,
        ["sandybrown"] = 0xF4A460, ["seagreen"] = 0x2E8B57, ["seashell"] = 0xFFF5EE, ["sienna"] = 0xA0522D, ["silver"] = 0xC0C0C0,
        ["skyblue"] = 0x87CEEB, ["slateblue"] = 0x6A5ACD, ["slategray"] = 0x708090, ["slategrey"] = 0x708090, ["snow"] = 0xFFFAFA,
        ["springgreen"] = 0x00FF7F, ["steelblue"] = 0x4682B4, ["tan"] = 0xD2B48C, ["teal"] = 0x008080, ["thistle"] = 0xD8BFD8,
        ["tomato"] = 0xFF6347, ["turquoise"] = 0x40E0D0, ["violet"] = 0xEE82EE, ["wheat"] = 0xF5DEB3, ["white"] = 0xFFFFFF,
        ["whitesmoke"] = 0xF5F5F5, ["yellow"] = 0xFFFF00, ["yellowgreen"] = 0x9ACD32,
        // System colours, as a light theme shows them.
        ["canvas"] = 0xFFFFFF, ["canvastext"] = 0x000000, ["linktext"] = 0x0000EE, ["visitedtext"] = 0x551A8B, ["activetext"] = 0xFF0000,
        ["buttonface"] = 0xEFEFEF, ["buttontext"] = 0x000000, ["buttonborder"] = 0x767676, ["field"] = 0xFFFFFF, ["fieldtext"] = 0x000000,
        ["highlight"] = 0x3399FF, ["highlighttext"] = 0xFFFFFF, ["selecteditem"] = 0x3399FF, ["selecteditemtext"] = 0xFFFFFF,
        ["mark"] = 0xFFFF00, ["marktext"] = 0x000000, ["graytext"] = 0x6D6D6D, ["accentcolor"] = 0x0A64D8, ["accentcolortext"] = 0xFFFFFF,
        ["window"] = 0xFFFFFF, ["windowtext"] = 0x000000, ["windowframe"] = 0x646464, ["menu"] = 0xF0F0F0, ["menutext"] = 0x000000,
        ["buttonhighlight"] = 0xFFFFFF, ["buttonshadow"] = 0xA0A0A0, ["threedface"] = 0xF0F0F0, ["infobackground"] = 0xFFFFE1, ["infotext"] = 0x000000,
    };

    public static bool TryParseNumberWithUnit(string text, out double number, out string unit)
    {
        number = 0;
        unit = string.Empty;
        text = text.Trim();
        int i = 0;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        int digitsStart = i;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
        {
            i++;
        }

        if (i == digitsStart)
        {
            return false;
        }

        // Exponent: 1e3, 2.5E-2 (but not "1em").
        if (i + 1 < text.Length && text[i] is 'e' or 'E' && (char.IsAsciiDigit(text[i + 1]) || (text[i + 1] is '+' or '-' && i + 2 < text.Length && char.IsAsciiDigit(text[i + 2]))))
        {
            i += 2;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (!double.TryParse(text[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
        {
            return false;
        }

        unit = text[i..].ToLowerInvariant();
        return unit.Length == 0 || unit == "%" || unit.All(char.IsAsciiLetter);
    }

    public static string FormatNumber(double value)
    {
        return Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Rewrites url(...) references so they are absolute (stylesheets resolve them against their own address).</summary>
    public static string MakeUrlsAbsolute(string css, Uri baseUri)
    {
        var output = new StringBuilder(css.Length + 32);
        int i = 0;
        while (i < css.Length)
        {
            int start = css.IndexOf("url(", i, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                output.Append(css, i, css.Length - i);
                break;
            }

            int valueStart = start + 4;
            int end = css.IndexOf(')', valueStart);
            if (end < 0)
            {
                output.Append(css, i, css.Length - i);
                break;
            }

            output.Append(css, i, valueStart - i);
            string value = css[valueStart..end].Trim().Trim('"', '\'').Trim();
            if (value.Length > 0 &&
                !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith('#') &&
                Uri.TryCreate(baseUri, value, out Uri? absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                output.Append('"').Append(absolute.AbsoluteUri.Replace("\"", "%22", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                output.Append(css, valueStart, end - valueStart);
            }

            output.Append(')');
            i = end + 1;
        }

        return output.ToString();
    }

    /// <summary>Decodes the escapes of a CSS string or identifier ("\201C", "\"").</summary>
    public static string UnescapeString(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char character = text[i];
            if (character != '\\' || i + 1 >= text.Length)
            {
                builder.Append(character);
                i++;
                continue;
            }

            int hexStart = i + 1;
            int hexEnd = hexStart;
            while (hexEnd < text.Length && hexEnd - hexStart < 6 && Uri.IsHexDigit(text[hexEnd]))
            {
                hexEnd++;
            }

            if (hexEnd > hexStart)
            {
                int code = int.Parse(text[hexStart..hexEnd], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                {
                    builder.Append(char.ConvertFromUtf32(code));
                }
                else
                {
                    builder.Append('�');
                }

                i = hexEnd;
                if (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                continue;
            }

            if (text[i + 1] == '\n')
            {
                // An escaped newline continues the string.
                i += 2;
                continue;
            }

            builder.Append(text[i + 1]);
            i += 2;
        }

        return builder.ToString();
    }

    /// <summary>Replaces var(--x, fallback) with custom property values. Null when a variable is missing without fallback.</summary>
    public static string? ResolveVariables(string value, Func<string, string?> lookup, int depth = 0)
    {
        if (depth > 24)
        {
            return null;
        }

        int index = IndexOfFunction(value, "var(", 0);
        if (index < 0)
        {
            return value;
        }

        var output = new StringBuilder(value.Length + 32);
        int cursor = 0;
        while (index >= 0)
        {
            output.Append(value, cursor, index - cursor);
            int close = FindClosingParenthesis(value, index + 3);
            if (close < 0)
            {
                return null;
            }

            string inside = value[(index + 4)..close];
            int comma = IndexOfTopLevelComma(inside);
            string name = (comma < 0 ? inside : inside[..comma]).Trim();
            string? fallback = comma < 0 ? null : inside[(comma + 1)..];
            string? replacement = name.StartsWith("--", StringComparison.Ordinal) ? lookup(name) : null;
            if (replacement is not null)
            {
                replacement = ResolveVariables(replacement, lookup, depth + 1);
            }

            if (replacement is null || (replacement.Trim().Length == 0 && fallback is not null))
            {
                replacement = fallback is null ? null : ResolveVariables(fallback.Trim(), lookup, depth + 1);
            }

            if (replacement is null)
            {
                return null;
            }

            output.Append(replacement);
            cursor = close + 1;
            index = IndexOfFunction(value, "var(", cursor);
        }

        output.Append(value, cursor, value.Length - cursor);
        return output.ToString();
    }

    public static int IndexOfFunction(string value, string function, int start)
    {
        int index = value.IndexOf(function, start, StringComparison.OrdinalIgnoreCase);
        while (index > 0 && (char.IsLetterOrDigit(value[index - 1]) || value[index - 1] is '-' or '_'))
        {
            index = value.IndexOf(function, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return index;
    }

    public static int FindClosingParenthesis(string value, int open)
    {
        int depth = 0;
        for (int i = open; i < value.Length; i++)
        {
            char character = value[i];
            if (character is '"' or '\'')
            {
                i = CssSheetParser.SkipString(value, i, value.Length) - 1;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int IndexOfTopLevelComma(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character is '"' or '\'')
            {
                i = CssSheetParser.SkipString(text, i, text.Length) - 1;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- Colours ----

    public static bool TryParseColor(string? text, CssColor currentColor, out CssColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = CssColor.Transparent;
            return true;
        }

        if (value.Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
        {
            color = currentColor;
            return true;
        }

        if (value.StartsWith('#'))
        {
            return TryParseHexColor(value[1..], out color);
        }

        if (NamedColors.TryGetValue(value, out uint rgb))
        {
            color = new CssColor((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 1);
            return true;
        }

        int open = value.IndexOf('(');
        if (open <= 0 || !value.EndsWith(')'))
        {
            return false;
        }

        string function = value[..open].Trim().ToLowerInvariant();
        string arguments = value[(open + 1)..^1];
        try
        {
            switch (function)
            {
                case "rgb":
                case "rgba":
                    return TryParseRgb(arguments, currentColor, out color);
                case "hsl":
                case "hsla":
                    return TryParseHsl(arguments, out color);
                case "hwb":
                    return TryParseHwb(arguments, out color);
                case "oklch":
                case "oklab":
                case "lch":
                case "lab":
                    return TryParseLabFamily(function, arguments, out color);
                case "color-mix":
                    return TryParseColorMix(arguments, currentColor, out color);
                case "light-dark":
                {
                    List<string> parts = CssSheetParser.SplitTopLevel(arguments, ',');
                    return parts.Count == 2 && TryParseColor(parts[0], currentColor, out color);
                }
                case "color":
                    return TryParseColorFunction(arguments, out color);
                case "-webkit-gradient":
                case "linear-gradient":
                case "radial-gradient":
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return false;
        }

        return false;
    }

    private static bool TryParseHexColor(string hex, out CssColor color)
    {
        color = default;
        if (!hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        int Value(int start, int length)
        {
            string part = hex.Substring(start, length);
            if (length == 1)
            {
                part += part;
            }

            return int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        switch (hex.Length)
        {
            case 3:
                color = new CssColor((byte)Value(0, 1), (byte)Value(1, 1), (byte)Value(2, 1), 1);
                return true;
            case 4:
                color = new CssColor((byte)Value(0, 1), (byte)Value(1, 1), (byte)Value(2, 1), Value(3, 1) / 255.0);
                return true;
            case 6:
                color = new CssColor((byte)Value(0, 2), (byte)Value(2, 2), (byte)Value(4, 2), 1);
                return true;
            case 8:
                color = new CssColor((byte)Value(0, 2), (byte)Value(2, 2), (byte)Value(4, 2), Value(6, 2) / 255.0);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The components of a colour function: "1, 2, 3, .5", "1 2 3 / 50%" or "from ..." (relative colours are not supported).</summary>
    private static List<string> SplitColorArguments(string arguments, out string? alpha)
    {
        alpha = null;
        string text = arguments.Trim();
        int slash = text.IndexOf('/');
        if (slash >= 0)
        {
            alpha = text[(slash + 1)..].Trim();
            text = text[..slash];
        }

        List<string> parts = text.Contains(',')
            ? text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : text.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (alpha is null && parts.Count == 4)
        {
            alpha = parts[3];
            parts.RemoveAt(3);
        }

        return parts;
    }

    private static double ParseAlpha(string? alpha)
    {
        if (alpha is null)
        {
            return 1;
        }

        string value = alpha.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (value.EndsWith('%'))
        {
            return Math.Clamp(double.Parse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0, 0, 1);
        }

        return Math.Clamp(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture), 0, 1);
    }

    private static double ParseComponent(string value, double percentScale)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (value.EndsWith('%'))
        {
            return double.Parse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0 * percentScale;
        }

        return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static double ParseHue(string value)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (TryParseNumberWithUnit(value, out double number, out string unit))
        {
            return unit switch
            {
                "" or "deg" => number,
                "rad" => number * 180 / Math.PI,
                "grad" => number * 0.9,
                "turn" => number * 360,
                _ => throw new FormatException("Unknown angle unit."),
            };
        }

        throw new FormatException("Not a hue.");
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static bool TryParseRgb(string arguments, CssColor currentColor, out CssColor color)
    {
        color = default;
        if (arguments.TrimStart().StartsWith("from ", StringComparison.OrdinalIgnoreCase))
        {
            color = currentColor;
            return false;
        }

        List<string> parts = SplitColorArguments(arguments, out string? alpha);
        if (parts.Count != 3)
        {
            return false;
        }

        color = new CssColor(
            ToByte(ParseComponent(parts[0], 255)),
            ToByte(ParseComponent(parts[1], 255)),
            ToByte(ParseComponent(parts[2], 255)),
            ParseAlpha(alpha));
        return true;
    }

    private static bool TryParseHsl(string arguments, out CssColor color)
    {
        color = default;
        List<string> parts = SplitColorArguments(arguments, out string? alpha);
        if (parts.Count != 3)
        {
            return false;
        }

        // Saturation and lightness are percentages ("50%", or "50" in the modern syntax).
        double hue = ParseHue(parts[0]);
        double saturation = Math.Clamp(ParseComponent(parts[1].TrimEnd('%'), 1) / 100, 0, 1);
        double lightness = Math.Clamp(ParseComponent(parts[2].TrimEnd('%'), 1) / 100, 0, 1);
        (double r, double g, double b) = HslToRgb(hue, saturation, lightness);
        color = new CssColor(ToByte(r * 255), ToByte(g * 255), ToByte(b * 255), ParseAlpha(alpha));
        return true;
    }

    private static (double R, double G, double B) HslToRgb(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360;
        double Channel(double n)
        {
            double k = (n + (hue / 30)) % 12;
            double a = saturation * Math.Min(lightness, 1 - lightness);
            return lightness - (a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
        }

        return (Channel(0), Channel(8), Channel(4));
    }

    private static bool TryParseHwb(string arguments, out CssColor color)
    {
        color = default;
        List<string> parts = SplitColorArguments(arguments, out string? alpha);
        if (parts.Count != 3)
        {
            return false;
        }

        double hue = ParseHue(parts[0]);
        double white = Math.Clamp(ParseComponent(parts[1].TrimEnd('%'), 1) / 100, 0, 1);
        double black = Math.Clamp(ParseComponent(parts[2].TrimEnd('%'), 1) / 100, 0, 1);
        if (white + black >= 1)
        {
            byte gray = ToByte(white / (white + black) * 255);
            color = new CssColor(gray, gray, gray, ParseAlpha(alpha));
            return true;
        }

        (double r, double g, double b) = HslToRgb(hue, 1, 0.5);
        double Mix(double channel) => (channel * (1 - white - black)) + white;
        color = new CssColor(ToByte(Mix(r) * 255), ToByte(Mix(g) * 255), ToByte(Mix(b) * 255), ParseAlpha(alpha));
        return true;
    }

    private static bool TryParseLabFamily(string function, string arguments, out CssColor color)
    {
        color = default;
        List<string> parts = SplitColorArguments(arguments, out string? alpha);
        if (parts.Count != 3)
        {
            return false;
        }

        double l;
        double a;
        double b;
        if (function is "oklch" or "oklab")
        {
            // Lightness is 0-1 or 0%-100%.
            l = ParseComponent(parts[0], 1);
            if (function == "oklch")
            {
                double chroma = ParseComponent(parts[1], 0.4);
                double hue = ParseHue(parts[2]) * Math.PI / 180;
                a = chroma * Math.Cos(hue);
                b = chroma * Math.Sin(hue);
            }
            else
            {
                a = ParseComponent(parts[1], 0.4);
                b = ParseComponent(parts[2], 0.4);
            }

            (double r, double g, double bl) = OklabToLinearSrgb(l, a, b);
            color = new CssColor(ToByte(LinearToSrgb(r) * 255), ToByte(LinearToSrgb(g) * 255), ToByte(LinearToSrgb(bl) * 255), ParseAlpha(alpha));
            return true;
        }

        l = ParseComponent(parts[0].TrimEnd('%'), 1);
        if (function == "lch")
        {
            double chroma = ParseComponent(parts[1], 150);
            double hue = ParseHue(parts[2]) * Math.PI / 180;
            a = chroma * Math.Cos(hue);
            b = chroma * Math.Sin(hue);
        }
        else
        {
            a = ParseComponent(parts[1], 125);
            b = ParseComponent(parts[2], 125);
        }

        (double x, double y, double z) = LabToXyzD50(l, a, b);
        (double lr, double lg, double lb) = XyzD50ToLinearSrgb(x, y, z);
        color = new CssColor(ToByte(LinearToSrgb(lr) * 255), ToByte(LinearToSrgb(lg) * 255), ToByte(LinearToSrgb(lb) * 255), ParseAlpha(alpha));
        return true;
    }

    private static (double R, double G, double B) OklabToLinearSrgb(double l, double a, double b)
    {
        double l1 = l + (0.3963377774 * a) + (0.2158037573 * b);
        double m1 = l - (0.1055613458 * a) - (0.0638541728 * b);
        double s1 = l - (0.0894841775 * a) - (1.2914855480 * b);
        double l3 = l1 * l1 * l1;
        double m3 = m1 * m1 * m1;
        double s3 = s1 * s1 * s1;
        return (
            (4.0767416621 * l3) - (3.3077115913 * m3) + (0.2309699292 * s3),
            (-1.2684380046 * l3) + (2.6097574011 * m3) - (0.3413193965 * s3),
            (-0.0041960863 * l3) - (0.7034186147 * m3) + (1.7076147010 * s3));
    }

    private static (double X, double Y, double Z) LabToXyzD50(double l, double a, double b)
    {
        const double kappa = 24389.0 / 27.0;
        const double epsilon = 216.0 / 24389.0;
        double fy = (l + 16) / 116;
        double fx = (a / 500) + fy;
        double fz = fy - (b / 200);
        double x = Math.Pow(fx, 3) > epsilon ? Math.Pow(fx, 3) : ((116 * fx) - 16) / kappa;
        double y = l > kappa * epsilon ? Math.Pow((l + 16) / 116, 3) : l / kappa;
        double z = Math.Pow(fz, 3) > epsilon ? Math.Pow(fz, 3) : ((116 * fz) - 16) / kappa;
        return (x * 0.3457 / 0.3585, y, z * (1 - 0.3457 - 0.3585) / 0.3585);
    }

    private static (double R, double G, double B) XyzD50ToLinearSrgb(double x, double y, double z)
    {
        // Bradford adaptation D50 -> D65, then XYZ -> linear sRGB.
        double x65 = (0.9554734527042182 * x) - (0.023098536874261423 * y) + (0.0632593086610217 * z);
        double y65 = (-0.028369706963208136 * x) + (1.0099954580058226 * y) + (0.021041398966943008 * z);
        double z65 = (0.012314001688319899 * x) - (0.020507696433477912 * y) + (1.3303659366080753 * z);
        return (
            (3.2409699419045226 * x65) - (1.537383177570094 * y65) - (0.4986107602930034 * z65),
            (-0.9692436362808796 * x65) + (1.8759675015077202 * y65) + (0.04155505740717559 * z65),
            (0.05563007969699366 * x65) - (0.20397695888897652 * y65) + (1.0569715142428786 * z65));
    }

    private static double LinearToSrgb(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= 0.0031308 ? 12.92 * value : (1.055 * Math.Pow(value, 1 / 2.4)) - 0.055;
    }

    private static bool TryParseColorFunction(string arguments, out CssColor color)
    {
        color = default;
        List<string> parts = SplitColorArguments(arguments, out string? alpha);
        if (parts.Count != 4)
        {
            return false;
        }

        string space = parts[0].ToLowerInvariant();
        if (space is not ("srgb" or "srgb-linear" or "display-p3"))
        {
            return false;
        }

        double r = ParseComponent(parts[1], 1);
        double g = ParseComponent(parts[2], 1);
        double b = ParseComponent(parts[3], 1);
        if (space == "srgb-linear")
        {
            r = LinearToSrgb(r);
            g = LinearToSrgb(g);
            b = LinearToSrgb(b);
        }

        color = new CssColor(ToByte(r * 255), ToByte(g * 255), ToByte(b * 255), ParseAlpha(alpha));
        return true;
    }

    private static bool TryParseColorMix(string arguments, CssColor currentColor, out CssColor color)
    {
        color = default;
        List<string> parts = CssSheetParser.SplitTopLevel(arguments, ',');
        if (parts.Count != 3)
        {
            return false;
        }

        (CssColor first, double? firstPercent) = ParseMixPart(parts[1], currentColor);
        (CssColor second, double? secondPercent) = ParseMixPart(parts[2], currentColor);
        double p1 = firstPercent ?? (secondPercent is double s ? 100 - s : 50);
        double p2 = secondPercent ?? (100 - p1);
        double total = p1 + p2;
        if (total <= 0)
        {
            return false;
        }

        double w1 = p1 / total;
        double w2 = p2 / total;
        double alpha = (first.A * w1) + (second.A * w2);
        double Mix(byte a, byte b) => alpha <= 0 ? 0 : ((a * first.A * w1) + (b * second.A * w2)) / alpha;
        color = new CssColor(ToByte(Mix(first.R, second.R)), ToByte(Mix(first.G, second.G)), ToByte(Mix(first.B, second.B)), alpha * Math.Min(1, total / 100));
        return true;
    }

    private static (CssColor Color, double? Percent) ParseMixPart(string part, CssColor currentColor)
    {
        double? percent = null;
        string colorText = part.Trim();
        foreach (string component in CssSheetParser.SplitComponents(colorText))
        {
            if (component.EndsWith('%') && double.TryParse(component[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                percent = value;
                colorText = colorText.Replace(component, string.Empty, StringComparison.Ordinal).Trim();
            }
        }

        if (!TryParseColor(colorText, currentColor, out CssColor color))
        {
            throw new FormatException("Not a colour.");
        }

        return (color, percent);
    }
}

// ---- The cascade ------------------------------------------------------------------------------------------------------

/// <summary>A selector compiled once per page, with the key that decides which elements are even checked against it.</summary>
internal sealed class CompiledSelector
{
    public CompiledSelector(ISelector selector, CssStyleRuleItem rule, int specificity, PseudoElementKind pseudo)
    {
        Selector = selector;
        Rule = rule;
        Specificity = specificity;
        Pseudo = pseudo;
    }

    public ISelector Selector { get; }
    public CssStyleRuleItem Rule { get; }
    public int Specificity { get; }
    public PseudoElementKind Pseudo { get; }
}

internal enum PseudoElementKind
{
    None,
    Before,
    After,
}

/// <summary>The result of the cascade for one element: property values in the order they must be applied.</summary>
internal sealed class CascadedStyle
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public IEnumerable<KeyValuePair<string, string>> Entries
    {
        get
        {
            foreach (string name in _order)
            {
                if (_values.TryGetValue(name, out string? value))
                {
                    yield return new KeyValuePair<string, string>(name, value);
                }
            }
        }
    }

    public int Count => _values.Count;

    public void Set(string name, string value)
    {
        if (_values.ContainsKey(name))
        {
            // A later declaration wins and moves to the end, so shorthands and longhands keep their relative order.
            _order.Remove(name);
        }

        _values[name] = value;
        _order.Add(name);
    }

    public string? Get(string name) => _values.TryGetValue(name, out string? value) ? value : null;

    public bool Has(string name) => _values.ContainsKey(name);

    public void Remove(string name)
    {
        if (_values.Remove(name))
        {
            _order.Remove(name);
        }
    }
}

/// <summary>
/// Indexes a page's style rules by the id, class or tag of each selector's last part, so an element is only checked
/// against selectors that can possibly match it (most rules of a framework stylesheet never match anything on a page).
/// </summary>
internal sealed class CssCascadeIndex
{
    private static readonly string[] DynamicPseudoClasses =
    [
        ":hover", ":active", ":focus", ":focus-visible", ":focus-within", ":visited", ":target", ":target-within",
        ":current", ":past", ":future", ":playing", ":paused", ":user-invalid", ":user-valid", ":autofill",
        ":-webkit-autofill", ":fullscreen", ":modal", ":picture-in-picture", ":popover-open", ":hover-within", ":drop",
        ":local-link", ":blank", ":seeking", ":stalled", ":buffering", ":muted", ":volume-locked",
    ];

    private readonly Dictionary<string, List<CompiledSelector>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CompiledSelector>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CompiledSelector>> _byTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CompiledSelector> _universal = new();

    public int SelectorCount { get; private set; }

    public CssCascadeIndex(IEnumerable<CssStyleRuleItem> rules)
    {
        var parser = new CssSelectorParser();
        var cache = new Dictionary<string, ISelector?>(StringComparer.Ordinal);
        foreach (CssStyleRuleItem rule in rules)
        {
            foreach (string part in CssSheetParser.SplitTopLevel(rule.SelectorText, ','))
            {
                string selectorText = part.Trim();
                if (selectorText.Length == 0 || selectorText.Length > 2000)
                {
                    continue;
                }

                PseudoElementKind pseudo = StripPseudoElement(ref selectorText);
                if (pseudo == (PseudoElementKind)(-1) || selectorText.Length == 0 || ContainsDynamicPseudoClass(selectorText))
                {
                    continue;
                }

                if (!cache.TryGetValue(selectorText, out ISelector? selector))
                {
                    try
                    {
                        selector = parser.ParseSelector(selectorText);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        selector = null;
                    }

                    cache[selectorText] = selector;
                }

                if (selector is null)
                {
                    continue;
                }

                AngleSharp.Css.Priority priority = selector.Specificity;
                int specificity = (priority.Ids * 65536) + (priority.Classes * 256) + priority.Tags;
                var compiled = new CompiledSelector(selector, rule, specificity, pseudo);
                Add(selectorText, compiled);
                SelectorCount++;
            }
        }
    }

    private void Add(string selectorText, CompiledSelector compiled)
    {
        (char kind, string? key) = ExtractKey(selectorText);
        Dictionary<string, List<CompiledSelector>>? bucket = kind switch
        {
            '#' => _byId,
            '.' => _byClass,
            't' => _byTag,
            _ => null,
        };

        if (bucket is null || key is null)
        {
            _universal.Add(compiled);
            return;
        }

        if (!bucket.TryGetValue(key, out List<CompiledSelector>? list))
        {
            list = new List<CompiledSelector>();
            bucket[key] = list;
        }

        list.Add(compiled);
    }

    /// <summary>The selectors that can match <paramref name="element"/> and do, with the pseudo-element they style.</summary>
    public List<CompiledSelector> Match(IElement element)
    {
        var matched = new List<CompiledSelector>();
        void Test(List<CompiledSelector> candidates)
        {
            foreach (CompiledSelector candidate in candidates)
            {
                bool matches;
                try
                {
                    matches = candidate.Selector.Match(element, null);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    matches = false;
                }

                if (matches)
                {
                    matched.Add(candidate);
                }
            }
        }

        Test(_universal);
        if (_byTag.TryGetValue(element.LocalName, out List<CompiledSelector>? byTag))
        {
            Test(byTag);
        }

        string? id = element.Id;
        if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out List<CompiledSelector>? byId))
        {
            Test(byId);
        }

        string? classes = element.GetAttribute("class");
        if (!string.IsNullOrWhiteSpace(classes))
        {
            HashSet<string>? seen = null;
            foreach (string className in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!_byClass.TryGetValue(className, out List<CompiledSelector>? byClass))
                {
                    continue;
                }

                seen ??= new HashSet<string>(StringComparer.Ordinal);
                if (seen.Add(className))
                {
                    Test(byClass);
                }
            }
        }

        return matched;
    }

    /// <summary>Removes ::before/::after from the end of a selector. Returns -1 for other pseudo-elements, which are not drawn.</summary>
    private static PseudoElementKind StripPseudoElement(ref string selector)
    {
        int depth = 0;
        int pseudoStart = -1;
        for (int i = 0; i < selector.Length; i++)
        {
            char character = selector[i];
            if (character is '"' or '\'')
            {
                i = CssSheetParser.SkipString(selector, i, selector.Length) - 1;
                continue;
            }

            if (character is '(' or '[')
            {
                depth++;
            }
            else if (character is ')' or ']')
            {
                depth--;
            }
            else if (character == ':' && depth == 0)
            {
                bool doubleColon = i + 1 < selector.Length && selector[i + 1] == ':';
                string rest = selector[(i + (doubleColon ? 2 : 1))..].ToLowerInvariant();
                if (doubleColon || rest.StartsWith("before", StringComparison.Ordinal) || rest.StartsWith("after", StringComparison.Ordinal) ||
                    rest.StartsWith("first-line", StringComparison.Ordinal) || rest.StartsWith("first-letter", StringComparison.Ordinal))
                {
                    pseudoStart = i;
                    break;
                }

                if (doubleColon)
                {
                    i++;
                }
            }
        }

        if (pseudoStart < 0)
        {
            return PseudoElementKind.None;
        }

        string pseudo = selector[pseudoStart..].TrimStart(':').Trim().ToLowerInvariant();
        PseudoElementKind kind = pseudo switch
        {
            "before" => PseudoElementKind.Before,
            "after" => PseudoElementKind.After,
            _ => (PseudoElementKind)(-1),
        };

        selector = selector[..pseudoStart].TrimEnd();
        if (kind != (PseudoElementKind)(-1) && (selector.Length == 0 || selector.EndsWith(' ') || selector[^1] is '>' or '+' or '~'))
        {
            selector += "*";
        }

        return kind;
    }

    private static bool ContainsDynamicPseudoClass(string selector)
    {
        if (!selector.Contains(':'))
        {
            return false;
        }

        // Only outside :not(...): ":not(:hover)" matches an element that is not hovered, which is how the page is drawn.
        int depth = 0;
        int notDepth = int.MaxValue;
        for (int i = 0; i < selector.Length; i++)
        {
            char character = selector[i];
            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                depth--;
                if (depth < notDepth)
                {
                    notDepth = int.MaxValue;
                }

                continue;
            }

            if (character != ':')
            {
                continue;
            }

            if (string.Compare(selector, i, ":not(", 0, 5, StringComparison.OrdinalIgnoreCase) == 0 && notDepth == int.MaxValue)
            {
                notDepth = depth + 1;
                continue;
            }

            if (depth >= notDepth)
            {
                continue;
            }

            foreach (string pseudo in DynamicPseudoClasses)
            {
                if (string.Compare(selector, i, pseudo, 0, pseudo.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int after = i + pseudo.Length;
                    if (after >= selector.Length || !(char.IsLetterOrDigit(selector[after]) || selector[after] == '-'))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The id ('#'), class ('.') or tag ('t') of the selector's last compound, or ('*', null) when it has none.</summary>
    internal static (char Kind, string? Key) ExtractKey(string selector)
    {
        int compoundStart = 0;
        int depth = 0;
        for (int i = 0; i < selector.Length; i++)
        {
            char character = selector[i];
            if (character == '\\')
            {
                i++;
                continue;
            }

            if (character is '"' or '\'')
            {
                i = CssSheetParser.SkipString(selector, i, selector.Length) - 1;
                continue;
            }

            if (character is '(' or '[')
            {
                depth++;
            }
            else if (character is ')' or ']')
            {
                depth--;
            }
            else if (depth == 0 && (char.IsWhiteSpace(character) || character is '>' or '+' or '~'))
            {
                compoundStart = i + 1;
            }
        }

        string compound = selector[compoundStart..].Trim();
        string? id = null;
        string? className = null;
        string? tag = null;
        depth = 0;
        for (int i = 0; i < compound.Length; i++)
        {
            char character = compound[i];
            if (character == '\\')
            {
                i++;
                continue;
            }

            if (character is '"' or '\'')
            {
                i = CssSheetParser.SkipString(compound, i, compound.Length) - 1;
                continue;
            }

            if (character is '(' or '[')
            {
                depth++;
                continue;
            }

            if (character is ')' or ']')
            {
                depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (character == '#' && id is null)
            {
                id = ReadIdentifier(compound, i + 1, out _);
            }
            else if (character == '.' && className is null)
            {
                className = ReadIdentifier(compound, i + 1, out _);
            }
            else if (i == 0 && (char.IsLetter(character) || character == '_' || character > 127))
            {
                string name = ReadIdentifier(compound, 0, out int end);
                if (end < compound.Length && compound[end] == '|')
                {
                    name = ReadIdentifier(compound, end + 1, out _);
                }

                tag = name;
            }
        }

        if (!string.IsNullOrEmpty(id))
        {
            return ('#', id);
        }

        if (!string.IsNullOrEmpty(className))
        {
            return ('.', className);
        }

        return !string.IsNullOrEmpty(tag) ? ('t', tag.ToLowerInvariant()) : ('*', null);
    }

    /// <summary>Reads a CSS identifier (with escapes such as ".md\:flex") starting at <paramref name="start"/>.</summary>
    private static string ReadIdentifier(string text, int start, out int end)
    {
        var builder = new StringBuilder();
        int i = start;
        while (i < text.Length)
        {
            char character = text[i];
            if (character == '\\' && i + 1 < text.Length)
            {
                int hexStart = i + 1;
                int hexEnd = hexStart;
                while (hexEnd < text.Length && hexEnd - hexStart < 6 && Uri.IsHexDigit(text[hexEnd]))
                {
                    hexEnd++;
                }

                if (hexEnd > hexStart)
                {
                    int code = int.Parse(text[hexStart..hexEnd], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    builder.Append(code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�");
                    i = hexEnd;
                    if (i < text.Length && text[i] == ' ')
                    {
                        i++;
                    }

                    continue;
                }

                builder.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (char.IsLetterOrDigit(character) || character is '-' or '_' || character > 127)
            {
                builder.Append(character);
                i++;
                continue;
            }

            break;
        }

        end = i;
        return builder.ToString();
    }
}
