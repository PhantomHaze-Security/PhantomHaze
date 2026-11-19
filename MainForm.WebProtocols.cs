using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Web View protocols (Settings -> General -> Web View protocol): how pages get from the web into a tab.
//  - WebView2 (default): Microsoft Edge WebView2 loads and runs pages itself, like a normal browser.
//  - HazeFetch: PhantomHaze downloads each page's raw HTML itself and hands it to the tab, which shows it and runs its
//    JavaScript like any other page. The page keeps its real address, so links, Back/Forward, Refresh, history,
//    favourites and the page's own scripts work as usual. Images, scripts, styles and other files the page uses, and
//    requests its scripts make, still load directly. Sites cannot install service workers while HazeFetch is on (see
//    HazeFetchPageScript), so the pages they open keep coming through PhantomHaze.
// Adding a protocol: add it to WebViewProtocol and WebViewProtocols, then handle it in TryServeDocumentWithProtocol.
public partial class MainForm
{
    private enum WebViewProtocol
    {
        WebView2 = 0,
        HazeFetch = 1,
    }

    /// <param name="Badge">Shown in the address box while the protocol is in use (null: no badge).</param>
    private sealed record WebViewProtocolInfo(WebViewProtocol Protocol, string Name, string Description, string? Badge);

    private static readonly WebViewProtocolInfo[] WebViewProtocols =
    [
        new(
            WebViewProtocol.WebView2,
            "WebView2 (default)",
            "Pages load and run in Microsoft Edge WebView2, like in a normal browser. Best for most sites.",
            Badge: null),
        new(
            WebViewProtocol.HazeFetch,
            "HazeFetch",
            "PhantomHaze downloads each page itself and hands it to the tab, which shows it and runs its JavaScript like " +
            "any other page. Images, scripts, games and other files the page uses still load directly, and sites that " +
            "need the browser's own loading (bot checks, sign-in prompts) are handed back to it automatically.",
            Badge: "HazeFetch"),
    ];

    // Pages and files HazeFetch fetches are held in memory; larger files are left to the tab.
    private const int HazeFetchMaximumBodyBytes = 25 * 1024 * 1024;
    private const int HazeFetchMaximumRedirects = 20;
    private static readonly TimeSpan HazeFetchTimeout = TimeSpan.FromSeconds(20);

    // Sites HazeFetch hands back to the tab for the rest of the session: they run a bot check that compares the
    // connection with the browser's, or ask for HTTP sign-in (see HazeFetchDocuments.NeedsBrowserLoading).
    private readonly HashSet<string> _hazeFetchBrowserSites = new(StringComparer.OrdinalIgnoreCase);

    // Redirects are not followed here: each hop is handed back to the tab, which requests it again, so web.block
    // rules apply to every address. Cookies come from (and go back to) the tab's own cookie store.
    private readonly HttpClient _hazeFetchHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
    })
    {
        // Each fetch has its own limit for the whole download (HazeFetchTimeout).
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static WebViewProtocolInfo GetWebViewProtocolInfo(WebViewProtocol protocol)
    {
        return WebViewProtocols.FirstOrDefault(info => info.Protocol == protocol) ?? WebViewProtocols[0];
    }

    /// <summary>Shows the protocol's badge in the address box (nothing for WebView2).</summary>
    private void UpdateProtocolBadge()
    {
        WebViewProtocolInfo info = GetWebViewProtocolInfo(_settings.WebViewProtocol);
        _protocolBadgeLabel.Text = info.Badge ?? string.Empty;
        _protocolBadgeLabel.Visible = info.Badge is not null;
        _chromeToolTip.SetToolTip(_protocolBadgeLabel, info.Badge is null ? null : $"Web View protocol: {info.Name}\n{info.Description}");
    }

    /// <summary>
    /// Runs in every page and frame of a tab while HazeFetch is on. With JavaScript running, a site could install a
    /// service worker, which answers the site's later page loads inside the browser: they would bypass PhantomHaze's
    /// own fetch and HazeFetch would quietly stop applying to that site. Registering one fails instead, as it does when
    /// a browser does not allow service workers, so sites carry on without one.
    /// </summary>
    private const string HazeFetchPageScript =
        "(() => {" +
        "try {" +
        "if (typeof ServiceWorkerContainer === 'undefined') { return; }" +
        "const message = 'Service workers are turned off while PhantomHaze uses the HazeFetch protocol.';" +
        "Object.defineProperty(ServiceWorkerContainer.prototype, 'register', {" +
        "configurable: true, writable: true," +
        "value: function register() { return Promise.reject(new DOMException(message, 'SecurityError')); }," +
        "});" +
        "} catch (_) { }" +
        "})();";

    /// <summary>Adds the HazeFetch page script to the tab while HazeFetch is on, and removes it otherwise.</summary>
    private async Task UpdateHazeFetchPageScriptAsync(BrowserTab tab)
    {
        if (tab.WebView.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        bool wanted = _settings.WebViewProtocol == WebViewProtocol.HazeFetch;
        if (wanted && tab.HazeFetchScriptId is null)
        {
            string id = await core.AddScriptToExecuteOnDocumentCreatedAsync(HazeFetchPageScript);

            // The protocol can change again (or another call add the script) while this one is being added.
            if (_settings.WebViewProtocol == WebViewProtocol.HazeFetch && tab.HazeFetchScriptId is null)
            {
                tab.HazeFetchScriptId = id;
            }
            else
            {
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            }
        }
        else if (!wanted && tab.HazeFetchScriptId is string existing)
        {
            tab.HazeFetchScriptId = null;
            core.RemoveScriptToExecuteOnDocumentCreated(existing);
        }
    }

    /// <summary>
    /// Settings were saved with another protocol: every open tab uses it from its next page, and the tab on screen
    /// reloads so it matches the setting straight away. Other tabs are not reloaded, so a form being filled in or a
    /// page that was the answer to a form is not lost or sent again. Switching to HazeFetch also removes the service
    /// workers sites installed earlier in the session (see HazeFetchPageScript).
    /// </summary>
    private async Task ApplyWebViewProtocolToOpenTabsAsync()
    {
        UpdateProtocolBadge();
        // Sites handed to the browser earlier get another chance with the new choice.
        _hazeFetchBrowserSites.Clear();
        WebViewProtocol protocol = _settings.WebViewProtocol;
        foreach (BrowserTab tab in _tabs.ToList())
        {
            try
            {
                tab.HazeFetchRedirectCount = 0;
                await UpdateHazeFetchPageScriptAsync(tab);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // That tab is closing.
            }
        }

        if (protocol == WebViewProtocol.HazeFetch &&
            _tabs.Select(tab => tab.WebView.CoreWebView2).FirstOrDefault(core => core is not null) is CoreWebView2 profileCore)
        {
            try
            {
                // Every tab shares one profile, so this covers all of them.
                await profileCore.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.ServiceWorkers);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                Log($"Service workers could not be removed for HazeFetch: {ex.Message}");
            }
        }

        // Settings may have been saved again while this ran; the newest save does its own reload.
        if (protocol != _settings.WebViewProtocol)
        {
            return;
        }

        bool reloaded = false;
        try
        {
            if (_webViewModeEnabled && _activeTab?.WebView.CoreWebView2 is CoreWebView2 activeCore)
            {
                activeCore.Reload();
                reloaded = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The tab is closing.
        }

        Log($"Web View protocol set to {GetWebViewProtocolInfo(protocol).Name}" +
            (reloaded ? "; the current tab was reloaded and other tabs use it from their next page." : "; open tabs use it from their next page."));
    }

    /// <summary>
    /// Called for every request a tab makes (after web.block rules). Under HazeFetch, the tab's page documents are
    /// fetched by PhantomHaze and handed to the tab. Frames inside a page, everything else, every request under
    /// WebView2 and pages of sites that need the browser's own loading (see HazeFetchBrowserSites) are left to the tab.
    /// </summary>
    private void TryServeDocumentWithProtocol(CoreWebView2 core, BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_settings.WebViewProtocol != WebViewProtocol.HazeFetch || !IsHazeFetchDocumentRequest(e) || !IsPendingPageRequest(tab, e.Request.Uri))
        {
            return;
        }

        // Answered once: a frame of the page that happens to load the same address is left to the tab.
        tab.PendingPageUrl = null;
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri) && _hazeFetchBrowserSites.Contains(uri.Host))
        {
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        _ = ServeHazeFetchDocumentAsync(core, tab, e, deferral);
    }

    private static bool IsHazeFetchDocumentRequest(CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (e.ResourceContext != CoreWebView2WebResourceContext.Document)
        {
            return false;
        }

        CoreWebView2WebResourceRequest request = e.Request;
        return string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
               Uri.TryCreate(request.Uri, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Whether a document request is the tab's own page (the navigation NavigationStarting just announced) rather than
    /// a frame inside it. The request address has no #fragment, so it is compared without one.
    /// </summary>
    private static bool IsPendingPageRequest(BrowserTab tab, string requestUrl)
    {
        return tab.PendingPageUrl is string pending &&
               string.Equals(HazeFetchDocuments.WithoutFragment(pending), HazeFetchDocuments.WithoutFragment(requestUrl), StringComparison.Ordinal);
    }

    private async Task ServeHazeFetchDocumentAsync(
        CoreWebView2 core,
        BrowserTab tab,
        CoreWebView2WebResourceRequestedEventArgs e,
        CoreWebView2Deferral deferral)
    {
        string url = e.Request.Uri;
        try
        {
            CoreWebView2WebResourceResponse? response = await FetchHazeFetchDocumentAsync(core, tab, e.Request, url);
            if (response is not null)
            {
                e.Response = response;
            }
        }
        catch (HazeFetchException ex) when (ex.SiteAnswered)
        {
            // The site already answered (a redirect loop, a broken download): asking it again could repeat what the
            // request did or use up a link that works only once, so the tab shows what went wrong instead.
            try
            {
                Log($"Tab {tab.Id} HazeFetch could not load {TrimForLog(url, 220)}: {ex.Message}");
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(HazeFetchDocuments.BuildErrorPage(url, ex.Message))),
                    502,
                    "Bad Gateway",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            }
            catch (Exception responseError) when (responseError is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The tab closed while the page was loading.
            }
        }
        catch (Exception ex) when (ex is (HttpRequestException or OperationCanceledException or IOException or InvalidOperationException
                                        or HazeFetchException or UriFormatException) and not ObjectDisposedException)
        {
            // Smooth browsing first: when the site could not be reached at all, the tab tries itself (with the
            // browser's own error page if the site really is unreachable).
            string problem = ex is OperationCanceledException ? $"no answer within {HazeFetchTimeout.TotalSeconds:0} seconds" : ex.Message;
            Log($"Tab {tab.Id} HazeFetch could not load {TrimForLog(url, 220)} ({problem}); the tab loads it itself.");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
            // The tab (or the whole window) closed while the page was loading.
        }
        finally
        {
            try
            {
                deferral.Complete();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The tab closed while the page was loading.
            }
        }
    }

    // Request headers the tab's own request carries that HazeFetch does not copy: HttpClient sets them itself (host,
    // connection, compression), PhantomHaze builds them (cookies), or they would let the site answer with less than the
    // whole page - "304 Not Modified" (an empty body) or "206 Partial Content" - which the tab would show as the page.
    private static readonly HashSet<string> HazeFetchSkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Content-Length",
        "Accept-Encoding", "Upgrade", "TE", "Trailer",
        "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since", "If-Range", "Range",
    };

    /// <returns>The response for the tab, or null to let the tab load the address itself.</returns>
    private async Task<CoreWebView2WebResourceResponse?> FetchHazeFetchDocumentAsync(
        CoreWebView2 core,
        BrowserTab tab,
        CoreWebView2WebResourceRequest original,
        string url)
    {
        // One limit for the whole fetch: connecting, the headers and the body (HttpClient.Timeout stops at the headers).
        using var timeout = new CancellationTokenSource(HazeFetchTimeout);
        var requestUri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri)
        {
            // HTTP/2 (multiplexed, header compression) where the site offers it, like the browser.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // The browser's own request headers (user agent, client hints, Sec-Fetch-*, language, referrer...), so sites
        // answer exactly as they would answer the browser.
        foreach (KeyValuePair<string, string> header in EnumerateRequestHeaders(original))
        {
            if (!HazeFetchSkippedRequestHeaders.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (!request.Headers.Contains("User-Agent"))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", core.Settings.UserAgent);
        }

        if (!request.Headers.Contains("Accept"))
        {
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        }

        // Like the browser: SameSite=Strict cookies are not sent when another site links to this page.
        bool crossSite = HazeFetchDocuments.IsCrossSiteNavigation(GetRequestHeader(original, "Sec-Fetch-Site"), GetRequestHeader(original, "Referer"), requestUri);
        List<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(url);
        string? cookieHeader = BuildCookieHeader(cookies.Where(cookie => !crossSite || cookie.SameSite != CoreWebView2CookieSameSiteKind.Strict));
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using HttpResponseMessage response = await _hazeFetchHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!_tabs.Contains(tab))
        {
            return null;
        }

        // Bot checks (Cloudflare, DataDome, ...) compare the connection with the browser, and sign-in prompts (HTTP
        // authentication) need the browser: those sites are loaded by the tab itself for the rest of the session.
        if (HazeFetchDocuments.NeedsBrowserLoading((int)response.StatusCode, EnumerateResponseHeaders(response), out string reason))
        {
            if (_hazeFetchBrowserSites.Add(requestUri.Host))
            {
                Log($"Tab {tab.Id} HazeFetch: {requestUri.Host} {reason}; the tab loads this site itself for the rest of the session.");
            }

            return null;
        }

        int status = (int)response.StatusCode;
        bool tooLarge = status is < 300 or >= 400 && response.Content.Headers.ContentLength is long declared && declared > HazeFetchMaximumBodyBytes;
        if (tooLarge || status is 304 or 206)
        {
            // A file too large to hold in memory (or, should a site send one anyway, a reply that is not the whole page):
            // the tab requests it again itself, so a large download shows the usual progress. This response's cookies
            // are not stored, because the tab's own request gets them again.
            Log($"Tab {tab.Id} HazeFetch: {TrimForLog(url, 220)} " +
                (tooLarge ? $"is larger than {HazeFetchMaximumBodyBytes / (1024 * 1024)} MB" : $"answered {status}") + "; the tab loads it itself.");
            return null;
        }

        if (ApplyHazeFetchSetCookies(core, requestUri, response) > 0)
        {
            QueueCookieRegistrationCheck(tab);
        }

        if (status is >= 300 and < 400 && response.Headers.Location is Uri location)
        {
            Uri target = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
            // A redirect to an app link (mailto:, sign-in callbacks of apps, ...) is followed the same way; the tab then
            // asks before opening the app (a protected PhantomHaze prompt).
            if (++tab.HazeFetchRedirectCount > HazeFetchMaximumRedirects)
            {
                // Stops a redirect loop for good (the next navigation starts counting again).
                tab.HazeFetchRedirectCount = 0;
                throw new HazeFetchException($"the site redirected more than {HazeFetchMaximumRedirects} times in a row", siteAnswered: true);
            }

            // The tab goes to the new address itself, so web.block rules and this protocol apply to it again. A
            // zero-second meta refresh replaces this page in the tab's history, so Back skips it.
            Log($"Tab {tab.Id} HazeFetch: {TrimForLog(url, 200)} redirects to {TrimForLog(target.AbsoluteUri, 200)}");
            return CreateHazeFetchRedirectResponse(core, target);
        }

        tab.HazeFetchRedirectCount = 0;
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        bool isPage = HazeFetchDocuments.IsHtmlMediaType(mediaType);

        // Files that are not pages (PDFs, images, downloads) are handed over from this same response, so a link that
        // works only once is not used up by a second request. A file too large to hold in memory is the exception.
        byte[]? body;
        try
        {
            body = await ReadHazeFetchBodyAsync(response.Content, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            throw new HazeFetchException(
                ex is OperationCanceledException ? $"the site stopped sending the page (no data within {HazeFetchTimeout.TotalSeconds:0} seconds)" : $"the download broke off ({ex.Message})",
                siteAnswered: true);
        }

        if (body is null)
        {
            Log($"Tab {tab.Id} HazeFetch: {TrimForLog(url, 220)} is larger than {HazeFetchMaximumBodyBytes / (1024 * 1024)} MB; the tab loads it itself.");
            return null;
        }

        string headers = HazeFetchDocuments.BuildResponseHeaders(
            response.Content.Headers.ContentType?.ToString() ?? (isPage ? "text/html" : "application/octet-stream"),
            EnumerateResponseHeaders(response));
        Log($"Tab {tab.Id} HazeFetch: fetched {TrimForLog(url, 220)} ({status}, HTTP/{response.Version}, {FormatHazeFetchSize(body.Length)}{(isPage ? string.Empty : ", " + (mediaType ?? "file"))})");
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(body),
            status,
            HazeFetchDocuments.SanitizeReasonPhrase(response.ReasonPhrase, status),
            headers);
    }

    private static IEnumerable<KeyValuePair<string, string>> EnumerateRequestHeaders(CoreWebView2WebResourceRequest request)
    {
        try
        {
            return request.Headers.ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }
    }

    private static string? GetRequestHeader(CoreWebView2WebResourceRequest request, string name)
    {
        try
        {
            return request.Headers.Contains(name) ? request.Headers.GetHeader(name) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            return null;
        }
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> EnumerateResponseHeaders(HttpResponseMessage response)
    {
        return response.Headers.Concat(response.Content.Headers);
    }

    /// <returns>The body, or null when it is larger than HazeFetchMaximumBodyBytes.</returns>
    private static async Task<byte[]?> ReadHazeFetchBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > HazeFetchMaximumBodyBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > HazeFetchMaximumBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The Cookie header for the tab's cookies that apply to the address (null when there are none).</summary>
    private static string? BuildCookieHeader(IEnumerable<CoreWebView2Cookie> cookies)
    {
        string header = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        return header.Length == 0 ? null : header;
    }

    /// <summary>
    /// Stores the page's Set-Cookie headers in the tab's cookie store (the response the tab gets has none), so logins
    /// and cookie banners work and "Save cookies across sessions" sees them. Returns how many cookies changed.
    /// </summary>
    private int ApplyHazeFetchSetCookies(CoreWebView2 core, Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return 0;
        }

        int changed = 0;
        foreach (string header in values)
        {
            try
            {
                foreach (HazeFetchDocuments.CookieChange change in HazeFetchDocuments.ParseSetCookieHeader(uri, header))
                {
                    if (change.Cookie is Cookie cookie)
                    {
                        CoreWebView2Cookie webCookie = core.CookieManager.CreateCookieWithSystemNetCookie(cookie);
                        if (change.SameSite is CoreWebView2CookieSameSiteKind sameSite)
                        {
                            webCookie.SameSite = sameSite;
                        }

                        core.CookieManager.AddOrUpdateCookie(webCookie);
                    }
                    else
                    {
                        // An expired cookie deletes the cookie with that name, domain and path.
                        core.CookieManager.DeleteCookiesWithDomainAndPath(change.Name, change.Domain, change.Path);
                    }

                    changed++;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or COMException or CookieException)
            {
                // One malformed cookie does not stop the page from loading.
            }
        }

        return changed;
    }

    /// <summary>A redirect, as a page that sends the tab to the new address (a meta refresh, which needs no script).</summary>
    private static CoreWebView2WebResourceResponse CreateHazeFetchRedirectResponse(CoreWebView2 core, Uri target)
    {
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(HazeFetchDocuments.BuildRedirectPage(target.AbsoluteUri))),
            200,
            "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    private static string FormatHazeFetchSize(int bytes)
    {
        return bytes < 1024 ? $"{bytes} bytes" : FormattableString.Invariant($"{bytes / 1024.0:0.#} KB");
    }

    /// <summary>A problem with a page HazeFetch fetched (too many redirects, a broken download, ...).</summary>
    private sealed class HazeFetchException : Exception
    {
        public HazeFetchException(string message, bool siteAnswered)
            : base(message)
        {
            SiteAnswered = siteAnswered;
        }

        /// <summary>True when the site already answered, so the request must not be sent again.</summary>
        public bool SiteAnswered { get; }
    }
}
