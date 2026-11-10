using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhantomHaze;

// A small, dependency-free SVG renderer for extension graphics (ui.svg, SVG meta.icon values). It supports what icon
// sets and hand-written graphics use: <svg viewBox>, <g>, <path> (every path command, including arcs), <rect>,
// <circle>, <ellipse>, <line>, <polyline>, <polygon>, fill/stroke/opacity styling (attributes or style=""),
// transforms, currentColor, and gradients (drawn with their average colour). Text, images, filters, masks, scripts
// and external references are ignored, and DTDs are never processed, so an SVG cannot read files or reach the network.
//
// Parsing (PhantomSvgDocument.Parse) only builds a geometry model; drawing it (PhantomSvgRenderer) is separate.

/// <summary>An SVG transform: (x, y) maps to (A·x + C·y + E, B·x + D·y + F), like matrix(a b c d e f).</summary>
internal readonly record struct PhantomSvgMatrix(float A, float B, float C, float D, float E, float F)
{
    public static PhantomSvgMatrix Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>This transform applied after <paramref name="inner"/> (SVG: parent transform × child transform).</summary>
    public PhantomSvgMatrix Multiply(PhantomSvgMatrix inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);

    public PointF Apply(PointF point) => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);

    /// <summary>How much the transform scales lengths on average (used to pad bounds for stroke widths).</summary>
    public float AverageScale => (float)Math.Sqrt(Math.Abs(A * D - B * C));
}

internal enum PhantomSvgPaintKind
{
    None,
    Color,

    /// <summary>The colour of the control the graphic is drawn on (its text colour), so icons follow the theme.</summary>
    CurrentColor,
}

internal readonly record struct PhantomSvgPaint(PhantomSvgPaintKind Kind, Color Color)
{
    public static PhantomSvgPaint None => new(PhantomSvgPaintKind.None, Color.Empty);
    public static PhantomSvgPaint CurrentColor => new(PhantomSvgPaintKind.CurrentColor, Color.Empty);
    public static PhantomSvgPaint FromColor(Color color) => new(PhantomSvgPaintKind.Color, color);
}

internal enum PhantomSvgSegmentKind
{
    Line,
    Cubic,
}

internal readonly record struct PhantomSvgSegment(PhantomSvgSegmentKind Kind, PointF Control1, PointF Control2, PointF End);

internal sealed class PhantomSvgFigure
{
    public PointF Start { get; init; }
    public List<PhantomSvgSegment> Segments { get; } = new();
    public bool Closed { get; set; }
}

internal enum PhantomSvgLineCap
{
    Butt,
    Round,
    Square,
}

internal enum PhantomSvgLineJoin
{
    Miter,
    Round,
    Bevel,
}

internal sealed class PhantomSvgShape
{
    public List<PhantomSvgFigure> Figures { get; } = new();
    public PhantomSvgPaint Fill { get; init; }
    public PhantomSvgPaint Stroke { get; init; }
    public float FillOpacity { get; init; } = 1f;
    public float StrokeOpacity { get; init; } = 1f;
    public float StrokeWidth { get; init; } = 1f;
    public bool EvenOdd { get; init; }
    public PhantomSvgLineCap LineCap { get; init; }
    public PhantomSvgLineJoin LineJoin { get; init; }
    public float MiterLimit { get; init; } = 4f;
    public PhantomSvgMatrix Transform { get; init; } = PhantomSvgMatrix.Identity;
}

internal sealed class PhantomSvgDocument
{
    private const int MaximumSourceLength = 1_000_000;
    private const int MaximumElements = 10_000;
    private const int MaximumSegments = 200_000;
    private const int MaximumDepth = 64;

    private PhantomSvgDocument(RectangleF viewBox, List<PhantomSvgShape> shapes, bool usesCurrentColor)
    {
        ViewBox = viewBox;
        Shapes = shapes;
        UsesCurrentColor = usesCurrentColor;
    }

    /// <summary>The area of the drawing that is fitted (centred, aspect ratio kept) into the target rectangle.</summary>
    public RectangleF ViewBox { get; }

    public IReadOnlyList<PhantomSvgShape> Shapes { get; }

    /// <summary>True when something is painted with currentColor, so the graphic must be redrawn when the text colour changes.</summary>
    public bool UsesCurrentColor { get; }

    /// <summary>Decodes data:image/svg+xml;base64,... and data:image/svg+xml,... (URL-encoded) values.</summary>
    public static bool TryDecodeDataUri(string? text, out string svgSource)
    {
        svgSource = string.Empty;
        string value = (text ?? string.Empty).Trim();
        if (!value.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int comma = value.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        string header = value[..comma];
        string payload = value[(comma + 1)..];
        try
        {
            svgSource = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                : Uri.UnescapeDataString(payload);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Parses SVG markup. Throws <see cref="FormatException"/> with a readable message when it cannot be used.</summary>
    public static PhantomSvgDocument Parse(string? source)
    {
        string text = (source ?? string.Empty).Trim().TrimStart('﻿');
        if (text.Length == 0)
        {
            throw new FormatException("The SVG is empty.");
        }

        if (text.Length > MaximumSourceLength)
        {
            throw new FormatException("The SVG is too large (the limit is 1 MB).");
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                // DTDs are skipped entirely: no entity expansion and nothing is ever loaded from outside the file.
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"The SVG is not valid XML: {ex.Message}");
        }

        XElement? root = document.Root;
        if (root is null || root.Name.LocalName != "svg")
        {
            throw new FormatException("The graphic must start with an <svg> element.");
        }

        RectangleF? declaredViewBox = ParseViewBox(root);
        var context = new ParseContext(declaredViewBox ?? new RectangleF(0, 0, 100, 100));
        CollectPaintServers(root, context);
        Walk(root, new Style(), PhantomSvgMatrix.Identity, context, depth: 0, isRoot: true);

        if (context.Shapes.Count == 0)
        {
            throw new FormatException("The SVG has nothing PhantomHaze can draw (supported: path, rect, circle, ellipse, line, polyline, polygon).");
        }

        RectangleF viewBox = declaredViewBox ?? ComputeBounds(context.Shapes);
        return new PhantomSvgDocument(viewBox, context.Shapes, context.UsesCurrentColor);
    }

    private sealed class ParseContext
    {
        public ParseContext(RectangleF referenceBox)
        {
            ReferenceBox = referenceBox;
        }

        /// <summary>What percentage lengths refer to.</summary>
        public RectangleF ReferenceBox { get; }

        public Dictionary<string, Color> PaintServers { get; } = new(StringComparer.Ordinal);
        public List<PhantomSvgShape> Shapes { get; } = new();
        public bool UsesCurrentColor { get; set; }
        public int ElementCount { get; set; }
        public int SegmentCount { get; set; }
    }

    private sealed class Style
    {
        public PhantomSvgPaint Fill = PhantomSvgPaint.FromColor(Color.Black);
        public PhantomSvgPaint Stroke = PhantomSvgPaint.None;
        public float FillOpacity = 1f;
        public float StrokeOpacity = 1f;

        /// <summary>Group opacity (opacity on an element and its ancestors), approximated per shape.</summary>
        public float Opacity = 1f;

        public float StrokeWidth = 1f;
        public bool EvenOdd;
        public PhantomSvgLineCap LineCap = PhantomSvgLineCap.Butt;
        public PhantomSvgLineJoin LineJoin = PhantomSvgLineJoin.Miter;
        public float MiterLimit = 4f;

        /// <summary>The CSS 'color' property: what currentColor means inside this element, when set.</summary>
        public Color? ColorProperty;

        public bool Hidden;

        public Style Clone() => (Style)MemberwiseClone();
    }

    private enum LengthAxis
    {
        X,
        Y,
        Other,
    }

    private static RectangleF? ParseViewBox(XElement root)
    {
        string? viewBox = (string?)root.Attribute("viewBox");
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            List<float> numbers = ParseNumberList(viewBox);
            if (numbers.Count == 4 && numbers[2] > 0 && numbers[3] > 0)
            {
                return new RectangleF(numbers[0], numbers[1], numbers[2], numbers[3]);
            }
        }

        var unitBox = new RectangleF(0, 0, 100, 100);
        string? widthText = (string?)root.Attribute("width");
        string? heightText = (string?)root.Attribute("height");
        if (widthText is not null && heightText is not null && !widthText.Contains('%') && !heightText.Contains('%') &&
            TryParseLength(widthText, LengthAxis.X, unitBox, out float width) &&
            TryParseLength(heightText, LengthAxis.Y, unitBox, out float height) &&
            width > 0 && height > 0)
        {
            return new RectangleF(0, 0, width, height);
        }

        return null;
    }

    /// <summary>Gradients are drawn with the average of their stop colours, which keeps icons recognisable.</summary>
    private static void CollectPaintServers(XElement root, ParseContext context)
    {
        var gradients = root.Descendants()
            .Where(element => element.Name.LocalName is "linearGradient" or "radialGradient")
            .ToList();

        // Gradients can borrow the stops of another gradient through href, sometimes in a chain.
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (XElement gradient in gradients)
            {
                string? id = (string?)gradient.Attribute("id");
                if (string.IsNullOrEmpty(id) || context.PaintServers.ContainsKey(id))
                {
                    continue;
                }

                List<Color> stops = gradient.Elements()
                    .Where(element => element.Name.LocalName == "stop")
                    .Select(ReadStopColor)
                    .ToList();
                if (stops.Count > 0)
                {
                    context.PaintServers[id] = AverageColor(stops);
                    continue;
                }

                string? reference = gradient.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "href")?.Value;
                if (reference is not null && reference.StartsWith('#') &&
                    context.PaintServers.TryGetValue(reference[1..], out Color inherited))
                {
                    context.PaintServers[id] = inherited;
                }
            }
        }
    }

    private static Color ReadStopColor(XElement stop)
    {
        string? colorText = (string?)stop.Attribute("stop-color");
        string? opacityText = (string?)stop.Attribute("stop-opacity");
        foreach ((string name, string value) in ReadStyleDeclarations((string?)stop.Attribute("style")))
        {
            if (name == "stop-color")
            {
                colorText = value;
            }
            else if (name == "stop-opacity")
            {
                opacityText = value;
            }
        }

        Color color = PhantomColorParser.TryParse(colorText, out Color parsed) ? parsed : Color.Black;
        if (TryParseOpacity(opacityText, out float opacity))
        {
            color = Color.FromArgb((int)Math.Round(color.A * opacity), color);
        }

        return color;
    }

    private static Color AverageColor(List<Color> colors)
    {
        int a = 0, r = 0, g = 0, b = 0;
        foreach (Color color in colors)
        {
            a += color.A;
            r += color.R;
            g += color.G;
            b += color.B;
        }

        int count = colors.Count;
        return Color.FromArgb(a / count, r / count, g / count, b / count);
    }

    private static void Walk(XElement element, Style parentStyle, PhantomSvgMatrix parentTransform, ParseContext context, int depth, bool isRoot)
    {
        if (depth > MaximumDepth)
        {
            throw new FormatException($"The SVG is nested too deeply (the limit is {MaximumDepth} levels).");
        }

        if (++context.ElementCount > MaximumElements)
        {
            throw new FormatException($"The SVG has too many elements (the limit is {MaximumElements}).");
        }

        string name = element.Name.LocalName;
        if (name is not ("svg" or "g" or "a" or "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
        {
            // defs, symbol, clipPath, mask, pattern, marker, text, image, use, style, script, foreignObject, ...
            return;
        }

        Style style = parentStyle.Clone();
        if (!ApplyPresentation(element, style, context))
        {
            return; // display="none"
        }

        PhantomSvgMatrix transform = parentTransform.Multiply(ParseTransform((string?)element.Attribute("transform")));
        if (name == "svg" && !isRoot)
        {
            // Nested <svg>: positioned by x/y; its own viewBox scaling is not applied.
            TryParseLength((string?)element.Attribute("x"), LengthAxis.X, context.ReferenceBox, out float x);
            TryParseLength((string?)element.Attribute("y"), LengthAxis.Y, context.ReferenceBox, out float y);
            transform = transform.Multiply(new PhantomSvgMatrix(1, 0, 0, 1, x, y));
        }

        if (name is "svg" or "g" or "a")
        {
            foreach (XElement child in element.Elements())
            {
                Walk(child, style, transform, context, depth + 1, isRoot: false);
            }

            return;
        }

        List<PhantomSvgFigure> figures = name switch
        {
            "path" => PhantomSvgPathParser.Parse((string?)element.Attribute("d"), MaximumSegments - context.SegmentCount),
            "rect" => BuildRect(element, context),
            "circle" => BuildCircle(element, context),
            "ellipse" => BuildEllipse(element, context),
            "line" => BuildLine(element, context),
            _ => BuildPoly(element, closed: name == "polygon"),
        };

        context.SegmentCount += figures.Sum(figure => figure.Segments.Count);
        if (context.SegmentCount > MaximumSegments)
        {
            throw new FormatException("The SVG is too complex to draw.");
        }

        AddShape(figures, style, transform, context);
    }

    private static void AddShape(List<PhantomSvgFigure> figures, Style style, PhantomSvgMatrix transform, ParseContext context)
    {
        figures.RemoveAll(figure => figure.Segments.Count == 0);
        if (figures.Count == 0 || style.Hidden)
        {
            return;
        }

        float fillOpacity = style.FillOpacity * style.Opacity;
        float strokeOpacity = style.StrokeOpacity * style.Opacity;
        bool hasFill = style.Fill.Kind != PhantomSvgPaintKind.None && fillOpacity > 0;
        bool hasStroke = style.Stroke.Kind != PhantomSvgPaintKind.None && style.StrokeWidth > 0 && strokeOpacity > 0;
        if (!hasFill && !hasStroke)
        {
            return;
        }

        if ((hasFill && style.Fill.Kind == PhantomSvgPaintKind.CurrentColor) ||
            (hasStroke && style.Stroke.Kind == PhantomSvgPaintKind.CurrentColor))
        {
            context.UsesCurrentColor = true;
        }

        var shape = new PhantomSvgShape
        {
            Fill = hasFill ? style.Fill : PhantomSvgPaint.None,
            Stroke = hasStroke ? style.Stroke : PhantomSvgPaint.None,
            FillOpacity = fillOpacity,
            StrokeOpacity = strokeOpacity,
            StrokeWidth = style.StrokeWidth,
            EvenOdd = style.EvenOdd,
            LineCap = style.LineCap,
            LineJoin = style.LineJoin,
            MiterLimit = style.MiterLimit,
            Transform = transform,
        };
        shape.Figures.AddRange(figures);
        context.Shapes.Add(shape);
    }

    /// <returns>False when the element has display="none".</returns>
    private static bool ApplyPresentation(XElement element, Style style, ParseContext context)
    {
        var declarations = new List<(string Name, string Value)>();
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.Name.NamespaceName.Length == 0 && attribute.Name.LocalName != "style")
            {
                declarations.Add((attribute.Name.LocalName, attribute.Value));
            }
        }

        // Inline style="" declarations come last, so they win over presentation attributes, as in browsers.
        declarations.AddRange(ReadStyleDeclarations((string?)element.Attribute("style")));

        // 'color' is applied first: currentColor anywhere on this element refers to it.
        foreach ((string name, string value) in declarations)
        {
            if (name == "color" && PhantomColorParser.TryParse(value, out Color color))
            {
                style.ColorProperty = color;
            }
        }

        float? elementOpacity = null;
        bool displayed = true;
        foreach ((string name, string rawValue) in declarations)
        {
            string value = rawValue.Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (value.Length == 0 || value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (name)
            {
                case "fill":
                    if (TryParsePaint(value, style, context, out PhantomSvgPaint fill))
                    {
                        style.Fill = fill;
                    }

                    break;
                case "stroke":
                    if (TryParsePaint(value, style, context, out PhantomSvgPaint stroke))
                    {
                        style.Stroke = stroke;
                    }

                    break;
                case "fill-opacity":
                    if (TryParseOpacity(value, out float fillOpacity))
                    {
                        style.FillOpacity = fillOpacity;
                    }

                    break;
                case "stroke-opacity":
                    if (TryParseOpacity(value, out float strokeOpacity))
                    {
                        style.StrokeOpacity = strokeOpacity;
                    }

                    break;
                case "opacity":
                    if (TryParseOpacity(value, out float opacity))
                    {
                        elementOpacity = opacity;
                    }

                    break;
                case "stroke-width":
                    if (TryParseLength(value, LengthAxis.Other, context.ReferenceBox, out float width) && width >= 0)
                    {
                        style.StrokeWidth = width;
                    }

                    break;
                case "fill-rule":
                    style.EvenOdd = value.Equals("evenodd", StringComparison.OrdinalIgnoreCase);
                    break;
                case "stroke-linecap":
                    style.LineCap = value.ToLowerInvariant() switch
                    {
                        "round" => PhantomSvgLineCap.Round,
                        "square" => PhantomSvgLineCap.Square,
                        _ => PhantomSvgLineCap.Butt,
                    };
                    break;
                case "stroke-linejoin":
                    style.LineJoin = value.ToLowerInvariant() switch
                    {
                        "round" => PhantomSvgLineJoin.Round,
                        "bevel" => PhantomSvgLineJoin.Bevel,
                        _ => PhantomSvgLineJoin.Miter,
                    };
                    break;
                case "stroke-miterlimit":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float miterLimit) && miterLimit >= 1)
                    {
                        style.MiterLimit = miterLimit;
                    }

                    break;
                case "visibility":
                    style.Hidden = value.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
                                   value.Equals("collapse", StringComparison.OrdinalIgnoreCase);
                    break;
                case "display":
                    if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        displayed = false;
                    }

                    break;
            }
        }

        if (elementOpacity is float groupOpacity)
        {
            style.Opacity *= groupOpacity;
        }

        return displayed;
    }

    private static IEnumerable<(string Name, string Value)> ReadStyleDeclarations(string? inlineStyle)
    {
        if (string.IsNullOrWhiteSpace(inlineStyle))
        {
            yield break;
        }

        foreach (string declaration in inlineStyle.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon > 0)
            {
                yield return (declaration[..colon].Trim().ToLowerInvariant(), declaration[(colon + 1)..].Trim());
            }
        }
    }

    private static bool TryParsePaint(string value, Style style, ParseContext context, out PhantomSvgPaint paint)
    {
        paint = PhantomSvgPaint.None;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            paint = style.ColorProperty is Color color ? PhantomSvgPaint.FromColor(color) : PhantomSvgPaint.CurrentColor;
            return true;
        }

        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = value.IndexOf(')');
            if (close < 0)
            {
                return false;
            }

            string reference = value[4..close].Trim().Trim('\'', '"');
            string fallback = value[(close + 1)..].Trim();
            if (reference.StartsWith('#') && context.PaintServers.TryGetValue(reference[1..], out Color serverColor))
            {
                paint = PhantomSvgPaint.FromColor(serverColor);
                return true;
            }

            // Patterns and missing references: use the fallback colour when one is given, otherwise paint nothing.
            return fallback.Length == 0 || TryParsePaint(fallback, style, context, out paint);
        }

        if (PhantomColorParser.TryParse(value, out Color parsed))
        {
            paint = parsed.A == 0 ? PhantomSvgPaint.None : PhantomSvgPaint.FromColor(parsed);
            return true;
        }

        return false;
    }

    private static bool TryParseOpacity(string? text, out float opacity)
    {
        opacity = 1f;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        bool percent = value.EndsWith('%');
        if (!float.TryParse(percent ? value[..^1] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            !float.IsFinite(parsed))
        {
            return false;
        }

        opacity = Math.Clamp(percent ? parsed / 100f : parsed, 0f, 1f);
        return true;
    }

    private static readonly (string Unit, float Pixels)[] LengthUnits =
    [
        ("px", 1f), ("pt", 96f / 72f), ("pc", 16f), ("mm", 96f / 25.4f), ("cm", 96f / 2.54f), ("in", 96f), ("em", 16f), ("ex", 8f),
    ];

    private static bool TryParseLength(string? text, LengthAxis axis, RectangleF referenceBox, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string number = text.Trim();
        float multiplier = 1f;
        if (number.EndsWith('%'))
        {
            float reference = axis switch
            {
                LengthAxis.X => referenceBox.Width,
                LengthAxis.Y => referenceBox.Height,
                _ => (float)Math.Sqrt(((referenceBox.Width * referenceBox.Width) + (referenceBox.Height * referenceBox.Height)) / 2),
            };
            multiplier = reference / 100f;
            number = number[..^1];
        }
        else
        {
            foreach ((string unit, float pixels) in LengthUnits)
            {
                if (number.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
                {
                    multiplier = pixels;
                    number = number[..^unit.Length];
                    break;
                }
            }
        }

        if (!float.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) || !float.IsFinite(parsed))
        {
            return false;
        }

        value = parsed * multiplier;
        return true;
    }

    private static float ReadLength(XElement element, string attribute, LengthAxis axis, ParseContext context)
    {
        return TryParseLength((string?)element.Attribute(attribute), axis, context.ReferenceBox, out float value) ? value : 0f;
    }

    internal static List<float> ParseNumberList(string text)
    {
        var reader = new PhantomSvgNumberReader(text);
        var numbers = new List<float>();
        while (reader.TryReadNumber(out float number))
        {
            numbers.Add(number);
        }

        return numbers;
    }

    internal static PhantomSvgMatrix ParseTransform(string? text)
    {
        PhantomSvgMatrix result = PhantomSvgMatrix.Identity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        int position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == ','))
            {
                position++;
            }

            if (position >= text.Length)
            {
                break;
            }

            int nameStart = position;
            while (position < text.Length && char.IsAsciiLetter(text[position]))
            {
                position++;
            }

            string name = text[nameStart..position];
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            int close = position < text.Length && text[position] == '(' ? text.IndexOf(')', position) : -1;
            if (name.Length == 0 || close < 0)
            {
                // An invalid transform list is ignored as a whole, as in browsers.
                return PhantomSvgMatrix.Identity;
            }

            List<float> args = ParseNumberList(text[(position + 1)..close]);
            position = close + 1;

            PhantomSvgMatrix? step = (name, args.Count) switch
            {
                ("matrix", 6) => new PhantomSvgMatrix(args[0], args[1], args[2], args[3], args[4], args[5]),
                ("translate", 1) => new PhantomSvgMatrix(1, 0, 0, 1, args[0], 0),
                ("translate", 2) => new PhantomSvgMatrix(1, 0, 0, 1, args[0], args[1]),
                ("scale", 1) => new PhantomSvgMatrix(args[0], 0, 0, args[0], 0, 0),
                ("scale", 2) => new PhantomSvgMatrix(args[0], 0, 0, args[1], 0, 0),
                ("rotate", 1) => Rotation(args[0]),
                ("rotate", 3) => new PhantomSvgMatrix(1, 0, 0, 1, args[1], args[2])
                    .Multiply(Rotation(args[0]))
                    .Multiply(new PhantomSvgMatrix(1, 0, 0, 1, -args[1], -args[2])),
                ("skewX", 1) => new PhantomSvgMatrix(1, 0, (float)Math.Tan(args[0] * Math.PI / 180), 1, 0, 0),
                ("skewY", 1) => new PhantomSvgMatrix(1, (float)Math.Tan(args[0] * Math.PI / 180), 0, 1, 0, 0),
                _ => null,
            };

            if (step is null)
            {
                return PhantomSvgMatrix.Identity;
            }

            result = result.Multiply(step.Value);
        }

        return result;
    }

    private static PhantomSvgMatrix Rotation(float degrees)
    {
        double radians = degrees * Math.PI / 180;
        float cos = (float)Math.Cos(radians);
        float sin = (float)Math.Sin(radians);
        return new PhantomSvgMatrix(cos, sin, -sin, cos, 0, 0);
    }

    private static List<PhantomSvgFigure> BuildRect(XElement element, ParseContext context)
    {
        float x = ReadLength(element, "x", LengthAxis.X, context);
        float y = ReadLength(element, "y", LengthAxis.Y, context);
        float width = ReadLength(element, "width", LengthAxis.X, context);
        float height = ReadLength(element, "height", LengthAxis.Y, context);
        if (width <= 0 || height <= 0)
        {
            return new List<PhantomSvgFigure>();
        }

        bool hasRx = TryParseLength((string?)element.Attribute("rx"), LengthAxis.X, context.ReferenceBox, out float rx) && rx > 0;
        bool hasRy = TryParseLength((string?)element.Attribute("ry"), LengthAxis.Y, context.ReferenceBox, out float ry) && ry > 0;
        if (!hasRx)
        {
            rx = hasRy ? ry : 0;
        }

        if (!hasRy)
        {
            ry = rx;
        }

        rx = Math.Min(rx, width / 2);
        ry = Math.Min(ry, height / 2);

        var builder = new PhantomSvgPathBuilder();
        if (rx <= 0 || ry <= 0)
        {
            builder.MoveTo(new PointF(x, y));
            builder.LineTo(new PointF(x + width, y));
            builder.LineTo(new PointF(x + width, y + height));
            builder.LineTo(new PointF(x, y + height));
            builder.Close();
            return builder.Figures;
        }

        float right = x + width;
        float bottom = y + height;
        builder.MoveTo(new PointF(x + rx, y));
        builder.LineTo(new PointF(right - rx, y));
        builder.ArcTo(rx, ry, 0, largeArc: false, sweep: true, new PointF(right, y + ry));
        builder.LineTo(new PointF(right, bottom - ry));
        builder.ArcTo(rx, ry, 0, largeArc: false, sweep: true, new PointF(right - rx, bottom));
        builder.LineTo(new PointF(x + rx, bottom));
        builder.ArcTo(rx, ry, 0, largeArc: false, sweep: true, new PointF(x, bottom - ry));
        builder.LineTo(new PointF(x, y + ry));
        builder.ArcTo(rx, ry, 0, largeArc: false, sweep: true, new PointF(x + rx, y));
        builder.Close();
        return builder.Figures;
    }

    private static List<PhantomSvgFigure> BuildCircle(XElement element, ParseContext context)
    {
        float r = ReadLength(element, "r", LengthAxis.Other, context);
        return BuildEllipseFigures(ReadLength(element, "cx", LengthAxis.X, context), ReadLength(element, "cy", LengthAxis.Y, context), r, r);
    }

    private static List<PhantomSvgFigure> BuildEllipse(XElement element, ParseContext context)
    {
        return BuildEllipseFigures(
            ReadLength(element, "cx", LengthAxis.X, context),
            ReadLength(element, "cy", LengthAxis.Y, context),
            ReadLength(element, "rx", LengthAxis.X, context),
            ReadLength(element, "ry", LengthAxis.Y, context));
    }

    private static List<PhantomSvgFigure> BuildEllipseFigures(float cx, float cy, float rx, float ry)
    {
        var builder = new PhantomSvgPathBuilder();
        if (rx <= 0 || ry <= 0)
        {
            return builder.Figures;
        }

        const float kappa = 0.5522847498f;
        float kx = kappa * rx;
        float ky = kappa * ry;
        builder.MoveTo(new PointF(cx + rx, cy));
        builder.CubicTo(new PointF(cx + rx, cy + ky), new PointF(cx + kx, cy + ry), new PointF(cx, cy + ry));
        builder.CubicTo(new PointF(cx - kx, cy + ry), new PointF(cx - rx, cy + ky), new PointF(cx - rx, cy));
        builder.CubicTo(new PointF(cx - rx, cy - ky), new PointF(cx - kx, cy - ry), new PointF(cx, cy - ry));
        builder.CubicTo(new PointF(cx + kx, cy - ry), new PointF(cx + rx, cy - ky), new PointF(cx + rx, cy));
        builder.Close();
        return builder.Figures;
    }

    private static List<PhantomSvgFigure> BuildLine(XElement element, ParseContext context)
    {
        var builder = new PhantomSvgPathBuilder();
        builder.MoveTo(new PointF(ReadLength(element, "x1", LengthAxis.X, context), ReadLength(element, "y1", LengthAxis.Y, context)));
        builder.LineTo(new PointF(ReadLength(element, "x2", LengthAxis.X, context), ReadLength(element, "y2", LengthAxis.Y, context)));
        return builder.Figures;
    }

    private static List<PhantomSvgFigure> BuildPoly(XElement element, bool closed)
    {
        List<float> numbers = ParseNumberList((string?)element.Attribute("points") ?? string.Empty);
        var builder = new PhantomSvgPathBuilder();
        for (int i = 0; i + 1 < numbers.Count; i += 2)
        {
            var point = new PointF(numbers[i], numbers[i + 1]);
            if (i == 0)
            {
                builder.MoveTo(point);
            }
            else
            {
                builder.LineTo(point);
            }
        }

        if (closed)
        {
            builder.Close();
        }

        return builder.Figures;
    }

    private static RectangleF ComputeBounds(List<PhantomSvgShape> shapes)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (PhantomSvgShape shape in shapes)
        {
            float pad = shape.Stroke.Kind == PhantomSvgPaintKind.None ? 0 : shape.StrokeWidth * shape.Transform.AverageScale / 2;

            void Include(PointF point)
            {
                PointF mapped = shape.Transform.Apply(point);
                minX = Math.Min(minX, mapped.X - pad);
                minY = Math.Min(minY, mapped.Y - pad);
                maxX = Math.Max(maxX, mapped.X + pad);
                maxY = Math.Max(maxY, mapped.Y + pad);
            }

            foreach (PhantomSvgFigure figure in shape.Figures)
            {
                Include(figure.Start);
                foreach (PhantomSvgSegment segment in figure.Segments)
                {
                    if (segment.Kind == PhantomSvgSegmentKind.Cubic)
                    {
                        Include(segment.Control1);
                        Include(segment.Control2);
                    }

                    Include(segment.End);
                }
            }
        }

        if (minX > maxX || minY > maxY)
        {
            return new RectangleF(0, 0, 24, 24);
        }

        return new RectangleF(minX, minY, Math.Max(maxX - minX, 0.001f), Math.Max(maxY - minY, 0.001f));
    }
}

/// <summary>Reads numbers the way SVG writes them: "1.5.5" is 1.5 and .5, "-1-2" is -1 and -2, exponents allowed.</summary>
internal sealed class PhantomSvgNumberReader
{
    private readonly string _text;
    private int _position;

    public PhantomSvgNumberReader(string? text)
    {
        _text = text ?? string.Empty;
    }

    public bool AtEnd
    {
        get
        {
            SkipSeparators();
            return _position >= _text.Length;
        }
    }

    public char Peek() => _position < _text.Length ? _text[_position] : '\0';

    public void Advance() => _position++;

    public void SkipSeparators()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }

        if (_position < _text.Length && _text[_position] == ',')
        {
            _position++;
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }
    }

    public bool TryReadNumber(out float value)
    {
        value = 0;
        SkipSeparators();
        int start = _position;
        int position = _position;
        if (position < _text.Length && (_text[position] == '+' || _text[position] == '-'))
        {
            position++;
        }

        bool hasDigits = false;
        while (position < _text.Length && char.IsAsciiDigit(_text[position]))
        {
            position++;
            hasDigits = true;
        }

        if (position < _text.Length && _text[position] == '.')
        {
            position++;
            while (position < _text.Length && char.IsAsciiDigit(_text[position]))
            {
                position++;
                hasDigits = true;
            }
        }

        if (!hasDigits)
        {
            return false;
        }

        if (position < _text.Length && (_text[position] == 'e' || _text[position] == 'E'))
        {
            int exponentStart = position;
            position++;
            if (position < _text.Length && (_text[position] == '+' || _text[position] == '-'))
            {
                position++;
            }

            bool hasExponentDigits = false;
            while (position < _text.Length && char.IsAsciiDigit(_text[position]))
            {
                position++;
                hasExponentDigits = true;
            }

            if (!hasExponentDigits)
            {
                position = exponentStart;
            }
        }

        if (!float.TryParse(_text.AsSpan(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !float.IsFinite(value))
        {
            return false;
        }

        _position = position;
        return true;
    }

    /// <summary>Arc flags are a single 0 or 1 and need no separator ("a1 1 0 00 1 1" is valid).</summary>
    public bool TryReadFlag(out bool flag)
    {
        flag = false;
        SkipSeparators();
        if (_position < _text.Length && (_text[_position] == '0' || _text[_position] == '1'))
        {
            flag = _text[_position] == '1';
            _position++;
            return true;
        }

        return false;
    }

    public bool TryReadPoint(out PointF point)
    {
        point = default;
        int start = _position;
        if (TryReadNumber(out float x) && TryReadNumber(out float y))
        {
            point = new PointF(x, y);
            return true;
        }

        _position = start;
        return false;
    }
}

internal sealed class PhantomSvgPathBuilder
{
    private PhantomSvgFigure? _figure;
    private PointF _current;
    private PointF _subpathStart;

    public List<PhantomSvgFigure> Figures { get; } = new();
    public PointF Current => _current;
    public int SegmentCount { get; private set; }

    public void MoveTo(PointF point)
    {
        _figure = new PhantomSvgFigure { Start = point };
        Figures.Add(_figure);
        _current = point;
        _subpathStart = point;
    }

    public void LineTo(PointF point)
    {
        EnsureFigure().Segments.Add(new PhantomSvgSegment(PhantomSvgSegmentKind.Line, default, default, point));
        SegmentCount++;
        _current = point;
    }

    public void CubicTo(PointF control1, PointF control2, PointF end)
    {
        EnsureFigure().Segments.Add(new PhantomSvgSegment(PhantomSvgSegmentKind.Cubic, control1, control2, end));
        SegmentCount++;
        _current = end;
    }

    public void QuadraticTo(PointF control, PointF end)
    {
        PointF start = _current;
        var control1 = new PointF(start.X + (2f / 3f * (control.X - start.X)), start.Y + (2f / 3f * (control.Y - start.Y)));
        var control2 = new PointF(end.X + (2f / 3f * (control.X - end.X)), end.Y + (2f / 3f * (control.Y - end.Y)));
        CubicTo(control1, control2, end);
    }

    /// <summary>An elliptical arc in SVG endpoint form, converted to cubic Béziers (SVG 1.1 appendix F.6).</summary>
    public void ArcTo(float radiusX, float radiusY, float rotationDegrees, bool largeArc, bool sweep, PointF end)
    {
        PointF start = _current;
        if (start == end)
        {
            return;
        }

        double rx = Math.Abs(radiusX);
        double ry = Math.Abs(radiusY);
        if (rx < 1e-9 || ry < 1e-9)
        {
            LineTo(end);
            return;
        }

        double phi = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(phi);
        double sin = Math.Sin(phi);
        double dx2 = (start.X - end.X) / 2.0;
        double dy2 = (start.Y - end.Y) / 2.0;
        double x1 = (cos * dx2) + (sin * dy2);
        double y1 = (-sin * dx2) + (cos * dy2);

        // Radii that are too small to reach the end point are scaled up (F.6.6).
        double lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1)
        {
            double scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        double numerator = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        double denominator = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        double coefficient = denominator <= 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
        if (largeArc == sweep)
        {
            coefficient = -coefficient;
        }

        double cxPrime = coefficient * (rx * y1 / ry);
        double cyPrime = coefficient * -(ry * x1 / rx);
        double cx = (cos * cxPrime) - (sin * cyPrime) + ((start.X + end.X) / 2.0);
        double cy = (sin * cxPrime) + (cos * cyPrime) + ((start.Y + end.Y) / 2.0);

        double theta1 = VectorAngle(1, 0, (x1 - cxPrime) / rx, (y1 - cyPrime) / ry);
        double deltaTheta = VectorAngle((x1 - cxPrime) / rx, (y1 - cyPrime) / ry, (-x1 - cxPrime) / rx, (-y1 - cyPrime) / ry);
        if (!sweep && deltaTheta > 0)
        {
            deltaTheta -= 2 * Math.PI;
        }
        else if (sweep && deltaTheta < 0)
        {
            deltaTheta += 2 * Math.PI;
        }

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(deltaTheta) / (Math.PI / 2) - 1e-9));
        double delta = deltaTheta / segments;
        double t = 4.0 / 3.0 * Math.Tan(delta / 4);

        PointF Map(double unitX, double unitY) => new(
            (float)(cx + (rx * unitX * cos) - (ry * unitY * sin)),
            (float)(cy + (rx * unitX * sin) + (ry * unitY * cos)));

        for (int i = 0; i < segments; i++)
        {
            double angle1 = theta1 + (i * delta);
            double angle2 = angle1 + delta;
            double cos1 = Math.Cos(angle1), sin1 = Math.Sin(angle1);
            double cos2 = Math.Cos(angle2), sin2 = Math.Sin(angle2);
            PointF control1 = Map(cos1 - (t * sin1), sin1 + (t * cos1));
            PointF control2 = Map(cos2 + (t * sin2), sin2 - (t * cos2));

            // The last segment ends exactly on the requested point, so rounding never leaves a gap.
            PointF segmentEnd = i == segments - 1 ? end : Map(cos2, sin2);
            CubicTo(control1, control2, segmentEnd);
        }
    }

    public void Close()
    {
        if (_figure is not null)
        {
            _figure.Closed = true;
            _current = _subpathStart;
        }
    }

    private PhantomSvgFigure EnsureFigure()
    {
        // A drawing command after a close path starts a new subpath at the same point (SVG 1.1, 8.3.3).
        if (_figure is null || _figure.Closed)
        {
            _figure = new PhantomSvgFigure { Start = _current };
            Figures.Add(_figure);
            _subpathStart = _current;
        }

        return _figure;
    }

    private static double VectorAngle(double ux, double uy, double vx, double vy)
    {
        return Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
    }
}

/// <summary>Parses SVG path data ("d"). Like browsers, it draws everything up to the first error instead of rejecting the path.</summary>
internal static class PhantomSvgPathParser
{
    private const string Commands = "MmLlHhVvCcSsQqTtAaZz";

    public static List<PhantomSvgFigure> Parse(string? data, int maximumSegments)
    {
        var builder = new PhantomSvgPathBuilder();
        var reader = new PhantomSvgNumberReader(data);
        char command = '\0';
        char previousCommand = '\0';
        PointF lastCubicControl = default;
        PointF lastQuadraticControl = default;

        while (!reader.AtEnd)
        {
            char next = reader.Peek();
            if (Commands.Contains(next, StringComparison.Ordinal))
            {
                command = next;
                reader.Advance();
            }
            else if (command is '\0' or 'Z' or 'z')
            {
                // Numbers before the first command or right after a close path are an error: stop here.
                break;
            }

            bool relative = char.IsLower(command);
            char kind = char.ToUpperInvariant(command);
            PointF current = builder.Current;
            PointF Offset(PointF point) => relative ? new PointF(current.X + point.X, current.Y + point.Y) : point;

            if (builder.Figures.Count == 0 && kind != 'M')
            {
                break; // Path data must start with a move.
            }

            bool ok;
            switch (kind)
            {
                case 'M':
                {
                    ok = reader.TryReadPoint(out PointF point);
                    if (ok)
                    {
                        builder.MoveTo(Offset(point));
                        // Further coordinate pairs after a move are implicit line commands.
                        command = relative ? 'l' : 'L';
                    }

                    break;
                }
                case 'L':
                {
                    ok = reader.TryReadPoint(out PointF point);
                    if (ok)
                    {
                        builder.LineTo(Offset(point));
                    }

                    break;
                }
                case 'H':
                {
                    ok = reader.TryReadNumber(out float x);
                    if (ok)
                    {
                        builder.LineTo(new PointF(relative ? current.X + x : x, current.Y));
                    }

                    break;
                }
                case 'V':
                {
                    ok = reader.TryReadNumber(out float y);
                    if (ok)
                    {
                        builder.LineTo(new PointF(current.X, relative ? current.Y + y : y));
                    }

                    break;
                }
                case 'C':
                {
                    ok = false;
                    if (reader.TryReadPoint(out PointF c1) && reader.TryReadPoint(out PointF c2) && reader.TryReadPoint(out PointF end))
                    {
                        lastCubicControl = Offset(c2);
                        builder.CubicTo(Offset(c1), lastCubicControl, Offset(end));
                        ok = true;
                    }

                    break;
                }
                case 'S':
                {
                    ok = false;
                    if (reader.TryReadPoint(out PointF c2) && reader.TryReadPoint(out PointF end))
                    {
                        PointF c1 = previousCommand is 'C' or 'S' ? Reflect(lastCubicControl, current) : current;
                        lastCubicControl = Offset(c2);
                        builder.CubicTo(c1, lastCubicControl, Offset(end));
                        ok = true;
                    }

                    break;
                }
                case 'Q':
                {
                    ok = false;
                    if (reader.TryReadPoint(out PointF control) && reader.TryReadPoint(out PointF end))
                    {
                        lastQuadraticControl = Offset(control);
                        builder.QuadraticTo(lastQuadraticControl, Offset(end));
                        ok = true;
                    }

                    break;
                }
                case 'T':
                {
                    ok = false;
                    if (reader.TryReadPoint(out PointF end))
                    {
                        lastQuadraticControl = previousCommand is 'Q' or 'T' ? Reflect(lastQuadraticControl, current) : current;
                        builder.QuadraticTo(lastQuadraticControl, Offset(end));
                        ok = true;
                    }

                    break;
                }
                case 'A':
                {
                    ok = false;
                    if (reader.TryReadNumber(out float rx) &&
                        reader.TryReadNumber(out float ry) &&
                        reader.TryReadNumber(out float rotation) &&
                        reader.TryReadFlag(out bool largeArc) &&
                        reader.TryReadFlag(out bool sweep) &&
                        reader.TryReadPoint(out PointF end))
                    {
                        builder.ArcTo(rx, ry, rotation, largeArc, sweep, Offset(end));
                        ok = true;
                    }

                    break;
                }
                case 'Z':
                    builder.Close();
                    ok = true;
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
            {
                break;
            }

            previousCommand = kind;
            if (builder.SegmentCount > maximumSegments)
            {
                throw new FormatException("The SVG is too complex to draw.");
            }
        }

        return builder.Figures;
    }

    private static PointF Reflect(PointF control, PointF around) => new((2 * around.X) - control.X, (2 * around.Y) - control.Y);
}

/// <summary>Draws a <see cref="PhantomSvgDocument"/> with GDI+.</summary>
internal static class PhantomSvgRenderer
{
    /// <param name="currentColor">The colour used for currentColor (normally the text colour of the control).</param>
    public static Bitmap Render(PhantomSvgDocument document, Size size, Color currentColor)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            Draw(graphics, document, new RectangleF(0, 0, bitmap.Width, bitmap.Height), currentColor);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Draws the document fitted into <paramref name="bounds"/> (centred, aspect ratio kept, like xMidYMid meet).</summary>
    public static void Draw(Graphics graphics, PhantomSvgDocument document, RectangleF bounds, Color currentColor)
    {
        RectangleF viewBox = document.ViewBox;
        if (bounds.Width <= 0 || bounds.Height <= 0 || viewBox.Width <= 0 || viewBox.Height <= 0)
        {
            return;
        }

        float scale = Math.Min(bounds.Width / viewBox.Width, bounds.Height / viewBox.Height);
        float offsetX = bounds.X + ((bounds.Width - (viewBox.Width * scale)) / 2f) - (viewBox.X * scale);
        float offsetY = bounds.Y + ((bounds.Height - (viewBox.Height * scale)) / 2f) - (viewBox.Y * scale);

        GraphicsState state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            using Matrix baseTransform = graphics.Transform;

            foreach (PhantomSvgShape shape in document.Shapes)
            {
                using GraphicsPath path = BuildPath(shape);
                using Matrix transform = baseTransform.Clone();
                using var viewTransform = new Matrix(scale, 0, 0, scale, offsetX, offsetY);
                PhantomSvgMatrix m = shape.Transform;
                using var shapeTransform = new Matrix(m.A, m.B, m.C, m.D, m.E, m.F);
                transform.Multiply(viewTransform, MatrixOrder.Prepend);
                transform.Multiply(shapeTransform, MatrixOrder.Prepend);
                graphics.Transform = transform;

                if (shape.Fill.Kind != PhantomSvgPaintKind.None)
                {
                    using var brush = new SolidBrush(ResolveColor(shape.Fill, currentColor, shape.FillOpacity));
                    graphics.FillPath(brush, path);
                }

                if (shape.Stroke.Kind != PhantomSvgPaintKind.None && shape.StrokeWidth > 0)
                {
                    using var pen = new Pen(ResolveColor(shape.Stroke, currentColor, shape.StrokeOpacity), shape.StrokeWidth)
                    {
                        MiterLimit = shape.MiterLimit,
                        LineJoin = shape.LineJoin switch
                        {
                            PhantomSvgLineJoin.Round => LineJoin.Round,
                            PhantomSvgLineJoin.Bevel => LineJoin.Bevel,
                            _ => LineJoin.MiterClipped,
                        },
                    };
                    LineCap cap = shape.LineCap switch
                    {
                        PhantomSvgLineCap.Round => LineCap.Round,
                        PhantomSvgLineCap.Square => LineCap.Square,
                        _ => LineCap.Flat,
                    };
                    pen.SetLineCap(cap, cap, cap == LineCap.Round ? DashCap.Round : DashCap.Flat);
                    graphics.DrawPath(pen, path);
                }
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static GraphicsPath BuildPath(PhantomSvgShape shape)
    {
        var path = new GraphicsPath(shape.EvenOdd ? FillMode.Alternate : FillMode.Winding);
        foreach (PhantomSvgFigure figure in shape.Figures)
        {
            path.StartFigure();
            PointF current = figure.Start;
            foreach (PhantomSvgSegment segment in figure.Segments)
            {
                if (segment.Kind == PhantomSvgSegmentKind.Line)
                {
                    path.AddLine(current, segment.End);
                }
                else
                {
                    path.AddBezier(current, segment.Control1, segment.Control2, segment.End);
                }

                current = segment.End;
            }

            if (figure.Closed)
            {
                path.CloseFigure();
            }
        }

        return path;
    }

    private static Color ResolveColor(PhantomSvgPaint paint, Color currentColor, float opacity)
    {
        Color color = paint.Kind == PhantomSvgPaintKind.CurrentColor ? currentColor : paint.Color;
        int alpha = (int)Math.Round(color.A * Math.Clamp(opacity, 0f, 1f));
        return Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);
    }
}

/// <summary>The outlines of ui.shape, as paths that fit a rectangle.</summary>
internal static class PhantomShapeGeometry
{
    public static GraphicsPath CreatePath(PhantomUiShape shape, RectangleF bounds)
    {
        var path = new GraphicsPath();
        float left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom;
        float width = bounds.Width, height = bounds.Height;
        float centerX = left + (width / 2f), centerY = top + (height / 2f);
        if (width <= 0 || height <= 0)
        {
            return path;
        }

        switch (shape)
        {
            case PhantomUiShape.Rounded:
                AddRoundedRectangle(path, bounds, Math.Min(width, height) * 0.3f);
                break;
            case PhantomUiShape.Pill:
                AddRoundedRectangle(path, bounds, Math.Min(width, height) / 2f);
                break;
            case PhantomUiShape.Circle:
            {
                float diameter = Math.Min(width, height);
                path.AddEllipse(centerX - (diameter / 2f), centerY - (diameter / 2f), diameter, diameter);
                break;
            }
            case PhantomUiShape.Ellipse:
                path.AddEllipse(bounds);
                break;
            case PhantomUiShape.Diamond:
                path.AddPolygon(new[] { new PointF(centerX, top), new PointF(right, centerY), new PointF(centerX, bottom), new PointF(left, centerY) });
                break;
            case PhantomUiShape.Triangle:
                path.AddPolygon(new[] { new PointF(centerX, top), new PointF(right, bottom), new PointF(left, bottom) });
                break;
            case PhantomUiShape.Hexagon:
            {
                float inset = Math.Min(width / 4f, height / 2f);
                path.AddPolygon(new[]
                {
                    new PointF(left + inset, top), new PointF(right - inset, top), new PointF(right, centerY),
                    new PointF(right - inset, bottom), new PointF(left + inset, bottom), new PointF(left, centerY),
                });
                break;
            }
            case PhantomUiShape.Star:
            {
                var points = new PointF[10];
                for (int i = 0; i < points.Length; i++)
                {
                    double angle = (-Math.PI / 2) + (i * Math.PI / 5);
                    float radius = i % 2 == 0 ? 1f : 0.45f;
                    points[i] = new PointF(
                        centerX + (float)(Math.Cos(angle) * radius * width / 2),
                        centerY + (float)(Math.Sin(angle) * radius * height / 2) + (height * 0.05f));
                }

                path.AddPolygon(points);
                break;
            }
            default:
                path.AddRectangle(bounds);
                break;
        }

        return path;
    }

    private static void AddRoundedRectangle(GraphicsPath path, RectangleF bounds, float radius)
    {
        radius = Math.Max(0.5f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
        float diameter = radius * 2f;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
    }
}
