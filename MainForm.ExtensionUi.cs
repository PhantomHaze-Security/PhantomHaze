using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

// What extensions add to and change in the PhantomHaze window: top-bar buttons and dropdowns (app.button,
// menu.button, menu.dropdown), pages shown like Logs and Web View (app.page), and changes to built-in controls
// (ui.element, ui.svg, ui.shape). The built-in elements cover the whole window - the top bar, the address row, the
// tab strip and tabs, and the log, history, find and loading panels - except the protection status and button and
// the update banner, which extensions must never be able to change (see PhantomUiTargets).
public partial class MainForm
{
    private enum ExtensionMenuEntryKind
    {
        Page,
        Button,
        Dropdown,
    }

    /// <summary>What a button in the extension area of the menu bar stands for (kept in Button.Tag).</summary>
    private sealed record ExtensionMenuEntry(
        LoadedExtension Extension,
        string ElementId,
        string Label,
        ExtensionMenuEntryKind Kind,
        PhantomExtensionMenuItemDefinition? Item,
        PhantomExtensionPageDefinition? Page);

    private sealed class ExtensionPageState
    {
        public ExtensionPageState(string key, LoadedExtension extension, PhantomExtensionPageDefinition page)
        {
            Key = key;
            Extension = extension;
            Page = page;
        }

        public string Key { get; }
        public LoadedExtension Extension { get; set; }
        public PhantomExtensionPageDefinition Page { get; set; }
        public WebView2? WebView { get; set; }
        public RichTextBox? FallbackBox { get; set; }
        public string? RenderedContent { get; set; }
        public bool Initializing { get; set; }
        public Control? Host => (Control?)WebView ?? FallbackBox;
    }

    /// <summary>A built-in control extensions may customise, with what it looks like when nothing customises it.</summary>
    private sealed class UiTargetBinding
    {
        public UiTargetBinding(string name, Control control, PhantomUiCapabilities capabilities)
        {
            Name = name;
            Control = control;
            Capabilities = capabilities;
        }

        public string Name { get; }
        public Control Control { get; }
        public PhantomUiCapabilities Capabilities { get; }

        /// <summary>The normal text; null where the text changes by itself (the clock, the zoom label, the star).</summary>
        public Func<string>? DefaultText { get; init; }

        public Func<Font>? DefaultFont { get; init; }
        public Func<Image?>? DefaultImage { get; init; }

        /// <summary>The hover label of a control that normally shows only an icon (null: none).</summary>
        public string? DefaultTooltip { get; init; }

        /// <summary>
        /// The font for a text label an extension gives a control whose normal text is an icon: its own font is a
        /// large symbol font, which is wrong for words.
        /// </summary>
        public Func<Font>? LabelFont { get; init; }

        /// <summary>
        /// Not part of the standard window (the logo, the name, the Go button): only shown while an extension shows
        /// it with "visible true" or customises it in any other way.
        /// </summary>
        public bool Optional { get; init; }
    }

    /// <summary>The combined customisations of one control (later extensions win, property by property).</summary>
    private sealed class UiOverride
    {
        public string? Text;
        public string? Tooltip;
        public bool? Visible;
        public Color? Background;
        public Color? Foreground;
        public Color? Border;
        public string? FontFamily;
        public float? FontSize;
        public PhantomUiFontStyle? FontStyle;
        public PhantomSvgDocument? Svg;
        public PhantomExtensionUiShapeDefinition? Shape;

        public bool HasFont => FontFamily is not null || FontSize is not null || FontStyle is not null;
    }

    /// <summary>The images and outlines currently applied to a control, so they can be reused, updated or removed.</summary>
    private sealed class AppliedUiVisual
    {
        public PhantomSvgDocument? Svg;
        public PhantomExtensionUiShapeDefinition? Shape;
        public Color ImageColor;
        public Size ImageSize;
        public Image? OwnedImage;
        public Region? OwnedRegion;
        public Size RegionSize;
        public FlatStyle? OriginalFlatStyle;
        public int OriginalBorderSize;
        public Size? OriginalMinimumSize;
        public Padding? OriginalPadding;
        public bool ShapeHooked;
    }

    private readonly ContextMenuStrip _extensionDropdownMenu = new();
    private readonly ToolTip _extensionUiToolTip = new();
    private readonly Dictionary<string, ExtensionPageState> _extensionPages = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeExtensionPageKey;
    private readonly Dictionary<string, UiTargetBinding> _uiTargetBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Control> _uiBoundControls = new();
    private readonly Dictionary<string, Button> _extensionElementButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Control, AppliedUiVisual> _appliedUiVisuals = new();
    private readonly HashSet<Control> _surfacesWithBackgroundOverride = new();

    // Controls that are currently hidden: by an extension (visible false), because they are optional and no
    // extension asks for them, or - for the containers of the top bar - because nothing in them is shown.
    private readonly HashSet<Control> _extensionHiddenControls = new();

    // Controls whose text, font or graphic an extension replaced (they may need more room than the standard design).
    private readonly HashSet<Control> _contentOverriddenControls = new();

    // Controls an extension gave a border colour, and the border sizes of the buttons that had no border before.
    private readonly HashSet<Control> _borderOverriddenControls = new();
    private readonly Dictionary<ButtonBase, int> _borderSizesBeforeOverride = new();

    // ui.element targets that style a group of controls rather than one: the tabs.
    private const string TabsTabTarget = "tabs.tab";
    private const string TabsActiveTarget = "tabs.active";
    private const string TabsCloseTarget = "tabs.close";
    private static readonly HashSet<string> UiGroupTargets = new(StringComparer.OrdinalIgnoreCase) { TabsTabTarget, TabsActiveTarget, TabsCloseTarget };
    private Dictionary<string, UiOverride> _uiGroupOverrides = new(StringComparer.OrdinalIgnoreCase);

    private bool _applyingUiElementState;

    /// <summary>The combined customisations of a group target (tabs.tab, tabs.active, tabs.close), if any.</summary>
    private UiOverride? GetUiGroupOverride(string target)
    {
        return _uiGroupOverrides.TryGetValue(target, out UiOverride? customization) ? customization : null;
    }

    private static string GetElementKey(string extensionId, string elementId) => $"{extensionId}\u001F{elementId}";

    private static string EscapeMenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private static void ClearMenuItems(ToolStrip menu)
    {
        foreach (ToolStripItem item in menu.Items.Cast<ToolStripItem>().ToArray())
        {
            item.Dispose();
        }

        menu.Items.Clear();
    }

    // ---- Menu bar -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the extension area of the menu bar: first the page buttons (they switch views, like Logs and Web View),
    /// then each extension's buttons and dropdowns in the order its file declares them.
    /// </summary>
    private void RebuildExtensionMenuButtons()
    {
        _extensionMenuButtonsPanel.SuspendLayout();
        try
        {
            // The overflow button is permanent; only the per-extension buttons are recreated.
            _extensionMenuButtonsPanel.Controls.Remove(_extensionMenuOverflowButton);
            foreach (Control control in _extensionMenuButtonsPanel.Controls.OfType<Control>().ToArray())
            {
                control.Dispose();
            }

            _extensionMenuButtonsPanel.Controls.Clear();
            _overflowedExtensionMenuButtons.Clear();
            _extensionElementButtons.Clear();

            List<LoadedExtension> extensions = GetEnabledExtensions().ToList();
            foreach (LoadedExtension extension in extensions)
            {
                foreach (PhantomExtensionPageDefinition page in extension.Definition.Pages)
                {
                    AddExtensionMenuButton(new ExtensionMenuEntry(extension, page.Id, page.Label, ExtensionMenuEntryKind.Page, null, page));
                }
            }

            foreach (LoadedExtension extension in extensions)
            {
                foreach (PhantomExtensionMenuItemDefinition item in extension.Definition.MenuItems)
                {
                    ExtensionMenuEntryKind kind = item.Kind == PhantomExtensionMenuItemKind.Dropdown
                        ? ExtensionMenuEntryKind.Dropdown
                        : ExtensionMenuEntryKind.Button;
                    AddExtensionMenuButton(new ExtensionMenuEntry(extension, item.Id, item.Label, kind, item, null));
                }
            }

            ConfigureMenuButton(_extensionMenuOverflowButton, "»");
            _extensionMenuOverflowButton.MinimumSize = new Size(ScaleByDpi(40), 0);
            _extensionMenuOverflowButton.Visible = false;
            _extensionMenuButtonsPanel.Controls.Add(_extensionMenuOverflowButton);
        }
        finally
        {
            _extensionMenuButtonsPanel.ResumeLayout(true);
        }

        ApplyThemeToDynamicExtensionControls();
        UpdateExtensionMenuOverflow();
    }

    private void AddExtensionMenuButton(ExtensionMenuEntry entry)
    {
        var button = new Button();
        ConfigureMenuButton(button, GetDefaultExtensionButtonText(entry));
        button.MinimumSize = new Size(ScaleByDpi(64), 0);
        // Labels come from extensions: show '&' literally instead of turning it into a mnemonic.
        button.UseMnemonic = false;
        button.AccessibleName = string.IsNullOrWhiteSpace(entry.Label) ? entry.ElementId : entry.Label;
        if (entry.Kind == ExtensionMenuEntryKind.Dropdown)
        {
            button.AccessibleRole = AccessibleRole.ButtonDropDown;
        }

        button.Tag = entry;
        button.Click += (_, _) => RunExtensionMenuEntry(entry, button);
        _extensionMenuButtonsPanel.Controls.Add(button);
        _extensionElementButtons[GetElementKey(entry.Extension.Definition.Id, entry.ElementId)] = button;
    }

    private static string GetDefaultExtensionButtonText(ExtensionMenuEntry entry)
    {
        return entry.Kind == ExtensionMenuEntryKind.Dropdown ? $"{entry.Label} ▾" : entry.Label;
    }

    private void RunExtensionMenuEntry(ExtensionMenuEntry entry, Button button)
    {
        switch (entry.Kind)
        {
            case ExtensionMenuEntryKind.Page when entry.Page is not null:
                _ = ShowExtensionPageAsync(entry.Extension, entry.Page);
                break;
            case ExtensionMenuEntryKind.Dropdown:
                ShowExtensionDropdown(entry, button);
                break;
            default:
                if (entry.Item?.Action is PhantomExtensionActionDefinition action)
                {
                    ExecuteExtensionAction(entry.Extension, entry.Label, action);
                }

                break;
        }
    }

    private void ShowExtensionDropdown(ExtensionMenuEntry entry, Control anchor)
    {
        ClearMenuItems(_extensionDropdownMenu);
        AddDropdownOptions(_extensionDropdownMenu.Items, entry);
        _extensionDropdownMenu.Show(anchor, new Point(0, anchor.Height));
    }

    private void AddDropdownOptions(ToolStripItemCollection items, ExtensionMenuEntry entry)
    {
        foreach (PhantomExtensionMenuOptionDefinition option in entry.Item?.Options ?? new List<PhantomExtensionMenuOptionDefinition>())
        {
            var item = new ToolStripMenuItem(EscapeMenuText(option.Label)) { AccessibleName = option.Label };
            if (option.Action.Kind == PhantomExtensionActionKind.Page)
            {
                item.Checked = IsActiveExtensionPage(entry.Extension, option.Action.Value);
            }

            item.Click += (_, _) => ExecuteExtensionAction(entry.Extension, option.Label, option.Action);
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new ToolStripMenuItem("(no options)") { Enabled = false });
        }
    }

    private void ShowExtensionMenuOverflow()
    {
        ClearMenuItems(_extensionMenuOverflowMenu);
        foreach (Button hiddenButton in _overflowedExtensionMenuButtons)
        {
            if (hiddenButton.IsDisposed || hiddenButton.Tag is not ExtensionMenuEntry entry)
            {
                continue;
            }

            // '&' is doubled so menu items show extension labels literally, like the toolbar buttons do.
            var item = new ToolStripMenuItem(EscapeMenuText(entry.Label)) { AccessibleName = hiddenButton.AccessibleName };
            switch (entry.Kind)
            {
                case ExtensionMenuEntryKind.Dropdown:
                    AddDropdownOptions(item.DropDownItems, entry);
                    // A submenu is its own top-level window, so it needs the same capture protection.
                    ProtectPopupMenuFromCapture(item.DropDown);
                    break;
                case ExtensionMenuEntryKind.Page when entry.Page is not null:
                    item.Checked = IsActiveExtensionPage(entry);
                    item.Click += (_, _) => _ = ShowExtensionPageAsync(entry.Extension, entry.Page);
                    break;
                default:
                    if (entry.Item?.Action is PhantomExtensionActionDefinition action)
                    {
                        item.Click += (_, _) => ExecuteExtensionAction(entry.Extension, entry.Label, action);
                    }

                    break;
            }

            _extensionMenuOverflowMenu.Items.Add(item);
        }

        if (_extensionMenuOverflowMenu.Items.Count > 0)
        {
            _extensionMenuOverflowMenu.Show(_extensionMenuOverflowButton, new Point(0, _extensionMenuOverflowButton.Height));
        }
    }

    /// <summary>Runs what an extension button or dropdown option does.</summary>
    private void ExecuteExtensionAction(LoadedExtension extension, string label, PhantomExtensionActionDefinition action)
    {
        // A menu opened before Settings were saved can still offer an extension that has since been turned off.
        if (!IsExtensionStillLoadedAndActive(extension))
        {
            return;
        }

        string name = extension.Definition.Name;
        switch (action.Kind)
        {
            case PhantomExtensionActionKind.Message:
            {
                string message = ResolveExtensionPlaceholders(action.Value, extension);
                Log($"[{NormalizeForLog(name)}] {NormalizeForLog(label)}: {NormalizeForLog(message)}");

                // The message can contain the user's setting values, so the dialog is protected like the app's windows.
                ShowCaptureProtectedDialog(() => MessageBox.Show(this, message, name, MessageBoxButtons.OK, MessageBoxIcon.Information));
                break;
            }
            case PhantomExtensionActionKind.Url:
            {
                // Setting values in the address are percent-encoded, so they cannot change which site is opened.
                string address = ResolveExtensionPlaceholders(action.Value, extension, PhantomExtensionPlaceholderContext.UrlComponent);
                if (!TryNormalizeUrl(address, out string url))
                {
                    Log($"[{NormalizeForLog(name)}] {NormalizeForLog(label)}: '{TrimForLog(address, 200)}' is not a valid http(s) address.");
                    return;
                }

                _ = OpenUrlInNewTabAsync(url, $"Extension {NormalizeForLog(name)} link");
                break;
            }
            case PhantomExtensionActionKind.Page:
                if (extension.Definition.FindPage(action.Value) is PhantomExtensionPageDefinition page)
                {
                    _ = ShowExtensionPageAsync(extension, page);
                }

                break;
            case PhantomExtensionActionKind.Window:
                OpenExtensionUiWindow(extension);
                break;
            case PhantomExtensionActionKind.View:
                _ = SetWebViewModeAsync(enableWebView: action.Value == "web");
                break;
            case PhantomExtensionActionKind.Log:
                Log($"[{NormalizeForLog(name)}] {NormalizeForLog(ResolveExtensionPlaceholders(action.Value, extension))}");
                break;
        }
    }

    // ---- Extension pages ----------------------------------------------------------------------------------------

    private static string GetExtensionPageKey(string extensionId, string pageId) => $"{extensionId}\u001F{pageId}";

    private bool IsActiveExtensionPage(ExtensionMenuEntry entry) => IsActiveExtensionPage(entry.Extension, entry.ElementId);

    private bool IsActiveExtensionPage(LoadedExtension extension, string pageId)
    {
        return _activeExtensionPageKey is not null &&
               string.Equals(_activeExtensionPageKey, GetExtensionPageKey(extension.Definition.Id, pageId), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shows an extension page in the main window, in place of the log panel or Web View. The page keeps its state
    /// while other views are shown. Like extension windows, it only ever shows the extension's own document: web links
    /// open in a protected tab, and web.block rules apply to everything it loads.
    /// </summary>
    private async Task ShowExtensionPageAsync(LoadedExtension extension, PhantomExtensionPageDefinition page)
    {
        if (_webViewModeSwitchInProgress || !IsExtensionStillLoadedAndActive(extension))
        {
            return;
        }

        string key = GetExtensionPageKey(extension.Definition.Id, page.Id);
        ExtensionPageState state;
        _webViewModeSwitchInProgress = true;
        try
        {
            if (_webViewModeEnabled)
            {
                LeaveWebViewMode();
            }

            if (!_extensionPages.TryGetValue(key, out ExtensionPageState? existing))
            {
                existing = new ExtensionPageState(key, extension, page);
                _extensionPages[key] = existing;
            }

            state = existing;
            state.Extension = extension;
            state.Page = page;

            bool alreadyShown = string.Equals(_activeExtensionPageKey, key, StringComparison.OrdinalIgnoreCase);
            _activeExtensionPageKey = key;
            _logBox.Visible = false;
            UpdateExtensionPageVisibility();
            _extensionPageHostPanel.Visible = true;
            _extensionPageHostPanel.BringToFront();
            PositionExtensionLauncherPanel();
            UpdateWebViewToggleVisual();
            if (!alreadyShown)
            {
                Log($"Extension page opened: {NormalizeForLog(extension.Definition.Name)} - {NormalizeForLog(page.Label)}");
            }
        }
        finally
        {
            _webViewModeSwitchInProgress = false;
        }

        // The view has switched already; loading the page's content must not block switching away again.
        await EnsureExtensionPageViewAsync(state);
    }

    private void UpdateExtensionPageVisibility()
    {
        foreach (ExtensionPageState state in _extensionPages.Values)
        {
            Control? host = state.Host;
            if (host is null || host.IsDisposed)
            {
                continue;
            }

            bool active = string.Equals(state.Key, _activeExtensionPageKey, StringComparison.OrdinalIgnoreCase);
            host.Visible = active;
            if (active)
            {
                host.BringToFront();
            }
        }
    }

    /// <summary>Hides the extension page view (the page keeps its state) without deciding what is shown instead.</summary>
    private void HideExtensionPageView()
    {
        _activeExtensionPageKey = null;
        _extensionPageHostPanel.Visible = false;
    }

    private async Task EnsureExtensionPageViewAsync(ExtensionPageState state)
    {
        if (state.Host is not null)
        {
            RenderExtensionPageSafely(state);
            return;
        }

        if (state.Initializing)
        {
            return;
        }

        state.Initializing = true;
        var webView = new WebView2 { Dock = DockStyle.Fill };
        state.WebView = webView;
        _extensionPageHostPanel.Controls.Add(webView);
        UpdateExtensionPageVisibility();
        try
        {
            CoreWebView2Environment environment = _webEnvironment ??
                await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _webSessionPath,
                    options: new CoreWebView2EnvironmentOptions("--inprivate"));

            // The page (or the whole app) can be closed while WebView2 is still starting up.
            if (IsDisposed || webView.IsDisposed)
            {
                return;
            }

            await webView.EnsureCoreWebView2Async(environment);
            if (IsDisposed || webView.IsDisposed || webView.CoreWebView2 is null)
            {
                return;
            }

            ConfigureExtensionSurfaceCore(webView.CoreWebView2);
            RenderExtensionPageSafely(state);
        }
        catch (Exception ex)
        {
            if (IsDisposed || webView.IsDisposed)
            {
                return;
            }

            Log($"Extension page '{NormalizeForLog(state.Page.Label)}' could not be shown with WebView2: {ex.Message}");
            ShowExtensionPageFallback(state);
        }
        finally
        {
            state.Initializing = false;
        }
    }

    private void ShowExtensionPageFallback(ExtensionPageState state)
    {
        if (state.WebView is not null)
        {
            _extensionPageHostPanel.Controls.Remove(state.WebView);
            state.WebView.Dispose();
            state.WebView = null;
        }

        var fallbackBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
        };

        state.FallbackBox = fallbackBox;
        state.RenderedContent = null;
        _extensionPageHostPanel.Controls.Add(fallbackBox);
        RegisterOriginalFonts(fallbackBox);
        ApplyAppFontToTree(fallbackBox);
        UpdateExtensionPageVisibility();
        RenderExtensionPageSafely(state);
    }

    private void RenderExtensionPageSafely(ExtensionPageState state)
    {
        try
        {
            RenderExtensionPage(state);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log($"Extension page '{NormalizeForLog(state.Page.Label)}' could not be rendered: {ex.Message}");
        }
    }

    /// <summary>Renders a page from its current extension; reloads only when the content actually changed.</summary>
    private void RenderExtensionPage(ExtensionPageState state)
    {
        if (state.FallbackBox is { IsDisposed: false } fallbackBox)
        {
            fallbackBox.BackColor = _palette.FallbackBack;
            fallbackBox.ForeColor = _palette.FallbackText;
            string text = $"{state.Page.Label} ({state.Extension.Definition.Name})\n\n" +
                          "This page could not be displayed with WebView2.\n\n" +
                          ResolveExtensionPlaceholders(state.Page.Html, state.Extension);
            if (!string.Equals(text, state.RenderedContent, StringComparison.Ordinal))
            {
                fallbackBox.Text = text;
                state.RenderedContent = text;
            }

            return;
        }

        CoreWebView2? core = state.WebView is { IsDisposed: false } webView ? webView.CoreWebView2 : null;
        if (core is null)
        {
            // Still starting up: EnsureExtensionPageViewAsync renders the latest state once WebView2 is ready.
            return;
        }

        string html = BuildExtensionHtmlDocument(state.Extension, state.Page.Html, $"{state.Page.Label} - {state.Extension.Definition.Name}");
        if (string.Equals(html, state.RenderedContent, StringComparison.Ordinal))
        {
            return;
        }

        core.NavigateToString(html);
        state.RenderedContent = html;
    }

    /// <summary>
    /// Runs after extensions were reloaded (Settings saved, settings imported, theme or font changed): pages of
    /// extensions that were disabled or uninstalled - or that no longer have that page - are closed (showing the log
    /// panel if one of them was open); the others are re-rendered with the new settings, source and theme.
    /// </summary>
    private void RefreshExtensionPages()
    {
        foreach ((string key, ExtensionPageState state) in _extensionPages.ToArray())
        {
            PhantomExtensionPageDefinition? page = null;
            if (_loadedExtensionById.TryGetValue(state.Extension.Definition.Id, out LoadedExtension? current) && IsExtensionActive(current))
            {
                page = current.Definition.FindPage(state.Page.Id);
            }

            if (current is null || page is null)
            {
                bool wasShown = string.Equals(key, _activeExtensionPageKey, StringComparison.OrdinalIgnoreCase);
                DisposeExtensionPage(state);
                _extensionPages.Remove(key);
                if (wasShown)
                {
                    HideExtensionPageView();
                    if (!_webViewModeEnabled)
                    {
                        ShowLogPanel();
                    }
                }

                continue;
            }

            state.Extension = current;
            state.Page = page;
            RenderExtensionPageSafely(state);
        }
    }

    private void DisposeExtensionPage(ExtensionPageState state)
    {
        try
        {
            if (state.WebView is { IsDisposed: false } webView)
            {
                if (webView.CoreWebView2 is CoreWebView2 core)
                {
                    core.NewWindowRequested -= OnExtensionWindowNewWindowRequested;
                    core.NavigationStarting -= OnExtensionWindowNavigationStarting;
                    core.WebResourceRequested -= OnExtensionWindowWebResourceRequested;
                }

                _extensionPageHostPanel.Controls.Remove(webView);
                webView.Dispose();
            }

            if (state.FallbackBox is { IsDisposed: false } fallbackBox)
            {
                _extensionPageHostPanel.Controls.Remove(fallbackBox);
                fallbackBox.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // Best effort: the page's browser process may already be gone.
        }

        state.WebView = null;
        state.FallbackBox = null;
    }

    /// <summary>
    /// Settings shared by extension windows and pages: they only ever show the extension's own document. Links that
    /// would navigate it to a web page open in a protected tab instead, and web.block rules apply to everything it loads.
    /// </summary>
    private void ConfigureExtensionSurfaceCore(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NewWindowRequested += OnExtensionWindowNewWindowRequested;
        core.NavigationStarting += OnExtensionWindowNavigationStarting;
        core.WebResourceRequested += OnExtensionWindowWebResourceRequested;
        AddWebResourceRequestedFilterForAllFrames(core);
    }

    // ---- ui.element / ui.svg / ui.shape -------------------------------------------------------------------------

    private void BuildUiTargetBindings()
    {
        void Bind(
            string name,
            Control control,
            Func<string>? text = null,
            Func<Font>? font = null,
            Func<Image?>? image = null,
            string? tooltip = null,
            Func<Font>? labelFont = null,
            bool optional = false)
        {
            PhantomUiCapabilities capabilities = PhantomUiTargets.TryGetBuiltIn(name, out PhantomUiTargetInfo info)
                ? info.Capabilities
                : PhantomUiCapabilities.None;
            _uiTargetBindings[name] = new UiTargetBinding(name, control, capabilities)
            {
                DefaultText = text,
                DefaultFont = font,
                DefaultImage = image,
                DefaultTooltip = tooltip,
                LabelFont = labelFont,
                Optional = optional,
            };

            if (font is not null)
            {
                _uiBoundControls.Add(control);
            }
        }

        _favoriteButton.AccessibleName = "Favourite";
        _newTabButton.AccessibleName = "New tab";

        // The font of a word an extension puts on an icon-only button.
        Font LabelFont() => MapFont(_stripButtonFont);

        // The top bar. Icon buttons keep their symbol font whatever the app font is: a downloaded font rarely has
        // these symbols.
        Bind("menu.bar", _menuBarPanel);
        Bind("menu.logo", _menuLogoPictureBox, image: () => _defaultLogoImage, optional: true);
        Bind("menu.title", _menuLogoLabel, text: () => "PhantomHaze", font: () => MapFont(_menuTitleFont), optional: true);
        Bind("menu.logs", _logsViewButton, text: () => "Log", font: () => MapFont(IsLogsViewSelected ? _menuButtonFontBold : _menuButtonFontRegular));
        Bind("menu.webview", _webViewToggleButton, text: () => "Web", font: () => MapFont(_webViewModeEnabled ? _menuButtonFontBold : _menuButtonFontRegular));
        Bind("menu.clock", _menuClockLabel, font: () => MapFont(_menuButtonFontRegular));
        Bind("menu.settings", _settingsButton, text: () => SettingsGlyph, font: () => _chromeIconFont, tooltip: "Settings", labelFont: LabelFont);

        // The address row. The address box and the find box inherit the window's font; MapFont(Font) is that same
        // font unless an app font is in use.
        Bind("address.panel", _addressBarPanel);
        Bind("address.bar", _addressBarTextBox, font: () => MapFont(Font));
        Bind("address.favorite", _favoriteButton, font: () => MapFont(_favoriteButtonFont), tooltip: "Add or remove this page as a favourite");
        Bind("address.back", _backButton, text: () => BackGlyph, font: () => _chromeIconFont, tooltip: "Back", labelFont: LabelFont);
        Bind("address.forward", _forwardButton, text: () => ForwardGlyph, font: () => _chromeIconFont, tooltip: "Forward", labelFont: LabelFont);
        Bind("address.go", _goButton, text: () => "Go", font: () => MapFont(_stripButtonFont), optional: true);
        Bind("address.refresh", _refreshButton, text: () => RefreshGlyph, font: () => _chromeRefreshFont, tooltip: "Refresh", labelFont: LabelFont);
        Bind("address.history", _historyButton, text: () => "Favorites", font: () => MapFont(_stripButtonFont), tooltip: "Favourites and history");
        Bind("address.zoom", _zoomLabel, font: () => MapFont(_zoomLabelFont));
        Bind("address.zoomin", _zoomInButton, text: () => "+", font: () => MapFont(_stripButtonFont), tooltip: "Zoom in");
        Bind("address.zoomout", _zoomOutButton, text: () => "−", font: () => MapFont(_stripButtonFont), tooltip: "Zoom out");

        // The tab strip. The tabs themselves (tabs.tab, tabs.active, tabs.close) are styled in UpdateTabHeaderVisuals.
        Bind("tabs.bar", _tabsHostPanel);
        Bind("tabs.new", _newTabButton, text: () => "+", font: () => MapFont(_newTabButtonFont), tooltip: "New tab");

        // The content area.
        Bind("log.panel", _logBox, font: () => MapFont(_logFont));
        Bind("history.panel", _historyPanel);
        Bind("history.title", _historyTitleLabel, text: GetHistoryTitleText, font: () => MapFont(_panelTitleFont));
        Bind("history.list", _historyListBox, font: () => MapFont(_listFont));
        Bind("history.clear", _historyClearButton, text: () => "Clear History", font: () => MapFont(Font));
        Bind("favorites.title", _favoritesTitleLabel, text: () => "Favourites", font: () => MapFont(_panelTitleFont));
        Bind("favorites.list", _favoritesListBox, font: () => MapFont(_listFont));
        Bind("find.panel", _findPanel);
        Bind("find.box", _findTextBox, font: () => MapFont(Font));
        Bind("find.count", _findCountLabel, font: () => MapFont(_stripButtonFont));
        Bind("find.close", _findCloseButton, text: () => "X", font: () => MapFont(Font));
        Bind("loading.panel", _webViewLoadingOverlayPanel);
        Bind("loading.label", _webViewLoadingLabel, text: () => LoadingLabelText, font: () => MapFont(_loadingFont));
    }

    /// <summary>
    /// Brings every customisable control to its current state: the normal text, font, image and outline, with the
    /// active extensions' ui.element / ui.svg / ui.shape changes on top. Because everything is recomputed from the
    /// defaults each time, turning an extension off restores the controls it changed. Colours come from the theme
    /// code that runs just before this.
    /// </summary>
    private void ApplyUiElementState()
    {
        if (_uiTargetBindings.Count == 0 || _applyingUiElementState || IsDisposed)
        {
            return;
        }

        _applyingUiElementState = true;
        try
        {
            Dictionary<Control, UiOverride> overrides = CollectUiOverrides(out Dictionary<string, UiOverride> groupOverrides);
            var previousBackgroundOverrides = new HashSet<Control>(_surfacesWithBackgroundOverride);
            var previousBorderOverrides = new HashSet<Control>(_borderOverriddenControls);
            _surfacesWithBackgroundOverride.Clear();
            _borderOverriddenControls.Clear();
            _contentOverriddenControls.Clear();

            // The Refresh button sits inside the address box and shares its colours. They are set before the
            // elements are processed, so the button's own customisations (and the colour of its SVG) land on top.
            overrides.TryGetValue(_addressBarTextBox, out UiOverride? addressBox);
            _refreshButton.BackColor = addressBox?.Background ?? _addressBarTextBox.BackColor;
            _refreshButton.ForeColor = addressBox?.Foreground ?? _addressBarTextBox.ForeColor;

            foreach (UiTargetBinding binding in _uiTargetBindings.Values)
            {
                overrides.TryGetValue(binding.Control, out UiOverride? customization);
                ApplyBuiltInElementState(binding, customization);
            }

            foreach (Button button in _extensionMenuButtonsPanel.Controls.OfType<Button>().ToList())
            {
                if (button.Tag is ExtensionMenuEntry entry)
                {
                    overrides.TryGetValue(button, out UiOverride? customization);
                    ApplyExtensionElementState(button, entry, customization);
                }
            }

            ApplyCompositeElementColors(overrides);
            RestoreBorderSizesWithoutOverride();

            // What extensions hide, show or relabel changes the columns of the top bar, and whether a round
            // button is a circle or a pill. It also restores the round outline after a ui.shape was removed.
            UpdateChromeMetrics();

            // The outline of a round control is drawn by PhantomHaze: repaint where one appeared or disappeared.
            previousBorderOverrides.SymmetricExceptWith(_borderOverriddenControls);
            foreach (Control control in previousBorderOverrides)
            {
                if (!control.IsDisposed)
                {
                    control.Invalidate();
                }
            }

            // Gradient surfaces repaint when a solid background override starts or stops covering them.
            if (!previousBackgroundOverrides.SetEquals(_surfacesWithBackgroundOverride))
            {
                foreach (Control surface in previousBackgroundOverrides.Union(_surfacesWithBackgroundOverride))
                {
                    surface.Invalidate(true);
                }
            }

            // The tabs are restyled when extensions customise them, and once more when they stop doing so.
            bool tabsWereCustomized = _uiGroupOverrides.Count > 0;
            _uiGroupOverrides = groupOverrides;
            if (tabsWereCustomized || groupOverrides.Count > 0)
            {
                UpdateTabLayout();
            }
        }
        finally
        {
            _applyingUiElementState = false;
        }

        // Text, images and fonts change button widths, so the overflow (») menu is recomputed.
        UpdateExtensionMenuOverflow();
    }

    /// <summary>
    /// Colours that belong to more than one control: the frame of the top bar follows menu.bar, the pill and the
    /// outline of the address box follow address.bar, and those of the zoom control follow address.zoom. The
    /// protection status and button keep the theme's colours whatever an extension sets.
    /// </summary>
    private void ApplyCompositeElementColors(Dictionary<Control, UiOverride> overrides)
    {
        overrides.TryGetValue(_menuBarPanel, out UiOverride? menuBar);
        _chromePanel.BackColor = menuBar?.Background ?? _palette.MenuBack;
        if (menuBar?.Background is not null)
        {
            _surfacesWithBackgroundOverride.Add(_chromePanel);
        }

        // The address box: the pill around the text box has the text box's colour, and its outline is the border.
        overrides.TryGetValue(_addressBarTextBox, out UiOverride? addressBox);
        if (_addressInputLayout is not null)
        {
            _addressInputLayout.BackColor = _addressBarTextBox.BackColor;
            if (_addressOutlineOverride != addressBox?.Border)
            {
                _addressOutlineOverride = addressBox?.Border;
                _addressInputLayout.Invalidate();
            }
        }

        // The zoom control: the percentage stays transparent on the pill, which takes its background.
        overrides.TryGetValue(_zoomLabel, out UiOverride? zoom);
        _zoomLabel.BackColor = Color.Transparent;
        if (_zoomLayout is not null)
        {
            if (zoom?.Background is Color zoomBack)
            {
                _zoomLayout.BackColor = zoomBack;
            }

            if (_zoomOutlineOverride != zoom?.Border)
            {
                _zoomOutlineOverride = zoom?.Border;
                _zoomLayout.Invalidate();
            }
        }
    }

    /// <summary>Buttons that have no border of their own got one for an extension's border colour; it goes with the colour.</summary>
    private void RestoreBorderSizesWithoutOverride()
    {
        foreach ((ButtonBase button, int borderSize) in _borderSizesBeforeOverride.ToArray())
        {
            if (_borderOverriddenControls.Contains(button))
            {
                continue;
            }

            _borderSizesBeforeOverride.Remove(button);
            if (!button.IsDisposed && !(_appliedUiVisuals.TryGetValue(button, out AppliedUiVisual? applied) && applied.Shape is not null))
            {
                button.FlatAppearance.BorderSize = borderSize;
            }
        }
    }

    /// <param name="groupOverrides">The customisations of the targets that style several controls (the tabs), by target name.</param>
    private Dictionary<Control, UiOverride> CollectUiOverrides(out Dictionary<string, UiOverride> groupOverrides)
    {
        var overrides = new Dictionary<Control, UiOverride>();
        var groups = new Dictionary<string, UiOverride>(StringComparer.OrdinalIgnoreCase);
        groupOverrides = groups;
        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            PhantomExtensionDefinition definition = extension.Definition;
            if (!definition.HasUiCustomizations)
            {
                continue;
            }

            UiOverride? For(string target)
            {
                // The same rule as the parser: a name means the built-in element, unless it is one of the newer
                // names and this extension has a control of its own with that id.
                bool hasOwnControl = _extensionElementButtons.TryGetValue(GetElementKey(definition.Id, target), out Button? own);
                Control? control;
                if (!PhantomUiTargets.ResolvesToBuiltIn(target, hasOwnControl, out _))
                {
                    control = own;
                }
                else if (UiGroupTargets.Contains(target))
                {
                    if (!groups.TryGetValue(target, out UiOverride? group))
                    {
                        group = new UiOverride();
                        groups[target] = group;
                    }

                    return group;
                }
                else
                {
                    control = _uiTargetBindings.TryGetValue(target, out UiTargetBinding? binding) ? binding.Control : null;
                }

                if (control is null || control.IsDisposed)
                {
                    return null;
                }

                if (!overrides.TryGetValue(control, out UiOverride? customization))
                {
                    customization = new UiOverride();
                    overrides[control] = customization;
                }

                return customization;
            }

            foreach (PhantomExtensionUiEditDefinition edit in definition.UiEdits)
            {
                if (For(edit.Target) is not UiOverride customization)
                {
                    continue;
                }

                switch (edit.Property)
                {
                    case PhantomUiProperty.Text:
                        customization.Text = ResolveExtensionPlaceholders(edit.Text, extension);
                        break;
                    case PhantomUiProperty.Tooltip:
                        customization.Tooltip = ResolveExtensionPlaceholders(edit.Text, extension);
                        break;
                    case PhantomUiProperty.Visible:
                        customization.Visible = edit.Flag;
                        break;
                    case PhantomUiProperty.Background:
                        customization.Background = edit.Color;
                        break;
                    case PhantomUiProperty.Foreground:
                        customization.Foreground = edit.Color;
                        break;
                    case PhantomUiProperty.Border:
                        customization.Border = edit.Color;
                        break;
                    case PhantomUiProperty.Font:
                        customization.FontFamily = edit.Text;
                        break;
                    case PhantomUiProperty.FontSize:
                        customization.FontSize = edit.Number;
                        break;
                    case PhantomUiProperty.FontStyle:
                        customization.FontStyle = edit.FontStyle;
                        break;
                }
            }

            foreach (PhantomExtensionUiSvgDefinition svg in definition.UiSvgs)
            {
                if (For(svg.Target) is UiOverride customization)
                {
                    customization.Svg = svg.Document;
                }
            }

            foreach (PhantomExtensionUiShapeDefinition shape in definition.UiShapes)
            {
                if (For(shape.Target) is UiOverride customization)
                {
                    customization.Shape = shape;
                }
            }
        }

        return overrides;
    }

    private void ApplyBuiltInElementState(UiTargetBinding binding, UiOverride? customization)
    {
        Control control = binding.Control;
        if (control.IsDisposed)
        {
            return;
        }

        if (binding.DefaultText is not null)
        {
            string text = customization?.Text ?? (customization?.Svg is not null ? string.Empty : binding.DefaultText());
            if (!string.Equals(control.Text, text, StringComparison.Ordinal))
            {
                control.Text = text;
            }
        }
        else if (customization?.Svg is not null && control is ButtonBase && control.Text.Length > 0)
        {
            // The star of the favourite button: its SVG replaces the glyph.
            control.Text = string.Empty;
        }

        // Only elements that may be hidden are touched, and only when they are hidden or shown again:
        // Control.Visible reports false for every control in a hidden panel (the address bar in the Logs view).
        if ((binding.Capabilities & PhantomUiCapabilities.Visible) != 0)
        {
            // An optional element is shown once an extension does anything with it, unless that is hiding it.
            bool visible = binding.Optional
                ? customization is not null && (customization.Visible ?? true)
                : customization?.Visible ?? true;
            if (!visible && _extensionHiddenControls.Add(control))
            {
                control.Visible = false;
            }
            else if (visible && _extensionHiddenControls.Remove(control))
            {
                control.Visible = true;
            }
        }

        // A control that shows only an icon is labelled when hovered. An extension's own tooltip replaces that
        // label (an empty one removes it).
        string? tooltip = customization?.Tooltip;
        if (tooltip is null && customization?.Text is null)
        {
            tooltip = binding.DefaultTooltip ??
                      (customization?.Svg is not null ? binding.DefaultText?.Invoke() ?? control.AccessibleName : null);
        }

        SetExtensionToolTip(control, tooltip);

        if (binding.DefaultFont is not null)
        {
            // A word on a button that normally shows a symbol is written in the normal text font.
            Font baseFont = customization?.Text is not null && binding.LabelFont is not null
                ? binding.LabelFont()
                : binding.DefaultFont();
            Font font = customization?.HasFont == true
                ? GetOverrideFont(baseFont, customization.FontFamily, customization.FontSize, customization.FontStyle)
                : baseFont;
            if (!ReferenceEquals(control.Font, font))
            {
                control.Font = font;
            }
        }

        ApplyColorOverrides(control, customization);
        ApplyGraphicOverrides(control, customization, binding.DefaultImage);

        if (customization is not null && (customization.Text is not null || customization.HasFont || customization.Svg is not null))
        {
            _contentOverriddenControls.Add(control);
        }
    }

    private void ApplyExtensionElementState(Button button, ExtensionMenuEntry entry, UiOverride? customization)
    {
        // Page buttons are bold while their page is shown, like Logs and Web View.
        Font baseFont = entry.Kind == ExtensionMenuEntryKind.Page
            ? MapFont(IsActiveExtensionPage(entry) ? _menuButtonFontBold : _menuButtonFontRegular)
            : MapFont(Font);
        Font font = customization?.HasFont == true
            ? GetOverrideFont(baseFont, customization.FontFamily, customization.FontSize, customization.FontStyle)
            : baseFont;
        if (!ReferenceEquals(button.Font, font))
        {
            button.Font = font;
        }

        if (customization is null && !_appliedUiVisuals.ContainsKey(button))
        {
            return;
        }

        string text = customization?.Text ?? (customization?.Svg is not null ? string.Empty : GetDefaultExtensionButtonText(entry));
        if (!string.Equals(button.Text, text, StringComparison.Ordinal))
        {
            button.Text = text;
        }

        string? tooltip = customization?.Tooltip;
        if (tooltip is null && customization?.Svg is not null && customization.Text is null)
        {
            tooltip = entry.Label;
        }

        SetExtensionToolTip(button, tooltip);
        ApplyColorOverrides(button, customization);
        ApplyGraphicOverrides(button, customization, defaultImage: null);
    }

    private void SetExtensionToolTip(Control control, string? tooltip)
    {
        string current = _extensionUiToolTip.GetToolTip(control) ?? string.Empty;
        if (!string.Equals(current, tooltip ?? string.Empty, StringComparison.Ordinal))
        {
            _extensionUiToolTip.SetToolTip(control, string.IsNullOrEmpty(tooltip) ? null : tooltip);
        }
    }

    private void ApplyColorOverrides(Control control, UiOverride? customization)
    {
        if (customization is null)
        {
            return;
        }

        if (customization.Background is Color background)
        {
            // The zoom percentage is transparent on the zoom control, which takes the colour instead
            // (ApplyCompositeElementColors).
            if (!ReferenceEquals(control, _zoomLabel))
            {
                control.BackColor = background;
            }

            if (control is ThemedPanel)
            {
                _surfacesWithBackgroundOverride.Add(control);
            }
        }
        else if (customization.Shape is PhantomExtensionUiShapeDefinition shape && control is not PictureBox)
        {
            // The shape's fill is the control's background (an image control has the shape drawn into its image).
            control.BackColor = shape.Fill;
        }

        if (customization.Foreground is Color foreground)
        {
            control.ForeColor = foreground;
        }

        if (customization.Border is Color border && control is ButtonBase button)
        {
            button.FlatAppearance.BorderColor = border;
            _borderOverriddenControls.Add(button);

            // Round controls draw the colour along their outline (OnRoundControlPaint) and shaped ones along their
            // shape; any other button needs a border for the colour to show.
            if (customization.Shape is null && !_roundControls.ContainsKey(button) && button.FlatAppearance.BorderSize == 0)
            {
                _borderSizesBeforeOverride.TryAdd(button, 0);
                button.FlatAppearance.BorderSize = 1;
            }
        }
    }

    private AppliedUiVisual GetOrCreateAppliedVisual(Control control)
    {
        if (_appliedUiVisuals.TryGetValue(control, out AppliedUiVisual? applied))
        {
            return applied;
        }

        applied = new AppliedUiVisual();
        _appliedUiVisuals[control] = applied;
        control.Disposed += (_, _) =>
        {
            if (_appliedUiVisuals.Remove(control, out AppliedUiVisual? removed))
            {
                removed.OwnedImage?.Dispose();
                removed.OwnedRegion?.Dispose();
            }
        };
        return applied;
    }

    private void ApplyGraphicOverrides(Control control, UiOverride? customization, Func<Image?>? defaultImage)
    {
        PhantomSvgDocument? svg = customization?.Svg;
        PhantomExtensionUiShapeDefinition? shape = customization?.Shape;
        _appliedUiVisuals.TryGetValue(control, out AppliedUiVisual? existing);
        if (svg is null && shape is null && existing is null)
        {
            return;
        }

        AppliedUiVisual applied = existing ?? GetOrCreateAppliedVisual(control);
        try
        {
            if (control is PictureBox picture)
            {
                ApplyPictureGraphic(picture, applied, svg, shape, defaultImage);
                return;
            }

            ApplySvgImage(control, applied, svg);
            ApplyShape(control, applied, shape);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log($"An extension's custom graphic could not be drawn: {ex.Message}");
        }
    }

    /// <summary>The logo shows the shape and/or SVG drawn into its image.</summary>
    private void ApplyPictureGraphic(PictureBox picture, AppliedUiVisual applied, PhantomSvgDocument? svg, PhantomExtensionUiShapeDefinition? shape, Func<Image?>? defaultImage)
    {
        if (svg is null && shape is null)
        {
            if (applied.OwnedImage is not null)
            {
                picture.Image = defaultImage?.Invoke();
                applied.OwnedImage.Dispose();
                applied.OwnedImage = null;
            }

            applied.Svg = null;
            applied.Shape = null;
            return;
        }

        Size size = picture.ClientSize.Width > 0 && picture.ClientSize.Height > 0 ? picture.ClientSize : picture.Size;
        Color color = picture.ForeColor;
        bool upToDate = applied.OwnedImage is not null &&
                        ReferenceEquals(applied.Svg, svg) &&
                        ReferenceEquals(applied.Shape, shape) &&
                        applied.ImageSize == size &&
                        (svg is null || !svg.UsesCurrentColor || applied.ImageColor == color);
        if (upToDate)
        {
            return;
        }

        Bitmap image = RenderElementGraphic(size, svg, shape, color);
        Image? previous = applied.OwnedImage;
        picture.Image = image;
        previous?.Dispose();
        applied.OwnedImage = image;
        applied.Svg = svg;
        applied.Shape = shape;
        applied.ImageSize = size;
        applied.ImageColor = color;
    }

    private Bitmap RenderElementGraphic(Size size, PhantomSvgDocument? svg, PhantomExtensionUiShapeDefinition? shape, Color currentColor)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var full = new RectangleF(0, 0, bitmap.Width, bitmap.Height);
            if (shape is not null)
            {
                float outlineWidth = shape.Outline is null ? 0f : Math.Max(1f, ScaleByDpi(1) * 1.5f);
                float inset = (outlineWidth / 2f) + 0.5f;
                using GraphicsPath path = PhantomShapeGeometry.CreatePath(shape.Shape, RectangleF.Inflate(full, -inset, -inset));
                using (var brush = new SolidBrush(shape.Fill))
                {
                    graphics.FillPath(brush, path);
                }

                if (shape.Outline is Color outline)
                {
                    using var pen = new Pen(outline, outlineWidth);
                    graphics.DrawPath(pen, path);
                }
            }

            if (svg is not null)
            {
                // Inside a shape, the graphic keeps a margin so it stays within the outline.
                RectangleF area = shape is null ? full : RectangleF.Inflate(full, -full.Width * 0.2f, -full.Height * 0.2f);
                PhantomSvgRenderer.Draw(graphics, svg, area, currentColor);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>An SVG replaces a button's or label's text (or sits before it when the extension also sets text).</summary>
    private void ApplySvgImage(Control control, AppliedUiVisual applied, PhantomSvgDocument? svg)
    {
        if (svg is null)
        {
            if (applied.OwnedImage is null)
            {
                return;
            }

            SetControlImage(control, null);
            applied.OwnedImage.Dispose();
            applied.OwnedImage = null;
            applied.Svg = null;
            if (control is Label label)
            {
                label.MinimumSize = applied.OriginalMinimumSize ?? label.MinimumSize;
                label.Padding = applied.OriginalPadding ?? label.Padding;
                applied.OriginalMinimumSize = null;
                applied.OriginalPadding = null;
            }

            return;
        }

        int side = ScaleByDpi(control is Label ? 20 : 18);
        var size = new Size(side, side);
        Color color = control.ForeColor;
        if (applied.OwnedImage is null || !ReferenceEquals(applied.Svg, svg) || applied.ImageSize != size || (svg.UsesCurrentColor && applied.ImageColor != color))
        {
            Bitmap image = PhantomSvgRenderer.Render(svg, size, color);
            Image? previous = applied.OwnedImage;
            SetControlImage(control, image);
            previous?.Dispose();
            applied.OwnedImage = image;
            applied.Svg = svg;
            applied.ImageSize = size;
            applied.ImageColor = color;
        }
        else
        {
            // Keeps the image alignment right when the text was changed or removed.
            SetControlImage(control, applied.OwnedImage);
        }

        if (control is Label imageLabel && imageLabel.AutoSize)
        {
            // An auto-sized label measures only its text, so room is made for the image explicitly.
            applied.OriginalMinimumSize ??= imageLabel.MinimumSize;
            applied.OriginalPadding ??= imageLabel.Padding;
            Padding padding = applied.OriginalPadding.Value;
            bool hasText = imageLabel.Text.Length > 0;
            imageLabel.Padding = hasText ? new Padding(padding.Left + side + ScaleByDpi(4), padding.Top, padding.Right, padding.Bottom) : padding;
            imageLabel.MinimumSize = new Size(Math.Max(applied.OriginalMinimumSize.Value.Width, side + padding.Horizontal), Math.Max(applied.OriginalMinimumSize.Value.Height, side));
        }
    }

    private static void SetControlImage(Control control, Image? image)
    {
        switch (control)
        {
            case ButtonBase button:
                if (!ReferenceEquals(button.Image, image))
                {
                    button.Image = image;
                }

                button.ImageAlign = image is null || button.Text.Length == 0 ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
                button.TextImageRelation = image is null ? TextImageRelation.Overlay : TextImageRelation.ImageBeforeText;
                break;
            case Label label:
                if (!ReferenceEquals(label.Image, image))
                {
                    label.Image = image;
                }

                label.ImageAlign = image is null || label.Text.Length == 0 ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
                break;
        }
    }

    /// <summary>A shape gives a button or label that outline (the control is clipped to it), filled with the shape's colour.</summary>
    private void ApplyShape(Control control, AppliedUiVisual applied, PhantomExtensionUiShapeDefinition? shape)
    {
        if (shape is null)
        {
            if (applied.Shape is null)
            {
                return;
            }

            Region? region = control.Region;
            control.Region = null;
            if (region is not null && ReferenceEquals(region, applied.OwnedRegion))
            {
                region.Dispose();
            }

            applied.OwnedRegion = null;
            applied.Shape = null;
            if (control is ButtonBase shapedButton && applied.OriginalFlatStyle is FlatStyle originalStyle)
            {
                shapedButton.FlatStyle = originalStyle;
                shapedButton.FlatAppearance.BorderSize = applied.OriginalBorderSize;
                applied.OriginalFlatStyle = null;
            }

            control.Invalidate();
            return;
        }

        if (control is ButtonBase button)
        {
            if (applied.OriginalFlatStyle is null)
            {
                applied.OriginalFlatStyle = button.FlatStyle;
                applied.OriginalBorderSize = button.FlatAppearance.BorderSize;
            }

            // The rectangular border would be cut off by the outline; the outline colour is drawn along the shape instead.
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
        }

        bool changed = !ReferenceEquals(applied.Shape, shape);
        applied.Shape = shape;
        if (changed || applied.RegionSize != control.Size || control.Region is null)
        {
            UpdateShapeRegion(control, applied);
        }

        if (!applied.ShapeHooked)
        {
            applied.ShapeHooked = true;
            control.Resize += OnShapedControlResize;
            control.Paint += OnShapedControlPaint;
        }

        if (changed)
        {
            control.Invalidate();
        }
    }

    private void UpdateShapeRegion(Control control, AppliedUiVisual applied)
    {
        if (applied.Shape is null || control.Width <= 0 || control.Height <= 0)
        {
            return;
        }

        using GraphicsPath path = PhantomShapeGeometry.CreatePath(applied.Shape.Shape, new RectangleF(0, 0, control.Width, control.Height));
        var region = new Region(path);
        Region? previous = control.Region;
        control.Region = region;
        if (previous is not null && ReferenceEquals(previous, applied.OwnedRegion))
        {
            previous.Dispose();
        }

        applied.OwnedRegion = region;
        applied.RegionSize = control.Size;
    }

    private void OnShapedControlResize(object? sender, EventArgs e)
    {
        if (sender is Control control &&
            _appliedUiVisuals.TryGetValue(control, out AppliedUiVisual? applied) &&
            applied.Shape is not null &&
            applied.RegionSize != control.Size)
        {
            UpdateShapeRegion(control, applied);
        }
    }

    private void OnShapedControlPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not Control control ||
            !_appliedUiVisuals.TryGetValue(control, out AppliedUiVisual? applied) ||
            applied.Shape?.Outline is not Color outline ||
            control.Width <= 2 || control.Height <= 2)
        {
            return;
        }

        float width = Math.Max(1f, ScaleByDpi(1) * 1.5f);
        float inset = width / 2f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = PhantomShapeGeometry.CreatePath(applied.Shape.Shape, new RectangleF(inset, inset, control.Width - width, control.Height - width));
        using var pen = new Pen(outline, width);
        e.Graphics.DrawPath(pen, path);
    }
}
