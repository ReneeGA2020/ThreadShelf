using System.Text.Json;

namespace ThreadShelf.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("Process environment")]
public sealed class CodexInteractiveLauncherTests : IDisposable
{
    private readonly string? _originalCli = Environment.GetEnvironmentVariable("THREADSHELF_CODEX_CLI");
    private readonly string? _originalTerminal = Environment.GetEnvironmentVariable("THREADSHELF_TERMINAL");
    private readonly string? _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    private readonly string? _originalLog = Environment.GetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "threadshelf-launch-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewTaskPlanUsesStructuredArgumentsAndRealWorkspace()
    {
        var workspace = CreateWorkspace("项目 with spaces & symbols");
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", FakeExecutable());
        Environment.SetEnvironmentVariable("THREADSHELF_TERMINAL", "direct");

        var plan = new CodexInteractiveLauncher().CreateNewTaskPlan(workspace);

        Assert.Equal(CodexTerminalKind.DirectConsole, plan.TerminalKind);
        Assert.Equal(Path.GetFullPath(workspace), plan.WorkingDirectory);
        Assert.Equal(["-C", Path.GetFullPath(workspace)], plan.Arguments);
        Assert.Equal(FakeExecutable(), plan.CodexExecutable);
    }

    [Fact]
    public void ResumePlanKeepsSessionIdAsOneArgument()
    {
        var workspace = CreateWorkspace("继续任务 [special] & 中文");
        const string threadId = "abc 123;&not-a-command";
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", FakeExecutable());
        Environment.SetEnvironmentVariable("THREADSHELF_TERMINAL", "direct");

        var plan = new CodexInteractiveLauncher().CreateResumePlan(workspace, threadId);

        Assert.Equal(
            ["resume", "-C", Path.GetFullPath(workspace), threadId],
            plan.Arguments);
    }

    [Fact]
    public void WindowsTerminalPlanKeepsEveryValueSeparate()
    {
        var plan = CodexInteractiveLauncher.BuildPlan(
            @"C:\工作 files\R&D;safe",
            @"C:\Program Files\Codex & Tools\codex.exe",
            ["resume", "-C", @"C:\工作 files\R&D;safe", "session;&safe"],
            @"C:\Program Files\WindowsApps\wt.exe");

        Assert.Equal(CodexTerminalKind.WindowsTerminal, plan.TerminalKind);
        Assert.Equal(
            [
                "-d",
                @"C:\工作 files\R&D;safe",
                @"C:\Program Files\Codex & Tools\codex.exe",
                "resume",
                "-C",
                @"C:\工作 files\R&D;safe",
                "session;&safe"
            ],
            plan.Arguments);
    }

    [Fact]
    public void MissingWorkspaceIsUnavailableBeforeCliCapability()
    {
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", FakeExecutable());
        var workspace = Path.Combine(_root, "does-not-exist");

        var availability = new CodexInteractiveLauncher().CheckAvailability(workspace);

        Assert.False(availability.CanLaunch);
        Assert.Equal(CodexLaunchProblem.WorkspaceNotFound, availability.Problem);
    }

    [Fact]
    public async Task FakeCliRecordsExecutableArgumentsAndWorkingDirectory()
    {
        var workspace = CreateWorkspace("自动化 two 空格 & safe");
        var logPath = Path.Combine(_root, "launch.jsonl");
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", FakeExecutable());
        Environment.SetEnvironmentVariable("THREADSHELF_TERMINAL", "direct");
        Environment.SetEnvironmentVariable("CODEX_HOME", _root);
        Environment.SetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG", logPath);

        new CodexInteractiveLauncher().ResumeThread(workspace, "session-中文 & safe");

        for (var attempt = 0; attempt < 50 && !File.Exists(logPath); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(logPath));
        using var document = JsonDocument.Parse(File.ReadLines(logPath).Single());
        var root = document.RootElement;
        Assert.EndsWith("ThreadShelf.FakeCodexCli.exe", root.GetProperty("executable").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(workspace), root.GetProperty("workingDirectory").GetString());
        Assert.Equal(
            ["resume", "-C", Path.GetFullPath(workspace), "session-中文 & safe"],
            root.GetProperty("arguments").EnumerateArray().Select(item => item.GetString() ?? "").ToArray());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("THREADSHELF_CODEX_CLI", _originalCli);
        Environment.SetEnvironmentVariable("THREADSHELF_TERMINAL", _originalTerminal);
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
        Environment.SetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG", _originalLog);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateWorkspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FakeExecutable()
    {
        var repoRoot = FindRepoRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Debug";
        var candidates = new[]
        {
            Path.Combine(repoRoot, "tests", "fixtures", "ThreadShelf.FakeCodexCli", "bin", configuration, "net10.0", "ThreadShelf.FakeCodexCli.exe"),
            Path.Combine(repoRoot, "tests", "fixtures", "ThreadShelf.FakeCodexCli", "bin", configuration, "net10.0", "win-x64", "ThreadShelf.FakeCodexCli.exe")
        };
        return candidates.First(File.Exists);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ThreadShelf.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "ThreadShelf.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("ThreadShelf repository root was not found.");
    }
}
