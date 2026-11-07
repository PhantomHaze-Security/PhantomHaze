using System.Globalization;
using System.Text;
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
    private static readonly Regex ValidIdentifierRegex = new("^[a-zA-Z0-9_.-]+$", RegexOptions.Compiled);

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
                    string[] options = args[4]
                        .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    if (options.Length == 0)
                    {
                        throw new PhextParseException(lineNumber, "setting.select requires at least one option in the pipe-delimited options list.");
                    }

                    if (!options.Contains(defaultValue, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new PhextParseException(lineNumber, "setting.select default value must be one of the provided options.");
                    }

                    definition.Settings.Add(new PhantomExtensionSettingDefinition
                    {
                        Key = key,
                        Label = args[2].Trim(),
                        Type = PhantomExtensionSettingType.Select,
                        DefaultValue = defaultValue,
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
                        MatchPattern = args[2].Trim(),
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
                        MatchPattern = args[2].Trim(),
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
                        MatchPattern = args[2].Trim(),
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
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.Ordinal))
        {
            return false;
        }

        throw new PhextParseException(lineNumber, $"'{value}' is not a valid boolean value.");
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

    private static string BuildDefaultExtensionId(string sourceName)
    {
        string candidate = Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"extension-{Guid.NewGuid():N}";
        }

        var normalized = new StringBuilder(candidate.Length);
        foreach (char character in candidate.ToLowerInvariant())
        {
            normalized.Append(character switch
            {
                >= 'a' and <= 'z' => character,
                >= '0' and <= '9' => character,
                '.' or '_' or '-' => character,
                _ => '-',
            });
        }

        string result = normalized.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? $"extension-{Guid.NewGuid():N}" : result;
    }
}

internal static class PhantomExtensionPatternMatcher
{
    private static readonly Dictionary<string, Regex> CachedRegexByPattern = new(StringComparer.Ordinal);
    private static readonly object PatternLock = new();

    public static bool IsMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Regex regex = GetOrCreateRegex(pattern);
        return regex.IsMatch(value);
    }

    private static Regex GetOrCreateRegex(string pattern)
    {
        lock (PatternLock)
        {
            if (CachedRegexByPattern.TryGetValue(pattern, out Regex? existing))
            {
                return existing;
            }

            string escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal);
            var compiled = new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            CachedRegexByPattern[pattern] = compiled;
            return compiled;
        }
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
