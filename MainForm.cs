using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

public class MainForm : Form
{
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
    }

    private readonly Label _contentLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Button _toggleButton = new();
    private readonly Button _webViewToggleButton = new();
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
    private readonly Panel _webViewHostPanel = new();
    private readonly Panel _historyPanel = new();
    private readonly ListBox _historyListBox = new();
    private readonly ListBox _favoritesListBox = new();
    private readonly Button _historyClearButton = new();
    private readonly Panel _findPanel = new();
    private readonly TextBox _findTextBox = new();
    private readonly Label _findCountLabel = new();
    private readonly Button _findCloseButton = new();
    private readonly TextBox _logBox = new();
    private readonly HttpClient _faviconHttpClient = new();
    private readonly Font _tabCloseFontRegular = new("Segoe UI", 8, FontStyle.Bold);
    private readonly Font _tabCloseFontCompact = new("Segoe UI", 6.5f, FontStyle.Bold);
    private readonly string _logFilePath;
    private readonly string _webSessionPath;
    private readonly string _favoritesFilePath;
    private readonly List<string> _sessionHistory = new();
    private readonly List<string> _favoriteUrls = new();
    private readonly List<BrowserTab> _tabs = new();
    private readonly Dictionary<CoreWebView2, BrowserTab> _tabByCore = new();

    private CoreWebView2Environment? _webEnvironment;
    private BrowserTab? _activeTab;
    private bool _protectionEnabled;
    private bool _webViewInitialized;
    private bool _webViewModeEnabled;
    private bool _sessionDataCleared;
    private int _nextTabId = 1;
    private int _findRequestVersion;

    private const string DefaultHomeUrl = "https://www.google.com";
    private const string FindShortcutMessage = "__PH_FIND__";
    private static readonly Image DefaultTabIcon = SystemIcons.Application.ToBitmap();

    public MainForm()
    {
        Text = "PhantomHaze v1.2.5 Beta";
        Width = 980;
        Height = 620;
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        KeyDown += OnMainFormKeyDown;

        _logFilePath = InitializeLogFilePath();
        _webSessionPath = InitializeWebSessionPath();
        _favoritesFilePath = InitializeFavoritesFilePath();
        _faviconHttpClient.Timeout = TimeSpan.FromSeconds(5);

        BuildUi();
        LoadFavorites();

        Load += (_, _) =>
        {
            Log($"Log file: {_logFilePath}");
            Log($"Web session path: {_webSessionPath}");
            Log($"Favorites file: {_favoritesFilePath}");
            LogTrueOsBuild();
            ApplyProtection(enable: true);
        };

        FormClosing += (_, _) => CleanupWebSession();
        Resize += (_, _) =>
        {
            UpdateTabLayout();
            UpdateResponsiveLayout();
        };
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

    private static string InitializeFavoritesFilePath()
    {
        string favoritesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhantomHaze");

        Directory.CreateDirectory(favoritesDir);
        return Path.Combine(favoritesDir, "favorites.json");
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
        _contentLabel.Text = "PhantomHaze Beta v1.2.5";
        _contentLabel.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        _contentLabel.ForeColor = Color.Firebrick;
        _contentLabel.TextAlign = ContentAlignment.MiddleCenter;
        _contentLabel.Dock = DockStyle.Top;
        _contentLabel.Height = 80;

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 34;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.Font = new Font("Segoe UI", 10, FontStyle.Bold);

        _toggleButton.Text = "Disable Protection";
        _toggleButton.Dock = DockStyle.Top;
        _toggleButton.Height = 36;
        _toggleButton.Click += (_, _) => ApplyProtection(enable: !_protectionEnabled);

        BuildAddressBar();
        BuildHistoryPanel();
        BuildFindPanel();

        _webViewToggleButton.Text = "Web View";
        _webViewToggleButton.Dock = DockStyle.Bottom;
        _webViewToggleButton.Height = 38;
        _webViewToggleButton.Click += async (_, _) => await ToggleWebViewModeAsync();
        UpdateWebViewToggleVisual();

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
        _contentHostPanel.Resize += (_, _) => PositionFindPanel();

        Controls.Add(_contentHostPanel);
        Controls.Add(_addressBarPanel);
        Controls.Add(_toggleButton);
        Controls.Add(_statusLabel);
        Controls.Add(_contentLabel);
        Controls.Add(_webViewToggleButton);

        UpdateResponsiveLayout();
    }

    private void BuildAddressBar()
    {
        _addressBarPanel.Dock = DockStyle.Top;
        _addressBarPanel.Height = 104;
        _addressBarPanel.Padding = new Padding(8, 8, 8, 6);
        _addressBarPanel.Visible = false;
        _addressBarPanel.SizeChanged += (_, _) => UpdateTabLayout();

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var stripLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        stripLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));

        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82f));
        stripLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185f));

        var addressInputLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        addressInputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addressInputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32f));

        _addressBarTextBox.Dock = DockStyle.Fill;
        _addressBarTextBox.PlaceholderText = DefaultHomeUrl;
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
        _favoriteButton.Margin = new Padding(3, 2, 0, 2);
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
        _zoomLabel.Margin = new Padding(3, 2, 3, 2);
        _zoomLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);

        _zoomSlider.Minimum = 50;
        _zoomSlider.Maximum = 200;
        _zoomSlider.Value = 100;
        _zoomSlider.TickFrequency = 10;
        _zoomSlider.AutoSize = false;
        _zoomSlider.Dock = DockStyle.Fill;
        _zoomSlider.Margin = new Padding(3, 4, 3, 2);
        _zoomSlider.Scroll += (_, _) => ApplyZoom();

        stripLayout.Controls.Add(addressInputLayout, 0, 0);
        stripLayout.Controls.Add(_backButton, 1, 0);
        stripLayout.Controls.Add(_goButton, 2, 0);
        stripLayout.Controls.Add(_refreshButton, 3, 0);
        stripLayout.Controls.Add(_historyButton, 4, 0);
        stripLayout.Controls.Add(_zoomLabel, 5, 0);
        stripLayout.Controls.Add(_zoomSlider, 6, 0);

        var tabsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 0),
            Padding = new Padding(0),
        };
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
        _newTabButton.MinimumSize = new Size(36, 28);
        _newTabButton.Padding = new Padding(0);
        _newTabButton.Margin = new Padding(2, 0, 0, 0);
        _newTabButton.Click += async (_, _) => await CreateTabAsync(DefaultHomeUrl, activate: true);

        tabsLayout.Controls.Add(_tabsFlowPanel, 0, 0);
        tabsLayout.Controls.Add(_newTabButton, 1, 0);

        rootLayout.Controls.Add(stripLayout, 0, 0);
        rootLayout.Controls.Add(tabsLayout, 0, 1);

        _addressBarPanel.Controls.Add(rootLayout);
        UpdateFavoriteButtonState();
    }

    private static void ConfigureStripButton(Button button)
    {
        button.AutoSize = false;
        button.Margin = new Padding(3, 2, 3, 2);
        button.Font = new Font("Segoe UI", 9, FontStyle.Regular);
    }

    private void BuildHistoryPanel()
    {
        _historyPanel.Dock = DockStyle.Right;
        _historyPanel.Width = 380;
        _historyPanel.Padding = new Padding(8);
        _historyPanel.Visible = false;
        _historyPanel.BackColor = Color.FromArgb(245, 248, 252);

        var historyTitle = new Label
        {
            Text = "Session History",
            Dock = DockStyle.Fill,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
        };

        var favoritesTitle = new Label
        {
            Text = "Favourites",
            Dock = DockStyle.Fill,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
        };

        _historyClearButton.Text = "Clear History";
        _historyClearButton.Dock = DockStyle.Fill;
        _historyClearButton.Height = 32;
        _historyClearButton.Click += (_, _) =>
        {
            _sessionHistory.Clear();
            RefreshHistoryPanelItems();
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

        var historyLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };

        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));

        historyLayout.Controls.Add(historyTitle, 0, 0);
        historyLayout.Controls.Add(_historyListBox, 0, 1);
        historyLayout.Controls.Add(favoritesTitle, 0, 2);
        historyLayout.Controls.Add(_favoritesListBox, 0, 3);
        historyLayout.Controls.Add(_historyClearButton, 0, 4);

        _historyPanel.Controls.Add(historyLayout);

        RefreshHistoryPanelItems();
        RefreshFavoritesPanelItems();
    }

    private void BuildFindPanel()
    {
        _findPanel.Visible = false;
        _findPanel.Size = new Size(360, 42);
        _findPanel.BackColor = Color.FromArgb(250, 252, 255);
        _findPanel.BorderStyle = BorderStyle.FixedSingle;
        _findPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        var findLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(6),
        };
        findLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        findLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
        findLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28f));

        _findTextBox.Dock = DockStyle.Fill;
        _findTextBox.PlaceholderText = "Find in page";
        _findTextBox.Margin = new Padding(0, 0, 6, 0);
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

    private async Task ToggleWebViewModeAsync()
    {
        bool enableWebView = !_webViewModeEnabled;
        if (enableWebView)
        {
            await EnsureWebViewInitializedAsync();
            if (!_webViewInitialized)
            {
                return;
            }

            if (_tabs.Count == 0)
            {
                BrowserTab? firstTab = await CreateTabAsync(DefaultHomeUrl, activate: true);
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
            _historyButton.BackColor = SystemColors.Control;
            UpdateWebViewToggleVisual();

            if (_activeTab is null && _tabs.Count > 0)
            {
                ActivateTab(_tabs[0]);
            }
            else if (_activeTab is not null)
            {
                ActivateTab(_activeTab);
            }

            PositionFindPanel();
            UpdateZoomControlsForActiveTab();
            Log("Web View mode enabled.");
            return;
        }

        _webViewModeEnabled = false;
        _historyPanel.Visible = false;
        _historyButton.BackColor = SystemColors.Control;
        HideFindPanel(clearHighlights: true);

        foreach (BrowserTab tab in _tabs)
        {
            tab.WebView.Visible = false;
        }

        _addressBarPanel.Visible = false;
        _webViewHostPanel.Visible = false;
        _logBox.Visible = true;
        _logBox.BringToFront();
        UpdateWebViewToggleVisual();
        UpdateZoomControlsForActiveTab();
        UpdateNavigationButtons();
        UpdateFavoriteButtonState();
        Log("Web View mode disabled; log panel restored.");
    }

    private void UpdateWebViewToggleVisual()
    {
        _webViewToggleButton.BackColor = _webViewModeEnabled ? Color.LightSkyBlue : SystemColors.Control;
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
            Height = 28,
            Width = 180,
            Margin = new Padding(0, 0, 2, 0),
            Padding = new Padding(4, 4, 4, 3),
            BackColor = Color.FromArgb(235, 240, 247),
            Cursor = Cursors.Hand,
        };

        faviconBox = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = 16,
            Height = 16,
            SizeMode = PictureBoxSizeMode.StretchImage,
            Image = DefaultTabIcon,
            Margin = new Padding(0),
            Cursor = Cursors.Hand,
        };

        closeButton = new Button
        {
            Dock = DockStyle.Right,
            Width = 22,
            Text = "x",
            Font = _tabCloseFontRegular,
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

        if (previous is not null && !ReferenceEquals(previous, tab))
        {
            previous.WebView.Visible = false;
        }

        _activeTab = tab;
        RefreshTabTitle(tab);

        if (_webViewModeEnabled)
        {
            tab.WebView.Visible = true;
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
                BrowserTab? replacement = await CreateTabAsync(DefaultHomeUrl, activate: true);
                if (replacement is null)
                {
                    _webViewModeEnabled = false;
                    _historyPanel.Visible = false;
                    _historyButton.BackColor = SystemColors.Control;
                    HideFindPanel(clearHighlights: true);
                    _addressBarPanel.Visible = false;
                    _webViewHostPanel.Visible = false;
                    _logBox.Visible = true;
                    _logBox.BringToFront();
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
        foreach (BrowserTab tab in _tabs)
        {
            bool active = ReferenceEquals(tab, _activeTab);
            tab.HeaderPanel.BackColor = active
                ? Color.FromArgb(221, 233, 255)
                : Color.FromArgb(235, 240, 247);

            tab.CloseButton.BackColor = active
                ? Color.FromArgb(228, 235, 247)
                : Color.FromArgb(243, 244, 247);

            tab.TitleLabel.ForeColor = active ? Color.Black : Color.FromArgb(35, 35, 35);
        }
    }

    private void UpdateTabLayout()
    {
        if (_tabs.Count == 0 || _tabsFlowPanel.ClientSize.Width <= 0)
        {
            return;
        }

        const int tabGap = 2;
        int totalGap = (_tabs.Count - 1) * tabGap;
        int available = Math.Max(1, _tabsFlowPanel.ClientSize.Width - totalGap);
        int rawWidth = Math.Max(1, available / _tabs.Count);
        bool compact = rawWidth < 130;
        int targetWidth = compact
            ? Math.Max(14, rawWidth)
            : Math.Clamp(rawWidth, 130, 220);

        for (int i = 0; i < _tabs.Count; i++)
        {
            BrowserTab tab = _tabs[i];
            tab.HeaderPanel.Width = targetWidth;
            tab.HeaderPanel.Height = compact ? 26 : 28;
            tab.HeaderPanel.Margin = new Padding(0, 0, i == _tabs.Count - 1 ? 0 : tabGap, 0);

            if (compact)
            {
                int closeWidth = Math.Clamp(targetWidth / 2, 6, 12);
                int iconWidth = Math.Clamp(targetWidth - closeWidth, 6, 14);

                tab.HeaderPanel.Padding = new Padding(0, 3, 0, 2);
                tab.TitleLabel.Visible = false;
                tab.CloseButton.Width = closeWidth;
                tab.CloseButton.Font = _tabCloseFontCompact;
                tab.FaviconBox.Width = iconWidth;
                tab.FaviconBox.Height = iconWidth;
            }
            else
            {
                tab.HeaderPanel.Padding = new Padding(4, 4, 4, 3);
                tab.TitleLabel.Visible = true;
                tab.CloseButton.Width = 22;
                tab.CloseButton.Font = _tabCloseFontRegular;
                tab.FaviconBox.Width = 16;
                tab.FaviconBox.Height = 16;
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

        string rawUrl = _addressBarTextBox.Text.Trim();
        if (!TryNormalizeUrl(rawUrl, out string normalizedUrl))
        {
            Log($"Invalid web address: {rawUrl}");
            return;
        }

        NavigateTabToUrl(_activeTab, normalizedUrl, "Navigate requested");
    }

    private void NavigateTabToUrl(BrowserTab tab, string normalizedUrl, string reason)
    {
        if (tab.WebView.CoreWebView2 is null)
        {
            Log("Tab navigation requested before Web View was initialized.");
            return;
        }

        tab.CurrentUrl = normalizedUrl;
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

    private static string NormalizeOrDefaultUrl(string? rawUrl)
    {
        return TryNormalizeUrl(rawUrl, out string normalizedUrl)
            ? normalizedUrl
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
            _historyButton.BackColor = SystemColors.Control;
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

        _historyButton.BackColor = Color.LightSteelBlue;
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
        _historyButton.BackColor = SystemColors.Control;
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
        _historyButton.BackColor = SystemColors.Control;
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
        _historyListBox.Items.Clear();
        if (_sessionHistory.Count == 0)
        {
            _historyListBox.Items.Add("(No history in this session)");
            return;
        }

        for (int i = _sessionHistory.Count - 1; i >= 0; i--)
        {
            _historyListBox.Items.Add(_sessionHistory[i]);
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
            await RefreshTabFaviconAsync(tab);
            Log($"Tab {tab.Id} navigation completed: {currentUrl}");
            await LogCookieJarSnapshotAsync(currentUrl, tab);
        }
        else
        {
            Log($"Tab {tab.Id} navigation failed: {e.WebErrorStatus}");
        }

        if (_webViewModeEnabled && ReferenceEquals(_activeTab, tab))
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

        _ = BeginInvoke(new Action(() =>
        {
            _ = CreateTabAsync(popupUrl, activate: true);
        }));

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
        const int margin = 10;
        int x = Math.Max(margin, _contentHostPanel.ClientSize.Width - _findPanel.Width - margin);
        int y = margin;
        _findPanel.Location = new Point(x, y);
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
        bool enabled = _webViewModeEnabled && _activeTab is not null;
        _favoriteButton.Enabled = enabled;
        _favoriteButton.Text = "✩";
        if (!enabled)
        {
            _favoriteButton.BackColor = SystemColors.Control;
            _favoriteButton.ForeColor = SystemColors.ControlText;
            return;
        }

        string? currentUrl = GetActiveTabUrl();
        bool isFavorite = TryNormalizeUrl(currentUrl, out string normalizedCurrentUrl) &&
            _favoriteUrls.Any(url => url.Equals(normalizedCurrentUrl, StringComparison.OrdinalIgnoreCase));

        _favoriteButton.BackColor = isFavorite ? Color.Gold : SystemColors.Control;
        _favoriteButton.ForeColor = isFavorite ? Color.Black : SystemColors.ControlText;
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

        if (_sessionHistory.Count == 0 ||
            !_sessionHistory[^1].Equals(url, StringComparison.OrdinalIgnoreCase))
        {
            _sessionHistory.Add(url);
            if (_historyPanel.Visible)
            {
                RefreshHistoryPanelItems();
            }
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_webViewModeEnabled || !TryGetTabFromSender(sender, out BrowserTab tab))
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
        _sessionHistory.Clear();
        _historyPanel.Visible = false;
        _findPanel.Visible = false;

        SaveFavorites();

        foreach (BrowserTab tab in _tabs.ToArray())
        {
            RemoveTabAndDispose(tab);
        }

        _tabByCore.Clear();
        _activeTab = null;

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

        _statusLabel.Text = _protectionEnabled
            ? "Protection Enabled - Your screen is hidden from capture software. yipee :D"
            : "Protection Disabled - Your screen is visible to capture software. oh noes :(";
        _statusLabel.BackColor = _protectionEnabled ? Color.LightGreen : Color.LightSalmon;

        _toggleButton.Text = _protectionEnabled
            ? "Disable Protection"
            : "Enable Protection";
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
        _historyPanel.Width = Math.Clamp(preferredHistoryWidth, 260, 420);
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