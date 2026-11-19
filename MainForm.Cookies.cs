using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// "Save cookies across sessions" (Settings -> General, off by default). Web View normally starts every session with
// no cookies and forgets them all when PhantomHaze closes. With the setting on, the cookies are also kept in
// %LOCALAPPDATA%\PhantomHaze\cookies.dat - encrypted with Windows DPAPI, so only the same Windows account on the same
// computer can read them - and put back into Web View at the next start. Cookies are dropped when the site's expiry
// date passes or when the retention period chosen in Settings is over. Turning the setting off deletes the file.
public partial class MainForm
{
    // Mixed into the DPAPI encryption, so other programs using DPAPI for the same account cannot decrypt the file by accident.
    private static readonly byte[] SavedCookiesEntropy = "PhantomHaze saved cookies v1"u8.ToArray();

    private readonly SavedCookieStore _savedCookies = new();
    private readonly string _savedCookiesFilePath = InitializeAppDataFilePath("cookies.dat");

    // The close is paused once for a final cookie snapshot: these track that pause.
    private bool _closeCookieCaptureInProgress;
    private bool _closeCookieCaptureDone;

    // True once the saved cookies are back in Web View (or there were none). Until then a snapshot of Web View's
    // cookies would be missing them, so it must not replace cookies.dat (that would delete every saved login).
    private bool _savedCookiesRestored = true;

    // Set when the window has cleaned up (CleanupWebSession): late snapshots no longer write cookies.dat.
    private bool _webSessionCleanedUp;

    /// <summary>Reads cookies.dat at startup. With the setting off, a file left from earlier is deleted.</summary>
    private void LoadSavedCookies()
    {
        _savedCookies.Clear();
        _savedCookiesRestored = true;
        if (!_settings.PersistCookiesAcrossSessions)
        {
            DeleteSavedCookiesFile();
            return;
        }

        try
        {
            if (!File.Exists(_savedCookiesFilePath))
            {
                return;
            }

            byte[] json = ProtectedData.Unprotect(File.ReadAllBytes(_savedCookiesFilePath), SavedCookiesEntropy, DataProtectionScope.CurrentUser);
            _savedCookies.Load(SavedCookieStore.Deserialize(json), _settings.CookieRetentionDays, DateTimeOffset.UtcNow);
            _savedCookiesRestored = _savedCookies.Cookies.Count == 0;
            Log(_savedCookies.Cookies.Count == 1 ? "1 saved cookie was loaded." : $"{_savedCookies.Cookies.Count} saved cookies were loaded.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException)
        {
            // Unreadable (damaged, or saved by another Windows account): it can never be used, so it is removed.
            Log($"The saved cookies could not be read and were deleted: {ex.Message}");
            _savedCookies.Clear();
            _savedCookiesRestored = true;
            DeleteSavedCookiesFile();
        }
    }

    /// <summary>
    /// Puts the saved cookies into Web View, for the first tab, before it loads anything. If that tab's WebView closes
    /// or fails meanwhile, the next tab tries again (see CreateTabAsync).
    /// </summary>
    private void RestoreSavedCookies(CoreWebView2 core)
    {
        if (!_settings.PersistCookiesAcrossSessions || _savedCookies.Cookies.Count == 0)
        {
            _savedCookiesRestored = true;
            return;
        }

        int restored = 0;
        foreach (SavedCookie saved in _savedCookies.Cookies)
        {
            try
            {
                CoreWebView2Cookie cookie = core.CookieManager.CreateCookie(saved.Name, saved.Value, saved.Domain, saved.Path);
                if (saved.ExpiresUtc is DateTimeOffset expires)
                {
                    cookie.Expires = expires.UtcDateTime;
                }

                cookie.IsHttpOnly = saved.IsHttpOnly;
                cookie.IsSecure = saved.IsSecure;
                if (Enum.IsDefined(typeof(CoreWebView2CookieSameSiteKind), saved.SameSite))
                {
                    cookie.SameSite = (CoreWebView2CookieSameSiteKind)saved.SameSite;
                }

                core.CookieManager.AddOrUpdateCookie(cookie);
                // Restored cookies are not "new" for the "N cookies were registered" log line.
                _knownCookieKeys.Add($"{saved.Name}\n{saved.Domain}\n{saved.Path}");
                restored++;
            }
            catch (Exception ex) when (ex is ArgumentException or COMException)
            {
                // A cookie WebView2 does not accept (for example an invalid domain) is skipped.
            }
            catch (InvalidOperationException ex)
            {
                // The WebView closed or its browser process went away (ObjectDisposedException is one of these): the
                // saved cookies stay saved and are put back by the next tab.
                Log($"The saved cookies could not be restored yet ({ex.Message}); the next tab tries again.");
                return;
            }
        }

        _savedCookiesRestored = true;
        Log(restored == 1 ? "1 saved cookie was restored." : $"{restored} saved cookies were restored.");
    }

    private static SavedCookie ToSavedCookie(CoreWebView2Cookie cookie)
    {
        // WebView2 reports the expiry in UTC (seconds since 1970 UTC); a local time is converted, never just relabelled.
        DateTimeOffset? expires = null;
        DateTime rawExpires = cookie.Expires;
        if (!cookie.IsSession && rawExpires != DateTime.MinValue)
        {
            DateTime utc = rawExpires.Kind switch
            {
                DateTimeKind.Local => rawExpires.ToUniversalTime(),
                DateTimeKind.Utc => rawExpires,
                _ => DateTime.SpecifyKind(rawExpires, DateTimeKind.Utc),
            };
            expires = utc == DateTime.MaxValue ? DateTimeOffset.MaxValue : new DateTimeOffset(utc);
        }

        return new SavedCookie
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            ExpiresUtc = expires,
            IsHttpOnly = cookie.IsHttpOnly,
            IsSecure = cookie.IsSecure,
            SameSite = (int)cookie.SameSite,
        };
    }

    /// <summary>Saves a snapshot of Web View's cookies (taken after page loads and Set-Cookie responses, and at close).</summary>
    private void UpdateSavedCookies(IReadOnlyList<CoreWebView2Cookie> liveCookies)
    {
        if (!_settings.PersistCookiesAcrossSessions || !_savedCookiesRestored || _webSessionCleanedUp)
        {
            return;
        }

        List<SavedCookie> snapshot = liveCookies.Select(ToSavedCookie).ToList();
        if (_savedCookies.Update(snapshot, _settings.CookieRetentionDays, DateTimeOffset.UtcNow))
        {
            WriteSavedCookiesFile();
        }
    }

    /// <summary>Reads every cookie from Web View and saves them (used at close and when the setting is turned on).</summary>
    private async Task CaptureCookiesForSavingAsync()
    {
        CoreWebView2? core = _tabs.Select(tab => tab.WebView.CoreWebView2).FirstOrDefault(candidate => candidate is not null);
        if (core is null || !_settings.PersistCookiesAcrossSessions)
        {
            return;
        }

        IReadOnlyList<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(null);
        UpdateSavedCookies(cookies);
    }

    private void WriteSavedCookiesFile()
    {
        try
        {
            byte[] encrypted = ProtectedData.Protect(_savedCookies.Serialize(), SavedCookiesEntropy, DataProtectionScope.CurrentUser);
            WriteBytesAtomically(_savedCookiesFilePath, encrypted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or NotSupportedException)
        {
            Log($"The saved cookies could not be written: {ex.Message}");
        }
    }

    private void DeleteSavedCookiesFile()
    {
        TryDeleteFile(_savedCookiesFilePath);
    }

    /// <summary>Like WriteFileAtomically, for binary contents.</summary>
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
    /// Settings were saved (or imported): turning the setting off deletes the saved cookies, turning it on saves the
    /// current session's cookies straight away, and a shorter retention period drops cookies that are now too old.
    /// </summary>
    private void ApplySavedCookieSettings(AppSettings previous)
    {
        if (!_settings.PersistCookiesAcrossSessions)
        {
            bool hadSavedCookies = _savedCookies.Cookies.Count > 0 || File.Exists(_savedCookiesFilePath);
            _savedCookies.Clear();
            _savedCookiesRestored = true;
            DeleteSavedCookiesFile();
            if (hadSavedCookies || previous.PersistCookiesAcrossSessions)
            {
                Log("Cookies are no longer saved across sessions; the cookies PhantomHaze had saved were deleted.");
            }

            return;
        }

        if (!previous.PersistCookiesAcrossSessions)
        {
            // Nothing was saved before, so there is nothing to put back: the current cookies are saved from now on.
            _savedCookiesRestored = true;
            Log($"Cookies are now saved across sessions (kept for {DescribeRetention(_settings.CookieRetentionDays)}).");
            _ = CaptureCookiesAfterSettingsChangeAsync();
            return;
        }

        if (previous.CookieRetentionDays != _settings.CookieRetentionDays)
        {
            Log($"Saved cookies are now kept for {DescribeRetention(_settings.CookieRetentionDays)}.");
            if (_savedCookies.Prune(_settings.CookieRetentionDays, DateTimeOffset.UtcNow))
            {
                WriteSavedCookiesFile();
            }
        }
    }

    /// <summary>
    /// Settings files exported before v1.3.6 have no Web View protocol or saved-cookie settings. Importing one keeps
    /// the current values instead of resetting them: resetting "Save cookies across sessions" would delete the saved
    /// cookies.
    /// </summary>
    private void KeepSettingsMissingFromImport(string settingsJson, AppSettings imported)
    {
        HashSet<string> present;
        try
        {
            using JsonDocument document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            present = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return;
        }

        if (!present.Contains(nameof(AppSettings.PersistCookiesAcrossSessions)))
        {
            imported.PersistCookiesAcrossSessions = _settings.PersistCookiesAcrossSessions;
        }

        if (!present.Contains(nameof(AppSettings.CookieRetentionDays)))
        {
            imported.CookieRetentionDays = _settings.CookieRetentionDays;
        }

        if (!present.Contains(nameof(AppSettings.WebViewProtocol)))
        {
            imported.WebViewProtocol = _settings.WebViewProtocol;
        }
    }

    private async Task CaptureCookiesAfterSettingsChangeAsync()
    {
        try
        {
            await CaptureCookiesForSavingAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // No tab could be asked; cookies are saved after the next page load instead.
        }
    }

    private static string DescribeRetention(int days) => days switch
    {
        < 0 => "as long as each site allows",
        1 => "1 day",
        _ => $"{days} days",
    };

    /// <summary>
    /// Closing the window: with saved cookies on, the close is paused once to take a last snapshot of the cookies
    /// (at most 3 seconds), then the window closes for real. Windows shutting down or Task Manager ending PhantomHaze
    /// are not delayed; the snapshot from the last page load is kept then.
    /// </summary>
    private async void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closeCookieCaptureInProgress && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing))
        {
            // Closing again while the snapshot runs: the window closes as soon as it is done.
            e.Cancel = true;
            return;
        }

        bool takeSnapshot = !_closeCookieCaptureDone &&
                            _settings.PersistCookiesAcrossSessions &&
                            _tabs.Any(tab => tab.WebView.CoreWebView2 is not null) &&
                            e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing);
        if (!takeSnapshot)
        {
            CleanupWebSession();
            return;
        }

        _closeCookieCaptureInProgress = true;
        e.Cancel = true;
        try
        {
            await CaptureCookiesForSavingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            // The window is closing either way; a failed snapshot must never stop it (the last saved one is kept).
            Log($"The last cookie snapshot before closing could not be taken: {ex.Message}");
        }
        finally
        {
            _closeCookieCaptureInProgress = false;
            _closeCookieCaptureDone = true;
        }

        // Windows shutting down (or Task Manager) can close the window while the snapshot ran; it is closed already then.
        if (!_webSessionCleanedUp && !IsDisposed && IsHandleCreated)
        {
            BeginInvoke(new Action(Close));
        }
    }
}
