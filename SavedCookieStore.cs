using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhantomHaze;

/// <summary>One cookie kept by "Save cookies across sessions".</summary>
internal sealed class SavedCookie
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Path { get; set; } = "/";

    /// <summary>The site's expiry date; null for a session cookie (one without an expiry date).</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    public bool IsHttpOnly { get; set; }
    public bool IsSecure { get; set; }

    /// <summary>The cookie's SameSite setting, as the number of the WebView2 CoreWebView2CookieSameSiteKind value.</summary>
    public int SameSite { get; set; }

    /// <summary>When PhantomHaze first saved this cookie; the retention period counts from here.</summary>
    public DateTimeOffset SavedAtUtc { get; set; }

    /// <summary>A cookie is identified by its name, domain and path (the same name can exist for several paths).</summary>
    [JsonIgnore]
    public string Key => MakeKey(Name, Domain, Path);

    public static string MakeKey(string name, string domain, string path) => $"{name}\n{domain}\n{path}";

    public bool HasSameContent(SavedCookie other)
    {
        return Name == other.Name && Value == other.Value && Domain == other.Domain && Path == other.Path &&
               ExpiresUtc == other.ExpiresUtc && IsHttpOnly == other.IsHttpOnly && IsSecure == other.IsSecure &&
               SameSite == other.SameSite && SavedAtUtc == other.SavedAtUtc;
    }
}

/// <summary>
/// The cookies PhantomHaze keeps between sessions, and the rules for keeping them: a cookie is dropped once the site's
/// expiry date passes, or once the retention period (Settings -> General, counted from when the cookie was first
/// saved) is over. Pure logic without WebView2 or file access, so it can be tested on its own; MainForm.Cookies.cs
/// connects it to the browser and to the encrypted cookies.dat file.
/// </summary>
internal sealed class SavedCookieStore
{
    private const int FileFormatVersion = 1;

    // When each cookie that is in the browser right now was first saved. Kept for cookies that are past the retention
    // period too, so a cookie the browser still holds is not saved again as if it were new.
    private readonly Dictionary<string, DateTimeOffset> _firstSavedUtc = new(StringComparer.Ordinal);
    private List<SavedCookie> _cookies = new();

    public IReadOnlyList<SavedCookie> Cookies => _cookies;

    /// <param name="retentionDays">Days a cookie is kept after it was first saved; negative keeps it until it expires.</param>
    public static bool IsWithinRetention(SavedCookie cookie, int retentionDays, DateTimeOffset now)
    {
        return retentionDays < 0 || cookie.SavedAtUtc > now.AddDays(-retentionDays);
    }

    public static bool IsExpired(SavedCookie cookie, DateTimeOffset now) => cookie.ExpiresUtc is DateTimeOffset expires && expires <= now;

    private static bool IsUsable(SavedCookie? cookie) => cookie is not null && !string.IsNullOrWhiteSpace(cookie.Domain) && cookie.Name is not null;

    /// <summary>Starts from the cookies read from cookies.dat, dropping expired ones and ones past the retention period.</summary>
    public void Load(IEnumerable<SavedCookie?> cookies, int retentionDays, DateTimeOffset now)
    {
        Clear();
        foreach (SavedCookie cookie in cookies.Where(IsUsable).Cast<SavedCookie>())
        {
            cookie.Path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
            cookie.Value ??= string.Empty;
            if (cookie.SavedAtUtc == default || cookie.SavedAtUtc > now)
            {
                // A missing or future date (a clock change) would keep the cookie for too long.
                cookie.SavedAtUtc = now;
            }

            if (IsExpired(cookie, now) || !IsWithinRetention(cookie, retentionDays, now))
            {
                continue;
            }

            // A file edited by hand could list a cookie twice; the last one wins.
            _cookies.RemoveAll(existing => existing.Key == cookie.Key);
            _cookies.Add(cookie);
            _firstSavedUtc[cookie.Key] = cookie.SavedAtUtc;
        }
    }

    /// <summary>
    /// Replaces the saved cookies with the browser's current cookies. Cookies keep the date they were first saved;
    /// new ones are dated now. Returns true when anything changed (so the file needs writing).
    /// </summary>
    public bool Update(IEnumerable<SavedCookie> live, int retentionDays, DateTimeOffset now)
    {
        var next = new List<SavedCookie>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SavedCookie cookie in live.Where(IsUsable))
        {
            string key = cookie.Key;
            if (!seen.Add(key) || IsExpired(cookie, now))
            {
                continue;
            }

            if (!_firstSavedUtc.TryGetValue(key, out DateTimeOffset firstSaved))
            {
                firstSaved = now;
                _firstSavedUtc[key] = now;
            }

            cookie.SavedAtUtc = firstSaved;
            if (IsWithinRetention(cookie, retentionDays, now))
            {
                next.Add(cookie);
            }
        }

        // A cookie the site deleted starts over if the site sets it again later (for example after signing back in).
        foreach (string key in _firstSavedUtc.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _firstSavedUtc.Remove(key);
        }

        next.Sort((first, second) => string.CompareOrdinal(first.Key, second.Key));
        bool changed = next.Count != _cookies.Count || next.Where((cookie, index) => !cookie.HasSameContent(_cookies[index])).Any();
        _cookies = next;
        return changed;
    }

    /// <summary>Drops expired cookies and ones past the retention period (after the period was shortened). True if any were dropped.</summary>
    public bool Prune(int retentionDays, DateTimeOffset now)
    {
        int removed = _cookies.RemoveAll(cookie => IsExpired(cookie, now) || !IsWithinRetention(cookie, retentionDays, now));
        return removed > 0;
    }

    public void Clear()
    {
        _cookies = new List<SavedCookie>();
        _firstSavedUtc.Clear();
    }

    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(new SavedCookieFile { Version = FileFormatVersion, Cookies = _cookies });
    }

    /// <exception cref="JsonException">The data is not a saved cookies file.</exception>
    public static List<SavedCookie?> Deserialize(byte[] json)
    {
        SavedCookieFile? file = JsonSerializer.Deserialize<SavedCookieFile>(json);
        return file?.Cookies?.Cast<SavedCookie?>().ToList() ?? new List<SavedCookie?>();
    }

    private sealed class SavedCookieFile
    {
        public int Version { get; set; }
        public List<SavedCookie>? Cookies { get; set; }
    }
}
