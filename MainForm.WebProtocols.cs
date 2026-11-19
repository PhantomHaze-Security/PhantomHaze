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

    private const int OpenHtmlMaximumDocumentBytes = 10 * 1024 * 1024;
    private const int OpenHtmlMaximumRedirects = 20;

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
        Timeout = TimeSpan.FromSeconds(30),
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
    /// Settings were saved with another protocol: every open tab switches to it and reloads, so what is on screen
    /// matches the setting straight away.
    /// </summary>
    private void ApplyWebViewProtocolToOpenTabs()
    {
        UpdateProtocolBadge();
        bool scriptEnabled = IsScriptEnabledFor(_settings.WebViewProtocol);
        int reloaded = 0;
        foreach (BrowserTab tab in _tabs)
        {
            try
            {
                if (tab.WebView.CoreWebView2 is CoreWebView2 core)
                {
                    core.Settings.IsScriptEnabled = scriptEnabled;
                    tab.OpenHtmlRedirectCount = 0;
                    core.Reload();
                    reloaded++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // That tab is closing.
            }
        }

        Log($"Web View protocol set to {GetWebViewProtocolInfo(_settings.WebViewProtocol).Name}" +
            (reloaded > 0 ? $"; {reloaded} open tab(s) reloaded." : "."));
    }

    /// <summary>
    /// Called for every request a tab makes (after web.block rules). Under OpenHTML, page documents are fetched by
    /// PhantomHaze and handed to the tab; everything else, and every request under WebView2, is left to the tab.
    /// </summary>
    private void TryServeDocumentWithProtocol(CoreWebView2 core, BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_settings.WebViewProtocol != WebViewProtocol.OpenHtml || !IsOpenHtmlDocumentRequest(e))
        {
            return;
        }

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                       or OpenHtmlException or UriFormatException)
        {
            try
            {
                Log($"Tab {tab.Id} OpenHTML could not load {TrimForLog(url, 220)}: {ex.Message}");
                e.Response = CreateOpenHtmlMessageResponse(core, 502, "Bad Gateway", "OpenHTML could not load this page", url, ex.Message);
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

    /// <returns>The response for the tab, or null to let the tab load the address itself (files that are not HTML).</returns>
    private async Task<CoreWebView2WebResourceResponse?> FetchOpenHtmlDocumentAsync(
        CoreWebView2 core,
        BrowserTab tab,
        CoreWebView2WebResourceRequest original,
        string url)
    {
        var requestUri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", core.Settings.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        foreach (string header in new[] { "Accept-Language", "Referer" })
        {
            if (original.Headers.Contains(header))
            {
                request.Headers.TryAddWithoutValidation(header, original.Headers.GetHeader(header));
            }
        }

        string? cookieHeader = BuildCookieHeader(await core.CookieManager.GetCookiesAsync(url));
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using HttpResponseMessage response = await _openHtmlHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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

            // The tab goes to the new address itself, so web.block rules and this protocol apply to it again.
            Log($"Tab {tab.Id} OpenHTML: {TrimForLog(url, 200)} redirects to {TrimForLog(target.AbsoluteUri, 200)}");
            return CreateOpenHtmlRedirectResponse(core, target);
        }

        tab.OpenHtmlRedirectCount = 0;
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!OpenHtmlDocuments.IsHtmlMediaType(mediaType))
        {
            // PDFs, images, downloads and other files: the tab loads them itself.
            return null;
        }

        byte[] body = await ReadOpenHtmlBodyAsync(response.Content);
        string contentType = response.Content.Headers.ContentType?.ToString() ?? "text/html";
        Log($"Tab {tab.Id} OpenHTML: fetched {TrimForLog(url, 220)} ({status}, {FormatOpenHtmlSize(body.Length)})");
        return core.Environment.CreateWebResourceResponse(
            new MemoryStream(body),
            status,
            OpenHtmlDocuments.SanitizeReasonPhrase(response.ReasonPhrase, status),
            $"Content-Type: {contentType}\r\nCache-Control: no-store");
    }

    private static async Task<byte[]> ReadOpenHtmlBodyAsync(HttpContent content)
    {
        if (content.Headers.ContentLength is long declared && declared > OpenHtmlMaximumDocumentBytes)
        {
            throw new OpenHtmlException($"The page is larger than {OpenHtmlMaximumDocumentBytes / (1024 * 1024)} MB.");
        }

        await using Stream stream = await content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > OpenHtmlMaximumDocumentBytes)
            {
                throw new OpenHtmlException($"The page is larger than {OpenHtmlMaximumDocumentBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The Cookie header for the tab's cookies that apply to the address (null when there are none).</summary>
    internal static string? BuildCookieHeader(IEnumerable<CoreWebView2Cookie> cookies)
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
                        // An expired cookie deletes the cookie of that name.
                        core.CookieManager.DeleteCookies(change.Name, uri.AbsoluteUri);
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
