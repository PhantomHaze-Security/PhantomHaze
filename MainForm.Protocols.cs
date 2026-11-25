using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Web View protocols in the main window: which protocol tabs use, the services protocol pages get from PhantomHaze
// (IWebProtocolHost), WebView2 environments, and reopening tabs when the protocol changes. The protocols themselves are
// in WebProtocols.cs, WebView2Protocol.cs, HtmlRendererProtocol.cs and LegacyProtocol.cs.
public partial class MainForm
{
    /// <summary>What protocol pages use from the main window.</summary>
    private sealed class ProtocolHost : IWebProtocolHost
    {
        private readonly MainForm _form;

        public ProtocolHost(MainForm form)
        {
            _form = form;
        }

        public string SessionDirectory => _form._webSessionPath;

        public void Log(string message) => _form.Log(message);

        public bool IsBlocked(string url, out string blockedBy)
        {
            if (_form.TryGetBlockingExtensionRule(url, out LoadedExtension? extension, out PhantomExtensionWebBlockDefinition? rule))
            {
                blockedBy = $"the extension {NormalizeForLog(extension!.Definition.Name)} ({rule!.Id})";
                return true;
            }

            blockedBy = string.Empty;
            return false;
        }

        public IReadOnlyList<string> GetExtensionStyleSheets(string url) => _form.GetExtensionStyleSheets(url);

        public WebRequestRules GetRequestRules() => _form.BuildRequestRules();

        public Task<CoreWebView2Environment> GetWebView2EnvironmentAsync(string? proxyServer) => _form.GetWebView2EnvironmentAsync(proxyServer);

        public Task OnWebView2CoreCreatedAsync(WebView2ProtocolPage page, CoreWebView2 core) => _form.OnWebView2CoreCreatedAsync(page, core);

        public void SetUiThreadWindowGuard(object owner, bool enabled) => _form.SetUiThreadWindowGuard(owner, enabled);
    }

    private ProtocolHost? _protocolHost;
    private Task<CoreWebView2Environment>? _defaultEnvironmentTask;
    private readonly Dictionary<string, Task<CoreWebView2Environment>> _proxyEnvironmentTasks = new(StringComparer.OrdinalIgnoreCase);

    // Each WebView2 environment starts from an empty web session (and, for the shared one, the saved cookies) once.
    private readonly Dictionary<CoreWebView2Environment, Task> _preparedEnvironments = new();

    // Per environment, the one CoreWebView2 that carries the service/shared worker request filter.
    private readonly Dictionary<CoreWebView2Environment, CoreWebView2> _workerRequestFilterCores = new();

    private readonly HashSet<string> _reportedProtocolFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private bool _protocolSwitchInProgress;
    private bool _protocolSwitchPending;

    // The Legacy protocol: every window the Internet Explorer engine opens on the UI thread is excluded from capture.
    private readonly HashSet<object> _uiThreadWindowGuardOwners = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IntPtr> _guardedUiThreadWindows = new();
    private NativeMethods.ThreadWindowHook? _uiThreadWindowGuard;

    private ProtocolHost ProtocolHostInstance => _protocolHost ??= new ProtocolHost(this);

    // ---- Choosing a protocol -------------------------------------------------------------------------------------

    /// <summary>The built-in protocols followed by the protocols enabled extensions add (protocol.add).</summary>
    private List<WebProtocol> GetAvailableWebProtocols()
    {
        BuiltInProtocolSettings settings = GetBuiltInProtocolSettings(_settings);
        var protocols = new List<WebProtocol>(WebProtocolCatalog.CreateBuiltIn(settings));
        foreach (LoadedExtension extension in GetEnabledExtensions())
        {
            foreach (PhantomExtensionProtocolDefinition definition in extension.Definition.Protocols)
            {
                // Setting values in the address are percent-encoded, so they can never change which server is used.
                string template = ResolveExtensionPlaceholders(definition.AddressTemplate, extension, PhantomExtensionPlaceholderContext.UrlComponent);
                protocols.Add(new ExtensionWebProtocol(extension.Definition.Id, extension.Definition.Name, definition, template, settings));
            }
        }

        return protocols;
    }

    /// <summary>What the built-in protocols are configured with (HazeGate's connection is built in: HazeGateConnection).</summary>
    private static BuiltInProtocolSettings GetBuiltInProtocolSettings(AppSettings settings)
    {
        return new BuiltInProtocolSettings(settings.RenderedPageScriptsEnabled);
    }

    /// <summary>
    /// The protocol new tabs use: the one chosen in Settings, or WebView2 when that one is not available (its extension
    /// was turned off, or it cannot run on this PC).
    /// </summary>
    private WebProtocol ResolveSelectedWebProtocol()
    {
        string id = _settings.WebViewProtocol;
        if (id.Equals(WebProtocolCatalog.WebView2Id, StringComparison.OrdinalIgnoreCase))
        {
            return WebProtocolCatalog.Default;
        }

        WebProtocol? protocol = GetAvailableWebProtocols().FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        string? problem = protocol is null
            ? "it is not available (the extension that adds it may be turned off or uninstalled)"
            : protocol.GetUnavailableReason();
        if (protocol is not null && problem is null)
        {
            return protocol;
        }

        if (_reportedProtocolFallbacks.Add($"{id}\n{problem}"))
        {
            Log($"The Web View protocol \"{NormalizeForLog(protocol?.DisplayName ?? id)}\" cannot be used, so WebView2 is used instead: {NormalizeForLog(problem)}");
        }

        return WebProtocolCatalog.Default;
    }

    private void LogSelectedWebViewProtocol()
    {
        WebProtocol protocol = ResolveSelectedWebProtocol();
        Log($"Web View protocol: {NormalizeForLog(protocol.DisplayName)}.");
    }

    // ---- Pages and tabs ------------------------------------------------------------------------------------------

    /// <summary>
    /// Connects a page's events to its tab. Events from a page that no longer belongs to the tab (it was replaced or the
    /// tab closed) are ignored.
    /// </summary>
    private void AttachPageToTab(BrowserTab tab, WebProtocolPage page)
    {
        bool IsCurrent(object? sender) => !IsDisposed && ReferenceEquals(tab.Page, sender) && _tabs.Contains(tab);

        page.NavigationStarting += (sender, e) =>
        {
            if (IsCurrent(sender))
            {
                OnPageNavigationStarting(tab, e);
            }
        };
        page.NavigationCompleted += (sender, e) =>
        {
            if (IsCurrent(sender))
            {
                OnPageNavigationCompleted(tab, e);
            }
        };
        page.SourceChanged += (sender, _) =>
        {
            if (IsCurrent(sender))
            {
                OnPageSourceChanged(tab);
            }
        };
        page.TitleChanged += (sender, _) =>
        {
            if (IsCurrent(sender))
            {
                OnPageTitleChanged(tab);
            }
        };
        page.HistoryChanged += (sender, _) =>
        {
            if (IsCurrent(sender))
            {
                OnPageHistoryChanged(tab);
            }
        };
        page.NewWindowRequested += (sender, e) =>
        {
            if (IsCurrent(sender))
            {
                OnPageNewWindowRequested(tab, e);
            }
        };
        page.FindRequested += (sender, _) =>
        {
            if (IsCurrent(sender))
            {
                OnPageFindRequested(tab);
            }
        };
    }

    /// <summary>
    /// Removes a page's control from Web View and undoes the WebView2-only hooks of a WebView2 page. Returns the page's
    /// WebView2 environment (null for other protocols), for <see cref="OnWebView2PageRemoved"/>.
    /// </summary>
    private CoreWebView2Environment? DetachPageFromTab(WebProtocolPage page)
    {
        CoreWebView2Environment? environment = null;
        if (page.InnermostPage is WebView2ProtocolPage { Core: CoreWebView2 core })
        {
            try
            {
                core.WebResourceRequested -= OnWebResourceRequested;
                core.WebResourceResponseReceived -= OnWebResourceResponseReceived;
                environment = core.Environment;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The browser process is already gone.
            }

            _tabByCore.Remove(core);
            if (environment is not null &&
                _workerRequestFilterCores.TryGetValue(environment, out CoreWebView2? filterCore) &&
                ReferenceEquals(filterCore, core))
            {
                _workerRequestFilterCores.Remove(environment);
            }
        }

        _webViewHostPanel.Controls.Remove(page.View);
        return environment;
    }

    /// <summary>
    /// Runs after a WebView2 page left the tab list: another tab of the same environment takes over the worker request
    /// filter, and an environment without tabs is prepared again (empty session, saved cookies) when it is next used,
    /// because its browser process (with the session's cookies in memory) closes when its last tab does.
    /// </summary>
    private void OnWebView2PageRemoved(CoreWebView2Environment? environment)
    {
        if (environment is null)
        {
            return;
        }

        bool stillUsed = false;
        foreach (BrowserTab remaining in _tabs)
        {
            if (remaining.Page.InnermostPage is not WebView2ProtocolPage { Core: CoreWebView2 core })
            {
                continue;
            }

            try
            {
                if (!ReferenceEquals(core.Environment, environment))
                {
                    continue;
                }

                stillUsed = true;
                EnsureWorkerRequestFilter(core);
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // That tab is closing; try the next one.
            }
        }

        if (!stillUsed)
        {
            _preparedEnvironments.Remove(environment);
        }
    }

    // ---- WebView2 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shared WebView2 environment (tabs, extension windows and pages) or, for an extension protocol with a proxy
    /// server, a separate environment used only for that proxy (with its own cookie store, never saved).
    /// </summary>
    private Task<CoreWebView2Environment> GetWebView2EnvironmentAsync(string? proxyServer)
    {
        if (proxyServer is null)
        {
            if (_defaultEnvironmentTask is null || _defaultEnvironmentTask.IsFaulted || _defaultEnvironmentTask.IsCanceled)
            {
                _defaultEnvironmentTask = CreateDefaultWebView2EnvironmentAsync();
            }

            return _defaultEnvironmentTask;
        }

        if (!_proxyEnvironmentTasks.TryGetValue(proxyServer, out Task<CoreWebView2Environment>? task) || task.IsFaulted || task.IsCanceled)
        {
            task = CreateProxyWebView2EnvironmentAsync(proxyServer);
            _proxyEnvironmentTasks[proxyServer] = task;
        }

        return task;
    }

    private async Task<CoreWebView2Environment> CreateDefaultWebView2EnvironmentAsync()
    {
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _webSessionPath,
                options: new CoreWebView2EnvironmentOptions("--inprivate"));
            _webEnvironment = environment;
            Log("Web View environment initialized. Session data remains local to this run.");
            return environment;
        }
        catch (Exception ex)
        {
            Log($"Web View initialization failed: {ex.Message}");
            throw;
        }
    }

    private async Task<CoreWebView2Environment> CreateProxyWebView2EnvironmentAsync(string proxyServer)
    {
        // A different proxy needs different browser arguments, which WebView2 only accepts in another data folder.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proxyServer.ToLowerInvariant())), 0, 6).ToLowerInvariant();
        string folder = Path.Combine(_webSessionPath, $"proxy-{hash}");
        Directory.CreateDirectory(folder);

        // The proxy server was checked when the extension was imported (scheme://host:port, no spaces or quotes).
        string arguments = $"--inprivate --proxy-server={proxyServer}";
        if (!proxyServer.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
        {
            // Names are looked up by the proxy, never with this PC's own DNS server (SOCKS4 cannot look names up).
            string proxyHost = new Uri(proxyServer).Host.Trim('[', ']');
            arguments += $" --host-resolver-rules=\"MAP * ~NOTFOUND , EXCLUDE {proxyHost}\"";
        }

        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: folder,
                options: new CoreWebView2EnvironmentOptions(arguments));
            Log($"A separate Web View environment was started for the proxy server {proxyServer}.");
            return environment;
        }
        catch (Exception ex)
        {
            Log($"The Web View environment for the proxy server {proxyServer} could not be started: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The WebView2-only parts of a tab, set up before its first navigation: extension request blocking (pages, frames
    /// and workers), the cookie notices, and the empty session with the saved cookies.
    /// </summary>
    private async Task OnWebView2CoreCreatedAsync(WebView2ProtocolPage page, CoreWebView2 core)
    {
        AddWebResourceRequestedFilterForAllFrames(core);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebResourceResponseReceived += OnWebResourceResponseReceived;
        if (_tabs.FirstOrDefault(tab => ReferenceEquals(tab.Page.InnermostPage, page)) is BrowserTab owner)
        {
            _tabByCore[core] = owner;
        }

        EnsureWorkerRequestFilter(core);
        await PrepareWebView2SessionAsync(core, restoreSavedCookies: !page.UsesIsolatedEnvironment);
    }

    private Task PrepareWebView2SessionAsync(CoreWebView2 core, bool restoreSavedCookies)
    {
        CoreWebView2Environment environment = core.Environment;
        if (!_preparedEnvironments.TryGetValue(environment, out Task? preparation) || preparation.IsFaulted || preparation.IsCanceled)
        {
            preparation = PrepareAsync();
            _preparedEnvironments[environment] = preparation;
        }

        return preparation;

        async Task PrepareAsync()
        {
            // Each run starts from an empty web session...
            core.CookieManager.DeleteAllCookies();
            await core.Profile.ClearBrowsingDataAsync();

            // ...plus the cookies saved with Settings -> Save cookies across sessions.
            if (restoreSavedCookies)
            {
                RestoreSavedCookies(core.CookieManager);
            }
        }
    }

    /// <summary>
    /// Service and shared workers do not belong to one WebView: WebView2 raises their requests on every CoreWebView2 of
    /// the environment that has a matching filter. The worker filter is therefore kept on exactly one tab per
    /// environment, so web.block applies to worker requests without every tab handling (and logging) each of them.
    /// </summary>
    private void EnsureWorkerRequestFilter(CoreWebView2 candidate)
    {
        if (_workerRequestFilterUnavailable)
        {
            return;
        }

        CoreWebView2Environment environment;
        try
        {
            environment = candidate.Environment;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            return;
        }

        if (_workerRequestFilterCores.ContainsKey(environment))
        {
            return;
        }

        try
        {
            candidate.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.ServiceWorker | CoreWebView2WebResourceRequestSourceKinds.SharedWorker);
            _workerRequestFilterCores[environment] = candidate;
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or ArgumentException)
        {
            _workerRequestFilterUnavailable = true;
            Log("This WebView2 runtime cannot filter service or shared worker requests; extension blocking does not cover them.");
        }
    }

    /// <summary>A CoreWebView2 of the shared environment (whose cookies can be saved), or null when no such tab is open.</summary>
    private CoreWebView2? FindSharedWebView2Core()
    {
        foreach (BrowserTab tab in _tabs)
        {
            if (tab.Page.InnermostPage is WebView2ProtocolPage { UsesIsolatedEnvironment: false, Core: CoreWebView2 core })
            {
                return core;
            }
        }

        return null;
    }

    // ---- Extension styles for protocols that apply them themselves ----------------------------------------------

    /// <summary>A snapshot of the enabled extensions' web.block and web.css rules, usable from any thread.</summary>
    private WebRequestRules BuildRequestRules()
    {
        if (!_settings.ExtensionsEnabled)
        {
            return WebRequestRules.None;
        }

        List<LoadedExtension> extensions = GetEnabledExtensions().ToList();
        var blocks = new List<(string Pattern, string BlockedBy)>();
        foreach (LoadedExtension extension in extensions)
        {
            foreach (PhantomExtensionWebBlockDefinition rule in extension.Definition.WebBlocks)
            {
                blocks.Add((rule.MatchPattern, $"the extension {NormalizeForLog(extension.Definition.Name)} ({rule.Id})"));
            }
        }

        var styles = new List<(string Pattern, string Css)>();
        foreach (PhantomExtensionScriptTiming timing in new[] { PhantomExtensionScriptTiming.DocumentStart, PhantomExtensionScriptTiming.DocumentEnd })
        {
            foreach (LoadedExtension extension in extensions)
            {
                foreach (PhantomExtensionWebScriptDefinition script in extension.Definition.WebScripts)
                {
                    if (script.IsCss && script.Timing == timing)
                    {
                        styles.Add((script.MatchPattern, ResolveExtensionPlaceholders(script.Content, extension)));
                    }
                }
            }
        }

        return new WebRequestRules(blocks, styles);
    }

    /// <summary>The CSS of enabled extensions' web.css rules matching <paramref name="url"/> (document-start rules first).</summary>
    private IReadOnlyList<string> GetExtensionStyleSheets(string url)
    {
        var styles = new List<string>();
        if (!_settings.ExtensionsEnabled)
        {
            return styles;
        }

        List<LoadedExtension> extensions = GetEnabledExtensions().ToList();
        foreach (PhantomExtensionScriptTiming timing in new[] { PhantomExtensionScriptTiming.DocumentStart, PhantomExtensionScriptTiming.DocumentEnd })
        {
            foreach (LoadedExtension extension in extensions)
            {
                foreach (PhantomExtensionWebScriptDefinition script in extension.Definition.WebScripts)
                {
                    if (script.IsCss && script.Timing == timing && PhantomExtensionPatternMatcher.IsMatch(url, script.MatchPattern))
                    {
                        styles.Add(ResolveExtensionPlaceholders(script.Content, extension));
                    }
                }
            }
        }

        return styles;
    }

    // ---- Changing the protocol of open tabs ----------------------------------------------------------------------

    /// <summary>
    /// After Settings were saved (or extensions changed): tabs whose protocol differs from the one that should now be
    /// used are reopened at the same address with the new protocol.
    /// </summary>
    private async Task ApplyWebViewProtocolToOpenTabsAsync()
    {
        if (_protocolSwitchInProgress)
        {
            _protocolSwitchPending = true;
            return;
        }

        _protocolSwitchInProgress = true;
        try
        {
            do
            {
                _protocolSwitchPending = false;
                WebProtocol protocol = ResolveSelectedWebProtocol();
                List<BrowserTab> outdated = _tabs.Where(tab => tab.Protocol.Signature != protocol.Signature).ToList();
                if (outdated.Count == 0)
                {
                    continue;
                }

                Log($"Web View protocol changed to {NormalizeForLog(protocol.DisplayName)}; reopening {outdated.Count} tab(s).");

                // The session's cookies live in the WebView2 browser process, which closes with its last tab.
                if (outdated.Any(tab => tab.Page.InnermostPage is WebView2ProtocolPage { UsesIsolatedEnvironment: false }))
                {
                    await SaveCookiesNowAsync();
                }

                string? failure = null;
                foreach (BrowserTab tab in outdated)
                {
                    if (IsDisposed || !_tabs.Contains(tab))
                    {
                        continue;
                    }

                    failure = await ReplaceTabPageAsync(tab, protocol);
                    if (failure is not null)
                    {
                        break;
                    }
                }

                if (IsDisposed)
                {
                    return;
                }

                if (_tabs.Count == 0 && _webViewModeEnabled)
                {
                    LeaveWebViewMode();
                    ShowLogPanel();
                }

                if (failure is not null)
                {
                    string message = $"Web View could not use the {protocol.DisplayName} protocol:\n{failure}\n\nChoose another Web View protocol in Settings -> General.";
                    ShowCaptureProtectedDialog(() => MessageBox.Show(this, message, "PhantomHaze", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                }
            }
            while (_protocolSwitchPending && !IsDisposed);
        }
        finally
        {
            _protocolSwitchInProgress = false;
        }
    }

    /// <summary>Gives a tab a new page made with <paramref name="protocol"/>, at the same address. Returns why it failed, or null.</summary>
    private async Task<string?> ReplaceTabPageAsync(BrowserTab tab, WebProtocol protocol)
    {
        string url = TryNormalizeUrl(tab.CurrentUrl, out string current) ? current : GetConfiguredStartPage();
        WebProtocolPage newPage;
        try
        {
            newPage = protocol.CreatePage(ProtocolHostInstance);
        }
        catch (Exception ex)
        {
            Log($"Tab {tab.Id} could not be reopened with the {NormalizeForLog(protocol.DisplayName)} protocol: {ex.Message}");
            return ex.Message;
        }

        bool wasActive = ReferenceEquals(_activeTab, tab);
        if (wasActive && _findPanel.Visible)
        {
            HideFindPanel(clearHighlights: true);
        }

        WebProtocolPage oldPage = tab.Page;
        CoreWebView2Environment? oldEnvironment = DetachPageFromTab(oldPage);
        tab.Page = newPage;
        tab.Protocol = protocol;
        // A new page renders until it is told otherwise.
        tab.RenderingSuspended = false;
        tab.FaviconBox.Invalidate();
        tab.IsNavigationPending = false;
        tab.NavigationSequence++;
        OnWebView2PageRemoved(oldEnvironment);
        try
        {
            oldPage.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // Best effort cleanup only.
        }

        newPage.View.Visible = false;
        _webViewHostPanel.Controls.Add(newPage.View);
        AttachPageToTab(tab, newPage);
        try
        {
            await newPage.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log($"Tab {tab.Id} could not be reopened with the {NormalizeForLog(protocol.DisplayName)} protocol: {ex.Message}");
            if (_tabs.Contains(tab) && ReferenceEquals(tab.Page, newPage))
            {
                RemoveTabAndDispose(tab);
                UpdateTabLayout();
                if (_activeTab is null && _tabs.Count > 0 && _webViewModeEnabled)
                {
                    ActivateTab(_tabs[0]);
                }
            }

            return ex.Message;
        }

        if (IsDisposed || !_tabs.Contains(tab) || !ReferenceEquals(tab.Page, newPage) || !newPage.IsReady)
        {
            return null;
        }

        if (newPage.Features.HasFlag(WebProtocolFeatures.Zoom))
        {
            newPage.SetZoom(tab.ZoomPercent);
        }

        SetTabFavicon(tab, DefaultTabIcon, ownsImage: false);
        if (ReferenceEquals(_activeTab, tab))
        {
            ActivateTab(tab);
        }
        else
        {
            UpdateTabVisibilityAndRendering();
        }

        NavigateTabToUrl(tab, url, $"Tab {tab.Id} reopened with the {NormalizeForLog(protocol.DisplayName)} protocol");
        return null;
    }

    // ---- Capture protection for windows the Legacy engine opens ---------------------------------------------------

    private void SetUiThreadWindowGuard(object owner, bool enabled)
    {
        if (enabled)
        {
            if (_uiThreadWindowGuardOwners.Add(owner) && _uiThreadWindowGuard is null && !IsDisposed)
            {
                _uiThreadWindowGuard = NativeMethods.InstallThreadWindowHook(OnUiThreadWindowOpening);
                if (!_uiThreadWindowGuard.IsInstalled)
                {
                    Log("ALERT: windows the Legacy protocol's Internet Explorer engine opens could not be protected from capture.");
                }
            }

            return;
        }

        if (_uiThreadWindowGuardOwners.Remove(owner) && _uiThreadWindowGuardOwners.Count == 0)
        {
            _uiThreadWindowGuard?.Dispose();
            _uiThreadWindowGuard = null;
            _guardedUiThreadWindows.Clear();
        }
    }

    /// <summary>
    /// Called (inside the hook, before the window is first drawn) for every window created or activated on the UI thread
    /// while a Legacy page exists. The app's own windows are protected already; anything else gets the same protection.
    /// A window is handled again when it is activated, in case Windows did not accept the setting while it was created.
    /// </summary>
    private void OnUiThreadWindowOpening(IntPtr window)
    {
        if (IsDisposed || !IsHandleCreated || window == Handle || !NativeMethods.IsTopLevelWindow(window))
        {
            return;
        }

        if (FromHandle(window) is Form form && (_protectedAuxiliaryWindows.ContainsKey(form) || ReferenceEquals(form, this)))
        {
            return;
        }

        if (_openSystemDialogWindows.Contains(window))
        {
            return;
        }

        if (_guardedUiThreadWindows.Count > 256)
        {
            // Closed windows are forgotten now and then (tooltips and menus come and go all the time).
            _guardedUiThreadWindows.RemoveWhere(existing => !NativeMethods.IsExistingWindow(existing));
        }

        _guardedUiThreadWindows.Add(window);
        _ = NativeMethods.ExcludeFromCapture(window, _protectionEnabled);
    }
}
