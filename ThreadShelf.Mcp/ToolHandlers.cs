using System.Text.Json;
using System.Text.Json.Serialization;

using ThreadShelf;

internal sealed class ThreadShelfToolHandlers(ThreadShelfCommandService service)
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

    private readonly ThreadShelfCommandService _service = service;

    public object ListThreads(JsonElement arguments) => ToolResponse(_service.ListThreads(
        new ListThreadsRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            Folder = GetStringOrDefault(arguments, "folder", ThreadFilters.All),
            Tag = GetStringOrDefault(arguments, "tag", ""),
            Query = GetStringOrDefault(arguments, "query", ""),
            Archived = GetNullableBool(arguments, "archived"),
            Limit = GetNullableInt(arguments, "limit")
        }));

    public object GetThread(JsonElement arguments) => ToolResponse(_service.GetThread(
        new GetThreadRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId")
        }));

    public object SearchThreads(JsonElement arguments) => ToolResponse(_service.SearchThreads(
        new ListThreadsRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            Query = GetString(arguments, "query"),
            Limit = GetNullableInt(arguments, "limit")
        }));

    public object UpdateThreadMetadata(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.UpdateThreadMetadata(new UpdateThreadMetadataRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId"),
            Folder = GetNullableString(arguments, "folder"),
            Notes = GetNullableString(arguments, "notes"),
            Favorite = GetNullableBool(arguments, "favorite")
        })));

    public object MoveThread(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.MoveThread(new MoveThreadRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId"),
            Folder = GetStringOrDefault(arguments, "folder", "")
        })));

    public object AddThreadTag(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.AddThreadTag(new ThreadTagRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId"),
            Tag = GetString(arguments, "tag")
        })));

    public object RemoveThreadTag(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.RemoveThreadTag(new ThreadTagRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId"),
            Tag = GetString(arguments, "tag")
        })));

    public object BatchUpdateThreads(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.ApplyOrganization(GetOrganizationRequest(arguments))));

    public object ListTags(JsonElement arguments) => ToolResponse(_service.ListTags(
        new ListTagsRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome")
        }));

    public object CreateTag(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.CreateTag(new CreateTagRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            Name = GetString(arguments, "name"),
            Color = GetStringOrDefault(arguments, "color", TagDefinition.DefaultColor),
            Description = GetStringOrDefault(arguments, "description", "")
        })));

    public object UpdateTag(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.UpdateTag(new UpdateTagRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            Name = GetString(arguments, "name"),
            NewName = GetNullableString(arguments, "newName"),
            Color = GetNullableString(arguments, "color"),
            Description = GetNullableString(arguments, "description")
        })));

    public object DeleteTag(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.DeleteTag(new DeleteTagRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            Name = GetString(arguments, "name")
        })));

    public object ArchiveThread(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.ArchiveThread(new NativeThreadRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId")
        })));

    public object UnarchiveThread(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.UnarchiveThread(new NativeThreadRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId")
        })));

    public object RenameThread(JsonElement arguments) => ToolResponse(RequireConfirmed(
        arguments,
        () => _service.RenameThread(new RenameThreadRequest
        {
            CodexHome = GetNullableString(arguments, "codexHome"),
            ThreadId = GetString(arguments, "threadId"),
            Title = GetString(arguments, "title")
        })));

    public object UnknownTool(string name) => ToolResponse(
        ThreadShelfCommandResult<object>.Failure(
            "invalid_argument",
            $"Unknown tool '{name}'."));

    private static ThreadShelfCommandResult<T> RequireConfirmed<T>(
        JsonElement arguments,
        Func<ThreadShelfCommandResult<T>> action) =>
        GetNullableBool(arguments, "confirmed") == true
            ? action()
            : ThreadShelfCommandResult<T>.Failure(
                "confirmation_required",
                "Set confirmed to true to allow this mutation.");

    private static object ToolResponse<T>(ThreadShelfCommandResult<T> result) =>
        new
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

    private static ApplyOrganizationRequest GetOrganizationRequest(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<ApplyOrganizationRequest>(arguments.GetRawText(), JsonOptions)
                ?? new ApplyOrganizationRequest()
            : new ApplyOrganizationRequest();

    private static string GetString(JsonElement element, string propertyName) =>
        GetNullableString(element, propertyName) ?? "";

    private static string GetStringOrDefault(
        JsonElement element,
        string propertyName,
        string defaultValue) =>
        GetNullableString(element, propertyName) ?? defaultValue;

    private static string? GetNullableString(JsonElement element, string propertyName) =>
        element.ValueKind != JsonValueKind.Object
        || !element.TryGetProperty(propertyName, out var property)
        || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ToString();

    private static bool? GetNullableBool(JsonElement element, string propertyName) =>
        element.ValueKind != JsonValueKind.Object
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

    private static int? GetNullableInt(JsonElement element, string propertyName) =>
        element.ValueKind != JsonValueKind.Object
        || !element.TryGetProperty(propertyName, out var property)
            ? null
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
                ? number
                : property.ValueKind == JsonValueKind.String
                  && int.TryParse(property.GetString(), out number)
                    ? number
                    : null;
}
