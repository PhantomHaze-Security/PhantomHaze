using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Web View protocols: the ways Web View loads and shows web pages (Settings -> General -> Web View protocol).
// WebView2 is the default; HTMLRenderer and Legacy are built in, and extensions can add their own (protocol.add).
// README.md ("Adding a Web View protocol") explains how to add another built-in protocol.

/// <summary>What the pages of a protocol can do. Web View enables its controls (zoom, find in page) to match.</summary>
[Flags]
internal enum WebProtocolFeatures
{
    None = 0,

    /// <summary>The zoom slider works (<see cref="WebProtocolPage.SetZoom"/>).</summary>
    Zoom = 1 << 0,

    /// <summary>Find in page (Ctrl+F) works (<see cref="WebProtocolPage.FindAsync"/>).</summary>
    FindInPage = 1 << 1,

    /// <summary>Extensions' web.script and web.css rules run in its pages (<see cref="WebProtocolPage.ExecuteScriptAsync"/>).</summary>
    ExtensionScripts = 1 << 2,

    /// <summary>Pages can run JavaScript.</summary>
    JavaScript = 1 << 3,

    /// <summary>Its cookies live in PhantomHaze's shared WebView2 cookie store, so Settings -> Save cookies applies to them.</summary>
    SavedCookies = 1 << 4,
}

/// <summary>Thrown by <see cref="WebProtocolPage.InitializeAsync"/> when a protocol cannot run on this PC.</summary>
internal sealed class WebProtocolUnavailableException : Exception
{
    public WebProtocolUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>What protocol pages need from PhantomHaze. Every member is called on the UI thread.</summary>
internal interface IWebProtocolHost
{
    /// <summary>Writes a line to the log panel and to today's log file.</summary>
    void Log(string message);

    /// <summary>
    /// True when an enabled extension's web.block rule matches <paramref name="url"/>. Protocols must check every request
    /// they make themselves (pages, frames, images, stylesheets) with this.
    /// </summary>
    bool IsBlocked(string url, out string blockedBy);

    /// <summary>The CSS of enabled extensions' web.css rules whose pattern matches <paramref name="url"/>, in order.</summary>
    IReadOnlyList<string> GetExtensionStyleSheets(string url);

    /// <summary>This run's private folder for temporary web data. It is deleted when PhantomHaze closes.</summary>
    string SessionDirectory { get; }

    /// <summary>The shared WebView2 environment or, with a proxy server, a separate one that is only used for that proxy.</summary>
    Task<CoreWebView2Environment> GetWebView2EnvironmentAsync(string? proxyServer);

    /// <summary>
    /// Runs once a WebView2 page's CoreWebView2 exists and before its first navigation: request filters, web.block,
    /// saved cookies.
    /// </summary>
    Task OnWebView2CoreCreatedAsync(WebView2ProtocolPage page, CoreWebView2 core);

    /// <summary>
    /// Asks PhantomHaze to exclude every top-level window that opens on the UI thread from screen capture while
    /// <paramref name="owner"/> exists (pages whose engine runs in-process and can open its own windows, like Legacy).
    /// </summary>
    void SetUiThreadWindowGuard(object owner, bool enabled);
}

/// <summary>
/// A way of loading and showing web pages. One instance describes the protocol and creates a
/// <see cref="WebProtocolPage"/> for every tab.
/// </summary>
internal abstract class WebProtocol
{
    /// <summary>The id stored in settings.json. Never change it once a protocol has been released.</summary>
    public abstract string Id { get; }

    /// <summary>The name shown in Settings and in the log.</summary>
    public abstract string DisplayName { get; }

    /// <summary>One or two sentences shown under the protocol list in Settings.</summary>
    public abstract string Description { get; }

    public abstract WebProtocolFeatures Features { get; }

    /// <summary>Changes whenever pages made by this protocol would behave differently; open tabs are reopened when it changes.</summary>
    public virtual string Signature => Id;

    /// <summary>Why the protocol cannot be used on this PC, or null when it can. Should be quick: Settings calls it.</summary>
    public virtual string? GetUnavailableReason() => null;

    /// <summary>
    /// False when pages are loaded through another server (a gateway address or a proxy server). PhantomHaze itself then
    /// never contacts the sites shown (for example to download their icons), so it never goes around that server.
    /// </summary>
    public virtual bool LoadsPagesDirectly => true;

    public abstract WebProtocolPage CreatePage(IWebProtocolHost host);

    public override string ToString() => DisplayName;
}

internal sealed class WebPageNavigationStartingEventArgs : EventArgs
{
    public WebPageNavigationStartingEventArgs(string url)
    {
        Url = url;
    }

    public string Url { get; }
}

internal sealed class WebPageNavigationCompletedEventArgs : EventArgs
{
    public WebPageNavigationCompletedEventArgs(bool isSuccess, string? errorDescription)
    {
        IsSuccess = isSuccess;
        ErrorDescription = errorDescription;
    }

    public bool IsSuccess { get; }

    /// <summary>Why the page did not load, for the log (null when it loaded).</summary>
    public string? ErrorDescription { get; }
}

internal sealed class WebPageNewWindowEventArgs : EventArgs
{
    public WebPageNewWindowEventArgs(string url, bool isUserInitiated)
    {
        Url = url;
        IsUserInitiated = isUserInitiated;
    }

    public string Url { get; }
    public bool IsUserInitiated { get; }
}

/// <summary>
/// One tab's page for a protocol: the control shown in Web View plus navigation. Pages are created, used and disposed on
/// the UI thread, and raise their events there. Rules every page follows:
/// <list type="bullet">
/// <item>Never open a top-level window of its own (it would not be excluded from screen capture): new windows are
/// reported with <see cref="NewWindowRequested"/>, and PhantomHaze opens them as tabs.</item>
/// <item>Check every request it makes with <see cref="IWebProtocolHost.IsBlocked"/>.</item>
/// <item>Keep temporary files in <see cref="IWebProtocolHost.SessionDirectory"/>.</item>
/// </list>
/// </summary>
internal abstract class WebProtocolPage : IDisposable
{
    protected WebProtocolPage(WebProtocol protocol, IWebProtocolHost host)
    {
        Protocol = protocol;
        Host = host;
    }

    public WebProtocol Protocol { get; }

    protected IWebProtocolHost Host { get; }

    public virtual WebProtocolFeatures Features => Protocol.Features;

    /// <summary>The page actually doing the work (a wrapping page, such as an extension protocol's, returns the page it wraps).</summary>
    public virtual WebProtocolPage InnermostPage => this;

    /// <summary>The control Web View shows for this page. It is created with the page and disposed with it.</summary>
    public abstract Control View { get; }

    /// <summary>True once <see cref="InitializeAsync"/> has finished and until the page is disposed.</summary>
    public abstract bool IsReady { get; }

    /// <summary>The address shown in the address bar (empty before the first navigation).</summary>
    public abstract string CurrentUrl { get; }

    /// <summary>The address of the document that is actually shown (what location.href reports), for matching extension rules.</summary>
    public virtual string DocumentUrl => CurrentUrl;

    /// <summary>The document's title (empty when it has none).</summary>
    public abstract string Title { get; }

    public abstract bool CanGoBack { get; }

    public abstract bool CanGoForward { get; }

    public bool IsDisposed { get; private set; }

    /// <summary>Prepares the page. Throws (for example <see cref="WebProtocolUnavailableException"/>) when it cannot be used.</summary>
    public abstract Task InitializeAsync();

    /// <summary>Loads an http(s) address. Raises <see cref="NavigationStarting"/> and later <see cref="NavigationCompleted"/>.</summary>
    public abstract void Navigate(string url);

    public abstract void GoBack();

    public abstract void GoForward();

    public abstract void Reload();

    /// <summary>Sets the zoom (50 to 200 percent). Only called when <see cref="Features"/> include <see cref="WebProtocolFeatures.Zoom"/>.</summary>
    public virtual void SetZoom(int percent)
    {
    }

    /// <summary>
    /// Highlights every match of <paramref name="query"/> (an empty query removes the highlights) and returns how many
    /// there are. Only called when <see cref="Features"/> include <see cref="WebProtocolFeatures.FindInPage"/>.
    /// </summary>
    public virtual Task<int> FindAsync(string query) => Task.FromResult(0);

    /// <summary>The absolute address of the icon the document declares (&lt;link rel="icon"&gt;), or null.</summary>
    public virtual Task<string?> GetDeclaredFaviconUrlAsync() => Task.FromResult<string?>(null);

    /// <summary>
    /// Runs JavaScript in the document and returns its JSON-encoded result. Only called when <see cref="Features"/>
    /// include <see cref="WebProtocolFeatures.ExtensionScripts"/>.
    /// </summary>
    public virtual Task<string?> ExecuteScriptAsync(string script) => Task.FromResult<string?>(null);

    /// <summary>Tells a hidden page whether it should keep rendering (Settings -> Render web pages in the background).</summary>
    public virtual void SetRenderingActive(bool active)
    {
    }

    public event EventHandler<WebPageNavigationStartingEventArgs>? NavigationStarting;

    public event EventHandler<WebPageNavigationCompletedEventArgs>? NavigationCompleted;

    /// <summary>The address of the document changed (redirects, in-page navigation).</summary>
    public event EventHandler? SourceChanged;

    public event EventHandler? TitleChanged;

    /// <summary>Back or Forward became available or unavailable.</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>The page wants a new window; PhantomHaze opens the address in a new protected tab instead.</summary>
    public event EventHandler<WebPageNewWindowEventArgs>? NewWindowRequested;

    /// <summary>Ctrl+F was pressed inside the page.</summary>
    public event EventHandler? FindRequested;

    protected void OnNavigationStarting(string url)
    {
        if (!IsDisposed)
        {
            NavigationStarting?.Invoke(this, new WebPageNavigationStartingEventArgs(url));
        }
    }

    protected void OnNavigationCompleted(bool isSuccess, string? errorDescription)
    {
        if (!IsDisposed)
        {
            NavigationCompleted?.Invoke(this, new WebPageNavigationCompletedEventArgs(isSuccess, errorDescription));
        }
    }

    protected void OnSourceChanged()
    {
        if (!IsDisposed)
        {
            SourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected void OnTitleChanged()
    {
        if (!IsDisposed)
        {
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected void OnHistoryChanged()
    {
        if (!IsDisposed)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected void OnNewWindowRequested(string url, bool isUserInitiated)
    {
        if (!IsDisposed)
        {
            NewWindowRequested?.Invoke(this, new WebPageNewWindowEventArgs(url, isUserInitiated));
        }
    }

    protected void OnFindRequested()
    {
        if (!IsDisposed)
        {
            FindRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>True for absolute http and https addresses, the only kind of address protocols load.</summary>
    public static bool IsWebAddress(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Shortens text for a log line.</summary>
    protected static string TrimForLog(string? value, int maxLength = 220)
    {
        string normalized = (value ?? string.Empty).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + " ...[truncated]";
    }
}

/// <summary>The built-in protocols and protocol ids.</summary>
internal static class WebProtocolCatalog
{
    public const string WebView2Id = "webview2";
    public const string HtmlRendererId = "htmlrenderer";
    public const string LegacyId = "legacy";

    /// <summary>The built-in protocols, in the order Settings lists them. A new built-in protocol is added here (see README).</summary>
    public static IReadOnlyList<WebProtocol> BuiltIn { get; } =
    [
        new WebView2WebProtocol(),
        new HtmlRendererWebProtocol(),
        new LegacyWebProtocol(),
    ];

    /// <summary>WebView2: used when no other protocol is chosen, or when the chosen one cannot be used.</summary>
    public static WebProtocol Default => BuiltIn[0];

    public static WebProtocol? FindBuiltIn(string? id)
    {
        return BuiltIn.FirstOrDefault(protocol => protocol.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The engine an extension protocol (protocol.add) runs on, configured with its proxy server.</summary>
    public static WebProtocol CreateEngine(PhantomExtensionProtocolEngine engine, string? proxyServer)
    {
        return engine switch
        {
            PhantomExtensionProtocolEngine.HtmlRenderer => new HtmlRendererWebProtocol(proxyServer),
            PhantomExtensionProtocolEngine.Legacy => new LegacyWebProtocol(),
            _ => new WebView2WebProtocol(proxyServer),
        };
    }

    /// <summary>
    /// The value stored in settings.json: a built-in id ("webview2"), or "extensionId/protocolId" for a protocol an
    /// extension adds. Anything else is replaced by WebView2.
    /// </summary>
    public static string NormalizeId(string? id)
    {
        string value = (id ?? string.Empty).Trim();
        if (FindBuiltIn(value) is WebProtocol builtIn)
        {
            return builtIn.Id;
        }

        int slash = value.IndexOf('/');
        if (slash > 0 && slash == value.LastIndexOf('/') &&
            PhextParser.IsValidIdentifier(value[..slash]) &&
            PhextParser.IsValidIdentifier(value[(slash + 1)..]))
        {
            return value;
        }

        return WebView2Id;
    }

    public static string BuildExtensionProtocolId(string extensionId, string protocolId) => $"{extensionId}/{protocolId}";
}

/// <summary>
/// The address an extension protocol actually loads for a page, built from its template ("https://gateway.example/
/// fetch?url={{url}}"): {{url}} is the page address percent-encoded, {{url.raw}} the address as it is. The address bar
/// still shows the page address: loaded addresses that match the template are mapped back to it.
/// </summary>
internal sealed class WebProtocolAddressTemplate
{
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly bool _raw;

    private WebProtocolAddressTemplate(string prefix, string suffix, bool raw)
    {
        _prefix = prefix;
        _suffix = suffix;
        _raw = raw;
    }

    /// <summary>A template that loads every page directly (the template "{{url}}").</summary>
    public bool IsDirect => _prefix.Length == 0 && _suffix.Length == 0;

    /// <param name="resolvedTemplate">The template, with extension placeholders already filled in.</param>
    public static bool TryCreate(string? resolvedTemplate, out WebProtocolAddressTemplate template, out string error)
    {
        template = null!;
        string value = (resolvedTemplate ?? string.Empty).Trim();
        (int index, int length, bool raw)? placeholder = PhextParser.FindProtocolUrlPlaceholder(value, out int count);
        if (placeholder is null || count != 1)
        {
            error = "The address must contain {{url}} or {{url.raw}} exactly once.";
            return false;
        }

        (int start, int placeholderLength, bool isRaw) = placeholder.Value;
        string prefix = value[..start];
        string suffix = value[(start + placeholderLength)..];
        if (prefix.Length > 0 || suffix.Length > 0)
        {
            string sample = prefix + (isRaw ? "https://example.com/" : Uri.EscapeDataString("https://example.com/")) + suffix;
            if (!WebProtocolPage.IsWebAddress(sample))
            {
                error = "The address must be an http:// or https:// address.";
                return false;
            }
        }

        template = new WebProtocolAddressTemplate(prefix, suffix, isRaw);
        error = string.Empty;
        return true;
    }

    public string BuildAddress(string pageUrl)
    {
        return IsDirect ? pageUrl : _prefix + (_raw ? pageUrl : Uri.EscapeDataString(pageUrl)) + _suffix;
    }

    /// <summary>The page address for a loaded address when it matches the template, otherwise the loaded address itself.</summary>
    public string ToPageUrl(string loadedAddress)
    {
        if (IsDirect || string.IsNullOrEmpty(loadedAddress) ||
            loadedAddress.Length <= _prefix.Length + _suffix.Length ||
            !loadedAddress.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) ||
            !loadedAddress.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
        {
            return loadedAddress;
        }

        string middle = loadedAddress[_prefix.Length..(loadedAddress.Length - _suffix.Length)];
        string candidate;
        try
        {
            candidate = _raw ? middle : Uri.UnescapeDataString(middle);
        }
        catch (UriFormatException)
        {
            return loadedAddress;
        }

        return WebProtocolPage.IsWebAddress(candidate) ? candidate : loadedAddress;
    }
}

/// <summary>A protocol an extension adds with protocol.add: one of the built-in engines, an address template and an optional proxy.</summary>
internal sealed class ExtensionWebProtocol : WebProtocol
{
    private readonly WebProtocol _engine;
    private readonly WebProtocolAddressTemplate? _template;
    private readonly string? _templateError;
    private readonly string _resolvedTemplate;

    public ExtensionWebProtocol(
        string extensionId,
        string extensionName,
        PhantomExtensionProtocolDefinition definition,
        string resolvedTemplate)
    {
        ExtensionId = extensionId;
        ExtensionName = extensionName;
        Definition = definition;
        _resolvedTemplate = resolvedTemplate;
        _engine = WebProtocolCatalog.CreateEngine(definition.Engine, definition.ProxyServer);
        if (WebProtocolAddressTemplate.TryCreate(resolvedTemplate, out WebProtocolAddressTemplate template, out string error))
        {
            _template = template;
        }
        else
        {
            _templateError = error;
        }
    }

    public string ExtensionId { get; }

    public string ExtensionName { get; }

    public PhantomExtensionProtocolDefinition Definition { get; }

    public override string Id => WebProtocolCatalog.BuildExtensionProtocolId(ExtensionId, Definition.Id);

    public override string DisplayName => Definition.Name;

    public override string Description
    {
        get
        {
            string how = _template is null || _template.IsDirect
                ? "Loads pages directly"
                : $"Loads every page you open through {DescribeServer()}";
            string proxy = Definition.ProxyServer is null ? string.Empty : $", through the proxy server {Definition.ProxyServer},";
            return $"Added by the extension \"{ExtensionName}\". {how}{proxy} with the {_engine.DisplayName} engine. {_engine.Description}";
        }
    }

    public override WebProtocolFeatures Features
    {
        get
        {
            WebProtocolFeatures features = _engine.Features;
            // A proxy protocol has its own, separate WebView2 cookie store, which is never saved.
            return Definition.ProxyServer is null ? features : features & ~WebProtocolFeatures.SavedCookies;
        }
    }

    public override string Signature => $"{Id}|{_engine.Id}|{_resolvedTemplate}|{Definition.ProxyServer}";

    public override bool LoadsPagesDirectly => _template is { IsDirect: true } && Definition.ProxyServer is null;

    public override string? GetUnavailableReason()
    {
        return _templateError is not null
            ? $"Its address cannot be used: {_templateError}"
            : _engine.GetUnavailableReason();
    }

    public override WebProtocolPage CreatePage(IWebProtocolHost host)
    {
        if (_template is null)
        {
            throw new WebProtocolUnavailableException($"The protocol \"{DisplayName}\" cannot be used: {_templateError}");
        }

        return new TemplateWebProtocolPage(this, host, _engine.CreatePage(host), _template);
    }

    private string DescribeServer()
    {
        int schemeEnd = _resolvedTemplate.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return "another address";
        }

        int hostEnd = _resolvedTemplate.IndexOfAny(new[] { '/', '?', '#' }, schemeEnd + 3);
        return hostEnd < 0 ? _resolvedTemplate[(schemeEnd + 3)..] : _resolvedTemplate[(schemeEnd + 3)..hostEnd];
    }
}

/// <summary>
/// The page of an extension protocol: it wraps a page of the protocol's engine. Addresses PhantomHaze opens (the address
/// bar, history, favourites, new tabs) are loaded through the protocol's address template; the address bar and history
/// show the page address. Links clicked inside the page are left to the page and its server.
/// </summary>
internal sealed class TemplateWebProtocolPage : WebProtocolPage
{
    private readonly WebProtocolPage _inner;
    private readonly WebProtocolAddressTemplate _template;

    public TemplateWebProtocolPage(WebProtocol protocol, IWebProtocolHost host, WebProtocolPage inner, WebProtocolAddressTemplate template)
        : base(protocol, host)
    {
        _inner = inner;
        _template = template;
        _inner.NavigationStarting += (_, e) => OnNavigationStarting(_template.ToPageUrl(e.Url));
        _inner.NavigationCompleted += (_, e) => OnNavigationCompleted(e.IsSuccess, e.ErrorDescription);
        _inner.SourceChanged += (_, _) => OnSourceChanged();
        _inner.TitleChanged += (_, _) => OnTitleChanged();
        _inner.HistoryChanged += (_, _) => OnHistoryChanged();
        _inner.NewWindowRequested += (_, e) => OnNewWindowRequested(_template.ToPageUrl(e.Url), e.IsUserInitiated);
        _inner.FindRequested += (_, _) => OnFindRequested();
    }

    public override WebProtocolFeatures Features => Protocol.Features;

    public override WebProtocolPage InnermostPage => _inner.InnermostPage;

    public override Control View => _inner.View;

    public override bool IsReady => !IsDisposed && _inner.IsReady;

    public override string CurrentUrl => _template.ToPageUrl(_inner.CurrentUrl);

    public override string DocumentUrl => _inner.DocumentUrl;

    public override string Title => _inner.Title;

    public override bool CanGoBack => _inner.CanGoBack;

    public override bool CanGoForward => _inner.CanGoForward;

    public override Task InitializeAsync() => _inner.InitializeAsync();

    public override void Navigate(string url) => _inner.Navigate(_template.BuildAddress(url));

    public override void GoBack() => _inner.GoBack();

    public override void GoForward() => _inner.GoForward();

    public override void Reload() => _inner.Reload();

    public override void SetZoom(int percent) => _inner.SetZoom(percent);

    public override Task<int> FindAsync(string query) => _inner.FindAsync(query);

    public override Task<string?> GetDeclaredFaviconUrlAsync() => _inner.GetDeclaredFaviconUrlAsync();

    public override Task<string?> ExecuteScriptAsync(string script) => _inner.ExecuteScriptAsync(script);

    public override void SetRenderingActive(bool active) => _inner.SetRenderingActive(active);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
    }
}
