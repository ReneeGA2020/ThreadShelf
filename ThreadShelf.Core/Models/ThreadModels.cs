using System.Globalization;

namespace ThreadShelf;

public sealed record ThreadMetadata
{
    public string Folder { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public string Notes { get; init; } = "";
    public bool Favorite { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record TagDefinition
{
    public const string DefaultColor = "#0969DA";

    public string Name { get; init; } = "";
    public string Color { get; init; } = DefaultColor;
    public string Description { get; init; } = "";
}

public sealed record CodexThread
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string Preview { get; init; } = "";
    public string? Description { get; init; }
    public string? Status { get; init; }
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
    public string CreatedLocal => CreatedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public string UpdatedLocal => UpdatedAt == DateTimeOffset.MinValue
        ? ""
        : UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string TagsText => Metadata.Tags.Count == 0 ? "" : string.Join(", ", Metadata.Tags);
}

public sealed record ThreadShelfSnapshot
{
    public IReadOnlyList<CodexThread> Threads { get; init; } = [];
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> ProjectAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string CodexHome { get; init; } = "";
    public string SidecarPath { get; init; } = "";
    public string DataSource { get; init; } = "";
    public string LoadWarning { get; init; } = "";
    public bool SupportsNativeActions { get; init; }
    public bool SupportsNativeProjectRename { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset SourceLoadedAt { get; init; } = DateTimeOffset.Now;
    public bool IsSourceCached { get; init; }

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

public sealed record FolderSummary(string Name, int Count);
public sealed record ProjectSummary(string Key, string Name, string Workspace, int Count);
public sealed record TagSummary(TagDefinition Definition, int Count);

public sealed record EditDraft(string Folder, string Notes, bool Favorite)
{
    public static EditDraft From(ThreadMetadata metadata) =>
        new(metadata.Folder, metadata.Notes, metadata.Favorite);
}

public sealed class ThreadShelfValidationException : InvalidOperationException
{
    public ThreadShelfValidationException(
        string code,
        string message,
        object? details = null,
        bool retryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
        Retryable = retryable;
    }

    public string Code { get; }
    public object? Details { get; }
    public bool Retryable { get; }
}
