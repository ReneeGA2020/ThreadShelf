internal sealed class ParsedArgs
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--archived",
        "--codex-home",
        "--color",
        "--description",
        "--favorite",
        "--file",
        "--fields",
        "--folder",
        "--format",
        "--limit",
        "--name",
        "--new-name",
        "--notes",
        "--query",
        "--tag",
        "--title",
        "--workspace",
        "--updated-after",
        "--updated-before",
        "--created-after",
        "--created-before",
        "--exclude-thread-ids"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help",
        "--dry-run",
        "--json",
        "--refresh",
        "--yes",
        "-h"
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = [];

    public static ParsedArgs Parse(string[] args)
    {
        var parsed = new ParsedArgs();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                parsed.Positionals.Add(arg);
                continue;
            }

            var equalIndex = arg.IndexOf('=');
            var name = equalIndex > 0 ? arg[..equalIndex] : arg;
            if (ValueOptions.Contains(name))
            {
                string value;
                if (equalIndex > 0)
                {
                    value = arg[(equalIndex + 1)..];
                }
                else
                {
                    if (index + 1 >= args.Length)
                    {
                        throw new CliUsageException($"Missing value for {name}.");
                    }

                    value = args[++index];
                }

                parsed._options[name] = value;
                continue;
            }

            if (FlagOptions.Contains(name))
            {
                parsed._flags.Add(name);
                continue;
            }

            throw new CliUsageException($"Unknown option '{name}'.");
        }

        return parsed;
    }

    public bool HasFlag(string name) => _flags.Contains(name);

    public string? OptionOrNull(string name) =>
        _options.TryGetValue(name, out var value) ? value : null;

    public string OptionOrDefault(string name, string defaultValue) =>
        OptionOrNull(name) ?? defaultValue;

    public int? OptionalInt(string name)
    {
        var value = OptionOrNull(name);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new CliUsageException($"{name} must be an integer.");
    }

    public bool? OptionalBool(string name)
    {
        var value = OptionOrNull(name);
        if (value is null)
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => throw new CliUsageException($"{name} must be true or false.")
        };
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
