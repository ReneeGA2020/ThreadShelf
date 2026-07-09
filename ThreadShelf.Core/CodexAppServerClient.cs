using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ThreadShelf;

public sealed class CodexAppServerClient : IDisposable
{
    private const int DefaultTimeoutMs = 30000;
    private const int PageSize = 250;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Process _process;
    private readonly List<string> _stderrLines = [];
    private int _nextRequestId;

    private CodexAppServerClient(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _stderrLines.Add(args.Data);
            }
        };
        _process.BeginErrorReadLine();
    }

    public static AppServerThreadIndex LoadThreadIndex()
    {
        using var client = Start();
        var codexHome = client.Initialize();
        var active = client.ListThreads(isArchived: false);
        var archived = client.ListThreads(isArchived: true);
        var threads = active
            .Concat(archived)
            .GroupBy(thread => thread.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(thread => thread.IsArchived).First())
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToList();

        return new AppServerThreadIndex(codexHome, threads);
    }

    public static void SetThreadArchived(string threadId, bool archived)
    {
        using var client = Start();
        client.Initialize();
        client.SendRequest(
            archived ? "thread/archive" : "thread/unarchive",
            new Dictionary<string, object?> { ["threadId"] = threadId },
            DefaultTimeoutMs);
    }

    public static void SetThreadName(string threadId, string name)
    {
        using var client = Start();
        client.Initialize();
        client.SendRequest(
            "thread/name/set",
            new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["name"] = name
            },
            DefaultTimeoutMs);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static CodexAppServerClient Start()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCodexCliExecutable(),
            Arguments = "app-server",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Codex CLI app-server.");

        return new CodexAppServerClient(process);
    }

    private static string ResolveCodexCliExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("THREADSHELF_CODEX_CLI");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsCliInstall = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        return File.Exists(windowsCliInstall) ? windowsCliInstall : "codex";
    }

    private string Initialize()
    {
        var result = SendRequest(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "threadshelf",
                    title = "ThreadShelf",
                    version = "0.1.0"
                },
                capabilities = new
                {
                    experimentalApi = true,
                    optOutNotificationMethods = new[]
                    {
                        "thread/started",
                        "thread/statusChanged",
                        "thread/nameUpdated"
                    }
                }
            },
            DefaultTimeoutMs);

        WriteMessage(new { method = "initialized" });
        return GetString(result, "codexHome");
    }

    private IReadOnlyList<CodexThread> ListThreads(bool isArchived)
    {
        var threads = new List<CodexThread>();
        string? cursor = null;

        do
        {
            var parameters = new Dictionary<string, object?>
            {
                ["archived"] = isArchived,
                ["limit"] = PageSize,
                ["sortKey"] = "updated_at",
                ["sortDirection"] = "desc"
            };

            if (!string.IsNullOrWhiteSpace(cursor))
            {
                parameters["cursor"] = cursor;
            }

            var result = SendRequest("thread/list", parameters, DefaultTimeoutMs);
            if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var thread = ParseThread(item, isArchived);
                    if (thread is not null)
                    {
                        threads.Add(thread);
                    }
                }
            }

            cursor = GetNullableString(result, "nextCursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return threads;
    }

    private JsonElement SendRequest(string method, object? parameters, int timeoutMs)
    {
        var id = _nextRequestId++;
        WriteMessage(new { id, method, @params = parameters ?? new { } });
        return ReadResponse(id, timeoutMs);
    }

    private void WriteMessage(object message)
    {
        var line = JsonSerializer.Serialize(message, JsonOptions);
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    private JsonElement ReadResponse(int id, int timeoutMs)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remainingMs = Math.Max(1, (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
            var readTask = _process.StandardOutput.ReadLineAsync();
            if (!readTask.Wait(remainingMs))
            {
                throw new TimeoutException($"Timed out waiting for codex app-server response {id}.{ErrorSuffix()}");
            }

            var line = readTask.Result;
            if (line is null)
            {
                throw new InvalidOperationException($"codex app-server closed stdout before response {id}.{ErrorSuffix()}");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId)
                || !string.Equals(responseId.ToString(), id.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"codex app-server error: {FormatError(error)}{ErrorSuffix()}");
            }

            return root.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }

        throw new TimeoutException($"Timed out waiting for codex app-server response {id}.{ErrorSuffix()}");
    }

    private string ErrorSuffix()
    {
        if (_stderrLines.Count == 0)
        {
            return "";
        }

        var tail = string.Join(" ", _stderrLines.TakeLast(3));
        return $" Last stderr: {tail}";
    }

    private static CodexThread? ParseThread(JsonElement item, bool isArchived)
    {
        var id = GetString(item, "id");
        if (id.Length == 0)
        {
            return null;
        }

        var path = GetString(item, "path");
        var preview = GetString(item, "preview");
        var name = GetString(item, "name");
        var updatedAt = GetUnixDate(item, "updatedAt")
            ?? GetUnixDate(item, "recencyAt")
            ?? DateTimeOffset.UtcNow;

        return new CodexThread
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(name) ? preview : name,
            UpdatedAt = updatedAt,
            SourcePath = path,
            IsArchived = isArchived,
            FileSizeBytes = GetFileSize(path),
            Workspace = GetString(item, "cwd"),
            Originator = "app-server",
            Source = FormatSource(item),
            Model = GetString(item, "modelProvider")
        };
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTimeOffset? GetUnixDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string FormatSource(JsonElement element)
    {
        if (!element.TryGetProperty("source", out var source))
        {
            return "";
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            return source.GetString() ?? "";
        }

        if (source.ValueKind == JsonValueKind.Object)
        {
            if (source.TryGetProperty("custom", out var custom))
            {
                return custom.GetString() ?? "";
            }

            if (source.TryGetProperty("subAgent", out _))
            {
                return "subAgent";
            }
        }

        return source.ToString();
    }

    private static string GetString(JsonElement element, string propertyName) =>
        GetNullableString(element, propertyName) ?? "";

    private static string? GetNullableString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static string FormatError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? error.ToString();
        }

        return error.ToString();
    }
}

public sealed record AppServerThreadIndex(string CodexHome, IReadOnlyList<CodexThread> Threads);
