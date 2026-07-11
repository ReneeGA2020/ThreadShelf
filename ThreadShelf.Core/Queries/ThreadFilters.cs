using System.Globalization;

namespace ThreadShelf;

public static class ThreadFilters
{
    public const string All = "__all";
    public const string Active = "__active";
    public const string Archived = "__archived";
    public const string Favorites = "__favorites";
    public const string Unfiled = "__unfiled";
    public const string AllProjects = "__all_projects";
    public const string NoProject = "__no_project";

    public static IReadOnlyList<CodexThread> Apply(
        IReadOnlyList<CodexThread> threads,
        string selectedFolder,
        string query,
        string selectedTag) =>
        Apply(threads, AllProjects, selectedFolder, query, selectedTag);

    public static IReadOnlyList<CodexThread> Apply(
        IReadOnlyList<CodexThread> threads,
        string selectedProject,
        string selectedFolder,
        string query,
        string selectedTag,
        IReadOnlyDictionary<string, string>? projectAliases = null)
    {
        var terms = (query ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return threads
            .Where(thread => MatchesProject(thread, selectedProject))
            .Where(thread => MatchesFolder(thread, selectedFolder))
            .Where(thread => MatchesTag(thread, selectedTag))
            .Where(thread => terms.All(term => MatchesTerm(thread, term, projectAliases)))
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToList();
    }

    public static IReadOnlyList<CodexThread> FilterByProject(
        IReadOnlyList<CodexThread> threads,
        string selectedProject) =>
        threads
            .Where(thread => MatchesProject(thread, selectedProject))
            .ToList();

    public static IReadOnlyList<ProjectSummary> BuildProjectSummaries(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyDictionary<string, string>? projectAliases = null)
    {
        var projects = threads
            .Where(thread => NormalizeProjectKey(thread.Workspace).Length > 0)
            .GroupBy(thread => NormalizeProjectKey(thread.Workspace), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Workspace = group.First().Workspace.Trim(),
                BaseName = ProjectDisplayNameForWorkspace(group.Key, projectAliases),
                ParentName = ProjectParentNameForWorkspace(group.Key),
                Count = group.Count()
            })
            .ToList();

        var duplicateBaseNames = projects
            .GroupBy(project => project.BaseName, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        var namedProjects = projects
            .Select(project => new
            {
                project.Key,
                project.Workspace,
                project.BaseName,
                project.Count,
                Name = duplicateBaseNames.Contains(project.BaseName)
                    ? $"{project.BaseName} · {(project.ParentName.Length > 0 ? project.ParentName : project.Workspace)}"
                    : project.BaseName
            })
            .ToList();

        var duplicateDisplayNames = namedProjects
            .GroupBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return namedProjects
            .Select(project => new ProjectSummary(
                project.Key,
                duplicateDisplayNames.Contains(project.Name)
                    ? $"{project.BaseName} · {project.Workspace}"
                    : project.Name,
                project.Workspace,
                project.Count))
            .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
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
            Active => "Unarchived",
            Archived => "Archived",
            Favorites => "Favorites",
            Unfiled => "Unfiled",
            _ => filter
        };

    public static string NormalizeProjectKey(string? workspace)
    {
        var normalized = (workspace ?? "").Trim();
        return normalized.Length == 0
            ? ""
            : normalized.TrimEnd('\\', '/');
    }

    public static string ProjectNameForWorkspace(string? workspace)
    {
        var normalized = NormalizeProjectKey(workspace);
        if (normalized.Length == 0)
        {
            return "No project";
        }

        var separatorIndex = Math.Max(normalized.LastIndexOf('\\'), normalized.LastIndexOf('/'));
        return separatorIndex >= 0 && separatorIndex < normalized.Length - 1
            ? normalized[(separatorIndex + 1)..]
            : normalized;
    }

    public static string ProjectDisplayNameForWorkspace(
        string? workspace,
        IReadOnlyDictionary<string, string>? projectAliases)
    {
        var key = NormalizeProjectKey(workspace);
        return key.Length > 0
            && projectAliases is not null
            && projectAliases.TryGetValue(key, out var alias)
            && !string.IsNullOrWhiteSpace(alias)
                ? alias.Trim()
                : ProjectNameForWorkspace(workspace);
    }

    private static string ProjectParentNameForWorkspace(string? workspace)
    {
        var normalized = NormalizeProjectKey(workspace);
        var separatorIndex = Math.Max(normalized.LastIndexOf('\\'), normalized.LastIndexOf('/'));
        if (separatorIndex <= 0)
        {
            return "";
        }

        var parent = normalized[..separatorIndex].TrimEnd('\\', '/');
        var parentSeparatorIndex = Math.Max(parent.LastIndexOf('\\'), parent.LastIndexOf('/'));
        return parentSeparatorIndex >= 0 && parentSeparatorIndex < parent.Length - 1
            ? parent[(parentSeparatorIndex + 1)..]
            : parent;
    }

    private static bool MatchesProject(CodexThread thread, string selectedProject) =>
        selectedProject switch
        {
            AllProjects => true,
            NoProject => NormalizeProjectKey(thread.Workspace).Length == 0,
            _ => NormalizeProjectKey(thread.Workspace).Equals(
                NormalizeProjectKey(selectedProject),
                StringComparison.OrdinalIgnoreCase)
        };

    private static bool MatchesFolder(CodexThread thread, string selectedFolder) =>
        selectedFolder switch
        {
            All => true,
            Active => !thread.IsArchived,
            Archived => thread.IsArchived,
            Favorites => thread.Metadata.Favorite,
            Unfiled => string.IsNullOrWhiteSpace(thread.Metadata.Folder),
            _ => thread.DisplayFolder.Equals(selectedFolder, StringComparison.OrdinalIgnoreCase)
        };

    private static bool MatchesTag(CodexThread thread, string selectedTag) =>
        string.IsNullOrWhiteSpace(selectedTag)
        || thread.Metadata.Tags.Any(tag => tag.Equals(selectedTag.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool MatchesTerm(
        CodexThread thread,
        string term,
        IReadOnlyDictionary<string, string>? projectAliases)
    {
        var fields = new[]
        {
            thread.DisplayTitle,
            thread.Preview,
            thread.Description,
            thread.Status,
            thread.Id,
            thread.DisplayFolder,
            thread.TagsText,
            thread.Metadata.Notes,
            thread.Workspace,
            ProjectDisplayNameForWorkspace(thread.Workspace, projectAliases),
            thread.Originator,
            thread.Source,
            thread.Model,
            thread.IsArchived ? "archived" : "unarchived active"
        };

        return fields.Any(field => field?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true);
    }
}
