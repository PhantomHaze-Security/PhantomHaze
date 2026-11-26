using System.Globalization;
using System.Net;
using TheArtOfDev.HtmlRenderer.Core.Entities;
using TheArtOfDev.HtmlRenderer.WinForms;

namespace PhantomHaze;

// The tab page of the protocols that draw pages with HTML Renderer (HTMLRenderer and HazeGate). A protocol only decides
// how pages and their parts are downloaded (IRenderedPageTransport); opening, scripting and drawing them is shared.

/// <summary>A page load: the address, and for a form submission its method and body.</summary>
internal sealed class PageRequest
{
    public PageRequest(string url)
    {
        Url = url;
    }

    public string Url { get; }
    public string Method { get; init; } = "GET";
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
    public string? Referrer { get; init; }
}

/// <summary>A downloaded page (after redirects), or a message page that is shown instead (blocked address, error).</summary>
internal sealed class FetchedDocument
{
    public FetchedDocument(string url, int statusCode, string contentType, byte[] body)
    {
        Url = url;
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
    }

    public string Url { get; }
    public int StatusCode { get; }
    public string ContentType { get; }
    public byte[] Body { get; }
    public HtmlRendererDocument? Message { get; private init; }

    public static FetchedDocument FromMessage(HtmlRendererDocument message) =>
        new(message.Url, 0, "text/html", Array.Empty<byte>()) { Message = message };
}

/// <summary>How a protocol downloads pages and their parts. Every address (and redirect) is checked against web.block.</summary>
internal interface IRenderedPageTransport : IPageResourceFetcher
{
    /// <summary>The protocol's name, for messages and the log.</summary>
    string ProtocolName { get; }

    /// <summary>The session's cookies (memory only).</summary>
    CookieContainer Cookies { get; }

    /// <summary>The session's localStorage and sessionStorage.</summary>
    PageSessionStorage Storage { get; }

    /// <summary>The extension rules every request is checked against from now on (a new snapshot for every page load).</summary>
    void UseRules(WebRequestRules rules);

    Task<FetchedDocument> FetchDocumentAsync(PageRequest request, IWebProtocolHost host, string folder, CancellationToken cancellationToken);

    /// <summary>Stops a page download that is running (another page was opened).</summary>
    void CancelDocument();
}

/// <summary>
/// The cookies and localStorage of HTMLRenderer and HazeGate pages: one set per protocol (and proxy server), shared by its
/// tabs for the whole session. They are kept in memory only.
/// </summary>
internal static class RenderedPageSessionData
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (CookieContainer Cookies, PageSessionStorage Storage)> Sessions = new(StringComparer.OrdinalIgnoreCase);

    public static (CookieContainer Cookies, PageSessionStorage Storage) Get(string key)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(key, out (CookieContainer Cookies, PageSessionStorage Storage) session))
            {
                session = (new CookieContainer { PerDomainCapacity = 60, Capacity = 3000, MaxCookieSize = 8192 }, new PageSessionStorage());
                Sessions[key] = session;
            }

            return session;
        }
    }

    /// <summary>Settings -> Clear cookies: every cookie and stored value of these pages is forgotten (open tabs included).</summary>
    public static void ClearAll()
    {
        lock (Gate)
        {
            foreach ((CookieContainer cookies, PageSessionStorage storage) in Sessions.Values)
            {
                foreach (Cookie cookie in cookies.GetAllCookies())
                {
                    cookie.Expired = true;
                }

                storage.ClearAll();
            }
        }
    }
}

/// <summary>A tab shown with HTML Renderer: the page engine's document, drawn, with real controls over its form fields.</summary>
internal sealed partial class RenderedWebProtocolPage : WebProtocolPage
{
    // Unknown (HTML5) elements are inline in HTML Renderer; these are blocks, as in every browser.
    private const string BaseStyleSheet =
        "article, aside, details, dialog, figcaption, figure, footer, header, hgroup, main, nav, section, summary { display: block; }\n" +
        "img { border: 0; }";

    private readonly HtmlPanel _panel;
    private readonly IRenderedPageTransport _transport;
    private readonly bool _scriptsEnabled;
    private readonly SynchronizationContext? _ui;
    private readonly List<string> _history = new();
    private readonly string _folderName = Guid.NewGuid().ToString("N");
    private readonly Dictionary<int, Control> _fieldControls = new();
    private readonly Dictionary<int, string> _pendingValues = new();
    private readonly System.Windows.Forms.Timer _valueTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 450 };
    private readonly SemaphoreSlim _interactionGate = new(1, 1);
    private int _historyIndex = -1;
    private string _currentUrl = string.Empty;
    private string _title = string.Empty;
    private Uri? _baseUri;
    private string? _faviconUrl;
    private WebRequestRules _rules = WebRequestRules.None;
    private string? _documentHtml;
    private bool _documentHighlighted;
    private int _findVersion;
    private IReadOnlyDictionary<string, string> _styleSheets = new Dictionary<string, string>();
    private Dictionary<string, Task<Image?>> _images = new(StringComparer.Ordinal);
    private CancellationTokenSource _documentCancellation = new();
    private CancellationTokenSource? _loadCancellation;
    private WebPageSession? _session;
    private RenderedPage? _rendered;
    private (double Width, double Height) _renderedViewport;
    private string? _sourceHtml;
    public bool IsInspectorPreview { get; private set; }
    private IPageResourceFetcher _documentResources;
    public Action<PageRequest>? ReturnToBrowser { get; init; }
    public override Task<string?> ReadHtmlAsync() => Task.FromResult(_sourceHtml);

    public void ShowEditedHtml(string url, string html, WebProtocolAddressTemplate? template = null) =>
        StartLoad(new PageRequest(url), HistoryUpdate.ReplaceCurrent, html, template);
    private int _loadSequence;
    private int _renderSequence;
    private bool _initialized;
    private bool _windowGuard;
    private bool _positioningFields;
    private bool _fieldLayoutPending;

    public RenderedWebProtocolPage(WebProtocol protocol, IWebProtocolHost host, IRenderedPageTransport transport, bool scriptsEnabled)
        : base(protocol, host)
    {
        _transport = transport;
        _documentResources = transport;
        _scriptsEnabled = scriptsEnabled;
        _ui = SynchronizationContext.Current;
        _panel = new HtmlPanel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = Color.White,
            // Its menu offers "Save image" (an unprotected file dialog) and opens links itself.
            IsContextMenuEnabled = false,
            IsSelectionEnabled = true,
            BaseStylesheet = BaseStyleSheet,
        };
        _panel.LinkClicked += OnLinkClicked;
        _panel.StylesheetLoad += OnStylesheetLoad;
        _panel.ImageLoad += OnImageLoad;
        _panel.Layout += OnPanelLayout;
        _panel.Resize += OnPanelResize;
        _valueTimer.Tick += (_, _) =>
        {
            _valueTimer.Stop();
            _ = FlushPendingValuesAsync(rerender: true);
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            _ = RerenderAsync();
        };
    }

    public override Control View => _panel;
    public override WebProtocolFeatures Features => GetFeatures(_scriptsEnabled);

    public override bool IsReady => _initialized && !IsDisposed;

    public override string CurrentUrl => _currentUrl;

    public override string Title => _title;

    public override bool CanGoBack => _historyIndex > 0;

    public override bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public override bool DownloadsOwnFavicon => true;

    /// <summary>What these pages can do: find in page always; with scripts on, JavaScript and extensions' web.script rules.</summary>
    public static WebProtocolFeatures GetFeatures(bool scriptsEnabled) => scriptsEnabled
        ? WebProtocolFeatures.FindInPage | WebProtocolFeatures.JavaScript | WebProtocolFeatures.ExtensionScripts
        : WebProtocolFeatures.FindInPage;

    private double PixelScale => Math.Clamp((_panel.DeviceDpi > 0 ? _panel.DeviceDpi : 96) / 96.0, 1, 4);

    /// <summary>The page area in CSS pixels (the scroll bar's width is left out).</summary>
    private (double Width, double Height) Viewport
    {
        get
        {
            double scale = PixelScale;
            int width = _panel.ClientSize.Width;
            int height = _panel.ClientSize.Height;
            if (_isEmbeddedFrame) return (Math.Max(1, width / scale), Math.Max(1, height / scale));
            if (width < 320 && _panel.Parent is Control parent)
            {
                width = parent.ClientSize.Width;
                height = parent.ClientSize.Height;
            }

            if (width < 320)
            {
                width = (int)(1000 * scale);
                height = (int)(700 * scale);
            }

            return (Math.Max(320, (width - SystemInformation.VerticalScrollBarWidth) / scale), Math.Max(240, height / scale));
        }
    }

    public override Task InitializeAsync()
    {
        if (Protocol.GetUnavailableReason() is string reason)
        {
            throw new WebProtocolUnavailableException(reason);
        }

        _initialized = true;
        return Task.CompletedTask;
    }

    public override void Navigate(string url) => StartLoad(new PageRequest(url), HistoryUpdate.Push);

    public override void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        _historyIndex--;
        StartLoad(new PageRequest(_history[_historyIndex]), HistoryUpdate.ReplaceCurrent);
    }

    public override void GoForward()
    {
        if (!CanGoForward)
        {
            return;
        }

        _historyIndex++;
        StartLoad(new PageRequest(_history[_historyIndex]), HistoryUpdate.ReplaceCurrent);
    }

    public override void Reload()
    {
        if (_currentUrl.Length > 0)
        {
            StartLoad(new PageRequest(_currentUrl), HistoryUpdate.ReplaceCurrent);
        }
    }

    public override Task<string?> GetDeclaredFaviconUrlAsync() => Task.FromResult(_faviconUrl);

    public override async Task<Image?> DownloadFaviconAsync()
    {
        string? url = _faviconUrl;
        if (url is null && Uri.TryCreate(_currentUrl, UriKind.Absolute, out Uri? page))
        {
            url = page.GetLeftPart(UriPartial.Authority) + "/favicon.ico";
        }

        if (url is null)
        {
            return null;
        }

        try
        {
            PageResource? resource = await _documentResources.FetchAsync(
                new PageResourceRequest(url, PageResourceKind.Image) { MaximumBytes = 1024 * 1024, Referrer = _currentUrl },
                _documentCancellation.Token);
            return resource is null || resource.Status is < 200 or >= 300 ? null : await Task.Run(() => HtmlRendererResources.DecodeImage(resource.Body));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public override async Task<string?> ExecuteScriptAsync(string script)
    {
        WebPageSession? session = _session;
        if (session is null || !session.ScriptsRunning)
        {
            // Nothing runs, and nothing is reported as a broken script.
            return "\"scripts are off\"";
        }

        string? result = await session.ExecuteScriptAsync(script, _documentCancellation.Token);
        if (ReferenceEquals(session, _session) && session.HasChangedSinceRender)
        {
            await RerenderAsync();
        }

        return result;
    }

    private enum HistoryUpdate
    {
        Push,
        ReplaceCurrent,
    }

    // ---- Loading ------------------------------------------------------------------------------------------------

    private void StartLoad(PageRequest request, HistoryUpdate historyUpdate, string? editedHtml = null, WebProtocolAddressTemplate? template = null)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_isEmbeddedFrame) return;

        if (editedHtml is null && ReturnToBrowser is not null)
        {
            ReturnToBrowser(request);
            return;
        }

        CancelLoad();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        int sequence = ++_loadSequence;

        // Extensions can change between pages; each page is checked against the rules as they were when it was opened.
        _rules = Host.GetRequestRules();
        _transport.UseRules(_rules);
        OnNavigationStarting(request.Url);
        IPageResourceFetcher resources = template is { IsDirect: false }
            ? new InspectorGatewayResources(_transport, template, new Uri(request.Url), _rules) : _transport;
        _ = LoadAsync(request, historyUpdate, sequence, cancellation.Token, editedHtml, resources);
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        if (!_isEmbeddedFrame) _transport.CancelDocument();
    }

    private bool IsCurrentLoad(int sequence) => !IsDisposed && sequence == _loadSequence;

    private async Task LoadAsync(PageRequest request, HistoryUpdate historyUpdate, int sequence, CancellationToken cancellationToken, string? editedHtml, IPageResourceFetcher resources)
    {
        string folder = Path.Combine(Host.SessionDirectory, "htmlrenderer", _folderName);
        FetchedDocument fetched;
        try
        {
            fetched = editedHtml is null ? await _transport.FetchDocumentAsync(request, Host, folder, cancellationToken)
                : new FetchedDocument(request.Url, 200, "text/html; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(editedHtml));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            fetched = FetchedDocument.FromMessage(HtmlRendererDocumentBuilder.BuildMessagePage(
                request.Url,
                "This page could not be opened",
                ex is HtmlRendererLoadException ? ex.Message : $"Something went wrong while opening the page: {ex.Message}",
                isSuccess: false));
        }

        if (!IsCurrentLoad(sequence))
        {
            return;
        }

        if (fetched.Message is HtmlRendererDocument message)
        {
            _sourceHtml = null;
            IsInspectorPreview = false;
            ShowStaticDocument(message, historyUpdate);
            return;
        }

        string? html = editedHtml ?? (IsHtml(fetched) ? PageText.Decode(fetched.Body, fetched.ContentType, isHtml: true) : null);
        if (html is not null && Uri.TryCreate(fetched.Url, UriKind.Absolute, out Uri? address))
        {
            WebPageSession? session = null;
            try
            {
                (double width, double height) = Viewport;
                var options = new PageEngineOptions
                {
                    ProtocolName = _transport.ProtocolName,
                    ScriptsEnabled = _scriptsEnabled,
                    DrawFrames = !_isEmbeddedFrame,
                    Language = RenderedPageEnvironment.Language,
                    ViewportWidth = width,
                    ViewportHeight = height,
                    PixelScale = PixelScale,
                    IsFontInstalled = RenderedPageEnvironment.IsFontInstalled,
                };
                var engineHost = new EngineHost(Host, _rules);
                session = await WebPageSession.CreateAsync(new PageLoadInput(address, html), options, engineHost, resources, _transport.Cookies, _transport.Storage, cancellationToken);
                RenderedPage page = await session.RenderAsync(width, height, cancellationToken);
                if (!IsCurrentLoad(sequence))
                {
                    session.Dispose();
                    return;
                }

                _sourceHtml = html;
                IsInspectorPreview = editedHtml is not null;
                _documentResources = resources;
                _renderedViewport = (width, height);
                ShowSession(session, page, fetched, historyUpdate);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                session?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                session?.Dispose();
                Host.Log($"{_transport.ProtocolName}: the page engine could not open {TrimForLog(fetched.Url)}, so it is drawn without scripts and modern styles: {ex.Message}");
            }
        }

        // Images, text files, and pages the engine could not open: drawn as they are.
        HtmlRendererDocument document;
        try
        {
            IReadOnlyList<string> extensionStyles = _rules.GetExtensionStyleSheets(fetched.Url);
            document = await Task.Run(
                () => HtmlRendererDocumentBuilder.Build(fetched.Url, fetched.StatusCode, fetched.ContentType, fetched.Body, extensionStyles),
                cancellationToken);
            if (document.StyleSheetLinks.Count > 0)
            {
                await DownloadStyleSheetsAsync(document, resources, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            document = HtmlRendererDocumentBuilder.BuildMessagePage(fetched.Url, "This page could not be opened", $"Something went wrong while opening the page: {ex.Message}", isSuccess: false);
        }

        if (!IsCurrentLoad(sequence))
        {
            document.PrimaryImage?.Dispose();
            return;
        }

        _sourceHtml = html;
        IsInspectorPreview = editedHtml is not null;
        _documentResources = resources;
        ShowStaticDocument(document, historyUpdate);
    }

    private static bool IsHtml(FetchedDocument fetched)
    {
        string mediaType = fetched.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            return fetched.Body.Length > 0;
        }

        if (mediaType.Length > 0 || fetched.Body.Length == 0)
        {
            return false;
        }

        foreach (byte b in fetched.Body.AsSpan(0, Math.Min(fetched.Body.Length, 512)))
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF)
            {
                continue;
            }

            return b == (byte)'<';
        }

        return false;
    }

    private static async Task DownloadStyleSheetsAsync(HtmlRendererDocument document, IPageResourceFetcher resources, CancellationToken cancellationToken)
    {
        var downloads = document.StyleSheetLinks.Select(async link =>
        {
            PageResource? response;
            try
            {
                response = await resources.FetchAsync(
                    new PageResourceRequest(link.Url, PageResourceKind.StyleSheet) { MaximumBytes = HtmlRendererResources.MaximumStyleSheetBytes, Referrer = document.Url },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
            {
                response = null;
            }

            if (response is null || response.Status is < 200 or >= 400 ||
                (response.ContentType.Length > 0 && !response.ContentType.Contains("css", StringComparison.OrdinalIgnoreCase)))
            {
                return (link.Token, Css: string.Empty);
            }

            string css = PageText.Decode(response.Body, response.ContentType, isHtml: false);
            return (link.Token, Css: CssValues.MakeUrlsAbsolute(css, new Uri(response.Url)));
        });

        foreach ((string token, string css) in await Task.WhenAll(downloads))
        {
            document.StyleSheets[token] = css;
        }
    }

    // ---- Showing ------------------------------------------------------------------------------------------------

    /// <summary>A new document starts a new set of images; the old ones are disposed once the panel no longer shows them.</summary>
    private void BeginDocument()
    {
        _documentCancellation.Cancel();
        _documentCancellation.Dispose();
        _documentCancellation = new CancellationTokenSource();
        Dictionary<string, Task<Image?>> previousImages = _images;
        _images = new Dictionary<string, Task<Image?>>(StringComparer.Ordinal);
        DisposeImagesLater(previousImages);
        ReplaceSession(null);
        ClearFieldControls();
        ClearFrames();
        _pendingValues.Clear();
        _valueTimer.Stop();
        _resizeTimer.Stop();
    }

    private void ShowStaticDocument(HtmlRendererDocument document, HistoryUpdate historyUpdate)
    {
        BeginDocument();
        if (document.PrimaryImage is Image primary)
        {
            _images[document.PrimaryImageUrl!] = Task.FromResult<Image?>(primary);
        }

        _rendered = null;
        _panel.BackColor = Color.White;
        _currentUrl = document.Url;
        _title = document.Title;
        _baseUri = document.BaseUri;
        _faviconUrl = document.FaviconUrl;
        _styleSheets = document.StyleSheets;
        _documentHtml = document.Html;
        _documentHighlighted = false;
        _findVersion++;
        SetPanelHtml(document.Html, keepScrollPosition: false);
        FinishShowing(historyUpdate, document.IsSuccess, document.ErrorDescription);
    }

    private void ShowSession(WebPageSession session, RenderedPage page, FetchedDocument fetched, HistoryUpdate historyUpdate)
    {
        BeginDocument();
        ReplaceSession(session);
        _currentUrl = session.DocumentUri.AbsoluteUri.Length > 0 ? MergeFragment(session.DocumentUri.AbsoluteUri, fetched.Url) : fetched.Url;
        _styleSheets = new Dictionary<string, string>();
        ApplyRenderedPage(page, keepScrollPosition: false);
        bool success = fetched.StatusCode is >= 200 and < 400;
        FinishShowing(historyUpdate, success, success ? null : $"HTTP {fetched.StatusCode}");
    }

    private static string MergeFragment(string documentUrl, string fetchedUrl)
    {
        int hash = fetchedUrl.IndexOf('#');
        return hash >= 0 && !documentUrl.Contains('#') ? documentUrl + fetchedUrl[hash..] : documentUrl;
    }

    private void FinishShowing(HistoryUpdate historyUpdate, bool success, string? error)
    {
        if (historyUpdate == HistoryUpdate.Push || _historyIndex < 0)
        {
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            _history.Add(_currentUrl);
            _historyIndex = _history.Count - 1;
        }
        else
        {
            _history[_historyIndex] = _currentUrl;
        }

        OnSourceChanged();
        OnTitleChanged();
        OnHistoryChanged();
        if (Uri.TryCreate(_currentUrl, UriKind.Absolute, out Uri? shown) && shown.Fragment.Length > 1)
        {
            ScrollToFragment(Uri.UnescapeDataString(shown.Fragment[1..]));
        }

        OnNavigationCompleted(success, error);
    }

    private void ApplyRenderedPage(RenderedPage page, bool keepScrollPosition)
    {
        _rendered = page;
        _baseUri = page.BaseUri;
        _faviconUrl = page.FaviconUrl;
        _documentHtml = page.Html;
        _documentHighlighted = false;
        _findVersion++;
        _panel.BackColor = ColorTranslator.FromHtml(page.CanvasColor);
        SetPanelHtml(page.Html, keepScrollPosition);
        UpdateFrames(page.Frames);
        UpdateFieldControls(page.Fields);
        string title = page.Title;
        if (!string.Equals(title, _title, StringComparison.Ordinal))
        {
            _title = title;
            OnTitleChanged();
        }
    }

    /// <summary>Draws the session's document again (after the page changed itself, or the window width changed).</summary>
    private async Task RerenderAsync()
    {
        WebPageSession? session = _session;
        if (session is null || IsDisposed)
        {
            return;
        }

        int sequence = ++_renderSequence;
        (double width, double height) = Viewport;
        try
        {
            RenderedPage page = await session.RenderAsync(width, height, _documentCancellation.Token);
            if (IsDisposed || !ReferenceEquals(session, _session) || sequence != _renderSequence)
            {
                return;
            }

            _renderedViewport = (width, height);
            ApplyRenderedPage(page, keepScrollPosition: true);
        }
        catch (OperationCanceledException)
        {
            // The page moved on.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            Host.Log($"{_transport.ProtocolName}: the page could not be drawn again: {ex.Message}");
        }
    }

    private void ReplaceSession(WebPageSession? session)
    {
        WebPageSession? previous = _session;
        if (ReferenceEquals(previous, session))
        {
            return;
        }

        if (previous is not null)
        {
            previous.NavigationRequested -= OnSessionNavigationRequested;
            previous.UrlChanged -= OnSessionUrlChanged;
            previous.HistoryNavigationRequested -= OnSessionHistoryNavigationRequested;
            previous.Dispose();
        }

        _session = session;
        if (session is not null)
        {
            session.NavigationRequested += OnSessionNavigationRequested;
            session.UrlChanged += OnSessionUrlChanged;
            session.HistoryNavigationRequested += OnSessionHistoryNavigationRequested;
        }
    }

    // The session raises these on its event loop thread.
    private void OnSessionNavigationRequested(object? sender, PageNavigationRequest e)
    {
        PostToUi(() =>
        {
            if (IsDisposed || !ReferenceEquals(sender, _session))
            {
                return;
            }

            if (e.NewWindow)
            {
                OnNewWindowRequested(e.Url, e.IsUserInitiated);
                return;
            }

            StartLoad(new PageRequest(e.Url) { Method = e.Method, Body = e.Body, ContentType = e.ContentType, Referrer = _currentUrl }, HistoryUpdate.Push);
        });
    }

    private void OnSessionUrlChanged(object? sender, PageUrlChangedEventArgs e)
    {
        PostToUi(() =>
        {
            if (IsDisposed || !ReferenceEquals(sender, _session) || string.Equals(e.Url, _currentUrl, StringComparison.Ordinal))
            {
                return;
            }

            _currentUrl = e.Url;
            if (e.Replace || _historyIndex < 0)
            {
                if (_historyIndex >= 0)
                {
                    _history[_historyIndex] = e.Url;
                }
            }
            else
            {
                if (_historyIndex < _history.Count - 1)
                {
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                }

                _history.Add(e.Url);
                _historyIndex = _history.Count - 1;
            }

            OnSourceChanged();
            OnHistoryChanged();
        });
    }

    private void OnSessionHistoryNavigationRequested(object? sender, int delta)
    {
        PostToUi(() =>
        {
            if (IsDisposed || !ReferenceEquals(sender, _session))
            {
                return;
            }

            if (delta < 0)
            {
                GoBack();
            }
            else if (delta > 0)
            {
                GoForward();
            }
        });
    }

    private void PostToUi(Action action)
    {
        if (_ui is null)
        {
            action();
            return;
        }

        _ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // The tab closed meanwhile.
            }
        }, null);
    }

    private void SetPanelHtml(string html, bool keepScrollPosition)
    {
        Point scroll = _panel.AutoScrollPosition;
        try
        {
            _panel.Text = html;
        }
        catch (Exception ex)
        {
            Host.Log($"HTML Renderer could not draw {TrimForLog(_currentUrl)}: {ex.Message}");
            _panel.Text = HtmlRendererDocumentBuilder.BuildMessagePage(_currentUrl, "This page could not be drawn", "HTML Renderer could not draw this page.", isSuccess: false).Html;
        }

        if (keepScrollPosition)
        {
            _panel.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
        }

        PositionFieldControls();
    }

    private void ScrollToFragment(string elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId))
        {
            return;
        }

        try
        {
            _panel.ScrollToElement(elementId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            // No such element.
        }
    }

    private void OnPanelResize(object? sender, EventArgs e)
    {
        if (_session is null || IsDisposed)
        {
            return;
        }

        // Media queries and grid columns depend on the width: the page is drawn again once resizing pauses.
        if (Viewport != _renderedViewport)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    private void OnPanelLayout(object? sender, LayoutEventArgs e)
    {
        // Images that arrive later move what is below them, fields included.
        if ((_fieldControls.Count > 0 || _frames.Count > 0) && !_positioningFields && !_fieldLayoutPending)
        {
            _fieldLayoutPending = true;
            PostToUi(() =>
            {
                _fieldLayoutPending = false;
                PositionFieldControls();
            });
        }
    }

    // ---- Links and interaction -------------------------------------------------------------------------------------

    private bool TryResolve(string reference, out string url)
    {
        url = string.Empty;
        Uri? baseUri = _baseUri ?? (Uri.TryCreate(_currentUrl, UriKind.Absolute, out Uri? current) ? current : null);
        if (!Uri.TryCreate(baseUri, reference.Trim(), out Uri? resolved) && !Uri.TryCreate(reference.Trim(), UriKind.Absolute, out resolved))
        {
            return false;
        }

        url = resolved.AbsoluteUri;
        return true;
    }

    private void OnLinkClicked(object? sender, HtmlLinkClickedEventArgs e)
    {
        // Always handled here: HTML Renderer would otherwise start the link with the Windows shell, outside PhantomHaze.
        e.Handled = true;
        try
        {
            string link = (e.Link ?? string.Empty).Trim();
            if (link.Length == 0)
            {
                return;
            }

            if (link.StartsWith(PageMarkupWriter.ScriptLinkPrefix, StringComparison.Ordinal))
            {
                if (int.TryParse(link[PageMarkupWriter.ScriptLinkPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeId))
                {
                    _ = ActivateAsync(nodeId);
                }

                return;
            }

            if (link.StartsWith('#'))
            {
                ScrollToFragment(Uri.UnescapeDataString(link[1..]));
                return;
            }

            if (!TryResolve(link, out string url) || !IsWebAddress(url))
            {
                Host.Log($"{_transport.ProtocolName} did not open a link that is not a web address: {TrimForLog(link, 200)}");
                return;
            }

            // A link to a part of the page that is already shown only scrolls.
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? target) &&
                Uri.TryCreate(_currentUrl, UriKind.Absolute, out Uri? shown) &&
                target.Fragment.Length > 1 &&
                string.Equals(target.GetLeftPart(UriPartial.Query), shown.GetLeftPart(UriPartial.Query), StringComparison.Ordinal))
            {
                ScrollToFragment(Uri.UnescapeDataString(target.Fragment[1..]));
                return;
            }

            string targetName = e.Attributes is not null && e.Attributes.TryGetValue("target", out string? value) ? value.Trim() : string.Empty;
            bool newWindow = targetName.Length > 0 &&
                             !targetName.Equals("_self", StringComparison.OrdinalIgnoreCase) &&
                             !targetName.Equals("_top", StringComparison.OrdinalIgnoreCase) &&
                             !targetName.Equals("_parent", StringComparison.OrdinalIgnoreCase);
            if (newWindow)
            {
                OnNewWindowRequested(url, isUserInitiated: true);
            }
            else
            {
                StartLoad(new PageRequest(url) { Referrer = _currentUrl }, HistoryUpdate.Push);
            }
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            Host.Log($"{_transport.ProtocolName} could not open a link: {ex.Message}");
        }
    }

    /// <summary>A click on a link or clickable element: the page's scripts handle it (and may change or leave the page).</summary>
    private async Task ActivateAsync(int nodeId)
    {
        WebPageSession? session = _session;
        if (session is null)
        {
            return;
        }

        await RunInteractionAsync(session, async token =>
        {
            await FlushPendingValuesAsync(rerender: false);
            return await session.ClickAsync(nodeId, token);
        });
    }

    private async Task RunInteractionAsync(WebPageSession session, Func<CancellationToken, Task<PageInteractionResult>> interaction)
    {
        CancellationToken token = _documentCancellation.Token;
        try
        {
            await _interactionGate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            PageInteractionResult result = await interaction(token);
            if (IsDisposed || !ReferenceEquals(session, _session))
            {
                return;
            }

            if (result.DocumentChanged)
            {
                await RerenderAsync();
            }

            if (result.ScrollToFragment is string fragment)
            {
                ScrollToFragment(fragment);
            }
        }
        catch (OperationCanceledException)
        {
            // The page moved on.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            Host.Log($"{_transport.ProtocolName}: the page did not respond: {ex.Message}");
        }
        finally
        {
            _interactionGate.Release();
        }
    }

    // ---- Form fields ----------------------------------------------------------------------------------------------

    private void UpdateFieldControls(IReadOnlyList<PageFormField> fields)
    {
        var seen = new HashSet<int>();
        foreach (PageFormField field in fields)
        {
            seen.Add(field.NodeId);
            if (_fieldControls.TryGetValue(field.NodeId, out Control? existing) && !Matches(existing, field))
            {
                RemoveFieldControl(field.NodeId);
                existing = null;
            }

            if (existing is null)
            {
                existing = CreateFieldControl(field);
                _fieldControls[field.NodeId] = existing;
                _panel.Controls.Add(existing);
            }

            UpdateFieldControl(existing, field);
        }

        foreach (int stale in _fieldControls.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            RemoveFieldControl(stale);
        }

        if (_fieldControls.Count > 0 && !_windowGuard)
        {
            // Lists drop down (and text boxes open menus) in windows of their own, which must be protected from capture too.
            _windowGuard = true;
            Host.SetUiThreadWindowGuard(this, true);
        }

        PositionFieldControls();
    }

    private static bool Matches(Control control, PageFormField field)
    {
        return (control.Tag as FieldTag)?.Kind == field.Kind;
    }

    private sealed record FieldTag(int NodeId, PageFormFieldKind Kind, string ElementId, Font OwnedFont);

    private Control CreateFieldControl(PageFormField field)
    {
        float fontSize = (float)Math.Clamp(field.FontSizePx * 0.75, 6, 30);
        var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Point);
        var tag = new FieldTag(field.NodeId, field.Kind, field.ElementId, font);
        switch (field.Kind)
        {
            case PageFormFieldKind.Select:
            {
                var comboBox = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    Font = font,
                    Tag = tag,
                    IntegralHeight = false,
                    MaxDropDownItems = 14,
                };
                comboBox.SelectionChangeCommitted += (_, _) =>
                {
                    WebPageSession? session = _session;
                    if (session is not null)
                    {
                        int index = comboBox.SelectedIndex;
                        _ = RunInteractionAsync(session, token => session.SelectOptionAsync(field.NodeId, index, token));
                    }
                };
                return comboBox;
            }
            case PageFormFieldKind.CheckBox:
            case PageFormFieldKind.Radio:
            {
                ButtonBase box = field.Kind == PageFormFieldKind.Radio
                    ? new RadioButton { AutoCheck = false, Tag = tag }
                    : new CheckBox { AutoCheck = true, Tag = tag };
                box.Font = font;
                box.Text = string.Empty;
                box.BackColor = Color.Transparent;
                box.Click += (_, _) =>
                {
                    WebPageSession? session = _session;
                    if (session is null)
                    {
                        return;
                    }

                    bool isChecked = box is CheckBox checkBox ? checkBox.Checked : true;
                    _ = RunInteractionAsync(session, token => session.SetCheckedAsync(field.NodeId, isChecked, token));
                };
                return box;
            }
            default:
            {
                var textBox = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    Font = font,
                    Tag = tag,
                    Multiline = field.Kind == PageFormFieldKind.TextArea,
                    ScrollBars = field.Kind == PageFormFieldKind.TextArea ? ScrollBars.Vertical : ScrollBars.None,
                    AcceptsReturn = field.Kind == PageFormFieldKind.TextArea,
                    UseSystemPasswordChar = field.Kind == PageFormFieldKind.Password,
                };
                textBox.TextChanged += (_, _) =>
                {
                    if (textBox.Focused)
                    {
                        _pendingValues[field.NodeId] = textBox.Text;
                        _valueTimer.Stop();
                        _valueTimer.Start();
                    }
                };
                textBox.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter && field.Kind != PageFormFieldKind.TextArea)
                    {
                        e.SuppressKeyPress = true;
                        WebPageSession? session = _session;
                        if (session is null)
                        {
                            return;
                        }

                        string text = textBox.Text;
                        _pendingValues.Remove(field.NodeId);
                        _ = RunInteractionAsync(session, async token =>
                        {
                            await session.SetValueAsync(field.NodeId, text, commit: false, token);
                            return await session.SubmitFromFieldAsync(field.NodeId, token);
                        });
                    }
                };
                textBox.Leave += (_, _) =>
                {
                    WebPageSession? session = _session;
                    if (session is null)
                    {
                        return;
                    }

                    string text = textBox.Text;
                    _pendingValues.Remove(field.NodeId);
                    _ = RunInteractionAsync(session, token => session.SetValueAsync(field.NodeId, text, commit: true, token));
                };
                return textBox;
            }
        }
    }

    private static void UpdateFieldControl(Control control, PageFormField field)
    {
        control.Enabled = !field.Disabled;
        Color textColor = Color.Black;
        if (field.TextColor is string color)
        {
            try
            {
                textColor = ColorTranslator.FromHtml(color.StartsWith("rgba", StringComparison.Ordinal) ? "#000000" : color);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                textColor = Color.Black;
            }
        }

        switch (control)
        {
            case TextBox textBox:
                textBox.ReadOnly = field.ReadOnly;
                textBox.PlaceholderText = field.Placeholder;
                textBox.ForeColor = textColor;
                textBox.BackColor = Color.White;
                if (field.MaxLength > 0)
                {
                    textBox.MaxLength = field.MaxLength;
                }

                // What the user is typing is never replaced; otherwise the page's value is shown.
                if (!textBox.Focused && !string.Equals(textBox.Text, field.Value, StringComparison.Ordinal))
                {
                    textBox.Text = field.Value;
                }

                break;
            case ComboBox comboBox:
                if (!comboBox.Items.Cast<string>().SequenceEqual(field.Options))
                {
                    comboBox.BeginUpdate();
                    comboBox.Items.Clear();
                    comboBox.Items.AddRange(field.Options.Cast<object>().ToArray());
                    comboBox.EndUpdate();
                }

                if (!comboBox.DroppedDown && comboBox.SelectedIndex != field.SelectedIndex && field.SelectedIndex < comboBox.Items.Count)
                {
                    comboBox.SelectedIndex = field.SelectedIndex;
                }

                comboBox.ForeColor = textColor;
                break;
            case CheckBox checkBox:
                checkBox.Checked = field.Checked;
                break;
            case RadioButton radioButton:
                radioButton.Checked = field.Checked;
                break;
        }
    }

    /// <summary>Puts every field control over its placeholder in the drawn page (and hides it when the field is not drawn).</summary>
    private void PositionFieldControls()
    {
        if ((_fieldControls.Count == 0 && _frames.Count == 0) || IsDisposed || _positioningFields)
        {
            return;
        }

        _positioningFields = true;
        try
        {
            Point scroll = _panel.AutoScrollPosition;
            PositionFrames(scroll);
            int inset = (int)Math.Ceiling(2 * PixelScale);
            foreach (Control control in _fieldControls.Values)
            {
                if (control.Tag is not FieldTag tag)
                {
                    continue;
                }

                RectangleF? found;
                try
                {
                    found = _panel.GetElementRectangle(tag.ElementId);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException)
                {
                    found = null;
                }

                if (found is not RectangleF rectangle || rectangle.Width < 2 || rectangle.Height < 2)
                {
                    control.Visible = false;
                    continue;
                }

                var bounds = Rectangle.Round(rectangle);
                bounds.Offset(scroll);
                switch (control)
                {
                    case CheckBox or RadioButton:
                    {
                        int side = Math.Max(13, Math.Min(bounds.Width, bounds.Height));
                        control.Bounds = new Rectangle(bounds.X, bounds.Y + ((bounds.Height - side) / 2), side + 2, side);
                        break;
                    }
                    case ComboBox comboBox:
                        comboBox.Bounds = new Rectangle(bounds.X, bounds.Y, Math.Max(40, bounds.Width), Math.Max(comboBox.PreferredHeight, bounds.Height));
                        break;
                    case TextBox { Multiline: true }:
                        control.Bounds = Rectangle.Inflate(bounds, -inset, -inset);
                        break;
                    case TextBox textBox:
                    {
                        int height = textBox.PreferredHeight;
                        int width = Math.Max(20, bounds.Width - (inset * 2));
                        control.Bounds = new Rectangle(bounds.X + inset, bounds.Y + Math.Max(0, (bounds.Height - height) / 2), width, Math.Min(height, Math.Max(12, bounds.Height)));
                        break;
                    }
                }

                control.Visible = true;
            }
        }
        finally
        {
            _positioningFields = false;
        }
    }

    private async Task FlushPendingValuesAsync(bool rerender)
    {
        WebPageSession? session = _session;
        if (session is null || _pendingValues.Count == 0)
        {
            return;
        }

        List<KeyValuePair<int, string>> values = _pendingValues.ToList();
        _pendingValues.Clear();
        bool changed = false;
        foreach ((int nodeId, string value) in values)
        {
            try
            {
                PageInteractionResult result = await session.SetValueAsync(nodeId, value, commit: false, _documentCancellation.Token);
                changed |= result.DocumentChanged;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException or TimeoutException)
            {
                return;
            }
        }

        if (changed && rerender && ReferenceEquals(session, _session))
        {
            await RerenderAsync();
        }
    }

    private void RemoveFieldControl(int nodeId)
    {
        if (_fieldControls.Remove(nodeId, out Control? control))
        {
            _panel.Controls.Remove(control);
            var tag = (FieldTag)control.Tag!;
            control.Dispose();
            tag.OwnedFont.Dispose();
        }
    }

    private void ClearFieldControls()
    {
        foreach (int nodeId in _fieldControls.Keys.ToList())
        {
            RemoveFieldControl(nodeId);
        }
    }

    // ---- Stylesheets and images ------------------------------------------------------------------------------------

    private void OnStylesheetLoad(object? sender, HtmlStylesheetLoadEventArgs e)
    {
        // Always answered here: HTML Renderer would otherwise download the stylesheet itself, on the UI thread and
        // without web.block rules. Stylesheets were downloaded before the page was shown.
        e.SetStyleSheet = _styleSheets.TryGetValue(e.Src ?? string.Empty, out string? css) && !string.IsNullOrWhiteSpace(css)
            ? css
            : "/* not loaded */";
    }

    private void OnImageLoad(object? sender, HtmlImageLoadEventArgs e)
    {
        string source = (e.Src ?? string.Empty).Trim();

        // Inline SVG of the page, drawn by PhantomHaze's SVG renderer.
        if (source.StartsWith(PageMarkupWriter.SvgImagePrefix, StringComparison.Ordinal))
        {
            e.Handled = true;
            Image? svg = RenderInlineSvg(source);
            if (svg is null)
            {
                e.Callback();
            }
            else
            {
                Track(source, svg);
                e.Callback(svg);
            }

            return;
        }

        if (source.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            Image? svg = PhantomSvgDocument.TryDecodeDataUri(source, out string markup) ? RenderSvgMarkup(markup, Color.Black, null) : null;
            if (svg is null)
            {
                e.Callback();
            }
            else
            {
                Track(source, svg);
                e.Callback(svg);
            }

            return;
        }

        // Other inline images (data:image/...) are decoded by HTML Renderer itself; nothing is downloaded.
        if (source.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Everything else is loaded here: HTML Renderer would otherwise download images itself into the system temp
        // folder, and read local files.
        e.Handled = true;
        if (source.Length == 0 || !TryResolve(source, out string url) || !IsWebAddress(url))
        {
            e.Callback();
            return;
        }

        if (!_images.TryGetValue(url, out Task<Image?>? image))
        {
            image = DownloadImageAsync(url, _documentCancellation.Token);
            _images[url] = image;
        }

        _ = DeliverImageAsync(image, e, _documentCancellation.Token);
    }

    private void Track(string key, Image image)
    {
        if (_images.TryGetValue(key, out Task<Image?>? previous))
        {
            DisposeImagesLater(new Dictionary<string, Task<Image?>> { [key] = previous });
        }

        _images[key] = Task.FromResult<Image?>(image);
    }

    private Image? RenderInlineSvg(string source)
    {
        // phsvg:{id}:{width}:{height} (the size in screen pixels the page draws it at).
        string[] parts = source[PageMarkupWriter.SvgImagePrefix.Length..].Split(':');
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
            _rendered?.SvgImages.TryGetValue(id, out PageSvgImage? svg) != true || svg is null)
        {
            return null;
        }

        Size? size = parts.Length >= 3 &&
                     int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) &&
                     int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
            ? new Size(Math.Clamp(width, 1, 2048), Math.Clamp(height, 1, 2048))
            : null;
        Color color;
        try
        {
            color = ColorTranslator.FromHtml(svg.Color);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            color = Color.Black;
        }

        return RenderSvgMarkup(svg.Markup, color, size);
    }

    private Image? RenderSvgMarkup(string markup, Color color, Size? size)
    {
        try
        {
            PhantomSvgDocument document = PhantomSvgDocument.Parse(markup);
            Size target = size ?? new Size(
                (int)Math.Clamp(document.ViewBox.Width * PixelScale, 1, 1024),
                (int)Math.Clamp(document.ViewBox.Height * PixelScale, 1, 1024));
            return PhantomSvgRenderer.Render(document, target, color);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private async Task<Image?> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        PageResource? response;
        try
        {
            response = await _documentResources.FetchAsync(
                new PageResourceRequest(url, PageResourceKind.Image) { MaximumBytes = HtmlRendererResources.MaximumImageBytes, Referrer = _currentUrl },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }

        if (response is null || response.Status is < 200 or >= 300 || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        double scale = PixelScale;
        bool svg = response.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
                   new Uri(response.Url).AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        return await Task.Run(() =>
        {
            if (svg)
            {
                return RenderSvgMarkup(PageText.Decode(response.Body, response.ContentType, isHtml: false), Color.Black, null);
            }

            Image? image = HtmlRendererResources.DecodeImage(response.Body);

            // Pages size images in CSS pixels; on a scaled display, an image without a size of its own is enlarged to match.
            if (image is not null && scale > 1.05 && image.Width * scale <= 3000 && image.Height * scale <= 3000)
            {
                var scaled = new Bitmap(image, (int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale));
                image.Dispose();
                return scaled;
            }

            return image;
        }, CancellationToken.None);
    }

    private async Task DeliverImageAsync(Task<Image?> image, HtmlImageLoadEventArgs e, CancellationToken cancellationToken)
    {
        Image? result = null;
        try
        {
            result = await image;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
        {
            // Shown without the image.
        }

        if (IsDisposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (result is null)
            {
                e.Callback();
            }
            else
            {
                e.Callback(result);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            // The document was replaced while the image was loading.
        }
    }

    private static void DisposeImagesLater(Dictionary<string, Task<Image?>> images)
    {
        foreach (Task<Image?> task in images.Values)
        {
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        completed.Result?.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    // ---- Find in page ------------------------------------------------------------------------------------------------

    public override async Task<int> FindAsync(string query)
    {
        if (_documentHtml is null || IsDisposed)
        {
            return 0;
        }

        int version = ++_findVersion;
        string trimmed = query.Trim();
        if (trimmed.Length > 0)
        {
            // Every change redraws the page, so wait until typing pauses.
            await Task.Delay(150);
            if (IsDisposed || version != _findVersion || _documentHtml is null)
            {
                return 0;
            }
        }

        string html = _documentHtml;
        (string highlighted, int count) = trimmed.Length == 0
            ? (html, 0)
            : await Task.Run(() => HtmlRendererDocumentBuilder.HighlightMatches(html, trimmed));
        if (IsDisposed || version != _findVersion || !ReferenceEquals(html, _documentHtml))
        {
            return count;
        }

        if (count > 0)
        {
            SetPanelHtml(highlighted, keepScrollPosition: false);
            _documentHighlighted = true;
            ScrollToFragment(HtmlRendererDocumentBuilder.FirstMatchId);
        }
        else if (_documentHighlighted)
        {
            SetPanelHtml(html, keepScrollPosition: true);
            _documentHighlighted = false;
        }

        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        CancelLoad();
        _documentCancellation.Cancel();
        ReplaceSession(null);
        _valueTimer.Dispose();
        _resizeTimer.Dispose();
        _panel.LinkClicked -= OnLinkClicked;
        _panel.StylesheetLoad -= OnStylesheetLoad;
        _panel.ImageLoad -= OnImageLoad;
        _panel.Layout -= OnPanelLayout;
        _panel.Resize -= OnPanelResize;
        ClearFieldControls();
        ClearFrames();
        if (_windowGuard)
        {
            _windowGuard = false;
            Host.SetUiThreadWindowGuard(this, false);
        }

        try
        {
            _panel.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Best effort cleanup only.
        }

        _documentCancellation.Dispose();
        DisposeImagesLater(_images);
        _images = new Dictionary<string, Task<Image?>>(StringComparer.Ordinal);
        if (_isEmbeddedFrame) return;
        try
        {
            Directory.Delete(Path.Combine(Host.SessionDirectory, "htmlrenderer", _folderName), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The session folder is deleted when PhantomHaze closes anyway.
        }
    }

    /// <summary>What the page engine uses from PhantomHaze (from its own threads).</summary>
    private sealed class EngineHost : IPageEngineHost
    {
        private readonly IWebProtocolHost _host;
        private readonly WebRequestRules _rules;

        public EngineHost(IWebProtocolHost host, WebRequestRules rules)
        {
            _host = host;
            _rules = rules;
        }

        public void Log(string message) => _host.Log(message);

        public bool IsBlocked(string url, out string blockedBy) => _rules.IsBlocked(url, out blockedBy);

        public IReadOnlyList<string> GetExtensionStyleSheets(string url) => _rules.GetExtensionStyleSheets(url);
    }
}
