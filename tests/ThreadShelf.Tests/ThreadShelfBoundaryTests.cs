using System.Text.Json;

using ThreadShelf;

namespace ThreadShelf.Tests;

public sealed class ThreadShelfBoundaryTests : IDisposable
{
    private const string ThreadId = "55555555-5555-5555-5555-555555555555";

    private readonly string _codexHome = Path.Combine(
        Path.GetTempPath(),
        "threadshelf-boundary-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LocalSourceReadsCodexFactsWhileFacadeAppliesSidecarMetadata()
    {
        CreateLocalFixture();
        var sidecarPath = Path.Combine(_codexHome, "threadshelf", "threadshelf.json");
        new SidecarStore(sidecarPath).SaveMetadata(
            ThreadId,
            new ThreadMetadata { Folder = "From sidecar", Tags = ["ready"] });

        var source = new LocalJsonlThreadSource(_codexHome).Load();
        var sourceThread = Assert.Single(source.Threads);
        Assert.Equal("", sourceThread.Metadata.Folder);
        Assert.Empty(sourceThread.Metadata.Tags);
        Assert.False(source.SupportsNativeActions);

        var repository = new ThreadShelfRepository(
            _codexHome,
            () => new ThrowingSource("app server unavailable"),
            home => new LocalJsonlThreadSource(home),
            new RecordingNativeActions());
        var snapshot = repository.Load();
        var assembled = Assert.Single(snapshot.Threads);

        Assert.Equal("From sidecar", assembled.Metadata.Folder);
        Assert.Equal(["ready"], assembled.Metadata.Tags);
        Assert.Equal("local JSONL files", snapshot.DataSource);
        Assert.False(snapshot.SupportsNativeActions);
        Assert.Contains("app server unavailable", snapshot.LoadWarning);
    }

    [Fact]
    public void LocalSourceReadsJsonlWhileCodexKeepsFilesOpenForWriting()
    {
        CreateLocalFixture();
        var indexPath = Path.Combine(_codexHome, "session_index.jsonl");
        var sessionPath = Path.Combine(
            _codexHome,
            "sessions",
            "2026",
            "07",
            $"{ThreadId}.jsonl");
        using var indexWriter = new FileStream(
            indexPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using var sessionWriter = new FileStream(
            sessionPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var source = new LocalJsonlThreadSource(_codexHome).Load();

        var thread = Assert.Single(source.Threads);
        Assert.Equal(ThreadId, thread.Id);
        Assert.Equal(@"E:\Boundary", thread.Workspace);
    }

    [Fact]
    public void LocalSourceDoesNotInventCurrentUpdatedTimeWhenIndexTimestampIsMissing()
    {
        CreateLocalFixture();
        File.WriteAllText(
            Path.Combine(_codexHome, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = ThreadId, thread_name = "Boundary fixture" }));
        var sessionPath = Path.Combine(
            _codexHome,
            "sessions",
            "2026",
            "07",
            $"{ThreadId}.jsonl");
        var expected = new DateTimeOffset(File.GetLastWriteTimeUtc(sessionPath), TimeSpan.Zero);

        var thread = Assert.Single(new LocalJsonlThreadSource(_codexHome).Load().Threads);

        Assert.Equal(expected, thread.UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T00:00:00Z"), thread.CreatedAt);
    }

    [Fact]
    public void LocalSourceReportsRetryableErrorWhenJsonlIsExclusivelyLocked()
    {
        CreateLocalFixture();
        var sessionPath = Path.Combine(
            _codexHome,
            "sessions",
            "2026",
            "07",
            $"{ThreadId}.jsonl");
        using var exclusiveWriter = new FileStream(
            sessionPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = Assert.Throws<ThreadShelfValidationException>(
            () => new LocalJsonlThreadSource(_codexHome).Load());

        Assert.Equal("local_jsonl_read_failed", exception.Code);
        Assert.True(exception.Retryable);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public void FacadeUsesProviderCodexHomeCapabilitiesAndNativeActions()
    {
        var providerHome = Path.Combine(_codexHome, "provider-home");
        var provider = new StubSource(new ThreadSourceSnapshot(
            [Thread("Provider thread")],
            providerHome,
            "Codex CLI app-server",
            SupportsNativeActions: true,
            LoadedAt: DateTimeOffset.UtcNow));
        var native = new RecordingNativeActions();
        var repository = new ThreadShelfRepository(
            _codexHome,
            () => provider,
            _ => throw new InvalidOperationException("fallback should not run"),
            native);

        var snapshot = repository.Load();
        repository.SetName(ThreadId, "Renamed");
        repository.SetArchived(ThreadId, archived: true);

        Assert.Equal(providerHome, repository.CodexHome);
        Assert.Equal(Path.Combine(providerHome, "threadshelf", "threadshelf.json"), snapshot.SidecarPath);
        Assert.True(snapshot.SupportsNativeActions);
        Assert.Equal("Codex CLI app-server", snapshot.DataSource);
        Assert.Equal((ThreadId, "Renamed"), native.Renamed);
        Assert.Equal((ThreadId, true), native.Archived);
    }

    [Fact]
    public void FacadeCachesProviderIndexUntilForcedRefresh()
    {
        var source = new CountingSource(new ThreadSourceSnapshot(
            [Thread("Cached thread")],
            _codexHome,
            "Codex CLI app-server",
            SupportsNativeActions: true,
            LoadedAt: DateTimeOffset.UtcNow));
        var repository = new ThreadShelfRepository(
            _codexHome,
            () => source,
            _ => throw new InvalidOperationException("fallback should not run"),
            new RecordingNativeActions());

        var cold = repository.Load();
        var hot = repository.Load();
        var refreshed = repository.Load(forceRefresh: true);

        Assert.Equal(2, source.LoadCount);
        Assert.False(cold.IsSourceCached);
        Assert.True(hot.IsSourceCached);
        Assert.False(refreshed.IsSourceCached);
        Assert.Equal(cold.SourceLoadedAt, hot.SourceLoadedAt);
    }

    [Fact]
    public void SidecarTagRenameMigratesReferencesInOneAtomicDocument()
    {
        var sidecarPath = Path.Combine(_codexHome, "threadshelf", "threadshelf.json");
        var store = new SidecarStore(sidecarPath);
        store.SaveOrganization(
            [new TagDefinition { Name = "bug", Color = "#D1242F" }],
            new Dictionary<string, ThreadMetadata>
            {
                [ThreadId] = new() { Folder = "Inbox", Tags = ["bug"] }
            });

        store.SaveTagDefinition(
            "bug",
            new TagDefinition { Name = "defect", Color = "#8250DF" });

        var document = store.Load();
        Assert.DoesNotContain("bug", document.Tags.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("#8250DF", document.Tags["defect"].Color);
        Assert.Equal(["defect"], document.Threads[ThreadId].Tags);
        Assert.False(File.Exists($"{sidecarPath}.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_codexHome))
        {
            Directory.Delete(_codexHome, recursive: true);
        }
    }

    private void CreateLocalFixture()
    {
        var sessions = Path.Combine(_codexHome, "sessions", "2026", "07");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(
            Path.Combine(_codexHome, "session_index.jsonl"),
            JsonSerializer.Serialize(new
            {
                id = ThreadId,
                thread_name = "Boundary fixture",
                updated_at = "2026-07-10T00:00:00Z"
            }));
        File.WriteAllText(
            Path.Combine(sessions, $"{ThreadId}.jsonl"),
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new
                {
                    cwd = @"E:\Boundary",
                    originator = "test",
                    source = "fixture",
                    timestamp = "2026-07-10T00:00:00Z"
                }
            }));
    }

    private static CodexThread Thread(string title) => new()
    {
        Id = ThreadId,
        Title = title,
        UpdatedAt = DateTimeOffset.Parse("2026-07-10T00:00:00Z")
    };

    private sealed class StubSource(ThreadSourceSnapshot snapshot) : IThreadSource
    {
        public ThreadSourceSnapshot Load() => snapshot;
    }

    private sealed class CountingSource(ThreadSourceSnapshot snapshot) : IThreadSource
    {
        public int LoadCount { get; private set; }

        public ThreadSourceSnapshot Load()
        {
            LoadCount++;
            return snapshot with { LoadedAt = DateTimeOffset.UtcNow };
        }
    }

    private sealed class ThrowingSource(string message) : IThreadSource
    {
        public ThreadSourceSnapshot Load() => throw new InvalidOperationException(message);
    }

    private sealed class RecordingNativeActions : IThreadNativeActions
    {
        public (string ThreadId, string Title)? Renamed { get; private set; }
        public (string ThreadId, bool Archived)? Archived { get; private set; }

        public void SetArchived(string threadId, bool archived) =>
            Archived = (threadId, archived);

        public void SetName(string threadId, string name) =>
            Renamed = (threadId, name);
    }
}
