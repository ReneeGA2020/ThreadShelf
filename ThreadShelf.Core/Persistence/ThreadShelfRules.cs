using System.Text.RegularExpressions;

namespace ThreadShelf;

internal static class ThreadShelfRules
{
    private static readonly Regex TagColorRegex = new(
        "^#?[0-9a-f]{6}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ThreadMetadata MetadataFrom(EditDraft draft, IEnumerable<string> tags) =>
        NormalizeMetadata(new ThreadMetadata
        {
            Folder = draft.Folder,
            Tags = tags.ToList(),
            Notes = draft.Notes,
            Favorite = draft.Favorite
        });

    public static string NormalizeTagName(string name) => (name ?? "").Trim();

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

    public static ThreadMetadata NormalizeMetadata(ThreadMetadata metadata) =>
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

    public static TagDefinition NormalizeTagDefinition(TagDefinition tag)
    {
        var name = NormalizeTagName(tag.Name);
        return tag with
        {
            Name = name,
            Color = NormalizeTagColor(tag.Color),
            Description = (tag.Description ?? "").Trim()
        };
    }

    public static ThreadShelfDocument NormalizeDocument(ThreadShelfDocument document)
    {
        var normalized = new ThreadShelfDocument
        {
            Version = Math.Max(document.Version, 3)
        };

        foreach (var (threadId, metadata) in document.Threads ?? new Dictionary<string, ThreadMetadata>())
        {
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                normalized.Threads[threadId] = NormalizeMetadata(metadata ?? new ThreadMetadata());
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

        foreach (var (workspace, alias) in document.ProjectAliases ?? new Dictionary<string, string>())
        {
            var normalizedWorkspace = ThreadFilters.NormalizeProjectKey(workspace);
            var normalizedAlias = (alias ?? "").Trim();
            if (normalizedWorkspace.Length > 0 && normalizedAlias.Length > 0)
            {
                normalized.ProjectAliases[normalizedWorkspace] = normalizedAlias;
            }
        }

        return normalized;
    }

    public static IReadOnlyList<TagDefinition> BuildTagDefinitions(
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
}

internal sealed class ThreadShelfDocument
{
    public int Version { get; set; } = 3;
    public Dictionary<string, ThreadMetadata> Threads { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TagDefinition> Tags { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
