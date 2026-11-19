using System.Net;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

/// <summary>
/// The parts of the OpenHTML protocol that do not need a browser tab (see MainForm.WebProtocols.cs): which responses
/// are pages, how Set-Cookie headers are read, and the redirect and error pages PhantomHaze shows.
/// </summary>
internal static class OpenHtmlDocuments
{
    public static bool IsHtmlMediaType(string? mediaType)
    {
        // Without a Content-Type the document is treated as HTML, which is how browsers sniff a page.
        return mediaType is null ||
               mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A cookie to store, or (Cookie is null) a cookie the header deletes: the one with that name, domain and path
    /// (a domain starting with "." is a domain cookie, otherwise a cookie for that exact host).
    /// </summary>
    public sealed record CookieChange(string Name, Cookie? Cookie, CoreWebView2CookieSameSiteKind? SameSite, string Domain, string Path);

    // Response headers handed on to the tab with an OpenHTML page or file. They protect the user (who may frame the page,
    // what it and its scripts may load, how it is sniffed), decide what the page's JavaScript may use (cross-origin
    // isolation) or say how to show it (a download's file name). Cookies were applied
    // separately; encoding and length headers describe the network transfer, not the decoded body the tab gets.
    private static readonly string[] PassedOnResponseHeaders =
    [
        "Content-Security-Policy",
        "X-Frame-Options",
        "X-Content-Type-Options",
        "Referrer-Policy",
        "Permissions-Policy",
        "Cross-Origin-Opener-Policy",
        "Cross-Origin-Embedder-Policy",
        "Cross-Origin-Resource-Policy",
        "Origin-Agent-Cluster",
        "Content-Disposition",
        "Content-Language",
        "Refresh",
    ];

    /// <summary>The response headers for the tab: the content type, the passed-on headers and no caching.</summary>
    public static string BuildResponseHeaders(string contentType, IEnumerable<KeyValuePair<string, IEnumerable<string>>> siteHeaders)
    {
        var lines = new List<string> { $"Content-Type: {RemoveLineBreaks(contentType)}", "Cache-Control: no-store" };
        foreach ((string name, IEnumerable<string> values) in siteHeaders)
        {
            string? allowed = PassedOnResponseHeaders.FirstOrDefault(header => header.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (allowed is null)
            {
                continue;
            }

            foreach (string value in values)
            {
                string cleaned = RemoveLineBreaks(value);
                if (cleaned.Length > 0)
                {
                    lines.Add($"{allowed}: {cleaned}");
                }
            }
        }

        return string.Join("\r\n", lines);
    }

    private static string RemoveLineBreaks(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>The address without its #fragment (requests never carry one; navigations can).</summary>
    public static string WithoutFragment(string url)
    {
        int hash = url.IndexOf('#');
        return hash < 0 ? url : url[..hash];
    }

    /// <summary>
    /// Whether another site led to this page (a link or redirect from it), in which case browsers do not send
    /// SameSite=Strict cookies. The Sec-Fetch-Site header says so when present; otherwise the Referer is compared. No
    /// Referer means the address was typed or opened from a bookmark: same-site.
    /// </summary>
    public static bool IsCrossSiteNavigation(string? secFetchSite, string? referer, Uri target)
    {
        if (!string.IsNullOrWhiteSpace(secFetchSite))
        {
            return secFetchSite.Trim().Equals("cross-site", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out Uri? from))
        {
            return false;
        }

        return !IsSameSite(from.Host, target.Host);
    }

    /// <summary>
    /// Whether two hosts belong to the same site (registrable domain), approximated without the public suffix list:
    /// the last two labels, or three for two-letter country domains with a short second level (example.co.uk).
    /// </summary>
    public static bool IsSameSite(string firstHost, string secondHost)
    {
        static string Site(string host)
        {
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (Uri.CheckHostName(host) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                return host;
            }

            string[] labels = host.Split('.');
            if (labels.Length <= 2)
            {
                return host;
            }

            int count = labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
            return string.Join('.', labels[^count..]);
        }

        return string.Equals(Site(firstHost), Site(secondHost), StringComparison.Ordinal);
    }

    /// <summary>The path a cookie gets without a Path attribute (RFC 6265: the request path up to its last "/").</summary>
    public static string GetDefaultCookiePath(Uri uri)
    {
        string path = uri.AbsolutePath;
        int lastSlash = path.LastIndexOf('/');
        return string.IsNullOrEmpty(path) || path[0] != '/' || lastSlash <= 0 ? "/" : path[..lastSlash];
    }

    /// <summary>Parses one Set-Cookie header with System.Net's cookie rules, plus the SameSite attribute it ignores.</summary>
    public static List<CookieChange> ParseSetCookieHeader(Uri uri, string header)
    {
        var changes = new List<CookieChange>();
        int equals = header.IndexOf('=');
        int semicolon = header.IndexOf(';');
        string name = (equals >= 0 && (semicolon < 0 || equals < semicolon) ? header[..equals] : string.Empty).Trim();
        if (name.Length == 0)
        {
            return changes;
        }

        CoreWebView2CookieSameSiteKind? sameSite = null;
        bool explicitDomain = false;
        string? domainAttribute = null;
        string? pathAttribute = null;
        foreach (string attribute in header.Split(';').Skip(1))
        {
            string[] parts = attribute.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string attributeName = parts[0].Trim();
            if (attributeName.Equals("SameSite", StringComparison.OrdinalIgnoreCase))
            {
                sameSite = parts[1].Trim().ToLowerInvariant() switch
                {
                    "none" => CoreWebView2CookieSameSiteKind.None,
                    "lax" => CoreWebView2CookieSameSiteKind.Lax,
                    "strict" => CoreWebView2CookieSameSiteKind.Strict,
                    _ => null,
                };
            }
            else if (attributeName.Equals("Domain", StringComparison.OrdinalIgnoreCase) && parts[1].Trim().Length > 0)
            {
                explicitDomain = true;
                domainAttribute = parts[1].Trim();
            }
            else if (attributeName.Equals("Path", StringComparison.OrdinalIgnoreCase) && parts[1].Trim().StartsWith('/'))
            {
                pathAttribute = parts[1].Trim();
            }
        }

        var container = new CookieContainer();
        try
        {
            container.SetCookies(uri, header);
        }
        catch (CookieException)
        {
            return changes;
        }

        List<Cookie> cookies = container.GetAllCookies().Where(cookie => !cookie.Expired).ToList();
        if (cookies.Count == 0)
        {
            // An expiry date in the past (or Max-Age=0): the site deletes the cookie with that name, domain and path.
            string domain = domainAttribute is null ? uri.Host : "." + domainAttribute.TrimStart('.');
            changes.Add(new CookieChange(name, null, null, domain, pathAttribute ?? GetDefaultCookiePath(uri)));
            return changes;
        }

        foreach (Cookie cookie in cookies)
        {
            // System.Net writes "Domain=example.com" as "example.com", which WebView2 would read as a cookie for that
            // exact host only. A leading dot keeps it a domain cookie, sent to www.example.com and other subdomains too.
            if (explicitDomain && !cookie.Domain.StartsWith('.'))
            {
                cookie.Domain = "." + cookie.Domain;
            }

            changes.Add(new CookieChange(cookie.Name, cookie, sameSite, cookie.Domain, cookie.Path));
        }

        return changes;
    }

    public static string BuildRedirectPage(string targetUrl)
    {
        string attribute = WebUtility.HtmlEncode(targetUrl);
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
               $"<meta http-equiv=\"refresh\" content=\"0; url={attribute}\"><title>Redirecting</title></head>" +
               "<body style=\"font-family:'Segoe UI',sans-serif;padding:24px\">" +
               $"<p>Redirecting to <a href=\"{attribute}\">{attribute}</a></p></body></html>";
    }

    public static string BuildMessagePage(string title, string url, string message)
    {
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
               $"<title>{WebUtility.HtmlEncode(title)}</title></head>" +
               "<body style=\"font-family:'Segoe UI',sans-serif;padding:32px;max-width:720px;margin:auto;line-height:1.5\">" +
               $"<h2>{WebUtility.HtmlEncode(title)}</h2>" +
               $"<p><b>Address:</b> {WebUtility.HtmlEncode(url)}</p>" +
               $"<p><b>Problem:</b> {WebUtility.HtmlEncode(message)}</p>" +
               "<p>You can try again with Refresh, or switch to the WebView2 protocol in Settings &rarr; General.</p>" +
               "</body></html>";
    }

    /// <summary>The reason phrase from the site, without characters that could break the response headers.</summary>
    public static string SanitizeReasonPhrase(string? reason, int status)
    {
        string cleaned = new((reason ?? string.Empty).Where(character => character >= ' ' && character != '\u007F').ToArray());
        if (cleaned.Length == 0)
        {
            cleaned = status is >= 200 and < 300 ? "OK" : "Status";
        }

        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }
}
