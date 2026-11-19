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

    /// <summary>A cookie to store, or (Cookie is null) the name of a cookie the header deletes.</summary>
    public sealed record CookieChange(string Name, Cookie? Cookie, CoreWebView2CookieSameSiteKind? SameSite);

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
            // An expiry date in the past (or Max-Age=0): the site deletes the cookie.
            changes.Add(new CookieChange(name, null, null));
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

            changes.Add(new CookieChange(cookie.Name, cookie, sameSite));
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
