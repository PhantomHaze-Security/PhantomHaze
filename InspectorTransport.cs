using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Gateway HTML often already has gateway-relative links. Absolute references to other sites still go through
// the selected template, including references introduced by the edit or by an imported stylesheet.
internal sealed class InspectorGatewayResources(IPageResourceFetcher transport, WebProtocolAddressTemplate template, Uri gateway, WebRequestRules rules) : IPageResourceFetcher
{
    public string Route(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? target) &&
        !string.Equals(target.GetLeftPart(UriPartial.Authority), gateway.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
        ? template.BuildAddress(url) : url;

    public async Task<PageResource?> FetchAsync(PageResourceRequest request, CancellationToken token)
    {
        if (!WebProtocolPage.IsWebAddress(request.Url) || rules.IsBlocked(request.Url, out _)) return null;
        string routed = Route(request.Url);
        PageResource? response = await transport.FetchAsync(request with { Url = routed }, token).ConfigureAwait(false);
        return response is null ? null : response with { Url = template.ToPageUrl(response.Url) };
    }
}

// Native tabs stay alive while HTMLRenderer draws an edit, so assets use the tab's browser session and proxy.
internal sealed class InspectorTransport(WebProtocolPage browser) : IRenderedPageTransport
{
    private volatile WebRequestRules _rules = WebRequestRules.None;
    public string ProtocolName => browser.Protocol.DisplayName;
    public CookieContainer Cookies { get; } = new();
    public PageSessionStorage Storage { get; } = new();
    public void CancelDocument() { }
    public void UseRules(WebRequestRules rules) => _rules = rules;
    public Task<FetchedDocument> FetchDocumentAsync(PageRequest request, IWebProtocolHost host, string folder, CancellationToken token) =>
        throw new InvalidOperationException("Page navigation belongs to the originating browser.");

    public async Task<PageResource?> FetchAsync(PageResourceRequest request, CancellationToken token)
    {
        if (!WebProtocolPage.IsWebAddress(request.Url) || _rules.IsBlocked(request.Url, out _)) return null;
        try
        {
            return await browser.View.InvokeAsync<PageResource?>(async ct => await browser.ReadResourceAsync(request, ct), token);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or IOException or TimeoutException)
        {
            return null;
        }
    }
}

internal sealed partial class WebView2ProtocolPage
{
    public override async Task<PageResource?> ReadResourceAsync(PageResourceRequest request, CancellationToken token)
    {
        if (Core is not { } core) return null;
        // Fetch in an isolated world so page scripts cannot replace fetch or its response methods.
        var frames = await Command("Page.getFrameTree", new { });
        string frameId = frames.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
        var world = await Command("Page.createIsolatedWorld", new { frameId, worldName = "PhantomHaze HTML inspector" });
        int contextId = world.GetProperty("executionContextId").GetInt32();
        if (request.Kind == PageResourceKind.Data)
        {
            var headers = (request.Headers ?? []).Where(h => HtmlRendererResources.IsAllowedRequestHeader(h.Key, h.Value)).ToArray();
            string arguments = JsonSerializer.Serialize(new { url = request.Url, method = request.Method,
                headers = headers.Select(h => new[] { h.Key, h.Value }), credentials = request.SendCookies ? "same-origin" : "omit",
                body = request.Body is null ? null : Convert.ToBase64String(request.Body), limit = request.MaximumBytes });
            // fetch enforces the page origin's CORS rules. Stream the response to enforce the same size cap as the renderer.
            const string fetch = """
                async a => {
                  const body = a.body === null ? undefined : Uint8Array.from(atob(a.body), c => c.charCodeAt(0));
                  const r = await fetch(a.url, {method:a.method, headers:a.headers, body, credentials:a.credentials});
                  const reader = r.body?.getReader();
                  let size = 0, binary = '';
                  while (reader) {
                    const {done, value} = await reader.read();
                    if (done) break;
                    if ((size += value.length) > a.limit) { await reader.cancel(); return null; }
                    for (let i = 0; i < value.length; i += 8192)
                      binary += String.fromCharCode(...value.subarray(i, i + 8192));
                  }
                  return {url:r.url, status:r.status, type:r.headers.get('content-type') || '', bytes:btoa(binary),
                          headers:Array.from(r.headers), statusText:r.statusText};
                }
                """;
            var result = await Command("Runtime.evaluate", new { expression = "(" + fetch + ")(" + arguments + ")",
                contextId, awaitPromise = true, returnByValue = true });
            if (!result.GetProperty("result").TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return null;
            return new PageResource(value.GetProperty("url").GetString()!, value.GetProperty("status").GetInt32(),
                value.GetProperty("type").GetString()!, value.GetProperty("bytes").GetBytesFromBase64())
            {
                StatusText = value.GetProperty("statusText").GetString() ?? "",
                Headers = value.GetProperty("headers").EnumerateArray().Select(h => KeyValuePair.Create(h[0].GetString()!, h[1].GetString()!)).ToList(),
            };
        }

        // Observe the response to this particular browser request, including its final address after redirects.
        // A zero-quality media type identifies it without changing the formats accepted or triggering a CORS preflight.
        string marker = "x/ph-" + Guid.NewGuid().ToString("N")[..16] + ";q=0";
        string accept = HtmlRendererResources.GetAcceptHeader(request.Kind) + "," + marker;
        var received = new TaskCompletionSource<(string Url, CoreWebView2WebResourceResponseView Response)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (e.Response.StatusCode is >= 300 and < 400 || !e.Request.Headers.Contains("Accept")) return;
            if (e.Request.Headers.GetHeader("Accept").Contains(marker, StringComparison.Ordinal))
                received.TrySetResult((e.Request.Uri, e.Response));
        }
        core.WebResourceResponseReceived += OnResponse;
        try
        {
            var options = new { mode = "no-cors", credentials = request.SendCookies ? "same-origin" : "omit", headers = new { Accept = accept } };
            string expression = $"fetch({JsonSerializer.Serialize(request.Url)}, {JsonSerializer.Serialize(options)}).then(r => r.status)";
            var fetchResult = await Command("Runtime.evaluate", new { expression, contextId, awaitPromise = true });
            if (fetchResult.TryGetProperty("exceptionDetails", out _)) return null;
            var (url, response) = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            if (Host.IsBlocked(url, out _) || response.StatusCode is < 200 or >= 400) return null;
            if (response.Headers.Contains("Content-Length") && long.TryParse(response.Headers.GetHeader("Content-Length"), out long length)
                && length > request.MaximumBytes) return null;
            string mime = response.Headers.Contains("Content-Type") ? response.Headers.GetHeader("Content-Type") : "";
            using Stream? content = await response.GetContentAsync();
            if (content is null) return null;
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[32768];
            int read;
            while ((read = await content.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > request.MaximumBytes) return null;
                bytes.Write(buffer, 0, read);
            }
            return new PageResource(url, response.StatusCode, mime, bytes.ToArray());
        }
        finally
        {
            if (!IsDisposed) core.WebResourceResponseReceived -= OnResponse;
        }

        async Task<JsonElement> Command(string method, object parameters)
        {
            token.ThrowIfCancellationRequested();
            string json = await core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters)).WaitAsync(TimeSpan.FromSeconds(30), token);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}

// Internet Explorer uses WinINet. Follow redirects explicitly so extensions can check every destination.
internal static class LegacyInspectorResources
{
    public static PageResource? Read(PageResourceRequest request, WebRequestRules rules, CancellationToken token)
    {
        using var session = InternetOpen(PageEngineDefaults.UserAgent, 0, null, null, 0);
        if (session.IsInvalid) return null;
        Uri uri = new(request.Url);
        string method = request.Method;
        byte[]? body = request.Body;
        for (int redirects = 0; redirects <= 10; redirects++)
        {
            token.ThrowIfCancellationRequested();
            if (!WebProtocolPage.IsWebAddress(uri.AbsoluteUri) || rules.IsBlocked(uri.AbsoluteUri, out _)) return null;
            using var connection = InternetConnect(session, uri.IdnHost, (ushort)uri.Port, null, null, 3, 0, IntPtr.Zero);
            if (connection.IsInvalid) return null;
            const uint noRedirect = 0x00200000, noUi = 0x200, noCacheWrite = 0x04000000, secure = 0x00800000, noCookies = 0x00080000;
            uint flags = noRedirect | noUi | noCacheWrite | (uri.Scheme == "https" ? secure : 0) |
                (request.SendCookies && uri.Authority == new Uri(request.Url).Authority ? 0 : noCookies);
            using var http = HttpOpenRequest(connection, method, uri.PathAndQuery, null, request.Referrer, IntPtr.Zero, flags, IntPtr.Zero);
            if (http.IsInvalid) return null;
            string headers = "Accept: " + HtmlRendererResources.GetAcceptHeader(request.Kind) + "\r\n";
            if (redirects == 0 && request.Headers is not null)
                foreach (var h in request.Headers.Where(h => HtmlRendererResources.IsAllowedRequestHeader(h.Key, h.Value)))
                    headers += h.Key + ": " + h.Value + "\r\n";
            if (!HttpSendRequest(http, headers, headers.Length, body, body?.Length ?? 0)) return null;
            if (!int.TryParse(Header(http, 19), out int status)) return null;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                string location = Header(http, 33);
                if (location.Length == 0 || !Uri.TryCreate(uri, location, out var next) || (uri.Scheme == "https" && next.Scheme != "https")) return null;
                if (status is 301 or 302 or 303) { method = "GET"; body = null; }
                uri = next;
                continue;
            }
            using var output = new MemoryStream();
            byte[] buffer = new byte[32768];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!InternetReadFile(http, buffer, buffer.Length, out int count)) return null;
                if (count == 0) break;
                if (output.Length + count > request.MaximumBytes) return null;
                output.Write(buffer, 0, count);
            }
            return new PageResource(uri.AbsoluteUri, status, Header(http, 1), output.ToArray());
        }
        return null;
    }

    private static string Header(InternetHandle handle, uint field)
    {
        var value = new StringBuilder(4096);
        int bytes = value.Capacity * sizeof(char);
        return HttpQueryInfo(handle, field, value, ref bytes, IntPtr.Zero) ? value.ToString() : "";
    }

    private sealed class InternetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public InternetHandle() : base(true) { }
        protected override bool ReleaseHandle() => InternetCloseHandle(handle);
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
    private static extern InternetHandle InternetOpen(string agent, int access, string? proxy, string? bypass, uint flags);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
    private static extern InternetHandle InternetConnect(InternetHandle session, string host, ushort port, string? user, string? password, int service, uint flags, IntPtr context);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
    private static extern InternetHandle HttpOpenRequest(InternetHandle connection, string verb, string path, string? version, string? referrer, IntPtr acceptTypes, uint flags, IntPtr context);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
    private static extern bool HttpSendRequest(InternetHandle request, string headers, int headerLength, byte[]? body, int length);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode)]
    private static extern bool HttpQueryInfo(InternetHandle request, uint field, StringBuilder value, ref int size, IntPtr index);
    [DllImport("wininet.dll")]
    private static extern bool InternetReadFile(InternetHandle request, byte[] data, int length, out int read);
    [DllImport("wininet.dll")]
    private static extern bool InternetCloseHandle(IntPtr handle);
}
