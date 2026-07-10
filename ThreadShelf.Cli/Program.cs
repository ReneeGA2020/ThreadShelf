using System.Text.Json;
using System.Text.Json.Serialization;
using ThreadShelf;

return ThreadShelfCli.Run(args);

internal static class ThreadShelfCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int Run(string[] args)
    {
        ParsedArgs parsed;
        try
        {
            parsed = ParsedArgs.Parse(args);
        }
        catch (CliUsageException ex)
        {
            return Print(
                ThreadShelfCommandResult<object>.Failure("invalid_argument", ex.Message),
                args.Contains("--json", StringComparer.OrdinalIgnoreCase));
        }

        if (parsed.HasFlag("--help") || parsed.HasFlag("-h") || parsed.Positionals.Count == 0)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return Dispatch(parsed);
        }
        catch (CliUsageException ex)
        {
            return Print(
                ThreadShelfCommandResult<object>.Failure("invalid_argument", ex.Message),
                parsed.HasFlag("--json"));
        }
    }

    private static int Dispatch(ParsedArgs parsed)
    {
        var service = new ThreadShelfCommandService();
        var area = parsed.Positionals[0].ToLowerInvariant();

        return area switch
        {
            "threads" => DispatchThreads(service, parsed),
            "tags" => DispatchTags(service, parsed),
            "native" => DispatchNative(service, parsed),
            _ => Print(
                ThreadShelfCommandResult<object>.Failure(
                    "invalid_argument",
                    $"Unknown command area '{parsed.Positionals[0]}'."),
                parsed.HasFlag("--json"))
        };
    }

    private static int DispatchThreads(ThreadShelfCommandService service, ParsedArgs parsed)
    {
        var action = RequirePosition(parsed, 1, "threads action").ToLowerInvariant();
        var json = parsed.HasFlag("--json");
        var codexHome = parsed.OptionOrNull("--codex-home");

        return action switch
        {
            "list" => Print(
                service.ListThreads(new ListThreadsRequest
                {
                    CodexHome = codexHome,
                    Folder = parsed.OptionOrDefault("--folder", ThreadFilters.All),
                    Tag = parsed.OptionOrDefault("--tag", ""),
                    Query = parsed.OptionOrDefault("--query", ""),
                    Archived = parsed.OptionalBool("--archived"),
                    Limit = parsed.OptionalInt("--limit")
                }),
                json),
            "get" => Print(
                service.GetThread(new GetThreadRequest
                {
                    CodexHome = codexHome,
                    ThreadId = RequirePosition(parsed, 2, "thread id")
                }),
                json),
            "search" => Print(
                service.SearchThreads(new ListThreadsRequest
                {
                    CodexHome = codexHome,
                    Query = QueryText(parsed, 2)
                }),
                json),
            "update" => Print(
                RequireYes(parsed, () => service.UpdateThreadMetadata(new UpdateThreadMetadataRequest
                {
                    CodexHome = codexHome,
                    ThreadId = RequirePosition(parsed, 2, "thread id"),
                    Folder = parsed.OptionOrNull("--folder"),
                    Notes = parsed.OptionOrNull("--notes"),
                    Favorite = parsed.OptionalBool("--favorite")
                })),
                json),
            "move" => Print(
                RequireYes(parsed, () => service.MoveThread(new MoveThreadRequest
                {
                    CodexHome = codexHome,
                    ThreadId = RequirePosition(parsed, 2, "thread id"),
                    Folder = parsed.OptionOrDefault("--folder", "")
                })),
                json),
            "batch-update" => Print(
                RequireYes(parsed, () => service.ApplyOrganization(
                    ReadOrganizationRequest(parsed, codexHome))),
                json),
            "tag" => DispatchThreadTag(service, parsed),
            _ => Print(
                ThreadShelfCommandResult<object>.Failure(
                    "invalid_argument",
                    $"Unknown threads action '{action}'."),
                json)
        };
    }

    private static int DispatchThreadTag(ThreadShelfCommandService service, ParsedArgs parsed)
    {
        var action = RequirePosition(parsed, 2, "thread tag action").ToLowerInvariant();
        var codexHome = parsed.OptionOrNull("--codex-home");
        var request = new ThreadTagRequest
        {
            CodexHome = codexHome,
            ThreadId = RequirePosition(parsed, 3, "thread id"),
            Tag = RequirePosition(parsed, 4, "tag name")
        };

        return action switch
        {
            "add" => Print(RequireYes(parsed, () => service.AddThreadTag(request)), parsed.HasFlag("--json")),
            "remove" => Print(RequireYes(parsed, () => service.RemoveThreadTag(request)), parsed.HasFlag("--json")),
            _ => Print(
                ThreadShelfCommandResult<object>.Failure(
                    "invalid_argument",
                    $"Unknown thread tag action '{action}'."),
                parsed.HasFlag("--json"))
        };
    }

    private static int DispatchTags(ThreadShelfCommandService service, ParsedArgs parsed)
    {
        var action = RequirePosition(parsed, 1, "tags action").ToLowerInvariant();
        var json = parsed.HasFlag("--json");
        var codexHome = parsed.OptionOrNull("--codex-home");

        return action switch
        {
            "list" => Print(
                service.ListTags(new ListTagsRequest { CodexHome = codexHome }),
                json),
            "create" => Print(
                RequireYes(parsed, () => service.CreateTag(new CreateTagRequest
                {
                    CodexHome = codexHome,
                    Name = RequiredOption(parsed, "--name"),
                    Color = parsed.OptionOrDefault("--color", TagDefinition.DefaultColor),
                    Description = parsed.OptionOrDefault("--description", "")
                })),
                json),
            "update" => Print(
                RequireYes(parsed, () => service.UpdateTag(new UpdateTagRequest
                {
                    CodexHome = codexHome,
                    Name = RequirePosition(parsed, 2, "tag name"),
                    NewName = parsed.OptionOrNull("--new-name") ?? parsed.OptionOrNull("--name"),
                    Color = parsed.OptionOrNull("--color"),
                    Description = parsed.OptionOrNull("--description")
                })),
                json),
            "delete" => Print(
                RequireYes(parsed, () => service.DeleteTag(new DeleteTagRequest
                {
                    CodexHome = codexHome,
                    Name = RequirePosition(parsed, 2, "tag name")
                })),
                json),
            _ => Print(
                ThreadShelfCommandResult<object>.Failure(
                    "invalid_argument",
                    $"Unknown tags action '{action}'."),
                json)
        };
    }

    private static int DispatchNative(ThreadShelfCommandService service, ParsedArgs parsed)
    {
        var action = RequirePosition(parsed, 1, "native action").ToLowerInvariant();
        var json = parsed.HasFlag("--json");
        var codexHome = parsed.OptionOrNull("--codex-home");

        return action switch
        {
            "archive" => Print(
                RequireYes(parsed, () => service.ArchiveThread(new NativeThreadRequest
                {
                    CodexHome = codexHome,
                    ThreadId = RequirePosition(parsed, 2, "thread id")
                })),
                json),
            "unarchive" => Print(
                RequireYes(parsed, () => service.UnarchiveThread(new NativeThreadRequest
                {
                    CodexHome = codexHome,
                    ThreadId = RequirePosition(parsed, 2, "thread id")
                })),
                json),
            "rename" => Print(
                RequireYes(parsed, () => service.RenameThread(new RenameThreadRequest
                {
                    CodexHome = codexHome,
                    ThreadId = RequirePosition(parsed, 2, "thread id"),
                    Title = RequiredOption(parsed, "--title")
                })),
                json),
            _ => Print(
                ThreadShelfCommandResult<object>.Failure(
                    "invalid_argument",
                    $"Unknown native action '{action}'."),
                json)
        };
    }

    private static ThreadShelfCommandResult<T> RequireYes<T>(
        ParsedArgs parsed,
        Func<ThreadShelfCommandResult<T>> action) =>
        parsed.HasFlag("--yes")
            ? action()
            : ThreadShelfCommandResult<T>.Failure(
                "confirmation_required",
                "Pass --yes to confirm this mutation.");

    private static int Print<T>(ThreadShelfCommandResult<T> result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }
        else if (result.Ok)
        {
            Console.WriteLine(JsonSerializer.Serialize(result.Data, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"{result.Error?.Code}: {result.Error?.Message}");
        }

        return result.Ok ? 0 : 2;
    }

    private static string RequirePosition(ParsedArgs parsed, int index, string label)
    {
        if (parsed.Positionals.Count <= index || string.IsNullOrWhiteSpace(parsed.Positionals[index]))
        {
            throw new CliUsageException($"Missing {label}.");
        }

        return parsed.Positionals[index];
    }

    private static string RequiredOption(ParsedArgs parsed, string name)
    {
        var value = parsed.OptionOrNull(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException($"Missing {name}.");
        }

        return value;
    }

    private static string QueryText(ParsedArgs parsed, int startIndex)
    {
        var query = parsed.OptionOrNull("--query")
            ?? string.Join(" ", parsed.Positionals.Skip(startIndex));
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new CliUsageException("Missing search query.");
        }

        return query;
    }

    private static ApplyOrganizationRequest ReadOrganizationRequest(
        ParsedArgs parsed,
        string? codexHome)
    {
        var path = RequiredOption(parsed, "--file");
        try
        {
            var json = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
            var request = JsonSerializer.Deserialize<ApplyOrganizationRequest>(json, JsonOptions)
                ?? throw new CliUsageException("Organization JSON cannot be empty.");
            return request with { CodexHome = codexHome ?? request.CodexHome };
        }
        catch (JsonException ex)
        {
            throw new CliUsageException($"Invalid organization JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new CliUsageException($"Cannot read organization JSON: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CliUsageException($"Cannot read organization JSON: {ex.Message}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        ThreadShelf CLI

        Global options:
          --codex-home <path>  Use a specific CODEX_HOME
          --json               Emit stable JSON envelope
          --yes                Confirm mutations

        Commands:
          threadshelf threads list [--folder <name>] [--tag <name>] [--query <text>] [--limit <n>] [--archived true|false]
          threadshelf threads get <id>
          threadshelf threads search <query>
          threadshelf threads update <id> [--folder <name>] [--notes <text>] [--favorite true|false] --yes
          threadshelf threads move <id> --folder <name> --yes
          threadshelf threads batch-update --file <path|-> --yes
          threadshelf threads tag add <id> <tag> --yes
          threadshelf threads tag remove <id> <tag> --yes
          threadshelf tags list
          threadshelf tags create --name <name> [--color #RRGGBB] [--description <text>] --yes
          threadshelf tags update <name> [--new-name <name>] [--color #RRGGBB] [--description <text>] --yes
          threadshelf tags delete <name> --yes
          threadshelf native archive <id> --yes
          threadshelf native unarchive <id> --yes
          threadshelf native rename <id> --title <title> --yes
        """);
    }
}

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
        "--folder",
        "--limit",
        "--name",
        "--new-name",
        "--notes",
        "--query",
        "--tag",
        "--title"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help",
        "--json",
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
