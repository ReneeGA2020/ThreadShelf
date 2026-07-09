using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ThreadShelf;

public sealed record ThreadShelfCommandSource(string Provider, string CodexHome, string SidecarPath);

public sealed record ThreadShelfCommandError(
    string Code,
    string Message,
    object? Details = null,
    bool Retryable = false);

public sealed record ThreadShelfCommandResult<T>
{
    public bool Ok { get; init; }
    public T? Data { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ThreadShelfCommandSource? Source { get; init; }
    public ThreadShelfCommandError? Error { get; init; }

    public static ThreadShelfCommandResult<T> Success(
        T data,
        ThreadShelfCommandSource source,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Ok = true,
            Data = data,
            Source = source,
            Warnings = warnings ?? []
        };

    public static ThreadShelfCommandResult<T> Failure(
        string code,
        string message,
        object? details = null,
        bool retryable = false) =>
        new()
        {
            Ok = false,
            Error = new ThreadShelfCommandError(code, message, details, retryable)
        };
}

public sealed record ThreadMetadataDto(
    string Folder,
    IReadOnlyList<string> Tags,
    string Notes,
    bool Favorite,
    DateTimeOffset? UpdatedAt);

public sealed record ThreadDto(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    string Workspace,
    string Model,
    string SourcePath,
    bool IsArchived,
    long FileSizeBytes,
    string Originator,
    string Source,
    ThreadMetadataDto Metadata);

public sealed record TagDto(string Name, string Color, string Description, int Count);

public sealed record ListThreadsRequest
{
    public string? CodexHome { get; init; }
    public string Folder { get; init; } = ThreadFilters.All;
    public string Tag { get; init; } = "";
    public string Query { get; init; } = "";
    public bool? Archived { get; init; }
    public int? Limit { get; init; }
}

public sealed record GetThreadRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
}

public sealed record UpdateThreadMetadataRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
    public string? Folder { get; init; }
    public string? Notes { get; init; }
    public bool? Favorite { get; init; }
}

public sealed record MoveThreadRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
    public string Folder { get; init; } = "";
}

public sealed record ThreadTagRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
    public string Tag { get; init; } = "";
}

public sealed record ListTagsRequest
{
    public string? CodexHome { get; init; }
}

public sealed record CreateTagRequest
{
    public string? CodexHome { get; init; }
    public string Name { get; init; } = "";
    public string Color { get; init; } = TagDefinition.DefaultColor;
    public string Description { get; init; } = "";
}

public sealed record UpdateTagRequest
{
    public string? CodexHome { get; init; }
    public string Name { get; init; } = "";
    public string? NewName { get; init; }
    public string? Color { get; init; }
    public string? Description { get; init; }
}

public sealed record DeleteTagRequest
{
    public string? CodexHome { get; init; }
    public string Name { get; init; } = "";
}

public sealed record NativeThreadRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
}

public sealed record RenameThreadRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
    public string Title { get; init; } = "";
}

public sealed class ThreadShelfCommandService
{
    public ThreadShelfCommandResult<IReadOnlyList<ThreadDto>> ListThreads(ListThreadsRequest request)
    {
        if (request.Limit is < 0)
        {
            return ThreadShelfCommandResult<IReadOnlyList<ThreadDto>>.Failure(
                "invalid_argument",
                "Limit must be zero or greater.",
                new { request.Limit });
        }

        return WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            var folder = string.IsNullOrWhiteSpace(request.Folder)
                ? ThreadFilters.All
                : request.Folder.Trim();
            var threads = ThreadFilters.Apply(snapshot.Threads, folder, request.Query, request.Tag);

            if (request.Archived is not null)
            {
                threads = threads
                    .Where(thread => thread.IsArchived == request.Archived.Value)
                    .ToList();
            }

            if (request.Limit is > 0)
            {
                threads = threads.Take(request.Limit.Value).ToList();
            }

            return Success(
                (IReadOnlyList<ThreadDto>)threads.Select(ToDto).ToList(),
                snapshot);
        });
    }

    public ThreadShelfCommandResult<IReadOnlyList<ThreadDto>> SearchThreads(ListThreadsRequest request) =>
        ListThreads(request);

    public ThreadShelfCommandResult<ThreadDto> GetThread(GetThreadRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            if (string.IsNullOrWhiteSpace(request.ThreadId))
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "invalid_argument",
                    "Thread id is required.");
            }

            var thread = FindThread(snapshot, request.ThreadId);
            return thread is null
                ? ThreadNotFound<ThreadDto>(request.ThreadId)
                : Success(ToDto(thread), snapshot);
        });

    public ThreadShelfCommandResult<ThreadDto> UpdateThreadMetadata(UpdateThreadMetadataRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            if (string.IsNullOrWhiteSpace(request.ThreadId))
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "invalid_argument",
                    "Thread id is required.");
            }

            if (request.Folder is null && request.Notes is null && request.Favorite is null)
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "invalid_argument",
                    "At least one metadata field must be supplied.");
            }

            var thread = FindThread(snapshot, request.ThreadId);
            if (thread is null)
            {
                return ThreadNotFound<ThreadDto>(request.ThreadId);
            }

            var metadata = ThreadShelfRepository.MetadataFrom(
                new EditDraft(
                    request.Folder ?? thread.Metadata.Folder,
                    request.Notes ?? thread.Metadata.Notes,
                    request.Favorite ?? thread.Metadata.Favorite),
                thread.Metadata.Tags);

            return SaveMetadataAndReload(repository, snapshot, thread.Id, metadata);
        });

    public ThreadShelfCommandResult<ThreadDto> MoveThread(MoveThreadRequest request) =>
        UpdateThreadMetadata(new UpdateThreadMetadataRequest
        {
            CodexHome = request.CodexHome,
            ThreadId = request.ThreadId,
            Folder = request.Folder ?? ""
        });

    public ThreadShelfCommandResult<ThreadDto> AddThreadTag(ThreadTagRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            var tagName = ThreadShelfRepository.NormalizeTagName(request.Tag);
            if (tagName.Length == 0)
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "invalid_argument",
                    "Tag name is required.");
            }

            var thread = FindThread(snapshot, request.ThreadId);
            if (thread is null)
            {
                return ThreadNotFound<ThreadDto>(request.ThreadId);
            }

            var tag = FindTag(snapshot, tagName);
            if (tag is null)
            {
                return TagNotFound<ThreadDto>(tagName);
            }

            if (thread.Metadata.Tags.Any(name => name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return Success(ToDto(thread), snapshot);
            }

            var metadata = thread.Metadata with
            {
                Tags = thread.Metadata.Tags
                    .Concat([tag.Name])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };

            return SaveMetadataAndReload(repository, snapshot, thread.Id, metadata);
        });

    public ThreadShelfCommandResult<ThreadDto> RemoveThreadTag(ThreadTagRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            var tagName = ThreadShelfRepository.NormalizeTagName(request.Tag);
            if (tagName.Length == 0)
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "invalid_argument",
                    "Tag name is required.");
            }

            var thread = FindThread(snapshot, request.ThreadId);
            if (thread is null)
            {
                return ThreadNotFound<ThreadDto>(request.ThreadId);
            }

            if (FindTag(snapshot, tagName) is null
                && !thread.Metadata.Tags.Any(name => name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
            {
                return TagNotFound<ThreadDto>(tagName);
            }

            var metadata = thread.Metadata with
            {
                Tags = thread.Metadata.Tags
                    .Where(name => !name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            };

            return SaveMetadataAndReload(repository, snapshot, thread.Id, metadata);
        });

    public ThreadShelfCommandResult<IReadOnlyList<TagDto>> ListTags(ListTagsRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
            Success(GetTagDtos(snapshot), snapshot));

    public ThreadShelfCommandResult<TagDto> CreateTag(CreateTagRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            var name = ThreadShelfRepository.NormalizeTagName(request.Name);
            if (name.Length == 0)
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "invalid_argument",
                    "Tag name is required.");
            }

            if (!ThreadShelfRepository.IsValidTagColor(request.Color))
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "invalid_argument",
                    "Tag color must be #RRGGBB.",
                    new { request.Color });
            }

            if (FindTag(snapshot, name) is not null)
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "tag_conflict",
                    $"Tag '{name}' already exists.",
                    new { name });
            }

            repository.SaveTagDefinition(
                "",
                new TagDefinition
                {
                    Name = name,
                    Color = request.Color,
                    Description = request.Description
                });

            return ReloadTag(repository, name);
        });

    public ThreadShelfCommandResult<TagDto> UpdateTag(UpdateTagRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            var oldName = ThreadShelfRepository.NormalizeTagName(request.Name);
            if (oldName.Length == 0)
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "invalid_argument",
                    "Tag name is required.");
            }

            var current = FindTag(snapshot, oldName);
            if (current is null)
            {
                return TagNotFound<TagDto>(oldName);
            }

            var nextName = ThreadShelfRepository.NormalizeTagName(request.NewName ?? current.Name);
            if (nextName.Length == 0)
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "invalid_argument",
                    "New tag name cannot be empty.");
            }

            var existing = FindTag(snapshot, nextName);
            if (existing is not null && !existing.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "tag_conflict",
                    $"Tag '{nextName}' already exists.",
                    new { name = nextName });
            }

            var color = request.Color ?? current.Color;
            if (!ThreadShelfRepository.IsValidTagColor(color))
            {
                return ThreadShelfCommandResult<TagDto>.Failure(
                    "invalid_argument",
                    "Tag color must be #RRGGBB.",
                    new { color });
            }

            repository.SaveTagDefinition(
                oldName,
                new TagDefinition
                {
                    Name = nextName,
                    Color = color,
                    Description = request.Description ?? current.Description
                });

            return ReloadTag(repository, nextName);
        });

    public ThreadShelfCommandResult<IReadOnlyList<TagDto>> DeleteTag(DeleteTagRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            var name = ThreadShelfRepository.NormalizeTagName(request.Name);
            if (name.Length == 0)
            {
                return ThreadShelfCommandResult<IReadOnlyList<TagDto>>.Failure(
                    "invalid_argument",
                    "Tag name is required.");
            }

            if (FindTag(snapshot, name) is null)
            {
                return TagNotFound<IReadOnlyList<TagDto>>(name);
            }

            repository.DeleteTagDefinition(name);
            var reloaded = repository.Load();
            return Success(GetTagDtos(reloaded), reloaded);
        });

    public ThreadShelfCommandResult<ThreadDto> ArchiveThread(NativeThreadRequest request) =>
        SetThreadArchived(request, archived: true);

    public ThreadShelfCommandResult<ThreadDto> UnarchiveThread(NativeThreadRequest request) =>
        SetThreadArchived(request, archived: false);

    public ThreadShelfCommandResult<ThreadDto> RenameThread(RenameThreadRequest request) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            if (!snapshot.SupportsNativeActions)
            {
                return NativeUnsupported<ThreadDto>(snapshot);
            }

            var title = (request.Title ?? "").Trim();
            if (title.Length == 0)
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "invalid_argument",
                    "Title is required.");
            }

            var thread = FindThread(snapshot, request.ThreadId);
            if (thread is null)
            {
                return ThreadNotFound<ThreadDto>(request.ThreadId);
            }

            try
            {
                repository.SetName(thread.Id, title);
                return ReloadThread(repository, thread.Id);
            }
            catch (Exception ex)
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "app_server_unavailable",
                    ex.Message,
                    retryable: true);
            }
        });

    private ThreadShelfCommandResult<ThreadDto> SetThreadArchived(
        NativeThreadRequest request,
        bool archived) =>
        WithSnapshot(request.CodexHome, (repository, snapshot) =>
        {
            if (!snapshot.SupportsNativeActions)
            {
                return NativeUnsupported<ThreadDto>(snapshot);
            }

            var thread = FindThread(snapshot, request.ThreadId);
            if (thread is null)
            {
                return ThreadNotFound<ThreadDto>(request.ThreadId);
            }

            try
            {
                repository.SetArchived(thread.Id, archived);
                return ReloadThread(repository, thread.Id);
            }
            catch (Exception ex)
            {
                return ThreadShelfCommandResult<ThreadDto>.Failure(
                    "app_server_unavailable",
                    ex.Message,
                    retryable: true);
            }
        });

    private static ThreadShelfCommandResult<T> WithSnapshot<T>(
        string? codexHome,
        Func<ThreadShelfRepository, ThreadShelfSnapshot, ThreadShelfCommandResult<T>> action)
    {
        try
        {
            var repository = new ThreadShelfRepository(codexHome);
            var snapshot = repository.Load();
            return action(repository, snapshot);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ThreadShelfCommandResult<T>.Failure(
                "permission_denied",
                ex.Message);
        }
        catch (IOException ex)
        {
            return ThreadShelfCommandResult<T>.Failure(
                "sidecar_write_failed",
                ex.Message,
                retryable: true);
        }
        catch (Exception ex)
        {
            return ThreadShelfCommandResult<T>.Failure(
                "invalid_argument",
                ex.Message);
        }
    }

    private static ThreadShelfCommandResult<T> Success<T>(T data, ThreadShelfSnapshot snapshot) =>
        ThreadShelfCommandResult<T>.Success(data, SourceFrom(snapshot), WarningsFrom(snapshot));

    private static ThreadShelfCommandResult<ThreadDto> SaveMetadataAndReload(
        ThreadShelfRepository repository,
        ThreadShelfSnapshot snapshot,
        string threadId,
        ThreadMetadata metadata)
    {
        try
        {
            repository.SaveMetadata(threadId, metadata);
            return ReloadThread(repository, threadId);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ThreadShelfCommandResult<ThreadDto>.Failure(
                "permission_denied",
                ex.Message);
        }
        catch (IOException ex)
        {
            return ThreadShelfCommandResult<ThreadDto>.Failure(
                "sidecar_write_failed",
                ex.Message,
                retryable: true);
        }
        catch (Exception ex)
        {
            return ThreadShelfCommandResult<ThreadDto>.Failure(
                "sidecar_write_failed",
                ex.Message);
        }
    }

    private static ThreadShelfCommandResult<ThreadDto> ReloadThread(
        ThreadShelfRepository repository,
        string threadId)
    {
        var reloaded = repository.Load();
        var updated = FindThread(reloaded, threadId);
        return updated is null
            ? ThreadNotFound<ThreadDto>(threadId)
            : Success(ToDto(updated), reloaded);
    }

    private static ThreadShelfCommandResult<TagDto> ReloadTag(
        ThreadShelfRepository repository,
        string name)
    {
        var reloaded = repository.Load();
        var tag = GetTagDtos(reloaded)
            .FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return tag is null
            ? TagNotFound<TagDto>(name)
            : Success(tag, reloaded);
    }

    private static IReadOnlyList<TagDto> GetTagDtos(ThreadShelfSnapshot snapshot) =>
        ThreadFilters.BuildTagSummaries(snapshot.Threads, snapshot.Tags)
            .Select(summary => new TagDto(
                summary.Definition.Name,
                summary.Definition.Color,
                summary.Definition.Description,
                summary.Count))
            .ToList();

    private static CodexThread? FindThread(ThreadShelfSnapshot snapshot, string threadId) =>
        snapshot.Threads.FirstOrDefault(thread =>
            thread.Id.Equals((threadId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static TagDefinition? FindTag(ThreadShelfSnapshot snapshot, string name) =>
        snapshot.Tags.FirstOrDefault(tag =>
            tag.Name.Equals(ThreadShelfRepository.NormalizeTagName(name), StringComparison.OrdinalIgnoreCase));

    private static ThreadShelfCommandResult<T> ThreadNotFound<T>(string threadId) =>
        ThreadShelfCommandResult<T>.Failure(
            "thread_not_found",
            $"Thread '{threadId}' was not found.",
            new { threadId });

    private static ThreadShelfCommandResult<T> TagNotFound<T>(string name) =>
        ThreadShelfCommandResult<T>.Failure(
            "tag_not_found",
            $"Tag '{name}' was not found.",
            new { name });

    private static ThreadShelfCommandResult<T> NativeUnsupported<T>(ThreadShelfSnapshot snapshot) =>
        ThreadShelfCommandResult<T>.Failure(
            "native_action_unsupported",
            "Native Codex actions require the Codex CLI app-server provider.",
            new
            {
                provider = SourceFrom(snapshot).Provider,
                snapshot.CodexHome
            });

    private static ThreadDto ToDto(CodexThread thread) =>
        new(
            thread.Id,
            thread.DisplayTitle,
            thread.UpdatedAt,
            thread.Workspace,
            thread.Model,
            thread.SourcePath,
            thread.IsArchived,
            thread.FileSizeBytes,
            thread.Originator,
            thread.Source,
            new ThreadMetadataDto(
                thread.Metadata.Folder,
                thread.Metadata.Tags,
                thread.Metadata.Notes,
                thread.Metadata.Favorite,
                thread.Metadata.UpdatedAt));

    private static ThreadShelfCommandSource SourceFrom(ThreadShelfSnapshot snapshot) =>
        new(
            snapshot.SupportsNativeActions ? "app-server" : "local-files",
            snapshot.CodexHome,
            snapshot.SidecarPath);

    private static IReadOnlyList<string> WarningsFrom(ThreadShelfSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.LoadWarning)
            ? []
            : [snapshot.LoadWarning];
}
