using System.Runtime.InteropServices;

namespace PhantomHaze;

/// <summary>
/// Thin wrapper around the Win32 display-affinity APIs. This is the only
/// place that talks to native Windows APIs, kept small and isolated per
/// the "native layer" separation in the architecture.
/// </summary>
internal static class NativeMethods
{
    // Values from winuser.h
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Windows 10 2004+ (build 19041)
    public const int MinimumBuildForExcludeFromCapture = 19041;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    // Thread-local CBT hook, used only while a system dialog (MessageBox, common file dialog) is open, so the
    // dialog's own top-level window can be excluded from capture like the rest of the app.
    private const int WH_CBT = 5;
    private const int HCBT_ACTIVATE = 5;
    private const string StandardDialogClassName = "#32770";

    private delegate IntPtr CbtHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, CbtHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Makes a downloaded font (Settings -> Appearance -> Font) available to GDI text drawing in this process only.
    // FR_PRIVATE fonts are not installed for other programs and disappear when PhantomHaze exits.
    private const uint FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "AddFontResourceExW")]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);

    /// <summary>Registers a font file for GDI in this process only. Returns false if Windows did not accept it.</summary>
    public static bool RegisterPrivateFontFile(string path)
    {
        try
        {
            return AddFontResourceEx(path, FR_PRIVATE, IntPtr.Zero) > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    /// <summary>
    /// Runs a modal system dialog on the current (UI) thread and calls <paramref name="onDialogActivated"/> with the
    /// handle of every standard dialog window (#32770, used by MessageBox and the common file dialogs) that is
    /// activated while it is open, before that window is first drawn. If the hook cannot be installed, the dialog
    /// simply runs without it.
    /// </summary>
    public static T RunWithDialogActivationHook<T>(Action<IntPtr> onDialogActivated, Func<T> showDialog)
    {
        IntPtr hook = IntPtr.Zero;
        CbtHookProc callback = (nCode, wParam, lParam) =>
        {
            if (nCode == HCBT_ACTIVATE)
            {
                try
                {
                    if (IsStandardDialogWindow(wParam))
                    {
                        onDialogActivated(wParam);
                    }
                }
                catch
                {
                    // An exception must never cross back into native code.
                }
            }

            return CallNextHookEx(hook, nCode, wParam, lParam);
        };

        hook = SetWindowsHookEx(WH_CBT, callback, IntPtr.Zero, GetCurrentThreadId());
        try
        {
            return showDialog();
        }
        finally
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
            }

            // The delegate must stay alive for as long as Windows can call it.
            GC.KeepAlive(callback);
        }
    }

    private static bool IsStandardDialogWindow(IntPtr hWnd)
    {
        var buffer = new char[16];
        int length = GetClassName(hWnd, buffer, buffer.Length);
        return length == StandardDialogClassName.Length &&
               new string(buffer, 0, length).Equals(StandardDialogClassName, StringComparison.Ordinal);
    }

    // .NET's Environment.OSVersion / the classic GetVersionEx API get lied to
    // by Windows' app-compatibility shim unless the .exe ships a manifest
    // declaring Windows 10/11 support. RtlGetVersion in ntdll is unaffected
    // by that shim, so it's the only reliable way to confirm the real build
    // number without also having to get the manifest right.
    // RtlGetVersion fills the wide-character (W) structure: szCSDVersion is 128 UTF-16 characters (256 bytes). Without
    // CharSet.Unicode it was marshalled as 128 bytes, so dwOSVersionInfoSize did not match any valid size and the
    // native call could write past the end of the marshalling buffer.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

    /// <summary>
    /// Returns the true OS build number (e.g. 22631), independent of any
    /// application manifest. Returns -1 if the call itself fails, which
    /// would be unusual and worth logging on its own.
    /// </summary>
    public static int GetTrueOsBuildNumber()
    {
        var info = new OSVERSIONINFOEX { dwOSVersionInfoSize = (uint)Marshal.SizeOf<OSVERSIONINFOEX>() };
        return RtlGetVersion(ref info) == 0 ? (int)info.dwBuildNumber : -1;
    }

    /// <summary>
    /// Human-readable explanation for the Win32 error codes actually seen
    /// from SetWindowDisplayAffinity, beyond just the bare number.
    /// </summary>
    public static string DescribeError(int win32Error) => win32Error switch
    {
        0 => "no error",
        6 => "ERROR_INVALID_HANDLE — the HWND wasn't valid yet when the call was made",
        87 => "ERROR_INVALID_PARAMETER — usually means WDA_EXCLUDEFROMCAPTURE isn't supported here: " +
              "either the OS build is below 19041, or the display is running without a hardware-accelerated " +
              "desktop (common in some VMs, Remote Desktop sessions, or with a Basic/Microsoft Basic Display driver)",
        _ => "see Microsoft's System Error Codes reference for this value"
    };

    /// <summary>
    /// Applies or clears WDA_EXCLUDEFROMCAPTURE on the given window handle.
    /// Returns whether the call succeeded and, if not, the Win32 error code
    /// (via Marshal.GetLastWin32Error) so the caller can log it per the
    /// Phase 1 requirement to "log whether the API call succeeds or fails".
    /// </summary>
    public static (bool success, int win32Error) ExcludeFromCapture(IntPtr hWnd, bool enable)
    {
        uint affinity = enable ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
        bool ok = SetWindowDisplayAffinity(hWnd, affinity);
        int error = ok ? 0 : Marshal.GetLastWin32Error();
        return (ok, error);
    }

    /// <summary>
    /// Reads back the affinity Windows currently has recorded for the window,
    /// rather than trusting the app's own "enabled" flag. Use this for the
    /// Phase 1 test checklist, not just the return value of SetWindowDisplayAffinity.
    /// </summary>
    public static bool TryGetCurrentAffinity(IntPtr hWnd, out uint affinity)
        => GetWindowDisplayAffinity(hWnd, out affinity);

    public static string Describe(uint affinity) => affinity switch
    {
        WDA_NONE => "WDA_NONE (not protected)",
        WDA_MONITOR => "WDA_MONITOR (legacy, blanks on all outputs including physical monitor)",
        WDA_EXCLUDEFROMCAPTURE => "WDA_EXCLUDEFROMCAPTURE (protected — physical view normal, capture excluded)",
        _ => $"Unknown affinity (0x{affinity:X})"
    };

    // ---- Watching PhantomHaze's own top-level windows ----
    // Tooltips, combo box drop-down lists, menus and dialogs are top-level windows of their own, so the main window's
    // capture exclusion does not cover them. A WinEvent hook reports every window this process creates or shows, so
    // each one can be excluded from capture too (see MainForm.CaptureShield.cs).
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const int OBJID_WINDOW = 0;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint GA_ROOT = 2;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

    /// <summary>Reports windows this process creates, destroys or shows; out of context, on the calling (UI) thread.</summary>
    public static IntPtr WatchProcessWindows(WinEventProc callback)
    {
        try
        {
            return SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, callback, (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    public static void StopWatchingProcessWindows(IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWinEvent(hook);
        }
    }

    /// <summary>True for a top-level window (not a child window inside another one).</summary>
    public static bool IsTopLevelWindow(IntPtr hWnd) => hWnd != IntPtr.Zero && GetAncestor(hWnd, GA_ROOT) == hWnd;

    /// <summary>Every top-level window of the calling thread (tooltips, drop-downs, menus, dialogs, forms).</summary>
    public static List<IntPtr> GetCurrentThreadTopLevelWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindowsProc callback = (hWnd, _) =>
        {
            windows.Add(hWnd);
            return true;
        };
        EnumThreadWindows(GetCurrentThreadId(), callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return windows;
    }

    // ---- Taskbar thumbnails, Alt+Tab and Peek ----
    // These previews are drawn by the Windows shell, so a screen recording would show a protected window's content in
    // them. While protection is on, Windows shows the app icon instead of a live preview and Peek ignores the window.
    private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
    private const int DWMWA_DISALLOW_PEEK = 11;
    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void HideFromShellPreviews(IntPtr hWnd, bool hide)
    {
        try
        {
            int value = hide ? 1 : 0;
            DwmSetWindowAttribute(hWnd, DWMWA_FORCE_ICONIC_REPRESENTATION, ref value, sizeof(int));
            DwmSetWindowAttribute(hWnd, DWMWA_DISALLOW_PEEK, ref value, sizeof(int));
            DwmSetWindowAttribute(hWnd, DWMWA_EXCLUDED_FROM_PEEK, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Desktop composition is unavailable; there are no previews to hide either.
        }
    }

    // A thread-local CBT hook that sees each window the UI thread creates before it is first shown, so pop-ups
    // (tooltips, the edit box right-click menu, drop-down lists) are excluded from capture before their first frame.
    // The WinEvent hook above covers windows created on other threads, a moment later.
    private const int HCBT_CREATEWND = 3;
    private const int WS_CHILD = 0x40000000;

    public static IntPtr WatchThreadWindowCreation(Action<IntPtr> onTopLevelWindowCreated, out object keepAlive)
    {
        IntPtr hook = IntPtr.Zero;
        CbtHookProc callback = (nCode, wParam, lParam) =>
        {
            if (nCode == HCBT_CREATEWND)
            {
                try
                {
                    // lParam is a CBT_CREATEWND whose first field points to the CREATESTRUCT; style follows four
                    // pointers and four ints.
                    IntPtr createStruct = Marshal.ReadIntPtr(lParam);
                    int style = Marshal.ReadInt32(createStruct, (4 * IntPtr.Size) + 16);
                    if ((style & WS_CHILD) == 0)
                    {
                        onTopLevelWindowCreated(wParam);
                    }
                }
                catch
                {
                    // An exception must never cross back into native code.
                }
            }

            return CallNextHookEx(hook, nCode, wParam, lParam);
        };

        keepAlive = callback;
        try
        {
            hook = SetWindowsHookEx(WH_CBT, callback, IntPtr.Zero, GetCurrentThreadId());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            hook = IntPtr.Zero;
        }

        return hook;
    }

    public static void StopWatchingThreadWindowCreation(IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
        }
    }
}
