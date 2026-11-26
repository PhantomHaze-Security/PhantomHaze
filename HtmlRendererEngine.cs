using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Browser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AngleSharp.Js;
using Jint;
using Jint.Native;

namespace PhantomHaze;

// The page engine of the HTMLRenderer and HazeGate protocols. A page is parsed by AngleSharp, the way a browser parses it,
// and its JavaScript runs with Jint (through AngleSharp.Js) against that document: inline and external scripts, modules,
// timers, events, fetch/XMLHttpRequest. Once the page has settled, its document - with the styles worked out by the
// cascade (HtmlRendererCss.cs) - is written as markup HTML Renderer can draw (HtmlRendererMarkup.cs). Clicks, typing and
// form submissions in the drawn page are passed back to the document, so the page's own scripts handle them.
//
// Everything that touches the document or the script engine runs on the AngleSharp.Js event loop thread (see
// RunOnLoopAsync): Jint is not thread-safe. Nothing in this file uses Windows Forms.

/// <summary>What a page asks the protocol to download.</summary>
internal enum PageResourceKind
{
    Script,
    StyleSheet,
    Image,

    /// <summary>fetch() and XMLHttpRequest from the page's scripts.</summary>
    Data,
}

internal sealed record PageResourceRequest
{
    public PageResourceRequest(string url, PageResourceKind kind)
    {
        Url = url;
        Kind = kind;
    }

    public string Url { get; init; }
    public PageResourceKind Kind { get; }
    public string Method { get; init; } = "GET";
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }

    /// <summary>Headers a script set (fetch/XMLHttpRequest); unsafe ones are left out by the fetcher.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }

    public string? Referrer { get; init; }

    /// <summary>Send the session's cookies (only for requests to the page's own site).</summary>
    public bool SendCookies { get; init; }

    public int MaximumBytes { get; init; } = 8 * 1024 * 1024;
}

internal sealed record PageResource
{
    public PageResource(string url, int status, string contentType, byte[] body)
    {
        Url = url;
        Status = status;
        ContentType = contentType;
        Body = body;
    }

    /// <summary>The address after redirects.</summary>
    public string Url { get; init; }
    public int Status { get; }
    public string ContentType { get; }
    public byte[] Body { get; }

    public string StatusText { get; init; } = string.Empty;

    /// <summary>Response headers a script may read (fetch/XMLHttpRequest).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();
}

/// <summary>Downloads the parts of a page. Implementations check every address (and redirect) against web.block.</summary>
internal interface IPageResourceFetcher
{
    Task<PageResource?> FetchAsync(PageResourceRequest request, CancellationToken cancellationToken);
}

/// <summary>What the page engine needs from PhantomHaze.</summary>
internal interface IPageEngineHost
{
    void Log(string message);
    bool IsBlocked(string url, out string blockedBy);
    IReadOnlyList<string> GetExtensionStyleSheets(string url);
}

/// <summary>The settings a page is opened with.</summary>
internal sealed class PageEngineOptions
{
    /// <summary>The protocol's name, for the log.</summary>
    public string ProtocolName { get; init; } = "HTMLRenderer";

    public bool ScriptsEnabled { get; init; } = true;
    public string UserAgent { get; init; } = PageEngineDefaults.UserAgent;
    public string Language { get; init; } = "en-US";

    /// <summary>The page area in CSS pixels.</summary>
    public double ViewportWidth { get; init; } = 1024;
    public double ViewportHeight { get; init; } = 768;

    /// <summary>Screen pixels per CSS pixel (the Windows display scale).</summary>
    public double PixelScale { get; init; } = 1;

    /// <summary>How long scripts may run while the page loads, in total.</summary>
    public TimeSpan LoadScriptBudget { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>How long one script (or one event handler) may run.</summary>
    public TimeSpan ScriptTimeLimit { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Real time given to timers and network requests after the page loaded, before it is drawn.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Whether a font family is installed (for picking the first available font of a font-family list).</summary>
    public Func<string, bool>? IsFontInstalled { get; init; }
}

internal static class PageEngineDefaults
{
    /// <summary>
    /// A desktop browser's user agent: many sites refuse or simplify pages for unknown clients. PhantomHaze identifies
    /// itself at the end.
    /// </summary>
    public static string UserAgent { get; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 PhantomHaze/" +
        (AppVersionInfo.Tag?.VersionNumber ?? "1");

    public const string AcceptHtml = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    // GDI+ cannot decode WebP or AVIF, so servers that negotiate the image format are asked for PNG, JPEG or GIF.
    public const string AcceptImages = "image/png,image/jpeg,image/gif,image/bmp,image/x-icon,image/svg+xml;q=0.8,image/*;q=0.5,*/*;q=0.3";
}

/// <summary>localStorage and sessionStorage of the session, per site. Kept in memory only, like the rest of a session.</summary>
internal sealed class PageSessionStorage
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _areas = new(StringComparer.Ordinal);

    public string? Get(string origin, string area, string key)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(origin + "\n" + area, out Dictionary<string, string>? values) && values.TryGetValue(key, out string? value) ? value : null;
        }
    }

    public void Set(string origin, string area, string key, string value)
    {
        lock (_gate)
        {
            string id = origin + "\n" + area;
            if (!_areas.TryGetValue(id, out Dictionary<string, string>? values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                _areas[id] = values;
            }

            // A page cannot make PhantomHaze keep more than about 5 MB per site (as browsers do).
            long size = values.Sum(pair => (long)pair.Key.Length + pair.Value.Length);
            if (size + key.Length + value.Length > 5 * 1024 * 1024)
            {
                return;
            }

            values[key] = value;
        }
    }

    public void Remove(string origin, string area, string key)
    {
        lock (_gate)
        {
            if (_areas.TryGetValue(origin + "\n" + area, out Dictionary<string, string>? values))
            {
                values.Remove(key);
            }
        }
    }

    public void Clear(string origin, string area)
    {
        lock (_gate)
        {
            _areas.Remove(origin + "\n" + area);
        }
    }

    /// <summary>Forgets everything (Settings -> Clear cookies).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _areas.Clear();
        }
    }

    public string[] Keys(string origin, string area)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(origin + "\n" + area, out Dictionary<string, string>? values) ? values.Keys.ToArray() : Array.Empty<string>();
        }
    }
}

/// <summary>A navigation the page asked for: a link, a form submission, location.href = ..., window.open().</summary>
internal sealed class PageNavigationRequest : EventArgs
{
    public PageNavigationRequest(string url)
    {
        Url = url;
    }

    public string Url { get; }
    public string Method { get; init; } = "GET";
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }

    /// <summary>The page wants a new window (target="_blank", window.open): PhantomHaze opens a protected tab.</summary>
    public bool NewWindow { get; init; }

    public bool IsUserInitiated { get; init; } = true;
}

/// <summary>history.pushState (or replaceState, <see cref="Replace"/>) changed the document's address.</summary>
internal sealed class PageUrlChangedEventArgs : EventArgs
{
    public PageUrlChangedEventArgs(string url, bool replace)
    {
        Url = url;
        Replace = replace;
    }

    public string Url { get; }
    public bool Replace { get; }
}

/// <summary>What an interaction with the drawn page led to.</summary>
internal sealed class PageInteractionResult
{
    public static readonly PageInteractionResult Nothing = new();

    /// <summary>The document changed, so the page should be drawn again.</summary>
    public bool DocumentChanged { get; init; }

    /// <summary>A link to a part of the same page: scroll to this element id.</summary>
    public string? ScrollToFragment { get; init; }
}

/// <summary>The input a page is opened from (the response of its address).</summary>
internal sealed class PageLoadInput
{
    public PageLoadInput(Uri url, string html)
    {
        Url = url;
        Html = html;
    }

    public Uri Url { get; }
    public string Html { get; }
}

/// <summary>
/// One opened page: its AngleSharp document and (with scripts on) its script engine. Pages are created with
/// <see cref="CreateAsync"/>, drawn with <see cref="RenderAsync"/> and disposed when the tab moves on.
/// </summary>
internal sealed class WebPageSession : IDisposable
{
    private const int MaximumScripts = 250;
    private const long MaximumScriptBytes = 40L * 1024 * 1024;

    private readonly PageEngineOptions _options;
    private readonly IPageEngineHost _host;
    private readonly IPageResourceFetcher _fetcher;
    private readonly PageSessionStorage _storage;
    private readonly CancellationTokenSource _lifetime = new();

    // Cancelled to stop the page's scripts (time budget used up, or the page closed); the document stays usable.
    private readonly CancellationTokenSource _scriptStop = new();
    private readonly ConcurrentSet _deniedResources = new();
    private readonly object _networkGate = new();
    private readonly NodeIdTable _nodeIds = new();
    private readonly PageStyleCache _styleCache = new();
    private IBrowsingContext? _context;
    private IDocument? _document;
    private IEventLoop? _loop;
    private Engine? _engine;
    private MutationObserver? _observer;
    private TaskCompletionSource<bool>? _networkIdle;
    private int _inFlight;
    private int _scriptCount;
    private long _scriptBytes;
    private int _documentVersion;
    private int _renderedVersion = -1;
    private int _reportedScriptErrors;
    private double _virtualHorizon;
    private bool _scriptsAlive;
    private bool _disposed;

    private WebPageSession(PageEngineOptions options, IPageEngineHost host, IPageResourceFetcher fetcher, PageSessionStorage storage)
    {
        _options = options;
        _host = host;
        _fetcher = fetcher;
        _storage = storage;
    }

    /// <summary>The address of the document (it changes with history.pushState).</summary>
    public Uri DocumentUri { get; private set; } = new("about:blank");

    /// <summary>The page's scripts can still run (scripts are on and were not stopped for using too much time).</summary>
    public bool ScriptsRunning => _scriptsAlive && !_disposed;

    /// <summary>The document changed since it was last drawn.</summary>
    public bool HasChangedSinceRender => Volatile.Read(ref _documentVersion) != _renderedVersion;

    /// <summary>Raised on the event loop thread; handlers must marshal to their own thread.</summary>
    public event EventHandler<PageNavigationRequest>? NavigationRequested;

    /// <summary>history.pushState/replaceState changed the address without loading a page (raised on the event loop thread).</summary>
    public event EventHandler<PageUrlChangedEventArgs>? UrlChanged;

    /// <summary>history.back()/forward()/go(n) (raised on the event loop thread).</summary>
    public event EventHandler<int>? HistoryNavigationRequested;

    public static async Task<WebPageSession> CreateAsync(
        PageLoadInput input,
        PageEngineOptions options,
        IPageEngineHost host,
        IPageResourceFetcher fetcher,
        CookieContainer cookies,
        PageSessionStorage storage,
        CancellationToken cancellationToken)
    {
        var session = new WebPageSession(options, host, fetcher, storage);
        try
        {
            await session.OpenAsync(input, cookies, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task OpenAsync(PageLoadInput input, CookieContainer cookies, CancellationToken cancellationToken)
    {
        DocumentUri = input.Url;
        bool scripts = _options.ScriptsEnabled;
        IConfiguration config = Configuration.Default
            .WithCss()
            .With(new SessionRequester(this))
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = scripts, IsNavigationDisabled = false })
            .With<IResourceLoader>(context => new ScriptOnlyResourceLoader(context, this))
            .With(new SessionNavigationHandler(this))
            .With(new MemoryCookieProvider(cookies))
            .WithEventLoop();
        if (scripts)
        {
            config = config
                .WithJs(new JsScriptingOptions { MaxCallStackDepth = 256 })
                .WithConsoleLogger(_ => new SessionConsoleLogger(this))
                .With(new EngineCreator(CreateEngine));
        }

        IBrowsingContext context = BrowsingContext.New(config);
        _context = context;
        _loop = context.GetService<IEventLoop>();
        _scriptsAlive = scripts;

        string html = scripts ? PrepareScriptedHtml(input.Html, BuildPreludeConfig()) : input.Html;

        // Scripts get a total time budget for loading the page; past it, they are stopped and the page is drawn as it is.
        using var budget = scripts ? new CancellationTokenSource(_options.LoadScriptBudget) : null;
        using CancellationTokenRegistration budgetRegistration = budget is null
            ? default
            : budget.Token.Register(() => StopScripts("The page's scripts used more than their time budget and were stopped."));
        Task<IDocument> open = await RunOnLoopAsync(() => context.OpenAsync(request => request.Content(html).Address(input.Url.AbsoluteUri), cancellationToken)).ConfigureAwait(false);
        IDocument document = await open.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        _document = document;
        cancellationToken.ThrowIfCancellationRequested();

        await RunOnLoopAsync(() =>
        {
            _observer = new MutationObserver((_, _) => Interlocked.Increment(ref _documentVersion));
            _observer.Connect(document, childList: true, subtree: true, attributes: true, characterData: true);
            return true;
        }).ConfigureAwait(false);

        if (_scriptsAlive)
        {
            _virtualHorizon = 3000;
            await SettleAsync(_options.SettleTime, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- Scripts -------------------------------------------------------------------------------------------------

    private Engine CreateEngine(IWindow window, Options options)
    {
        options.TimeoutInterval(_options.ScriptTimeLimit);
        options.MaxStatements(60_000_000);
        options.LimitMemory(768L * 1024 * 1024);
        options.CancellationToken(_scriptStop.Token);
        var engine = new Engine(options);

        // The bridge the prelude uses (it removes these names from the page's global object right away).
        engine.SetValue("__phFetch", new Action<int, string, string, string, string, string>(OnScriptFetch));
        engine.SetValue("__phMatchMedia", new Func<string, bool>(query => CssMediaQueryEvaluator.Matches(query, BuildMediaContext())));
        engine.SetValue("__phOpen", new Action<string>(OnScriptOpenWindow));
        engine.SetValue("__phUrl", new Action<string, bool>(OnScriptUrlChanged));
        engine.SetValue("__phGo", new Action<int>(delta => HistoryNavigationRequested?.Invoke(this, delta)));
        engine.SetValue("__phStorageGet", new Func<string, string, string?>((area, key) => _storage.Get(Origin, area, key)));
        engine.SetValue("__phStorageSet", new Action<string, string, string>((area, key, value) => _storage.Set(Origin, area, key, value)));
        engine.SetValue("__phStorageRemove", new Action<string, string>((area, key) => _storage.Remove(Origin, area, key)));
        engine.SetValue("__phStorageClear", new Action<string>(area => _storage.Clear(Origin, area)));
        engine.SetValue("__phStorageKeys", new Func<string, string>(area => JsonSerializer.Serialize(_storage.Keys(Origin, area))));
        engine.SetValue("__phLog", new Action<string, string>(OnScriptLog));
        _engine = engine;
        return engine;
    }

    private string Origin => DocumentUri.IsAbsoluteUri ? DocumentUri.GetLeftPart(UriPartial.Authority) : "null";

    private void StopScripts(string reason)
    {
        if (!_scriptsAlive || _disposed)
        {
            return;
        }

        _scriptsAlive = false;
        _host.Log($"{_options.ProtocolName}: {reason}");
        try
        {
            _scriptStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private string BuildPreludeConfig()
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ua"] = _options.UserAgent,
            ["lang"] = _options.Language,
            ["width"] = Math.Round(_options.ViewportWidth),
            ["height"] = Math.Round(_options.ViewportHeight),
            ["dpr"] = Math.Round(_options.PixelScale, 2),
        });
    }

    /// <summary>
    /// Adds the prelude (the browser parts AngleSharp.Js does not have) as the first script of the page, and turns
    /// &lt;script nomodule&gt; fallbacks off: module scripts run here, so a browser would skip them too.
    /// </summary>
    internal static string PrepareScriptedHtml(string html, string preludeConfig)
    {
        string prelude = "<script>" + PagePrelude.Script.Replace("__PH_CONFIG__", preludeConfig, StringComparison.Ordinal) + "</script>";
        string withoutFallbacks = DisableNoModuleScripts(html);
        int insertAt = FindPreludeInsertPosition(withoutFallbacks);
        return withoutFallbacks.Insert(insertAt, prelude);
    }

    private static int FindPreludeInsertPosition(string html)
    {
        foreach (string tag in new[] { "<head", "<html" })
        {
            int index = html.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                int after = index + tag.Length;
                if (after < html.Length && (html[after] == '>' || char.IsWhiteSpace(html[after])))
                {
                    int end = html.IndexOf('>', after);
                    if (end >= 0)
                    {
                        return end + 1;
                    }
                }

                index = html.IndexOf(tag, after, StringComparison.OrdinalIgnoreCase);
            }
        }

        int doctype = html.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
        if (doctype >= 0 && doctype < 64)
        {
            int end = html.IndexOf('>', doctype);
            if (end >= 0)
            {
                return end + 1;
            }
        }

        return 0;
    }

    private static string DisableNoModuleScripts(string html)
    {
        int index = html.IndexOf("nomodule", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return html;
        }

        var output = new StringBuilder(html.Length + 64);
        int cursor = 0;
        int search = 0;
        while (true)
        {
            int start = html.IndexOf("<script", search, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            int end = html.IndexOf('>', start);
            if (end < 0)
            {
                break;
            }

            string tag = html[start..end];
            if (tag.Contains("nomodule", StringComparison.OrdinalIgnoreCase))
            {
                // The first of two duplicate attributes wins, so this type comes before the script's own.
                output.Append(html, cursor, start - cursor).Append("<script type=\"text/x-phantomhaze-nomodule\"");
                cursor = start + "<script".Length;
            }

            search = end + 1;
        }

        output.Append(html, cursor, html.Length - cursor);
        return output.ToString();
    }

    private void OnScriptLog(string level, string message)
    {
        if (level is "alert" or "confirm" or "prompt")
        {
            _host.Log($"{_options.ProtocolName}: the page showed a message ({level}): {LogText(message, 300)}");
            return;
        }

        if (level == "error" && Interlocked.Increment(ref _reportedScriptErrors) <= 5)
        {
            _host.Log($"{_options.ProtocolName}: a script error on {LogText(DocumentUri.AbsoluteUri, 120)}: {LogText(message, 300)}");
        }
    }

    private void OnScriptOpenWindow(string url)
    {
        if (TryResolve(url, out string absolute))
        {
            NavigationRequested?.Invoke(this, new PageNavigationRequest(absolute) { NewWindow = true, IsUserInitiated = false });
        }
    }

    private void OnScriptUrlChanged(string url, bool replace)
    {
        if (!TryResolve(url, out string absolute) || !Uri.TryCreate(absolute, UriKind.Absolute, out Uri? target))
        {
            return;
        }

        // pushState can only change the address within the same site.
        if (!string.Equals(target.GetLeftPart(UriPartial.Authority), DocumentUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DocumentUri = target;
        UpdateDocumentAddress(target);
        UrlChanged?.Invoke(this, new PageUrlChangedEventArgs(target.AbsoluteUri, replace));
    }

    /// <summary>
    /// Makes location.href (and relative links) follow history.pushState. AngleSharp has no public way to change a
    /// document's address without loading it, so its internal property is set when it exists.
    /// </summary>
    private void UpdateDocumentAddress(Uri target)
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            System.Reflection.PropertyInfo? property = _document.GetType().GetProperty(
                "DocumentUri",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property?.SetMethod is not null)
            {
                property.SetValue(_document, target.AbsoluteUri);
            }
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or ArgumentException or MethodAccessException or InvalidOperationException)
        {
            // location keeps the address the page was loaded from.
        }
    }

    private bool TryResolve(string reference, out string absolute)
    {
        absolute = string.Empty;
        Uri baseUri = DocumentUri;
        try
        {
            if (_document?.BaseUrl is Url baseUrl && Uri.TryCreate(baseUrl.Href, UriKind.Absolute, out Uri? documentBase))
            {
                baseUri = documentBase;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Keep the document address.
        }

        if (!Uri.TryCreate(baseUri, (reference ?? string.Empty).Trim(), out Uri? resolved) ||
            !(resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
        {
            return false;
        }

        absolute = resolved.AbsoluteUri;
        return true;
    }

    // fetch() and XMLHttpRequest: downloaded by the protocol's fetcher, answered on the event loop.
    private void OnScriptFetch(int id, string url, string method, string headersJson, string body, string mode)
    {
        if (!TryResolve(url, out string absolute))
        {
            CompleteScriptFetch(id, 0, string.Empty, url, "{}", string.Empty, "text", "The address is not a web address.");
            return;
        }

        var headers = new List<KeyValuePair<string, string>>();
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(headersJson) ? "{}" : headersJson);
            foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            {
                headers.Add(new KeyValuePair<string, string>(property.Name, property.Value.ToString()));
            }
        }
        catch (JsonException)
        {
            // No headers.
        }

        bool sameSite = Uri.TryCreate(absolute, UriKind.Absolute, out Uri? target) &&
                        string.Equals(target.GetLeftPart(UriPartial.Authority), DocumentUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        string upperMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        string? contentType = headers.FirstOrDefault(header => header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value;
        var request = new PageResourceRequest(absolute, PageResourceKind.Data)
        {
            Method = upperMethod,
            Body = upperMethod is "GET" or "HEAD" || string.IsNullOrEmpty(body) ? null : Encoding.UTF8.GetBytes(body),
            ContentType = contentType,
            Headers = headers,
            Referrer = DocumentUri.AbsoluteUri,
            // Cookies only go to the page's own site, and never when the script asked for none.
            SendCookies = sameSite && !string.Equals(mode, "omit", StringComparison.OrdinalIgnoreCase),
            MaximumBytes = 15 * 1024 * 1024,
        };

        BeginNetworkRequest();
        _ = Task.Run(async () =>
        {
            try
            {
                if (_host.IsBlocked(absolute, out _))
                {
                    CompleteScriptFetch(id, 0, string.Empty, absolute, "{}", string.Empty, "text", "Blocked by an extension.");
                    return;
                }

                PageResource? resource = await _fetcher.FetchAsync(request, _lifetime.Token).ConfigureAwait(false);
                if (resource is null)
                {
                    CompleteScriptFetch(id, 0, string.Empty, absolute, "{}", string.Empty, "text", "The request failed.");
                    return;
                }

                var headerObject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach ((string name, string value) in resource.Headers)
                {
                    headerObject[name.ToLowerInvariant()] = headerObject.TryGetValue(name.ToLowerInvariant(), out string? existing) ? existing + ", " + value : value;
                }

                if (!headerObject.ContainsKey("content-type") && resource.ContentType.Length > 0)
                {
                    headerObject["content-type"] = resource.ContentType;
                }

                bool text = IsTextContent(resource.ContentType);
                string payload = text ? PageText.Decode(resource.Body, resource.ContentType, isHtml: false) : Convert.ToBase64String(resource.Body);
                CompleteScriptFetch(id, resource.Status, resource.StatusText, resource.Url, JsonSerializer.Serialize(headerObject), payload, text ? "text" : "base64", null);
            }
            catch (Exception ex)
            {
                CompleteScriptFetch(id, 0, string.Empty, absolute, "{}", string.Empty, "text", ex is OperationCanceledException ? "The request was cancelled." : "The request failed.");
            }
            finally
            {
                EndNetworkRequest();
            }
        });
    }

    private static bool IsTextContent(string contentType)
    {
        string type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type.Length == 0 || type.StartsWith("text/", StringComparison.Ordinal) || type.Contains("json", StringComparison.Ordinal) ||
               type.Contains("javascript", StringComparison.Ordinal) || type.Contains("xml", StringComparison.Ordinal) ||
               type.Contains("x-www-form-urlencoded", StringComparison.Ordinal) || type.Contains("ecmascript", StringComparison.Ordinal);
    }

    private void CompleteScriptFetch(int id, int status, string statusText, string url, string headersJson, string body, string bodyKind, string? error)
    {
        if (_disposed || _loop is null)
        {
            return;
        }

        try
        {
            _loop.Enqueue(_ =>
            {
                if (!_scriptsAlive || _engine is null)
                {
                    return;
                }

                InvokeScript("__phFetchDone", id, status, statusText, url, headersJson, body, bodyKind, error ?? string.Empty);
            }, TaskPriority.Normal);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The page closed meanwhile.
        }
    }

    /// <summary>Calls a function of the page (on the event loop thread) and runs the promise jobs it queued.</summary>
    private JsValue? InvokeScript(string function, params object?[] arguments)
    {
        if (!_scriptsAlive || _engine is null)
        {
            return null;
        }

        try
        {
            JsValue result = _engine.Invoke(function, arguments!);
            _engine.Advanced.ProcessTasks();
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (ex is Jint.Runtime.ExecutionCanceledException or OperationCanceledException)
            {
                return null;
            }

            if (Interlocked.Increment(ref _reportedScriptErrors) <= 5)
            {
                _host.Log($"{_options.ProtocolName}: a script error on {LogText(DocumentUri.AbsoluteUri, 120)}: {LogText(ex.Message, 300)}");
            }

            try
            {
                _engine.Advanced.ResetCallStack();
            }
            catch (Exception resetError) when (resetError is InvalidOperationException or NullReferenceException)
            {
                // Best effort.
            }

            return null;
        }
    }

    /// <summary>
    /// Lets the page finish what it does after loading: timers (run in virtual time, so a 2 second delay does not take
    /// 2 seconds), network requests its scripts make, and the work queued on the event loop.
    /// </summary>
    private async Task SettleAsync(TimeSpan realBudget, CancellationToken cancellationToken)
    {
        if (!_scriptsAlive || _loop is null)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        int totalCallbacks = 0;
        int idleRounds = 0;
        while (watch.Elapsed < realBudget && _scriptsAlive && !cancellationToken.IsCancellationRequested)
        {
            double horizon = _virtualHorizon;
            int ran = await RunOnLoopAsync(() => PumpTimers(horizon, 60)).ConfigureAwait(false);
            totalCallbacks += ran;
            await YieldToLoopAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _inFlight) > 0)
            {
                TimeSpan remaining = realBudget - watch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await WaitForNetworkIdleAsync(remaining < TimeSpan.FromMilliseconds(400) ? remaining : TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                }

                idleRounds = 0;
                continue;
            }

            if (ran == 0)
            {
                // Nothing due within the horizon and nothing downloading: give queued loop work one more chance.
                if (++idleRounds >= 2)
                {
                    break;
                }
            }
            else
            {
                idleRounds = 0;
            }

            if (totalCallbacks > 1500)
            {
                break;
            }
        }
    }

    private int PumpTimers(double horizon, int maximum)
    {
        int ran = 0;
        while (ran < maximum && _scriptsAlive)
        {
            JsValue? result = InvokeScript("__phPump", horizon);
            if (result is null || !result.IsNumber() || result.AsNumber() < 1)
            {
                break;
            }

            ran++;
        }

        return ran;
    }

    private Task YieldToLoopAsync() => RunOnLoopAsync(() => true, TaskPriority.None);

    private void BeginNetworkRequest()
    {
        lock (_networkGate)
        {
            _inFlight++;
            _networkIdle ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void EndNetworkRequest()
    {
        lock (_networkGate)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            if (_inFlight == 0)
            {
                _networkIdle?.TrySetResult(true);
                _networkIdle = null;
            }
        }
    }

    private async Task WaitForNetworkIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task? idle;
        lock (_networkGate)
        {
            idle = _inFlight == 0 ? null : (_networkIdle ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        if (idle is null)
        {
            return;
        }

        try
        {
            await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still downloading; drawn without it.
        }
    }

    /// <summary>Runs <paramref name="action"/> on the event loop thread (where the document and scripts live).</summary>
    private Task<T> RunOnLoopAsync<T>(Func<T> action, TaskPriority priority = TaskPriority.Normal)
    {
        if (_disposed)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(WebPageSession)));
        }

        IEventLoop? loop = _loop;
        if (loop is null)
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            loop.Enqueue(cancel =>
            {
                if (cancel.IsCancellationRequested || _disposed)
                {
                    completion.TrySetCanceled();
                    return;
                }

                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, priority);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            completion.TrySetException(ex);
        }

        // A loop that stopped working must never leave the caller waiting forever.
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    // ---- Drawing -------------------------------------------------------------------------------------------------

    private CssMediaContext BuildMediaContext() => new()
    {
        WidthPx = Math.Max(200, _options.ViewportWidth),
        HeightPx = Math.Max(200, _options.ViewportHeight),
        DevicePixelRatio = _options.PixelScale,
        ScriptingEnabled = _options.ScriptsEnabled,
    };

    /// <summary>Works out the styles and writes the document as HTML Renderer markup.</summary>
    public async Task<RenderedPage> RenderAsync(double viewportWidth, double viewportHeight, CancellationToken cancellationToken)
    {
        IDocument document = _document ?? throw new InvalidOperationException("The page is not open.");
        var media = new CssMediaContext
        {
            WidthPx = Math.Max(200, viewportWidth),
            HeightPx = Math.Max(200, viewportHeight),
            DevicePixelRatio = _options.PixelScale,
            ScriptingEnabled = _options.ScriptsEnabled,
        };

        // 1. Which stylesheets the document has now (scripts may have added or removed some).
        List<StyleSheetReference> references = await RunOnLoopAsync(() => PageStyleCollector.Collect(document)).ConfigureAwait(false);

        // 2. Their text (downloaded once per session) and the rules that apply at this page width.
        CssCascadeIndex index = await _styleCache.GetIndexAsync(
            references,
            _host.GetExtensionStyleSheets(DocumentUri.AbsoluteUri),
            media,
            DownloadStyleSheetAsync,
            cancellationToken).ConfigureAwait(false);

        // 3. The cascade and the markup, on the loop (they read the document).
        int version = Volatile.Read(ref _documentVersion);
        RenderedPage page = await RunOnLoopAsync(() =>
        {
            var writer = new PageMarkupWriter(document, index, media, _options, _nodeIds, _scriptsAlive);
            return writer.Write();
        }).ConfigureAwait(false);
        _renderedVersion = version;
        return page;
    }

    private async Task<DownloadedStyleSheet?> DownloadStyleSheetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (_host.IsBlocked(url.AbsoluteUri, out _))
        {
            return null;
        }

        PageResource? resource = await _fetcher.FetchAsync(
            new PageResourceRequest(url.AbsoluteUri, PageResourceKind.StyleSheet)
            {
                Referrer = DocumentUri.AbsoluteUri,
                SendCookies = false,
                MaximumBytes = 4 * 1024 * 1024,
            },
            cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.Status is < 200 or >= 400)
        {
            return null;
        }

        string type = resource.ContentType.Split(';')[0].Trim();
        if (type.Length > 0 && !type.Contains("css", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new DownloadedStyleSheet(PageText.Decode(resource.Body, resource.ContentType, isHtml: false), new Uri(resource.Url));
    }

    /// <summary>
    /// Runs JavaScript in the page (extensions' web.script and web.css rules) and returns its result as JSON, like
    /// WebView2: "null" when the script failed or returned nothing, null when the page runs no scripts.
    /// </summary>
    public async Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken)
    {
        if (!_scriptsAlive)
        {
            return null;
        }

        string? json = await RunOnLoopAsync(() =>
        {
            if (!_scriptsAlive || _engine is null)
            {
                return null;
            }

            try
            {
                JsValue result = _engine.Evaluate(script);
                _engine.Advanced.ProcessTasks();
                if (result.IsUndefined() || result.IsNull())
                {
                    return "null";
                }

                JsValue serialized = new Jint.Native.Json.JsonSerializer(_engine).Serialize(result, JsValue.Undefined, JsValue.Undefined);
                return serialized.IsString() ? serialized.AsString() : "null";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                if (ex is not Jint.Runtime.ExecutionCanceledException and not OperationCanceledException &&
                    Interlocked.Increment(ref _reportedScriptErrors) <= 5)
                {
                    _host.Log($"{_options.ProtocolName}: an extension script failed on {LogText(DocumentUri.AbsoluteUri, 120)}: {LogText(ex.Message, 300)}");
                }

                try
                {
                    _engine.Advanced.ResetCallStack();
                }
                catch (Exception resetError) when (resetError is InvalidOperationException or NullReferenceException)
                {
                    // Best effort.
                }

                return "null";
            }
        }).ConfigureAwait(false);

        await SettleAfterInteractionAsync(cancellationToken, TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
        return json;
    }

    // ---- Interaction ----------------------------------------------------------------------------------------------

    /// <summary>A click on an element of the drawn page: the page's handlers run, then what the click does by default.</summary>
    public async Task<PageInteractionResult> ClickAsync(int nodeId, CancellationToken cancellationToken)
    {
        string? fragment = await RunOnLoopAsync(() =>
        {
            if (_nodeIds.Find(nodeId) is not IElement element || !IsInDocument(element))
            {
                return null;
            }

            bool proceed = true;
            if (_scriptsAlive)
            {
                DispatchMouse(element, "pointerdown");
                DispatchMouse(element, "mousedown");
                DispatchMouse(element, "pointerup");
                DispatchMouse(element, "mouseup");
                proceed = DispatchMouse(element, "click");
                RunScriptTasks();
            }

            return proceed ? RunDefaultAction(element) : null;
        }).ConfigureAwait(false);

        await SettleAfterInteractionAsync(cancellationToken).ConfigureAwait(false);
        return new PageInteractionResult { DocumentChanged = HasChangedSinceRender, ScrollToFragment = fragment };
    }

    /// <summary>Text typed into a field of the drawn page: the field's value changes and the page gets input events.</summary>
    public async Task<PageInteractionResult> SetValueAsync(int nodeId, string value, bool commit, CancellationToken cancellationToken)
    {
        bool changed = await RunOnLoopAsync(() =>
        {
            switch (_nodeIds.Find(nodeId))
            {
                case IHtmlInputElement input when !string.Equals(input.Value, value, StringComparison.Ordinal):
                    input.Value = value;
                    break;
                case IHtmlTextAreaElement textArea when !string.Equals(textArea.Value, value, StringComparison.Ordinal):
                    textArea.Value = value;
                    break;
                case IHtmlInputElement or IHtmlTextAreaElement:
                    if (!commit)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            if (_scriptsAlive && _nodeIds.Find(nodeId) is IElement element)
            {
                element.Dispatch(new AngleSharp.Dom.Events.Event("input", true, false));
                if (commit)
                {
                    element.Dispatch(new AngleSharp.Dom.Events.Event("change", true, false));
                }

                RunScriptTasks();
            }

            return true;
        }).ConfigureAwait(false);

        if (changed && _scriptsAlive)
        {
            await SettleAfterInteractionAsync(cancellationToken, TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
        }

        return new PageInteractionResult { DocumentChanged = HasChangedSinceRender };
    }

    /// <summary>A checkbox or radio button of the drawn page was switched.</summary>
    public async Task<PageInteractionResult> SetCheckedAsync(int nodeId, bool isChecked, CancellationToken cancellationToken)
    {
        await RunOnLoopAsync(() =>
        {
            if (_nodeIds.Find(nodeId) is not IHtmlInputElement input || input.IsChecked == isChecked)
            {
                return false;
            }

            input.IsChecked = isChecked;
            if (_scriptsAlive)
            {
                DispatchMouse(input, "click");
                input.Dispatch(new AngleSharp.Dom.Events.Event("input", true, false));
                input.Dispatch(new AngleSharp.Dom.Events.Event("change", true, false));
                RunScriptTasks();
            }

            return true;
        }).ConfigureAwait(false);

        await SettleAfterInteractionAsync(cancellationToken, TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        return new PageInteractionResult { DocumentChanged = HasChangedSinceRender };
    }

    /// <summary>An option of a drop-down list of the drawn page was chosen.</summary>
    public async Task<PageInteractionResult> SelectOptionAsync(int nodeId, int optionIndex, CancellationToken cancellationToken)
    {
        await RunOnLoopAsync(() =>
        {
            if (_nodeIds.Find(nodeId) is not IHtmlSelectElement select || optionIndex < 0 || optionIndex >= select.Options.Length)
            {
                return false;
            }

            // AngleSharp's SelectedIndex is read-only: the option itself is selected (and, for a single list, the others not).
            if (!select.IsMultiple)
            {
                foreach (IHtmlOptionElement other in select.Options)
                {
                    other.IsSelected = false;
                }
            }

            if (select.Options[optionIndex] is IHtmlOptionElement option)
            {
                option.IsSelected = true;
            }
            if (_scriptsAlive)
            {
                select.Dispatch(new AngleSharp.Dom.Events.Event("input", true, false));
                select.Dispatch(new AngleSharp.Dom.Events.Event("change", true, false));
                RunScriptTasks();
            }

            return true;
        }).ConfigureAwait(false);

        await SettleAfterInteractionAsync(cancellationToken, TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        return new PageInteractionResult { DocumentChanged = HasChangedSinceRender };
    }

    /// <summary>Enter pressed in a text field: the page gets the key, then the field's form is submitted (implicit submission).</summary>
    public async Task<PageInteractionResult> SubmitFromFieldAsync(int nodeId, CancellationToken cancellationToken)
    {
        await RunOnLoopAsync(() =>
        {
            if (_nodeIds.Find(nodeId) is not IElement element || !IsInDocument(element))
            {
                return false;
            }

            bool proceed = true;
            if (_scriptsAlive)
            {
                proceed = DispatchKey(element, "keydown", "Enter");
                DispatchKey(element, "keypress", "Enter");
                DispatchKey(element, "keyup", "Enter");
                element.Dispatch(new AngleSharp.Dom.Events.Event("change", true, false));
                RunScriptTasks();
            }

            if (!proceed || element is IHtmlTextAreaElement)
            {
                return false;
            }

            IHtmlFormElement? form = (element as IHtmlInputElement)?.Form ?? element.Closest("form") as IHtmlFormElement;
            if (form is null)
            {
                return false;
            }

            // The form's first submit button is clicked when it has one (as browsers do); otherwise it is submitted.
            IHtmlElement? submitter = form.Elements.OfType<IHtmlElement>().FirstOrDefault(IsSubmitButton);
            if (submitter is not null)
            {
                bool clickProceeds = !_scriptsAlive || DispatchMouse(submitter, "click");
                RunScriptTasks();
                if (clickProceeds)
                {
                    SubmitForm(form, submitter);
                }
            }
            else
            {
                SubmitForm(form, null);
            }

            return true;
        }).ConfigureAwait(false);

        await SettleAfterInteractionAsync(cancellationToken).ConfigureAwait(false);
        return new PageInteractionResult { DocumentChanged = HasChangedSinceRender };
    }

    private async Task SettleAfterInteractionAsync(CancellationToken cancellationToken, TimeSpan? realBudget = null)
    {
        if (!_scriptsAlive)
        {
            return;
        }

        double now = await RunOnLoopAsync(() => InvokeScript("__phNow") is JsValue value && value.IsNumber() ? value.AsNumber() : _virtualHorizon).ConfigureAwait(false);
        _virtualHorizon = Math.Max(_virtualHorizon, now + 1500);
        await SettleAsync(realBudget ?? TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
    }

    private void RunScriptTasks()
    {
        if (!_scriptsAlive || _engine is null)
        {
            return;
        }

        try
        {
            _engine.Advanced.ProcessTasks();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A failing promise job is the page's problem, not PhantomHaze's.
        }
    }

    private static bool IsInDocument(INode node)
    {
        for (INode? current = node; current is not null; current = current.Parent)
        {
            if (current is IDocument)
            {
                return true;
            }
        }

        return false;
    }

    private bool DispatchMouse(IElement element, string type)
    {
        try
        {
            var mouseEvent = new AngleSharp.Html.Dom.Events.MouseEvent(
                type, true, true, _document?.DefaultView!, type == "click" ? 1 : 0, 0, 0, 0, 0,
                false, false, false, false, AngleSharp.Html.Dom.Events.MouseButton.Primary, null!, 0, 0);
            return element.Dispatch(mouseEvent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ReportHandlerError(ex);
            return !ex.Message.Contains("preventDefault", StringComparison.Ordinal);
        }
    }

    private bool DispatchKey(IElement element, string type, string key)
    {
        try
        {
            var keyEvent = new AngleSharp.Html.Dom.Events.KeyboardEvent(
                type, true, true, _document?.DefaultView!, 0, key, AngleSharp.Html.Dom.Events.KeyboardLocation.Standard, string.Empty, false);
            return element.Dispatch(keyEvent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ReportHandlerError(ex);
            return true;
        }
    }

    private void ReportHandlerError(Exception ex)
    {
        if (ex is Jint.Runtime.ExecutionCanceledException or OperationCanceledException)
        {
            return;
        }

        if (Interlocked.Increment(ref _reportedScriptErrors) <= 5)
        {
            _host.Log($"{_options.ProtocolName}: a page event handler failed: {LogText(ex.Message, 300)}");
        }

        try
        {
            _engine?.Advanced.ResetCallStack();
        }
        catch (Exception resetError) when (resetError is InvalidOperationException or NullReferenceException)
        {
            // Best effort.
        }
    }

    private static bool IsSubmitButton(IHtmlElement element)
    {
        return element switch
        {
            IHtmlButtonElement button => !button.IsDisabled && (string.IsNullOrEmpty(button.Type) || button.Type.Equals("submit", StringComparison.OrdinalIgnoreCase)),
            IHtmlInputElement input => !input.IsDisabled && input.Type is "submit" or "image",
            _ => false,
        };
    }

    /// <summary>What a click does when no handler prevented it: follow a link, submit a form, switch a checkbox, open details.</summary>
    private string? RunDefaultAction(IElement target)
    {
        for (IElement? element = target; element is not null; element = element.ParentElement)
        {
            switch (element)
            {
                case IHtmlAnchorElement anchor when anchor.HasAttribute("href"):
                    return FollowLink(anchor.GetAttribute("href") ?? string.Empty, anchor.Target);
                case IHtmlAreaElement area when area.HasAttribute("href"):
                    return FollowLink(area.GetAttribute("href") ?? string.Empty, area.Target);
                case IHtmlButtonElement button:
                    if (button.IsDisabled)
                    {
                        return null;
                    }

                    string type = (button.Type ?? "submit").ToLowerInvariant();
                    if (type == "submit" && button.Form is IHtmlFormElement buttonForm)
                    {
                        SubmitForm(buttonForm, button);
                    }
                    else if (type == "reset")
                    {
                        button.Form?.Reset();
                    }

                    return null;
                case IHtmlInputElement input:
                    if (input.IsDisabled)
                    {
                        return null;
                    }

                    switch (input.Type)
                    {
                        case "submit":
                        case "image":
                            if (input.Form is IHtmlFormElement inputForm)
                            {
                                SubmitForm(inputForm, input);
                            }

                            break;
                        case "reset":
                            input.Form?.Reset();
                            break;
                    }

                    return null;
                case IHtmlLabelElement label:
                    if (label.Control is IHtmlInputElement { Type: "checkbox" or "radio" } control && !control.IsDisabled)
                    {
                        control.IsChecked = control.Type == "radio" || !control.IsChecked;
                        control.Dispatch(new AngleSharp.Dom.Events.Event("input", true, false));
                        control.Dispatch(new AngleSharp.Dom.Events.Event("change", true, false));
                    }

                    return null;
                default:
                    if (element.LocalName == "summary" && element.ParentElement is IElement details && details.LocalName == "details")
                    {
                        if (details.HasAttribute("open"))
                        {
                            details.RemoveAttribute("open");
                        }
                        else
                        {
                            details.SetAttribute("open", string.Empty);
                        }

                        return null;
                    }

                    break;
            }
        }

        return null;
    }

    private string? FollowLink(string href, string? target)
    {
        string trimmed = href.Trim();
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            if (_scriptsAlive)
            {
                string code;
                try
                {
                    code = Uri.UnescapeDataString(trimmed["javascript:".Length..]);
                }
                catch (UriFormatException)
                {
                    code = trimmed["javascript:".Length..];
                }

                try
                {
                    _document?.ExecuteScript(code);
                    RunScriptTasks();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    ReportHandlerError(ex);
                }
            }

            return null;
        }

        if (!TryResolve(trimmed, out string url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? destination))
        {
            return null;
        }

        bool newWindow = !string.IsNullOrWhiteSpace(target) &&
                         !target.Equals("_self", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("_top", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("_parent", StringComparison.OrdinalIgnoreCase);

        // A link to a part of the page that is shown only scrolls.
        if (!newWindow && destination.Fragment.Length > 1 &&
            string.Equals(destination.GetLeftPart(UriPartial.Query), DocumentUri.GetLeftPart(UriPartial.Query), StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(destination.Fragment[1..]);
        }

        NavigationRequested?.Invoke(this, new PageNavigationRequest(url) { NewWindow = newWindow });
        return null;
    }

    private void SubmitForm(IHtmlFormElement form, IHtmlElement? submitter)
    {
        try
        {
            if (_scriptsAlive)
            {
                bool proceed = form.Dispatch(new AngleSharp.Dom.Events.Event("submit", true, true));
                RunScriptTasks();
                if (!proceed)
                {
                    return;
                }
            }

            DocumentRequest? request = submitter is null ? form.GetSubmission() : form.GetSubmission(submitter);
            if (request is not null)
            {
                RaiseNavigation(request, form.Target);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ReportHandlerError(ex);
        }
    }

    private void RaiseNavigation(DocumentRequest request, string? target)
    {
        string url = request.Target.Href;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? destination) ||
            !(destination.Scheme == Uri.UriSchemeHttp || destination.Scheme == Uri.UriSchemeHttps))
        {
            return;
        }

        byte[]? body = null;
        bool post = request.Method == AngleSharp.Io.HttpMethod.Post;
        if (post && request.Body is Stream stream)
        {
            try
            {
                using var buffer = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(buffer);
                body = buffer.ToArray();
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
            {
                body = Array.Empty<byte>();
            }
        }

        bool newWindow = !string.IsNullOrWhiteSpace(target) &&
                         !target.Equals("_self", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("_top", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("_parent", StringComparison.OrdinalIgnoreCase);
        NavigationRequested?.Invoke(this, new PageNavigationRequest(destination.AbsoluteUri)
        {
            Method = post ? "POST" : "GET",
            Body = body,
            ContentType = post ? (string.IsNullOrEmpty(request.MimeType) ? "application/x-www-form-urlencoded" : request.MimeType) : null,
            NewWindow = newWindow,
        });
    }

    internal static string LogText(string? value, int maximum)
    {
        string text = (value ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return text.Length <= maximum ? text : text[..maximum] + "...";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scriptsAlive = false;
        try
        {
            _scriptStop.Cancel();
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        try
        {
            _observer?.Disconnect();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Best effort.
        }

        try
        {
            _loop?.CancelAll();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Best effort.
        }

        try
        {
            _context?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The page's scripts may still be unwinding; nothing to keep.
        }

        _lifetime.Dispose();
        _scriptStop.Dispose();
    }

    // ---- AngleSharp services ----------------------------------------------------------------------------------------

    /// <summary>Downloads scripts (and module imports) for AngleSharp; everything else a page refers to is answered "not found".</summary>
    private sealed class SessionRequester : BaseRequester
    {
        private readonly WebPageSession _session;

        public SessionRequester(WebPageSession session)
        {
            _session = session;
        }

        public override bool SupportsProtocol(string protocol) => protocol is "http" or "https";

        protected override async Task<IResponse?> PerformRequestAsync(Request request, CancellationToken cancel)
        {
            IResponse response = await _session.FetchForDocumentAsync(request, cancel).ConfigureAwait(false);

            // Continue on the event loop, so AngleSharp runs the downloaded script there (scripts must never run on two
            // threads at once).
            IEventLoop? loop = _session._loop;
            if (loop is null || _session._disposed)
            {
                return response;
            }

            var handOver = new TaskCompletionSource<IResponse?>();
            try
            {
                loop.Enqueue(_ => handOver.TrySetResult(response), TaskPriority.Normal);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                return response;
            }

            Task finished = await Task.WhenAny(handOver.Task, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)).ConfigureAwait(false);
            return finished == handOver.Task ? handOver.Task.Result : response;
        }
    }

    private async Task<IResponse> FetchForDocumentAsync(Request request, CancellationToken cancel)
    {
        string url = request.Address.Href;
        if (_deniedResources.Contains(url) || !_scriptsAlive || _host.IsBlocked(url, out _) ||
            Interlocked.Increment(ref _scriptCount) > MaximumScripts || Interlocked.Read(ref _scriptBytes) > MaximumScriptBytes)
        {
            return NotFound(request.Address);
        }

        BeginNetworkRequest();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _lifetime.Token);
            bool sameSite = Uri.TryCreate(url, UriKind.Absolute, out Uri? target) &&
                            string.Equals(target.GetLeftPart(UriPartial.Authority), DocumentUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
            PageResource? resource = await _fetcher.FetchAsync(
                new PageResourceRequest(url, PageResourceKind.Script)
                {
                    Referrer = DocumentUri.AbsoluteUri,
                    SendCookies = sameSite,
                    MaximumBytes = 10 * 1024 * 1024,
                },
                linked.Token).ConfigureAwait(false);
            if (resource is null)
            {
                return NotFound(request.Address);
            }

            Interlocked.Add(ref _scriptBytes, resource.Body.Length);
            var response = new DefaultResponse
            {
                Address = new Url(resource.Url),
                StatusCode = (HttpStatusCode)resource.Status,
                Content = new MemoryStream(resource.Body, writable: false),
            };
            response.Headers["Content-Type"] = string.IsNullOrEmpty(resource.ContentType) ? "application/javascript" : resource.ContentType;
            return response;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return NotFound(request.Address);
        }
        finally
        {
            EndNetworkRequest();
        }
    }

    private static IResponse NotFound(Url address)
    {
        return new DefaultResponse
        {
            Address = address,
            StatusCode = HttpStatusCode.NotFound,
            Content = new MemoryStream(Array.Empty<byte>(), writable: false),
        };
    }

    /// <summary>
    /// AngleSharp would also download images, frames, media and stylesheets. Only scripts are downloaded for the
    /// document: images are drawn (and downloaded) by HTML Renderer, and stylesheets are read by the cascade.
    /// </summary>
    private sealed class ScriptOnlyResourceLoader : DefaultResourceLoader
    {
        private readonly WebPageSession _session;

        public ScriptOnlyResourceLoader(IBrowsingContext context, WebPageSession session)
            : base(context, null)
        {
            _session = session;
        }

        public override IDownload FetchAsync(ResourceRequest request)
        {
            bool script = request.Source is IHtmlScriptElement scriptElement && !scriptElement.HasAttribute("nomodule");
            if (!script)
            {
                _session._deniedResources.Add(request.Target.Href);
            }

            return base.FetchAsync(request);
        }
    }

    /// <summary>Navigations a page starts (location.href = ..., form.submit(), links) are handed to PhantomHaze.</summary>
    private sealed class SessionNavigationHandler : INavigationHandler
    {
        private readonly WebPageSession _session;

        public SessionNavigationHandler(WebPageSession session)
        {
            _session = session;
        }

        public bool SupportsProtocol(string protocol) => protocol is "http" or "https";

        public Task<IDocument> NavigateAsync(DocumentRequest request, CancellationToken cancel)
        {
            if (!_session._disposed)
            {
                _session.RaiseNavigation(request, target: null);
            }

            // The page stays as it is until PhantomHaze opens the new one.
            return Task.FromResult(_session._document ?? _session._context?.Active!);
        }
    }

    private sealed class SessionConsoleLogger : IConsoleLogger
    {
        private readonly WebPageSession _session;

        public SessionConsoleLogger(WebPageSession session)
        {
            _session = session;
        }

        public void Log(object[] values)
        {
            // console.log output is not written to PhantomHaze's log (pages log a lot); errors are reported by the prelude.
        }
    }

    /// <summary>A small thread-safe set of strings.</summary>
    private sealed class ConcurrentSet
    {
        private readonly HashSet<string> _items = new(StringComparer.Ordinal);

        public void Add(string item)
        {
            lock (_items)
            {
                if (_items.Count < 20_000)
                {
                    _items.Add(item);
                }
            }
        }

        public bool Contains(string item)
        {
            lock (_items)
            {
                return _items.Contains(item);
            }
        }
    }
}

/// <summary>Stable numbers for the nodes of a document, so the drawn page can refer back to them.</summary>
internal sealed class NodeIdTable
{
    private readonly ConditionalWeakTable<INode, StrongBox<int>> _ids = new();
    private readonly Dictionary<int, WeakReference<INode>> _nodes = new();
    private int _next = 1;

    public int GetId(INode node)
    {
        if (_ids.TryGetValue(node, out StrongBox<int>? box))
        {
            return box.Value;
        }

        int id = _next++;
        _ids.Add(node, new StrongBox<int>(id));
        _nodes[id] = new WeakReference<INode>(node);
        if (_nodes.Count > 50_000)
        {
            foreach (int stale in _nodes.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList())
            {
                _nodes.Remove(stale);
            }
        }

        return id;
    }

    public INode? Find(int id) => _nodes.TryGetValue(id, out WeakReference<INode>? reference) && reference.TryGetTarget(out INode? node) ? node : null;
}

/// <summary>A stylesheet of the document: linked (an address) or written in the page (text).</summary>
internal sealed class StyleSheetReference
{
    public StyleSheetReference(Uri? url, string? text, Uri baseUri, string? media)
    {
        Url = url;
        Text = text;
        BaseUri = baseUri;
        Media = media;
    }

    public Uri? Url { get; }
    public string? Text { get; }
    public Uri BaseUri { get; }
    public string? Media { get; }
}

internal static class PageStyleCollector
{
    /// <summary>The document's stylesheets in document order (must run on the event loop thread).</summary>
    public static List<StyleSheetReference> Collect(IDocument document)
    {
        var references = new List<StyleSheetReference>();
        Uri baseUri = Uri.TryCreate(document.BaseUrl?.Href ?? document.Url, UriKind.Absolute, out Uri? parsed) ? parsed : new Uri("about:blank");
        foreach (IElement element in document.QuerySelectorAll("link, style"))
        {
            if (references.Count >= 120)
            {
                break;
            }

            if (element.LocalName == "style")
            {
                // Extensions' web.css rules are applied by the engine itself (GetExtensionStyleSheets); the <style>
                // elements their injection scripts add would only repeat them.
                if (element.Closest("template, svg") is not null ||
                    (element.Id ?? string.Empty).StartsWith("ph-ext-style-", StringComparison.Ordinal))
                {
                    continue;
                }

                string type = element.GetAttribute("type") ?? string.Empty;
                if (type.Length > 0 && !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                references.Add(new StyleSheetReference(null, element.TextContent, baseUri, element.GetAttribute("media")));
                continue;
            }

            string relation = (element.GetAttribute("rel") ?? string.Empty).ToLowerInvariant();
            string[] relations = relation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!relations.Contains("stylesheet") || relations.Contains("alternate") || element.HasAttribute("disabled"))
            {
                continue;
            }

            string href = element.GetAttribute("href") ?? string.Empty;
            if (href.Length == 0 || !Uri.TryCreate(baseUri, href.Trim(), out Uri? url) ||
                !(url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
            {
                continue;
            }

            references.Add(new StyleSheetReference(url, null, url, element.GetAttribute("media")));
        }

        return references;
    }
}

/// <summary>Keeps a page's downloaded and parsed stylesheets, and the rule index for the last page width.</summary>
internal sealed class PageStyleCache
{
    private readonly Dictionary<string, Task<ParsedCssSheet?>> _sheets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _indexKey;
    private CssCascadeIndex? _index;

    public async Task<CssCascadeIndex> GetIndexAsync(
        List<StyleSheetReference> references,
        IReadOnlyList<string> extensionStyles,
        CssMediaContext media,
        Func<Uri, CancellationToken, Task<DownloadedStyleSheet?>> download,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var budget = new CssImportBudget();
            var sheets = new List<Task<ParsedCssSheet?>>();
            var key = new StringBuilder();
            key.Append(CssValues.FormatNumber(media.WidthPx)).Append('x').Append(CssValues.FormatNumber(media.HeightPx)).Append('|');
            foreach (StyleSheetReference reference in references)
            {
                string id = reference.Url is not null
                    ? "url:" + reference.Url.AbsoluteUri
                    : "text:" + reference.BaseUri.AbsoluteUri + "\n" + reference.Text;
                key.Append(id.Length > 200 ? id[..100] + id.GetHashCode().ToString(CultureInfo.InvariantCulture) + id.Length.ToString(CultureInfo.InvariantCulture) : id)
                   .Append('@').Append(reference.Media).Append('|');
                if (!_sheets.TryGetValue(id + "@" + reference.Media, out Task<ParsedCssSheet?>? sheet))
                {
                    sheet = LoadAsync(reference, download, budget, cancellationToken);
                    if (_sheets.Count > 400)
                    {
                        _sheets.Clear();
                    }

                    _sheets[id + "@" + reference.Media] = sheet;
                }

                sheets.Add(sheet);
            }

            foreach (string css in extensionStyles)
            {
                key.Append("ext:").Append(css.GetHashCode().ToString(CultureInfo.InvariantCulture)).Append('|');
            }

            string indexKey = key.ToString();
            if (_index is not null && string.Equals(indexKey, _indexKey, StringComparison.Ordinal))
            {
                return _index;
            }

            var collector = new CssRuleCollector(media);
            collector.AddSheet(UserAgentStyles.Sheet);
            foreach (Task<ParsedCssSheet?> task in sheets)
            {
                ParsedCssSheet? sheet;
                try
                {
                    sheet = await task.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sheet = null;
                }

                if (sheet is not null)
                {
                    collector.AddSheet(sheet);
                }
            }

            foreach (string css in extensionStyles)
            {
                if (!string.IsNullOrWhiteSpace(css))
                {
                    collector.AddSheet(new ParsedCssSheet(new CssSheetSource(css, null, null, CssOrigin.Extension), CssSheetParser.Parse(css)));
                }
            }

            CssCascadeIndex index = await Task.Run(() => new CssCascadeIndex(collector.Rules), cancellationToken).ConfigureAwait(false);
            _index = index;
            _indexKey = indexKey;
            return index;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ParsedCssSheet?> LoadAsync(
        StyleSheetReference reference,
        Func<Uri, CancellationToken, Task<DownloadedStyleSheet?>> download,
        CssImportBudget budget,
        CancellationToken cancellationToken)
    {
        string? text = reference.Text;
        Uri baseUri = reference.BaseUri;
        if (reference.Url is not null)
        {
            var response = await download(reference.Url, cancellationToken).ConfigureAwait(false);
            text = response?.Text;
            baseUri = response?.FinalUri ?? baseUri;
        }

        if (text is null)
        {
            return null;
        }

        var source = new CssSheetSource(text, baseUri, reference.Media, CssOrigin.Author);
        return await CssSheetLoader.LoadAsync(source, download, budget, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>PhantomHaze's browser defaults: how elements are displayed before the page's own styles.</summary>
internal static class UserAgentStyles
{
    private const string Css = """
html, address, blockquote, body, center, dialog, div, figure, figcaption, footer, form, header, hr, legend, listing, main,
p, plaintext, pre, search, xmp, article, aside, h1, h2, h3, h4, h5, h6, hgroup, nav, section, dir, dd, dl, dt, menu, ol, ul,
details, summary, fieldset, optgroup, option, frameset, frame { display: block }
li { display: list-item }
table { display: table }
caption { display: table-caption }
colgroup { display: table-column-group }
col { display: table-column }
thead { display: table-header-group }
tbody { display: table-row-group }
tfoot { display: table-footer-group }
tr { display: table-row }
td, th { display: table-cell }
[hidden], area, base, basefont, datalist, head, link, meta, noembed, noframes, param, rp, script, style, template, title { display: none }
dialog:not([open]) { display: none }
details:not([open]) > :not(summary:first-of-type) { display: none }
img, video, canvas, iframe, embed, object, input, select, textarea, button, svg, meter, progress { display: inline-block }
input[type="hidden" i] { display: none }
ruby { display: inline }
rt { display: none }
mark { background-color: #ffff00; color: #000000 }
figure { margin: 1em 40px }
h1 { font-size: 2em; font-weight: bold }
h2 { font-size: 1.5em; font-weight: bold }
h3 { font-size: 1.17em; font-weight: bold }
h4 { font-size: 1em; font-weight: bold }
h5 { font-size: 0.83em; font-weight: bold }
h6 { font-size: 0.67em; font-weight: bold }
small { font-size: smaller }
big { font-size: larger }
code, kbd, samp, pre, tt { font-family: monospace }
button, input[type="submit" i], input[type="button" i], input[type="reset" i] {
  background-color: #efefef; border: 1px solid #767676; border-radius: 3px; padding: 1px 6px; color: #000000;
  font-size: 13.33px; text-align: center
}
input, textarea, select { font-size: 13.33px }
input:not([type="checkbox" i]):not([type="radio" i]):not([type="submit" i]):not([type="button" i]):not([type="reset" i]):not([type="image" i]),
textarea, select { border: 1px solid #767676; background-color: #ffffff; color: #000000; padding: 1px 2px }
input[type="checkbox" i], input[type="radio" i] { margin: 3px 3px 3px 4px }
""";

    public static ParsedCssSheet Sheet { get; } =
        new(new CssSheetSource(Css, null, null, CssOrigin.UserAgent), CssSheetParser.Parse(Css));
}

/// <summary>The markup HTML Renderer draws, with what the page layer needs to make it interactive.</summary>
internal sealed class RenderedPage
{
    public RenderedPage(string html)
    {
        Html = html;
    }

    public string Html { get; }
    public string Title { get; init; } = string.Empty;
    public Uri? BaseUri { get; init; }
    public string? FaviconUrl { get; init; }

    /// <summary>The page background (the html or body background colour, as browsers paint the whole window with it).</summary>
    public string CanvasColor { get; init; } = "#ffffff";

    /// <summary>Form fields, drawn as placeholders that PhantomHaze covers with real text boxes and lists.</summary>
    public IReadOnlyList<PageFormField> Fields { get; init; } = Array.Empty<PageFormField>();

    /// <summary>Inline SVG graphics by the number of their phsvg: image address.</summary>
    public IReadOnlyDictionary<int, PageSvgImage> SvgImages { get; init; } = new Dictionary<int, PageSvgImage>();
}

internal enum PageFormFieldKind
{
    Text,
    Password,
    TextArea,
    Select,
    CheckBox,
    Radio,
}

/// <summary>A form field of the drawn page. Its placeholder has the id "ph-field-{NodeId}".</summary>
internal sealed class PageFormField
{
    public PageFormField(int nodeId, PageFormFieldKind kind)
    {
        NodeId = nodeId;
        Kind = kind;
    }

    public int NodeId { get; }
    public PageFormFieldKind Kind { get; }
    public string Value { get; init; } = string.Empty;
    public string Placeholder { get; init; } = string.Empty;
    public bool Checked { get; init; }
    public bool Disabled { get; init; }
    public bool ReadOnly { get; init; }
    public int MaxLength { get; init; } = -1;
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public int SelectedIndex { get; init; } = -1;
    public string InputType { get; init; } = "text";

    /// <summary>The field's text colour and font size (CSS pixels) from the page's styles.</summary>
    public string? TextColor { get; init; }
    public double FontSizePx { get; init; } = 13.33;

    public string ElementId => "ph-field-" + NodeId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An inline &lt;svg&gt; of the page, drawn by PhantomHaze's SVG renderer.</summary>
internal sealed class PageSvgImage
{
    public PageSvgImage(string markup, string color)
    {
        Markup = markup;
        Color = color;
    }

    public string Markup { get; }

    /// <summary>The colour currentColor stands for (#rrggbb).</summary>
    public string Color { get; }
}

/// <summary>Decodes downloaded pages, stylesheets and scripts: byte order mark, Content-Type charset, &lt;meta charset&gt;, UTF-8.</summary>
internal static class PageText
{
    private static readonly System.Text.RegularExpressions.Regex MetaCharsetRegex = new(
        @"<meta[^>]{0,400}?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]{1,40})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    static PageText()
    {
        // windows-1252, Shift_JIS, GB2312 and the other legacy page encodings.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[] body, string contentType, bool isHtml)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(body, 3, body.Length - 3);
        }

        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(body, 2, body.Length - 2);
        }

        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2);
        }

        string? charset = null;
        foreach (string part in (contentType ?? string.Empty).Split(';'))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                charset = trimmed["charset=".Length..].Trim().Trim('"', '\'');
            }
        }

        if (charset is null && isHtml)
        {
            string head = Encoding.Latin1.GetString(body, 0, Math.Min(body.Length, 4096));
            try
            {
                System.Text.RegularExpressions.Match match = MetaCharsetRegex.Match(head);
                if (match.Success)
                {
                    charset = match.Groups[1].Value;
                }
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // Decoded as UTF-8.
            }
        }

        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Unknown name: UTF-8.
            }
        }

        // A page that declares UTF-16 in a meta tag is really ASCII-compatible (browsers treat it as UTF-8).
        if (encoding is UnicodeEncoding)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(body);
    }
}
