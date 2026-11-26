using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using TheArtOfDev.HtmlRenderer.Core.Entities;
using TheArtOfDev.HtmlRenderer.WinForms;

namespace PhantomHaze;

/// <summary>
/// The HTMLRenderer protocol: every page is downloaded with the curl.exe that ships with Windows, opened by PhantomHaze's
/// page engine (AngleSharp for the document, Jint for its JavaScript, a CSS cascade of its own - see
/// HtmlRendererEngine.cs) and drawn with HTML Renderer (https://github.com/ArthurHub/HTML-Renderer). WebView2 is not used
/// at all.
/// </summary>
internal sealed class HtmlRendererWebProtocol : WebProtocol
{
    public HtmlRendererWebProtocol(string? proxyServer = null, bool scriptsEnabled = true)
    {
        ProxyServer = proxyServer;
        ScriptsEnabled = scriptsEnabled;
    }

    /// <summary>The proxy server of an extension protocol (pages, images and stylesheets all go through it), or null.</summary>
    public string? ProxyServer { get; }

    /// <summary>Pages run their JavaScript (Settings -> General).</summary>
    public bool ScriptsEnabled { get; }

    public override string Id => WebProtocolCatalog.HtmlRendererId;

    public override string DisplayName => "HTMLRenderer";

    public override string Description =>
        "Downloads each page with curl and draws it with HTML Renderer, without WebView2. Pages run their JavaScript (with " +
        "Jint, a JavaScript engine written in C#) and modern CSS - variables, media queries, flex and grid layouts - is " +
        "converted into what HTML Renderer can draw. Lighter than WebView2, but complex sites may look simpler. " +
        "Extensions' web.css styles and web.script scripts apply.";

    public override WebProtocolFeatures Features => RenderedWebProtocolPage.GetFeatures(ScriptsEnabled);

    public override string Signature => $"{Id}|{ProxyServer}|{(ScriptsEnabled ? "scripts" : "no-scripts")}";

    public override string? GetUnavailableReason()
    {
        return CurlTool.ExecutablePath is null
            ? $"curl.exe was not found in {Environment.SystemDirectory} (it ships with Windows 10 version 1803 and later)."
            : null;
    }

    public override WebProtocolPage CreatePage(IWebProtocolHost host) =>
        new RenderedWebProtocolPage(this, host, new CurlPageTransport(ProxyServer), ScriptsEnabled);
}

/// <summary>
/// The HTMLRenderer protocol's way of downloading: pages with curl (one run per redirect, so every address is checked
/// against web.block first), everything else with HttpClient. Cookies are kept for the session (in memory only).
/// </summary>
internal sealed class CurlPageTransport : IRenderedPageTransport
{
    private const int MaximumDocumentBytes = 10 * 1024 * 1024;
    private const int MaximumRedirects = 10;

    private readonly string? _proxyServer;
    private Process? _curlProcess;
    private volatile WebRequestRules _rules = WebRequestRules.None;

    public CurlPageTransport(string? proxyServer)
    {
        _proxyServer = proxyServer;

        // One cookie jar and one localStorage per proxy server, for the whole session (never written to disk).
        (Cookies, Storage) = RenderedPageSessionData.Get("htmlrenderer|" + proxyServer);
    }

    public string ProtocolName => "HTMLRenderer";

    public void UseRules(WebRequestRules rules)
    {
        _rules = rules;
    }

    public CookieContainer Cookies { get; }

    public PageSessionStorage Storage { get; }

    public void CancelDocument()
    {
        if (_curlProcess is Process process)
        {
            CurlTool.TryKill(process);
            _curlProcess = null;
        }
    }

    public async Task<FetchedDocument> FetchDocumentAsync(PageRequest request, IWebProtocolHost host, string folder, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? requested) || !WebProtocolPage.IsWebAddress(request.Url))
        {
            throw new HtmlRendererLoadException("The HTMLRenderer protocol only opens http:// and https:// addresses.");
        }

        string curl = CurlTool.ExecutablePath
            ?? throw new HtmlRendererLoadException($"curl.exe was not found in {Environment.SystemDirectory}.");
        Directory.CreateDirectory(folder);
        string outputPath = Path.Combine(folder, "index.html");
        string headersPath = Path.Combine(folder, "headers.txt");
        string bodyPath = Path.Combine(folder, "request-body.bin");
        string fragment = requested.Fragment;
        string method = request.Method;
        byte[]? body = request.Body;
        string? referrer = request.Referrer;

        // Redirects are followed here, one curl run per step, so every address is checked against web.block before
        // anything is requested from it (curl -L would follow them without asking).
        string current = requested.GetLeftPart(UriPartial.Query);
        for (int redirects = 0; ; redirects++)
        {
            if (_rules.IsBlocked(current, out string blockedBy))
            {
                host.Log($"{ProtocolName} did not open {WebPageSession.LogText(current, 220)}: it is blocked by {blockedBy}.");
                return FetchedDocument.FromMessage(HtmlRendererDocumentBuilder.BuildMessagePage(current, "Blocked by an extension", $"This address is blocked by {blockedBy}.", isSuccess: false));
            }

            if (body is not null)
            {
                await File.WriteAllBytesAsync(bodyPath, body, cancellationToken);
            }

            CurlResult result = await RunCurlAsync(curl, current, method, body is null ? null : bodyPath, request.ContentType, referrer, outputPath, headersPath, cancellationToken);
            StoreCookies(new Uri(current), headersPath);
            if (result.ExitCode != 0)
            {
                throw new HtmlRendererLoadException(DescribeCurlFailure(result));
            }

            if (result.StatusCode is 301 or 302 or 303 or 307 or 308 && !string.IsNullOrWhiteSpace(result.RedirectUrl))
            {
                if (redirects >= MaximumRedirects)
                {
                    throw new HtmlRendererLoadException("The page redirected too many times.");
                }

                if (!Uri.TryCreate(new Uri(current), result.RedirectUrl, out Uri? next) || !WebProtocolPage.IsWebAddress(next.AbsoluteUri))
                {
                    throw new HtmlRendererLoadException($"The page redirected to an address that is not a web page: {WebPageSession.LogText(result.RedirectUrl, 200)}");
                }

                // Like browsers: 303 (and 301/302 after a form) continue with GET; 307 and 308 repeat the request.
                if (result.StatusCode is 301 or 302 or 303 && method != "GET")
                {
                    method = "GET";
                    body = null;
                }

                referrer = current;
                current = next.GetLeftPart(UriPartial.Query);
                if (next.Fragment.Length > 0)
                {
                    fragment = next.Fragment;
                }

                continue;
            }

            byte[] content = File.Exists(outputPath) ? await File.ReadAllBytesAsync(outputPath, cancellationToken) : Array.Empty<byte>();
            if (content.Length > MaximumDocumentBytes)
            {
                throw new HtmlRendererLoadException("The page is larger than 10 MB, which the HTMLRenderer protocol does not open.");
            }

            return new FetchedDocument(current + fragment, result.StatusCode, result.ContentType, content);
        }
    }

    private void StoreCookies(Uri address, string headersPath)
    {
        try
        {
            if (!File.Exists(headersPath))
            {
                return;
            }

            foreach (string line in File.ReadLines(headersPath))
            {
                if (line.StartsWith("set-cookie:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Cookies.SetCookies(address, line["set-cookie:".Length..].Trim());
                    }
                    catch (CookieException)
                    {
                        // An invalid cookie is ignored, as browsers do.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No cookies from this response.
        }
    }

    private sealed record CurlResult(int ExitCode, int StatusCode, string RedirectUrl, string ContentType, string Error);

    private async Task<CurlResult> RunCurlAsync(
        string curl,
        string url,
        string method,
        string? bodyPath,
        string? contentType,
        string? referrer,
        string outputPath,
        string headersPath,
        CancellationToken cancellationToken)
    {
        Version? version = await CurlTool.GetVersionAsync();
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteFile(outputPath);
        TryDeleteFile(headersPath);

        var startInfo = new ProcessStartInfo(curl)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // curl -o index.html <address>, plus what keeps it safe and predictable inside PhantomHaze.
        var arguments = startInfo.ArgumentList;
        arguments.Add("-q"); // must be first: never read a .curlrc file that could change what curl does
        arguments.Add("--silent");
        arguments.Add("--show-error");
        arguments.Add("--globoff"); // [] and {} in an address are not curl patterns
        arguments.Add("--proto");
        arguments.Add("=http,https");
        arguments.Add("--compressed");
        arguments.Add("--connect-timeout");
        arguments.Add("15");
        arguments.Add("--max-time");
        arguments.Add("30");
        arguments.Add("--max-filesize");
        arguments.Add(MaximumDocumentBytes.ToString(CultureInfo.InvariantCulture));
        if (version is not null && version >= new Version(7, 70))
        {
            // Like browsers, do not fail when the certificate revocation server cannot be reached.
            arguments.Add("--ssl-revoke-best-effort");
        }

        if (_proxyServer is not null)
        {
            arguments.Add("--proxy");
            arguments.Add(CurlTool.ToCurlProxy(_proxyServer));
        }

        // A browser's request: its user agent, what it accepts and the session's cookies for the site.
        arguments.Add("--user-agent");
        arguments.Add(PageEngineDefaults.UserAgent);
        arguments.Add("--header");
        arguments.Add("Accept: " + PageEngineDefaults.AcceptHtml);
        arguments.Add("--header");
        arguments.Add("Accept-Language: " + RenderedPageEnvironment.AcceptLanguage);
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? address))
        {
            string cookieHeader = Cookies.GetCookieHeader(address);
            if (cookieHeader.Length > 0)
            {
                arguments.Add("--cookie");
                arguments.Add(cookieHeader);
            }
        }

        if (!string.IsNullOrEmpty(referrer))
        {
            arguments.Add("--referer");
            arguments.Add(referrer);
        }

        if (method != "GET")
        {
            arguments.Add("--request");
            arguments.Add(method);
        }

        if (bodyPath is not null)
        {
            arguments.Add("--data-binary");
            arguments.Add("@" + bodyPath);
            arguments.Add("--header");
            arguments.Add("Content-Type: " + (string.IsNullOrWhiteSpace(contentType) ? "application/x-www-form-urlencoded" : contentType));
        }

        arguments.Add("--dump-header");
        arguments.Add(headersPath);
        arguments.Add("--output");
        arguments.Add(outputPath);
        arguments.Add("--write-out");
        arguments.Add("%{http_code}\\n%{redirect_url}\\n%{content_type}\\n");
        arguments.Add("--url");
        arguments.Add(url);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _curlProcess = process;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CurlTool.TryKill(process);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_curlProcess, process))
            {
                _curlProcess = null;
            }
        }

        string[] lines = (await output).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int statusCode = lines.Length > 0 && int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int status) ? status : 0;
        return new CurlResult(
            process.ExitCode,
            statusCode,
            lines.Length > 1 ? lines[1].Trim() : string.Empty,
            lines.Length > 2 ? lines[2].Trim() : string.Empty,
            (await error).Trim());
    }

    private static string DescribeCurlFailure(CurlResult result)
    {
        string reason = result.ExitCode switch
        {
            5 => "The proxy server could not be found.",
            6 => "The server could not be found. Check the address, or your internet connection.",
            7 => "The server could not be reached.",
            28 => "The server took too long to respond.",
            35 or 51 or 53 or 54 or 58 or 59 or 60 or 64 or 66 or 77 or 80 or 82 or 83 or 90 or 91 => "A secure connection to the server could not be made.",
            52 => "The server did not send anything back.",
            55 or 56 => "The connection to the server was interrupted.",
            63 => "The page is larger than 10 MB, which the HTMLRenderer protocol does not open.",
            97 => "The proxy server refused the connection.",
            _ => $"curl could not download the page (error {result.ExitCode}).",
        };

        return string.IsNullOrWhiteSpace(result.Error) ? reason : $"{reason}\n\n{result.Error}";
    }

    public Task<PageResource?> FetchAsync(PageResourceRequest request, CancellationToken cancellationToken)
    {
        WebRequestRules rules = _rules;
        return HtmlRendererResources.FetchAsync(request, _proxyServer, Cookies, url => rules.IsBlocked(url, out _), cancellationToken);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // curl overwrites it.
        }
    }
}

/// <summary>Things every rendered page shares: the language sent to sites and the fonts Windows has.</summary>
internal static class RenderedPageEnvironment
{
    private static HashSet<string>? s_fonts;

    /// <summary>The Accept-Language header, from the Windows display language (for example "en-AU,en;q=0.9").</summary>
    public static string AcceptLanguage
    {
        get
        {
            string name = CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrEmpty(name))
            {
                return "en-US,en;q=0.9";
            }

            string language = name.Split('-')[0];
            return language.Equals(name, StringComparison.OrdinalIgnoreCase) ? name + ";q=1" : $"{name},{language};q=0.9";
        }
    }

    public static string Language => string.IsNullOrEmpty(CultureInfo.CurrentUICulture.Name) ? "en-US" : CultureInfo.CurrentUICulture.Name;

    public static bool IsFontInstalled(string family)
    {
        HashSet<string>? fonts = s_fonts;
        if (fonts is null)
        {
            fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var collection = new System.Drawing.Text.InstalledFontCollection();
                foreach (FontFamily installed in collection.Families)
                {
                    fonts.Add(installed.Name);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                fonts.Add("Segoe UI");
            }

            s_fonts = fonts;
        }

        return fonts.Contains(family);
    }
}

/// <summary>The curl.exe that ships with Windows.</summary>
internal static class CurlTool
{
    private static readonly Lazy<string?> s_executablePath = new(() =>
    {
        // Only the copy in the Windows system folder is used: a curl.exe found through PATH or in the working folder
        // could be anything.
        string path = Path.Combine(Environment.SystemDirectory, "curl.exe");
        return File.Exists(path) ? path : null;
    });

    private static Task<Version?>? s_versionTask;

    public static string? ExecutablePath => s_executablePath.Value;

    /// <summary>The version of curl.exe (null when it cannot be read). Read once per session; call on the UI thread.</summary>
    public static Task<Version?> GetVersionAsync() => s_versionTask ??= ReadVersionAsync();

    private static async Task<Version?> ReadVersionAsync()
    {
        string? curl = ExecutablePath;
        if (curl is null)
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(curl)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "-q", "--version" },
                },
            };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }

            await error;
            Match match = Regex.Match(await output, @"^curl\s+(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);
            return match.Success
                ? new Version(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0)
                : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exiting.
        }
    }

    /// <summary>The proxy address curl is given. socks5 becomes socks5h so names are looked up by the proxy, as WebView2 does.</summary>
    public static string ToCurlProxy(string proxyServer)
    {
        return proxyServer.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? "socks5h://" + proxyServer["socks5://".Length..]
            : proxyServer;
    }
}

/// <summary>A page could not be opened with the HTMLRenderer protocol; the message is shown on the page.</summary>
internal sealed class HtmlRendererLoadException : Exception
{
    public HtmlRendererLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Downloads the parts of HTMLRenderer pages (scripts, stylesheets, images, the page's fetch/XMLHttpRequest requests),
/// checking every address - redirects included - against web.block.
/// </summary>
internal static class HtmlRendererResources
{
    public const int MaximumImageBytes = 8 * 1024 * 1024;
    public const int MaximumStyleSheetBytes = 4 * 1024 * 1024;
    private const long MaximumImagePixels = 40_000_000;
    private const int MaximumRedirects = 5;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    // Headers a page script may not set (as in browsers), or that PhantomHaze sets itself.
    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept-charset", "accept-encoding", "access-control-request-headers", "access-control-request-method", "connection",
        "content-length", "cookie", "cookie2", "date", "dnt", "expect", "host", "keep-alive", "origin", "referer", "te", "trailer",
        "transfer-encoding", "upgrade", "user-agent", "via", "content-type", "authorization", "proxy-authorization",
    };

    public static async Task<PageResource?> FetchAsync(
        PageResourceRequest request,
        string? proxyServer,
        CookieContainer cookies,
        Func<string, bool> isBlocked,
        CancellationToken cancellationToken)
    {
        HttpClient client = GetClient(proxyServer);
        string current = request.Url;
        string method = request.Method;
        byte[]? body = request.Body;
        bool sendCookies = request.SendCookies;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out Uri? uri) || !WebProtocolPage.IsWebAddress(current) || isBlocked(uri.AbsoluteUri))
            {
                return null;
            }

            using var message = new HttpRequestMessage(new HttpMethod(method), uri);
            message.Headers.TryAddWithoutValidation("User-Agent", PageEngineDefaults.UserAgent);
            message.Headers.TryAddWithoutValidation("Accept-Language", RenderedPageEnvironment.AcceptLanguage);
            message.Headers.TryAddWithoutValidation("Accept", GetAcceptHeader(request.Kind));
            if (!string.IsNullOrEmpty(request.Referrer))
            {
                message.Headers.TryAddWithoutValidation("Referer", request.Referrer);
            }

            if (sendCookies)
            {
                string cookieHeader = cookies.GetCookieHeader(uri);
                if (cookieHeader.Length > 0)
                {
                    message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }
            }

            if (request.Headers is not null)
            {
                foreach ((string name, string value) in request.Headers)
                {
                    if (IsAllowedRequestHeader(name, value))
                    {
                        message.Headers.TryAddWithoutValidation(name, value);
                    }
                }
            }

            if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.TryAddWithoutValidation("Content-Type", string.IsNullOrWhiteSpace(request.ContentType) ? "text/plain;charset=UTF-8" : request.ContentType);
            }

            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (sendCookies && response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
            {
                foreach (string setCookie in setCookies)
                {
                    try
                    {
                        cookies.SetCookies(uri, setCookie);
                    }
                    catch (CookieException)
                    {
                        // An invalid cookie is ignored, as browsers do.
                    }
                }
            }

            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is Uri location)
            {
                var next = new Uri(uri, location);

                // Like browsers, never follow an https -> http redirect for a part of a page.
                if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                {
                    return null;
                }

                if (status is 301 or 302 or 303 && method != "GET" && method != "HEAD")
                {
                    method = "GET";
                    body = null;
                }

                sendCookies &= Uri.Compare(uri, next, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
                current = next.AbsoluteUri;
                continue;
            }

            string contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            if (response.Content.Headers.ContentLength > request.MaximumBytes)
            {
                return null;
            }

            byte[] content;
            await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > request.MaximumBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                content = buffer.ToArray();
            }

            var headers = new List<KeyValuePair<string, string>>();
            if (request.Kind is PageResourceKind.Data or PageResourceKind.Frame)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
                {
                    if (!header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        headers.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", header.Value)));
                    }
                }
            }

            return new PageResource(uri.AbsoluteUri, status, contentType, content)
            {
                StatusText = response.ReasonPhrase ?? string.Empty,
                Headers = headers,
            };
        }

        return null;
    }

    /// <summary>The Accept header for a part of a page.</summary>
    public static string GetAcceptHeader(PageResourceKind kind) => kind switch
    {
        PageResourceKind.Image => PageEngineDefaults.AcceptImages,
        PageResourceKind.Frame => PageEngineDefaults.AcceptHtml,
        PageResourceKind.StyleSheet => "text/css,*/*;q=0.1",
        _ => "*/*",
    };

    /// <summary>False for headers a page script may not set (as in browsers), or that PhantomHaze sets itself.</summary>
    public static bool IsAllowedRequestHeader(string name, string value)
    {
        return !ForbiddenRequestHeaders.Contains(name) &&
               !name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) &&
               name.Length is > 0 and < 100 &&
               value.Length < 8000 &&
               name.All(c => c is > ' ' and < (char)127 and not ':') &&
               !value.Contains('\r') && !value.Contains('\n');
    }

    /// <summary>A copy of the image as a plain bitmap, or null for files that are not (supported) images or are huge.</summary>
    public static Image? DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using Image raw = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if ((long)raw.Width * raw.Height > MaximumImagePixels)
            {
                return null;
            }

            return new Bitmap(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private static HttpClient GetClient(string? proxyServer)
    {
        string key = proxyServer ?? string.Empty;
        lock (Gate)
        {
            if (Clients.TryGetValue(key, out HttpClient? existing))
            {
                return existing;
            }

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (proxyServer is not null)
            {
                handler.Proxy = new WebProxy(new Uri(proxyServer));
                handler.UseProxy = true;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25),
                MaxResponseContentBufferSize = 16 * 1024 * 1024,
            };
            Clients[key] = client;
            return client;
        }
    }
}

/// <summary>An HTMLRenderer page, ready to be drawn.</summary>
internal sealed class HtmlRendererDocument
{
    public HtmlRendererDocument(string url, Uri? baseUri, string html)
    {
        Url = url;
        BaseUri = baseUri;
        Html = html;
    }

    /// <summary>The page address (after redirects).</summary>
    public string Url { get; }

    /// <summary>What relative addresses in the page are resolved against (the page address, or its &lt;base href&gt;).</summary>
    public Uri? BaseUri { get; }

    /// <summary>The HTML handed to HTML Renderer (already made safe for it).</summary>
    public string Html { get; }

    public string Title { get; init; } = string.Empty;
    public string? FaviconUrl { get; init; }
    public bool IsSuccess { get; init; } = true;
    public string? ErrorDescription { get; init; }

    /// <summary>The page itself when it is an image (already downloaded by curl).</summary>
    public Image? PrimaryImage { get; init; }

    public string? PrimaryImageUrl { get; init; }

    /// <summary>The page's stylesheets: the placeholder address each &lt;link&gt; now points to, and the real address.</summary>
    public List<(string Token, string Url)> StyleSheetLinks { get; } = new();

    /// <summary>Downloaded stylesheets by placeholder address.</summary>
    public Dictionary<string, string> StyleSheets { get; } = new(StringComparer.Ordinal);
}

/// <summary>Turns downloaded pages into HTML that HTML Renderer can draw safely.</summary>
internal static class HtmlRendererDocumentBuilder
{
    public const string FirstMatchId = "ph-find-first";
    private const string StyleSheetToken = "phcss:";
    private const int MaximumStyleSheets = 40;
    private const int MaximumTextCharacters = 2 * 1024 * 1024;

    // Removed with their content: scripts never run here, and the others are either unsupported (shown as garbage) or,
    // for iframes, would make HTML Renderer download video details itself.
    private static readonly HashSet<string> RemovedElements = new(StringComparer.Ordinal)
    {
        "script", "template", "svg", "math", "iframe", "object", "applet", "frameset", "noembed", "audio", "video",
    };

    // Void elements that would load or embed something; dropped.
    private static readonly HashSet<string> RemovedVoidElements = new(StringComparer.Ordinal) { "embed", "frame", "base", "param", "source", "track" };

    public static HtmlRendererDocument Build(string pageUrl, int statusCode, string contentType, byte[] body, IReadOnlyList<string> extensionStyles)
    {
        var pageUri = new Uri(pageUrl);
        bool isSuccess = statusCode is >= 200 and < 400;
        string? error = isSuccess ? null : $"HTTP {statusCode}";
        string mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();

        if (body.Length == 0)
        {
            return BuildMessagePage(
                pageUrl,
                isSuccess ? "This page is empty" : "This page could not be opened",
                isSuccess ? "The server sent an empty page." : $"The server answered with HTTP {statusCode}{DescribeStatus(statusCode)}.",
                isSuccess);
        }

        if (mediaType.StartsWith("image/", StringComparison.Ordinal) && mediaType != "image/svg+xml")
        {
            Image? image = HtmlRendererResources.DecodeImage(body);
            if (image is not null)
            {
                string encoded = WebUtility.HtmlEncode(pageUri.AbsoluteUri);
                return new HtmlRendererDocument(pageUrl, pageUri, $"<html><body style=\"margin:8px; text-align:center\"><img src=\"{encoded}\"></body></html>{Padding}")
                {
                    Title = GetFileTitle(pageUri),
                    IsSuccess = isSuccess,
                    ErrorDescription = error,
                    PrimaryImage = image,
                    PrimaryImageUrl = pageUri.AbsoluteUri,
                };
            }
        }

        bool isHtml = mediaType is "text/html" or "application/xhtml+xml" ||
                      (mediaType.Length == 0 && LooksLikeHtml(body));
        bool isText = !isHtml &&
                      (mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType.EndsWith("+json", StringComparison.Ordinal) ||
                       mediaType.EndsWith("+xml", StringComparison.Ordinal) ||
                       mediaType is "application/json" or "application/javascript" or "application/xml" or "application/x-javascript" ||
                       mediaType.Length == 0);
        if (!isHtml && !isText)
        {
            return BuildMessagePage(
                pageUrl,
                "This file cannot be shown",
                $"This address is a file ({(mediaType.Length == 0 ? "unknown type" : mediaType)}) that the HTMLRenderer protocol cannot show. Files are never downloaded.",
                isSuccess);
        }

        string text = DecodeText(body, contentType, isHtml);
        if (!isHtml)
        {
            bool truncated = text.Length > MaximumTextCharacters;
            string shown = truncated ? text[..MaximumTextCharacters] : text;
            var builder = new StringBuilder(shown.Length + 256);
            builder.Append("<html><body><pre>").Append(WebUtility.HtmlEncode(shown)).Append("</pre>");
            if (truncated)
            {
                builder.Append("<p><i>Only the first 2 MB of this file are shown.</i></p>");
            }

            builder.Append("</body></html>").Append(Padding);
            return new HtmlRendererDocument(pageUrl, pageUri, builder.ToString())
            {
                Title = GetFileTitle(pageUri),
                IsSuccess = isSuccess,
                ErrorDescription = error,
            };
        }

        var styleSheetLinks = new List<(string Token, string Url)>();
        string html = PrepareHtml(text, pageUri, extensionStyles, styleSheetLinks, out Uri baseUri, out string title, out string? favicon);
        var document = new HtmlRendererDocument(pageUrl, baseUri, html)
        {
            Title = title,
            FaviconUrl = favicon,
            IsSuccess = isSuccess,
            ErrorDescription = error,
        };
        document.StyleSheetLinks.AddRange(styleSheetLinks);
        return document;
    }

    /// <summary>A simple page with a title and a message (errors, blocked addresses, files that cannot be shown).</summary>
    public static HtmlRendererDocument BuildMessagePage(string pageUrl, string title, string message, bool isSuccess)
    {
        Uri? pageUri = Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? parsed) ? parsed : null;
        string paragraphs = string.Join(
            string.Empty,
            message.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(part => $"<p>{WebUtility.HtmlEncode(part.Trim())}</p>"));
        string html =
            "<html><body style=\"font-family: 'Segoe UI', sans-serif; font-size: 10pt; color: #222222; margin: 32px\">" +
            $"<h2 style=\"font-weight: bold\">{WebUtility.HtmlEncode(title)}</h2>" +
            paragraphs +
            $"<p style=\"color: #666666\">{WebUtility.HtmlEncode(pageUrl)}</p>" +
            "</body></html>" + Padding;
        return new HtmlRendererDocument(pageUrl, pageUri, html)
        {
            Title = title,
            IsSuccess = isSuccess,
            ErrorDescription = isSuccess ? null : message.Split('\n')[0],
        };
    }

    // HTML Renderer's parser reads a few characters past each '<'; trailing spaces keep a '<' near the end in bounds.
    private const string Padding = "\n   ";

    private static string DescribeStatus(int statusCode)
    {
        return statusCode switch
        {
            400 => " (bad request)",
            401 => " (sign-in required)",
            403 => " (forbidden)",
            404 => " (not found)",
            410 => " (gone)",
            429 => " (too many requests)",
            500 => " (server error)",
            502 => " (bad gateway)",
            503 => " (service unavailable)",
            504 => " (gateway timeout)",
            _ => string.Empty,
        };
    }

    private static string GetFileTitle(Uri uri)
    {
        string name = Path.GetFileName(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? uri.Host : Uri.UnescapeDataString(name);
    }

    private static bool LooksLikeHtml(byte[] body)
    {
        foreach (byte b in body.AsSpan(0, Math.Min(body.Length, 512)))
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF)
            {
                continue;
            }

            return b == (byte)'<';
        }

        return false;
    }

    /// <summary>Decodes a page or stylesheet: byte order mark, then the Content-Type charset, then &lt;meta charset&gt;, then UTF-8.</summary>
    public static string DecodeText(byte[] body, string contentType, bool isHtml) => PageText.Decode(body, contentType, isHtml);

    /// <summary>
    /// Makes a page safe for HTML Renderer: scripts and unsupported embedded content are removed, every tag is written
    /// again with properly encoded attributes (its parser does not understand quoted '&gt;'), stylesheets are pointed at
    /// placeholders PhantomHaze answers itself, and extensions' web.css styles are added at the end.
    /// </summary>
    private static string PrepareHtml(
        string html,
        Uri pageUri,
        IReadOnlyList<string> extensionStyles,
        List<(string Token, string Url)> styleSheetLinks,
        out Uri baseUri,
        out string title,
        out string? faviconUrl)
    {
        baseUri = FindBaseUri(html, pageUri);
        string? foundTitle = null;
        faviconUrl = null;
        var output = new StringBuilder(html.Length + 1024);
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                output.Append(html, i, html.Length - i);
                break;
            }

            output.Append(html, i, lt - i);
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            bool closing = lt + 1 < html.Length && html[lt + 1] == '/';
            int nameStart = closing ? lt + 2 : lt + 1;
            if (nameStart >= html.Length || !char.IsAsciiLetter(html[nameStart]))
            {
                if (lt + 1 < html.Length && html[lt + 1] is '!' or '?')
                {
                    // <!DOCTYPE ...> and <?xml ...?> are not needed.
                    int end = html.IndexOf('>', lt + 1);
                    i = end < 0 ? html.Length : end + 1;
                    continue;
                }

                output.Append("&lt;");
                i = lt + 1;
                continue;
            }

            int nameEnd = nameStart;
            while (nameEnd < html.Length && IsNameCharacter(html[nameEnd]))
            {
                nameEnd++;
            }

            string name = html[nameStart..nameEnd].ToLowerInvariant();
            int tagEnd = FindTagEnd(html, lt);
            if (tagEnd < 0)
            {
                output.Append("&lt;");
                i = lt + 1;
                continue;
            }

            if (closing)
            {
                if (!RemovedElements.Contains(name))
                {
                    output.Append("</").Append(name).Append('>');
                }

                i = tagEnd + 1;
                continue;
            }

            if (RemovedElements.Contains(name))
            {
                int close = IndexOfClosingTag(html, tagEnd + 1, name);
                if (close >= 0)
                {
                    int closeEnd = html.IndexOf('>', close);
                    i = closeEnd < 0 ? html.Length : closeEnd + 1;
                }
                else
                {
                    // An unclosed script runs to the end of the page; anything else just loses its tag.
                    i = name == "script" ? html.Length : tagEnd + 1;
                }

                continue;
            }

            if (name is "style" or "title" or "textarea" or "xmp")
            {
                int close = IndexOfClosingTag(html, tagEnd + 1, name);
                int contentEnd = close < 0 ? html.Length : close;
                int closeEnd = close < 0 ? html.Length : html.IndexOf('>', close);
                closeEnd = closeEnd < 0 ? html.Length : closeEnd + 1;
                string content = html[(tagEnd + 1)..contentEnd];
                if (name == "style")
                {
                    // HTML Renderer only recognises exactly "</style>" as the end of a style sheet.
                    output.Append("<style>").Append(content).Append("</style>");
                }
                else
                {
                    string decoded = WebUtility.HtmlDecode(content);
                    if (name == "title")
                    {
                        foundTitle ??= CollapseWhitespace(decoded);
                    }

                    output.Append('<').Append(name).Append('>').Append(WebUtility.HtmlEncode(decoded)).Append("</").Append(name).Append('>');
                }

                i = closeEnd;
                continue;
            }

            if (RemovedVoidElements.Contains(name))
            {
                i = tagEnd + 1;
                continue;
            }

            Dictionary<string, string> attributes = ParseAttributes(html, lt, tagEnd);
            bool selfClosing = tagEnd > lt && html[tagEnd - 1] == '/';
            if (name == "link")
            {
                AppendLink(output, attributes, baseUri, styleSheetLinks, ref faviconUrl);
                i = tagEnd + 1;
                continue;
            }

            if (name == "meta" && attributes.TryGetValue("http-equiv", out string? httpEquiv) &&
                httpEquiv.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
            {
                i = tagEnd + 1;
                continue;
            }

            if (name == "a" && attributes.TryGetValue("name", out string? anchorName) && !attributes.ContainsKey("id"))
            {
                // Links to #name scroll to the element with that id.
                attributes["id"] = anchorName;
            }

            AppendTag(output, name, attributes, selfClosing);
            i = tagEnd + 1;
        }

        foreach (string css in extensionStyles)
        {
            if (!string.IsNullOrWhiteSpace(css))
            {
                output.Append("<style>").Append(css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)).Append("</style>");
            }
        }

        output.Append(Padding);
        title = foundTitle ?? string.Empty;
        return output.ToString();
    }

    private static void AppendLink(
        StringBuilder output,
        Dictionary<string, string> attributes,
        Uri baseUri,
        List<(string Token, string Url)> styleSheetLinks,
        ref string? faviconUrl)
    {
        string[] relations = (attributes.GetValueOrDefault("rel") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string href = attributes.GetValueOrDefault("href") ?? string.Empty;
        if (href.Length == 0 || !Uri.TryCreate(baseUri, href.Trim(), out Uri? target) || !WebProtocolPage.IsWebAddress(target.AbsoluteUri))
        {
            return;
        }

        if (faviconUrl is null && relations.Any(relation => relation.Equals("icon", StringComparison.OrdinalIgnoreCase)))
        {
            faviconUrl = target.AbsoluteUri;
        }

        bool styleSheet = relations.Any(relation => relation.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)) &&
                          !relations.Any(relation => relation.Equals("alternate", StringComparison.OrdinalIgnoreCase));
        if (!styleSheet || styleSheetLinks.Count >= MaximumStyleSheets || !IsScreenMedia(attributes.GetValueOrDefault("media")))
        {
            return;
        }

        string token = StyleSheetToken + styleSheetLinks.Count.ToString(CultureInfo.InvariantCulture);
        styleSheetLinks.Add((token, target.AbsoluteUri));
        output.Append("<link rel=\"stylesheet\" href=\"").Append(token).Append("\">");
    }

    private static bool IsScreenMedia(string? media)
    {
        if (string.IsNullOrWhiteSpace(media))
        {
            return true;
        }

        string value = media.ToLowerInvariant();
        return value.Contains("screen", StringComparison.Ordinal) || value.Contains("all", StringComparison.Ordinal);
    }

    private static void AppendTag(StringBuilder output, string name, Dictionary<string, string> attributes, bool selfClosing)
    {
        output.Append('<').Append(name);
        foreach ((string attributeName, string value) in attributes)
        {
            // Event handlers never run here; leaving them out keeps the document small.
            if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || !attributeName.All(IsNameCharacter))
            {
                continue;
            }

            output.Append(' ').Append(attributeName.ToLowerInvariant()).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
        }

        output.Append(selfClosing ? " />" : ">");
    }

    private static Uri FindBaseUri(string html, Uri pageUri)
    {
        int from = 0;
        while (from < html.Length)
        {
            int index = html.IndexOf("<base", from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            int after = index + 5;
            if (after < html.Length && !IsNameCharacter(html[after]))
            {
                int end = FindTagEnd(html, index);
                if (end < 0)
                {
                    break;
                }

                if (ParseAttributes(html, index, end).TryGetValue("href", out string? href) &&
                    Uri.TryCreate(pageUri, href.Trim(), out Uri? baseUri) &&
                    WebProtocolPage.IsWebAddress(baseUri.AbsoluteUri))
                {
                    return baseUri;
                }

                break;
            }

            from = after;
        }

        return pageUri;
    }

    private static bool IsNameCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.';

    /// <summary>The '&gt;' that ends the tag starting at <paramref name="start"/>, ignoring any inside quoted attribute values.</summary>
    private static int FindTagEnd(string html, int start)
    {
        char quote = '\0';
        for (int i = start + 1; i < html.Length; i++)
        {
            char character = html[i];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                int previous = i - 1;
                while (previous > start && char.IsWhiteSpace(html[previous]))
                {
                    previous--;
                }

                if (html[previous] == '=')
                {
                    quote = character;
                }

                continue;
            }

            if (character == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfClosingTag(string html, int from, string name)
    {
        string needle = "</" + name;
        while (from < html.Length)
        {
            int index = html.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            int after = index + needle.Length;
            if (after >= html.Length || !IsNameCharacter(html[after]))
            {
                return index;
            }

            from = after;
        }

        return -1;
    }

    private static Dictionary<string, string> ParseAttributes(string html, int tagStart, int tagEnd)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = tagStart + 1;
        while (i < tagEnd && IsNameCharacter(html[i]))
        {
            i++;
        }

        while (i < tagEnd)
        {
            while (i < tagEnd && (char.IsWhiteSpace(html[i]) || html[i] == '/'))
            {
                i++;
            }

            int nameStart = i;
            while (i < tagEnd && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '/' or '>'))
            {
                i++;
            }

            if (i == nameStart)
            {
                i++;
                continue;
            }

            string name = html[nameStart..i];
            while (i < tagEnd && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            string value = string.Empty;
            if (i < tagEnd && html[i] == '=')
            {
                i++;
                while (i < tagEnd && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < tagEnd && html[i] is '"' or '\'')
                {
                    char quote = html[i++];
                    int valueStart = i;
                    while (i < tagEnd && html[i] != quote)
                    {
                        i++;
                    }

                    value = html[valueStart..i];
                    if (i < tagEnd)
                    {
                        i++;
                    }
                }
                else
                {
                    int valueStart = i;
                    while (i < tagEnd && !char.IsWhiteSpace(html[i]))
                    {
                        i++;
                    }

                    value = html[valueStart..i];
                }
            }

            attributes.TryAdd(name, WebUtility.HtmlDecode(value));
        }

        return attributes;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(character);
        }

        return builder.ToString();
    }


    /// <summary>Wraps every match of <paramref name="query"/> in the visible text of a prepared page in a highlight.</summary>
    public static (string Html, int Count) HighlightMatches(string html, string query)
    {
        var output = new StringBuilder(html.Length + 256);
        int count = 0;
        int hiddenDepth = 0;
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            int textEnd = lt < 0 ? html.Length : lt;
            if (textEnd > i)
            {
                string segment = html[i..textEnd];
                if (hiddenDepth > 0)
                {
                    output.Append(segment);
                }
                else
                {
                    count += AppendHighlightedText(output, segment, query, count);
                }
            }

            if (lt < 0)
            {
                break;
            }

            int tagEnd = html.IndexOf('>', lt);
            if (tagEnd < 0)
            {
                output.Append(html, lt, html.Length - lt);
                break;
            }

            string tag = html[(lt + 1)..tagEnd];
            output.Append(html, lt, tagEnd + 1 - lt);
            bool closing = tag.StartsWith('/');
            string name = new(tag.TrimStart('/').TakeWhile(IsNameCharacter).ToArray());
            if (name.Equals("style", StringComparison.OrdinalIgnoreCase) || name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("head", StringComparison.OrdinalIgnoreCase) || name.Equals("textarea", StringComparison.OrdinalIgnoreCase))
            {
                hiddenDepth = Math.Max(0, hiddenDepth + (closing ? -1 : 1));
            }

            i = tagEnd + 1;
        }

        return (output.ToString(), count);
    }

    private static int AppendHighlightedText(StringBuilder output, string segment, string query, int countSoFar)
    {
        // Ordinal comparison keeps every match exactly as long as the query.
        string text = WebUtility.HtmlDecode(segment);
        int index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            output.Append(segment);
            return 0;
        }

        int count = 0;
        int cursor = 0;
        while (index >= 0)
        {
            output.Append(WebUtility.HtmlEncode(text[cursor..index]));
            string id = countSoFar + count == 0 ? $" id=\"{FirstMatchId}\"" : string.Empty;
            output.Append("<span").Append(id).Append(" style=\"background-color: #ffe07d; color: #111111\">")
                  .Append(WebUtility.HtmlEncode(text.Substring(index, query.Length)))
                  .Append("</span>");
            count++;
            cursor = index + query.Length;
            index = cursor < text.Length ? text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase) : -1;
        }

        output.Append(WebUtility.HtmlEncode(text[cursor..]));
        return count;
    }
}
