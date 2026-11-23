using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace PhantomHaze;

/// <summary>
/// The Legacy protocol: pages are loaded with the Internet Explorer engine built into Windows (the legacy WebBrowser
/// control). WebView2 is not used. The engine runs inside PhantomHaze, so its window is covered by capture protection;
/// its own dialogs, menus and new windows are suppressed or redirected (see <see cref="LegacyProtocolPage"/>).
/// </summary>
internal sealed class LegacyWebProtocol : WebProtocol
{
    public override string Id => WebProtocolCatalog.LegacyId;

    public override string DisplayName => "Legacy";

    public override string Description =>
        "The Internet Explorer engine built into Windows (the legacy WebBrowser control), without WebView2. It runs " +
        "JavaScript, but many modern sites no longer support Internet Explorer. Pages can be kept in the Windows internet " +
        "cache like any Internet Explorer page; cookies only last until PhantomHaze closes.";

    public override WebProtocolFeatures Features =>
        WebProtocolFeatures.Zoom | WebProtocolFeatures.FindInPage | WebProtocolFeatures.JavaScript;

    public override string? GetUnavailableReason() => LegacyBrowserSupport.GetUnavailableReason();

    public override WebProtocolPage CreatePage(IWebProtocolHost host)
    {
        LegacyBrowserSupport.ConfigureProcess(host);
        return new LegacyProtocolPage(this, host);
    }
}

/// <summary>Process-wide settings of the Internet Explorer engine, applied once before the first Legacy page is created.</summary>
internal static class LegacyBrowserSupport
{
    private const string BrowserEmulationKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";
    private const int InternetExplorer11EdgeMode = 11001;

    // INTERNETFEATURELIST values (urlmon.h): security hardening recommended for applications that host the engine.
    private static readonly (int Feature, string Name)[] ProcessFeatures =
    [
        (1, "zone elevation protection"),
        (3, "MIME sniffing protection"),
        (4, "window restrictions"),
        (5, "pop-up management"),
        (7, "mk: protocol off"),
        (8, "local machine lockdown"),
        (10, "ActiveX install restriction"),
        (11, "address validation"),
        (12, "download restriction"),
        (13, "add-on management"),
        (14, "protocol lockdown"),
        (15, "user name and password in addresses off"),
        (16, "safe binding to objects"),
        (21, "navigation sounds off"),
        (25, "telnet: protocol off"),
        (27, "script input prompts blocked"),
    ];

    private static bool s_configured;
    private static string? s_createdEmulationValue;

    public static string? GetUnavailableReason()
    {
        try
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(@"CLSID\{8856F961-340A-11D0-A96B-00C04FD705A2}\InprocServer32");
            return key is null ? "The Internet Explorer engine (the WebBrowser control) is not installed on this PC." : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Cannot tell: let the page try.
            return null;
        }
    }

    public static void ConfigureProcess(IWebProtocolHost host)
    {
        if (s_configured)
        {
            return;
        }

        s_configured = true;

        // Without this the engine draws every page like Internet Explorer 7. The value is read by Windows when the first
        // control is created in this process, and is removed again when PhantomHaze closes.
        string? executable = Path.GetFileName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(executable))
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(BrowserEmulationKey);
                if (key.GetValue(executable) is null)
                {
                    key.SetValue(executable, InternetExplorer11EdgeMode, RegistryValueKind.DWord);
                    s_createdEmulationValue = executable;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                host.Log($"The Legacy protocol could not select Internet Explorer 11 mode, so pages may look older: {ex.Message}");
            }
        }

        var failed = new List<string>();
        foreach ((int feature, string name) in ProcessFeatures)
        {
            if (!NativeMethods.SetProcessInternetFeature(feature, enable: true))
            {
                failed.Add(name);
            }
        }

        if (failed.Count > 0)
        {
            host.Log($"Some Internet Explorer engine settings could not be applied: {string.Join(", ", failed)}.");
        }

        // Cookies set by Legacy pages are never written to the cookie store Windows shares with Internet Explorer.
        bool cookiesSessionOnly = NativeMethods.SuppressPersistentInternetCookies();
        host.Log(cookiesSessionOnly
            ? "The Legacy protocol is ready: Internet Explorer 11 mode, cookies kept until PhantomHaze closes."
            : "The Legacy protocol is ready, but Windows did not keep its cookies session-only.");
    }

    /// <summary>Removes the Internet Explorer 11 mode setting PhantomHaze added (called when PhantomHaze closes).</summary>
    public static void Cleanup()
    {
        if (s_createdEmulationValue is not string name)
        {
            return;
        }

        s_createdEmulationValue = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BrowserEmulationKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Harmless: it only selects Internet Explorer 11 mode for PhantomHaze.
        }
    }
}

/// <summary>
/// A tab shown with the Legacy protocol. Internet Explorer's own windows would not be excluded from screen capture, so:
/// its dialogs are suppressed (silent mode), its context menu is off, the shortcuts that open its windows are kept from
/// it, new windows open as PhantomHaze tabs, file downloads are cancelled, its error pages are replaced and every other
/// window it opens on the UI thread is protected by PhantomHaze (<see cref="IWebProtocolHost.SetUiThreadWindowGuard"/>).
/// </summary>
internal sealed class LegacyProtocolPage : WebProtocolPage
{
    // NWMF_USERINITED | NWMF_USERREQUESTED | NWMF_USERALLOWED: the new window comes from something the user did.
    private const uint NewWindowFromUser = 0x2 | 0x80 | 0x100;

    private readonly PhantomLegacyBrowser _browser;
    private string _currentUrl = string.Empty;
    private string _title = string.Empty;
    private int _zoomPercent = 100;
    private int _appliedZoomPercent = 100;
    private bool _initialized;
    private bool _showingMessagePage;
    private bool _reportedMissingEvents;

    public LegacyProtocolPage(WebProtocol protocol, IWebProtocolHost host)
        : base(protocol, host)
    {
        _browser = new PhantomLegacyBrowser
        {
            Dock = DockStyle.Fill,
            Visible = false,
            // Silent mode: Internet Explorer shows none of its dialogs (script errors, alert(), security prompts).
            ScriptErrorsSuppressed = true,
            // The context menu is a separate popup window and offers Print, View source and Save as.
            IsWebBrowserContextMenuEnabled = false,
            // Dropping a file onto the page would open it from disk.
            AllowWebBrowserDrop = false,
            WebBrowserShortcutsEnabled = true,
            NavigationFilter = CheckNavigation,
        };

        _browser.NavigationBlocked += OnNavigationBlocked;
        _browser.TopLevelNavigationStarting += OnTopLevelNavigationStarting;
        _browser.TopLevelNavigated += OnTopLevelNavigated;
        _browser.TopLevelDocumentCompleted += OnTopLevelDocumentCompleted;
        _browser.TopLevelNavigationFailed += OnTopLevelNavigationFailed;
        _browser.NewWindowRequested += OnBrowserNewWindowRequested;
        _browser.DownloadBlocked += OnDownloadBlocked;
        _browser.FindShortcutPressed += OnFindShortcutPressed;
        _browser.DocumentTitleChanged += OnDocumentTitleChanged;
        _browser.CanGoBackChanged += OnCanGoBackChanged;

        // In-process windows the engine opens (showModalDialog and anything else) are excluded from capture as they open.
        Host.SetUiThreadWindowGuard(this, enabled: true);
    }

    public override Control View => _browser;

    public override bool IsReady => _initialized && !IsDisposed;

    public override string CurrentUrl => _currentUrl;

    public override string Title => _title;

    public override bool CanGoBack => !IsDisposed && _browser.CanGoBack;

    public override Task InitializeAsync()
    {
        if (Protocol.GetUnavailableReason() is string reason)
        {
            throw new WebProtocolUnavailableException(reason);
        }

        // The Internet Explorer control is created with the window.
        _ = _browser.Handle;
        _initialized = true;
        return Task.CompletedTask;
    }

    public override void Navigate(string url)
    {
        if (!IsReady)
        {
            return;
        }

        _browser.Navigate(url);
        if (!_browser.EventsConnected && !_reportedMissingEvents)
        {
            _reportedMissingEvents = true;
            Host.Log("The Legacy protocol could not connect to all Internet Explorer events: links that open a new window are blocked instead of opening in a new tab.");
        }
    }

    public override void GoBack()
    {
        if (IsReady && _browser.CanGoBack)
        {
            _browser.GoBack();
        }
    }

    public override void Reload()
    {
        if (!IsReady)
        {
            return;
        }

        // A message page is a document written by PhantomHaze; reloading it means trying the address again.
        if (_showingMessagePage && IsWebAddress(_currentUrl))
        {
            Navigate(_currentUrl);
            return;
        }

        _browser.Refresh();
    }

    public override void SetZoom(int percent)
    {
        _zoomPercent = Math.Clamp(percent, 10, 1000);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (IsDisposed || _zoomPercent == _appliedZoomPercent || _browser.ActiveXInstance is not object browser ||
            _browser.ReadyState is WebBrowserReadyState.Uninitialized or WebBrowserReadyState.Loading)
        {
            return;
        }

        try
        {
            // ExecWB(OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, percent): Internet Explorer's own page zoom.
            browser.GetType().InvokeMember("ExecWB", BindingFlags.InvokeMethod, null, browser, new object?[] { 63, 2, _zoomPercent, null }, CultureInfo.InvariantCulture);
            _appliedZoomPercent = _zoomPercent;
        }
        catch (Exception ex) when (ex is TargetInvocationException or COMException or MissingMethodException or InvalidCastException or ArgumentException)
        {
            // Not ready for zooming yet; applied again when the page finishes loading.
        }
    }

    public override Task<int> FindAsync(string query)
    {
        if (!IsReady || _browser.Document is not HtmlDocument document)
        {
            return Task.FromResult(0);
        }

        try
        {
            object? result = document.InvokeScript("eval", new object[] { BuildFindScript(query) });
            return Task.FromResult(result is null ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or OverflowException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Task.FromResult(0);
        }
    }

    public override Task<string?> GetDeclaredFaviconUrlAsync()
    {
        try
        {
            if (!IsReady || _showingMessagePage || _browser.Document is not HtmlDocument document || _browser.Url is not Uri pageUri)
            {
                return Task.FromResult<string?>(null);
            }

            foreach (HtmlElement link in document.GetElementsByTagName("link"))
            {
                string relation = link.GetAttribute("rel") ?? string.Empty;
                string href = link.GetAttribute("href") ?? string.Empty;
                if (relation.Contains("icon", StringComparison.OrdinalIgnoreCase) &&
                    href.Length > 0 &&
                    Uri.TryCreate(pageUri, href, out Uri? icon) &&
                    IsWebAddress(icon.AbsoluteUri))
                {
                    return Task.FromResult<string?>(icon.AbsoluteUri);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or UriFormatException)
        {
            // Treated as having no declared icon.
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>Why the engine may not open <paramref name="url"/>, or null when it may (it is asked for every frame).</summary>
    private string? CheckNavigation(string url)
    {
        if (url.Length == 0 ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            // about:blank and in-page scripts stay inside the page.
            return null;
        }

        if (!IsWebAddress(url))
        {
            // file:, res:, mailto: and every other kind of address would open local files or other programs.
            return "only http:// and https:// addresses can be opened";
        }

        return Host.IsBlocked(url, out string blockedBy) ? $"it is blocked by {blockedBy}" : null;
    }

    private void OnNavigationBlocked(object? sender, LegacyBrowserEventArgs e)
    {
        Host.Log($"Legacy did not open {TrimForLog(e.Url)}: {e.Detail}.");
        if (e.IsTopLevel && IsWebAddress(e.Url))
        {
            string url = e.Url;
            string reason = e.Detail;
            PostToUi(() => ShowMessagePage(url, "This page was not opened", $"PhantomHaze did not open this page: {reason}."));
        }
    }

    private void OnTopLevelNavigationStarting(object? sender, LegacyBrowserEventArgs e)
    {
        if (_showingMessagePage && e.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _showingMessagePage = false;
        OnNavigationStarting(e.Url);
    }

    private void OnTopLevelNavigated(object? sender, LegacyBrowserEventArgs e)
    {
        if (_showingMessagePage)
        {
            return;
        }

        if (e.Url.Length > 0)
        {
            _currentUrl = e.Url;
        }

        OnSourceChanged();
    }

    private void OnTopLevelDocumentCompleted(object? sender, LegacyBrowserEventArgs e)
    {
        if (_showingMessagePage)
        {
            return;
        }

        if (e.Url.Length > 0)
        {
            _currentUrl = e.Url;
        }

        _title = SafeDocumentTitle();
        _appliedZoomPercent = 100;
        ApplyZoom();
        OnTitleChanged();
        OnNavigationCompleted(isSuccess: true, errorDescription: null);
    }

    private void OnTopLevelNavigationFailed(object? sender, LegacyBrowserEventArgs e)
    {
        // Internet Explorer's error pages offer to run Windows tools, so PhantomHaze shows its own message instead.
        string url = e.Url;
        string reason = e.Detail;
        Host.Log($"Legacy could not open {TrimForLog(url)}: {reason}.");
        PostToUi(() => ShowMessagePage(url, "This page could not be opened", reason));
    }

    private void OnBrowserNewWindowRequested(object? sender, LegacyBrowserEventArgs e)
    {
        bool fromUser = (e.Flags & NewWindowFromUser) != 0;
        if (IsWebAddress(e.Url) && fromUser)
        {
            OnNewWindowRequested(e.Url, isUserInitiated: true);
            return;
        }

        Host.Log(e.Url.Length == 0
            ? "Legacy blocked a page from opening a new window."
            : $"Legacy blocked a page from opening a new window: {TrimForLog(e.Url)}");
    }

    private void OnDownloadBlocked(object? sender, LegacyBrowserEventArgs e)
    {
        Host.Log("The Legacy protocol does not download files; a download was cancelled.");
    }

    private void OnFindShortcutPressed(object? sender, EventArgs e) => OnFindRequested();

    private void OnDocumentTitleChanged(object? sender, EventArgs e)
    {
        if (_showingMessagePage)
        {
            return;
        }

        _title = SafeDocumentTitle();
        OnTitleChanged();
    }

    private void OnCanGoBackChanged(object? sender, EventArgs e) => OnHistoryChanged();

    private string SafeDocumentTitle()
    {
        try
        {
            return _browser.DocumentTitle ?? string.Empty;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private void ShowMessagePage(string url, string title, string message)
    {
        if (IsDisposed)
        {
            return;
        }

        _showingMessagePage = true;
        _currentUrl = url;
        _title = title;
        _browser.DocumentText =
            "<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><title>" + WebUtility.HtmlEncode(title) + "</title></head>" +
            "<body style=\"font-family: 'Segoe UI', sans-serif; font-size: 10pt; color: #222; margin: 32px\">" +
            $"<h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p>" +
            $"<p style=\"color: #666\">{WebUtility.HtmlEncode(url)}</p></body></html>";
        OnSourceChanged();
        OnTitleChanged();
        OnNavigationCompleted(isSuccess: false, errorDescription: message);
    }

    private void PostToUi(Action action)
    {
        try
        {
            // Not from inside the engine's own event: it is still in the middle of a navigation.
            _browser.BeginInvoke(new Action(() =>
            {
                if (!IsDisposed)
                {
                    action();
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The tab is closing.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        Host.SetUiThreadWindowGuard(this, enabled: false);
        _browser.NavigationBlocked -= OnNavigationBlocked;
        _browser.TopLevelNavigationStarting -= OnTopLevelNavigationStarting;
        _browser.TopLevelNavigated -= OnTopLevelNavigated;
        _browser.TopLevelDocumentCompleted -= OnTopLevelDocumentCompleted;
        _browser.TopLevelNavigationFailed -= OnTopLevelNavigationFailed;
        _browser.NewWindowRequested -= OnBrowserNewWindowRequested;
        _browser.DownloadBlocked -= OnDownloadBlocked;
        _browser.FindShortcutPressed -= OnFindShortcutPressed;
        _browser.DocumentTitleChanged -= OnDocumentTitleChanged;
        _browser.CanGoBackChanged -= OnCanGoBackChanged;
        try
        {
            _browser.Dispose();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // Best effort cleanup only.
        }
    }

    /// <summary>Find in page for Internet Explorer: the same highlighting as WebView2, written for its JavaScript (ES5).</summary>
    private static string BuildFindScript(string query)
    {
        string encodedQuery = JsonSerializer.Serialize(query);
        return """
(function (query) {
  var markerAttr = "data-ph-find";
  var doc = document;
  var i;
  if (doc.querySelectorAll) {
    var existing = doc.querySelectorAll("mark[" + markerAttr + "]");
    for (i = existing.length - 1; i >= 0; i--) {
      var mark = existing[i];
      var markParent = mark.parentNode;
      if (!markParent) { continue; }
      markParent.replaceChild(doc.createTextNode(mark.textContent || ""), mark);
      if (markParent.normalize) { markParent.normalize(); }
    }
  }
  if (!doc.body || !doc.createTreeWalker) { return 0; }
  var needle = String(query || "").replace(/^\s+|\s+$/g, "").toLowerCase();
  if (!needle) { return 0; }
  var blocked = { SCRIPT: true, STYLE: true, NOSCRIPT: true, TEXTAREA: true, INPUT: true, MARK: true };
  var filter = function (node) {
    if (!node.nodeValue || !/\S/.test(node.nodeValue)) { return 2; }
    var parent = node.parentNode;
    if (!parent || blocked[parent.nodeName]) { return 2; }
    return 1;
  };
  filter.acceptNode = filter;
  var walker = doc.createTreeWalker(doc.body, 4, filter, false);
  var nodes = [];
  while (walker.nextNode()) { nodes.push(walker.currentNode); }
  var count = 0;
  for (i = 0; i < nodes.length; i++) {
    var node = nodes[i];
    var original = node.nodeValue;
    var lower = original.toLowerCase();
    var cursor = 0;
    var index = lower.indexOf(needle);
    if (index < 0) { continue; }
    var fragment = doc.createDocumentFragment();
    while (index >= 0) {
      if (index > cursor) { fragment.appendChild(doc.createTextNode(original.substring(cursor, index))); }
      var highlight = doc.createElement("mark");
      highlight.setAttribute(markerAttr, "1");
      highlight.style.backgroundColor = "#ffe07d";
      highlight.style.color = "#111111";
      highlight.appendChild(doc.createTextNode(original.substring(index, index + needle.length)));
      fragment.appendChild(highlight);
      cursor = index + needle.length;
      count++;
      index = lower.indexOf(needle, cursor);
    }
    if (cursor < original.length) { fragment.appendChild(doc.createTextNode(original.substring(cursor))); }
    node.parentNode.replaceChild(fragment, node);
  }
  return count;
})(
""" + encodedQuery + ")";
    }
}

internal sealed class LegacyBrowserEventArgs : EventArgs
{
    public LegacyBrowserEventArgs(string url, string detail = "", bool isTopLevel = true, uint flags = 0)
    {
        Url = url;
        Detail = detail;
        IsTopLevel = isTopLevel;
        Flags = flags;
    }

    public string Url { get; }
    public string Detail { get; }
    public bool IsTopLevel { get; }
    public uint Flags { get; }
}

/// <summary>
/// The WebBrowser control with what the Legacy protocol needs on top of it: top-level navigation, error and new-window
/// events with addresses (DWebBrowserEvents2), cancelled file downloads, a navigation filter applied to every frame, and
/// keyboard shortcuts that would open Internet Explorer's own windows (Find, Print, New window, Open, Save as) kept from
/// the engine.
/// </summary>
internal sealed class PhantomLegacyBrowser : WebBrowser
{
    private AxHost.ConnectionPointCookie? _eventCookie;

    /// <summary>Returns why an address may not be opened, or null when it may.</summary>
    public Func<string, string?>? NavigationFilter { get; set; }

    /// <summary>True while PhantomHaze receives the engine's own events (top-level navigation, new windows, downloads).</summary>
    public bool EventsConnected => _eventCookie is not null;

    public event EventHandler<LegacyBrowserEventArgs>? NavigationBlocked;
    public event EventHandler<LegacyBrowserEventArgs>? TopLevelNavigationStarting;
    public event EventHandler<LegacyBrowserEventArgs>? TopLevelNavigated;
    public event EventHandler<LegacyBrowserEventArgs>? TopLevelDocumentCompleted;
    public event EventHandler<LegacyBrowserEventArgs>? TopLevelNavigationFailed;
    public event EventHandler<LegacyBrowserEventArgs>? NewWindowRequested;
    public event EventHandler<LegacyBrowserEventArgs>? DownloadBlocked;
    public event EventHandler? FindShortcutPressed;

    protected override void CreateSink()
    {
        base.CreateSink();
        if (ActiveXInstance is not object browser)
        {
            return;
        }

        try
        {
            _eventCookie = new AxHost.ConnectionPointCookie(browser, new EventSink(this), typeof(IWebBrowserEvents));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or InvalidOperationException)
        {
            _eventCookie = null;
        }
    }

    protected override void DetachSink()
    {
        try
        {
            _eventCookie?.Disconnect();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The engine is already gone.
        }

        _eventCookie = null;
        base.DetachSink();
    }

    private bool IsTopLevel(object? frame) => frame is not null && ReferenceEquals(frame, ActiveXInstance);

    protected override void OnNavigating(WebBrowserNavigatingEventArgs e)
    {
        // Every frame's navigation passes the filter here too, so a navigation is never allowed just because the
        // engine's own event could not be read.
        string url = e.Url?.OriginalString ?? string.Empty;
        string? reason = NavigationFilter?.Invoke(url);
        if (reason is not null)
        {
            e.Cancel = true;
            if (!EventsConnected)
            {
                NavigationBlocked?.Invoke(this, new LegacyBrowserEventArgs(url, reason, isTopLevel: false));
            }

            return;
        }

        base.OnNavigating(e);
    }

    protected override void OnNewWindow(System.ComponentModel.CancelEventArgs e)
    {
        // Internet Explorer would open the window as a separate program, outside PhantomHaze and its capture protection.
        // With the engine's events connected, NewWindow3 reports the address and PhantomHaze opens it as a tab.
        e.Cancel = true;
        if (!EventsConnected)
        {
            NewWindowRequested?.Invoke(this, new LegacyBrowserEventArgs(string.Empty));
        }

        base.OnNewWindow(e);
    }

    protected override void OnNavigated(WebBrowserNavigatedEventArgs e)
    {
        base.OnNavigated(e);
        if (!EventsConnected && e.Url == Url)
        {
            TopLevelNavigated?.Invoke(this, new LegacyBrowserEventArgs(e.Url?.ToString() ?? string.Empty));
        }
    }

    protected override void OnDocumentCompleted(WebBrowserDocumentCompletedEventArgs e)
    {
        base.OnDocumentCompleted(e);
        if (!EventsConnected && ReadyState == WebBrowserReadyState.Complete)
        {
            TopLevelDocumentCompleted?.Invoke(this, new LegacyBrowserEventArgs(Url?.ToString() ?? string.Empty));
        }
    }

    public override bool PreProcessMessage(ref Message msg)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        if (msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            Keys key = (Keys)(int)(msg.WParam.ToInt64() & 0xFFFF);
            Keys modifiers = ModifierKeys;
            if (modifiers == Keys.Control && key == Keys.F)
            {
                FindShortcutPressed?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (OpensEngineWindow(key, modifiers))
            {
                return true;
            }
        }

        return base.PreProcessMessage(ref msg);
    }

    /// <summary>
    /// Shortcuts that would make Internet Explorer open a window of its own (new window, Open, Print, Save as, downloads,
    /// favourites, history, help, developer tools) or go to its home page. Editing shortcuts (Ctrl+B, Ctrl+I...) still work.
    /// </summary>
    private static bool OpensEngineWindow(Keys key, Keys modifiers)
    {
        if (modifiers == Keys.Control)
        {
            return key is Keys.N or Keys.O or Keys.L or Keys.P or Keys.S or Keys.J or Keys.D or Keys.H or Keys.T or Keys.K;
        }

        if (modifiers == (Keys.Control | Keys.Shift))
        {
            return key is Keys.Delete or Keys.P or Keys.H or Keys.J;
        }

        if (modifiers == Keys.Alt)
        {
            return key == Keys.Home;
        }

        return modifiers == Keys.None && key is Keys.F1 or Keys.F3 or Keys.F12;
    }

    /// <summary>The engine's DWebBrowserEvents2 events PhantomHaze listens to (by DISPID; the others are ignored).</summary>
    [ComImport]
    [Guid("34A715A0-6587-11D0-924A-0020AFC7AC4D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    [TypeLibType(TypeLibTypeFlags.FHidden)]
    internal interface IWebBrowserEvents
    {
        [DispId(250)]
        void BeforeNavigate2(
            [In, MarshalAs(UnmanagedType.IDispatch)] object pDisp,
            [In] ref object url,
            [In] ref object flags,
            [In] ref object targetFrameName,
            [In] ref object postData,
            [In] ref object headers,
            [In, Out] ref bool cancel);

        [DispId(252)]
        void NavigateComplete2([In, MarshalAs(UnmanagedType.IDispatch)] object pDisp, [In] ref object url);

        [DispId(259)]
        void DocumentComplete([In, MarshalAs(UnmanagedType.IDispatch)] object pDisp, [In] ref object url);

        [DispId(270)]
        void FileDownload([In] object activeDocument, [In, Out] ref bool cancel);

        [DispId(271)]
        void NavigateError(
            [In, MarshalAs(UnmanagedType.IDispatch)] object pDisp,
            [In] ref object url,
            [In] ref object frame,
            [In] ref object statusCode,
            [In, Out] ref bool cancel);

        [DispId(273)]
        void NewWindow3(
            [In, Out, MarshalAs(UnmanagedType.IDispatch)] ref object ppDisp,
            [In, Out] ref bool cancel,
            [In] object flags,
            [In] object urlContext,
            [In] object url);
    }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private sealed class EventSink : StandardOleMarshalObject, IWebBrowserEvents
    {
        private readonly PhantomLegacyBrowser _owner;

        public EventSink(PhantomLegacyBrowser owner)
        {
            _owner = owner;
        }

        public void BeforeNavigate2(object pDisp, ref object url, ref object flags, ref object targetFrameName, ref object postData, ref object headers, ref bool cancel)
        {
            try
            {
                string address = url as string ?? string.Empty;
                bool topLevel = _owner.IsTopLevel(pDisp);
                string? reason = _owner.NavigationFilter?.Invoke(address);
                if (reason is not null)
                {
                    cancel = true;
                    _owner.NavigationBlocked?.Invoke(_owner, new LegacyBrowserEventArgs(address, reason, topLevel));
                    return;
                }

                if (topLevel)
                {
                    _owner.TopLevelNavigationStarting?.Invoke(_owner, new LegacyBrowserEventArgs(address));
                }
            }
            catch (Exception)
            {
                // An exception must never cross back into the engine; when in doubt the navigation is stopped.
                cancel = true;
            }
        }

        public void NavigateComplete2(object pDisp, ref object url)
        {
            try
            {
                if (_owner.IsTopLevel(pDisp))
                {
                    _owner.TopLevelNavigated?.Invoke(_owner, new LegacyBrowserEventArgs(url as string ?? string.Empty));
                }
            }
            catch (Exception)
            {
                // Never crosses back into the engine.
            }
        }

        public void DocumentComplete(object pDisp, ref object url)
        {
            try
            {
                if (_owner.IsTopLevel(pDisp))
                {
                    _owner.TopLevelDocumentCompleted?.Invoke(_owner, new LegacyBrowserEventArgs(url as string ?? string.Empty));
                }
            }
            catch (Exception)
            {
                // Never crosses back into the engine.
            }
        }

        public void FileDownload(object activeDocument, ref bool cancel)
        {
            try
            {
                // activeDocument is true for pages Internet Explorer shows itself; anything else is a file download.
                if (activeDocument is not true)
                {
                    cancel = true;
                    _owner.DownloadBlocked?.Invoke(_owner, new LegacyBrowserEventArgs(string.Empty));
                }
            }
            catch (Exception)
            {
                cancel = true;
            }
        }

        public void NavigateError(object pDisp, ref object url, ref object frame, ref object statusCode, ref bool cancel)
        {
            try
            {
                // Internet Explorer's error pages (res://ieframe.dll) offer to run Windows tools, so they are never shown.
                cancel = true;
                if (_owner.IsTopLevel(pDisp))
                {
                    string address = url as string ?? string.Empty;
                    _owner.TopLevelNavigationFailed?.Invoke(_owner, new LegacyBrowserEventArgs(address, DescribeError(statusCode)));
                }
            }
            catch (Exception)
            {
                cancel = true;
            }
        }

        public void NewWindow3(ref object ppDisp, ref bool cancel, object flags, object urlContext, object url)
        {
            // Always cancelled: Internet Explorer would open the window as a separate program. PhantomHaze opens the
            // address in a new protected tab instead.
            cancel = true;
            try
            {
                uint newWindowFlags = flags switch
                {
                    int value => unchecked((uint)value),
                    uint value => value,
                    _ => 0,
                };
                _owner.NewWindowRequested?.Invoke(_owner, new LegacyBrowserEventArgs(url as string ?? string.Empty, flags: newWindowFlags));
            }
            catch (Exception)
            {
                // Never crosses back into the engine.
            }
        }

        private static string DescribeError(object statusCode)
        {
            int code;
            try
            {
                code = Convert.ToInt32(statusCode, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return "the page could not be loaded";
            }

            if (code is >= 400 and < 600)
            {
                return $"the server answered with HTTP {code}";
            }

            return unchecked((uint)code) switch
            {
                0x800C0005 => "the server could not be found",
                0x800C0007 => "the server could not be reached",
                0x800C000B => "the connection timed out",
                0x800C0019 or 0x800C0016 => "the server's security certificate is not valid",
                0x800C0008 => "the page could not be downloaded",
                0x800C000E => "a secure connection could not be made",
                0x800C0004 => "the connection was interrupted",
                _ => $"the page could not be loaded (error 0x{unchecked((uint)code):X8})",
            };
        }
    }
}
