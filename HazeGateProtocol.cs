using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhantomHaze;

// The HazeGate Web View protocol. Pages are not downloaded by this PC but by a HazeGate Azure function (the
// azure-cloud-webview-protocol repository): PhantomHaze sends it an address, the function downloads the page together
// with the stylesheets and scripts it uses and sends everything back in one answer, and PhantomHaze draws the page with
// its HTMLRenderer page engine (RenderedProtocolPage.cs). Images and the page's own requests (fetch, XMLHttpRequest) go
// through the function too, so the sites shown only ever see Azure. The function's protocol is described in that
// repository's README ("The HazeGate protocol").

/// <summary>
/// The HazeGate function this build uses. Beta builds have it built in (it cannot be seen or changed in Settings), so
/// testers can use HazeGate straight away. Anyone with this build can find the key: if it is misused, renew the
/// function's default key in Azure Portal (Functions -> hazegate -> Function Keys) and put the new one here.
/// </summary>
internal static class HazeGateConnection
{
    public const string Address = "https://website-fetcher-f2gddhfyhrf9etdr.australiasoutheast-01.azurewebsites.net/api/hazegate";

    /// <summary>The function's "default" function key (never the _master key).</summary>
    public const string Key = "G0oz2xtj4RguVJUkEzkoLA8tBuygQN-pdvTk3A4cxFOMAzFuMoKFWw==";
}

/// <summary>The HazeGate protocol (Settings -> General -> Web View protocol).</summary>
internal sealed class HazeGateWebProtocol : WebProtocol
{
    public HazeGateWebProtocol(bool scriptsEnabled)
    {
        ScriptsEnabled = scriptsEnabled;
    }

    public bool ScriptsEnabled { get; }

    public override string Id => WebProtocolCatalog.HazeGateId;

    public override string DisplayName => "HazeGate";

    public override string Description =>
        "Sends each address to the HazeGate Azure function, which downloads the page together with its stylesheets and " +
        "scripts and sends everything back; PhantomHaze then draws it like the HTMLRenderer protocol does (with its " +
        "JavaScript and modern CSS support). Images and the page's own requests go through the function too, so the " +
        "sites you visit only see Azure, never this PC.";

    public override WebProtocolFeatures Features => RenderedWebProtocolPage.GetFeatures(ScriptsEnabled);

    public override string Signature => $"{Id}|{(ScriptsEnabled ? "scripts" : "no-scripts")}";

    /// <summary>Every page, image and request goes through the function: PhantomHaze never contacts the sites itself.</summary>
    public override bool LoadsPagesDirectly => false;

    public override WebProtocolPage CreatePage(IWebProtocolHost host) =>
        new RenderedWebProtocolPage(this, host, new HazeGatePageTransport(HazeGateConnection.Address, HazeGateConnection.Key), ScriptsEnabled);
}

/// <summary>Something went wrong between PhantomHaze and the HazeGate function (the message is for people).</summary>
internal sealed class HazeGateException : Exception
{
    public HazeGateException(string message, int statusCode = 0)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The function's HTTP status, or 0 when it could not be reached.</summary>
    public int StatusCode { get; }
}

/// <summary>A request to the HazeGate function (version 1 of its protocol).</summary>
internal sealed class HazeGateRequest
{
    public HazeGateRequest(string mode, string url)
    {
        Mode = mode;
        Url = url;
    }

    /// <summary>"page" (the page, plus its stylesheets and scripts when <see cref="Bundle"/> is set) or "resource".</summary>
    public string Mode { get; }
    public string Url { get; }
    public string Method { get; init; } = "GET";
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
    public string? Cookie { get; init; }
    public string? Referrer { get; init; }
    public string Accept { get; init; } = "*/*";
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }
    public bool Bundle { get; init; }
    public int MaximumBytes { get; init; } = 8 * 1024 * 1024;

    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("mode", Mode);
            writer.WriteString("url", Url);
            writer.WriteString("method", Method);
            if (Body is not null)
            {
                writer.WriteBase64String("body", Body);
            }

            if (!string.IsNullOrWhiteSpace(ContentType))
            {
                writer.WriteString("contentType", ContentType);
            }

            if (!string.IsNullOrEmpty(Cookie))
            {
                writer.WriteString("cookie", Cookie);
            }

            if (!string.IsNullOrEmpty(Referrer))
            {
                writer.WriteString("referrer", Referrer);
            }

            writer.WriteString("userAgent", PageEngineDefaults.UserAgent);
            writer.WriteString("acceptLanguage", RenderedPageEnvironment.AcceptLanguage);
            writer.WriteString("accept", Accept);
            if (Headers is { Count: > 0 })
            {
                writer.WriteStartObject("headers");
                foreach ((string name, string value) in Headers)
                {
                    writer.WriteString(name, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteBoolean("bundle", Bundle);
            writer.WriteNumber("maxBytes", MaximumBytes);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

/// <summary>A downloaded part of a page in a HazeGate answer.</summary>
internal sealed class HazeGateResource
{
    public string Url { get; init; } = string.Empty;
    public string FinalUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> Redirects { get; init; } = Array.Empty<string>();
    public int Status { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string? Error { get; init; }
}

/// <summary>The HazeGate function's answer: the response of the address (redirects are not followed) and, for pages, its parts.</summary>
internal sealed class HazeGateResponse
{
    public string Url { get; init; } = string.Empty;
    public int Status { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string? Location { get; init; }
    public IReadOnlyList<string> SetCookies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<HazeGateResource> Resources { get; init; } = Array.Empty<HazeGateResource>();

    public static HazeGateResponse Parse(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new HazeGateException("The HazeGate function sent an answer PhantomHaze does not understand.");
        }

        var resources = new List<HazeGateResource>();
        if (root.TryGetProperty("resources", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string url = GetString(item, "url");
                resources.Add(new HazeGateResource
                {
                    Url = url,
                    FinalUrl = GetString(item, "finalUrl") is { Length: > 0 } finalUrl ? finalUrl : url,
                    Redirects = GetStrings(item, "redirects"),
                    Status = GetInt(item, "status"),
                    ContentType = GetString(item, "contentType"),
                    Body = GetBody(item),
                    Error = item.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
                });
            }
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty("headers", out JsonElement headerList) && headerList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement pair in headerList.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 &&
                    pair[0].ValueKind == JsonValueKind.String && pair[1].ValueKind == JsonValueKind.String)
                {
                    headers.Add(new KeyValuePair<string, string>(pair[0].GetString()!, pair[1].GetString()!));
                }
            }
        }

        return new HazeGateResponse
        {
            Url = GetString(root, "url"),
            Status = GetInt(root, "status"),
            StatusText = GetString(root, "statusText"),
            ContentType = GetString(root, "contentType"),
            Location = GetString(root, "location") is { Length: > 0 } location ? location : null,
            SetCookies = GetStrings(root, "setCookies"),
            Headers = headers,
            Body = GetBody(root),
            Resources = resources,
        };
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static int GetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    }

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).ToList();
    }

    /// <summary>Text that is valid UTF-8 comes as "text" (smaller than Base64), anything else as "body" (Base64).</summary>
    private static byte[] GetBody(JsonElement element)
    {
        if (element.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
        {
            return Encoding.UTF8.GetBytes(text.GetString() ?? string.Empty);
        }

        if (element.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String && body.TryGetBytesFromBase64(out byte[]? bytes))
        {
            return bytes;
        }

        return Array.Empty<byte>();
    }
}

/// <summary>Talks to HazeGate functions.</summary>
internal static class HazeGateClient
{
    private const int MaximumAnswerBytes = 64 * 1024 * 1024;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 12,
    })
    {
        // A Consumption plan function that has been idle starts first (a "cold start"), which can take a while.
        Timeout = TimeSpan.FromSeconds(100),
        MaxResponseContentBufferSize = MaximumAnswerBytes,
    };

    public static async Task<HazeGateResponse> SendAsync(string address, string key, HazeGateRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, address);
        AddHeaders(message, key);
        message.Content = new ByteArrayContent(request.ToJson());
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        byte[] answer;
        HttpStatusCode status;
        try
        {
            using HttpResponseMessage response = await Client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            answer = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HazeGateException("The HazeGate function did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new HazeGateException($"The HazeGate function could not be reached: {ex.Message}");
        }

        if (status != HttpStatusCode.OK)
        {
            throw new HazeGateException(DescribeFailure((int)status, answer), (int)status);
        }

        try
        {
            return HazeGateResponse.Parse(answer);
        }
        catch (JsonException)
        {
            throw new HazeGateException("The HazeGate function sent an answer PhantomHaze does not understand. Is the address the HazeGate function's?");
        }
    }

    private static void AddHeaders(HttpRequestMessage message, string key)
    {
        message.Headers.TryAddWithoutValidation("User-Agent", "PhantomHaze/" + (AppVersionInfo.Tag?.VersionNumber ?? "1"));
        message.Headers.Accept.ParseAdd("application/json");
        if (key.Length > 0)
        {
            // Azure Functions accepts the function key in this header, so it never appears in an address.
            message.Headers.TryAddWithoutValidation("x-functions-key", key);
        }
    }

    private static string DescribeFailure(int status, byte[] answer)
    {
        string? detail = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(answer);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.String)
            {
                detail = error.GetString();
            }
        }
        catch (JsonException)
        {
            // Not a HazeGate answer.
        }

        return status switch
        {
            401 or 403 when detail is null =>
                "The HazeGate function refused PhantomHaze: the function key built into this version is no longer valid. " +
                "A newer version of PhantomHaze may be needed.",
            404 => "The HazeGate function was not found. It may not be deployed (or was moved); a newer version of PhantomHaze may be needed.",
            429 => "The HazeGate function is busy (too many requests). Try again in a moment.",
            503 => "The HazeGate function is not running. It may be stopped in Azure Portal, or still starting.",
            _ when detail is not null => detail,
            _ => $"The HazeGate function answered with an error (HTTP {status}).",
        };
    }
}

/// <summary>
/// The HazeGate protocol's way of downloading: everything goes through the HazeGate function. Redirects are followed
/// here, one request per step, so every address is checked against web.block before the function is asked for it.
/// Cookies are kept for the session (in memory only) and sent to the function only for requests to the page's own site.
/// </summary>
internal sealed class HazeGatePageTransport : IRenderedPageTransport
{
    private const int MaximumDocumentBytes = 10 * 1024 * 1024;
    private const int MaximumRedirects = 10;
    private const int MaximumResourceRedirects = 5;

    private readonly string _address;
    private readonly string _key;
    private readonly SemaphoreSlim _resourceGate = new(6, 6);
    private volatile WebRequestRules _rules = WebRequestRules.None;
    private volatile Dictionary<string, PageResource> _bundle = new(StringComparer.Ordinal);
    private CancellationTokenSource? _documentCancellation;

    public HazeGatePageTransport(string address, string key)
    {
        _address = address;
        _key = key;
        (Cookies, Storage) = RenderedPageSessionData.Get("hazegate|" + address);
    }

    public string ProtocolName => "HazeGate";

    public CookieContainer Cookies { get; }

    public PageSessionStorage Storage { get; }

    public void UseRules(WebRequestRules rules)
    {
        _rules = rules;
        // An inspector save starts a new document without downloading a bundle. Its resources use the current rules.
        _bundle = new Dictionary<string, PageResource>(StringComparer.Ordinal);
    }

    public void CancelDocument()
    {
        try
        {
            _documentCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That download already finished.
        }
    }

    public async Task<FetchedDocument> FetchDocumentAsync(PageRequest request, IWebProtocolHost host, string folder, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? requested) || !WebProtocolPage.IsWebAddress(request.Url))
        {
            throw new HtmlRendererLoadException("The HazeGate protocol only opens http:// and https:// addresses.");
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _documentCancellation = cancellation;
        try
        {
            string fragment = requested.Fragment;
            string method = request.Method;
            byte[]? body = request.Body;
            string? contentType = request.ContentType;
            string? referrer = request.Referrer;
            string current = requested.GetLeftPart(UriPartial.Query);
            for (int redirects = 0; ; redirects++)
            {
                if (_rules.IsBlocked(current, out string blockedBy))
                {
                    host.Log($"HazeGate did not open {WebPageSession.LogText(current, 220)}: it is blocked by {blockedBy}.");
                    return FetchedDocument.FromMessage(HtmlRendererDocumentBuilder.BuildMessagePage(current, "Blocked by an extension", $"This address is blocked by {blockedBy}.", isSuccess: false));
                }

                var address = new Uri(current);
                HazeGateResponse response;
                try
                {
                    response = await HazeGateClient.SendAsync(_address, _key, new HazeGateRequest("page", current)
                    {
                        Method = method,
                        Body = body,
                        ContentType = contentType,
                        Cookie = Cookies.GetCookieHeader(address) is { Length: > 0 } cookie ? cookie : null,
                        Referrer = referrer,
                        Accept = PageEngineDefaults.AcceptHtml,
                        Bundle = true,
                        MaximumBytes = MaximumDocumentBytes,
                    }, cancellation.Token);
                }
                catch (HazeGateException ex)
                {
                    throw new HtmlRendererLoadException(ex.Message);
                }

                StoreCookies(address, response.SetCookies);
                if (response.Status is 301 or 302 or 303 or 307 or 308 && response.Location is string location)
                {
                    if (redirects >= MaximumRedirects)
                    {
                        throw new HtmlRendererLoadException("The page redirected too many times.");
                    }

                    if (!Uri.TryCreate(address, location, out Uri? next) || !WebProtocolPage.IsWebAddress(next.AbsoluteUri))
                    {
                        throw new HtmlRendererLoadException($"The page redirected to an address that is not a web page: {WebPageSession.LogText(location, 200)}");
                    }

                    // Like browsers: 303 (and 301/302 after a form) continue with GET; 307 and 308 repeat the request.
                    if (response.Status is 301 or 302 or 303 && method != "GET")
                    {
                        method = "GET";
                        body = null;
                        contentType = null;
                    }

                    referrer = current;
                    current = next.GetLeftPart(UriPartial.Query);
                    if (next.Fragment.Length > 0)
                    {
                        fragment = next.Fragment;
                    }

                    continue;
                }

                // The stylesheets and scripts the function downloaded with the page; parts at blocked addresses (or that
                // were redirected through one) are left out, so the page engine never uses them.
                var bundle = new Dictionary<string, PageResource>(StringComparer.Ordinal);
                foreach (HazeGateResource resource in response.Resources)
                {
                    if (resource.Error is not null || resource.Status == 0 || !WebProtocolPage.IsWebAddress(resource.Url) ||
                        !WebProtocolPage.IsWebAddress(resource.FinalUrl) ||
                        _rules.IsBlocked(resource.Url, out _) || _rules.IsBlocked(resource.FinalUrl, out _) ||
                        resource.Redirects.Any(hop => _rules.IsBlocked(hop, out _)))
                    {
                        continue;
                    }

                    bundle[resource.Url] = new PageResource(resource.FinalUrl, resource.Status, resource.ContentType, resource.Body);
                }

                _bundle = bundle;
                return new FetchedDocument(current + fragment, response.Status, response.ContentType, response.Body);
            }
        }
        finally
        {
            if (ReferenceEquals(_documentCancellation, cancellation))
            {
                _documentCancellation = null;
            }
        }
    }

    public async Task<PageResource?> FetchAsync(PageResourceRequest request, CancellationToken cancellationToken)
    {
        // Stylesheets and scripts usually came with the page.
        if (request.Kind is not (PageResourceKind.Data or PageResourceKind.Frame) && request.Method == "GET" && request.Body is null &&
            _bundle.TryGetValue(request.Url, out PageResource? bundled))
        {
            return bundled.Body.Length <= request.MaximumBytes ? bundled : null;
        }

        await _resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string current = request.Url;
            string method = request.Method;
            byte[]? body = request.Body;
            string? contentType = request.ContentType;
            for (int redirect = 0; redirect <= MaximumResourceRedirects; redirect++)
            {
                if (!Uri.TryCreate(current, UriKind.Absolute, out Uri? uri) || !WebProtocolPage.IsWebAddress(current) || _rules.IsBlocked(uri.AbsoluteUri, out _))
                {
                    return null;
                }

                HazeGateResponse response;
                try
                {
                    response = await HazeGateClient.SendAsync(_address, _key, new HazeGateRequest("resource", uri.AbsoluteUri)
                    {
                        Method = method,
                        Body = body,
                        ContentType = contentType,
                        Headers = request.Headers?.Where(header => HtmlRendererResources.IsAllowedRequestHeader(header.Key, header.Value)).ToList(),
                        Cookie = request.SendCookies && Cookies.GetCookieHeader(uri) is { Length: > 0 } cookie ? cookie : null,
                        Referrer = request.Referrer,
                        Accept = HtmlRendererResources.GetAcceptHeader(request.Kind),
                        MaximumBytes = request.MaximumBytes,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (HazeGateException)
                {
                    return null;
                }

                if (request.SendCookies)
                {
                    StoreCookies(uri, response.SetCookies);
                }

                if (response.Status is 301 or 302 or 303 or 307 or 308 && response.Location is string location)
                {
                    if (!Uri.TryCreate(uri, location, out Uri? next))
                    {
                        return null;
                    }

                    // Like browsers, never follow an https -> http redirect for a part of a page.
                    if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                    {
                        return null;
                    }

                    if (response.Status is 301 or 302 or 303 && method != "GET" && method != "HEAD")
                    {
                        method = "GET";
                        body = null;
                        contentType = null;
                    }

                    current = next.AbsoluteUri;
                    continue;
                }

                if (response.Body.Length > request.MaximumBytes)
                {
                    return null;
                }

                return new PageResource(uri.AbsoluteUri, response.Status, response.ContentType, response.Body)
                {
                    StatusText = response.StatusText,
                    Headers = request.Kind is PageResourceKind.Data or PageResourceKind.Frame ? response.Headers : Array.Empty<KeyValuePair<string, string>>(),
                };
            }

            return null;
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    private void StoreCookies(Uri address, IReadOnlyList<string> setCookies)
    {
        foreach (string setCookie in setCookies)
        {
            try
            {
                Cookies.SetCookies(address, setCookie);
            }
            catch (CookieException)
            {
                // An invalid cookie is ignored, as browsers do.
            }
        }
    }
}
