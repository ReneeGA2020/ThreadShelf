namespace ThreadShelf;

public sealed class ThreadShelfRepository : IThreadShelfRepository
{
    private readonly Func<IThreadSource> _appServerSourceFactory;
    private readonly Func<string, IThreadSource> _localSourceFactory;
    private readonly IThreadNativeActions _nativeActions;

    public ThreadShelfRepository(string? codexHome = null)
        : this(
            codexHome
                ?? Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            () => new AppServerThreadSource(),
            home => new LocalJsonlThreadSource(home),
            new CodexThreadNativeActions())
    {
    }

    internal ThreadShelfRepository(
        string codexHome,
        Func<IThreadSource> appServerSourceFactory,
        Func<string, IThreadSource> localSourceFactory,
        IThreadNativeActions nativeActions)
    {
        CodexHome = codexHome;
        _appServerSourceFactory = appServerSourceFactory;
        _localSourceFactory = localSourceFactory;
        _nativeActions = nativeActions;
    }

    public string CodexHome { get; private set; }
    public string SessionIndexPath => Path.Combine(CodexHome, "session_index.jsonl");
    public string SidecarPath => Path.Combine(CodexHome, "threadshelf", "threadshelf.json");

    public ThreadShelfSnapshot Load()
    {
        try
        {
            return BuildSnapshot(_appServerSourceFactory().Load(), loadWarning: "");
        }
        catch (Exception ex)
        {
            var warning = $"Codex CLI app-server unavailable, using local JSONL files: {ex.Message}";
            return BuildSnapshot(_localSourceFactory(CodexHome).Load(), warning);
        }
    }

    public void SaveMetadata(string threadId, ThreadMetadata metadata) =>
        Sidecar().SaveMetadata(threadId, metadata);

    public void SaveOrganization(
        IReadOnlyList<TagDefinition> tags,
        IReadOnlyDictionary<string, ThreadMetadata> threads) =>
        Sidecar().SaveOrganization(tags, threads);

    public void SaveTagDefinition(string editingName, TagDefinition definition) =>
        Sidecar().SaveTagDefinition(editingName, definition);

    public void DeleteTagDefinition(string name) =>
        Sidecar().DeleteTagDefinition(name);

    public void RenameProjectAlias(
        string projectKey,
        string newName,
        IReadOnlyList<CodexThread> threads) =>
        Sidecar().RenameProjectAlias(projectKey, newName, threads);

    public void RenameFolder(
        string projectKey,
        string oldName,
        string newName,
        IReadOnlyList<CodexThread> threads) =>
        Sidecar().RenameFolder(projectKey, oldName, newName, threads);

    public void SetArchived(string threadId, bool archived) =>
        _nativeActions.SetArchived(threadId, archived);

    public void SetName(string threadId, string name) =>
        _nativeActions.SetName(threadId, name);

    public static ThreadMetadata MetadataFrom(EditDraft draft, IEnumerable<string> tags) =>
        ThreadShelfRules.MetadataFrom(draft, tags);

    public static string NormalizeTagName(string name) =>
        ThreadShelfRules.NormalizeTagName(name);

    public static string NormalizeTagColor(string color) =>
        ThreadShelfRules.NormalizeTagColor(color);

    public static bool IsValidTagColor(string color) =>
        ThreadShelfRules.IsValidTagColor(color);

    private ThreadShelfSnapshot BuildSnapshot(ThreadSourceSnapshot source, string loadWarning)
    {
        if (!string.IsNullOrWhiteSpace(source.CodexHome))
        {
            CodexHome = source.CodexHome;
        }

        var sidecar = Sidecar().Load();
        var threads = source.Threads
            .Select(thread => thread with
            {
                Metadata = ThreadShelfRules.NormalizeMetadata(
                    sidecar.Threads.GetValueOrDefault(thread.Id) ?? new ThreadMetadata())
            })
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToList();

        return new ThreadShelfSnapshot
        {
            Threads = threads,
            Tags = ThreadShelfRules.BuildTagDefinitions(sidecar, threads),
            ProjectAliases = sidecar.ProjectAliases,
            CodexHome = CodexHome,
            SidecarPath = SidecarPath,
            DataSource = source.DataSource,
            LoadWarning = loadWarning,
            SupportsNativeActions = source.SupportsNativeActions,
            SupportsNativeProjectRename = false,
            LoadedAt = DateTimeOffset.Now
        };
    }

    private SidecarStore Sidecar() => new(SidecarPath);
}
