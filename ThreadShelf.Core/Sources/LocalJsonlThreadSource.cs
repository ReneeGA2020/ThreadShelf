using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThreadShelf;

internal sealed class LocalJsonlThreadSource(string codexHome) : IThreadSource
{
    private static readonly Regex SessionIdRegex = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string CodexHome { get; } = codexHome;
    private string SessionIndexPath => Path.Combine(CodexHome, "session_index.jsonl");

    public ThreadSourceSnapshot Load()
    {
        try
        {
            return LoadCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ThreadShelfValidationException(
                "local_jsonl_read_failed",
                $"Local Codex JSONL files are unavailable: {ex.Message}",
                new { codexHome = CodexHome },
                retryable: true,
                innerException: ex);
        }
    }

    private ThreadSourceSnapshot LoadCore()
    {
        var index = LoadSessionIndex();
        var files = FindSessionFiles();
        var threads = new List<CodexThread>(Math.Max(index.Count, files.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in index.Values.OrderByDescending(entry => entry.UpdatedAt))
        {
            files.TryGetValue(entry.Id, out var sessionFile);
            var facts = sessionFile is null ? SessionFacts.Empty : ReadSessionFacts(sessionFile.Path);

            threads.Add(new CodexThread
            {
                Id = entry.Id,
                Title = entry.Title,
                UpdatedAt = entry.UpdatedAt,
                SourcePath = sessionFile?.Path ?? "",
                IsArchived = sessionFile?.IsArchived ?? false,
                FileSizeBytes = sessionFile?.Length ?? 0,
                Workspace = facts.Workspace,
                Originator = facts.Originator,
                Source = facts.Source,
                Model = facts.Model
            });

            seen.Add(entry.Id);
        }

        foreach (var sessionFile in files.Values.Where(file => !seen.Contains(file.Id)))
        {
            var facts = ReadSessionFacts(sessionFile.Path);
            threads.Add(new CodexThread
            {
                Id = sessionFile.Id,
                Title = facts.TitleFallback.Length > 0
                    ? facts.TitleFallback
                    : Path.GetFileNameWithoutExtension(sessionFile.Path),
                UpdatedAt = facts.Timestamp ?? sessionFile.LastWriteTime,
                SourcePath = sessionFile.Path,
                IsArchived = sessionFile.IsArchived,
                FileSizeBytes = sessionFile.Length,
                Workspace = facts.Workspace,
                Originator = facts.Originator,
                Source = facts.Source,
                Model = facts.Model
            });
        }

        return new ThreadSourceSnapshot(
            threads.OrderByDescending(thread => thread.UpdatedAt).ToList(),
            CodexHome,
            "local JSONL files",
            SupportsNativeActions: false);
    }

    private Dictionary<string, SessionIndexEntry> LoadSessionIndex()
    {
        var entries = new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SessionIndexPath))
        {
            return entries;
        }

        foreach (var line in ReadSharedLines(SessionIndexPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = GetString(root, "id");
                if (id.Length == 0)
                {
                    continue;
                }

                var updatedAt = TryGetDate(root, "updated_at") ?? DateTimeOffset.MinValue;
                entries[id] = new SessionIndexEntry(
                    id,
                    GetString(root, "thread_name"),
                    updatedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : updatedAt);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return entries;
    }

    private Dictionary<string, SessionFile> FindSessionFiles()
    {
        var files = new Dictionary<string, SessionFile>(StringComparer.OrdinalIgnoreCase);
        AddSessionFiles(Path.Combine(CodexHome, "sessions"), isArchived: false, files);
        AddSessionFiles(Path.Combine(CodexHome, "archived_sessions"), isArchived: true, files);
        return files;
    }

    private static void AddSessionFiles(
        string directory,
        bool isArchived,
        Dictionary<string, SessionFile> files)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
        {
            var id = ExtractId(path);
            if (id.Length == 0)
            {
                continue;
            }

            var info = new FileInfo(path);
            var candidate = new SessionFile(
                id,
                path,
                isArchived,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

            if (!files.TryGetValue(id, out var current)
                || current.IsArchived && !candidate.IsArchived
                || current.LastWriteTime < candidate.LastWriteTime)
            {
                files[id] = candidate;
            }
        }
    }

    private static SessionFacts ReadSessionFacts(string path)
    {
        if (!File.Exists(path))
        {
            return SessionFacts.Empty;
        }

        var facts = SessionFacts.Empty;
        var lineCount = 0;

        foreach (var line in ReadSharedLines(path))
        {
            if (++lineCount > 80 || string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = GetString(root, "type");
                if (type == "session_meta")
                {
                    facts = facts with
                    {
                        Workspace = FirstNonEmpty(GetString(payload, "cwd"), facts.Workspace),
                        Originator = FirstNonEmpty(GetString(payload, "originator"), facts.Originator),
                        Source = FirstNonEmpty(GetString(payload, "source"), facts.Source),
                        Timestamp = TryGetDate(payload, "timestamp") ?? facts.Timestamp
                    };
                }
                else if (type == "turn_context")
                {
                    facts = facts with
                    {
                        Workspace = FirstNonEmpty(GetString(payload, "cwd"), facts.Workspace),
                        Model = FirstNonEmpty(GetString(payload, "model"), facts.Model)
                    };
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return facts;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string ExtractId(string value)
    {
        var match = SessionIdRegex.Match(value);
        return match.Success ? match.Value : "";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
    }

    private static DateTimeOffset? TryGetDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            property.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;
    }

    private static string FirstNonEmpty(string candidate, string fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;

    private sealed record SessionIndexEntry(string Id, string Title, DateTimeOffset UpdatedAt);
    private sealed record SessionFile(
        string Id,
        string Path,
        bool IsArchived,
        long Length,
        DateTimeOffset LastWriteTime);

    private sealed record SessionFacts(
        string Workspace,
        string Originator,
        string Source,
        string Model,
        string TitleFallback,
        DateTimeOffset? Timestamp)
    {
        public static SessionFacts Empty { get; } = new("", "", "", "", "", null);
    }
}
