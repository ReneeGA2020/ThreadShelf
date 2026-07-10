using System.Text.Json;
using System.Text.Json.Serialization;

using ThreadShelf;

var server = new ThreadShelfMcpServer();
server.Run();
return 0;

internal sealed class ThreadShelfMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ThreadShelfCommandService _service = new();

    public void Run()
    {
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            object? response;
            try
            {
                using var document = JsonDocument.Parse(line);
                response = Handle(document.RootElement);
            }
            catch (JsonException ex)
            {
                response = RpcError(null, -32700, ex.Message);
            }

            if (response is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
                Console.Out.Flush();
            }
        }
    }

    private object? Handle(JsonElement root)
    {
        var method = GetString(root, "method");
        var hasId = root.TryGetProperty("id", out var idElement);
        var id = hasId ? ReadRpcId(idElement) : null;

        return !hasId && method.StartsWith("notifications/", StringComparison.Ordinal)
            ? null
            : !hasId
            ? null
            : method switch
            {
                "initialize" => RpcResult(
                    id,
                    new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "threadshelf-mcp", version = "0.1.0" }
                    }),
                "tools/list" => RpcResult(id, new { tools = ToolCatalog() }),
                "tools/call" => RpcResult(id, HandleToolCall(GetParams(root))),
                _ => RpcError(id, -32601, $"Unknown method '{method}'.")
            };
    }

    private object HandleToolCall(JsonElement parameters)
    {
        var name = GetString(parameters, "name");
        var arguments = GetObject(parameters, "arguments");

        return name switch
        {
            "threadshelf_list_threads" => ToolResponse(_service.ListThreads(new ListThreadsRequest
            {
                CodexHome = GetNullableString(arguments, "codexHome"),
                Folder = GetStringOrDefault(arguments, "folder", ThreadFilters.All),
                Tag = GetStringOrDefault(arguments, "tag", ""),
                Query = GetStringOrDefault(arguments, "query", ""),
                Archived = GetNullableBool(arguments, "archived"),
                Limit = GetNullableInt(arguments, "limit")
            })),
            "threadshelf_get_thread" => ToolResponse(_service.GetThread(new GetThreadRequest
            {
                CodexHome = GetNullableString(arguments, "codexHome"),
                ThreadId = GetString(arguments, "threadId")
            })),
            "threadshelf_search_threads" => ToolResponse(_service.SearchThreads(new ListThreadsRequest
            {
                CodexHome = GetNullableString(arguments, "codexHome"),
                Query = GetString(arguments, "query"),
                Limit = GetNullableInt(arguments, "limit")
            })),
            "threadshelf_update_thread_metadata" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.UpdateThreadMetadata(new UpdateThreadMetadataRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId"),
                    Folder = GetNullableString(arguments, "folder"),
                    Notes = GetNullableString(arguments, "notes"),
                    Favorite = GetNullableBool(arguments, "favorite")
                }))),
            "threadshelf_move_thread" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.MoveThread(new MoveThreadRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId"),
                    Folder = GetStringOrDefault(arguments, "folder", "")
                }))),
            "threadshelf_add_thread_tag" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.AddThreadTag(new ThreadTagRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId"),
                    Tag = GetString(arguments, "tag")
                }))),
            "threadshelf_remove_thread_tag" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.RemoveThreadTag(new ThreadTagRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId"),
                    Tag = GetString(arguments, "tag")
                }))),
            "threadshelf_batch_update_threads" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.ApplyOrganization(GetOrganizationRequest(arguments)))),
            "threadshelf_list_tags" => ToolResponse(_service.ListTags(new ListTagsRequest
            {
                CodexHome = GetNullableString(arguments, "codexHome")
            })),
            "threadshelf_create_tag" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.CreateTag(new CreateTagRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    Name = GetString(arguments, "name"),
                    Color = GetStringOrDefault(arguments, "color", TagDefinition.DefaultColor),
                    Description = GetStringOrDefault(arguments, "description", "")
                }))),
            "threadshelf_update_tag" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.UpdateTag(new UpdateTagRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    Name = GetString(arguments, "name"),
                    NewName = GetNullableString(arguments, "newName"),
                    Color = GetNullableString(arguments, "color"),
                    Description = GetNullableString(arguments, "description")
                }))),
            "threadshelf_delete_tag" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.DeleteTag(new DeleteTagRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    Name = GetString(arguments, "name")
                }))),
            "threadshelf_archive_thread" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.ArchiveThread(new NativeThreadRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId")
                }))),
            "threadshelf_unarchive_thread" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.UnarchiveThread(new NativeThreadRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId")
                }))),
            "threadshelf_rename_thread" => ToolResponse(RequireConfirmed(
                arguments,
                () => _service.RenameThread(new RenameThreadRequest
                {
                    CodexHome = GetNullableString(arguments, "codexHome"),
                    ThreadId = GetString(arguments, "threadId"),
                    Title = GetString(arguments, "title")
                }))),
            _ => ToolResponse(ThreadShelfCommandResult<object>.Failure(
                "invalid_argument",
                $"Unknown tool '{name}'."))
        };
    }

    private static ThreadShelfCommandResult<T> RequireConfirmed<T>(
        JsonElement arguments,
        Func<ThreadShelfCommandResult<T>> action)
    {
        return GetNullableBool(arguments, "confirmed") == true
            ? action()
            : ThreadShelfCommandResult<T>.Failure(
                "confirmation_required",
                "Set confirmed to true to allow this mutation.");
    }

    private static object ToolResponse<T>(ThreadShelfCommandResult<T> result)
    {
        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(result, PrettyJsonOptions)
                }
            },
            isError = !result.Ok
        };
    }

    private static object[] ToolCatalog()
    {
        var tagDefinitionSchema = ObjectProp(Props(
            ("name", StringProp("Tag name.")),
            ("color", StringProp("Hex color, #RRGGBB.")),
            ("description", StringProp("Tag description."))), ["name", "color"]);
        var threadUpdateSchema = ObjectProp(Props(
            ("threadId", StringProp("Thread id.")),
            ("folder", StringProp("Folder value. Empty clears the folder.")),
            ("tags", ArrayProp("Exact global tag names for the thread.", StringProp("Global tag name.")))),
            ["threadId"]);

        return [
        Tool("threadshelf_list_threads", "List Codex threads with optional folder, tag, query, archive, and limit filters.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("folder", StringProp("Folder filter. Use __all, __favorites, __unfiled, or a folder name.")),
            ("tag", StringProp("Global tag filter.")),
            ("query", StringProp("Search query.")),
            ("archived", BoolProp("Only archived or active threads.")),
            ("limit", IntProp("Maximum number of threads.")))),
        Tool("threadshelf_get_thread", "Get one thread by id.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id."))), ["threadId"]),
        Tool("threadshelf_search_threads", "Search threads across title, id, folder, tags, notes, workspace, source, and model.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("query", StringProp("Search query.")),
            ("limit", IntProp("Maximum number of threads."))), ["query"]),
        Tool("threadshelf_update_thread_metadata", "Patch ThreadShelf-owned folder, notes, and favorite metadata.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("folder", StringProp("Folder value. Empty clears the folder.")),
            ("notes", StringProp("Thread notes.")),
            ("favorite", BoolProp("Favorite flag.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "confirmed"]),
        Tool("threadshelf_move_thread", "Set or clear a ThreadShelf folder.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("folder", StringProp("Folder value. Empty clears the folder.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "folder", "confirmed"]),
        Tool("threadshelf_add_thread_tag", "Attach a global tag to a thread.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("tag", StringProp("Global tag name.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "tag", "confirmed"]),
        Tool("threadshelf_remove_thread_tag", "Remove a tag from a thread.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("tag", StringProp("Tag name.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "tag", "confirmed"]),
        Tool("threadshelf_batch_update_threads", "Atomically upsert tag definitions and set folders/tags on many threads after validating the complete request.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("tags", ArrayProp("Global tag definitions to create or update.", tagDefinitionSchema)),
            ("threads", ArrayProp("Thread metadata updates. Omit folder or tags to preserve that field; an empty value clears it.", threadUpdateSchema)),
            ("confirmed", BoolProp("Must be true for mutations."))), ["confirmed"]),
        Tool("threadshelf_list_tags", "List global tag definitions and usage counts.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")))),
        Tool("threadshelf_create_tag", "Create a global tag definition.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("name", StringProp("Tag name.")),
            ("color", StringProp("Hex color, #RRGGBB.")),
            ("description", StringProp("Tag description.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["name", "confirmed"]),
        Tool("threadshelf_update_tag", "Rename or edit a global tag definition and migrate thread references.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("name", StringProp("Existing tag name.")),
            ("newName", StringProp("New tag name.")),
            ("color", StringProp("Hex color, #RRGGBB.")),
            ("description", StringProp("Tag description.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["name", "confirmed"]),
        Tool("threadshelf_delete_tag", "Delete a tag definition and remove thread references.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("name", StringProp("Tag name.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["name", "confirmed"]),
        Tool("threadshelf_archive_thread", "Archive a thread through Codex app-server when supported.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "confirmed"]),
        Tool("threadshelf_unarchive_thread", "Unarchive a thread through Codex app-server when supported.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "confirmed"]),
        Tool("threadshelf_rename_thread", "Rename a thread through Codex app-server when supported.", Props(
            ("codexHome", StringProp("Optional CODEX_HOME path.")),
            ("threadId", StringProp("Thread id.")),
            ("title", StringProp("New Codex title.")),
            ("confirmed", BoolProp("Must be true for mutations."))), ["threadId", "title", "confirmed"])
    ];
    }

    private static object Tool(
        string name,
        string description,
        Dictionary<string, object> properties,
        string[]? required = null)
    {
        return new
        {
            name,
            description,
            inputSchema = new
            {
                type = "object",
                properties,
                required = required ?? []
            }
        };
    }

    private static Dictionary<string, object> Props(params (string Name, object Schema)[] properties)
    {
        return properties.ToDictionary(property => property.Name, property => property.Schema);
    }

    private static object StringProp(string description)
    {
        return new { type = "string", description };
    }

    private static object BoolProp(string description)
    {
        return new { type = "boolean", description };
    }

    private static object IntProp(string description)
    {
        return new { type = "integer", description };
    }

    private static object ArrayProp(string description, object items)
    {
        return new { type = "array", description, items };
    }

    private static object ObjectProp(Dictionary<string, object> properties, string[] required)
    {
        return new { type = "object", properties, required };
    }

    private static ApplyOrganizationRequest GetOrganizationRequest(JsonElement arguments)
    {
        return arguments.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<ApplyOrganizationRequest>(arguments.GetRawText(), JsonOptions)
                ?? new ApplyOrganizationRequest()
            : new ApplyOrganizationRequest();
    }

    private static JsonElement GetParams(JsonElement root)
    {
        return GetObject(root, "params");
    }

    private static JsonElement GetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Object
                ? property
                : default;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return GetNullableString(element, propertyName) ?? "";
    }

    private static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue)
    {
        return GetNullableString(element, propertyName) ?? defaultValue;
    }

    private static string? GetNullableString(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static bool? GetNullableBool(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            ? null
            : property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => null
            }
            : null;
    }

    private static int? GetNullableInt(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            ? null
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number
            : property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out number)
                ? number
                : null;
    }

    private static object? ReadRpcId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number when id.TryGetInt64(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => id.Clone()
        };
    }

    private static object RpcResult(object? id, object result)
    {
        return new { jsonrpc = "2.0", id, result };
    }

    private static object RpcError(object? id, int code, string message)
    {
        return new { jsonrpc = "2.0", id, error = new { code, message } };
    }
}
