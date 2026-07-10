using System.Text.Json;

namespace ThreadShelf.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("Process environment")]
public sealed class CodexInteractiveLauncherTests : IDisposable
{
    private readonly string? _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    private readonly string? _originalLog = Environment.GetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "threadshelf-launch-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DesktopAppIsPreferredWhenDesktopAndCliAreAvailable()
    {
        var workspace = CreateWorkspace("桌面 app & CLI");
        var cliProbeCount = 0;
        var launcher = new CodexInteractiveLauncher(
            desktopAppAvailable: () => true,
            cliExecutable: () =>
            {
                cliProbeCount++;
                return FakeExecutable();
            },
            windowsTerminalExecutable: () => null);

        var plan = launcher.CreateResumePlan(workspace, "thread/中文 & safe");

        Assert.Equal(CodexLaunchProvider.DesktopApp, plan.Provider);
        Assert.Equal(CodexTerminalKind.None, plan.TerminalKind);
        Assert.Equal("codex://threads/thread%2F%E4%B8%AD%E6%96%87%20%26%20safe", plan.Executable);
        Assert.Empty(plan.Arguments);
        Assert.Null(plan.CodexExecutable);
        Assert.Equal(0, cliProbeCount);
    }

    [Fact]
    public void DesktopNewTaskPlanEscapesAbsoluteWorkspacePath()
    {
        var workspace = CreateWorkspace("项目 with spaces & symbols");
        var launcher = new CodexInteractiveLauncher(
            desktopAppAvailable: () => true,
            cliExecutable: () => null,
            windowsTerminalExecutable: () => null);

        var plan = launcher.CreateNewTaskPlan(workspace);

        Assert.Equal(CodexLaunchProvider.DesktopApp, plan.Provider);
        Assert.Equal(
            $"codex://threads/new?path={Uri.EscapeDataString(Path.GetFullPath(workspace))}",
            plan.Executable);
        Assert.Equal(Path.GetFullPath(workspace), plan.WorkingDirectory);
    }

    [Fact]
    public void CliIsUsedWhenDesktopAppIsUnavailable()
    {
        var workspace = CreateWorkspace("项目 with spaces & symbols");
        var launcher = CliOnlyLauncher();

        var plan = launcher.CreateNewTaskPlan(workspace);

        Assert.Equal(CodexLaunchProvider.Cli, plan.Provider);
        Assert.Equal(CodexTerminalKind.DirectConsole, plan.TerminalKind);
        Assert.Equal(Path.GetFullPath(workspace), plan.WorkingDirectory);
        Assert.Equal(["-C", Path.GetFullPath(workspace)], plan.Arguments);
        Assert.Equal(FakeExecutable(), plan.CodexExecutable);
    }

    [Fact]
    public void CliResumePlanKeepsSessionIdAsOneArgument()
    {
        var workspace = CreateWorkspace("继续任务 [special] & 中文");
        const string threadId = "abc 123;&not-a-command";

        var plan = CliOnlyLauncher().CreateResumePlan(workspace, threadId);

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

        Assert.Equal(CodexLaunchProvider.Cli, plan.Provider);
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
    public void NeitherDesktopNorCliIsUnavailable()
    {
        var workspace = CreateWorkspace("no provider");
        var launcher = new CodexInteractiveLauncher(
            desktopAppAvailable: () => false,
            cliExecutable: () => null,
            windowsTerminalExecutable: () => null);

        var availability = launcher.CheckNewTaskAvailability(workspace);

        Assert.False(availability.CanLaunch);
        Assert.Equal(CodexLaunchProblem.CodexUnavailable, availability.Problem);
        Assert.Equal(CodexLaunchProvider.None, availability.Provider);
    }

    [Fact]
    public void MissingWorkspaceIsUnavailableBeforeProviderCapability()
    {
        var launcher = new CodexInteractiveLauncher(
            desktopAppAvailable: () => true,
            cliExecutable: () => FakeExecutable(),
            windowsTerminalExecutable: () => null);

        var availability = launcher.CheckNewTaskAvailability("  ");

        Assert.False(availability.CanLaunch);
        Assert.Equal(CodexLaunchProblem.WorkspaceMissing, availability.Problem);
    }

    [Fact]
    public void NonexistentWorkspaceIsUnavailableBeforeProviderCapability()
    {
        var workspace = Path.Combine(_root, "does-not-exist");
        var launcher = new CodexInteractiveLauncher(
            desktopAppAvailable: () => true,
            cliExecutable: () => FakeExecutable(),
            windowsTerminalExecutable: () => null);

        var availability = launcher.CheckNewTaskAvailability(workspace);

        Assert.False(availability.CanLaunch);
        Assert.Equal(CodexLaunchProblem.WorkspaceNotFound, availability.Problem);
    }

    [Fact]
    public void MissingThreadIdMakesResumeUnavailable()
    {
        var workspace = CreateWorkspace("missing id");

        var availability = CliOnlyLauncher().CheckResumeAvailability(workspace, " ");

        Assert.False(availability.CanLaunch);
        Assert.Equal(CodexLaunchProblem.ThreadIdMissing, availability.Problem);
    }

    [Fact]
    public async Task FakeCliRecordsExecutableArgumentsAndWorkingDirectory()
    {
        var workspace = CreateWorkspace("自动化 two 空格 & safe");
        var logPath = Path.Combine(_root, "launch.jsonl");
        Environment.SetEnvironmentVariable("CODEX_HOME", _root);
        Environment.SetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG", logPath);

        CliOnlyLauncher().ResumeThread(workspace, "session-中文 & safe");

        for (var attempt = 0; attempt < 50 && !File.Exists(logPath); attempt++)
        {
            await Task.Delay(50);
        }

        string? logLine = null;
        for (var attempt = 0; attempt < 50 && logLine is null; attempt++)
        {
            try
            {
                logLine = File.ReadLines(logPath).Single();
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(logLine);
        using var document = JsonDocument.Parse(logLine);
        var root = document.RootElement;
        Assert.EndsWith("ThreadShelf.FakeCodexCli.exe", root.GetProperty("executable").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(workspace), root.GetProperty("workingDirectory").GetString());
        Assert.Equal(
            ["resume", "-C", Path.GetFullPath(workspace), "session-中文 & safe"],
            root.GetProperty("arguments").EnumerateArray().Select(item => item.GetString() ?? "").ToArray());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
        Environment.SetEnvironmentVariable("THREADSHELF_FAKE_LAUNCH_LOG", _originalLog);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CodexInteractiveLauncher CliOnlyLauncher() =>
        new(
            desktopAppAvailable: () => false,
            cliExecutable: () => FakeExecutable(),
            windowsTerminalExecutable: () => null);

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

public sealed class ThreadShelfSystemActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "threadshelf-folder-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenFolderPlanKeepsSpecialWorkspaceAsOneArgument()
    {
        var workspace = Path.Combine(_root, "folder with 空格 & (special);safe");
        Directory.CreateDirectory(workspace);

        var plan = ThreadShelfSystemActions.CreateOpenFolderPlan(workspace);

        Assert.Equal("explorer.exe", plan.Executable);
        Assert.Equal([Path.GetFullPath(workspace)], plan.Arguments);
    }

    [Theory]
    [InlineData(null, FolderOpenProblem.PathMissing)]
    [InlineData("", FolderOpenProblem.PathMissing)]
    public void MissingFolderIsUnavailable(string? path, FolderOpenProblem expected)
    {
        var availability = ThreadShelfSystemActions.CheckFolderAvailability(path);

        Assert.False(availability.CanOpen);
        Assert.Equal(expected, availability.Problem);
    }

    [Fact]
    public void NonexistentFolderIsUnavailable()
    {
        var availability = ThreadShelfSystemActions.CheckFolderAvailability(
            Path.Combine(_root, "missing"));

        Assert.False(availability.CanOpen);
        Assert.Equal(FolderOpenProblem.DirectoryNotFound, availability.Problem);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
