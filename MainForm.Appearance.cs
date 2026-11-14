using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

// Themes (Light, Dark, a custom colour or a custom gradient), the text colour (theme default, white, black, a custom
// colour or Rainbow), the app font (from Google Fonts) and the Settings -> Appearance tab.
public partial class MainForm
{
    private enum ThemeGradientDirection
    {
        Horizontal,
        Vertical,
        Diagonal,
    }

    private enum TextColorMode
    {
        /// <summary>The theme's own text colours.</summary>
        Automatic,
        White,
        Black,
        Custom,

        /// <summary>A new random colour every half second.</summary>
        Rainbow,
    }

    private const string DefaultCustomThemeColor = "#2F5D8A";
    private const string DefaultGradientStartColor = "#1E3C72";
    private const string DefaultGradientEndColor = "#7F53AC";
    private const string DefaultCustomTextColor = "#FFFFFF";

    private sealed record ThemeGradient(Color Start, Color End, ThemeGradientDirection Direction)
    {
        public LinearGradientMode Mode => Direction switch
        {
            ThemeGradientDirection.Vertical => LinearGradientMode.Vertical,
            ThemeGradientDirection.Diagonal => LinearGradientMode.ForwardDiagonal,
            _ => LinearGradientMode.Horizontal,
        };

        /// <summary>The same direction for CSS linear-gradient() (extension pages and windows).</summary>
        public string CssAngle => Direction switch
        {
            ThemeGradientDirection.Vertical => "180deg",
            ThemeGradientDirection.Diagonal => "135deg",
            _ => "90deg",
        };
    }

    /// <summary>Every colour the window uses, for one theme.</summary>
    private sealed record ThemePalette
    {
        public bool IsDark { get; init; }

        /// <summary>Light theme dialogs keep the standard Windows colours.</summary>
        public bool UseSystemDialogColors { get; init; }

        public Color AppBack { get; init; }
        public Color PanelBack { get; init; }
        public Color MenuBack { get; init; }
        public Color Text { get; init; }
        public Color InputBack { get; init; }
        public Color InputText { get; init; }
        public Color ButtonBack { get; init; }
        public Color ButtonText { get; init; }
        public Color ButtonBorder { get; init; }
        public Color HistoryButtonActive { get; init; }
        public Color ViewSelectedBack { get; init; }
        public Color ViewNormalBack { get; init; }
        public Color ViewSelectedText { get; init; }
        public Color ViewNormalText { get; init; }
        public Color TabActiveBack { get; init; }
        public Color TabInactiveBack { get; init; }
        public Color TabCloseActiveBack { get; init; }
        public Color TabCloseInactiveBack { get; init; }
        public Color TabActiveText { get; init; }
        public Color TabInactiveText { get; init; }
        public Color LogBack { get; init; }
        public Color LogText { get; init; }
        public Color OverlayBack { get; init; }

        // The browser-style controls of the top bar. Every theme defines them, so no colour is hard-coded for one
        // theme and then shown on another (a dark zoom control with dark text in the Light theme, for example).

        /// <summary>The flat round buttons (Settings, favourite, zoom, Favorites) and the zoom control.</summary>
        public Color ChromeBack { get; init; }
        public Color ChromeText { get; init; }

        /// <summary>The Back and Forward buttons and their outline.</summary>
        public Color NavigationBack { get; init; }
        public Color NavigationText { get; init; }

        /// <summary>The address box (with its Refresh button) and its outline.</summary>
        public Color AddressBack { get; init; }
        public Color AddressText { get; init; }
        public Color AddressBorder { get; init; }

        /// <summary>The strip behind the tabs, and the new tab button on it.</summary>
        public Color TabStripBack { get; init; }
        public Color TabStripText { get; init; }

        public Color ExtensionWindowBack { get; init; }
        public Color FallbackBack { get; init; }
        public Color FallbackText { get; init; }
        public Color DocumentBack { get; init; }
        public Color DocumentText { get; init; }
        public Color DialogBack { get; init; }
        public Color DialogText { get; init; }
        public Color DialogInputBack { get; init; }
        public Color DialogInputText { get; init; }
        public Color DialogButtonBack { get; init; }

        /// <summary>Set for gradient themes: painted across the whole window behind the menu bar, tab strip and panels.</summary>
        public ThemeGradient? Gradient { get; init; }
    }

    // Dark is the new browser-chrome default; Light remains available as a user-selectable theme.
    private static readonly ThemePalette LightPalette = new()
    {
        IsDark = false,
        UseSystemDialogColors = true,
        AppBack = Color.FromArgb(246, 249, 253),
        PanelBack = Color.FromArgb(243, 247, 252),
        MenuBack = Color.FromArgb(224, 228, 235),
        Text = Color.FromArgb(25, 25, 25),
        InputBack = Color.White,
        InputText = Color.Black,
        ButtonBack = SystemColors.Control,
        ButtonText = SystemColors.ControlText,
        ButtonBorder = Color.FromArgb(158, 177, 201),
        HistoryButtonActive = Color.LightSteelBlue,
        ViewSelectedBack = Color.FromArgb(201, 214, 232),
        ViewNormalBack = Color.FromArgb(224, 228, 235),
        ViewSelectedText = Color.FromArgb(30, 36, 43),
        ViewNormalText = Color.FromArgb(72, 77, 86),
        TabActiveBack = Color.FromArgb(221, 233, 255),
        TabInactiveBack = Color.FromArgb(235, 240, 247),
        TabCloseActiveBack = Color.FromArgb(228, 235, 247),
        TabCloseInactiveBack = Color.FromArgb(243, 244, 247),
        TabActiveText = Color.Black,
        TabInactiveText = Color.FromArgb(35, 35, 35),
        LogBack = Color.White,
        LogText = Color.Black,
        OverlayBack = Color.FromArgb(246, 249, 253),
        ChromeBack = SystemColors.Control,
        ChromeText = SystemColors.ControlText,
        NavigationBack = SystemColors.Control,
        NavigationText = Color.FromArgb(20, 21, 22),
        AddressBack = Color.White,
        AddressText = Color.FromArgb(34, 36, 42),
        AddressBorder = Color.FromArgb(97, 133, 188),
        TabStripBack = Color.FromArgb(224, 228, 235),
        TabStripText = Color.FromArgb(35, 35, 35),
        ExtensionWindowBack = Color.White,
        FallbackBack = Color.White,
        FallbackText = Color.Black,
        DocumentBack = Color.White,
        DocumentText = Color.FromArgb(17, 17, 17),
        DialogBack = SystemColors.Control,
        DialogText = SystemColors.ControlText,
        DialogInputBack = SystemColors.Window,
        DialogInputText = SystemColors.WindowText,
        DialogButtonBack = SystemColors.Control,
    };

    private static readonly ThemePalette DarkPalette = new()
    {
        IsDark = true,
        UseSystemDialogColors = false,
        AppBack = Color.FromArgb(33, 35, 41),
        PanelBack = Color.FromArgb(34, 36, 42),
        MenuBack = Color.FromArgb(34, 36, 42),
        Text = Color.FromArgb(224, 233, 245),
        InputBack = Color.FromArgb(54, 62, 74),
        InputText = Color.FromArgb(234, 241, 252),
        ButtonBack = Color.FromArgb(67, 76, 90),
        ButtonText = Color.FromArgb(225, 233, 244),
        ButtonBorder = Color.FromArgb(92, 143, 136),
        HistoryButtonActive = Color.FromArgb(86, 96, 111),
        ViewSelectedBack = Color.FromArgb(231, 255, 245),
        ViewNormalBack = Color.FromArgb(172, 190, 185),
        ViewSelectedText = Color.FromArgb(34, 36, 42),
        ViewNormalText = Color.FromArgb(34, 36, 42),
        TabActiveBack = Color.FromArgb(100, 100, 100),
        TabInactiveBack = Color.FromArgb(52, 60, 72),
        TabCloseActiveBack = Color.FromArgb(100, 100, 100),
        TabCloseInactiveBack = Color.FromArgb(52, 60, 72),
        TabActiveText = Color.FromArgb(240, 245, 252),
        TabInactiveText = Color.FromArgb(200, 211, 226),
        LogBack = Color.FromArgb(33, 35, 41),
        LogText = Color.FromArgb(210, 225, 240),
        OverlayBack = Color.FromArgb(33, 35, 41),
        ChromeBack = Color.FromArgb(34, 36, 42),
        ChromeText = Color.FromArgb(231, 245, 255),
        NavigationBack = Color.FromArgb(231, 245, 255),
        NavigationText = Color.FromArgb(20, 21, 22),
        AddressBack = Color.White,
        AddressText = Color.FromArgb(34, 36, 42),
        AddressBorder = Color.FromArgb(97, 133, 188),
        TabStripBack = Color.FromArgb(52, 60, 72),
        TabStripText = Color.FromArgb(231, 245, 255),
        ExtensionWindowBack = Color.FromArgb(38, 44, 52),
        FallbackBack = Color.FromArgb(31, 36, 43),
        FallbackText = Color.FromArgb(228, 236, 247),
        DocumentBack = Color.FromArgb(38, 44, 52),
        DocumentText = Color.FromArgb(228, 236, 247),
        DialogBack = Color.FromArgb(38, 44, 52),
        DialogText = Color.FromArgb(228, 236, 247),
        DialogInputBack = Color.FromArgb(55, 64, 77),
        DialogInputText = Color.FromArgb(228, 236, 247),
        DialogButtonBack = Color.FromArgb(67, 76, 90),
    };

    private static readonly object DialogThemeExemptTag = new();

    private ThemePalette _palette = LightPalette;
    private readonly System.Windows.Forms.Timer _rainbowTimer = new() { Interval = 500 };
    private Color _rainbowColor = CreateRandomColor();
    private int[]? _customColorDialogColors;

    // App font (Settings -> Appearance -> Font). Loaded fonts are kept for the whole session: disposing a font
    // collection would invalidate every Font object created from it while controls may still be using them.
    private LoadedGoogleFont? _appFont;
    private readonly Dictionary<string, LoadedGoogleFont> _loadedGoogleFonts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string CssUrl, float Size, FontStyle Style), Font> _googleFontCache = new();
    private readonly HashSet<Font> _googleFontObjects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Family, float Size, FontStyle Style), Font> _systemFontCache = new();
    private readonly ConditionalWeakTable<Control, Font> _originalFonts = new();
    private HashSet<string>? _installedFontFamilies;
    private readonly HashSet<string> _reportedMissingFonts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _fontLoadCancellation;
    private Font? _defaultMenuFont;

    private bool IsDarkTheme() => _palette.IsDark;

    private Button[] CommandButtons =>
    [
        _toggleButton, _backButton, _forwardButton, _goButton, _refreshButton, _historyButton,
        _zoomInButton, _zoomOutButton, _newTabButton, _historyClearButton, _findCloseButton,
        _settingsButton, _updateBannerUpdateButton,
    ];

    /// <summary>The panels a gradient theme paints (everything else shows them through a transparent background).</summary>
    private ThemedPanel[] ThemeSurfaces =>
    [
        _menuBarPanel, _chromePanel, _protectionCardPanel, _addressBarPanel, _tabsHostPanel, _historyPanel,
        _contentHostPanel, _webViewHostPanel, _extensionPageHostPanel, _webViewLoadingOverlayPanel,
    ];

    private ToolStrip[] PopupMenus => [_disableProtectionMenu, _extensionMenuOverflowMenu, _extensionDropdownMenu, _updateBannerSnoozeMenu];

    private static ThemePalette BuildThemePalette(AppSettings settings)
    {
        switch (settings.Theme)
        {
            case AppTheme.Dark:
                return DarkPalette;
            case AppTheme.Custom:
                return CreateCustomPalette(ParseColorSetting(settings.CustomThemeColor, DefaultCustomThemeColor), gradient: null);
            case AppTheme.Gradient:
            {
                Color start = ParseColorSetting(settings.GradientStartColor, DefaultGradientStartColor);
                Color end = ParseColorSetting(settings.GradientEndColor, DefaultGradientEndColor);
                // Solid surfaces (inputs, buttons, the log) are derived from the colour halfway along the gradient.
                return CreateCustomPalette(Blend(start, end, 0.5), new ThemeGradient(start, end, settings.GradientDirection));
            }
            default:
                return LightPalette;
        }
    }

    /// <summary>A full palette derived from one colour: lighter shades on a dark colour, darker shades on a light one.</summary>
    private static ThemePalette CreateCustomPalette(Color baseColor, ThemeGradient? gradient)
    {
        bool dark = IsDarkColor(baseColor);
        Color Shade(double amount) => dark ? Blend(baseColor, Color.White, amount) : Blend(baseColor, Color.Black, amount);
        Color text = dark ? Color.FromArgb(236, 241, 248) : Color.FromArgb(25, 25, 25);
        Color inputBack = dark ? Blend(baseColor, Color.White, 0.16) : Blend(baseColor, Color.White, 0.86);
        Color inputText = dark ? Color.FromArgb(240, 244, 250) : Color.Black;
        Color selectedText = dark ? Color.White : Color.FromArgb(20, 20, 20);
        Color normalText = dark ? Color.FromArgb(214, 222, 233) : Color.FromArgb(62, 66, 74);

        return new ThemePalette
        {
            IsDark = dark,
            UseSystemDialogColors = false,
            AppBack = baseColor,
            PanelBack = Shade(0.06),
            MenuBack = Shade(0.14),
            Text = text,
            InputBack = inputBack,
            InputText = inputText,
            ButtonBack = Shade(0.20),
            ButtonText = text,
            ButtonBorder = Shade(0.42),
            HistoryButtonActive = Shade(0.34),
            ViewSelectedBack = Shade(0.30),
            ViewNormalBack = Shade(0.14),
            ViewSelectedText = selectedText,
            ViewNormalText = normalText,
            TabActiveBack = Shade(0.26),
            TabInactiveBack = Shade(0.10),
            TabCloseActiveBack = Shade(0.32),
            TabCloseInactiveBack = Shade(0.16),
            TabActiveText = selectedText,
            TabInactiveText = normalText,
            LogBack = dark ? Blend(baseColor, Color.Black, 0.35) : Blend(baseColor, Color.White, 0.93),
            LogText = dark ? Color.FromArgb(222, 231, 243) : Color.Black,
            OverlayBack = baseColor,
            ChromeBack = Shade(0.06),
            ChromeText = text,
            NavigationBack = dark ? Blend(baseColor, Color.White, 0.88) : Shade(0.20),
            NavigationText = Color.FromArgb(20, 21, 22),
            AddressBack = Color.White,
            AddressText = Color.FromArgb(34, 36, 42),
            AddressBorder = Shade(0.42),
            TabStripBack = dark ? Shade(0.10) : Shade(0.14),
            TabStripText = normalText,
            ExtensionWindowBack = Shade(0.06),
            FallbackBack = Shade(0.06),
            FallbackText = text,
            DocumentBack = Shade(0.06),
            DocumentText = text,
            DialogBack = Shade(0.06),
            DialogText = text,
            DialogInputBack = inputBack,
            DialogInputText = inputText,
            DialogButtonBack = Shade(0.20),
            Gradient = gradient,
        };
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int Mix(int a, int b) => (int)Math.Round(a + ((b - a) * amount));
        return Color.FromArgb(255, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    private static double RelativeLuminance(Color color)
    {
        static double Channel(int value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>True when white text reads better on the colour than black text.</summary>
    private static bool IsDarkColor(Color color) => ContrastRatio(color, Color.White) >= ContrastRatio(color, Color.Black);

    private static Color GetContrastingTextColor(Color background) => IsDarkColor(background) ? Color.White : Color.Black;

    private static Color ParseColorSetting(string? value, string fallback)
    {
        if (PhantomColorParser.TryParseOpaque(value, out Color color))
        {
            return color;
        }

        PhantomColorParser.TryParseOpaque(fallback, out color);
        return color;
    }

    private static string NormalizeColorSetting(string? value, string fallback)
    {
        return PhantomColorParser.ToHex(ParseColorSetting(value, fallback));
    }

    private static Color CreateRandomColor() => Color.FromArgb(255, Random.Shared.Next(256), Random.Shared.Next(256), Random.Shared.Next(256));

    /// <summary>The text colour to use where the theme would use <paramref name="themeDefault"/>.</summary>
    private Color ResolveTextColor(Color themeDefault)
    {
        return _settings.TextColorMode switch
        {
            TextColorMode.White => Color.White,
            TextColorMode.Black => Color.Black,
            TextColorMode.Custom => ParseColorSetting(_settings.CustomTextColor, DefaultCustomTextColor),
            TextColorMode.Rainbow => _rainbowColor,
            _ => themeDefault,
        };
    }

    /// <summary>
    /// The background for a control that holds text (an input, button or tab). With a fixed text colour that would be
    /// nearly invisible on it (white text on the white address box of the Light theme, for example), a neutral
    /// background is used instead, so what you type always stays readable. Rainbow colours are random by design.
    /// </summary>
    private Color ResolveSurfaceBack(Color themeBack, Color text)
    {
        if (_settings.TextColorMode is TextColorMode.Automatic or TextColorMode.Rainbow || ContrastRatio(themeBack, text) >= 2.0)
        {
            return themeBack;
        }

        return IsDarkColor(text) ? Color.FromArgb(246, 248, 251) : Color.FromArgb(43, 47, 54);
    }

    /// <summary>
    /// <paramref name="preferred"/> when it can be read on <paramref name="background"/>, otherwise black or white.
    /// Used where a colour carries meaning or belongs to a fixed design and must stay legible on any theme.
    /// </summary>
    private static Color EnsureReadable(Color preferred, Color background, double minimumContrast = 3.0)
    {
        return ContrastRatio(preferred, background) >= minimumContrast ? preferred : GetContrastingTextColor(background);
    }

    // The Log / Web switch keeps its own colours on every theme (text colour settings still apply to its labels).
    private static readonly Color SegmentedSelectedBack = Color.FromArgb(231, 255, 245);
    private static readonly Color SegmentedNormalBack = Color.FromArgb(172, 190, 185);
    private static readonly Color SegmentedText = Color.FromArgb(34, 36, 42);
    private static readonly Color SegmentedBorder = Color.FromArgb(92, 143, 136);

    // The protection button never follows the theme or the text colour setting: it must always be readable.
    private static readonly Color ProtectionButtonBack = Color.FromArgb(240, 240, 240);
    private static readonly Color ProtectionButtonText = Color.FromArgb(34, 36, 42);

    private void ApplyTheme()
    {
        bool hadGradient = _palette.Gradient is not null;
        _palette = BuildThemePalette(_settings);
        ThemePalette palette = _palette;

        BackColor = palette.AppBack;
        _menuBarPanel.BackColor = palette.MenuBack;
        _chromePanel.BackColor = palette.MenuBack;
        _protectionCardPanel.BackColor = palette.MenuBack;
        _addressBarPanel.BackColor = palette.PanelBack;
        _tabsHostPanel.BackColor = palette.TabStripBack;
        _contentHostPanel.BackColor = palette.AppBack;
        _webViewHostPanel.BackColor = palette.AppBack;
        _extensionPageHostPanel.BackColor = palette.AppBack;
        _webViewLoadingOverlayPanel.BackColor = palette.OverlayBack;
        _historyPanel.BackColor = palette.PanelBack;
        _findPanel.BackColor = palette.PanelBack;
        _extensionLauncherPanel.BackColor = palette.PanelBack;

        foreach (Button button in CommandButtons)
        {
            button.FlatAppearance.BorderColor = palette.ButtonBorder;
        }

        _favoriteButton.FlatAppearance.BorderColor = palette.ButtonBorder;
        _backButton.FlatAppearance.BorderColor = palette.NavigationText;
        _forwardButton.FlatAppearance.BorderColor = palette.NavigationText;

        // The layout panels inside each surface have no colour of their own: with a gradient they are transparent,
        // otherwise they show their surface's colour. That is also what lets ui.element recolour a whole surface
        // (menu.bar, address.panel, tabs.bar) by changing the colour of the surface alone.
        ApplyGradientTransparency(palette.Gradient is not null);
        _zoomLabel.BackColor = Color.Transparent;
        UpdateProtectionStatusVisual();
        ApplyThemeToUpdateBanner();
        ApplyTextColors();
        if (hadGradient || palette.Gradient is not null)
        {
            // Colour changes repaint solid themes by themselves; a gradient is painted across several surfaces.
            InvalidateThemeSurfaces();
        }
    }

    /// <summary>Applies the text colour everywhere. Runs every half second in Rainbow mode, so it never logs.</summary>
    private void ApplyTextColors()
    {
        ThemePalette palette = _palette;
        Color text = ResolveTextColor(palette.Text);
        Color inputText = ResolveTextColor(palette.InputText);
        Color inputBack = ResolveSurfaceBack(palette.InputBack, inputText);
        Color logText = ResolveTextColor(palette.LogText);
        Color buttonText = ResolveTextColor(palette.ButtonText);
        Color buttonBack = ResolveSurfaceBack(palette.ButtonBack, buttonText);

        foreach (Control label in new Control[] { _menuLogoLabel, _menuLogoPictureBox, _menuClockLabel, _webViewLoadingLabel, _historyTitleLabel, _favoritesTitleLabel, _findCountLabel })
        {
            label.ForeColor = text;
        }

        foreach (Control input in new Control[] { _historyListBox, _favoritesListBox, _findTextBox })
        {
            input.BackColor = inputBack;
            input.ForeColor = inputText;
        }

        _logBox.BackColor = ResolveSurfaceBack(palette.LogBack, logText);
        _logBox.ForeColor = logText;

        foreach (Button button in CommandButtons)
        {
            button.BackColor = buttonBack;
            button.ForeColor = buttonText;
        }

        // The browser-style controls take their colours from the theme like everything else, and the text colour
        // setting (including Rainbow) applies to them too.
        Color chromeText = ResolveTextColor(palette.ChromeText);
        Color chromeBack = ResolveSurfaceBack(palette.ChromeBack, chromeText);
        foreach (Button button in new[] { _settingsButton, _zoomInButton, _zoomOutButton })
        {
            button.BackColor = chromeBack;
            button.ForeColor = chromeText;
        }

        _zoomLabel.ForeColor = chromeText;
        if (_zoomLayout is not null)
        {
            _zoomLayout.BackColor = chromeBack;
        }

        Color navigationText = ResolveTextColor(EnsureReadable(palette.NavigationText, palette.NavigationBack));
        Color navigationBack = ResolveSurfaceBack(palette.NavigationBack, navigationText);
        foreach (Button button in new[] { _backButton, _forwardButton })
        {
            button.BackColor = navigationBack;
            button.ForeColor = navigationText;
        }

        Color addressText = ResolveTextColor(palette.AddressText);
        Color addressBack = ResolveSurfaceBack(palette.AddressBack, addressText);
        _addressBarTextBox.BackColor = addressBack;
        _addressBarTextBox.ForeColor = addressText;
        _refreshButton.BackColor = addressBack;
        _refreshButton.ForeColor = addressText;
        if (_addressInputLayout is not null)
        {
            _addressInputLayout.BackColor = addressBack;
        }

        Color tabStripText = ResolveTextColor(palette.TabStripText);
        _newTabButton.BackColor = ResolveSurfaceBack(palette.TabStripBack, tabStripText);
        _newTabButton.ForeColor = tabStripText;

        _toggleButton.BackColor = ProtectionButtonBack;
        _toggleButton.ForeColor = ProtectionButtonText;

        UpdateHistoryButtonVisual();
        UpdateTabHeaderVisuals();
        UpdateFavoriteButtonState();
        ApplyThemeToDynamicExtensionControls();
        UpdateWebViewToggleVisual();
    }

    /// <summary>The Favorites button is highlighted while the history and favourites panel is open.</summary>
    private void UpdateHistoryButtonVisual()
    {
        Color text = ResolveTextColor(_palette.ChromeText);
        _historyButton.ForeColor = text;
        _historyButton.BackColor = ResolveSurfaceBack(_historyPanel.Visible ? _palette.HistoryButtonActive : _palette.ChromeBack, text);
    }

    private void ApplyGradientTransparency(bool gradient)
    {
        Control?[] containers =
        [
            _menuLayout, _menuLogoLayout, _extensionMenuButtonsPanel, _menuLogoLabel, _menuLogoPictureBox, _menuClockLabel,
            _addressRootLayout, _addressStripLayout, _tabsLayout, _tabsFlowPanel, _zoomLabel,
            _historyLayout, _historyTitleLabel, _favoritesTitleLabel, _webViewLoadingLabel,
        ];

        foreach (Control? control in containers)
        {
            if (control is null)
            {
                continue;
            }

            try
            {
                if (gradient)
                {
                    control.BackColor = Color.Transparent;
                }
                else
                {
                    control.ResetBackColor();
                }
            }
            catch (ArgumentException)
            {
                // This control cannot be transparent; it keeps its solid colour.
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!PaintThemeGradient(this, e))
        {
            base.OnPaintBackground(e);
        }
    }

    /// <summary>
    /// Paints the gradient of a gradient theme. It is laid out across the whole window, so the menu bar, tab strip and
    /// panels together show one continuous gradient. Returns false (plain background) for other themes.
    /// </summary>
    private bool PaintThemeGradient(Control surface, PaintEventArgs e)
    {
        if (_palette.Gradient is not ThemeGradient gradient || _surfacesWithBackgroundOverride.Contains(surface))
        {
            return false;
        }

        Size windowSize = ClientSize;
        if (windowSize.Width <= 0 || windowSize.Height <= 0 || !IsHandleCreated || !surface.IsHandleCreated)
        {
            return false;
        }

        Point origin = ReferenceEquals(surface, this) ? Point.Empty : surface.PointToClient(PointToScreen(Point.Empty));
        using var brush = new LinearGradientBrush(new Rectangle(origin, windowSize), gradient.Start, gradient.End, gradient.Mode);
        e.Graphics.FillRectangle(brush, surface.ClientRectangle);
        return true;
    }

    private void InvalidateThemeSurfaces()
    {
        Invalidate(false);
        foreach (ThemedPanel surface in ThemeSurfaces)
        {
            surface.Invalidate(true);
        }
    }

    private void UpdateRainbowTimer()
    {
        if (_settings.TextColorMode == TextColorMode.Rainbow)
        {
            if (!_rainbowTimer.Enabled)
            {
                _rainbowTimer.Start();
            }
        }
        else if (_rainbowTimer.Enabled)
        {
            _rainbowTimer.Stop();
        }
    }

    private void OnRainbowTimerTick(object? sender, EventArgs e)
    {
        if (_settings.TextColorMode != TextColorMode.Rainbow || IsDisposed)
        {
            _rainbowTimer.Stop();
            return;
        }

        _rainbowColor = CreateRandomColor();
        ApplyTextColors();
    }

    // ---- App font ---------------------------------------------------------------------------------------------

    /// <summary>The app-font version of <paramref name="baseline"/> (same size and style), or the baseline itself.</summary>
    private Font MapFont(Font baseline)
    {
        return _appFont is null ? baseline : GetGoogleFont(_appFont, baseline.SizeInPoints, baseline.Style);
    }

    private Font GetGoogleFont(LoadedGoogleFont font, float size, FontStyle style)
    {
        FontStyle available = ChooseAvailableStyle(font.FontFamily, style);
        var key = (font.CssUrl, size, available);
        if (!_googleFontCache.TryGetValue(key, out Font? cached))
        {
            cached = new Font(font.FontFamily, size, available, GraphicsUnit.Point);
            _googleFontCache[key] = cached;
            _googleFontObjects.Add(cached);
        }

        return cached;
    }

    /// <summary>
    /// The font for a ui.element font/fontsize/fontstyle change. Font families that are not installed are ignored
    /// (and reported once), so a typo never turns a control's text into an unrelated fallback font.
    /// </summary>
    private Font GetOverrideFont(Font baseFont, string? family, float? size, PhantomUiFontStyle? style)
    {
        float targetSize = size ?? baseFont.SizeInPoints;
        FontStyle targetStyle = style switch
        {
            PhantomUiFontStyle.Regular => FontStyle.Regular,
            PhantomUiFontStyle.Bold => FontStyle.Bold,
            PhantomUiFontStyle.Italic => FontStyle.Italic,
            PhantomUiFontStyle.BoldItalic => FontStyle.Bold | FontStyle.Italic,
            _ => baseFont.Style,
        };

        string name = baseFont.Name;
        if (family is not null)
        {
            if ((_appFont is not null && family.Equals(_appFont.Family, StringComparison.OrdinalIgnoreCase)) || IsInstalledFontFamily(family))
            {
                name = family;
            }
            else if (_reportedMissingFonts.Add(family))
            {
                Log($"An extension asked for the font \"{NormalizeForLog(family)}\", which is not installed; the normal font is used instead.");
            }
        }

        if (_appFont is not null && name.Equals(_appFont.Family, StringComparison.OrdinalIgnoreCase))
        {
            return GetGoogleFont(_appFont, targetSize, targetStyle);
        }

        var key = (name.ToLowerInvariant(), targetSize, targetStyle);
        if (!_systemFontCache.TryGetValue(key, out Font? cached))
        {
            try
            {
                using var fontFamily = new FontFamily(name);
                cached = new Font(fontFamily, targetSize, ChooseAvailableStyle(fontFamily, targetStyle), GraphicsUnit.Point);
            }
            catch (ArgumentException)
            {
                return baseFont;
            }

            _systemFontCache[key] = cached;
        }

        return cached;
    }

    private bool IsInstalledFontFamily(string family)
    {
        if (_installedFontFamilies is null)
        {
            _installedFontFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (FontFamily installed in FontFamily.Families)
                {
                    _installedFontFamilies.Add(installed.Name);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                // Font enumeration failed; treat every family as missing.
            }
        }

        return _installedFontFamilies.Contains(family);
    }

    private static FontStyle ChooseAvailableStyle(FontFamily family, FontStyle wanted)
    {
        if (family.IsStyleAvailable(wanted))
        {
            return wanted;
        }

        foreach (FontStyle candidate in new[] { wanted & ~FontStyle.Italic, wanted & ~FontStyle.Bold, FontStyle.Regular, FontStyle.Bold, FontStyle.Italic })
        {
            if (family.IsStyleAvailable(candidate))
            {
                return candidate;
            }
        }

        return FontStyle.Regular;
    }

    /// <summary>Remembers the fonts controls were built with, so the default font can be restored after a custom one.</summary>
    private void RegisterOriginalFonts(Control root)
    {
        foreach (Control control in EnumerateControlTree(root))
        {
            if (!_originalFonts.TryGetValue(control, out _))
            {
                _originalFonts.Add(control, control.Font);
            }
        }
    }

    private static IEnumerable<Control> EnumerateControlTree(Control root)
    {
        var pending = new Stack<Control>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Control control = pending.Pop();
            yield return control;
            foreach (Control child in control.Controls)
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Switches every control below <paramref name="root"/> to the app font (or back to its own font). Controls whose
    /// font depends on their state - the built-in elements extensions can customise, extension menu buttons and tab
    /// close buttons - are handled by ApplyUiElementState and UpdateTabLayout instead.
    /// </summary>
    private void ApplyAppFontToTree(Control root)
    {
        foreach (Control control in EnumerateControlTree(root).ToList())
        {
            if (control is WebView2 || _uiBoundControls.Contains(control) ||
                ReferenceEquals(control.Tag, TabCloseButtonTag) || control.Tag is ExtensionMenuEntry)
            {
                continue;
            }

            // A control that inherits its parent's font follows the parent (which is visited first); setting its
            // font would stop it from following later changes, such as DPI scaling.
            Font current = control.Font;
            if (!ReferenceEquals(control, root) && control.Parent is Control parent && ReferenceEquals(current, parent.Font))
            {
                continue;
            }

            if (_appFont is null)
            {
                // Back to the default font: only controls that were switched to a downloaded font are touched.
                if (_googleFontObjects.Contains(current) && _originalFonts.TryGetValue(control, out Font? original))
                {
                    control.Font = original;
                }

                continue;
            }

            if (!_originalFonts.TryGetValue(control, out Font? baseline))
            {
                baseline = current;
                _originalFonts.Add(control, baseline);
            }

            Font target = MapFont(baseline);
            if (!ReferenceEquals(current, target))
            {
                control.Font = target;
            }
        }
    }

    /// <summary>Uses the app font in a dialog (Settings, the import prompt) that was built with the default font.</summary>
    private void ApplyAppFontToDialog(Control root)
    {
        if (_appFont is null)
        {
            return;
        }

        foreach (Control control in EnumerateControlTree(root).ToList())
        {
            if (control is not WebView2)
            {
                control.Font = MapFont(control.Font);
            }
        }
    }

    private bool IsBlockedByExtension(string url) => TryGetBlockingExtensionRule(url, out _, out _);

    private async Task<LoadedGoogleFont> LoadGoogleFontAsync(string cssUrl, CancellationToken cancellationToken)
    {
        if (_loadedGoogleFonts.TryGetValue(cssUrl, out LoadedGoogleFont? loaded))
        {
            return loaded;
        }

        GoogleFontFiles files = GoogleFontLoader.TryGetCached(cssUrl, _fontCacheDirectory) ??
                                await GoogleFontLoader.DownloadAsync(cssUrl, _fontCacheDirectory, IsBlockedByExtension, cancellationToken);

        // Another load of the same link may have finished while this one was downloading.
        if (_loadedGoogleFonts.TryGetValue(cssUrl, out loaded))
        {
            return loaded;
        }

        loaded = GoogleFontLoader.Load(files);
        _loadedGoogleFonts[cssUrl] = loaded;
        return loaded;
    }

    /// <summary>Loads the font in Settings -> Appearance -> Font (or goes back to the default font) and applies it.</summary>
    private async Task ApplyCustomFontAsync()
    {
        string cssUrl = _settings.CustomFontUrl;
        _fontLoadCancellation?.Cancel();
        if (string.IsNullOrWhiteSpace(cssUrl))
        {
            if (_appFont is not null)
            {
                _appFont = null;
                OnAppFontChanged();
                Log("The app font was reset to the default font.");
            }

            return;
        }

        if (_appFont is not null && string.Equals(_appFont.CssUrl, cssUrl, StringComparison.Ordinal))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _fontLoadCancellation = cancellation;
        try
        {
            if (!_loadedGoogleFonts.ContainsKey(cssUrl) && GoogleFontLoader.TryGetCached(cssUrl, _fontCacheDirectory) is null)
            {
                Log("Downloading the app font from Google Fonts...");
            }

            LoadedGoogleFont font = await LoadGoogleFontAsync(cssUrl, cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || !string.Equals(_settings.CustomFontUrl, cssUrl, StringComparison.Ordinal))
            {
                return;
            }

            _appFont = font;
            OnAppFontChanged();
            Log($"App font set to \"{NormalizeForLog(font.Family)}\" from Google Fonts.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Replaced by a newer font choice, or the window is closing.
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Log($"The app font could not be loaded from Google Fonts, so the default font is used: {ex.Message}");
            }
        }
    }

    private void OnAppFontChanged()
    {
        SuspendLayout();
        try
        {
            ApplyAppFontToTree(this);
            _defaultMenuFont ??= _disableProtectionMenu.Font;
            foreach (ToolStrip menu in PopupMenus)
            {
                menu.Font = MapFont(_defaultMenuFont);
            }

            UpdateTabLayout();
            ApplyUiElementState();
        }
        finally
        {
            ResumeLayout(true);
        }

        UpdateExtensionMenuOverflow();
        // Extension windows and pages load the same font through their documents.
        RefreshOpenExtensionWindows();
        RefreshExtensionPages();
    }

    // ---- Dialogs ----------------------------------------------------------------------------------------------

    /// <summary>Colours a dialog for the current theme (Light theme dialogs keep the standard Windows look).</summary>
    private void ApplyDialogTheme(Control root)
    {
        if (_palette.UseSystemDialogColors)
        {
            return;
        }

        ApplyDialogColors(root, _palette);
    }

    private static void ApplyDialogColors(Control control, ThemePalette palette)
    {
        if (ReferenceEquals(control.Tag, DialogThemeExemptTag))
        {
            return;
        }

        switch (control)
        {
            case TextBoxBase:
            case ComboBox:
            case ListBox:
            case NumericUpDown:
                control.BackColor = palette.DialogInputBack;
                control.ForeColor = palette.DialogInputText;
                break;
            case Button:
                control.BackColor = palette.DialogButtonBack;
                control.ForeColor = palette.DialogText;
                break;
            default:
                control.BackColor = palette.DialogBack;
                control.ForeColor = palette.DialogText;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyDialogColors(child, palette);
        }
    }

    /// <summary>A tab of the Settings window.</summary>
    private sealed class SettingsTab
    {
        public SettingsTab(TabPage page)
        {
            Page = page;
        }

        public TabPage Page { get; }

        /// <summary>Copies the tab's choices into the settings that are about to be saved.</summary>
        public Action<AppSettings>? ApplyTo { get; init; }

        /// <summary>Returns a message when the tab's input cannot be saved (Save then keeps the window open).</summary>
        public Func<string?>? Validate { get; init; }

        /// <summary>Runs when the tab is shown, for work only needed when someone looks at it.</summary>
        public Action? Shown { get; init; }
    }

    /// <summary>A colour button shows its colour, its #RRGGBB value and readable text on top.</summary>
    private static void ShowSwatchColor(Button button, Color color)
    {
        Color text = GetContrastingTextColor(color);
        button.BackColor = color;
        button.ForeColor = text;
        button.FlatAppearance.BorderColor = Blend(color, text, 0.4);
        button.Text = $"{PhantomColorParser.ToHex(color)}   Choose…";
    }

    private bool PickColor(IWin32Window owner, Color initial, out Color picked)
    {
        using var dialog = new ColorDialog
        {
            Color = initial,
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
        };

        if (_customColorDialogColors is not null)
        {
            dialog.CustomColors = _customColorDialogColors;
        }

        bool ok = ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) == DialogResult.OK;
        // The "custom colours" row of the picker is remembered for the rest of the session.
        _customColorDialogColors = dialog.CustomColors;
        picked = Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B);
        return ok;
    }

    private SettingsTab BuildAppearanceTab(AppSettings draft, Form owner, Font boldFont)
    {
        var page = new TabPage("Appearance");
        AppTheme theme = draft.Theme;
        Color themeColor = ParseColorSetting(draft.CustomThemeColor, DefaultCustomThemeColor);
        Color gradientStart = ParseColorSetting(draft.GradientStartColor, DefaultGradientStartColor);
        Color gradientEnd = ParseColorSetting(draft.GradientEndColor, DefaultGradientEndColor);
        ThemeGradientDirection gradientDirection = draft.GradientDirection;
        TextColorMode textMode = draft.TextColorMode;
        Color customTextColor = ParseColorSetting(draft.CustomTextColor, DefaultCustomTextColor);
        LoadedGoogleFont? previewFont = _appFont is not null && string.Equals(_appFont.CssUrl, draft.CustomFontUrl, StringComparison.Ordinal)
            ? _appFont
            : null;
        Color previewRainbow = CreateRandomColor();
        bool initializing = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(160)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;

        Label AddRow(string? labelText, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label
            {
                Text = labelText ?? string.Empty,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = ScalePadding(0, 8, 8, 8),
            };
            layout.Controls.Add(label, 0, row);
            control.Margin = ScalePadding(0, 4, 0, 4);
            layout.Controls.Add(control, 1, row);
            row++;
            return label;
        }

        var preview = new Panel
        {
            Size = new Size(ScaleByDpi(420), ScaleByDpi(64)),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle,
            Tag = DialogThemeExemptTag,
        };

        void RefreshPreview() => preview.Invalidate();

        Button CreateSwatchButton(string accessibleName, Func<Color> getColor, Action<Color> setColor)
        {
            var button = new Button
            {
                AutoSize = true,
                MinimumSize = new Size(ScaleByDpi(140), ScaleByDpi(28)),
                FlatStyle = FlatStyle.Flat,
                AccessibleName = accessibleName,
                Tag = DialogThemeExemptTag,
                Margin = ScalePadding(0, 0, 8, 0),
            };

            button.Click += (_, _) =>
            {
                if (PickColor(owner, getColor(), out Color picked))
                {
                    setColor(picked);
                    ShowSwatchColor(button, picked);
                    RefreshPreview();
                }
            };
            ShowSwatchColor(button, getColor());
            return button;
        }

        // Theme
        var themeComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = ScaleByDpi(220) };
        OptionItem<AppTheme>[] themeOptions =
        [
            new(AppTheme.Light, "Light"),
            new(AppTheme.Dark, "Dark"),
            new(AppTheme.Custom, "Custom colour"),
            new(AppTheme.Gradient, "Custom gradient"),
        ];
        themeComboBox.Items.AddRange(themeOptions);
        themeComboBox.SelectedItem = themeOptions.FirstOrDefault(option => option.Value == theme) ?? themeOptions[0];
        AddRow("Theme", themeComboBox);

        Button themeColorButton = CreateSwatchButton("Theme colour", () => themeColor, color => themeColor = color);
        Label themeColorLabel = AddRow("Theme colour", themeColorButton);

        var gradientPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        Button gradientStartButton = CreateSwatchButton("Gradient start colour", () => gradientStart, color => gradientStart = color);
        Button gradientEndButton = CreateSwatchButton("Gradient end colour", () => gradientEnd, color => gradientEnd = color);
        var directionComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = ScaleByDpi(130), Margin = ScalePadding(0, 2, 0, 0) };
        OptionItem<ThemeGradientDirection>[] directionOptions =
        [
            new(ThemeGradientDirection.Horizontal, "Left to right"),
            new(ThemeGradientDirection.Vertical, "Top to bottom"),
            new(ThemeGradientDirection.Diagonal, "Diagonal"),
        ];
        directionComboBox.Items.AddRange(directionOptions);
        directionComboBox.SelectedItem = directionOptions.FirstOrDefault(option => option.Value == gradientDirection) ?? directionOptions[0];
        directionComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (directionComboBox.SelectedItem is OptionItem<ThemeGradientDirection> option)
            {
                gradientDirection = option.Value;
                RefreshPreview();
            }
        };
        gradientPanel.Controls.Add(gradientStartButton);
        gradientPanel.Controls.Add(gradientEndButton);
        gradientPanel.Controls.Add(directionComboBox);
        Label gradientLabel = AddRow("Gradient", gradientPanel);

        AddRow("Preview", preview);

        // Text colour
        var textColorComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = ScaleByDpi(220) };
        OptionItem<TextColorMode>[] textOptions =
        [
            new(TextColorMode.Automatic, "Theme default"),
            new(TextColorMode.White, "White"),
            new(TextColorMode.Black, "Black"),
            new(TextColorMode.Custom, "Custom"),
            new(TextColorMode.Rainbow, "Rainbow"),
        ];
        textColorComboBox.Items.AddRange(textOptions);
        textColorComboBox.SelectedItem = textOptions.FirstOrDefault(option => option.Value == textMode) ?? textOptions[0];
        AddRow("Text colour", textColorComboBox);

        Button customTextColorButton = CreateSwatchButton("Custom text colour", () => customTextColor, color => customTextColor = color);
        Label customTextColorLabel = AddRow("Custom text colour", customTextColorButton);

        // Font
        var fontPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
        fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fontPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var fontTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = draft.CustomFontUrl,
            PlaceholderText = "https://fonts.google.com/specimen/Roboto",
            AccessibleName = "Google Fonts link",
            Margin = ScalePadding(0, 2, 6, 0),
        };
        var loadFontButton = new Button { Text = "Load font", AutoSize = true, Margin = ScalePadding(0, 0, 6, 0) };
        var defaultFontButton = new Button { Text = "Default font", AutoSize = true, Margin = new Padding(0) };
        fontPanel.Controls.Add(fontTextBox, 0, 0);
        fontPanel.Controls.Add(loadFontButton, 1, 0);
        fontPanel.Controls.Add(defaultFontButton, 2, 0);
        AddRow("Font (Google Fonts)", fontPanel);

        var fontStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(ScaleByDpi(480), 0),
            Text = previewFont is not null
                ? $"Using \"{previewFont.Family}\" from Google Fonts."
                : (string.IsNullOrWhiteSpace(draft.CustomFontUrl)
                    ? "Paste a link to a font on fonts.google.com (or a fonts.googleapis.com stylesheet link), then click Load font."
                    : "The font is loaded when you click Load font or Save."),
        };
        AddRow(null, fontStatusLabel);

        void UpdateRowVisibility()
        {
            themeColorLabel.Visible = themeColorButton.Visible = theme == AppTheme.Custom;
            gradientLabel.Visible = gradientPanel.Visible = theme == AppTheme.Gradient;
            customTextColorLabel.Visible = customTextColorButton.Visible = textMode == TextColorMode.Custom;
        }

        themeComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (themeComboBox.SelectedItem is not OptionItem<AppTheme> option)
            {
                return;
            }

            theme = option.Value;
            UpdateRowVisibility();
            // Choosing "Custom colour" opens the colour picker straight away.
            if (theme == AppTheme.Custom && !initializing && PickColor(owner, themeColor, out Color picked))
            {
                themeColor = picked;
                ShowSwatchColor(themeColorButton, picked);
            }

            RefreshPreview();
        };

        textColorComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (textColorComboBox.SelectedItem is not OptionItem<TextColorMode> option)
            {
                return;
            }

            textMode = option.Value;
            UpdateRowVisibility();
            if (textMode == TextColorMode.Custom && !initializing && PickColor(owner, customTextColor, out Color picked))
            {
                customTextColor = picked;
                ShowSwatchColor(customTextColorButton, picked);
            }

            RefreshPreview();
        };

        // The preview shows the theme, text colour and font as they will look (Rainbow included).
        var previewTimer = new System.Windows.Forms.Timer { Interval = 500 };
        previewTimer.Tick += (_, _) =>
        {
            if (textMode == TextColorMode.Rainbow)
            {
                previewRainbow = CreateRandomColor();
                RefreshPreview();
            }
        };
        previewTimer.Start();
        owner.FormClosed += (_, _) => previewTimer.Dispose();

        preview.Paint += (_, e) =>
        {
            var sample = new AppSettings
            {
                Theme = theme,
                CustomThemeColor = PhantomColorParser.ToHex(themeColor),
                GradientStartColor = PhantomColorParser.ToHex(gradientStart),
                GradientEndColor = PhantomColorParser.ToHex(gradientEnd),
                GradientDirection = gradientDirection,
            };
            ThemePalette palette = BuildThemePalette(sample);
            Rectangle bounds = preview.ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            int menuHeight = bounds.Height / 2;
            var menuBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, menuHeight);
            var bodyBounds = new Rectangle(bounds.X, bounds.Y + menuHeight, bounds.Width, bounds.Height - menuHeight);
            if (palette.Gradient is ThemeGradient gradient)
            {
                using var brush = new LinearGradientBrush(bounds, gradient.Start, gradient.End, gradient.Mode);
                e.Graphics.FillRectangle(brush, bounds);
            }
            else
            {
                using var menuBrush = new SolidBrush(palette.MenuBack);
                using var bodyBrush = new SolidBrush(palette.AppBack);
                e.Graphics.FillRectangle(menuBrush, menuBounds);
                e.Graphics.FillRectangle(bodyBrush, bodyBounds);
            }

            Color textColor = textMode switch
            {
                TextColorMode.White => Color.White,
                TextColorMode.Black => Color.Black,
                TextColorMode.Custom => customTextColor,
                TextColorMode.Rainbow => previewRainbow,
                _ => palette.Text,
            };

            Font titleFont = previewFont is not null ? GetGoogleFont(previewFont, 10, FontStyle.Bold) : _menuTitleFont;
            Font bodyFont = previewFont is not null ? GetGoogleFont(previewFont, 9, FontStyle.Regular) : _stripButtonFont;
            TextRenderer.DrawText(e.Graphics, $"PhantomHaze    {DateTime.Now:T}", titleFont, Rectangle.Inflate(menuBounds, -ScaleByDpi(10), 0), textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, "The quick brown fox jumps over the lazy dog.", bodyFont, Rectangle.Inflate(bodyBounds, -ScaleByDpi(10), 0), textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };

        loadFontButton.Click += async (_, _) =>
        {
            if (!GoogleFontLoader.TryNormalizeFontUrl(fontTextBox.Text, out string cssUrl, out string error))
            {
                fontStatusLabel.Text = error;
                return;
            }

            loadFontButton.Enabled = false;
            fontStatusLabel.Text = "Loading the font from Google Fonts…";
            try
            {
                LoadedGoogleFont font = await LoadGoogleFontAsync(cssUrl, CancellationToken.None);
                if (owner.IsDisposed)
                {
                    return;
                }

                previewFont = font;
                fontStatusLabel.Text = $"Loaded \"{font.Family}\". Click Save to use it for PhantomHaze.";
                RefreshPreview();
            }
            catch (Exception ex)
            {
                if (!owner.IsDisposed)
                {
                    fontStatusLabel.Text = $"The font could not be loaded: {ex.Message}";
                }
            }
            finally
            {
                if (!loadFontButton.IsDisposed)
                {
                    loadFontButton.Enabled = true;
                }
            }
        };

        defaultFontButton.Click += (_, _) =>
        {
            fontTextBox.Text = string.Empty;
            previewFont = null;
            fontStatusLabel.Text = "PhantomHaze will use its default font (Segoe UI) after you click Save.";
            RefreshPreview();
        };

        UpdateRowVisibility();
        initializing = false;
        page.Controls.Add(layout);

        return new SettingsTab(page)
        {
            Validate = () =>
            {
                string text = fontTextBox.Text.Trim();
                if (text.Length == 0 || GoogleFontLoader.TryNormalizeFontUrl(text, out _, out _))
                {
                    return null;
                }

                GoogleFontLoader.TryNormalizeFontUrl(text, out _, out string error);
                return $"The font link on the Appearance tab cannot be used:\n{error}";
            },
            ApplyTo = settings =>
            {
                settings.Theme = theme;
                settings.CustomThemeColor = PhantomColorParser.ToHex(themeColor);
                settings.GradientStartColor = PhantomColorParser.ToHex(gradientStart);
                settings.GradientEndColor = PhantomColorParser.ToHex(gradientEnd);
                settings.GradientDirection = gradientDirection;
                settings.TextColorMode = textMode;
                settings.CustomTextColor = PhantomColorParser.ToHex(customTextColor);
                string fontText = fontTextBox.Text.Trim();
                settings.CustomFontUrl = fontText.Length > 0 && GoogleFontLoader.TryNormalizeFontUrl(fontText, out string cssUrl, out _)
                    ? cssUrl
                    : string.Empty;
            },
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        // After the controls are gone, nothing can draw with these fonts any more.
        foreach (Font font in new[]
                 {
                     _tabCloseFontRegular, _tabCloseFontCompact, _menuButtonFontRegular, _menuButtonFontBold, _menuTitleFont,
                     _tabTitleFont, _stripButtonFont, _chromeIconFont, _chromeRefreshFont, _favoriteButtonFont,
                     _zoomLabelFont, _newTabButtonFont, _panelTitleFont, _listFont, _loadingFont, _logFont,
                 })
        {
            font.Dispose();
        }

        foreach (RoundControlState state in _roundControls.Values)
        {
            state.Region?.Dispose();
        }

        _roundControls.Clear();

        foreach (Font font in _googleFontCache.Values.Concat(_systemFontCache.Values))
        {
            font.Dispose();
        }

        foreach (LoadedGoogleFont font in _loadedGoogleFonts.Values)
        {
            font.Collection.Dispose();
        }
    }
}

/// <summary>A double-buffered panel whose background can be painted by the theme (gradient themes).</summary>
internal sealed class ThemedPanel : Panel
{
    public ThemedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    /// <summary>Paints the background instead of the plain BackColor when it returns true.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Control, PaintEventArgs, bool>? BackgroundPainter { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (BackgroundPainter?.Invoke(this, e) == true)
        {
            return;
        }

        base.OnPaintBackground(e);
    }
}
