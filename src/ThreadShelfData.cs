using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ThreadShelf;

internal sealed record ThreadMetadata
{
    public string Folder { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public string Notes { get; init; } = "";
    public bool Favorite { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record TagDefinition
{
    public const string DefaultColor = "#0969DA";

    public string Name { get; init; } = "";
    public string Color { get; init; } = DefaultColor;
    public string Description { get; init; } = "";
}

internal sealed record CodexThread
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string SourcePath { get; init; } = "";
    public bool IsArchived { get; init; }
    public long FileSizeBytes { get; init; }
    public string Workspace { get; init; } = "";
    public string Originator { get; init; } = "";
    public string Source { get; init; } = "";
    public string Model { get; init; } = "";
    public ThreadMetadata Metadata { get; init; } = new();

    public string DisplayFolder => string.IsNullOrWhiteSpace(Metadata.Folder) ? "Unfiled" : Metadata.Folder.Trim();
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(Untitled thread)" : Title.Trim();
    public string UpdatedLocal => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string TagsText => Metadata.Tags.Count == 0 ? "" : string.Join(", ", Metadata.Tags);
}

internal sealed record ThreadShelfSnapshot
{
    public IReadOnlyList<CodexThread> Threads { get; init; } = [];
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
    public string CodexHome { get; init; } = "";
    public string SidecarPath { get; init; } = "";
    public string DataSource { get; init; } = "";
    public string LoadWarning { get; init; } = "";
    public bool SupportsNativeActions { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.Now;

    public ThreadShelfSnapshot WithMetadata(string threadId, ThreadMetadata metadata) =>
        this with
        {
            Threads = Threads
                .Select(thread => thread.Id.Equals(threadId, StringComparison.OrdinalIgnoreCase)
                    ? thread with { Metadata = metadata }
                    : thread)
                .ToList(),
            LoadedAt = DateTimeOffset.Now
        };

    public ThreadShelfSnapshot WithTags(IReadOnlyList<TagDefinition> tags) =>
        this with
        {
            Tags = tags
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            LoadedAt = DateTimeOffset.Now
        };
}

internal sealed record FolderSummary(string Name, int Count);
internal sealed record TagSummary(TagDefinition Definition, int Count);

internal sealed record EditDraft(string Folder, string Notes, bool Favorite)
{
    public static EditDraft From(ThreadMetadata metadata) =>
        new(metadata.Folder, metadata.Notes, metadata.Favorite);
}

internal static class ThreadFilters
{
    public const string All = "__all";
    public const string Favorites = "__favorites";
    public const string Unfiled = "__unfiled";

    public static IReadOnlyList<CodexThread> Apply(
        IReadOnlyList<CodexThread> threads,
        string selectedFolder,
        string query,
        string selectedTag)
    {
        var terms = (query ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return threads
            .Where(thread => MatchesFolder(thread, selectedFolder))
            .Where(thread => MatchesTag(thread, selectedTag))
            .Where(thread => terms.All(term => MatchesTerm(thread, term)))
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToList();
    }

    public static IReadOnlyList<FolderSummary> BuildFolderSummaries(IReadOnlyList<CodexThread> threads) =>
        threads
            .Where(thread => !string.IsNullOrWhiteSpace(thread.Metadata.Folder))
            .GroupBy(thread => thread.Metadata.Folder.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new FolderSummary(group.Key, group.Count()))
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static IReadOnlyList<TagSummary> BuildTagSummaries(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyList<TagDefinition> tags)
    {
        var counts = threads
            .SelectMany(thread => thread.Metadata.Tags)
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return tags
            .Select(tag => new TagSummary(tag, counts.GetValueOrDefault(tag.Name)))
            .OrderBy(summary => summary.Definition.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string LabelForFilter(string filter) =>
        filter switch
        {
            All => "All",
            Favorites => "Favorites",
            Unfiled => "Unfiled",
            _ => filter
        };

    private static bool MatchesFolder(CodexThread thread, string selectedFolder) =>
        selectedFolder switch
        {
            All => true,
            Favorites => thread.Metadata.Favorite,
            Unfiled => string.IsNullOrWhiteSpace(thread.Metadata.Folder),
            _ => thread.DisplayFolder.Equals(selectedFolder, StringComparison.OrdinalIgnoreCase)
        };

    private static bool MatchesTag(CodexThread thread, string selectedTag) =>
        string.IsNullOrWhiteSpace(selectedTag)
        || thread.Metadata.Tags.Any(tag => tag.Equals(selectedTag.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool MatchesTerm(CodexThread thread, string term)
    {
        var fields = new[]
        {
            thread.DisplayTitle,
            thread.Id,
            thread.DisplayFolder,
            thread.TagsText,
            thread.Metadata.Notes,
            thread.Workspace,
            thread.Originator,
            thread.Source,
            thread.Model,
            thread.IsArchived ? "archived" : "active"
        };

        return fields.Any(field => field?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true);
    }
}

internal sealed class ThreadShelfRepository
{
    private static readonly Regex SessionIdRegex = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagColorRegex = new(
        "^#?[0-9a-f]{6}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string CodexHome { get; private set; }
    public string SessionIndexPath => Path.Combine(CodexHome, "session_index.jsonl");
    public string SidecarPath => Path.Combine(CodexHome, "threadshelf", "threadshelf.json");

    public ThreadShelfRepository(string? codexHome = null)
    {
        CodexHome = codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public ThreadShelfSnapshot Load()
    {
        try
        {
            return LoadFromAppServer();
        }
        catch (Exception ex)
        {
            return LoadFromLocalFiles($"Codex CLI app-server unavailable, using local JSONL files: {ex.Message}");
        }
    }

    private ThreadShelfSnapshot LoadFromAppServer()
    {
        var appServerIndex = CodexAppServerClient.LoadThreadIndex();
        if (!string.IsNullOrWhiteSpace(appServerIndex.CodexHome))
        {
            CodexHome = appServerIndex.CodexHome;
        }

        var sidecar = LoadSidecar();
        var threads = appServerIndex.Threads
            .Select(thread => thread with
            {
                Metadata = Normalize(sidecar.Threads.GetValueOrDefault(thread.Id) ?? new ThreadMetadata())
            })
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToList();

        return new ThreadShelfSnapshot
        {
            Threads = threads,
            Tags = BuildTagDefinitions(sidecar, threads),
            CodexHome = CodexHome,
            SidecarPath = SidecarPath,
            DataSource = "Codex CLI app-server",
            SupportsNativeActions = true,
            LoadedAt = DateTimeOffset.Now
        };
    }

    private ThreadShelfSnapshot LoadFromLocalFiles(string loadWarning)
    {
        var sidecar = LoadSidecar();
        var index = LoadSessionIndex();
        var files = FindSessionFiles();
        var threads = new List<CodexThread>(Math.Max(index.Count, files.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in index.Values.OrderByDescending(entry => entry.UpdatedAt))
        {
            files.TryGetValue(entry.Id, out var sessionFile);
            var facts = sessionFile is null ? SessionFacts.Empty : ReadSessionFacts(sessionFile.Path);
            var metadata = Normalize(sidecar.Threads.GetValueOrDefault(entry.Id) ?? new ThreadMetadata());

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
                Model = facts.Model,
                Metadata = metadata
            });

            seen.Add(entry.Id);
        }

        foreach (var sessionFile in files.Values.Where(file => !seen.Contains(file.Id)))
        {
            var facts = ReadSessionFacts(sessionFile.Path);
            var metadata = Normalize(sidecar.Threads.GetValueOrDefault(sessionFile.Id) ?? new ThreadMetadata());

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
                Model = facts.Model,
                Metadata = metadata
            });
        }

        return new ThreadShelfSnapshot
        {
            Threads = threads.OrderByDescending(thread => thread.UpdatedAt).ToList(),
            Tags = BuildTagDefinitions(sidecar, threads),
            CodexHome = CodexHome,
            SidecarPath = SidecarPath,
            DataSource = "local JSONL files",
            LoadWarning = loadWarning,
            SupportsNativeActions = false,
            LoadedAt = DateTimeOffset.Now
        };
    }

    public void SaveMetadata(string threadId, ThreadMetadata metadata)
    {
        var document = LoadSidecar();
        document.Threads[threadId] = Normalize(metadata) with { UpdatedAt = DateTimeOffset.UtcNow };
        SaveSidecar(document);
    }

    public void SaveTagDefinition(string editingName, TagDefinition definition)
    {
        var normalized = NormalizeTagDefinition(definition);
        if (normalized.Name.Length == 0)
        {
            throw new InvalidOperationException("Tag name cannot be empty.");
        }

        var oldName = NormalizeTagName(editingName);
        var document = LoadSidecar();

        if (oldName.Length > 0
            && !oldName.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase))
        {
            document.Tags.Remove(oldName);
            foreach (var (threadId, metadata) in document.Threads.ToArray())
            {
                if (!metadata.Tags.Any(tag => tag.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                document.Threads[threadId] = Normalize(metadata with
                {
                    Tags = metadata.Tags
                        .Select(tag => tag.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? normalized.Name : tag)
                        .ToList()
                });
            }
        }

        document.Tags[normalized.Name] = normalized;
        SaveSidecar(document);
    }

    public void SetArchived(string threadId, bool archived) =>
        CodexAppServerClient.SetThreadArchived(threadId, archived);

    public void SetName(string threadId, string name) =>
        CodexAppServerClient.SetThreadName(threadId, name.Trim());

    public static ThreadMetadata MetadataFrom(EditDraft draft, IEnumerable<string> tags) =>
        Normalize(new ThreadMetadata
        {
            Folder = draft.Folder,
            Tags = tags.ToList(),
            Notes = draft.Notes,
            Favorite = draft.Favorite
        });

    public static string NormalizeTagName(string name) =>
        (name ?? "").Trim();

    public static string NormalizeTagColor(string color)
    {
        var trimmed = (color ?? "").Trim();
        if (!TagColorRegex.IsMatch(trimmed))
        {
            return TagDefinition.DefaultColor;
        }

        return (trimmed.StartsWith('#') ? trimmed : $"#{trimmed}").ToUpperInvariant();
    }

    public static bool IsValidTagColor(string color) =>
        TagColorRegex.IsMatch((color ?? "").Trim());

    public static void OpenThreadInCodex(string threadId)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"codex://threads/{Uri.EscapeDataString(threadId)}",
            UseShellExecute = true
        });
    }

    public static void RevealFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private ThreadShelfDocument LoadSidecar()
    {
        if (!File.Exists(SidecarPath))
        {
            return new ThreadShelfDocument();
        }

        try
        {
            using var stream = File.OpenRead(SidecarPath);
            return NormalizeDocument(JsonSerializer.Deserialize<ThreadShelfDocument>(stream, JsonOptions)
                ?? new ThreadShelfDocument());
        }
        catch (JsonException)
        {
            return new ThreadShelfDocument();
        }
        catch (IOException)
        {
            return new ThreadShelfDocument();
        }
    }

    private void SaveSidecar(ThreadShelfDocument document)
    {
        var directory = Path.GetDirectoryName(SidecarPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{SidecarPath}.tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, document, JsonOptions);
        }

        File.Move(tempPath, SidecarPath, overwrite: true);
    }

    private Dictionary<string, SessionIndexEntry> LoadSessionIndex()
    {
        var entries = new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SessionIndexPath))
        {
            return entries;
        }

        foreach (var line in File.ReadLines(SessionIndexPath))
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

        foreach (var line in File.ReadLines(path))
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

    private static string ExtractId(string value)
    {
        var match = SessionIdRegex.Match(value);
        return match.Success ? match.Value : "";
    }

    private static ThreadMetadata Normalize(ThreadMetadata metadata) =>
        metadata with
        {
            Folder = (metadata.Folder ?? "").Trim(),
            Notes = (metadata.Notes ?? "").Trim(),
            Tags = metadata.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(NormalizeTagName)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        };

    private static ThreadShelfDocument NormalizeDocument(ThreadShelfDocument document)
    {
        var normalized = new ThreadShelfDocument
        {
            Version = Math.Max(document.Version, 2)
        };

        foreach (var (threadId, metadata) in document.Threads ?? new Dictionary<string, ThreadMetadata>())
        {
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                normalized.Threads[threadId] = Normalize(metadata ?? new ThreadMetadata());
            }
        }

        foreach (var (key, tag) in document.Tags ?? new Dictionary<string, TagDefinition>())
        {
            var candidate = tag ?? new TagDefinition();
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                candidate = candidate with { Name = key };
            }

            var definition = NormalizeTagDefinition(candidate);
            if (definition.Name.Length > 0)
            {
                normalized.Tags[definition.Name] = definition;
            }
        }

        return normalized;
    }

    private static TagDefinition NormalizeTagDefinition(TagDefinition tag)
    {
        var name = NormalizeTagName(tag.Name);
        return tag with
        {
            Name = name,
            Color = NormalizeTagColor(tag.Color),
            Description = (tag.Description ?? "").Trim()
        };
    }

    private static IReadOnlyList<TagDefinition> BuildTagDefinitions(
        ThreadShelfDocument sidecar,
        IReadOnlyList<CodexThread> threads)
    {
        var tags = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in sidecar.Tags.Values.Select(NormalizeTagDefinition))
        {
            if (tag.Name.Length > 0)
            {
                tags[tag.Name] = tag;
            }
        }

        foreach (var name in threads.SelectMany(thread => thread.Metadata.Tags))
        {
            var normalized = NormalizeTagName(name);
            if (normalized.Length > 0 && !tags.ContainsKey(normalized))
            {
                tags[normalized] = new TagDefinition { Name = normalized };
            }
        }

        return tags.Values
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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
    private sealed record SessionFile(string Id, string Path, bool IsArchived, long Length, DateTimeOffset LastWriteTime);

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

    private sealed class ThreadShelfDocument
    {
        public int Version { get; set; } = 2;
        public Dictionary<string, ThreadMetadata> Threads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TagDefinition> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
