using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomHaze;

internal enum PhantomExtensionSettingType
{
    Toggle,
    Text,
    Number,
    Select,
}

internal enum PhantomExtensionScriptTiming
{
    DocumentStart,
    DocumentEnd,
}

internal enum PhantomExtensionSearchEngine
{
    Google,
    Bing,
    Yahoo,
    DuckDuckGo,
    Startpage,
}

internal sealed class PhantomExtensionSettingDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public PhantomExtensionSettingType Type { get; init; }
    public string DefaultValue { get; init; } = string.Empty;
    public int? Minimum { get; init; }
    public int? Maximum { get; init; }
    public IReadOnlyList<string> SelectOptions { get; init; } = Array.Empty<string>();
}

internal sealed class PhantomExtensionAppButtonDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

internal sealed class PhantomExtensionWebScriptDefinition
{
    public string Id { get; init; } = string.Empty;
    public string MatchPattern { get; init; } = "*";
    public PhantomExtensionScriptTiming Timing { get; init; }
    public bool IsCss { get; init; }
    public string Content { get; init; } = string.Empty;
}

internal sealed class PhantomExtensionWebBlockDefinition
{
    public string Id { get; init; } = string.Empty;
    public string MatchPattern { get; init; } = "*";
}

internal sealed class PhantomExtensionUiDefinition
{
    public string Title { get; set; } = string.Empty;
    public int Width { get; set; } = 760;
    public int Height { get; set; } = 520;
    public string Html { get; set; } = string.Empty;

    public bool HasUi => !string.IsNullOrWhiteSpace(Html);
}

/// <summary>What a menu.button, a menu.option or an app.button does when it is clicked.</summary>
internal enum PhantomExtensionActionKind
{
    /// <summary>Shows a message box (what app.button has always done).</summary>
    Message,

    /// <summary>Opens an http(s) address in a new protected tab.</summary>
    Url,

    /// <summary>Shows one of the extension's own pages (app.page).</summary>
    Page,

    /// <summary>Opens the extension's ui.html window.</summary>
    Window,

    /// <summary>Switches to a built-in view: "logs" or "web".</summary>
    View,

    /// <summary>Writes a line to the log panel.</summary>
    Log,
}

internal sealed class PhantomExtensionActionDefinition
{
    public PhantomExtensionActionKind Kind { get; init; }

    /// <summary>The message, address, page id or view name the action uses ("" for window).</summary>
    public string Value { get; init; } = string.Empty;
}

internal enum PhantomExtensionMenuItemKind
{
    /// <summary>A top-bar button that runs one action (app.button and menu.button).</summary>
    Button,

    /// <summary>A top-bar button that opens a list of options (menu.dropdown with its menu.option lines).</summary>
    Dropdown,
}

internal sealed class PhantomExtensionMenuOptionDefinition
{
    public string Label { get; init; } = string.Empty;
    public PhantomExtensionActionDefinition Action { get; init; } = new();
}

/// <summary>A control an extension adds to the top menu bar, kept in the order the file declares it.</summary>
internal sealed class PhantomExtensionMenuItemDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public PhantomExtensionMenuItemKind Kind { get; init; }

    /// <summary>The action of a Button; null for a Dropdown.</summary>
    public PhantomExtensionActionDefinition? Action { get; init; }

    /// <summary>The options of a Dropdown, in declaration order; empty for a Button.</summary>
    public List<PhantomExtensionMenuOptionDefinition> Options { get; } = new();
}

/// <summary>
/// A page of the extension's own (app.page). It gets a button next to Logs and Web View and is shown in the main
/// content area, like those built-in views.
/// </summary>
internal sealed class PhantomExtensionPageDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
}

/// <summary>A property of a built-in or extension-owned control that ui.element can change.</summary>
internal enum PhantomUiProperty
{
    Text,
    Tooltip,
    Visible,
    Background,
    Foreground,
    Border,
    Font,
    FontSize,
    FontStyle,
}

internal enum PhantomUiFontStyle
{
    Regular,
    Bold,
    Italic,
    BoldItalic,
}

/// <summary>The outlines ui.shape can give a control.</summary>
internal enum PhantomUiShape
{
    Rectangle,
    Rounded,
    Pill,
    Circle,
    Ellipse,
    Diamond,
    Triangle,
    Hexagon,
    Star,
}

/// <summary>One ui.element line: a single property change on one control.</summary>
internal sealed class PhantomExtensionUiEditDefinition
{
    public string Target { get; init; } = string.Empty;
    public PhantomUiProperty Property { get; init; }

    /// <summary>The text of a text/tooltip edit or the family of a font edit.</summary>
    public string Text { get; init; } = string.Empty;

    public bool Flag { get; init; }
    public Color Color { get; init; }
    public float Number { get; init; }
    public PhantomUiFontStyle FontStyle { get; init; }
    public int LineNumber { get; init; }
}

/// <summary>One ui.svg block: an SVG graphic that replaces how a control looks.</summary>
internal sealed class PhantomExtensionUiSvgDefinition
{
    public string Target { get; init; } = string.Empty;
    public PhantomSvgDocument Document { get; init; } = null!;
    public int LineNumber { get; init; }
}

/// <summary>One ui.shape line: an outline and fill colour that replace a control's rectangular look.</summary>
internal sealed class PhantomExtensionUiShapeDefinition
{
    public string Target { get; init; } = string.Empty;
    public PhantomUiShape Shape { get; init; }
    public Color Fill { get; init; }
    public Color? Outline { get; init; }
    public int LineNumber { get; init; }
}

[Flags]
internal enum PhantomUiCapabilities
{
    None = 0,
    Text = 1 << 0,
    Tooltip = 1 << 1,
    Visible = 1 << 2,
    Background = 1 << 3,
    Foreground = 1 << 4,
    Border = 1 << 5,
    Font = 1 << 6,
    Svg = 1 << 7,
    Shape = 1 << 8,
}

/// <param name="IdReserved">
/// True for the element names that have existed since ui.* was introduced (v1.3.5): an extension's own menu.button,
/// menu.dropdown or app.page may not use them as its id. Names added later are not reserved, so an extension written
/// before they existed keeps loading when one of its own controls happens to have that id; ui.* lines in that file
/// then keep meaning its own control, exactly as they did before.
/// </param>
internal sealed record PhantomUiTargetInfo(string Name, string Description, PhantomUiCapabilities Capabilities, bool IdReserved = true);

/// <summary>
/// The built-in controls extensions may customise with ui.element, ui.svg and ui.shape, and what each one allows.
/// Together they cover the whole PhantomHaze window, with deliberate exceptions. The protection status and the
/// protection button are not listed: an extension must never be able to make PhantomHaze look protected when it is
/// not. The update banner is not listed either, so an extension cannot hide that a newer version is available. The
/// Settings button can be restyled but never hidden, so an extension can never lock the user out of the place where
/// it is disabled or uninstalled, and Clear History cannot be hidden because it is a privacy control.
/// </summary>
internal static class PhantomUiTargets
{
    private const PhantomUiCapabilities ButtonLike =
        PhantomUiCapabilities.Text | PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Visible |
        PhantomUiCapabilities.Background | PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Border |
        PhantomUiCapabilities.Font | PhantomUiCapabilities.Svg | PhantomUiCapabilities.Shape;

    private const PhantomUiCapabilities LabelLike =
        PhantomUiCapabilities.Text | PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Font;

    private const PhantomUiCapabilities ListLike =
        PhantomUiCapabilities.Background | PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Font;

    /// <summary>What an extension may change on its own top-bar buttons, dropdowns and page buttons.</summary>
    public const PhantomUiCapabilities OwnElementCapabilities = ButtonLike & ~PhantomUiCapabilities.Visible;

    public static IReadOnlyList<PhantomUiTargetInfo> BuiltIn { get; } =
    [
        // ---- The top bar ----
        new("menu.bar", "The top bar: the row with Log, Web and Settings and the frame around the address row", PhantomUiCapabilities.Background),
        new("menu.logo", "The PhantomHaze logo (only shown when an extension shows or customises it)",
            PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Visible | PhantomUiCapabilities.Background |
            PhantomUiCapabilities.Svg | PhantomUiCapabilities.Shape),
        new("menu.title", "The PhantomHaze name next to the logo (only shown when an extension shows or customises it)",
            PhantomUiCapabilities.Text | PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Visible |
            PhantomUiCapabilities.Background | PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Font |
            PhantomUiCapabilities.Svg | PhantomUiCapabilities.Shape),
        new("menu.logs", "The Log button", ButtonLike),
        new("menu.webview", "The Web button", ButtonLike),
        new("menu.clock", "The clock",
            PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Visible | PhantomUiCapabilities.Background |
            PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Font | PhantomUiCapabilities.Shape),
        new("menu.settings", "The Settings button (it can be restyled but never hidden)", ButtonLike & ~PhantomUiCapabilities.Visible),

        // ---- The address row ----
        new("address.panel", "The row that holds the address box and its buttons", PhantomUiCapabilities.Background, IdReserved: false),
        new("address.bar", "The address box (border: its outline)",
            PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Background | PhantomUiCapabilities.Foreground |
            PhantomUiCapabilities.Border | PhantomUiCapabilities.Font),
        new("address.favorite", "The favourite (star) button",
            PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Visible | PhantomUiCapabilities.Border |
            PhantomUiCapabilities.Font | PhantomUiCapabilities.Svg | PhantomUiCapabilities.Shape),
        new("address.back", "The Back button", ButtonLike),
        new("address.forward", "The Forward button", ButtonLike, IdReserved: false),
        new("address.go", "The Go button (only shown when an extension shows or customises it)", ButtonLike),
        new("address.refresh", "The Refresh button", ButtonLike),
        new("address.history", "The Favorites button, which opens the history and favourites panel", ButtonLike),
        new("address.zoom", "The zoom percentage (background and border: the zoom control around it)",
            PhantomUiCapabilities.Tooltip | PhantomUiCapabilities.Visible | PhantomUiCapabilities.Background |
            PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Border | PhantomUiCapabilities.Font),
        new("address.zoomin", "The zoom in (+) button", ButtonLike, IdReserved: false),
        new("address.zoomout", "The zoom out (-) button", ButtonLike, IdReserved: false),

        // ---- The tab strip ----
        new("tabs.bar", "The tab strip", PhantomUiCapabilities.Background, IdReserved: false),
        new("tabs.new", "The new tab (+) button", ButtonLike),
        new("tabs.tab", "Every tab (font: the tab titles)", ListLike, IdReserved: false),
        new("tabs.active", "The selected tab, on top of tabs.tab",
            PhantomUiCapabilities.Background | PhantomUiCapabilities.Foreground, IdReserved: false),
        new("tabs.close", "The close button of every tab",
            PhantomUiCapabilities.Text | PhantomUiCapabilities.Background | PhantomUiCapabilities.Foreground |
            PhantomUiCapabilities.Border, IdReserved: false),

        // ---- The content area ----
        new("log.panel", "The log panel", ListLike),
        new("history.panel", "The history and favourites panel", PhantomUiCapabilities.Background),
        new("history.title", "The heading above the history list", LabelLike, IdReserved: false),
        new("history.list", "The history list", ListLike, IdReserved: false),
        new("history.clear", "The Clear History button (it can be restyled but never hidden)",
            ButtonLike & ~PhantomUiCapabilities.Visible, IdReserved: false),
        new("favorites.title", "The heading above the favourites list", LabelLike, IdReserved: false),
        new("favorites.list", "The favourites list", ListLike, IdReserved: false),
        new("find.panel", "The find-in-page panel", PhantomUiCapabilities.Background, IdReserved: false),
        new("find.box", "The find-in-page text box", PhantomUiCapabilities.Tooltip | ListLike, IdReserved: false),
        new("find.count", "The find-in-page match count",
            PhantomUiCapabilities.Foreground | PhantomUiCapabilities.Font, IdReserved: false),
        new("find.close", "The find-in-page close button", ButtonLike & ~PhantomUiCapabilities.Visible, IdReserved: false),
        new("loading.panel", "The panel shown while Web View is loading", PhantomUiCapabilities.Background, IdReserved: false),
        new("loading.label", "The text shown while Web View is loading", LabelLike, IdReserved: false),
    ];

    private static readonly Dictionary<string, PhantomUiTargetInfo> BuiltInByName =
        BuiltIn.ToDictionary(target => target.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every built-in element name, for error messages (the language has no separate reference file).</summary>
    public static string BuiltInNameList { get; } = string.Join(", ", BuiltIn.Select(target => target.Name));

    public static bool TryGetBuiltIn(string? name, out PhantomUiTargetInfo info)
    {
        info = null!;
        return name is not null && BuiltInByName.TryGetValue(name.Trim(), out info!);
    }

    /// <summary>
    /// Whether a ui.* target means the built-in element of that name rather than one of the extension's own controls.
    /// Reserved names always mean the built-in element; a newer name means the extension's own control when the
    /// extension declares one with that id (see <see cref="PhantomUiTargetInfo"/>).
    /// </summary>
    public static bool ResolvesToBuiltIn(string? name, bool extensionDeclaresOwnControl, out PhantomUiTargetInfo info)
    {
        return TryGetBuiltIn(name, out info) && (info.IdReserved || !extensionDeclaresOwnControl);
    }

    public static PhantomUiCapabilities GetRequiredCapability(PhantomUiProperty property) => property switch
    {
        PhantomUiProperty.Text => PhantomUiCapabilities.Text,
        PhantomUiProperty.Tooltip => PhantomUiCapabilities.Tooltip,
        PhantomUiProperty.Visible => PhantomUiCapabilities.Visible,
        PhantomUiProperty.Background => PhantomUiCapabilities.Background,
        PhantomUiProperty.Foreground => PhantomUiCapabilities.Foreground,
        PhantomUiProperty.Border => PhantomUiCapabilities.Border,
        _ => PhantomUiCapabilities.Font,
    };

    public static string GetPropertyName(PhantomUiProperty property) => property switch
    {
        PhantomUiProperty.FontSize => "fontsize",
        PhantomUiProperty.FontStyle => "fontstyle",
        _ => property.ToString().ToLowerInvariant(),
    };
}

/// <summary>Colour values used by .phext files and the theme settings: #RGB, #RRGGBB, rgb(r, g, b) or a CSS colour name.</summary>
internal static class PhantomColorParser
{
    public static bool TryParse(string? text, out Color color)
    {
        color = Color.Empty;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] == '#')
        {
            return TryParseHex(value.AsSpan(1), out color);
        }

        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgbFunction(value, out color);
        }

        // Only CSS colour names; Windows "system" colour names (Control, Window, ...) would follow the OS theme.
        Color named = Color.FromName(value);
        if (!named.IsKnownColor || named.IsSystemColor)
        {
            return false;
        }

        color = Color.FromArgb(named.A, named.R, named.G, named.B);
        return true;
    }

    /// <summary>Parses a colour for a window control: it must be fully opaque, because WinForms controls reject see-through colours.</summary>
    public static bool TryParseOpaque(string? text, out Color color)
    {
        if (!TryParse(text, out color) || color.A == 0)
        {
            color = Color.Empty;
            return false;
        }

        color = Color.FromArgb(255, color.R, color.G, color.B);
        return true;
    }

    public static string ToHex(Color color) => string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    private static bool TryParseHex(ReadOnlySpan<char> hex, out Color color)
    {
        color = Color.Empty;
        foreach (char character in hex)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        static int Nibble(char character) => Convert.ToInt32(character.ToString(), 16);
        static int Byte(ReadOnlySpan<char> pair) => int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        switch (hex.Length)
        {
            case 3:
            case 4:
            {
                int r = Nibble(hex[0]) * 17;
                int g = Nibble(hex[1]) * 17;
                int b = Nibble(hex[2]) * 17;
                int a = hex.Length == 4 ? Nibble(hex[3]) * 17 : 255;
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
            case 6:
            case 8:
            {
                int r = Byte(hex[..2]);
                int g = Byte(hex[2..4]);
                int b = Byte(hex[4..6]);
                int a = hex.Length == 8 ? Byte(hex[6..8]) : 255;
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryParseRgbFunction(string value, out Color color)
    {
        color = Color.Empty;
        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        string[] parts = value[(open + 1)..close]
            .Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4))
        {
            return false;
        }

        var channels = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!TryParseChannel(parts[i], 255, out channels[i]))
            {
                return false;
            }
        }

        int alpha = 255;
        if (parts.Length == 4 && !TryParseChannel(parts[3], 1, out alpha))
        {
            return false;
        }

        color = Color.FromArgb(alpha, channels[0], channels[1], channels[2]);
        return true;
    }

    /// <param name="scale">255 for colour channels; 1 for alpha, which is written as 0..1 (or a percentage).</param>
    private static bool TryParseChannel(string text, int scale, out int channel)
    {
        channel = 0;
        bool percent = text.EndsWith('%');
        string number = percent ? text[..^1] : text;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || double.IsNaN(parsed))
        {
            return false;
        }

        double fraction = percent ? parsed / 100.0 : parsed / scale;
        channel = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 255);
        return true;
    }
}

internal sealed class PhantomExtensionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string IconToken { get; set; } = "shield";
    public string StartPageOverride { get; set; } = string.Empty;
    public PhantomExtensionSearchEngine? SearchEngineOverride { get; set; }
    public PhantomExtensionUiDefinition Ui { get; } = new();
    public List<string> StartupLogMessages { get; } = new();
    public List<PhantomExtensionSettingDefinition> Settings { get; } = new();
    public List<PhantomExtensionAppButtonDefinition> AppButtons { get; } = new();
    public List<PhantomExtensionWebScriptDefinition> WebScripts { get; } = new();
    public List<PhantomExtensionWebBlockDefinition> WebBlocks { get; } = new();

    /// <summary>Top-bar buttons and dropdowns (app.button, menu.button, menu.dropdown) in declaration order.</summary>
    public List<PhantomExtensionMenuItemDefinition> MenuItems { get; } = new();

    public List<PhantomExtensionPageDefinition> Pages { get; } = new();
    public List<PhantomExtensionUiEditDefinition> UiEdits { get; } = new();
    public List<PhantomExtensionUiSvgDefinition> UiSvgs { get; } = new();
    public List<PhantomExtensionUiShapeDefinition> UiShapes { get; } = new();

    public bool HasUiCustomizations => UiEdits.Count > 0 || UiSvgs.Count > 0 || UiShapes.Count > 0;

    public PhantomExtensionPageDefinition? FindPage(string? pageId)
    {
        return Pages.FirstOrDefault(page => page.Id.Equals(pageId ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class PhextParseException : Exception
{
    public PhextParseException(int lineNumber, string message)
        : base($"Line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}

internal static class PhextParser
{
    // \z (not $) so a value ending in an escaped newline such as "abc\n" is rejected; $ also matches before a final '\n'.
    private static readonly Regex ValidIdentifierRegex = new("^[a-zA-Z0-9_.-]+\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PhantomExtensionDefinition Parse(string source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);

        string normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalizedSource.Split('\n');
        var definition = new PhantomExtensionDefinition();

        var seenSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // app.button, menu.button, menu.dropdown and app.page ids share one namespace: ui.element, ui.svg and
        // ui.shape refer to an extension's own controls by these ids.
        var seenElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropdownsById = new Dictionary<string, PhantomExtensionMenuItemDefinition>(StringComparer.OrdinalIgnoreCase);
        // Checked once the whole file is read, because pages and ui.html may be declared after the lines using them.
        var pendingActionChecks = new List<(PhantomExtensionActionDefinition Action, int LineNumber)>();
        var pendingTargetChecks = new List<(string Target, PhantomUiCapabilities Required, string What, int LineNumber)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith('#') ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int lineNumber = i + 1;
            List<string> args = Tokenize(trimmed, lineNumber);
            if (args.Count == 0)
            {
                continue;
            }

            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "meta.name":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Name = args[1].Trim();
                    break;
                case "meta.id":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Id = ValidateIdentifier(args[1], lineNumber, "extension id");
                    break;
                case "meta.version":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Version = args[1].Trim();
                    break;
                case "meta.description":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Description = args[1].Trim();
                    break;
                case "meta.icon":
                    RequireArgCount(args, 2, lineNumber);
                    definition.IconToken = args[1].Trim();
                    break;
                case "setting.toggle":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    bool defaultValue = ParseBoolean(args[3], lineNumber);
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Toggle,
                        DefaultValue = defaultValue ? "true" : "false",
                    });
                    break;
                }
                case "setting.text":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Text,
                        DefaultValue = args[3],
                    });
                    break;
                }
                case "setting.number":
                {
                    RequireArgCount(args, 6, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    int defaultValue = ParseInt(args[3], lineNumber, "default number");
                    int minimum = ParseInt(args[4], lineNumber, "minimum number");
                    int maximum = ParseInt(args[5], lineNumber, "maximum number");
                    if (minimum > maximum)
                    {
                        throw new PhextParseException(lineNumber, "setting.number minimum cannot be greater than maximum.");
                    }

                    if (defaultValue < minimum || defaultValue > maximum)
                    {
                        throw new PhextParseException(lineNumber, "setting.number default must be within the provided minimum/maximum range.");
                    }

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Number,
                        DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
                        Minimum = minimum,
                        Maximum = maximum,
                    });
                    break;
                }
                case "setting.select":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    string defaultValue = args[3].Trim();
                    // Options are matched case-insensitively everywhere, so drop case-insensitive duplicates.
                    string[] options = args[4]
                        .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (options.Length == 0)
                    {
                        throw new PhextParseException(lineNumber, "setting.select requires at least one option in the pipe-delimited options list.");
                    }

                    string? canonicalDefault = options.FirstOrDefault(option =>
                        option.Equals(defaultValue, StringComparison.OrdinalIgnoreCase));
                    if (canonicalDefault is null)
                    {
                        throw new PhextParseException(lineNumber, "setting.select default value must be one of the provided options.");
                    }

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Select,
                        // Store the option's own spelling so the default compares equal to the value scripts receive.
                        DefaultValue = canonicalDefault,
                        SelectOptions = options,
                    });
                    break;
                }
                case "app.button":
                {
                    RequireArgCount(args, 4, lineNumber);
                    // Unlike the newer commands, app.button ids may match a built-in element name (files written
                    // before ui.* existed keep working); ui.* lines then mean the built-in element when its name is
                    // one of the original, reserved ones (see PhantomUiTargets.ResolvesToBuiltIn).
                    string id = ValidateIdentifier(args[1], lineNumber, "app button id");
                    EnsureUnique(seenElements, id, lineNumber, "element id");

                    definition.AppButtons.Add(new PhantomExtensionAppButtonDefinition
                    {
                        Id = id,
                        Label = args[2].Trim(),
                        Message = args[3],
                    });

                    // An app.button is a menu.button whose action is "message".
                    definition.MenuItems.Add(new PhantomExtensionMenuItemDefinition
                    {
                        Id = id,
                        Label = args[2].Trim(),
                        Kind = PhantomExtensionMenuItemKind.Button,
                        Action = new PhantomExtensionActionDefinition { Kind = PhantomExtensionActionKind.Message, Value = args[3] },
                    });
                    break;
                }
                case "menu.button":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string id = ValidateElementId(args[1], lineNumber, "menu button id");
                    EnsureUnique(seenElements, id, lineNumber, "element id");
                    PhantomExtensionActionDefinition action = ParseAction(args[3], args[4], lineNumber);
                    pendingActionChecks.Add((action, lineNumber));

                    definition.MenuItems.Add(new PhantomExtensionMenuItemDefinition
                    {
                        Id = id,
                        Label = args[2].Trim(),
                        Kind = PhantomExtensionMenuItemKind.Button,
                        Action = action,
                    });
                    break;
                }
                case "menu.dropdown":
                {
                    RequireArgCount(args, 3, lineNumber);
                    string id = ValidateElementId(args[1], lineNumber, "menu dropdown id");
                    EnsureUnique(seenElements, id, lineNumber, "element id");

                    var dropdown = new PhantomExtensionMenuItemDefinition
                    {
                        Id = id,
                        Label = args[2].Trim(),
                        Kind = PhantomExtensionMenuItemKind.Dropdown,
                    };
                    definition.MenuItems.Add(dropdown);
                    dropdownsById[id] = dropdown;
                    break;
                }
                case "menu.option":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string dropdownId = ValidateIdentifier(args[1], lineNumber, "menu dropdown id");
                    if (!dropdownsById.TryGetValue(dropdownId, out PhantomExtensionMenuItemDefinition? dropdown))
                    {
                        throw new PhextParseException(
                            lineNumber,
                            $"menu.option refers to the dropdown '{dropdownId}', which is not declared above it. Declare it first with menu.dropdown.");
                    }

                    if (dropdown.Options.Count >= MaximumDropdownOptions)
                    {
                        throw new PhextParseException(lineNumber, $"A dropdown can have at most {MaximumDropdownOptions} options.");
                    }

                    PhantomExtensionActionDefinition action = ParseAction(args[3], args[4], lineNumber);
                    pendingActionChecks.Add((action, lineNumber));
                    dropdown.Options.Add(new PhantomExtensionMenuOptionDefinition
                    {
                        Label = args[2].Trim(),
                        Action = action,
                    });
                    break;
                }
                case "app.page":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string id = ValidateElementId(args[1], lineNumber, "page id");
                    EnsureUnique(seenElements, id, lineNumber, "element id");

                    string terminator = ParseHereDocToken(args[3], lineNumber);
                    string html = ReadBlock(lines, ref i, lineNumber, terminator);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        throw new PhextParseException(lineNumber, "app.page content block cannot be empty.");
                    }

                    string label = args[2].Trim();
                    definition.Pages.Add(new PhantomExtensionPageDefinition
                    {
                        Id = id,
                        Label = string.IsNullOrWhiteSpace(label) ? id : label,
                        Html = html,
                    });
                    break;
                }
                case "ui.element":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string target = ParseUiTargetName(args[1], lineNumber);
                    PhantomUiProperty property = ParseUiProperty(args[2], lineNumber);
                    pendingTargetChecks.Add((
                        target,
                        PhantomUiTargets.GetRequiredCapability(property),
                        $"the '{PhantomUiTargets.GetPropertyName(property)}' property",
                        lineNumber));
                    definition.UiEdits.Add(BuildUiEdit(target, property, args[3], lineNumber));
                    break;
                }
                case "ui.svg":
                {
                    RequireArgCount(args, 3, lineNumber);
                    string target = ParseUiTargetName(args[1], lineNumber);
                    pendingTargetChecks.Add((target, PhantomUiCapabilities.Svg, "ui.svg", lineNumber));

                    string svgSource;
                    if (args[2].StartsWith("<<", StringComparison.Ordinal))
                    {
                        string terminator = ParseHereDocToken(args[2], lineNumber);
                        svgSource = ReadBlock(lines, ref i, lineNumber, terminator);
                    }
                    else if (!PhantomSvgDocument.TryDecodeDataUri(args[2], out svgSource))
                    {
                        throw new PhextParseException(
                            lineNumber,
                            "ui.svg expects a <<TAG block or a data:image/svg+xml URI.");
                    }

                    PhantomSvgDocument document;
                    try
                    {
                        document = PhantomSvgDocument.Parse(svgSource);
                    }
                    catch (FormatException ex)
                    {
                        throw new PhextParseException(lineNumber, $"ui.svg: {ex.Message}");
                    }

                    definition.UiSvgs.Add(new PhantomExtensionUiSvgDefinition
                    {
                        Target = target,
                        Document = document,
                        LineNumber = lineNumber,
                    });
                    break;
                }
                case "ui.shape":
                {
                    if (args.Count is not (4 or 5))
                    {
                        throw new PhextParseException(
                            lineNumber,
                            $"'{args[0]}' expects 3 or 4 argument(s) but received {args.Count - 1}.");
                    }

                    string target = ParseUiTargetName(args[1], lineNumber);
                    pendingTargetChecks.Add((target, PhantomUiCapabilities.Shape, "ui.shape", lineNumber));
                    definition.UiShapes.Add(new PhantomExtensionUiShapeDefinition
                    {
                        Target = target,
                        Shape = ParseShape(args[2], lineNumber),
                        Fill = ParseOpaqueColor(args[3], lineNumber, "fill colour"),
                        Outline = args.Count == 5 ? ParseOpaqueColor(args[4], lineNumber, "outline colour") : null,
                        LineNumber = lineNumber,
                    });
                    break;
                }
                case "app.startpage":
                    RequireArgCount(args, 2, lineNumber);
                    definition.StartPageOverride = args[1].Trim();
                    break;
                case "app.searchengine":
                    RequireArgCount(args, 2, lineNumber);
                    definition.SearchEngineOverride = ParseSearchEngine(args[1], lineNumber);
                    break;
                case "app.log":
                    RequireArgCount(args, 2, lineNumber);
                    definition.StartupLogMessages.Add(args[1]);
                    break;
                case "web.script":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "web script id");
                    EnsureUnique(seenScripts, id, lineNumber, "web script id");

                    string terminator = ParseHereDocToken(args[4], lineNumber);
                    string script = ReadBlock(lines, ref i, lineNumber, terminator);
                    if (string.IsNullOrWhiteSpace(script))
                    {
                        throw new PhextParseException(lineNumber, "web.script content block cannot be empty.");
                    }

                    definition.WebScripts.Add(new PhantomExtensionWebScriptDefinition
                    {
                        Id = id,
                        MatchPattern = ParseUrlPattern(args[2], lineNumber),
                        Timing = ParseScriptTiming(args[3], lineNumber),
                        IsCss = false,
                        Content = script,
                    });
                    break;
                }
                case "web.css":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "web css id");
                    EnsureUnique(seenScripts, id, lineNumber, "web css id");

                    string terminator = ParseHereDocToken(args[4], lineNumber);
                    string css = ReadBlock(lines, ref i, lineNumber, terminator);
                    if (string.IsNullOrWhiteSpace(css))
                    {
                        throw new PhextParseException(lineNumber, "web.css content block cannot be empty.");
                    }

                    definition.WebScripts.Add(new PhantomExtensionWebScriptDefinition
                    {
                        Id = id,
                        MatchPattern = ParseUrlPattern(args[2], lineNumber),
                        Timing = ParseScriptTiming(args[3], lineNumber),
                        IsCss = true,
                        Content = css,
                    });
                    break;
                }
                case "web.block":
                {
                    RequireArgCount(args, 3, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "web block id");
                    EnsureUnique(seenBlocks, id, lineNumber, "web block id");
                    definition.WebBlocks.Add(new PhantomExtensionWebBlockDefinition
                    {
                        Id = id,
                        MatchPattern = ParseUrlPattern(args[2], lineNumber),
                    });
                    break;
                }
                case "ui.title":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Ui.Title = args[1].Trim();
                    break;
                case "ui.size":
                {
                    RequireArgCount(args, 3, lineNumber);
                    int width = ParseInt(args[1], lineNumber, "UI width");
                    int height = ParseInt(args[2], lineNumber, "UI height");
                    definition.Ui.Width = Math.Clamp(width, 320, 2000);
                    definition.Ui.Height = Math.Clamp(height, 240, 1400);
                    break;
                }
                case "ui.html":
                {
                    RequireArgCount(args, 2, lineNumber);
                    string terminator = ParseHereDocToken(args[1], lineNumber);
                    definition.Ui.Html = ReadBlock(lines, ref i, lineNumber, terminator);
                    break;
                }
                default:
                    throw new PhextParseException(lineNumber, $"Unknown command '{args[0]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            definition.Id = BuildDefaultExtensionId(sourceName);
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            definition.Name = definition.Id;
        }

        if (string.IsNullOrWhiteSpace(definition.Version))
        {
            definition.Version = "1.0.0";
        }

        if (string.IsNullOrWhiteSpace(definition.Ui.Title))
        {
            definition.Ui.Title = $"{definition.Name} UI";
        }

        foreach ((PhantomExtensionActionDefinition action, int actionLine) in pendingActionChecks)
        {
            if (action.Kind == PhantomExtensionActionKind.Page && definition.FindPage(action.Value) is null)
            {
                throw new PhextParseException(actionLine, $"The page action refers to '{action.Value}', but this file has no app.page with that id.");
            }

            if (action.Kind == PhantomExtensionActionKind.Window && !definition.Ui.HasUi)
            {
                throw new PhextParseException(actionLine, "The window action opens the ui.html window, but this file has no ui.html block.");
            }
        }

        foreach ((string target, PhantomUiCapabilities required, string what, int targetLine) in pendingTargetChecks)
        {
            PhantomUiCapabilities allowed;
            string description;
            bool ownControl = seenElements.Contains(target);
            if (PhantomUiTargets.ResolvesToBuiltIn(target, ownControl, out PhantomUiTargetInfo builtIn))
            {
                allowed = builtIn.Capabilities;
                description = builtIn.Name;
            }
            else if (ownControl)
            {
                allowed = PhantomUiTargets.OwnElementCapabilities;
                description = $"this extension's own '{target}' control";
            }
            else
            {
                throw new PhextParseException(
                    targetLine,
                    $"Unknown UI element '{target}'. Use the id of one of this extension's app.button, menu.button, " +
                    $"menu.dropdown or app.page controls, or a built-in element: {PhantomUiTargets.BuiltInNameList}.");
            }

            if ((allowed & required) != 0)
            {
                continue;
            }

            if (required == PhantomUiCapabilities.Visible && target.Equals("menu.settings", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhextParseException(targetLine, "menu.settings cannot be hidden: Settings is where extensions are disabled and uninstalled.");
            }

            if (required == PhantomUiCapabilities.Visible && target.Equals("history.clear", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhextParseException(targetLine, "history.clear cannot be hidden: clearing the history must always stay possible.");
            }

            throw new PhextParseException(targetLine, $"{char.ToUpperInvariant(what[0])}{what[1..]} cannot be used on {description}.");
        }

        return definition;
    }

    private const int MaximumDropdownOptions = 50;

    /// <summary>
    /// An id for an extension's own control. The original built-in element names are reserved so ui.* targets stay
    /// unambiguous; element names added later are not, so files written before they existed keep loading.
    /// </summary>
    private static string ValidateElementId(string value, int lineNumber, string label)
    {
        string id = ValidateIdentifier(value, lineNumber, label);
        if (PhantomUiTargets.TryGetBuiltIn(id, out PhantomUiTargetInfo builtIn) && builtIn.IdReserved)
        {
            throw new PhextParseException(lineNumber, $"{label} '{id}' is reserved for a built-in PhantomHaze element. Choose another id.");
        }

        return id;
    }

    private static string ParseUiTargetName(string value, int lineNumber)
    {
        string target = value.Trim();
        if (!ValidIdentifierRegex.IsMatch(target))
        {
            throw new PhextParseException(lineNumber, $"UI element '{value}' is invalid. Use a name such as menu.settings.");
        }

        return PhantomUiTargets.TryGetBuiltIn(target, out PhantomUiTargetInfo builtIn) ? builtIn.Name : target;
    }

    private static PhantomUiProperty ParseUiProperty(string value, int lineNumber)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "text" or "label" => PhantomUiProperty.Text,
            "tooltip" => PhantomUiProperty.Tooltip,
            "visible" => PhantomUiProperty.Visible,
            "background" or "background-color" or "backcolor" => PhantomUiProperty.Background,
            "foreground" or "color" or "text-color" or "forecolor" => PhantomUiProperty.Foreground,
            "border" or "border-color" => PhantomUiProperty.Border,
            "font" or "font-family" => PhantomUiProperty.Font,
            "fontsize" or "font-size" => PhantomUiProperty.FontSize,
            "fontstyle" or "font-style" => PhantomUiProperty.FontStyle,
            _ => throw new PhextParseException(
                lineNumber,
                $"Unknown UI property '{value}'. Use text, tooltip, visible, background, foreground, border, font, fontsize or fontstyle."),
        };
    }

    private static PhantomExtensionUiEditDefinition BuildUiEdit(string target, PhantomUiProperty property, string rawValue, int lineNumber)
    {
        switch (property)
        {
            case PhantomUiProperty.Text:
            case PhantomUiProperty.Tooltip:
                return new PhantomExtensionUiEditDefinition { Target = target, Property = property, Text = rawValue, LineNumber = lineNumber };

            case PhantomUiProperty.Visible:
                return new PhantomExtensionUiEditDefinition { Target = target, Property = property, Flag = ParseBoolean(rawValue.Trim(), lineNumber), LineNumber = lineNumber };

            case PhantomUiProperty.Background:
            case PhantomUiProperty.Foreground:
            case PhantomUiProperty.Border:
                return new PhantomExtensionUiEditDefinition
                {
                    Target = target,
                    Property = property,
                    Color = ParseOpaqueColor(rawValue, lineNumber, $"{PhantomUiTargets.GetPropertyName(property)} colour"),
                    LineNumber = lineNumber,
                };

            case PhantomUiProperty.Font:
            {
                string family = rawValue.Trim();
                if (family.Length is 0 or > 64 || family.Any(char.IsControl))
                {
                    throw new PhextParseException(lineNumber, "font expects the name of a font installed on Windows, such as \"Consolas\".");
                }

                return new PhantomExtensionUiEditDefinition { Target = target, Property = property, Text = family, LineNumber = lineNumber };
            }

            case PhantomUiProperty.FontSize:
            {
                if (!float.TryParse(rawValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float size) ||
                    float.IsNaN(size) || size < 6 || size > 48)
                {
                    throw new PhextParseException(lineNumber, $"fontsize expects a size in points between 6 and 48 but received '{rawValue}'.");
                }

                return new PhantomExtensionUiEditDefinition { Target = target, Property = property, Number = size, LineNumber = lineNumber };
            }

            default:
            {
                PhantomUiFontStyle style = rawValue.Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal) switch
                {
                    "regular" or "normal" => PhantomUiFontStyle.Regular,
                    "bold" => PhantomUiFontStyle.Bold,
                    "italic" => PhantomUiFontStyle.Italic,
                    "bold-italic" or "bolditalic" or "italic-bold" => PhantomUiFontStyle.BoldItalic,
                    _ => throw new PhextParseException(lineNumber, $"fontstyle expects regular, bold, italic or bold-italic but received '{rawValue}'."),
                };

                return new PhantomExtensionUiEditDefinition { Target = target, Property = property, FontStyle = style, LineNumber = lineNumber };
            }
        }
    }

    private static PhantomUiShape ParseShape(string value, int lineNumber)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "rectangle" or "rect" or "square" => PhantomUiShape.Rectangle,
            "rounded" or "rounded-rectangle" or "roundrect" => PhantomUiShape.Rounded,
            "pill" or "capsule" => PhantomUiShape.Pill,
            "circle" => PhantomUiShape.Circle,
            "ellipse" or "oval" => PhantomUiShape.Ellipse,
            "diamond" => PhantomUiShape.Diamond,
            "triangle" => PhantomUiShape.Triangle,
            "hexagon" => PhantomUiShape.Hexagon,
            "star" => PhantomUiShape.Star,
            _ => throw new PhextParseException(
                lineNumber,
                $"Unknown shape '{value}'. Use rectangle, rounded, pill, circle, ellipse, diamond, triangle, hexagon or star."),
        };
    }

    private static Color ParseOpaqueColor(string value, int lineNumber, string label)
    {
        if (PhantomColorParser.TryParseOpaque(value, out Color color))
        {
            return color;
        }

        throw new PhextParseException(
            lineNumber,
            $"'{value}' is not a valid {label}. Use #RGB, #RRGGBB, rgb(r, g, b) or a colour name such as \"steelblue\".");
    }

    private static PhantomExtensionActionDefinition ParseAction(string kindText, string value, int lineNumber)
    {
        PhantomExtensionActionKind kind = kindText.Trim().ToLowerInvariant() switch
        {
            "message" => PhantomExtensionActionKind.Message,
            "url" => PhantomExtensionActionKind.Url,
            "page" => PhantomExtensionActionKind.Page,
            "window" => PhantomExtensionActionKind.Window,
            "view" => PhantomExtensionActionKind.View,
            "log" => PhantomExtensionActionKind.Log,
            _ => throw new PhextParseException(
                lineNumber,
                $"Unknown action '{kindText}'. Use message, url, page, window, view or log."),
        };

        switch (kind)
        {
            case PhantomExtensionActionKind.Url:
            {
                string url = value.Trim();
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PhextParseException(lineNumber, $"The url action needs an http:// or https:// address but received '{value}'.");
                }

                return new PhantomExtensionActionDefinition { Kind = kind, Value = url };
            }
            case PhantomExtensionActionKind.Page:
                return new PhantomExtensionActionDefinition { Kind = kind, Value = ValidateIdentifier(value.Trim(), lineNumber, "page id") };
            case PhantomExtensionActionKind.View:
            {
                string view = value.Trim().ToLowerInvariant() switch
                {
                    "logs" or "log" => "logs",
                    "web" or "webview" or "web-view" => "web",
                    _ => throw new PhextParseException(lineNumber, $"The view action accepts logs or web but received '{value}'."),
                };

                return new PhantomExtensionActionDefinition { Kind = kind, Value = view };
            }
            case PhantomExtensionActionKind.Window:
                return new PhantomExtensionActionDefinition { Kind = kind, Value = string.Empty };
            default:
                return new PhantomExtensionActionDefinition { Kind = kind, Value = value };
        }
    }

    private static void EnsureUnique(HashSet<string> seenKeys, string key, int lineNumber, string label)
    {
        if (seenKeys.Add(key))
        {
            return;
        }

        throw new PhextParseException(lineNumber, $"Duplicate {label} '{key}'.");
    }

    private static string ValidateIdentifier(string value, int lineNumber, string label)
    {
        if (!ValidIdentifierRegex.IsMatch(value))
        {
            throw new PhextParseException(
                lineNumber,
                $"{label} '{value}' is invalid. Use only letters, numbers, dot, dash, or underscore.");
        }

        return value;
    }

    private static void RequireArgCount(IReadOnlyList<string> args, int expected, int lineNumber)
    {
        if (args.Count == expected)
        {
            return;
        }

        throw new PhextParseException(
            lineNumber,
            $"'{args[0]}' expects {expected - 1} argument(s) but received {args.Count - 1}.");
    }

    private static bool ParseBoolean(string value, int lineNumber)
    {
        if (TryParseBoolean(value, out bool parsed))
        {
            return parsed;
        }

        throw new PhextParseException(lineNumber, $"'{value}' is not a valid boolean value.");
    }

    /// <summary>The boolean grammar shared by .phext files and stored toggle setting values.</summary>
    internal static bool TryParseBoolean(string? value, out bool parsed)
    {
        parsed = false;
        if (value is null)
        {
            return false;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal))
        {
            parsed = true;
            return true;
        }

        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("0", StringComparison.Ordinal);
    }

    /// <summary>The rule for extension ids and keys (letters, digits, '.', '_' and '-'), shared with the app settings.</summary>
    internal static bool IsValidIdentifier(string? value)
    {
        return value is not null && ValidIdentifierRegex.IsMatch(value);
    }

    private static int ParseInt(string value, int lineNumber, string label)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        throw new PhextParseException(lineNumber, $"'{value}' is not a valid integer for {label}.");
    }

    private static PhantomExtensionSearchEngine ParseSearchEngine(string value, int lineNumber)
    {
        return value.ToLowerInvariant() switch
        {
            "google" => PhantomExtensionSearchEngine.Google,
            "bing" => PhantomExtensionSearchEngine.Bing,
            "yahoo" => PhantomExtensionSearchEngine.Yahoo,
            "duckduckgo" => PhantomExtensionSearchEngine.DuckDuckGo,
            "startpage" => PhantomExtensionSearchEngine.Startpage,
            _ => throw new PhextParseException(lineNumber, $"Unknown search engine '{value}'."),
        };
    }

    private static string ParseUrlPattern(string value, int lineNumber)
    {
        // A blank pattern used to match every URL, so a forgotten value in `web.block id ""` blocked all traffic.
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PhextParseException(lineNumber, "URL pattern cannot be empty. Use \"*\" to match every page.");
        }

        // Stored in the form browsers report hosts in, so "https://*.bücher.de/*" matches xn--bcher-kva.de pages.
        return PhantomExtensionPatternMatcher.CanonicalizeUrlPattern(value);
    }

    private static PhantomExtensionScriptTiming ParseScriptTiming(string value, int lineNumber)
    {
        return value.ToLowerInvariant() switch
        {
            "document-start" => PhantomExtensionScriptTiming.DocumentStart,
            "document-end" => PhantomExtensionScriptTiming.DocumentEnd,
            _ => throw new PhextParseException(lineNumber, $"Unknown script timing '{value}'. Use document-start or document-end."),
        };
    }

    private static string ParseHereDocToken(string token, int lineNumber)
    {
        if (!token.StartsWith("<<", StringComparison.Ordinal) || token.Length <= 2)
        {
            throw new PhextParseException(lineNumber, $"Expected heredoc token in the form <<TAG but received '{token}'.");
        }

        return token[2..];
    }

    private static string ReadBlock(string[] lines, ref int index, int commandLineNumber, string terminator)
    {
        var builder = new StringBuilder();
        for (int i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals(terminator, StringComparison.Ordinal))
            {
                index = i;
                return builder.ToString();
            }

            builder.AppendLine(lines[i]);
        }

        throw new PhextParseException(commandLineNumber, $"Unterminated heredoc block. Missing closing token '{terminator}'.");
    }

    private static List<string> Tokenize(string line, int lineNumber)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool escapeNext = false;

        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (escapeNext)
            {
                current.Append(character switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => character,
                });
                escapeNext = false;
                continue;
            }

            if (character == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                if (!inQuotes && current.Length == 0 &&
                    (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
                {
                    tokens.Add(string.Empty);
                }

                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (escapeNext)
        {
            throw new PhextParseException(lineNumber, "Trailing escape character is not allowed.");
        }

        if (inQuotes)
        {
            throw new PhextParseException(lineNumber, "Unterminated quoted string.");
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Id for a file without meta.id, derived from its file name (also used for hand-edited settings entries).</summary>
    internal static string BuildDefaultExtensionId(string sourceName)
    {
        string candidate = Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"extension-{Guid.NewGuid():N}";
        }

        var normalized = new StringBuilder(candidate.Length);
        bool droppedNonAsciiText = false;
        foreach (char character in candidate.ToLowerInvariant())
        {
            switch (character)
            {
                case >= 'a' and <= 'z':
                case >= '0' and <= '9':
                case '.' or '_' or '-':
                    normalized.Append(character);
                    break;
                default:
                    droppedNonAsciiText |= character > 127 && char.IsLetterOrDigit(character);
                    normalized.Append('-');
                    break;
            }
        }

        string result = normalized.ToString().Trim('-');
        if (droppedNonAsciiText)
        {
            // File names in non-Latin scripts used to collapse to the same id ("Плагин v2" and "Расширение v2"
            // both became "v2", so importing one replaced the other) or to a new random id on every import.
            // A short hash of the original name keeps such ids distinct and stable across re-imports.
            result = $"{(result.Length == 0 ? "extension" : result)}-{BuildShortHash(candidate)}";
        }

        return string.IsNullOrWhiteSpace(result) ? $"extension-{Guid.NewGuid():N}" : result;
    }

    private static string BuildShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}

internal static class PhantomExtensionPatternMatcher
{
    private static readonly char[] PathStartCharacters = { '/', '?', '#' };

    /// <summary>True only for the explicit match-everything pattern "*".</summary>
    public static bool MatchesEverything(string? pattern)
    {
        return pattern is not null && pattern.Trim() == "*";
    }

    /// <summary>
    /// Case-insensitive wildcard match ('*' = any run of characters) of a URL against an extension URL pattern.
    /// Patterns written as scheme://host/path (the form used throughout PHEXT_GUIDE.md) are matched part by
    /// part, so a wildcard in the scheme or host can never reach into the rest of the URL: before this,
    /// "https://*.github.com/*" also matched "https://evil.example/?a.github.com/" and ran the extension's
    /// script there. A host written as "*.example.org" matches example.org itself and all of its subdomains.
    /// Other patterns are matched against the whole URL. Matching never uses backtracking regular
    /// expressions, so long URLs cannot freeze the UI thread (request blocking runs for every request).
    /// </summary>
    public static bool IsMatch(string? value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        string glob = pattern.Trim();
        if (glob == "*")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int schemeSeparator = glob.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0 || glob.IndexOfAny(PathStartCharacters, 0, schemeSeparator) >= 0)
        {
            return GlobMatch(value, glob);
        }

        if (!TrySplitUrl(value, out string scheme, out string host, out string port, out string rest))
        {
            return false;
        }

        string remainder = glob[(schemeSeparator + 3)..];
        int pathStart = remainder.IndexOfAny(PathStartCharacters);
        // "example.com." (a fully qualified name with a trailing dot) is the same host as "example.com".
        string hostGlob = TrimTrailingDot(pathStart < 0 ? remainder : remainder[..pathStart]);
        string pathGlob = pathStart < 0 ? "*" : remainder[pathStart..];
        if (!pathGlob.StartsWith('/'))
        {
            pathGlob = "/" + pathGlob;
        }

        // A port in the pattern ("localhost:8080") is compared together with the URL's port; otherwise any port matches.
        bool patternHasPort = hostGlob.StartsWith('[') ? hostGlob.Contains("]:", StringComparison.Ordinal) : hostGlob.Contains(':');
        string hostTarget = patternHasPort && port.Length > 0 ? $"{host}:{port}" : host;
        bool hostMatches = GlobMatch(hostTarget, hostGlob) ||
                           (hostGlob.StartsWith("*.", StringComparison.Ordinal) && GlobMatch(hostTarget, hostGlob[2..]));

        return GlobMatch(scheme, glob[..schemeSeparator]) && hostMatches && GlobMatch(rest, pathGlob);
    }

    /// <summary>
    /// Splits an absolute URL into scheme, host, port and the path/query/fragment remainder (always starting
    /// with '/'). User info is dropped, so "https://trusted.example@evil.example/" has the host evil.example.
    /// </summary>
    private static bool TrySplitUrl(string url, out string scheme, out string host, out string port, out string rest)
    {
        scheme = host = port = rest = string.Empty;
        int separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        scheme = url[..separator];
        int authorityStart = separator + 3;
        int authorityEnd = url.IndexOfAny(PathStartCharacters, authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        string authority = url[authorityStart..authorityEnd];
        rest = authorityEnd < url.Length ? url[authorityEnd..] : "/";
        if (!rest.StartsWith('/'))
        {
            rest = "/" + rest;
        }

        int userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            authority = authority[(userInfoEnd + 1)..];
        }

        host = authority;
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close > 0)
            {
                host = authority[..(close + 1)];
                if (close + 1 < authority.Length && authority[close + 1] == ':')
                {
                    port = authority[(close + 2)..];
                }
            }
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority[..colon];
                port = authority[(colon + 1)..];
            }

            // Browsers keep the trailing dot of a fully qualified host ("ad.tracker.example."), which would
            // otherwise slip past host patterns such as "*.tracker.example".
            host = TrimTrailingDot(host);
        }

        return true;
    }

    private static string TrimTrailingDot(string host)
    {
        return host.Length > 1 && host[^1] == '.' ? host[..^1] : host;
    }

    /// <summary>
    /// Brings the host of a scheme://host/path pattern into the form browsers report hosts in: internationalised
    /// labels are converted to their ASCII ("xn--") form and a trailing dot is dropped. Other patterns are only trimmed.
    /// </summary>
    public static string CanonicalizeUrlPattern(string pattern)
    {
        string glob = pattern.Trim();
        int schemeSeparator = glob.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0 || glob.IndexOfAny(PathStartCharacters, 0, schemeSeparator) >= 0)
        {
            return glob;
        }

        int hostStart = schemeSeparator + 3;
        int hostEnd = glob.IndexOfAny(PathStartCharacters, hostStart);
        if (hostEnd < 0)
        {
            hostEnd = glob.Length;
        }

        string authority = glob[hostStart..hostEnd];
        if (authority.StartsWith('['))
        {
            return glob;
        }

        int colon = authority.LastIndexOf(':');
        string host = TrimTrailingDot(colon >= 0 ? authority[..colon] : authority);
        string port = colon >= 0 ? authority[colon..] : string.Empty;
        if (host.Any(character => character > 127))
        {
            var mapping = new IdnMapping();
            string[] labels = host.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                // Labels containing '*' cannot be converted; they are left exactly as written.
                if (labels[i].Any(character => character > 127) && !labels[i].Contains('*'))
                {
                    try
                    {
                        labels[i] = mapping.GetAscii(labels[i]);
                    }
                    catch (ArgumentException)
                    {
                        // Not a valid internationalised label; keep it as written.
                    }
                }
            }

            host = string.Join('.', labels);
        }

        return string.Concat(glob.AsSpan(0, hostStart), host, port, glob.AsSpan(hostEnd));
    }

    /// <summary>
    /// Anchored, case-insensitive glob match using the iterative two-pointer algorithm (O(n*m) worst case).
    /// </summary>
    private static bool GlobMatch(string text, string glob)
    {
        int textIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int starTextIndex = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < glob.Length &&
                glob[patternIndex] != '*' &&
                char.ToLowerInvariant(glob[patternIndex]) == char.ToLowerInvariant(text[textIndex]))
            {
                textIndex++;
                patternIndex++;
            }
            else if (patternIndex < glob.Length && glob[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starTextIndex = textIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                textIndex = ++starTextIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < glob.Length && glob[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == glob.Length;
    }
}

internal enum PhantomExtensionPlaceholderContext
{
    /// <summary>Values are inserted verbatim (message boxes, log lines, CSS).</summary>
    PlainText,

    /// <summary>Values are escaped so they are safe inside JavaScript string literals ("...", '...' or `...`).</summary>
    JavaScriptString,

    /// <summary>Values are HTML-encoded.</summary>
    Html,

    /// <summary>Values are percent-encoded, so a setting can never change which site a url action opens.</summary>
    UrlComponent,
}

internal static class PhantomExtensionPlaceholders
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{\{(?:extension\.(?<meta>id|name|version)|setting\.(?<key>[A-Za-z0-9_.-]+))\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Replaces {{extension.*}} and {{setting.*}} placeholders in a single pass, so values inserted for one
    /// placeholder are never re-scanned for further placeholders. Unknown placeholders are left untouched.
    /// </summary>
    public static string Resolve(
        string? template,
        PhantomExtensionDefinition definition,
        IReadOnlyDictionary<string, string> settingValues,
        PhantomExtensionPlaceholderContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            string? value = null;
            if (match.Groups["meta"].Success)
            {
                value = match.Groups["meta"].Value.ToLowerInvariant() switch
                {
                    "id" => definition.Id,
                    "name" => definition.Name,
                    "version" => definition.Version,
                    _ => null,
                };
            }
            else
            {
                string key = match.Groups["key"].Value;
                PhantomExtensionSettingDefinition? setting = definition.Settings.FirstOrDefault(candidate =>
                    candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (setting is not null)
                {
                    value = settingValues.TryGetValue(setting.Key, out string? stored) && stored is not null
                        ? stored
                        : setting.DefaultValue;
                }
            }

            if (value is null)
            {
                return match.Value;
            }

            return context switch
            {
                PhantomExtensionPlaceholderContext.JavaScriptString => PhantomExtensionScriptBuilder.EscapeJavaScriptStringContent(value),
                PhantomExtensionPlaceholderContext.Html => WebUtility.HtmlEncode(value),
                PhantomExtensionPlaceholderContext.UrlComponent => Uri.EscapeDataString(value),
                _ => value,
            };
        });
    }
}

internal static class PhantomExtensionScriptBuilder
{
    private static readonly Regex LegacyGlobalAccessRegex = new(
        @"(?<![\w$.])(?:window|globalThis|self)\s*(?:\?\.|\.)\s*phantomHazeExtensions\b|(?<![\w$.])(?:window|globalThis|self)\s*(?:\?\.)?\s*\[\s*(?:""phantomHazeExtensions""|'phantomHazeExtensions')\s*\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Escapes a value so it can be placed between the quotes of any JavaScript string literal
    /// ("...", '...' or a template literal) without terminating it or starting an interpolation.
    /// </summary>
    public static string EscapeJavaScriptStringContent(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // The default System.Text.Json encoder escapes quotes, apostrophes, backticks, backslashes, control
        // characters, U+2028/U+2029, '<', '>' and '&'. '$' is additionally escaped so "${" cannot open a
        // template-literal substitution.
        string json = JsonSerializer.Serialize(value);
        return json[1..^1].Replace("$", "\\u0024", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension scripts used to read their settings from a page-global window.phantomHazeExtensions object.
    /// That global is no longer created (pages could read or tamper with it); references to it inside an
    /// extension's own script are redirected to the private, closure-scoped equivalent.
    /// </summary>
    public static string RewriteLegacyGlobalAccess(string? script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return string.Empty;
        }

        return LegacyGlobalAccessRegex.Replace(script, "phantomHazeExtensions");
    }

    /// <summary>Completion value of a wrapped web script that ran (ExecuteScriptAsync returns it JSON-encoded).</summary>
    public const string WebScriptRanResult = "phantomhaze:ran";

    /// <summary>Completion value of a wrapped web script that was skipped by its in-page guards.</summary>
    public const string WebScriptSkippedResult = "phantomhaze:skipped";

    private static readonly string SkipWebScriptStatement = $"return {JsonSerializer.Serialize(WebScriptSkippedResult)};";

    /// <summary>
    /// Wraps an extension web script so it runs with private access to its own context
    /// (phantomHazeExtension / phantomHazeExtensions parameters) and only when the document that actually
    /// executes it is fully loaded and still matches the script's URL pattern.
    /// The wrapper evaluates to <see cref="WebScriptRanResult"/> or <see cref="WebScriptSkippedResult"/>, so a
    /// result of "null" from ExecuteScriptAsync means the script could not run at all (usually a syntax error).
    /// </summary>
    public static string BuildWebScript(string contextJson, string? urlPattern, string payload)
    {
        var builder = new StringBuilder(payload.Length + 1024);
        builder.Append("(function (phantomHazeExtension, phantomHazeUrlPattern) {\n");
        AppendUrlGuard(builder, SkipWebScriptStatement);
        // Scripts are injected once a navigation has completed, so the document is "complete". A script that is
        // delivered to the next document instead (the tab navigated while it was being sent) arrives while that
        // document is still loading; it is skipped there, and that document gets its own injection once it completes.
        builder.Append("  if (document.readyState !== \"complete\") {\n");
        builder.Append("    ").Append(SkipWebScriptStatement).Append('\n');
        builder.Append("  }\n");
        builder.Append("  (function (phantomHazeExtension, phantomHazeExtensions) {\n");
        builder.Append("    try {\n");
        builder.Append(payload);
        builder.Append("\n    } catch (error) {\n");
        builder.Append("      console.error(\"PhantomHaze extension script failure\", error);\n");
        builder.Append("    }\n");
        builder.Append("  })(phantomHazeExtension, { [phantomHazeExtension.id]: phantomHazeExtension });\n");
        builder.Append("  return ").Append(JsonSerializer.Serialize(WebScriptRanResult)).Append(";\n");
        builder.Append("})(");
        builder.Append(contextJson);
        builder.Append(", ");
        builder.Append(SerializeUrlPattern(urlPattern));
        builder.Append(");");
        return builder.ToString();
    }

    public static string BuildStyleInjectionScript(string extensionId, string scriptId, string? urlPattern, string css)
    {
        // CSSStyleSheet.replaceSync() silently drops @import rules (it does not throw), so a style sheet that
        // imports others (web fonts, for example) has to keep using a <style> element.
        bool useConstructableSheet = css.IndexOf("@import", StringComparison.OrdinalIgnoreCase) < 0;
        var builder = new StringBuilder(css.Length + 2048);
        builder.Append("(function (phantomHazeUrlPattern) {\n");
        AppendUrlGuard(builder, "return;");
        builder.Append("  var styleId = ").Append(JsonSerializer.Serialize(BuildStyleElementId(extensionId, scriptId))).Append(";\n");
        builder.Append("  var cssText = ").Append(JsonSerializer.Serialize(css)).Append(";\n");
        builder.Append("  var registryKey = typeof Symbol === \"function\" ? Symbol.for(\"phantomHaze.extensionStyles\") : \"__phantomHazeExtensionStyles\";\n");
        if (useConstructableSheet)
        {
            // Constructable stylesheets are not "inline" styles, so a page's Content-Security-Policy (style-src
            // without 'unsafe-inline') cannot silently drop them the way it drops an injected <style> element.
            builder.Append("  try {\n");
            builder.Append("    if (typeof CSSStyleSheet === \"function\" && document.adoptedStyleSheets) {\n");
            builder.Append("      var registry = document[registryKey];\n");
            builder.Append("      if (!registry) {\n");
            builder.Append("        registry = {};\n");
            builder.Append("        Object.defineProperty(document, registryKey, { value: registry });\n");
            builder.Append("      }\n");
            builder.Append("      var sheet = registry[styleId];\n");
            builder.Append("      if (!sheet) {\n");
            builder.Append("        sheet = new CSSStyleSheet();\n");
            builder.Append("        registry[styleId] = sheet;\n");
            builder.Append("      }\n");
            builder.Append("      sheet.replaceSync(cssText);\n");
            builder.Append("      if (document.adoptedStyleSheets.indexOf(sheet) < 0) {\n");
            builder.Append("        document.adoptedStyleSheets = document.adoptedStyleSheets.concat([sheet]);\n");
            builder.Append("      }\n");
            // A <style> element left by an earlier injection of this rule (e.g. before a setting change) is removed.
            builder.Append("      var staleElement = document.getElementById(styleId);\n");
            builder.Append("      if (staleElement && staleElement.parentNode) {\n");
            builder.Append("        staleElement.parentNode.removeChild(staleElement);\n");
            builder.Append("      }\n");
            builder.Append("      return;\n");
            builder.Append("    }\n");
            builder.Append("  } catch (error) {\n");
            builder.Append("    // Fall back to a <style> element below.\n");
            builder.Append("  }\n");
        }

        // <style> element path. A constructable sheet adopted for this rule earlier in the same document is emptied
        // so the two never apply at the same time.
        builder.Append("  try {\n");
        builder.Append("    var adoptedRegistry = document[registryKey];\n");
        builder.Append("    if (adoptedRegistry && adoptedRegistry[styleId]) {\n");
        builder.Append("      adoptedRegistry[styleId].replaceSync(\"\");\n");
        builder.Append("    }\n");
        builder.Append("  } catch (error) {\n");
        builder.Append("  }\n");
        builder.Append("  if (!document.head) {\n    return;\n  }\n");
        builder.Append("  var styleElement = document.getElementById(styleId);\n");
        builder.Append("  if (!styleElement) {\n");
        builder.Append("    styleElement = document.createElement(\"style\");\n");
        builder.Append("    styleElement.id = styleId;\n");
        builder.Append("    document.head.appendChild(styleElement);\n");
        builder.Append("  }\n");
        builder.Append("  styleElement.textContent = cssText;\n");
        builder.Append("})(");
        builder.Append(SerializeUrlPattern(urlPattern));
        builder.Append(");");
        return builder.ToString();
    }

    /// <summary>
    /// Length-prefixed, lower-cased (ids are case-insensitive) DOM id, so distinct extension/script id pairs can
    /// never produce the same element id (e.g. "a-b"+"c" vs "a"+"b-c").
    /// </summary>
    public static string BuildStyleElementId(string extensionId, string scriptId)
    {
        string extension = (extensionId ?? string.Empty).ToLowerInvariant();
        string script = (scriptId ?? string.Empty).ToLowerInvariant();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ph-ext-style-{extension.Length}-{extension}-{script.Length}-{script}");
    }

    private static string SerializeUrlPattern(string? urlPattern)
    {
        return PhantomExtensionPatternMatcher.MatchesEverything(urlPattern)
            ? "null"
            : JsonSerializer.Serialize((urlPattern ?? string.Empty).Trim().ToLowerInvariant());
    }

    // Re-checks, inside the page and at the moment the script runs, that location.href still matches the rule's
    // pattern, using exactly the same rules as PhantomExtensionPatternMatcher.IsMatch (and, like it, without
    // regular expressions). Everything is local to an inner function, so nothing leaks into the extension script.
    private static void AppendUrlGuard(StringBuilder builder, string skipStatement)
    {
        builder.Append("  if (phantomHazeUrlPattern !== null && !(function (href, pattern) {\n");
        builder.Append("    function glob(text, p) {\n");
        builder.Append("      var t = 0, i = 0, star = -1, mark = 0;\n");
        builder.Append("      while (t < text.length) {\n");
        builder.Append("        if (i < p.length && p.charAt(i) !== \"*\" && p.charAt(i) === text.charAt(t)) { t++; i++; }\n");
        builder.Append("        else if (i < p.length && p.charAt(i) === \"*\") { star = i++; mark = t; }\n");
        builder.Append("        else if (star !== -1) { i = star + 1; t = ++mark; }\n");
        builder.Append("        else { return false; }\n");
        builder.Append("      }\n");
        builder.Append("      while (i < p.length && p.charAt(i) === \"*\") { i++; }\n");
        builder.Append("      return i === p.length;\n");
        builder.Append("    }\n");
        builder.Append("    function pathStart(text, from) {\n");
        builder.Append("      for (var k = from; k < text.length; k++) {\n");
        builder.Append("        var c = text.charAt(k);\n");
        builder.Append("        if (c === \"/\" || c === \"?\" || c === \"#\") { return k; }\n");
        builder.Append("      }\n");
        builder.Append("      return -1;\n");
        builder.Append("    }\n");
        builder.Append("    var sep = pattern.indexOf(\"://\");\n");
        builder.Append("    if (sep < 0 || (pathStart(pattern, 0) !== -1 && pathStart(pattern, 0) < sep)) { return glob(href, pattern); }\n");
        builder.Append("    var urlSep = href.indexOf(\"://\");\n");
        builder.Append("    if (urlSep <= 0) { return false; }\n");
        builder.Append("    var authorityEnd = pathStart(href, urlSep + 3);\n");
        builder.Append("    if (authorityEnd < 0) { authorityEnd = href.length; }\n");
        builder.Append("    var authority = href.slice(urlSep + 3, authorityEnd);\n");
        builder.Append("    var rest = authorityEnd < href.length ? href.slice(authorityEnd) : \"/\";\n");
        builder.Append("    if (rest.charAt(0) !== \"/\") { rest = \"/\" + rest; }\n");
        builder.Append("    var at = authority.lastIndexOf(\"@\");\n");
        builder.Append("    if (at >= 0) { authority = authority.slice(at + 1); }\n");
        builder.Append("    var host = authority, port = \"\";\n");
        builder.Append("    if (authority.charAt(0) === \"[\") {\n");
        builder.Append("      var close = authority.indexOf(\"]\");\n");
        builder.Append("      if (close > 0) { host = authority.slice(0, close + 1); if (authority.charAt(close + 1) === \":\") { port = authority.slice(close + 2); } }\n");
        builder.Append("    } else {\n");
        builder.Append("      var colon = authority.lastIndexOf(\":\");\n");
        builder.Append("      if (colon >= 0) { host = authority.slice(0, colon); port = authority.slice(colon + 1); }\n");
        builder.Append("      if (host.length > 1 && host.charAt(host.length - 1) === \".\") { host = host.slice(0, -1); }\n");
        builder.Append("    }\n");
        builder.Append("    var remainder = pattern.slice(sep + 3);\n");
        builder.Append("    var patternPath = pathStart(remainder, 0);\n");
        builder.Append("    var hostGlob = patternPath < 0 ? remainder : remainder.slice(0, patternPath);\n");
        builder.Append("    if (hostGlob.length > 1 && hostGlob.charAt(hostGlob.length - 1) === \".\") { hostGlob = hostGlob.slice(0, -1); }\n");
        builder.Append("    var pathGlob = patternPath < 0 ? \"*\" : remainder.slice(patternPath);\n");
        builder.Append("    if (pathGlob.charAt(0) !== \"/\") { pathGlob = \"/\" + pathGlob; }\n");
        builder.Append("    var hasPort = hostGlob.charAt(0) === \"[\" ? hostGlob.indexOf(\"]:\") >= 0 : hostGlob.indexOf(\":\") >= 0;\n");
        builder.Append("    var hostTarget = hasPort && port.length > 0 ? host + \":\" + port : host;\n");
        builder.Append("    var hostMatches = glob(hostTarget, hostGlob) || (hostGlob.slice(0, 2) === \"*.\" && glob(hostTarget, hostGlob.slice(2)));\n");
        builder.Append("    return glob(href.slice(0, urlSep), pattern.slice(0, sep)) && hostMatches && glob(rest, pathGlob);\n");
        builder.Append("  })(String(location.href).toLowerCase(), phantomHazeUrlPattern)) {\n");
        builder.Append("    ").Append(skipStatement).Append('\n');
        builder.Append("  }\n");
    }
}

internal static class PhantomExtensionIconFactory
{
    public static Image CreateIcon(string? iconToken)
    {
        if (string.IsNullOrWhiteSpace(iconToken))
        {
            return SystemIcons.Shield.ToBitmap();
        }

        string trimmed = iconToken.Trim();

        if (TryCreateImageFromBase64(trimmed, out Image? decoded) && decoded is not null)
        {
            return decoded;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "shield" => SystemIcons.Shield.ToBitmap(),
            "warning" => SystemIcons.Warning.ToBitmap(),
            "info" => SystemIcons.Information.ToBitmap(),
            "error" => SystemIcons.Error.ToBitmap(),
            "question" => SystemIcons.Question.ToBitmap(),
            "security" => SystemIcons.Shield.ToBitmap(),
            "app" => SystemIcons.Application.ToBitmap(),
            _ => SystemIcons.Application.ToBitmap(),
        };
    }

    private static bool TryCreateImageFromBase64(string token, out Image? image)
    {
        image = null;

        // SVG icons (data:image/svg+xml;base64,... or data:image/svg+xml,...) are drawn by PhantomHaze's own renderer.
        if (PhantomSvgDocument.TryDecodeDataUri(token, out string svgSource))
        {
            try
            {
                PhantomSvgDocument document = PhantomSvgDocument.Parse(svgSource);
                image = PhantomSvgRenderer.Render(document, new Size(64, 64), Color.FromArgb(60, 66, 76));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        string payload = token;
        const string base64Prefix = "base64:";
        const string dataPrefix = "data:image";

        if (token.StartsWith(base64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            payload = token[base64Prefix.Length..];
        }
        else if (token.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            int commaIndex = token.IndexOf(',');
            if (commaIndex < 0 || commaIndex >= token.Length - 1)
            {
                return false;
            }

            payload = token[(commaIndex + 1)..];
        }
        else
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes);
            using Image parsed = Image.FromStream(stream);
            image = new Bitmap(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
