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
    private readonly SemaphoreSlim _cookieSnapshotGate = new(1, 1);
    private int _cookieGeneration;

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
            return UniqueSavedCookies(file?.Cookies ?? []).Where(IsCookieStillSaved).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The file is replaced at the next save; there is nothing else in it worth keeping.
            Log($"The saved cookies could not be read and were not restored: {ex.Message}");
            return new List<SavedCookie>();
        }
    }

    // The file format cannot distinguish cookies with the same triple (for example from
    // different partitions). Never restore an arbitrary value under the shared identity.
    private static IEnumerable<SavedCookie> UniqueSavedCookies(IEnumerable<SavedCookie> cookies)
    {
        return cookies.Where(cookie => cookie is not null)
            .GroupBy(cookie => (cookie.Name, cookie.Domain, cookie.Path))
            .Where(group => group.Count() == 1)
            .Select(group => group.First());
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
    private async Task RestoreSavedCookiesAsync(CoreWebView2CookieManager manager)
    {
        if (!_settings.PersistCookiesAcrossSessions)
        {
            return;
        }

        int generation = _cookieGeneration;
        List<SavedCookie> saved = await Task.Run(LoadSavedCookies);
        if (generation != _cookieGeneration || IsDisposed) return;
        _savedCookieTimes = saved.ToDictionary(cookie => (cookie.Name, cookie.Domain, cookie.Path), cookie => cookie.SavedAtUtc);
        int restored = 0;
        for (int i = 0; i < saved.Count; i++)
        {
            if (i > 0 && i % 32 == 0) await Task.Delay(1);
            if (generation != _cookieGeneration || IsDisposed) return;
            SavedCookie savedCookie = saved[i];
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
    private async Task RefreshCookiesAsync(CoreWebView2 core, bool countRegistered)
    {
        int generation = _cookieGeneration;
        await _cookieSnapshotGate.WaitAsync();
        try
        {
            if (generation != _cookieGeneration || IsDisposed) return;
            var cookies = await core.CookieManager.GetCookiesAsync(null);
            bool persist = _settings.PersistCookiesAcrossSessions;
            if (persist && _savedCookieTimes is null)
            {
                var saved = await Task.Run(LoadSavedCookies);
                if (generation != _cookieGeneration || IsDisposed) return;
                _savedCookieTimes = saved.ToDictionary(c => (c.Name, c.Domain, c.Path), c => c.SavedAtUtc);
            }

            int registered = 0;
            var times = new Dictionary<(string Name, string Domain, string Path), DateTimeOffset>();
            var file = new SavedCookieFile();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < cookies.Count; i++)
            {
                // WebView2 cookies are apartment-bound. Give WinForms time for input
                // between batches while keeping COM property reads on the UI thread.
                if (i > 0 && i % 32 == 0) await Task.Delay(1);
                if (generation != _cookieGeneration || IsDisposed) return;
                try
                {
                    var live = cookies[i];
                    string path = live.Path;
                    var key = (live.Name, live.Domain, Path: string.IsNullOrEmpty(path) ? "/" : path);
                    if (countRegistered && _knownCookieKeys.Add($"{key.Name}\n{key.Domain}\n{key.Path}"))
                        registered++;
                    if (!persist) continue;

                    var cookie = new SavedCookie
                    {
                        Name = key.Name, Domain = key.Domain, Path = key.Path, Value = live.Value,
                        ExpiresUtc = live.IsSession ? null : ToUniversalTime(live.Expires),
                        IsSecure = live.IsSecure, IsHttpOnly = live.IsHttpOnly, SameSite = (int)live.SameSite,
                        SavedAtUtc = _savedCookieTimes!.TryGetValue(key, out var firstSaved) ? firstSaved : now,
                    };
                    times[key] = cookie.SavedAtUtc;
                    if (IsCookieStillSaved(cookie)) file.Cookies.Add(cookie);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    // WebView2 no longer has this cookie.
                }
            }

            if (registered > 0) Log(registered == 1 ? "1 cookie was registered" : $"{registered} cookies were registered");
            if (!persist || generation != _cookieGeneration || IsDisposed) return;
            string temp = BuildSidecarFilePath(_cookiesFilePath, ".tmp");
            try
            {
                await Task.Run(() =>
                {
                    file.Cookies = UniqueSavedCookies(file.Cookies).ToList();
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(file);
                    File.WriteAllBytes(temp, ProtectedData.Protect(json, SavedCookiesEntropy, DataProtectionScope.CurrentUser));
                });
                // Clear cookies or switching users during the write invalidates this snapshot.
                if (generation != _cookieGeneration || IsDisposed) return;
                File.Move(temp, _cookiesFilePath, overwrite: true);
                _savedCookieTimes = times;
            }
            finally { TryDeleteFile(temp); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log($"The cookies could not be saved: {ex.Message}");
        }
        finally { _cookieSnapshotGate.Release(); }
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
            await RefreshCookiesAsync(core, countRegistered: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The tab closed while its cookies were being read; the last save stands.
        }
    }

    /// <summary>True when <paramref name="tab"/> is the only open tab of the shared WebView2 session.</summary>
    private bool IsLastSharedWebView2Tab(BrowserTab tab)
    {
        static bool IsSharedWebView2(BrowserTab candidate) =>
            candidate.Browser.InnermostPage is WebView2ProtocolPage { UsesIsolatedEnvironment: false, Core: not null };

        return IsSharedWebView2(tab) && !_tabs.Any(other => !ReferenceEquals(other, tab) && IsSharedWebView2(other));
    }

    private void DeleteSavedCookiesFile()
    {
        _cookieGeneration++;
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

        // HTMLRenderer and HazeGate pages keep their own cookies and localStorage (in memory).
        RenderedPageSessionData.ClearAll();
        Log("All cookies were cleared, including the saved ones.");
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
