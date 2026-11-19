using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhantomHaze;

// Capture shield: everything PhantomHaze shows must stay hidden from screen capture, not just the main window.
//
// 1. PhantomHaze's own pop-up windows (tooltips, drop-down lists, menus, dialogs) are separate top-level windows. A
//    WinEvent hook sees every window this process creates or shows and excludes it from capture straight away, and
//    taskbar thumbnails, Alt+Tab and Peek show the app icon instead of a live preview.
// 2. Web pages run in Microsoft Edge's own processes, and the windows those processes open (the right-click menu,
//    alert/confirm/prompt boxes, permission and sign-in prompts, the download pop-up, Save As, print preview, file and
//    colour pickers, tooltips, the link status bubble, picture-in-picture) cannot be excluded from capture by
//    PhantomHaze: Windows only allows that for a process's own windows. Each of them is therefore replaced: by a
//    protected PhantomHaze window (menus, dialogs, pickers), by something drawn inside the page (drop-down lists,
//    tooltips) or turned off (picture-in-picture, sharing, screen sharing, Windows notifications).
public partial class MainForm
{
    /// <summary>A WebView (tab, extension page or extension window) whose browser pop-ups are replaced by protected ones.</summary>
    private sealed class ShieldSurface
    {
        public ShieldSurface(WebView2 view, bool isTab)
        {
            View = view;
            IsTab = isTab;
        }

        public WebView2 View { get; }
        public bool IsTab { get; }
    }

    private readonly Dictionary<CoreWebView2, ShieldSurface> _shieldSurfaces = new();
    private readonly HashSet<IntPtr> _watchedProcessWindows = new();
    // Answers to permission prompts, per site and permission, for the rest of the session.
    private readonly Dictionary<string, bool> _sitePermissionAnswers = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _processWindowHook;
    private NativeMethods.WinEventProc? _processWindowCallback;
    private IntPtr _windowCreationHook;
    private object? _windowCreationCallback;
    private bool _shieldDialogOpen;
    private bool _screenSharingNoticeShown;
    // Sites whose picker or print requests keep being cancelled are refused for a minute, across page loads (a page
    // could otherwise reopen a modal dialog the moment the previous one closes, or reload itself to start over).
    private static readonly TimeSpan ShieldRefusalLength = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, (int Count, DateTime FirstUtc)> _cancelledShieldRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _shieldRequestsRefusedUntil = new(StringComparer.OrdinalIgnoreCase);
    // Permission prompts waiting for an answer, per site origin and permission: a second request waits for the same answer.
    private readonly Dictionary<string, List<Action<bool>>> _pendingPermissionAnswers = new(StringComparer.OrdinalIgnoreCase);

    // Link addresses while hovering (the browser's status bubble is a window of its own) and short notices.
    private readonly Label _browserStatusLabel = new();
    private readonly System.Windows.Forms.Timer _browserNoticeTimer = new() { Interval = 4000 };
    private string? _browserNotice;
    private string? _hoveredLinkText;

    // Element fullscreen (videos, web games): the tab fills the screen and the top bar and tabs are hidden.
    private BrowserTab? _fullScreenTab;
    private FormBorderStyle _borderStyleBeforeFullScreen;
    private FormWindowState _windowStateBeforeFullScreen;
    private bool _updateBannerVisibleBeforeFullScreen;

    // ---- 1. PhantomHaze's own windows -----------------------------------------------------------------------------

    /// <summary>Starts watching this process's windows. Runs before protection is first applied.</summary>
    private void StartCaptureShield()
    {
        _processWindowCallback = OnProcessWindowEvent;
        _processWindowHook = NativeMethods.WatchProcessWindows(_processWindowCallback);
        _windowCreationHook = NativeMethods.WatchThreadWindowCreation(ProtectNewWindow, out object creationCallback);
        _windowCreationCallback = creationCallback;
        if (_processWindowHook == IntPtr.Zero)
        {
            Log("The capture shield could not watch PhantomHaze's pop-up windows; known pop-ups are still protected one by one.");
        }

        // If Windows ever recreates the main window (its handle), the new window gets the current protection too.
        HandleCreated += (_, _) =>
        {
            _ = NativeMethods.ExcludeFromCapture(Handle, _protectionEnabled);
            NativeMethods.HideFromShellPreviews(Handle, _protectionEnabled);
        };

        // Tooltip windows are protected again just before each one is shown.
        foreach (ToolTip toolTip in new[] { _chromeToolTip, _extensionUiToolTip, _updateBannerToolTip, _extensionLauncherToolTip })
        {
            toolTip.Popup += (_, _) => ProtectThreadWindows();
        }

        _browserNoticeTimer.Tick += (_, _) =>
        {
            _browserNoticeTimer.Stop();
            _browserNotice = null;
            UpdateBrowserStatusLabel();
        };
    }

    private void StopCaptureShield()
    {
        NativeMethods.StopWatchingProcessWindows(_processWindowHook);
        _processWindowHook = IntPtr.Zero;
        NativeMethods.StopWatchingThreadWindowCreation(_windowCreationHook);
        _windowCreationHook = IntPtr.Zero;
        GC.KeepAlive(_windowCreationCallback);
        _browserNoticeTimer.Stop();
    }

    private void OnProcessWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero || IsDisposed)
        {
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_DESTROY)
        {
            _watchedProcessWindows.Remove(hwnd);
            return;
        }

        if (NativeMethods.IsTopLevelWindow(hwnd))
        {
            ProtectProcessWindow(hwnd);
        }
    }

    /// <summary>Called by the CBT hook while a top-level window is being created (before it can be shown).</summary>
    private void ProtectNewWindow(IntPtr hwnd)
    {
        if (!IsDisposed)
        {
            ProtectProcessWindow(hwnd);
        }
    }

    private void ProtectProcessWindow(IntPtr hwnd)
    {
        if (IsHandleCreated && hwnd == Handle)
        {
            return;
        }

        _ = NativeMethods.ExcludeFromCapture(hwnd, _protectionEnabled);
        NativeMethods.HideFromShellPreviews(hwnd, _protectionEnabled);
        _watchedProcessWindows.Add(hwnd);
    }

    /// <summary>Protects every top-level window of the UI thread right now (synchronously).</summary>
    private void ProtectThreadWindows()
    {
        foreach (IntPtr hwnd in NativeMethods.GetCurrentThreadTopLevelWindows())
        {
            ProtectProcessWindow(hwnd);
        }
    }

    /// <summary>After a protection change: every watched window follows the main window.</summary>
    private void SyncProcessWindowsProtection()
    {
        if (IsHandleCreated)
        {
            NativeMethods.HideFromShellPreviews(Handle, _protectionEnabled);
        }

        foreach (IntPtr hwnd in _watchedProcessWindows.ToArray())
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                _watchedProcessWindows.Remove(hwnd);
                continue;
            }

            ProtectProcessWindow(hwnd);
        }

        ProtectThreadWindows();
    }

    // ---- 2. Web pages ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces the browser's own pop-ups of a WebView with protected ones. Runs before the WebView shows anything.
    /// </summary>
    private async Task ConfigureCaptureShieldAsync(WebView2 view, CoreWebView2 core, bool isTab)
    {
        _shieldSurfaces[core] = new ShieldSurface(view, isTab);
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        if (!isTab)
        {
            // Extension pages and windows need no browser shortcuts (print, save, find), which open browser windows.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        }

        core.ContextMenuRequested += OnShieldContextMenuRequested;
        core.ScriptDialogOpening += OnShieldScriptDialogOpening;
        core.PermissionRequested += OnShieldPermissionRequested;
        core.BasicAuthenticationRequested += OnShieldBasicAuthenticationRequested;
        core.ClientCertificateRequested += OnShieldClientCertificateRequested;
        core.DownloadStarting += OnShieldDownloadStarting;
        core.IsDefaultDownloadDialogOpenChanged += OnShieldDownloadDialogOpenChanged;
        core.SaveAsUIShowing += OnShieldSaveAsUiShowing;
        core.NotificationReceived += OnShieldNotificationReceived;
        core.LaunchingExternalUriScheme += OnShieldLaunchingExternalUriScheme;
        core.ScreenCaptureStarting += OnShieldScreenCaptureStarting;
        core.WebMessageReceived += OnShieldWebMessageReceived;
        if (isTab)
        {
            core.StatusBarTextChanged += OnShieldStatusBarTextChanged;
            core.ContainsFullScreenElementChanged += OnShieldFullScreenChanged;
        }

        view.KeyDown += OnShieldWebViewKeyDown;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(CaptureShieldPageScript);
    }

    private void ForgetShieldSurface(CoreWebView2? core)
    {
        if (core is not null)
        {
            _shieldSurfaces.Remove(core);
        }
    }

    private bool TryGetShieldSurface(object? sender, out CoreWebView2 core, out ShieldSurface surface)
    {
        core = (sender as CoreWebView2)!;
        surface = null!;
        return core is not null && _shieldSurfaces.TryGetValue(core, out surface!) && !surface.View.IsDisposed;
    }

    private IWin32Window GetShieldOwner(ShieldSurface surface)
    {
        return (IWin32Window?)surface.View.FindForm() ?? this;
    }

    private static string DescribeSite(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : "This page";
    }

    /// <summary>Runs after the WebView2 event handler has returned (deferral pattern), so a modal dialog never re-enters it.</summary>
    private void RunAfterEvent(CoreWebView2Deferral deferral, Action action)
    {
        void Run()
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException or ArgumentException)
            {
                // The page or tab went away while the dialog was open.
            }
            finally
            {
                try
                {
                    deferral.Complete();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
                {
                    // Already gone.
                }
            }
        }

        if (IsDisposed || !IsHandleCreated)
        {
            Run();
            return;
        }

        BeginInvoke(new Action(Run));
    }

    // -- Right-click menu --

    // Menu commands that open a browser window or bubble (QR code, web capture, sharing, picture-in-picture) or a file
    // picker of the browser's own (save link/image/media as) are left out; Inspect is unavailable anyway.
    private static readonly HashSet<string> HiddenContextMenuCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "inspectElement", "createQrCode", "webCapture", "share", "saveLinkAs", "saveImageAs", "saveMediaAs",
        "pictureInPicture", "openLinkInNewWindow",
    };

    private void OnShieldContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out CoreWebView2 core, out ShieldSurface surface))
        {
            return;
        }

        // The browser's own menu is a window of its own that capture would see: PhantomHaze shows the same commands.
        e.Handled = true;
        IList<CoreWebView2ContextMenuItem> items = e.MenuItems;
        CoreWebView2ContextMenuTarget target = e.ContextMenuTarget;
        CoreWebView2Deferral deferral = e.GetDeferral();
        int selectedCommand = -1;
        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        ProtectPopupMenuFromCapture(menu);
        menu.Font = MapFont(_defaultMenuFont ?? menu.Font);

        void AddItems(ToolStripItemCollection collection, IEnumerable<CoreWebView2ContextMenuItem> source)
        {
            foreach (CoreWebView2ContextMenuItem item in source)
            {
                if (item.Kind == CoreWebView2ContextMenuItemKind.Separator)
                {
                    if (collection.Count > 0 && collection[^1] is not ToolStripSeparator)
                    {
                        collection.Add(new ToolStripSeparator());
                    }

                    continue;
                }

                if (HiddenContextMenuCommands.Contains(item.Name))
                {
                    continue;
                }

                var menuItem = new ToolStripMenuItem(item.Label)
                {
                    Enabled = item.IsEnabled,
                    Checked = item.IsChecked,
                    ShortcutKeyDisplayString = item.ShortcutKeyDescription,
                };

                if (string.Equals(item.Name, "print", StringComparison.OrdinalIgnoreCase))
                {
                    // Printing goes through PhantomHaze (the browser's print preview is a window of its own).
                    menuItem.Click += (_, _) => BeginInvoke(new Action(() => _ = PrintPageAsync(core, surface)));
                }
                else if (item.Children.Count > 0)
                {
                    ProtectPopupMenuFromCapture(menuItem.DropDown);
                    AddItems(menuItem.DropDownItems, item.Children);
                }
                else
                {
                    int commandId = item.CommandId;
                    menuItem.Click += (_, _) => selectedCommand = commandId;
                }

                collection.Add(menuItem);
            }

            while (collection.Count > 0 && collection[^1] is ToolStripSeparator)
            {
                collection.RemoveAt(collection.Count - 1);
            }
        }

        AddItems(menu.Items, items);
        if (target.HasLinkUri && Uri.TryCreate(target.LinkUri, UriKind.Absolute, out Uri? link) &&
            (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps))
        {
            // "Open link in new window" is replaced by a PhantomHaze tab.
            var openInTab = new ToolStripMenuItem("Open link in new tab");
            openInTab.Click += (_, _) => BeginInvoke(new Action(() => _ = OpenUrlInNewTabAsync(link.AbsoluteUri, "Link")));
            menu.Items.Insert(0, openInTab);
            if (menu.Items.Count > 1 && menu.Items[1] is not ToolStripSeparator)
            {
                menu.Items.Insert(1, new ToolStripSeparator());
            }
        }

        bool completed = false;
        menu.Closed += (_, _) =>
        {
            // After any item's Click handler has run.
            BeginInvoke(new Action(() =>
            {
                if (!completed)
                {
                    completed = true;
                    try
                    {
                        if (selectedCommand >= 0)
                        {
                            e.SelectedCommandId = selectedCommand;
                        }

                        deferral.Complete();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
                    {
                        // The page went away while the menu was open.
                    }
                }

                menu.Dispose();
            }));
        };

        if (menu.Items.Count == 0)
        {
            menu.Dispose();
            deferral.Complete();
            return;
        }

        WebView2 view = surface.View;
        Point cursor = Cursor.Position;
        Point screenPoint = view.RectangleToScreen(view.ClientRectangle).Contains(cursor) ? cursor : view.PointToScreen(e.Location);
        menu.Show(screenPoint);
    }

    // -- alert(), confirm(), prompt() and "Leave site?" --

    private void OnShieldScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        RunAfterEvent(deferral, () =>
        {
            IWin32Window owner = GetShieldOwner(surface);
            string site = DescribeSite(e.Uri);
            string message = e.Message ?? string.Empty;
            switch (e.Kind)
            {
                case CoreWebView2ScriptDialogKind.Alert:
                    ShowCaptureProtectedDialog(() => MessageBox.Show(owner, message, site, MessageBoxButtons.OK, MessageBoxIcon.None));
                    e.Accept();
                    break;
                case CoreWebView2ScriptDialogKind.Confirm:
                    if (ShowCaptureProtectedDialog(() => MessageBox.Show(owner, message, site, MessageBoxButtons.OKCancel, MessageBoxIcon.Question)) == DialogResult.OK)
                    {
                        e.Accept();
                    }

                    break;
                case CoreWebView2ScriptDialogKind.Prompt:
                    if (ShowTextInputDialog(owner, site, message, e.DefaultText ?? string.Empty) is string answer)
                    {
                        e.ResultText = answer;
                        e.Accept();
                    }

                    break;
                default:
                    if (ShowCaptureProtectedDialog(() => MessageBox.Show(
                            owner,
                            "Leave this page?\n\nChanges you made may not be saved.",
                            site,
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2)) == DialogResult.Yes)
                    {
                        e.Accept();
                    }

                    break;
            }
        });
    }

    // -- Permissions (camera, microphone, location, ...) --

    private void OnShieldPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            return;
        }

        // The browser's permission prompt is a window of its own: PhantomHaze always answers.
        e.Handled = true;
        e.SavesInProfile = false;
        string site = DescribeSite(e.Uri);
        CoreWebView2PermissionKind kind = e.PermissionKind;
        switch (kind)
        {
            case CoreWebView2PermissionKind.Notifications:
                // Windows shows notifications outside PhantomHaze, where screen capture sees them.
                e.State = CoreWebView2PermissionState.Deny;
                Log($"Notifications from {site} were blocked: Windows would show them outside PhantomHaze.");
                return;
            case CoreWebView2PermissionKind.Autoplay:
                e.State = CoreWebView2PermissionState.Allow;
                return;
        }

        // Answers are kept per origin (scheme, host and port), like a browser.
        string origin = Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? requestUri) ? requestUri.GetLeftPart(UriPartial.Authority) : site;
        string key = $"{origin}|{kind}";
        if (_sitePermissionAnswers.TryGetValue(key, out bool remembered))
        {
            e.State = remembered ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        void Answer(bool allow)
        {
            try
            {
                e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The page went away.
            }
            finally
            {
                try
                {
                    deferral.Complete();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
                {
                    // Already gone.
                }
            }
        }

        if (_pendingPermissionAnswers.TryGetValue(key, out List<Action<bool>>? waiting))
        {
            // The same question is already on screen: this request gets the same answer.
            waiting.Add(Answer);
            return;
        }

        _pendingPermissionAnswers[key] = new List<Action<bool>> { Answer };
        BeginInvoke(new Action(() =>
        {
            bool allow = false;
            try
            {
                allow = ShowCaptureProtectedDialog(() => MessageBox.Show(
                    GetShieldOwner(surface),
                    $"{origin} wants to {DescribePermission(kind)}.\n\nAllow it for the rest of this session?",
                    "PhantomHaze - Permission",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2)) == DialogResult.Yes;
                _sitePermissionAnswers[key] = allow;
                Log($"{origin}: permission to {DescribePermission(kind)} {(allow ? "allowed" : "denied")} for this session.");
            }
            finally
            {
                if (_pendingPermissionAnswers.Remove(key, out List<Action<bool>>? answers))
                {
                    answers.ForEach(answer => answer(allow));
                }
            }
        }));
    }

    private static string DescribePermission(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.OtherSensors => "use your device's sensors",
        CoreWebView2PermissionKind.ClipboardRead => "read your clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "download several files",
        CoreWebView2PermissionKind.FileReadWrite => "read and change files you choose",
        CoreWebView2PermissionKind.LocalFonts => "see the fonts installed on this computer",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "control your MIDI devices",
        CoreWebView2PermissionKind.WindowManagement => "manage windows on your screens",
        _ => "use a feature of your computer",
    };

    // -- HTTP sign-in and client certificates --

    private void OnShieldBasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        RunAfterEvent(deferral, () =>
        {
            string site = DescribeSite(e.Uri);
            if (ShowCredentialsDialog(GetShieldOwner(surface), site, $"{site} asks you to sign in.") is (string user, string password))
            {
                e.Response.UserName = user;
                e.Response.Password = password;
            }
            else
            {
                e.Cancel = true;
            }
        });
    }

    private void OnShieldClientCertificateRequested(object? sender, CoreWebView2ClientCertificateRequestedEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            return;
        }

        // The browser's certificate picker is a window of its own.
        e.Handled = true;
        IReadOnlyList<CoreWebView2ClientCertificate> certificates = e.MutuallyTrustedCertificates;
        if (certificates.Count == 0)
        {
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        RunAfterEvent(deferral, () =>
        {
            List<string> options = certificates.Select(certificate => $"{certificate.DisplayName} (issued by {certificate.Issuer})").ToList();
            options.Add("Don't use a certificate");
            int choice = ShowChoiceDialog(GetShieldOwner(surface), "PhantomHaze - Certificate", $"{e.Host} asks for a certificate to identify you.", options);
            if (choice >= 0 && choice < certificates.Count)
            {
                e.SelectedCertificate = certificates[choice];
            }
        });
    }

    // -- Downloads and Save As --

    private void OnShieldDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out _))
        {
            return;
        }

        // The browser's download pop-up is a window of its own: downloads go to the Downloads folder, and progress is
        // shown in PhantomHaze.
        e.Handled = true;
        CoreWebView2DownloadOperation download = e.DownloadOperation;
        string name = Path.GetFileName(e.ResultFilePath);
        Log($"Download started: {NormalizeForLog(name)} -> {NormalizeForLog(e.ResultFilePath)}");
        ShowBrowserNotice($"Downloading {name}...");
        download.StateChanged += (_, _) =>
        {
            try
            {
                switch (download.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        Log($"Download finished: {NormalizeForLog(download.ResultFilePath)}");
                        ShowBrowserNotice($"Downloaded {Path.GetFileName(download.ResultFilePath)} to {Path.GetDirectoryName(download.ResultFilePath)}");
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        Log($"Download stopped ({download.InterruptReason}): {NormalizeForLog(name)}");
                        ShowBrowserNotice($"Download stopped: {name}");
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The tab closed.
            }
        };
    }

    private void OnShieldDownloadDialogOpenChanged(object? sender, object e)
    {
        try
        {
            if (sender is CoreWebView2 core && core.IsDefaultDownloadDialogOpen)
            {
                core.CloseDefaultDownloadDialog();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The tab closed.
        }
    }

    private void OnShieldSaveAsUiShowing(object? sender, CoreWebView2SaveAsUIShowingEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        RunAfterEvent(deferral, () =>
        {
            string suggested = e.SaveAsFilePath ?? string.Empty;
            string extension = Path.GetExtension(suggested);
            using var dialog = new SaveFileDialog
            {
                Title = "Save page as",
                FileName = Path.GetFileName(suggested),
                InitialDirectory = Path.GetDirectoryName(suggested) ?? string.Empty,
                Filter = string.IsNullOrEmpty(extension) ? "All files (*.*)|*.*" : $"{extension.TrimStart('.').ToUpperInvariant()} file (*{extension})|*{extension}|All files (*.*)|*.*",
                OverwritePrompt = true,
            };

            if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(GetShieldOwner(surface))) == DialogResult.OK)
            {
                e.SaveAsFilePath = dialog.FileName;
                e.AllowReplace = true;
                e.SuppressDefaultDialog = true;
            }
            else
            {
                e.Cancel = true;
            }
        });
    }

    // -- Notifications, app links and screen sharing --

    private void OnShieldNotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        // Notifications are denied (PermissionRequested); one that arrives anyway is shown inside PhantomHaze.
        e.Handled = true;
        try
        {
            string title = e.Notification.Title ?? string.Empty;
            ShowBrowserNotice($"{DescribeSite(e.SenderOrigin)}: {title}");
            e.Notification.ReportShown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The page went away.
        }
    }

    private void OnShieldLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            return;
        }

        // The browser's "Open this app?" prompt is a window of its own: PhantomHaze asks, then opens the link itself.
        e.Cancel = true;
        string uri = e.Uri;
        string origin = DescribeSite(e.InitiatingOrigin);
        CoreWebView2Deferral deferral = e.GetDeferral();
        RunAfterEvent(deferral, () =>
        {
            string scheme = uri.Split(':', 2)[0];
            if (ShowCaptureProtectedDialog(() => MessageBox.Show(
                    GetShieldOwner(surface),
                    $"{origin} wants to open a \"{scheme}:\" link in another app:\n\n{TrimForLog(uri, 300)}\n\nThat app is not protected from screen capture. Open it?",
                    "PhantomHaze - Open another app",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2)) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                Log($"Opened a {scheme}: link in another app.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log($"The {scheme}: link could not be opened: {ex.Message}");
            }
        });
    }

    private void OnShieldScreenCaptureStarting(object? sender, CoreWebView2ScreenCaptureStartingEventArgs e)
    {
        // Sharing your screen from a web page shows the browser's screen picker (a window of its own) and would send
        // what is on screen to the site.
        e.Cancel = true;
        e.Handled = true;
        Log("A web page tried to share your screen; screen sharing is turned off in PhantomHaze.");
        ShowBrowserNotice("Screen sharing from web pages is turned off in PhantomHaze.");
        if (!_screenSharingNoticeShown && TryGetShieldSurface(sender, out _, out ShieldSurface surface))
        {
            _screenSharingNoticeShown = true;
            BeginInvoke(new Action(() => ShowCaptureProtectedDialog(() => MessageBox.Show(
                GetShieldOwner(surface),
                "This page tried to share your screen. Screen sharing from web pages is turned off in PhantomHaze, " +
                "because the screen picker would be visible to capture software.",
                "PhantomHaze",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information))));
        }
    }

    // -- Keyboard shortcuts that open browser windows --

    private void OnShieldWebViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not WebView2 { CoreWebView2: CoreWebView2 core } || !_shieldSurfaces.TryGetValue(core, out ShieldSurface? surface))
        {
            return;
        }

        switch (e.KeyData)
        {
            case Keys.Control | Keys.P:
            case Keys.Control | Keys.Shift | Keys.P:
                e.Handled = true;
                BeginInvoke(new Action(() => _ = PrintPageAsync(core, surface)));
                break;
            case Keys.Control | Keys.F:
            case Keys.F3:
            case Keys.Control | Keys.G:
                // The browser's find bar is a window of its own; PhantomHaze has its own find panel.
                e.Handled = true;
                if (surface.IsTab)
                {
                    BeginInvoke(new Action(ShowFindPanel));
                }

                break;
            case Keys.Control | Keys.Shift | Keys.S:
                // Web capture (an overlay window of its own).
                e.Handled = true;
                break;
        }
    }

    // -- Printing --

    /// <param name="requestedByPage">
    /// True when the page asked (window.print()): cancelled requests then count towards refusing the site for a while,
    /// so a page cannot keep the print dialog open. Ctrl+P and the menu are the user's own and always work.
    /// </param>
    private async Task PrintPageAsync(CoreWebView2 core, ShieldSurface surface, bool requestedByPage = false)
    {
        string site = GetShieldSite(core);
        if (_shieldDialogOpen || surface.View.IsDisposed || (requestedByPage && IsShieldRequestRefused(site)))
        {
            return;
        }

        IWin32Window owner = GetShieldOwner(surface);
        int choice;
        _shieldDialogOpen = true;
        try
        {
            choice = ShowChoiceDialog(owner, "PhantomHaze - Print", "How do you want to print this page?", ["Save as PDF...", "Print on the default printer"]);
        }
        finally
        {
            _shieldDialogOpen = false;
        }

        if (requestedByPage)
        {
            NoteShieldRequestOutcome(site, accepted: choice >= 0);
        }

        try
        {
            if (choice == 0)
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Save page as PDF",
                    Filter = "PDF document (*.pdf)|*.pdf",
                    FileName = MakeSafeFileName(core.DocumentTitle, "page") + ".pdf",
                    OverwritePrompt = true,
                };
                if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
                {
                    return;
                }

                bool saved = await core.PrintToPdfAsync(dialog.FileName);
                Log(saved ? $"Page saved as PDF: {dialog.FileName}" : "The page could not be saved as PDF.");
                ShowBrowserNotice(saved ? $"Saved {Path.GetFileName(dialog.FileName)}" : "The page could not be saved as PDF.");
            }
            else if (choice == 1)
            {
                CoreWebView2PrintStatus status = await core.PrintAsync(core.Environment.CreatePrintSettings());
                Log($"Print: {status}.");
                ShowBrowserNotice(status == CoreWebView2PrintStatus.Succeeded ? "Sent to the printer." : $"Printing failed ({status}).");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException or ArgumentException or IOException)
        {
            Log($"Printing failed: {ex.Message}");
        }
    }

    private static string MakeSafeFileName(string? text, string fallback)
    {
        string name = new((text ?? string.Empty).Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray());
        name = name.Trim();
        return name.Length == 0 ? fallback : (name.Length > 80 ? name[..80] : name);
    }

    // -- Requests from the page script (file and colour pickers, printing) --

    private void OnShieldWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!TryGetShieldSurface(sender, out CoreWebView2 core, out ShieldSurface surface))
        {
            return;
        }

        string id;
        string type;
        JsonElement message;
        try
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            message = document.RootElement.Clone();
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("phantomHaze", out JsonElement marker) || marker.GetString() != "shield" ||
                !message.TryGetProperty("id", out JsonElement idElement) || idElement.GetString() is not string idText || idText.Length is 0 or > 100 ||
                !message.TryGetProperty("type", out JsonElement typeElement) || typeElement.GetString() is not string typeText)
            {
                return;
            }

            id = idText;
            type = typeText;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return;
        }

        BeginInvoke(new Action(() => HandleShieldRequest(core, surface, id, type, message)));
    }

    private void HandleShieldRequest(CoreWebView2 core, ShieldSurface surface, string id, string type, JsonElement message)
    {
        if (surface.View.IsDisposed)
        {
            return;
        }

        if (type == "print")
        {
            ReplyToPage(core, id, ok: true, value: null, objects: null);
            _ = PrintPageAsync(core, surface, requestedByPage: true);
            return;
        }

        string site = GetShieldSite(core);
        if (_shieldDialogOpen || IsShieldRequestRefused(site))
        {
            ReplyToPage(core, id, ok: false, value: null, objects: null);
            return;
        }

        _shieldDialogOpen = true;
        bool accepted = false;
        try
        {
            IWin32Window owner = GetShieldOwner(surface);
            switch (type)
            {
                case "file":
                {
                    List<object>? handles = PickFilesForPage(core, owner, message);
                    accepted = handles is { Count: > 0 };
                    ReplyToPage(core, id, accepted, value: null, objects: handles);
                    break;
                }
                case "color":
                {
                    string current = message.TryGetProperty("value", out JsonElement value) ? value.GetString() ?? "#000000" : "#000000";
                    Color initial = PhantomColorParser.TryParseOpaque(current, out Color parsed) ? parsed : Color.Black;
                    accepted = PickColor(owner, initial, out Color color);
                    ReplyToPage(core, id, accepted, accepted ? PhantomColorParser.ToHex(color).ToLowerInvariant() : null, objects: null);
                    break;
                }
                default:
                    ReplyToPage(core, id, ok: false, value: null, objects: null);
                    return;
            }

            NoteShieldRequestOutcome(site, accepted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Log($"A page's {type} request could not be completed: {ex.Message}");
            ReplyToPage(core, id, ok: false, value: null, objects: null);
        }
        finally
        {
            _shieldDialogOpen = false;
        }
    }

    /// <summary>The site a WebView shows (scheme, host and port), which picker and print limits are kept for.</summary>
    private static string GetShieldSite(CoreWebView2 core)
    {
        try
        {
            return Uri.TryCreate(core.Source, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : "(page)";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return "(page)";
        }
    }

    private bool IsShieldRequestRefused(string site)
    {
        if (!_shieldRequestsRefusedUntil.TryGetValue(site, out DateTime until))
        {
            return false;
        }

        if (DateTime.UtcNow < until)
        {
            return true;
        }

        _shieldRequestsRefusedUntil.Remove(site);
        return false;
    }

    /// <summary>
    /// Three cancelled picker or print requests from a site within 30 seconds: the site is refused more for a minute,
    /// even if it reloads.
    /// </summary>
    private void NoteShieldRequestOutcome(string site, bool accepted)
    {
        if (accepted)
        {
            _cancelledShieldRequests.Remove(site);
            return;
        }

        DateTime now = DateTime.UtcNow;
        (int count, DateTime first) = _cancelledShieldRequests.TryGetValue(site, out var previous) && now - previous.FirstUtc < TimeSpan.FromSeconds(30)
            ? (previous.Count + 1, previous.FirstUtc)
            : (1, now);
        _cancelledShieldRequests[site] = (count, first);
        if (count >= 3)
        {
            _cancelledShieldRequests.Remove(site);
            _shieldRequestsRefusedUntil[site] = now + ShieldRefusalLength;
            Log($"{site} kept opening pickers or print dialogs that were cancelled; it can open no more for a minute.");
            ShowBrowserNotice("This site's pickers and print dialogs are blocked for a minute.");
        }
    }

    /// <summary>
    /// The browser's file picker is a window of its own: PhantomHaze shows a protected one and gives the page the
    /// chosen files (as file handles, which the page script turns into the files of the input element).
    /// </summary>
    private List<object>? PickFilesForPage(CoreWebView2 core, IWin32Window owner, JsonElement message)
    {
        string mode = message.TryGetProperty("mode", out JsonElement modeElement) ? modeElement.GetString() ?? "open" : "open";
        bool multiple = message.TryGetProperty("multiple", out JsonElement multipleElement) && multipleElement.ValueKind == JsonValueKind.True;
        bool write = message.TryGetProperty("write", out JsonElement writeElement) && writeElement.ValueKind == JsonValueKind.True;
        string accept = message.TryGetProperty("accept", out JsonElement acceptElement) ? acceptElement.GetString() ?? string.Empty : string.Empty;
        string suggestedName = message.TryGetProperty("suggestedName", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        CoreWebView2Environment environment = core.Environment;

        switch (mode)
        {
            case "directory":
            {
                using var dialog = new FolderBrowserDialog { Description = "Choose a folder for the page", UseDescriptionForTitle = true };
                if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
                {
                    return null;
                }

                // Like a browser: changing files is a separate, explicit permission (otherwise the page may only read).
                if (write)
                {
                    string site = DescribeSite(core.Source);
                    write = ShowCaptureProtectedDialog(() => MessageBox.Show(
                        owner,
                        $"Let {site} edit files in \"{dialog.SelectedPath}\"?\n\nIt will be able to change and delete files in this folder. Choose No to let it only read them.",
                        "PhantomHaze - Edit files",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2)) == DialogResult.Yes;
                }

                return [environment.CreateWebFileSystemDirectoryHandle(
                    dialog.SelectedPath,
                    write ? CoreWebView2FileSystemHandlePermission.ReadWrite : CoreWebView2FileSystemHandlePermission.ReadOnly)];
            }
            case "save":
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Save file",
                    FileName = MakeSafeFileName(suggestedName, "download"),
                    Filter = BuildFileDialogFilter(accept),
                    OverwritePrompt = true,
                };
                if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
                {
                    return null;
                }

                if (!File.Exists(dialog.FileName))
                {
                    File.WriteAllBytes(dialog.FileName, []);
                }

                return [environment.CreateWebFileSystemFileHandle(dialog.FileName, CoreWebView2FileSystemHandlePermission.ReadWrite)];
            }
            default:
            {
                using var dialog = new OpenFileDialog
                {
                    Title = multiple ? "Choose files for the page" : "Choose a file for the page",
                    Multiselect = multiple,
                    CheckFileExists = true,
                    Filter = BuildFileDialogFilter(accept),
                };
                if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
                {
                    return null;
                }

                return dialog.FileNames
                    .Take(500)
                    .Select(path => (object)environment.CreateWebFileSystemFileHandle(path, CoreWebView2FileSystemHandlePermission.ReadOnly))
                    .ToList();
            }
        }
    }

    private static readonly Dictionary<string, string> AcceptPatternsByMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/*"] = "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg;*.heic;*.avif;*.ico",
        ["video/*"] = "*.mp4;*.webm;*.mov;*.mkv;*.avi;*.m4v",
        ["audio/*"] = "*.mp3;*.wav;*.ogg;*.m4a;*.flac;*.aac;*.opus",
        ["text/*"] = "*.txt;*.csv;*.html;*.htm;*.css;*.md",
        ["image/png"] = "*.png",
        ["image/jpeg"] = "*.jpg;*.jpeg",
        ["image/gif"] = "*.gif",
        ["image/webp"] = "*.webp",
        ["application/pdf"] = "*.pdf",
        ["text/plain"] = "*.txt",
        ["text/csv"] = "*.csv",
        ["application/json"] = "*.json",
        ["application/zip"] = "*.zip",
    };

    /// <summary>The file dialog filter for an accept="..." list: the accepted types first, all files always available.</summary>
    internal static string BuildFileDialogFilter(string accept)
    {
        var patterns = new List<string>();
        foreach (string token in accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith('.') && token.Length > 1 && token.Length < 20 && token.IndexOfAny(['*', '?', '|', ';']) < 0)
            {
                patterns.Add("*" + token);
            }
            else if (AcceptPatternsByMediaType.TryGetValue(token, out string? mapped))
            {
                patterns.AddRange(mapped.Split(';'));
            }
        }

        patterns = patterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return patterns.Count == 0
            ? "All files (*.*)|*.*"
            : $"Accepted files ({string.Join(", ", patterns.Take(6))}{(patterns.Count > 6 ? ", ..." : string.Empty)})|{string.Join(";", patterns)}|All files (*.*)|*.*";
    }

    private void ReplyToPage(CoreWebView2 core, string id, bool ok, string? value, List<object>? objects)
    {
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["phantomHaze"] = "shield-result",
                ["id"] = id,
                ["ok"] = ok,
                ["value"] = value,
            });
            if (objects is { Count: > 0 })
            {
                core.PostWebMessageAsJson(json, objects);
            }
            else
            {
                core.PostWebMessageAsJson(json);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException or ArgumentException)
        {
            // The page went away.
        }
    }

    // -- Link status (hover) and short notices --

    private void BuildBrowserStatusLabel()
    {
        _browserStatusLabel.AutoSize = true;
        _browserStatusLabel.Visible = false;
        _browserStatusLabel.UseMnemonic = false;
        _browserStatusLabel.AutoEllipsis = true;
        _browserStatusLabel.Font = _compactProtectionFont;
        _browserStatusLabel.Padding = ScalePadding(8, 3, 8, 3);
        _browserStatusLabel.BorderStyle = BorderStyle.FixedSingle;
        _browserStatusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _contentHostPanel.Controls.Add(_browserStatusLabel);
        _contentHostPanel.Resize += (_, _) => PositionBrowserStatusLabel();
    }

    private void OnShieldStatusBarTextChanged(object? sender, object e)
    {
        if (sender is CoreWebView2 core && _activeTab?.WebView.CoreWebView2 == core)
        {
            try
            {
                _hoveredLinkText = core.StatusBarText;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                _hoveredLinkText = null;
            }

            UpdateBrowserStatusLabel();
        }
    }

    private void ShowBrowserNotice(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        _browserNotice = text;
        _browserNoticeTimer.Stop();
        _browserNoticeTimer.Start();
        UpdateBrowserStatusLabel();
    }

    private void UpdateBrowserStatusLabel()
    {
        if (_browserStatusLabel.IsDisposed)
        {
            return;
        }

        string? text = !string.IsNullOrEmpty(_hoveredLinkText) ? _hoveredLinkText : _browserNotice;
        bool show = !string.IsNullOrEmpty(text) && (_webViewModeEnabled || _browserNotice is not null);
        if (show)
        {
            _browserStatusLabel.Text = TrimForLog(text, 300);
            _browserStatusLabel.BackColor = _palette.PanelBack;
            _browserStatusLabel.ForeColor = ResolveTextColor(_palette.Text);
            _browserStatusLabel.MaximumSize = new Size(Math.Max(ScaleByDpi(200), (_contentHostPanel.ClientSize.Width * 2) / 3), 0);
            PositionBrowserStatusLabel();
        }

        if (_browserStatusLabel.Visible != show)
        {
            _browserStatusLabel.Visible = show;
        }

        if (show)
        {
            _browserStatusLabel.BringToFront();
        }
    }

    private void PositionBrowserStatusLabel()
    {
        int margin = ScaleByDpi(6);
        _browserStatusLabel.Location = new Point(margin, Math.Max(0, _contentHostPanel.ClientSize.Height - _browserStatusLabel.Height - margin));
    }

    // -- Fullscreen (videos, web games) --

    private void OnShieldFullScreenChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2 core || !_tabByCore.TryGetValue(core, out BrowserTab? tab))
        {
            return;
        }

        bool wantsFullScreen;
        try
        {
            wantsFullScreen = core.ContainsFullScreenElement;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            return;
        }

        if (wantsFullScreen && _webViewModeEnabled && ReferenceEquals(tab, _activeTab))
        {
            EnterFullScreen(tab);
        }
        else if (!wantsFullScreen && ReferenceEquals(tab, _fullScreenTab))
        {
            ExitFullScreen(askPage: false);
        }
    }

    private void EnterFullScreen(BrowserTab tab)
    {
        if (_fullScreenTab is not null)
        {
            _fullScreenTab = tab;
            return;
        }

        _fullScreenTab = tab;
        _borderStyleBeforeFullScreen = FormBorderStyle;
        _windowStateBeforeFullScreen = WindowState;
        _updateBannerVisibleBeforeFullScreen = _updateBannerPanel.Visible;
        SuspendLayout();
        try
        {
            HideFindPanel(clearHighlights: false);
            _historyPanel.Visible = false;
            _menuBarPanel.Visible = false;
            _updateBannerPanel.Visible = false;
            _tabStripPanel.Visible = false;
            _statusLabel.Visible = false;
            _toggleButton.Visible = false;
            _extensionLauncherPanel.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized)
            {
                // Maximizing again after removing the border makes the window cover the taskbar too.
                WindowState = FormWindowState.Normal;
            }

            WindowState = FormWindowState.Maximized;
        }
        finally
        {
            ResumeLayout(true);
        }

        Log($"Tab {tab.Id} entered fullscreen (press Esc to leave).");
    }

    /// <param name="askPage">True when PhantomHaze ends fullscreen (the page is told to leave it too).</param>
    private void ExitFullScreen(bool askPage)
    {
        BrowserTab? tab = _fullScreenTab;
        if (tab is null)
        {
            return;
        }

        _fullScreenTab = null;
        SuspendLayout();
        try
        {
            FormBorderStyle = _borderStyleBeforeFullScreen;
            // Leaving Maximized and maximizing again makes Windows fit the window to the work area (above the taskbar).
            WindowState = FormWindowState.Normal;
            if (_windowStateBeforeFullScreen != FormWindowState.Normal)
            {
                WindowState = _windowStateBeforeFullScreen;
            }
            _menuBarPanel.Visible = true;
            _updateBannerPanel.Visible = _updateBannerVisibleBeforeFullScreen;
            _extensionLauncherPanel.Visible = _settings.ExtensionsEnabled && _extensionLauncherPanel.Controls.Count > 0;
        }
        finally
        {
            ResumeLayout(true);
        }

        ApplyViewChrome();
        PositionExtensionLauncherPanel();
        if (askPage && tab.WebView.CoreWebView2 is CoreWebView2 core)
        {
            try
            {
                _ = core.ExecuteScriptAsync("if (document.fullscreenElement) { document.exitFullscreen().catch(() => {}); }");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
            {
                // The tab is closing.
            }
        }
    }

    // ---- Protected dialogs used in place of the browser's own ------------------------------------------------------

    private Form CreateShieldDialog(string title, int width)
    {
        var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(ScaleByDpi(width), 0),
            Padding = ScalePadding(14, 12, 14, 12),
        };
        RegisterProtectedAuxiliaryWindow(dialog, title);
        return dialog;
    }

    private Label CreateShieldMessageLabel(string message, int width)
    {
        return new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(ScaleByDpi(width), 0),
            UseMnemonic = false,
            Margin = ScalePadding(0, 0, 0, 10),
        };
    }

    private T ShowShieldDialog<T>(Form dialog, IWin32Window owner, Func<DialogResult, T> result)
    {
        ApplyDialogTheme(dialog);
        ApplyAppFontToDialog(dialog);
        DialogResult answer = dialog.ShowDialog(owner);
        return result(answer);
    }

    /// <returns>The index of the chosen option, or -1 when the dialog was cancelled.</returns>
    private int ShowChoiceDialog(IWin32Window owner, string title, string message, IReadOnlyList<string> options)
    {
        using Form dialog = CreateShieldDialog(title, 380);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(CreateShieldMessageLabel(message, 420));
        int chosen = -1;
        for (int index = 0; index < options.Count; index++)
        {
            int option = index;
            var button = new Button
            {
                Text = options[index],
                AutoSize = true,
                MinimumSize = new Size(ScaleByDpi(340), ScaleByDpi(32)),
                Margin = ScalePadding(0, 0, 0, 6),
                UseMnemonic = false,
                DialogResult = DialogResult.OK,
            };
            button.Click += (_, _) => chosen = option;
            layout.Controls.Add(button);
        }

        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            MinimumSize = new Size(ScaleByDpi(340), ScaleByDpi(32)),
            DialogResult = DialogResult.Cancel,
        };
        layout.Controls.Add(cancel);
        dialog.CancelButton = cancel;
        dialog.Controls.Add(layout);
        return ShowShieldDialog(dialog, owner, answer => answer == DialogResult.OK ? chosen : -1);
    }

    /// <returns>The text typed, or null when cancelled (prompt()).</returns>
    private string? ShowTextInputDialog(IWin32Window owner, string title, string message, string defaultText)
    {
        using Form dialog = CreateShieldDialog(title, 400);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(CreateShieldMessageLabel(message, 440));
        var input = new TextBox { Text = defaultText, Width = ScaleByDpi(380), Margin = ScalePadding(0, 0, 0, 10) };
        layout.Controls.Add(input);
        layout.Controls.Add(CreateOkCancelRow(dialog));
        dialog.Controls.Add(layout);
        return ShowShieldDialog(dialog, owner, answer => answer == DialogResult.OK ? input.Text : null);
    }

    /// <returns>The user name and password, or null when cancelled.</returns>
    private (string User, string Password)? ShowCredentialsDialog(IWin32Window owner, string title, string message)
    {
        using Form dialog = CreateShieldDialog($"Sign in - {title}", 380);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(CreateShieldMessageLabel(message, 420));
        layout.Controls.Add(new Label { Text = "User name", AutoSize = true });
        var user = new TextBox { Width = ScaleByDpi(360), Margin = ScalePadding(0, 0, 0, 8) };
        layout.Controls.Add(user);
        layout.Controls.Add(new Label { Text = "Password", AutoSize = true });
        var password = new TextBox { Width = ScaleByDpi(360), UseSystemPasswordChar = true, Margin = ScalePadding(0, 0, 0, 10) };
        layout.Controls.Add(password);
        layout.Controls.Add(CreateOkCancelRow(dialog, "Sign in"));
        dialog.Controls.Add(layout);
        return ShowShieldDialog<(string, string)?>(dialog, owner, answer => answer == DialogResult.OK ? (user.Text, password.Text) : null);
    }

    private FlowLayoutPanel CreateOkCancelRow(Form dialog, string okText = "OK")
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        var ok = new Button { Text = okText, AutoSize = true, MinimumSize = new Size(ScaleByDpi(90), ScaleByDpi(30)), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(ScaleByDpi(90), ScaleByDpi(30)), DialogResult = DialogResult.Cancel };
        row.Controls.Add(ok);
        row.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return row;
    }

    // ---- The page script ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs in every page and frame before the page's own scripts. It keeps the browser from opening windows of its
    /// own: drop-down lists and tooltips are drawn inside the page, picture-in-picture, sharing and the eyedropper are
    /// turned off, date pickers are typed into, and file pickers, colour pickers and printing go through PhantomHaze's
    /// protected dialogs (frames ask through the top page, and only right after a click or key press).
    /// </summary>
    private const string CaptureShieldPageScript = """
        (() => {
          'use strict';
          if (window.__phantomHazeShield) { return; }
          try { Object.defineProperty(window, '__phantomHazeShield', { value: true }); } catch (_) { return; }
          const isTop = window.top === window;
          const webview = isTop && window.chrome && window.chrome.webview ? window.chrome.webview : null;
          const notAllowed = (what) => Promise.reject(new DOMException(what + ' is turned off in PhantomHaze: it opens a window screen capture could see.', 'NotAllowedError'));
          const define = (target, name, value) => { try { if (target) { Object.defineProperty(target, name, { configurable: true, writable: true, value }); } } catch (_) { } };

          // Drop-down lists drawn inside the page; no calendar pop-ups; the in-page tooltip.
          const css =
            'select:not([multiple]), select:not([multiple])::picker(select) { appearance: base-select; }' +
            'input::-webkit-calendar-picker-indicator { display: none !important; }' +
            '#__phantomhaze-tip { position: fixed; z-index: 2147483647; max-width: 360px; padding: 4px 8px; border-radius: 6px;' +
            ' background: rgba(32, 33, 36, 0.94); color: #fff; font: 12px/1.4 "Segoe UI", system-ui, sans-serif; pointer-events: none;' +
            ' white-space: pre-wrap; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3); }';
          let sheet = null;
          try { sheet = new CSSStyleSheet(); sheet.replaceSync(css); } catch (_) { sheet = null; }
          const adopt = (root) => {
            if (!sheet || !root) { return; }
            try { if (!root.adoptedStyleSheets.includes(sheet)) { root.adoptedStyleSheets = [...root.adoptedStyleSheets, sheet]; } } catch (_) { }
          };
          adopt(document);
          document.addEventListener('DOMContentLoaded', () => adopt(document));
          window.addEventListener('load', () => adopt(document));
          try {
            const attachShadow = Element.prototype.attachShadow;
            Element.prototype.attachShadow = function attachShadowWithShield(init) { const root = attachShadow.call(this, init); adopt(root); return root; };
          } catch (_) { }

          // Tooltips (title attributes and SVG titles) drawn inside the page.
          let tip = null, tipOwner = null, tipTimer = 0, lastX = 0, lastY = 0;
          const restoreTitle = () => {
            clearTimeout(tipTimer);
            if (tip) { tip.remove(); tip = null; }
            if (tipOwner) {
              try { if (tipOwner.kind === 'attr') { tipOwner.el.setAttribute('title', tipOwner.text); } else { tipOwner.node.textContent = tipOwner.text; } } catch (_) { }
              tipOwner = null;
            }
          };
          const findTooltipOwner = (path) => {
            for (const node of path) {
              if (!(node instanceof Element)) { continue; }
              if (node.hasAttribute('title')) { return { el: node, kind: 'attr', text: node.getAttribute('title') || '' }; }
              if (node instanceof SVGElement) {
                const title = Array.from(node.children).find((child) => child.localName === 'title');
                if (title) { return { el: node, kind: 'svg', node: title, text: title.textContent || '' }; }
              }
            }
            return null;
          };
          const placeTip = () => {
            if (!tip) { return; }
            const rect = tip.getBoundingClientRect();
            let x = lastX + 12, y = lastY + 20;
            if (x + rect.width > innerWidth - 4) { x = Math.max(4, innerWidth - rect.width - 4); }
            if (y + rect.height > innerHeight - 4) { y = Math.max(4, lastY - rect.height - 8); }
            tip.style.left = x + 'px';
            tip.style.top = y + 'px';
          };
          addEventListener('mousemove', (event) => { lastX = event.clientX; lastY = event.clientY; }, { capture: true, passive: true });
          addEventListener('mouseover', (event) => {
            const owner = findTooltipOwner(event.composedPath());
            if (owner && tipOwner && owner.el === tipOwner.el) { return; }
            restoreTitle();
            if (!owner || !owner.text.trim()) { return; }
            try { if (owner.kind === 'attr') { owner.el.removeAttribute('title'); } else { owner.node.textContent = ''; } } catch (_) { return; }
            tipOwner = owner;
            const text = owner.text;
            tipTimer = setTimeout(() => {
              if (tipOwner !== owner || !document.documentElement) { return; }
              tip = document.createElement('div');
              tip.id = '__phantomhaze-tip';
              tip.setAttribute('role', 'tooltip');
              tip.textContent = text;
              document.documentElement.appendChild(tip);
              placeTip();
            }, 600);
          }, true);
          addEventListener('mouseout', (event) => { if (!event.relatedTarget) { restoreTitle(); } }, true);
          addEventListener('mousedown', restoreTitle, true);
          addEventListener('keydown', restoreTitle, true);
          addEventListener('blur', restoreTitle);

          // No picture-in-picture windows, share sheet or eyedropper.
          if (window.HTMLVideoElement) { define(HTMLVideoElement.prototype, 'requestPictureInPicture', function requestPictureInPicture() { return notAllowed('Picture-in-picture'); }); }
          try { Object.defineProperty(Document.prototype, 'pictureInPictureEnabled', { configurable: true, get: () => false }); } catch (_) { }
          if (window.documentPictureInPicture) { define(Object.getPrototypeOf(window.documentPictureInPicture), 'requestWindow', function requestWindow() { return notAllowed('Picture-in-picture'); }); }
          const noPictureInPicture = (event) => { if (event.target instanceof HTMLVideoElement) { try { event.target.disablePictureInPicture = true; } catch (_) { } } };
          ['loadstart', 'play', 'mouseover'].forEach((type) => addEventListener(type, noPictureInPicture, true));
          if (navigator.share) { define(Navigator.prototype, 'share', function share() { return notAllowed('Sharing'); }); }
          if (window.EyeDropper) { define(EyeDropper.prototype, 'open', function open() { return notAllowed('The eyedropper'); }); }

          // Requests to PhantomHaze. Frames ask through the top page, which only passes on requests made right after
          // the user clicked or pressed a key (user activation reaches the top page too).
          const pending = new Map();
          const newId = () => (crypto.randomUUID ? crypto.randomUUID() : String(Math.random()).slice(2) + String(Date.now()));
          const request = (message) => new Promise((resolve) => {
            if (isTop) {
              if (!webview) { resolve(null); return; }
              // Pickers open only right after a click or key press, as in a browser.
              if (message.type !== 'print' && navigator.userActivation && !navigator.userActivation.isActive) { resolve(null); return; }
              const id = newId();
              pending.set(id, resolve);
              try { webview.postMessage(Object.assign({}, message, { phantomHaze: 'shield', id })); } catch (_) { pending.delete(id); resolve(null); }
            } else {
              try {
                const channel = new MessageChannel();
                channel.port1.onmessage = (event) => { resolve(event.data || null); channel.port1.close(); };
                window.top.postMessage({ phantomHazeRelay: message }, '*', [channel.port2]);
              } catch (_) { resolve(null); }
            }
          });
          const toFiles = async (handles, mode) => {
            if (mode !== 'directory') { return Promise.all(handles.map((handle) => handle.getFile())); }
            const files = [];
            const walk = async (directory, depth) => {
              for await (const entry of directory.values()) {
                if (files.length >= 2000) { return; }
                if (entry.kind === 'file') { files.push(await entry.getFile()); } else if (depth < 16) { await walk(entry, depth + 1); }
              }
            };
            await walk(handles[0], 0);
            return files;
          };
          if (webview) {
            webview.addEventListener('message', (event) => {
              const data = event.data;
              if (!data || data.phantomHaze !== 'shield-result') { return; }
              const resolve = pending.get(data.id);
              if (!resolve) { return; }
              pending.delete(data.id);
              resolve({ ok: !!data.ok, value: data.value, handles: Array.from(event.additionalObjects || []) });
            });
            addEventListener('message', async (event) => {
              const data = event.data;
              if (!data || !data.phantomHazeRelay || !event.ports || !event.ports[0]) { return; }
              event.stopImmediatePropagation();
              const port = event.ports[0];
              if (navigator.userActivation && !navigator.userActivation.isActive) { port.postMessage(null); return; }
              const relayed = data.phantomHazeRelay;
              const result = await request(relayed);
              if (!result) { port.postMessage(null); return; }
              const answer = { ok: result.ok, value: result.value };
              if (result.handles && result.handles.length) {
                try { answer.files = await toFiles(result.handles, relayed.mode); } catch (_) { answer.files = []; }
              }
              port.postMessage(answer);
            }, true);
          }

          // File pickers.
          const fireValueEvents = (input) => {
            input.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
          };
          const pickFiles = async (input) => {
            const mode = input.webkitdirectory ? 'directory' : 'open';
            const result = await request({ type: 'file', mode, multiple: !!input.multiple, accept: input.accept || '' });
            let files = [];
            try { files = result ? (result.files || (result.handles && result.handles.length ? await toFiles(result.handles, mode) : [])) : []; } catch (_) { files = []; }
            if (!files.length) { input.dispatchEvent(new Event('cancel', { bubbles: true })); return; }
            const transfer = new DataTransfer();
            files.forEach((file) => transfer.items.add(file));
            input.files = transfer.files;
            fireValueEvents(input);
          };
          const pickColor = async (input) => {
            const result = await request({ type: 'color', value: input.value || '#000000' });
            if (!result || !result.ok || typeof result.value !== 'string' || input.value === result.value) { return; }
            input.value = result.value;
            fireValueEvents(input);
          };
          const pickerTypes = ['date', 'time', 'datetime-local', 'month', 'week'];
          addEventListener('click', (event) => {
            const input = event.composedPath().find((node) => node instanceof HTMLInputElement && (node.type === 'file' || node.type === 'color'));
            if (!input || input.disabled) { return; }
            event.preventDefault();
            if (input.type === 'file') { pickFiles(input); } else { pickColor(input); }
          }, true);
          addEventListener('keydown', (event) => {
            const target = event.composedPath()[0];
            if (target instanceof HTMLInputElement && pickerTypes.includes(target.type) &&
                (event.key === 'F4' || (event.altKey && (event.key === 'ArrowDown' || event.key === 'ArrowUp')))) {
              event.preventDefault();
            }
          }, true);
          addEventListener('focusin', (event) => {
            const target = event.composedPath()[0];
            if (target instanceof HTMLInputElement && target.hasAttribute('list')) {
              target.setAttribute('data-phantomhaze-list', target.getAttribute('list'));
              target.removeAttribute('list');
            }
          }, true);
          try {
            const showPicker = HTMLInputElement.prototype.showPicker;
            if (showPicker) {
              HTMLInputElement.prototype.showPicker = function showPickerWithShield() {
                if (this.type === 'file') { pickFiles(this); return; }
                if (this.type === 'color') { pickColor(this); return; }
                if (pickerTypes.includes(this.type) || this.hasAttribute('list')) { return; }
                return showPicker.call(this);
              };
            }
          } catch (_) { }

          // File System Access pickers.
          const acceptOf = (types) => (types || []).flatMap((type) => Object.entries((type && type.accept) || {}).flatMap(([mime, extensions]) => [mime].concat(extensions || []))).join(',');
          const aborted = () => new DOMException('The user aborted a request.', 'AbortError');
          if (isTop) {
            if ('showOpenFilePicker' in window) {
              window.showOpenFilePicker = async (options = {}) => {
                const result = await request({ type: 'file', mode: 'open', multiple: !!options.multiple, accept: acceptOf(options.types) });
                if (!result || !result.handles || !result.handles.length) { throw aborted(); }
                return result.handles;
              };
            }
            if ('showSaveFilePicker' in window) {
              window.showSaveFilePicker = async (options = {}) => {
                const result = await request({ type: 'file', mode: 'save', suggestedName: options.suggestedName || '', accept: acceptOf(options.types) });
                if (!result || !result.handles || !result.handles.length) { throw aborted(); }
                return result.handles[0];
              };
            }
            if ('showDirectoryPicker' in window) {
              window.showDirectoryPicker = async (options = {}) => {
                const result = await request({ type: 'file', mode: 'directory', write: options.mode === 'readwrite' });
                if (!result || !result.handles || !result.handles.length) { throw aborted(); }
                return result.handles[0];
              };
            }
          } else {
            ['showOpenFilePicker', 'showSaveFilePicker', 'showDirectoryPicker'].forEach((name) => {
              if (name in window) { window[name] = () => Promise.reject(new DOMException('File pickers in embedded frames are turned off in PhantomHaze.', 'SecurityError')); }
            });
          }

          // Printing through PhantomHaze (the browser's print preview is a window of its own). Ctrl+P is also caught
          // by PhantomHaze before the page sees it; this is the second line of defence.
          try { window.print = function print() { request({ type: 'print' }); }; } catch (_) { }
          if (isTop) {
            addEventListener('keydown', (event) => {
              if ((event.ctrlKey || event.metaKey) && !event.altKey && (event.key === 'p' || event.key === 'P')) {
                event.preventDefault();
                event.stopImmediatePropagation();
                request({ type: 'print' });
              }
            }, true);
          }
        })();
        """;
}
