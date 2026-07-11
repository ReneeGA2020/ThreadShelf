using System.Text.Json;

internal sealed record McpToolDescriptor(
    string Name,
    string Description,
    Dictionary<string, object> Properties,
    IReadOnlyList<string> Required,
    Func<JsonElement, object> Handler)
{
    public object CatalogEntry => new
    {
        name = Name,
        description = Description,
        inputSchema = new
        {
            type = "object",
            properties = Properties,
            required = Required
        }
    };
}

internal sealed class ThreadShelfMcpToolRegistry
{
    private readonly ThreadShelfToolHandlers _handlers;
    private readonly IReadOnlyDictionary<string, McpToolDescriptor> _byName;

    public ThreadShelfMcpToolRegistry(ThreadShelfToolHandlers handlers)
    {
        _handlers = handlers;
        Descriptors = ThreadShelfToolCatalog.Create(handlers);
        _byName = Descriptors.ToDictionary(
            descriptor => descriptor.Name,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<McpToolDescriptor> Descriptors { get; }

    public object[] Catalog() => Descriptors
        .Select(descriptor => descriptor.CatalogEntry)
        .ToArray();

    public object Handle(string name, JsonElement arguments) =>
        _byName.TryGetValue(name, out var descriptor)
            ? descriptor.Handler(arguments)
            : _handlers.UnknownTool(name);
}

internal static class ThreadShelfToolCatalog
{
    public static IReadOnlyList<McpToolDescriptor> Create(ThreadShelfToolHandlers handlers)
    {
        var tagDefinitionSchema = ObjectProp(Props(
            ("name", StringProp("Tag name.")),
            ("color", StringProp("Hex color, #RRGGBB.")),
            ("description", StringProp("Tag description."))), ["name", "color"]);
        var threadUpdateSchema = ObjectProp(Props(
            ("threadId", StringProp("Thread id.")),
            ("folder", StringProp("Folder value. Empty clears the folder.")),
            ("tags", ArrayProp("Deprecated alias for setTags; exact global tag names.", StringProp("Global tag name."))),
            ("setTags", ArrayProp("Replace all tags with these global tag names.", StringProp("Global tag name."))),
            ("addTags", ArrayProp("Add tags without replacing existing tags.", StringProp("Global tag name."))),
            ("removeTags", ArrayProp("Remove tags without replacing other tags.", StringProp("Global tag name.")))),
            ["threadId"]);

        return
        [
            Tool(
                "threadshelf_list_threads",
                "List Codex threads with optional folder, tag, query, archive, and limit filters.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("folder", StringProp("Folder filter. Use __all, __favorites, __unfiled, or a folder name.")),
                    ("tag", StringProp("Global tag filter.")),
                    ("query", StringProp("Search query.")),
                    ("archived", BoolProp("Only archived or active threads.")),
                    ("limit", IntProp("Maximum number of threads.")),
                    ("workspace", StringProp("Exact workspace path; case-insensitive with trailing separators ignored.")),
                    ("updatedAfter", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("updatedBefore", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("createdAfter", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("createdBefore", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("excludeThreadIds", ArrayProp("Thread ids to exclude.", StringProp("Thread id."))),
                    ("fields", ArrayProp("Optional compact field projection.", StringProp("Thread field name."))),
                    ("refresh", BoolProp("Force a fresh provider load."))),
                handlers.ListThreads),
            Tool(
                "threadshelf_get_thread",
                "Get one thread by id.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("refresh", BoolProp("Force a fresh provider load."))),
                handlers.GetThread,
                ["threadId"]),
            Tool(
                "threadshelf_search_threads",
                "Search threads across title, id, folder, tags, notes, workspace, source, and model.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("query", StringProp("Search query.")),
                    ("limit", IntProp("Maximum number of threads.")),
                    ("folder", StringProp("Folder filter.")),
                    ("tag", StringProp("Global tag filter.")),
                    ("archived", BoolProp("Only archived or active threads.")),
                    ("workspace", StringProp("Exact workspace path.")),
                    ("updatedAfter", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("updatedBefore", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("createdAfter", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("createdBefore", StringProp("Exclusive ISO 8601 boundary with timezone.")),
                    ("excludeThreadIds", ArrayProp("Thread ids to exclude.", StringProp("Thread id."))),
                    ("fields", ArrayProp("Optional compact field projection.", StringProp("Thread field name."))),
                    ("refresh", BoolProp("Force a fresh provider load."))),
                handlers.SearchThreads,
                ["query"]),
            Tool(
                "threadshelf_update_thread_metadata",
                "Patch ThreadShelf-owned folder, notes, and favorite metadata.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("folder", StringProp("Folder value. Empty clears the folder.")),
                    ("notes", StringProp("Thread notes.")),
                    ("favorite", BoolProp("Favorite flag.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.UpdateThreadMetadata,
                ["threadId", "confirmed"]),
            Tool(
                "threadshelf_move_thread",
                "Set or clear a ThreadShelf folder.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("folder", StringProp("Folder value. Empty clears the folder.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.MoveThread,
                ["threadId", "folder", "confirmed"]),
            Tool(
                "threadshelf_add_thread_tag",
                "Attach a global tag to a thread.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("tag", StringProp("Global tag name.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.AddThreadTag,
                ["threadId", "tag", "confirmed"]),
            Tool(
                "threadshelf_remove_thread_tag",
                "Remove a tag from a thread.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("tag", StringProp("Tag name.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.RemoveThreadTag,
                ["threadId", "tag", "confirmed"]),
            Tool(
                "threadshelf_batch_update_threads",
                "Validate and atomically update folders/tags on many threads, with incremental tags and dry-run preview.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("tags", ArrayProp("Global tag definitions to create or update.", tagDefinitionSchema)),
                    ("threads", ArrayProp("Thread metadata updates. Omitted operations preserve existing values.", threadUpdateSchema)),
                    ("dryRun", BoolProp("Validate and return before/after changes without writing; confirmation is not required.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.BatchUpdateThreads),
            Tool(
                "threadshelf_list_tags",
                "List global tag definitions and usage counts.",
                Props(("codexHome", StringProp("Optional CODEX_HOME path."))),
                handlers.ListTags),
            Tool(
                "threadshelf_create_tag",
                "Create a global tag definition.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("name", StringProp("Tag name.")),
                    ("color", StringProp("Hex color, #RRGGBB.")),
                    ("description", StringProp("Tag description.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.CreateTag,
                ["name", "confirmed"]),
            Tool(
                "threadshelf_update_tag",
                "Rename or edit a global tag definition and migrate thread references.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("name", StringProp("Existing tag name.")),
                    ("newName", StringProp("New tag name.")),
                    ("color", StringProp("Hex color, #RRGGBB.")),
                    ("description", StringProp("Tag description.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.UpdateTag,
                ["name", "confirmed"]),
            Tool(
                "threadshelf_delete_tag",
                "Delete a tag definition and remove thread references.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("name", StringProp("Tag name.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.DeleteTag,
                ["name", "confirmed"]),
            Tool(
                "threadshelf_archive_thread",
                "Archive a thread through Codex app-server when supported.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.ArchiveThread,
                ["threadId", "confirmed"]),
            Tool(
                "threadshelf_unarchive_thread",
                "Unarchive a thread through Codex app-server when supported.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.UnarchiveThread,
                ["threadId", "confirmed"]),
            Tool(
                "threadshelf_rename_thread",
                "Rename a thread through Codex app-server when supported.",
                Props(
                    ("codexHome", StringProp("Optional CODEX_HOME path.")),
                    ("threadId", StringProp("Thread id.")),
                    ("title", StringProp("New Codex title.")),
                    ("confirmed", BoolProp("Must be true for mutations."))),
                handlers.RenameThread,
                ["threadId", "title", "confirmed"])
        ];
    }

    private static McpToolDescriptor Tool(
        string name,
        string description,
        Dictionary<string, object> properties,
        Func<JsonElement, object> handler,
        string[]? required = null) =>
        new(name, description, properties, required ?? [], handler);

    private static Dictionary<string, object> Props(
        params (string Name, object Schema)[] properties) =>
        properties.ToDictionary(property => property.Name, property => property.Schema);

    private static object StringProp(string description) =>
        new { type = "string", description };

    private static object BoolProp(string description) =>
        new { type = "boolean", description };

    private static object IntProp(string description) =>
        new { type = "integer", description };

    private static object ArrayProp(string description, object items) =>
        new { type = "array", description, items };

    private static object ObjectProp(
        Dictionary<string, object> properties,
        string[] required) =>
        new { type = "object", properties, required };
}
