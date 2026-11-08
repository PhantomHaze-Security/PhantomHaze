using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomHaze;

internal enum PhantomExtensionSettingType
{
    Toggle,
    Text,
    Number,
    Select,
}

internal enum PhantomExtensionScriptTiming
{
    DocumentStart,
    DocumentEnd,
}

internal enum PhantomExtensionSearchEngine
{
    Google,
    Bing,
    Yahoo,
    DuckDuckGo,
    Startpage,
}

internal sealed class PhantomExtensionSettingDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public PhantomExtensionSettingType Type { get; init; }
    public string DefaultValue { get; init; } = string.Empty;
    public int? Minimum { get; init; }
    public int? Maximum { get; init; }
    public IReadOnlyList<string> SelectOptions { get; init; } = Array.Empty<string>();
}

internal sealed class PhantomExtensionAppButtonDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

internal sealed class PhantomExtensionWebScriptDefinition
{
    public string Id { get; init; } = string.Empty;
    public string MatchPattern { get; init; } = "*";
    public PhantomExtensionScriptTiming Timing { get; init; }
    public bool IsCss { get; init; }
    public string Content { get; init; } = string.Empty;
}

internal sealed class PhantomExtensionWebBlockDefinition
{
    public string Id { get; init; } = string.Empty;
    public string MatchPattern { get; init; } = "*";
}

internal sealed class PhantomExtensionUiDefinition
{
    public string Title { get; set; } = string.Empty;
    public int Width { get; set; } = 760;
    public int Height { get; set; } = 520;
    public string Html { get; set; } = string.Empty;

    public bool HasUi => !string.IsNullOrWhiteSpace(Html);
}

internal sealed class PhantomExtensionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string IconToken { get; set; } = "shield";
    public string StartPageOverride { get; set; } = string.Empty;
    public PhantomExtensionSearchEngine? SearchEngineOverride { get; set; }
    public PhantomExtensionUiDefinition Ui { get; } = new();
    public List<string> StartupLogMessages { get; } = new();
    public List<PhantomExtensionSettingDefinition> Settings { get; } = new();
    public List<PhantomExtensionAppButtonDefinition> AppButtons { get; } = new();
    public List<PhantomExtensionWebScriptDefinition> WebScripts { get; } = new();
    public List<PhantomExtensionWebBlockDefinition> WebBlocks { get; } = new();
}

internal sealed class PhextParseException : Exception
{
    public PhextParseException(int lineNumber, string message)
        : base($"Line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}

internal static class PhextParser
{
    // \z (not $) so a value ending in an escaped newline such as "abc\n" is rejected; $ also matches before a final '\n'.
    private static readonly Regex ValidIdentifierRegex = new("^[a-zA-Z0-9_.-]+\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PhantomExtensionDefinition Parse(string source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);

        string normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalizedSource.Split('\n');
        var definition = new PhantomExtensionDefinition();

        var seenSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenButtons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith('#') ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int lineNumber = i + 1;
            List<string> args = Tokenize(trimmed, lineNumber);
            if (args.Count == 0)
            {
                continue;
            }

            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "meta.name":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Name = args[1].Trim();
                    break;
                case "meta.id":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Id = ValidateIdentifier(args[1], lineNumber, "extension id");
                    break;
                case "meta.version":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Version = args[1].Trim();
                    break;
                case "meta.description":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Description = args[1].Trim();
                    break;
                case "meta.icon":
                    RequireArgCount(args, 2, lineNumber);
                    definition.IconToken = args[1].Trim();
                    break;
                case "setting.toggle":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    bool defaultValue = ParseBoolean(args[3], lineNumber);
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Toggle,
                        DefaultValue = defaultValue ? "true" : "false",
                    });
                    break;
                }
                case "setting.text":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Text,
                        DefaultValue = args[3],
                    });
                    break;
                }
                case "setting.number":
                {
                    RequireArgCount(args, 6, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    int defaultValue = ParseInt(args[3], lineNumber, "default number");
                    int minimum = ParseInt(args[4], lineNumber, "minimum number");
                    int maximum = ParseInt(args[5], lineNumber, "maximum number");
                    if (minimum > maximum)
                    {
                        throw new PhextParseException(lineNumber, "setting.number minimum cannot be greater than maximum.");
                    }

                    if (defaultValue < minimum || defaultValue > maximum)
                    {
                        throw new PhextParseException(lineNumber, "setting.number default must be within the provided minimum/maximum range.");
                    }

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Number,
                        DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
                        Minimum = minimum,
                        Maximum = maximum,
                    });
                    break;
                }
                case "setting.select":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string key = ValidateIdentifier(args[1], lineNumber, "setting key");
                    EnsureUnique(seenSettings, key, lineNumber, "setting key");

                    string defaultValue = args[3].Trim();
                    // Options are matched case-insensitively everywhere, so drop case-insensitive duplicates.
                    string[] options = args[4]
                        .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (options.Length == 0)
                    {
                        throw new PhextParseException(lineNumber, "setting.select requires at least one option in the pipe-delimited options list.");
                    }

                    string? canonicalDefault = options.FirstOrDefault(option =>
                        option.Equals(defaultValue, StringComparison.OrdinalIgnoreCase));
                    if (canonicalDefault is null)
                    {
                        throw new PhextParseException(lineNumber, "setting.select default value must be one of the provided options.");
                    }

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Select,
                        // Store the option's own spelling so the default compares equal to the value scripts receive.
                        DefaultValue = canonicalDefault,
                        SelectOptions = options,
                    });
                    break;
                }
                case "app.button":
                {
                    RequireArgCount(args, 4, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "app button id");
                    EnsureUnique(seenButtons, id, lineNumber, "app button id");

                    definition.AppButtons.Add(new PhantomExtensionAppButtonDefinition
                    {
                        Id = id,
                        Label = args[2].Trim(),
                        Message = args[3],
                    });
                    break;
                }
                case "app.startpage":
                    RequireArgCount(args, 2, lineNumber);
                    definition.StartPageOverride = args[1].Trim();
                    break;
                case "app.searchengine":
                    RequireArgCount(args, 2, lineNumber);
                    definition.SearchEngineOverride = ParseSearchEngine(args[1], lineNumber);
                    break;
                case "app.log":
                    RequireArgCount(args, 2, lineNumber);
                    definition.StartupLogMessages.Add(args[1]);
                    break;
                case "web.script":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "web script id");
                    EnsureUnique(seenScripts, id, lineNumber, "web script id");

                    string terminator = ParseHereDocToken(args[4], lineNumber);
                    string script = ReadBlock(lines, ref i, lineNumber, terminator);
                    if (string.IsNullOrWhiteSpace(script))
                    {
                        throw new PhextParseException(lineNumber, "web.script content block cannot be empty.");
                    }

                    definition.WebScripts.Add(new PhantomExtensionWebScriptDefinition
                    {
                        Id = id,
                        MatchPattern = ParseUrlPattern(args[2], lineNumber),
                        Timing = ParseScriptTiming(args[3], lineNumber),
                        IsCss = false,
                        Content = script,
                    });
                    break;
                }
                case "web.css":
                {
                    RequireArgCount(args, 5, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "web css id");
                    EnsureUnique(seenScripts, id, lineNumber, "web css id");

                    string terminator = ParseHereDocToken(args[4], lineNumber);
                    string css = ReadBlock(lines, ref i, lineNumber, terminator);
                    if (string.IsNullOrWhiteSpace(css))
                    {
                        throw new PhextParseException(lineNumber, "web.css content block cannot be empty.");
                    }

                    definition.WebScripts.Add(new PhantomExtensionWebScriptDefinition
                    {
                        Id = id,
                        MatchPattern = ParseUrlPattern(args[2], lineNumber),
                        Timing = ParseScriptTiming(args[3], lineNumber),
                        IsCss = true,
                        Content = css,
                    });
                    break;
                }
                case "web.block":
                {
                    RequireArgCount(args, 3, lineNumber);
                    string id = ValidateIdentifier(args[1], lineNumber, "web block id");
                    EnsureUnique(seenBlocks, id, lineNumber, "web block id");
                    definition.WebBlocks.Add(new PhantomExtensionWebBlockDefinition
                    {
                        Id = id,
                        MatchPattern = ParseUrlPattern(args[2], lineNumber),
                    });
                    break;
                }
                case "ui.title":
                    RequireArgCount(args, 2, lineNumber);
                    definition.Ui.Title = args[1].Trim();
                    break;
                case "ui.size":
                {
                    RequireArgCount(args, 3, lineNumber);
                    int width = ParseInt(args[1], lineNumber, "UI width");
                    int height = ParseInt(args[2], lineNumber, "UI height");
                    definition.Ui.Width = Math.Clamp(width, 320, 2000);
                    definition.Ui.Height = Math.Clamp(height, 240, 1400);
                    break;
                }
                case "ui.html":
                {
                    RequireArgCount(args, 2, lineNumber);
                    string terminator = ParseHereDocToken(args[1], lineNumber);
                    definition.Ui.Html = ReadBlock(lines, ref i, lineNumber, terminator);
                    break;
                }
                default:
                    throw new PhextParseException(lineNumber, $"Unknown command '{args[0]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            definition.Id = BuildDefaultExtensionId(sourceName);
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            definition.Name = definition.Id;
        }

        if (string.IsNullOrWhiteSpace(definition.Version))
        {
            definition.Version = "1.0.0";
        }

        if (string.IsNullOrWhiteSpace(definition.Ui.Title))
        {
            definition.Ui.Title = $"{definition.Name} UI";
        }

        return definition;
    }

    private static void EnsureUnique(HashSet<string> seenKeys, string key, int lineNumber, string label)
    {
        if (seenKeys.Add(key))
        {
            return;
        }

        throw new PhextParseException(lineNumber, $"Duplicate {label} '{key}'.");
    }

    private static string ValidateIdentifier(string value, int lineNumber, string label)
    {
        if (!ValidIdentifierRegex.IsMatch(value))
        {
            throw new PhextParseException(
                lineNumber,
                $"{label} '{value}' is invalid. Use only letters, numbers, dot, dash, or underscore.");
        }

        return value;
    }

    private static void RequireArgCount(IReadOnlyList<string> args, int expected, int lineNumber)
    {
        if (args.Count == expected)
        {
            return;
        }

        throw new PhextParseException(
            lineNumber,
            $"'{args[0]}' expects {expected - 1} argument(s) but received {args.Count - 1}.");
    }

    private static bool ParseBoolean(string value, int lineNumber)
    {
        if (TryParseBoolean(value, out bool parsed))
        {
            return parsed;
        }

        throw new PhextParseException(lineNumber, $"'{value}' is not a valid boolean value.");
    }

    /// <summary>The boolean grammar shared by .phext files and stored toggle setting values.</summary>
    internal static bool TryParseBoolean(string? value, out bool parsed)
    {
        parsed = false;
        if (value is null)
        {
            return false;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal))
        {
            parsed = true;
            return true;
        }

        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("0", StringComparison.Ordinal);
    }

    /// <summary>The rule for extension ids and keys (letters, digits, '.', '_' and '-'), shared with the app settings.</summary>
    internal static bool IsValidIdentifier(string? value)
    {
        return value is not null && ValidIdentifierRegex.IsMatch(value);
    }

    private static int ParseInt(string value, int lineNumber, string label)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        throw new PhextParseException(lineNumber, $"'{value}' is not a valid integer for {label}.");
    }

    private static PhantomExtensionSearchEngine ParseSearchEngine(string value, int lineNumber)
    {
        return value.ToLowerInvariant() switch
        {
            "google" => PhantomExtensionSearchEngine.Google,
            "bing" => PhantomExtensionSearchEngine.Bing,
            "yahoo" => PhantomExtensionSearchEngine.Yahoo,
            "duckduckgo" => PhantomExtensionSearchEngine.DuckDuckGo,
            "startpage" => PhantomExtensionSearchEngine.Startpage,
            _ => throw new PhextParseException(lineNumber, $"Unknown search engine '{value}'."),
        };
    }

    private static string ParseUrlPattern(string value, int lineNumber)
    {
        // A blank pattern used to match every URL, so a forgotten value in `web.block id ""` blocked all traffic.
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PhextParseException(lineNumber, "URL pattern cannot be empty. Use \"*\" to match every page.");
        }

        // Stored in the form browsers report hosts in, so "https://*.bücher.de/*" matches xn--bcher-kva.de pages.
        return PhantomExtensionPatternMatcher.CanonicalizeUrlPattern(value);
    }

    private static PhantomExtensionScriptTiming ParseScriptTiming(string value, int lineNumber)
    {
        return value.ToLowerInvariant() switch
        {
            "document-start" => PhantomExtensionScriptTiming.DocumentStart,
            "document-end" => PhantomExtensionScriptTiming.DocumentEnd,
            _ => throw new PhextParseException(lineNumber, $"Unknown script timing '{value}'. Use document-start or document-end."),
        };
    }

    private static string ParseHereDocToken(string token, int lineNumber)
    {
        if (!token.StartsWith("<<", StringComparison.Ordinal) || token.Length <= 2)
        {
            throw new PhextParseException(lineNumber, $"Expected heredoc token in the form <<TAG but received '{token}'.");
        }

        return token[2..];
    }

    private static string ReadBlock(string[] lines, ref int index, int commandLineNumber, string terminator)
    {
        var builder = new StringBuilder();
        for (int i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals(terminator, StringComparison.Ordinal))
            {
                index = i;
                return builder.ToString();
            }

            builder.AppendLine(lines[i]);
        }

        throw new PhextParseException(commandLineNumber, $"Unterminated heredoc block. Missing closing token '{terminator}'.");
    }

    private static List<string> Tokenize(string line, int lineNumber)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool escapeNext = false;

        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (escapeNext)
            {
                current.Append(character switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => character,
                });
                escapeNext = false;
                continue;
            }

            if (character == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                if (!inQuotes && current.Length == 0 &&
                    (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
                {
                    tokens.Add(string.Empty);
                }

                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (escapeNext)
        {
            throw new PhextParseException(lineNumber, "Trailing escape character is not allowed.");
        }

        if (inQuotes)
        {
            throw new PhextParseException(lineNumber, "Unterminated quoted string.");
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Id for a file without meta.id, derived from its file name (also used for hand-edited settings entries).</summary>
    internal static string BuildDefaultExtensionId(string sourceName)
    {
        string candidate = Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"extension-{Guid.NewGuid():N}";
        }

        var normalized = new StringBuilder(candidate.Length);
        bool droppedNonAsciiText = false;
        foreach (char character in candidate.ToLowerInvariant())
        {
            switch (character)
            {
                case >= 'a' and <= 'z':
                case >= '0' and <= '9':
                case '.' or '_' or '-':
                    normalized.Append(character);
                    break;
                default:
                    droppedNonAsciiText |= character > 127 && char.IsLetterOrDigit(character);
                    normalized.Append('-');
                    break;
            }
        }

        string result = normalized.ToString().Trim('-');
        if (droppedNonAsciiText)
        {
            // File names in non-Latin scripts used to collapse to the same id ("Плагин v2" and "Расширение v2"
            // both became "v2", so importing one replaced the other) or to a new random id on every import.
            // A short hash of the original name keeps such ids distinct and stable across re-imports.
            result = $"{(result.Length == 0 ? "extension" : result)}-{BuildShortHash(candidate)}";
        }

        return string.IsNullOrWhiteSpace(result) ? $"extension-{Guid.NewGuid():N}" : result;
    }

    private static string BuildShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}

internal static class PhantomExtensionPatternMatcher
{
    private static readonly char[] PathStartCharacters = { '/', '?', '#' };

    /// <summary>True only for the explicit match-everything pattern "*".</summary>
    public static bool MatchesEverything(string? pattern)
    {
        return pattern is not null && pattern.Trim() == "*";
    }

    /// <summary>
    /// Case-insensitive wildcard match ('*' = any run of characters) of a URL against an extension URL pattern.
    /// Patterns written as scheme://host/path (the form used throughout PHEXT_GUIDE.md) are matched part by
    /// part, so a wildcard in the scheme or host can never reach into the rest of the URL: before this,
    /// "https://*.github.com/*" also matched "https://evil.example/?a.github.com/" and ran the extension's
    /// script there. A host written as "*.example.org" matches example.org itself and all of its subdomains.
    /// Other patterns are matched against the whole URL. Matching never uses backtracking regular
    /// expressions, so long URLs cannot freeze the UI thread (request blocking runs for every request).
    /// </summary>
    public static bool IsMatch(string? value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        string glob = pattern.Trim();
        if (glob == "*")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int schemeSeparator = glob.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0 || glob.IndexOfAny(PathStartCharacters, 0, schemeSeparator) >= 0)
        {
            return GlobMatch(value, glob);
        }

        if (!TrySplitUrl(value, out string scheme, out string host, out string port, out string rest))
        {
            return false;
        }

        string remainder = glob[(schemeSeparator + 3)..];
        int pathStart = remainder.IndexOfAny(PathStartCharacters);
        // "example.com." (a fully qualified name with a trailing dot) is the same host as "example.com".
        string hostGlob = TrimTrailingDot(pathStart < 0 ? remainder : remainder[..pathStart]);
        string pathGlob = pathStart < 0 ? "*" : remainder[pathStart..];
        if (!pathGlob.StartsWith('/'))
        {
            pathGlob = "/" + pathGlob;
        }

        // A port in the pattern ("localhost:8080") is compared together with the URL's port; otherwise any port matches.
        bool patternHasPort = hostGlob.StartsWith('[') ? hostGlob.Contains("]:", StringComparison.Ordinal) : hostGlob.Contains(':');
        string hostTarget = patternHasPort && port.Length > 0 ? $"{host}:{port}" : host;
        bool hostMatches = GlobMatch(hostTarget, hostGlob) ||
                           (hostGlob.StartsWith("*.", StringComparison.Ordinal) && GlobMatch(hostTarget, hostGlob[2..]));

        return GlobMatch(scheme, glob[..schemeSeparator]) && hostMatches && GlobMatch(rest, pathGlob);
    }

    /// <summary>
    /// Splits an absolute URL into scheme, host, port and the path/query/fragment remainder (always starting
    /// with '/'). User info is dropped, so "https://trusted.example@evil.example/" has the host evil.example.
    /// </summary>
    private static bool TrySplitUrl(string url, out string scheme, out string host, out string port, out string rest)
    {
        scheme = host = port = rest = string.Empty;
        int separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        scheme = url[..separator];
        int authorityStart = separator + 3;
        int authorityEnd = url.IndexOfAny(PathStartCharacters, authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        string authority = url[authorityStart..authorityEnd];
        rest = authorityEnd < url.Length ? url[authorityEnd..] : "/";
        if (!rest.StartsWith('/'))
        {
            rest = "/" + rest;
        }

        int userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            authority = authority[(userInfoEnd + 1)..];
        }

        host = authority;
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close > 0)
            {
                host = authority[..(close + 1)];
                if (close + 1 < authority.Length && authority[close + 1] == ':')
                {
                    port = authority[(close + 2)..];
                }
            }
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority[..colon];
                port = authority[(colon + 1)..];
            }

            // Browsers keep the trailing dot of a fully qualified host ("ad.tracker.example."), which would
            // otherwise slip past host patterns such as "*.tracker.example".
            host = TrimTrailingDot(host);
        }

        return true;
    }

    private static string TrimTrailingDot(string host)
    {
        return host.Length > 1 && host[^1] == '.' ? host[..^1] : host;
    }

    /// <summary>
    /// Brings the host of a scheme://host/path pattern into the form browsers report hosts in: internationalised
    /// labels are converted to their ASCII ("xn--") form and a trailing dot is dropped. Other patterns are only trimmed.
    /// </summary>
    public static string CanonicalizeUrlPattern(string pattern)
    {
        string glob = pattern.Trim();
        int schemeSeparator = glob.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0 || glob.IndexOfAny(PathStartCharacters, 0, schemeSeparator) >= 0)
        {
            return glob;
        }

        int hostStart = schemeSeparator + 3;
        int hostEnd = glob.IndexOfAny(PathStartCharacters, hostStart);
        if (hostEnd < 0)
        {
            hostEnd = glob.Length;
        }

        string authority = glob[hostStart..hostEnd];
        if (authority.StartsWith('['))
        {
            return glob;
        }

        int colon = authority.LastIndexOf(':');
        string host = TrimTrailingDot(colon >= 0 ? authority[..colon] : authority);
        string port = colon >= 0 ? authority[colon..] : string.Empty;
        if (host.Any(character => character > 127))
        {
            var mapping = new IdnMapping();
            string[] labels = host.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                // Labels containing '*' cannot be converted; they are left exactly as written.
                if (labels[i].Any(character => character > 127) && !labels[i].Contains('*'))
                {
                    try
                    {
                        labels[i] = mapping.GetAscii(labels[i]);
                    }
                    catch (ArgumentException)
                    {
                        // Not a valid internationalised label; keep it as written.
                    }
                }
            }

            host = string.Join('.', labels);
        }

        return string.Concat(glob.AsSpan(0, hostStart), host, port, glob.AsSpan(hostEnd));
    }

    /// <summary>
    /// Anchored, case-insensitive glob match using the iterative two-pointer algorithm (O(n*m) worst case).
    /// </summary>
    private static bool GlobMatch(string text, string glob)
    {
        int textIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int starTextIndex = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < glob.Length &&
                glob[patternIndex] != '*' &&
                char.ToLowerInvariant(glob[patternIndex]) == char.ToLowerInvariant(text[textIndex]))
            {
                textIndex++;
                patternIndex++;
            }
            else if (patternIndex < glob.Length && glob[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starTextIndex = textIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                textIndex = ++starTextIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < glob.Length && glob[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == glob.Length;
    }
}

internal enum PhantomExtensionPlaceholderContext
{
    /// <summary>Values are inserted verbatim (message boxes, log lines, CSS).</summary>
    PlainText,

    /// <summary>Values are escaped so they are safe inside JavaScript string literals ("...", '...' or `...`).</summary>
    JavaScriptString,

    /// <summary>Values are HTML-encoded.</summary>
    Html,
}

internal static class PhantomExtensionPlaceholders
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{\{(?:extension\.(?<meta>id|name|version)|setting\.(?<key>[A-Za-z0-9_.-]+))\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Replaces {{extension.*}} and {{setting.*}} placeholders in a single pass, so values inserted for one
    /// placeholder are never re-scanned for further placeholders. Unknown placeholders are left untouched.
    /// </summary>
    public static string Resolve(
        string? template,
        PhantomExtensionDefinition definition,
        IReadOnlyDictionary<string, string> settingValues,
        PhantomExtensionPlaceholderContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            string? value = null;
            if (match.Groups["meta"].Success)
            {
                value = match.Groups["meta"].Value.ToLowerInvariant() switch
                {
                    "id" => definition.Id,
                    "name" => definition.Name,
                    "version" => definition.Version,
                    _ => null,
                };
            }
            else
            {
                string key = match.Groups["key"].Value;
                PhantomExtensionSettingDefinition? setting = definition.Settings.FirstOrDefault(candidate =>
                    candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (setting is not null)
                {
                    value = settingValues.TryGetValue(setting.Key, out string? stored) && stored is not null
                        ? stored
                        : setting.DefaultValue;
                }
            }

            if (value is null)
            {
                return match.Value;
            }

            return context switch
            {
                PhantomExtensionPlaceholderContext.JavaScriptString => PhantomExtensionScriptBuilder.EscapeJavaScriptStringContent(value),
                PhantomExtensionPlaceholderContext.Html => WebUtility.HtmlEncode(value),
                _ => value,
            };
        });
    }
}

internal static class PhantomExtensionScriptBuilder
{
    private static readonly Regex LegacyGlobalAccessRegex = new(
        @"(?<![\w$.])(?:window|globalThis|self)\s*(?:\?\.|\.)\s*phantomHazeExtensions\b|(?<![\w$.])(?:window|globalThis|self)\s*(?:\?\.)?\s*\[\s*(?:""phantomHazeExtensions""|'phantomHazeExtensions')\s*\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Escapes a value so it can be placed between the quotes of any JavaScript string literal
    /// ("...", '...' or a template literal) without terminating it or starting an interpolation.
    /// </summary>
    public static string EscapeJavaScriptStringContent(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // The default System.Text.Json encoder escapes quotes, apostrophes, backticks, backslashes, control
        // characters, U+2028/U+2029, '<', '>' and '&'. '$' is additionally escaped so "${" cannot open a
        // template-literal substitution.
        string json = JsonSerializer.Serialize(value);
        return json[1..^1].Replace("$", "\\u0024", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension scripts used to read their settings from a page-global window.phantomHazeExtensions object.
    /// That global is no longer created (pages could read or tamper with it); references to it inside an
    /// extension's own script are redirected to the private, closure-scoped equivalent.
    /// </summary>
    public static string RewriteLegacyGlobalAccess(string? script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return string.Empty;
        }

        return LegacyGlobalAccessRegex.Replace(script, "phantomHazeExtensions");
    }

    /// <summary>Completion value of a wrapped web script that ran (ExecuteScriptAsync returns it JSON-encoded).</summary>
    public const string WebScriptRanResult = "phantomhaze:ran";

    /// <summary>Completion value of a wrapped web script that was skipped by its in-page guards.</summary>
    public const string WebScriptSkippedResult = "phantomhaze:skipped";

    private static readonly string SkipWebScriptStatement = $"return {JsonSerializer.Serialize(WebScriptSkippedResult)};";

    /// <summary>
    /// Wraps an extension web script so it runs with private access to its own context
    /// (phantomHazeExtension / phantomHazeExtensions parameters) and only when the document that actually
    /// executes it is fully loaded and still matches the script's URL pattern.
    /// The wrapper evaluates to <see cref="WebScriptRanResult"/> or <see cref="WebScriptSkippedResult"/>, so a
    /// result of "null" from ExecuteScriptAsync means the script could not run at all (usually a syntax error).
    /// </summary>
    public static string BuildWebScript(string contextJson, string? urlPattern, string payload)
    {
        var builder = new StringBuilder(payload.Length + 1024);
        builder.Append("(function (phantomHazeExtension, phantomHazeUrlPattern) {\n");
        AppendUrlGuard(builder, SkipWebScriptStatement);
        // Scripts are injected once a navigation has completed, so the document is "complete". A script that is
        // delivered to the next document instead (the tab navigated while it was being sent) arrives while that
        // document is still loading; it is skipped there, and that document gets its own injection once it completes.
        builder.Append("  if (document.readyState !== \"complete\") {\n");
        builder.Append("    ").Append(SkipWebScriptStatement).Append('\n');
        builder.Append("  }\n");
        builder.Append("  (function (phantomHazeExtension, phantomHazeExtensions) {\n");
        builder.Append("    try {\n");
        builder.Append(payload);
        builder.Append("\n    } catch (error) {\n");
        builder.Append("      console.error(\"PhantomHaze extension script failure\", error);\n");
        builder.Append("    }\n");
        builder.Append("  })(phantomHazeExtension, { [phantomHazeExtension.id]: phantomHazeExtension });\n");
        builder.Append("  return ").Append(JsonSerializer.Serialize(WebScriptRanResult)).Append(";\n");
        builder.Append("})(");
        builder.Append(contextJson);
        builder.Append(", ");
        builder.Append(SerializeUrlPattern(urlPattern));
        builder.Append(");");
        return builder.ToString();
    }

    public static string BuildStyleInjectionScript(string extensionId, string scriptId, string? urlPattern, string css)
    {
        // CSSStyleSheet.replaceSync() silently drops @import rules (it does not throw), so a style sheet that
        // imports others (web fonts, for example) has to keep using a <style> element.
        bool useConstructableSheet = css.IndexOf("@import", StringComparison.OrdinalIgnoreCase) < 0;
        var builder = new StringBuilder(css.Length + 2048);
        builder.Append("(function (phantomHazeUrlPattern) {\n");
        AppendUrlGuard(builder, "return;");
        builder.Append("  var styleId = ").Append(JsonSerializer.Serialize(BuildStyleElementId(extensionId, scriptId))).Append(";\n");
        builder.Append("  var cssText = ").Append(JsonSerializer.Serialize(css)).Append(";\n");
        builder.Append("  var registryKey = typeof Symbol === \"function\" ? Symbol.for(\"phantomHaze.extensionStyles\") : \"__phantomHazeExtensionStyles\";\n");
        if (useConstructableSheet)
        {
            // Constructable stylesheets are not "inline" styles, so a page's Content-Security-Policy (style-src
            // without 'unsafe-inline') cannot silently drop them the way it drops an injected <style> element.
            builder.Append("  try {\n");
            builder.Append("    if (typeof CSSStyleSheet === \"function\" && document.adoptedStyleSheets) {\n");
            builder.Append("      var registry = document[registryKey];\n");
            builder.Append("      if (!registry) {\n");
            builder.Append("        registry = {};\n");
            builder.Append("        Object.defineProperty(document, registryKey, { value: registry });\n");
            builder.Append("      }\n");
            builder.Append("      var sheet = registry[styleId];\n");
            builder.Append("      if (!sheet) {\n");
            builder.Append("        sheet = new CSSStyleSheet();\n");
            builder.Append("        registry[styleId] = sheet;\n");
            builder.Append("      }\n");
            builder.Append("      sheet.replaceSync(cssText);\n");
            builder.Append("      if (document.adoptedStyleSheets.indexOf(sheet) < 0) {\n");
            builder.Append("        document.adoptedStyleSheets = document.adoptedStyleSheets.concat([sheet]);\n");
            builder.Append("      }\n");
            // A <style> element left by an earlier injection of this rule (e.g. before a setting change) is removed.
            builder.Append("      var staleElement = document.getElementById(styleId);\n");
            builder.Append("      if (staleElement && staleElement.parentNode) {\n");
            builder.Append("        staleElement.parentNode.removeChild(staleElement);\n");
            builder.Append("      }\n");
            builder.Append("      return;\n");
            builder.Append("    }\n");
            builder.Append("  } catch (error) {\n");
            builder.Append("    // Fall back to a <style> element below.\n");
            builder.Append("  }\n");
        }

        // <style> element path. A constructable sheet adopted for this rule earlier in the same document is emptied
        // so the two never apply at the same time.
        builder.Append("  try {\n");
        builder.Append("    var adoptedRegistry = document[registryKey];\n");
        builder.Append("    if (adoptedRegistry && adoptedRegistry[styleId]) {\n");
        builder.Append("      adoptedRegistry[styleId].replaceSync(\"\");\n");
        builder.Append("    }\n");
        builder.Append("  } catch (error) {\n");
        builder.Append("  }\n");
        builder.Append("  if (!document.head) {\n    return;\n  }\n");
        builder.Append("  var styleElement = document.getElementById(styleId);\n");
        builder.Append("  if (!styleElement) {\n");
        builder.Append("    styleElement = document.createElement(\"style\");\n");
        builder.Append("    styleElement.id = styleId;\n");
        builder.Append("    document.head.appendChild(styleElement);\n");
        builder.Append("  }\n");
        builder.Append("  styleElement.textContent = cssText;\n");
        builder.Append("})(");
        builder.Append(SerializeUrlPattern(urlPattern));
        builder.Append(");");
        return builder.ToString();
    }

    /// <summary>
    /// Length-prefixed, lower-cased (ids are case-insensitive) DOM id, so distinct extension/script id pairs can
    /// never produce the same element id (e.g. "a-b"+"c" vs "a"+"b-c").
    /// </summary>
    public static string BuildStyleElementId(string extensionId, string scriptId)
    {
        string extension = (extensionId ?? string.Empty).ToLowerInvariant();
        string script = (scriptId ?? string.Empty).ToLowerInvariant();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ph-ext-style-{extension.Length}-{extension}-{script.Length}-{script}");
    }

    private static string SerializeUrlPattern(string? urlPattern)
    {
        return PhantomExtensionPatternMatcher.MatchesEverything(urlPattern)
            ? "null"
            : JsonSerializer.Serialize((urlPattern ?? string.Empty).Trim().ToLowerInvariant());
    }

    // Re-checks, inside the page and at the moment the script runs, that location.href still matches the rule's
    // pattern, using exactly the same rules as PhantomExtensionPatternMatcher.IsMatch (and, like it, without
    // regular expressions). Everything is local to an inner function, so nothing leaks into the extension script.
    private static void AppendUrlGuard(StringBuilder builder, string skipStatement)
    {
        builder.Append("  if (phantomHazeUrlPattern !== null && !(function (href, pattern) {\n");
        builder.Append("    function glob(text, p) {\n");
        builder.Append("      var t = 0, i = 0, star = -1, mark = 0;\n");
        builder.Append("      while (t < text.length) {\n");
        builder.Append("        if (i < p.length && p.charAt(i) !== \"*\" && p.charAt(i) === text.charAt(t)) { t++; i++; }\n");
        builder.Append("        else if (i < p.length && p.charAt(i) === \"*\") { star = i++; mark = t; }\n");
        builder.Append("        else if (star !== -1) { i = star + 1; t = ++mark; }\n");
        builder.Append("        else { return false; }\n");
        builder.Append("      }\n");
        builder.Append("      while (i < p.length && p.charAt(i) === \"*\") { i++; }\n");
        builder.Append("      return i === p.length;\n");
        builder.Append("    }\n");
        builder.Append("    function pathStart(text, from) {\n");
        builder.Append("      for (var k = from; k < text.length; k++) {\n");
        builder.Append("        var c = text.charAt(k);\n");
        builder.Append("        if (c === \"/\" || c === \"?\" || c === \"#\") { return k; }\n");
        builder.Append("      }\n");
        builder.Append("      return -1;\n");
        builder.Append("    }\n");
        builder.Append("    var sep = pattern.indexOf(\"://\");\n");
        builder.Append("    if (sep < 0 || (pathStart(pattern, 0) !== -1 && pathStart(pattern, 0) < sep)) { return glob(href, pattern); }\n");
        builder.Append("    var urlSep = href.indexOf(\"://\");\n");
        builder.Append("    if (urlSep <= 0) { return false; }\n");
        builder.Append("    var authorityEnd = pathStart(href, urlSep + 3);\n");
        builder.Append("    if (authorityEnd < 0) { authorityEnd = href.length; }\n");
        builder.Append("    var authority = href.slice(urlSep + 3, authorityEnd);\n");
        builder.Append("    var rest = authorityEnd < href.length ? href.slice(authorityEnd) : \"/\";\n");
        builder.Append("    if (rest.charAt(0) !== \"/\") { rest = \"/\" + rest; }\n");
        builder.Append("    var at = authority.lastIndexOf(\"@\");\n");
        builder.Append("    if (at >= 0) { authority = authority.slice(at + 1); }\n");
        builder.Append("    var host = authority, port = \"\";\n");
        builder.Append("    if (authority.charAt(0) === \"[\") {\n");
        builder.Append("      var close = authority.indexOf(\"]\");\n");
        builder.Append("      if (close > 0) { host = authority.slice(0, close + 1); if (authority.charAt(close + 1) === \":\") { port = authority.slice(close + 2); } }\n");
        builder.Append("    } else {\n");
        builder.Append("      var colon = authority.lastIndexOf(\":\");\n");
        builder.Append("      if (colon >= 0) { host = authority.slice(0, colon); port = authority.slice(colon + 1); }\n");
        builder.Append("      if (host.length > 1 && host.charAt(host.length - 1) === \".\") { host = host.slice(0, -1); }\n");
        builder.Append("    }\n");
        builder.Append("    var remainder = pattern.slice(sep + 3);\n");
        builder.Append("    var patternPath = pathStart(remainder, 0);\n");
        builder.Append("    var hostGlob = patternPath < 0 ? remainder : remainder.slice(0, patternPath);\n");
        builder.Append("    if (hostGlob.length > 1 && hostGlob.charAt(hostGlob.length - 1) === \".\") { hostGlob = hostGlob.slice(0, -1); }\n");
        builder.Append("    var pathGlob = patternPath < 0 ? \"*\" : remainder.slice(patternPath);\n");
        builder.Append("    if (pathGlob.charAt(0) !== \"/\") { pathGlob = \"/\" + pathGlob; }\n");
        builder.Append("    var hasPort = hostGlob.charAt(0) === \"[\" ? hostGlob.indexOf(\"]:\") >= 0 : hostGlob.indexOf(\":\") >= 0;\n");
        builder.Append("    var hostTarget = hasPort && port.length > 0 ? host + \":\" + port : host;\n");
        builder.Append("    var hostMatches = glob(hostTarget, hostGlob) || (hostGlob.slice(0, 2) === \"*.\" && glob(hostTarget, hostGlob.slice(2)));\n");
        builder.Append("    return glob(href.slice(0, urlSep), pattern.slice(0, sep)) && hostMatches && glob(rest, pathGlob);\n");
        builder.Append("  })(String(location.href).toLowerCase(), phantomHazeUrlPattern)) {\n");
        builder.Append("    ").Append(skipStatement).Append('\n');
        builder.Append("  }\n");
    }
}

internal static class PhantomExtensionIconFactory
{
    public static Image CreateIcon(string? iconToken)
    {
        if (string.IsNullOrWhiteSpace(iconToken))
        {
            return SystemIcons.Shield.ToBitmap();
        }

        string trimmed = iconToken.Trim();

        if (TryCreateImageFromBase64(trimmed, out Image? decoded) && decoded is not null)
        {
            return decoded;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "shield" => SystemIcons.Shield.ToBitmap(),
            "warning" => SystemIcons.Warning.ToBitmap(),
            "info" => SystemIcons.Information.ToBitmap(),
            "error" => SystemIcons.Error.ToBitmap(),
            "question" => SystemIcons.Question.ToBitmap(),
            "security" => SystemIcons.Shield.ToBitmap(),
            "app" => SystemIcons.Application.ToBitmap(),
            _ => SystemIcons.Application.ToBitmap(),
        };
    }

    private static bool TryCreateImageFromBase64(string token, out Image? image)
    {
        image = null;
        string payload = token;
        const string base64Prefix = "base64:";
        const string dataPrefix = "data:image";

        if (token.StartsWith(base64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            payload = token[base64Prefix.Length..];
        }
        else if (token.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            int commaIndex = token.IndexOf(',');
            if (commaIndex < 0 || commaIndex >= token.Length - 1)
            {
                return false;
            }

            payload = token[(commaIndex + 1)..];
        }
        else
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes);
            using Image parsed = Image.FromStream(stream);
            image = new Bitmap(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
