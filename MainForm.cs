using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

public class MainForm : Form
{
    private enum AppTheme
    {
        Light,
        Dark,
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
        public bool RenderWebViewInBackground { get; set; }
        public bool PersistHistoryAcrossSessions { get; set; }
        public int HistoryRetentionDays { get; set; } = 1;
        public SearchEngine DefaultSearchEngine { get; set; } = SearchEngine.Google;
        public string StartingPage { get; set; } = DefaultHomeUrl;
        public bool ExtensionsEnabled { get; set; } = true;
        public List<InstalledExtensionEntry> Extensions { get; set; } = new();

        public AppSettings Clone()
        {
            return new AppSettings
            {
                Theme = Theme,
                RenderWebViewInBackground = RenderWebViewInBackground,
                PersistHistoryAcrossSessions = PersistHistoryAcrossSessions,
                HistoryRetentionDays = HistoryRetentionDays,
                DefaultSearchEngine = DefaultSearchEngine,
                StartingPage = StartingPage,
                ExtensionsEnabled = ExtensionsEnabled,
                Extensions = Extensions.Select(entry => entry.Clone()).ToList(),
            };
        }
    }

    private sealed class InstalledExtensionEntry
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
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
            WebView2 webView,
            Panel headerPanel,
            PictureBox faviconBox,
            Label titleLabel,
            Button closeButton)
        {
            Id = id;
            WebView = webView;
            HeaderPanel = headerPanel;
            FaviconBox = faviconBox;
            TitleLabel = titleLabel;
            CloseButton = closeButton;
            CurrentUrl = DefaultHomeUrl;
            Title = "New Tab";
            ZoomPercent = 100;
        }

        public int Id { get; }
        public WebView2 WebView { get; }
        public Panel HeaderPanel { get; }
        public PictureBox FaviconBox { get; }
        public Label TitleLabel { get; }
        public Button CloseButton { get; }
        public string CurrentUrl { get; set; }
        public string Title { get; set; }
        public Image? OwnedFaviconImage { get; set; }
        public int ZoomPercent { get; set; }
        public bool IsNavigationPending { get; set; }
    }

    private readonly Panel _menuBarPanel = new();
    private readonly PictureBox _menuLogoPictureBox = new();
    private readonly Label _menuLogoLabel = new();
    private readonly Button _logsViewButton = new();
    private readonly FlowLayoutPanel _extensionMenuButtonsPanel = new();
    private readonly Label _statusLabel = new();
    private readonly Button _toggleButton = new();
    private readonly Button _webViewToggleButton = new();
    private readonly Button _settingsButton = new();
    private readonly ContextMenuStrip _disableProtectionMenu = new();
    private readonly System.Windows.Forms.Timer _protectionRestoreTimer = new();
    private readonly Panel _addressBarPanel = new();
    private readonly TextBox _addressBarTextBox = new();
    private readonly Button _favoriteButton = new();
    private readonly Button _backButton = new();
    private readonly Button _goButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _historyButton = new();
    private readonly Label _zoomLabel = new();
    private readonly TrackBar _zoomSlider = new();
    private readonly FlowLayoutPanel _tabsFlowPanel = new();
    private readonly Button _newTabButton = new();
    private readonly Panel _contentHostPanel = new();
    private readonly FlowLayoutPanel _extensionLauncherPanel = new();
    private readonly Panel _webViewHostPanel = new();
    private readonly Panel _webViewLoadingOverlayPanel = new();
    private readonly Label _webViewLoadingLabel = new();
    private readonly Panel _historyPanel = new();
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
    private readonly HttpClient _faviconHttpClient = new();
    private readonly Font _tabCloseFontRegular = new("Segoe UI", 8, FontStyle.Bold);
    private readonly Font _tabCloseFontCompact = new("Segoe UI", 6.5f, FontStyle.Bold);
    private readonly Font _menuButtonFontRegular = new("Segoe UI", 10, FontStyle.Regular);
    private readonly Font _menuButtonFontBold = new("Segoe UI", 10, FontStyle.Bold);
    private readonly string _logFilePath;
    private readonly string _webSessionPath;
    private readonly string _favoritesFilePath;
    private readonly string _historyFilePath;
    private readonly string _settingsFilePath;
    private readonly string _extensionsRootPath;
    private readonly List<HistoryEntry> _historyEntries = new();
    private readonly List<string> _favoriteUrls = new();
    private readonly List<BrowserTab> _tabs = new();
    private readonly List<LoadedExtension> _loadedExtensions = new();
    private readonly Dictionary<string, LoadedExtension> _loadedExtensionById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Form> _openExtensionWindowsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CoreWebView2, BrowserTab> _tabByCore = new();
    private AppSettings _settings = new();
    private TableLayoutPanel? _addressRootLayout;
    private TableLayoutPanel? _addressStripLayout;
    private TableLayoutPanel? _tabsLayout;
    private TableLayoutPanel? _historyLayout;
    private TableLayoutPanel? _findLayout;

    private CoreWebView2Environment? _webEnvironment;
    private BrowserTab? _activeTab;
    private bool _protectionEnabled;
    private bool _webViewInitialized;
    private bool _webViewModeEnabled;
    private bool _webViewModeSwitchInProgress;
    private bool _sessionDataCleared;
    private int _nextTabId = 1;
    private int _findRequestVersion;
    private int _webViewModeSwitchVersion;
    private DateTimeOffset? _protectionResumeAtUtc;

    private const string DefaultHomeUrl = "https://www.google.com";
    private const string FindShortcutMessage = "__PH_FIND__";
    private const int DefaultBackButtonWidth = 64;
    private const int DefaultGoButtonWidth = 56;
    private const int DefaultRefreshButtonWidth = 80;
    private const int DefaultHistoryButtonWidth = 80;
    private const int DefaultZoomLabelWidth = 96;
    private const int DefaultZoomSliderWidth = 185;
    private const int CompactBackButtonWidth = 56;
    private const int CompactGoButtonWidth = 48;
    private const int CompactRefreshButtonWidth = 70;
    private const int CompactHistoryButtonWidth = 70;
    private const int CompactZoomSliderWidth = 135;
    private const int MinimumAddressInputWidth = 180;
    private static readonly Image DefaultTabIcon = SystemIcons.Application.ToBitmap();
    private static readonly int[] ValidHistoryRetentionDays = new[] { 1, 7, 14, 30, 80, -1 };

    public MainForm()
    {
        Text = "PhantomHaze v1.3.4";
        Width = 980;
        Height = 620;
        MinimumSize = new Size(860, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 249, 253);
        KeyPreview = true;
        KeyDown += OnMainFormKeyDown;
        TrySetWindowIcon();

        _logFilePath = InitializeLogFilePath();
        _webSessionPath = InitializeWebSessionPath();
        _favoritesFilePath = InitializeFavoritesFilePath();
        _historyFilePath = InitializeHistoryFilePath();
        _settingsFilePath = InitializeSettingsFilePath();
        _extensionsRootPath = InitializeExtensionsDirectoryPath();
        _faviconHttpClient.Timeout = TimeSpan.FromSeconds(5);
        _protectionRestoreTimer.Interval = 1000;
        _protectionRestoreTimer.Tick += OnProtectionRestoreTimerTick;

        LoadSettings();
        NormalizeSettings();

        BuildUi();
        LoadInstalledExtensions();
        RefreshExtensionSurface();
        _addressBarTextBox.PlaceholderText = NormalizeStartPage(GetEffectiveStartPage());
        LoadFavorites();
        LoadHistory();

        Load += (_, _) =>
        {
            ApplyDpiAwareMetrics();
            Log($"Log file: {_logFilePath}");
            Log($"Web session path: {_webSessionPath}");
            Log($"Favorites file: {_favoritesFilePath}");
            Log($"History file: {_historyFilePath}");
            Log($"Settings file: {_settingsFilePath}");
            Log($"Extensions root: {_extensionsRootPath}");
            LogTrueOsBuild();
            ApplyProtection(enable: true);
        };

        FormClosing += (_, _) => CleanupWebSession();
        Resize += (_, _) =>
        {
            UpdateTabLayout();
            UpdateResponsiveLayout();
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

    private void ApplyDpiAwareMetrics()
    {
        MinimumSize = new Size(ScaleByDpi(860), ScaleByDpi(560));

        _menuBarPanel.MinimumSize = new Size(0, ScaleByDpi(52));
        _menuBarPanel.Padding = ScalePadding(12, 8, 12, 8);
        int menuLogoSize = ScaleByDpi(24);
        _menuLogoPictureBox.Size = new Size(menuLogoSize, menuLogoSize);
        _menuLogoPictureBox.Margin = ScalePadding(0, 2, 8, 2);
        _menuLogoLabel.Margin = ScalePadding(0, 0, 10, 0);
        _statusLabel.Height = ScaleByDpi(34);
        _toggleButton.Height = ScaleByDpi(36);
        _logsViewButton.MinimumSize = new Size(ScaleByDpi(90), 0);
        _webViewToggleButton.MinimumSize = new Size(ScaleByDpi(90), 0);
        _settingsButton.MinimumSize = new Size(ScaleByDpi(95), 0);
        Padding menuButtonPadding = ScalePadding(12, 4, 12, 4);
        _logsViewButton.Padding = menuButtonPadding;
        _webViewToggleButton.Padding = menuButtonPadding;
        _settingsButton.Padding = menuButtonPadding;
        _logsViewButton.Margin = ScalePadding(0, 0, 6, 0);
        _webViewToggleButton.Margin = ScalePadding(0, 0, 6, 0);
        _settingsButton.Margin = new Padding(0);

        _addressBarPanel.Height = ScaleByDpi(104);
        _addressBarPanel.Padding = ScalePadding(8, 8, 8, 6);

        if (_addressRootLayout is not null && _addressRootLayout.RowStyles.Count > 0)
        {
            _addressRootLayout.RowStyles[0].Height = ScaleByDpi(38);
        }

        if (_addressStripLayout is not null && _addressStripLayout.RowStyles.Count > 0)
        {
            _addressStripLayout.RowStyles[0].Height = ScaleByDpi(36);
        }

        if (_tabsLayout is not null)
        {
            _tabsLayout.Margin = ScalePadding(0, 4, 0, 0);
            if (_tabsLayout.ColumnStyles.Count > 1)
            {
                _tabsLayout.ColumnStyles[1].Width = ScaleByDpi(40);
            }
        }

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

        Padding stripButtonMargin = ScalePadding(3, 2, 3, 2);
        _backButton.Margin = stripButtonMargin;
        _goButton.Margin = stripButtonMargin;
        _refreshButton.Margin = stripButtonMargin;
        _historyButton.Margin = stripButtonMargin;

        _favoriteButton.Margin = ScalePadding(3, 2, 0, 2);
        _zoomSlider.Margin = ScalePadding(3, 4, 3, 2);
        _newTabButton.MinimumSize = new Size(ScaleByDpi(36), ScaleByDpi(28));
        _newTabButton.Margin = ScalePadding(2, 0, 0, 0);

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

    private static string InitializeLogFilePath()
    {
        string logDir = Path.Combine(Path.GetTempPath(), "PhantomHaze", "logs");
        Directory.CreateDirectory(logDir);
        return Path.Combine(logDir, $"phantomhaze-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    private static string InitializeWebSessionPath()
    {
        string sessionPath = Path.Combine(
            Path.GetTempPath(),
            "PhantomHaze",
            "webview-session",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(sessionPath);
        return sessionPath;
    }

    private static string InitializeFavoritesFilePath() => InitializeAppDataFilePath("favorites.json");

    private static string InitializeHistoryFilePath() => InitializeAppDataFilePath("history.json");

    private static string InitializeSettingsFilePath() => InitializeAppDataFilePath("settings.json");

    private static string InitializeExtensionsDirectoryPath()
    {
        string extensionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhantomHaze",
            "extensions");

        Directory.CreateDirectory(extensionsDir);
        return extensionsDir;
    }

    private static string InitializeAppDataFilePath(string fileName)
    {
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhantomHaze");

        Directory.CreateDirectory(appDataDir);
        return Path.Combine(appDataDir, fileName);
    }

    private void LoadSettings()
    {
        _settings = new AppSettings();

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
            _settings = new AppSettings();
        }
    }

    private void NormalizeSettings()
    {
        if (!Enum.IsDefined(_settings.Theme))
        {
            _settings.Theme = AppTheme.Light;
        }

        if (!Enum.IsDefined(_settings.DefaultSearchEngine))
        {
            _settings.DefaultSearchEngine = SearchEngine.Google;
        }

        if (!ValidHistoryRetentionDays.Contains(_settings.HistoryRetentionDays))
        {
            _settings.HistoryRetentionDays = 1;
        }

        _settings.StartingPage = NormalizeStartPage(_settings.StartingPage);

        _settings.Extensions ??= new List<InstalledExtensionEntry>();

        var normalizedExtensions = new List<InstalledExtensionEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (InstalledExtensionEntry entry in _settings.Extensions)
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
            entry.FileName = NormalizeStoredExtensionFileName(entry.FileName, entry.Id);
            if (string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                entry.DisplayName = entry.Id;
            }

            entry.SettingValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry.SettingValues.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                entry.SettingValues = new Dictionary<string, string>(entry.SettingValues, StringComparer.OrdinalIgnoreCase);
            }

            normalizedExtensions.Add(entry);
        }

        _settings.Extensions = normalizedExtensions;
    }

    private static bool IsValidExtensionIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (char character in value)
        {
            bool valid = (character >= 'a' && character <= 'z') ||
                         (character >= 'A' && character <= 'Z') ||
                         (character >= '0' && character <= '9') ||
                         character is '.' or '_' or '-';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildExtensionIdFromFileName(string? fileName)
    {
        string candidate = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(candidate.Length);
        foreach (char character in candidate.ToLowerInvariant())
        {
            normalized.Append(character switch
            {
                >= 'a' and <= 'z' => character,
                >= '0' and <= '9' => character,
                '.' or '_' or '-' => character,
                _ => '-',
            });
        }

        return normalized.ToString().Trim('-');
    }

    private static string BuildExtensionFileName(string extensionId)
    {
        string id = extensionId.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"extension-{Guid.NewGuid():N}";
        }

        return $"{id}.phext";
    }

    private static string NormalizeStoredExtensionFileName(string fileName, string extensionId)
    {
        string candidate = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.EndsWith(".phext", StringComparison.OrdinalIgnoreCase))
        {
            return BuildExtensionFileName(extensionId);
        }

        return candidate;
    }

    private void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(
                _settings,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"Settings save failed: {ex.Message}");
        }
    }

    private void LoadInstalledExtensions()
    {
        _loadedExtensions.Clear();
        _loadedExtensionById.Clear();

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
                PhantomExtensionDefinition definition = PhextParser.Parse(source, installed.FileName);
                if (!definition.Id.Equals(installed.Id, StringComparison.OrdinalIgnoreCase))
                {
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

                foreach (string startupMessage in loaded.Definition.StartupLogMessages)
                {
                    string resolved = ResolveExtensionPlaceholders(startupMessage, loaded);
                    Log($"[{loaded.Definition.Name}] {resolved}");
                }
            }
            catch (Exception ex)
            {
                Log($"Extension '{installed.Id}' failed to load from '{installed.FileName}': {ex.Message}");
            }
        }
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
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal))
        {
            parsed = true;
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.Ordinal))
        {
            parsed = false;
            return true;
        }

        parsed = false;
        return false;
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
                if (!int.TryParse(candidate, out int number))
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

                normalized = number.ToString();
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
        installation.SettingValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (installation.SettingValues.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            installation.SettingValues = new Dictionary<string, string>(installation.SettingValues, StringComparer.OrdinalIgnoreCase);
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
    }

    private void RebuildExtensionMenuButtons()
    {
        foreach (Control control in _extensionMenuButtonsPanel.Controls.OfType<Control>().ToArray())
        {
            control.Dispose();
        }

        _extensionMenuButtonsPanel.Controls.Clear();

        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            foreach (PhantomExtensionAppButtonDefinition appButton in extension.Definition.AppButtons)
            {
                var button = new Button();
                ConfigureMenuButton(button, appButton.Label);
                button.MinimumSize = new Size(ScaleByDpi(64), 0);
                button.Click += (_, _) => ExecuteExtensionAppButton(extension, appButton);
                button.Tag = appButton;
                _extensionMenuButtonsPanel.Controls.Add(button);
            }
        }

        ApplyThemeToDynamicExtensionControls();
    }

    private void RebuildExtensionLauncherButtons()
    {
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
                Tag = extension,
            };

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
        bool darkTheme = IsDarkTheme();
        Color buttonBackColor = GetCommandButtonBackColor();
        Color buttonTextColor = darkTheme ? Color.FromArgb(225, 233, 244) : SystemColors.ControlText;
        Color buttonBorderColor = darkTheme ? Color.FromArgb(105, 116, 132) : Color.FromArgb(158, 177, 201);

        foreach (Button button in _extensionMenuButtonsPanel.Controls.OfType<Button>())
        {
            button.BackColor = buttonBackColor;
            button.ForeColor = buttonTextColor;
            button.FlatAppearance.BorderColor = buttonBorderColor;
            button.FlatAppearance.BorderSize = 1;
        }

        foreach (Button button in _extensionLauncherPanel.Controls.OfType<Button>())
        {
            button.BackColor = buttonBackColor;
            button.ForeColor = buttonTextColor;
            button.FlatAppearance.BorderColor = buttonBorderColor;
            button.FlatAppearance.BorderSize = 1;
        }
    }

    private void ExecuteExtensionAppButton(LoadedExtension extension, PhantomExtensionAppButtonDefinition appButton)
    {
        string resolvedMessage = ResolveExtensionPlaceholders(appButton.Message, extension);
        Log($"[{extension.Definition.Name}] {appButton.Label}: {resolvedMessage}");

        MessageBox.Show(
            this,
            resolvedMessage,
            extension.Definition.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private string ResolveExtensionPlaceholders(string template, LoadedExtension extension)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        string resolved = template
            .Replace("{{extension.id}}", extension.Definition.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("{{extension.name}}", extension.Definition.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{{extension.version}}", extension.Definition.Version, StringComparison.OrdinalIgnoreCase);

        foreach ((string key, string value) in extension.Installation.SettingValues)
        {
            resolved = resolved.Replace($"{{{{setting.{key}}}}}", value, StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    private void OpenExtensionUiWindow(LoadedExtension extension)
    {
        if (!_settings.ExtensionsEnabled || !extension.Installation.Enabled || !extension.Definition.Ui.HasUi)
        {
            return;
        }

        if (_openExtensionWindowsById.TryGetValue(extension.Definition.Id, out Form? existingWindow) &&
            !existingWindow.IsDisposed)
        {
            existingWindow.Activate();
            return;
        }

        var extensionWindow = new Form
        {
            Text = extension.Definition.Ui.Title,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(
                ScaleByDpi(Math.Clamp(extension.Definition.Ui.Width, 320, 2000)),
                ScaleByDpi(Math.Clamp(extension.Definition.Ui.Height, 240, 1400))),
            MinimizeBox = true,
            MaximizeBox = true,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = IsDarkTheme() ? Color.FromArgb(38, 44, 52) : Color.White,
        };

        _openExtensionWindowsById[extension.Definition.Id] = extensionWindow;
        extensionWindow.FormClosed += (_, _) =>
        {
            _openExtensionWindowsById.Remove(extension.Definition.Id);
        };

        extensionWindow.Shown += async (_, _) =>
        {
            ApplyProtectionToSettingsWindow(extensionWindow);
            await LoadExtensionWindowContentAsync(extensionWindow, extension);
        };

        extensionWindow.Show(this);
    }

    private async Task LoadExtensionWindowContentAsync(Form extensionWindow, LoadedExtension extension)
    {
        string html = BuildExtensionUiDocument(extension);
        var webView = new WebView2
        {
            Dock = DockStyle.Fill,
        };

        extensionWindow.Controls.Clear();
        extensionWindow.Controls.Add(webView);

        try
        {
            CoreWebView2Environment environment = _webEnvironment ??
                await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _webSessionPath,
                    options: new CoreWebView2EnvironmentOptions("--inprivate"));

            await webView.EnsureCoreWebView2Async(environment);
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            webView.Dispose();
            extensionWindow.Controls.Clear();

            var fallbackBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = true,
                BorderStyle = BorderStyle.None,
                BackColor = IsDarkTheme() ? Color.FromArgb(31, 36, 43) : Color.White,
                ForeColor = IsDarkTheme() ? Color.FromArgb(228, 236, 247) : Color.Black,
                Text = $"Extension UI fallback for '{extension.Definition.Name}'\n\n" +
                       "WebView2 could not be initialized for this window.\n\n" +
                       ResolveExtensionPlaceholders(extension.Definition.Ui.Html, extension),
            };

            extensionWindow.Controls.Add(fallbackBox);
            Log($"Extension UI failed for '{extension.Definition.Id}': {ex.Message}");
        }
    }

    private string BuildExtensionUiDocument(LoadedExtension extension)
    {
        string resolvedHtml = ResolveExtensionPlaceholders(extension.Definition.Ui.Html, extension);
        if (resolvedHtml.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return resolvedHtml;
        }

        string textColor = IsDarkTheme() ? "#E4ECF7" : "#111111";
        string backColor = IsDarkTheme() ? "#262C34" : "#FFFFFF";
        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{extension.Definition.Ui.Title}}</title>
  <style>
    body {
      margin: 0;
      padding: 16px;
      font-family: "Segoe UI", sans-serif;
      color: {{textColor}};
      background: {{backColor}};
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

            File.WriteAllText(_historyFilePath, json);
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
        BuildProtectionDisableMenu();

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 34;
        _statusLabel.BorderStyle = BorderStyle.FixedSingle;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.Font = new Font("Segoe UI", 10, FontStyle.Bold);

        _toggleButton.Dock = DockStyle.Top;
        _toggleButton.Height = 36;
        _toggleButton.FlatStyle = FlatStyle.Flat;
        _toggleButton.FlatAppearance.BorderColor = Color.FromArgb(158, 177, 201);
        _toggleButton.FlatAppearance.BorderSize = 1;
        _toggleButton.Text = "Enable Protection";
        _toggleButton.Click += (_, _) => OnProtectionButtonClick();

        BuildAddressBar();
        BuildHistoryPanel();
        BuildFindPanel();
        BuildWebViewLoadingOverlay();
        BuildExtensionLauncherPanel();

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font("Consolas", 9);

        _webViewHostPanel.Dock = DockStyle.Fill;
        _webViewHostPanel.Visible = false;

        _contentHostPanel.Dock = DockStyle.Fill;
        _contentHostPanel.Controls.Add(_webViewHostPanel);
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
        Controls.Add(_addressBarPanel);
        Controls.Add(_toggleButton);
        Controls.Add(_statusLabel);
        Controls.Add(_menuBarPanel);

        ApplyDpiAwareMetrics();
        ApplyTheme();
        UpdateWebViewToggleVisual();
        UpdateResponsiveLayout();
        PositionExtensionLauncherPanel();
    }

    private void BuildMenuBar()
    {
        _menuBarPanel.Dock = DockStyle.Top;
        _menuBarPanel.AutoSize = true;
        _menuBarPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _menuBarPanel.MinimumSize = new Size(0, ScaleByDpi(52));
        _menuBarPanel.Padding = ScalePadding(12, 8, 12, 8);

        var menuLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        menuLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
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
        logoLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _menuLogoPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        _menuLogoPictureBox.Size = new Size(ScaleByDpi(24), ScaleByDpi(24));
        _menuLogoPictureBox.Margin = ScalePadding(0, 3, 8, 3);
        _menuLogoPictureBox.Image = Icon?.ToBitmap() ?? DefaultTabIcon;

        _menuLogoLabel.Text = "PhantomHaze";
        _menuLogoLabel.Dock = DockStyle.Fill;
        _menuLogoLabel.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _menuLogoLabel.TextAlign = ContentAlignment.MiddleLeft;
        _menuLogoLabel.Margin = ScalePadding(0, 0, 10, 0);

        logoLayout.Controls.Add(_menuLogoPictureBox, 0, 0);
        logoLayout.Controls.Add(_menuLogoLabel, 1, 0);

        ConfigureMenuButton(_logsViewButton, "Logs");
        _logsViewButton.Click += async (_, _) => await SetWebViewModeAsync(enableWebView: false);

        ConfigureMenuButton(_webViewToggleButton, "Web View");
        _webViewToggleButton.Click += async (_, _) => await SetWebViewModeAsync(enableWebView: true);

        _extensionMenuButtonsPanel.AutoSize = true;
        _extensionMenuButtonsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _extensionMenuButtonsPanel.Dock = DockStyle.Fill;
        _extensionMenuButtonsPanel.WrapContents = false;
        _extensionMenuButtonsPanel.Margin = new Padding(0);
        _extensionMenuButtonsPanel.Padding = new Padding(0);
        _extensionMenuButtonsPanel.FlowDirection = FlowDirection.LeftToRight;

        _settingsButton.Text = "Settings";
        _settingsButton.AutoSize = true;
        _settingsButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _settingsButton.Dock = DockStyle.None;
        _settingsButton.Anchor = AnchorStyles.Left;
        _settingsButton.MinimumSize = new Size(ScaleByDpi(95), 0);
        _settingsButton.Padding = ScalePadding(12, 4, 12, 4);
        _settingsButton.Margin = new Padding(0);
        _settingsButton.FlatStyle = FlatStyle.Flat;
        _settingsButton.FlatAppearance.BorderSize = 1;
        _settingsButton.Click += (_, _) => OpenSettingsWindow();

        menuLayout.Controls.Add(logoLayout, 0, 0);
        menuLayout.Controls.Add(_logsViewButton, 1, 0);
        menuLayout.Controls.Add(_webViewToggleButton, 2, 0);
        menuLayout.Controls.Add(_extensionMenuButtonsPanel, 3, 0);
        menuLayout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 4, 0);
        menuLayout.Controls.Add(_settingsButton, 5, 0);

        _menuBarPanel.Controls.Add(menuLayout);
    }

    private void ConfigureMenuButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Dock = DockStyle.None;
        button.Anchor = AnchorStyles.Left;
        button.MinimumSize = new Size(ScaleByDpi(90), 0);
        button.Padding = ScalePadding(12, 4, 12, 4);
        button.Margin = ScalePadding(0, 0, 6, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
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
        _extensionLauncherPanel.WrapContents = false;
        _extensionLauncherPanel.FlowDirection = FlowDirection.TopDown;
        _extensionLauncherPanel.Padding = new Padding(0);
        _extensionLauncherPanel.Margin = new Padding(0);
        _extensionLauncherPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
    }

    private void PositionExtensionLauncherPanel()
    {
        if (_contentHostPanel.IsDisposed)
        {
            return;
        }

        Size preferred = _extensionLauncherPanel.PreferredSize;
        int margin = ScaleByDpi(12);
        int x = Math.Max(margin, _contentHostPanel.ClientSize.Width - preferred.Width - margin);
        int y = Math.Max(margin, _contentHostPanel.ClientSize.Height - preferred.Height - margin);
        _extensionLauncherPanel.Location = new Point(x, y);

        if (_extensionLauncherPanel.Visible)
        {
            _extensionLauncherPanel.BringToFront();
        }
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

    private void BuildAddressBar()
    {
        _addressBarPanel.Dock = DockStyle.Top;
        _addressBarPanel.Height = ScaleByDpi(104);
        _addressBarPanel.BackColor = Color.FromArgb(243, 247, 252);
        _addressBarPanel.Padding = ScalePadding(8, 8, 8, 6);
        _addressBarPanel.Visible = false;
        _addressBarPanel.SizeChanged += (_, _) =>
        {
            UpdateTabLayout();
            UpdateAddressStripLayout();
        };

        _addressRootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        TableLayoutPanel rootLayout = _addressRootLayout;
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _addressStripLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        TableLayoutPanel stripLayout = _addressStripLayout;
        stripLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));

        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DefaultBackButtonWidth));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DefaultGoButtonWidth));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DefaultRefreshButtonWidth));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DefaultHistoryButtonWidth));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DefaultZoomLabelWidth));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DefaultZoomSliderWidth));

        var addressInputLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        addressInputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addressInputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(32)));

        _addressBarTextBox.Dock = DockStyle.Fill;
        _addressBarTextBox.PlaceholderText = _settings.StartingPage;
        _addressBarTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateToAddressBarUrl();
            }
        };

        _favoriteButton.Text = "✩";
        _favoriteButton.Dock = DockStyle.Fill;
        _favoriteButton.Margin = ScalePadding(3, 2, 0, 2);
        _favoriteButton.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _favoriteButton.FlatStyle = FlatStyle.Flat;
        _favoriteButton.FlatAppearance.BorderColor = SystemColors.ControlDark;
        _favoriteButton.FlatAppearance.BorderSize = 1;
        _favoriteButton.Click += (_, _) => ToggleFavoriteForCurrentPage();
        _favoriteButton.TabStop = false;

        addressInputLayout.Controls.Add(_addressBarTextBox, 0, 0);
        addressInputLayout.Controls.Add(_favoriteButton, 1, 0);

        _backButton.Text = "Back";
        _backButton.Dock = DockStyle.Fill;
        _backButton.Enabled = false;
        ConfigureStripButton(_backButton);
        _backButton.Click += (_, _) => GoBack();

        _goButton.Text = "Go";
        _goButton.Dock = DockStyle.Fill;
        ConfigureStripButton(_goButton);
        _goButton.Click += (_, _) => NavigateToAddressBarUrl();

        _refreshButton.Text = "Refresh";
        _refreshButton.Dock = DockStyle.Fill;
        ConfigureStripButton(_refreshButton);
        _refreshButton.Click += (_, _) => RefreshWebView();

        _historyButton.Text = "History";
        _historyButton.Dock = DockStyle.Fill;
        ConfigureStripButton(_historyButton);
        _historyButton.Click += (_, _) => ToggleHistoryPanel();

        _zoomLabel.Text = "Zoom 100%";
        _zoomLabel.Dock = DockStyle.Fill;
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        _zoomLabel.Margin = ScalePadding(3, 2, 3, 2);
        _zoomLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);

        _zoomSlider.Minimum = 50;
        _zoomSlider.Maximum = 200;
        _zoomSlider.Value = 100;
        _zoomSlider.TickFrequency = 10;
        _zoomSlider.AutoSize = false;
        _zoomSlider.Dock = DockStyle.Fill;
        _zoomSlider.Margin = ScalePadding(3, 4, 3, 2);
        _zoomSlider.Scroll += (_, _) => ApplyZoom();

        stripLayout.Controls.Add(addressInputLayout, 0, 0);
        stripLayout.Controls.Add(_backButton, 1, 0);
        stripLayout.Controls.Add(_goButton, 2, 0);
        stripLayout.Controls.Add(_refreshButton, 3, 0);
        stripLayout.Controls.Add(_historyButton, 4, 0);
        stripLayout.Controls.Add(_zoomLabel, 5, 0);
        stripLayout.Controls.Add(_zoomSlider, 6, 0);

        _tabsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 0),
            Padding = new Padding(0),
        };
        TableLayoutPanel tabsLayout = _tabsLayout;
        tabsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tabsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40f));
        tabsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _tabsFlowPanel.Dock = DockStyle.Fill;
        _tabsFlowPanel.FlowDirection = FlowDirection.LeftToRight;
        _tabsFlowPanel.WrapContents = false;
        _tabsFlowPanel.AutoScroll = true;
        _tabsFlowPanel.Margin = new Padding(0);
        _tabsFlowPanel.Padding = new Padding(0);
        _tabsFlowPanel.SizeChanged += (_, _) => UpdateTabLayout();

        _newTabButton.Text = "+";
        _newTabButton.Dock = DockStyle.Fill;
        _newTabButton.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _newTabButton.TextAlign = ContentAlignment.MiddleCenter;
        _newTabButton.FlatStyle = FlatStyle.Flat;
        _newTabButton.FlatAppearance.BorderColor = SystemColors.ControlDark;
        _newTabButton.FlatAppearance.BorderSize = 1;
        _newTabButton.AutoSize = false;
        _newTabButton.MinimumSize = new Size(ScaleByDpi(36), ScaleByDpi(28));
        _newTabButton.Padding = new Padding(0);
        _newTabButton.Margin = ScalePadding(2, 0, 0, 0);
        _newTabButton.Click += async (_, _) => await CreateTabAsync(GetConfiguredStartPage(), activate: true);

        tabsLayout.Controls.Add(_tabsFlowPanel, 0, 0);
        tabsLayout.Controls.Add(_newTabButton, 1, 0);

        rootLayout.Controls.Add(stripLayout, 0, 0);
        rootLayout.Controls.Add(tabsLayout, 0, 1);

        _addressBarPanel.Controls.Add(rootLayout);
        UpdateAddressStripLayout();
        UpdateFavoriteButtonState();
    }

    private void ConfigureStripButton(Button button)
    {
        button.AutoSize = false;
        button.Margin = ScalePadding(3, 2, 3, 2);
        button.Font = new Font("Segoe UI", 9, FontStyle.Regular);
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

        if (_webViewModeEnabled == enableWebView)
        {
            UpdateWebViewToggleVisual();
            return;
        }

        _webViewModeSwitchInProgress = true;
        try
        {
            if (enableWebView)
            {
                await EnsureWebViewInitializedAsync();
                if (!_webViewInitialized)
                {
                    return;
                }

                if (_tabs.Count == 0)
                {
                    BrowserTab? firstTab = await CreateTabAsync(GetConfiguredStartPage(), activate: true);
                    if (firstTab is null)
                    {
                        return;
                    }
                }

                _webViewModeEnabled = true;
                _addressBarPanel.Visible = true;
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

            _webViewModeSwitchVersion++;
            HideWebViewLoadingOverlay();
            _webViewModeEnabled = false;
            _historyPanel.Visible = false;
            _historyButton.BackColor = GetCommandButtonBackColor();
            HideFindPanel(clearHighlights: true);

            _addressBarPanel.Visible = false;
            _webViewHostPanel.Visible = false;
            _logBox.Visible = true;
            _logBox.BringToFront();
            UpdateTabVisibilityAndRendering();
            UpdateWebViewToggleVisual();
            UpdateZoomControlsForActiveTab();
            UpdateNavigationButtons();
            UpdateFavoriteButtonState();
            Log("Web View mode disabled; log panel restored.");
        }
        finally
        {
            _webViewModeSwitchInProgress = false;
        }
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
        if (!_webViewModeEnabled || tab?.WebView.CoreWebView2 is null)
        {
            return;
        }

        Task minimumOverlayTask = Task.Delay(180);
        bool mustWaitForNavigation = tab.IsNavigationPending || IsTabShowingPlaceholder(tab);
        if (mustWaitForNavigation)
        {
            await Task.WhenAll(
                minimumOverlayTask,
                WaitForNavigationCompletionAsync(tab, TimeSpan.FromSeconds(8)));
        }
        else
        {
            int stabilizationDelay = _settings.RenderWebViewInBackground ? 75 : 220;
            await Task.WhenAll(minimumOverlayTask, Task.Delay(stabilizationDelay));
        }

        if (switchVersion != _webViewModeSwitchVersion || !_webViewModeEnabled)
        {
            return;
        }
    }

    private async Task WaitForNavigationCompletionAsync(BrowserTab tab, TimeSpan timeout)
    {
        if (tab.WebView.CoreWebView2 is null || !tab.IsNavigationPending)
        {
            return;
        }

        CoreWebView2 core = tab.WebView.CoreWebView2;
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (ReferenceEquals(sender, core))
            {
                completionSource.TrySetResult(true);
            }
        }

        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            using var timeoutToken = new CancellationTokenSource(timeout);
            using CancellationTokenRegistration registration = timeoutToken.Token.Register(() => completionSource.TrySetResult(false));
            await completionSource.Task;
        }
        finally
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private static bool IsTabShowingPlaceholder(BrowserTab tab)
    {
        string source = tab.WebView.Source?.ToString() ??
                        tab.WebView.CoreWebView2?.Source ??
                        tab.CurrentUrl;

        return string.IsNullOrWhiteSpace(source) ||
               source.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateWebViewToggleVisual()
    {
        bool darkTheme = IsDarkTheme();
        Color selectedBackColor = darkTheme ? Color.FromArgb(73, 84, 100) : Color.FromArgb(201, 214, 232);
        Color normalBackColor = darkTheme ? Color.FromArgb(52, 60, 72) : Color.FromArgb(224, 228, 235);
        Color selectedTextColor = darkTheme ? Color.White : Color.FromArgb(30, 36, 43);
        Color normalTextColor = darkTheme ? Color.FromArgb(210, 220, 232) : Color.FromArgb(72, 77, 86);

        _logsViewButton.Font = _webViewModeEnabled ? _menuButtonFontRegular : _menuButtonFontBold;
        _webViewToggleButton.Font = _webViewModeEnabled ? _menuButtonFontBold : _menuButtonFontRegular;

        _logsViewButton.BackColor = _webViewModeEnabled ? normalBackColor : selectedBackColor;
        _webViewToggleButton.BackColor = _webViewModeEnabled ? selectedBackColor : normalBackColor;

        _logsViewButton.ForeColor = _webViewModeEnabled ? normalTextColor : selectedTextColor;
        _webViewToggleButton.ForeColor = _webViewModeEnabled ? selectedTextColor : normalTextColor;
    }

    private void UpdateTabVisibilityAndRendering()
    {
        foreach (BrowserTab tab in _tabs)
        {
            bool isActiveTab = _webViewModeEnabled && ReferenceEquals(tab, _activeTab);
            tab.WebView.Visible = isActiveTab;

            if (tab.WebView.CoreWebView2 is null)
            {
                continue;
            }

            bool shouldRender = isActiveTab || _settings.RenderWebViewInBackground;
            if (shouldRender)
            {
                tab.WebView.CoreWebView2.Resume();
            }
            else
            {
                _ = tab.WebView.CoreWebView2.TrySuspendAsync();
            }
        }
    }

    private async Task EnsureWebViewInitializedAsync()
    {
        if (_webViewInitialized)
        {
            return;
        }

        try
        {
            var options = new CoreWebView2EnvironmentOptions("--inprivate");
            _webEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _webSessionPath,
                options: options);

            _webViewInitialized = true;
            UpdateNavigationButtons();
            Log("Web View environment initialized. Session data remains local to this run.");
        }
        catch (Exception ex)
        {
            Log($"Web View initialization failed: {ex.Message}");
            MessageBox.Show(
                "Web View could not be initialized. Install Microsoft Edge WebView2 Runtime on the Windows host.",
                "PhantomHaze",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task<BrowserTab?> CreateTabAsync(string? rawUrl, bool activate)
    {
        await EnsureWebViewInitializedAsync();
        if (!_webViewInitialized || _webEnvironment is null)
        {
            return null;
        }

        var webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false,
        };

        Panel tabHeader = CreateTabHeaderPanel(out PictureBox faviconBox, out Label titleLabel, out Button closeButton);
        var tab = new BrowserTab(_nextTabId++, webView, tabHeader, faviconBox, titleLabel, closeButton);

        tabHeader.Tag = tab;
        titleLabel.Tag = tab;
        faviconBox.Tag = tab;

        tabHeader.Click += OnTabHeaderClicked;
        titleLabel.Click += OnTabHeaderClicked;
        faviconBox.Click += OnTabHeaderClicked;
        closeButton.Click += async (_, _) => await CloseTabAsync(tab);

        _tabs.Add(tab);
        _tabsFlowPanel.Controls.Add(tabHeader);
        _webViewHostPanel.Controls.Add(webView);

        try
        {
            await webView.EnsureCoreWebView2Async(_webEnvironment);
            CoreWebView2 core = webView.CoreWebView2;
            ConfigureCoreSettings(core);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            AttachCoreHandlers(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildFindShortcutBridgeScript());
            _tabByCore[core] = tab;

            if (!_sessionDataCleared)
            {
                await ClearWebSiteDataAsync(core);
                _sessionDataCleared = true;
            }

            webView.ZoomFactor = tab.ZoomPercent / 100.0;
        }
        catch (Exception ex)
        {
            Log($"Web tab initialization failed: {ex.Message}");
            RemoveTabAndDispose(tab);
            MessageBox.Show(
                "A browser tab could not be created. Install Microsoft Edge WebView2 Runtime on the Windows host.",
                "PhantomHaze",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            UpdateTabLayout();
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

    private Panel CreateTabHeaderPanel(
        out PictureBox faviconBox,
        out Label titleLabel,
        out Button closeButton)
    {
        var header = new Panel
        {
            Height = ScaleByDpi(28),
            Width = ScaleByDpi(180),
            Margin = ScalePadding(0, 0, 2, 0),
            Padding = ScalePadding(4, 4, 4, 3),
            BackColor = Color.FromArgb(235, 240, 247),
            Cursor = Cursors.Hand,
        };

        faviconBox = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = ScaleByDpi(16),
            Height = ScaleByDpi(16),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Image = DefaultTabIcon,
            Margin = new Padding(0),
            Cursor = Cursors.Hand,
        };

        closeButton = new Button
        {
            Dock = DockStyle.Right,
            Width = ScaleByDpi(22),
            Text = "X",
            Font = _tabCloseFontRegular,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(0),
            Margin = new Padding(0),
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            BackColor = Color.FromArgb(243, 244, 247),
            Cursor = Cursors.Hand,
        };
        closeButton.FlatAppearance.BorderSize = 1;
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(195, 199, 207);

        titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = "New Tab",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Padding = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };

        header.Controls.Add(titleLabel);
        header.Controls.Add(closeButton);
        header.Controls.Add(faviconBox);
        return header;
    }

    private void ConfigureCoreSettings(CoreWebView2 core)
    {
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
    }

    private static async Task ClearWebSiteDataAsync(CoreWebView2 core)
    {
        core.CookieManager.DeleteAllCookies();
        await core.Profile.ClearBrowsingDataAsync();
    }

    private void AttachCoreHandlers(CoreWebView2 core)
    {
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnTabNavigationStarting;
        core.NavigationCompleted += OnTabNavigationCompleted;
        core.SourceChanged += OnTabSourceChanged;
        core.DocumentTitleChanged += OnTabDocumentTitleChanged;
        core.HistoryChanged += OnTabHistoryChanged;
        core.NewWindowRequested += OnTabNewWindowRequested;
        core.WebResourceResponseReceived += OnWebResourceResponseReceived;
        core.WebMessageReceived += OnTabWebMessageReceived;
    }

    private void DetachCoreHandlers(CoreWebView2 core)
    {
        core.WebResourceRequested -= OnWebResourceRequested;
        core.NavigationStarting -= OnTabNavigationStarting;
        core.NavigationCompleted -= OnTabNavigationCompleted;
        core.SourceChanged -= OnTabSourceChanged;
        core.DocumentTitleChanged -= OnTabDocumentTitleChanged;
        core.HistoryChanged -= OnTabHistoryChanged;
        core.NewWindowRequested -= OnTabNewWindowRequested;
        core.WebResourceResponseReceived -= OnWebResourceResponseReceived;
        core.WebMessageReceived -= OnTabWebMessageReceived;
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
        RefreshTabTitle(tab);
        UpdateTabVisibilityAndRendering();

        if (_webViewModeEnabled)
        {
            tab.WebView.BringToFront();
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
        int closingIndex = _tabs.IndexOf(tab);
        if (closingIndex < 0)
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

            if (_webViewModeEnabled)
            {
                BrowserTab? replacement = await CreateTabAsync(GetConfiguredStartPage(), activate: true);
                if (replacement is null)
                {
                    _webViewModeEnabled = false;
                    _historyPanel.Visible = false;
                    _historyButton.BackColor = GetCommandButtonBackColor();
                    HideFindPanel(clearHighlights: true);
                    _addressBarPanel.Visible = false;
                    _webViewHostPanel.Visible = false;
                    _logBox.Visible = true;
                    _logBox.BringToFront();
                    UpdateTabVisibilityAndRendering();
                    UpdateNavigationButtons();
                    UpdateFavoriteButtonState();
                    UpdateWebViewToggleVisual();
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

        if (tab.WebView.CoreWebView2 is not null)
        {
            CoreWebView2 core = tab.WebView.CoreWebView2;
            DetachCoreHandlers(core);
            _tabByCore.Remove(core);
        }

        _tabs.Remove(tab);
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
        _webViewHostPanel.Controls.Remove(tab.WebView);

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
            tab.WebView.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private void UpdateTabHeaderVisuals()
    {
        bool darkTheme = IsDarkTheme();
        Color activeTabColor = darkTheme ? Color.FromArgb(69, 81, 97) : Color.FromArgb(221, 233, 255);
        Color inactiveTabColor = darkTheme ? Color.FromArgb(53, 61, 73) : Color.FromArgb(235, 240, 247);
        Color activeCloseColor = darkTheme ? Color.FromArgb(76, 87, 103) : Color.FromArgb(228, 235, 247);
        Color inactiveCloseColor = darkTheme ? Color.FromArgb(63, 72, 85) : Color.FromArgb(243, 244, 247);
        Color activeTextColor = darkTheme ? Color.FromArgb(240, 245, 252) : Color.Black;
        Color inactiveTextColor = darkTheme ? Color.FromArgb(200, 211, 226) : Color.FromArgb(35, 35, 35);

        foreach (BrowserTab tab in _tabs)
        {
            bool active = ReferenceEquals(tab, _activeTab);
            tab.HeaderPanel.BackColor = active
                ? activeTabColor
                : inactiveTabColor;

            tab.CloseButton.BackColor = active
                ? activeCloseColor
                : inactiveCloseColor;

            tab.TitleLabel.ForeColor = active ? activeTextColor : inactiveTextColor;
        }
    }

    private void UpdateTabLayout()
    {
        if (_tabs.Count == 0 || _tabsFlowPanel.ClientSize.Width <= 0)
        {
            return;
        }

        const int tabGap = 2;
        int scaledTabGap = ScaleByDpi(tabGap);
        int compactCloseWidthMinimum = Math.Max(ScaleByDpi(14), TextRenderer.MeasureText("X", _tabCloseFontCompact).Width + ScaleByDpi(6));
        int regularCloseWidth = Math.Max(ScaleByDpi(22), TextRenderer.MeasureText("X", _tabCloseFontRegular).Width + ScaleByDpi(8));
        int minimumCompactTabWidth = compactCloseWidthMinimum + ScaleByDpi(10);
        int totalGap = (_tabs.Count - 1) * scaledTabGap;
        int available = Math.Max(1, _tabsFlowPanel.ClientSize.Width - totalGap);
        int rawWidth = Math.Max(1, available / _tabs.Count);
        bool compact = rawWidth < ScaleByDpi(130);
        int targetWidth = compact
            ? Math.Max(minimumCompactTabWidth, rawWidth)
            : Math.Clamp(rawWidth, ScaleByDpi(130), ScaleByDpi(220));

        for (int i = 0; i < _tabs.Count; i++)
        {
            BrowserTab tab = _tabs[i];
            tab.HeaderPanel.Width = targetWidth;
            tab.HeaderPanel.Height = compact ? ScaleByDpi(26) : ScaleByDpi(28);
            tab.HeaderPanel.Margin = new Padding(0, 0, i == _tabs.Count - 1 ? 0 : scaledTabGap, 0);

            if (compact)
            {
                int closeWidth = Math.Clamp(targetWidth / 2, compactCloseWidthMinimum, compactCloseWidthMinimum + 4);
                int iconWidth = Math.Clamp(targetWidth - closeWidth - ScaleByDpi(2), ScaleByDpi(8), ScaleByDpi(14));

                tab.HeaderPanel.Padding = ScalePadding(0, 3, 0, 2);
                tab.TitleLabel.Visible = false;
                tab.CloseButton.Width = closeWidth;
                tab.CloseButton.Font = _tabCloseFontCompact;
                tab.CloseButton.FlatAppearance.BorderSize = 0;
                tab.FaviconBox.Width = iconWidth;
                tab.FaviconBox.Height = iconWidth;
            }
            else
            {
                tab.HeaderPanel.Padding = ScalePadding(4, 4, 4, 3);
                tab.TitleLabel.Visible = true;
                tab.CloseButton.Width = regularCloseWidth;
                tab.CloseButton.Font = _tabCloseFontRegular;
                tab.CloseButton.FlatAppearance.BorderSize = 1;
                tab.FaviconBox.Width = ScaleByDpi(16);
                tab.FaviconBox.Height = ScaleByDpi(16);
            }
        }
    }

    private void NavigateToAddressBarUrl()
    {
        if (!_webViewInitialized || _activeTab?.WebView.CoreWebView2 is null)
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
            isSearchQuery ? $"Search requested ({_settings.DefaultSearchEngine})" : "Navigate requested");
    }

    private void NavigateTabToUrl(BrowserTab tab, string normalizedUrl, string reason)
    {
        if (tab.WebView.CoreWebView2 is null)
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

        tab.WebView.CoreWebView2.Navigate(normalizedUrl);
        Log($"{reason}: {normalizedUrl}");
    }

    private static bool TryNormalizeUrl(string? rawUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        string candidate = rawUrl.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
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

        if (hasExplicitHttpScheme || LooksLikeLocalAddress(candidate) || LooksLikeDirectAddress(candidate))
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
        LoadedExtension? extension = GetEnabledExtensions()
            .FirstOrDefault(current => !string.IsNullOrWhiteSpace(current.Definition.StartPageOverride));

        return extension?.Definition.StartPageOverride;
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
        if (!_webViewInitialized || _activeTab?.WebView.CoreWebView2 is null)
        {
            Log("Back navigation requested before Web View was initialized.");
            return;
        }

        if (!_activeTab.WebView.CoreWebView2.CanGoBack)
        {
            return;
        }

        _activeTab.WebView.CoreWebView2.GoBack();
        Log("Web back navigation requested.");
    }

    private void RefreshWebView()
    {
        if (!_webViewInitialized || _activeTab?.WebView.CoreWebView2 is null)
        {
            Log("Refresh requested before Web View was initialized.");
            return;
        }

        _activeTab.WebView.Reload();
        Log("Web page refresh requested.");
    }

    private void ToggleHistoryPanel()
    {
        if (!_webViewInitialized || _activeTab?.WebView.CoreWebView2 is null)
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
        if (!_webViewInitialized || _activeTab?.WebView.CoreWebView2 is null)
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
        if (!_webViewInitialized || _activeTab?.WebView.CoreWebView2 is null)
        {
            return;
        }

        double zoom = _zoomSlider.Value / 100.0;
        _activeTab.ZoomPercent = _zoomSlider.Value;
        _activeTab.WebView.ZoomFactor = zoom;

        Log($"Tab {_activeTab.Id} zoom set to {_zoomSlider.Value}%.");
    }

    private void UpdateNavigationButtons()
    {
        _backButton.Enabled = _webViewModeEnabled &&
                              _activeTab?.WebView.CoreWebView2 is not null &&
                              _activeTab.WebView.CoreWebView2.CanGoBack;
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
        _favoritesListBox.Items.Clear();
        if (_favoriteUrls.Count == 0)
        {
            _favoritesListBox.Items.Add("(No favourites saved)");
            return;
        }

        for (int i = _favoriteUrls.Count - 1; i >= 0; i--)
        {
            _favoritesListBox.Items.Add(_favoriteUrls[i]);
        }
    }

    private void OnTabNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        tab.CurrentUrl = e.Uri;
        tab.IsNavigationPending = true;
        if (_webViewModeEnabled && ReferenceEquals(_activeTab, tab))
        {
            _addressBarTextBox.Text = e.Uri;
            UpdateFavoriteButtonState();
        }

        Log($"Tab {tab.Id} navigation starting: {e.Uri}");
    }

    private async void OnTabNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        tab.IsNavigationPending = false;

        try
        {
            if (e.IsSuccess)
            {
                string currentUrl = tab.WebView.Source?.ToString() ?? tab.WebView.CoreWebView2?.Source ?? tab.CurrentUrl;
                if (!string.IsNullOrWhiteSpace(currentUrl))
                {
                    tab.CurrentUrl = currentUrl;
                    if (!currentUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        AddHistoryEntry(currentUrl);
                    }
                }

                RefreshTabTitle(tab);
                await ApplyExtensionWebContentAsync(tab, currentUrl);
                await RefreshTabFaviconAsync(tab);
                Log($"Tab {tab.Id} navigation completed: {currentUrl}");
                await LogCookieJarSnapshotAsync(currentUrl, tab);
            }
            else
            {
                Log($"Tab {tab.Id} navigation failed: {e.WebErrorStatus}");
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

    private void OnTabSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        string source = tab.WebView.Source?.ToString() ?? tab.WebView.CoreWebView2?.Source ?? string.Empty;
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

    private void OnTabDocumentTitleChanged(object? sender, object e)
    {
        if (!TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        RefreshTabTitle(tab);
        Log($"Tab {tab.Id} title: {tab.Title}");
    }

    private void OnTabHistoryChanged(object? sender, object e)
    {
        if (!TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        if (tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        if (!ReferenceEquals(_activeTab, tab))
        {
            return;
        }

        Log($"Web history state changed (Back: {tab.WebView.CoreWebView2.CanGoBack}, Forward: {tab.WebView.CoreWebView2.CanGoForward}).");
        UpdateNavigationButtons();
    }

    private void OnTabNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        string popupUrl = NormalizeOrDefaultUrl(e.Uri);

        try
        {
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

    private void OnTabWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (!message.Equals(FindShortcutMessage, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(ShowFindPanel));
        }
        catch
        {
            // Best effort only.
        }
    }

    private void RefreshTabTitle(BrowserTab tab)
    {
        string title = tab.WebView.CoreWebView2?.DocumentTitle ?? string.Empty;
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
        if (!_tabs.Contains(tab) || tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        string? faviconUrl = await GetDeclaredFaviconUrlAsync(tab.WebView.CoreWebView2);
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

    private static async Task<string?> GetDeclaredFaviconUrlAsync(CoreWebView2 core)
    {
        const string script = "(() => {" +
            "const links = document.querySelectorAll('link[rel]');" +
            "for (const link of links) {" +
            "const rel = (link.rel || '').toLowerCase();" +
            "if (!rel.includes('icon')) { continue; }" +
            "const href = link.getAttribute('href');" +
            "if (!href) { continue; }" +
            "try { return new URL(href, document.baseURI).href; } catch (_) { }" +
            "}" +
            "return '';" +
            "})();";

        try
        {
            string raw = await core.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch
        {
            return null;
        }
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
            using HttpResponseMessage response = await _faviconHttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

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

    private static string BuildFindHighlightScript(string query)
    {
        string encodedQuery = JsonSerializer.Serialize(query);
        return $$"""
(() => {
  const markerAttr = "data-ph-find";
  const existing = Array.from(document.querySelectorAll("mark[" + markerAttr + "]"));
  for (const mark of existing) {
    const text = document.createTextNode(mark.textContent || "");
    mark.replaceWith(text);
    if (text.parentNode) {
      text.parentNode.normalize();
    }
  }

  if (!document.body) {
    return 0;
  }

  const query = {{encodedQuery}};
  const needle = (query || "").trim().toLowerCase();
  if (!needle) {
    return 0;
  }

  const blockedTags = new Set(["SCRIPT", "STYLE", "NOSCRIPT", "TEXTAREA", "INPUT"]);
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      if (!node || !node.nodeValue || !node.nodeValue.trim()) {
        return NodeFilter.FILTER_REJECT;
      }

      const parent = node.parentElement;
      if (!parent || blockedTags.has(parent.tagName)) {
        return NodeFilter.FILTER_REJECT;
      }

      if (parent.closest("mark[" + markerAttr + "]")) {
        return NodeFilter.FILTER_REJECT;
      }

      return NodeFilter.FILTER_ACCEPT;
    }
  });

  const nodes = [];
  while (walker.nextNode()) {
    nodes.push(walker.currentNode);
  }

  let count = 0;
  for (const node of nodes) {
    const original = node.nodeValue || "";
    const lower = original.toLowerCase();
    let cursor = 0;
    let index = lower.indexOf(needle, cursor);

    if (index < 0) {
      continue;
    }

    const fragment = document.createDocumentFragment();
    while (index >= 0) {
      if (index > cursor) {
        fragment.appendChild(document.createTextNode(original.slice(cursor, index)));
      }

      const mark = document.createElement("mark");
      mark.setAttribute(markerAttr, "1");
      mark.style.background = "#ffe07d";
      mark.style.color = "#111111";
      mark.textContent = original.slice(index, index + needle.length);
      fragment.appendChild(mark);

      cursor = index + needle.length;
      count += 1;
      index = lower.indexOf(needle, cursor);
    }

    if (cursor < original.length) {
      fragment.appendChild(document.createTextNode(original.slice(cursor)));
    }

    node.replaceWith(fragment);
  }

  return count;
})();
""";
    }

    private static string BuildFindShortcutBridgeScript()
    {
        return "window.addEventListener('keydown', function(e) {" +
            "if (!e.ctrlKey || e.altKey || e.metaKey) { return; }" +
            "if (e.key !== 'f' && e.key !== 'F') { return; }" +
            "e.preventDefault();" +
            "if (window.chrome && window.chrome.webview) {" +
            "window.chrome.webview.postMessage('" + FindShortcutMessage + "');" +
            "}" +
            "}, true);";
    }

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

    private async Task ApplyExtensionWebContentAsync(BrowserTab tab, string currentUrl)
    {
        if (!_settings.ExtensionsEnabled || tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        await ApplyExtensionScriptsByTimingAsync(tab, currentUrl, PhantomExtensionScriptTiming.DocumentStart);
        await ApplyExtensionScriptsByTimingAsync(tab, currentUrl, PhantomExtensionScriptTiming.DocumentEnd);
    }

    private async Task ApplyExtensionScriptsByTimingAsync(
        BrowserTab tab,
        string currentUrl,
        PhantomExtensionScriptTiming timing)
    {
        if (tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            foreach (PhantomExtensionWebScriptDefinition script in extension.Definition.WebScripts)
            {
                if (script.Timing != timing)
                {
                    continue;
                }

                if (!PhantomExtensionPatternMatcher.IsMatch(currentUrl, script.MatchPattern))
                {
                    continue;
                }

                try
                {
                    await EnsureExtensionContextObjectAsync(tab, extension);
                    await ExecuteExtensionScriptAsync(tab, extension, script);
                }
                catch (Exception ex)
                {
                    Log($"Extension script failed [{extension.Definition.Id}/{script.Id}]: {ex.Message}");
                }
            }
        }
    }

    private async Task EnsureExtensionContextObjectAsync(BrowserTab tab, LoadedExtension extension)
    {
        if (tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        string idJson = JsonSerializer.Serialize(extension.Definition.Id);
        string nameJson = JsonSerializer.Serialize(extension.Definition.Name);
        string versionJson = JsonSerializer.Serialize(extension.Definition.Version);
        string settingsJson = JsonSerializer.Serialize(extension.Installation.SettingValues);

        string bootstrapScript = $$"""
(() => {
  window.phantomHazeExtensions = window.phantomHazeExtensions || {};
  window.phantomHazeExtensions[{{idJson}}] = {
    id: {{idJson}},
    name: {{nameJson}},
    version: {{versionJson}},
    settings: {{settingsJson}}
  };
})();
""";

        await tab.WebView.CoreWebView2.ExecuteScriptAsync(bootstrapScript);
    }

    private async Task ExecuteExtensionScriptAsync(
        BrowserTab tab,
        LoadedExtension extension,
        PhantomExtensionWebScriptDefinition script)
    {
        if (tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        if (script.IsCss)
        {
            string css = ResolveExtensionPlaceholders(script.Content, extension);
            string styleScript = BuildCssInjectionScript(extension.Definition.Id, script.Id, css);
            await tab.WebView.CoreWebView2.ExecuteScriptAsync(styleScript);
            return;
        }

        string jsPayload = ResolveExtensionPlaceholders(script.Content, extension);
        string wrappedScript =
            "(function(){try{" +
            jsPayload +
            "}catch(error){console.error('PhantomHaze extension script failure', error);}})();";

        await tab.WebView.CoreWebView2.ExecuteScriptAsync(wrappedScript);
    }

    private static string BuildCssInjectionScript(string extensionId, string scriptId, string css)
    {
        string domId = $"ph-ext-style-{SanitizeDomToken(extensionId)}-{SanitizeDomToken(scriptId)}";
        string domIdJson = JsonSerializer.Serialize(domId);
        string cssJson = JsonSerializer.Serialize(css);
        return $$"""
(() => {
  if (!document.head) {
    return;
  }

  const styleId = {{domIdJson}};
  let styleElement = document.getElementById(styleId);
  if (!styleElement) {
    styleElement = document.createElement('style');
    styleElement.id = styleId;
    document.head.appendChild(styleElement);
  }

  styleElement.textContent = {{cssJson}};
})();
""";
    }

    private static string SanitizeDomToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "extension";
        }

        var builder = new StringBuilder(value.Length);
        foreach (char character in value.ToLowerInvariant())
        {
            bool valid = (character >= 'a' && character <= 'z') ||
                         (character >= '0' && character <= '9') ||
                         character is '-' or '_';
            builder.Append(valid ? character : '-');
        }

        return builder.ToString();
    }

    private async Task<int> ApplyFindScriptAsync(BrowserTab tab, string query)
    {
        if (!_tabs.Contains(tab) || tab.WebView.CoreWebView2 is null)
        {
            return 0;
        }

        try
        {
            string script = BuildFindHighlightScript(query);
            string rawResult = await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            return ParseScriptIntResult(rawResult);
        }
        catch
        {
            return 0;
        }
    }

    private static int ParseScriptIntResult(string rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
        {
            return 0;
        }

        string candidate = rawResult.Trim().Trim('"');
        return int.TryParse(candidate, out int count) ? count : 0;
    }

    private void ShowFindPanel()
    {
        if (!_webViewModeEnabled || _activeTab?.WebView.CoreWebView2 is null)
        {
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

        if (_activeTab?.WebView.CoreWebView2 is null)
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

        int existingIndex = _favoriteUrls.FindIndex(url =>
            url.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            _favoriteUrls.RemoveAt(existingIndex);
            Log($"Favourite removed: {normalizedUrl}");
        }
        else
        {
            _favoriteUrls.Add(normalizedUrl);
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

        string source = _activeTab.WebView.Source?.ToString() ??
                        _activeTab.WebView.CoreWebView2?.Source ??
                        _activeTab.CurrentUrl;

        if (string.IsNullOrWhiteSpace(source))
        {
            source = _addressBarTextBox.Text.Trim();
        }

        return string.IsNullOrWhiteSpace(source) ? null : source;
    }

    private void UpdateFavoriteButtonState()
    {
        bool darkTheme = IsDarkTheme();
        Color idleBackColor = darkTheme ? Color.FromArgb(67, 76, 90) : SystemColors.Control;
        Color idleForeColor = darkTheme ? Color.FromArgb(225, 233, 244) : SystemColors.ControlText;

        bool enabled = _webViewModeEnabled && _activeTab is not null;
        _favoriteButton.Enabled = enabled;
        _favoriteButton.Text = "✩";
        if (!enabled)
        {
            _favoriteButton.BackColor = idleBackColor;
            _favoriteButton.ForeColor = idleForeColor;
            return;
        }

        string? currentUrl = GetActiveTabUrl();
        bool isFavorite = TryNormalizeUrl(currentUrl, out string normalizedCurrentUrl) &&
            _favoriteUrls.Any(url => url.Equals(normalizedCurrentUrl, StringComparison.OrdinalIgnoreCase));

        _favoriteButton.BackColor = isFavorite ? Color.Gold : idleBackColor;
        _favoriteButton.ForeColor = isFavorite ? Color.Black : idleForeColor;
        _favoriteButton.Text = isFavorite ? "★" : "✩";
    }

    private void LoadFavorites()
    {
        _favoriteUrls.Clear();

        try
        {
            if (!File.Exists(_favoritesFilePath))
            {
                RefreshFavoritesPanelItems();
                return;
            }

            string rawJson = File.ReadAllText(_favoritesFilePath);
            List<string>? loaded = JsonSerializer.Deserialize<List<string>>(rawJson);
            if (loaded is null)
            {
                RefreshFavoritesPanelItems();
                return;
            }

            foreach (string item in loaded)
            {
                if (!TryNormalizeUrl(item, out string normalized))
                {
                    continue;
                }

                bool exists = _favoriteUrls.Any(url =>
                    url.Equals(normalized, StringComparison.OrdinalIgnoreCase));

                if (!exists)
                {
                    _favoriteUrls.Add(normalized);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Favourites load failed: {ex.Message}");
        }

        RefreshFavoritesPanelItems();
        UpdateFavoriteButtonState();
    }

    private void SaveFavorites()
    {
        try
        {
            string json = JsonSerializer.Serialize(
                _favoriteUrls,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(_favoritesFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"Favourites save failed: {ex.Message}");
        }
    }

    private void SetTabFavicon(BrowserTab tab, Image image, bool ownsImage)
    {
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
                byte[] payload = Encoding.UTF8.GetBytes("Blocked by PhantomHaze extension.");
                var stream = new MemoryStream(payload);
                e.Response = core.Environment.CreateWebResourceResponse(
                    stream,
                    403,
                    "Forbidden",
                    "Content-Type: text/plain; charset=utf-8");
            }

            Log($"Tab {tab.Id} blocked by extension {blockingExtension!.Definition.Name} ({blockingRule!.Id}): {e.Request.Uri}");
            return;
        }

        if (!_webViewModeEnabled)
        {
            return;
        }

        if (e.ResourceContext != CoreWebView2WebResourceContext.Document)
        {
            return;
        }

        if (!e.Request.Headers.Contains("Cookie"))
        {
            return;
        }

        try
        {
            string cookieHeader = e.Request.Headers.GetHeader("Cookie");
            string safeUri = TrimForLog(e.Request.Uri, 220);
            string safeHeader = TrimForLog(NormalizeForLog(cookieHeader), 1200);
            Log($"Tab {tab.Id} cookie request header to {safeUri}: {safeHeader}");
        }
        catch (Exception ex)
        {
            Log($"Cookie request header logging failed for {e.Request.Uri}: {ex.Message}");
        }
    }

    private async void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!_webViewModeEnabled || !TryGetTabFromSender(sender, out BrowserTab tab))
        {
            return;
        }

        try
        {
            CoreWebView2WebResourceResponseView? response = e.Response;
            if (response is null)
            {
                return;
            }

            bool sawSetCookie = false;
            foreach ((string key, string value) in response.Headers)
            {
                if (!key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sawSetCookie = true;
                Log($"Tab {tab.Id} Set-Cookie from {e.Request.Uri}: {NormalizeForLog(value)}");
            }

            if (sawSetCookie)
            {
                await LogCookieJarSnapshotAsync(e.Request.Uri, tab);
            }
        }
        catch (Exception ex)
        {
            Log($"Set-Cookie logging failed for {e.Request.Uri}: {ex.Message}");
        }
    }

    private async Task LogCookieJarSnapshotAsync(string source, BrowserTab tab)
    {
        if (tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await tab.WebView.CoreWebView2.CookieManager.GetCookiesAsync(null);
            if (cookies.Count == 0)
            {
                Log($"Cookie jar after {source}: (empty)");
                return;
            }

            Log($"Cookie jar after {source}: {cookies.Count} cookie(s).");
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                string expires = cookie.IsSession
                    ? "session"
                    : cookie.Expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                Log($"Cookie: {NormalizeForLog(cookie.Name)}={NormalizeForLog(cookie.Value)}; domain={NormalizeForLog(cookie.Domain)}; path={NormalizeForLog(cookie.Path)}; secure={cookie.IsSecure}; httpOnly={cookie.IsHttpOnly}; sameSite={cookie.SameSite}; expires={expires}");
            }
        }
        catch (Exception ex)
        {
            Log($"Cookie jar snapshot failed after {source}: {ex.Message}");
        }
    }

    private void CleanupWebSession()
    {
        SaveSettings();
        SaveHistory();
        SaveFavorites();

        _historyEntries.Clear();
        _historyPanel.Visible = false;
        _findPanel.Visible = false;
        _protectionRestoreTimer.Stop();

        foreach (BrowserTab tab in _tabs.ToArray())
        {
            RemoveTabAndDispose(tab);
        }

        _tabByCore.Clear();
        _activeTab = null;

        foreach (Form extensionWindow in _openExtensionWindowsById.Values.ToArray())
        {
            try
            {
                extensionWindow.Close();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        _openExtensionWindowsById.Clear();

        try
        {
            _faviconHttpClient.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }

        try
        {
            _tabCloseFontRegular.Dispose();
            _tabCloseFontCompact.Dispose();
            _menuButtonFontRegular.Dispose();
            _menuButtonFontBold.Dispose();
        }
        catch
        {
            // Best effort cleanup only.
        }

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

        UpdateProtectionStatusVisual();
        _toggleButton.Text = _protectionEnabled
            ? "Disable Protection ▼"
            : "Enable Protection";
    }

    private void OnProtectionButtonClick()
    {
        if (_protectionEnabled)
        {
            _disableProtectionMenu.Show(_toggleButton, new Point(0, _toggleButton.Height));
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

    private void UpdateProtectionStatusVisual()
    {
        if (_protectionEnabled)
        {
            _statusLabel.Text = "Protection Enabled - This window is excluded from supported capture software.";
        }
        else if (_protectionResumeAtUtc is not null)
        {
            TimeSpan remaining = _protectionResumeAtUtc.Value - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            _statusLabel.Text = $"Protection Disabled - This window is visible to capture software. Auto re-enables in {FormatProtectionCountdown(remaining)}.";
        }
        else
        {
            _statusLabel.Text = "Protection Disabled - This window is visible to capture software.";
        }

        if (IsDarkTheme())
        {
            _statusLabel.BackColor = _protectionEnabled
                ? Color.FromArgb(58, 123, 85)
                : Color.FromArgb(133, 76, 73);
            _statusLabel.ForeColor = Color.White;
            return;
        }

        _statusLabel.BackColor = _protectionEnabled ? Color.LightGreen : Color.LightSalmon;
        _statusLabel.ForeColor = Color.Black;
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
            return $"{totalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }

        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void UpdateZoomControlsForActiveTab()
    {
        bool enabled = _webViewModeEnabled && _activeTab is not null;
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

    private void UpdateAddressStripLayout()
    {
        if (_addressStripLayout is null || _addressStripLayout.ColumnStyles.Count < 7)
        {
            return;
        }

        int availableWidth = Math.Max(1, _addressBarPanel.ClientSize.Width - _addressBarPanel.Padding.Horizontal);
        int compactThreshold = ScaleByDpi(MinimumAddressInputWidth) +
                               ScaleByDpi(CompactBackButtonWidth) +
                               ScaleByDpi(CompactGoButtonWidth) +
                               ScaleByDpi(CompactRefreshButtonWidth) +
                               ScaleByDpi(CompactHistoryButtonWidth) +
                               ScaleByDpi(DefaultZoomLabelWidth) +
                               ScaleByDpi(CompactZoomSliderWidth);

        bool compact = availableWidth < compactThreshold;

        _addressStripLayout.ColumnStyles[1].Width = ScaleByDpi(compact ? CompactBackButtonWidth : DefaultBackButtonWidth);
        _addressStripLayout.ColumnStyles[2].Width = ScaleByDpi(compact ? CompactGoButtonWidth : DefaultGoButtonWidth);
        _addressStripLayout.ColumnStyles[3].Width = ScaleByDpi(compact ? CompactRefreshButtonWidth : DefaultRefreshButtonWidth);
        _addressStripLayout.ColumnStyles[4].Width = ScaleByDpi(compact ? CompactHistoryButtonWidth : DefaultHistoryButtonWidth);
        _addressStripLayout.ColumnStyles[5].Width = ScaleByDpi(DefaultZoomLabelWidth);
        _addressStripLayout.ColumnStyles[6].Width = ScaleByDpi(compact ? CompactZoomSliderWidth : DefaultZoomSliderWidth);

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

    private bool IsDarkTheme() => _settings.Theme == AppTheme.Dark;

    private Color GetCommandButtonBackColor()
    {
        return IsDarkTheme()
            ? Color.FromArgb(67, 76, 90)
            : SystemColors.Control;
    }

    private Color GetHistoryButtonActiveBackColor()
    {
        return IsDarkTheme()
            ? Color.FromArgb(86, 96, 111)
            : Color.LightSteelBlue;
    }

    private void ApplyTheme()
    {
        bool darkTheme = IsDarkTheme();
        Color appBackColor = darkTheme ? Color.FromArgb(31, 36, 43) : Color.FromArgb(246, 249, 253);
        Color panelBackColor = darkTheme ? Color.FromArgb(40, 45, 53) : Color.FromArgb(243, 247, 252);
        Color menuBackColor = darkTheme ? Color.FromArgb(52, 60, 72) : Color.FromArgb(224, 228, 235);
        Color textColor = darkTheme ? Color.FromArgb(224, 233, 245) : Color.FromArgb(25, 25, 25);
        Color inputBackColor = darkTheme ? Color.FromArgb(54, 62, 74) : Color.White;
        Color inputTextColor = darkTheme ? Color.FromArgb(234, 241, 252) : Color.Black;
        Color buttonBackColor = GetCommandButtonBackColor();
        Color buttonTextColor = darkTheme ? Color.FromArgb(225, 233, 244) : SystemColors.ControlText;
        Color buttonBorderColor = darkTheme ? Color.FromArgb(105, 116, 132) : Color.FromArgb(158, 177, 201);

        BackColor = appBackColor;
        _menuBarPanel.BackColor = menuBackColor;
        _menuLogoLabel.ForeColor = textColor;
        _addressBarPanel.BackColor = panelBackColor;
        _contentHostPanel.BackColor = appBackColor;
        _extensionLauncherPanel.BackColor = panelBackColor;
        _webViewHostPanel.BackColor = appBackColor;
        _webViewLoadingOverlayPanel.BackColor = darkTheme
            ? Color.FromArgb(31, 36, 43)
            : Color.FromArgb(246, 249, 253);
        _webViewLoadingLabel.ForeColor = textColor;
        _historyPanel.BackColor = panelBackColor;
        _findPanel.BackColor = panelBackColor;

        _addressBarTextBox.BackColor = inputBackColor;
        _addressBarTextBox.ForeColor = inputTextColor;
        _historyListBox.BackColor = inputBackColor;
        _historyListBox.ForeColor = inputTextColor;
        _favoritesListBox.BackColor = inputBackColor;
        _favoritesListBox.ForeColor = inputTextColor;
        _findTextBox.BackColor = inputBackColor;
        _findTextBox.ForeColor = inputTextColor;
        _logBox.BackColor = darkTheme ? Color.FromArgb(23, 27, 33) : Color.White;
        _logBox.ForeColor = darkTheme ? Color.FromArgb(210, 225, 240) : Color.Black;

        _historyTitleLabel.ForeColor = textColor;
        _favoritesTitleLabel.ForeColor = textColor;
        _zoomLabel.ForeColor = textColor;
        _findCountLabel.ForeColor = textColor;

        foreach (Button button in new[] { _toggleButton, _backButton, _goButton, _refreshButton, _historyButton, _newTabButton, _historyClearButton, _findCloseButton, _settingsButton })
        {
            button.BackColor = buttonBackColor;
            button.ForeColor = buttonTextColor;
            button.FlatAppearance.BorderColor = buttonBorderColor;
        }

        _historyButton.BackColor = _historyPanel.Visible
            ? GetHistoryButtonActiveBackColor()
            : buttonBackColor;

        _favoriteButton.FlatAppearance.BorderColor = buttonBorderColor;
        _webViewToggleButton.FlatAppearance.BorderColor = buttonBorderColor;
        _logsViewButton.FlatAppearance.BorderColor = buttonBorderColor;

        UpdateProtectionStatusVisual();
        UpdateWebViewToggleVisual();
        UpdateFavoriteButtonState();
        UpdateTabHeaderVisuals();
        ApplyThemeToDynamicExtensionControls();
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

                PhantomExtensionDefinition definition = PhextParser.Parse(source, installed.FileName);
                if (!definition.Id.Equals(installed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    installed.FileName = BuildExtensionFileName(definition.Id);
                }

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

    private void PersistExtensionDraftChanges(
        IReadOnlyList<InstalledExtensionEntry> previousExtensions,
        IReadOnlyList<InstalledExtensionEntry> draftExtensions,
        IReadOnlyDictionary<string, string> stagedSourcesById)
    {
        var previousFileNames = previousExtensions
            .Select(entry => NormalizeStoredExtensionFileName(entry.FileName, entry.Id))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var retainedFileNames = draftExtensions
            .Select(entry => NormalizeStoredExtensionFileName(entry.FileName, entry.Id))
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string removedFileName in previousFileNames.Except(retainedFileNames, StringComparer.OrdinalIgnoreCase))
        {
            string removedPath = Path.Combine(_extensionsRootPath, removedFileName);
            TryDeleteFile(removedPath);
        }

        foreach (InstalledExtensionEntry entry in draftExtensions)
        {
            if (!stagedSourcesById.TryGetValue(entry.Id, out string? source))
            {
                continue;
            }

            string fileName = NormalizeStoredExtensionFileName(entry.FileName, entry.Id);
            string targetPath = Path.Combine(_extensionsRootPath, fileName);
            File.WriteAllText(targetPath, source);
        }
    }

    private static void ApplyColorsRecursively(Control control, Color backColor, Color foreColor)
    {
        control.BackColor = backColor;
        control.ForeColor = foreColor;

        foreach (Control child in control.Controls)
        {
            ApplyColorsRecursively(child, backColor, foreColor);
        }
    }

    private void OpenSettingsWindow()
    {
        AppSettings draftSettings = _settings.Clone();
        List<InstalledExtensionEntry> previousExtensions = _settings.Extensions
            .Select(entry => entry.Clone())
            .ToList();
        var stagedSourcesByExtensionId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool darkTheme = IsDarkTheme();

        static Label BuildSettingsLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
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
            ClientSize = new Size(ScaleByDpi(760), ScaleByDpi(580)),
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

        var generalTab = new TabPage("General");
        var extensionsTab = new TabPage("Extensions");
        settingsTabs.TabPages.Add(generalTab);
        settingsTabs.TabPages.Add(extensionsTab);

        var generalLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(250)));
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleByDpi(36)));
        generalLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var themeComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        themeComboBox.Items.Add(new OptionItem<AppTheme>(AppTheme.Light, "Light"));
        themeComboBox.Items.Add(new OptionItem<AppTheme>(AppTheme.Dark, "Dark"));
        themeComboBox.SelectedIndex = draftSettings.Theme == AppTheme.Dark ? 1 : 0;

        var renderInBackgroundCheckBox = new CheckBox
        {
            Text = "Render web pages in the background",
            Dock = DockStyle.Fill,
            Checked = draftSettings.RenderWebViewInBackground,
            TextAlign = ContentAlignment.MiddleLeft,
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
        OptionItem<int>[] retentionOptions =
        [
            new OptionItem<int>(1, "1 day"),
            new OptionItem<int>(7, "7 days"),
            new OptionItem<int>(14, "14 days"),
            new OptionItem<int>(30, "30 days"),
            new OptionItem<int>(80, "80 days"),
            new OptionItem<int>(-1, "Forever"),
        ];

        foreach (OptionItem<int> option in retentionOptions)
        {
            retentionComboBox.Items.Add(option);
        }

        OptionItem<int>? selectedRetentionOption = retentionOptions.FirstOrDefault(option => option.Value == draftSettings.HistoryRetentionDays) ?? retentionOptions[0];
        retentionComboBox.SelectedItem = selectedRetentionOption;

        persistHistoryCheckBox.CheckedChanged += (_, _) =>
        {
            retentionComboBox.Enabled = persistHistoryCheckBox.Checked;
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

        generalLayout.Controls.Add(BuildSettingsLabel("Theme"), 0, 0);
        generalLayout.Controls.Add(themeComboBox, 1, 0);

        generalLayout.Controls.Add(renderInBackgroundCheckBox, 0, 1);
        generalLayout.SetColumnSpan(renderInBackgroundCheckBox, 2);

        generalLayout.Controls.Add(persistHistoryCheckBox, 0, 2);
        generalLayout.SetColumnSpan(persistHistoryCheckBox, 2);

        generalLayout.Controls.Add(BuildSettingsLabel("Saved history retention"), 0, 3);
        generalLayout.Controls.Add(retentionComboBox, 1, 3);

        generalLayout.Controls.Add(BuildSettingsLabel("Default search engine"), 0, 4);
        generalLayout.Controls.Add(searchEngineComboBox, 1, 4);

        generalLayout.Controls.Add(BuildSettingsLabel("Web View starting page"), 0, 5);
        generalLayout.Controls.Add(startPageTextBox, 1, 5);

        generalTab.Controls.Add(generalLayout);

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
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
        };

        var extensionSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = ScaleByDpi(300),
            Panel1MinSize = ScaleByDpi(220),
            Panel2MinSize = ScaleByDpi(280),
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
        bool listPopulationInProgress = false;

        void ApplySettingsInputColors(Control control)
        {
            Color inputBackColor = Color.FromArgb(55, 64, 77);
            Color inputForeColor = Color.FromArgb(228, 236, 247);

            switch (control)
            {
                case TextBoxBase textBox:
                    textBox.BackColor = inputBackColor;
                    textBox.ForeColor = inputForeColor;
                    break;
                case ComboBox comboBox:
                    comboBox.BackColor = inputBackColor;
                    comboBox.ForeColor = inputForeColor;
                    break;
                case CheckedListBox checkedListBox:
                    checkedListBox.BackColor = inputBackColor;
                    checkedListBox.ForeColor = inputForeColor;
                    break;
                case ListBox listBox:
                    listBox.BackColor = inputBackColor;
                    listBox.ForeColor = inputForeColor;
                    break;
                case NumericUpDown numericUpDown:
                    numericUpDown.BackColor = inputBackColor;
                    numericUpDown.ForeColor = inputForeColor;
                    break;
            }

            foreach (Control child in control.Controls)
            {
                ApplySettingsInputColors(child);
            }
        }

        void RenderSelectedExtensionSettings()
        {
            extensionSettingsPanel.Controls.Clear();

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
                extensionSettingsPanel.Controls.Add(new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Text = $"This extension failed to load:\n{selectedItem.LoadError}",
                    ForeColor = Color.FromArgb(197, 70, 70),
                });
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
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Margin = ScalePadding(0, 0, 8, 6),
                }, 0, row);

                detailsLayout.Controls.Add(new Label
                {
                    Text = value,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoSize = true,
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
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
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
                    Margin = ScalePadding(0, 0, 8, 6),
                }, 0, row);

                Control editorControl;
                if (setting.Type == PhantomExtensionSettingType.Number)
                {
                    int minimum = setting.Minimum ?? -100000;
                    int maximum = setting.Maximum ?? 100000;
                    int numericValue = int.TryParse(currentValue, out int parsedValue)
                        ? Math.Clamp(parsedValue, minimum, maximum)
                        : Math.Clamp(int.TryParse(setting.DefaultValue, out int parsedDefault) ? parsedDefault : 0, minimum, maximum);

                    var numeric = new NumericUpDown
                    {
                        Dock = DockStyle.Fill,
                        Minimum = minimum,
                        Maximum = maximum,
                        Value = numericValue,
                        Margin = ScalePadding(0, 0, 0, 6),
                    };
                    numeric.ValueChanged += (_, _) =>
                    {
                        selectedItem.Installation.SettingValues[setting.Key] =
                            numeric.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    };

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

            if (darkTheme)
            {
                ApplyColorsRecursively(detailsLayout, Color.FromArgb(38, 44, 52), Color.FromArgb(228, 236, 247));
                ApplySettingsInputColors(detailsLayout);
            }

            extensionSettingsPanel.Controls.Add(detailsLayout);
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

        extensionsListBox.SelectedIndexChanged += (_, _) => RenderSelectedExtensionSettings();
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

            if (picker.ShowDialog(settingsWindow) != DialogResult.OK)
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
                        FileName = BuildExtensionFileName(definition.Id),
                        Enabled = true,
                        SettingValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    };

                    draftSettings.Extensions.Add(existing);
                }

                existing.Id = definition.Id;
                existing.DisplayName = definition.Name;
                existing.FileName = BuildExtensionFileName(definition.Id);
                existing.Enabled = true;
                EnsureExtensionSettingValues(existing, definition);

                stagedSourcesByExtensionId[definition.Id] = rawSource;
                extensionStatusLabel.Text = $"Imported '{definition.Name}'. Click Save to install it.";
                RefreshExtensionDraftList(definition.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    settingsWindow,
                    $"Extension import failed:\n{ex.Message}",
                    "PhantomHaze",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };

        uninstallAllExtensionsButton.Click += (_, _) =>
        {
            if (draftSettings.Extensions.Count == 0)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                settingsWindow,
                "This will uninstall every extension when you save settings. Continue?",
                "PhantomHaze",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                return;
            }

            draftSettings.Extensions.Clear();
            stagedSourcesByExtensionId.Clear();
            extensionStatusLabel.Text = "All extensions are marked for uninstallation. Click Save to apply.";
            RefreshExtensionDraftList();
        };

        RefreshExtensionDraftList();

        if (darkTheme)
        {
            settingsWindow.BackColor = Color.FromArgb(38, 44, 52);
            ApplyColorsRecursively(settingsWindow, Color.FromArgb(38, 44, 52), Color.FromArgb(228, 236, 247));
            ApplySettingsInputColors(settingsWindow);

            saveButton.BackColor = Color.FromArgb(67, 76, 90);
            saveButton.ForeColor = Color.FromArgb(228, 236, 247);
            cancelButton.BackColor = Color.FromArgb(67, 76, 90);
            cancelButton.ForeColor = Color.FromArgb(228, 236, 247);
        }

        settingsWindow.Shown += (_, _) => ApplyProtectionToSettingsWindow(settingsWindow);

        if (settingsWindow.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (themeComboBox.SelectedItem is OptionItem<AppTheme> themeOption)
        {
            draftSettings.Theme = themeOption.Value;
        }

        draftSettings.RenderWebViewInBackground = renderInBackgroundCheckBox.Checked;
        draftSettings.PersistHistoryAcrossSessions = persistHistoryCheckBox.Checked;

        if (retentionComboBox.SelectedItem is OptionItem<int> retentionOption)
        {
            draftSettings.HistoryRetentionDays = retentionOption.Value;
        }

        if (searchEngineComboBox.SelectedItem is OptionItem<SearchEngine> searchOption)
        {
            draftSettings.DefaultSearchEngine = searchOption.Value;
        }

        draftSettings.StartingPage = NormalizeStartPage(startPageTextBox.Text.Trim());
        draftSettings.ExtensionsEnabled = extensionsEnabledCheckBox.Checked;

        try
        {
            PersistExtensionDraftChanges(previousExtensions, draftSettings.Extensions, stagedSourcesByExtensionId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Extension changes could not be applied:\n{ex.Message}",
                "PhantomHaze",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            Log($"Extension persistence failed: {ex.Message}");
            return;
        }

        _settings = draftSettings;
        NormalizeSettings();
        SaveSettings();
        LoadInstalledExtensions();
        RefreshExtensionSurface();

        if (_settings.PersistHistoryAcrossSessions)
        {
            SaveHistory();
        }
        else
        {
            TryDeleteFile(_historyFilePath);
        }

        _addressBarTextBox.PlaceholderText = NormalizeStartPage(GetEffectiveStartPage());
        RefreshHistoryPanelItems();
        UpdateTabVisibilityAndRendering();
        ApplyTheme();
        Log("Settings updated and saved.");
    }

    private void ApplyProtectionToSettingsWindow(Form settingsWindow)
    {
        if (!_protectionEnabled)
        {
            return;
        }

        var (ok, error) = NativeMethods.ExcludeFromCapture(settingsWindow.Handle, enable: true);
        if (!ok)
        {
            Log($"Settings window protection failed. Win32 error {error}: {NativeMethods.DescribeError(error)}.");
            return;
        }

        Log("Settings window protection applied.");
    }

    private void Log(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        AppendLogEntryToUi(entry);

        try
        {
            File.AppendAllText(_logFilePath, entry);
        }
        catch
        {
            // Keep the app functional if temp storage is unavailable.
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