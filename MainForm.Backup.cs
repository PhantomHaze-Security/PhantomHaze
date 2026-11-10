using System.Globalization;
using System.Text.Json;

namespace PhantomHaze;

// Settings -> Backup: exporting settings and extensions to a .phstg file, and importing one.
public partial class MainForm
{
    /// <summary>What to do with installed extensions when the imported file has extensions too.</summary>
    private enum ExtensionImportChoice
    {
        /// <summary>Keep the installed extensions and do not install the ones in the file.</summary>
        Skip,

        /// <summary>Keep the installed extensions and install the ones in the file.</summary>
        Merge,

        /// <summary>Remove the installed extensions and install the ones in the file.</summary>
        Overwrite,
    }

    private SettingsTab BuildBackupTab(Form owner, Font boldFont, Action onImported)
    {
        var page = new TabPage("Backup");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = ScalePadding(12, 12, 12, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        void Add(Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, layout.RowStyles.Count - 1);
        }

        Label Paragraph(string text, Font? font = null)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(ScaleByDpi(660), 0),
                Margin = ScalePadding(0, 0, 0, 10),
                UseMnemonic = false,
            };
            if (font is not null)
            {
                label.Font = font;
            }

            return label;
        }

        Add(Paragraph("Back up or move your settings", boldFont));
        Add(Paragraph(
            "Export saves your settings, your installed extensions (with their settings) and the Master Log to a " +
            ".phstg file. Import a .phstg file into any PhantomHaze to sync it to that file straight away."));

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = ScalePadding(0, 2, 0, 10) };
        var exportButton = new Button { Text = "Export settings…", AutoSize = true, Height = ScaleByDpi(30), Margin = ScalePadding(0, 0, 8, 0) };
        var importButton = new Button { Text = "Import settings…", AutoSize = true, Height = ScaleByDpi(30), Margin = new Padding(0) };
        buttons.Controls.Add(exportButton);
        buttons.Controls.Add(importButton);
        Add(buttons);

        Label statusLabel = Paragraph(string.Empty);
        Add(statusLabel);
        Add(Paragraph(
            "Exports contain your saved settings: click Save first to include changes made in this window. The Master " +
            "Log in an export lists the pages you visited, so only share exports with people you trust. Logs inside a " +
            ".phstg file are never imported."));

        exportButton.Click += (_, _) =>
        {
            string? summary = ExportSettingsPackage(owner);
            if (summary is not null)
            {
                statusLabel.Text = summary;
            }
        };

        importButton.Click += (_, _) =>
        {
            if (ImportSettingsPackage(owner))
            {
                onImported();
            }
        };

        page.Controls.Add(layout);
        return new SettingsTab(page);
    }

    /// <returns>A summary for the Backup tab, or null when the user cancelled.</returns>
    private string? ExportSettingsPackage(IWin32Window owner)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export PhantomHaze Settings",
            Filter = "PhantomHaze settings (*.phstg)|*.phstg",
            DefaultExt = "phstg",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"PhantomHaze-settings-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{PhantomSettingsPackage.FileExtension}",
        };

        if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
        {
            return null;
        }

        string path = dialog.FileName;
        if (!path.EndsWith(PhantomSettingsPackage.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += PhantomSettingsPackage.FileExtension;
        }

        string fileName = Path.GetFileName(path);
        try
        {
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (InstalledExtensionEntry entry in _settings.Extensions)
            {
                try
                {
                    sources[entry.FileName] = File.ReadAllText(Path.Combine(_extensionsRootPath, entry.FileName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    missing.Add(string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id : entry.DisplayName);
                }
            }

            AppSettings exported = _settings.Clone();
            exported.Extensions.RemoveAll(entry => !sources.ContainsKey(entry.FileName));
            string settingsJson = JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true });

            // Logged first, so it is the last line of the Master Log inside the export.
            Log($"User exported settings to {fileName}");
            PhantomSettingsPackage.Write(path, AppVersionInfo.InformationalVersion, settingsJson, sources, GetLogFiles());

            string summary = $"Exported your settings, {sources.Count} extension{(sources.Count == 1 ? string.Empty : "s")} and the Master Log to {fileName}.";
            if (missing.Count > 0)
            {
                summary += $" Not included because their files are missing: {string.Join(", ", missing)}.";
            }

            return summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log($"Settings export to {fileName} failed: {ex.Message}");
            ShowCaptureProtectedDialog(() => MessageBox.Show(owner, $"The settings could not be exported:\n{ex.Message}", "PhantomHaze", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return "The export failed; nothing was saved.";
        }
    }

    /// <summary>
    /// Imports a .phstg file and applies it straight away: the general settings, theme and font come from the file;
    /// what happens to installed extensions is asked when both the client and the file have some.
    /// </summary>
    /// <returns>True when the import was applied (the Settings window then closes without saving its draft).</returns>
    private bool ImportSettingsPackage(Form owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import PhantomHaze Settings",
            Filter = "PhantomHaze settings (*.phstg)|*.phstg",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (ShowCaptureProtectedDialog(() => dialog.ShowDialog(owner)) != DialogResult.OK)
        {
            return false;
        }

        string path = dialog.FileName;
        string fileName = Path.GetFileName(path);
        PhantomSettingsPackageContents contents;
        AppSettings imported;
        try
        {
            contents = PhantomSettingsPackage.Read(path);
            imported = JsonSerializer.Deserialize<AppSettings>(contents.SettingsJson)
                ?? throw new InvalidDataException("The settings in this file are empty.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            string reason = ex is JsonException ? "The settings in this file could not be read." : ex.Message;
            Log($"Settings import from {fileName} failed: {reason}");
            ShowCaptureProtectedDialog(() => MessageBox.Show(owner, $"\"{fileName}\" could not be imported:\n{reason}", "PhantomHaze", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return false;
        }

        NormalizeSettings(imported);

        // Every extension in the file is checked exactly like an imported .phext file; broken ones are skipped.
        var incoming = new List<(InstalledExtensionEntry Entry, string Source, PhantomExtensionDefinition Definition)>();
        var problems = new List<string>();
        var incomingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledExtensionEntry entry in imported.Extensions)
        {
            string label = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id : entry.DisplayName;
            if (!contents.ExtensionSources.TryGetValue(entry.FileName, out string? source))
            {
                problems.Add($"{label}: its file is missing from the settings file");
                continue;
            }

            PhantomExtensionDefinition definition;
            try
            {
                definition = PhextParser.Parse(source, GetExtensionParserSourceName(entry));
            }
            catch (PhextParseException ex)
            {
                problems.Add($"{label}: {ex.Message}");
                continue;
            }

            if (!incomingIds.Add(definition.Id))
            {
                problems.Add($"{label}: another extension in the file has the same id");
                continue;
            }

            entry.Id = definition.Id;
            entry.DisplayName = definition.Name;
            EnsureExtensionSettingValues(entry, definition);
            incoming.Add((entry, source, definition));
        }

        ExtensionImportChoice choice = ExtensionImportChoice.Merge;
        if (_settings.Extensions.Count > 0 && incoming.Count > 0)
        {
            ExtensionImportChoice? answer = ShowExtensionImportPrompt(owner, _settings.Extensions.Count, incoming.Count);
            if (answer is null)
            {
                Log($"Settings import from {fileName} was cancelled.");
                return false;
            }

            choice = answer.Value;
        }

        // The settings come from the file; which extensions end up installed depends on the choice.
        AppSettings newSettings = imported.Clone();
        newSettings.Extensions = choice == ExtensionImportChoice.Overwrite
            ? new List<InstalledExtensionEntry>()
            : _settings.Extensions.Select(entry => entry.Clone()).ToList();
        var stagedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (choice != ExtensionImportChoice.Skip)
        {
            foreach ((InstalledExtensionEntry entry, string source, _) in incoming)
            {
                InstalledExtensionEntry target = entry.Clone();
                int existingIndex = newSettings.Extensions.FindIndex(installed => installed.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    // The same extension is installed: the file's version replaces it, in its current file.
                    target.FileName = newSettings.Extensions[existingIndex].FileName;
                    newSettings.Extensions[existingIndex] = target;
                }
                else
                {
                    newSettings.Extensions.Add(target);
                }

                target.FileName = ChooseImportedExtensionFileName(target, newSettings.Extensions);
                stagedSources[target.Id] = source;
            }
        }

        HashSet<string> previousFileNames = _settings.Extensions
            .Select(entry => NormalizeStoredExtensionFileName(entry.FileName, entry.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> referencedFileNames = newSettings.Extensions
            .Select(entry => NormalizeStoredExtensionFileName(entry.FileName, entry.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        NormalizeSettings(newSettings);
        referencedFileNames.UnionWith(newSettings.Extensions.Select(entry => entry.FileName));

        try
        {
            // The same all-or-nothing commit as Settings -> Save: on failure nothing is changed.
            CommitSettingsChanges(newSettings, previousFileNames, referencedFileNames, stagedSources);
        }
        catch (Exception ex)
        {
            Log($"Settings import from {fileName} failed; nothing was changed: {ex.Message}");
            ShowCaptureProtectedDialog(() => MessageBox.Show(owner, $"The settings could not be imported, so nothing was changed:\n{ex.Message}", "PhantomHaze", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return false;
        }

        int previouslyInstalled = _settings.Extensions.Count;
        ApplyCommittedSettings(newSettings, stagedSources.Keys);

        var summary = new List<string> { $"Your settings were synced to \"{fileName}\"." };
        string choiceText = choice switch
        {
            _ when incoming.Count == 0 => "no extensions in the file",
            ExtensionImportChoice.Skip => "skipped the file's extensions",
            ExtensionImportChoice.Overwrite => "overwrote the installed extensions",
            _ => "merged extensions",
        };

        if (incoming.Count > 0 && choice == ExtensionImportChoice.Skip)
        {
            summary.Add("Your installed extensions were kept, and the extensions in the file were not installed.");
        }
        else if (incoming.Count > 0)
        {
            summary.Add($"Installed from the file: {string.Join(", ", incoming.Select(item => item.Definition.Name))}.");
            if (choice == ExtensionImportChoice.Overwrite && previouslyInstalled > 0)
            {
                summary.Add("The extensions that were installed before were removed.");
            }
        }

        if (problems.Count > 0)
        {
            summary.Add("Not installed:\n" + string.Join("\n", problems.Select(problem => $"• {problem}")));
        }

        Log($"Settings imported from {fileName} ({choiceText}; {stagedSources.Count} extension(s) installed from the file).");
        ShowCaptureProtectedDialog(() => MessageBox.Show(owner, string.Join("\n\n", summary), "PhantomHaze", MessageBoxButtons.OK, MessageBoxIcon.Information));
        return true;
    }

    /// <summary>The Skip / Merge / Overwrite question. Returns null when the window is closed without choosing.</summary>
    private ExtensionImportChoice? ShowExtensionImportPrompt(IWin32Window owner, int installedCount, int incomingCount)
    {
        ExtensionImportChoice? choice = null;
        using var titleFont = new Font("Segoe UI", 11, FontStyle.Bold);
        using var prompt = new Form
        {
            Text = "PhantomHaze",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            KeyPreview = true,
            Padding = ScalePadding(18, 16, 18, 14),
        };

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
        for (int column = 0; column < 3; column++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        int columnWidth = ScaleByDpi(170);
        var title = new Label
        {
            Text = "Oops! You already have some extensions installed... What should we do with them?",
            Font = titleFont,
            AutoSize = true,
            MaximumSize = new Size((columnWidth * 3) + ScaleByDpi(20), 0),
            Margin = ScalePadding(0, 0, 0, 8),
            UseMnemonic = false,
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 3);

        var detail = new Label
        {
            Text = $"You have {installedCount} extension{(installedCount == 1 ? string.Empty : "s")} installed, and this file contains {incomingCount}.",
            AutoSize = true,
            MaximumSize = new Size((columnWidth * 3) + ScaleByDpi(20), 0),
            Margin = ScalePadding(0, 0, 0, 14),
        };
        layout.Controls.Add(detail, 0, 1);
        layout.SetColumnSpan(detail, 3);

        (ExtensionImportChoice Choice, string Label, string Description)[] options =
        [
            (ExtensionImportChoice.Skip, "Skip", "Keep your installed extensions and don't install the ones in the file."),
            (ExtensionImportChoice.Merge, "Merge", "Keep your installed extensions and install the ones in the file."),
            (ExtensionImportChoice.Overwrite, "Overwrite", "Delete your installed extensions and install the ones in the file."),
        ];

        for (int index = 0; index < options.Length; index++)
        {
            (ExtensionImportChoice optionChoice, string label, string description) = options[index];
            int rightMargin = index < options.Length - 1 ? 10 : 0;
            var button = new Button
            {
                Text = label,
                Size = new Size(columnWidth, ScaleByDpi(32)),
                Margin = ScalePadding(0, 0, rightMargin, 6),
                AccessibleDescription = description,
            };
            button.Click += (_, _) =>
            {
                choice = optionChoice;
                prompt.DialogResult = DialogResult.OK;
            };

            layout.Controls.Add(button, index, 2);
            layout.Controls.Add(new Label
            {
                Text = description,
                AutoSize = true,
                MaximumSize = new Size(columnWidth, 0),
                Margin = ScalePadding(0, 0, rightMargin, 0),
            }, index, 3);

            if (optionChoice == ExtensionImportChoice.Merge)
            {
                // Enter picks Merge, which never removes anything.
                prompt.AcceptButton = button;
            }
        }

        prompt.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                prompt.DialogResult = DialogResult.Cancel;
            }
        };

        ApplyAppFontToDialog(layout);
        title.Font = MapFont(titleFont);
        Size preferred = layout.GetPreferredSize(Size.Empty);
        prompt.ClientSize = new Size(preferred.Width + prompt.Padding.Horizontal, preferred.Height + prompt.Padding.Vertical);
        layout.Dock = DockStyle.Fill;
        prompt.Controls.Add(layout);
        ApplyDialogTheme(prompt);
        RegisterProtectedAuxiliaryWindow(prompt, "Import prompt");
        prompt.ShowDialog(owner);
        return choice;
    }
}
