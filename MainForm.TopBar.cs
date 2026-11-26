namespace PhantomHaze;

// The top bar's styles (Settings -> Appearance -> Top bar):
// - Comfortable: the full top bar, as in earlier versions (the default).
// - Comfy: the same controls, closer together and a little smaller.
// - Compact: only Logs, Web and extension buttons. A drop-down button at the right shows the rest (the protection
//   banner or strip, the Web View address and tab rows, the clock and Settings); its dot always shows whether protection
//   is on (green) or off (red).
// - Simple: a small ON/OFF protection switch next to Settings (its menu turns protection off), and in Web View only Back,
//   Forward, the address box and small tabs, together in one row.
// Controls a style leaves out are hidden together with extensions' ui.element changes: a control is shown only when
// neither hides it (ApplyCombinedVisibility).
public partial class MainForm
{
    /// <summary>How the top bar looks. Stored in settings.json by name.</summary>
    private enum TopBarStyle
    {
        /// <summary>The full top bar (the default).</summary>
        Comfortable,

        /// <summary>Only the Logs and Web View buttons and extension buttons; the rest opens from a drop-down button.</summary>
        Compact,

        /// <summary>Everything, closer together.</summary>
        Comfy,

        /// <summary>An ON/OFF protection switch and only the essential Web View controls.</summary>
        Simple,
    }

    /// <summary>The sizes of the top bar in one style, in logical pixels (they are scaled with ScaleByDpi).</summary>
    private sealed record TopBarMetrics
    {
        public int OuterMargin { get; init; }
        public int CornerRadius { get; init; }
        public int InnerPadding { get; init; }
        public int ButtonHeight { get; init; }
        public int MenuButtonMinWidth { get; init; }
        public int MenuButtonPaddingX { get; init; }
        public int MenuButtonPaddingY { get; init; }
        public int MenuButtonGap { get; init; }
        public int AddressRowHeight { get; init; }
        public int TabsRowHeight { get; init; }
        public int BannerWidth { get; init; }
        public int BannerHeight { get; init; }
        public int StripButtonWidth { get; init; }
        public int NarrowStripButtonWidth { get; init; }
        public int ZoomLabelWidth { get; init; }
        public int ZoomSliderWidth { get; init; }
        public int NarrowZoomSliderWidth { get; init; }
        public int TabHeight { get; init; }
        public int NarrowTabHeight { get; init; }
        public int TabMinWidth { get; init; }
        public int TabMaxWidth { get; init; }
        public int ProtectionStatusHeight { get; init; }
        public int ProtectionStripGap { get; init; }
        public int ProtectionButtonHeight { get; init; }
    }

    private static readonly TopBarMetrics ComfortableTopBar = new()
    {
        OuterMargin = 8,
        CornerRadius = 26,
        InnerPadding = 12,
        ButtonHeight = 36,
        MenuButtonMinWidth = 84,
        MenuButtonPaddingX = 16,
        MenuButtonPaddingY = 4,
        MenuButtonGap = 8,
        AddressRowHeight = 46,
        TabsRowHeight = 42,
        BannerWidth = 212,
        BannerHeight = 132,
        StripButtonWidth = 46,
        NarrowStripButtonWidth = 40,
        ZoomLabelWidth = 96,
        ZoomSliderWidth = 185,
        NarrowZoomSliderWidth = 135,
        TabHeight = 34,
        NarrowTabHeight = 30,
        TabMinWidth = 130,
        TabMaxWidth = 230,
        ProtectionStatusHeight = 34,
        ProtectionStripGap = 4,
        ProtectionButtonHeight = 36,
    };

    private static readonly TopBarMetrics ComfyTopBar = ComfortableTopBar with
    {
        OuterMargin = 5,
        CornerRadius = 20,
        InnerPadding = 7,
        ButtonHeight = 30,
        MenuButtonMinWidth = 62,
        MenuButtonPaddingX = 11,
        MenuButtonPaddingY = 2,
        MenuButtonGap = 5,
        AddressRowHeight = 38,
        TabsRowHeight = 34,
        BannerWidth = 184,
        BannerHeight = 100,
        StripButtonWidth = 38,
        NarrowStripButtonWidth = 34,
        ZoomLabelWidth = 86,
        ZoomSliderWidth = 150,
        NarrowZoomSliderWidth = 110,
        TabHeight = 28,
        NarrowTabHeight = 26,
        TabMinWidth = 116,
        TabMaxWidth = 200,
        ProtectionStatusHeight = 28,
        ProtectionStripGap = 3,
        ProtectionButtonHeight = 30,
    };

    // Simple puts the address box and the tabs in one row (TabsRowHeight 0); it has no protection banner or strip.
    private static readonly TopBarMetrics SimpleTopBar = ComfyTopBar with
    {
        OuterMargin = 4,
        CornerRadius = 16,
        InnerPadding = 6,
        ButtonHeight = 28,
        MenuButtonMinWidth = 54,
        MenuButtonPaddingX = 10,
        MenuButtonGap = 4,
        AddressRowHeight = 34,
        TabsRowHeight = 0,
        StripButtonWidth = 32,
        NarrowStripButtonWidth = 30,
        TabHeight = 24,
        NarrowTabHeight = 22,
        TabMinWidth = 84,
        TabMaxWidth = 150,
    };

    // The Simple style's protection switch and the Compact style's drop-down button (hidden in the other styles).
    private readonly ProtectionSwitch _protectionSwitch = new();
    private readonly TopBarRevealButton _topBarRevealButton = new();

    // Controls the current style leaves out (see ApplyCombinedVisibility).
    private readonly HashSet<Control> _topBarStyleHiddenControls = new();

    // Compact: the rest of the top bar is shown (the drop-down button was used). Never saved: every session starts compact.
    private bool _topBarRevealed;
    private TopBarStyle? _appliedTopBarStyle;

    private TopBarStyle CurrentTopBarStyle => Enum.IsDefined(_settings.TopBarStyle) ? _settings.TopBarStyle : TopBarStyle.Comfortable;

    private TopBarMetrics CurrentTopBarMetrics => CurrentTopBarStyle switch
    {
        TopBarStyle.Comfy or TopBarStyle.Compact => ComfyTopBar,
        TopBarStyle.Simple => SimpleTopBar,
        _ => ComfortableTopBar,
    };

    /// <summary>Compact, with only the menu row showing.</summary>
    private bool IsTopBarCollapsed => CurrentTopBarStyle == TopBarStyle.Compact && !_topBarRevealed;

    // The sizes the layout code uses (logical pixels), from the current style.
    private int HeaderOuterMargin => CurrentTopBarMetrics.OuterMargin;
    private int HeaderCornerRadius => CurrentTopBarMetrics.CornerRadius;
    private int HeaderInnerPadding => CurrentTopBarMetrics.InnerPadding;
    private int HeaderButtonHeight => CurrentTopBarMetrics.ButtonHeight;
    private int AddressRowHeight => CurrentTopBarMetrics.AddressRowHeight;
    private int TabsRowHeight => CurrentTopBarMetrics.TabsRowHeight;
    private int CompactBannerWidth => CurrentTopBarMetrics.BannerWidth;
    private int ProtectionStatusHeight => CurrentTopBarMetrics.ProtectionStatusHeight;
    private int ProtectionStripGap => CurrentTopBarMetrics.ProtectionStripGap;
    private int ProtectionButtonHeight => CurrentTopBarMetrics.ProtectionButtonHeight;

    /// <summary>Sets up the protection switch and the drop-down button (BuildMenuBar places them in the menu row).</summary>
    private void ConfigureTopBarStyleControls()
    {
        _protectionSwitch.Anchor = AnchorStyles.Left;
        _protectionSwitch.Visible = false;
        _protectionSwitch.ToggleRequested += (_, _) => OnProtectionButtonClick(_protectionSwitch);

        ConfigureMenuButton(_topBarRevealButton, "▾");
        // Like the Go and Refresh icons, the arrow keeps its symbol font (an app font may not have it).
        _topBarRevealButton.Font = _stripGlyphFont;
        _uiBoundControls.Add(_topBarRevealButton);
        _topBarRevealButton.UseMnemonic = false;
        _topBarRevealButton.AccessibleRole = AccessibleRole.ButtonDropDown;
        _topBarRevealButton.Visible = false;
        _topBarRevealButton.Click += (_, _) => SetTopBarRevealed(!_topBarRevealed);
    }

    /// <summary>
    /// Arranges the top bar for the chosen style: its sizes, which controls are shown, and the address and tab rows.
    /// Runs when the window is built and whenever Settings change the style (or Compact's drop-down button is used).
    /// </summary>
    private void ApplyTopBarStyle()
    {
        TopBarStyle style = CurrentTopBarStyle;
        bool firstTime = _appliedTopBarStyle is null;
        bool styleChanged = _appliedTopBarStyle != style;
        _appliedTopBarStyle = style;
        if (style != TopBarStyle.Compact)
        {
            _topBarRevealed = false;
        }

        bool compact = style == TopBarStyle.Compact;
        bool simple = style == TopBarStyle.Simple;
        bool collapsed = compact && !_topBarRevealed;

        SuspendLayout();
        _menuBarPanel.SuspendLayout();
        try
        {
            SetTopBarStyleHidden(_menuLogoPictureBox, compact);
            SetTopBarStyleHidden(_menuLogoLabel, compact || simple);
            SetTopBarStyleHidden(_menuClockLabel, collapsed || simple);
            SetTopBarStyleHidden(_usersButton, collapsed);
            SetTopBarStyleHidden(_settingsButton, collapsed);
            SetTopBarStyleHidden(_goButton, simple);
            SetTopBarStyleHidden(_refreshButton, simple);
            SetTopBarStyleHidden(_historyButton, simple);
            SetTopBarStyleHidden(_favoriteButton, simple);
            SetTopBarStyleHidden(_newTabButton, simple);
            SetTopBarStyleHidden(_zoomLabel, simple);
            SetTopBarStyleHidden(_zoomSlider, simple);
            _protectionSwitch.Visible = simple;
            _topBarRevealButton.Visible = compact;
            if (simple && _historyPanel.Visible)
            {
                // Simple has no History button; Ctrl+H still opens and closes the panel.
                ToggleHistoryPanel();
            }

            // A slider without tick marks fits the smaller tab rows.
            _zoomSlider.TickStyle = style == TopBarStyle.Comfortable ? TickStyle.BottomRight : TickStyle.None;
            _protectionBanner.PreferredHeightLogical = CurrentTopBarMetrics.BannerHeight;
            ArrangeAddressRows(simple);
            ApplyDpiAwareMetrics();
            UpdateResponsiveLayout();
            UpdateTopBarRevealButton();
        }
        finally
        {
            _menuBarPanel.ResumeLayout(true);
            ResumeLayout(true);
        }

        UpdateTabLayout();

        // Extension buttons are made with the style's sizes.
        if (styleChanged && !firstTime)
        {
            RefreshExtensionSurface();
        }

        _menuBarPanel.Invalidate(true);
    }

    /// <summary>
    /// Simple: the address strip and the tabs share one row (Back, Forward, the address box | the tabs). Other styles: the
    /// address strip above the tab row, each across the whole width.
    /// </summary>
    private void ArrangeAddressRows(bool simple)
    {
        if (_addressRootLayout is null || _addressStripLayout is null || _tabsLayout is null ||
            _addressRootLayout.ColumnStyles.Count < 2 || _addressRootLayout.RowStyles.Count < 2)
        {
            return;
        }

        _addressRootLayout.SuspendLayout();
        try
        {
            if (simple)
            {
                _addressRootLayout.ColumnStyles[0].SizeType = SizeType.Percent;
                _addressRootLayout.ColumnStyles[0].Width = 46f;
                _addressRootLayout.ColumnStyles[1].SizeType = SizeType.Percent;
                _addressRootLayout.ColumnStyles[1].Width = 54f;
                _addressRootLayout.RowStyles[1].SizeType = SizeType.Absolute;
                _addressRootLayout.RowStyles[1].Height = 0;
                _addressRootLayout.SetColumnSpan(_addressStripLayout, 1);
                _addressRootLayout.SetColumnSpan(_tabsLayout, 1);
                _addressRootLayout.SetCellPosition(_tabsLayout, new TableLayoutPanelCellPosition(1, 0));
                _tabsLayout.Margin = ScalePadding(6, 0, 0, 0);
            }
            else
            {
                _addressRootLayout.ColumnStyles[0].SizeType = SizeType.Percent;
                _addressRootLayout.ColumnStyles[0].Width = 100f;
                _addressRootLayout.ColumnStyles[1].SizeType = SizeType.Absolute;
                _addressRootLayout.ColumnStyles[1].Width = 0;
                _addressRootLayout.RowStyles[1].SizeType = SizeType.Percent;
                _addressRootLayout.RowStyles[1].Height = 100f;
                _addressRootLayout.SetCellPosition(_tabsLayout, new TableLayoutPanelCellPosition(0, 1));
                _addressRootLayout.SetColumnSpan(_addressStripLayout, 2);
                _addressRootLayout.SetColumnSpan(_tabsLayout, 2);
                _tabsLayout.Margin = new Padding(0);
            }
        }
        finally
        {
            _addressRootLayout.ResumeLayout(true);
        }
    }

    /// <summary>Hides (or shows again) a control the current style leaves out.</summary>
    private void SetTopBarStyleHidden(Control control, bool hidden)
    {
        bool changed = hidden ? _topBarStyleHiddenControls.Add(control) : _topBarStyleHiddenControls.Remove(control);
        if (changed)
        {
            ApplyCombinedVisibility(control);
        }
    }

    /// <summary>
    /// Shows a control unless the top bar's style or an extension (ui.element) hides it. Always assigned: Control.Visible
    /// reads false for every control in a hidden panel, so reading it cannot tell whether the control itself is hidden.
    /// </summary>
    private void ApplyCombinedVisibility(Control control)
    {
        control.Visible = !_topBarStyleHiddenControls.Contains(control) && !_extensionHiddenControls.Contains(control);
    }

    /// <summary>Compact: shows or hides the rest of the top bar.</summary>
    private void SetTopBarRevealed(bool revealed)
    {
        if (CurrentTopBarStyle != TopBarStyle.Compact)
        {
            revealed = false;
        }

        if (_topBarRevealed == revealed)
        {
            return;
        }

        bool focusWasInHiddenPart = !revealed && (_addressBarPanel.ContainsFocus || _protectionBanner.Focused || _toggleButton.Focused);
        _topBarRevealed = revealed;
        ApplyTopBarStyle();
        if (focusWasInHiddenPart)
        {
            _topBarRevealButton.Focus();
        }
    }

    /// <summary>The drop-down button's arrow, protection dot and description.</summary>
    private void UpdateTopBarRevealButton()
    {
        bool dark = IsDarkTheme();
        _topBarRevealButton.DotColor = _protectionEnabled
            ? (dark ? Color.FromArgb(22, 134, 77) : Color.FromArgb(0, 191, 99))
            : (dark ? Color.FromArgb(214, 58, 58) : Color.FromArgb(204, 0, 0));
        string arrow = _topBarRevealed ? "▴" : "▾";
        if (!string.Equals(_topBarRevealButton.Text, arrow, StringComparison.Ordinal))
        {
            _topBarRevealButton.Text = arrow;
        }

        string what = _webViewModeEnabled ? "the protection switch, the Web View controls and Settings" : "the protection status and switch, and Settings";
        string description = $"{(_topBarRevealed ? "Hide" : "Show")} {what} (protection is {(_protectionEnabled ? "on" : "OFF")})";
        _topBarRevealButton.AccessibleName = description;
        if (!string.Equals(_headerToolTip.GetToolTip(_topBarRevealButton), description, StringComparison.Ordinal))
        {
            _headerToolTip.SetToolTip(_topBarRevealButton, description);
        }
    }

    /// <summary>The sizes of the protection switch and the drop-down button for the current style (from ApplyDpiAwareMetrics).</summary>
    private void ApplyTopBarStyleControlMetrics()
    {
        int height = ScaleByDpi(HeaderButtonHeight);
        _protectionSwitch.Size = new Size(ScaleByDpi(74), Math.Min(height, ScaleByDpi(28)));
        _protectionSwitch.Margin = ScalePadding(4, 0, 6, 0);
        _topBarRevealButton.MinimumSize = new Size(ScaleByDpi(54), height);
        // The protection dot sits in the left padding.
        _topBarRevealButton.Padding = ScalePadding(22, 0, 10, 0);
        _topBarRevealButton.Margin = ScalePadding(CurrentTopBarMetrics.MenuButtonGap, 0, 0, 0);
    }

    /// <summary>
    /// Keyboard shortcuts, which also reach what a style leaves out: Ctrl+L, Alt+D or F6 (the address box), Ctrl+T (new
    /// tab), Ctrl+W or Ctrl+F4 (close the tab), F5 or Ctrl+R (reload) and Ctrl+H (history and favourites).
    /// </summary>
    private Action? GetBrowserShortcut(Keys keyData)
    {
        var tab = _activeTab;
        if (keyData == (Keys.Control | Keys.T)) return () => _ = OpenNewTabFromShortcutAsync();
        if (!_webViewModeEnabled) return null;
        return keyData switch
        {
            (Keys.Control | Keys.L) or (Keys.Alt | Keys.D) or Keys.F6 => FocusAddressBar,
            (Keys.Control | Keys.H) => ToggleHistoryPanel,
            (Keys.Control | Keys.W) or (Keys.Control | Keys.F4) when tab is not null => () => _ = CloseTabAsync(tab),
            Keys.F5 or (Keys.Control | Keys.R) when tab is not null => RefreshWebView,
            (Keys.Alt | Keys.Left) or Keys.BrowserBack when tab is not null => GoBack,
            (Keys.Alt | Keys.Right) or Keys.BrowserForward when tab is not null => GoForward,
            _ => null,
        };
    }

    /// <summary>Puts the cursor in the address box (Compact shows it first).</summary>
    private void FocusAddressBar()
    {
        if (IsTopBarCollapsed)
        {
            SetTopBarRevealed(true);
        }

        if (_addressBarTextBox.CanFocus)
        {
            _addressBarTextBox.Focus();
            _addressBarTextBox.SelectAll();
        }
    }

    private async Task OpenNewTabFromShortcutAsync()
    {
        if (!_webViewModeEnabled)
        {
            // Entering Web View opens the first tab when there is none.
            bool hadTabs = _tabs.Count > 0;
            await SetWebViewModeAsync(enableWebView: true);
            if (!hadTabs || !_webViewModeEnabled)
            {
                return;
            }
        }

        if (await CreateTabAsync(GetConfiguredStartPage(), activate: true) is not null)
        {
            FocusAddressBar();
        }
    }
}
