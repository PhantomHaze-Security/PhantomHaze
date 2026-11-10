using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace PhantomHaze;

// Settings -> About: name, version, sizes, technical information and the Master Log.
public partial class MainForm
{
    private sealed record AboutSizes(
        long App,
        string AppDescription,
        long Extensions,
        long History,
        long Favorites,
        long Settings,
        long Fonts,
        long Logs,
        long TemporaryWebData);

    private SettingsTab BuildAboutTab(Form owner, Font boldFont, Font titleFont)
    {
        var page = new TabPage("About");
        var scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleByDpi(190)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        int valueWidth = ScaleByDpi(460);
        int row = 0;

        void AddSpanning(Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
            row++;
        }

        Label AddRow(string name, string value)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = name,
                AutoSize = true,
                Font = boldFont,
                Margin = ScalePadding(0, 3, 12, 3),
            }, 0, row);
            var valueLabel = new Label
            {
                Text = value,
                AutoSize = true,
                MaximumSize = new Size(valueWidth, 0),
                Margin = ScalePadding(0, 3, 0, 3),
                UseMnemonic = false,
            };
            layout.Controls.Add(valueLabel, 1, row);
            row++;
            return valueLabel;
        }

        void AddSection(string title)
        {
            AddSpanning(new Label { Text = title, AutoSize = true, Font = boldFont, Margin = ScalePadding(0, 16, 0, 6) });
        }

        Label AddParagraph(string text)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(valueWidth + ScaleByDpi(190), 0),
                Margin = ScalePadding(0, 2, 0, 6),
                UseMnemonic = false,
            };
            AddSpanning(label);
            return label;
        }

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = ScalePadding(0, 0, 0, 12) };
        header.Controls.Add(new PictureBox
        {
            Image = _defaultLogoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(ScaleByDpi(48), ScaleByDpi(48)),
            Margin = ScalePadding(0, 0, 12, 0),
        });
        header.Controls.Add(new Label
        {
            Text = "PhantomHaze",
            Font = titleFont,
            AutoSize = true,
            Margin = ScalePadding(0, 8, 0, 0),
        });
        AddSpanning(header);

        ReleaseTag? tag = AppVersionInfo.Tag;
        AddRow("App name", "PhantomHaze");
        AddRow("Version", AppVersionInfo.InformationalVersion);
        AddRow("Release channel", tag is ReleaseTag current ? (current.Channel == ReleaseChannel.Beta ? "Beta" : "Stable") : "Unknown");
        Label appSizeLabel = AddRow("App size", "Calculating…");
        Label dataSizeLabel = AddRow("Saved data", "Calculating…");
        Label extensionsSizeLabel = AddRow("    Extensions", "…");
        Label historySizeLabel = AddRow("    History", "…");
        Label favoritesSizeLabel = AddRow("    Favourites", "…");
        Label settingsSizeLabel = AddRow("    Settings", "…");
        Label fontsSizeLabel = AddRow("    Fonts", "…");
        Label logsSizeLabel = AddRow("    Logs", "…");
        Label temporarySizeLabel = AddRow("    Temporary web data", "…");

        AddSection("Technical information");
        List<(string Name, string Value)> technicalInformation = BuildTechnicalInformation();
        foreach ((string name, string value) in technicalInformation)
        {
            AddRow(name, value);
        }

        AddSection("Master Log");
        Label masterLogLabel = AddParagraph("Every log PhantomHaze has written since it was installed, in one place.");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = ScalePadding(0, 4, 0, 6) };
        var downloadButton = new Button { Text = "Download all logs (.zip)…", AutoSize = true, Height = ScaleByDpi(30), Margin = ScalePadding(0, 0, 8, 4) };
        var deleteButton = new Button { Text = "Delete all logs", AutoSize = true, Height = ScaleByDpi(30), Margin = ScalePadding(0, 0, 8, 4) };
        var copyButton = new Button { Text = "Copy technical details", AutoSize = true, Height = ScaleByDpi(30), Margin = ScalePadding(0, 0, 0, 4) };
        buttons.Controls.Add(downloadButton);
        buttons.Controls.Add(deleteButton);
        buttons.Controls.Add(copyButton);
        AddSpanning(buttons);
        Label masterLogStatusLabel = AddParagraph(string.Empty);

        void RefreshMasterLogSummary()
        {
            List<PhantomLogFile> logs = GetLogFiles();
            if (logs.Count == 0)
            {
                masterLogLabel.Text = $"No logs have been saved yet. They are kept in {_logDirectory}.";
                return;
            }

            long total = logs.Sum(log => log.Length);
            string first = DescribeLogDate(logs[0]);
            string last = DescribeLogDate(logs[^1]);
            masterLogLabel.Text =
                $"Every log PhantomHaze has written since it was installed: {logs.Count} daily log file{(logs.Count == 1 ? string.Empty : "s")} " +
                $"({FormatMegabytes(total)}), from {first} to {last}. They are kept in {_logDirectory}.";
        }

        downloadButton.Click += (_, _) =>
        {
            List<PhantomLogFile> logs = GetLogFiles();
            if (logs.Count == 0)
            {
                masterLogStatusLabel.Text = "There are no logs to download yet.";
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "Download PhantomHaze Master Log",
                Filter = "ZIP archive (*.zip)|*.zip",
                DefaultExt = "zip",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"PhantomHaze-Master-Log-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip",
            };
            if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
            {
                return;
            }

            try
            {
                Log($"Master Log downloaded to {Path.GetFileName(dialog.FileName)}");
                PhantomSettingsPackage.WriteMasterLogArchive(dialog.FileName, AppVersionInfo.InformationalVersion, GetLogFiles());
                masterLogStatusLabel.Text = $"Saved {logs.Count} log file{(logs.Count == 1 ? string.Empty : "s")} and the combined Master Log to {Path.GetFileName(dialog.FileName)}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log($"The Master Log could not be saved: {ex.Message}");
                masterLogStatusLabel.Text = $"The Master Log could not be saved: {ex.Message}";
            }
        };

        deleteButton.Click += (_, _) =>
        {
            DialogResult answer = ShowCaptureProtectedDialog(() => MessageBox.Show(
                owner,
                "Delete every saved log? The Master Log starts again from now. This cannot be undone.",
                "PhantomHaze",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2));
            if (answer != DialogResult.Yes)
            {
                return;
            }

            int deleted = 0;
            int failed = 0;
            foreach (PhantomLogFile log in GetLogFiles())
            {
                try
                {
                    File.Delete(log.FullPath);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }

            Log($"{deleted} saved log file(s) were deleted by the user.");
            masterLogStatusLabel.Text = failed == 0
                ? $"Deleted {deleted} log file{(deleted == 1 ? string.Empty : "s")}."
                : $"Deleted {deleted} log file(s); {failed} could not be deleted.";
            RefreshMasterLogSummary();
        };

        copyButton.Click += (_, _) =>
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"PhantomHaze {AppVersionInfo.InformationalVersion}");
            foreach ((string name, string value) in BuildTechnicalInformation())
            {
                text.AppendLine($"{name}: {value}");
            }

            try
            {
                Clipboard.SetText(text.ToString());
                masterLogStatusLabel.Text = "The technical details were copied to the clipboard.";
            }
            catch (ExternalException ex)
            {
                masterLogStatusLabel.Text = $"The clipboard is not available: {ex.Message}";
            }
        };

        bool measured = false;
        async Task MeasureAsync()
        {
            RefreshMasterLogSummary();
            AboutSizes sizes = await Task.Run(MeasureAboutSizes);
            if (owner.IsDisposed)
            {
                return;
            }

            appSizeLabel.Text = $"{FormatMegabytes(sizes.App)} ({sizes.AppDescription})";
            long total = sizes.Extensions + sizes.History + sizes.Favorites + sizes.Settings + sizes.Fonts + sizes.Logs + sizes.TemporaryWebData;
            dataSizeLabel.Text = FormatMegabytes(total);
            extensionsSizeLabel.Text = FormatMegabytes(sizes.Extensions);
            historySizeLabel.Text = FormatMegabytes(sizes.History);
            favoritesSizeLabel.Text = FormatMegabytes(sizes.Favorites);
            settingsSizeLabel.Text = FormatMegabytes(sizes.Settings);
            fontsSizeLabel.Text = FormatMegabytes(sizes.Fonts);
            logsSizeLabel.Text = FormatMegabytes(sizes.Logs);
            temporarySizeLabel.Text = $"{FormatMegabytes(sizes.TemporaryWebData)} (cleared when PhantomHaze closes)";
        }

        scrollPanel.Controls.Add(layout);
        page.Controls.Add(scrollPanel);
        return new SettingsTab(page)
        {
            // Sizes are measured in the background the first time the tab is opened.
            Shown = () =>
            {
                if (measured)
                {
                    return;
                }

                measured = true;
                _ = MeasureAsync().ContinueWith(
                    task => Log($"About: the sizes could not be measured: {task.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.FromCurrentSynchronizationContext());
            },
        };
    }

    private List<(string Name, string Value)> BuildTechnicalInformation()
    {
        int build = NativeMethods.GetTrueOsBuildNumber();
        string buildText = build < 0
            ? "Unknown"
            : build >= NativeMethods.MinimumBuildForExcludeFromCapture
                ? build.ToString(CultureInfo.InvariantCulture)
                : $"{build.ToString(CultureInfo.InvariantCulture)} (capture protection needs {NativeMethods.MinimumBuildForExcludeFromCapture} or later)";

        string webView2;
        try
        {
            webView2 = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "Not installed";
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or DllNotFoundException or InvalidOperationException)
        {
            webView2 = "Not installed";
        }

        string sessionStarted;
        try
        {
            using Process process = Process.GetCurrentProcess();
            sessionStarted = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            sessionStarted = "Unknown";
        }

        int activeExtensions = GetEnabledExtensions().Count();
        return
        [
            (".NET runtime", RuntimeInformation.FrameworkDescription),
            ("Operating system", RuntimeInformation.OSDescription),
            ("Windows build", buildText),
            ("Architecture", RuntimeInformation.ProcessArchitecture.ToString()),
            ("WebView2 runtime", webView2),
            ("Capture protection", _protectionEnabled ? "On" : "Off"),
            ("Display scale", string.Create(CultureInfo.InvariantCulture, $"{DeviceDpi * 100 / 96}%")),
            ("Extensions", $"{_settings.Extensions.Count} installed, {activeExtensions} active"),
            ("Session started", sessionStarted),
            ("Executable", Environment.ProcessPath ?? "Unknown"),
            ("Data folder", GetAppDataDirectory()),
            ("Logs folder", _logDirectory),
        ];
    }

    /// <summary>Runs on a background thread: only reads paths that never change after startup.</summary>
    private AboutSizes MeasureAboutSizes()
    {
        long appSize;
        string appDescription;
        string? processPath = Environment.ProcessPath;
        // A single-file build has no PhantomHaze.dll next to the executable: its size is the size of the .exe.
        bool singleFile = !File.Exists(Path.Combine(AppContext.BaseDirectory, "PhantomHaze.dll"));
        if (singleFile && processPath is not null && File.Exists(processPath))
        {
            appSize = new FileInfo(processPath).Length;
            appDescription = Path.GetFileName(processPath);
        }
        else
        {
            appSize = GetDirectorySize(AppContext.BaseDirectory);
            appDescription = "app folder";
        }

        long extensions = GetDirectorySize(_extensionsRootPath);
        long history = GetFileSize(_historyFilePath);
        long favorites = GetFileSize(_favoritesFilePath);
        long fonts = GetDirectorySize(_fontCacheDirectory);
        long logs = GetDirectorySize(_logDirectory);
        long appData = GetDirectorySize(GetAppDataDirectory());
        // Everything else in the data folder: settings.json, the update reminder, preserved unreadable files.
        long settings = Math.Max(0, appData - extensions - history - favorites - fonts - logs);
        long temporary = GetDirectorySize(Path.Combine(Path.GetTempPath(), "PhantomHaze"));
        return new AboutSizes(appSize, appDescription, extensions, history, favorites, settings, fonts, logs, temporary);
    }

    private static long GetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            long total = 0;
            foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try
                {
                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The file disappeared while it was being measured.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    private static string FormatMegabytes(long bytes)
    {
        double megabytes = bytes / (1024.0 * 1024.0);
        string format = megabytes >= 10 ? "0.0" : "0.00";
        return $"{megabytes.ToString(format, CultureInfo.CurrentCulture)} MB";
    }

    /// <summary>"phantomhaze-20261110.log" becomes "2026-11-10" (falling back to the file's date).</summary>
    private static string DescribeLogDate(PhantomLogFile log)
    {
        string stamp = Path.GetFileNameWithoutExtension(log.FileName).Replace("phantomhaze-", string.Empty, StringComparison.OrdinalIgnoreCase);
        return DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : log.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
