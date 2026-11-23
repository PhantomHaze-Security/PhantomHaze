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
/// The HTMLRenderer protocol: every page's HTML is downloaded with the curl.exe that ships with Windows
/// (curl -o index.html https://example.com/) and drawn with HTML Renderer (https://github.com/ArthurHub/HTML-Renderer).
/// WebView2 is not used at all. Pages cannot run JavaScript, and HTML Renderer supports HTML 4 and CSS 2.
/// </summary>
internal sealed class HtmlRendererWebProtocol : WebProtocol
{
    public HtmlRendererWebProtocol(string? proxyServer = null)
    {
        ProxyServer = proxyServer;
    }

    /// <summary>The proxy server of an extension protocol (pages, images and stylesheets all go through it), or null.</summary>
    public string? ProxyServer { get; }

    public override string Id => WebProtocolCatalog.HtmlRendererId;

    public override string DisplayName => "HTMLRenderer";

    public override string Description =>
        "Downloads each page's HTML with curl (curl -o index.html <address>) and draws it with HTML Renderer, without " +
        "WebView2. Light and script-free: no JavaScript, HTML 4 and CSS 2 only, so modern sites look plain. Extensions' " +
        "web.css styles apply; web.script does not.";

    public override WebProtocolFeatures Features => WebProtocolFeatures.FindInPage;

    public override string? GetUnavailableReason()
    {
        return CurlTool.ExecutablePath is null
            ? $"curl.exe was not found in {Environment.SystemDirectory} (it ships with Windows 10 version 1803 and later)."
            : null;
    }

    public override WebProtocolPage CreatePage(IWebProtocolHost host) => new HtmlRendererProtocolPage(this, host, ProxyServer);
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

/// <summary>A tab shown with the HTMLRenderer protocol.</summary>
internal sealed class HtmlRendererProtocolPage : WebProtocolPage
{
    private const int MaximumDocumentBytes = 10 * 1024 * 1024;
    private const int MaximumRedirects = 10;

    // Unknown (HTML5) elements are inline in HTML Renderer; these are blocks, as in every browser.
    private const string BaseStyleSheet =
        "article, aside, details, dialog, figcaption, figure, footer, header, hgroup, main, nav, section, summary { display: block; }\n" +
        "img { border: 0; }";

    private readonly HtmlPanel _panel;
    private readonly string? _proxyServer;
    private readonly List<string> _history = new();
    private readonly string _folderName = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _resourceSlots = new(6, 6);
    private int _historyIndex = -1;
    private string _currentUrl = string.Empty;
    private string _title = string.Empty;
    private Uri? _baseUri;
    private string? _faviconUrl;
    private string? _documentHtml;
    private bool _documentHighlighted;
    private int _findVersion;
    private IReadOnlyDictionary<string, string> _styleSheets = new Dictionary<string, string>();
    private Dictionary<string, Task<Image?>> _images = new(StringComparer.Ordinal);
    private CancellationTokenSource _documentCancellation = new();
    private CancellationTokenSource? _loadCancellation;
    private Process? _curlProcess;
    private int _loadSequence;
    private bool _initialized;

    public HtmlRendererProtocolPage(WebProtocol protocol, IWebProtocolHost host, string? proxyServer)
        : base(protocol, host)
    {
        _proxyServer = proxyServer;
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
    }

    public override Control View => _panel;

    public override bool IsReady => _initialized && !IsDisposed;

    public override string CurrentUrl => _currentUrl;

    public override string Title => _title;

    public override bool CanGoBack => _historyIndex > 0;

    public override bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public override Task InitializeAsync()
    {
        if (Protocol.GetUnavailableReason() is string reason)
        {
            throw new WebProtocolUnavailableException(reason);
        }

        _initialized = true;
        return Task.CompletedTask;
    }

    public override void Navigate(string url) => StartLoad(url, HistoryUpdate.Push);

    public override void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        _historyIndex--;
        StartLoad(_history[_historyIndex], HistoryUpdate.ReplaceCurrent);
    }

    public override void GoForward()
    {
        if (!CanGoForward)
        {
            return;
        }

        _historyIndex++;
        StartLoad(_history[_historyIndex], HistoryUpdate.ReplaceCurrent);
    }

    public override void Reload()
    {
        if (_currentUrl.Length > 0)
        {
            StartLoad(_currentUrl, HistoryUpdate.ReplaceCurrent);
        }
    }

    public override Task<string?> GetDeclaredFaviconUrlAsync() => Task.FromResult(_faviconUrl);

    private enum HistoryUpdate
    {
        Push,
        ReplaceCurrent,
    }

    private void StartLoad(string url, HistoryUpdate historyUpdate)
    {
        if (IsDisposed)
        {
            return;
        }

        CancelLoad();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        int sequence = ++_loadSequence;
        OnNavigationStarting(url);
        _ = LoadAsync(url, historyUpdate, sequence, cancellation.Token);
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        if (_curlProcess is Process process)
        {
            CurlTool.TryKill(process);
            _curlProcess = null;
        }
    }

    private async Task LoadAsync(string requestedUrl, HistoryUpdate historyUpdate, int sequence, CancellationToken cancellationToken)
    {
        HtmlRendererDocument document;
        try
        {
            document = await DownloadDocumentAsync(requestedUrl, cancellationToken);
            if (document.StyleSheetLinks.Count > 0)
            {
                await DownloadStyleSheetsAsync(document, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            document = HtmlRendererDocumentBuilder.BuildMessagePage(
                requestedUrl,
                "This page could not be opened",
                ex is HtmlRendererLoadException ? ex.Message : $"Something went wrong while opening the page: {ex.Message}",
                isSuccess: false);
        }

        if (IsDisposed || sequence != _loadSequence)
        {
            document.PrimaryImage?.Dispose();
            return;
        }

        ShowDocument(document, historyUpdate);
    }

    private async Task<HtmlRendererDocument> DownloadDocumentAsync(string requestedUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out Uri? requested) || !IsWebAddress(requestedUrl))
        {
            throw new HtmlRendererLoadException("The HTMLRenderer protocol only opens http:// and https:// addresses.");
        }

        string curl = CurlTool.ExecutablePath
            ?? throw new HtmlRendererLoadException($"curl.exe was not found in {Environment.SystemDirectory}.");
        string folder = Path.Combine(Host.SessionDirectory, "htmlrenderer", _folderName);
        Directory.CreateDirectory(folder);
        string outputPath = Path.Combine(folder, "index.html");
        string fragment = requested.Fragment;

        // Redirects are followed here, one curl run per step, so every address is checked against web.block before
        // anything is requested from it (curl -L would follow them without asking).
        string current = requested.GetLeftPart(UriPartial.Query);
        for (int redirects = 0; ; redirects++)
        {
            if (Host.IsBlocked(current, out string blockedBy))
            {
                Host.Log($"HTMLRenderer did not open {TrimForLog(current)}: it is blocked by {blockedBy}.");
                return HtmlRendererDocumentBuilder.BuildMessagePage(current, "Blocked by an extension", $"This address is blocked by {blockedBy}.", isSuccess: false);
            }

            CurlResult result = await RunCurlAsync(curl, current, outputPath, cancellationToken);
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

                if (!Uri.TryCreate(new Uri(current), result.RedirectUrl, out Uri? next) || !IsWebAddress(next.AbsoluteUri))
                {
                    throw new HtmlRendererLoadException($"The page redirected to an address that is not a web page: {TrimForLog(result.RedirectUrl, 200)}");
                }

                current = next.GetLeftPart(UriPartial.Query);
                if (next.Fragment.Length > 0)
                {
                    fragment = next.Fragment;
                }

                continue;
            }

            byte[] body = File.Exists(outputPath) ? await File.ReadAllBytesAsync(outputPath, cancellationToken) : Array.Empty<byte>();
            if (body.Length > MaximumDocumentBytes)
            {
                throw new HtmlRendererLoadException("The page is larger than 10 MB, which the HTMLRenderer protocol does not open.");
            }

            string pageUrl = current + fragment;
            IReadOnlyList<string> extensionStyles = Host.GetExtensionStyleSheets(current);
            return await Task.Run(
                () => HtmlRendererDocumentBuilder.Build(pageUrl, result.StatusCode, result.ContentType, body, extensionStyles),
                cancellationToken);
        }
    }

    private sealed record CurlResult(int ExitCode, int StatusCode, string RedirectUrl, string ContentType, string Error);

    private async Task<CurlResult> RunCurlAsync(string curl, string url, string outputPath, CancellationToken cancellationToken)
    {
        Version? version = await CurlTool.GetVersionAsync();
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteFile(outputPath);

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

    private async Task DownloadStyleSheetsAsync(HtmlRendererDocument document, CancellationToken cancellationToken)
    {
        var downloads = document.StyleSheetLinks.Select(async link =>
        {
            ResourceResponse? response = await DownloadResourceAsync(link.Url, HtmlRendererResources.MaximumStyleSheetBytes, cancellationToken);
            if (response is null ||
                (response.ContentType.Length > 0 && !response.ContentType.Contains("css", StringComparison.OrdinalIgnoreCase)))
            {
                return (link.Token, Css: string.Empty);
            }

            string css = HtmlRendererDocumentBuilder.DecodeText(response.Body, response.ContentType, isHtml: false);
            return (link.Token, Css: HtmlRendererDocumentBuilder.MakeCssUrlsAbsolute(css, new Uri(response.Url)));
        });

        foreach ((string token, string css) in await Task.WhenAll(downloads))
        {
            document.StyleSheets[token] = css;
        }
    }

    private sealed record ResourceResponse(string Url, string ContentType, byte[] Body);

    /// <summary>Downloads an image or stylesheet, checking every address (including redirects) against web.block.</summary>
    private async Task<ResourceResponse?> DownloadResourceAsync(string url, int maximumBytes, CancellationToken cancellationToken)
    {
        await _resourceSlots.WaitAsync(cancellationToken);
        try
        {
            string current = url;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                if (!Uri.TryCreate(current, UriKind.Absolute, out Uri? uri) || !IsWebAddress(current) || Host.IsBlocked(uri.AbsoluteUri, out _))
                {
                    return null;
                }

                HtmlRendererResources.Response response = await Task.Run(
                    () => HtmlRendererResources.GetAsync(uri, _proxyServer, maximumBytes, cancellationToken),
                    cancellationToken);
                if (response.RedirectTo is Uri redirect)
                {
                    // Like browsers, never follow an https -> http redirect for a part of a page.
                    if (uri.Scheme == Uri.UriSchemeHttps && redirect.Scheme != Uri.UriSchemeHttps)
                    {
                        return null;
                    }

                    current = redirect.AbsoluteUri;
                    continue;
                }

                return response.Body is null ? null : new ResourceResponse(uri.AbsoluteUri, response.ContentType, response.Body);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _resourceSlots.Release();
        }
    }

    private void ShowDocument(HtmlRendererDocument document, HistoryUpdate historyUpdate)
    {
        // Images belong to one document: the next document starts a new set, and the old images are disposed once the
        // panel no longer shows them.
        _documentCancellation.Cancel();
        _documentCancellation.Dispose();
        _documentCancellation = new CancellationTokenSource();
        Dictionary<string, Task<Image?>> previousImages = _images;
        _images = new Dictionary<string, Task<Image?>>(StringComparer.Ordinal);
        if (document.PrimaryImage is Image primary)
        {
            _images[document.PrimaryImageUrl!] = Task.FromResult<Image?>(primary);
        }

        _currentUrl = document.Url;
        _title = document.Title;
        _baseUri = document.BaseUri;
        _faviconUrl = document.FaviconUrl;
        _styleSheets = document.StyleSheets;
        _documentHtml = document.Html;
        _documentHighlighted = false;
        _findVersion++;
        SetPanelHtml(document.Html, keepScrollPosition: false);
        DisposeImages(previousImages);

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

        OnNavigationCompleted(document.IsSuccess, document.ErrorDescription);
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

    private static void DisposeImages(Dictionary<string, Task<Image?>> images)
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

            if (link.StartsWith('#'))
            {
                ScrollToFragment(Uri.UnescapeDataString(link[1..]));
                return;
            }

            if (!TryResolve(link, out string url) || !IsWebAddress(url))
            {
                Host.Log($"HTMLRenderer did not open a link that is not a web address: {TrimForLog(link, 200)}");
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
                Navigate(url);
            }
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            Host.Log($"HTMLRenderer could not open a link: {ex.Message}");
        }
    }

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

        // Inline images (data:image/...) are decoded by HTML Renderer itself; nothing is downloaded.
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

    private async Task<Image?> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        ResourceResponse? response = await DownloadResourceAsync(url, HtmlRendererResources.MaximumImageBytes, cancellationToken);
        if (response is null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return await Task.Run(() => HtmlRendererResources.DecodeImage(response.Body), CancellationToken.None);
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
        _panel.LinkClicked -= OnLinkClicked;
        _panel.StylesheetLoad -= OnStylesheetLoad;
        _panel.ImageLoad -= OnImageLoad;
        try
        {
            _panel.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Best effort cleanup only.
        }

        DisposeImages(_images);
        _images = new Dictionary<string, Task<Image?>>(StringComparer.Ordinal);
        try
        {
            Directory.Delete(Path.Combine(Host.SessionDirectory, "htmlrenderer", _folderName), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The session folder is deleted when PhantomHaze closes anyway.
        }
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

/// <summary>A page could not be opened with the HTMLRenderer protocol; the message is shown on the page.</summary>
internal sealed class HtmlRendererLoadException : Exception
{
    public HtmlRendererLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>Downloads the images and stylesheets of HTMLRenderer pages.</summary>
internal static class HtmlRendererResources
{
    public const int MaximumImageBytes = 8 * 1024 * 1024;
    public const int MaximumStyleSheetBytes = 2 * 1024 * 1024;
    private const long MaximumImagePixels = 40_000_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    public sealed record Response(Uri? RedirectTo, string ContentType, byte[]? Body);

    /// <summary>One request, without following redirects (the caller checks each redirect against web.block first).</summary>
    public static async Task<Response> GetAsync(Uri uri, string? proxyServer, int maximumBytes, CancellationToken cancellationToken)
    {
        HttpClient client = GetClient(proxyServer);
        using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        if (status is 301 or 302 or 303 or 307 or 308)
        {
            return response.Headers.Location is Uri location
                ? new Response(new Uri(uri, location), string.Empty, null)
                : new Response(null, string.Empty, null);
        }

        string contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes)
        {
            return new Response(null, contentType, null);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                return new Response(null, contentType, null);
            }

            buffer.Write(chunk, 0, read);
        }

        return new Response(null, contentType, buffer.ToArray());
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
                Timeout = TimeSpan.FromSeconds(20),
                MaxResponseContentBufferSize = MaximumImageBytes,
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PhantomHaze", AppVersionInfo.Tag?.VersionNumber ?? "1"));
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

    private static readonly Regex MetaCharsetRegex = new(
        @"<meta[^>]{0,400}?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]{1,40})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    static HtmlRendererDocumentBuilder()
    {
        // windows-1252, Shift_JIS, GB2312 and the other legacy page encodings.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
    public static string DecodeText(byte[] body, string contentType, bool isHtml)
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
        foreach (string part in contentType.Split(';'))
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
                Match match = MetaCharsetRegex.Match(head);
                if (match.Success)
                {
                    charset = match.Groups[1].Value;
                }
            }
            catch (RegexMatchTimeoutException)
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

    /// <summary>Rewrites url(...) references in a downloaded stylesheet so they are resolved against the stylesheet's own address.</summary>
    public static string MakeCssUrlsAbsolute(string css, Uri styleSheetUri)
    {
        var output = new StringBuilder(css.Length + 64);
        int i = 0;
        while (i < css.Length)
        {
            int start = css.IndexOf("url(", i, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                output.Append(css, i, css.Length - i);
                break;
            }

            int valueStart = start + 4;
            int end = css.IndexOf(')', valueStart);
            if (end < 0)
            {
                output.Append(css, i, css.Length - i);
                break;
            }

            output.Append(css, i, valueStart - i);
            string value = css[valueStart..end].Trim().Trim('"', '\'').Trim();
            if (value.Length > 0 &&
                !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(styleSheetUri, value, out Uri? absolute) &&
                WebProtocolPage.IsWebAddress(absolute.AbsoluteUri))
            {
                output.Append('"').Append(absolute.AbsoluteUri.Replace("\"", "%22", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                output.Append(css, valueStart, end - valueStart);
            }

            output.Append(')');
            i = end + 1;
        }

        return output.ToString();
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
