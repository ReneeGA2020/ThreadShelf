using System;
using System.Collections.Generic;
using System.Linq;

namespace ThreadShelf;

public interface IThreadShelfRepository
{
    string CodexHome { get; }
    string SidecarPath { get; }

    ThreadShelfSnapshot Load(bool forceRefresh = false);
    void SaveMetadata(string threadId, ThreadMetadata metadata);
    void SaveOrganization(
        IReadOnlyList<TagDefinition> tags,
        IReadOnlyDictionary<string, ThreadMetadata> threads);
    void SaveTagDefinition(string editingName, TagDefinition definition);
    void DeleteTagDefinition(string name);
    void RenameProjectAlias(
        string projectKey,
        string newName,
        IReadOnlyList<CodexThread> threads);
    void RenameFolder(
        string projectKey,
        string oldName,
        string newName,
        IReadOnlyList<CodexThread> threads);
    void SetArchived(string threadId, bool archived);
    void SetName(string threadId, string name);
}

public sealed record ThreadShelfServiceResult<T>(T Data, ThreadShelfSnapshot Snapshot);

public sealed record ThreadOrganizationUpdate(
    string ThreadId,
    string? Folder,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<string>? SetTags = null,
    IReadOnlyList<string>? AddTags = null,
    IReadOnlyList<string>? RemoveTags = null);

public sealed record ThreadOrganizationChange(
    string ThreadId,
    string BeforeFolder,
    string AfterFolder,
    IReadOnlyList<string> BeforeTags,
    IReadOnlyList<string> AfterTags,
    bool Changed);

public sealed record ThreadOrganizationResult(
    int TagsUpserted,
    int ThreadsUpdated,
    bool DryRun,
    IReadOnlyList<ThreadOrganizationChange> Changes);

public sealed class ThreadShelfService(IThreadShelfRepository repository)
{
    private readonly IThreadShelfRepository _repository = repository
        ?? throw new ArgumentNullException(nameof(repository));

    public string CodexHome => _repository.CodexHome;
    public string SidecarPath => _repository.SidecarPath;

    public ThreadShelfSnapshot Load(bool forceRefresh = false) => _repository.Load(forceRefresh);

    public ThreadShelfServiceResult<CodexThread> GetThread(string threadId, bool forceRefresh = false)
    {
        var snapshot = Load(forceRefresh);
        return new(RequireThread(snapshot, threadId), snapshot);
    }

    public ThreadShelfServiceResult<CodexThread> SaveThreadMetadata(
        string threadId,
        ThreadMetadata metadata)
    {
        var snapshot = Load();
        var thread = RequireThread(snapshot, threadId);
        var normalized = ThreadShelfRepository.MetadataFrom(
            new EditDraft(metadata.Folder, metadata.Notes, metadata.Favorite),
            metadata.Tags);
        return SaveMetadataAndReload(snapshot, thread, normalized);
    }

    public ThreadShelfServiceResult<CodexThread> UpdateThreadMetadata(
        string threadId,
        string? folder,
        string? notes,
        bool? favorite)
    {
        var normalizedThreadId = RequireThreadId(threadId);
        if (folder is null && notes is null && favorite is null)
        {
            throw Invalid(
                "metadata_patch_empty",
                "At least one metadata field must be supplied.");
        }

        var snapshot = Load();
        var thread = RequireThread(snapshot, normalizedThreadId);
        var metadata = ThreadShelfRepository.MetadataFrom(
            new EditDraft(
                folder ?? thread.Metadata.Folder,
                notes ?? thread.Metadata.Notes,
                favorite ?? thread.Metadata.Favorite),
            thread.Metadata.Tags);
        return SaveMetadataAndReload(snapshot, thread, metadata);
    }

    public ThreadShelfServiceResult<CodexThread> MoveThread(string threadId, string? folder) =>
        UpdateThreadMetadata(threadId, folder ?? "", null, null);

    public ThreadShelfServiceResult<CodexThread> SetThreadTag(
        string threadId,
        string? tagName,
        bool assigned)
    {
        var normalizedName = ThreadShelfRepository.NormalizeTagName(tagName ?? "");
        if (normalizedName.Length == 0)
        {
            throw Invalid("tag_name_empty", "Tag name is required.");
        }

        var snapshot = Load();
        var thread = RequireThread(snapshot, threadId);
        var tag = FindTag(snapshot, normalizedName);
        var hasTag = thread.Metadata.Tags.Any(name =>
            name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

        if (assigned && tag is null || !assigned && tag is null && !hasTag)
        {
            throw new ThreadShelfValidationException(
                "tag_not_found",
                $"Tag '{normalizedName}' was not found.",
                new { name = normalizedName });
        }

        if (assigned == hasTag)
        {
            return new(thread, snapshot);
        }

        var canonicalName = tag?.Name ?? normalizedName;
        var tags = assigned
            ? thread.Metadata.Tags
                .Concat([canonicalName])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
            : thread.Metadata.Tags
                .Where(name => !name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return SaveMetadataAndReload(snapshot, thread, thread.Metadata with { Tags = tags });
    }

    public ThreadShelfServiceResult<TagDefinition> CreateTag(
        string? name,
        string? color,
        string? description) =>
        SaveTagDefinitionCore("", new TagDefinition
        {
            Name = name ?? "",
            Color = color ?? "",
            Description = description ?? ""
        });

    public ThreadShelfServiceResult<TagDefinition> UpdateTag(
        string? name,
        string? newName,
        string? color,
        string? description)
    {
        var snapshot = Load();
        var normalizedName = RequireTagName(name);
        var current = RequireTag(snapshot, normalizedName);
        if (newName is not null && ThreadShelfRepository.NormalizeTagName(newName).Length == 0)
        {
            throw Invalid("tag_name_empty", "New tag name cannot be empty.");
        }

        return SaveTagDefinitionCore(
            current.Name,
            new TagDefinition
            {
                Name = newName ?? current.Name,
                Color = color ?? current.Color,
                Description = description ?? current.Description
            },
            snapshot);
    }

    public ThreadShelfServiceResult<TagDefinition> SaveTagDefinition(
        string? editingName,
        TagDefinition definition) =>
        SaveTagDefinitionCore(editingName ?? "", definition);

    public ThreadShelfServiceResult<string> DeleteTag(string? name)
    {
        var snapshot = Load();
        var normalizedName = RequireTagName(name);
        var current = RequireTag(snapshot, normalizedName);
        _repository.DeleteTagDefinition(current.Name);
        return new(current.Name, Load());
    }

    public ThreadShelfServiceResult<ThreadOrganizationResult> ApplyOrganization(
        IReadOnlyList<TagDefinition>? requestedTags,
        IReadOnlyList<ThreadOrganizationUpdate>? requestedThreads,
        bool dryRun = false)
    {
        requestedTags ??= [];
        requestedThreads ??= [];
        if (requestedTags.Count == 0 && requestedThreads.Count == 0)
        {
            throw Invalid(
                "organization_empty",
                "At least one tag definition or thread update is required.");
        }

        var snapshot = Load();
        var tags = new List<TagDefinition>(requestedTags.Count);
        var tagNames = snapshot.Tags.ToDictionary(
            tag => tag.Name,
            tag => tag.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (var tag in requestedTags)
        {
            if (tag is null)
            {
                throw Invalid(
                    "organization_tag_null",
                    "Tag definitions cannot contain null entries.");
            }

            var normalized = NormalizeTagDefinition(tag);
            if (tags.Any(candidate =>
                candidate.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw Invalid(
                    "organization_tag_duplicate",
                    $"Tag '{normalized.Name}' appears more than once in the organization request.",
                    new { name = normalized.Name });
            }

            tags.Add(normalized);
            tagNames[normalized.Name] = normalized.Name;
        }

        var updates = new Dictionary<string, ThreadMetadata>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<ThreadOrganizationChange>(requestedThreads.Count);
        foreach (var update in requestedThreads)
        {
            if (update is null)
            {
                throw Invalid(
                    "organization_thread_null",
                    "Thread updates cannot contain null entries.");
            }

            var threadId = RequireThreadId(update.ThreadId);
            if (updates.ContainsKey(threadId))
            {
                throw Invalid(
                    "organization_thread_duplicate",
                    $"Thread '{threadId}' appears more than once in the organization request.",
                    new { threadId });
            }

            var thread = RequireThread(snapshot, threadId);
            var hasLegacySet = update.Tags is not null;
            var hasExplicitSet = update.SetTags is not null;
            var hasIncremental = update.AddTags is not null || update.RemoveTags is not null;
            if (hasLegacySet && (hasExplicitSet || hasIncremental)
                || hasExplicitSet && hasIncremental)
            {
                throw Invalid(
                    "organization_tag_operation_conflict",
                    $"Thread '{thread.Id}' mixes full replacement and incremental tag operations.",
                    new { threadId = thread.Id });
            }

            if (update.Folder is null && !hasLegacySet && !hasExplicitSet && !hasIncremental)
            {
                throw Invalid(
                    "organization_thread_empty",
                    $"Thread '{thread.Id}' does not contain an organization change.",
                    new { threadId = thread.Id });
            }

            var normalizedTags = CanonicalizeTagNames(
                NormalizeTagNames(update.SetTags ?? update.Tags), tagNames);
            var addTags = CanonicalizeTagNames(NormalizeTagNames(update.AddTags), tagNames) ?? [];
            var removeTags = CanonicalizeTagNames(NormalizeTagNames(update.RemoveTags), tagNames) ?? [];
            var conflictTag = addTags.FirstOrDefault(candidate =>
                removeTags.Contains(candidate, StringComparer.OrdinalIgnoreCase));
            if (conflictTag is not null)
            {
                throw Invalid(
                    "organization_tag_operation_conflict",
                    $"Tag '{conflictTag}' cannot be added and removed from thread '{thread.Id}' in one request.",
                    new { threadId = thread.Id, name = conflictTag });
            }

            var finalTags = normalizedTags ?? thread.Metadata.Tags.ToList();
            if (hasIncremental)
            {
                finalTags = finalTags
                    .Where(name => !removeTags.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .Concat(addTags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(candidate => candidate, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            var referencedTags = finalTags.Concat(addTags).Concat(removeTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missingTag = referencedTags.FirstOrDefault(candidate => !tagNames.ContainsKey(candidate));
            if (missingTag is not null)
            {
                throw new ThreadShelfValidationException(
                    "tag_not_found",
                    $"Tag '{missingTag}' was not found.",
                    new { name = missingTag });
            }

            var next = ThreadShelfRepository.MetadataFrom(
                new EditDraft(
                    update.Folder ?? thread.Metadata.Folder,
                    thread.Metadata.Notes,
                    thread.Metadata.Favorite),
                finalTags);
            var changed = !next.Folder.Equals(thread.Metadata.Folder, StringComparison.Ordinal)
                || !next.Tags.SequenceEqual(thread.Metadata.Tags, StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                updates[thread.Id] = next;
            }

            changes.Add(new ThreadOrganizationChange(
                thread.Id,
                thread.Metadata.Folder,
                next.Folder,
                thread.Metadata.Tags,
                next.Tags,
                changed));
        }

        if (!dryRun)
        {
            _repository.SaveOrganization(tags, updates);
        }

        var reloaded = dryRun ? snapshot : Load();
        return new(new ThreadOrganizationResult(tags.Count, updates.Count, dryRun, changes), reloaded);
    }

    private static List<string>? NormalizeTagNames(IReadOnlyList<string>? names) =>
        names?.Select(name => ThreadShelfRepository.NormalizeTagName(name ?? ""))
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<string>? CanonicalizeTagNames(
        List<string>? names,
        IReadOnlyDictionary<string, string> canonicalNames) =>
        names?.Select(name => canonicalNames.GetValueOrDefault(name) ?? name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public ThreadShelfServiceResult<ThreadShelfSnapshot> RenameProjectAlias(
        string projectKey,
        string newName)
    {
        var snapshot = Load();
        _repository.RenameProjectAlias(projectKey, newName, snapshot.Threads);
        var reloaded = Load();
        return new(reloaded, reloaded);
    }

    public ThreadShelfServiceResult<ThreadShelfSnapshot> RenameFolder(
        string projectKey,
        string oldName,
        string newName)
    {
        var snapshot = Load();
        _repository.RenameFolder(projectKey, oldName, newName, snapshot.Threads);
        var reloaded = Load();
        return new(reloaded, reloaded);
    }

    public ThreadShelfServiceResult<CodexThread> SetArchived(string threadId, bool archived)
    {
        var snapshot = Load();
        EnsureNativeActions(snapshot);
        var thread = RequireThread(snapshot, threadId);

        try
        {
            _repository.SetArchived(thread.Id, archived);
            return ReloadThread(thread.Id);
        }
        catch (ThreadShelfValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ThreadShelfValidationException(
                "app_server_unavailable",
                ex.Message,
                retryable: true,
                innerException: ex);
        }
    }

    public ThreadShelfServiceResult<CodexThread> RenameThread(string threadId, string? title)
    {
        var snapshot = Load();
        EnsureNativeActions(snapshot);
        var trimmedTitle = (title ?? "").Trim();
        if (trimmedTitle.Length == 0)
        {
            throw Invalid("thread_title_empty", "Title is required.");
        }

        var thread = RequireThread(snapshot, threadId);
        try
        {
            _repository.SetName(thread.Id, trimmedTitle);
            return ReloadThread(thread.Id);
        }
        catch (ThreadShelfValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ThreadShelfValidationException(
                "app_server_unavailable",
                ex.Message,
                retryable: true,
                innerException: ex);
        }
    }

    private ThreadShelfServiceResult<TagDefinition> SaveTagDefinitionCore(
        string editingName,
        TagDefinition definition,
        ThreadShelfSnapshot? loaded = null)
    {
        var snapshot = loaded ?? Load();
        var normalized = NormalizeTagDefinition(definition);
        var normalizedEditingName = ThreadShelfRepository.NormalizeTagName(editingName);

        if (normalizedEditingName.Length == 0)
        {
            if (FindTag(snapshot, normalized.Name) is not null)
            {
                throw new ThreadShelfValidationException(
                    "tag_conflict",
                    $"Tag '{normalized.Name}' already exists.",
                    new { name = normalized.Name });
            }
        }
        else
        {
            var current = RequireTag(snapshot, normalizedEditingName);
            var conflict = FindTag(snapshot, normalized.Name);
            if (conflict is not null
                && !conflict.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ThreadShelfValidationException(
                    "tag_conflict",
                    $"Tag '{normalized.Name}' already exists.",
                    new { name = normalized.Name });
            }

            normalizedEditingName = current.Name;
        }

        _repository.SaveTagDefinition(normalizedEditingName, normalized);
        var reloaded = Load();
        return new(RequireTag(reloaded, normalized.Name), reloaded);
    }

    private ThreadShelfServiceResult<CodexThread> SaveMetadataAndReload(
        ThreadShelfSnapshot snapshot,
        CodexThread thread,
        ThreadMetadata metadata)
    {
        if (SameMetadata(thread.Metadata, metadata))
        {
            return new(thread, snapshot);
        }

        try
        {
            _repository.SaveMetadata(thread.Id, metadata);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (System.IO.IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ThreadShelfValidationException(
                "sidecar_write_failed",
                ex.Message,
                innerException: ex);
        }

        return ReloadThread(thread.Id);
    }

    private ThreadShelfServiceResult<CodexThread> ReloadThread(string threadId)
    {
        var reloaded = Load();
        return new(RequireThread(reloaded, threadId), reloaded);
    }

    private static TagDefinition NormalizeTagDefinition(TagDefinition definition)
    {
        var name = RequireTagName(definition.Name);
        if (!ThreadShelfRepository.IsValidTagColor(definition.Color))
        {
            throw Invalid(
                "tag_color_invalid",
                "Tag color must be #RRGGBB.",
                new { definition.Color });
        }

        return definition with
        {
            Name = name,
            Color = ThreadShelfRepository.NormalizeTagColor(definition.Color),
            Description = definition.Description ?? ""
        };
    }

    private static string RequireThreadId(string? threadId)
    {
        var normalized = (threadId ?? "").Trim();
        return normalized.Length > 0
            ? normalized
            : throw Invalid("thread_id_empty", "Thread id is required.");
    }

    private static string RequireTagName(string? name)
    {
        var normalized = ThreadShelfRepository.NormalizeTagName(name ?? "");
        return normalized.Length > 0
            ? normalized
            : throw Invalid("tag_name_empty", "Tag name is required.");
    }

    private static CodexThread RequireThread(ThreadShelfSnapshot snapshot, string? threadId)
    {
        var normalizedId = RequireThreadId(threadId);
        return snapshot.Threads.FirstOrDefault(thread =>
                thread.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ThreadShelfValidationException(
                "thread_not_found",
                $"Thread '{normalizedId}' was not found.",
                new { threadId = normalizedId });
    }

    private static TagDefinition RequireTag(ThreadShelfSnapshot snapshot, string name) =>
        FindTag(snapshot, name)
        ?? throw new ThreadShelfValidationException(
            "tag_not_found",
            $"Tag '{name}' was not found.",
            new { name });

    private static TagDefinition? FindTag(ThreadShelfSnapshot snapshot, string name) =>
        snapshot.Tags.FirstOrDefault(tag =>
            tag.Name.Equals(
                ThreadShelfRepository.NormalizeTagName(name),
                StringComparison.OrdinalIgnoreCase));

    private static void EnsureNativeActions(ThreadShelfSnapshot snapshot)
    {
        if (!snapshot.SupportsNativeActions)
        {
            throw new ThreadShelfValidationException(
                "native_action_unsupported",
                "Native Codex actions require the Codex CLI app-server provider.",
                new
                {
                    provider = snapshot.SupportsNativeActions ? "app-server" : "local-files",
                    snapshot.CodexHome
                });
        }
    }

    private static bool SameMetadata(ThreadMetadata left, ThreadMetadata right) =>
        string.Equals(left.Folder, right.Folder, StringComparison.Ordinal)
        && string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
        && left.Favorite == right.Favorite
        && left.Tags.SequenceEqual(right.Tags, StringComparer.OrdinalIgnoreCase);

    private static ThreadShelfValidationException Invalid(
        string code,
        string message,
        object? details = null) =>
        new(code, message, details);
}
