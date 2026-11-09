using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomHaze;

/// <summary>The channels PhantomHaze is released on. GitHub release tags look like v1.3.4-stable and v1.3.4-beta.</summary>
internal enum ReleaseChannel
{
    Stable,
    Beta,
}

/// <summary>A release tag such as "v1.3.5-beta": a three-part version number plus the release channel.</summary>
internal readonly record struct ReleaseTag(int Major, int Minor, int Patch, ReleaseChannel Channel)
{
    private static readonly Regex TagPattern = new(
        @"^v(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)-(?<channel>stable|beta)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The version number without prefix or channel, for example "1.3.5".</summary>
    public string VersionNumber => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The tag in the format of the GitHub releases and ver-banner.csv, for example "v1.3.5-beta".</summary>
    public override string ToString() => $"v{VersionNumber}-{GetChannelName(Channel)}";

    public static string GetChannelName(ReleaseChannel channel) => channel == ReleaseChannel.Beta ? "beta" : "stable";

    /// <summary>
    /// Parses a tag such as "v1.3.5-stable" or "v1.3.5-beta". Letter case and surrounding whitespace are ignored, so
    /// "v1.3.3-Beta" is the same release as "v1.3.3-beta".
    /// </summary>
    public static bool TryParse(string? text, out ReleaseTag tag)
    {
        tag = default;
        Match match = TagPattern.Match((text ?? string.Empty).Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        ReleaseChannel channel = match.Groups["channel"].Value.Equals("beta", StringComparison.OrdinalIgnoreCase)
            ? ReleaseChannel.Beta
            : ReleaseChannel.Stable;
        tag = new ReleaseTag(major, minor, patch, channel);
        return true;
    }
}

/// <summary>
/// The version of this build. It is read from &lt;InformationalVersion&gt;, which PhantomHaze.csproj builds from
/// &lt;Version&gt; and &lt;PhantomHazeReleaseChannel&gt; (for example "v1.3.5-beta"), so a release is numbered and
/// labelled in one place and the window title and the update check can never disagree.
/// </summary>
internal static class AppVersionInfo
{
    /// <summary>The informational version without the "+&lt;commit&gt;" suffix the SDK adds when building from git.</summary>
    public static string InformationalVersion { get; } = ReadInformationalVersion();

    /// <summary>This build's release tag, or null when the informational version is not a vX.Y.Z-stable/beta tag.</summary>
    public static ReleaseTag? Tag { get; } = ReleaseTag.TryParse(InformationalVersion, out ReleaseTag tag) ? tag : null;

    /// <summary>"PhantomHaze v1.3.5" for a stable build and "PhantomHaze v1.3.5 Beta" for a beta build.</summary>
    public static string WindowTitle => Tag is ReleaseTag current
        ? $"PhantomHaze v{current.VersionNumber}{(current.Channel == ReleaseChannel.Beta ? " Beta" : string.Empty)}"
        : $"PhantomHaze {InformationalVersion}";

    private static string ReadInformationalVersion()
    {
        // Read while the main window is being created, so it must never throw.
        try
        {
            Assembly assembly = typeof(AppVersionInfo).Assembly;
            string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataStart = informationalVersion.IndexOf('+');
                return (metadataStart >= 0 ? informationalVersion[..metadataStart] : informationalVersion).Trim();
            }

            Version? version = assembly.GetName().Version;
            return version is null ? "(unknown version)" : $"v{version.ToString(3)}";
        }
        catch (Exception)
        {
            return "(unknown version)";
        }
    }
}

/// <summary>One choice in the update banner's snooze menu.</summary>
internal sealed record UpdateReminderSnoozeOption(string Label, Func<DateTimeOffset, DateTimeOffset> GetSnoozeEnd);

/// <summary>
/// Decides whether the update banner is shown. ver-banner.csv on the main branch lists the latest releases: line 1
/// is the latest stable release and line 2 the latest beta release (for example "v1.3.4-stable" and "v1.3.4-beta").
/// A build only compares itself with the line for its own channel, so stable users are never reminded about a beta
/// release and beta users are never reminded about a stable release.
/// </summary>
internal static class UpdateReminder
{
    public const string PublishedReleasesUrl =
        "https://raw.githubusercontent.com/PhantomHaze-Security/PhantomHaze/refs/heads/main/ver-banner.csv";

    public const string ReleasesPageUrl = "https://www.github.com/PhantomHaze-Security/PhantomHaze/releases";

    /// <summary>The only snooze lengths offered. There is deliberately no "don't remind me again" choice.</summary>
    public static IReadOnlyList<UpdateReminderSnoozeOption> SnoozeOptions { get; } =
    [
        new("1 day", start => start.AddDays(1)),
        new("1 week", start => start.AddDays(7)),
        new("2 weeks", start => start.AddDays(14)),
        new("1 month", start => start.AddMonths(1)),
    ];

    /// <summary>
    /// The longest snooze offered (a month is at most 31 days) plus a day of tolerance for clock corrections. A saved
    /// snooze that ends later than this is ignored, so neither a hand-edited state file nor a clock that was set far
    /// ahead when the reminder was snoozed can hide the banner indefinitely.
    /// </summary>
    public static readonly TimeSpan MaximumSnoozeLength = TimeSpan.FromDays(32);

    public static bool IsSnoozed(DateTimeOffset? snoozedUntilUtc, DateTimeOffset nowUtc)
    {
        return snoozedUntilUtc is DateTimeOffset snoozedUntil &&
               snoozedUntil > nowUtc &&
               snoozedUntil - nowUtc <= MaximumSnoozeLength;
    }

    /// <summary>
    /// True while a snooze made on this same build is running. A snooze only ever covers the build it was made on:
    /// after installing another version the check starts afresh, so a reminder snoozed before updating cannot also
    /// hide the reminder for a release that comes out after the update.
    /// </summary>
    public static bool IsSnoozed(UpdateReminderState state, ReleaseTag current, DateTimeOffset nowUtc)
    {
        return WasSnoozedOn(state, current) && IsSnoozed(state.SnoozedUntilUtc, nowUtc);
    }

    /// <summary>True when the saved snooze was made on the <paramref name="current"/> build.</summary>
    public static bool WasSnoozedOn(UpdateReminderState state, ReleaseTag current)
    {
        return ReleaseTag.TryParse(state.SnoozedOnVersion, out ReleaseTag snoozedOn) && snoozedOn == current;
    }

    /// <summary>True when a saved snooze ends further in the future than any snooze option allows.</summary>
    public static bool IsSnoozeTooLong(DateTimeOffset? snoozedUntilUtc, DateTimeOffset nowUtc)
    {
        return snoozedUntilUtc is DateTimeOffset snoozedUntil && snoozedUntil - nowUtc > MaximumSnoozeLength;
    }

    /// <summary>Reads the latest release on <paramref name="channel"/> from the contents of ver-banner.csv.</summary>
    /// <param name="problem">When false is returned: why the file could not be used, for the log.</param>
    public static bool TryGetLatestRelease(string? publishedReleases, ReleaseChannel channel, out ReleaseTag latest, out string problem)
    {
        latest = default;
        string channelName = ReleaseTag.GetChannelName(channel);
        int lineIndex = channel == ReleaseChannel.Stable ? 0 : 1;
        string[] lines = (publishedReleases ?? string.Empty).Split('\n');
        string value = lineIndex < lines.Length
            ? lines[lineIndex].Split(',')[0].Trim().Trim('﻿', '"').Trim()
            : string.Empty;

        // A line for the other channel (or anything else) is rejected rather than compared, so a mistake in the file
        // can never show the banner to everyone.
        if (!ReleaseTag.TryParse(value, out ReleaseTag tag) || tag.Channel != channel)
        {
            problem = $"line {lineIndex + 1} of ver-banner.csv should be the latest {channelName} release " +
                      $"(for example v1.3.4-{channelName}) but is '{DescribeForLog(value)}'.";
            return false;
        }

        latest = tag;
        problem = string.Empty;
        return true;
    }

    /// <summary>The banner is shown whenever this build does not match the release listed for its channel.</summary>
    public static bool ShouldShowBanner(ReleaseTag current, ReleaseTag latestOnChannel) => current != latestOnChannel;

    private static string DescribeForLog(string value)
    {
        string singleLine = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
        return singleLine.Length <= 60 ? singleLine : singleLine[..60] + "...";
    }
}

/// <summary>What update-reminder.json remembers between sessions.</summary>
internal sealed class UpdateReminderState
{
    /// <summary>When the user snoozed the banner: it stays hidden until this time.</summary>
    public DateTimeOffset? SnoozedUntilUtc { get; set; }

    /// <summary>The build the banner was snoozed on (for example "v1.3.5-beta"); the snooze only applies to that build.</summary>
    public string? SnoozedOnVersion { get; set; }
}

internal static class UpdateReminderStore
{
    /// <summary>
    /// Reads the saved state. A missing file is the normal empty state; an unreadable one is also treated as empty
    /// (it only ever holds a snooze time) and the reason is returned in <paramref name="problem"/>.
    /// </summary>
    public static UpdateReminderState Load(string filePath, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(filePath))
            {
                return new UpdateReminderState();
            }

            return JsonSerializer.Deserialize<UpdateReminderState>(File.ReadAllText(filePath)) ?? new UpdateReminderState();
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return new UpdateReminderState();
        }
    }

    /// <summary>Writes the state atomically (temporary file + rename). Throws when it cannot be written.</summary>
    public static void Save(string filePath, UpdateReminderState state)
    {
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best effort: the temporary file is normally gone already because it was renamed.
            }
        }
    }
}
