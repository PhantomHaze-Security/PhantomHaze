namespace PhantomHaze;

// Background tabs and the resource saver. A tab that goes into the background (another tab is shown, or another view)
// keeps rendering its page for the time chosen in Settings -> General ("Render background tabs for"), then stops until
// it is shown again. With more than 20 tabs open, the resource saver shortens that time to 10 seconds and says so in a
// green banner at the top of the window (its Disable button turns the resource saver off; its X hides the banner for
// good). Only protocols that can pause a page (WebView2) actually stop rendering; the others ignore it.
public partial class MainForm
{
    private const int DefaultBackgroundTabRenderSeconds = 60;
    private const int ResourceSaverTabThreshold = 20;
    private const int ResourceSaverRenderSeconds = 10;

    /// <summary>The choices of Settings -> General -> Render background tabs for (-1: forever).</summary>
    private static readonly OptionItem<int>[] BackgroundTabRenderOptions =
    [
        new OptionItem<int>(10, "10 secs"),
        new OptionItem<int>(30, "30 secs"),
        new OptionItem<int>(60, "1 minute"),
        new OptionItem<int>(300, "5 mins"),
        new OptionItem<int>(600, "10 mins"),
        new OptionItem<int>(1800, "30 mins"),
        new OptionItem<int>(3600, "1 hour"),
        new OptionItem<int>(-1, "Forever"),
    ];

    // Fires when the next background tab is due to stop rendering (stopped while no tab is waiting).
    private readonly System.Windows.Forms.Timer _backgroundRenderTimer = new();
    private bool _backgroundRenderTimerHooked;
    private bool _resourceSaverWasActive;

    private readonly TableLayoutPanel _resourceSaverBannerPanel = new();
    private readonly Label _resourceSaverTitleLabel = new();
    private readonly Label _resourceSaverMessageLabel = new();
    private readonly PillButton _resourceSaverDisableButton = new();
    private readonly Button _resourceSaverCloseButton = new();
    private readonly ToolTip _resourceSaverToolTip = new();

    private bool IsResourceSaverActive => _settings.ResourceSaverEnabled && _tabs.Count > ResourceSaverTabThreshold;

    /// <summary>How long a background tab keeps rendering (null: forever).</summary>
    private TimeSpan? GetBackgroundRenderLimit()
    {
        if (IsResourceSaverActive)
        {
            return TimeSpan.FromSeconds(ResourceSaverRenderSeconds);
        }

        int seconds = _settings.BackgroundTabRenderSeconds ?? DefaultBackgroundTabRenderSeconds;
        return seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Lets shown tabs (and background tabs within their time) render, stops background tabs whose time is up, and sets
    /// the timer for the next one. Called whenever tabs are shown or hidden, opened or closed, and when settings change.
    /// </summary>
    private void ApplyBackgroundTabRendering()
    {
        if (!_backgroundRenderTimerHooked)
        {
            _backgroundRenderTimerHooked = true;
            _backgroundRenderTimer.Tick += (_, _) =>
            {
                _backgroundRenderTimer.Stop();
                ApplyBackgroundTabRendering();
            };
        }

        _backgroundRenderTimer.Stop();
        if (IsDisposed)
        {
            return;
        }

        UpdateResourceSaverState();

        DateTime now = DateTime.UtcNow;
        TimeSpan? limit = GetBackgroundRenderLimit();
        TimeSpan? nextCheck = null;
        int stopped = 0;
        foreach (BrowserTab tab in _tabs)
        {
            if (!tab.Page.IsReady || !tab.Page.CanPauseRendering)
            {
                continue;
            }

            bool render = true;
            if (tab.BackgroundSinceUtc is DateTime since && limit is TimeSpan backgroundLimit)
            {
                TimeSpan remaining = since + backgroundLimit - now;
                render = remaining > TimeSpan.Zero;
                if (render && (nextCheck is null || remaining < nextCheck))
                {
                    nextCheck = remaining;
                }
            }

            if (render == !tab.RenderingSuspended)
            {
                // Already in that state.
                continue;
            }

            if (render && tab.BackgroundSinceUtc is null)
            {
                tab.ResumedFromSuspension = true;
            }

            tab.RenderingSuspended = !render;
            tab.Page.SetRenderingActive(render);
            tab.FaviconBox.Invalidate();
            if (!render)
            {
                stopped++;
            }
        }

        if (stopped > 0)
        {
            Log(stopped == 1
                ? "1 background tab stopped rendering to save resources (it continues when it is shown)."
                : $"{stopped} background tabs stopped rendering to save resources (they continue when they are shown).");
        }

        if (nextCheck is TimeSpan wait)
        {
            _backgroundRenderTimer.Interval = (int)Math.Clamp(wait.TotalMilliseconds + 50, 250, int.MaxValue);
            _backgroundRenderTimer.Start();
        }
    }

    private const string PausedTabGlyph = "💤";

    /// <summary>
    /// Draws 💤 over the icon of a tab whose page has stopped rendering in the background (the icon comes back as soon as
    /// the tab renders again). Windows draws emoji in one colour here, so it is drawn in a blue that suits the tab.
    /// </summary>
    private static void OnTabFaviconPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not PictureBox { Tag: BrowserTab { RenderingSuspended: true } } faviconBox)
        {
            return;
        }

        Rectangle bounds = faviconBox.ClientRectangle;
        if (bounds.Width <= 2 || bounds.Height <= 2)
        {
            return;
        }

        Color back = faviconBox.BackColor.A == 255 ? faviconBox.BackColor : (faviconBox.Parent?.BackColor ?? SystemColors.Control);
        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, bounds);
        }

        Color glyph = IsDarkColor(back) ? Color.FromArgb(147, 197, 253) : Color.FromArgb(37, 99, 235);
        float size = Math.Max(6f, Math.Min(bounds.Width, bounds.Height) * 0.85f);
        using var font = new Font("Segoe UI Emoji", size, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(
            e.Graphics,
            PausedTabGlyph,
            font,
            bounds,
            glyph,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>Logs when the resource saver starts or stops, and shows or hides its banner.</summary>
    private void UpdateResourceSaverState()
    {
        bool active = IsResourceSaverActive;
        if (active != _resourceSaverWasActive)
        {
            _resourceSaverWasActive = active;
            Log(active
                ? $"Resource saver is on: more than {ResourceSaverTabThreshold} tabs are open, so background tabs stop rendering after {ResourceSaverRenderSeconds} seconds."
                : "Resource saver is off: background tabs follow Settings -> General again.");
        }

        bool showBanner = active && !_settings.ResourceSaverBannerDismissed;
        if (_resourceSaverBannerPanel.Visible != showBanner)
        {
            _resourceSaverBannerPanel.Visible = showBanner;
        }
    }

    /// <summary>The green resource saver banner, built like the update banner (it sits just below it).</summary>
    private void BuildResourceSaverBanner()
    {
        _resourceSaverBannerPanel.Dock = DockStyle.Top;
        _resourceSaverBannerPanel.Visible = false;
        _resourceSaverBannerPanel.Margin = new Padding(0);
        _resourceSaverBannerPanel.BorderStyle = BorderStyle.FixedSingle;
        _resourceSaverBannerPanel.ColumnCount = 4;
        _resourceSaverBannerPanel.RowCount = 1;
        _resourceSaverBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _resourceSaverBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _resourceSaverBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _resourceSaverBannerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _resourceSaverBannerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _resourceSaverBannerPanel.AccessibleName = "Resource saver";
        _resourceSaverBannerPanel.AccessibleRole = AccessibleRole.Alert;

        _resourceSaverTitleLabel.Text = "PhantomHaze is saving resources to stop your client from crashing";
        _resourceSaverTitleLabel.AutoSize = true;
        _resourceSaverTitleLabel.Dock = DockStyle.Fill;
        _resourceSaverTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _resourceSaverTitleLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _resourceSaverTitleLabel.UseMnemonic = false;

        // Takes the remaining width and ends in "..." when the window is too narrow, so the buttons always stay visible.
        _resourceSaverMessageLabel.Text =
            $"Tabs in the background stop rendering after {ResourceSaverRenderSeconds} seconds while more than {ResourceSaverTabThreshold} tabs are open.";
        _resourceSaverMessageLabel.Dock = DockStyle.Fill;
        _resourceSaverMessageLabel.AutoEllipsis = true;
        _resourceSaverMessageLabel.TextAlign = ContentAlignment.MiddleLeft;
        _resourceSaverMessageLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _resourceSaverMessageLabel.Margin = new Padding(0);
        _resourceSaverMessageLabel.UseMnemonic = false;

        _resourceSaverDisableButton.Text = "Disable";
        _resourceSaverDisableButton.AutoSize = true;
        _resourceSaverDisableButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _resourceSaverDisableButton.Anchor = AnchorStyles.None;
        _resourceSaverDisableButton.FlatStyle = FlatStyle.Flat;
        _resourceSaverDisableButton.FlatAppearance.BorderSize = 1;
        _resourceSaverDisableButton.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _resourceSaverDisableButton.UseMnemonic = false;
        _resourceSaverDisableButton.AccessibleDescription = "Turns the resource saver off. It can be turned back on in Settings -> General.";
        _resourceSaverDisableButton.Click += (_, _) => DisableResourceSaver();

        // Unlike the update banner's X, this one hides the banner for good (the resource saver itself stays on).
        _resourceSaverCloseButton.Text = "X";
        _resourceSaverCloseButton.AutoSize = false;
        _resourceSaverCloseButton.Anchor = AnchorStyles.None;
        _resourceSaverCloseButton.FlatStyle = FlatStyle.Flat;
        _resourceSaverCloseButton.FlatAppearance.BorderSize = 0;
        _resourceSaverCloseButton.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _resourceSaverCloseButton.Margin = new Padding(0);
        _resourceSaverCloseButton.AccessibleName = "Don't show this message again";
        _resourceSaverCloseButton.Click += (_, _) => DismissResourceSaverBanner();
        _resourceSaverToolTip.SetToolTip(_resourceSaverCloseButton, "Don't show this again");

        _resourceSaverBannerPanel.Controls.Add(_resourceSaverTitleLabel, 0, 0);
        _resourceSaverBannerPanel.Controls.Add(_resourceSaverMessageLabel, 1, 0);
        _resourceSaverBannerPanel.Controls.Add(_resourceSaverDisableButton, 2, 0);
        _resourceSaverBannerPanel.Controls.Add(_resourceSaverCloseButton, 3, 0);
        ApplyResourceSaverBannerMetrics();
    }

    private void ApplyResourceSaverBannerMetrics()
    {
        _resourceSaverBannerPanel.Height = ScaleByDpi(36);
        _resourceSaverBannerPanel.Padding = ScalePadding(12, 3, 6, 3);
        _resourceSaverTitleLabel.Margin = ScalePadding(0, 0, 6, 0);
        _resourceSaverDisableButton.MinimumSize = new Size(ScaleByDpi(76), ScaleByDpi(26));
        _resourceSaverDisableButton.Padding = ScalePadding(10, 0, 10, 0);
        _resourceSaverDisableButton.Margin = ScalePadding(8, 0, 6, 0);
        _resourceSaverCloseButton.Size = new Size(ScaleByDpi(30), ScaleByDpi(26));
    }

    private void ApplyThemeToResourceSaverBanner()
    {
        bool darkTheme = IsDarkTheme();
        Color backColor = darkTheme ? Color.FromArgb(38, 102, 60) : Color.FromArgb(176, 232, 186);
        Color textColor = darkTheme ? Color.White : Color.Black;

        // The labels inherit the banner background; the Disable button is themed with the other command buttons.
        _resourceSaverBannerPanel.BackColor = backColor;
        _resourceSaverTitleLabel.ForeColor = textColor;
        _resourceSaverMessageLabel.ForeColor = textColor;
        _resourceSaverCloseButton.BackColor = backColor;
        _resourceSaverCloseButton.ForeColor = textColor;
        _resourceSaverCloseButton.FlatAppearance.MouseOverBackColor = darkTheme ? Color.FromArgb(50, 122, 75) : Color.FromArgb(156, 220, 168);
        _resourceSaverCloseButton.FlatAppearance.MouseDownBackColor = darkTheme ? Color.FromArgb(62, 140, 89) : Color.FromArgb(136, 206, 150);
    }

    private void DisableResourceSaver()
    {
        SaveSettingChange(settings => settings.ResourceSaverEnabled = false);
        Log("Resource saver disabled from its banner. It can be turned back on in Settings -> General.");
        ApplyBackgroundTabRendering();
    }

    private void DismissResourceSaverBanner()
    {
        SaveSettingChange(settings => settings.ResourceSaverBannerDismissed = true);
        Log("The resource saver message was hidden for good; the resource saver itself stays on.");
        UpdateResourceSaverState();
    }

    /// <summary>
    /// Applies one change to the settings and saves it straight away (outside the Settings window). If another
    /// PhantomHaze window changed settings.json in the meantime, only this change is written into its version, and this
    /// window still leaves the file alone when it closes, so the other window's changes are never undone.
    /// </summary>
    private void SaveSettingChange(Action<AppSettings> change)
    {
        change(_settings);
        try
        {
            if (!HasSettingsFileChangedSinceLastAccess())
            {
                WriteSettingsFile(_settings);
                return;
            }

            AppSettings? onDisk = TryReadSettingsFile();
            if (onDisk is null)
            {
                Log("The change applies to this session only: settings.json could not be read.");
                return;
            }

            change(onDisk);
            SettingsFileStamp? stamp = _settingsFileStamp;
            WriteSettingsFile(onDisk);
            _settingsFileStamp = stamp;
        }
        catch (Exception ex)
        {
            Log($"The change could not be saved and applies to this session only: {ex.Message}");
        }
    }
}
