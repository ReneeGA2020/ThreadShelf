using System.Text.Json;

using ThreadShelf;

namespace ThreadShelf.Tests;

[Collection("Process environment")]
public sealed class ThreadShelfRepositoryRenameTests : IDisposable
{
    private const string AlphaOneId = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string AlphaTwoId = "aaaaaaaa-2222-2222-2222-222222222222";
    private const string AlphaExistingId = "aaaaaaaa-3333-3333-3333-333333333333";
    private const string BetaId = "bbbbbbbb-1111-1111-1111-111111111111";

    private readonly string _codexHome = Path.Combine(
        Path.GetTempPath(),
        "threadshelf-rename-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string? _previousCodexCli;

    public ThreadShelfRepositoryRenameTests()
    {
        _previousCodexCli = Environment.GetEnvironmentVariable("THREADSHELF_CODEX_CLI");
        Environment.SetEnvironmentVariable(
            "THREADSHELF_CODEX_CLI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"));
    }

    [Fact]
    public void ProjectAlias_PersistsAcrossReloadAndProviderReportsFallbackCapability()
    {
        var threads = CreateFixture();
        var repository = new ThreadShelfRepository(_codexHome);

        repository.RenameProjectAlias(@"E:\Work\Alpha\", "  Client Portal  ", threads);

        var snapshot = new ThreadShelfRepository(_codexHome).Load();
        Assert.Equal("Client Portal", snapshot.ProjectAliases[@"E:\Work\Alpha"]);
        Assert.False(snapshot.SupportsNativeProjectRename);
        Assert.Equal(
            "Client Portal",
            ThreadFilters.BuildProjectSummaries(snapshot.Threads, snapshot.ProjectAliases)
                .Single(project => project.Key == @"E:\Work\Alpha")
                .Name);
    }

    [Fact]
    public void FolderRename_UpdatesOnlyCurrentProjectInOneSidecarWrite()
    {
        var threads = CreateFixture();
        var repository = new ThreadShelfRepository(_codexHome);

        repository.RenameFolder(@"E:\Work\Alpha", "Planning", " Delivery ", threads);

        var snapshot = repository.Load();
        Assert.Equal("Delivery", snapshot.Threads.Single(thread => thread.Id == AlphaOneId).Metadata.Folder);
        Assert.Equal("Delivery", snapshot.Threads.Single(thread => thread.Id == AlphaTwoId).Metadata.Folder);
        Assert.Equal("Existing", snapshot.Threads.Single(thread => thread.Id == AlphaExistingId).Metadata.Folder);
        Assert.Equal("Planning", snapshot.Threads.Single(thread => thread.Id == BetaId).Metadata.Folder);
    }

    [Fact]
    public void RenameConflict_DoesNotPartiallyModifySidecar()
    {
        var threads = CreateFixture();
        var repository = new ThreadShelfRepository(_codexHome);
        var before = File.ReadAllText(repository.SidecarPath);

        var folderError = Assert.Throws<ThreadShelfValidationException>(() =>
            repository.RenameFolder(@"E:\Work\Alpha", "Planning", "existing", threads));
        Assert.Equal("rename_name_conflict", folderError.Code);
        Assert.Equal(before, File.ReadAllText(repository.SidecarPath));

        var projectError = Assert.Throws<ThreadShelfValidationException>(() =>
            repository.RenameProjectAlias(@"E:\Work\Alpha", "Beta", threads));
        Assert.Equal("rename_name_conflict", projectError.Code);
        Assert.Equal(before, File.ReadAllText(repository.SidecarPath));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", _previousCodexCli);
        if (Directory.Exists(_codexHome))
        {
            Directory.Delete(_codexHome, recursive: true);
        }
    }

    private IReadOnlyList<CodexThread> CreateFixture()
    {
        var threads = new[]
        {
            Thread(AlphaOneId, @"E:\Work\Alpha", "Planning"),
            Thread(AlphaTwoId, @"E:\Work\Alpha", "Planning"),
            Thread(AlphaExistingId, @"E:\Work\Alpha", "Existing"),
            Thread(BetaId, @"E:\Work\Beta", "Planning")
        };
        var repository = new ThreadShelfRepository(_codexHome);
        foreach (var thread in threads)
        {
            repository.SaveMetadata(thread.Id, thread.Metadata);
        }

        Directory.CreateDirectory(Path.Combine(_codexHome, "sessions", "2026", "07"));
        File.WriteAllLines(
            Path.Combine(_codexHome, "session_index.jsonl"),
            threads.Select(thread => JsonSerializer.Serialize(new
            {
                id = thread.Id,
                thread_name = thread.Title,
                updated_at = "2026-07-10T00:00:00Z"
            })));
        foreach (var thread in threads)
        {
            File.WriteAllText(
                Path.Combine(_codexHome, "sessions", "2026", "07", $"{thread.Id}.jsonl"),
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new
                    {
                        cwd = thread.Workspace,
                        originator = "test",
                        source = "fixture",
                        timestamp = "2026-07-10T00:00:00Z"
                    }
                }));
        }

        return threads;
    }

    private static CodexThread Thread(string id, string workspace, string folder) => new()
    {
        Id = id,
        Title = id,
        Workspace = workspace,
        UpdatedAt = DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        Metadata = new ThreadMetadata { Folder = folder }
    };
}
