using System.Text.Json;
using System.Text.Json.Serialization;

using ThreadShelf;

internal sealed class ThreadShelfMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ThreadShelfMcpToolRegistry _tools;

    public ThreadShelfMcpServer()
        : this(new ThreadShelfCommandService())
    {
    }

    internal ThreadShelfMcpServer(ThreadShelfCommandService service)
    {
        _tools = new ThreadShelfMcpToolRegistry(new ThreadShelfToolHandlers(service));
    }

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
                    "tools/list" => RpcResult(id, new { tools = _tools.Catalog() }),
                    "tools/call" => RpcResult(id, HandleToolCall(GetParams(root))),
                    _ => RpcError(id, -32601, $"Unknown method '{method}'.")
                };
    }

    private object HandleToolCall(JsonElement parameters)
    {
        var name = GetString(parameters, "name");
        var arguments = GetObject(parameters, "arguments");
        return _tools.Handle(name, arguments);
    }

    private static JsonElement GetParams(JsonElement root) => GetObject(root, "params");

    private static JsonElement GetObject(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Object
            ? property
            : default;

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? ""
                : property.ToString()
            : "";

    private static object? ReadRpcId(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number when id.TryGetInt64(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => id.Clone()
        };

    private static object RpcResult(object? id, object result) =>
        new { jsonrpc = "2.0", id, result };

    private static object RpcError(object? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };
}
