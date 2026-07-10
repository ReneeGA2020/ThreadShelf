using System.Text.Json;

var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")
    ?? throw new InvalidOperationException("CODEX_HOME is required for the fake Codex CLI.");
Directory.CreateDirectory(codexHome);
var workspaceRoot = Environment.GetEnvironmentVariable("THREADSHELF_FAKE_WORKSPACE_ROOT")
    ?? @"C:\ThreadShelf Demo";
var atlasWorkspace = Path.Combine(workspaceRoot, "Atlas 项目 & One");
var researchWorkspace = Path.Combine(workspaceRoot, "研究 Space (Two)");

if (args.Length == 0 || !args[0].Equals("app-server", StringComparison.OrdinalIgnoreCase))
{
    var launchLogPath = Environment.GetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG")
        ?? Path.Combine(codexHome, "fake-interactive-launch.jsonl");
    var launchLogDirectory = Path.GetDirectoryName(launchLogPath);
    if (!string.IsNullOrWhiteSpace(launchLogDirectory))
    {
        Directory.CreateDirectory(launchLogDirectory);
    }

    File.AppendAllText(
        launchLogPath,
        JsonSerializer.Serialize(new
        {
            executable = Environment.ProcessPath,
            arguments = args,
            workingDirectory = Environment.CurrentDirectory
        }) + Environment.NewLine);
    return;
}

var definitions = new[]
{
    new DemoThread(
        "11111111-1111-1111-1111-111111111111",
        "Prepare Atlas release checklist",
        atlasWorkspace,
        1783648800,
        false),
    new DemoThread(
        "22222222-2222-2222-2222-222222222222",
        "Investigate sign-in regression",
        atlasWorkspace,
        1783562400,
        false),
    new DemoThread(
        "33333333-3333-3333-3333-333333333333",
        "Summarize retrieval research",
        researchWorkspace,
        1783476000,
        false),
    new DemoThread(
        "44444444-4444-4444-4444-444444444444",
        "Archived onboarding notes",
        researchWorkspace,
        1783389600,
        true)
};

var statePath = Path.Combine(codexHome, "fake-archive-state.json");
var logPath = Path.Combine(codexHome, "fake-codex.log");
var archiveState = File.Exists(statePath)
    ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(statePath))
        ?? new Dictionary<string, bool>()
    : new Dictionary<string, bool>();
foreach (var thread in definitions)
{
    archiveState.TryAdd(thread.Id, thread.ArchivedByDefault);
}

while (Console.ReadLine() is { } line)
{
    using var message = JsonDocument.Parse(line);
    var root = message.RootElement;
    if (!root.TryGetProperty("id", out var idElement)
        || !root.TryGetProperty("method", out var methodElement))
    {
        continue;
    }

    var id = idElement.GetInt32();
    var method = methodElement.GetString() ?? "";
    File.AppendAllText(logPath, method + Environment.NewLine);
    object result = method switch
    {
        "initialize" => new { codexHome },
        "thread/list" => ListThreads(root.GetProperty("params").GetProperty("archived").GetBoolean()),
        "thread/archive" => SetArchived(root.GetProperty("params").GetProperty("threadId").GetString() ?? "", true),
        "thread/unarchive" => SetArchived(root.GetProperty("params").GetProperty("threadId").GetString() ?? "", false),
        "thread/name/set" => new { },
        _ => new { }
    };
    Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
}

object ListThreads(bool archived)
{
    var data = definitions
        .Where(thread => archiveState.GetValueOrDefault(thread.Id) == archived)
        .Select(thread => new
        {
            id = thread.Id,
            name = thread.Title,
            preview = thread.Title,
            updatedAt = thread.UpdatedAt,
            cwd = thread.Workspace,
            path = "",
            modelProvider = "fake-demo",
            source = new { custom = "threadshelf-demo" }
        })
        .ToArray();
    return new { data, nextCursor = (string?)null };
}

object SetArchived(string threadId, bool archived)
{
    archiveState[threadId] = archived;
    File.WriteAllText(statePath, JsonSerializer.Serialize(archiveState));
    return new { };
}

internal sealed record DemoThread(
    string Id,
    string Title,
    string Workspace,
    long UpdatedAt,
    bool ArchivedByDefault);
