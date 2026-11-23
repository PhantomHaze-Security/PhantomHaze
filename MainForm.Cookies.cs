using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Settings -> General -> Save cookies across sessions. Off by default, like history: Web View cookies then only live in
// memory and are gone when PhantomHaze closes. When it is on, the cookies of the WebView2 protocol are saved in
// cookies.dat (encrypted with Windows DPAPI for the current user), restored in the next session, and deleted again a
// chosen number of days after they were first saved.
public partial class MainForm
{
    private const string SavedCookiesFileName = "cookies.dat";

    // Extra input for DPAPI, so the file cannot be decrypted by code that only knows it is DPAPI-protected.
    private static readonly byte[] SavedCookiesEntropy = Encoding.UTF8.GetBytes("PhantomHaze saved cookies v1");

    /// <summary>One saved cookie. Cookie names and values are never logged.</summary>
    private sealed class SavedCookie
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = "/";

        /// <summary>When the cookie expires; null for a session cookie (kept until the retention ends).</summary>
        public DateTimeOffset? ExpiresUtc { get; set; }

        public bool IsSecure { get; set; }
        public bool IsHttpOnly { get; set; }
        public int SameSite { get; set; }

        /// <summary>When PhantomHaze first saved this cookie; the retention period counts from here.</summary>
        public DateTimeOffset SavedAtUtc { get; set; }
    }

    private sealed class SavedCookieFile
    {
        public int Version { get; set; } = 1;
        public List<SavedCookie> Cookies { get; set; } = new();
    }

    // When each saved cookie (name, domain, path) was first saved, kept from one save to the next.
    private Dictionary<(string Name, string Domain, string Path), DateTimeOffset>? _savedCookieTimes;
    private bool _finalCookieSaveDone;

    private static OptionItem<int>[] BuildRetentionOptions() =>
    [
        new OptionItem<int>(1, "1 day"),
        new OptionItem<int>(7, "7 days"),
        new OptionItem<int>(14, "14 days"),
        new OptionItem<int>(30, "30 days"),
        new OptionItem<int>(80, "80 days"),
        new OptionItem<int>(-1, "Forever"),
    ];

    /// <summary>Reads the saved cookies, without those that expired or are past the retention period.</summary>
    private List<SavedCookie> LoadSavedCookies()
    {
        try
        {
            if (!File.Exists(_cookiesFilePath))
            {
                return new List<SavedCookie>();
            }

            byte[] protectedBytes = File.ReadAllBytes(_cookiesFilePath);
            byte[] json = ProtectedData.Unprotect(protectedBytes, SavedCookiesEntropy, DataProtectionScope.CurrentUser);
            SavedCookieFile? file = JsonSerializer.Deserialize<SavedCookieFile>(json);
            return (file?.Cookies ?? new List<SavedCookie>()).Where(IsCookieStillSaved).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The file is replaced at the next save; there is nothing else in it worth keeping.
            Log($"The saved cookies could not be read and were not restored: {ex.Message}");
            return new List<SavedCookie>();
        }
    }

    private bool IsCookieStillSaved(SavedCookie cookie)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Domain) ||
            (cookie.ExpiresUtc is DateTimeOffset expires && expires <= now))
        {
            return false;
        }

        int retentionDays = _settings.CookieRetentionDays;
        return retentionDays < 0 || cookie.SavedAtUtc >= now.AddDays(-retentionDays);
    }

    /// <summary>Puts the saved cookies into a fresh WebView2 session (called once per session, before its first page).</summary>
    private void RestoreSavedCookies(CoreWebView2CookieManager manager)
    {
        if (!_settings.PersistCookiesAcrossSessions)
        {
            return;
        }

        List<SavedCookie> saved = LoadSavedCookies();
        _savedCookieTimes = saved.ToDictionary(cookie => (cookie.Name, cookie.Domain, cookie.Path), cookie => cookie.SavedAtUtc);
        int restored = 0;
        foreach (SavedCookie savedCookie in saved)
        {
            try
            {
                CoreWebView2Cookie cookie = manager.CreateCookie(savedCookie.Name, savedCookie.Value, savedCookie.Domain, savedCookie.Path);
                cookie.IsSecure = savedCookie.IsSecure;
                cookie.IsHttpOnly = savedCookie.IsHttpOnly;
                if (Enum.IsDefined(typeof(CoreWebView2CookieSameSiteKind), savedCookie.SameSite))
                {
                    cookie.SameSite = (CoreWebView2CookieSameSiteKind)savedCookie.SameSite;
                }

                // A cookie without an expiry date stays a session cookie.
                if (savedCookie.ExpiresUtc is DateTimeOffset expires)
                {
                    cookie.Expires = expires.UtcDateTime;
                }

                manager.AddOrUpdateCookie(cookie);
                _knownCookieKeys.Add($"{savedCookie.Name}\n{savedCookie.Domain}\n{savedCookie.Path}");
                restored++;
            }
            catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException)
            {
                // A cookie WebView2 no longer accepts (for example after a change in its cookie rules) is skipped.
            }
        }

        if (restored > 0)
        {
            Log(restored == 1 ? "1 saved cookie was restored." : $"{restored} saved cookies were restored.");
        }
    }

    /// <summary>
    /// Saves the shared WebView2 session's cookies (when saving is on). Called a moment after page loads and Set-Cookie
    /// responses (with the cookies already read for the cookie notice), before WebView2 tabs close, and when
    /// PhantomHaze closes.
    /// </summary>
    private void WriteSavedCookies(IReadOnlyList<CoreWebView2Cookie> liveCookies)
    {
        if (!_settings.PersistCookiesAcrossSessions)
        {
            return;
        }

        _savedCookieTimes ??= LoadSavedCookies().ToDictionary(cookie => (cookie.Name, cookie.Domain, cookie.Path), cookie => cookie.SavedAtUtc);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var file = new SavedCookieFile();
        var times = new Dictionary<(string Name, string Domain, string Path), DateTimeOffset>();
        foreach (CoreWebView2Cookie live in liveCookies)
        {
            SavedCookie cookie;
            try
            {
                var key = (live.Name, live.Domain, live.Path);
                cookie = new SavedCookie
                {
                    Name = live.Name,
                    Value = live.Value,
                    Domain = live.Domain,
                    Path = string.IsNullOrEmpty(live.Path) ? "/" : live.Path,
                    ExpiresUtc = live.IsSession ? null : ToUniversalTime(live.Expires),
                    IsSecure = live.IsSecure,
                    IsHttpOnly = live.IsHttpOnly,
                    SameSite = (int)live.SameSite,
                    SavedAtUtc = _savedCookieTimes.TryGetValue(key, out DateTimeOffset savedAt) ? savedAt : now,
                };
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                continue;
            }

            if (!IsCookieStillSaved(cookie))
            {
                // Past its retention period: it is used for the rest of this session but no longer saved.
                continue;
            }

            file.Cookies.Add(cookie);
            times[(cookie.Name, cookie.Domain, cookie.Path)] = cookie.SavedAtUtc;
        }

        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(file);
            byte[] protectedBytes = ProtectedData.Protect(json, SavedCookiesEntropy, DataProtectionScope.CurrentUser);
            WriteBytesAtomically(_cookiesFilePath, protectedBytes);
            _savedCookieTimes = times;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log($"The cookies could not be saved: {ex.Message}");
        }
    }

    private static DateTimeOffset ToUniversalTime(DateTime value)
    {
        return value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : new DateTimeOffset(value).ToUniversalTime();
    }

    /// <summary>Reads the shared WebView2 session's cookies and saves them (nothing happens when saving is off).</summary>
    private async Task SaveCookiesNowAsync()
    {
        if (!_settings.PersistCookiesAcrossSessions || FindSharedWebView2Core() is not CoreWebView2 core)
        {
            return;
        }

        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(null);
            WriteSavedCookies(cookies);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The tab closed while its cookies were being read; the last save stands.
        }
    }

    private void DeleteSavedCookiesFile()
    {
        _savedCookieTimes = null;
        TryDeleteFile(_cookiesFilePath);
    }

    /// <summary>Settings -> Clear cookies: deletes the saved cookies and the cookies of this session.</summary>
    private void ClearCookies()
    {
        DeleteSavedCookiesFile();
        if (FindSharedWebView2Core() is CoreWebView2 core)
        {
            try
            {
                core.CookieManager.DeleteAllCookies();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The tab is closing.
            }
        }

        _knownCookieKeys.Clear();
        Log("All cookies were cleared, including the saved ones.");
    }

    private static void WriteBytesAtomically(string filePath, byte[] contents)
    {
        string tempPath = BuildSidecarFilePath(filePath, ".tmp");
        try
        {
            File.WriteAllBytes(tempPath, contents);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// Closing saves the session's cookies one last time first (with cookie saving on), because reading them from
    /// WebView2 takes a moment: the close is held back until that is done (at most a few seconds), then the window
    /// closes as usual.
    /// </summary>
    private async void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_finalCookieSaveDone &&
            _settings.PersistCookiesAcrossSessions &&
            e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) &&
            FindSharedWebView2Core() is not null)
        {
            _finalCookieSaveDone = true;
            e.Cancel = true;
            try
            {
                await SaveCookiesNowAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                Log("The cookies could not be saved in time; the last save is kept.");
            }

            if (!IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(Close));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // Already closing.
                }
            }

            return;
        }

        if (!e.Cancel)
        {
            CleanupWebSession();
        }
    }
}
