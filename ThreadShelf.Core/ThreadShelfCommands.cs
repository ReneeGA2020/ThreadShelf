using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ThreadShelf;

public sealed record ThreadShelfCommandSource(
    string Provider,
    string CodexHome,
    string SidecarPath,
    DateTimeOffset LoadedAt,
    bool Cached);

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
    DateTimeOffset? CreatedAt,
    DateTimeOffset UpdatedAt,
    string Preview,
    string? Description,
    string? Status,
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
    public string Workspace { get; init; } = "";
    public string? UpdatedAfter { get; init; }
    public string? UpdatedBefore { get; init; }
    public string? CreatedAfter { get; init; }
    public string? CreatedBefore { get; init; }
    public IReadOnlyList<string> ExcludeThreadIds { get; init; } = [];
    public IReadOnlyList<string> Fields { get; init; } = [];
    public bool Refresh { get; init; }
}

public sealed record GetThreadRequest
{
    public string? CodexHome { get; init; }
    public string ThreadId { get; init; } = "";
    public bool Refresh { get; init; }
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
    public IReadOnlyList<string>? SetTags { get; init; }
    public IReadOnlyList<string>? AddTags { get; init; }
    public IReadOnlyList<string>? RemoveTags { get; init; }
}

public sealed record ApplyOrganizationRequest
{
    public string? CodexHome { get; init; }
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
    public IReadOnlyList<OrganizationThreadUpdate> Threads { get; init; } = [];
    public bool DryRun { get; init; }
}

public sealed record ApplyOrganizationResult(
    int TagsUpserted,
    int ThreadsUpdated,
    bool DryRun,
    IReadOnlyList<ThreadOrganizationChange> Changes);

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
    private readonly Dictionary<string, ThreadShelfService> _services =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _servicesGate = new();

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
            var snapshot = service.Load(request.Refresh);
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

            var workspace = ThreadFilters.NormalizeProjectKey(request.Workspace);
            if (workspace.Length > 0)
            {
                threads = threads
                    .Where(thread => ThreadFilters.NormalizeProjectKey(thread.Workspace)
                        .Equals(workspace, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var updatedAfter = ParseBoundary(request.UpdatedAfter, nameof(request.UpdatedAfter));
            var updatedBefore = ParseBoundary(request.UpdatedBefore, nameof(request.UpdatedBefore));
            var createdAfter = ParseBoundary(request.CreatedAfter, nameof(request.CreatedAfter));
            var createdBefore = ParseBoundary(request.CreatedBefore, nameof(request.CreatedBefore));
            threads = threads
                .Where(thread => updatedAfter is null || thread.UpdatedAt > updatedAfter)
                .Where(thread => updatedBefore is null || thread.UpdatedAt < updatedBefore)
                .Where(thread => createdAfter is null || thread.CreatedAt > createdAfter)
                .Where(thread => createdBefore is null || thread.CreatedAt < createdBefore)
                .ToList();

            if (request.ExcludeThreadIds.Count > 0)
            {
                var excluded = request.ExcludeThreadIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                threads = threads.Where(thread => !excluded.Contains(thread.Id)).ToList();
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

    public ThreadShelfCommandResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListThreadsProjected(
        ListThreadsRequest request)
    {
        var result = ListThreads(request);
        if (!result.Ok)
        {
            return ThreadShelfCommandResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>.Failure(
                result.Error!.Code,
                result.Error.Message,
                result.Error.Details,
                result.Error.Retryable);
        }

        try
        {
            var fields = NormalizeFields(request.Fields);
            var data = result.Data!.Select(thread => ProjectThread(thread, fields)).ToList();
            return ThreadShelfCommandResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>.Success(
                data,
                result.Source!,
                result.Warnings);
        }
        catch (ThreadShelfValidationException ex)
        {
            return ThreadShelfCommandResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>.Failure(
                "invalid_argument", ex.Message, ex.Details);
        }
    }

    public ThreadShelfCommandResult<ThreadDto> GetThread(GetThreadRequest request) =>
        WithService(request.CodexHome, service =>
        {
            var result = service.GetThread(request.ThreadId, request.Refresh);
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
                    : new ThreadOrganizationUpdate(
                        update.ThreadId,
                        update.Folder,
                        update.Tags,
                        update.SetTags,
                        update.AddTags,
                        update.RemoveTags))
                .ToList();
            var result = service.ApplyOrganization(request.Tags, updates, request.DryRun);
            return Success(
                new ApplyOrganizationResult(
                    result.Data.TagsUpserted,
                    result.Data.ThreadsUpdated,
                    result.Data.DryRun,
                    result.Data.Changes),
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
            var key = string.IsNullOrWhiteSpace(codexHome) ? "<default>" : Path.GetFullPath(codexHome);
            ThreadShelfService service;
            lock (_servicesGate)
            {
                if (!_services.TryGetValue(key, out service!))
                {
                    var repository = _repositoryFactory(codexHome)
                        ?? throw new InvalidOperationException("Repository factory returned null.");
                    service = new ThreadShelfService(repository);
                    _services[key] = service;
                }
            }

            return action(service);
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
            "organization_tag_operation_conflict" or
            "organization_thread_null" or
            "organization_thread_duplicate" or
            "organization_thread_empty" => "invalid_argument",
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
            thread.CreatedAt,
            thread.UpdatedAt,
            thread.Preview,
            thread.Description,
            thread.Status,
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
            snapshot.SidecarPath,
            snapshot.SourceLoadedAt,
            snapshot.IsSourceCached);

    private static DateTimeOffset? ParseBoundary(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var hasZone = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || text.Length >= 6
                && text[^3] == ':'
                && text[^6] is '+' or '-';
        if (!hasZone || !DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed))
        {
            throw new ThreadShelfValidationException(
                "invalid_argument",
                $"{name} must be an ISO 8601 timestamp with an explicit timezone.",
                new { name, value });
        }

        return parsed;
    }

    private static IReadOnlyList<string> NormalizeFields(IReadOnlyList<string> requested)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "title", "createdAt", "updatedAt", "preview", "description", "status",
            "workspace", "model", "sourcePath", "isArchived", "fileSizeBytes", "originator",
            "source", "folder", "tags", "notes", "favorite", "metadataUpdatedAt"
        };
        var fields = requested
            .SelectMany(field => (field ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = fields.FirstOrDefault(field => !allowed.Contains(field));
        if (invalid is not null)
        {
            throw new ThreadShelfValidationException(
                "invalid_argument",
                $"Unknown thread field '{invalid}'.",
                new { field = invalid, allowed });
        }

        return fields.Count > 0 ? fields : ["id", "title", "updatedAt", "workspace", "isArchived", "tags"];
    }

    private static IReadOnlyDictionary<string, object?> ProjectThread(
        ThreadDto thread,
        IReadOnlyList<string> fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            values[field] = field.ToLowerInvariant() switch
            {
                "id" => thread.Id,
                "title" => thread.Title,
                "createdat" => thread.CreatedAt,
                "updatedat" => thread.UpdatedAt,
                "preview" => thread.Preview,
                "description" => thread.Description,
                "status" => thread.Status,
                "workspace" => thread.Workspace,
                "model" => thread.Model,
                "sourcepath" => thread.SourcePath,
                "isarchived" => thread.IsArchived,
                "filesizebytes" => thread.FileSizeBytes,
                "originator" => thread.Originator,
                "source" => thread.Source,
                "folder" => thread.Metadata.Folder,
                "tags" => thread.Metadata.Tags,
                "notes" => thread.Metadata.Notes,
                "favorite" => thread.Metadata.Favorite,
                "metadataupdatedat" => thread.Metadata.UpdatedAt,
                _ => null
            };
        }

        return values;
    }

    private static IReadOnlyList<string> WarningsFrom(ThreadShelfSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.LoadWarning)
            ? []
            : [snapshot.LoadWarning];
}
