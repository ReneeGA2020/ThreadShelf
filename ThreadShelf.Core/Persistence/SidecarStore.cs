using System.Text.Json;

namespace ThreadShelf;

internal sealed class SidecarStore(string sidecarPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string SidecarPath { get; } = sidecarPath;

    public ThreadShelfDocument Load()
    {
        if (!File.Exists(SidecarPath))
        {
            return new ThreadShelfDocument();
        }

        try
        {
            using var stream = File.OpenRead(SidecarPath);
            return ThreadShelfRules.NormalizeDocument(
                JsonSerializer.Deserialize<ThreadShelfDocument>(stream, JsonOptions)
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

    public void SaveMetadata(string threadId, ThreadMetadata metadata)
    {
        var document = Load();
        document.Threads[threadId] = ThreadShelfRules.NormalizeMetadata(metadata) with
        {
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Save(document);
    }

    public void SaveOrganization(
        IReadOnlyList<TagDefinition> tags,
        IReadOnlyDictionary<string, ThreadMetadata> threads)
    {
        var document = Load();

        foreach (var tag in tags)
        {
            var normalized = ThreadShelfRules.NormalizeTagDefinition(tag);
            if (normalized.Name.Length == 0)
            {
                throw new InvalidOperationException("Tag name cannot be empty.");
            }

            document.Tags[normalized.Name] = normalized;
        }

        var updatedAt = DateTimeOffset.UtcNow;
        foreach (var (threadId, metadata) in threads)
        {
            document.Threads[threadId] = ThreadShelfRules.NormalizeMetadata(metadata) with
            {
                UpdatedAt = updatedAt
            };
        }

        Save(document);
    }

    public void SaveTagDefinition(string editingName, TagDefinition definition)
    {
        var normalized = ThreadShelfRules.NormalizeTagDefinition(definition);
        if (normalized.Name.Length == 0)
        {
            throw new InvalidOperationException("Tag name cannot be empty.");
        }

        var oldName = ThreadShelfRules.NormalizeTagName(editingName);
        var document = Load();

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

                document.Threads[threadId] = ThreadShelfRules.NormalizeMetadata(metadata with
                {
                    Tags = metadata.Tags
                        .Select(tag => tag.Equals(oldName, StringComparison.OrdinalIgnoreCase)
                            ? normalized.Name
                            : tag)
                        .ToList()
                });
            }
        }

        document.Tags[normalized.Name] = normalized;
        Save(document);
    }

    public void DeleteTagDefinition(string name)
    {
        var normalizedName = ThreadShelfRules.NormalizeTagName(name);
        if (normalizedName.Length == 0)
        {
            throw new InvalidOperationException("Tag name cannot be empty.");
        }

        var document = Load();
        document.Tags.Remove(normalizedName);

        foreach (var (threadId, metadata) in document.Threads.ToArray())
        {
            if (!metadata.Tags.Any(tag => tag.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            document.Threads[threadId] = ThreadShelfRules.NormalizeMetadata(metadata with
            {
                Tags = metadata.Tags
                    .Where(tag => !tag.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        Save(document);
    }

    public void RenameProjectAlias(
        string projectKey,
        string newName,
        IReadOnlyList<CodexThread> threads)
    {
        var normalizedKey = ThreadFilters.NormalizeProjectKey(projectKey);
        var trimmedName = (newName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ThreadShelfValidationException("rename_name_empty", "Name cannot be empty.");
        }

        var projectKeys = threads
            .Select(thread => ThreadFilters.NormalizeProjectKey(thread.Workspace))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedKey.Length == 0
            || !projectKeys.Contains(normalizedKey, StringComparer.OrdinalIgnoreCase))
        {
            throw new ThreadShelfValidationException("project_not_found", "Project was not found.");
        }

        var document = Load();
        foreach (var otherKey in projectKeys.Where(key =>
            !key.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase)))
        {
            var otherName = ThreadFilters.ProjectDisplayNameForWorkspace(otherKey, document.ProjectAliases);
            if (trimmedName.Equals(otherName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ThreadShelfValidationException(
                    "rename_name_conflict",
                    "A project with that name already exists.");
            }
        }

        document.ProjectAliases[normalizedKey] = trimmedName;
        Save(document);
    }

    public void RenameFolder(
        string projectKey,
        string oldName,
        string newName,
        IReadOnlyList<CodexThread> threads)
    {
        var normalizedOldName = (oldName ?? "").Trim();
        var trimmedNewName = (newName ?? "").Trim();
        if (trimmedNewName.Length == 0)
        {
            throw new ThreadShelfValidationException("rename_name_empty", "Name cannot be empty.");
        }

        var projectThreads = ThreadFilters.FilterByProject(threads, projectKey);
        var affected = projectThreads
            .Where(thread => thread.Metadata.Folder.Trim().Equals(
                normalizedOldName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (normalizedOldName.Length == 0 || affected.Length == 0)
        {
            throw new ThreadShelfValidationException(
                "folder_not_found",
                "Folder was not found in this project.");
        }

        var conflicts = projectThreads.Any(thread =>
            !thread.Metadata.Folder.Trim().Equals(normalizedOldName, StringComparison.OrdinalIgnoreCase)
            && thread.Metadata.Folder.Trim().Equals(trimmedNewName, StringComparison.OrdinalIgnoreCase));
        if (conflicts)
        {
            throw new ThreadShelfValidationException(
                "rename_name_conflict",
                "A folder with that name already exists in this project.");
        }

        if (normalizedOldName.Equals(trimmedNewName, StringComparison.Ordinal))
        {
            return;
        }

        var document = Load();
        var updatedAt = DateTimeOffset.UtcNow;
        foreach (var thread in affected)
        {
            var metadata = document.Threads.GetValueOrDefault(thread.Id) ?? thread.Metadata;
            document.Threads[thread.Id] = ThreadShelfRules.NormalizeMetadata(metadata with
            {
                Folder = trimmedNewName,
                UpdatedAt = updatedAt
            });
        }

        Save(document);
    }

    private void Save(ThreadShelfDocument document)
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
}
