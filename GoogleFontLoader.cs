using System.Drawing.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomHaze;

/// <summary>One @font-face rule of a Google Fonts stylesheet.</summary>
internal sealed record GoogleFontFace(string Family, string Style, int MinimumWeight, int MaximumWeight, string Url)
{
    public bool Covers(int weight) => weight >= MinimumWeight && weight <= MaximumWeight;

    public int DistanceTo(int weight) => Covers(weight) ? 0 : Math.Min(Math.Abs(weight - MinimumWeight), Math.Abs(weight - MaximumWeight));
}

/// <summary>Downloaded font files for one Google Fonts link, kept in the font cache folder.</summary>
internal sealed class GoogleFontFiles
{
    public string Family { get; init; } = string.Empty;
    public string CssUrl { get; init; } = string.Empty;
    public string RegularPath { get; init; } = string.Empty;
    public string? BoldPath { get; init; }
}

/// <summary>A Google font loaded into this process, ready to be used by WinForms controls.</summary>
internal sealed class LoadedGoogleFont
{
    public LoadedGoogleFont(string family, FontFamily fontFamily, PrivateFontCollection collection, string cssUrl)
    {
        Family = family;
        FontFamily = fontFamily;
        Collection = collection;
        CssUrl = cssUrl;
    }

    public string Family { get; }
    public FontFamily FontFamily { get; }

    /// <summary>Kept alive for as long as the font is used: disposing it invalidates every Font created from it.</summary>
    public PrivateFontCollection Collection { get; }

    public string CssUrl { get; }
}

/// <summary>
/// Turns a Google Fonts link into a font WinForms can draw with. The stylesheet is requested without a browser
/// User-Agent, for which Google Fonts serves TrueType files (browsers get WOFF2, which Windows cannot load).
/// Only fonts.googleapis.com and fonts.gstatic.com are ever contacted, and the files are cached, so a font is only
/// downloaded once.
/// </summary>
internal static class GoogleFontLoader
{
    private const string CssHost = "fonts.googleapis.com";
    private const string FontFileHost = "fonts.gstatic.com";
    private const int MaximumCssBytes = 512 * 1024;
    private const int MaximumFontBytes = 20 * 1024 * 1024;

    private static readonly Regex UrlInTextRegex = new(@"https?://[^\s""'<>()]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FamilyNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9 +\-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex FontFaceRegex = new(@"@font-face\s*\{(?<body>[^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FamilyRegex = new(@"font-family\s*:\s*['""]?(?<value>[^;'""]+)['""]?\s*;", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StyleRegex = new(@"font-style\s*:\s*(?<value>[a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WeightRegex = new(@"font-weight\s*:\s*(?<min>\d{1,4})(?:\s+(?<max>\d{1,4}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SourceRegex = new(@"url\((?<url>[^)]+)\)\s*(?:format\(\s*['""]?(?<format>[a-z0-9-]+)['""]?\s*\))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Accepts what people copy from Google Fonts - a font's page (fonts.google.com/specimen/...), a share link, a
    /// stylesheet link (fonts.googleapis.com/css or /css2), the whole &lt;link&gt; or @import snippet - or just a
    /// family name, and returns the stylesheet address to download.
    /// </summary>
    public static bool TryNormalizeFontUrl(string? input, out string cssUrl, out string error)
    {
        cssUrl = string.Empty;
        error = string.Empty;
        string text = WebUtility.HtmlDecode((input ?? string.Empty).Trim());
        if (text.Length == 0)
        {
            error = "Paste a link from Google Fonts, for example https://fonts.google.com/specimen/Roboto.";
            return false;
        }

        Match urlMatch = UrlInTextRegex.Match(text);
        if (!urlMatch.Success)
        {
            if (FamilyNameRegex.IsMatch(text))
            {
                cssUrl = BuildLegacyCssUrl(text.Replace('+', ' '));
                return true;
            }

            error = "That is not a Google Fonts link. Copy the address of a font's page on fonts.google.com.";
            return false;
        }

        if (!Uri.TryCreate(urlMatch.Value, UriKind.Absolute, out Uri? uri))
        {
            error = "That link is not a valid web address.";
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        string path = uri.AbsolutePath.TrimEnd('/');
        if (host == CssHost)
        {
            if (!path.Equals("/css", StringComparison.OrdinalIgnoreCase) && !path.Equals("/css2", StringComparison.OrdinalIgnoreCase))
            {
                error = "Use a fonts.googleapis.com/css or /css2 stylesheet link.";
                return false;
            }

            if (!uri.Query.Contains("family=", StringComparison.OrdinalIgnoreCase))
            {
                error = "The stylesheet link does not name a font family.";
                return false;
            }

            // Always https, and always the canonical host, whatever was pasted.
            cssUrl = $"https://{CssHost}{path.ToLowerInvariant()}{uri.Query}";
            return true;
        }

        if (host is "fonts.google.com" or "www.fonts.google.com")
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0].Equals("specimen", StringComparison.OrdinalIgnoreCase))
            {
                string family = Uri.UnescapeDataString(segments[1].Replace('+', ' ')).Trim();
                if (FamilyNameRegex.IsMatch(family))
                {
                    cssUrl = BuildLegacyCssUrl(family);
                    return true;
                }
            }

            if (segments.Length >= 1 && segments[0].Equals("share", StringComparison.OrdinalIgnoreCase))
            {
                string? selection = GetQueryValue(uri.Query, "selection.family");
                string? first = selection?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(first))
                {
                    cssUrl = $"https://{CssHost}/css2?family={Uri.EscapeDataString(first).Replace("%20", "+", StringComparison.Ordinal)}";
                    return true;
                }
            }

            error = "Open the font's page on Google Fonts (fonts.google.com/specimen/...) and copy its address.";
            return false;
        }

        error = "Only Google Fonts links (fonts.google.com or fonts.googleapis.com) can be used.";
        return false;
    }

    /// <summary>The legacy stylesheet API returns whichever of the requested weights a family has, so asking for 400 and 700 never fails.</summary>
    private static string BuildLegacyCssUrl(string family)
    {
        return $"https://{CssHost}/css?family={Uri.EscapeDataString(family.Trim()).Replace("%20", "+", StringComparison.Ordinal)}:400,700";
    }

    private static string? GetQueryValue(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    /// <summary>Reads the @font-face rules of a stylesheet, keeping only TrueType/OpenType sources.</summary>
    public static IReadOnlyList<GoogleFontFace> ParseCss(string css)
    {
        var faces = new List<GoogleFontFace>();
        foreach (Match block in FontFaceRegex.Matches(css ?? string.Empty))
        {
            string body = block.Groups["body"].Value;
            Match family = FamilyRegex.Match(body);
            if (!family.Success)
            {
                continue;
            }

            Match style = StyleRegex.Match(body);
            int minimumWeight = 400;
            int maximumWeight = 400;
            Match weight = WeightRegex.Match(body);
            if (weight.Success)
            {
                minimumWeight = int.Parse(weight.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture);
                maximumWeight = weight.Groups["max"].Success
                    ? int.Parse(weight.Groups["max"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : minimumWeight;
                if (maximumWeight < minimumWeight)
                {
                    (minimumWeight, maximumWeight) = (maximumWeight, minimumWeight);
                }
            }

            foreach (Match source in SourceRegex.Matches(body))
            {
                string url = source.Groups["url"].Value.Trim().Trim('\'', '"');
                string format = source.Groups["format"].Value.ToLowerInvariant();
                bool usable = format is "truetype" or "opentype" ||
                              (format.Length == 0 && (url.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                                      url.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)));
                if (usable)
                {
                    faces.Add(new GoogleFontFace(
                        family.Groups["value"].Value.Trim(),
                        style.Success ? style.Groups["value"].Value.ToLowerInvariant() : "normal",
                        minimumWeight,
                        maximumWeight,
                        url));
                    break;
                }
            }
        }

        return faces;
    }

    /// <summary>Picks the upright regular (400) face and, when there is a different one, the bold (700) face of the first family.</summary>
    public static (GoogleFontFace Regular, GoogleFontFace? Bold)? ChooseFaces(IReadOnlyList<GoogleFontFace> faces)
    {
        if (faces.Count == 0)
        {
            return null;
        }

        string family = faces[0].Family;
        List<GoogleFontFace> candidates = faces
            .Where(face => face.Family.Equals(family, StringComparison.OrdinalIgnoreCase) && face.Style == "normal")
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = faces.Where(face => face.Family.Equals(family, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        GoogleFontFace regular = candidates.OrderBy(face => face.DistanceTo(400)).First();
        GoogleFontFace? bold = candidates
            .Where(face => face.MaximumWeight >= 600 && !face.Url.Equals(regular.Url, StringComparison.Ordinal))
            .OrderBy(face => face.DistanceTo(700))
            .FirstOrDefault();
        return (regular, bold);
    }

    public static string GetCacheKey(string cssUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cssUrl));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private sealed class CacheRecord
    {
        public string Family { get; set; } = string.Empty;
        public string CssUrl { get; set; } = string.Empty;
        public string RegularFile { get; set; } = string.Empty;
        public string? BoldFile { get; set; }
    }

    /// <summary>The cached files for a link, or null when it has not been downloaded (or the cache is incomplete).</summary>
    public static GoogleFontFiles? TryGetCached(string cssUrl, string cacheDirectory)
    {
        try
        {
            string recordPath = Path.Combine(cacheDirectory, $"{GetCacheKey(cssUrl)}.json");
            if (!File.Exists(recordPath))
            {
                return null;
            }

            CacheRecord? record = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(recordPath));
            if (record is null || !string.Equals(record.CssUrl, cssUrl, StringComparison.Ordinal) ||
                !IsPlainFileName(record.RegularFile) || (record.BoldFile is not null && !IsPlainFileName(record.BoldFile)))
            {
                return null;
            }

            string regularPath = Path.Combine(cacheDirectory, record.RegularFile);
            string? boldPath = record.BoldFile is null ? null : Path.Combine(cacheDirectory, record.BoldFile);
            if (!File.Exists(regularPath) || (boldPath is not null && !File.Exists(boldPath)))
            {
                return null;
            }

            return new GoogleFontFiles { Family = record.Family, CssUrl = cssUrl, RegularPath = regularPath, BoldPath = boldPath };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <param name="isBlocked">True for addresses an extension's web.block rule blocks; those are never requested.</param>
    public static async Task<GoogleFontFiles> DownloadAsync(string cssUrl, string cacheDirectory, Func<string, bool> isBlocked, CancellationToken cancellationToken)
    {
        // Redirects are not followed: every address is checked against the two Google Fonts hosts first.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaximumFontBytes,
        };

        byte[] cssBytes = await DownloadAsync(client, cssUrl, CssHost, MaximumCssBytes, isBlocked, cancellationToken);
        IReadOnlyList<GoogleFontFace> faces = ParseCss(Encoding.UTF8.GetString(cssBytes));
        (GoogleFontFace Regular, GoogleFontFace? Bold)? choice = ChooseFaces(faces);
        if (choice is null)
        {
            throw new InvalidDataException("Google Fonts did not return a font PhantomHaze can use for that link.");
        }

        (GoogleFontFace regular, GoogleFontFace? bold) = choice.Value;
        Directory.CreateDirectory(cacheDirectory);
        string key = GetCacheKey(cssUrl);
        string regularFile = $"{key}-regular{GetFontExtension(regular.Url)}";
        await SaveFontAsync(client, regular.Url, Path.Combine(cacheDirectory, regularFile), isBlocked, cancellationToken);

        string? boldFile = null;
        if (bold is not null)
        {
            boldFile = $"{key}-bold{GetFontExtension(bold.Url)}";
            try
            {
                await SaveFontAsync(client, bold.Url, Path.Combine(cacheDirectory, boldFile), isBlocked, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
            {
                // Bold is optional: Windows draws a bold version of the regular face instead.
                boldFile = null;
            }
        }

        var record = new CacheRecord { Family = regular.Family, CssUrl = cssUrl, RegularFile = regularFile, BoldFile = boldFile };
        string recordPath = Path.Combine(cacheDirectory, $"{key}.json");
        string tempPath = recordPath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(record), cancellationToken);
        File.Move(tempPath, recordPath, overwrite: true);

        return new GoogleFontFiles
        {
            Family = regular.Family,
            CssUrl = cssUrl,
            RegularPath = Path.Combine(cacheDirectory, regularFile),
            BoldPath = boldFile is null ? null : Path.Combine(cacheDirectory, boldFile),
        };
    }

    /// <summary>
    /// Loads the files for use by WinForms: into a PrivateFontCollection (for GDI+ and the Font objects) and as a
    /// process-private GDI font (for the GDI text drawing most controls use), so the font is visible to both.
    /// </summary>
    public static LoadedGoogleFont Load(GoogleFontFiles files)
    {
        var collection = new PrivateFontCollection();
        try
        {
            foreach (string path in new[] { files.RegularPath, files.BoldPath }.OfType<string>())
            {
                collection.AddFontFile(path);
                NativeMethods.RegisterPrivateFontFile(path);
            }

            FontFamily family = collection.Families.FirstOrDefault(candidate =>
                                    candidate.Name.Equals(files.Family, StringComparison.OrdinalIgnoreCase)) ??
                                collection.Families.FirstOrDefault() ??
                                throw new InvalidDataException("The downloaded font file contains no usable font.");
            return new LoadedGoogleFont(family.Name, family, collection, files.CssUrl);
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    private static async Task SaveFontAsync(HttpClient client, string url, string path, Func<string, bool> isBlocked, CancellationToken cancellationToken)
    {
        byte[] bytes = await DownloadAsync(client, url, FontFileHost, MaximumFontBytes, isBlocked, cancellationToken);
        if (!LooksLikeFontFile(bytes))
        {
            throw new InvalidDataException("Google Fonts returned a file that is not a TrueType or OpenType font.");
        }

        string tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Normally already gone, because it was renamed.
            }
        }
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string url, string requiredHost, int maximumBytes, Func<string, bool> isBlocked, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(requiredHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Refused to download from {uri?.Host ?? url}: fonts are only downloaded from Google Fonts.");
        }

        if (isBlocked(uri.AbsoluteUri))
        {
            throw new InvalidOperationException($"An extension blocks {uri.Host}, so the font cannot be downloaded.");
        }

        using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException($"Google Fonts redirected the request ({(int)response.StatusCode}).");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest && requiredHost == CssHost)
        {
            throw new InvalidDataException("Google Fonts does not know that font (check the link or the family name).");
        }

        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException("The download from Google Fonts was unexpectedly large.");
        }

        return bytes;
    }

    private static bool LooksLikeFontFile(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        uint tag = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        return tag is 0x00010000 /* TrueType */ or 0x4F54544F /* OTTO */ or 0x74727565 /* true */;
    }

    private static string GetFontExtension(string url)
    {
        return url.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? ".otf" : ".ttf";
    }

    private static bool IsPlainFileName(string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal);
    }
}
