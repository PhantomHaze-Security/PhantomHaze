using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PhantomHaze;

/// <summary>A log file written by PhantomHaze, as found in the logs folder.</summary>
internal sealed record PhantomLogFile(string FileName, string FullPath, long Length, DateTime LastWriteUtc);

internal sealed class PhantomSettingsPackageManifest
{
    public string Format { get; set; } = PhantomSettingsPackage.FormatName;
    public int FormatVersion { get; set; } = PhantomSettingsPackage.CurrentFormatVersion;
    public string AppVersion { get; set; } = string.Empty;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public int ExtensionCount { get; set; }
    public int LogFileCount { get; set; }
}

/// <summary>What a .phstg file contains, once it has been read and checked.</summary>
internal sealed class PhantomSettingsPackageContents
{
    public PhantomSettingsPackageManifest Manifest { get; init; } = new();

    /// <summary>The exported settings.json (the same JSON as the settings file on disk).</summary>
    public string SettingsJson { get; init; } = string.Empty;

    /// <summary>Extension sources, keyed by the file name the settings refer to them by (case-insensitive).</summary>
    public Dictionary<string, string> ExtensionSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads and writes .phstg files: ZIP archives with manifest.json, settings.json, the installed extensions'
/// sources (extensions/*.phext) and the Master Log (logs/). Also writes the Master Log download (a ZIP of the logs).
/// Reading never extracts anything to disk and limits how much is decompressed, so a damaged or hostile file
/// cannot fill the disk or memory. Logs inside a .phstg are for diagnostics only and are never imported.
/// </summary>
internal static class PhantomSettingsPackage
{
    public const string FileExtension = ".phstg";
    public const string FormatName = "PhantomHaze settings";
    public const int CurrentFormatVersion = 1;
    public const string MasterLogFileName = "PhantomHaze Master Log.txt";

    private const string ManifestEntry = "manifest.json";
    private const string SettingsEntry = "settings.json";
    private const string ExtensionsFolder = "extensions/";
    private const string LogsFolder = "logs/";
    private const string DailyLogsFolder = "daily/";
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumSettingsBytes = 8 * 1024 * 1024;
    private const int MaximumExtensionBytes = 4 * 1024 * 1024;
    private const int MaximumExtensions = 500;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static void Write(
        string path,
        string appVersion,
        string settingsJson,
        IReadOnlyDictionary<string, string> extensionSourcesByFileName,
        IReadOnlyList<PhantomLogFile> logs)
    {
        var manifest = new PhantomSettingsPackageManifest
        {
            AppVersion = appVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            ExtensionCount = extensionSourcesByFileName.Count,
            LogFileCount = logs.Count,
        };

        WriteArchiveAtomically(path, archive =>
        {
            WriteTextEntry(archive, ManifestEntry, JsonSerializer.Serialize(manifest, IndentedJson));
            WriteTextEntry(archive, SettingsEntry, settingsJson);
            foreach ((string fileName, string source) in extensionSourcesByFileName.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                WriteTextEntry(archive, ExtensionsFolder + fileName, source);
            }

            AddLogs(archive, LogsFolder, logs, appVersion);
        });
    }

    /// <summary>The Master Log download: every log file plus one combined, chronological log.</summary>
    public static void WriteMasterLogArchive(string path, string appVersion, IReadOnlyList<PhantomLogFile> logs)
    {
        WriteArchiveAtomically(path, archive => AddLogs(archive, string.Empty, logs, appVersion));
    }

    /// <summary>Reads a .phstg file. Throws <see cref="InvalidDataException"/> with a readable message when it cannot be used.</summary>
    public static PhantomSettingsPackageContents Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The settings file could not be found.", path);
        }

        if (info.Length > MaximumPackageBytes)
        {
            throw new InvalidDataException("This file is too large to be a PhantomHaze settings file.");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This is not a PhantomHaze settings file (it is not a .phstg archive).");
        }

        using (archive)
        {
            ZipArchiveEntry manifestEntry = FindEntry(archive, ManifestEntry)
                ?? throw new InvalidDataException("This is not a PhantomHaze settings file (manifest.json is missing).");

            PhantomSettingsPackageManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PhantomSettingsPackageManifest>(ReadEntryText(manifestEntry, MaximumManifestBytes));
            }
            catch (JsonException)
            {
                manifest = null;
            }

            if (manifest is null || !string.Equals(manifest.Format, FormatName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("This is not a PhantomHaze settings file (its manifest is not recognised).");
            }

            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"This settings file was made by a newer version of PhantomHaze ({manifest.AppVersion}). Update PhantomHaze to import it.");
            }

            ZipArchiveEntry settingsEntry = FindEntry(archive, SettingsEntry)
                ?? throw new InvalidDataException("This settings file is incomplete (settings.json is missing).");
            string settingsJson = ReadEntryText(settingsEntry, MaximumSettingsBytes);

            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = NormalizeEntryName(entry.FullName);
                if (!name.StartsWith(ExtensionsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fileName = name[ExtensionsFolder.Length..];
                // Only plain file names are accepted; the archive never decides where anything is written.
                if (fileName.Length == 0 ||
                    !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                    !fileName.EndsWith(".phext", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sources.Count >= MaximumExtensions)
                {
                    throw new InvalidDataException($"This settings file contains more than {MaximumExtensions} extensions.");
                }

                sources[fileName] = ReadEntryText(entry, MaximumExtensionBytes);
            }

            return new PhantomSettingsPackageContents
            {
                Manifest = manifest,
                SettingsJson = settingsJson,
                ExtensionSources = sources,
            };
        }
    }

    private static void WriteArchiveAtomically(string path, Action<ZipArchive> fill)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                fill(archive);
            }

            File.Move(tempPath, fullPath, overwrite: true);
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

    private static void AddLogs(ZipArchive archive, string folder, IReadOnlyList<PhantomLogFile> logs, string appVersion)
    {
        List<PhantomLogFile> ordered = logs
            .OrderBy(log => log.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(log => log.LastWriteUtc)
            .ToList();

        ZipArchiveEntry masterEntry = archive.CreateEntry(folder + MasterLogFileName, CompressionLevel.Optimal);
        using (var writer = new StreamWriter(masterEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine("PhantomHaze Master Log");
            writer.WriteLine($"Created: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (local time)");
            writer.WriteLine($"App version: {appVersion}");
            writer.WriteLine($"Log files: {ordered.Count}");
            foreach (PhantomLogFile log in ordered)
            {
                writer.WriteLine();
                writer.WriteLine($"===== {log.FileName} =====");
                writer.Flush();
                try
                {
                    using FileStream source = OpenLogForReading(log.FullPath);
                    using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    char[] buffer = new char[32 * 1024];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, read);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    writer.WriteLine($"(This log file could not be read: {ex.Message})");
                }
            }
        }

        foreach (PhantomLogFile log in ordered)
        {
            try
            {
                using FileStream source = OpenLogForReading(log.FullPath);
                ZipArchiveEntry entry = archive.CreateEntry(folder + DailyLogsFolder + log.FileName, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(log.LastWriteUtc, DateTimeKind.Utc));
                using Stream target = entry.Open();
                source.CopyTo(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Already noted in the combined log; skip the copy.
            }
        }
    }

    /// <summary>PhantomHaze appends to today's log while it runs, so logs are opened without blocking those writes.</summary>
    private static FileStream OpenLogForReading(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeEntryName(entry.FullName), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    /// <summary>Reads at most <paramref name="maximumBytes"/> decompressed bytes, whatever size the entry claims to have.</summary>
    private static string ReadEntryText(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"'{entry.FullName}' in the settings file is too large.");
        }

        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"'{entry.FullName}' in the settings file is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
