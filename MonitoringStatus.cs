using System.Diagnostics;

namespace PhantomHaze;

/// <summary>
/// Polls for signals that something might be watching the screen: known
/// recording/remote-access process names running, or an active Remote
/// Desktop session.
///
/// This is presence detection only, by design - there's no public Windows
/// API to tell whether an app is actively recording/sharing right now,
/// only whether it's running. Treat this as an awareness signal, not a
/// guarantee: matches the original plan for this feature ("secondary
/// detection for awareness/alerts only, not the core security
/// mechanism"). The core mechanism is still SetWindowDisplayAffinity on
/// each ProtectedForm, which protects continuously and needs no
/// detection loop at all.
/// </summary>
public sealed class MonitoringStatus : IDisposable
{
    private static readonly string[] WatchedProcessNames =
    {
        "obs64", "obs32", "obs",
        "camtasia",
        "zoom",
        "teams",
        "teamviewer",
        "anydesk",
        "chrome_remote_desktop_host",
        "mstsc", // Remote Desktop Connection client
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private bool _lastState;

    /// <summary>Fires only on a state transition (clear→detected or detected→clear),
    /// with a human-readable reason for the log.</summary>
    public event Action<bool, string>? MonitoringStateChanged;

    public bool IsMonitoringDetected { get; private set; }

    public MonitoringStatus() => _timer.Tick += (_, _) => Poll();

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        var (detected, reason) = Detect();
        if (detected == _lastState) return;

        _lastState = detected;
        IsMonitoringDetected = detected;
        MonitoringStateChanged?.Invoke(detected, reason);
    }

    private static (bool detected, string reason) Detect()
    {
        if (NativeMethods.IsRemoteSession())
        {
            return (true, "active Remote Desktop session");
        }

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                string name = proc.ProcessName.ToLowerInvariant();
                foreach (var watched in WatchedProcessNames)
                {
                    if (name.Contains(watched))
                    {
                        return (true, $"process \"{proc.ProcessName}\" running");
                    }
                }
            }
        }

        return (false, "clear");
    }
}