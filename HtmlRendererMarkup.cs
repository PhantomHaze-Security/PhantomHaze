using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace PhantomHaze;

/// <summary>
/// Writes a document as markup HTML Renderer can draw. Every element gets its cascaded style (HtmlRendererCss.cs) as an
/// inline style in the CSS that HTML Renderer understands; flex and grid layouts become tables; form fields become
/// placeholders the page covers with real controls; inline SVG becomes an image drawn by PhantomHaze's SVG renderer; and
/// links and other clickable elements point back to the document ("phjs:{node}"), so clicks run the page's scripts.
/// Must run on the page's event loop thread (it reads the document).
/// </summary>
internal sealed class PageMarkupWriter
{
    public const string ScriptLinkPrefix = "phjs:";
    public const string SvgImagePrefix = "phsvg:";

    private const int MaximumDepth = 72;
    private const int MaximumElements = 25_000;
    private const int MaximumOutputCharacters = 8_000_000;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    // Elements HTML Renderer knows; anything else is written as a div or span with the element's display.
    private static readonly HashSet<string> KnownTags = new(StringComparer.Ordinal)
    {
        "a", "abbr", "acronym", "address", "b", "bdo", "big", "blockquote", "br", "caption", "center", "cite", "code",
        "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "font", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
        "i", "ins", "kbd", "li", "ol", "p", "pre", "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup",
        "table", "tbody", "td", "tfoot", "th", "thead", "tr", "tt", "u", "ul", "var",
    };

    private static readonly Dictionary<string, string> DefaultDisplays = new(StringComparer.Ordinal)
    {
        ["div"] = "block", ["p"] = "block", ["h1"] = "block", ["h2"] = "block", ["h3"] = "block", ["h4"] = "block", ["h5"] = "block",
        ["h6"] = "block", ["ul"] = "block", ["ol"] = "block", ["dl"] = "block", ["dt"] = "block", ["dd"] = "block", ["pre"] = "block",
        ["blockquote"] = "block", ["address"] = "block", ["center"] = "block", ["hr"] = "block", ["li"] = "list-item",
        ["table"] = "table", ["caption"] = "table-caption", ["thead"] = "table-header-group", ["tbody"] = "table-row-group",
        ["tfoot"] = "table-footer-group", ["tr"] = "table-row", ["td"] = "table-cell", ["th"] = "table-cell",
        ["col"] = "table-column", ["colgroup"] = "table-column-group", ["html"] = "block", ["body"] = "block",
    };

    // Not drawn at all (their content is not page content).
    private static readonly HashSet<string> SkippedTags = new(StringComparer.Ordinal)
    {
        "script", "style", "template", "head", "title", "meta", "link", "base", "basefont", "param", "source", "track",
        "datalist", "noembed", "noframes", "area", "map", "audio", "embed", "applet", "frameset", "frame", "dialog", "slot",
        "math", "portal", "wbr",
    };

    // Inherited properties carried through elements that are not drawn themselves (display: contents).
    private static readonly HashSet<string> InheritedProperties = new(StringComparer.Ordinal)
    {
        "color", "font", "font-family", "font-size", "font-style", "font-variant", "font-weight", "line-height", "text-align",
        "text-indent", "white-space", "visibility", "word-spacing", "list-style", "list-style-type", "list-style-position",
        "list-style-image", "direction", "border-collapse", "border-spacing", "empty-cells",
    };

    private static readonly string[] CommonWindowsFonts =
    [
        "Arial", "Segoe UI", "Times New Roman", "Consolas", "Georgia", "Verdana", "Tahoma", "Courier New", "Trebuchet MS",
        "Calibri", "Cambria", "Candara", "Constantia", "Corbel", "Lucida Console", "Lucida Sans Unicode", "Palatino Linotype",
        "Segoe UI Emoji", "Segoe UI Symbol", "Comic Sans MS", "Impact", "Arial Black", "Franklin Gothic Medium", "Garamond",
        "Book Antiqua", "Century Gothic", "Microsoft Sans Serif", "Sitka Text", "Bahnschrift", "Ebrima", "Gadugi",
    ];

    private readonly IDocument _document;
    private readonly CssCascadeIndex _index;
    private readonly CssMediaContext _media;
    private readonly PageEngineOptions _options;
    private readonly NodeIdTable _ids;
    private readonly bool _scriptsAlive;
    private readonly double _scale;
    private readonly StringBuilder _out = new(64 * 1024);
    private readonly List<PageFormField> _fields = new();
    private readonly Dictionary<int, PageSvgImage> _svgs = new();
    private readonly Dictionary<string, bool> _fontChecks = new(StringComparer.OrdinalIgnoreCase);

    // Boxes worked out for this drawing (flex and grid items are looked at before they are written).
    private readonly Dictionary<IElement, ElementBox> _boxes = new(ReferenceEqualityComparer.Instance);
    private Uri _baseUri;
    private double _rootFontSize = 16;
    private int _elementCount;
    private int _nextSvg = 1;

    // Inside a link (a real one or a clickable element drawn as one): HTML Renderer cannot nest links.
    private int _linkDepth;

    public PageMarkupWriter(IDocument document, CssCascadeIndex index, CssMediaContext media, PageEngineOptions options, NodeIdTable ids, bool scriptsAlive)
    {
        _document = document;
        _index = index;
        _media = media;
        _options = options;
        _ids = ids;
        _scriptsAlive = scriptsAlive;
        _scale = Math.Clamp(options.PixelScale, 0.5, 4);
        _baseUri = Uri.TryCreate(document.BaseUrl?.Href ?? document.Url, UriKind.Absolute, out Uri? parsed) ? parsed : new Uri("about:blank");
    }

    public RenderedPage Write()
    {
        IElement? root = _document.DocumentElement;
        IHtmlElement? body = _document.Body;
        var rootContext = new ElementBox
        {
            FontSizePx = 16,
            Color = CssColor.Black,
            AvailableWidthPx = _media.WidthPx,
            Custom = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        string canvasColor = "#ffffff";
        ElementBox htmlBox = rootContext;
        if (root is not null)
        {
            htmlBox = ComputeBox(root, rootContext);
            _rootFontSize = htmlBox.FontSizePx;
            if (TryGetBackgroundColor(htmlBox, out CssColor htmlBackground) && htmlBackground.A > 0.004)
            {
                canvasColor = Opaque(htmlBackground).ToCss();
            }
        }

        _out.Append("<html");
        if (root is not null)
        {
            string htmlStyle = BuildStyle(root, htmlBox, "html", htmlBox.EmittedDisplay, inheritedOnly: true);
            AppendAttribute("style", htmlStyle);
        }

        _out.Append("><body");
        if (body is not null && !htmlBox.Hidden)
        {
            ElementBox bodyBox = ComputeBox(body, htmlBox);
            if (canvasColor == "#ffffff" && TryGetBackgroundColor(bodyBox, out CssColor bodyBackground) && bodyBackground.A > 0.004)
            {
                canvasColor = Opaque(bodyBackground).ToCss();
            }

            AppendAttribute("style", BuildStyle(body, bodyBox, "body", "block", inheritedOnly: false));
            _out.Append('>');
            WritePseudo(body, bodyBox, PseudoElementKind.Before);
            WriteChildren(body, bodyBox, 1);
            WritePseudo(body, bodyBox, PseudoElementKind.After);
        }
        else
        {
            _out.Append('>');
            if (root is not null && !htmlBox.Hidden)
            {
                WriteChildren(root, htmlBox, 1);
            }
        }

        // HTML Renderer reads a few characters past the last '<'.
        _out.Append("</body></html>\n   ");
        return new RenderedPage(_out.ToString())
        {
            Title = CollapseWhitespace(_document.Title ?? string.Empty),
            BaseUri = _baseUri,
            FaviconUrl = FindFavicon(),
            Fields = _fields,
            SvgImages = _svgs,
            CanvasColor = canvasColor,
        };
    }

    private static CssColor Opaque(CssColor color) => color.A >= 0.996 ? color : new CssColor(
        (byte)Math.Round((color.R * color.A) + (255 * (1 - color.A))),
        (byte)Math.Round((color.G * color.A) + (255 * (1 - color.A))),
        (byte)Math.Round((color.B * color.A) + (255 * (1 - color.A))),
        1);

    private string? FindFavicon()
    {
        foreach (IElement link in _document.QuerySelectorAll("link[rel][href]"))
        {
            string relation = (link.GetAttribute("rel") ?? string.Empty).ToLowerInvariant();
            if (relation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(part => part is "icon" or "shortcut") &&
                TryResolveUrl(link.GetAttribute("href"), out string url))
            {
                return url;
            }
        }

        return null;
    }

    // ---- Boxes: the cascade for one element ----------------------------------------------------------------------

    /// <summary>The styles of one element, with what its children inherit.</summary>
    private sealed class ElementBox
    {
        public CascadedStyle Style = new();
        public CascadedStyle? Before;
        public CascadedStyle? After;
        public ElementBox? Parent;
        public Dictionary<string, string> Custom = null!;
        public double FontSizePx = 16;
        public CssColor Color = CssColor.Black;
        public string Display = "inline";
        public string EmittedDisplay = "inline";
        public string Position = "static";
        public string? TextTransform;
        public double AvailableWidthPx;
        public bool Hidden;

        /// <summary>Inherited declarations of ancestors that were not drawn (display: contents).</summary>
        public CascadedStyle? Carry;
    }

    private ElementBox ComputeBox(IElement element, ElementBox parent)
    {
        if (_boxes.TryGetValue(element, out ElementBox? cached) && ReferenceEquals(cached.Parent, parent))
        {
            return cached;
        }

        ElementBox computed = ComputeBoxCore(element, parent);
        _boxes[element] = computed;
        return computed;
    }

    private ElementBox ComputeBoxCore(IElement element, ElementBox parent)
    {
        List<CompiledSelector> matched = _index.Match(element);
        var box = new ElementBox { Parent = parent, Custom = parent.Custom, TextTransform = parent.TextTransform };
        box.Style = Cascade(matched, PseudoElementKind.None, element.GetAttribute("style"));
        bool hasBefore = matched.Any(selector => selector.Pseudo == PseudoElementKind.Before);
        bool hasAfter = matched.Any(selector => selector.Pseudo == PseudoElementKind.After);

        // Custom properties (CSS variables) are inherited; an element that declares some gets its own table.
        Dictionary<string, string>? ownCustom = null;
        foreach ((string name, string value) in box.Style.Entries)
        {
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                ownCustom ??= new Dictionary<string, string>(parent.Custom, StringComparer.Ordinal);
                ownCustom[name] = value;
            }
        }

        if (ownCustom is not null)
        {
            box.Custom = ownCustom;
        }

        box.Style = ResolveVariables(box.Style, box.Custom);
        ResolveInheritance(box, parent);
        box.FontSizePx = ComputeFontSize(box.Style, parent.FontSizePx);
        box.Color = ComputeColor(box.Style, parent.Color);
        box.Display = NormalizeDisplay(box.Style.Get("display"), parent);
        box.Position = (box.Style.Get("position") ?? "static").Trim().ToLowerInvariant();
        string? transform = box.Style.Get("text-transform")?.Trim().ToLowerInvariant();
        if (transform is not null)
        {
            box.TextTransform = transform is "none" or "initial" or "unset" ? null : transform;
        }

        box.Hidden = IsHidden(element, box);
        box.AvailableWidthPx = ComputeAvailableWidth(box, parent);
        box.EmittedDisplay = box.Display switch
        {
            "flex" or "grid" or "flow-root" or "-webkit-box" => "block",
            "inline-flex" or "inline-grid" or "-webkit-inline-box" => "inline-block",
            "run-in" => "block",
            _ => box.Display,
        };

        if (hasBefore)
        {
            box.Before = ResolveVariables(Cascade(matched, PseudoElementKind.Before, null), box.Custom);
        }

        if (hasAfter)
        {
            box.After = ResolveVariables(Cascade(matched, PseudoElementKind.After, null), box.Custom);
        }

        return box;
    }

    private static CascadedStyle Cascade(List<CompiledSelector> matched, PseudoElementKind pseudo, string? inlineStyle)
    {
        var style = new CascadedStyle();
        List<CompiledSelector> applicable = matched.Where(selector => selector.Pseudo == pseudo).ToList();
        applicable.Sort(static (a, b) =>
        {
            int origin = OriginRank(a.Rule.Origin).CompareTo(OriginRank(b.Rule.Origin));
            if (origin != 0)
            {
                return origin;
            }

            int layer = a.Rule.LayerRank.CompareTo(b.Rule.LayerRank);
            if (layer != 0)
            {
                return layer;
            }

            int specificity = a.Specificity.CompareTo(b.Specificity);
            return specificity != 0 ? specificity : a.Rule.Order.CompareTo(b.Rule.Order);
        });

        foreach (CompiledSelector selector in applicable)
        {
            foreach (CssDeclarationItem declaration in selector.Rule.Normal)
            {
                style.Set(declaration.Name, declaration.Value);
            }
        }

        List<CssDeclarationItem> inline = string.IsNullOrWhiteSpace(inlineStyle)
            ? new List<CssDeclarationItem>()
            : CssSheetParser.ParseDeclarationList(inlineStyle);
        foreach (CssDeclarationItem declaration in inline)
        {
            if (!declaration.Important)
            {
                style.Set(declaration.Name, declaration.Value);
            }
        }

        // !important: user-agent important beats author important; within an origin, earlier layers win.
        List<CompiledSelector> important = applicable.Where(selector => selector.Rule.Important.Count > 0).ToList();
        important.Sort(static (a, b) =>
        {
            int origin = OriginRank(b.Rule.Origin).CompareTo(OriginRank(a.Rule.Origin));
            if (origin != 0)
            {
                return origin;
            }

            int layer = b.Rule.LayerRank.CompareTo(a.Rule.LayerRank);
            if (layer != 0)
            {
                return layer;
            }

            int specificity = a.Specificity.CompareTo(b.Specificity);
            return specificity != 0 ? specificity : a.Rule.Order.CompareTo(b.Rule.Order);
        });

        foreach (CompiledSelector selector in important)
        {
            foreach (CssDeclarationItem declaration in selector.Rule.Important)
            {
                style.Set(declaration.Name, declaration.Value);
            }
        }

        foreach (CssDeclarationItem declaration in inline)
        {
            if (declaration.Important)
            {
                style.Set(declaration.Name, declaration.Value);
            }
        }

        return style;
    }

    private static int OriginRank(CssOrigin origin) => origin == CssOrigin.UserAgent ? 0 : 1;

    private static CascadedStyle ResolveVariables(CascadedStyle style, Dictionary<string, string> custom)
    {
        var resolved = new CascadedStyle();
        foreach ((string name, string value) in style.Entries)
        {
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (!value.Contains("var(", StringComparison.OrdinalIgnoreCase) && !value.Contains("env(", StringComparison.OrdinalIgnoreCase))
            {
                resolved.Set(name, value);
                continue;
            }

            string? result = CssValues.ResolveVariables(value, key => custom.TryGetValue(key, out string? found) ? found : null);
            if (result is not null)
            {
                result = ReplaceEnvironmentVariables(result);
                if (result.Trim().Length > 0)
                {
                    resolved.Set(name, result);
                }
            }
        }

        return resolved;
    }

    /// <summary>env(safe-area-inset-top, 0px) and friends: there are no safe areas on a desktop, so the fallback (or 0) is used.</summary>
    private static string ReplaceEnvironmentVariables(string value)
    {
        int index = CssValues.IndexOfFunction(value, "env(", 0);
        while (index >= 0)
        {
            int close = CssValues.FindClosingParenthesis(value, index + 3);
            if (close < 0)
            {
                break;
            }

            string inside = value[(index + 4)..close];
            int comma = inside.IndexOf(',');
            string replacement = comma < 0 ? "0px" : inside[(comma + 1)..].Trim();
            value = value[..index] + replacement + value[(close + 1)..];
            index = CssValues.IndexOfFunction(value, "env(", index + replacement.Length);
        }

        return value;
    }

    /// <summary>inherit/initial/unset/revert for the properties PhantomHaze writes.</summary>
    private static void ResolveInheritance(ElementBox box, ElementBox parent)
    {
        foreach ((string name, string value) in box.Style.Entries.ToList())
        {
            string keyword = value.Trim().ToLowerInvariant();
            if (keyword is not ("inherit" or "initial" or "unset" or "revert" or "revert-layer"))
            {
                continue;
            }

            bool inherited = InheritedProperties.Contains(name);
            if (keyword == "inherit" || (keyword == "unset" && inherited) || (keyword is "revert" or "revert-layer" && inherited))
            {
                string? parentValue = parent.Style.Get(name);
                if (parentValue is not null && !inherited)
                {
                    box.Style.Set(name, parentValue);
                }
                else
                {
                    // Inherited properties are inherited by HTML Renderer itself.
                    box.Style.Remove(name);
                }

                continue;
            }

            string? initial = InitialValue(name);
            if (initial is null)
            {
                box.Style.Remove(name);
            }
            else
            {
                box.Style.Set(name, initial);
            }
        }
    }

    private static string? InitialValue(string property)
    {
        return property switch
        {
            "display" => "inline",
            "color" => "canvastext",
            "background" or "background-color" => "transparent",
            "background-image" => "none",
            "margin" or "margin-top" or "margin-right" or "margin-bottom" or "margin-left" => "0",
            "padding" or "padding-top" or "padding-right" or "padding-bottom" or "padding-left" => "0",
            "border" or "border-top" or "border-right" or "border-bottom" or "border-left" or "border-style" => "none",
            "width" or "height" => "auto",
            "max-width" => "none",
            "font-weight" => "normal",
            "font-style" => "normal",
            "text-decoration" or "text-decoration-line" => "none",
            "text-align" => "left",
            "float" => "none",
            "position" => "static",
            "visibility" => "visible",
            "white-space" => "normal",
            "line-height" => "normal",
            "font-size" => "medium",
            "vertical-align" => "baseline",
            "list-style-type" => "disc",
            _ => null,
        };
    }

    private double ComputeFontSize(CascadedStyle style, double parentSize)
    {
        string? value = style.Get("font-size");
        string? shorthand = style.Get("font");
        if (shorthand is not null && (value is null || IsAfter(style, "font", "font-size")))
        {
            value = ParseFontShorthand(shorthand).Size ?? value;
        }

        if (value is null)
        {
            return parentSize;
        }

        string keyword = value.Trim().ToLowerInvariant();
        double? size = keyword switch
        {
            "xx-small" => 9,
            "x-small" => 10,
            "small" => 13,
            "medium" => 16,
            "large" => 18,
            "x-large" => 24,
            "xx-large" => 32,
            "xxx-large" => 48,
            "smaller" => parentSize / 1.2,
            "larger" => parentSize * 1.2,
            "math" => parentSize,
            _ => null,
        };

        if (size is null && CssLength.TryEvaluate(value, LengthContext(parentSize, parentSize), out CssLength.Value length))
        {
            size = length.HasPercent ? (parentSize * length.Percent / 100) + length.Px : length.Px;
        }

        return size is double resolved && resolved >= 0 && resolved < 2000 ? resolved : parentSize;
    }

    private static bool IsAfter(CascadedStyle style, string first, string second)
    {
        int firstIndex = -1;
        int secondIndex = -1;
        int index = 0;
        foreach ((string name, _) in style.Entries)
        {
            if (name == first)
            {
                firstIndex = index;
            }
            else if (name == second)
            {
                secondIndex = index;
            }

            index++;
        }

        return firstIndex > secondIndex;
    }

    private static CssColor ComputeColor(CascadedStyle style, CssColor parentColor)
    {
        string? value = style.Get("color");
        if (value is null)
        {
            return parentColor;
        }

        return CssValues.TryParseColor(value, parentColor, out CssColor color) ? color : parentColor;
    }

    private static string NormalizeDisplay(string? value, ElementBox parent)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "inline";
        }

        string display = value.Trim().ToLowerInvariant();
        switch (display)
        {
            case "inline flex":
            case "inline-flexbox":
            case "-ms-inline-flexbox":
            case "-webkit-inline-flex":
            case "-webkit-inline-box":
                return "inline-flex";
            case "block flex":
            case "flexbox":
            case "-ms-flexbox":
            case "-webkit-flex":
            case "-webkit-box":
            case "-moz-box":
                return "flex";
            case "inline grid":
            case "-ms-inline-grid":
                return "inline-grid";
            case "block grid":
            case "-ms-grid":
                return "grid";
            case "block flow":
            case "block flow-root":
            case "flow-root":
            case "run-in":
                return "block";
            case "inline flow":
                return "inline";
            case "inline flow-root":
                return "inline-block";
            case "block flow list-item":
            case "list-item block":
                return "list-item";
            case "contents":
            case "none":
            case "block":
            case "inline":
            case "inline-block":
            case "list-item":
            case "flex":
            case "inline-flex":
            case "grid":
            case "inline-grid":
            case "table":
            case "inline-table":
            case "table-row":
            case "table-cell":
            case "table-row-group":
            case "table-header-group":
            case "table-footer-group":
            case "table-column":
            case "table-column-group":
            case "table-caption":
                return display;
            default:
                return display.StartsWith("inline", StringComparison.Ordinal) ? "inline" : "block";
        }
    }

    private double ComputeAvailableWidth(ElementBox box, ElementBox parent)
    {
        double available = parent.AvailableWidthPx;
        if (box.Display is "inline" or "contents")
        {
            return available;
        }

        if (box.Style.Get("width") is string width &&
            CssLength.TryEvaluate(width, LengthContext(box.FontSizePx, parent.AvailableWidthPx), out CssLength.Value value))
        {
            double resolved = value.HasPercent ? (parent.AvailableWidthPx * value.Percent / 100) + value.Px : value.Px;
            if (resolved > 0)
            {
                available = Math.Min(available, resolved);
            }
        }

        double horizontal = 0;
        foreach (string property in new[] { "padding-left", "padding-right", "margin-left", "margin-right" })
        {
            if (box.Style.Get(property) is string side && CssLength.TryEvaluate(side, LengthContext(box.FontSizePx, parent.AvailableWidthPx), out CssLength.Value sideValue))
            {
                horizontal += sideValue.HasPercent ? parent.AvailableWidthPx * sideValue.Percent / 100 : Math.Max(0, sideValue.Px);
            }
        }

        foreach (string property in new[] { "padding", "margin" })
        {
            if (box.Style.Get(property) is string shorthand)
            {
                List<string> parts = CssSheetParser.SplitComponents(shorthand);
                string right = parts.Count switch { 0 => "0", 1 => parts[0], _ => parts[1] };
                string left = parts.Count switch { 0 => "0", 1 => parts[0], 2 => parts[1], 3 => parts[1], _ => parts[3] };
                foreach (string side in new[] { left, right })
                {
                    if (CssLength.TryEvaluate(side, LengthContext(box.FontSizePx, parent.AvailableWidthPx), out CssLength.Value sideValue))
                    {
                        horizontal += sideValue.HasPercent ? parent.AvailableWidthPx * sideValue.Percent / 100 : Math.Max(0, sideValue.Px);
                    }
                }
            }
        }

        return Math.Max(40, available - horizontal);
    }

    private CssLength.Context LengthContext(double fontSize, double percentBasis) => new(fontSize, _rootFontSize, _media.WidthPx, _media.HeightPx, percentBasis);

    // ---- Hidden elements ---------------------------------------------------------------------------------------------

    private bool IsHidden(IElement element, ElementBox box)
    {
        if (box.Display == "none")
        {
            return true;
        }

        CascadedStyle style = box.Style;
        string Lower(string property) => (style.Get(property) ?? string.Empty).Trim().ToLowerInvariant();
        bool outOfFlow = box.Position is "absolute" or "fixed";
        string overflow = Lower("overflow");
        bool clipsContent = overflow is "hidden" or "clip" || Lower("overflow-y") is "hidden" or "clip";

        // Screen-reader-only text (".sr-only"), skip links and the like, which browsers draw nowhere visible.
        string clip = Lower("clip");
        if (clip.StartsWith("rect(", StringComparison.Ordinal) && (clip.Contains("0px, 0px, 0px, 0px") || clip.Contains("0 0 0 0") || clip.Contains("1px, 1px, 1px, 1px") || clip.Contains("1px 1px 1px 1px") || clip.Contains("0,0,0,0")))
        {
            return true;
        }

        string clipPath = Lower("clip-path");
        if (clipPath.StartsWith("inset(50%", StringComparison.Ordinal) || clipPath.StartsWith("inset(100%", StringComparison.Ordinal) ||
            clipPath.StartsWith("circle(0", StringComparison.Ordinal) || clipPath.StartsWith("polygon(0 0, 0 0", StringComparison.Ordinal))
        {
            return true;
        }

        double? width = ResolvePx(style.Get("width"), box);
        double? height = ResolvePx(style.Get("height"), box);
        if ((outOfFlow || clipsContent) && ((width is double w && w <= 1.01) || (height is double h && h <= 1.01)) &&
            !element.LocalName.Equals("img", StringComparison.Ordinal))
        {
            return true;
        }

        if (clipsContent && ((ResolvePx(style.Get("max-height"), box) is double maxHeight && maxHeight <= 0.01) || (height is double zero && zero <= 0.01)))
        {
            return true;
        }

        if (outOfFlow)
        {
            foreach (string side in new[] { "left", "top", "right", "bottom" })
            {
                if (ResolvePx(style.Get(side), box) is double offset && offset <= -500)
                {
                    return true;
                }
            }

            string transform = Lower("transform");
            if (transform.Contains("scale(0)") || transform.Contains("translatex(-100%)") || transform.Contains("translatex(100%)") ||
                transform.Contains("translatey(-100%)") || transform.Contains("translate(-100%") || transform.Contains("translate3d(-100%") ||
                transform.Contains("translatex(-9999") || transform.Contains("translate3d(100%"))
            {
                return true;
            }

            if (Lower("text-indent").StartsWith("-9999", StringComparison.Ordinal) || Lower("text-indent").StartsWith("-999em", StringComparison.Ordinal))
            {
                return true;
            }
        }

        string opacity = Lower("opacity");
        bool transparent = opacity is "0" or "0.0" or "0%" or ".0";
        if (transparent && (outOfFlow || Lower("pointer-events") == "none" || Lower("visibility") is "hidden" or "collapse"))
        {
            return true;
        }

        if (outOfFlow && Lower("visibility") is "hidden" or "collapse")
        {
            return true;
        }

        return false;
    }

    private double? ResolvePx(string? value, ElementBox box)
    {
        if (value is null || !CssLength.TryEvaluate(value, LengthContext(box.FontSizePx, box.Parent?.AvailableWidthPx ?? _media.WidthPx), out CssLength.Value length))
        {
            return null;
        }

        return length.HasPercent ? ((box.Parent?.AvailableWidthPx ?? _media.WidthPx) * length.Percent / 100) + length.Px : length.Px;
    }

    // ---- Writing --------------------------------------------------------------------------------------------------

    private bool OutputFull => _out.Length > MaximumOutputCharacters || _elementCount > MaximumElements;

    private void WriteChildren(INode parent, ElementBox parentBox, int depth)
    {
        if (parent is IElement element && depth > 1)
        {
            if (parentBox.Display is "flex" or "inline-flex" or "-webkit-box")
            {
                WriteFlexChildren(element, parentBox, depth);
                return;
            }

            if (parentBox.Display is "grid" or "inline-grid")
            {
                WriteGridChildren(element, parentBox, depth);
                return;
            }
        }

        foreach (INode child in GetRenderedChildren(parent))
        {
            if (OutputFull)
            {
                return;
            }

            WriteNode(child, parentBox, depth, itemStyle: null);
        }
    }

    /// <summary>The children to draw: the shadow tree (with slots filled) when the element has one.</summary>
    private static IEnumerable<INode> GetRenderedChildren(INode parent)
    {
        if (parent is IElement { ShadowRoot: IShadowRoot shadow } host)
        {
            return ExpandSlots(shadow.ChildNodes, host);
        }

        return parent.ChildNodes;
    }

    private static IEnumerable<INode> ExpandSlots(INodeList nodes, IElement host)
    {
        foreach (INode node in nodes)
        {
            if (node is IElement { LocalName: "slot" } slot)
            {
                string name = slot.GetAttribute("name") ?? string.Empty;
                List<INode> assigned = host.ChildNodes.Where(child =>
                    (child is IElement childElement ? childElement.GetAttribute("slot") ?? string.Empty : string.Empty) == name &&
                    (name.Length > 0 || child is not IElement || !((IElement)child).HasAttribute("slot"))).ToList();
                if (assigned.Count > 0)
                {
                    foreach (INode child in assigned)
                    {
                        yield return child;
                    }
                }
                else
                {
                    foreach (INode child in slot.ChildNodes)
                    {
                        yield return child;
                    }
                }

                continue;
            }

            yield return node;
        }
    }

    private void WriteNode(INode node, ElementBox parentBox, int depth, CascadedStyle? itemStyle)
    {
        switch (node)
        {
            case IText text:
                WriteText(text.Data, parentBox);
                break;
            case IElement element:
                WriteElement(element, parentBox, depth, itemStyle);
                break;
        }
    }

    private void WriteText(string text, ElementBox box)
    {
        if (text.Length == 0)
        {
            return;
        }

        string transformed = box.TextTransform switch
        {
            "uppercase" => text.ToUpper(CultureInfo.CurrentCulture),
            "lowercase" => text.ToLower(CultureInfo.CurrentCulture),
            "capitalize" => Capitalize(text),
            _ => text,
        };

        _out.Append(WebUtility.HtmlEncode(transformed));
    }

    private static string Capitalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool startOfWord = true;
        foreach (char character in text)
        {
            builder.Append(startOfWord && char.IsLetter(character) ? char.ToUpper(character, CultureInfo.CurrentCulture) : character);
            startOfWord = char.IsWhiteSpace(character) || character is '-' or '(' or '"';
        }

        return builder.ToString();
    }

    private void WriteElement(IElement element, ElementBox parentBox, int depth, CascadedStyle? itemStyle)
    {
        string tag = element.LocalName.ToLowerInvariant();
        if (element.NamespaceUri == SvgNamespace)
        {
            if (tag == "svg")
            {
                ElementBox svgBox = ComputeBox(element, parentBox);
                if (!svgBox.Hidden)
                {
                    WriteInlineSvg(element, svgBox, itemStyle);
                }
            }

            return;
        }

        if (SkippedTags.Contains(tag) || (tag == "noscript" && _scriptsAlive))
        {
            return;
        }

        if (++_elementCount > MaximumElements)
        {
            return;
        }

        ElementBox box = ComputeBox(element, parentBox);
        if (box.Hidden)
        {
            return;
        }

        if (box.Display == "contents")
        {
            var carried = new ElementBox
            {
                Parent = parentBox.Parent,
                Style = parentBox.Style,
                Custom = box.Custom,
                FontSizePx = box.FontSizePx,
                Color = box.Color,
                Display = parentBox.Display,
                EmittedDisplay = parentBox.EmittedDisplay,
                TextTransform = box.TextTransform,
                AvailableWidthPx = parentBox.AvailableWidthPx,
                Carry = MergeCarry(parentBox.Carry, box.Style),
            };
            WritePseudo(element, box, PseudoElementKind.Before);
            WriteChildren(element, carried, depth);
            WritePseudo(element, box, PseudoElementKind.After);
            return;
        }

        if (depth > MaximumDepth)
        {
            // Too deep for HTML Renderer's recursive layout: the content is kept as plain text.
            WriteText(CollapseWhitespace(element.TextContent) + " ", box);
            return;
        }

        switch (tag)
        {
            case "img":
                WriteImage(element, box, itemStyle);
                return;
            case "input":
                WriteInput((IHtmlInputElement)element, box, itemStyle);
                return;
            case "textarea":
            case "select":
                WriteField(element, box, itemStyle);
                return;
            case "button":
                WriteButton(element, box, depth, itemStyle);
                return;
            case "iframe":
                WriteFrame(element, box, itemStyle);
                return;
            case "video":
                WriteVideo(element, box, itemStyle);
                return;
            case "br":
                _out.Append("<br>");
                return;
            case "hr":
                _out.Append("<hr");
                AppendAttribute("style", BuildStyle(element, box, "hr", box.EmittedDisplay, inheritedOnly: false, itemStyle));
                _out.Append('>');
                return;
            case "picture":
            case "canvas":
            case "object":
            case "noscript":
                // Their content (the <img> of a picture, the fallback of a canvas or object) is drawn instead.
                WriteChildren(element, box, depth + 1);
                return;
        }

        string outputTag = ChooseOutputTag(tag, box.EmittedDisplay);
        string? href = null;
        if (tag == "a" && element.HasAttribute("href") && _linkDepth == 0)
        {
            href = BuildLinkTarget(element);
            outputTag = href is null ? ChooseOutputTag("span", box.EmittedDisplay) : "a";
        }
        else if (tag == "a")
        {
            // A link inside a link (or without an address) is drawn as text.
            outputTag = ChooseOutputTag("span", box.EmittedDisplay);
        }
        else if (_linkDepth == 0 && IsClickable(element, box))
        {
            href = ScriptLinkPrefix + _ids.GetId(element).ToString(CultureInfo.InvariantCulture);
            outputTag = "a";
        }

        _out.Append('<').Append(outputTag);
        WriteCommonAttributes(element, tag, outputTag);
        if (href is not null)
        {
            AppendAttribute("href", href);
            string? target = element.GetAttribute("target");
            if (!string.IsNullOrWhiteSpace(target) && !href.StartsWith(ScriptLinkPrefix, StringComparison.Ordinal))
            {
                AppendAttribute("target", target);
            }
        }

        string style = BuildStyle(element, box, outputTag, box.EmittedDisplay, inheritedOnly: false, itemStyle);
        if (href is not null && tag != "a")
        {
            // A clickable element drawn as a link keeps its own look.
            style = "text-decoration:none;color:" + box.Color.ToCss() + ";" + style;
        }

        AppendAttribute("style", style);
        _out.Append('>');
        if (href is not null)
        {
            _linkDepth++;
        }

        WritePseudo(element, box, PseudoElementKind.Before);
        WriteChildren(element, box, depth + 1);
        WritePseudo(element, box, PseudoElementKind.After);
        if (href is not null)
        {
            _linkDepth--;
        }

        if (outputTag != "col")
        {
            _out.Append("</").Append(outputTag).Append('>');
        }
    }

    private static CascadedStyle MergeCarry(CascadedStyle? existing, CascadedStyle style)
    {
        var carry = new CascadedStyle();
        if (existing is not null)
        {
            foreach ((string name, string value) in existing.Entries)
            {
                carry.Set(name, value);
            }
        }

        foreach ((string name, string value) in style.Entries)
        {
            if (InheritedProperties.Contains(name))
            {
                carry.Set(name, value);
            }
        }

        return carry;
    }

    private static string ChooseOutputTag(string tag, string display)
    {
        return KnownTags.Contains(tag) ? tag : IsBlockDisplay(display) ? "div" : "span";
    }

    private static bool IsBlockDisplay(string display) =>
        display is "block" or "list-item" or "table" or "table-row" or "table-cell" or "table-row-group" or "table-header-group" or
            "table-footer-group" or "table-caption" or "flex" or "grid" or "flow-root";

    private void WriteCommonAttributes(IElement element, string tag, string outputTag)
    {
        string? id = element.Id ?? (tag == "a" ? element.GetAttribute("name") : null);
        if (!string.IsNullOrEmpty(id) && id.Length <= 200 && id.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ':' or '.'))
        {
            AppendAttribute("id", id);
        }

        string? title = element.GetAttribute("title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            AppendAttribute("title", title.Length > 300 ? title[..300] : title);
        }

        switch (outputTag)
        {
            case "td":
            case "th":
                CopyAttributes(element, "colspan", "rowspan", "align", "valign", "width", "height", "bgcolor", "nowrap");
                break;
            case "tr":
                CopyAttributes(element, "align", "valign", "bgcolor");
                break;
            case "table":
                CopyAttributes(element, "width", "border", "cellpadding", "cellspacing", "bgcolor", "align");
                break;
            case "col":
            case "colgroup":
                CopyAttributes(element, "span", "width");
                break;
            case "ol":
                CopyAttributes(element, "start", "type", "reversed");
                break;
            case "li":
                CopyAttributes(element, "value", "type");
                break;
            case "font":
                CopyAttributes(element, "color", "face", "size");
                break;
            case "div":
            case "p":
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                CopyAttributes(element, "align");
                break;
        }

        if (element.GetAttribute("dir") is string direction && direction is "rtl" or "ltr")
        {
            AppendAttribute("dir", direction);
        }
    }

    private void CopyAttributes(IElement element, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = element.GetAttribute(name);
            if (value is not null && value.Length <= 100)
            {
                AppendAttribute(name, value);
            }
        }
    }

    private void AppendAttribute(string name, string value)
    {
        if (value.Length == 0 && name == "style")
        {
            return;
        }

        _out.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
    }

    // ---- Links and clickable elements ------------------------------------------------------------------------------

    private string? BuildLinkTarget(IElement anchor)
    {
        string href = (anchor.GetAttribute("href") ?? string.Empty).Trim();
        if (_scriptsAlive)
        {
            // Every link goes through the document, so the page's own click handlers (and routers) decide what happens.
            return ScriptLinkPrefix + _ids.GetId(anchor).ToString(CultureInfo.InvariantCulture);
        }

        if (href.StartsWith('#'))
        {
            return href;
        }

        return TryResolveUrl(href, out string url) ? url : null;
    }

    private bool IsClickable(IElement element, ElementBox box)
    {
        if (!_scriptsAlive && element.LocalName != "summary" && element.LocalName != "label")
        {
            return false;
        }

        bool candidate = element.HasAttribute("onclick") || element.HasAttribute("data-ph-click") || element.LocalName == "summary" ||
                         (element.GetAttribute("role") is string role && role is "button" or "link" or "tab" or "menuitem" or "option" or "switch" or "checkbox" or "radio" or "treeitem") ||
                         (element.LocalName == "label" && element is IHtmlLabelElement { Control: IHtmlInputElement { Type: "checkbox" or "radio" } }) ||
                         (_scriptsAlive && (box.Style.Get("cursor") ?? string.Empty).Trim().Equals("pointer", StringComparison.OrdinalIgnoreCase));
        if (!candidate)
        {
            return false;
        }

        // Never inside another link (HTML Renderer cannot nest them), and never around links or fields of its own.
        if (element.Closest("a[href], button") is not null || element.QuerySelector("a[href], button, input, select, textarea, [onclick], [data-ph-click]") is not null)
        {
            return false;
        }

        // Frameworks listen for clicks on a whole page area (event delegation): such an area is not one clickable thing.
        if (element.LocalName is "html" or "body" or "main" || element.Id is "root" or "app" or "__next" or "__nuxt" or "svelte" or "___gatsby")
        {
            return false;
        }

        int descendants = 0;
        foreach (IElement _ in element.QuerySelectorAll("*"))
        {
            if (++descendants > 60)
            {
                return false;
            }
        }

        return element.TextContent.Length <= 600;
    }

    // ---- Pseudo-elements -------------------------------------------------------------------------------------------

    private void WritePseudo(IElement element, ElementBox box, PseudoElementKind kind)
    {
        CascadedStyle? style = kind == PseudoElementKind.Before ? box.Before : box.After;
        if (style is null || style.Get("content") is not string content)
        {
            return;
        }

        string display = (style.Get("display") ?? "inline").Trim().ToLowerInvariant();
        string position = (style.Get("position") ?? "static").Trim().ToLowerInvariant();
        if (display == "none" || position is "absolute" or "fixed")
        {
            return;
        }

        string? text = EvaluateContent(content, element);
        if (text is null)
        {
            return;
        }

        var pseudoBox = new ElementBox
        {
            Parent = box,
            Style = style,
            Custom = box.Custom,
            FontSizePx = ComputeFontSize(style, box.FontSizePx),
            Color = ComputeColor(style, box.Color),
            Display = NormalizeDisplay(display, box),
            Position = position,
            TextTransform = box.TextTransform,
            AvailableWidthPx = box.AvailableWidthPx,
        };
        pseudoBox.EmittedDisplay = pseudoBox.Display is "flex" or "grid" ? "block" : pseudoBox.Display is "inline-flex" or "inline-grid" ? "inline-block" : pseudoBox.Display;

        // Empty decorations are only drawn when they have a size and something to show.
        if (text.Length == 0)
        {
            bool sized = ResolvePx(style.Get("width"), pseudoBox) is double w && w > 0 && ResolvePx(style.Get("height"), pseudoBox) is double h && h > 0;
            bool visible = (style.Get("background-color") ?? style.Get("background")) is not null || style.Get("border") is not null;
            if (!sized || !visible || pseudoBox.EmittedDisplay == "inline")
            {
                return;
            }
        }

        _out.Append("<span");
        AppendAttribute("style", BuildStyle(element, pseudoBox, "span", pseudoBox.EmittedDisplay, inheritedOnly: false));
        _out.Append('>');
        WriteText(text, pseudoBox);
        _out.Append("</span>");
    }

    /// <summary>The text of a content property ("x", attr(title), open-quote...). Null when nothing is generated.</summary>
    private static string? EvaluateContent(string content, IElement element)
    {
        string value = content.Trim();
        if (value is "none" or "normal")
        {
            return null;
        }

        int slash = value.LastIndexOf(" / ", StringComparison.Ordinal);
        if (slash > 0)
        {
            // content: "x" / "alternative text"
            value = value[..slash];
        }

        var builder = new StringBuilder();
        foreach (string part in CssSheetParser.SplitComponents(value))
        {
            if (part.Length == 0 || part == ",")
            {
                continue;
            }

            if (part[0] is '"' or '\'')
            {
                builder.Append(CssValues.UnescapeString(part.Length >= 2 ? part[1..^1] : string.Empty));
            }
            else if (part.StartsWith("attr(", StringComparison.OrdinalIgnoreCase) && part.EndsWith(')'))
            {
                string name = part[5..^1].Split(' ', ',')[0].Trim();
                builder.Append(element.GetAttribute(name) ?? string.Empty);
            }
            else if (part.Equals("open-quote", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('“');
            }
            else if (part.Equals("close-quote", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('”');
            }
            else if (part.Equals("none", StringComparison.OrdinalIgnoreCase) || part.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // counter(), url() and other values are not generated.
        }

        // Icon fonts put their glyphs in the Private Use Area; without the font they would be drawn as boxes.
        var text = new StringBuilder(builder.Length);
        string generated = builder.ToString();
        for (int i = 0; i < generated.Length; i++)
        {
            char character = generated[i];
            if (char.IsHighSurrogate(character) && i + 1 < generated.Length && char.IsLowSurrogate(generated[i + 1]))
            {
                int code = char.ConvertToUtf32(character, generated[i + 1]);
                if (code < 0xF0000)
                {
                    text.Append(character).Append(generated[i + 1]);
                }

                i++;
                continue;
            }

            if (char.IsSurrogate(character) || character is >= '' and <= '')
            {
                continue;
            }

            text.Append(character);
        }

        return text.ToString();
    }

    // ---- Styles -----------------------------------------------------------------------------------------------------

    /// <summary>The inline style for an element: its cascaded declarations converted to what HTML Renderer draws.</summary>
    private string BuildStyle(IElement element, ElementBox box, string outputTag, string display, bool inheritedOnly, CascadedStyle? itemStyle = null)
    {
        var output = new CascadedStyle();
        if (box.Parent?.Carry is CascadedStyle carry)
        {
            foreach ((string name, string value) in carry.Entries)
            {
                ConvertDeclaration(name, value, box, output);
            }
        }

        foreach ((string name, string value) in box.Style.Entries)
        {
            if (inheritedOnly && !InheritedProperties.Contains(name))
            {
                continue;
            }

            ConvertDeclaration(name, value, box, output);
        }

        if (!inheritedOnly)
        {
            AdjustForBoxSizing(box, output);
            string defaultDisplay = DefaultDisplays.TryGetValue(outputTag, out string? known) ? known : "inline";
            if (!string.Equals(display, defaultDisplay, StringComparison.Ordinal) && display is not ("contents" or "none"))
            {
                output.Set("display", display);
            }

            // Fixed and sticky boxes are drawn where they are in the document.
            if (box.Style.Get("min-height") is string minHeight && !box.Style.Has("height") &&
                ResolvePx(minHeight, box) is double minimum && minimum > 0 && minimum < 4000 && IsBlockDisplay(display))
            {
                output.Set("height", FormatPx(minimum));
            }
        }

        if (itemStyle is not null)
        {
            foreach ((string name, string value) in itemStyle.Entries)
            {
                output.Set(name, value);
            }
        }

        var builder = new StringBuilder();
        foreach ((string name, string value) in output.Entries)
        {
            builder.Append(name).Append(':').Append(value).Append(';');
        }

        return builder.ToString();
    }

    private void AdjustForBoxSizing(ElementBox box, CascadedStyle output)
    {
        string sizing = (box.Style.Get("box-sizing") ?? string.Empty).Trim().ToLowerInvariant();
        if (sizing != "border-box")
        {
            return;
        }

        // HTML Renderer sizes boxes like content-box: the padding and border of a border-box are taken off its size.
        foreach ((string dimension, string first, string second) in new[] { ("width", "left", "right"), ("height", "top", "bottom") })
        {
            if (output.Get(dimension) is not string value || !value.EndsWith("px", StringComparison.Ordinal) ||
                !double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double size))
            {
                continue;
            }

            double extra = 0;
            foreach (string property in new[] { "padding-" + first, "padding-" + second, "border-" + first + "-width", "border-" + second + "-width" })
            {
                if (output.Get(property) is string part && part.EndsWith("px", StringComparison.Ordinal) &&
                    double.TryParse(part[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
                {
                    extra += Math.Max(0, amount);
                }
            }

            if (extra > 0)
            {
                output.Set(dimension, CssValues.FormatNumber(Math.Max(0, size - extra)) + "px");
            }
        }
    }

    private string FormatPx(double cssPx) => CssValues.FormatNumber(cssPx * _scale) + "px";

    /// <summary>A length (or percentage) as HTML Renderer understands it, or null.</summary>
    private string? ConvertLength(string value, ElementBox box, bool allowAuto, bool allowNegative = true, bool allowPercent = true)
    {
        string text = value.Trim();
        string lower = text.ToLowerInvariant();
        if (lower == "auto")
        {
            return allowAuto ? "auto" : null;
        }

        if (!CssLength.TryEvaluate(text, LengthContext(box.FontSizePx, box.Parent?.AvailableWidthPx ?? _media.WidthPx), out CssLength.Value length))
        {
            return null;
        }

        if (length.HasPercent && length.Px == 0)
        {
            if (!allowPercent)
            {
                return null;
            }

            return length.Percent < 0 && !allowNegative ? null : CssValues.FormatNumber(length.Percent) + "%";
        }

        double px = length.HasPercent ? ((box.Parent?.AvailableWidthPx ?? _media.WidthPx) * length.Percent / 100) + length.Px : length.Px;
        if (px < 0 && !allowNegative)
        {
            return null;
        }

        return FormatPx(px);
    }

    private void ConvertDeclaration(string name, string value, ElementBox box, CascadedStyle output)
    {
        string trimmed = value.Trim();
        string lower = trimmed.ToLowerInvariant();
        switch (name)
        {
            case "color":
                output.Set("color", box.Color.ToCss());
                return;
            case "background-color":
                if (CssValues.TryParseColor(trimmed, box.Color, out CssColor background))
                {
                    output.Set("background-color", background.ToCss());
                }

                return;
            case "background":
                ConvertBackgroundShorthand(trimmed, box, output);
                return;
            case "background-image":
                ConvertBackgroundImage(trimmed, box, output);
                return;
            case "background-repeat":
            {
                string repeat = lower.Split(',')[0].Trim();
                string[] words = repeat.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string result = words.Length == 2
                    ? (words[0] == "repeat" && words[1] == "no-repeat" ? "repeat-x" : words[0] == "no-repeat" && words[1] == "repeat" ? "repeat-y" : words[0] == "no-repeat" ? "no-repeat" : "repeat")
                    : repeat is "space" or "round" ? "repeat" : repeat;
                if (result is "repeat" or "repeat-x" or "repeat-y" or "no-repeat")
                {
                    output.Set("background-repeat", result);
                }

                return;
            }
            case "background-position":
            {
                string position = lower.Split(',')[0].Trim();
                var parts = new List<string>();
                foreach (string part in CssSheetParser.SplitComponents(position))
                {
                    parts.Add(part is "left" or "right" or "top" or "bottom" or "center" ? part : ConvertLength(part, box, allowAuto: false) ?? "0");
                }

                if (parts.Count > 0)
                {
                    output.Set("background-position", string.Join(' ', parts.Take(2)));
                }

                return;
            }
            case "margin":
            case "padding":
                ConvertBoxShorthand(name, trimmed, box, output);
                return;
            case "margin-top":
            case "margin-right":
            case "margin-bottom":
            case "margin-left":
                SetLength(output, name, trimmed, box, allowAuto: true, allowNegative: true);
                return;
            case "padding-top":
            case "padding-right":
            case "padding-bottom":
            case "padding-left":
                SetLength(output, name, trimmed, box, allowAuto: false, allowNegative: false);
                return;
            case "margin-inline":
            case "padding-inline":
            case "margin-block":
            case "padding-block":
            {
                string property = name.StartsWith("margin", StringComparison.Ordinal) ? "margin" : "padding";
                bool inline = name.EndsWith("inline", StringComparison.Ordinal);
                List<string> parts = CssSheetParser.SplitComponents(trimmed);
                if (parts.Count == 0)
                {
                    return;
                }

                string start = parts[0];
                string end = parts.Count > 1 ? parts[1] : parts[0];
                SetLength(output, property + (inline ? "-left" : "-top"), start, box, property == "margin", property == "margin");
                SetLength(output, property + (inline ? "-right" : "-bottom"), end, box, property == "margin", property == "margin");
                return;
            }
            case "margin-inline-start":
            case "margin-inline-end":
            case "margin-block-start":
            case "margin-block-end":
            case "padding-inline-start":
            case "padding-inline-end":
            case "padding-block-start":
            case "padding-block-end":
            {
                string property = name.StartsWith("margin", StringComparison.Ordinal) ? "margin" : "padding";
                string side = name.EndsWith("inline-start", StringComparison.Ordinal) ? "left" : name.EndsWith("inline-end", StringComparison.Ordinal) ? "right" :
                    name.EndsWith("block-start", StringComparison.Ordinal) ? "top" : "bottom";
                SetLength(output, property + "-" + side, trimmed, box, property == "margin", property == "margin");
                return;
            }
            case "border":
            case "border-top":
            case "border-right":
            case "border-bottom":
            case "border-left":
            case "border-inline":
            case "border-block":
            case "border-inline-start":
            case "border-inline-end":
            case "border-block-start":
            case "border-block-end":
                ConvertBorderShorthand(name, trimmed, box, output);
                return;
            case "border-width":
            case "border-style":
            case "border-color":
            {
                string kind = name[7..];
                List<string> parts = CssSheetParser.SplitComponents(trimmed);
                if (parts.Count == 0)
                {
                    return;
                }

                string[] sides = ExpandFourSides(parts);
                string[] names = ["top", "right", "bottom", "left"];
                for (int i = 0; i < 4; i++)
                {
                    SetBorderPart(output, names[i], kind, sides[i], box);
                }

                return;
            }
            case "border-top-width":
            case "border-right-width":
            case "border-bottom-width":
            case "border-left-width":
            case "border-top-style":
            case "border-right-style":
            case "border-bottom-style":
            case "border-left-style":
            case "border-top-color":
            case "border-right-color":
            case "border-bottom-color":
            case "border-left-color":
            {
                string[] pieces = name.Split('-');
                SetBorderPart(output, pieces[1], pieces[2], trimmed, box);
                return;
            }
            case "border-radius":
            {
                string first = trimmed.Split('/')[0].Trim();
                List<string> parts = CssSheetParser.SplitComponents(first);
                if (parts.Count == 0)
                {
                    return;
                }

                string[] corners = ExpandFourSides(parts);
                string[] names = ["nw", "ne", "se", "sw"];
                for (int i = 0; i < 4; i++)
                {
                    if (ConvertRadius(corners[i], box) is string radius)
                    {
                        output.Set("corner-" + names[i] + "-radius", radius);
                    }
                }

                return;
            }
            case "border-top-left-radius":
            case "border-top-right-radius":
            case "border-bottom-right-radius":
            case "border-bottom-left-radius":
            {
                string corner = name switch
                {
                    "border-top-left-radius" => "nw",
                    "border-top-right-radius" => "ne",
                    "border-bottom-right-radius" => "se",
                    _ => "sw",
                };
                if (ConvertRadius(CssSheetParser.SplitComponents(trimmed).FirstOrDefault() ?? trimmed, box) is string radius)
                {
                    output.Set("corner-" + corner + "-radius", radius);
                }

                return;
            }
            case "width":
            case "inline-size":
                SetSize(output, "width", trimmed, box);
                return;
            case "height":
            case "block-size":
                SetSize(output, "height", trimmed, box);
                return;
            case "max-width":
            case "max-inline-size":
                if (lower == "none")
                {
                    output.Set("max-width", "none");
                }
                else if (ConvertLength(trimmed, box, allowAuto: false, allowNegative: false) is string maxWidth)
                {
                    output.Set("max-width", maxWidth);
                }

                return;
            case "font":
                ConvertFontShorthand(trimmed, box, output);
                return;
            case "font-size":
                output.Set("font-size", FormatPx(box.FontSizePx));
                return;
            case "font-family":
                output.Set("font-family", ChooseFontFamily(trimmed));
                return;
            case "font-weight":
                if (ConvertFontWeight(lower) is string weight)
                {
                    output.Set("font-weight", weight);
                }

                return;
            case "font-style":
                output.Set("font-style", lower.StartsWith("italic", StringComparison.Ordinal) || lower.StartsWith("oblique", StringComparison.Ordinal) ? "italic" : "normal");
                return;
            case "font-variant":
            case "font-variant-caps":
                output.Set("font-variant", lower.Contains("small-caps") ? "small-caps" : "normal");
                return;
            case "line-height":
                if (ConvertLineHeight(trimmed, box) is string lineHeight)
                {
                    output.Set("line-height", lineHeight);
                }

                return;
            case "text-align":
            {
                string align = lower switch
                {
                    "start" or "-webkit-left" or "-moz-left" => "left",
                    "end" or "-webkit-right" or "-moz-right" => "right",
                    "-webkit-center" or "-moz-center" or "-khtml-center" => "center",
                    "justify" or "justify-all" => "justify",
                    "left" or "right" or "center" => lower,
                    _ => string.Empty,
                };
                if (align.Length > 0)
                {
                    output.Set("text-align", align);
                }

                return;
            }
            case "text-decoration":
            case "text-decoration-line":
            {
                string decoration = lower.Contains("underline") ? "underline" : lower.Contains("line-through") ? "line-through" :
                    lower.Contains("overline") ? "overline" : lower.Contains("none") ? "none" : string.Empty;
                if (decoration.Length > 0)
                {
                    output.Set("text-decoration", decoration);
                }

                return;
            }
            case "text-indent":
                SetLength(output, "text-indent", trimmed, box, allowAuto: false, allowNegative: true);
                return;
            case "vertical-align":
                if (lower is "baseline" or "sub" or "super" or "top" or "text-top" or "middle" or "bottom" or "text-bottom")
                {
                    output.Set("vertical-align", lower);
                }
                else if (ConvertLength(trimmed, box, allowAuto: false) is string verticalAlign)
                {
                    output.Set("vertical-align", verticalAlign);
                }

                return;
            case "white-space":
            {
                string whiteSpace = lower switch
                {
                    "break-spaces" => "pre-wrap",
                    "normal" or "nowrap" or "pre" or "pre-wrap" or "pre-line" => lower,
                    _ => lower.Contains("nowrap") ? "nowrap" : lower.Contains("pre") ? "pre-wrap" : string.Empty,
                };
                if (whiteSpace.Length > 0)
                {
                    output.Set("white-space", whiteSpace);
                }

                return;
            }
            case "text-wrap":
            case "text-wrap-mode":
                if (lower == "nowrap")
                {
                    output.Set("white-space", "nowrap");
                }

                return;
            case "word-break":
                if (lower is "break-all" or "keep-all" or "normal")
                {
                    output.Set("word-break", lower);
                }

                return;
            case "word-spacing":
                SetLength(output, "word-spacing", trimmed, box, allowAuto: false, allowNegative: true);
                return;
            case "visibility":
                if (lower is "visible" or "hidden" or "collapse")
                {
                    output.Set("visibility", lower == "collapse" ? "hidden" : lower);
                }

                return;
            case "direction":
                if (lower is "ltr" or "rtl")
                {
                    output.Set("direction", lower);
                }

                return;
            case "list-style":
            case "list-style-type":
            case "list-style-position":
                output.Set(name, lower.Contains("url(") ? trimmed : lower);
                return;
            case "list-style-image":
                if (lower == "none" || lower.StartsWith("url(", StringComparison.Ordinal))
                {
                    output.Set(name, trimmed);
                }

                return;
            case "border-collapse":
            case "empty-cells":
                output.Set(name, lower);
                return;
            case "border-spacing":
            {
                List<string> parts = CssSheetParser.SplitComponents(trimmed);
                var converted = parts.Select(part => ConvertLength(part, box, allowAuto: false, allowNegative: false)).Where(part => part is not null).ToList();
                if (converted.Count > 0)
                {
                    output.Set("border-spacing", string.Join(' ', converted));
                }

                return;
            }
            case "float":
            {
                string floatValue = lower switch
                {
                    "inline-start" => "left",
                    "inline-end" => "right",
                    "left" or "right" or "none" => lower,
                    _ => string.Empty,
                };
                if (floatValue.Length > 0)
                {
                    output.Set("float", floatValue);
                }

                return;
            }
            case "overflow":
            case "overflow-x":
            case "overflow-y":
                if (lower.Contains("hidden") || lower.Contains("clip"))
                {
                    output.Set("overflow", "hidden");
                }

                return;
        }
    }

    private void SetLength(CascadedStyle output, string property, string value, ElementBox box, bool allowAuto, bool allowNegative)
    {
        if (ConvertLength(value, box, allowAuto, allowNegative) is string converted)
        {
            output.Set(property, converted);
        }
    }

    private void SetSize(CascadedStyle output, string property, string value, ElementBox box)
    {
        string lower = value.Trim().ToLowerInvariant();
        if (lower == "auto")
        {
            output.Set(property, "auto");
            return;
        }

        if (lower is "fit-content" or "max-content" or "min-content" or "-webkit-fill-available" or "-moz-available" or "stretch" or "-webkit-max-content")
        {
            return;
        }

        if (ConvertLength(value, box, allowAuto: true, allowNegative: false, allowPercent: property == "width") is string converted)
        {
            output.Set(property, converted);
        }
    }

    private string? ConvertRadius(string value, ElementBox box)
    {
        if (CssLength.TryEvaluate(value, LengthContext(box.FontSizePx, 0), out CssLength.Value length))
        {
            double px = length.Px;
            if (length.HasPercent)
            {
                // A percentage of a box whose size is not known: 50% (a circle or pill) is drawn as a large radius.
                double? width = ResolvePx(box.Style.Get("width"), box);
                double? height = ResolvePx(box.Style.Get("height"), box);
                double side = width is double w && height is double h ? Math.Min(w, h) : width ?? height ?? 24;
                px += side * length.Percent / 100;
            }

            return px > 0 ? FormatPx(Math.Min(px, 999)) : null;
        }

        return null;
    }

    private static string[] ExpandFourSides(List<string> parts)
    {
        return parts.Count switch
        {
            1 => [parts[0], parts[0], parts[0], parts[0]],
            2 => [parts[0], parts[1], parts[0], parts[1]],
            3 => [parts[0], parts[1], parts[2], parts[1]],
            _ => [parts[0], parts[1], parts[2], parts[3]],
        };
    }

    private void ConvertBoxShorthand(string property, string value, ElementBox box, CascadedStyle output)
    {
        List<string> parts = CssSheetParser.SplitComponents(value);
        if (parts.Count == 0)
        {
            return;
        }

        string[] sides = ExpandFourSides(parts);
        string[] names = ["top", "right", "bottom", "left"];
        bool margin = property == "margin";
        for (int i = 0; i < 4; i++)
        {
            SetLength(output, property + "-" + names[i], sides[i], box, allowAuto: margin, allowNegative: margin);
        }
    }

    private void ConvertBorderShorthand(string property, string value, ElementBox box, CascadedStyle output)
    {
        string[] sides = property switch
        {
            "border" => ["top", "right", "bottom", "left"],
            "border-inline" => ["left", "right"],
            "border-block" => ["top", "bottom"],
            "border-inline-start" => ["left"],
            "border-inline-end" => ["right"],
            "border-block-start" => ["top"],
            "border-block-end" => ["bottom"],
            _ => [property[7..]],
        };

        string lower = value.Trim().ToLowerInvariant();
        string width = "medium";
        string style = "none";
        string color = box.Color.ToCss();
        if (lower is "none" or "0" or "hidden")
        {
            style = "none";
            width = "0";
        }
        else
        {
            foreach (string part in CssSheetParser.SplitComponents(value))
            {
                string token = part.ToLowerInvariant();
                if (token is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset")
                {
                    style = token;
                }
                else if (token is "thin" or "medium" or "thick" || CssLength.TryEvaluate(part, LengthContext(box.FontSizePx, 0), out _))
                {
                    width = part;
                }
                else if (CssValues.TryParseColor(part, box.Color, out CssColor parsed))
                {
                    color = parsed.ToCss();
                }
            }
        }

        foreach (string side in sides)
        {
            SetBorderPart(output, side, "width", width, box);
            SetBorderPart(output, side, "style", style, box);
            SetBorderPart(output, side, "color", color, box);
        }
    }

    private void SetBorderPart(CascadedStyle output, string side, string kind, string value, ElementBox box)
    {
        string lower = value.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "width":
            {
                double? px = lower switch
                {
                    "thin" => 1,
                    "medium" => 3,
                    "thick" => 5,
                    _ => CssLength.TryEvaluate(value, LengthContext(box.FontSizePx, 0), out CssLength.Value length) && !length.HasPercent ? length.Px : null,
                };
                if (px is double width && width >= 0)
                {
                    // Hairlines (0.5px) are drawn one pixel wide.
                    output.Set("border-" + side + "-width", width > 0 && width * _scale < 1 ? "1px" : FormatPx(width));
                }

                return;
            }
            case "style":
                if (lower is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset")
                {
                    output.Set("border-" + side + "-style", lower == "hidden" ? "none" : lower);
                }

                return;
            case "color":
                if (CssValues.TryParseColor(value, box.Color, out CssColor color))
                {
                    output.Set("border-" + side + "-color", color.ToCss());
                }

                return;
        }
    }

    private void ConvertBackgroundShorthand(string value, ElementBox box, CascadedStyle output)
    {
        List<string> layers = CssSheetParser.SplitTopLevel(value, ',');
        string lastLayer = layers[^1];
        bool colorFound = false;
        foreach (string part in CssSheetParser.SplitComponents(lastLayer))
        {
            if (part.Contains('(') && !part.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("hsl", StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith("hwb", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("lab", StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith("lch", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("oklab", StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith("oklch", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("color", StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith("light-dark", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CssValues.TryParseColor(part, box.Color, out CssColor color))
            {
                output.Set("background-color", color.ToCss());
                colorFound = true;
            }
        }

        if (!colorFound)
        {
            // The shorthand resets the colour to transparent.
            output.Set("background-color", "transparent");
        }

        string firstLayer = layers[0];
        string imagePart = CssSheetParser.SplitComponents(firstLayer).FirstOrDefault(part =>
            part.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || part.Contains("gradient(", StringComparison.OrdinalIgnoreCase) ||
            part.Contains("image-set(", StringComparison.OrdinalIgnoreCase)) ?? "none";
        ConvertBackgroundImage(imagePart, box, output);
        string lowerFirst = firstLayer.ToLowerInvariant();
        string repeat = lowerFirst.Contains("no-repeat") ? "no-repeat" : lowerFirst.Contains("repeat-x") ? "repeat-x" : lowerFirst.Contains("repeat-y") ? "repeat-y" : "repeat";
        if (imagePart != "none")
        {
            output.Set("background-repeat", repeat);
        }
    }

    private void ConvertBackgroundImage(string value, ElementBox box, CascadedStyle output)
    {
        string first = CssSheetParser.SplitTopLevel(value, ',')[0].Trim();
        string lower = first.ToLowerInvariant();
        if (lower == "none")
        {
            output.Set("background-image", "none");
            return;
        }

        if (lower.StartsWith("url(", StringComparison.Ordinal))
        {
            string address = first[4..^1].Trim().Trim('"', '\'');
            if (address.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase) || address.Length == 0)
            {
                return;
            }

            if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || TryResolveUrl(address, out address))
            {
                output.Set("background-image", "url(\"" + address.Replace("\"", "%22", StringComparison.Ordinal) + "\")");
            }

            return;
        }

        if (lower.Contains("image-set(", StringComparison.Ordinal))
        {
            int url = lower.IndexOf("url(", StringComparison.Ordinal);
            int quote = lower.IndexOf('"');
            if (url >= 0)
            {
                int close = first.IndexOf(')', url);
                if (close > url)
                {
                    ConvertBackgroundImage(first[url..(close + 1)], box, output);
                }
            }
            else if (quote >= 0)
            {
                int end = first.IndexOf('"', quote + 1);
                if (end > quote)
                {
                    ConvertBackgroundImage("url(" + first[quote..(end + 1)] + ")", box, output);
                }
            }

            return;
        }

        if (lower.Contains("linear-gradient(", StringComparison.Ordinal) || lower.Contains("radial-gradient(", StringComparison.Ordinal) ||
            lower.Contains("conic-gradient(", StringComparison.Ordinal))
        {
            ConvertGradient(first, box, output);
        }
    }

    /// <summary>A CSS gradient becomes HTML Renderer's two-colour gradient (background-color to background-gradient).</summary>
    private static void ConvertGradient(string value, ElementBox box, CascadedStyle output)
    {
        int open = value.IndexOf('(');
        int close = CssValues.FindClosingParenthesis(value, open);
        if (open < 0 || close < 0)
        {
            return;
        }

        bool linear = value.Contains("linear", StringComparison.OrdinalIgnoreCase);
        List<string> arguments = CssSheetParser.SplitTopLevel(value[(open + 1)..close], ',');
        double angle = 180;
        var colors = new List<CssColor>();
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i].Trim();
            string lowerArgument = argument.ToLowerInvariant();
            if (i == 0 && linear && (lowerArgument.StartsWith("to ", StringComparison.Ordinal) || lowerArgument.EndsWith("deg", StringComparison.Ordinal) ||
                                     lowerArgument.EndsWith("turn", StringComparison.Ordinal) || lowerArgument.EndsWith("rad", StringComparison.Ordinal)))
            {
                angle = lowerArgument switch
                {
                    "to top" => 0,
                    "to right" => 90,
                    "to bottom" => 180,
                    "to left" => 270,
                    "to top right" or "to right top" => 45,
                    "to bottom right" or "to right bottom" => 135,
                    "to bottom left" or "to left bottom" => 225,
                    "to top left" or "to left top" => 315,
                    _ => CssValues.TryParseNumberWithUnit(lowerArgument, out double number, out string unit)
                        ? unit switch { "deg" => number, "turn" => number * 360, "rad" => number * 180 / Math.PI, "grad" => number * 0.9, _ => 180 }
                        : 180,
                };
                continue;
            }

            if (i == 0 && !linear && !char.IsDigit(argument.FirstOrDefault()) && !CssValues.TryParseColor(CssSheetParser.SplitComponents(argument).FirstOrDefault(), box.Color, out _))
            {
                // radial-gradient(circle at center, ...)
                continue;
            }

            string colorText = CssSheetParser.SplitComponents(argument).FirstOrDefault() ?? string.Empty;
            if (CssValues.TryParseColor(colorText, box.Color, out CssColor color))
            {
                colors.Add(color);
            }
        }

        if (colors.Count == 0)
        {
            return;
        }

        CssColor start = colors[0];
        CssColor end = colors[^1];
        output.Set("background-color", start.ToCss());
        if (colors.Count > 1 && start != end)
        {
            output.Set("background-gradient", end.ToCss());
            // GDI+ angles start at "to right" and turn clockwise; CSS angles start at "to top".
            double gdiAngle = ((angle - 90) % 360 + 360) % 360;
            output.Set("background-gradient-angle", CssValues.FormatNumber(gdiAngle));
        }
    }

    private void ConvertFontShorthand(string value, ElementBox box, CascadedStyle output)
    {
        FontShorthand font = ParseFontShorthand(value);
        if (font.System)
        {
            output.Set("font-family", "Segoe UI");
            output.Set("font-size", FormatPx(box.FontSizePx));
            return;
        }

        if (font.Size is null)
        {
            return;
        }

        output.Set("font-style", font.Italic ? "italic" : "normal");
        output.Set("font-weight", font.Weight ?? "normal");
        output.Set("font-variant", font.SmallCaps ? "small-caps" : "normal");
        output.Set("font-size", FormatPx(box.FontSizePx));
        if (font.LineHeight is string lineHeight && ConvertLineHeight(lineHeight, box) is string convertedLineHeight)
        {
            output.Set("line-height", convertedLineHeight);
        }

        if (font.Family is string family)
        {
            output.Set("font-family", ChooseFontFamily(family));
        }
    }

    private sealed class FontShorthand
    {
        public string? Size;
        public string? LineHeight;
        public string? Family;
        public string? Weight;
        public bool Italic;
        public bool SmallCaps;
        public bool System;
    }

    private static FontShorthand ParseFontShorthand(string value)
    {
        var font = new FontShorthand();
        string lower = value.Trim().ToLowerInvariant();
        if (lower is "caption" or "icon" or "menu" or "message-box" or "small-caption" or "status-bar" || lower.StartsWith("-apple-system", StringComparison.Ordinal))
        {
            font.System = true;
            return font;
        }

        List<string> parts = CssSheetParser.SplitComponents(value);
        int i = 0;
        for (; i < parts.Count; i++)
        {
            string part = parts[i];
            string token = part.ToLowerInvariant();
            if (token is "normal")
            {
                continue;
            }

            if (token is "italic" or "oblique")
            {
                font.Italic = true;
                continue;
            }

            if (token == "small-caps")
            {
                font.SmallCaps = true;
                continue;
            }

            if (token is "bold" or "bolder" or "lighter" || (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric) && numeric is >= 1 and <= 1000))
            {
                font.Weight = ConvertFontWeight(token);
                continue;
            }

            if (token.EndsWith("condensed", StringComparison.Ordinal) || token.EndsWith("expanded", StringComparison.Ordinal))
            {
                continue;
            }

            string sizePart = part;
            int slash = part.IndexOf('/');
            if (slash > 0)
            {
                sizePart = part[..slash];
                font.LineHeight = part[(slash + 1)..];
            }
            else if (i + 1 < parts.Count && parts[i + 1].StartsWith('/'))
            {
                string next = parts[i + 1];
                font.LineHeight = next.Length > 1 ? next[1..] : (i + 2 < parts.Count ? parts[i + 2] : null);
                i += next.Length > 1 ? 1 : 2;
            }

            font.Size = sizePart;
            i++;
            break;
        }

        if (font.Size is not null && i < parts.Count)
        {
            font.Family = string.Join(' ', parts.Skip(i)).Replace(" , ", ", ", StringComparison.Ordinal);
        }

        return font;
    }

    private static string? ConvertFontWeight(string value)
    {
        return value switch
        {
            "bold" or "bolder" => "bold",
            "normal" or "lighter" => "normal",
            _ => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) ? (weight >= 600 ? "bold" : "normal") : null,
        };
    }

    private string? ConvertLineHeight(string value, ElementBox box)
    {
        string lower = value.Trim().ToLowerInvariant();
        if (lower == "normal")
        {
            return "normal";
        }

        if (double.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor))
        {
            return factor > 0 ? FormatPx(factor * box.FontSizePx) : null;
        }

        if (CssLength.TryEvaluate(value, LengthContext(box.FontSizePx, box.FontSizePx), out CssLength.Value length))
        {
            double px = length.HasPercent ? (box.FontSizePx * length.Percent / 100) + length.Px : length.Px;
            return px > 0 ? FormatPx(px) : null;
        }

        return null;
    }

    private string ChooseFontFamily(string value)
    {
        foreach (string part in CssSheetParser.SplitTopLevel(value, ','))
        {
            string family = part.Trim().Trim('"', '\'').Trim();
            if (family.Length == 0)
            {
                continue;
            }

            string generic = family.ToLowerInvariant() switch
            {
                "sans-serif" or "system-ui" or "ui-sans-serif" or "-apple-system" or "blinkmacsystemfont" or "ui-rounded" or "helvetica neue" or "helvetica" or "roboto" or "inter" or "open sans" => "Segoe UI",
                "serif" or "ui-serif" => "Georgia",
                "monospace" or "ui-monospace" or "sfmono-regular" or "sf mono" or "menlo" or "monaco" => "Consolas",
                "cursive" => "Comic Sans MS",
                "fantasy" => "Impact",
                "emoji" or "apple color emoji" or "noto color emoji" => "Segoe UI Emoji",
                "math" or "fangsong" => "Cambria",
                _ => string.Empty,
            };

            if (IsFontInstalled(family))
            {
                return family;
            }

            if (generic.Length > 0)
            {
                return generic;
            }
        }

        return "Segoe UI";
    }

    private bool IsFontInstalled(string family)
    {
        if (!_fontChecks.TryGetValue(family, out bool installed))
        {
            installed = _options.IsFontInstalled is Func<string, bool> check
                ? check(family)
                : CommonWindowsFonts.Contains(family, StringComparer.OrdinalIgnoreCase);
            _fontChecks[family] = installed;
        }

        return installed;
    }

    private static bool TryGetBackgroundColor(ElementBox box, out CssColor color)
    {
        color = CssColor.Transparent;
        string? background = null;
        foreach ((string name, string value) in box.Style.Entries)
        {
            if (name == "background-color")
            {
                background = value;
            }
            else if (name == "background")
            {
                background = CssSheetParser.SplitComponents(CssSheetParser.SplitTopLevel(value, ',')[^1])
                    .FirstOrDefault(part => CssValues.TryParseColor(part, box.Color, out _)) ?? "transparent";
            }
        }

        return background is not null && CssValues.TryParseColor(background, box.Color, out color);
    }

    // ---- Flex and grid ------------------------------------------------------------------------------------------------

    private sealed record LayoutItem(INode Node, ElementBox? Box, int Order);

    /// <summary>The children of a flex or grid container that are laid out (text and elements that are drawn).</summary>
    private List<LayoutItem> CollectItems(IElement container, ElementBox containerBox, List<INode> outOfFlow)
    {
        var items = new List<LayoutItem>();
        foreach (INode child in GetRenderedChildren(container))
        {
            if (child is IText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Data))
                {
                    items.Add(new LayoutItem(child, null, 0));
                }

                continue;
            }

            if (child is not IElement element)
            {
                continue;
            }

            string tag = element.LocalName.ToLowerInvariant();
            if (SkippedTags.Contains(tag) || (tag == "noscript" && _scriptsAlive))
            {
                continue;
            }

            ElementBox box = ComputeBox(element, containerBox);
            if (box.Hidden)
            {
                continue;
            }

            if (box.Position is "absolute" or "fixed")
            {
                outOfFlow.Add(child);
                continue;
            }

            int order = int.TryParse(box.Style.Get("order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
            items.Add(new LayoutItem(child, box, order));
        }

        return items.Select((item, index) => (item, index)).OrderBy(pair => pair.item.Order).ThenBy(pair => pair.index).Select(pair => pair.item).ToList();
    }

    private double GapPx(ElementBox box, string property)
    {
        string? gap = box.Style.Get(property);
        if (gap is null && box.Style.Get("gap") is string shorthand)
        {
            List<string> parts = CssSheetParser.SplitComponents(shorthand);
            gap = property == "row-gap" ? parts.FirstOrDefault() : (parts.Count > 1 ? parts[1] : parts.FirstOrDefault());
        }

        gap ??= box.Style.Get(property == "row-gap" ? "grid-row-gap" : "grid-column-gap");
        return gap is not null && ResolvePx(gap, box) is double px && px > 0 ? px : 0;
    }

    private void WriteFlexChildren(IElement container, ElementBox box, int depth)
    {
        string Lower(string property) => (box.Style.Get(property) ?? string.Empty).Trim().ToLowerInvariant();
        string direction = Lower("flex-direction");
        string wrap = Lower("flex-wrap");
        string flow = Lower("flex-flow");
        if (flow.Length > 0)
        {
            foreach (string part in flow.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("row", StringComparison.Ordinal) || part.StartsWith("column", StringComparison.Ordinal))
                {
                    direction = direction.Length == 0 ? part : direction;
                }
                else
                {
                    wrap = wrap.Length == 0 ? part : wrap;
                }
            }
        }

        if (box.Display == "-webkit-box" && Lower("-webkit-box-orient") == "vertical")
        {
            direction = "column";
        }

        var outOfFlow = new List<INode>();
        List<LayoutItem> items = CollectItems(container, box, outOfFlow);
        foreach (INode node in outOfFlow)
        {
            WriteNode(node, box, depth, null);
        }

        if (direction.EndsWith("reverse", StringComparison.Ordinal))
        {
            items.Reverse();
        }

        string alignItems = Lower("align-items");
        if (direction.StartsWith("column", StringComparison.Ordinal))
        {
            // A column: every item is a block; "align-items: center" centres them.
            foreach (LayoutItem item in items)
            {
                var itemStyle = new CascadedStyle();
                if (item.Box is ElementBox itemBox && !IsBlockDisplay(itemBox.EmittedDisplay) && itemBox.EmittedDisplay != "inline-block")
                {
                    itemStyle.Set("display", "block");
                }

                if (alignItems is "center" && item.Box is ElementBox centered && centered.Style.Get("width") is not null)
                {
                    itemStyle.Set("margin-left", "auto");
                    itemStyle.Set("margin-right", "auto");
                }

                WriteLayoutItem(item, box, depth, itemStyle, centerText: alignItems == "center");
            }

            return;
        }

        double columnGap = GapPx(box, "column-gap");
        if (wrap.StartsWith("wrap", StringComparison.Ordinal))
        {
            // A wrapping row: items flow like words, so they wrap onto new lines.
            double rowGap = GapPx(box, "row-gap");
            foreach (LayoutItem item in items)
            {
                var itemStyle = new CascadedStyle();
                itemStyle.Set("display", "inline-block");
                itemStyle.Set("vertical-align", alignItems is "center" ? "middle" : alignItems is "flex-end" or "end" ? "bottom" : "top");
                if (columnGap > 0)
                {
                    itemStyle.Set("margin-right", FormatPx(columnGap));
                }

                if (rowGap > 0)
                {
                    itemStyle.Set("margin-bottom", FormatPx(rowGap));
                }

                if (item.Box is ElementBox itemBox && itemBox.Style.Get("width") is null && FlexBasis(itemBox) is string basis)
                {
                    itemStyle.Set("width", basis);
                }

                WriteLayoutItem(item, box, depth, itemStyle, centerText: false);
            }

            return;
        }

        WriteFlexRowTable(items, box, depth, columnGap, Lower("justify-content"), alignItems);
    }

    private string? FlexBasis(ElementBox box)
    {
        string? basis = box.Style.Get("flex-basis");
        if (basis is null && box.Style.Get("flex") is string flex)
        {
            List<string> parts = CssSheetParser.SplitComponents(flex);
            basis = parts.Count >= 3 ? parts[2] : parts.Count == 1 && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? parts[0] : null;
        }

        if (basis is null || basis.Trim() is "auto" or "content" or "0" or "0%")
        {
            return null;
        }

        return ConvertLength(basis, box, allowAuto: false, allowNegative: false);
    }

    private static double FlexGrow(ElementBox? box)
    {
        if (box is null)
        {
            return 0;
        }

        if (box.Style.Get("flex-grow") is string grow && double.TryParse(grow, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return Math.Max(0, value);
        }

        if (box.Style.Get("flex") is string flex)
        {
            string lower = flex.Trim().ToLowerInvariant();
            if (lower == "auto")
            {
                return 1;
            }

            if (lower is "none" or "initial")
            {
                return 0;
            }

            string first = CssSheetParser.SplitComponents(flex).FirstOrDefault() ?? "0";
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double shorthandGrow) ? Math.Max(0, shorthandGrow) : 1;
        }

        return 0;
    }

    /// <summary>
    /// A row that does not wrap is drawn as a one-row table: each item in a cell, with empty cells as the space
    /// "justify-content" (or an item's auto margin) puts between them.
    /// </summary>
    private void WriteFlexRowTable(List<LayoutItem> items, ElementBox box, int depth, double gap, string justify, string alignItems)
    {
        if (items.Count == 0)
        {
            return;
        }

        bool inline = box.Display is "inline-flex" or "-webkit-inline-box";
        double totalGrow = items.Sum(item => FlexGrow(item.Box));
        bool growing = totalGrow > 0;
        _out.Append("<table cellpadding=\"0\" cellspacing=\"0\"");
        var tableStyle = new StringBuilder("border-collapse:separate;");
        tableStyle.Append("border-spacing:").Append(gap > 0 ? FormatPx(gap) : "0px").Append(" 0px;");
        if (!inline)
        {
            tableStyle.Append("width:100%;");
        }
        else
        {
            tableStyle.Append("display:inline-table;");
        }

        AppendAttribute("style", tableStyle.ToString());
        _out.Append("><tr>");

        bool spaceBefore = !growing && !inline && justify is "center" or "flex-end" or "end" or "right" or "space-around" or "space-evenly";
        bool spaceBetween = !growing && !inline && justify is "space-between" or "space-around" or "space-evenly";
        bool spaceAfter = !growing && !inline && justify is "center" or "flex-start" or "start" or "left" or "normal" or "" or "space-around" or "space-evenly";
        if (spaceBefore)
        {
            WriteSpacerCell(justify == "center" ? 50 : justify is "space-around" or "space-evenly" ? 100.0 / (items.Count + 1) : 100);
        }

        for (int i = 0; i < items.Count; i++)
        {
            LayoutItem item = items[i];
            string Lower(string property) => (item.Box?.Style.Get(property) ?? string.Empty).Trim().ToLowerInvariant();
            string[] marginParts = Lower("margin").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string marginLeft = marginParts.Length switch { 0 => string.Empty, 1 => marginParts[0], 4 => marginParts[3], _ => marginParts[1] };
            bool autoLeft = Lower("margin-left") == "auto" || Lower("margin-inline-start") == "auto" || (marginParts.Length > 0 && marginLeft == "auto");
            if (autoLeft && !growing && i > 0 && !spaceBefore)
            {
                WriteSpacerCell(100);
            }

            string align = Lower("align-self") is string self && self.Length > 0 && self != "auto" ? self : alignItems;
            string verticalAlign = align switch
            {
                "center" => "middle",
                "flex-end" or "end" or "self-end" => "bottom",
                "baseline" or "first baseline" or "last baseline" => "baseline",
                _ => "top",
            };

            var cellStyle = new StringBuilder("padding:0px;vertical-align:").Append(verticalAlign).Append(';');
            double grow = FlexGrow(item.Box);
            if (growing && grow > 0)
            {
                cellStyle.Append("width:").Append(CssValues.FormatNumber(Math.Max(1, 100 * grow / totalGrow))).Append("%;");
            }
            else if (item.Box is ElementBox itemBox && itemBox.Style.Get("width") is null && FlexBasis(itemBox) is string basis)
            {
                cellStyle.Append("width:").Append(basis).Append(';');
            }

            if (item.Box is ElementBox noShrink && (noShrink.Style.Get("flex-shrink") ?? string.Empty).Trim() == "0" || (Lower("white-space") == "nowrap"))
            {
                cellStyle.Append("white-space:nowrap;");
            }

            _out.Append("<td");
            AppendAttribute("style", cellStyle.ToString());
            _out.Append('>');
            WriteLayoutItem(item, box, depth, item.Box is null ? null : new CascadedStyle(), centerText: false);
            _out.Append("</td>");

            if (spaceBetween && i < items.Count - 1)
            {
                WriteSpacerCell(justify == "space-between" ? 100.0 / Math.Max(1, items.Count - 1) : 100.0 / (items.Count + 1));
            }
        }

        if (spaceAfter && !growing)
        {
            WriteSpacerCell(justify == "center" ? 50 : justify is "space-around" or "space-evenly" ? 100.0 / (items.Count + 1) : 100);
        }

        _out.Append("</tr></table>");
    }

    private void WriteSpacerCell(double percent)
    {
        _out.Append("<td style=\"padding:0px;width:").Append(CssValues.FormatNumber(Math.Max(0.1, percent))).Append("%\"></td>");
    }

    private void WriteLayoutItem(LayoutItem item, ElementBox containerBox, int depth, CascadedStyle? itemStyle, bool centerText)
    {
        if (item.Node is IText text)
        {
            if (centerText)
            {
                _out.Append("<div style=\"text-align:center\">");
                WriteText(text.Data, containerBox);
                _out.Append("</div>");
            }
            else
            {
                WriteText(text.Data, containerBox);
            }

            return;
        }

        if (centerText)
        {
            _out.Append("<div style=\"text-align:center\">");
        }

        WriteNode(item.Node, containerBox, depth, itemStyle is { Count: > 0 } ? itemStyle : null);
        if (centerText)
        {
            _out.Append("</div>");
        }
    }

    /// <summary>A grid is drawn as a table with as many columns as the grid template has (auto-fill is worked out from the width).</summary>
    private void WriteGridChildren(IElement container, ElementBox box, int depth)
    {
        var outOfFlow = new List<INode>();
        List<LayoutItem> items = CollectItems(container, box, outOfFlow);
        foreach (INode node in outOfFlow)
        {
            WriteNode(node, box, depth, null);
        }

        double columnGap = GapPx(box, "column-gap");
        double rowGap = GapPx(box, "row-gap");
        int columns = CountGridColumns(box, columnGap);
        if ((box.Style.Get("grid-auto-flow") ?? string.Empty).Contains("column", StringComparison.OrdinalIgnoreCase) && box.Style.Get("grid-template-columns") is null)
        {
            columns = Math.Max(1, items.Count);
        }

        if (columns <= 1 || items.Count == 0)
        {
            foreach (LayoutItem item in items)
            {
                var itemStyle = new CascadedStyle();
                if (item.Box is ElementBox itemBox && !IsBlockDisplay(itemBox.EmittedDisplay) && itemBox.EmittedDisplay != "inline-block")
                {
                    itemStyle.Set("display", "block");
                }

                if (rowGap > 0)
                {
                    itemStyle.Set("margin-bottom", FormatPx(rowGap));
                }

                WriteLayoutItem(item, box, depth, itemStyle, centerText: false);
            }

            return;
        }

        _out.Append("<table cellpadding=\"0\" cellspacing=\"0\"");
        AppendAttribute("style", $"width:100%;border-collapse:separate;border-spacing:{(columnGap > 0 ? FormatPx(columnGap) : "0px")} {(rowGap > 0 ? FormatPx(rowGap) : "0px")};");
        _out.Append('>');
        int used = 0;
        bool rowOpen = false;
        string cellWidth = CssValues.FormatNumber(100.0 / columns);
        foreach (LayoutItem item in items)
        {
            int span = Math.Clamp(GridSpan(item.Box, columns), 1, columns);
            if (used + span > columns)
            {
                _out.Append("</tr>");
                rowOpen = false;
                used = 0;
            }

            if (!rowOpen)
            {
                _out.Append("<tr>");
                rowOpen = true;
            }

            _out.Append("<td");
            if (span > 1)
            {
                AppendAttribute("colspan", span.ToString(CultureInfo.InvariantCulture));
            }

            string align = (item.Box?.Style.Get("align-self") ?? box.Style.Get("align-items") ?? string.Empty).Trim().ToLowerInvariant();
            AppendAttribute("style", $"padding:0px;vertical-align:{(align == "center" ? "middle" : align is "end" or "flex-end" ? "bottom" : "top")};width:{CssValues.FormatNumber(100.0 * span / columns)}%;");
            _out.Append('>');
            WriteLayoutItem(item, box, depth, null, centerText: false);
            _out.Append("</td>");
            used += span;
            if (used >= columns)
            {
                _out.Append("</tr>");
                rowOpen = false;
                used = 0;
            }
        }

        if (rowOpen)
        {
            for (; used < columns; used++)
            {
                _out.Append("<td style=\"width:").Append(cellWidth).Append("%\"></td>");
            }

            _out.Append("</tr>");
        }

        _out.Append("</table>");
    }

    private int CountGridColumns(ElementBox box, double gap)
    {
        string? template = box.Style.Get("grid-template-columns");
        if (template is null && box.Style.Get("grid-template") is string shorthand && shorthand.Contains('/'))
        {
            template = shorthand[(shorthand.LastIndexOf('/') + 1)..];
        }

        if (template is null)
        {
            string? areas = box.Style.Get("grid-template-areas");
            if (areas is not null)
            {
                int quote = areas.IndexOfAny(new[] { '"', '\'' });
                if (quote >= 0)
                {
                    int end = areas.IndexOf(areas[quote], quote + 1);
                    if (end > quote)
                    {
                        return areas[(quote + 1)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    }
                }
            }

            return 1;
        }

        double width = box.AvailableWidthPx;
        int count = 0;
        foreach (string part in CssSheetParser.SplitComponents(template))
        {
            string lower = part.ToLowerInvariant();
            if (lower.StartsWith('[') || lower is "none" or "subgrid" or "masonry" or ",")
            {
                continue;
            }

            if (lower.StartsWith("repeat(", StringComparison.Ordinal))
            {
                int close = CssValues.FindClosingParenthesis(part, 6);
                string inside = close > 7 ? part[7..close] : string.Empty;
                int comma = inside.IndexOf(',');
                if (comma < 0)
                {
                    continue;
                }

                string repetitions = inside[..comma].Trim().ToLowerInvariant();
                string tracks = inside[(comma + 1)..];
                int tracksPerRepeat = Math.Max(1, CssSheetParser.SplitComponents(tracks).Count(track => !track.StartsWith('[') && track != ","));
                if (repetitions is "auto-fill" or "auto-fit")
                {
                    double minimum = MinimumTrackWidth(tracks, box);
                    int fits = minimum > 0 ? (int)Math.Floor((width + gap) / (minimum + gap)) : 1;
                    count += Math.Max(1, fits) * tracksPerRepeat;
                }
                else if (int.TryParse(repetitions, NumberStyles.Integer, CultureInfo.InvariantCulture, out int times))
                {
                    count += Math.Clamp(times, 1, 24) * tracksPerRepeat;
                }

                continue;
            }

            count++;
        }

        return Math.Clamp(count, 1, 24);
    }

    private double MinimumTrackWidth(string tracks, ElementBox box)
    {
        string text = tracks.Trim();
        if (text.StartsWith("minmax(", StringComparison.OrdinalIgnoreCase))
        {
            int close = CssValues.FindClosingParenthesis(text, 6);
            string inside = close > 7 ? text[7..close] : string.Empty;
            List<string> arguments = CssSheetParser.SplitTopLevel(inside, ',');
            text = arguments.Count > 0 ? arguments[0] : text;
        }

        return ResolvePx(text.Trim(), new ElementBox { Parent = box, FontSizePx = box.FontSizePx, AvailableWidthPx = box.AvailableWidthPx, Custom = box.Custom }) ?? 200;
    }

    private static int GridSpan(ElementBox? box, int columns)
    {
        if (box is null)
        {
            return 1;
        }

        string? column = box.Style.Get("grid-column") ?? box.Style.Get("grid-area");
        string? end = box.Style.Get("grid-column-end");
        string text = (column ?? end ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return 1;
        }

        int spanIndex = text.IndexOf("span", StringComparison.Ordinal);
        if (spanIndex >= 0)
        {
            string number = new string(text[(spanIndex + 4)..].Trim().TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int span) ? span : 1;
        }

        string[] parts = text.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stop))
        {
            if (stop < 0)
            {
                stop = columns + 2 + stop;
            }

            return Math.Max(1, stop - start);
        }

        return 1;
    }

    // ---- Images, media and frames -------------------------------------------------------------------------------------

    private void WriteImage(IElement element, ElementBox box, CascadedStyle? itemStyle)
    {
        string? source = ChooseImageSource(element, box);
        if (source is null)
        {
            string alt = element.GetAttribute("alt") ?? string.Empty;
            if (alt.Length > 0 && alt.Length < 200)
            {
                WriteText(alt, box);
            }

            return;
        }

        _out.Append("<img");
        AppendAttribute("src", source);
        if (element.GetAttribute("alt") is string altText && altText.Length > 0)
        {
            AppendAttribute("alt", altText.Length > 300 ? altText[..300] : altText);
        }

        var extra = new CascadedStyle();
        foreach ((string attribute, string property) in new[] { ("width", "width"), ("height", "height") })
        {
            if (!box.Style.Has(property) && element.GetAttribute(attribute) is string size &&
                double.TryParse(size.Trim().TrimEnd('x', 'p'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels) && pixels > 0)
            {
                extra.Set(property, FormatPx(pixels));
            }
        }

        if (itemStyle is not null)
        {
            foreach ((string name, string value) in itemStyle.Entries)
            {
                extra.Set(name, value);
            }
        }

        AppendAttribute("style", BuildStyle(element, box, "img", box.EmittedDisplay is "inline-block" ? "inline" : box.EmittedDisplay, inheritedOnly: false, extra));
        _out.Append('>');
    }

    /// <summary>The image address to draw: srcset (for the display scale), lazy-loading attributes, or src.</summary>
    private string? ChooseImageSource(IElement element, ElementBox box)
    {
        string? src = element.GetAttribute("src");
        bool placeholder = string.IsNullOrWhiteSpace(src) ||
                           (src.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase) && src.Length < 200) ||
                           (src.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase) && src.Length < 300) ||
                           src.Contains("placeholder", StringComparison.OrdinalIgnoreCase) || src.Contains("blank.gif", StringComparison.OrdinalIgnoreCase) ||
                           src.Contains("spacer.gif", StringComparison.OrdinalIgnoreCase) || src.Contains("lazy", StringComparison.OrdinalIgnoreCase);
        string? srcset = element.GetAttribute("srcset");
        if (placeholder)
        {
            src = element.GetAttribute("data-src") ?? element.GetAttribute("data-lazy-src") ?? element.GetAttribute("data-original") ??
                  element.GetAttribute("data-lazy") ?? element.GetAttribute("data-url") ?? src;
            srcset = element.GetAttribute("data-srcset") ?? element.GetAttribute("data-lazy-srcset") ?? srcset;
        }

        // <picture>: the first <source> whose media matches and whose type GDI+ can decode.
        if (element.ParentElement is IElement { LocalName: "picture" } picture)
        {
            foreach (IElement source in picture.Children.Where(child => child.LocalName == "source"))
            {
                string type = (source.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
                if (type.Contains("webp") || type.Contains("avif") || type.Contains("jxl") || type.Contains("heic"))
                {
                    continue;
                }

                if (!CssMediaQueryEvaluator.Matches(source.GetAttribute("media"), _media))
                {
                    continue;
                }

                string? sourceSet = source.GetAttribute("srcset") ?? source.GetAttribute("data-srcset");
                if (!string.IsNullOrWhiteSpace(sourceSet))
                {
                    srcset = sourceSet;
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(srcset) && PickFromSourceSet(srcset, box) is string candidate)
        {
            src = candidate;
        }

        if (string.IsNullOrWhiteSpace(src))
        {
            return null;
        }

        string trimmed = src.Trim();
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Length <= 2_000_000 ? trimmed : null;
        }

        return TryResolveUrl(trimmed, out string url) ? url : null;
    }

    private string? PickFromSourceSet(string srcset, ElementBox box)
    {
        var candidates = new List<(string Url, double Width, double Density)>();
        foreach (string entry in SplitSourceSet(srcset))
        {
            string[] parts = entry.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            double width = 0;
            double density = 1;
            if (parts.Length > 1)
            {
                string descriptor = parts[1].ToLowerInvariant();
                if (descriptor.EndsWith('w') && double.TryParse(descriptor[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                {
                    width = w;
                }
                else if (descriptor.EndsWith('x') && double.TryParse(descriptor[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                {
                    density = x;
                }
            }

            string lower = parts[0].ToLowerInvariant();
            if (lower.Contains(".webp") || lower.Contains(".avif"))
            {
                continue;
            }

            candidates.Add((parts[0], width, density));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Any(candidate => candidate.Width > 0))
        {
            double wanted = (ResolvePx(box.Style.Get("width"), box) ?? Math.Min(box.AvailableWidthPx, 1200)) * _scale;
            return candidates.Where(candidate => candidate.Width > 0).OrderBy(candidate => candidate.Width >= wanted ? 0 : 1)
                .ThenBy(candidate => candidate.Width >= wanted ? candidate.Width : -candidate.Width).First().Url;
        }

        return candidates.OrderBy(candidate => Math.Abs(candidate.Density - _scale)).First().Url;
    }

    private static IEnumerable<string> SplitSourceSet(string srcset)
    {
        // Commas separate candidates, but data: URLs contain commas too.
        int start = 0;
        for (int i = 0; i < srcset.Length; i++)
        {
            if (srcset[i] == ',' && (i + 1 >= srcset.Length || char.IsWhiteSpace(srcset[i + 1])))
            {
                yield return srcset[start..i];
                start = i + 1;
            }
        }

        yield return srcset[start..];
    }

    private void WriteVideo(IElement element, ElementBox box, CascadedStyle? itemStyle)
    {
        string? poster = element.GetAttribute("poster");
        if (!string.IsNullOrWhiteSpace(poster) && TryResolveUrl(poster, out string posterUrl))
        {
            _out.Append("<img");
            AppendAttribute("src", posterUrl);
            AppendAttribute("alt", "Video");
            AppendAttribute("style", BuildStyle(element, box, "img", "inline", inheritedOnly: false, itemStyle) + "max-width:100%;");
            _out.Append('>');
        }
    }

    private void WriteFrame(IElement element, ElementBox box, CascadedStyle? itemStyle)
    {
        string? source = element.GetAttribute("src") ?? element.GetAttribute("data-src");
        if (string.IsNullOrWhiteSpace(source) || !TryResolveUrl(source, out string url))
        {
            return;
        }

        string label = "Embedded page: " + (Uri.TryCreate(url, UriKind.Absolute, out Uri? frame) ? frame.Host : url);
        // A YouTube player becomes a link to the video.
        if (frame is not null && frame.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase) && frame.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
        {
            string id = frame.AbsolutePath["/embed/".Length..].Split('/', '?')[0];
            if (id.Length > 0)
            {
                url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
                label = "Watch the video on YouTube";
            }
        }

        string? title = element.GetAttribute("title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            label += " (" + (title.Length > 80 ? title[..80] : title) + ")";
        }

        _out.Append("<div");
        AppendAttribute("style", "border:1px solid #c8c8c8;padding:" + FormatPx(10) + ";margin:" + FormatPx(4) + " 0px;background-color:#f4f6f9;");
        _out.Append("><a");
        AppendAttribute("href", url);
        AppendAttribute("target", "_blank");
        _out.Append('>');
        _out.Append(WebUtility.HtmlEncode("▶ " + label));
        _out.Append("</a></div>");
    }

    private void WriteInlineSvg(IElement svg, ElementBox box, CascadedStyle? itemStyle)
    {
        double? width = ResolvePx(box.Style.Get("width"), box);
        double? height = ResolvePx(box.Style.Get("height"), box);
        double? attributeWidth = ParseSvgLength(svg.GetAttribute("width"), box);
        double? attributeHeight = ParseSvgLength(svg.GetAttribute("height"), box);
        width ??= attributeWidth;
        height ??= attributeHeight;
        double ratio = 1;
        string? viewBox = svg.GetAttribute("viewBox");
        if (viewBox is not null)
        {
            string[] numbers = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length == 4 && double.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double viewWidth) &&
                double.TryParse(numbers[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double viewHeight) && viewWidth > 0 && viewHeight > 0)
            {
                ratio = viewWidth / viewHeight;
            }
        }

        if (width is null && height is not null)
        {
            width = height * ratio;
        }
        else if (height is null && width is not null)
        {
            height = width / ratio;
        }
        else if (width is null && height is null)
        {
            width = Math.Min(300, box.AvailableWidthPx);
            height = viewBox is null ? 150 : width / ratio;
        }

        double w = Math.Clamp(width!.Value, 1, 4000);
        double h = Math.Clamp(height!.Value, 1, 4000);
        string markup = SerializeSvg(svg, box);
        if (markup.Length > 1_000_000)
        {
            return;
        }

        int id = _nextSvg++;
        _svgs[id] = new PageSvgImage(markup, Opaque(box.Color).ToCss());
        _out.Append("<img");
        // phsvg:{id}:{width}:{height}, the size in screen pixels: the page draws the SVG at exactly that size.
        AppendAttribute("src", string.Create(
            CultureInfo.InvariantCulture,
            $"{SvgImagePrefix}{id}:{(int)Math.Ceiling(w * _scale)}:{(int)Math.Ceiling(h * _scale)}"));
        var extra = new CascadedStyle();
        extra.Set("width", FormatPx(w));
        extra.Set("height", FormatPx(h));
        if (!box.Style.Has("vertical-align"))
        {
            extra.Set("vertical-align", "middle");
        }

        if (itemStyle is not null)
        {
            foreach ((string name, string value) in itemStyle.Entries)
            {
                extra.Set(name, value);
            }
        }

        AppendAttribute("style", BuildStyle(svg, box, "img", box.EmittedDisplay is "inline-block" ? "inline" : box.EmittedDisplay, inheritedOnly: false, extra));
        _out.Append('>');
    }

    private double? ParseSvgLength(string? value, ElementBox box)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
        {
            return plain > 0 ? plain : null;
        }

        return ResolvePx(text, box) is double px && px > 0 ? px : null;
    }

    /// <summary>
    /// The SVG as XML for PhantomHaze's renderer: &lt;use&gt; references are replaced by what they point to, and the styles
    /// the page's stylesheets give its shapes (fill: currentColor and the like) are written as style attributes.
    /// </summary>
    private string SerializeSvg(IElement svg, ElementBox box)
    {
        var builder = new StringBuilder(1024);
        string? viewBox = svg.GetAttribute("viewBox");
        if (viewBox is null)
        {
            // An icon from a sprite takes the viewBox of the symbol it uses.
            IElement? use = svg.QuerySelector("use");
            if (use is not null && ResolveUseTarget(use) is IElement symbol && symbol.GetAttribute("viewBox") is string symbolViewBox)
            {
                viewBox = symbolViewBox;
            }
        }

        builder.Append("<svg xmlns=\"").Append(SvgNamespace).Append('"');
        if (viewBox is not null)
        {
            builder.Append(" viewBox=\"").Append(WebUtility.HtmlEncode(viewBox)).Append('"');
        }

        foreach (string name in new[] { "width", "height", "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "opacity", "fill-rule", "color" })
        {
            if (svg.GetAttribute(name) is string attribute)
            {
                builder.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(attribute)).Append('"');
            }
        }

        string styles = SvgStyleAttribute(svg, box, includeExisting: true);
        if (styles.Length > 0)
        {
            builder.Append(" style=\"").Append(WebUtility.HtmlEncode(styles)).Append('"');
        }

        builder.Append('>');
        int budget = 4000;
        foreach (INode child in svg.ChildNodes)
        {
            SerializeSvgNode(child, builder, box, ref budget, 0);
        }

        builder.Append("</svg>");
        return builder.ToString();
    }

    private void SerializeSvgNode(INode node, StringBuilder builder, ElementBox parentBox, ref int budget, int depth)
    {
        if (node is not IElement element || --budget <= 0 || depth > 40)
        {
            return;
        }

        string name = element.LocalName;
        if (name is "script" or "style" or "foreignObject" or "foreignobject" or "image" or "text" or "title" or "desc" or "metadata")
        {
            return;
        }

        if (name == "use")
        {
            if (ResolveUseTarget(element) is IElement target)
            {
                double.TryParse(element.GetAttribute("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out double x);
                double.TryParse(element.GetAttribute("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out double y);
                builder.Append("<g");
                if (x != 0 || y != 0)
                {
                    builder.Append(" transform=\"translate(").Append(CssValues.FormatNumber(x)).Append(' ').Append(CssValues.FormatNumber(y)).Append(")\"");
                }

                CopySvgAttributes(element, builder, parentBox);
                builder.Append('>');
                if (target.LocalName is "symbol" or "svg")
                {
                    foreach (INode child in target.ChildNodes)
                    {
                        SerializeSvgNode(child, builder, parentBox, ref budget, depth + 1);
                    }
                }
                else
                {
                    SerializeSvgNode(target, builder, parentBox, ref budget, depth + 1);
                }

                builder.Append("</g>");
            }

            return;
        }

        // Symbols are only drawn where a <use> refers to them.
        if (name == "symbol")
        {
            return;
        }

        builder.Append('<').Append(name);
        CopySvgAttributes(element, builder, parentBox);
        builder.Append('>');
        foreach (INode child in element.ChildNodes)
        {
            SerializeSvgNode(child, builder, parentBox, ref budget, depth + 1);
        }

        builder.Append("</").Append(name).Append('>');
    }

    private void CopySvgAttributes(IElement element, StringBuilder builder, ElementBox parentBox)
    {
        foreach (IAttr attribute in element.Attributes)
        {
            string name = attribute.Name;
            if (name.Contains(':') || name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "class" or "style" or "href")
            {
                continue;
            }

            builder.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(attribute.Value)).Append('"');
        }

        string styles = SvgStyleAttribute(element, parentBox, includeExisting: true);
        if (styles.Length > 0)
        {
            builder.Append(" style=\"").Append(WebUtility.HtmlEncode(styles)).Append('"');
        }
    }

    private string SvgStyleAttribute(IElement element, ElementBox parentBox, bool includeExisting)
    {
        List<CompiledSelector> matched = _index.Match(element);
        CascadedStyle style = ResolveVariables(Cascade(matched, PseudoElementKind.None, includeExisting ? element.GetAttribute("style") : null), parentBox.Custom);
        var builder = new StringBuilder();
        foreach ((string name, string value) in style.Entries)
        {
            if (name is "fill" or "stroke" or "stroke-width" or "stroke-linecap" or "stroke-linejoin" or "stroke-miterlimit" or "fill-rule" or
                "opacity" or "fill-opacity" or "stroke-opacity" or "color" or "display" or "visibility")
            {
                builder.Append(name).Append(':').Append(value.Trim()).Append(';');
            }
        }

        return builder.ToString();
    }

    private IElement? ResolveUseTarget(IElement use)
    {
        string? href = use.GetAttribute("href") ?? use.GetAttribute("xlink:href") ?? use.Attributes.FirstOrDefault(attribute => attribute.LocalName == "href")?.Value;
        if (string.IsNullOrEmpty(href) || !href.StartsWith('#'))
        {
            return null;
        }

        return _document.GetElementById(href[1..]);
    }

    // ---- Form fields -------------------------------------------------------------------------------------------------

    private void WriteInput(IHtmlInputElement input, ElementBox box, CascadedStyle? itemStyle)
    {
        string type = (input.Type ?? "text").ToLowerInvariant();
        switch (type)
        {
            case "hidden":
                return;
            case "submit":
            case "button":
            case "reset":
            {
                string label = input.Value is { Length: > 0 } value ? value : type == "reset" ? "Reset" : type == "submit" ? "Submit" : string.Empty;
                WriteButtonLike(input, box, label, itemStyle);
                return;
            }
            case "image":
            {
                string? source = input.GetAttribute("src");
                if (source is not null && TryResolveUrl(source, out string url))
                {
                    _out.Append("<a");
                    AppendAttribute("href", ScriptLinkPrefix + _ids.GetId(input).ToString(CultureInfo.InvariantCulture));
                    _out.Append("><img");
                    AppendAttribute("src", url);
                    AppendAttribute("alt", input.GetAttribute("alt") ?? "Submit");
                    AppendAttribute("style", BuildStyle(input, box, "img", "inline", inheritedOnly: false, itemStyle));
                    _out.Append("></a>");
                }

                return;
            }
            case "file":
                WriteText("(File uploads are not supported here.)", box);
                return;
            case "checkbox":
            case "radio":
                WriteFieldPlaceholder(input, box, new PageFormField(_ids.GetId(input), type == "checkbox" ? PageFormFieldKind.CheckBox : PageFormFieldKind.Radio)
                {
                    Checked = input.IsChecked,
                    Disabled = input.IsDisabled,
                    InputType = type,
                }, 13, 13, itemStyle);
                return;
        }

        double fontSize = box.FontSizePx;
        int size = int.TryParse(input.GetAttribute("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int declaredSize) && declaredSize > 0 ? Math.Min(declaredSize, 200) : 20;
        WriteFieldPlaceholder(input, box, new PageFormField(_ids.GetId(input), type == "password" ? PageFormFieldKind.Password : PageFormFieldKind.Text)
        {
            Value = input.Value ?? string.Empty,
            Placeholder = input.GetAttribute("placeholder") ?? string.Empty,
            Disabled = input.IsDisabled,
            ReadOnly = input.IsReadOnly,
            MaxLength = input.MaxLength,
            InputType = type,
            TextColor = box.Color.ToCss(),
            FontSizePx = fontSize,
        }, Math.Max(40, size * fontSize * 0.5), Math.Max(14, fontSize * 1.25), itemStyle);
    }

    private void WriteField(IElement element, ElementBox box, CascadedStyle? itemStyle)
    {
        double fontSize = box.FontSizePx;
        if (element is IHtmlTextAreaElement textArea)
        {
            int columns = textArea.Columns > 0 ? Math.Min(textArea.Columns, 300) : 20;
            int rows = textArea.Rows > 0 ? Math.Min(textArea.Rows, 100) : 2;
            WriteFieldPlaceholder(element, box, new PageFormField(_ids.GetId(element), PageFormFieldKind.TextArea)
            {
                Value = textArea.Value ?? string.Empty,
                Placeholder = textArea.GetAttribute("placeholder") ?? string.Empty,
                Disabled = textArea.IsDisabled,
                ReadOnly = textArea.IsReadOnly,
                MaxLength = textArea.MaxLength,
                TextColor = box.Color.ToCss(),
                FontSizePx = fontSize,
            }, columns * fontSize * 0.55, rows * fontSize * 1.3, itemStyle);
            return;
        }

        if (element is IHtmlSelectElement select)
        {
            var options = new List<string>();
            int selected = -1;
            int index = 0;
            foreach (IHtmlOptionElement option in select.Options)
            {
                options.Add(CollapseWhitespace(option.Label ?? option.Text ?? string.Empty));
                if (option.IsSelected && selected < 0)
                {
                    selected = index;
                }

                index++;
                if (index >= 1000)
                {
                    break;
                }
            }

            if (selected < 0 && options.Count > 0 && !select.IsMultiple)
            {
                selected = 0;
            }

            double longest = options.Count == 0 ? 6 : Math.Min(60, options.Max(option => option.Length));
            WriteFieldPlaceholder(element, box, new PageFormField(_ids.GetId(element), PageFormFieldKind.Select)
            {
                Options = options,
                SelectedIndex = selected,
                Disabled = select.IsDisabled,
                TextColor = box.Color.ToCss(),
                FontSizePx = fontSize,
            }, (longest * fontSize * 0.55) + 28, Math.Max(14, fontSize * 1.25), itemStyle);
        }
    }

    private void WriteFieldPlaceholder(IElement element, ElementBox box, PageFormField field, double defaultWidth, double defaultHeight, CascadedStyle? itemStyle)
    {
        _fields.Add(field);
        var extra = new CascadedStyle();
        if (!box.Style.Has("width") && !box.Style.Has("inline-size"))
        {
            extra.Set("width", FormatPx(defaultWidth));
        }

        if (!box.Style.Has("height") && !box.Style.Has("block-size"))
        {
            extra.Set("height", FormatPx(defaultHeight));
        }

        if (itemStyle is not null)
        {
            foreach ((string name, string value) in itemStyle.Entries)
            {
                extra.Set(name, value);
            }
        }

        string display = box.EmittedDisplay is "inline" or "inline-block" ? "inline-block" : box.EmittedDisplay;
        _out.Append("<span");
        AppendAttribute("id", field.ElementId);
        AppendAttribute("style", BuildStyle(element, box, "span", display, inheritedOnly: false, extra));
        _out.Append(">&nbsp;</span>");
    }

    private void WriteButton(IElement button, ElementBox box, int depth, CascadedStyle? itemStyle)
    {
        if (_linkDepth > 0)
        {
            // A button inside a link: its content is drawn, the link handles the click.
            WriteChildren(button, box, depth + 1);
            return;
        }

        string display = box.EmittedDisplay is "inline" ? "inline-block" : box.EmittedDisplay;
        _out.Append("<a");
        AppendAttribute("href", ScriptLinkPrefix + _ids.GetId(button).ToString(CultureInfo.InvariantCulture));
        AppendAttribute("style", "text-decoration:none;color:" + box.Color.ToCss() + ";" + BuildStyle(button, box, "a", display, inheritedOnly: false, itemStyle));
        _out.Append('>');
        _linkDepth++;
        WritePseudo(button, box, PseudoElementKind.Before);
        int before = _out.Length;
        WriteChildren(button, box, depth + 1);
        if (_out.Length == before)
        {
            // An icon button whose icon could not be drawn still shows something to click.
            string label = button.GetAttribute("aria-label") ?? button.GetAttribute("title") ?? string.Empty;
            WriteText(label.Length is > 0 and < 60 ? label : "•", box);
        }

        WritePseudo(button, box, PseudoElementKind.After);
        _linkDepth--;
        _out.Append("</a>");
    }

    private void WriteButtonLike(IElement element, ElementBox box, string label, CascadedStyle? itemStyle)
    {
        string display = box.EmittedDisplay is "inline" ? "inline-block" : box.EmittedDisplay;
        _out.Append("<a");
        AppendAttribute("href", ScriptLinkPrefix + _ids.GetId(element).ToString(CultureInfo.InvariantCulture));
        AppendAttribute("style", "text-decoration:none;color:" + box.Color.ToCss() + ";" + BuildStyle(element, box, "a", display, inheritedOnly: false, itemStyle));
        _out.Append('>');
        WriteText(label, box);
        _out.Append("</a>");
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private bool TryResolveUrl(string? reference, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (!Uri.TryCreate(_baseUri, reference.Trim(), out Uri? resolved) ||
            !(resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
        {
            return false;
        }

        url = resolved.AbsoluteUri;
        return true;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(character);
        }

        return builder.ToString();
    }
}

/// <summary>CSS lengths and math functions (calc, min, max, clamp) worked out to pixels where possible.</summary>
internal static class CssLength
{
    /// <summary>What lengths are relative to.</summary>
    public readonly record struct Context(double FontSizePx, double RootFontSizePx, double ViewportWidthPx, double ViewportHeightPx, double PercentBasisPx);

    /// <summary>A length as pixels plus (when it has one) a percentage of something not known here.</summary>
    public readonly record struct Value(double Px, double Percent, bool HasPercent, bool IsNumber);

    public static bool TryEvaluate(string? text, Context context, out Value value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        try
        {
            var parser = new Parser(trimmed, context);
            Value? result = parser.ParseExpression();
            parser.SkipWhitespace();
            if (result is null || !parser.AtEnd || result.Value.IsNumber && result.Value.Px != 0)
            {
                return false;
            }

            value = result.Value;
            return !double.IsNaN(value.Px) && !double.IsInfinity(value.Px);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException or DivideByZeroException)
        {
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly Context _context;
        private int _position;
        private int _depth;

        public Parser(string text, Context context)
        {
            _text = text;
            _context = context;
        }

        public bool AtEnd => _position >= _text.Length;

        public void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        public Value? ParseExpression()
        {
            if (++_depth > 32)
            {
                throw new FormatException("Too deeply nested.");
            }

            Value? left = ParseTerm();
            while (left is not null)
            {
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] is not ('+' or '-'))
                {
                    break;
                }

                char op = _text[_position++];
                Value? right = ParseTerm();
                if (right is null)
                {
                    return null;
                }

                left = Combine(left.Value, right.Value, op == '+' ? 1 : -1);
            }

            _depth--;
            return left;
        }

        private static Value Combine(Value a, Value b, int sign)
        {
            if (a.IsNumber && b.IsNumber)
            {
                return new Value(a.Px + (sign * b.Px), 0, false, true);
            }

            return new Value(a.Px + (sign * b.Px), a.Percent + (sign * b.Percent), a.HasPercent || b.HasPercent, false);
        }

        private Value? ParseTerm()
        {
            Value? left = ParseFactor();
            while (left is not null)
            {
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] is not ('*' or '/'))
                {
                    break;
                }

                char op = _text[_position++];
                Value? right = ParseFactor();
                if (right is null)
                {
                    return null;
                }

                if (op == '*')
                {
                    if (right.Value.IsNumber)
                    {
                        left = Scale(left.Value, right.Value.Px);
                    }
                    else if (left.Value.IsNumber)
                    {
                        left = Scale(right.Value, left.Value.Px);
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    if (!right.Value.IsNumber || right.Value.Px == 0)
                    {
                        return null;
                    }

                    left = Scale(left.Value, 1 / right.Value.Px);
                }
            }

            return left;
        }

        private static Value Scale(Value value, double factor) => new(value.Px * factor, value.Percent * factor, value.HasPercent, value.IsNumber);

        private Value? ParseFactor()
        {
            SkipWhitespace();
            if (_position >= _text.Length)
            {
                return null;
            }

            char character = _text[_position];
            if (character == '(')
            {
                _position++;
                Value? inner = ParseExpression();
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] != ')')
                {
                    return null;
                }

                _position++;
                return inner;
            }

            if (char.IsLetter(character) || character == '-' && _position + 1 < _text.Length && char.IsLetter(_text[_position + 1]))
            {
                int start = _position;
                while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '-'))
                {
                    _position++;
                }

                string name = _text[start.._position].ToLowerInvariant();
                SkipWhitespace();
                if (_position < _text.Length && _text[_position] == '(')
                {
                    _position++;
                    return ParseFunction(name.TrimStart('-').Replace("webkit-", string.Empty, StringComparison.Ordinal).Replace("moz-", string.Empty, StringComparison.Ordinal));
                }

                return name switch
                {
                    "pi" => new Value(Math.PI, 0, false, true),
                    "e" => new Value(Math.E, 0, false, true),
                    _ => null,
                };
            }

            int numberStart = _position;
            if (_position < _text.Length && _text[_position] is '+' or '-')
            {
                _position++;
            }

            while (_position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.'))
            {
                _position++;
            }

            if (_position < _text.Length - 1 && _text[_position] is 'e' or 'E' && (char.IsAsciiDigit(_text[_position + 1]) || _text[_position + 1] is '-' or '+'))
            {
                _position += 2;
                while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
                {
                    _position++;
                }
            }

            if (_position == numberStart)
            {
                return null;
            }

            double number = double.Parse(_text[numberStart.._position], NumberStyles.Float, CultureInfo.InvariantCulture);
            int unitStart = _position;
            while (_position < _text.Length && (char.IsLetter(_text[_position]) || _text[_position] == '%'))
            {
                _position++;
            }

            string unit = _text[unitStart.._position].ToLowerInvariant();
            return ToValue(number, unit);
        }

        private Value? ToValue(double number, string unit)
        {
            double vw = _context.ViewportWidthPx / 100;
            double vh = _context.ViewportHeightPx / 100;
            double? px = unit switch
            {
                "px" => number,
                "em" => number * _context.FontSizePx,
                "rem" => number * _context.RootFontSizePx,
                "ex" or "ch" => number * _context.FontSizePx * 0.5,
                "rex" or "rch" => number * _context.RootFontSizePx * 0.5,
                "cap" => number * _context.FontSizePx * 0.7,
                "ic" => number * _context.FontSizePx,
                "lh" => number * _context.FontSizePx * 1.2,
                "rlh" => number * _context.RootFontSizePx * 1.2,
                "vw" or "svw" or "lvw" or "dvw" or "vi" or "svi" or "lvi" or "dvi" or "cqw" or "cqi" => number * vw,
                "vh" or "svh" or "lvh" or "dvh" or "vb" or "svb" or "lvb" or "dvb" or "cqh" or "cqb" => number * vh,
                "vmin" or "svmin" or "lvmin" or "dvmin" or "cqmin" => number * Math.Min(vw, vh),
                "vmax" or "svmax" or "lvmax" or "dvmax" or "cqmax" => number * Math.Max(vw, vh),
                "pt" => number * 96 / 72,
                "pc" => number * 16,
                "in" => number * 96,
                "cm" => number * 96 / 2.54,
                "mm" => number * 96 / 25.4,
                "q" => number * 96 / 101.6,
                _ => null,
            };

            if (px is double pixels)
            {
                return new Value(pixels, 0, false, false);
            }

            if (unit == "%")
            {
                return new Value(0, number, true, false);
            }

            if (unit.Length == 0)
            {
                // A unitless 0 is a length; other numbers are factors (in calc).
                return new Value(number, 0, false, true);
            }

            return null;
        }

        private Value? ParseFunction(string name)
        {
            var arguments = new List<Value>();
            while (true)
            {
                Value? argument = ParseExpression();
                if (argument is null)
                {
                    return null;
                }

                arguments.Add(argument.Value);
                SkipWhitespace();
                if (_position >= _text.Length)
                {
                    return null;
                }

                if (_text[_position] == ',')
                {
                    _position++;
                    continue;
                }

                if (_text[_position] == ')')
                {
                    _position++;
                    break;
                }

                return null;
            }

            switch (name)
            {
                case "calc":
                    return arguments.Count == 1 ? arguments[0] : null;
                case "min":
                case "max":
                {
                    Value best = arguments[0];
                    foreach (Value candidate in arguments.Skip(1))
                    {
                        double a = Resolve(best);
                        double b = Resolve(candidate);
                        if ((name == "min" && b < a) || (name == "max" && b > a))
                        {
                            best = candidate;
                        }
                    }

                    return best;
                }
                case "clamp":
                {
                    if (arguments.Count != 3)
                    {
                        return null;
                    }

                    double minimum = Resolve(arguments[0]);
                    double preferred = Resolve(arguments[1]);
                    double maximum = Resolve(arguments[2]);
                    return preferred < minimum ? arguments[0] : preferred > maximum ? arguments[2] : arguments[1];
                }
                case "abs":
                    return arguments.Count == 1 ? new Value(Math.Abs(arguments[0].Px), Math.Abs(arguments[0].Percent), arguments[0].HasPercent, arguments[0].IsNumber) : null;
                case "round":
                    // round(value, step) is close enough to the value itself for drawing.
                    return arguments[0];
                default:
                    return null;
            }
        }

        // Comparing a percentage needs what it is a percentage of.
        private double Resolve(Value value) => value.Px + (value.HasPercent ? _context.PercentBasisPx * value.Percent / 100 : 0);
    }
}
