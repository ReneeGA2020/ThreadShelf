using System.Text.Json;

using ThreadShelf;

namespace ThreadShelf.Tests;

[Collection("Process environment")]
public sealed class ThreadShelfServiceTests : IDisposable
{
    private const string ThreadId = "33333333-3333-3333-3333-333333333333";

    private readonly string _codexHome = Path.Combine(
        Path.GetTempPath(),
        "threadshelf-service-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string? _previousCodexCli;

    public ThreadShelfServiceTests()
    {
        _previousCodexCli = Environment.GetEnvironmentVariable("THREADSHELF_CODEX_CLI");
        Environment.SetEnvironmentVariable(
            "THREADSHELF_CODEX_CLI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"));
        CreateFixture();
    }

    [Fact]
    public void MetadataFolderAndTagChangesShareOneApplicationService()
    {
        var service = new ThreadShelfService(new ThreadShelfRepository(_codexHome));

        var metadata = service.SaveThreadMetadata(
            ThreadId,
            new ThreadMetadata
            {
                Folder = "  Planning  ",
                Notes = "  Follow up  ",
                Favorite = true
            });

        Assert.Equal("Planning", metadata.Data.Metadata.Folder);
        Assert.Equal("Follow up", metadata.Data.Metadata.Notes);
        Assert.True(metadata.Data.Metadata.Favorite);

        var tag = service.CreateTag("  bug  ", "d1242f", "Needs a fix");
        Assert.Equal("bug", tag.Data.Name);
        Assert.Equal("#D1242F", tag.Data.Color);

        var tagged = service.SetThreadTag(ThreadId, "BUG", assigned: true);
        Assert.Equal(["bug"], tagged.Data.Metadata.Tags);

        var moved = service.MoveThread(ThreadId, "  Delivery  ");
        Assert.Equal("Delivery", moved.Data.Metadata.Folder);
        Assert.Equal(["bug"], moved.Data.Metadata.Tags);
    }

    [Fact]
    public void TagValidationAndConflictsDoNotPartiallyWrite()
    {
        var service = new ThreadShelfService(new ThreadShelfRepository(_codexHome));
        service.CreateTag("bug", "#D1242F", "");
        var sidecarPath = Path.Combine(_codexHome, "threadshelf", "threadshelf.json");
        var before = File.ReadAllText(sidecarPath);

        var conflict = Assert.Throws<ThreadShelfValidationException>(() =>
            service.CreateTag(" BUG ", "#8250DF", "duplicate"));
        Assert.Equal("tag_conflict", conflict.Code);
        Assert.Equal(before, File.ReadAllText(sidecarPath));

        var invalidColor = Assert.Throws<ThreadShelfValidationException>(() =>
            service.UpdateTag("bug", null, "purple", null));
        Assert.Equal("tag_color_invalid", invalidColor.Code);
        Assert.Equal(before, File.ReadAllText(sidecarPath));
    }

    [Fact]
    public void NativeRenameAndArchiveUseTheSameCapabilityBoundary()
    {
        var repository = new NativeRepository();
        var service = new ThreadShelfService(repository);

        var emptyTitle = Assert.Throws<ThreadShelfValidationException>(() =>
            service.RenameThread(ThreadId, "   "));
        Assert.Equal("thread_title_empty", emptyTitle.Code);

        var renamed = service.RenameThread(ThreadId, "  Renamed  ");
        Assert.Equal("Renamed", renamed.Data.Title);
        Assert.Equal("Renamed", repository.LastTitle);

        var archived = service.SetArchived(ThreadId, archived: true);
        Assert.True(archived.Data.IsArchived);
        Assert.True(repository.LastArchived);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", _previousCodexCli);
        if (Directory.Exists(_codexHome))
        {
            Directory.Delete(_codexHome, recursive: true);
        }
    }

    private void CreateFixture()
    {
        Directory.CreateDirectory(Path.Combine(_codexHome, "sessions", "2026", "07"));
        File.WriteAllText(
            Path.Combine(_codexHome, "session_index.jsonl"),
            JsonSerializer.Serialize(new
            {
                id = ThreadId,
                thread_name = "Shared service test",
                updated_at = "2026-07-10T00:00:00Z"
            }));
        File.WriteAllText(
            Path.Combine(_codexHome, "sessions", "2026", "07", $"{ThreadId}.jsonl"),
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new
                {
                    cwd = @"E:\ServiceTest",
                    originator = "test",
                    source = "fixture",
                    timestamp = "2026-07-10T00:00:00Z"
                }
            }));
    }

    private sealed class NativeRepository : IThreadShelfRepository
    {
        private CodexThread _thread = new()
        {
            Id = ThreadId,
            Title = "Original",
            UpdatedAt = DateTimeOffset.Parse("2026-07-10T00:00:00Z")
        };

        public string CodexHome => @"E:\FakeCodexHome";
        public string SidecarPath => @"E:\FakeCodexHome\threadshelf\threadshelf.json";
        public string LastTitle { get; private set; } = "";
        public bool LastArchived { get; private set; }

        public ThreadShelfSnapshot Load(bool forceRefresh = false) => new()
        {
            Threads = [_thread],
            CodexHome = CodexHome,
            SidecarPath = SidecarPath,
            DataSource = "test app-server",
            SupportsNativeActions = true
        };

        public void SetArchived(string threadId, bool archived)
        {
            LastArchived = archived;
            _thread = _thread with { IsArchived = archived };
        }

        public void SetName(string threadId, string name)
        {
            LastTitle = name;
            _thread = _thread with { Title = name };
        }

        public void SaveMetadata(string threadId, ThreadMetadata metadata) =>
            throw new NotSupportedException();

        public void SaveOrganization(
            IReadOnlyList<TagDefinition> tags,
            IReadOnlyDictionary<string, ThreadMetadata> threads) =>
            throw new NotSupportedException();

        public void SaveTagDefinition(string editingName, TagDefinition definition) =>
            throw new NotSupportedException();

        public void DeleteTagDefinition(string name) => throw new NotSupportedException();

        public void RenameProjectAlias(
            string projectKey,
            string newName,
            IReadOnlyList<CodexThread> threads) =>
            throw new NotSupportedException();

        public void RenameFolder(
            string projectKey,
            string oldName,
            string newName,
            IReadOnlyList<CodexThread> threads) =>
            throw new NotSupportedException();
    }
}
