using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

/// <summary>The default protocol: Microsoft Edge WebView2 (Chromium).</summary>
internal sealed class WebView2WebProtocol : WebProtocol
{
    public WebView2WebProtocol(string? proxyServer = null)
    {
        ProxyServer = proxyServer;
    }

    /// <summary>The proxy server of an extension protocol (it then gets its own WebView2 environment), or null.</summary>
    public string? ProxyServer { get; }

    public override string Id => WebProtocolCatalog.WebView2Id;

    public override string DisplayName => "WebView2";

    public override string Description =>
        "Microsoft Edge WebView2 (Chromium), the default. Full modern web support: JavaScript, extensions' web scripts and " +
        "styles, zoom, find in page and saved cookies.";

    public override WebProtocolFeatures Features =>
        WebProtocolFeatures.Zoom | WebProtocolFeatures.FindInPage | WebProtocolFeatures.ExtensionScripts |
        WebProtocolFeatures.JavaScript | WebProtocolFeatures.SavedCookies;

    public override string? GetUnavailableReason()
    {
        try
        {
            string? version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(version) ? "The Microsoft Edge WebView2 Runtime is not installed." : null;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or DllNotFoundException or InvalidOperationException)
        {
            return "The Microsoft Edge WebView2 Runtime is not installed.";
        }
    }

    public override WebProtocolPage CreatePage(IWebProtocolHost host) => new WebView2ProtocolPage(this, host, ProxyServer);
}

/// <summary>A tab shown with WebView2.</summary>
internal sealed partial class WebView2ProtocolPage : WebProtocolPage
{
    private const string FindShortcutMessage = "__PH_FIND__";

    private readonly WebView2 _webView;
    private readonly string? _proxyServer;
    private CoreWebView2? _core;
    private bool _hasFaviconEvents;
    private int _zoomPercent = 100;

    public WebView2ProtocolPage(WebProtocol protocol, IWebProtocolHost host, string? proxyServer)
        : base(protocol, host)
    {
        _proxyServer = proxyServer;
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false,
        };
    }

    public override Control View => _webView;

    /// <summary>The CoreWebView2 once the page is ready (null before that and after the page is disposed).</summary>
    public CoreWebView2? Core => IsDisposed ? null : _core;

    /// <summary>True when the page runs in a proxy protocol's own WebView2 environment (with its own cookie store).</summary>
    public bool UsesIsolatedEnvironment => _proxyServer is not null;

    public override bool IsReady => !IsDisposed && _core is not null;

    public override string CurrentUrl
    {
        get
        {
            if (IsDisposed)
            {
                return string.Empty;
            }

            return _webView.Source?.ToString() ?? _core?.Source ?? string.Empty;
        }
    }

    // CoreWebView2.Source is the browser's own serialisation of the address (what location.href reports).
    public override string DocumentUrl => Core?.Source ?? CurrentUrl;

    public override string Title => Core?.DocumentTitle ?? string.Empty;

    public override bool DownloadsOwnFavicon => true;
    public override async Task<Image?> DownloadFaviconAsync()
    {
        if (Core is not { } core) return null;
        try
        {
            using Stream? icon = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (icon is null || icon.Length == 0) return null;
            using var bitmap = new Bitmap(icon);
            return new Bitmap(bitmap);
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidOperationException or COMException or ArgumentException)
        {
            return null;
        }
    }

    private void OnCoreFaviconChanged(object? sender, object e) => OnFaviconChanged();

    public override async Task<string?> ReadHtmlAsync()
    {
        if (Core is not { } core) return null;
        string result = await core.ExecuteScriptAsync(
            "document.documentElement && ((document.doctype ? new XMLSerializer().serializeToString(document.doctype) + '\\n' : '') + document.documentElement.outerHTML)");
        return JsonSerializer.Deserialize<string?>(result);
    }

    public override void NavigateRequest(PageRequest request)
    {
        if (Core is not { } core) return;
        if (request.Method == "GET") { Navigate(request.Url); return; }
        var resource = core.Environment.CreateWebResourceRequest(request.Url, request.Method,
            new MemoryStream(request.Body ?? []), "Content-Type: " + request.ContentType);
        core.NavigateWithWebResourceRequest(resource);
    }

    public override bool CanGoBack => Core?.CanGoBack == true;

    public override bool CanGoForward => Core?.CanGoForward == true;

    public override async Task InitializeAsync()
    {
        CoreWebView2Environment environment = await Host.GetWebView2EnvironmentAsync(_proxyServer);

        // The tab (or the whole app) can be closed while WebView2 is still starting up.
        if (IsDisposed)
        {
            return;
        }

        await _webView.EnsureCoreWebView2Async(environment);
        if (IsDisposed || _webView.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NavigationStarting += OnCoreNavigationStarting;
        core.NavigationCompleted += OnCoreNavigationCompleted;
        core.SourceChanged += OnCoreSourceChanged;
        core.DocumentTitleChanged += OnCoreDocumentTitleChanged;
        try
        {
            core.FaviconChanged += OnCoreFaviconChanged;
            _hasFaviconEvents = true;
        }
        catch (NotImplementedException) { /* Older runtimes use the existing icon lookup. */ }
        core.HistoryChanged += OnCoreHistoryChanged;
        core.NewWindowRequested += OnCoreNewWindowRequested;
        core.WebMessageReceived += OnCoreWebMessageReceived;

        // Ctrl+F pressed inside a page never reaches the window, so the page reports it.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildFindShortcutBridgeScript());
        if (IsDisposed)
        {
            return;
        }

        await Host.OnWebView2CoreCreatedAsync(this, core);
        if (IsDisposed)
        {
            return;
        }

        _core = core;
        _webView.ZoomFactor = _zoomPercent / 100.0;
    }

    public override void Navigate(string url)
    {
        if (Core is not CoreWebView2 core)
        {
            Host.Log("A page was opened before WebView2 was ready.");
            return;
        }

        core.Navigate(url);
    }

    public override void GoBack()
    {
        if (Core is CoreWebView2 core && core.CanGoBack)
        {
            core.GoBack();
        }
    }

    public override void GoForward()
    {
        if (Core is CoreWebView2 core && core.CanGoForward)
        {
            core.GoForward();
        }
    }

    public override void Reload()
    {
        if (Core is not null)
        {
            _webView.Reload();
        }
    }

    public override void SetZoom(int percent)
    {
        _zoomPercent = percent;
        if (Core is not null)
        {
            _webView.ZoomFactor = percent / 100.0;
        }
    }

    public override async Task<int> FindAsync(string query)
    {
        if (Core is not CoreWebView2 core)
        {
            return 0;
        }

        try
        {
            string result = await core.ExecuteScriptAsync(BuildFindHighlightScript(query));
            return ParseScriptInt(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException or ArgumentException)
        {
            return 0;
        }
    }

    public override async Task<string?> GetDeclaredFaviconUrlAsync()
    {
        if (Core is not CoreWebView2 core)
        {
            return null;
        }

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
            string? url = JsonSerializer.Deserialize<string>(raw);
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public override async Task<string?> ExecuteScriptAsync(string script)
    {
        return Core is CoreWebView2 core ? await core.ExecuteScriptAsync(script) : null;
    }

    public override bool CanPauseRendering => true;

    public override void SetRenderingActive(bool active)
    {
        if (Core is not CoreWebView2 core)
        {
            return;
        }

        if (active)
        {
            core.Resume();
        }
        else
        {
            _ = SuspendQuietlyAsync(core);
        }
    }

    private static async Task SuspendQuietlyAsync(CoreWebView2 core)
    {
        try
        {
            await core.TrySuspendAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // Suspending is only an optimisation; a page that cannot be suspended keeps running.
        }
    }

    private void OnCoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) => OnNavigationStarting(e.Uri);

    private void OnCoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        OnNavigationCompleted(e.IsSuccess, e.IsSuccess ? null : e.WebErrorStatus.ToString());
    }

    private void OnCoreSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => OnSourceChanged();

    private void OnCoreDocumentTitleChanged(object? sender, object e) => OnTitleChanged();

    private void OnCoreHistoryChanged(object? sender, object e) => OnHistoryChanged();

    private void OnCoreNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never let WebView2 open a popup window of its own: it would be a top-level window that is not excluded from
        // screen capture. PhantomHaze opens the address in a new protected tab instead.
        e.Handled = true;
        OnNewWindowRequested(e.Uri, e.IsUserInitiated);
    }

    private void OnCoreWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            return;
        }

        if (message.Equals(FindShortcutMessage, StringComparison.Ordinal))
        {
            OnFindRequested();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        if (_webView.CoreWebView2 is CoreWebView2 core)
        {
            try
            {
                core.NavigationStarting -= OnCoreNavigationStarting;
                core.NavigationCompleted -= OnCoreNavigationCompleted;
                core.SourceChanged -= OnCoreSourceChanged;
                core.DocumentTitleChanged -= OnCoreDocumentTitleChanged;
                if (_hasFaviconEvents) core.FaviconChanged -= OnCoreFaviconChanged;
                core.HistoryChanged -= OnCoreHistoryChanged;
                core.NewWindowRequested -= OnCoreNewWindowRequested;
                core.WebMessageReceived -= OnCoreWebMessageReceived;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The browser process is already gone.
            }
        }

        _core = null;
        try
        {
            _webView.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // Best effort cleanup only.
        }
    }

    private static int ParseScriptInt(string rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
        {
            return 0;
        }

        string candidate = rawResult.Trim().Trim('"');
        return int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
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
}
