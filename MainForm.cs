using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

public partial class MainForm : Form
{
    private enum AppTheme
    {
        Light,
        Dark,

        /// <summary>A colour the user picked (Settings -> Appearance).</summary>
        Custom,

        /// <summary>A gradient between two colours the user picked.</summary>
        Gradient,
    }

    private enum SearchEngine
    {
        Google,
        Bing,
        Yahoo,
        DuckDuckGo,
        Startpage,
    }

    private sealed class AppSettings
    {
        public AppTheme Theme { get; set; } = AppTheme.Light;

        /// <summary>
        /// The setting of v1.3.5 and earlier ("Render web pages in the background"). It is now written as
        /// BackgroundTabRenderSeconds == -1, so an older PhantomHaze reading this file still understands it.
        /// </summary>
        public bool RenderWebViewInBackground { get; set; }

        /// <summary>
        /// How long a tab keeps rendering its page after it goes into the background, in seconds (-1: forever). Null in
        /// files written before v1.3.6, where RenderWebViewInBackground decides.
        /// </summary>
        public int? BackgroundTabRenderSeconds { get; set; }

        /// <summary>Resource saver: with more than 20 tabs open, background tabs stop rendering after 10 seconds.</summary>
        public bool ResourceSaverEnabled { get; set; } = true;

        /// <summary>The resource saver banner was closed with its X and is never shown again.</summary>
        public bool ResourceSaverBannerDismissed { get; set; }
        public bool PersistHistoryAcrossSessions { get; set; }
        public int HistoryRetentionDays { get; set; } = 1;

        /// <summary>Keeps WebView2 cookies between sessions (off by default, like history).</summary>
        public bool PersistCookiesAcrossSessions { get; set; }

        /// <summary>How long a saved cookie is kept after it was first saved, in days (-1: until it expires).</summary>
        public int CookieRetentionDays { get; set; } = 1;

        /// <summary>How Web View loads pages: a built-in protocol id ("webview2") or "extensionId/protocolId".</summary>
        public string WebViewProtocol { get; set; } = WebProtocolCatalog.WebView2Id;

        /// <summary>HTMLRenderer and HazeGate pages run their JavaScript (Settings -> General).</summary>
        public bool RenderedPageScriptsEnabled { get; set; } = true;
        public bool InspectHtml { get; set; }
        public bool ShowFavoritesBar { get; set; } = true;

        /// <summary>How the top bar looks (Settings -> Appearance -> Top bar).</summary>
        public TopBarStyle TopBarStyle { get; set; } = TopBarStyle.Comfortable;

        public SearchEngine DefaultSearchEngine { get; set; } = SearchEngine.Google;
        public string StartingPage { get; set; } = DefaultHomeUrl;
        public bool ExtensionsEnabled { get; set; } = true;
        public List<InstalledExtensionEntry> Extensions { get; set; } = new();

        // Appearance (stored as #RRGGBB so settings.json and .phstg exports stay readable).
        public string CustomThemeColor { get; set; } = DefaultCustomThemeColor;
        public string GradientStartColor { get; set; } = DefaultGradientStartColor;
        public string GradientEndColor { get; set; } = DefaultGradientEndColor;
        public ThemeGradientDirection GradientDirection { get; set; } = ThemeGradientDirection.Horizontal;
        public TextColorMode TextColorMode { get; set; } = TextColorMode.Automatic;
        public string CustomTextColor { get; set; } = DefaultCustomTextColor;

        /// <summary>The Google Fonts stylesheet the app font comes from ("" for the default font).</summary>
        public string CustomFontUrl { get; set; } = string.Empty;

        public AppSettings Clone()
        {
            return new AppSettings
            {
                Theme = Theme,
                RenderWebViewInBackground = RenderWebViewInBackground,
                BackgroundTabRenderSeconds = BackgroundTabRenderSeconds,
                ResourceSaverEnabled = ResourceSaverEnabled,
                ResourceSaverBannerDismissed = ResourceSaverBannerDismissed,
                PersistHistoryAcrossSessions = PersistHistoryAcrossSessions,
                HistoryRetentionDays = HistoryRetentionDays,
                PersistCookiesAcrossSessions = PersistCookiesAcrossSessions,
                CookieRetentionDays = CookieRetentionDays,
                WebViewProtocol = WebViewProtocol,
                RenderedPageScriptsEnabled = RenderedPageScriptsEnabled,
                InspectHtml = InspectHtml,
                ShowFavoritesBar = ShowFavoritesBar,
                TopBarStyle = TopBarStyle,
                DefaultSearchEngine = DefaultSearchEngine,
                StartingPage = StartingPage,
                ExtensionsEnabled = ExtensionsEnabled,
                Extensions = Extensions.Select(entry => entry.Clone()).ToList(),
                CustomThemeColor = CustomThemeColor,
                GradientStartColor = GradientStartColor,
                GradientEndColor = GradientEndColor,
                GradientDirection = GradientDirection,
                TextColorMode = TextColorMode,
                CustomTextColor = CustomTextColor,
                CustomFontUrl = CustomFontUrl,
            };
        }
    }

    private sealed class InstalledExtensionEntry
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        [JsonConverter(typeof(LenientSettingValuesConverter))]
        public Dictionary<string, string> SettingValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public InstalledExtensionEntry Clone()
        {
            return new InstalledExtensionEntry
            {
                Id = Id,
                DisplayName = DisplayName,
                FileName = FileName,
                Enabled = Enabled,
                SettingValues = new Dictionary<string, string>(SettingValues, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    /// <summary>
    /// Reads extension setting values written as JSON numbers or booleans (for example by hand-editing
    /// settings.json) instead of failing the whole settings file, which would reset every setting and orphan all
    /// installed extensions. Values are always written back as strings.
    /// </summary>
    private sealed class LenientSettingValuesConverter : JsonConverter<Dictionary<string, string>>
    {
        public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return values;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string key = reader.GetString() ?? string.Empty;
                reader.Read();
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        values[key] = reader.GetString() ?? string.Empty;
                        break;
                    case JsonTokenType.Number:
                        values[key] = reader.TryGetDecimal(out decimal number)
                            ? number.ToString(CultureInfo.InvariantCulture)
                            : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                        break;
                    case JsonTokenType.True:
                        values[key] = "true";
                        break;
                    case JsonTokenType.False:
                        values[key] = "false";
                        break;
                    default:
                        // null, objects and arrays: ignore the value so the declared default is used.
                        reader.Skip();
                        break;
                }
            }

            return values;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach ((string key, string item) in value)
            {
                writer.WriteString(key, item);
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>Identifies one version of settings.json, to notice writes made by another PhantomHaze window.</summary>
    private readonly record struct SettingsFileStamp(DateTime LastWriteUtc, long Length);

    private sealed class LoadedExtension
    {
        public LoadedExtension(InstalledExtensionEntry installation, PhantomExtensionDefinition definition, string sourcePath)
        {
            Installation = installation;
            Definition = definition;
            SourcePath = sourcePath;
        }

        public InstalledExtensionEntry Installation { get; }
        public PhantomExtensionDefinition Definition { get; }
        public string SourcePath { get; }
    }

    private sealed class ExtensionWindowState
    {
        public ExtensionWindowState(Form window, LoadedExtension extension)
        {
            Window = window;
            Extension = extension;
        }

        public Form Window { get; }
        public LoadedExtension Extension { get; set; }
        public WebView2? WebView { get; set; }
        public RichTextBox? FallbackBox { get; set; }
        public string? RenderedContent { get; set; }
    }

    private sealed class AuxiliaryWindowProtection
    {
        public AuxiliaryWindowProtection(string description)
        {
            Description = description;
        }

        public string Description { get; }
        public bool? AppliedState { get; set; }
    }

    private sealed class ExtensionListItem
    {
        public ExtensionListItem(InstalledExtensionEntry installation, PhantomExtensionDefinition? definition, string? loadError)
        {
            Installation = installation;
            Definition = definition;
            LoadError = loadError;
        }

        public InstalledExtensionEntry Installation { get; }
        public PhantomExtensionDefinition? Definition { get; }
        public string? LoadError { get; }

        public override string ToString()
        {
            string displayName = !string.IsNullOrWhiteSpace(Definition?.Name)
                ? Definition.Name
                : (!string.IsNullOrWhiteSpace(Installation.DisplayName) ? Installation.DisplayName : Installation.Id);

            if (!string.IsNullOrWhiteSpace(LoadError))
            {
                return $"{displayName} (load failed)";
            }

            string? version = Definition?.Version;
            return string.IsNullOrWhiteSpace(version)
                ? displayName
                : $"{displayName} v{version}";
        }
    }

    private sealed class HistoryEntry
    {
        public string Url { get; set; } = string.Empty;
        public DateTimeOffset VisitedAtUtc { get; set; }
    }

    private sealed class OptionItem<T>
    {
        public OptionItem(T value, string label)
        {
            Value = value;
            Label = label;
        }

        public T Value { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }

    private sealed class BrowserTab
    {
        public BrowserTab(
            int id,
            WebProtocol protocol,
            WebProtocolPage page,
            Panel headerPanel,
            PictureBox faviconBox,
            Label titleLabel,
            Button closeButton)
        {
            Id = id;
            Protocol = protocol;
            Page = page;
            HeaderPanel = headerPanel;
            FaviconBox = faviconBox;
            TitleLabel = titleLabel;
            CloseButton = closeButton;
            CurrentUrl = DefaultHomeUrl;
            Title = "New Tab";
            ZoomPercent = 100;
        }

        public int Id { get; }

        /// <summary>The Web View protocol the tab's page was made with.</summary>
        public WebProtocol Protocol { get; set; }

        /// <summary>What shows the tab's web page; replaced when the Web View protocol changes.</summary>
        public WebProtocolPage Page { get; set; }
        public WebProtocolPage? OriginalPage { get; set; }
        public WebProtocolPage Browser => OriginalPage ?? Page;
        public string? InspectorDraft { get; set; }

        public Panel HeaderPanel { get; }
        public PictureBox FaviconBox { get; }
        public Label TitleLabel { get; }
        public Button CloseButton { get; }
        public string CurrentUrl { get; set; }
        public string Title { get; set; }
        public Image? OwnedFaviconImage { get; set; }
        public int ZoomPercent { get; set; }
        public bool IsNavigationPending { get; set; }

        /// <summary>The tab's close button was pressed and the tab is waiting for its cookies to be saved.</summary>
        public bool IsClosing { get; set; }

        /// <summary>When the tab went into the background (null while it is shown); see ApplyBackgroundTabRendering.</summary>
        public DateTime? BackgroundSinceUtc { get; set; }

        /// <summary>The page was told to stop rendering because it was in the background for too long.</summary>
        public bool RenderingSuspended { get; set; }

        /// <summary>The page was suspended and has just been shown again, so it may take a moment to draw.</summary>
        public bool ResumedFromSuspension { get; set; }

        // Incremented on every NavigationStarting so async work started for one document can detect that the
        // tab has moved on before touching the new document.
        public int NavigationSequence { get; set; }
        public int FaviconSequence { get; set; } = -1;
    }

    // The rounded top bar ("header"): the menu row, the Web View address and tab rows and the protection banner.
    private readonly ThemedPanel _menuBarPanel = new();
    private readonly TableLayoutPanel _headerLayout = new();
    private readonly TableLayoutPanel _headerLeftLayout = new();
    private readonly PictureBox _menuLogoPictureBox = new();
    private readonly Label _menuLogoLabel = new();
    private readonly PillButton _logsViewButton = new();
    private readonly FlowLayoutPanel _extensionMenuButtonsPanel = new();
    private readonly Label _menuClockLabel = new();
    // Ticks once per second, aligned to the system clock. The clock is never written to the log.
    private readonly System.Windows.Forms.Timer _clockTimer = new();
    private readonly TableLayoutPanel _updateBannerPanel = new();
    private readonly Label _updateBannerTitleLabel = new();
    private readonly Label _updateBannerMessageLabel = new();
    private readonly PillButton _updateBannerUpdateButton = new();
    private readonly Button _updateBannerSnoozeButton = new();
    private readonly ContextMenuStrip _updateBannerSnoozeMenu = new();
    private readonly ToolTip _updateBannerToolTip = new();
    // Cancelled when the window closes, so a slow update check never touches a closed window.
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    // The protection status and switch of Web View. Deliberately not a ui.element target (see PhantomUiTargets).
    private readonly ProtectionBanner _protectionBanner = new();
    // The Logs view and extension pages: the protection status across the window under the top bar, with the protection
    // button below it (as in earlier versions). Not ui.element targets either.
    private readonly ThemedPanel _protectionStripPanel = new();
    private readonly ProtectionStatusBar _protectionStatusBar = new();
    private readonly PillButton _toggleButton = new();
    private readonly Font _protectionStatusFont = new("Segoe UI", 10, FontStyle.Bold);
    // The protection banner's tooltip (extension tooltips, and the Settings gear's, use _extensionUiToolTip).
    private readonly ToolTip _headerToolTip = new();
    private readonly PillButton _webViewToggleButton = new();
    private readonly PillButton _settingsButton = new();
    private readonly ContextMenuStrip _disableProtectionMenu = new();
    private readonly System.Windows.Forms.Timer _protectionRestoreTimer = new();
    private readonly ThemedPanel _addressBarPanel = new();
    private readonly PillPanel _addressPill = new();
    private readonly TextBox _addressBarTextBox = new();
    private readonly PillButton _favoriteButton = new();
    private readonly PillButton _backButton = new();
    private readonly PillButton _forwardButton = new();
    private readonly PillButton _goButton = new();
    private readonly PillButton _refreshButton = new();
    private readonly PillButton _historyButton = new();
    private readonly Label _zoomLabel = new();
    private readonly TrackBar _zoomSlider = new();
    private readonly FlowLayoutPanel _tabsFlowPanel = new();
    private readonly PillButton _newTabButton = new();
    private readonly ThemedPanel _contentHostPanel = new();
    private readonly FlowLayoutPanel _extensionLauncherPanel = new();
    private readonly ThemedPanel _webViewHostPanel = new();
    // Hosts the pages extensions add with app.page; shown instead of the log panel or Web View.
    private readonly ThemedPanel _extensionPageHostPanel = new();
    private readonly ThemedPanel _webViewLoadingOverlayPanel = new();
    private readonly Label _webViewLoadingLabel = new();
    private readonly ThemedPanel _historyPanel = new();
    private readonly ListBox _historyListBox = new();
    private readonly ListBox _favoritesListBox = new();
    private readonly Button _historyClearButton = new();
    private readonly Label _historyTitleLabel = new();
    private readonly Label _favoritesTitleLabel = new();
    private readonly Panel _findPanel = new();
    private readonly TextBox _findTextBox = new();
    private readonly Label _findCountLabel = new();
    private readonly Button _findCloseButton = new();
    private readonly TextBox _logBox = new();
    // Redirects are followed manually so every hop can be checked against extension web.block rules. No cookies: a
    // shared cookie jar would let sites recognise the user across tabs through their icons.
    private readonly HttpClient _faviconHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly Font _tabCloseFontRegular = new("Segoe UI", 8, FontStyle.Bold);
    private readonly Font _tabCloseFontCompact = new("Segoe UI", 6.5f, FontStyle.Bold);
    private readonly Font _menuButtonFontRegular = new("Segoe UI", 10, FontStyle.Regular);
    private readonly Font _menuButtonFontBold = new("Segoe UI", 10, FontStyle.Bold);
    // Shared fonts: these used to be created per control (one per tab opened, several per Settings window opened)
    // and were never disposed, leaking a GDI font handle each time.
    private readonly Font _menuTitleFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Font _tabTitleFont = new("Segoe UI", 8.5f, FontStyle.Regular);
    private readonly Font _stripButtonFont = new("Segoe UI", 9, FontStyle.Regular);
    private readonly Font _favoriteButtonFont = new("Segoe UI", 11, FontStyle.Bold);
    private readonly Font _zoomLabelFont = new("Segoe UI", 9, FontStyle.Regular);
    private readonly Font _newTabButtonFont = new("Segoe UI", 12, FontStyle.Bold);
    // The Settings gear; never replaced by the app font, which may not have the gear symbol.
    private readonly Font _settingsGlyphFont = new("Segoe UI Symbol", 13, FontStyle.Regular);
    // The Back and Forward arrows and the Go, Refresh and History icons; like the gear, never replaced by the app font
    // (which may not have these symbols).
    private readonly Font _navigationGlyphFont = new("Segoe UI", 12, FontStyle.Bold);
    private readonly Font _stripGlyphFont = new("Segoe UI Symbol", 12, FontStyle.Regular);
    private readonly Font _logFont = new("Consolas", 9);
    private readonly string _logDirectory;
    private readonly string _fontCacheDirectory;
    private readonly string _webSessionPath;
    // Held open for the whole session, so another PhantomHaze window can tell this session folder is in use.
    private FileStream? _webSessionLock;
    private readonly string _favoritesFilePath;
    private readonly string _historyFilePath;
    private readonly string _cookiesFilePath;
    private readonly string _settingsFilePath;
    private readonly string _extensionsRootPath;
    private readonly string _updateReminderFilePath;
    private readonly List<HistoryEntry> _historyEntries = new();
    private readonly List<Favorite> _favorites = new();
    private readonly List<BrowserTab> _tabs = new();
    private readonly List<LoadedExtension> _loadedExtensions = new();
    private readonly Dictionary<string, LoadedExtension> _loadedExtensionById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExtensionWindowState> _openExtensionWindowsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Form, AuxiliaryWindowProtection> _protectedAuxiliaryWindows = new();
    private readonly ToolTip _extensionLauncherToolTip = new();
    private readonly PillButton _extensionMenuOverflowButton = new();
    private readonly ContextMenuStrip _extensionMenuOverflowMenu = new();
    private readonly List<Button> _overflowedExtensionMenuButtons = new();
    // "extensionId/scriptId" keys already reported as not running, so a broken script is logged once per load.
    private readonly HashSet<string> _reportedNonRunningExtensionScripts = new(StringComparer.OrdinalIgnoreCase);
    // Handles of system dialogs (MessageBox, file picker) currently open through ShowCaptureProtectedDialog.
    private readonly HashSet<IntPtr> _openSystemDialogWindows = new();
    // settings.json as this window last read or wrote it (null: no file); see HasSettingsFileChangedSinceLastAccess.
    private SettingsFileStamp? _settingsFileStamp;
    // WebView2 tabs by their CoreWebView2, for the WebView2-only request handlers (web.block, cookie notices).
    private readonly Dictionary<CoreWebView2, BrowserTab> _tabByCore = new();
    private bool _workerRequestFilterUnavailable;
    private AppSettings _settings = new();
    private TableLayoutPanel? _addressRootLayout;
    private TableLayoutPanel? _addressStripLayout;
    private TableLayoutPanel? _addressInputLayout;
    private TableLayoutPanel? _tabsLayout;
    private TableLayoutPanel? _historyLayout;
    private TableLayoutPanel? _findLayout;
    private TableLayoutPanel? _menuLayout;
    private TableLayoutPanel? _menuLogoLayout;
    private Image? _defaultLogoImage;

    // The shared WebView2 environment, once a WebView2 tab has created it (extension windows and pages reuse it).
    private CoreWebView2Environment? _webEnvironment;
    private BrowserTab? _activeTab;
    private bool _protectionEnabled;
    // The last request to turn protection on did not succeed (Windows refused it).
    private bool _protectionEnableFailed;
    private bool _webViewModeEnabled;
    private bool _webViewModeSwitchInProgress;
    private bool _updatingExtensionMenuOverflow;
    private int _nextTabId = 1;
    private int _findRequestVersion;
    private int _webViewModeSwitchVersion;
    private DateTimeOffset? _protectionResumeAtUtc;

    // New cookies are counted a moment after a page load or Set-Cookie burst and logged as one summary line.
    private readonly System.Windows.Forms.Timer _cookieCheckTimer = new() { Interval = 1500 };
    private readonly HashSet<string> _knownCookieKeys = new(StringComparer.Ordinal);
    private BrowserTab? _cookieCheckTab;
    private bool _cookieCheckInProgress;

    // The zoom slider is logged once it settles, not for every step while it is dragged.
    private readonly System.Windows.Forms.Timer _zoomLogTimer = new() { Interval = 700 };
    private (int TabId, int Percent)? _pendingZoomLog;

    /// <summary>Marks tab close buttons, whose font is chosen by UpdateTabLayout rather than the app font walk.</summary>
    private static readonly object TabCloseButtonTag = new();

    private const string DefaultHomeUrl = "https://www.google.com";
    // The address row's buttons are icons (their names are their tooltips). Their widths depend on the top bar's
    // style (MainForm.TopBar.cs).
    private const string BackGlyph = "←";
    private const string ForwardGlyph = "→";
    private const string GoGlyph = "⏎";
    private const string RefreshGlyph = "⟳";
    private const string HistoryGlyph = "🕘";
    private const int MinimumAddressInputWidth = 180;
    private static readonly Image DefaultTabIcon = SystemIcons.Application.ToBitmap();
    private static readonly int[] ValidHistoryRetentionDays = new[] { 1, 7, 14, 30, 80, -1 };
    private const string ExtensionStagingFileSuffix = ".phext-staging";
    private const string ExtensionBackupFileSuffix = ".phext-backup";
    private static readonly HashSet<string> WindowsReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly PhantomUserStore _userStore;
    private readonly Guid _userId;
    private readonly FileStream? _userLease;
    internal PhantomUser? NextUser { get; private set; }
    internal string? LogForNextUser { get; private set; }

    internal MainForm(PhantomUserStore userStore, PhantomUser user, FileStream? lease = null, string? sessionLog = null)
    {
        _userStore = userStore;
        _userId = user.Id;
        _userLease = lease;
        _logBox.Text = sessionLog ?? string.Empty;
        // The version and release channel come from PhantomHaze.csproj (see AppVersionInfo).
        Text = AppVersionInfo.WindowTitle;
        Width = 980;
        Height = 620;
        MinimumSize = new Size(860, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 249, 253);
        KeyPreview = true;
        KeyDown += OnMainFormKeyDown;
        TrySetWindowIcon();

        _logDirectory = InitializeLogDirectory(userStore.Root);
        string dataDirectory = userStore.DirectoryFor(user.Id);
        _fontCacheDirectory = Path.Combine(dataDirectory, "fonts");
        _webSessionPath = InitializeWebSessionPath();
        _webSessionLock = LockWebSessionFolder(_webSessionPath);
        _favoritesFilePath = Path.Combine(dataDirectory, "favorites.json");
        _historyFilePath = Path.Combine(dataDirectory, "history.json");
        _cookiesFilePath = Path.Combine(dataDirectory, SavedCookiesFileName);
        _settingsFilePath = Path.Combine(dataDirectory, "settings.json");
        _extensionsRootPath = Directory.CreateDirectory(Path.Combine(dataDirectory, "extensions")).FullName;
        _updateReminderFilePath = Path.Combine(dataDirectory, "update-reminder.json");
        _faviconHttpClient.Timeout = TimeSpan.FromSeconds(5);
        // Favicons are downloaded for every page, so a site must not be able to make PhantomHaze buffer an
        // arbitrarily large response (the default limit is 2 GB). Real favicons are a few kilobytes.
        _faviconHttpClient.MaxResponseContentBufferSize = 1024 * 1024;
        _protectionRestoreTimer.Interval = 1000;
        _protectionRestoreTimer.Tick += OnProtectionRestoreTimerTick;
        _clockTimer.Interval = 1000;
        _clockTimer.Tick += (_, _) => UpdateClock();
        _rainbowTimer.Tick += OnRainbowTimerTick;
        _cookieCheckTimer.Tick += OnCookieCheckTimerTick;
        _zoomLogTimer.Tick += OnZoomLogTimerTick;

        LoadSettings();
        NormalizeSettings();

        BuildUi();
        CleanupStaleExtensionTransactionFiles();
        LoadInstalledExtensions();
        RefreshExtensionSurface();
        _addressBarTextBox.PlaceholderText = NormalizeStartPage(GetEffectiveStartPage());
        LoadFavorites();
        LoadHistory();
        if (!_settings.PersistCookiesAcrossSessions)
        {
            // Like history: nothing is kept on disk while saving is turned off.
            DeleteSavedCookiesFile();
        }

        Load += (_, _) =>
        {
            ApplyDpiAwareMetrics();
            UpdateClock();
            _clockTimer.Start();
            UpdateRainbowTimer();
            Log($"Logs folder: {_logDirectory}");
            Log($"Web session path: {_webSessionPath}");
            Log($"Favorites file: {_favoritesFilePath}");
            Log($"History file: {_historyFilePath}");
            Log($"Settings file: {_settingsFilePath}");
            Log($"Extensions root: {_extensionsRootPath}");
            LogTrueOsBuild();
            ApplyProtection(enable: true);
            LogSelectedWebViewProtocol();

            // Session folders an earlier PhantomHaze window could not delete when it closed (WebView2 was still
            // holding them) are removed now, in the background.
            _ = DeleteStaleWebSessionFoldersAsync();

            // Decides at every startup whether the update banner is shown; it runs in the background.
            _ = CheckForUpdatesAsync();

            // A custom font from Google Fonts loads from the local cache (downloading it only the first time).
            _ = ApplyCustomFontAsync();
        };

        FormClosing += OnMainFormClosing;
        Resize += (_, _) =>
        {
            UpdateTabLayout();
            UpdateResponsiveLayout();
            if (_palette.Gradient is not null)
            {
                // The gradient spans the whole window, so every surface changes when the window is resized.
                InvalidateThemeSurfaces();
            }
        };
        DpiChanged += (_, _) =>
        {
            ApplyDpiAwareMetrics();
            UpdateResponsiveLayout();
            UpdateTabLayout();
            RefreshExtensionSurface();
        };
    }

    private int ScaleByDpi(int logicalPixels)
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * (dpi / 96.0)));
    }

    private Padding ScalePadding(int left, int top, int right, int bottom)
    {
        return new Padding(
            ScaleByDpi(left),
            ScaleByDpi(top),
            ScaleByDpi(right),
            ScaleByDpi(bottom));
    }

    // The rounded top bar's sizes (the margin around it, its corner radius, the padding inside it, its rows) depend on
    // its style: see TopBarMetrics in MainForm.TopBar.cs.
    private int FavoritesBarHeight => _settings.ShowFavoritesBar && _favorites.Count > 0 ? ScaleByDpi(28) : 0;
    private int AddressPanelHeight => ScaleByDpi(AddressRowHeight + TabsRowHeight) + FavoritesBarHeight;

    /// <summary>The width of the ring around the pills extensions add to the top bar.</summary>
    private int ExtensionPillRingWidth => Math.Max(1, ScaleByDpi(2));

    private void ApplyDpiAwareMetrics()
    {
        MinimumSize = new Size(ScaleByDpi(860), ScaleByDpi(560));

        int outer = HeaderOuterMargin + HeaderInnerPadding;
        _menuBarPanel.MinimumSize = new Size(0, ScaleByDpi(HeaderButtonHeight + (outer * 2)));
        _menuBarPanel.Padding = ScalePadding(outer, outer - 2, outer, outer - 2);
        int menuLogoSize = ScaleByDpi(24);
        _menuLogoPictureBox.Size = new Size(menuLogoSize, menuLogoSize);
        _menuLogoPictureBox.Margin = ScalePadding(2, 2, 8, 2);
        _menuLogoLabel.Margin = ScalePadding(0, 0, 12, 0);
        _updateBannerPanel.Height = ScaleByDpi(36);
        _updateBannerPanel.Padding = ScalePadding(12, 3, 6, 3);
        _updateBannerTitleLabel.Margin = ScalePadding(0, 0, 4, 0);
        _updateBannerUpdateButton.MinimumSize = new Size(ScaleByDpi(76), ScaleByDpi(26));
        _updateBannerUpdateButton.Padding = ScalePadding(10, 0, 10, 0);
        _updateBannerUpdateButton.Margin = ScalePadding(8, 0, 6, 0);
        _updateBannerSnoozeButton.Size = new Size(ScaleByDpi(30), ScaleByDpi(26));
        ApplyResourceSaverBannerMetrics();
        TopBarMetrics metrics = CurrentTopBarMetrics;
        _logsViewButton.MinimumSize = new Size(ScaleByDpi(metrics.MenuButtonMinWidth), ScaleByDpi(HeaderButtonHeight));
        _webViewToggleButton.MinimumSize = new Size(ScaleByDpi(metrics.MenuButtonMinWidth), ScaleByDpi(HeaderButtonHeight));
        // The gear is a circle; a longer text set by an extension makes it a pill.
        _settingsButton.MinimumSize = new Size(ScaleByDpi(HeaderButtonHeight), ScaleByDpi(HeaderButtonHeight));
        Padding menuButtonPadding = ScalePadding(metrics.MenuButtonPaddingX, metrics.MenuButtonPaddingY, metrics.MenuButtonPaddingX, metrics.MenuButtonPaddingY);
        _logsViewButton.Padding = menuButtonPadding;
        _webViewToggleButton.Padding = menuButtonPadding;
        _settingsButton.Padding = ScalePadding(2, 0, 2, 0);
        _logsViewButton.Margin = ScalePadding(0, 0, metrics.MenuButtonGap, 0);
        _webViewToggleButton.Margin = ScalePadding(0, 0, metrics.MenuButtonGap, 0);
        _settingsButton.Margin = new Padding(0);
        ApplyTopBarStyleControlMetrics();
        _menuClockLabel.Margin = ScalePadding(6, 0, 10, 0);
        UpdateClockMinimumWidth();

        _addressBarPanel.Height = AddressPanelHeight;
        _addressBarPanel.Padding = new Padding(0);
        _addressBarPanel.Margin = new Padding(0);
        _addressPill.Margin = ScalePadding(0, 4, 4, 4);
        _addressPill.Padding = new Padding(0);
        if (_addressInputLayout is not null)
        {
            _addressInputLayout.Padding = ScalePadding(16, 0, 4, 0);
            if (_addressInputLayout.ColumnStyles.Count > 1)
            {
                // The favourite star's column (Simple leaves the star out).
                _addressInputLayout.ColumnStyles[1].Width = _topBarStyleHiddenControls.Contains(_favoriteButton) ? 0 : ScaleByDpi(34);
            }
        }

        if (_addressRootLayout is not null && _addressRootLayout.RowStyles.Count > 0)
        {
            _addressRootLayout.RowStyles[0].Height = ScaleByDpi(AddressRowHeight);
            _addressRootLayout.RowStyles[2].Height = FavoritesBarHeight;
        }

        if (_addressStripLayout is not null && _addressStripLayout.RowStyles.Count > 0)
        {
            _addressStripLayout.RowStyles[0].Height = ScaleByDpi(AddressRowHeight);
        }

        if (_tabsLayout is not null && _tabsLayout.ColumnStyles.Count > 1)
        {
            _tabsLayout.ColumnStyles[1].Width = NewTabColumnWidth;
        }

        _zoomSlider.Margin = ScalePadding(3, CurrentTopBarStyle == TopBarStyle.Comfortable ? 8 : 4, 3, 2);

        _protectionBanner.MinimumSize = new Size(0, 0);
        ApplyProtectionStripMetrics();
        UpdateHeaderLayout();

        if (_historyLayout is not null && _historyLayout.RowStyles.Count >= 5)
        {
            _historyLayout.RowStyles[0].Height = ScaleByDpi(30);
            _historyLayout.RowStyles[2].Height = ScaleByDpi(30);
            _historyLayout.RowStyles[3].Height = ScaleByDpi(150);
            _historyLayout.RowStyles[4].Height = ScaleByDpi(36);
        }

        _historyPanel.Padding = new Padding(ScaleByDpi(8));
        _historyClearButton.Height = ScaleByDpi(32);

        if (_findLayout is not null)
        {
            _findLayout.Padding = new Padding(ScaleByDpi(6));
            if (_findLayout.ColumnStyles.Count >= 3)
            {
                _findLayout.ColumnStyles[1].Width = ScaleByDpi(80);
                _findLayout.ColumnStyles[2].Width = ScaleByDpi(28);
            }
        }

        _findPanel.Height = ScaleByDpi(42);
        _findTextBox.Margin = ScalePadding(0, 0, 6, 0);
        _webViewLoadingLabel.Margin = ScalePadding(24, 0, 24, 0);

        Padding stripButtonMargin = ScalePadding(4, 5, 0, 5);
        _backButton.Margin = stripButtonMargin;
        _forwardButton.Margin = stripButtonMargin;
        _goButton.Margin = stripButtonMargin;
        _refreshButton.Margin = stripButtonMargin;
        _historyButton.Margin = stripButtonMargin;

        _favoriteButton.Size = new Size(ScaleByDpi(30), ScaleByDpi(30));
        _favoriteButton.Margin = new Padding(0);
        _zoomSlider.Margin = ScalePadding(3, 8, 3, 2);
        _newTabButton.Size = new Size(ScaleByDpi(32), ScaleByDpi(32));
        _newTabButton.MinimumSize = new Size(ScaleByDpi(32), ScaleByDpi(32));
        _newTabButton.Margin = new Padding(0);

        UpdateAddressStripLayout();
    }

    private void TrySetWindowIcon()
    {
        try
        {
            using Icon? exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon is not null)
            {
                Icon = (Icon)exeIcon.Clone();
            }
        }
        catch
        {
            // Keep the framework default icon if extraction is unavailable.
        }
    }

    /// <summary>
    /// Logs are kept with the rest of PhantomHaze's data, one file per day, so the Master Log (Settings -> About)
    /// really holds everything since PhantomHaze was installed. Until v1.3.5 they were written to %TEMP%, which
    /// Windows clears on its own; any logs still there are moved here.
    /// </summary>
    private static string InitializeLogDirectory(string root)
    {
        string logDirectory = Path.Combine(root, "logs");
        try
        {
            Directory.CreateDirectory(logDirectory);
            MigrateLegacyLogs(logDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; Log() ignores write failures.
        }

        return logDirectory;
    }

    private static void MigrateLegacyLogs(string logDirectory)
    {
        string legacyDirectory = Path.Combine(Path.GetTempPath(), "PhantomHaze", "logs");
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        foreach (string source in Directory.EnumerateFiles(legacyDirectory, LogFileSearchPattern).ToList())
        {
            try
            {
                string target = Path.Combine(logDirectory, Path.GetFileName(source));
                if (File.Exists(target))
                {
                    // Both folders have a file for the same day: the older (temp) lines go first.
                    WriteFileAtomically(target, File.ReadAllText(source) + File.ReadAllText(target));
                    File.Delete(source);
                }
                else
                {
                    File.Move(source, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start.
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not important.
        }
    }

    private const string LogFileSearchPattern = "phantomhaze-*.log";

    /// <summary>Today's log file. Computed for every entry, so a session that runs past midnight starts a new file.</summary>
    private string GetCurrentLogFilePath()
    {
        return Path.Combine(_logDirectory, $"phantomhaze-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
    }

    /// <summary>Every log file PhantomHaze has written (the Master Log), oldest first.</summary>
    private List<PhantomLogFile> GetLogFiles()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                return new List<PhantomLogFile>();
            }

            return new DirectoryInfo(_logDirectory)
                .EnumerateFiles(LogFileSearchPattern)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => new PhantomLogFile(file.Name, file.FullName, file.Length, file.LastWriteTimeUtc))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<PhantomLogFile>();
        }
    }

    private static string InitializeWebSessionPath()
    {
        string sessionPath = Path.Combine(GetWebSessionRootDirectory(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionPath);
        return sessionPath;
    }

    private static string GetWebSessionRootDirectory() => Path.Combine(Path.GetTempPath(), "PhantomHaze", "webview-session");

    private const string WebSessionLockFileName = ".phantomhaze-session.lock";

    /// <summary>
    /// Keeps a lock file open in this session's folder for as long as PhantomHaze runs, so a PhantomHaze window that
    /// starts later knows the folder is in use and leaves it alone (see DeleteStaleWebSessionFolders).
    /// </summary>
    private static FileStream? LockWebSessionFolder(string sessionPath)
    {
        try
        {
            return new FileStream(
                Path.Combine(sessionPath, WebSessionLockFileName),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task DeleteStaleWebSessionFoldersAsync()
    {
        try
        {
            int deleted = await Task.Run(() => DeleteStaleWebSessionFolders(_webSessionPath));
            if (deleted > 0 && !IsDisposed)
            {
                Log($"{deleted} web session folder(s) left behind by earlier sessions were deleted.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start.
        }
    }

    /// <summary>
    /// Deletes the web session folders of earlier sessions. WebView2 shuts down after PhantomHaze's window has closed,
    /// so the folder cannot always be deleted on exit. A folder whose lock file is still held belongs to a PhantomHaze
    /// window that is open; folders without a lock file (from older versions) are only deleted when no other PhantomHaze
    /// is running.
    /// </summary>
    private static int DeleteStaleWebSessionFolders(string currentSessionPath)
    {
        string root = GetWebSessionRootDirectory();
        if (!Directory.Exists(root))
        {
            return 0;
        }

        bool otherInstanceRunning;
        try
        {
            using System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();
            System.Diagnostics.Process[] instances = System.Diagnostics.Process.GetProcessesByName(current.ProcessName);
            otherInstanceRunning = instances.Length > 1;
            foreach (System.Diagnostics.Process instance in instances)
            {
                instance.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            otherInstanceRunning = true;
        }

        int deleted = 0;
        foreach (string folder in Directory.EnumerateDirectories(root).ToList())
        {
            if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(currentSessionPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string lockPath = Path.Combine(folder, WebSessionLockFileName);
            if (File.Exists(lockPath))
            {
                try
                {
                    // Opening it fails while the session that created it is still running.
                    using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }
            else if (otherInstanceRunning)
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use (or locked by another program); tried again at the next start.
            }
        }

        return deleted;
    }

    private void LoadSettings()
    {
        _settings = new AppSettings();

        // Taken before reading, so a write by another window in between is noticed rather than missed.
        _settingsFileStamp = ReadSettingsFileStamp();

        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return;
            }

            string rawJson = File.ReadAllText(_settingsFilePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(rawJson);
            if (loaded is null)
            {
                return;
            }

            _settings = loaded;
        }
        catch (Exception ex)
        {
            Log($"Settings load failed: {ex.Message}");
            PreserveUnreadableSettingsFile();
            _settings = new AppSettings();
        }
    }

    /// <summary>
    /// settings.json is the only record of installed extensions and their settings, and it is rewritten on exit.
    /// Keep a copy of a file that could not be read so that data can still be recovered.
    /// </summary>
    private void PreserveUnreadableSettingsFile()
    {
        PreserveUnreadableFile(_settingsFilePath, "settings file", "Default settings are used for this session.");
    }

    /// <summary>
    /// Keeps a copy of a data file that could not be read (settings, favourites). These files are rewritten later in
    /// the session, which would otherwise destroy whatever could still be recovered from them. History is deliberately
    /// not copied: a leftover copy would survive Clear History and turning history saving off.
    /// </summary>
    private void PreserveUnreadableFile(string filePath, string description, string consequence)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            string backupPath = $"{filePath}.unreadable-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            File.Copy(filePath, backupPath, overwrite: true);
            Log($"The unreadable {description} was preserved as {backupPath}. {consequence}");
        }
        catch (Exception ex)
        {
            Log($"The unreadable {description} could not be preserved: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces a file atomically (temporary file + rename), so a crash, power loss or full disk can never leave a
    /// truncated file behind. Throws on failure.
    /// </summary>
    private static void WriteFileAtomically(string filePath, string contents)
    {
        string tempPath = BuildSidecarFilePath(filePath, ".tmp");
        try
        {
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private void NormalizeSettings()
    {
        NormalizeSettings(_settings);
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
        {
            settings.Theme = AppTheme.Light;
        }

        if (!Enum.IsDefined(settings.DefaultSearchEngine))
        {
            settings.DefaultSearchEngine = SearchEngine.Google;
        }

        if (!ValidHistoryRetentionDays.Contains(settings.HistoryRetentionDays))
        {
            settings.HistoryRetentionDays = 1;
        }

        if (!ValidHistoryRetentionDays.Contains(settings.CookieRetentionDays))
        {
            settings.CookieRetentionDays = 1;
        }

        // Settings from before v1.3.6 keep their choice: rendering in the background was either on (forever) or off
        // (now the default of one minute).
        settings.BackgroundTabRenderSeconds ??= settings.RenderWebViewInBackground ? -1 : DefaultBackgroundTabRenderSeconds;
        if (!BackgroundTabRenderOptions.Any(option => option.Value == settings.BackgroundTabRenderSeconds))
        {
            settings.BackgroundTabRenderSeconds = DefaultBackgroundTabRenderSeconds;
        }

        settings.RenderWebViewInBackground = settings.BackgroundTabRenderSeconds == -1;

        settings.WebViewProtocol = WebProtocolCatalog.NormalizeId(settings.WebViewProtocol);
        if (!Enum.IsDefined(settings.TopBarStyle))
        {
            settings.TopBarStyle = TopBarStyle.Comfortable;
        }

        if (!Enum.IsDefined(settings.GradientDirection))
        {
            settings.GradientDirection = ThemeGradientDirection.Horizontal;
        }

        if (!Enum.IsDefined(settings.TextColorMode))
        {
            settings.TextColorMode = TextColorMode.Automatic;
        }

        settings.CustomThemeColor = NormalizeColorSetting(settings.CustomThemeColor, DefaultCustomThemeColor);
        settings.GradientStartColor = NormalizeColorSetting(settings.GradientStartColor, DefaultGradientStartColor);
        settings.GradientEndColor = NormalizeColorSetting(settings.GradientEndColor, DefaultGradientEndColor);
        settings.CustomTextColor = NormalizeColorSetting(settings.CustomTextColor, DefaultCustomTextColor);
        settings.CustomFontUrl = !string.IsNullOrWhiteSpace(settings.CustomFontUrl) &&
                                 GoogleFontLoader.TryNormalizeFontUrl(settings.CustomFontUrl, out string fontUrl, out _)
            ? fontUrl
            : string.Empty;

        settings.StartingPage = NormalizeStartPage(settings.StartingPage);

        settings.Extensions ??= new List<InstalledExtensionEntry>();

        var normalizedExtensions = new List<InstalledExtensionEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (InstalledExtensionEntry entry in settings.Extensions)
        {
            if (entry is null)
            {
                continue;
            }

            string idCandidate = entry.Id;
            if (string.IsNullOrWhiteSpace(idCandidate))
            {
                idCandidate = BuildExtensionIdFromFileName(entry.FileName);
            }

            if (!IsValidExtensionIdentifier(idCandidate))
            {
                continue;
            }

            if (!seenIds.Add(idCandidate))
            {
                continue;
            }

            entry.Id = idCandidate;
            entry.FileName = NormalizeStoredExtensionFileName(entry.FileName, entry.Id, usedFileNames);
            usedFileNames.Add(entry.FileName);
            if (string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                entry.DisplayName = entry.Id;
            }

            // Rebuilt key by key: copying a hand-edited settings.json whose keys differ only by case into a
            // case-insensitive dictionary would otherwise throw during startup.
            entry.SettingValues = CreateSettingValueDictionary(entry.SettingValues);

            normalizedExtensions.Add(entry);
        }

        settings.Extensions = normalizedExtensions;
    }

    private static Dictionary<string, string> CreateSettingValueDictionary(IEnumerable<KeyValuePair<string, string>>? source)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return values;
        }

        foreach (KeyValuePair<string, string> pair in source)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                continue;
            }

            values[pair.Key] = pair.Value ?? string.Empty;
        }

        return values;
    }

    private static bool IsValidExtensionIdentifier(string value)
    {
        // One rule for the parser and the settings file, so they can never disagree about a valid id.
        return PhextParser.IsValidIdentifier(value);
    }

    private static string BuildExtensionIdFromFileName(string? fileName)
    {
        string candidate = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        // The same derivation the parser applies to files without meta.id (including the hash for non-Latin names).
        return PhextParser.BuildDefaultExtensionId(fileName!);
    }

    /// <summary>
    /// Builds the stored file name for an extension from its complete id (lower-cased, because ids are
    /// case-insensitive). Ids whose first segment is a Windows device name such as "con" or "aux" get a prefix,
    /// and a numeric suffix is added when another installed extension already uses the name.
    /// </summary>
    private static string BuildExtensionFileName(string? extensionId, IEnumerable<string>? unavailableFileNames = null)
    {
        string baseName = (extensionId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"extension-{Guid.NewGuid():N}";
        }

        if (IsWindowsReservedFileName(baseName))
        {
            baseName = $"ext-{baseName}";
        }

        HashSet<string>? unavailable = unavailableFileNames?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string candidate = $"{baseName}.phext";
        for (int suffix = 2; unavailable is not null && unavailable.Contains(candidate); suffix++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{baseName}-{suffix}.phext");
        }

        return candidate;
    }

    private static bool IsWindowsReservedFileName(string fileNameWithoutExtension)
    {
        // Windows treats names such as "NUL.phext" or "con.tools.phext" as devices, so the source would be lost.
        string firstSegment = fileNameWithoutExtension.Split('.')[0].TrimEnd(' ');
        return WindowsReservedFileNames.Contains(firstSegment);
    }

    private static string NormalizeStoredExtensionFileName(string? fileName, string extensionId, IEnumerable<string>? unavailableFileNames = null)
    {
        string candidate = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.EndsWith(".phext", StringComparison.OrdinalIgnoreCase))
        {
            return BuildExtensionFileName(extensionId, unavailableFileNames);
        }

        return candidate;
    }

    /// <summary>
    /// Re-imports keep the extension's current file (it is replaced atomically on Save); new imports get a name
    /// derived from the complete id that no other installed extension uses, so one extension's source can never
    /// overwrite another's.
    /// </summary>
    private static string ChooseImportedExtensionFileName(InstalledExtensionEntry entry, IEnumerable<InstalledExtensionEntry> allEntries)
    {
        HashSet<string> otherFileNames = allEntries
            .Where(other => other is not null && !ReferenceEquals(other, entry) && !string.IsNullOrWhiteSpace(other.FileName))
            .Select(other => NormalizeStoredExtensionFileName(other.FileName, other.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string currentFileName = Path.GetFileName(entry.FileName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(currentFileName) &&
            currentFileName.EndsWith(".phext", StringComparison.OrdinalIgnoreCase) &&
            !otherFileNames.Contains(currentFileName) &&
            !IsWindowsReservedFileName(Path.GetFileNameWithoutExtension(currentFileName)))
        {
            return currentFileName;
        }

        return BuildExtensionFileName(entry.Id, otherFileNames);
    }

    /// <summary>
    /// Name handed to the parser for an installed extension. The parser only uses it to derive an id for files
    /// without meta.id, so it is based on the stored id rather than the stored file name (which can carry a
    /// collision suffix or device-name prefix) and therefore always yields the same id again.
    /// </summary>
    private static string GetExtensionParserSourceName(InstalledExtensionEntry installed)
    {
        return IsValidExtensionIdentifier(installed.Id)
            ? $"{installed.Id}.phext"
            : installed.FileName;
    }

    private void SaveSettings()
    {
        // Another PhantomHaze window (or a manual edit) changed settings.json after this window read or wrote it.
        // Everything the user changed here was already written when Settings was saved, so overwriting the file now
        // would only throw away the other window's changes, such as extensions it installed.
        if (HasSettingsFileChangedSinceLastAccess())
        {
            Log("settings.json was changed by another PhantomHaze window after this one loaded it, so it was not overwritten on exit.");
            return;
        }

        try
        {
            WriteSettingsFile(_settings);
        }
        catch (Exception ex)
        {
            Log($"Settings save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes settings.json atomically (temporary file + rename) so a crash or a full disk can never leave a
    /// truncated file behind, which would reset every setting and orphan all installed extensions on the next
    /// start. Throws on failure.
    /// </summary>
    private void WriteSettingsFile(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });

        WriteFileAtomically(_settingsFilePath, json);
        _settingsFileStamp = ReadSettingsFileStamp();
    }

    private SettingsFileStamp? ReadSettingsFileStamp()
    {
        try
        {
            var info = new FileInfo(_settingsFilePath);
            return info.Exists ? new SettingsFileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool HasSettingsFileChangedSinceLastAccess()
    {
        return ReadSettingsFileStamp() != _settingsFileStamp;
    }

    /// <summary>Reads and normalises the settings file as it is on disk now (null if it is missing or unreadable).</summary>
    private AppSettings? TryReadSettingsFile()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return null;
            }

            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFilePath));
            if (settings is not null)
            {
                NormalizeSettings(settings);
            }

            return settings;
        }
        catch (Exception ex)
        {
            Log($"settings.json could not be re-read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Another PhantomHaze window can install or uninstall extensions while this one is open. Before this window
    /// saves, extensions it never knew about are added to its draft, and extensions it knew about that are no longer
    /// installed are dropped (unless they were imported again in this dialog), so neither window undoes the other.
    /// Everything else in the draft is what the user just chose in this window and is kept as it is.
    /// </summary>
    private static void MergeExternalExtensionChanges(
        AppSettings draft,
        IEnumerable<InstalledExtensionEntry> knownWhenDialogOpened,
        IEnumerable<InstalledExtensionEntry> currentlyOnDisk,
        IEnumerable<string> extensionIdsImportedInDialog)
    {
        var known = knownWhenDialogOpened.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<InstalledExtensionEntry> onDisk = currentlyOnDisk.Where(entry => entry is not null).ToList();
        var onDiskIds = onDisk.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = extensionIdsImportedInDialog.ToHashSet(StringComparer.OrdinalIgnoreCase);

        draft.Extensions.RemoveAll(entry =>
            known.Contains(entry.Id) && !onDiskIds.Contains(entry.Id) && !imported.Contains(entry.Id));

        var draftIds = draft.Extensions.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var draftFileNames = draft.Extensions.Select(entry => entry.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledExtensionEntry entry in onDisk)
        {
            if (known.Contains(entry.Id) || draftIds.Contains(entry.Id) || draftFileNames.Contains(entry.FileName))
            {
                continue;
            }

            draft.Extensions.Add(entry.Clone());
            draftIds.Add(entry.Id);
            draftFileNames.Add(entry.FileName);
        }
    }

    private static string BuildSidecarFilePath(string targetPath, string suffix)
    {
        string directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        return Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}{suffix}");
    }

    /// <param name="shouldAnnounce">
    /// Optional filter for which active extensions write their app.log startup messages (all of them when null).
    /// </param>
    private void LoadInstalledExtensions(Func<LoadedExtension, bool>? shouldAnnounce = null)
    {
        _loadedExtensions.Clear();
        _loadedExtensionById.Clear();
        _reportedNonRunningExtensionScripts.Clear();

        foreach (InstalledExtensionEntry installed in _settings.Extensions)
        {
            string extensionPath = Path.Combine(_extensionsRootPath, installed.FileName);
            if (!File.Exists(extensionPath))
            {
                Log($"Extension '{installed.Id}' skipped because file is missing: {installed.FileName}");
                continue;
            }

            try
            {
                string source = File.ReadAllText(extensionPath);
                PhantomExtensionDefinition definition = PhextParser.Parse(source, GetExtensionParserSourceName(installed));
                if (!definition.Id.Equals(installed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    // The file was edited on disk. If its new id belongs to another installed extension, this entry is
                    // left untouched: renaming it would create a duplicate that the next settings normalisation drops,
                    // orphaning its file and losing its setting values.
                    bool idUsedByAnotherExtension = _settings.Extensions.Any(other =>
                        !ReferenceEquals(other, installed) &&
                        other.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
                    if (idUsedByAnotherExtension)
                    {
                        Log($"Extension '{installed.Id}' was not loaded: {installed.FileName} now declares the id " +
                            $"'{definition.Id}', which another installed extension already uses.");
                        continue;
                    }

                    Log($"Extension id corrected from '{installed.Id}' to '{definition.Id}' based on {installed.FileName}.");
                    installed.Id = definition.Id;
                }

                installed.DisplayName = string.IsNullOrWhiteSpace(installed.DisplayName)
                    ? definition.Name
                    : installed.DisplayName;
                installed.FileName = NormalizeStoredExtensionFileName(installed.FileName, installed.Id);
                EnsureExtensionSettingValues(installed, definition);

                var loaded = new LoadedExtension(installed, definition, extensionPath);
                if (_loadedExtensionById.ContainsKey(loaded.Definition.Id))
                {
                    Log($"Duplicate extension id '{loaded.Definition.Id}' ignored.");
                    continue;
                }

                _loadedExtensionById[loaded.Definition.Id] = loaded;
                _loadedExtensions.Add(loaded);

                // app.log messages announce an extension starting, so disabled extensions stay silent.
                if (!IsExtensionActive(loaded) || (shouldAnnounce is not null && !shouldAnnounce(loaded)))
                {
                    continue;
                }

                foreach (string startupMessage in loaded.Definition.StartupLogMessages)
                {
                    string resolved = ResolveExtensionPlaceholders(startupMessage, loaded);
                    Log($"[{NormalizeForLog(loaded.Definition.Name)}] {NormalizeForLog(resolved)}");
                }
            }
            catch (Exception ex)
            {
                Log($"Extension '{installed.Id}' failed to load from '{installed.FileName}': {ex.Message}");
            }
        }
    }

    private bool IsExtensionActive(LoadedExtension extension)
    {
        return _settings.ExtensionsEnabled && extension.Installation.Enabled;
    }

    private IEnumerable<LoadedExtension> GetEnabledExtensions()
    {
        if (!_settings.ExtensionsEnabled)
        {
            return Enumerable.Empty<LoadedExtension>();
        }

        return _loadedExtensions.Where(extension => extension.Installation.Enabled);
    }

    private static bool TryParseBooleanSetting(string value, out bool parsed)
    {
        return PhextParser.TryParseBoolean(value, out parsed);
    }

    private static bool TryNormalizeSettingValue(PhantomExtensionSettingDefinition setting, string? input, out string normalized)
    {
        normalized = setting.DefaultValue;
        string candidate = input?.Trim() ?? string.Empty;

        switch (setting.Type)
        {
            case PhantomExtensionSettingType.Toggle:
                if (!TryParseBooleanSetting(candidate, out bool toggleValue))
                {
                    return false;
                }

                normalized = toggleValue ? "true" : "false";
                return true;

            case PhantomExtensionSettingType.Text:
                normalized = input ?? string.Empty;
                return true;

            case PhantomExtensionSettingType.Number:
                if (!int.TryParse(
                        candidate,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int number))
                {
                    return false;
                }

                if (setting.Minimum is int minimum && number < minimum)
                {
                    return false;
                }

                if (setting.Maximum is int maximum && number > maximum)
                {
                    return false;
                }

                normalized = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;

            case PhantomExtensionSettingType.Select:
                if (setting.SelectOptions.Count == 0)
                {
                    return false;
                }

                string? option = setting.SelectOptions.FirstOrDefault(value =>
                    value.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                {
                    return false;
                }

                normalized = option;
                return true;

            default:
                return false;
        }
    }

    private static string GetDefaultSettingValue(PhantomExtensionSettingDefinition setting)
    {
        // Text defaults are kept exactly as declared, including "" and whitespace-only values.
        if (setting.Type == PhantomExtensionSettingType.Text)
        {
            return setting.DefaultValue ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(setting.DefaultValue))
        {
            return setting.Type switch
            {
                PhantomExtensionSettingType.Toggle => "false",
                PhantomExtensionSettingType.Number => "0",
                _ => string.Empty,
            };
        }

        return setting.DefaultValue;
    }

    private static void EnsureExtensionSettingValues(InstalledExtensionEntry installation, PhantomExtensionDefinition definition)
    {
        if (installation.SettingValues is null || installation.SettingValues.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            installation.SettingValues = CreateSettingValueDictionary(installation.SettingValues);
        }

        HashSet<string> validKeys = definition.Settings
            .Select(setting => setting.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string staleKey in installation.SettingValues.Keys.Where(key => !validKeys.Contains(key)).ToArray())
        {
            installation.SettingValues.Remove(staleKey);
        }

        foreach (PhantomExtensionSettingDefinition setting in definition.Settings)
        {
            if (!installation.SettingValues.TryGetValue(setting.Key, out string? existingValue))
            {
                installation.SettingValues[setting.Key] = GetDefaultSettingValue(setting);
                continue;
            }

            if (!TryNormalizeSettingValue(setting, existingValue, out string normalized))
            {
                installation.SettingValues[setting.Key] = GetDefaultSettingValue(setting);
                continue;
            }

            installation.SettingValues[setting.Key] = normalized;
        }
    }

    private void RefreshExtensionSurface()
    {
        RebuildExtensionMenuButtons();
        RebuildExtensionLauncherButtons();
        PositionExtensionLauncherPanel();

        // Re-applies colours, fonts and the extensions' ui.element / ui.svg / ui.shape customisations, so controls
        // customised by an extension that was just disabled or uninstalled go back to how they normally look.
        ApplyTheme();
    }

    /// <summary>
    /// Shows as many extension menu buttons as fit in the space left of Settings and moves the rest into the
    /// overflow (») menu, so extension buttons can never push the built-in controls out of view.
    /// </summary>
    private void UpdateExtensionMenuOverflow()
    {
        if (_updatingExtensionMenuOverflow || _extensionMenuButtonsPanel.IsDisposed)
        {
            return;
        }

        _updatingExtensionMenuOverflow = true;
        try
        {
            List<Button> buttons = _extensionMenuButtonsPanel.Controls
                .OfType<Button>()
                .Where(button => !ReferenceEquals(button, _extensionMenuOverflowButton))
                .ToList();

            _overflowedExtensionMenuButtons.Clear();
            int available = _extensionMenuButtonsPanel.ClientSize.Width - _extensionMenuButtonsPanel.Padding.Horizontal;
            int required = buttons.Sum(GetMenuButtonFootprint);
            if (buttons.Count == 0 || available <= 0 || required <= available)
            {
                foreach (Button button in buttons)
                {
                    button.Visible = true;
                }

                _extensionMenuOverflowButton.Visible = false;
                return;
            }

            int budget = available - GetMenuButtonFootprint(_extensionMenuOverflowButton);
            int used = 0;
            foreach (Button button in buttons)
            {
                int footprint = GetMenuButtonFootprint(button);
                bool fits = _overflowedExtensionMenuButtons.Count == 0 && used + footprint <= budget;
                button.Visible = fits;
                if (fits)
                {
                    used += footprint;
                }
                else
                {
                    _overflowedExtensionMenuButtons.Add(button);
                }
            }

            _extensionMenuOverflowButton.Visible = true;
        }
        finally
        {
            _updatingExtensionMenuOverflow = false;
        }
    }

    private static int GetMenuButtonFootprint(Button button)
    {
        Size preferred = button.GetPreferredSize(Size.Empty);
        return Math.Max(preferred.Width, button.MinimumSize.Width) + button.Margin.Horizontal;
    }

    private void RebuildExtensionLauncherButtons()
    {
        _extensionLauncherToolTip.RemoveAll();
        foreach (Button button in _extensionLauncherPanel.Controls.OfType<Button>().ToArray())
        {
            Image? icon = button.Image;
            button.Image = null;
            icon?.Dispose();
            button.Dispose();
        }

        _extensionLauncherPanel.Controls.Clear();

        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            if (!extension.Definition.Ui.HasUi)
            {
                continue;
            }

            var button = new Button
            {
                Width = ScaleByDpi(42),
                Height = ScaleByDpi(42),
                Margin = ScalePadding(0, 0, 0, 6),
                FlatStyle = FlatStyle.Flat,
                ImageAlign = ContentAlignment.MiddleCenter,
                Text = string.Empty,
                // Icon-only button: expose the extension's name to assistive technology and as a hover tooltip.
                AccessibleName = extension.Definition.Name,
                AccessibleDescription = $"Opens the {extension.Definition.Name} extension window.",
                AccessibleRole = AccessibleRole.PushButton,
                Tag = extension,
            };
            _extensionLauncherToolTip.SetToolTip(button, extension.Definition.Name);

            Image icon = PhantomExtensionIconFactory.CreateIcon(extension.Definition.IconToken);
            button.Image = new Bitmap(icon, new Size(ScaleByDpi(20), ScaleByDpi(20)));
            icon.Dispose();

            button.Click += (_, _) => OpenExtensionUiWindow(extension);
            _extensionLauncherPanel.Controls.Add(button);
        }

        _extensionLauncherPanel.Visible = _settings.ExtensionsEnabled && _extensionLauncherPanel.Controls.Count > 0;
        ApplyThemeToDynamicExtensionControls();
    }

    private void ApplyThemeToDynamicExtensionControls()
    {
        Color buttonTextColor = ResolveTextColor(_palette.ButtonText);
        Color buttonBackColor = ResolveSurfaceBack(_palette.ButtonBack, buttonTextColor);
        Color buttonBorderColor = _palette.ButtonBorder;
        Color pillTextColor = ResolveTextColor(_palette.PillText);
        Color pillBackColor = ResolveSurfaceBack(_palette.PillBack, pillTextColor);

        foreach (Button button in _extensionMenuButtonsPanel.Controls.OfType<Button>())
        {
            if (button.Tag is not ExtensionMenuEntry entry)
            {
                // The overflow (») button is PhantomHaze's own pill (ApplyTheme).
                continue;
            }

            // Page buttons are view switchers and are coloured with Logs and Web (UpdateWebViewToggleVisual).
            if (entry.Kind != ExtensionMenuEntryKind.Page)
            {
                button.BackColor = pillBackColor;
                button.ForeColor = pillTextColor;
            }

            // Pills that extensions add have a coloured ring, so they are never mistaken for PhantomHaze's own buttons.
            button.FlatAppearance.BorderColor = _palette.ExtensionOutline;
            button.FlatAppearance.BorderSize = ExtensionPillRingWidth;
        }

        foreach (Button button in _extensionLauncherPanel.Controls.OfType<Button>())
        {
            button.BackColor = buttonBackColor;
            button.ForeColor = buttonTextColor;
            button.FlatAppearance.BorderColor = buttonBorderColor;
            button.FlatAppearance.BorderSize = 1;
        }
    }

    /// <summary>
    /// Resolves {{extension.*}} / {{setting.*}} placeholders in one pass (values are never re-scanned), encoding
    /// the inserted values for the context the template is used in.
    /// </summary>
    private static string ResolveExtensionPlaceholders(
        string template,
        LoadedExtension extension,
        PhantomExtensionPlaceholderContext context = PhantomExtensionPlaceholderContext.PlainText)
    {
        return PhantomExtensionPlaceholders.Resolve(
            template,
            extension.Definition,
            extension.Installation.SettingValues,
            context);
    }

    private void OpenExtensionUiWindow(LoadedExtension extension)
    {
        if (!IsExtensionActive(extension) || !extension.Definition.Ui.HasUi)
        {
            return;
        }

        string windowKey = extension.Definition.Id;
        if (_openExtensionWindowsById.TryGetValue(windowKey, out ExtensionWindowState? existing))
        {
            if (!existing.Window.IsDisposed)
            {
                // The window has no taskbar button, so a minimised window must be restored from here.
                if (existing.Window.WindowState == FormWindowState.Minimized)
                {
                    existing.Window.WindowState = FormWindowState.Normal;
                }

                existing.Window.Activate();
                return;
            }

            _openExtensionWindowsById.Remove(windowKey);
        }

        var extensionWindow = new Form
        {
            Text = extension.Definition.Ui.Title,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = GetExtensionWindowClientSize(extension.Definition.Ui),
            MinimizeBox = true,
            MaximizeBox = true,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = GetExtensionWindowBackColor(),
        };

        var state = new ExtensionWindowState(extensionWindow, extension);
        _openExtensionWindowsById[windowKey] = state;

        // Registered before the handle exists, so capture protection is in place before the window is first shown
        // and follows every later protection change.
        RegisterProtectedAuxiliaryWindow(extensionWindow, $"Extension '{extension.Definition.Name}'");

        extensionWindow.FormClosed += (_, _) =>
        {
            if (_openExtensionWindowsById.TryGetValue(windowKey, out ExtensionWindowState? current) &&
                ReferenceEquals(current, state))
            {
                _openExtensionWindowsById.Remove(windowKey);
            }
        };

        extensionWindow.Shown += (_, _) => _ = InitializeExtensionWindowAsync(state);
        extensionWindow.Show(this);
    }

    private Size GetExtensionWindowClientSize(PhantomExtensionUiDefinition ui)
    {
        int width = ScaleByDpi(Math.Clamp(ui.Width, 320, 2000));
        int height = ScaleByDpi(Math.Clamp(ui.Height, 240, 1400));

        // Keep the window (and its frame) on screen even for large ui.size values at high DPI.
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        width = Math.Min(width, Math.Max(ScaleByDpi(320), workingArea.Width - ScaleByDpi(40)));
        height = Math.Min(height, Math.Max(ScaleByDpi(240), workingArea.Height - ScaleByDpi(80)));
        return new Size(width, height);
    }

    private Color GetExtensionWindowBackColor()
    {
        return _palette.ExtensionWindowBack;
    }

    private async Task InitializeExtensionWindowAsync(ExtensionWindowState state)
    {
        Form window = state.Window;
        if (window.IsDisposed || state.WebView is not null || state.FallbackBox is not null)
        {
            return;
        }

        try
        {
            var webView = new WebView2
            {
                Dock = DockStyle.Fill,
            };

            state.WebView = webView;
            window.Controls.Add(webView);

            CoreWebView2Environment environment = _webEnvironment ??
                await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _webSessionPath,
                    options: new CoreWebView2EnvironmentOptions("--inprivate"));

            // The window (or the whole app) can be closed while WebView2 is still starting up.
            if (window.IsDisposed || webView.IsDisposed)
            {
                return;
            }

            await webView.EnsureCoreWebView2Async(environment);
            if (window.IsDisposed || webView.IsDisposed || webView.CoreWebView2 is null)
            {
                return;
            }

            ConfigureExtensionSurfaceCore(webView.CoreWebView2);
            RenderExtensionWindow(state);
        }
        catch (Exception ex)
        {
            if (window.IsDisposed)
            {
                return;
            }

            Log($"Extension UI failed for '{state.Extension.Definition.Id}': {ex.Message}");
            ShowExtensionWindowFallback(state);
        }
    }

    private void ShowExtensionWindowFallback(ExtensionWindowState state)
    {
        try
        {
            if (state.WebView is not null)
            {
                state.Window.Controls.Remove(state.WebView);
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
            state.Window.Controls.Add(fallbackBox);
            RenderExtensionWindow(state);
        }
        catch (Exception ex)
        {
            Log($"Extension UI fallback failed for '{state.Extension.Definition.Id}': {ex.Message}");
        }
    }

    /// <summary>
    /// Renders an extension window from its current LoadedExtension. Content is reloaded only when it actually
    /// changed (settings, extension source or theme), so unrelated Settings saves do not reset the extension UI.
    /// </summary>
    private void RenderExtensionWindow(ExtensionWindowState state)
    {
        Form window = state.Window;
        if (window.IsDisposed)
        {
            return;
        }

        LoadedExtension extension = state.Extension;
        window.Text = extension.Definition.Ui.Title;
        window.BackColor = GetExtensionWindowBackColor();

        if (state.FallbackBox is { IsDisposed: false } fallbackBox)
        {
            fallbackBox.BackColor = _palette.FallbackBack;
            fallbackBox.ForeColor = _palette.FallbackText;
            string fallbackText = $"Extension UI fallback for '{extension.Definition.Name}'\n\n" +
                                  "The extension UI could not be displayed with WebView2.\n\n" +
                                  ResolveExtensionPlaceholders(extension.Definition.Ui.Html, extension);
            if (!string.Equals(fallbackText, state.RenderedContent, StringComparison.Ordinal))
            {
                fallbackBox.Text = fallbackText;
                state.RenderedContent = fallbackText;
            }

            return;
        }

        CoreWebView2? core = state.WebView is { IsDisposed: false } webView ? webView.CoreWebView2 : null;
        if (core is null)
        {
            // Still initialising: InitializeExtensionWindowAsync renders the latest state once WebView2 is ready.
            return;
        }

        string html = BuildExtensionUiDocument(extension);
        if (string.Equals(html, state.RenderedContent, StringComparison.Ordinal))
        {
            return;
        }

        core.NavigateToString(html);
        state.RenderedContent = html;
    }

    /// <summary>
    /// Runs after Settings were saved and the extensions reloaded: windows of extensions that were disabled,
    /// uninstalled or no longer have a UI are closed; the others are re-rendered with the new settings, source
    /// and theme.
    /// </summary>
    private void RefreshOpenExtensionWindows()
    {
        foreach ((string extensionId, ExtensionWindowState state) in _openExtensionWindowsById.ToArray())
        {
            if (state.Window.IsDisposed)
            {
                _openExtensionWindowsById.Remove(extensionId);
                continue;
            }

            if (!_loadedExtensionById.TryGetValue(extensionId, out LoadedExtension? current) ||
                !IsExtensionActive(current) ||
                !current.Definition.Ui.HasUi)
            {
                state.Window.Close();
                continue;
            }

            state.Extension = current;
            try
            {
                RenderExtensionWindow(state);
            }
            catch (Exception ex)
            {
                Log($"Extension UI refresh failed for '{extensionId}': {ex.Message}");
            }
        }
    }

    private void OnExtensionWindowNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never let WebView2 open its own popup window: it would be a separate top-level window outside
        // PhantomHaze's control and not excluded from screen capture. Web links open in a protected tab instead.
        e.Handled = true;
        OpenExtensionWindowLinkInTab(e.Uri, e.IsUserInitiated, "popup");
    }

    private void OnExtensionWindowNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The extension UI is a NavigateToString document; same-page (#fragment), about: and data: navigations keep
        // it. A web page or local file would replace the UI in a window that has no way back, so those navigations
        // are cancelled and web links the user clicked open in a protected tab.
        string requestedUri = e.Uri ?? string.Empty;
        bool replacesExtensionUi =
            requestedUri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            requestedUri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            requestedUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!replacesExtensionUi)
        {
            return;
        }

        e.Cancel = true;
        OpenExtensionWindowLinkInTab(requestedUri, e.IsUserInitiated, "navigation");
    }

    private void OnExtensionWindowWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core ||
            !TryGetBlockingExtensionRule(e.Request.Uri, out LoadedExtension? blockingExtension, out PhantomExtensionWebBlockDefinition? blockingRule))
        {
            return;
        }

        e.Response = CreateBlockedResourceResponse(core);
        Log($"Extension window request blocked by extension {NormalizeForLog(blockingExtension!.Definition.Name)} " +
            $"({blockingRule!.Id}): {TrimForLog(e.Request.Uri, 220)}");
    }

    /// <summary>Opens an http(s) link from an extension window in a new protected tab if the user clicked it.</summary>
    private void OpenExtensionWindowLinkInTab(string? requestedUri, bool isUserInitiated, string kind)
    {
        requestedUri ??= string.Empty;
        if (!isUserInitiated ||
            !Uri.TryCreate(requestedUri, UriKind.Absolute, out Uri? uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"Blocked a {kind} from an extension window: {TrimForLog(requestedUri, 220)}");
            return;
        }

        string url = uri.AbsoluteUri;
        try
        {
            BeginInvoke(new Action(() => _ = OpenUrlInNewTabAsync(url, "Extension window link")));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The main window is closing; ignore the late request.
        }
    }

    /// <summary>Opens a link in a new protected tab and switches to Web View (extension window links, the Update button).</summary>
    /// <param name="description">What is being opened, for the log (for example "Extension window link").</param>
    private async Task OpenUrlInNewTabAsync(string url, string description)
    {
        try
        {
            BrowserTab? tab = await CreateTabAsync(url, activate: true);
            if (tab is null || IsDisposed)
            {
                return;
            }

            if (!_webViewModeEnabled)
            {
                await SetWebViewModeAsync(enableWebView: true);
            }

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            Activate();
            Log($"{description} opened in a new tab: {url}");
        }
        catch (Exception ex)
        {
            Log($"{description} could not be opened in a new tab: {ex.Message}");
        }
    }

    private string BuildExtensionUiDocument(LoadedExtension extension)
    {
        return BuildExtensionHtmlDocument(extension, extension.Definition.Ui.Html, extension.Definition.Ui.Title);
    }

    /// <summary>
    /// The document shown in an extension window (ui.html) or extension page (app.page). Fragments are wrapped in a
    /// page that follows the app's theme, text colour and font; complete documents (with &lt;html&gt;) are used as written.
    /// </summary>
    private string BuildExtensionHtmlDocument(LoadedExtension extension, string html, string title)
    {
        // Placeholder values are user-entered text, not markup, so they are HTML-encoded here.
        string resolvedHtml = ResolveExtensionPlaceholders(html, extension, PhantomExtensionPlaceholderContext.Html);
        if (html.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return resolvedHtml;
        }

        ThemePalette palette = _palette;
        // Rainbow text would need constant re-rendering, so documents use the theme's text colour in that mode.
        Color text = _settings.TextColorMode == TextColorMode.Rainbow ? palette.DocumentText : ResolveTextColor(palette.DocumentText);
        string textColor = PhantomColorParser.ToHex(text);
        string background = palette.Gradient is ThemeGradient gradient
            ? string.Create(CultureInfo.InvariantCulture, $"linear-gradient({gradient.CssAngle}, {PhantomColorParser.ToHex(gradient.Start)}, {PhantomColorParser.ToHex(gradient.End)}) fixed")
            : PhantomColorParser.ToHex(palette.DocumentBack);
        string fontFamily = "\"Segoe UI\", sans-serif";
        string fontLink = string.Empty;
        if (_appFont is not null)
        {
            // The window's browser process cannot see fonts loaded into PhantomHaze itself, so it loads the same
            // Google Fonts stylesheet (only fonts.googleapis.com links are ever stored in this setting).
            fontFamily = $"\"{_appFont.Family.Replace("\"", string.Empty, StringComparison.Ordinal)}\", {fontFamily}";
            fontLink = $"<link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(_appFont.CssUrl)}\">";
        }

        string encodedTitle = WebUtility.HtmlEncode(title);
        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{encodedTitle}}</title>
  {{fontLink}}
  <style>
    body {
      margin: 0;
      padding: 16px;
      min-height: 100vh;
      box-sizing: border-box;
      font-family: {{fontFamily}};
      color: {{textColor}};
      background: {{background}};
    }
  </style>
</head>
<body>
{{resolvedHtml}}
</body>
</html>
""";
    }

    private void LoadHistory()
    {
        _historyEntries.Clear();

        if (_settings.PersistHistoryAcrossSessions)
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    string rawJson = File.ReadAllText(_historyFilePath);
                    List<HistoryEntry>? loadedEntries = JsonSerializer.Deserialize<List<HistoryEntry>>(rawJson);
                    if (loadedEntries is not null)
                    {
                        foreach (HistoryEntry entry in loadedEntries)
                        {
                            if (!TryNormalizeUrl(entry.Url, out string normalizedUrl))
                            {
                                continue;
                            }

                            DateTimeOffset visitedAt = entry.VisitedAtUtc == default
                                ? DateTimeOffset.UtcNow
                                : entry.VisitedAtUtc;

                            _historyEntries.Add(new HistoryEntry
                            {
                                Url = normalizedUrl,
                                VisitedAtUtc = visitedAt,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"History load failed: {ex.Message}");
            }
        }

        PruneHistoryByRetention();
        RefreshHistoryPanelItems();
        SaveHistory();
    }

    private void SaveHistory()
    {
        if (!_settings.PersistHistoryAcrossSessions)
        {
            TryDeleteFile(_historyFilePath);
            return;
        }

        PruneHistoryByRetention();

        try
        {
            string json = JsonSerializer.Serialize(
                _historyEntries,
                new JsonSerializerOptions { WriteIndented = true });

            // Saved after every page load, so a crash mid-write must not be able to leave a truncated file.
            WriteFileAtomically(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"History save failed: {ex.Message}");
        }
    }

    private void PruneHistoryByRetention()
    {
        int retentionDays = _settings.HistoryRetentionDays;
        if (retentionDays < 0)
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        _historyEntries.RemoveAll(entry => entry.VisitedAtUtc < cutoff);
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            File.Delete(filePath);
        }
        catch
        {
            // Best effort only; keeping the app functional is preferred.
        }
    }

    private void LogTrueOsBuild()
    {
        int build = NativeMethods.GetTrueOsBuildNumber();
        if (build < 0)
        {
            Log("Could not read the true OS build number via RtlGetVersion.");
            return;
        }

        bool meetsMinimum = build >= NativeMethods.MinimumBuildForExcludeFromCapture;
        Log($"Detected Windows build: {build} " +
            $"(minimum for WDA_EXCLUDEFROMCAPTURE is {NativeMethods.MinimumBuildForExcludeFromCapture}) " +
            $"- {(meetsMinimum ? "meets minimum" : "BELOW MINIMUM, the API call below is expected to fail")}.");
    }

    private void BuildUi()
    {
        BuildMenuBar();
        BuildUpdateBanner();
        BuildResourceSaverBanner();
        BuildProtectionDisableMenu();
        ProtectPopupMenuFromCapture(_disableProtectionMenu);
        ProtectPopupMenuFromCapture(_extensionMenuOverflowMenu);
        ProtectPopupMenuFromCapture(_extensionDropdownMenu);
        ProtectPopupMenuFromCapture(_updateBannerSnoozeMenu);

        BuildProtectionBanner();
        BuildAddressBar();
        BuildHeaderLayout();
        BuildHistoryPanel();
        BuildFindPanel();
        BuildWebViewLoadingOverlay();
        BuildExtensionLauncherPanel();

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = _logFont;

        _webViewHostPanel.Dock = DockStyle.Fill;
        _webViewHostPanel.Visible = false;

        _extensionPageHostPanel.Dock = DockStyle.Fill;
        _extensionPageHostPanel.Visible = false;

        _contentHostPanel.Dock = DockStyle.Fill;
        _contentHostPanel.Controls.Add(_webViewHostPanel);
        _contentHostPanel.Controls.Add(_extensionPageHostPanel);
        _contentHostPanel.Controls.Add(_logBox);
        _contentHostPanel.Controls.Add(_historyPanel);
        _contentHostPanel.Controls.Add(_findPanel);
        _contentHostPanel.Controls.Add(_extensionLauncherPanel);
        _contentHostPanel.Controls.Add(_webViewLoadingOverlayPanel);
        _contentHostPanel.Resize += (_, _) =>
        {
            PositionFindPanel();
            PositionExtensionLauncherPanel();
        };

        Controls.Add(_contentHostPanel);
        BuildInspector();
        // Under the top bar, outside Web View: the protection status and button.
        Controls.Add(_protectionStripPanel);
        // The top bar holds the menu, the Web View address and tab rows and (in Web View) the protection banner.
        Controls.Add(_menuBarPanel);
        // Above the top bar, below the update banner.
        Controls.Add(_resourceSaverBannerPanel);
        // Docked last, so the update banner is the topmost strip of the window, above the top bar.
        Controls.Add(_updateBannerPanel);

        foreach (ThemedPanel surface in ThemeSurfaces)
        {
            surface.BackgroundPainter = PaintThemeGradient;
        }

        // The top bar paints the window background around its own rounded shape.
        _menuBarPanel.BackgroundPainter = PaintHeaderBackground;

        BuildUiTargetBindings();
        // Taken before any custom font is applied, so the default fonts can always be restored.
        RegisterOriginalFonts(this);
        // Applies the sizes too (ApplyDpiAwareMetrics).
        ApplyTopBarStyle();
        ApplyTheme();
        UpdateWebViewToggleVisual();
        UpdateResponsiveLayout();
        PositionExtensionLauncherPanel();
    }

    private void BuildMenuBar()
    {
        // The top bar: a rounded shape (painted by PaintHeaderBackground) with the menu row, the Web View address and
        // tab rows and the protection banner inside it (see BuildHeaderLayout).
        _menuBarPanel.Dock = DockStyle.Top;
        _menuBarPanel.AutoSize = true;
        _menuBarPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // Extension buttons live in the stretching column (where the spacer used to be), so however many buttons
        // extensions add, the built-in controls - Settings in particular - always keep their space. Buttons that
        // do not fit are moved into an overflow (») menu by UpdateExtensionMenuOverflow.
        // Columns: logo | Logs | Web | extension pages and buttons (stretches) | clock | protection switch (Simple) |
        // Settings gear | drop-down button (Compact). See MainForm.TopBar.cs for the styles.
        var menuLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _menuLayout = menuLayout;
        menuLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var logoLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _menuLogoLayout = logoLayout;
        logoLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _menuLogoPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        _menuLogoPictureBox.Size = new Size(ScaleByDpi(24), ScaleByDpi(24));
        _menuLogoPictureBox.Margin = ScalePadding(0, 3, 8, 3);
        _defaultLogoImage = Icon?.ToBitmap() ?? DefaultTabIcon;
        _menuLogoPictureBox.Image = _defaultLogoImage;

        _menuLogoLabel.Text = "PhantomHaze";
        // Without AutoSize a Label keeps its default 100px width, which cut the bold name off at "Phantom".
        _menuLogoLabel.AutoSize = true;
        _menuLogoLabel.Dock = DockStyle.Fill;
        _menuLogoLabel.Font = _menuTitleFont;
        _menuLogoLabel.TextAlign = ContentAlignment.MiddleLeft;
        _menuLogoLabel.Margin = ScalePadding(0, 0, 10, 0);
        _menuLogoLabel.UseMnemonic = false;

        logoLayout.Controls.Add(_menuLogoPictureBox, 0, 0);
        logoLayout.Controls.Add(_menuLogoLabel, 1, 0);

        ConfigureMenuButton(_logsViewButton, "Logs");
        _logsViewButton.Click += async (_, _) => await SetWebViewModeAsync(enableWebView: false);

        ConfigureMenuButton(_webViewToggleButton, "Web");
        _webViewToggleButton.AccessibleName = "Web View";
        _webViewToggleButton.Click += async (_, _) => await SetWebViewModeAsync(enableWebView: true);

        _extensionMenuButtonsPanel.AutoSize = true;
        _extensionMenuButtonsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _extensionMenuButtonsPanel.Dock = DockStyle.Fill;
        _extensionMenuButtonsPanel.WrapContents = false;
        _extensionMenuButtonsPanel.Margin = new Padding(0);
        _extensionMenuButtonsPanel.Padding = new Padding(0);
        _extensionMenuButtonsPanel.FlowDirection = FlowDirection.LeftToRight;
        _extensionMenuButtonsPanel.SizeChanged += (_, _) => UpdateExtensionMenuOverflow();

        ConfigureMenuButton(_extensionMenuOverflowButton, "»");
        _extensionMenuOverflowButton.MinimumSize = new Size(ScaleByDpi(44), ScaleByDpi(HeaderButtonHeight));
        _extensionMenuOverflowButton.UseMnemonic = false;
        _extensionMenuOverflowButton.Visible = false;
        _extensionMenuOverflowButton.AccessibleName = "More extension buttons";
        _extensionMenuOverflowButton.Click += (_, _) => ShowExtensionMenuOverflow();

        // A round gear button, at the right of the menu row.
        _settingsButton.Text = SettingsGlyph;
        _settingsButton.Font = _settingsGlyphFont;
        _settingsButton.AutoSize = true;
        _settingsButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _settingsButton.Dock = DockStyle.None;
        _settingsButton.Anchor = AnchorStyles.Left;
        _settingsButton.MinimumSize = new Size(ScaleByDpi(HeaderButtonHeight), ScaleByDpi(HeaderButtonHeight));
        _settingsButton.Padding = ScalePadding(2, 0, 2, 0);
        _settingsButton.Margin = new Padding(0);
        _settingsButton.UseMnemonic = false;
        _settingsButton.AccessibleName = "Settings";
        // Its "Settings" tooltip is set with the extension customisations (BuildUiTargetBindings), so it never shows twice.
        _settingsButton.Click += (_, _) => OpenSettingsWindow();

        // A live clock (with seconds) from the computer's clock, to the left of Settings.
        _menuClockLabel.AutoSize = true;
        _menuClockLabel.Anchor = AnchorStyles.Left;
        _menuClockLabel.TextAlign = ContentAlignment.MiddleCenter;
        _menuClockLabel.Font = _menuButtonFontRegular;
        _menuClockLabel.Margin = ScalePadding(6, 0, 10, 0);
        _menuClockLabel.UseMnemonic = false;
        _menuClockLabel.AccessibleName = "Clock";
        _menuClockLabel.AccessibleRole = AccessibleRole.StaticText;
        // The width is reserved for the widest time, so the buttons beside the clock never shift as it ticks.
        _menuClockLabel.FontChanged += (_, _) => UpdateClockMinimumWidth();
        UpdateClock();

        menuLayout.Controls.Add(logoLayout, 0, 0);
        menuLayout.Controls.Add(_logsViewButton, 1, 0);
        menuLayout.Controls.Add(_webViewToggleButton, 2, 0);
        menuLayout.Controls.Add(_extensionMenuButtonsPanel, 3, 0);
        ConfigureTopBarStyleControls();

        menuLayout.Controls.Add(_menuClockLabel, 4, 0);
        menuLayout.Controls.Add(_protectionSwitch, 5, 0);
        menuLayout.Controls.Add(_settingsButton, 6, 0);
        menuLayout.Controls.Add(_topBarRevealButton, 7, 0);
    }

    /// <summary>The gear symbol shown on the Settings button.</summary>
    private const string SettingsGlyph = "⚙";

    /// <summary>
    /// Assembles the top bar: the menu row and (in Web View) the address and tab rows on the left, and (in Web View) the
    /// protection banner on the right. Outside Web View the protection status and button are shown across the window
    /// under the top bar instead (see UpdateHeaderLayout).
    /// </summary>
    private void BuildHeaderLayout()
    {
        _headerLeftLayout.Dock = DockStyle.Fill;
        _headerLeftLayout.ColumnCount = 1;
        _headerLeftLayout.RowCount = 2;
        _headerLeftLayout.Margin = new Padding(0);
        _headerLeftLayout.Padding = new Padding(0);
        _headerLeftLayout.AutoSize = true;
        _headerLeftLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _headerLeftLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _headerLeftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerLeftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _headerLeftLayout.Controls.Add(_menuLayout!, 0, 0);
        _headerLeftLayout.Controls.Add(_addressBarPanel, 0, 1);

        _headerLayout.Dock = DockStyle.Fill;
        _headerLayout.ColumnCount = 2;
        _headerLayout.RowCount = 1;
        _headerLayout.Margin = new Padding(0);
        _headerLayout.Padding = new Padding(0);
        _headerLayout.AutoSize = true;
        _headerLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerLayout.Controls.Add(_headerLeftLayout, 0, 0);
        _headerLayout.Controls.Add(_protectionBanner, 1, 0);

        _menuBarPanel.Controls.Add(_headerLayout);
        UpdateHeaderLayout();
    }

    /// <summary>
    /// Web View: the address and tab rows are shown and the protection banner is a compact card at the right of the top
    /// bar. Logs and extension pages: the top bar only has the menu row, and the protection status and button are shown
    /// across the window under it.
    /// </summary>
    private void UpdateHeaderLayout()
    {
        if (_headerLayout.ColumnStyles.Count < 2 || _headerLeftLayout.RowStyles.Count < 2)
        {
            return;
        }

        // Compact shows these parts only after its drop-down button is used; Simple has the protection switch instead of the
        // banner and the strip (MainForm.TopBar.cs).
        bool webView = _webViewModeEnabled;
        TopBarStyle style = CurrentTopBarStyle;
        bool collapsed = IsTopBarCollapsed;
        bool showAddress = webView && !collapsed;
        bool showBanner = webView && !collapsed && style != TopBarStyle.Simple;
        bool showStrip = !webView && !collapsed && style != TopBarStyle.Simple;
        int bannerGap = style == TopBarStyle.Comfortable ? 12 : 8;
        SuspendLayout();
        _menuBarPanel.SuspendLayout();
        _headerLayout.SuspendLayout();
        try
        {
            _addressBarPanel.Visible = showAddress;
            _headerLeftLayout.RowStyles[1].Height = showAddress ? AddressPanelHeight : 0;
            _protectionBanner.Visible = showBanner;
            _protectionBanner.Margin = ScalePadding(bannerGap, 0, 0, 0);
            _headerLayout.ColumnStyles[1].Width = showBanner ? ScaleByDpi(CompactBannerWidth) + ScaleByDpi(bannerGap) : 0;
            _protectionStripPanel.Visible = showStrip;
            UpdateTopBarRevealButton();
        }
        finally
        {
            _headerLayout.ResumeLayout(true);
            _menuBarPanel.ResumeLayout(true);
            ResumeLayout(true);
        }

        _menuBarPanel.Invalidate(true);
    }

    private void BuildProtectionBanner()
    {
        _protectionBanner.Dock = DockStyle.Fill;
        _protectionBanner.TabIndex = 0;
        _protectionBanner.ToggleRequested += (_, _) => OnProtectionButtonClick(_protectionBanner);

        // Outside Web View: the status banner across the window, with the protection button below it.
        _protectionStatusBar.Dock = DockStyle.Top;
        _protectionStatusBar.Font = _protectionStatusFont;
        _toggleButton.Dock = DockStyle.Bottom;
        _toggleButton.FlatStyle = FlatStyle.Flat;
        _toggleButton.FlatAppearance.BorderSize = 1;
        _toggleButton.UseMnemonic = false;
        _toggleButton.Text = "Enable Protection";
        _toggleButton.Click += (_, _) => OnProtectionButtonClick(_toggleButton);
        _protectionStripPanel.Dock = DockStyle.Top;
        _protectionStripPanel.Controls.Add(_toggleButton);
        _protectionStripPanel.Controls.Add(_protectionStatusBar);
        ApplyProtectionStripMetrics();
        UpdateProtectionStatusVisual();
    }

    // The protection strip of the Logs view: the status banner, the gap and the button (their heights depend on the top
    // bar's style, see TopBarMetrics).
    private const int ProtectionStatusCornerRadius = 12;

    private void ApplyProtectionStripMetrics()
    {
        // Lined up with the rounded top bar above it.
        _protectionStripPanel.Padding = ScalePadding(HeaderOuterMargin, 0, HeaderOuterMargin, HeaderOuterMargin);
        _protectionStatusBar.Height = ScaleByDpi(ProtectionStatusHeight);
        _toggleButton.Height = ScaleByDpi(ProtectionButtonHeight);
        // As rounded as the status banner above it.
        _toggleButton.CornerRadius = ScaleByDpi(ProtectionStatusCornerRadius);
        _protectionStripPanel.Height = _protectionStripPanel.Padding.Vertical + _protectionStatusBar.Height +
                                       ScaleByDpi(ProtectionStripGap) + _toggleButton.Height;
    }

    /// <summary>Shows the computer's current time, including seconds. Never logged: it changes every second.</summary>
    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        string text = now.ToString("T", CultureInfo.CurrentCulture);
        if (!string.Equals(_menuClockLabel.Text, text, StringComparison.Ordinal))
        {
            _menuClockLabel.Text = text;
        }

        // Timers drift; aiming just past the next whole second keeps the seconds in step with the system clock.
        _clockTimer.Interval = Math.Clamp(1000 - now.Millisecond + 15, 50, 1015);
    }

    private void UpdateClockMinimumWidth()
    {
        Font font = _menuClockLabel.Font;
        char widestDigit = "0123456789"
            .OrderByDescending(digit => TextRenderer.MeasureText(digit.ToString(), font).Width)
            .First();

        // Morning and evening times can differ in shape (for example AM/PM), so both are measured.
        int width = 0;
        foreach (DateTime sample in new[] { new DateTime(2000, 1, 1, 10, 0, 0), new DateTime(2000, 1, 1, 22, 0, 0) })
        {
            string text = sample.ToString("T", CultureInfo.CurrentCulture);
            string widest = new(text.Select(character => char.IsDigit(character) ? widestDigit : character).ToArray());
            width = Math.Max(width, TextRenderer.MeasureText(widest, font).Width);
        }

        var minimum = new Size(width + _menuClockLabel.Padding.Horizontal, 0);
        if (_menuClockLabel.MinimumSize != minimum)
        {
            _menuClockLabel.MinimumSize = minimum;
        }
    }

    /// <summary>A pill-shaped button of the menu row (Logs, Web, extension buttons, the overflow button).</summary>
    private void ConfigureMenuButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Dock = DockStyle.None;
        button.Anchor = AnchorStyles.Left;
        TopBarMetrics metrics = CurrentTopBarMetrics;
        button.MinimumSize = new Size(ScaleByDpi(metrics.MenuButtonMinWidth), ScaleByDpi(HeaderButtonHeight));
        button.Padding = ScalePadding(metrics.MenuButtonPaddingX, metrics.MenuButtonPaddingY, metrics.MenuButtonPaddingX, metrics.MenuButtonPaddingY);
        button.Margin = ScalePadding(0, 0, metrics.MenuButtonGap, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
    }

    private void BuildWebViewLoadingOverlay()
    {
        _webViewLoadingOverlayPanel.Dock = DockStyle.Fill;
        _webViewLoadingOverlayPanel.Visible = false;
        _webViewLoadingOverlayPanel.Padding = ScalePadding(24, 24, 24, 24);

        _webViewLoadingLabel.Dock = DockStyle.Fill;
        _webViewLoadingLabel.TextAlign = ContentAlignment.MiddleCenter;
        _webViewLoadingLabel.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _webViewLoadingLabel.Text = "Loading Web View...";
        _webViewLoadingLabel.Margin = ScalePadding(24, 0, 24, 0);

        _webViewLoadingOverlayPanel.Controls.Add(_webViewLoadingLabel);
    }

    private void BuildExtensionLauncherPanel()
    {
        _extensionLauncherPanel.Visible = false;
        _extensionLauncherPanel.AutoSize = true;
        _extensionLauncherPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // With a height limit (set in PositionExtensionLauncherPanel) many launchers wrap into extra columns
        // instead of running off the top of the content area.
        _extensionLauncherPanel.WrapContents = true;
        _extensionLauncherPanel.FlowDirection = FlowDirection.TopDown;
        _extensionLauncherPanel.Padding = new Padding(0);
        _extensionLauncherPanel.Margin = new Padding(0);
        _extensionLauncherPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
    }

    private void PositionExtensionLauncherPanel()
    {
        if (_contentHostPanel.IsDisposed || _extensionLauncherPanel.IsDisposed)
        {
            return;
        }

        int margin = ScaleByDpi(12);
        int availableHeight = Math.Max(ScaleByDpi(48), _contentHostPanel.ClientSize.Height - (margin * 2));
        if (_extensionLauncherPanel.MaximumSize.Height != availableHeight)
        {
            _extensionLauncherPanel.MaximumSize = new Size(0, availableHeight);
        }

        Size preferred = _extensionLauncherPanel.PreferredSize;

        // Stay clear of the history/favourites panel while it is open on the right-hand side.
        int rightEdge = _contentHostPanel.ClientSize.Width;
        if (_historyPanel.Visible)
        {
            rightEdge -= _historyPanel.Width;
        }

        int x = Math.Max(margin, rightEdge - preferred.Width - margin);
        int y = Math.Max(margin, _contentHostPanel.ClientSize.Height - preferred.Height - margin);
        _extensionLauncherPanel.Location = new Point(x, y);

        if (_extensionLauncherPanel.Visible)
        {
            _extensionLauncherPanel.BringToFront();
        }
    }

    /// <summary>
    /// Drop-down menus are separate top-level windows, so the main window's capture exclusion does not cover them.
    /// The current protection state is applied when the menu window is created (before it is first shown) and
    /// again every time it opens, because protection can change in between.
    /// </summary>
    private void ProtectPopupMenuFromCapture(ToolStripDropDown menu)
    {
        void ApplyCurrentProtection()
        {
            if (!menu.IsDisposed && menu.IsHandleCreated)
            {
                // Best effort: a failure leaves the menu capturable but must not stop it from opening.
                _ = NativeMethods.ExcludeFromCapture(menu.Handle, _protectionEnabled);
            }
        }

        menu.HandleCreated += (_, _) => ApplyCurrentProtection();
        menu.Opening += (_, _) => ApplyCurrentProtection();
    }

    private void BuildProtectionDisableMenu()
    {
        _disableProtectionMenu.Items.Clear();

        AddProtectionDisableOption("5 mins", TimeSpan.FromMinutes(5));
        AddProtectionDisableOption("10 mins", TimeSpan.FromMinutes(10));
        AddProtectionDisableOption("30 mins", TimeSpan.FromMinutes(30));
        AddProtectionDisableOption("1 hour", TimeSpan.FromHours(1));
        AddProtectionDisableOption("8 hours", TimeSpan.FromHours(8));
        AddProtectionDisableOption("1 day", TimeSpan.FromDays(1));
        AddProtectionDisableOption("Until I turn it on again", null);
    }

    private void AddProtectionDisableOption(string label, TimeSpan? duration)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => DisableProtectionForDuration(label, duration);
        _disableProtectionMenu.Items.Add(item);
    }

    /// <summary>
    /// The yellow "new version" banner above the protection banner. It stays hidden unless the startup update check
    /// (CheckForUpdatesAsync) finds that this build does not match the latest release on its channel.
    /// </summary>
    private void BuildUpdateBanner()
    {
        _updateBannerPanel.Dock = DockStyle.Top;
        _updateBannerPanel.Visible = false;
        _updateBannerPanel.Height = ScaleByDpi(36);
        _updateBannerPanel.Padding = ScalePadding(12, 3, 6, 3);
        _updateBannerPanel.Margin = new Padding(0);
        _updateBannerPanel.BorderStyle = BorderStyle.FixedSingle;
        _updateBannerPanel.ColumnCount = 4;
        _updateBannerPanel.RowCount = 1;
        _updateBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _updateBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _updateBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _updateBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _updateBannerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _updateBannerPanel.AccessibleName = "PhantomHaze update available";
        _updateBannerPanel.AccessibleRole = AccessibleRole.Alert;

        _updateBannerTitleLabel.Text = "You're missing out on new features!";
        _updateBannerTitleLabel.AutoSize = true;
        _updateBannerTitleLabel.Dock = DockStyle.Fill;
        _updateBannerTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _updateBannerTitleLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _updateBannerTitleLabel.Margin = ScalePadding(0, 0, 4, 0);
        _updateBannerTitleLabel.UseMnemonic = false;

        // Takes the remaining width and is shortened with "..." (the full text then shows as a tooltip) when the
        // window is too narrow for the whole sentence, so the Update and X buttons always stay visible.
        _updateBannerMessageLabel.Text = "Update to the latest version of PhantomHaze to get the latest features!";
        _updateBannerMessageLabel.Dock = DockStyle.Fill;
        _updateBannerMessageLabel.AutoEllipsis = true;
        _updateBannerMessageLabel.TextAlign = ContentAlignment.MiddleLeft;
        _updateBannerMessageLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _updateBannerMessageLabel.Margin = new Padding(0);
        _updateBannerMessageLabel.UseMnemonic = false;

        _updateBannerUpdateButton.Text = "Update";
        _updateBannerUpdateButton.AutoSize = true;
        _updateBannerUpdateButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _updateBannerUpdateButton.Anchor = AnchorStyles.None;
        _updateBannerUpdateButton.FlatStyle = FlatStyle.Flat;
        _updateBannerUpdateButton.FlatAppearance.BorderSize = 1;
        _updateBannerUpdateButton.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _updateBannerUpdateButton.MinimumSize = new Size(ScaleByDpi(76), 0);
        _updateBannerUpdateButton.Padding = ScalePadding(10, 0, 10, 0);
        _updateBannerUpdateButton.Margin = ScalePadding(8, 0, 6, 0);
        _updateBannerUpdateButton.AccessibleDescription = "Opens the PhantomHaze releases page on GitHub in a new tab.";
        _updateBannerUpdateButton.Click += (_, _) =>
            _ = OpenUrlInNewTabAsync(UpdateReminder.ReleasesPageUrl, "PhantomHaze releases page");

        // The X does not dismiss the reminder for good: it opens the menu of (limited) snooze lengths.
        _updateBannerSnoozeButton.Text = "X";
        _updateBannerSnoozeButton.AutoSize = false;
        _updateBannerSnoozeButton.Size = new Size(ScaleByDpi(30), ScaleByDpi(26));
        _updateBannerSnoozeButton.Anchor = AnchorStyles.None;
        _updateBannerSnoozeButton.FlatStyle = FlatStyle.Flat;
        _updateBannerSnoozeButton.FlatAppearance.BorderSize = 0;
        _updateBannerSnoozeButton.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _updateBannerSnoozeButton.Margin = new Padding(0);
        _updateBannerSnoozeButton.AccessibleName = "Snooze update reminder";
        _updateBannerSnoozeButton.Click += (_, _) => ShowUpdateBannerSnoozeMenu();
        _updateBannerToolTip.SetToolTip(_updateBannerSnoozeButton, "Snooze this reminder");

        _updateBannerSnoozeMenu.ShowImageMargin = false;
        _updateBannerSnoozeMenu.Items.Add(new ToolStripLabel("How long should we snooze this reminder for?")
        {
            Font = new Font(_updateBannerSnoozeMenu.Font, FontStyle.Bold),
        });
        _updateBannerSnoozeMenu.Items.Add(new ToolStripSeparator());
        foreach (UpdateReminderSnoozeOption option in UpdateReminder.SnoozeOptions)
        {
            var item = new ToolStripMenuItem(option.Label);
            item.Click += (_, _) => SnoozeUpdateBanner(option);
            _updateBannerSnoozeMenu.Items.Add(item);
        }

        _updateBannerPanel.Controls.Add(_updateBannerTitleLabel, 0, 0);
        _updateBannerPanel.Controls.Add(_updateBannerMessageLabel, 1, 0);
        _updateBannerPanel.Controls.Add(_updateBannerUpdateButton, 2, 0);
        _updateBannerPanel.Controls.Add(_updateBannerSnoozeButton, 3, 0);
    }

    private void ShowUpdateBannerSnoozeMenu()
    {
        // Opens below the X and extends to the left, so it does not run off the right-hand edge of the window.
        _updateBannerSnoozeMenu.Show(
            _updateBannerSnoozeButton,
            new Point(_updateBannerSnoozeButton.Width, _updateBannerSnoozeButton.Height),
            ToolStripDropDownDirection.BelowLeft);
    }

    private void SnoozeUpdateBanner(UpdateReminderSnoozeOption option)
    {
        DateTimeOffset snoozedUntil = option.GetSnoozeEnd(DateTimeOffset.UtcNow);
        _updateBannerPanel.Visible = false;

        try
        {
            UpdateReminderStore.Save(_updateReminderFilePath, new UpdateReminderState
            {
                SnoozedUntilUtc = snoozedUntil,
                SnoozedOnVersion = AppVersionInfo.Tag?.ToString(),
            });
            Log($"Update reminder snoozed for {option.Label} (until {FormatLocalTimeForLog(snoozedUntil)}).");
        }
        catch (Exception ex)
        {
            Log($"Update reminder hidden for this session only; the snooze could not be saved: {ex.Message}");
        }
    }

    private void ApplyThemeToUpdateBanner()
    {
        bool darkTheme = IsDarkTheme();
        Color bannerBackColor = darkTheme ? Color.FromArgb(125, 104, 30) : Color.FromArgb(255, 226, 110);
        Color bannerTextColor = darkTheme ? Color.White : Color.Black;

        // The labels inherit the banner background; the Update button is themed with the other command buttons.
        _updateBannerPanel.BackColor = bannerBackColor;
        _updateBannerTitleLabel.ForeColor = bannerTextColor;
        _updateBannerMessageLabel.ForeColor = bannerTextColor;
        _updateBannerSnoozeButton.BackColor = bannerBackColor;
        _updateBannerSnoozeButton.ForeColor = bannerTextColor;
        _updateBannerSnoozeButton.FlatAppearance.MouseOverBackColor = darkTheme ? Color.FromArgb(148, 125, 44) : Color.FromArgb(242, 206, 82);
        _updateBannerSnoozeButton.FlatAppearance.MouseDownBackColor = darkTheme ? Color.FromArgb(166, 142, 56) : Color.FromArgb(228, 189, 60);
    }

    /// <summary>
    /// Runs at every startup. Shows the update banner when this build does not match the latest release on its own
    /// channel (see UpdateReminder). Nothing is downloaded while the reminder is snoozed, and a failed check (offline,
    /// GitHub unavailable, unexpected ver-banner.csv contents) only writes a log line and leaves the banner hidden.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            if (AppVersionInfo.Tag is not ReleaseTag currentRelease)
            {
                Log($"Update check skipped: this build's version '{NormalizeForLog(AppVersionInfo.InformationalVersion)}' " +
                    "is not a vX.Y.Z-stable or vX.Y.Z-beta release tag.");
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            UpdateReminderState reminderState = UpdateReminderStore.Load(_updateReminderFilePath, out string? loadProblem);
            if (loadProblem is not null)
            {
                Log($"The saved update reminder state could not be read and was ignored: {loadProblem}");
            }
            else if (UpdateReminder.IsSnoozeTooLong(reminderState.SnoozedUntilUtc, now))
            {
                Log($"A saved update reminder snooze until {FormatLocalTimeForLog(reminderState.SnoozedUntilUtc!.Value)} " +
                    "is longer than any snooze option allows and was ignored.");
            }
            else if (UpdateReminder.IsSnoozed(reminderState.SnoozedUntilUtc, now) &&
                     !UpdateReminder.WasSnoozedOn(reminderState, currentRelease))
            {
                Log($"The update reminder was snoozed on {NormalizeForLog(reminderState.SnoozedOnVersion ?? "another build")}, " +
                    $"so the snooze does not apply to PhantomHaze {currentRelease}.");
            }

            if (UpdateReminder.IsSnoozed(reminderState, currentRelease, now))
            {
                Log($"Update check skipped for PhantomHaze {currentRelease}: the update reminder is snoozed until " +
                    $"{FormatLocalTimeForLog(reminderState.SnoozedUntilUtc!.Value)}.");
                return;
            }

            string publishedReleases = await DownloadPublishedReleasesAsync(_updateCheckCancellation.Token);
            if (IsDisposed || _updateCheckCancellation.IsCancellationRequested)
            {
                return;
            }

            string channelName = ReleaseTag.GetChannelName(currentRelease.Channel);
            if (!UpdateReminder.TryGetLatestRelease(publishedReleases, currentRelease.Channel, out ReleaseTag latestRelease, out string problem))
            {
                Log($"Update check failed: {problem}");
                return;
            }

            if (!UpdateReminder.ShouldShowBanner(currentRelease, latestRelease))
            {
                Log(UpdateReminder.CompareVersions(currentRelease, latestRelease) > 0
                    ? $"Update check: PhantomHaze {currentRelease} is newer than the latest published {channelName} release ({latestRelease})."
                    : $"Update check: PhantomHaze {currentRelease} is the latest {channelName} release.");
                return;
            }

            Log($"Update check: this is PhantomHaze {currentRelease} and the latest {channelName} release is {latestRelease}; " +
                "showing the update banner.");
            _updateBannerPanel.Visible = true;
        }
        catch (OperationCanceledException) when (_updateCheckCancellation.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            Log($"Update check failed: {ex.Message}");
        }
    }

    private static async Task<string> DownloadPublishedReleasesAsync(CancellationToken cancellationToken)
    {
        // One small request per startup, so a short-lived client is fine. HttpClient never follows a redirect from
        // https to http, and the buffer limit stops an unexpectedly large response from being read into memory.
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 64 * 1024,
        };

        using HttpResponseMessage response = await httpClient.GetAsync(UpdateReminder.PublishedReleasesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string FormatLocalTimeForLog(DateTimeOffset value)
    {
        return value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The Web View rows of the top bar: the address box (a pill with the favourite star) with Back, Go, Refresh and
    /// History, and below it the tabs, the new tab (+) button and the zoom controls.
    /// </summary>
    private void BuildAddressBar()
    {
        _addressBarPanel.Dock = DockStyle.Fill;
        _addressBarPanel.Height = AddressPanelHeight;
        _addressBarPanel.Margin = new Padding(0);
        _addressBarPanel.Padding = new Padding(0);
        _addressBarPanel.Visible = false;
        _addressBarPanel.SizeChanged += (_, _) =>
        {
            UpdateTabLayout();
            UpdateAddressStripLayout();
        };

        // Rows: the address strip, then the tabs. The second column is only used by the Simple top bar, which puts the
        // tabs beside the address strip (ArrangeAddressRows).
        _addressRootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        TableLayoutPanel rootLayout = _addressRootLayout;
        rootLayout.RowCount = 3;
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(AddressRowHeight)));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, FavoritesBarHeight));

        // Columns: address box (stretches) | Back (←) | Forward (→) | Go | Refresh | History.
        _addressStripLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        TableLayoutPanel stripLayout = _addressStripLayout;
        stripLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(AddressRowHeight)));
        // (UpdateAddressStripLayout sets the widths, and the order for the Simple top bar.)
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int stripColumn = 1; stripColumn < 6; stripColumn++)
        {
            stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(CurrentTopBarMetrics.StripButtonWidth)));
        }

        // The address box is a pill; the text box inside it has no border of its own and follows the pill's colour.
        _addressPill.Dock = DockStyle.Fill;
        var addressInputLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = ScalePadding(16, 0, 4, 0),
        };
        _addressInputLayout = addressInputLayout;
        // It fills the whole pill, so a solid background would paint over the pill's rounded ends and make the address
        // box look square: it always shows the pill through it instead.
        addressInputLayout.BackColor = Color.Transparent;
        addressInputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addressInputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(34)));
        addressInputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // Anchored left and right only, so the single-line box is centred vertically in the pill.
        _addressBarTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _addressBarTextBox.BorderStyle = BorderStyle.None;
        _addressBarTextBox.TextAlign = HorizontalAlignment.Center;
        _addressBarTextBox.Margin = new Padding(0);
        _addressBarTextBox.PlaceholderText = _settings.StartingPage;
        _addressBarTextBox.AccessibleName = "Address";
        _addressBarTextBox.BackColorChanged += (_, _) => _addressPill.BackColor = _addressBarTextBox.BackColor;
        _addressBarTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateToAddressBarUrl();
            }
        };

        _favoriteButton.Text = "✩";
        _favoriteButton.Anchor = AnchorStyles.None;
        _favoriteButton.AutoSize = false;
        _favoriteButton.Size = new Size(ScaleByDpi(30), ScaleByDpi(30));
        _favoriteButton.Margin = new Padding(0);
        _favoriteButton.Padding = new Padding(0);
        _favoriteButton.Font = _favoriteButtonFont;
        _favoriteButton.FlatStyle = FlatStyle.Flat;
        _favoriteButton.FlatAppearance.BorderSize = 0;
        _favoriteButton.Click += (_, _) => ToggleFavoriteForCurrentPage();
        _favoriteButton.TabStop = false;

        addressInputLayout.Controls.Add(_addressBarTextBox, 0, 0);
        addressInputLayout.Controls.Add(_favoriteButton, 1, 0);
        _addressPill.Controls.Add(addressInputLayout);

        // The buttons show arrows and icons (their names are their tooltips, set in BuildUiTargetBindings).
        _backButton.Dock = DockStyle.Fill;
        _backButton.Enabled = false;
        ConfigureStripButton(_backButton);
        _backButton.Text = BackGlyph;
        _backButton.Font = _navigationGlyphFont;
        _backButton.AccessibleName = "Back";
        _backButton.Click += (_, _) => GoBack();

        _forwardButton.Dock = DockStyle.Fill;
        _forwardButton.Enabled = false;
        ConfigureStripButton(_forwardButton);
        _forwardButton.Text = ForwardGlyph;
        _forwardButton.Font = _navigationGlyphFont;
        _forwardButton.AccessibleName = "Forward";
        _forwardButton.Click += (_, _) => GoForward();

        _goButton.Dock = DockStyle.Fill;
        ConfigureStripButton(_goButton);
        _goButton.Text = GoGlyph;
        _goButton.Font = _stripGlyphFont;
        _goButton.AccessibleName = "Go";
        _goButton.Click += (_, _) => NavigateToAddressBarUrl();

        _refreshButton.Dock = DockStyle.Fill;
        ConfigureStripButton(_refreshButton);
        _refreshButton.Text = RefreshGlyph;
        _refreshButton.Font = _stripGlyphFont;
        _refreshButton.AccessibleName = "Refresh";
        _refreshButton.Click += (_, _) => RefreshWebView();

        _historyButton.Dock = DockStyle.Fill;
        ConfigureStripButton(_historyButton);
        _historyButton.Text = HistoryGlyph;
        _historyButton.Font = _stripGlyphFont;
        _historyButton.AccessibleName = "History";
        _historyButton.Click += (_, _) => ToggleHistoryPanel();

        stripLayout.Controls.Add(_addressPill, 0, 0);
        stripLayout.Controls.Add(_backButton, 1, 0);
        stripLayout.Controls.Add(_forwardButton, 2, 0);
        stripLayout.Controls.Add(_goButton, 3, 0);
        stripLayout.Controls.Add(_refreshButton, 4, 0);
        stripLayout.Controls.Add(_historyButton, 5, 0);

        _zoomLabel.Text = "Zoom 100%";
        _zoomLabel.Dock = DockStyle.Fill;
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        _zoomLabel.Margin = ScalePadding(3, 2, 3, 2);
        _zoomLabel.Font = _zoomLabelFont;

        _zoomSlider.Minimum = 50;
        _zoomSlider.Maximum = 200;
        _zoomSlider.Value = 100;
        _zoomSlider.TickFrequency = 10;
        _zoomSlider.AutoSize = false;
        _zoomSlider.Dock = DockStyle.Fill;
        _zoomSlider.Margin = ScalePadding(3, 8, 3, 2);
        _zoomSlider.Scroll += (_, _) => ApplyZoom();

        // Columns: tabs (stretches) | new tab (+) | zoom label | zoom slider.
        _tabsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        TableLayoutPanel tabsLayout = _tabsLayout;
        tabsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tabsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(44)));
        tabsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(CurrentTopBarMetrics.ZoomLabelWidth)));
        tabsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(CurrentTopBarMetrics.ZoomSliderWidth)));
        tabsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _tabsFlowPanel.Dock = DockStyle.Fill;
        _tabsFlowPanel.FlowDirection = FlowDirection.LeftToRight;
        _tabsFlowPanel.WrapContents = false;
        _tabsFlowPanel.AutoScroll = true;
        _tabsFlowPanel.Margin = new Padding(0);
        _tabsFlowPanel.Padding = new Padding(0);
        _tabsFlowPanel.SizeChanged += (_, _) => UpdateTabLayout();

        // A double-click next to the tabs opens a new one (the only way with the mouse in the Simple top bar).
        _tabsFlowPanel.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                await CreateTabAsync(GetConfiguredStartPage(), activate: true);
            }
        };

        _newTabButton.Text = "+";
        _newTabButton.Anchor = AnchorStyles.None;
        _newTabButton.Font = _newTabButtonFont;
        _newTabButton.TextAlign = ContentAlignment.MiddleCenter;
        _newTabButton.FlatStyle = FlatStyle.Flat;
        _newTabButton.FlatAppearance.BorderSize = 0;
        _newTabButton.AutoSize = false;
        _newTabButton.Size = new Size(ScaleByDpi(32), ScaleByDpi(32));
        _newTabButton.MinimumSize = new Size(ScaleByDpi(32), ScaleByDpi(32));
        _newTabButton.Padding = new Padding(0);
        _newTabButton.Margin = new Padding(0);
        _newTabButton.Click += async (_, _) => await CreateTabAsync(GetConfiguredStartPage(), activate: true);

        tabsLayout.Controls.Add(_tabsFlowPanel, 0, 0);
        tabsLayout.Controls.Add(_newTabButton, 1, 0);
        tabsLayout.Controls.Add(_zoomLabel, 2, 0);
        tabsLayout.Controls.Add(_zoomSlider, 3, 0);

        rootLayout.Controls.Add(stripLayout, 0, 0);
        rootLayout.Controls.Add(tabsLayout, 0, 1);
        rootLayout.SetColumnSpan(stripLayout, 2);
        rootLayout.SetColumnSpan(tabsLayout, 2);
        rootLayout.Controls.Add(_favoritesBar, 0, 2);
        rootLayout.SetColumnSpan(_favoritesBar, 2);
        ProtectPopupMenuFromCapture(_favoritesBar.OverflowButton.DropDown);

        _addressBarPanel.Controls.Add(rootLayout);
        UpdateAddressStripLayout();
        UpdateFavoriteButtonState();
    }

    private void ConfigureStripButton(Button button)
    {
        button.AutoSize = false;
        button.Margin = ScalePadding(4, 5, 0, 5);
        button.Font = _stripButtonFont;
        button.UseMnemonic = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
    }

    private void BuildHistoryPanel()
    {
        _historyPanel.Dock = DockStyle.Right;
        _historyPanel.Width = ScaleByDpi(380);
        _historyPanel.Padding = new Padding(ScaleByDpi(8));
        _historyPanel.Visible = false;
        _historyPanel.BackColor = Color.FromArgb(245, 248, 252);

        _historyTitleLabel.Text = GetHistoryTitleText();
        _historyTitleLabel.Dock = DockStyle.Fill;
        _historyTitleLabel.Height = 28;
        _historyTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _historyTitleLabel.Font = new Font("Segoe UI", 10, FontStyle.Bold);

        _favoritesTitleLabel.Text = "Favourites";
        _favoritesTitleLabel.Dock = DockStyle.Fill;
        _favoritesTitleLabel.Height = 28;
        _favoritesTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _favoritesTitleLabel.Font = new Font("Segoe UI", 10, FontStyle.Bold);

        _historyClearButton.Text = "Clear History";
        _historyClearButton.Dock = DockStyle.Fill;
        _historyClearButton.Height = 32;
        _historyClearButton.Click += (_, _) =>
        {
            _historyEntries.Clear();
            RefreshHistoryPanelItems();
            SaveHistory();
            Log("Web history cleared manually.");
        };

        _historyListBox.Dock = DockStyle.Fill;
        _historyListBox.Font = new Font("Segoe UI", 9);
        _historyListBox.DoubleClick += (_, _) => NavigateFromHistorySelection();
        _historyListBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromHistorySelection();
            }
        };

        _favoritesListBox.Dock = DockStyle.Fill;
        _favoritesListBox.Font = new Font("Segoe UI", 9);
        _favoritesListBox.DoubleClick += (_, _) => NavigateFromFavoritesSelection();
        _favoritesListBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromFavoritesSelection();
            }
        };

        _historyLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        TableLayoutPanel historyLayout = _historyLayout;

        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));

        historyLayout.Controls.Add(_historyTitleLabel, 0, 0);
        historyLayout.Controls.Add(_historyListBox, 0, 1);
        historyLayout.Controls.Add(_favoritesTitleLabel, 0, 2);
        historyLayout.Controls.Add(_favoritesListBox, 0, 3);
        historyLayout.Controls.Add(_historyClearButton, 0, 4);

        _historyPanel.Controls.Add(historyLayout);
        _historyPanel.VisibleChanged += (_, _) => PositionExtensionLauncherPanel();

        RefreshHistoryPanelItems();
        RefreshFavoritesPanelItems();
    }

    private void BuildFindPanel()
    {
        _findPanel.Visible = false;
        _findPanel.Size = new Size(ScaleByDpi(360), ScaleByDpi(42));
        _findPanel.BackColor = Color.FromArgb(250, 252, 255);
        _findPanel.BorderStyle = BorderStyle.FixedSingle;
        _findPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _findLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(ScaleByDpi(6)),
        };
        TableLayoutPanel findLayout = _findLayout;
        findLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        findLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(80)));
        findLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(28)));

        _findTextBox.Dock = DockStyle.Fill;
        _findTextBox.PlaceholderText = "Find in page";
        _findTextBox.Margin = ScalePadding(0, 0, 6, 0);
        _findTextBox.TextChanged += async (_, _) => await ApplyFindQueryAsync();
        _findTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                HideFindPanel(clearHighlights: true);
            }
        };

        _findCountLabel.Dock = DockStyle.Fill;
        _findCountLabel.TextAlign = ContentAlignment.MiddleCenter;
        _findCountLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);

        _findCloseButton.Text = "X";
        _findCloseButton.Dock = DockStyle.Fill;
        _findCloseButton.Margin = new Padding(0);
        _findCloseButton.Click += (_, _) => HideFindPanel(clearHighlights: true);

        findLayout.Controls.Add(_findTextBox, 0, 0);
        findLayout.Controls.Add(_findCountLabel, 1, 0);
        findLayout.Controls.Add(_findCloseButton, 2, 0);

        _findPanel.Controls.Add(findLayout);
    }

    private async Task SetWebViewModeAsync(bool enableWebView)
    {
        if (_webViewModeSwitchInProgress)
        {
            return;
        }

        // An extension page (app.page) is a third view: from there, Logs and Web View always switch.
        bool extensionPageShown = _activeExtensionPageKey is not null;
        if (_webViewModeEnabled == enableWebView && !extensionPageShown)
        {
            UpdateWebViewToggleVisual();
            return;
        }

        _webViewModeSwitchInProgress = true;
        try
        {
            if (enableWebView)
            {
                if (_tabs.Count == 0)
                {
                    BrowserTab? firstTab = await CreateTabAsync(GetConfiguredStartPage(), activate: true);
                    if (firstTab is null)
                    {
                        return;
                    }
                }

                HideExtensionPageView();
                _webViewModeEnabled = true;
                UpdateHeaderLayout();
                _webViewHostPanel.Visible = true;
                _logBox.Visible = false;
                _historyPanel.Visible = false;
                _historyButton.BackColor = GetCommandButtonBackColor();

                if (_activeTab is null && _tabs.Count > 0)
                {
                    ActivateTab(_tabs[0]);
                }
                else if (_activeTab is not null)
                {
                    ActivateTab(_activeTab);
                }

                UpdateTabVisibilityAndRendering();
                UpdateWebViewToggleVisual();
                PositionFindPanel();
                UpdateZoomControlsForActiveTab();

                int switchVersion = ++_webViewModeSwitchVersion;
                ShowWebViewLoadingOverlay();
                await WaitForActiveWebViewDisplayReadinessAsync(switchVersion);

                if (_webViewModeEnabled && switchVersion == _webViewModeSwitchVersion)
                {
                    HideWebViewLoadingOverlay();
                }

                Log("Web View mode enabled.");
                return;
            }

            HideExtensionPageView();
            bool wasWebView = _webViewModeEnabled;
            if (wasWebView)
            {
                LeaveWebViewMode();
            }

            ShowLogPanel();
            Log(wasWebView ? "Web View mode disabled; log panel restored." : "Log panel restored.");
        }
        finally
        {
            _webViewModeSwitchInProgress = false;
        }
    }

    /// <summary>Hides Web View (address bar, tabs, history and find panels) without deciding what is shown instead.</summary>
    private void LeaveWebViewMode()
    {
        _webViewModeSwitchVersion++;
        HideWebViewLoadingOverlay();
        _webViewModeEnabled = false;
        RefreshInspector();
        _historyPanel.Visible = false;
        _historyButton.BackColor = GetCommandButtonBackColor();
        HideFindPanel(clearHighlights: true);
        UpdateHeaderLayout();
        _webViewHostPanel.Visible = false;
        UpdateTabVisibilityAndRendering();
        UpdateZoomControlsForActiveTab();
        UpdateNavigationButtons();
        UpdateFavoriteButtonState();
    }

    private void ShowLogPanel()
    {
        _logBox.Visible = true;
        _logBox.BringToFront();
        // BringToFront put the log box above the extension launcher; restore the launcher on top.
        PositionExtensionLauncherPanel();
        UpdateWebViewToggleVisual();
    }

    private void ShowWebViewLoadingOverlay()
    {
        _webViewLoadingOverlayPanel.Visible = true;
        _webViewLoadingOverlayPanel.BringToFront();
    }

    private void HideWebViewLoadingOverlay()
    {
        _webViewLoadingOverlayPanel.Visible = false;
    }

    private async Task WaitForActiveWebViewDisplayReadinessAsync(int switchVersion)
    {
        BrowserTab? tab = _activeTab;
        if (!_webViewModeEnabled || tab is null || !tab.Page.IsReady)
        {
            return;
        }

        Task minimumOverlayTask = Task.Delay(180);
        // A page that had stopped rendering in the background takes a little longer to draw again.
        bool wasSuspended = tab.ResumedFromSuspension;
        tab.ResumedFromSuspension = false;
        bool mustWaitForNavigation = tab.IsNavigationPending || IsTabShowingPlaceholder(tab);
        if (mustWaitForNavigation)
        {
            await Task.WhenAll(
                minimumOverlayTask,
                WaitForNavigationCompletionAsync(tab, TimeSpan.FromSeconds(8)));
        }
        else
        {
            int stabilizationDelay = wasSuspended ? 220 : 75;
            await Task.WhenAll(minimumOverlayTask, Task.Delay(stabilizationDelay));
        }

        if (switchVersion != _webViewModeSwitchVersion || !_webViewModeEnabled)
        {
            return;
        }
    }

    private async Task WaitForNavigationCompletionAsync(BrowserTab tab, TimeSpan timeout)
    {
        if (!tab.Page.IsReady || !tab.IsNavigationPending)
        {
            return;
        }

        WebProtocolPage page = tab.Page;
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(object? sender, WebPageNavigationCompletedEventArgs args)
        {
            completionSource.TrySetResult(true);
        }

        page.NavigationCompleted += OnNavigationCompleted;
        try
        {
            using var timeoutToken = new CancellationTokenSource(timeout);
            using CancellationTokenRegistration registration = timeoutToken.Token.Register(() => completionSource.TrySetResult(false));
            await completionSource.Task;
        }
        finally
        {
            page.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private static bool IsTabShowingPlaceholder(BrowserTab tab)
    {
        string source = tab.Page.CurrentUrl;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = tab.CurrentUrl;
        }

        return string.IsNullOrWhiteSpace(source) ||
               source.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Highlights the active view: Logs, Web View or one of the extension pages.</summary>
    private void UpdateWebViewToggleVisual()
    {
        ThemePalette palette = _palette;

        void StyleViewButton(Button button, bool selected)
        {
            button.BackColor = selected ? palette.ViewSelectedBack : palette.ViewNormalBack;
            button.ForeColor = ResolveTextColor(selected ? palette.ViewSelectedText : palette.ViewNormalText);
        }

        StyleViewButton(_logsViewButton, IsLogsViewSelected);
        StyleViewButton(_webViewToggleButton, _webViewModeEnabled);
        foreach (Button button in _extensionMenuButtonsPanel.Controls.OfType<Button>())
        {
            if (button.Tag is ExtensionMenuEntry { Kind: ExtensionMenuEntryKind.Page } entry)
            {
                StyleViewButton(button, IsActiveExtensionPage(entry));
            }
        }

        // Fonts (bold marks the selected view) and extension customisations are applied on top.
        ApplyUiElementState();
    }

    private bool IsLogsViewSelected => !_webViewModeEnabled && _activeExtensionPageKey is null;

    /// <summary>
    /// Shows the active tab's page (in Web View) and hides the others. Hidden tabs keep rendering for the time chosen
    /// in Settings, then stop (see ApplyBackgroundTabRendering).
    /// </summary>
    private void UpdateTabVisibilityAndRendering()
    {
        DateTime now = DateTime.UtcNow;
        foreach (BrowserTab tab in _tabs)
        {
            bool isActiveTab = _webViewModeEnabled && ReferenceEquals(tab, _activeTab);
            tab.Page.View.Visible = isActiveTab;
            if (isActiveTab)
            {
                tab.BackgroundSinceUtc = null;
            }
            else if (tab.BackgroundSinceUtc is null)
            {
                tab.BackgroundSinceUtc = now;
                tab.ResumedFromSuspension = false;
            }
        }

        ApplyBackgroundTabRendering();
    }

    /// <summary>
    /// Opens a tab with the Web View protocol chosen in Settings (WebView2 unless another one is chosen and can be used).
    /// </summary>
    private async Task<BrowserTab?> CreateTabAsync(string? rawUrl, bool activate)
    {
        WebProtocol protocol = ResolveSelectedWebProtocol();
        WebProtocolPage page;
        try
        {
            page = protocol.CreatePage(ProtocolHostInstance);
        }
        catch (Exception ex)
        {
            Log($"Web tab could not be created with the {protocol.DisplayName} protocol: {ex.Message}");
            ShowTabCreationFailure(protocol, ex);
            return null;
        }

        Panel tabHeader = CreateTabHeaderPanel(out PictureBox faviconBox, out Label titleLabel, out Button closeButton);
        var tab = new BrowserTab(_nextTabId++, protocol, page, tabHeader, faviconBox, titleLabel, closeButton);

        tabHeader.Tag = tab;
        titleLabel.Tag = tab;
        faviconBox.Tag = tab;

        tabHeader.Click += OnTabHeaderClicked;
        titleLabel.Click += OnTabHeaderClicked;
        faviconBox.Click += OnTabHeaderClicked;
        closeButton.Click += async (_, _) => await CloseTabAsync(tab);

        _tabs.Add(tab);
        _tabsFlowPanel.Controls.Add(tabHeader);
        page.View.Visible = false;
        _webViewHostPanel.Controls.Add(page.View);
        AttachPageToTab(tab, page);

        try
        {
            await page.InitializeAsync();
            if (!page.IsReady)
            {
                // Closed (or the window closed) while the page was starting up.
                if (_tabs.Contains(tab) && ReferenceEquals(tab.Page, page))
                {
                    RemoveTabAndDispose(tab);
                    UpdateTabLayout();
                }

                return null;
            }

            if (page.Features.HasFlag(WebProtocolFeatures.Zoom))
            {
                page.SetZoom(tab.ZoomPercent);
            }
        }
        catch (Exception ex)
        {
            Log($"Web tab initialization failed ({protocol.DisplayName} protocol): {ex.Message}");
            if (_tabs.Contains(tab))
            {
                RemoveTabAndDispose(tab);
            }

            ShowTabCreationFailure(protocol, ex);
            UpdateTabLayout();
            return null;
        }

        if (!_tabs.Contains(tab) || !ReferenceEquals(tab.Page, page))
        {
            return null;
        }

        string normalizedUrl = NormalizeOrDefaultUrl(rawUrl);
        tab.CurrentUrl = normalizedUrl;

        if (activate)
        {
            ActivateTab(tab);
        }
        else
        {
            UpdateTabHeaderVisuals();
            UpdateTabLayout();
            UpdateTabVisibilityAndRendering();
        }

        NavigateTabToUrl(tab, normalizedUrl, "Tab opened");
        return tab;
    }

    private void ShowTabCreationFailure(WebProtocol protocol, Exception error)
    {
        string message = protocol is WebView2WebProtocol || protocol.Id == WebProtocolCatalog.WebView2Id
            ? "A browser tab could not be created. Install Microsoft Edge WebView2 Runtime on the Windows host, " +
              "or choose another Web View protocol in Settings -> General."
            : $"A browser tab could not be created with the {protocol.DisplayName} protocol:\n{error.Message}\n\n" +
              "Choose another Web View protocol in Settings -> General.";
        ShowCaptureProtectedDialog(() => MessageBox.Show(
            message,
            "PhantomHaze",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning));
    }

    /// <summary>A tab: a pill with the site's icon, its title and a round close (X) button.</summary>
    private Panel CreateTabHeaderPanel(
        out PictureBox faviconBox,
        out Label titleLabel,
        out Button closeButton)
    {
        var header = new PillPanel
        {
            Height = ScaleByDpi(34),
            Width = ScaleByDpi(180),
            Margin = ScalePadding(0, 4, 6, 0),
            Padding = ScalePadding(10, 5, 5, 5),
            BackColor = _palette.TabInactiveBack,
            Cursor = Cursors.Hand,
        };

        faviconBox = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = ScaleByDpi(16),
            Height = ScaleByDpi(16),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = DefaultTabIcon,
            Margin = new Padding(0),
            Cursor = Cursors.Hand,
        };
        // A paused background tab shows 💤 instead of its icon.
        faviconBox.Paint += OnTabFaviconPaint;

        var closePill = new PillButton
        {
            Dock = DockStyle.Right,
            Width = ScaleByDpi(24),
            Text = "X",
            Font = MapFont(_tabCloseFontRegular),
            Tag = TabCloseButtonTag,
            AccessibleName = "Close tab",
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(0),
            Margin = new Padding(0),
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        closePill.FlatAppearance.BorderSize = 0;
        closeButton = closePill;

        titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = "New Tab",
            // Centred between the icon and the X, as in the v1.3.6 design (long titles fill the space and end in "...").
            TextAlign = ContentAlignment.MiddleCenter,
            Font = _tabTitleFont,
            UseMnemonic = false,
            Padding = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };

        header.Controls.Add(titleLabel);
        header.Controls.Add(closeButton);
        header.Controls.Add(faviconBox);
        RegisterOriginalFonts(header);
        ApplyAppFontToTree(header);
        return header;
    }

    private void AddWebResourceRequestedFilterForAllFrames(CoreWebView2 core)
    {
        try
        {
            // The two-argument overload is deprecated: it only reports requests from the main frame and same-origin
            // iframes, so web.block rules were never applied to cross-origin iframes. Document covers the page, all of
            // its iframes and their dedicated workers; service and shared workers have their own filter
            // (EnsureWorkerRequestFilter).
            core.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.Document);
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException)
        {
            Log("This WebView2 runtime cannot filter requests from all frames; extension blocking covers the main frame only.");
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
    }

    private bool TryGetTabFromSender(object? sender, out BrowserTab tab)
    {
        tab = null!;
        if (sender is not CoreWebView2 core)
        {
            return false;
        }

        if (!_tabByCore.TryGetValue(core, out BrowserTab? mappedTab) || mappedTab is null)
        {
            return false;
        }

        tab = mappedTab;
        return true;
    }

    private void OnTabHeaderClicked(object? sender, EventArgs e)
    {
        if (sender is not Control control || control.Tag is not BrowserTab tab)
        {
            return;
        }

        ActivateTab(tab);
    }

    private void ActivateTab(BrowserTab tab)
    {
        int tabIndex = _tabs.IndexOf(tab);
        if (tabIndex < 0)
        {
            return;
        }

        BrowserTab? previous = _activeTab;
        if (!ReferenceEquals(previous, tab) &&
            previous is not null &&
            _findPanel.Visible &&
            !string.IsNullOrWhiteSpace(_findTextBox.Text))
        {
            _ = ClearFindHighlightsAsync(previous);
        }

        _activeTab = tab;
        RefreshInspector();
        RefreshTabTitle(tab);
        UpdateTabVisibilityAndRendering();

        if (_webViewModeEnabled)
        {
            tab.Page.View.BringToFront();
            if (_historyPanel.Visible)
            {
                _historyPanel.BringToFront();
            }

            if (_findPanel.Visible)
            {
                _findPanel.BringToFront();
            }
        }

        _addressBarTextBox.Text = tab.CurrentUrl;
        UpdateNavigationButtons();
        UpdateFavoriteButtonState();
        UpdateZoomControlsForActiveTab();
        UpdateTabHeaderVisuals();
        UpdateTabLayout();

        if (_findPanel.Visible && !string.IsNullOrWhiteSpace(_findTextBox.Text))
        {
            _ = ApplyFindQueryAsync();
        }
    }

    private async Task CloseTabAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab) || tab.IsClosing)
        {
            return;
        }

        // The session's cookies live in the WebView2 browser process, which ends with the last WebView2 tab, so they are
        // saved first (with Settings -> Save cookies across sessions on).
        if (_settings.PersistCookiesAcrossSessions && IsLastSharedWebView2Tab(tab))
        {
            tab.IsClosing = true;
            try
            {
                await SaveCookiesNowAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                Log("The cookies could not be saved in time; the last save is kept.");
            }
        }

        int closingIndex = _tabs.IndexOf(tab);
        if (closingIndex < 0 || IsDisposed)
        {
            return;
        }

        bool wasActive = ReferenceEquals(_activeTab, tab);
        RemoveTabAndDispose(tab);

        if (_tabs.Count == 0)
        {
            UpdateTabLayout();
            UpdateNavigationButtons();
            UpdateFavoriteButtonState();
            ApplyBackgroundTabRendering();

            if (_webViewModeEnabled)
            {
                BrowserTab? replacement = await CreateTabAsync(GetConfiguredStartPage(), activate: true);
                if (replacement is null && _webViewModeEnabled)
                {
                    LeaveWebViewMode();
                    ShowLogPanel();
                }
            }

            return;
        }

        if (wasActive)
        {
            int nextIndex = Math.Clamp(closingIndex, 0, _tabs.Count - 1);
            ActivateTab(_tabs[nextIndex]);
        }
        else
        {
            UpdateTabHeaderVisuals();
            UpdateTabLayout();
            UpdateTabVisibilityAndRendering();
        }
    }

    private void RemoveTabAndDispose(BrowserTab tab)
    {
        if (_findPanel.Visible && ReferenceEquals(_activeTab, tab) && !string.IsNullOrWhiteSpace(_findTextBox.Text))
        {
            _ = ClearFindHighlightsAsync(tab);
        }

        RestoreNativePage(tab);
        WebProtocolPage page = tab.Page;
        CoreWebView2Environment? environment = DetachPageFromTab(page);
        _tabs.Remove(tab);
        OnWebView2PageRemoved(environment);

        if (ReferenceEquals(_activeTab, tab))
        {
            _activeTab = null;
        }

        if (tab.OwnedFaviconImage is not null)
        {
            tab.OwnedFaviconImage.Dispose();
            tab.OwnedFaviconImage = null;
        }

        _tabsFlowPanel.Controls.Remove(tab.HeaderPanel);

        try
        {
            tab.HeaderPanel.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }

        try
        {
            page.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private void UpdateTabHeaderVisuals()
    {
        ThemePalette palette = _palette;
        Color activeTextColor = ResolveTextColor(palette.TabActiveText);
        Color inactiveTextColor = ResolveTextColor(palette.TabInactiveText);

        foreach (BrowserTab tab in _tabs)
        {
            bool active = ReferenceEquals(tab, _activeTab);
            Color textColor = active ? activeTextColor : inactiveTextColor;
            tab.HeaderPanel.BackColor = ResolveSurfaceBack(active ? palette.TabActiveBack : palette.TabInactiveBack, textColor);
            tab.CloseButton.BackColor = ResolveSurfaceBack(active ? palette.TabCloseActiveBack : palette.TabCloseInactiveBack, textColor);
            tab.CloseButton.ForeColor = textColor;
            tab.TitleLabel.ForeColor = textColor;
        }
    }

    private void UpdateTabLayout()
    {
        if (_tabs.Count == 0 || _tabsFlowPanel.ClientSize.Width <= 0)
        {
            return;
        }

        // Tabs are pills; narrow tabs (many open) show only their icon and a smaller close button.
        const int tabGap = 6;
        int scaledTabGap = ScaleByDpi(tabGap);
        int compactCloseWidthMinimum = Math.Max(ScaleByDpi(16), TextRenderer.MeasureText("X", _tabCloseFontCompact).Width + ScaleByDpi(6));
        int regularCloseWidth = Math.Max(ScaleByDpi(24), TextRenderer.MeasureText("X", _tabCloseFontRegular).Width + ScaleByDpi(10));
        int minimumCompactTabWidth = compactCloseWidthMinimum + ScaleByDpi(22);
        int totalGap = (_tabs.Count - 1) * scaledTabGap;
        int available = Math.Max(1, _tabsFlowPanel.ClientSize.Width - totalGap);
        int rawWidth = Math.Max(1, available / _tabs.Count);
        TopBarMetrics metrics = CurrentTopBarMetrics;
        bool compact = rawWidth < ScaleByDpi(metrics.TabMinWidth);
        int targetWidth = compact
            ? Math.Max(minimumCompactTabWidth, rawWidth)
            : Math.Clamp(rawWidth, ScaleByDpi(metrics.TabMinWidth), ScaleByDpi(metrics.TabMaxWidth));
        int tabHeight = ScaleByDpi(compact ? metrics.NarrowTabHeight : metrics.TabHeight);
        int tabPadding = metrics.TabHeight >= 34 ? 5 : metrics.TabHeight >= 28 ? 4 : 3;
        int iconSize = metrics.TabHeight >= 28 ? 16 : 14;
        int topMargin = Math.Max(0, (_tabsFlowPanel.ClientSize.Height - tabHeight) / 2);

        for (int i = 0; i < _tabs.Count; i++)
        {
            BrowserTab tab = _tabs[i];
            tab.HeaderPanel.Width = targetWidth;
            tab.HeaderPanel.Height = tabHeight;
            tab.HeaderPanel.Margin = new Padding(0, topMargin, i == _tabs.Count - 1 ? 0 : scaledTabGap, 0);

            if (compact)
            {
                int closeWidth = Math.Clamp(targetWidth / 2, compactCloseWidthMinimum, compactCloseWidthMinimum + 4);
                int iconWidth = Math.Clamp(targetWidth - closeWidth - ScaleByDpi(14), ScaleByDpi(8), ScaleByDpi(14));

                tab.HeaderPanel.Padding = ScalePadding(7, Math.Min(4, tabPadding), 3, Math.Min(4, tabPadding));
                tab.TitleLabel.Visible = false;
                tab.CloseButton.Width = closeWidth;
                tab.CloseButton.Font = MapFont(_tabCloseFontCompact);
                tab.FaviconBox.Width = iconWidth;
                tab.FaviconBox.Height = iconWidth;
            }
            else
            {
                tab.HeaderPanel.Padding = ScalePadding(10, tabPadding, 5, tabPadding);
                tab.TitleLabel.Visible = true;
                tab.CloseButton.Width = regularCloseWidth;
                tab.CloseButton.Font = MapFont(_tabCloseFontRegular);
                tab.FaviconBox.Width = ScaleByDpi(iconSize);
                tab.FaviconBox.Height = ScaleByDpi(iconSize);
            }
        }
    }

    private void NavigateToAddressBarUrl()
    {
        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            Log("Web navigation requested before Web View was initialized.");
            return;
        }

        string rawInput = _addressBarTextBox.Text.Trim();
        if (!TryResolveNavigationTarget(rawInput, out string targetUrl, out bool isSearchQuery))
        {
            Log($"Invalid web address: {rawInput}");
            return;
        }

        NavigateTabToUrl(
            _activeTab,
            targetUrl,
            isSearchQuery ? $"Search requested ({GetEffectiveSearchEngine()})" : "Navigate requested");
    }

    private void NavigateTabToUrl(BrowserTab tab, string normalizedUrl, string reason)
    {
        RestoreNativePage(tab);
        if (!tab.Page.IsReady)
        {
            Log("Tab navigation requested before Web View was initialized.");
            return;
        }

        tab.CurrentUrl = normalizedUrl;
        tab.IsNavigationPending = true;
        if (ReferenceEquals(_activeTab, tab))
        {
            _addressBarTextBox.Text = normalizedUrl;
            UpdateFavoriteButtonState();
        }

        // Logged first: some protocols report the navigation starting from inside Navigate.
        Log($"{reason}: {normalizedUrl}");
        tab.Page.Navigate(normalizedUrl);
    }

    private static bool TryNormalizeUrl(string? rawUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        string candidate = rawUrl.Trim();
        // Only a scheme at the very start counts: "example.com/?next=https://other.example" has none (it used to be
        // taken for one, so the address could not be opened).
        if (!StartsWithUrlScheme(candidate))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUrl = uri.ToString();
        return true;
    }

    /// <summary>True when <paramref name="value"/> starts with "scheme://" (a letter, then letters, digits, '+', '-' or '.').</summary>
    private static bool StartsWithUrlScheme(string value)
    {
        int separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (int i = 1; i < separator; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryResolveNavigationTarget(string rawInput, out string targetUrl, out bool isSearchQuery)
    {
        targetUrl = string.Empty;
        isSearchQuery = false;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        string candidate = rawInput.Trim();
        bool hasExplicitHttpScheme = candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!hasExplicitHttpScheme && LooksLikeLocalAddress(candidate))
        {
            // Servers on this PC or the local network ("localhost:3000") rarely have https; typed without a scheme they
            // are opened with http, as browsers do.
            return TryNormalizeUrl("http://" + candidate, out targetUrl);
        }

        if (hasExplicitHttpScheme || LooksLikeDirectAddress(candidate))
        {
            return TryNormalizeUrl(candidate, out targetUrl);
        }

        targetUrl = BuildSearchUrl(candidate);
        isSearchQuery = true;
        return true;
    }

    private static bool LooksLikeDirectAddress(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
        {
            return false;
        }

        if (!Uri.TryCreate($"https://{candidate}", UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string host = parsed.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        UriHostNameType hostType = Uri.CheckHostName(host);
        if (hostType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int lastDotIndex = host.LastIndexOf('.');
        if (lastDotIndex <= 0 || lastDotIndex >= host.Length - 1)
        {
            return false;
        }

        string extension = host[(lastDotIndex + 1)..];
        if (extension.Length is < 2 or > 24)
        {
            return false;
        }

        return extension.All(ch => char.IsLetterOrDigit(ch) || ch == '-');
    }

    private static bool LooksLikeLocalAddress(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string authority = candidate;
        int splitIndex = authority.IndexOfAny(new[] { '/', '?', '#' });
        if (splitIndex >= 0)
        {
            authority = authority[..splitIndex];
        }

        string host = authority;
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            int endBracket = host.IndexOf(']');
            if (endBracket > 1)
            {
                host = host[1..endBracket];
            }
        }
        else
        {
            int colonIndex = host.IndexOf(':');
            if (colonIndex > 0)
            {
                host = host[..colonIndex];
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out IPAddress? parsedIp))
        {
            return false;
        }

        return IPAddress.IsLoopback(parsedIp) ||
               parsedIp.Equals(IPAddress.Any) ||
               parsedIp.Equals(IPAddress.IPv6Any);
    }

    private string BuildSearchUrl(string query)
    {
        SearchEngine effectiveEngine = GetEffectiveSearchEngine();
        string encodedQuery = Uri.EscapeDataString(query.Trim());
        return effectiveEngine switch
        {
            SearchEngine.Bing => $"https://www.bing.com/search?q={encodedQuery}",
            SearchEngine.Yahoo => $"https://search.yahoo.com/search?p={encodedQuery}",
            SearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={encodedQuery}",
            SearchEngine.Startpage => $"https://www.startpage.com/sp/search?query={encodedQuery}",
            _ => $"https://www.google.com/search?q={encodedQuery}",
        };
    }

    private string NormalizeOrDefaultUrl(string? rawUrl)
    {
        return TryNormalizeUrl(rawUrl, out string normalizedUrl)
            ? normalizedUrl
            : GetConfiguredStartPage();
    }

    private string GetConfiguredStartPage()
    {
        string sourceStartPage = GetEffectiveStartPage();
        string normalizedStartPage = NormalizeStartPage(sourceStartPage);
        if (string.IsNullOrWhiteSpace(GetExtensionStartPageOverride()))
        {
            _settings.StartingPage = normalizedStartPage;
        }

        return normalizedStartPage;
    }

    private SearchEngine GetEffectiveSearchEngine()
    {
        PhantomExtensionSearchEngine? extensionOverride = GetExtensionSearchEngineOverride();
        if (extensionOverride is null)
        {
            return _settings.DefaultSearchEngine;
        }

        return extensionOverride.Value switch
        {
            PhantomExtensionSearchEngine.Bing => SearchEngine.Bing,
            PhantomExtensionSearchEngine.Yahoo => SearchEngine.Yahoo,
            PhantomExtensionSearchEngine.DuckDuckGo => SearchEngine.DuckDuckGo,
            PhantomExtensionSearchEngine.Startpage => SearchEngine.Startpage,
            _ => SearchEngine.Google,
        };
    }

    private string GetEffectiveStartPage()
    {
        string? extensionOverride = GetExtensionStartPageOverride();
        if (!string.IsNullOrWhiteSpace(extensionOverride))
        {
            return extensionOverride;
        }

        return _settings.StartingPage;
    }

    private string? GetExtensionStartPageOverride()
    {
        // Only valid http(s) overrides count: an invalid app.startpage must not replace the user's own start page
        // with the built-in default.
        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            string candidate = extension.Definition.StartPageOverride;
            if (!string.IsNullOrWhiteSpace(candidate) && TryNormalizeUrl(candidate, out string normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private PhantomExtensionSearchEngine? GetExtensionSearchEngineOverride()
    {
        LoadedExtension? extension = GetEnabledExtensions()
            .FirstOrDefault(current => current.Definition.SearchEngineOverride is not null);

        return extension?.Definition.SearchEngineOverride;
    }

    private static string NormalizeStartPage(string? rawValue)
    {
        return TryNormalizeUrl(rawValue, out string normalizedStartPage)
            ? normalizedStartPage
            : DefaultHomeUrl;
    }

    private void GoBack()
    {
        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            Log("Back navigation requested before Web View was initialized.");
            return;
        }

        if (!_activeTab.Browser.CanGoBack)
        {
            return;
        }

        RestoreNativePage(_activeTab);
        _activeTab.Page.GoBack();
        Log("Web back navigation requested.");
    }

    private void GoForward()
    {
        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            Log("Forward navigation requested before Web View was initialized.");
            return;
        }

        if (!_activeTab.Browser.CanGoForward)
        {
            return;
        }

        RestoreNativePage(_activeTab);
        _activeTab.Page.GoForward();
        Log("Web forward navigation requested.");
    }

    private void RefreshWebView()
    {
        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            Log("Refresh requested before Web View was initialized.");
            return;
        }

        RestoreNativePage(_activeTab);
        _activeTab.Page.Reload();
        Log("Web page refresh requested.");
    }

    private void ToggleHistoryPanel()
    {
        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            Log("History requested before Web View was initialized.");
            return;
        }

        if (_historyPanel.Visible)
        {
            _historyPanel.Visible = false;
            _historyButton.BackColor = GetCommandButtonBackColor();
            Log("Web history panel closed.");
            return;
        }

        RefreshHistoryPanelItems();
        RefreshFavoritesPanelItems();
        _historyPanel.Visible = true;
        _historyPanel.BringToFront();
        if (_findPanel.Visible)
        {
            _findPanel.BringToFront();
        }

        _historyButton.BackColor = GetHistoryButtonActiveBackColor();
        Log("Web history panel opened.");
    }

    private void NavigateFromHistorySelection()
    {
        if (_historyListBox.SelectedItem is not string selectedUrl)
        {
            return;
        }

        if (selectedUrl.StartsWith("(", StringComparison.Ordinal))
        {
            return;
        }

        NavigateToHistoryUrl(selectedUrl);
        _historyPanel.Visible = false;
        _historyButton.BackColor = GetCommandButtonBackColor();
    }

    private void NavigateFromFavoritesSelection()
    {
        if (_favoritesListBox.SelectedItem is not string selectedUrl)
        {
            return;
        }

        if (selectedUrl.StartsWith("(", StringComparison.Ordinal))
        {
            return;
        }

        NavigateToHistoryUrl(selectedUrl);
        _historyPanel.Visible = false;
        _historyButton.BackColor = GetCommandButtonBackColor();
    }

    private void NavigateToHistoryUrl(string url)
    {
        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            Log("History navigation requested before Web View was initialized.");
            return;
        }

        if (!TryNormalizeUrl(url, out string normalizedUrl))
        {
            Log($"Ignored invalid history URL: {url}");
            return;
        }

        NavigateTabToUrl(_activeTab, normalizedUrl, "History navigation");
    }

    private void ApplyZoom()
    {
        _zoomLabel.Text = $"Zoom {_zoomSlider.Value}%";
        if (_activeTab is null || !_activeTab.Page.IsReady || !_activeTab.Page.Features.HasFlag(WebProtocolFeatures.Zoom))
        {
            return;
        }

        _activeTab.ZoomPercent = _zoomSlider.Value;
        _activeTab.Page.SetZoom(_zoomSlider.Value);

        // Dragging the slider changes the zoom many times a second; only the final value is logged.
        _pendingZoomLog = (_activeTab.Id, _zoomSlider.Value);
        _zoomLogTimer.Stop();
        _zoomLogTimer.Start();
    }

    private void OnZoomLogTimerTick(object? sender, EventArgs e)
    {
        _zoomLogTimer.Stop();
        if (_pendingZoomLog is (int tabId, int percent))
        {
            _pendingZoomLog = null;
            Log($"Tab {tabId} zoom set to {percent}%.");
        }
    }

    private void UpdateNavigationButtons()
    {
        bool ready = _webViewModeEnabled && _activeTab is not null && _activeTab.Page.IsReady;
        _backButton.Enabled = ready && _activeTab!.Browser.CanGoBack;
        _forwardButton.Enabled = ready && _activeTab!.Browser.CanGoForward;
    }

    private void RefreshHistoryPanelItems()
    {
        _historyTitleLabel.Text = GetHistoryTitleText();
        _historyListBox.Items.Clear();
        if (_historyEntries.Count == 0)
        {
            _historyListBox.Items.Add(GetNoHistoryMessage());
            return;
        }

        for (int i = _historyEntries.Count - 1; i >= 0; i--)
        {
            _historyListBox.Items.Add(_historyEntries[i].Url);
        }
    }

    private void RefreshFavoritesPanelItems()
    {
        RefreshFavoritesBar();
        _favoritesListBox.Items.Clear();
        if (_favorites.Count == 0)
        {
            _favoritesListBox.Items.Add("(No favourites saved)");
            return;
        }

        for (int i = _favorites.Count - 1; i >= 0; i--)
        {
            _favoritesListBox.Items.Add(_favorites[i].Url);
        }
    }

    private void OnPageNavigationStarting(BrowserTab tab, WebPageNavigationStartingEventArgs e)
    {
        tab.CurrentUrl = e.Url;
        tab.IsNavigationPending = true;
        tab.NavigationSequence++;
        tab.InspectorDraft = null;
        if (ReferenceEquals(_activeTab, tab)) RefreshInspector();
        if (_webViewModeEnabled && ReferenceEquals(_activeTab, tab))
        {
            _addressBarTextBox.Text = e.Url;
            UpdateFavoriteButtonState();
        }

        Log($"Tab {tab.Id} navigation starting: {e.Url}");
    }

    private async void OnPageNavigationCompleted(BrowserTab tab, WebPageNavigationCompletedEventArgs e)
    {
        tab.IsNavigationPending = false;
        int navigationSequence = tab.NavigationSequence;
        if (ReferenceEquals(_activeTab, tab)) RefreshInspector();
        WebProtocolPage page = tab.Page;

        try
        {
            if (e.IsSuccess)
            {
                string currentUrl = page.CurrentUrl;
                if (string.IsNullOrWhiteSpace(currentUrl))
                {
                    currentUrl = tab.CurrentUrl;
                }

                if (!string.IsNullOrWhiteSpace(currentUrl))
                {
                    tab.CurrentUrl = currentUrl;
                    if (!currentUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        AddHistoryEntry(currentUrl);
                    }
                }

                RefreshTabTitle(tab);
                if (page.Features.HasFlag(WebProtocolFeatures.ExtensionScripts))
                {
                    await ApplyExtensionWebContentAsync(tab, navigationSequence);
                }

                await RefreshTabFaviconAsync(tab);
                Log($"Tab {tab.Id} navigation completed: {currentUrl}");
                if (page.InnermostPage is WebView2ProtocolPage)
                {
                    QueueCookieRegistrationCheck(tab);
                }
            }
            else
            {
                RefreshTabTitle(tab);
                Log($"Tab {tab.Id} navigation failed: {NormalizeForLog(e.ErrorDescription ?? "unknown error")}");
            }
        }
        catch (ObjectDisposedException)
        {
            // Late async callback while tab/form is closing; ignore.
        }
        catch (Exception ex)
        {
            Log($"Tab {tab.Id} navigation completion handling failed: {ex.Message}");
        }

        if (!IsDisposed && !_addressBarTextBox.IsDisposed && _webViewModeEnabled && ReferenceEquals(_activeTab, tab))
        {
            _addressBarTextBox.Text = tab.CurrentUrl;
            UpdateNavigationButtons();
            UpdateFavoriteButtonState();
            UpdateZoomControlsForActiveTab();
        }
    }

    private void OnPageSourceChanged(BrowserTab tab)
    {
        string source = tab.Page.CurrentUrl;
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        tab.CurrentUrl = source;
        if (_webViewModeEnabled && ReferenceEquals(_activeTab, tab))
        {
            _addressBarTextBox.Text = source;
            UpdateFavoriteButtonState();
        }

        Log($"Tab {tab.Id} source changed: {source}");
    }

    private void OnPageTitleChanged(BrowserTab tab)
    {
        RefreshTabTitle(tab);
        UpdateSavedFavorite(tab);
        Log($"Tab {tab.Id} title: {tab.Title}");
    }

    private void OnPageHistoryChanged(BrowserTab tab)
    {
        if (!tab.Page.IsReady || !ReferenceEquals(_activeTab, tab))
        {
            return;
        }

        Log($"Web history state changed (Back: {tab.Page.CanGoBack}, Forward: {tab.Page.CanGoForward}).");
        UpdateNavigationButtons();
    }

    private void OnPageNewWindowRequested(BrowserTab tab, WebPageNewWindowEventArgs e)
    {
        string popupUrl = NormalizeOrDefaultUrl(e.Url);

        try
        {
            // Not from inside the page's own event: the page is still handling it.
            _ = BeginInvoke(new Action(() =>
            {
                _ = CreateTabAsync(popupUrl, activate: true);
            }));
        }
        catch (ObjectDisposedException)
        {
            // Form is closing; ignore late popup dispatch.
            return;
        }
        catch (InvalidOperationException)
        {
            // Handle may be unavailable while shutting down.
            return;
        }

        Log($"Web popup opened in new tab: {popupUrl}");
    }

    private void OnPageFindRequested(BrowserTab tab)
    {
        try
        {
            BeginInvoke(new Action(ShowFindPanel));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Best effort only.
        }
    }

    private void RefreshTabTitle(BrowserTab tab)
    {
        string title = tab.Page.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = BuildFallbackTitle(tab.CurrentUrl);
        }

        tab.Title = title;
        tab.TitleLabel.Text = title;
    }

    private static string BuildFallbackTitle(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return "New Tab";
        }

        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return "New Tab";
    }

    private async Task RefreshTabFaviconAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab) || !tab.Page.IsReady)
        {
            return;
        }

        // Pages that download their parts themselves (HTMLRenderer, HazeGate) download their icon the same way: through
        // their proxy server or gateway, checked against web.block.
        var page = tab.Page;
        int sequence = tab.NavigationSequence;
        bool Current() => _tabs.Contains(tab) && ReferenceEquals(tab.Page, page) && tab.NavigationSequence == sequence;
        if (page.DownloadsOwnFavicon)
        {
            Image? ownIcon = await page.DownloadFaviconAsync();
            if (!Current())
            {
                ownIcon?.Dispose();
                return;
            }

            if (ownIcon is not null || !tab.Protocol.LoadsPagesDirectly || page.InnermostPage is not WebView2ProtocolPage)
            {
                SetTabFavicon(tab, ownIcon ?? DefaultTabIcon, ownsImage: ownIcon is not null);
                return;
            }
        }

        // Icons are downloaded by PhantomHaze itself, which would contact the site directly and go around the gateway or
        // proxy server an extension protocol loads pages through. Those tabs keep the default icon.
        if (!tab.Protocol.LoadsPagesDirectly)
        {
            SetTabFavicon(tab, DefaultTabIcon, ownsImage: false);
            return;
        }

        string? faviconUrl = await tab.Page.GetDeclaredFaviconUrlAsync();
        if (!Current()) return;
        if (string.IsNullOrWhiteSpace(faviconUrl))
        {
            faviconUrl = BuildFallbackFaviconUrl(tab.CurrentUrl);
        }

        if (string.IsNullOrWhiteSpace(faviconUrl))
        {
            SetTabFavicon(tab, DefaultTabIcon, ownsImage: false);
            return;
        }

        Image? favicon = await TryDownloadImageAsync(faviconUrl);
        if (!Current())
        {
            favicon?.Dispose();
            return;
        }
        if (favicon is null)
        {
            SetTabFavicon(tab, DefaultTabIcon, ownsImage: false);
            return;
        }

        if (!_tabs.Contains(tab))
        {
            favicon.Dispose();
            return;
        }

        SetTabFavicon(tab, favicon, ownsImage: true);
    }

    private static string? BuildFallbackFaviconUrl(string? pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string authority = uri.IsDefaultPort
            ? uri.Host
            : $"{uri.Host}:{uri.Port}";

        return $"{uri.Scheme}://{authority}/favicon.ico";
    }

    private async Task<Image?> TryDownloadImageAsync(string url)
    {
        try
        {
            const int maximumRedirects = 5;
            string requestUrl = url;
            for (int redirect = 0; ; redirect++)
            {
                if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? requestUri) ||
                    (!requestUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !requestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                // Favicons are downloaded by PhantomHaze itself rather than by WebView2, so extension block rules
                // have to be applied here as well, including to redirect targets.
                if (TryGetBlockingExtensionRule(requestUri.AbsoluteUri, out _, out _))
                {
                    return null;
                }

                using HttpResponseMessage response = await _faviconHttpClient.GetAsync(requestUri);
                int statusCode = (int)response.StatusCode;
                if (statusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirect >= maximumRedirects || response.Headers.Location is not Uri location)
                    {
                        return null;
                    }

                    var redirectUri = new Uri(requestUri, location);

                    // Like the default handler, never follow an https -> http redirect: the request would be sent
                    // in plain text and its response could be replaced on the way.
                    if (requestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                        !redirectUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    requestUrl = redirectUri.AbsoluteUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await ReadImageAsync(response);
            }
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Image?> ReadImageAsync(HttpResponseMessage response)
    {
        try
        {
            await using Stream contentStream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            await contentStream.CopyToAsync(ms);
            ms.Position = 0;
            using Image rawImage = Image.FromStream(ms);
            return new Bitmap(rawImage);
        }
        catch
        {
            return null;
        }
    }

    // Find in page and the Ctrl+F bridge are implemented by each Web View protocol's page (see WebProtocolPage).

    private bool TryGetBlockingExtensionRule(
        string requestUri,
        out LoadedExtension? matchingExtension,
        out PhantomExtensionWebBlockDefinition? matchingRule)
    {
        matchingExtension = null;
        matchingRule = null;

        if (!_settings.ExtensionsEnabled || string.IsNullOrWhiteSpace(requestUri))
        {
            return false;
        }

        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            foreach (PhantomExtensionWebBlockDefinition rule in extension.Definition.WebBlocks)
            {
                if (!PhantomExtensionPatternMatcher.IsMatch(requestUri, rule.MatchPattern))
                {
                    continue;
                }

                matchingExtension = extension;
                matchingRule = rule;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Injects the enabled extensions' web.script / web.css rules into the document whose navigation just
    /// completed (document-start rules first, then document-end, as documented in the PhantomScript guide).
    /// The work is asynchronous, so before every injection the tab is re-checked to still be on that same
    /// navigation; otherwise a rule matched for one URL could run on whatever page the tab moved to next.
    /// The injected script additionally re-checks location.href against the rule's pattern when it executes.
    /// </summary>
    private async Task ApplyExtensionWebContentAsync(BrowserTab tab, int navigationSequence)
    {
        if (!_settings.ExtensionsEnabled || !IsTabStillOnNavigation(tab, navigationSequence))
        {
            return;
        }

        // Snapshot: Settings can be saved (which reloads the extension list) while an injection is awaited.
        List<LoadedExtension> extensions = GetEnabledExtensions().ToList();
        if (extensions.Count == 0)
        {
            return;
        }

        string pageUrl = GetTabDocumentUrl(tab);
        PhantomExtensionScriptTiming[] timings = { PhantomExtensionScriptTiming.DocumentStart, PhantomExtensionScriptTiming.DocumentEnd };
        foreach (PhantomExtensionScriptTiming timing in timings)
        {
            foreach (LoadedExtension extension in extensions)
            {
                foreach (PhantomExtensionWebScriptDefinition script in extension.Definition.WebScripts)
                {
                    if (script.Timing != timing || !PhantomExtensionPatternMatcher.IsMatch(pageUrl, script.MatchPattern))
                    {
                        continue;
                    }

                    if (!IsTabStillOnNavigation(tab, navigationSequence))
                    {
                        return;
                    }

                    if (!IsExtensionStillLoadedAndActive(extension))
                    {
                        // Disabled, uninstalled or reloaded with new settings while this cycle was running.
                        break;
                    }

                    try
                    {
                        string injectedScript = BuildExtensionInjectionScript(extension, script);
                        string? result = await tab.Page.ExecuteScriptAsync(injectedScript);

                        // The wrapper always evaluates to a status string, so "null" means the script never ran:
                        // WebView2 reports syntax errors that way and nothing else would show the failure.
                        if (!script.IsCss &&
                            string.Equals(result, "null", StringComparison.Ordinal) &&
                            IsTabStillOnNavigation(tab, navigationSequence) &&
                            _reportedNonRunningExtensionScripts.Add($"{extension.Definition.Id}/{script.Id}"))
                        {
                            Log($"Extension script [{extension.Definition.Id}/{script.Id}] did not run on {TrimForLog(pageUrl, 220)}. " +
                                "If this happens on every page, check the script for JavaScript syntax errors.");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!IsTabStillOnNavigation(tab, navigationSequence))
                        {
                            return;
                        }

                        Log($"Extension script failed [{extension.Definition.Id}/{script.Id}]: {ex.Message}");
                    }
                }
            }
        }
    }

    private bool IsTabStillOnNavigation(BrowserTab tab, int navigationSequence)
    {
        return !IsDisposed &&
               _tabs.Contains(tab) &&
               tab.NavigationSequence == navigationSequence &&
               tab.Page.IsReady;
    }

    private bool IsExtensionStillLoadedAndActive(LoadedExtension extension)
    {
        return IsExtensionActive(extension) && _loadedExtensions.Contains(extension);
    }

    private static string GetTabDocumentUrl(BrowserTab tab)
    {
        // The browser's own serialisation of the URL (what location.href reports), so a rule is matched the same way
        // here and by the in-page guard of the injected script.
        string source = tab.Page.DocumentUrl;
        return string.IsNullOrWhiteSpace(source) ? tab.CurrentUrl : source;
    }

    private static string BuildExtensionInjectionScript(LoadedExtension extension, PhantomExtensionWebScriptDefinition script)
    {
        if (script.IsCss)
        {
            string css = ResolveExtensionPlaceholders(script.Content, extension);
            return PhantomExtensionScriptBuilder.BuildStyleInjectionScript(
                extension.Definition.Id,
                script.Id,
                script.MatchPattern,
                css);
        }

        // Legacy window.phantomHazeExtensions references are redirected to the private context first; then
        // placeholders are resolved with JavaScript string escaping so a setting value cannot break out of the
        // string literal it is placed in.
        string source = PhantomExtensionScriptBuilder.RewriteLegacyGlobalAccess(script.Content);
        string payload = ResolveExtensionPlaceholders(source, extension, PhantomExtensionPlaceholderContext.JavaScriptString);
        return PhantomExtensionScriptBuilder.BuildWebScript(
            BuildExtensionRuntimeContextJson(extension),
            script.MatchPattern,
            payload);
    }

    /// <summary>
    /// The context handed privately to an extension's own scripts (it is never stored on the page's window, where
    /// any site script could read or modify it), with every setting serialised according to its declared type.
    /// </summary>
    private static string BuildExtensionRuntimeContextJson(LoadedExtension extension)
    {
        var runtimeSettings = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (PhantomExtensionSettingDefinition setting in extension.Definition.Settings)
        {
            string value = extension.Installation.SettingValues.TryGetValue(setting.Key, out string? storedValue) && storedValue is not null
                ? storedValue
                : GetDefaultSettingValue(setting);

            runtimeSettings[setting.Key] = setting.Type switch
            {
                PhantomExtensionSettingType.Toggle => TryParseBooleanSetting(value, out bool toggleValue) && toggleValue,
                PhantomExtensionSettingType.Number => TryParseInvariantInt(value, out int numberValue)
                    ? numberValue
                    : (TryParseInvariantInt(GetDefaultSettingValue(setting), out int defaultNumber) ? defaultNumber : 0),
                _ => value,
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = extension.Definition.Id,
            ["name"] = extension.Definition.Name,
            ["version"] = extension.Definition.Version,
            ["settings"] = runtimeSettings,
        });
    }

    private static bool TryParseInvariantInt(string? value, out int parsed)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private async Task<int> ApplyFindScriptAsync(BrowserTab tab, string query)
    {
        if (!_tabs.Contains(tab) || !tab.Page.IsReady || !tab.Page.Features.HasFlag(WebProtocolFeatures.FindInPage))
        {
            return 0;
        }

        try
        {
            return await tab.Page.FindAsync(query);
        }
        catch
        {
            return 0;
        }
    }

    private void ShowFindPanel()
    {
        if (!_webViewModeEnabled || _activeTab is null || !_activeTab.Page.IsReady)
        {
            return;
        }

        if (!_activeTab.Page.Features.HasFlag(WebProtocolFeatures.FindInPage))
        {
            Log($"Find in page is not available with the {_activeTab.Protocol.DisplayName} protocol.");
            return;
        }

        _findPanel.Visible = true;
        PositionFindPanel();
        _findPanel.BringToFront();
        _findTextBox.Focus();
        _findTextBox.SelectAll();
    }

    private void HideFindPanel(bool clearHighlights)
    {
        if (clearHighlights)
        {
            _findRequestVersion++;
            BrowserTab? tab = _activeTab;
            if (tab is not null)
            {
                _ = ClearFindHighlightsAsync(tab);
            }

            _findTextBox.Text = string.Empty;
            _findCountLabel.Text = string.Empty;
        }

        _findPanel.Visible = false;
    }

    private async Task ClearFindHighlightsAsync(BrowserTab tab)
    {
        await ApplyFindScriptAsync(tab, string.Empty);
    }

    private async Task ApplyFindQueryAsync()
    {
        if (!_findPanel.Visible)
        {
            return;
        }

        if (_activeTab is null || !_activeTab.Page.IsReady)
        {
            _findCountLabel.Text = string.Empty;
            return;
        }

        BrowserTab targetTab = _activeTab;
        string query = _findTextBox.Text;
        int requestId = ++_findRequestVersion;
        int matches = await ApplyFindScriptAsync(targetTab, query);

        if (requestId != _findRequestVersion || !ReferenceEquals(_activeTab, targetTab))
        {
            return;
        }

        _findCountLabel.Text = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : $"{matches} match{(matches == 1 ? string.Empty : "es")}";
    }

    private void PositionFindPanel()
    {
        int margin = ScaleByDpi(10);
        int maxWidth = Math.Max(1, _contentHostPanel.ClientSize.Width - (margin * 2));
        int targetWidth = maxWidth >= ScaleByDpi(220)
            ? Math.Min(ScaleByDpi(360), maxWidth)
            : maxWidth;
        if (_findPanel.Width != targetWidth)
        {
            _findPanel.Width = targetWidth;
        }

        int x = Math.Max(margin, _contentHostPanel.ClientSize.Width - _findPanel.Width - margin);
        _findPanel.Location = new Point(x, margin);
    }

    private void ToggleFavoriteForCurrentPage()
    {
        string? currentUrl = GetActiveTabUrl();
        if (!TryNormalizeUrl(currentUrl, out string normalizedUrl))
        {
            Log("Favourite toggle ignored because no valid page is active.");
            return;
        }

        int existingIndex = FindFavorite(normalizedUrl);

        if (existingIndex >= 0)
        {
            _favorites.RemoveAt(existingIndex);
            Log($"Favourite removed: {normalizedUrl}");
        }
        else
        {
            _favorites.Add(FavoriteFromTab(_activeTab!, normalizedUrl));
            Log($"Favourite added: {normalizedUrl}");
        }

        SaveFavorites();
        RefreshFavoritesPanelItems();
        UpdateFavoriteButtonState();
    }

    private string? GetActiveTabUrl()
    {
        if (_activeTab is null)
        {
            return null;
        }

        string source = _activeTab.Page.CurrentUrl;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = _activeTab.CurrentUrl;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            source = _addressBarTextBox.Text.Trim();
        }

        return string.IsNullOrWhiteSpace(source) ? null : source;
    }

    private void UpdateFavoriteButtonState()
    {
        Color idleForeColor = ResolveTextColor(_palette.FavoriteIdleText);
        Color idleBackColor = ResolveSurfaceBack(_palette.FavoriteIdleBack, idleForeColor);

        bool enabled = _webViewModeEnabled && _activeTab is not null;
        _favoriteButton.Enabled = enabled;
        bool isFavorite = false;
        if (enabled)
        {
            string? currentUrl = GetActiveTabUrl();
            isFavorite = TryNormalizeUrl(currentUrl, out string normalizedCurrentUrl) &&
                FindFavorite(normalizedCurrentUrl) >= 0;
        }

        _favoriteButton.BackColor = isFavorite ? Color.Gold : idleBackColor;
        _favoriteButton.ForeColor = isFavorite ? Color.Black : idleForeColor;
        string text = isFavorite ? "★" : "✩";
        if (!string.Equals(_favoriteButton.Text, text, StringComparison.Ordinal))
        {
            _favoriteButton.Text = text;
        }

        // An extension may have replaced the star with an SVG (ui.svg address.favorite).
        ApplyUiElementState();
    }

    private void LoadFavorites()
    {
        _favorites.Clear();

        try
        {
            if (!File.Exists(_favoritesFilePath))
            {
                RefreshFavoritesPanelItems();
                return;
            }

            string rawJson = File.ReadAllText(_favoritesFilePath);
            using JsonDocument loaded = JsonDocument.Parse(rawJson);
            foreach (JsonElement item in loaded.RootElement.EnumerateArray())
            {
                Favorite? favorite = item.ValueKind switch
                {
                    JsonValueKind.String => new Favorite(item.GetString() ?? "", "", null),
                    JsonValueKind.Object => item.Deserialize<Favorite>(),
                    _ => null,
                };
                if (favorite is null || !TryNormalizeUrl(favorite.Url, out string normalized))
                {
                    continue;
                }

                if (FindFavorite(normalized) < 0)
                {
                    _favorites.Add(favorite with { Url = normalized });
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Favourites load failed: {ex.Message}");
            // favorites.json is rewritten on exit, which would otherwise replace the saved favourites with an empty list.
            PreserveUnreadableFile(_favoritesFilePath, "favourites file", "Favourites start empty for this session.");
        }

        RefreshFavoritesPanelItems();
        UpdateFavoriteButtonState();
    }

    private void SaveFavorites()
    {
        try
        {
            string json = JsonSerializer.Serialize(
                _favorites,
                new JsonSerializerOptions { WriteIndented = true });

            WriteFileAtomically(_favoritesFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"Favourites save failed: {ex.Message}");
        }
    }

    private void SetTabFavicon(BrowserTab tab, Image image, bool ownsImage)
    {
        tab.FaviconSequence = tab.NavigationSequence;
        if (tab.OwnedFaviconImage is not null)
        {
            tab.OwnedFaviconImage.Dispose();
            tab.OwnedFaviconImage = null;
        }

        tab.FaviconBox.Image = image;
        if (ownsImage)
        {
            tab.OwnedFaviconImage = image;
        }
        UpdateSavedFavorite(tab);
    }

    private void AddHistoryEntry(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (_historyEntries.Count == 0 ||
            !_historyEntries[^1].Url.Equals(url, StringComparison.OrdinalIgnoreCase))
        {
            _historyEntries.Add(new HistoryEntry
            {
                Url = url,
                VisitedAtUtc = DateTimeOffset.UtcNow,
            });

            PruneHistoryByRetention();
            if (_settings.PersistHistoryAcrossSessions)
            {
                SaveHistory();
            }

            if (_historyPanel.Visible)
            {
                RefreshHistoryPanelItems();
            }
        }
    }

    private static CoreWebView2WebResourceResponse CreateBlockedResourceResponse(CoreWebView2 core)
    {
        byte[] payload = Encoding.UTF8.GetBytes("Blocked by PhantomHaze extension.");
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(payload),
            403,
            "Forbidden",
            "Content-Type: text/plain; charset=utf-8");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        if (TryGetBlockingExtensionRule(e.Request.Uri, out LoadedExtension? blockingExtension, out PhantomExtensionWebBlockDefinition? blockingRule))
        {
            if (sender is CoreWebView2 core)
            {
                e.Response = CreateBlockedResourceResponse(core);
            }

            Log($"Tab {tab.Id} blocked by extension {NormalizeForLog(blockingExtension!.Definition.Name)} ({blockingRule!.Id}): {TrimForLog(e.Request.Uri, 220)}");
            return;
        }

        // Cookies sent with requests are no longer logged one by one: new cookies are summarised in a single
        // "N cookies were registered" line (see QueueCookieRegistrationCheck).
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!_webViewModeEnabled || !TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        try
        {
            CoreWebView2WebResourceResponseView? response = e.Response;
            if (response is not null &&
                response.Headers.Any(header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                QueueCookieRegistrationCheck(tab);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // The tab is closing; nothing to report.
        }
    }

    /// <summary>
    /// Cookies used to be logged one line each - every cookie in the jar after every page load and every Set-Cookie
    /// response - which buried everything else in the log. Now new cookies are counted a moment after a page load or
    /// a burst of Set-Cookie responses and reported in one line: "[time] 12 cookies were registered".
    /// Cookie names and values are never logged.
    /// </summary>
    private void QueueCookieRegistrationCheck(BrowserTab tab)
    {
        _cookieCheckTab = tab;
        _cookieCheckTimer.Stop();
        _cookieCheckTimer.Start();
    }

    private async void OnCookieCheckTimerTick(object? sender, EventArgs e)
    {
        _cookieCheckTimer.Stop();
        if (_cookieCheckInProgress)
        {
            // A check is still waiting for WebView2; look again once it is done.
            _cookieCheckTimer.Start();
            return;
        }

        // Every WebView2 tab of the shared environment uses one cookie store, so any of them can be asked. (Proxy
        // protocols have cookie stores of their own, which are neither counted nor saved.)
        CoreWebView2? core = _cookieCheckTab is { } preferred && _tabs.Contains(preferred) &&
                             preferred.Browser.InnermostPage is WebView2ProtocolPage { UsesIsolatedEnvironment: false } preferredPage
            ? preferredPage.Core
            : null;
        core ??= FindSharedWebView2Core();
        _cookieCheckTab = null;
        if (core is null)
        {
            return;
        }

        _cookieCheckInProgress = true;
        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(null);
            int registered = 0;
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                if (_knownCookieKeys.Add($"{cookie.Name}\n{cookie.Domain}\n{cookie.Path}"))
                {
                    registered++;
                }
            }

            if (registered > 0 && !IsDisposed)
            {
                Log(registered == 1 ? "1 cookie was registered" : $"{registered} cookies were registered");
            }

            // With Settings -> Save cookies across sessions on, the saved cookies follow the cookie store.
            if (!IsDisposed)
            {
                WriteSavedCookies(cookies);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // The tab closed while its cookies were being read.
        }
        finally
        {
            _cookieCheckInProgress = false;
        }
    }

    private void CleanupWebSession()
    {
        if (NextUser is not null) LogForNextUser = _logBox.Text;
        _updateCheckCancellation.Cancel();
        SaveSettings();
        SaveHistory();
        SaveFavorites();

        _historyEntries.Clear();
        _historyPanel.Visible = false;
        _findPanel.Visible = false;
        _protectionRestoreTimer.Stop();
        _clockTimer.Stop();
        _rainbowTimer.Stop();
        _cookieCheckTimer.Stop();
        _zoomLogTimer.Stop();
        _fontLoadCancellation?.Cancel();
        OnZoomLogTimerTick(this, EventArgs.Empty);

        _backgroundRenderTimer.Stop();
        foreach (BrowserTab tab in _tabs.ToArray())
        {
            RemoveTabAndDispose(tab);
        }

        _tabByCore.Clear();
        _workerRequestFilterCores.Clear();
        _activeTab = null;
        _uiThreadWindowGuard?.Dispose();
        _uiThreadWindowGuard = null;
        LegacyBrowserSupport.Cleanup();

        foreach (ExtensionWindowState extensionWindowState in _openExtensionWindowsById.Values.ToArray())
        {
            try
            {
                extensionWindowState.Window.Close();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        _openExtensionWindowsById.Clear();
        _protectedAuxiliaryWindows.Clear();

        foreach (ExtensionPageState pageState in _extensionPages.Values.ToArray())
        {
            DisposeExtensionPage(pageState);
        }

        _extensionPages.Clear();
        _activeExtensionPageKey = null;

        try
        {
            _extensionLauncherToolTip.Dispose();
            _extensionMenuOverflowMenu.Dispose();
            _extensionDropdownMenu.Dispose();
            _extensionUiToolTip.Dispose();
            _updateBannerToolTip.Dispose();
            _headerToolTip.Dispose();
            _resourceSaverToolTip.Dispose();
            _updateBannerSnoozeMenu.Dispose();
            _clockTimer.Dispose();
            _backgroundRenderTimer.Dispose();
            _rainbowTimer.Dispose();
            _cookieCheckTimer.Dispose();
            _zoomLogTimer.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }

        try
        {
            _faviconHttpClient.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }

        // Shared fonts are disposed in Dispose(bool), after the controls using them: disposing them here, while the
        // window can still repaint, risked drawing with a disposed font.
        // The session lock is released first (it deletes itself), so the whole folder can go.
        _webSessionLock?.Dispose();
        _webSessionLock = null;
        TryDeleteDirectoryWithRetry(_webSessionPath);
    }

    private static void TryDeleteDirectoryWithRetry(string path)
    {
        const int attempts = 5;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                if (i == attempts - 1)
                {
                    return;
                }

                Thread.Sleep(120);
            }
        }
    }

    /// <summary>
    /// Alt+Left / Alt+Right (and the keyboard's Back / Forward keys) go back and forward in Web View. WebView2 passes these
    /// keys here as accelerator keys, so they are handled once; the Legacy engine handles them itself.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_webViewModeEnabled && _activeTab is not null)
        {
            if (keyData is (Keys.Alt | Keys.Left) or Keys.BrowserBack)
            {
                GoBack();
                return true;
            }

            if (keyData is (Keys.Alt | Keys.Right) or Keys.BrowserForward)
            {
                GoForward();
                return true;
            }
        }

        // Ctrl+L, Ctrl+T, Ctrl+W, F5, Ctrl+H...: they also reach what a top bar style leaves out (MainForm.TopBar.cs).
        if (HandleTopBarShortcut(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnMainFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.F)
        {
            e.SuppressKeyPress = true;
            ShowFindPanel();
            return;
        }

        if (e.KeyCode == Keys.Escape && _findPanel.Visible)
        {
            e.SuppressKeyPress = true;
            HideFindPanel(clearHighlights: true);
        }
    }

    private void ApplyProtection(bool enable)
    {
        var (ok, error) = NativeMethods.ExcludeFromCapture(Handle, enable);

        Log(ok
            ? $"SetWindowDisplayAffinity({(enable ? "WDA_EXCLUDEFROMCAPTURE" : "WDA_NONE")}) succeeded."
            : $"SetWindowDisplayAffinity failed. Win32 error {error}: {NativeMethods.DescribeError(error)}.");

        if (NativeMethods.TryGetCurrentAffinity(Handle, out uint current))
        {
            _protectionEnabled = current == NativeMethods.WDA_EXCLUDEFROMCAPTURE;
            Log($"Current window affinity: {NativeMethods.Describe(current)}.");

            if (enable && !_protectionEnabled)
            {
                Log("ALERT: Capture protection request is being blocked or downgraded; the window is not using WDA_EXCLUDEFROMCAPTURE.");
            }
        }
        else
        {
            _protectionEnabled = ok && enable;
            Log("GetWindowDisplayAffinity failed to read back the current state.");

            if (enable && !_protectionEnabled)
            {
                Log("ALERT: Could not confirm capture protection state and protection is currently not active.");
            }
        }

        if (_protectionEnabled)
        {
            _protectionRestoreTimer.Stop();
            _protectionResumeAtUtc = null;
        }

        _protectionEnableFailed = enable && !_protectionEnabled;

        // Keep the Settings window and extension windows in the same capture state as the main window.
        SyncAuxiliaryWindowProtection();

        UpdateProtectionStatusVisual();
    }

    /// <param name="source">The Web View banner's switch or the protection button of the other views.</param>
    private void OnProtectionButtonClick(Control source)
    {
        if (_protectionEnabled)
        {
            // Opens the menu of how long protection should be off for, right under the switch or button.
            if (ReferenceEquals(source, _protectionBanner))
            {
                Rectangle toggle = _protectionBanner.ToggleBounds;
                _disableProtectionMenu.Show(_protectionBanner, new Point(toggle.Left, toggle.Bottom + ScaleByDpi(2)));
            }
            else
            {
                _disableProtectionMenu.Show(source, new Point(0, source.Height));
            }

            return;
        }

        _protectionRestoreTimer.Stop();
        _protectionResumeAtUtc = null;
        ApplyProtection(enable: true);
        if (_protectionEnabled)
        {
            Log("Protection re-enabled manually.");
        }
    }

    private void DisableProtectionForDuration(string label, TimeSpan? duration)
    {
        ApplyProtection(enable: false);
        if (_protectionEnabled)
        {
            Log($"Protection disable request failed for duration: {label}.");
            return;
        }

        if (duration is null)
        {
            _protectionRestoreTimer.Stop();
            _protectionResumeAtUtc = null;
            UpdateProtectionStatusVisual();
            Log("Protection disabled until manually re-enabled.");
            return;
        }

        _protectionResumeAtUtc = DateTimeOffset.UtcNow.Add(duration.Value);
        _protectionRestoreTimer.Stop();
        _protectionRestoreTimer.Interval = 1000;
        _protectionRestoreTimer.Start();
        UpdateProtectionStatusVisual();
        Log($"Protection disabled for {label} (until {_protectionResumeAtUtc.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}).");
    }

    private void OnProtectionRestoreTimerTick(object? sender, EventArgs e)
    {
        if (_protectionResumeAtUtc is null)
        {
            _protectionRestoreTimer.Stop();
            return;
        }

        if (DateTimeOffset.UtcNow < _protectionResumeAtUtc.Value)
        {
            UpdateProtectionStatusVisual();
            return;
        }

        _protectionRestoreTimer.Stop();
        _protectionResumeAtUtc = null;
        ApplyProtection(enable: true);
        if (_protectionEnabled)
        {
            Log("Timed protection disable period has ended; protection re-enabled automatically.");
        }
    }

    /// <summary>
    /// Shows the real protection state (read back from Windows in ApplyProtection) on the protection banner: green and
    /// ON while the window is excluded from capture, red and OFF (with the countdown of a timed pause) while it is not.
    /// </summary>
    private void UpdateProtectionStatusVisual()
    {
        // Web View's card has a title and a short line; the status banner of the other views has one sentence.
        string title;
        string subtitle;
        string? countdown = null;
        string statusText;
        string tooltip;
        if (_protectionEnabled)
        {
            title = "Protection Enabled";
            subtitle = "All good over here!";
            statusText = "Protection Enabled - This window is excluded from supported capture software.";
            tooltip = "This window is excluded from supported screen capture, recording and sharing software.";
        }
        else if (_protectionResumeAtUtc is not null)
        {
            TimeSpan remaining = _protectionResumeAtUtc.Value - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            title = "Protection Disabled";
            subtitle = "Re-enables automatically in";
            countdown = FormatProtectionCountdown(remaining);
            statusText = $"Protection Disabled - This window is visible to capture software. Auto re-enables in {countdown}.";
            tooltip = "This window is visible to capture software until protection turns back on.";
        }
        else if (_protectionEnableFailed)
        {
            title = "Protection Disabled";
            subtitle = "Windows did not allow capture protection. See the logs.";
            statusText = "Protection Disabled - Windows did not allow capture protection, so this window is visible to capture software.";
            tooltip = "This window is visible to capture software: Windows refused to exclude it from capture.";
        }
        else
        {
            title = "Protection Disabled";
            subtitle = "You might wanna turn this back on...";
            statusText = "Protection Disabled - This window is visible to capture software.";
            tooltip = "This window is visible to capture software.";
        }

        bool dark = IsDarkTheme();
        _protectionBanner.DarkStyle = dark;
        _protectionBanner.SetStatus(_protectionEnabled, title, subtitle, countdown);
        _protectionSwitch.DarkStyle = dark;
        _protectionSwitch.SetStatus(_protectionEnabled, countdown is null ? $"{title}. {subtitle}" : $"{title}. {subtitle} {countdown}");
        if (!string.Equals(_headerToolTip.GetToolTip(_protectionSwitch), statusText, StringComparison.Ordinal))
        {
            _headerToolTip.SetToolTip(_protectionSwitch, statusText);
        }

        UpdateTopBarRevealButton();
        _protectionStatusBar.DarkStyle = dark;
        _protectionStatusBar.SetStatus(_protectionEnabled, statusText);
        string buttonText = _protectionEnabled ? "Disable Protection ▼" : "Enable Protection";
        if (!string.Equals(_toggleButton.Text, buttonText, StringComparison.Ordinal))
        {
            _toggleButton.Text = buttonText;
        }

        foreach (Control status in new Control[] { _protectionBanner, _protectionStatusBar })
        {
            if (!string.Equals(_headerToolTip.GetToolTip(status), tooltip, StringComparison.Ordinal))
            {
                _headerToolTip.SetToolTip(status, tooltip);
            }
        }
    }

    private static string FormatProtectionCountdown(TimeSpan remaining)
    {
        int totalHours = (int)Math.Floor(remaining.TotalHours);
        if (totalHours >= 24)
        {
            int days = totalHours / 24;
            int hours = totalHours % 24;
            return $"{days}d {hours:00}h {remaining.Minutes:00}m {remaining.Seconds:00}s";
        }

        if (totalHours > 0)
        {
            return $"{totalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        return $"{remaining.Minutes}:{remaining.Seconds:00}";
    }

    private void UpdateZoomControlsForActiveTab()
    {
        bool enabled = _webViewModeEnabled && _activeTab is not null && _activeTab.Page.Features.HasFlag(WebProtocolFeatures.Zoom);
        _zoomSlider.Enabled = enabled;

        int zoomPercent = enabled ? Math.Clamp(_activeTab!.ZoomPercent, _zoomSlider.Minimum, _zoomSlider.Maximum) : 100;
        if (_zoomSlider.Value != zoomPercent)
        {
            _zoomSlider.Value = zoomPercent;
        }

        _zoomLabel.Text = $"Zoom {zoomPercent}%";
    }

    private void UpdateResponsiveLayout()
    {
        int preferredHistoryWidth = ClientSize.Width / 3;
        _historyPanel.Width = Math.Clamp(preferredHistoryWidth, ScaleByDpi(260), ScaleByDpi(420));

        UpdateAddressStripLayout();
        if (_findPanel.Visible)
        {
            PositionFindPanel();
        }

        PositionExtensionLauncherPanel();
    }

    /// <summary>The width of the new tab (+) column of the tab row (0 when the style leaves the button out).</summary>
    private int NewTabColumnWidth => _topBarStyleHiddenControls.Contains(_newTabButton) ? 0 : ScaleByDpi(CurrentTopBarMetrics.StripButtonWidth - 2);

    private void UpdateAddressStripLayout()
    {
        if (_addressStripLayout is null || _addressStripLayout.ColumnStyles.Count < 6 ||
            _tabsLayout is null || _tabsLayout.ColumnStyles.Count < 4)
        {
            return;
        }

        TopBarMetrics metrics = CurrentTopBarMetrics;
        bool simple = CurrentTopBarStyle == TopBarStyle.Simple;
        int availableWidth = Math.Max(1, _addressBarPanel.ClientSize.Width - _addressBarPanel.Padding.Horizontal);
        int compactThreshold = ScaleByDpi(MinimumAddressInputWidth) + (5 * ScaleByDpi(metrics.StripButtonWidth)) + ScaleByDpi(160);
        bool compact = availableWidth < compactThreshold;
        int buttonWidth = ScaleByDpi(compact ? metrics.NarrowStripButtonWidth : metrics.StripButtonWidth);

        // Which column holds what: the Simple top bar puts Back and Forward before the address box and leaves out Go,
        // Refresh and History (their columns get no width).
        Control[] order = simple
            ? [_backButton, _forwardButton, _addressPill, _goButton, _refreshButton, _historyButton]
            : [_addressPill, _backButton, _forwardButton, _goButton, _refreshButton, _historyButton];
        _addressStripLayout.SuspendLayout();
        try
        {
            for (int column = 0; column < order.Length; column++)
            {
                Control control = order[column];
                if (_addressStripLayout.GetColumn(control) != column)
                {
                    _addressStripLayout.SetColumn(control, column);
                }

                ColumnStyle style = _addressStripLayout.ColumnStyles[column];
                if (ReferenceEquals(control, _addressPill))
                {
                    style.SizeType = SizeType.Percent;
                    style.Width = 100f;
                }
                else
                {
                    style.SizeType = SizeType.Absolute;
                    style.Width = _topBarStyleHiddenControls.Contains(control) ? 0 : buttonWidth;
                }
            }
        }
        finally
        {
            _addressStripLayout.ResumeLayout(true);
        }

        // The tab row: tabs | new tab | zoom label | zoom slider (the Simple top bar: only the tabs).
        _tabsLayout.ColumnStyles[1].Width = NewTabColumnWidth;
        _tabsLayout.ColumnStyles[2].Width = simple ? 0 : ScaleByDpi(metrics.ZoomLabelWidth);
        _tabsLayout.ColumnStyles[3].Width = simple ? 0 : ScaleByDpi(compact ? metrics.NarrowZoomSliderWidth : metrics.ZoomSliderWidth);

        _zoomLabel.TextAlign = compact ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter;
        _zoomLabel.Margin = compact
            ? ScalePadding(0, 2, 3, 2)
            : ScalePadding(3, 2, 3, 2);
    }

    private string GetHistoryTitleText()
    {
        return _settings.PersistHistoryAcrossSessions
            ? "Saved History"
            : "Session History";
    }

    private string GetNoHistoryMessage()
    {
        return _settings.PersistHistoryAcrossSessions
            ? "(No saved history yet)"
            : "(No history in this session)";
    }

    private List<ExtensionListItem> BuildExtensionDraftItems(
        IReadOnlyList<InstalledExtensionEntry> draftExtensions,
        IReadOnlyDictionary<string, string> stagedSourcesById)
    {
        var items = new List<ExtensionListItem>(draftExtensions.Count);
        foreach (InstalledExtensionEntry installed in draftExtensions)
        {
            try
            {
                string source;
                if (!stagedSourcesById.TryGetValue(installed.Id, out source!))
                {
                    string extensionPath = Path.Combine(_extensionsRootPath, installed.FileName);
                    source = File.ReadAllText(extensionPath);
                }

                // The parsed id can differ from the stored one when the file was edited on disk. The stored file
                // name is deliberately left alone: pointing the entry at a new name would make Save delete the
                // existing source file without ever writing a replacement. LoadInstalledExtensions reconciles the
                // id after Save, exactly as it does at startup.
                PhantomExtensionDefinition definition = PhextParser.Parse(source, GetExtensionParserSourceName(installed));

                if (string.IsNullOrWhiteSpace(installed.DisplayName))
                {
                    installed.DisplayName = definition.Name;
                }

                EnsureExtensionSettingValues(installed, definition);
                items.Add(new ExtensionListItem(installed, definition, loadError: null));
            }
            catch (Exception ex)
            {
                items.Add(new ExtensionListItem(installed, definition: null, loadError: ex.Message));
            }
        }

        return items;
    }

    /// <summary>
    /// Commits a Settings save as one unit: staged extension sources are first written to temporary files, then
    /// swapped into place (keeping backups), then settings.json is written. If any step fails, every swapped file
    /// is restored and the exception is rethrown so nothing is applied; settings.json and the extensions folder
    /// therefore never disagree. Source files of uninstalled extensions are deleted only after the commit.
    /// </summary>
    private void CommitSettingsChanges(
        AppSettings newSettings,
        IReadOnlyCollection<string> previousFileNames,
        IReadOnlyCollection<string> referencedFileNames,
        IReadOnlyDictionary<string, string> stagedSourcesById)
    {
        var writes = new List<(string TargetPath, string Source)>();
        foreach (InstalledExtensionEntry entry in newSettings.Extensions)
        {
            if (stagedSourcesById.TryGetValue(entry.Id, out string? source))
            {
                writes.Add((Path.Combine(_extensionsRootPath, entry.FileName), source));
            }
        }

        List<string> removedFileNames = previousFileNames
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Except(referencedFileNames, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stagedFiles = new List<(string TempPath, string TargetPath)>();
        var committedFiles = new List<(string TargetPath, string? BackupPath)>();
        var backupPaths = new List<string>();
        try
        {
            if (writes.Count > 0)
            {
                Directory.CreateDirectory(_extensionsRootPath);
            }

            foreach ((string targetPath, string source) in writes)
            {
                string tempPath = BuildSidecarFilePath(targetPath, ExtensionStagingFileSuffix);
                stagedFiles.Add((tempPath, targetPath));
                File.WriteAllText(tempPath, source);
            }

            foreach ((string tempPath, string targetPath) in stagedFiles)
            {
                string? backupPath = null;
                if (File.Exists(targetPath))
                {
                    backupPath = BuildSidecarFilePath(targetPath, ExtensionBackupFileSuffix);
                    backupPaths.Add(backupPath);
                    File.Copy(targetPath, backupPath);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                committedFiles.Add((targetPath, backupPath));
            }

            try
            {
                WriteSettingsFile(newSettings);
            }
            catch (Exception ex) when (writes.Count == 0 && removedFileNames.Count == 0)
            {
                // No extension files are involved, so keep the previous behaviour for plain settings changes:
                // apply them now and let the next save (for example on exit) persist them.
                Log($"Settings save failed: {ex.Message}");
            }
        }
        catch
        {
            List<string> unrestoredBackups = RollBackExtensionFiles(committedFiles);
            foreach ((string tempPath, _) in stagedFiles)
            {
                TryDeleteFile(tempPath);
            }

            foreach (string backupPath in backupPaths)
            {
                if (unrestoredBackups.Contains(backupPath, StringComparer.OrdinalIgnoreCase))
                {
                    // The restore failed, so this backup is the only copy of the previous source: keep it.
                    Log($"The previous version of an extension file was kept at {backupPath}.");
                    continue;
                }

                TryDeleteFile(backupPath);
            }

            throw;
        }

        foreach (string backupPath in backupPaths)
        {
            TryDeleteFile(backupPath);
        }

        foreach (string removedFileName in removedFileNames)
        {
            TryDeleteFile(Path.Combine(_extensionsRootPath, removedFileName));
        }
    }

    /// <returns>Backups that could not be restored (they are the only remaining copy of the previous source).</returns>
    private List<string> RollBackExtensionFiles(List<(string TargetPath, string? BackupPath)> committedFiles)
    {
        var unrestoredBackups = new List<string>();
        for (int i = committedFiles.Count - 1; i >= 0; i--)
        {
            (string targetPath, string? backupPath) = committedFiles[i];
            try
            {
                if (backupPath is null)
                {
                    File.Delete(targetPath);
                }
                else
                {
                    File.Copy(backupPath, targetPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Log($"Extension file rollback failed for '{Path.GetFileName(targetPath)}': {ex.Message}");
                if (backupPath is not null)
                {
                    unrestoredBackups.Add(backupPath);
                }
            }
        }

        return unrestoredBackups;
    }

    private void CleanupStaleExtensionTransactionFiles()
    {
        try
        {
            foreach (string stagingPath in Directory.EnumerateFiles(_extensionsRootPath, "*" + ExtensionStagingFileSuffix).ToList())
            {
                TryDeleteFile(stagingPath);
            }

            // A leftover backup means a save was interrupted (or its rollback failed), so it can be the only copy of an
            // extension's previous source. It is moved aside rather than deleted.
            foreach (string backupPath in Directory.EnumerateFiles(_extensionsRootPath, "*" + ExtensionBackupFileSuffix).ToList())
            {
                PreserveStaleExtensionBackup(backupPath);
            }
        }
        catch (Exception ex)
        {
            Log($"Extension folder cleanup skipped: {ex.Message}");
        }
    }

    private void PreserveStaleExtensionBackup(string backupPath)
    {
        try
        {
            string recoveredDirectory = Path.Combine(_extensionsRootPath, "recovered");
            Directory.CreateDirectory(recoveredDirectory);
            string originalFileName = GetBackupTargetFileName(Path.GetFileName(backupPath));
            string recoveredPath = Path.Combine(
                recoveredDirectory,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileNameWithoutExtension(originalFileName)}.recovered-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.phext"));
            File.Move(backupPath, recoveredPath);
            Log($"An interrupted Settings save left an earlier version of {originalFileName}; it was moved to {recoveredPath}.");
        }
        catch (Exception ex)
        {
            Log($"An earlier extension file version could not be moved aside ({Path.GetFileName(backupPath)}): {ex.Message}");
        }
    }

    /// <summary>Recovers "name.phext" from a backup file name built by BuildSidecarFilePath (".name.phext.{guid}.phext-backup").</summary>
    private static string GetBackupTargetFileName(string backupFileName)
    {
        string name = backupFileName;
        if (name.EndsWith(ExtensionBackupFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^ExtensionBackupFileSuffix.Length];
        }

        if (name.StartsWith('.'))
        {
            name = name[1..];
        }

        int lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && name.Length - lastDot - 1 == 32)
        {
            name = name[..lastDot];
        }

        return string.IsNullOrWhiteSpace(name) ? "extension.phext" : name;
    }

    private void OpenSettingsWindow()
    {
        AppSettings draftSettings = _settings.Clone();
        List<InstalledExtensionEntry> previousExtensions = _settings.Extensions
            .Select(entry => entry.Clone())
            .ToList();
        var stagedSourcesByExtensionId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // One set of fonts for the whole window, disposed with it (they used to be created per label and leaked).
        using var dialogFont = new Font("Segoe UI", 9, FontStyle.Regular);
        using var dialogBoldFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var dialogTitleFont = new Font("Segoe UI", 16, FontStyle.Bold);

        Label BuildSettingsLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = dialogFont,
            };
        }

        using var settingsWindow = new Form
        {
            Text = "PhantomHaze Settings",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(Math.Min(ScaleByDpi(760), Screen.FromControl(this).WorkingArea.Width - ScaleByDpi(24)),
                Math.Min(ScaleByDpi(580), Screen.FromControl(this).WorkingArea.Height - ScaleByDpi(60))),
            AutoScaleMode = AutoScaleMode.Dpi,
        };

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(44)));

        var settingsTabs = new TabControl
        {
            Dock = DockStyle.Fill,
        };

        // Set by Backup -> Import: the import has already been applied, so this window's draft must not be saved.
        bool settingsImported = false;

        var generalTab = new TabPage("General");
        SettingsTab appearanceTab = BuildAppearanceTab(draftSettings, settingsWindow, dialogBoldFont);
        var extensionsTab = new TabPage("Extensions");
        SettingsTab backupTab = BuildBackupTab(settingsWindow, dialogBoldFont, onImported: () =>
        {
            settingsImported = true;
            settingsWindow.DialogResult = DialogResult.Abort;
        });
        SettingsTab aboutTab = BuildAboutTab(settingsWindow, dialogBoldFont, dialogTitleFont);
        SettingsTab[] customTabs = [appearanceTab, backupTab, aboutTab];
        settingsTabs.TabPages.Add(generalTab);
        settingsTabs.TabPages.Add(appearanceTab.Page);
        settingsTabs.TabPages.Add(extensionsTab);
        settingsTabs.TabPages.Add(BuildUsersTab(settingsWindow));
        settingsTabs.TabPages.Add(backupTab.Page);
        settingsTabs.TabPages.Add(aboutTab.Page);
        settingsTabs.SelectedIndexChanged += (_, _) =>
        {
            customTabs.FirstOrDefault(tab => ReferenceEquals(tab.Page, settingsTabs.SelectedTab))?.Shown?.Invoke();
        };

        var generalLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 13,
            AutoScroll = true,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(250)));
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int generalRow = 0; generalRow < 6; generalRow++)
        {
            generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        }

        generalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));

        // How long a tab keeps rendering after it goes into the background (replaces "Render web pages in the
        // background": Forever is that setting turned on).
        var backgroundRenderComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Render background tabs for",
        };
        backgroundRenderComboBox.Items.AddRange(BackgroundTabRenderOptions);
        backgroundRenderComboBox.SelectedItem =
            BackgroundTabRenderOptions.FirstOrDefault(option => option.Value == draftSettings.BackgroundTabRenderSeconds) ??
            BackgroundTabRenderOptions.First(option => option.Value == DefaultBackgroundTabRenderSeconds);

        var resourceSaverCheckBox = new CheckBox
        {
            Text = $"Resource saver: save resources when more than {ResourceSaverTabThreshold} tabs are open",
            Dock = DockStyle.Fill,
            Checked = draftSettings.ResourceSaverEnabled,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleDescription = $"With more than {ResourceSaverTabThreshold} tabs open, background tabs stop rendering after {ResourceSaverRenderSeconds} seconds.",
        };

        var persistHistoryCheckBox = new CheckBox
        {
            Text = "Save history across sessions",
            Dock = DockStyle.Fill,
            Checked = draftSettings.PersistHistoryAcrossSessions,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var retentionComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled = draftSettings.PersistHistoryAcrossSessions,
        };
        OptionItem<int>[] retentionOptions = BuildRetentionOptions();
        retentionComboBox.Items.AddRange(retentionOptions);
        retentionComboBox.SelectedItem = retentionOptions.FirstOrDefault(option => option.Value == draftSettings.HistoryRetentionDays) ?? retentionOptions[0];

        persistHistoryCheckBox.CheckedChanged += (_, _) =>
        {
            retentionComboBox.Enabled = persistHistoryCheckBox.Checked;
        };

        // Cookies work like history: not saved unless this is turned on, and saved cookies are deleted after a while.
        var persistCookiesCheckBox = new CheckBox
        {
            Text = "Save cookies across sessions",
            Dock = DockStyle.Fill,
            Checked = draftSettings.PersistCookiesAcrossSessions,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var cookieRetentionComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled = draftSettings.PersistCookiesAcrossSessions,
            Margin = ScalePadding(0, 3, 8, 3),
        };
        OptionItem<int>[] cookieRetentionOptions = BuildRetentionOptions();
        cookieRetentionComboBox.Items.AddRange(cookieRetentionOptions);
        cookieRetentionComboBox.SelectedItem = cookieRetentionOptions.FirstOrDefault(option => option.Value == draftSettings.CookieRetentionDays) ?? cookieRetentionOptions[0];

        var clearCookiesButton = new Button
        {
            Text = "Clear cookies",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = ScalePadding(0, 2, 0, 2),
            AccessibleDescription = "Deletes saved cookies and the cookies of this session.",
        };
        var cookieRetentionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        cookieRetentionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        cookieRetentionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cookieRetentionLayout.Controls.Add(cookieRetentionComboBox, 0, 0);
        cookieRetentionLayout.Controls.Add(clearCookiesButton, 1, 0);

        var cookieNoteLabel = new Label
        {
            Text = "Saved cookies keep you signed in to websites opened with the WebView2 protocol. They are encrypted " +
                   "for your Windows account and deleted after the time you choose.",
            AutoSize = true,
            MaximumSize = new Size(ScaleByDpi(440), 0),
            Margin = ScalePadding(3, 0, 0, 8),
            Font = dialogFont,
        };

        persistCookiesCheckBox.CheckedChanged += (_, _) =>
        {
            cookieRetentionComboBox.Enabled = persistCookiesCheckBox.Checked;
        };

        clearCookiesButton.Click += (_, _) =>
        {
            DialogResult answer = ShowCaptureProtectedDialog(() => MessageBox.Show(
                settingsWindow,
                "Delete all cookies? This signs you out of websites, including those open in Web View, and removes " +
                "every saved cookie. This cannot be undone.",
                "PhantomHaze",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2));
            if (answer != DialogResult.Yes)
            {
                return;
            }

            ClearCookies();
            clearCookiesButton.Text = "Cookies cleared";
            clearCookiesButton.Enabled = false;
        };

        var searchEngineComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        OptionItem<SearchEngine>[] searchOptions =
        [
            new OptionItem<SearchEngine>(SearchEngine.Google, "Google"),
            new OptionItem<SearchEngine>(SearchEngine.Bing, "Bing"),
            new OptionItem<SearchEngine>(SearchEngine.Yahoo, "Yahoo"),
            new OptionItem<SearchEngine>(SearchEngine.DuckDuckGo, "DuckDuckGo"),
            new OptionItem<SearchEngine>(SearchEngine.Startpage, "Startpage"),
        ];

        foreach (OptionItem<SearchEngine> option in searchOptions)
        {
            searchEngineComboBox.Items.Add(option);
        }

        OptionItem<SearchEngine>? selectedSearchOption = searchOptions.FirstOrDefault(option => option.Value == draftSettings.DefaultSearchEngine) ?? searchOptions[0];
        searchEngineComboBox.SelectedItem = selectedSearchOption;

        var startPageTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = draftSettings.StartingPage,
        };

        // Web View protocols: how Web View loads pages. The built-in ones first, then those enabled extensions add.
        var protocolComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Web View protocol",
        };
        var protocolsById = new Dictionary<string, WebProtocol>(StringComparer.OrdinalIgnoreCase);
        var protocolOptions = new List<OptionItem<string>>();
        foreach (WebProtocol protocol in GetAvailableWebProtocols())
        {
            if (!protocolsById.TryAdd(protocol.Id, protocol))
            {
                continue;
            }

            string label = protocol is ExtensionWebProtocol extensionProtocol
                ? $"{protocol.DisplayName} (from {extensionProtocol.ExtensionName})"
                : protocol.DisplayName;
            if (protocol.GetUnavailableReason() is not null)
            {
                label += " - not available";
            }

            protocolOptions.Add(new OptionItem<string>(protocol.Id, label));
        }

        OptionItem<string>? selectedProtocolOption = protocolOptions.FirstOrDefault(option =>
            option.Value.Equals(draftSettings.WebViewProtocol, StringComparison.OrdinalIgnoreCase));
        if (selectedProtocolOption is null)
        {
            // The chosen protocol comes from an extension that is disabled or uninstalled: the choice is kept for when
            // it comes back, and Web View uses WebView2 meanwhile.
            selectedProtocolOption = new OptionItem<string>(draftSettings.WebViewProtocol, $"{draftSettings.WebViewProtocol} (not available now)");
            protocolOptions.Add(selectedProtocolOption);
        }

        protocolComboBox.Items.AddRange(protocolOptions.ToArray());
        protocolComboBox.SelectedItem = selectedProtocolOption;

        var protocolDescriptionLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(ScaleByDpi(440), 0),
            Margin = ScalePadding(3, 0, 0, 8),
            Font = dialogFont,
            UseMnemonic = false,
        };

        void UpdateProtocolDescription()
        {
            if (protocolComboBox.SelectedItem is not OptionItem<string> option)
            {
                protocolDescriptionLabel.Text = string.Empty;
                return;
            }

            if (!protocolsById.TryGetValue(option.Value, out WebProtocol? protocol))
            {
                protocolDescriptionLabel.Text =
                    "This protocol comes from an extension that is turned off or not installed. Web View uses WebView2 until it is back.";
                return;
            }

            string applies = _tabs.Count > 0 ? " Open tabs are reopened with the new protocol when you click Save." : string.Empty;
            string? unavailable = protocol.GetUnavailableReason();
            protocolDescriptionLabel.Text = unavailable is null
                ? protocol.Description + applies
                : $"{protocol.Description}\n\nNot available on this PC: {unavailable}";
        }

        protocolComboBox.SelectedIndexChanged += (_, _) => UpdateProtocolDescription();
        UpdateProtocolDescription();

        var renderedScriptsCheckBox = new CheckBox
        {
            Text = "Run JavaScript in HTMLRenderer and HazeGate pages",
            Dock = DockStyle.Fill,
            Checked = draftSettings.RenderedPageScriptsEnabled,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleDescription = "Pages shown with the HTMLRenderer or HazeGate protocol run their scripts (with Jint). Turned off, they are shown as they are downloaded.",
        };

        // The theme moved to the Appearance tab, together with the text colour and font.
        generalLayout.Controls.Add(BuildSettingsLabel("Render background tabs for"), 0, 0);
        generalLayout.Controls.Add(backgroundRenderComboBox, 1, 0);

        generalLayout.Controls.Add(resourceSaverCheckBox, 0, 1);
        generalLayout.SetColumnSpan(resourceSaverCheckBox, 2);

        generalLayout.Controls.Add(persistHistoryCheckBox, 0, 2);
        generalLayout.SetColumnSpan(persistHistoryCheckBox, 2);

        generalLayout.Controls.Add(BuildSettingsLabel("Saved history retention"), 0, 3);
        generalLayout.Controls.Add(retentionComboBox, 1, 3);

        generalLayout.Controls.Add(persistCookiesCheckBox, 0, 4);
        generalLayout.SetColumnSpan(persistCookiesCheckBox, 2);

        generalLayout.Controls.Add(BuildSettingsLabel("Saved cookie retention"), 0, 5);
        generalLayout.Controls.Add(cookieRetentionLayout, 1, 5);

        generalLayout.Controls.Add(cookieNoteLabel, 1, 6);

        generalLayout.Controls.Add(BuildSettingsLabel("Default search engine"), 0, 7);
        generalLayout.Controls.Add(searchEngineComboBox, 1, 7);

        generalLayout.Controls.Add(BuildSettingsLabel("Web View starting page"), 0, 8);
        generalLayout.Controls.Add(startPageTextBox, 1, 8);

        generalLayout.Controls.Add(BuildSettingsLabel("Web View protocol"), 0, 9);
        generalLayout.Controls.Add(protocolComboBox, 1, 9);

        generalLayout.Controls.Add(protocolDescriptionLabel, 1, 10);

        generalLayout.Controls.Add(renderedScriptsCheckBox, 0, 11);
        generalLayout.SetColumnSpan(renderedScriptsCheckBox, 2);

        var inspect = new CheckBox { Text = "Inspect and edit page HTML in Web View", Checked = draftSettings.InspectHtml, AutoSize = true };
        generalLayout.Controls.Add(inspect, 0, 12);
        generalLayout.SetColumnSpan(inspect, 2);
        AddScrollingSettingsLayout(generalTab, generalLayout);

        // A built-in protocol that cannot run here is not saved (a missing extension's protocol is kept, see above).
        string? ValidateProtocolSelection()
        {
            if (protocolComboBox.SelectedItem is OptionItem<string> option &&
                protocolsById.TryGetValue(option.Value, out WebProtocol? protocol) &&
                protocol.GetUnavailableReason() is string reason)
            {
                return $"The {protocol.DisplayName} protocol cannot be used on this PC:\n{reason}\n\nChoose another Web View protocol on the General tab.";
            }

            return null;
        }

        var extensionsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        extensionsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        extensionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(34)));
        extensionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(38)));
        extensionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(28)));
        extensionsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        extensionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(28)));

        var extensionsEnabledCheckBox = new CheckBox
        {
            Dock = DockStyle.Fill,
            Text = "Enable all PhantomHaze extensions",
            Checked = draftSettings.ExtensionsEnabled,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var extensionActionsLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };

        var importExtensionButton = new Button
        {
            Text = "Import extension",
            AutoSize = true,
            Height = ScaleByDpi(30),
            Margin = ScalePadding(0, 2, 8, 2),
        };

        var uninstallAllExtensionsButton = new Button
        {
            Text = "Uninstall all extensions",
            AutoSize = true,
            Height = ScaleByDpi(30),
            Margin = ScalePadding(0, 2, 0, 2),
        };

        extensionActionsLayout.Controls.Add(importExtensionButton);
        extensionActionsLayout.Controls.Add(uninstallAllExtensionsButton);

        var installedExtensionsLabel = new Label
        {
            Text = "Installed extensions",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = dialogBoldFont,
        };

        var extensionSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Size = new Size(ScaleByDpi(700), ScaleByDpi(300)),

            Panel1MinSize = ScaleByDpi(220),
            Panel2MinSize = ScaleByDpi(280),
            SplitterDistance = ScaleByDpi(300),

        };

        var extensionsListBox = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };

        var extensionSettingsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
        };

        extensionSplit.Panel1.Controls.Add(extensionsListBox);
        extensionSplit.Panel2.Controls.Add(extensionSettingsPanel);

        var extensionStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "No extension changes pending.",
        };

        extensionsLayout.Controls.Add(extensionsEnabledCheckBox, 0, 0);
        extensionsLayout.Controls.Add(extensionActionsLayout, 0, 1);
        extensionsLayout.Controls.Add(installedExtensionsLabel, 0, 2);
        extensionsLayout.Controls.Add(extensionSplit, 0, 3);
        extensionsLayout.Controls.Add(extensionStatusLabel, 0, 4);

        extensionsTab.Controls.Add(extensionsLayout);

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Width = ScaleByDpi(90),
            Height = ScaleByDpi(30),
            Margin = ScalePadding(8, 0, 0, 0),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = ScaleByDpi(90),
            Height = ScaleByDpi(30),
        };

        buttonsPanel.Controls.Add(saveButton);
        buttonsPanel.Controls.Add(cancelButton);

        settingsWindow.AcceptButton = saveButton;
        settingsWindow.CancelButton = cancelButton;
        rootLayout.Controls.Add(settingsTabs, 0, 0);
        rootLayout.Controls.Add(buttonsPanel, 0, 1);
        settingsWindow.Controls.Add(rootLayout);

        List<ExtensionListItem> extensionItems = new();
        var activeNumericEditors = new List<NumericUpDown>();
        bool listPopulationInProgress = false;

        // Reading NumericUpDown.Value commits text the user typed but has not confirmed yet (for example when Enter
        // triggers Save while the editor still has focus), so that value is saved instead of silently dropped.
        saveButton.Click += (_, _) =>
        {
            foreach (NumericUpDown numericEditor in activeNumericEditors.Where(editor => !editor.IsDisposed))
            {
                _ = numericEditor.Value;
            }

            // Input that cannot be saved keeps the window open (the button's DialogResult is undone).
            string? problem = ValidateProtocolSelection() ??
                              customTabs.Select(tab => tab.Validate?.Invoke()).FirstOrDefault(message => message is not null);
            if (problem is not null)
            {
                settingsWindow.DialogResult = DialogResult.None;
                ShowCaptureProtectedDialog(() => MessageBox.Show(settingsWindow, problem, "PhantomHaze", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
        };

        void UninstallDraftExtension(ExtensionListItem item)
        {
            string name = item.ToString();
            DialogResult answer = ShowCaptureProtectedDialog(() => MessageBox.Show(
                settingsWindow,
                $"Uninstall \"{name}\"?\n\nIt is removed, together with its settings, when you click Save.",
                "PhantomHaze",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2));
            if (answer != DialogResult.Yes)
            {
                return;
            }

            // Rebuilding the list disposes the details panel - including the button whose Click is still being
            // handled - so it runs once the click has finished.
            settingsWindow.BeginInvoke(new Action(() =>
            {
                if (settingsWindow.IsDisposed)
                {
                    return;
                }

                draftSettings.Extensions.Remove(item.Installation);
                stagedSourcesByExtensionId.Remove(item.Installation.Id);
                RefreshExtensionDraftList();
                extensionStatusLabel.Text = $"\"{name}\" will be uninstalled when you click Save.";
            }));
        }

        Control BuildUninstallButtonRow(ExtensionListItem item)
        {
            var uninstallButton = new Button
            {
                Text = "Uninstall this extension",
                AutoSize = true,
                Height = ScaleByDpi(30),
                Margin = ScalePadding(0, 12, 0, 6),
                ForeColor = Color.FromArgb(178, 34, 34),
                AccessibleDescription = "Marks this extension for uninstallation; it is removed when you click Save.",
            };
            uninstallButton.Click += (_, _) => UninstallDraftExtension(item);
            return uninstallButton;
        }

        void RenderSelectedExtensionSettings()
        {
            // Dispose the previous editors: Controls.Clear() alone leaks their window handles on every selection change.
            foreach (Control previousControl in extensionSettingsPanel.Controls.Cast<Control>().ToArray())
            {
                previousControl.Dispose();
            }

            extensionSettingsPanel.Controls.Clear();
            activeNumericEditors.Clear();

            if (extensionsListBox.SelectedItem is not ExtensionListItem selectedItem)
            {
                extensionSettingsPanel.Controls.Add(new Label
                {
                    Dock = DockStyle.Top,
                    Text = "Select an extension to view its configurable settings.",
                    Height = ScaleByDpi(26),
                    TextAlign = ContentAlignment.MiddleLeft,
                });
                return;
            }

            if (!string.IsNullOrWhiteSpace(selectedItem.LoadError))
            {
                // A broken extension can still be uninstalled from here.
                var errorLayout = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                };
                errorLayout.Controls.Add(new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(Math.Max(ScaleByDpi(200), extensionSettingsPanel.ClientSize.Width - ScaleByDpi(24)), 0),
                    Text = $"This extension failed to load:\n{selectedItem.LoadError}",
                    ForeColor = Color.FromArgb(197, 70, 70),
                    UseMnemonic = false,
                });
                errorLayout.Controls.Add(BuildUninstallButtonRow(selectedItem));
                ApplyDialogTheme(errorLayout);
                extensionSettingsPanel.Controls.Add(errorLayout);
                ApplyAppFontToDialog(errorLayout);
                return;
            }

            PhantomExtensionDefinition? definition = selectedItem.Definition;
            if (definition is null)
            {
                return;
            }

            var detailsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };

            detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(150)));
            detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int row = 0;

            void AddMetaRow(string label, string value)
            {
                detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                detailsLayout.Controls.Add(new Label
                {
                    Text = label,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = dialogBoldFont,
                    Margin = ScalePadding(0, 0, 8, 6),
                }, 0, row);

                detailsLayout.Controls.Add(new Label
                {
                    Text = value,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoSize = true,
                    UseMnemonic = false,
                    Margin = ScalePadding(0, 0, 0, 6),
                }, 1, row);

                row++;
            }

            AddMetaRow("Name", definition.Name);
            AddMetaRow("ID", definition.Id);
            AddMetaRow("Version", definition.Version);
            if (!string.IsNullOrWhiteSpace(definition.Description))
            {
                AddMetaRow("Description", definition.Description);
            }

            if (definition.Settings.Count > 0)
            {
                detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var settingsHeader = new Label
                {
                    Text = "Extension settings",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = dialogBoldFont,
                    Margin = ScalePadding(0, 8, 0, 6),
                };
                detailsLayout.Controls.Add(settingsHeader, 0, row);
                detailsLayout.SetColumnSpan(settingsHeader, 2);
                row++;
            }

            foreach (PhantomExtensionSettingDefinition setting in definition.Settings)
            {
                detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                string currentValue = selectedItem.Installation.SettingValues.TryGetValue(setting.Key, out string? value)
                    ? value
                    : GetDefaultSettingValue(setting);

                if (setting.Type == PhantomExtensionSettingType.Toggle)
                {
                    bool checkedValue = TryParseBooleanSetting(currentValue, out bool parsed) && parsed;
                    var toggle = new CheckBox
                    {
                        Text = setting.Label,
                        Checked = checkedValue,
                        Dock = DockStyle.Fill,
                        UseMnemonic = false,
                        Margin = ScalePadding(0, 0, 0, 6),
                    };
                    toggle.CheckedChanged += (_, _) =>
                    {
                        selectedItem.Installation.SettingValues[setting.Key] = toggle.Checked ? "true" : "false";
                    };

                    detailsLayout.Controls.Add(toggle, 0, row);
                    detailsLayout.SetColumnSpan(toggle, 2);
                    row++;
                    continue;
                }

                detailsLayout.Controls.Add(new Label
                {
                    Text = setting.Label,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    UseMnemonic = false,
                    Margin = ScalePadding(0, 0, 8, 6),
                }, 0, row);

                Control editorControl;
                if (setting.Type == PhantomExtensionSettingType.Number)
                {
                    int minimum = setting.Minimum ?? -100000;
                    int maximum = Math.Max(minimum, setting.Maximum ?? 100000);
                    // Stored values are invariant-culture integers (see TryNormalizeSettingValue).
                    int numericValue = TryParseInvariantInt(currentValue, out int parsedValue)
                        ? Math.Clamp(parsedValue, minimum, maximum)
                        : Math.Clamp(TryParseInvariantInt(setting.DefaultValue, out int parsedDefault) ? parsedDefault : 0, minimum, maximum);

                    var numeric = new NumericUpDown
                    {
                        Dock = DockStyle.Fill,
                        Minimum = minimum,
                        Maximum = maximum,
                        Value = numericValue,
                        DecimalPlaces = 0,
                        Increment = 1,
                        Margin = ScalePadding(0, 0, 0, 6),
                    };
                    numeric.ValueChanged += (_, _) =>
                    {
                        // NumericUpDown accepts typed decimals such as "5.5" even with DecimalPlaces = 0. Only whole
                        // numbers are stored, otherwise the value is rejected (and reset to the default) on load.
                        decimal wholeValue = decimal.Round(numeric.Value, 0, MidpointRounding.AwayFromZero);
                        if (wholeValue != numeric.Value)
                        {
                            numeric.Value = wholeValue;
                            return;
                        }

                        selectedItem.Installation.SettingValues[setting.Key] =
                            decimal.ToInt32(wholeValue).ToString(CultureInfo.InvariantCulture);
                    };
                    activeNumericEditors.Add(numeric);

                    editorControl = numeric;
                }
                else if (setting.Type == PhantomExtensionSettingType.Select)
                {
                    var combo = new ComboBox
                    {
                        Dock = DockStyle.Fill,
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Margin = ScalePadding(0, 0, 0, 6),
                    };

                    foreach (string option in setting.SelectOptions)
                    {
                        combo.Items.Add(option);
                    }

                    string selectedOption = setting.SelectOptions.FirstOrDefault(option =>
                        option.Equals(currentValue, StringComparison.OrdinalIgnoreCase)) ?? setting.SelectOptions[0];
                    combo.SelectedItem = selectedOption;
                    combo.SelectedIndexChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string option)
                        {
                            selectedItem.Installation.SettingValues[setting.Key] = option;
                        }
                    };

                    editorControl = combo;
                }
                else
                {
                    var textBox = new TextBox
                    {
                        Dock = DockStyle.Fill,
                        Text = currentValue,
                        Margin = ScalePadding(0, 0, 0, 6),
                    };
                    textBox.TextChanged += (_, _) =>
                    {
                        selectedItem.Installation.SettingValues[setting.Key] = textBox.Text;
                    };
                    editorControl = textBox;
                }

                detailsLayout.Controls.Add(editorControl, 1, row);
                row++;
            }

            // Every extension's panel ends with a way to uninstall just that extension.
            detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Control uninstallRow = BuildUninstallButtonRow(selectedItem);
            detailsLayout.Controls.Add(uninstallRow, 0, row);
            detailsLayout.SetColumnSpan(uninstallRow, 2);

            ApplyDialogTheme(detailsLayout);
            extensionSettingsPanel.Controls.Add(detailsLayout);
            ApplyAppFontToDialog(detailsLayout);
        }

        void RefreshExtensionDraftList(string? preferredExtensionId = null)
        {
            extensionItems = BuildExtensionDraftItems(draftSettings.Extensions, stagedSourcesByExtensionId);
            listPopulationInProgress = true;

            extensionsListBox.Items.Clear();
            for (int index = 0; index < extensionItems.Count; index++)
            {
                ExtensionListItem item = extensionItems[index];
                extensionsListBox.Items.Add(item);
                extensionsListBox.SetItemChecked(index, item.Installation.Enabled);
            }

            int selectedIndex = -1;
            if (!string.IsNullOrWhiteSpace(preferredExtensionId))
            {
                selectedIndex = extensionItems.FindIndex(item =>
                    item.Installation.Id.Equals(preferredExtensionId, StringComparison.OrdinalIgnoreCase));
            }

            if (selectedIndex < 0 && extensionItems.Count > 0)
            {
                selectedIndex = 0;
            }

            if (selectedIndex >= 0)
            {
                extensionsListBox.SelectedIndex = selectedIndex;
            }

            extensionStatusLabel.Text = extensionItems.Count == 0
                ? "No extensions installed."
                : $"{extensionItems.Count} extension(s) in this settings draft.";

            listPopulationInProgress = false;
            RenderSelectedExtensionSettings();
        }

        extensionsListBox.SelectedIndexChanged += (_, _) =>
        {
            // While the list is rebuilt, Items.Clear() and the SelectedIndex assignment both raise this event;
            // RefreshExtensionDraftList renders once when it is done.
            if (!listPopulationInProgress)
            {
                RenderSelectedExtensionSettings();
            }
        };
        extensionsListBox.ItemCheck += (_, eventArgs) =>
        {
            if (listPopulationInProgress || eventArgs.Index < 0 || eventArgs.Index >= extensionItems.Count)
            {
                return;
            }

            extensionItems[eventArgs.Index].Installation.Enabled = eventArgs.NewValue == CheckState.Checked;
        };

        importExtensionButton.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Filter = "PhantomHaze extension (*.phext)|*.phext",
                Multiselect = false,
                CheckFileExists = true,
                Title = "Import PhantomHaze Extension",
            };

            if (ShowCaptureProtectedDialog(() => picker.ShowDialog(settingsWindow)) != DialogResult.OK)
            {
                return;
            }

            try
            {
                string rawSource = File.ReadAllText(picker.FileName);
                string sourceName = Path.GetFileName(picker.FileName);
                PhantomExtensionDefinition definition = PhextParser.Parse(rawSource, sourceName);

                InstalledExtensionEntry? existing = draftSettings.Extensions.FirstOrDefault(entry =>
                    entry.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    existing = new InstalledExtensionEntry
                    {
                        Id = definition.Id,
                        DisplayName = definition.Name,
                        FileName = string.Empty,
                        Enabled = true,
                        SettingValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    };

                    draftSettings.Extensions.Add(existing);
                }

                existing.Id = definition.Id;
                existing.DisplayName = definition.Name;
                existing.FileName = ChooseImportedExtensionFileName(existing, draftSettings.Extensions);
                existing.Enabled = true;
                EnsureExtensionSettingValues(existing, definition);

                stagedSourcesByExtensionId[definition.Id] = rawSource;
                RefreshExtensionDraftList(definition.Id);
                // Set after the refresh, which writes its own summary to this label.
                extensionStatusLabel.Text = $"Imported '{definition.Name}'. Click Save to install it.";
            }
            catch (Exception ex)
            {
                ShowCaptureProtectedDialog(() => MessageBox.Show(
                    settingsWindow,
                    $"Extension import failed:\n{ex.Message}",
                    "PhantomHaze",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning));
            }
        };

        uninstallAllExtensionsButton.Click += (_, _) =>
        {
            if (draftSettings.Extensions.Count == 0)
            {
                return;
            }

            DialogResult result = ShowCaptureProtectedDialog(() => MessageBox.Show(
                settingsWindow,
                "This will uninstall every extension when you save settings. Continue?",
                "PhantomHaze",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning));

            if (result != DialogResult.Yes)
            {
                return;
            }

            draftSettings.Extensions.Clear();
            stagedSourcesByExtensionId.Clear();
            RefreshExtensionDraftList();
            extensionStatusLabel.Text = "All extensions are marked for uninstallation. Click Save to apply.";
        };

        RefreshExtensionDraftList();

        ApplyDialogTheme(settingsWindow);
        ApplyAppFontToDialog(settingsWindow);
        RegisterProtectedAuxiliaryWindow(settingsWindow, "Settings");

        DialogResult result = settingsWindow.ShowDialog(this);
        if (NextUser is not null)
        {
            Close();
            return;
        }
        if (settingsImported || result != DialogResult.OK)
        {
            return;
        }

        appearanceTab.ApplyTo?.Invoke(draftSettings);
        if (backgroundRenderComboBox.SelectedItem is OptionItem<int> backgroundRenderOption)
        {
            // RenderWebViewInBackground follows it in NormalizeSettings.
            draftSettings.BackgroundTabRenderSeconds = backgroundRenderOption.Value;
        }

        draftSettings.ResourceSaverEnabled = resourceSaverCheckBox.Checked;
        draftSettings.InspectHtml = inspect.Checked;
        draftSettings.PersistHistoryAcrossSessions = persistHistoryCheckBox.Checked;

        if (retentionComboBox.SelectedItem is OptionItem<int> retentionOption)
        {
            draftSettings.HistoryRetentionDays = retentionOption.Value;
        }

        draftSettings.PersistCookiesAcrossSessions = persistCookiesCheckBox.Checked;
        if (cookieRetentionComboBox.SelectedItem is OptionItem<int> cookieRetentionOption)
        {
            draftSettings.CookieRetentionDays = cookieRetentionOption.Value;
        }

        if (searchEngineComboBox.SelectedItem is OptionItem<SearchEngine> searchOption)
        {
            draftSettings.DefaultSearchEngine = searchOption.Value;
        }

        if (protocolComboBox.SelectedItem is OptionItem<string> protocolOption)
        {
            draftSettings.WebViewProtocol = protocolOption.Value;
        }

        draftSettings.StartingPage = NormalizeStartPage(startPageTextBox.Text.Trim());
        draftSettings.ExtensionsEnabled = extensionsEnabledCheckBox.Checked;
        draftSettings.RenderedPageScriptsEnabled = renderedScriptsCheckBox.Checked;

        if (HasSettingsFileChangedSinceLastAccess() && TryReadSettingsFile() is AppSettings currentOnDisk)
        {
            MergeExternalExtensionChanges(draftSettings, previousExtensions, currentOnDisk.Extensions, stagedSourcesByExtensionId.Keys);
            Log("settings.json was changed by another PhantomHaze window; extensions it installed or uninstalled were kept.");
        }

        // File names referenced by the draft are captured before normalisation (which can drop invalid or
        // duplicate entries), so only extensions the user actually uninstalled have their source files deleted.
        HashSet<string> referencedFileNames = draftSettings.Extensions
            .Where(entry => entry is not null)
            .Select(entry => NormalizeStoredExtensionFileName(entry.FileName, entry.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        NormalizeSettings(draftSettings);
        referencedFileNames.UnionWith(draftSettings.Extensions.Select(entry => entry.FileName));
        HashSet<string> previousFileNames = previousExtensions
            .Select(entry => NormalizeStoredExtensionFileName(entry.FileName, entry.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            CommitSettingsChanges(draftSettings, previousFileNames, referencedFileNames, stagedSourcesByExtensionId);
        }
        catch (Exception ex)
        {
            ShowCaptureProtectedDialog(() => MessageBox.Show(
                this,
                $"Settings could not be saved, so no changes were applied:\n{ex.Message}",
                "PhantomHaze",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning));
            Log($"Settings save failed; extension and settings changes were rolled back: {ex.Message}");
            return;
        }

        ApplyCommittedSettings(draftSettings, stagedSourcesByExtensionId.Keys);
        Log("Settings updated and saved.");
    }

    /// <summary>
    /// Makes settings that were just written to disk (Settings saved, or a .phstg file imported) take effect: reloads
    /// the extensions and refreshes everything that depends on settings.
    /// </summary>
    /// <param name="newOrUpdatedExtensionIds">Extensions whose source changed; they announce themselves (app.log) again.</param>
    private void ApplyCommittedSettings(AppSettings newSettings, IEnumerable<string> newOrUpdatedExtensionIds)
    {
        HashSet<string> previouslyActiveExtensionIds = _loadedExtensions
            .Where(IsExtensionActive)
            .Select(extension => extension.Definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updated = new HashSet<string>(newOrUpdatedExtensionIds, StringComparer.OrdinalIgnoreCase);
        string previousFontUrl = _settings.CustomFontUrl;
        TopBarStyle previousTopBarStyle = _settings.TopBarStyle;
        bool cookiesWereSaved = _settings.PersistCookiesAcrossSessions;
        int previousCookieRetention = _settings.CookieRetentionDays;

        _settings = newSettings;
        RefreshFavoritesBar();
        RefreshInspector();
        LoadInstalledExtensions(extension =>
            !previouslyActiveExtensionIds.Contains(extension.Definition.Id) ||
            updated.Contains(extension.Definition.Id));
        RefreshExtensionSurface();
        RefreshOpenExtensionWindows();
        RefreshExtensionPages();

        if (_settings.PersistHistoryAcrossSessions)
        {
            SaveHistory();
        }
        else
        {
            TryDeleteFile(_historyFilePath);
        }

        if (!_settings.PersistCookiesAcrossSessions)
        {
            DeleteSavedCookiesFile();
        }
        else if (!cookiesWereSaved || previousCookieRetention != _settings.CookieRetentionDays)
        {
            // Starts saving this session's cookies straight away (and applies a shorter retention to saved ones).
            _ = SaveCookiesNowAsync();
        }

        // The protocol (or an extension protocol's address or settings) may have changed: open tabs follow it.
        _ = ApplyWebViewProtocolToOpenTabsAsync();

        _addressBarTextBox.PlaceholderText = NormalizeStartPage(GetEffectiveStartPage());
        RefreshHistoryPanelItems();
        UpdateTabVisibilityAndRendering();
        if (previousTopBarStyle != _settings.TopBarStyle)
        {
            ApplyTopBarStyle();
            Log($"Top bar style: {_settings.TopBarStyle}.");
        }

        ApplyTheme();
        UpdateRainbowTimer();
        if (!string.Equals(previousFontUrl, _settings.CustomFontUrl, StringComparison.Ordinal) ||
            (_appFont is null && _settings.CustomFontUrl.Length > 0))
        {
            _ = ApplyCustomFontAsync();
        }
    }

    /// <summary>
    /// Tracks a secondary top-level window (Settings, extension UI windows) so its capture exclusion always matches
    /// the main window: it is applied as soon as the window handle exists - before the window is first shown - and
    /// re-synchronised on every protection change (manual toggle, timed disable, automatic re-enable).
    /// </summary>
    private void RegisterProtectedAuxiliaryWindow(Form window, string description)
    {
        _protectedAuxiliaryWindows[window] = new AuxiliaryWindowProtection(description);
        window.HandleCreated += (_, _) => ApplyProtectionToAuxiliaryWindow(window);
        window.FormClosed += (_, _) => _protectedAuxiliaryWindows.Remove(window);
        window.Disposed += (_, _) => _protectedAuxiliaryWindows.Remove(window);

        if (window.IsHandleCreated)
        {
            ApplyProtectionToAuxiliaryWindow(window);
        }
    }

    private void SyncAuxiliaryWindowProtection()
    {
        foreach (Form window in _protectedAuxiliaryWindows.Keys.ToArray())
        {
            ApplyProtectionToAuxiliaryWindow(window);
        }

        foreach (IntPtr dialogWindow in _openSystemDialogWindows.ToArray())
        {
            _ = NativeMethods.ExcludeFromCapture(dialogWindow, _protectionEnabled);
        }

        // Windows the Internet Explorer engine opened itself (Legacy protocol), while they are still open.
        _guardedUiThreadWindows.RemoveWhere(window => !NativeMethods.IsExistingWindow(window));
        foreach (IntPtr guardedWindow in _guardedUiThreadWindows.ToArray())
        {
            _ = NativeMethods.ExcludeFromCapture(guardedWindow, _protectionEnabled);
        }
    }

    /// <summary>
    /// Shows a system dialog (MessageBox, file picker) with the same capture protection as the app's own windows.
    /// Windows creates these dialogs itself, so they are excluded from capture as they open and kept in step with
    /// later protection changes (for example the timed re-enable) while they stay open.
    /// </summary>
    private T ShowCaptureProtectedDialog<T>(Func<T> showDialog)
    {
        var dialogWindows = new List<IntPtr>();
        try
        {
            return NativeMethods.RunWithDialogActivationHook(dialogWindow =>
            {
                if (_openSystemDialogWindows.Add(dialogWindow))
                {
                    dialogWindows.Add(dialogWindow);
                }

                _ = NativeMethods.ExcludeFromCapture(dialogWindow, _protectionEnabled);
            }, showDialog);
        }
        finally
        {
            foreach (IntPtr dialogWindow in dialogWindows)
            {
                _openSystemDialogWindows.Remove(dialogWindow);
            }
        }
    }

    private void ApplyProtectionToAuxiliaryWindow(Form window)
    {
        if (window.IsDisposed ||
            !window.IsHandleCreated ||
            !_protectedAuxiliaryWindows.TryGetValue(window, out AuxiliaryWindowProtection? protection))
        {
            return;
        }

        bool enable = _protectionEnabled;
        var (ok, error) = NativeMethods.ExcludeFromCapture(window.Handle, enable);
        if (!ok)
        {
            protection.AppliedState = null;
            Log($"{protection.Description} window protection {(enable ? "could not be applied" : "could not be removed")}. " +
                $"Win32 error {error}: {NativeMethods.DescribeError(error)}.");
            return;
        }

        bool? previous = protection.AppliedState;
        protection.AppliedState = enable;
        if (enable && previous != true)
        {
            Log($"{protection.Description} window protection applied.");
        }
        else if (!enable && previous == true)
        {
            Log($"{protection.Description} window protection removed because protection is disabled.");
        }
    }

    private void Log(string message)
    {
        string entry = $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}";
        AppendLogEntryToUi(entry);

        try
        {
            File.AppendAllText(GetCurrentLogFilePath(), entry);
        }
        catch
        {
            // Keep the app functional if the logs folder is unavailable.
        }
    }

    private void AppendLogEntryToUi(string entry)
    {
        if (_logBox.IsDisposed)
        {
            return;
        }

        try
        {
            if (_logBox.InvokeRequired)
            {
                _logBox.BeginInvoke(new Action<string>(AppendLogEntryToUi), entry);
                return;
            }

            _logBox.AppendText(entry);
        }
        catch (ObjectDisposedException)
        {
            // Form is shutting down; ignore trailing async log writes.
        }
        catch (InvalidOperationException)
        {
            // Control handle is not available; skip non-critical UI log append.
        }
    }

    private static string NormalizeForLog(string? value)
        => (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");

    private static string TrimForLog(string? value, int maxLength)
    {
        string normalized = NormalizeForLog(value);
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + " ...[truncated]";
    }
}
