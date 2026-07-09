using System.Text.Json;
using ThreadShelf;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThreadShelf.Tests;

public sealed class ThreadShelfCommandServiceTests : IDisposable
{
    private const string FirstThreadId = "11111111-1111-1111-1111-111111111111";
    private const string SecondThreadId = "22222222-2222-2222-2222-222222222222";

    private readonly string _codexHome;
    private readonly string? _previousCodexCli;
    private readonly ThreadShelfCommandService _service = new();

    public ThreadShelfCommandServiceTests()
    {
        _codexHome = Path.Combine(Path.GetTempPath(), "threadshelf-tests", Guid.NewGuid().ToString("N"));
        _previousCodexCli = Environment.GetEnvironmentVariable("THREADSHELF_CODEX_CLI");
        Environment.SetEnvironmentVariable(
            "THREADSHELF_CODEX_CLI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"));

        CreateCodexHomeFixture(_codexHome);
    }

    [Fact]
    public void ListAndSearchUseLocalFallback()
    {
        var list = _service.ListThreads(new ListThreadsRequest { CodexHome = _codexHome });

        AssertSuccess(list);
        Assert.Equal("local-files", list.Source?.Provider);
        Assert.NotEmpty(list.Warnings);
        Assert.Equal(2, list.Data!.Count);

        var search = _service.SearchThreads(new ListThreadsRequest
        {
            CodexHome = _codexHome,
            Query = "login"
        });

        AssertSuccess(search);
        var thread = Assert.Single(search.Data!);
        Assert.Equal(FirstThreadId, thread.Id);
        Assert.Equal(@"E:\Widget", thread.Workspace);
        Assert.Equal("gpt-5", thread.Model);
    }

    [Fact]
    public void MetadataPatchPersistsInSidecar()
    {
        var update = _service.UpdateThreadMetadata(new UpdateThreadMetadataRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId,
            Folder = "Work",
            Notes = "Follow up",
            Favorite = true
        });

        AssertSuccess(update);
        Assert.Equal("Work", update.Data!.Metadata.Folder);
        Assert.Equal("Follow up", update.Data.Metadata.Notes);
        Assert.True(update.Data.Metadata.Favorite);
        Assert.NotNull(update.Data.Metadata.UpdatedAt);

        var get = _service.GetThread(new GetThreadRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId
        });

        AssertSuccess(get);
        Assert.Equal("Work", get.Data!.Metadata.Folder);
        Assert.Equal("Follow up", get.Data.Metadata.Notes);
        Assert.True(get.Data.Metadata.Favorite);
    }

    [Fact]
    public void TagRenameMigratesThreadReferences()
    {
        AssertSuccess(_service.CreateTag(new CreateTagRequest
        {
            CodexHome = _codexHome,
            Name = "bug",
            Color = "#D1242F",
            Description = "Needs a fix"
        }));
        AssertSuccess(_service.AddThreadTag(new ThreadTagRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId,
            Tag = "bug"
        }));

        var update = _service.UpdateTag(new UpdateTagRequest
        {
            CodexHome = _codexHome,
            Name = "bug",
            NewName = "defect",
            Color = "#8250DF"
        });

        AssertSuccess(update);
        Assert.Equal("defect", update.Data!.Name);
        Assert.Equal("#8250DF", update.Data.Color);

        var thread = _service.GetThread(new GetThreadRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId
        });

        AssertSuccess(thread);
        Assert.Contains("defect", thread.Data!.Metadata.Tags);
        Assert.DoesNotContain("bug", thread.Data.Metadata.Tags);
    }

    [Fact]
    public void TagDeleteRemovesThreadReferences()
    {
        AssertSuccess(_service.CreateTag(new CreateTagRequest
        {
            CodexHome = _codexHome,
            Name = "cleanup",
            Color = "#1F883D"
        }));
        AssertSuccess(_service.AddThreadTag(new ThreadTagRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId,
            Tag = "cleanup"
        }));

        var delete = _service.DeleteTag(new DeleteTagRequest
        {
            CodexHome = _codexHome,
            Name = "cleanup"
        });

        AssertSuccess(delete);
        Assert.DoesNotContain(delete.Data!, tag => tag.Name.Equals("cleanup", StringComparison.OrdinalIgnoreCase));

        var thread = _service.GetThread(new GetThreadRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId
        });

        AssertSuccess(thread);
        Assert.DoesNotContain("cleanup", thread.Data!.Metadata.Tags);
    }

    [Fact]
    public void NativeActionReturnsUnsupportedErrorOnFallback()
    {
        var result = _service.ArchiveThread(new NativeThreadRequest
        {
            CodexHome = _codexHome,
            ThreadId = FirstThreadId
        });

        Assert.False(result.Ok);
        Assert.Equal("native_action_unsupported", result.Error?.Code);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", _previousCodexCli);

        try
        {
            if (Directory.Exists(_codexHome))
            {
                Directory.Delete(_codexHome, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AssertSuccess<T>(ThreadShelfCommandResult<T> result)
    {
        Assert.True(result.Ok, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Source);
    }

    private static void CreateCodexHomeFixture(string codexHome)
    {
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions", "2026", "07"));

        File.WriteAllLines(
            Path.Combine(codexHome, "session_index.jsonl"),
            [
                JsonSerializer.Serialize(new
                {
                    id = FirstThreadId,
                    thread_name = "Fix login bug",
                    updated_at = "2026-07-09T08:00:00Z"
                }),
                JsonSerializer.Serialize(new
                {
                    id = SecondThreadId,
                    thread_name = "Write docs",
                    updated_at = "2026-07-08T08:00:00Z"
                })
            ]);

        WriteSession(
            Path.Combine(codexHome, "sessions", "2026", "07", $"{FirstThreadId}.jsonl"),
            @"E:\Widget",
            "gpt-5",
            "2026-07-09T08:00:00Z");

        WriteSession(
            Path.Combine(codexHome, "sessions", "2026", "07", $"{SecondThreadId}.jsonl"),
            @"E:\Docs",
            "gpt-5-mini",
            "2026-07-08T08:00:00Z");
    }

    private static void WriteSession(string path, string cwd, string model, string timestamp)
    {
        File.WriteAllLines(
            path,
            [
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new
                    {
                        cwd,
                        originator = "codex",
                        source = "cli",
                        timestamp
                    }
                }),
                JsonSerializer.Serialize(new
                {
                    type = "turn_context",
                    payload = new
                    {
                        cwd,
                        model
                    }
                })
            ]);
    }
}
