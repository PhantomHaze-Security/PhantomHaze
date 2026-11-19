using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Web View protocols (Settings -> General -> Web View protocol): how pages get from the web into a tab.
//  - WebView2 (default): Microsoft Edge WebView2 loads and runs pages itself, like a normal browser.
//  - OpenHTML: PhantomHaze downloads each page's raw HTML itself and hands it to the tab, which shows it with the site's
//    JavaScript turned off. The page keeps its real address, so links, Back/Forward, Refresh, history and favourites
//    work as usual. Images, styles and other files the page uses still load directly.
// Adding a protocol: add it to WebViewProtocol and WebViewProtocols, then handle it in IsScriptEnabledFor and in
// TryServeDocumentWithProtocol.
public partial class MainForm
{
    private enum WebViewProtocol
    {
        WebView2 = 0,
        OpenHtml = 1,
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
            WebViewProtocol.OpenHtml,
            "OpenHTML",
            "PhantomHaze downloads each page's raw HTML itself and shows it with the site's JavaScript turned off. " +
            "Pages run no site scripts, but sites that need JavaScript will not work properly.",
            Badge: "OpenHTML"),
    ];

    // Pages and files OpenHTML fetches are held in memory; larger files are left to the tab.
    private const int OpenHtmlMaximumBodyBytes = 25 * 1024 * 1024;
    private const int OpenHtmlMaximumRedirects = 20;
    private static readonly TimeSpan OpenHtmlTimeout = TimeSpan.FromSeconds(30);

    // Redirects are not followed here: each hop is handed back to the tab, which requests it again, so web.block
    // rules apply to every address. Cookies come from (and go back to) the tab's own cookie store.
    private readonly HttpClient _openHtmlHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // Each fetch has its own limit for the whole download (OpenHtmlTimeout).
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static WebViewProtocolInfo GetWebViewProtocolInfo(WebViewProtocol protocol)
    {
        return WebViewProtocols.FirstOrDefault(info => info.Protocol == protocol) ?? WebViewProtocols[0];
    }

    /// <summary>Whether pages run their own JavaScript under the protocol (scripts PhantomHaze injects always run).</summary>
    private static bool IsScriptEnabledFor(WebViewProtocol protocol) => protocol != WebViewProtocol.OpenHtml;

    /// <summary>Shows the protocol's badge in the address box (nothing for WebView2).</summary>
    private void UpdateProtocolBadge()
    {
        WebViewProtocolInfo info = GetWebViewProtocolInfo(_settings.WebViewProtocol);
        _protocolBadgeLabel.Text = info.Badge ?? string.Empty;
        _protocolBadgeLabel.Visible = info.Badge is not null;
        _chromeToolTip.SetToolTip(_protocolBadgeLabel, info.Badge is null ? null : $"Web View protocol: {info.Name}\n{info.Description}");
    }

    /// <summary>
    /// Settings were saved with another protocol: every open tab switches to it for its next page, and the tab on
    /// screen reloads so it matches the setting straight away. Other tabs are not reloaded, so a form being filled in
    /// or a page that was the answer to a form is not lost or sent again.
    /// </summary>
    private void ApplyWebViewProtocolToOpenTabs()
    {
        UpdateProtocolBadge();
        bool scriptEnabled = IsScriptEnabledFor(_settings.WebViewProtocol);
        bool reloaded = false;
        foreach (BrowserTab tab in _tabs)
        {
            try
            {
                if (tab.WebView.CoreWebView2 is CoreWebView2 core)
                {
                    core.Settings.IsScriptEnabled = scriptEnabled;
                    tab.OpenHtmlRedirectCount = 0;
                    if (_webViewModeEnabled && ReferenceEquals(tab, _activeTab))
                    {
                        core.Reload();
                        reloaded = true;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // That tab is closing.
            }
        }

        Log($"Web View protocol set to {GetWebViewProtocolInfo(_settings.WebViewProtocol).Name}" +
            (reloaded ? "; the current tab was reloaded and other tabs use it from their next page." : "; open tabs use it from their next page."));
    }

    /// <summary>
    /// Called for every request a tab makes (after web.block rules). Under OpenHTML, the tab's page documents are
    /// fetched by PhantomHaze and handed to the tab. Frames inside a page, everything else, and every request under
    /// WebView2 are left to the tab (frames keep the browser's own cookie rules, such as SameSite).
    /// </summary>
    private void TryServeDocumentWithProtocol(CoreWebView2 core, BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_settings.WebViewProtocol != WebViewProtocol.OpenHtml || !IsOpenHtmlDocumentRequest(e) || !IsPendingPageRequest(tab, e.Request.Uri))
        {
            return;
        }

        // Answered once: a frame of the page that happens to load the same address is left to the tab.
        tab.PendingPageUrl = null;
        CoreWebView2Deferral deferral = e.GetDeferral();
        _ = ServeOpenHtmlDocumentAsync(core, tab, e, deferral);
    }

    private static bool IsOpenHtmlDocumentRequest(CoreWebView2WebResourceRequestedEventArgs e)
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
               string.Equals(OpenHtmlDocuments.WithoutFragment(pending), OpenHtmlDocuments.WithoutFragment(requestUrl), StringComparison.Ordinal);
    }

    private async Task ServeOpenHtmlDocumentAsync(
        CoreWebView2 core,
        BrowserTab tab,
        CoreWebView2WebResourceRequestedEventArgs e,
        CoreWebView2Deferral deferral)
    {
        string url = e.Request.Uri;
        try
        {
            CoreWebView2WebResourceResponse? response = await FetchOpenHtmlDocumentAsync(core, tab, e.Request, url);
            if (response is not null)
            {
                e.Response = response;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException
                                       or OpenHtmlException or UriFormatException)
        {
            try
            {
                string problem = ex is OperationCanceledException ? $"The site did not answer within {OpenHtmlTimeout.TotalSeconds:0} seconds." : ex.Message;
                Log($"Tab {tab.Id} OpenHTML could not load {TrimForLog(url, 220)}: {problem}");
                e.Response = CreateOpenHtmlMessageResponse(core, 502, "Bad Gateway", "OpenHTML could not load this page", url, problem);
            }
            catch (Exception responseError) when (responseError is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The tab closed while the page was loading.
            }
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

    /// <returns>The response for the tab, or null to let the tab load the address itself.</returns>
    private async Task<CoreWebView2WebResourceResponse?> FetchOpenHtmlDocumentAsync(
        CoreWebView2 core,
        BrowserTab tab,
        CoreWebView2WebResourceRequest original,
        string url)
    {
        // One limit for the whole fetch: connecting, the headers and the body (HttpClient.Timeout stops at the headers).
        using var timeout = new CancellationTokenSource(OpenHtmlTimeout);
        var requestUri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", core.Settings.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        string? referer = GetRequestHeader(original, "Referer");
        foreach (string header in new[] { "Accept-Language", "Referer" })
        {
            if (GetRequestHeader(original, header) is string value)
            {
                request.Headers.TryAddWithoutValidation(header, value);
            }
        }

        // Like the browser: SameSite=Strict cookies are not sent when another site links to this page.
        bool crossSite = OpenHtmlDocuments.IsCrossSiteNavigation(GetRequestHeader(original, "Sec-Fetch-Site"), referer, requestUri);
        List<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(url);
        string? cookieHeader = BuildCookieHeader(cookies.Where(cookie => !crossSite || cookie.SameSite != CoreWebView2CookieSameSiteKind.Strict));
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using HttpResponseMessage response = await _openHtmlHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!_tabs.Contains(tab))
        {
            return null;
        }

        if (ApplyOpenHtmlSetCookies(core, requestUri, response) > 0)
        {
            QueueCookieRegistrationCheck(tab);
        }

        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400 && response.Headers.Location is Uri location)
        {
            Uri target = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
            if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
            {
                throw new OpenHtmlException($"The site redirected to an address OpenHTML cannot open ({target.Scheme}:).");
            }

            if (++tab.OpenHtmlRedirectCount > OpenHtmlMaximumRedirects)
            {
                tab.OpenHtmlRedirectCount = 0;
                throw new OpenHtmlException($"The site redirected more than {OpenHtmlMaximumRedirects} times in a row.");
            }

            // The tab goes to the new address itself, so web.block rules and this protocol apply to it again. A
            // zero-second meta refresh replaces this page in the tab's history, so Back skips it.
            Log($"Tab {tab.Id} OpenHTML: {TrimForLog(url, 200)} redirects to {TrimForLog(target.AbsoluteUri, 200)}");
            return CreateOpenHtmlRedirectResponse(core, target);
        }

        tab.OpenHtmlRedirectCount = 0;
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        bool isPage = OpenHtmlDocuments.IsHtmlMediaType(mediaType);

        // Files that are not pages (PDFs, images, downloads) are handed over from this same response, so a link that
        // works only once is not used up by a second request. A file too large to hold in memory is the exception.
        byte[]? body = await ReadOpenHtmlBodyAsync(response.Content, timeout.Token);
        if (body is null)
        {
            if (isPage)
            {
                throw new OpenHtmlException($"The page is larger than {OpenHtmlMaximumBodyBytes / (1024 * 1024)} MB.");
            }

            Log($"Tab {tab.Id} OpenHTML: {TrimForLog(url, 220)} is a file larger than {OpenHtmlMaximumBodyBytes / (1024 * 1024)} MB; the tab downloads it itself.");
            return null;
        }

        string headers = OpenHtmlDocuments.BuildResponseHeaders(
            response.Content.Headers.ContentType?.ToString() ?? (isPage ? "text/html" : "application/octet-stream"),
            EnumerateResponseHeaders(response));
        Log($"Tab {tab.Id} OpenHTML: fetched {TrimForLog(url, 220)} ({status}, {FormatOpenHtmlSize(body.Length)}{(isPage ? string.Empty : ", " + (mediaType ?? "file"))})");
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(body),
            status,
            OpenHtmlDocuments.SanitizeReasonPhrase(response.ReasonPhrase, status),
            headers);
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

    /// <returns>The body, or null when it is larger than OpenHtmlMaximumBodyBytes.</returns>
    private static async Task<byte[]?> ReadOpenHtmlBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > OpenHtmlMaximumBodyBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > OpenHtmlMaximumBodyBytes)
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
    private int ApplyOpenHtmlSetCookies(CoreWebView2 core, Uri uri, HttpResponseMessage response)
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
                foreach (OpenHtmlDocuments.CookieChange change in OpenHtmlDocuments.ParseSetCookieHeader(uri, header))
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

    /// <summary>A redirect, as a page that sends the tab to the new address (meta refresh works with scripts off).</summary>
    private static CoreWebView2WebResourceResponse CreateOpenHtmlRedirectResponse(CoreWebView2 core, Uri target)
    {
        return CreateHtmlResponse(core, 200, "OK", OpenHtmlDocuments.BuildRedirectPage(target.AbsoluteUri));
    }

    private static CoreWebView2WebResourceResponse CreateOpenHtmlMessageResponse(
        CoreWebView2 core,
        int status,
        string reason,
        string title,
        string url,
        string message)
    {
        return CreateHtmlResponse(core, status, reason, OpenHtmlDocuments.BuildMessagePage(title, url, message));
    }

    private static CoreWebView2WebResourceResponse CreateHtmlResponse(CoreWebView2 core, int status, string reason, string html)
    {
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(html)),
            status,
            reason,
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    private static string FormatOpenHtmlSize(int bytes)
    {
        return bytes < 1024 ? $"{bytes} bytes" : FormattableString.Invariant($"{bytes / 1024.0:0.#} KB");
    }

    /// <summary>A problem with a page OpenHTML fetched (too large, too many redirects, ...); shown on the error page.</summary>
    private sealed class OpenHtmlException : Exception
    {
        public OpenHtmlException(string message)
            : base(message)
        {
        }
    }
}
