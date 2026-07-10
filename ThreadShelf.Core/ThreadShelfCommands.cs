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

public sealed record OrganizationThreadUpdate
{
    public string ThreadId { get; init; } = "";
    public string? Folder { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record ApplyOrganizationRequest
{
    public string? CodexHome { get; init; }
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
    public IReadOnlyList<OrganizationThreadUpdate> Threads { get; init; } = [];
}

public sealed record ApplyOrganizationResult(int TagsUpserted, int ThreadsUpdated);

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
    private readonly Func<string?, IThreadShelfRepository> _repositoryFactory;

    public ThreadShelfCommandService()
        : this(codexHome => new ThreadShelfRepository(codexHome))
    {
    }

    public ThreadShelfCommandService(Func<string?, IThreadShelfRepository> repositoryFactory)
    {
        _repositoryFactory = repositoryFactory
            ?? throw new ArgumentNullException(nameof(repositoryFactory));
    }

    public ThreadShelfCommandResult<IReadOnlyList<ThreadDto>> ListThreads(ListThreadsRequest request)
    {
        if (request.Limit is < 0)
        {
            return ThreadShelfCommandResult<IReadOnlyList<ThreadDto>>.Failure(
                "invalid_argument",
                "Limit must be zero or greater.",
                new { request.Limit });
        }

        return WithService(request.CodexHome, service =>
        {
            var snapshot = service.Load();
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
        WithService(request.CodexHome, service =>
        {
            var result = service.GetThread(request.ThreadId);
            return Success(ToDto(result.Data), result.Snapshot);
        });

    public ThreadShelfCommandResult<ThreadDto> UpdateThreadMetadata(UpdateThreadMetadataRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.UpdateThreadMetadata(
                request.ThreadId,
                request.Folder,
                request.Notes,
                request.Favorite);
            return Success(ToDto(result.Data), result.Snapshot);
        });

    public ThreadShelfCommandResult<ThreadDto> MoveThread(MoveThreadRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.MoveThread(request.ThreadId, request.Folder);
            return Success(ToDto(result.Data), result.Snapshot);
        });

    public ThreadShelfCommandResult<ThreadDto> AddThreadTag(ThreadTagRequest request) =>
        SetThreadTag(request, assigned: true);

    public ThreadShelfCommandResult<ThreadDto> RemoveThreadTag(ThreadTagRequest request) =>
        SetThreadTag(request, assigned: false);

    public ThreadShelfCommandResult<IReadOnlyList<TagDto>> ListTags(ListTagsRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var snapshot = service.Load();
            return Success(GetTagDtos(snapshot), snapshot);
        });

    public ThreadShelfCommandResult<TagDto> CreateTag(CreateTagRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.CreateTag(request.Name, request.Color, request.Description);
            return Success(ToTagDto(result.Snapshot, result.Data), result.Snapshot);
        });

    public ThreadShelfCommandResult<TagDto> UpdateTag(UpdateTagRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.UpdateTag(
                request.Name,
                request.NewName,
                request.Color,
                request.Description);
            return Success(ToTagDto(result.Snapshot, result.Data), result.Snapshot);
        });

    public ThreadShelfCommandResult<IReadOnlyList<TagDto>> DeleteTag(DeleteTagRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.DeleteTag(request.Name);
            return Success(GetTagDtos(result.Snapshot), result.Snapshot);
        });

    public ThreadShelfCommandResult<ApplyOrganizationResult> ApplyOrganization(
        ApplyOrganizationRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var updates = request.Threads?
                .Select(update => update is null
                    ? null!
                    : new ThreadOrganizationUpdate(update.ThreadId, update.Folder, update.Tags))
                .ToList();
            var result = service.ApplyOrganization(request.Tags, updates);
            return Success(
                new ApplyOrganizationResult(
                    result.Data.TagsUpserted,
                    result.Data.ThreadsUpdated),
                result.Snapshot);
        });

    public ThreadShelfCommandResult<ThreadDto> ArchiveThread(NativeThreadRequest request) =>
        SetThreadArchived(request, archived: true);

    public ThreadShelfCommandResult<ThreadDto> UnarchiveThread(NativeThreadRequest request) =>
        SetThreadArchived(request, archived: false);

    public ThreadShelfCommandResult<ThreadDto> RenameThread(RenameThreadRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.RenameThread(request.ThreadId, request.Title);
            return Success(ToDto(result.Data), result.Snapshot);
        });

    private ThreadShelfCommandResult<ThreadDto> SetThreadTag(
        ThreadTagRequest request,
        bool assigned) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.SetThreadTag(request.ThreadId, request.Tag, assigned);
            return Success(ToDto(result.Data), result.Snapshot);
        });

    private ThreadShelfCommandResult<ThreadDto> SetThreadArchived(
        NativeThreadRequest request,
        bool archived) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.SetArchived(request.ThreadId, archived);
            return Success(ToDto(result.Data), result.Snapshot);
        });

    private ThreadShelfCommandResult<T> WithService<T>(
        string? codexHome,
        Func<ThreadShelfService, ThreadShelfCommandResult<T>> action)
    {
        try
        {
            var repository = _repositoryFactory(codexHome)
                ?? throw new InvalidOperationException("Repository factory returned null.");
            return action(new ThreadShelfService(repository));
        }
        catch (ThreadShelfValidationException ex)
        {
            return ThreadShelfCommandResult<T>.Failure(
                ToCommandErrorCode(ex.Code),
                ex.Message,
                ex.Details,
                ex.Retryable);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ThreadShelfCommandResult<T>.Failure("permission_denied", ex.Message);
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
            return ThreadShelfCommandResult<T>.Failure("invalid_argument", ex.Message);
        }
    }

    private static string ToCommandErrorCode(string serviceCode) =>
        serviceCode switch
        {
            "thread_id_empty" or
            "metadata_patch_empty" or
            "tag_name_empty" or
            "tag_color_invalid" or
            "thread_title_empty" or
            "organization_empty" or
            "organization_tag_null" or
            "organization_tag_duplicate" or
            "organization_thread_null" or
            "organization_thread_duplicate" => "invalid_argument",
            _ => serviceCode
        };

    private static ThreadShelfCommandResult<T> Success<T>(T data, ThreadShelfSnapshot snapshot) =>
        ThreadShelfCommandResult<T>.Success(data, SourceFrom(snapshot), WarningsFrom(snapshot));

    private static IReadOnlyList<TagDto> GetTagDtos(ThreadShelfSnapshot snapshot) =>
        ThreadFilters.BuildTagSummaries(snapshot.Threads, snapshot.Tags)
            .Select(summary => new TagDto(
                summary.Definition.Name,
                summary.Definition.Color,
                summary.Definition.Description,
                summary.Count))
            .ToList();

    private static TagDto ToTagDto(ThreadShelfSnapshot snapshot, TagDefinition tag)
    {
        var count = snapshot.Threads.Count(thread =>
            thread.Metadata.Tags.Any(name =>
                name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)));
        return new TagDto(tag.Name, tag.Color, tag.Description, count);
    }

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
