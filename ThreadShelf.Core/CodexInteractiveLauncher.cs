using System.Diagnostics;

namespace ThreadShelf;

public enum CodexLaunchProblem
{
    None,
    CliNotFound,
    WorkspaceMissing,
    WorkspaceNotFound,
    ThreadIdMissing
}

public enum CodexTerminalKind
{
    WindowsTerminal,
    DirectConsole
}

public sealed record CodexLaunchAvailability(
    bool CanLaunch,
    CodexLaunchProblem Problem,
    string Workspace,
    string? CodexExecutable);

public sealed record CodexLaunchPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    CodexTerminalKind TerminalKind,
    string CodexExecutable);

public static class CodexCliLocator
{
    public static string? TryResolveExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("THREADSHELF_CODEX_CLI");
        if (IsFile(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var windowsInstall = Path.Combine(
                localAppData,
                "Programs",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe");
            if (IsFile(windowsInstall))
            {
                return Path.GetFullPath(windowsInstall);
            }
        }

        return FindOnPath("codex");
    }

    public static string ResolveExecutable() =>
        TryResolveExecutable()
        ?? throw new FileNotFoundException(
            "Codex CLI was not found. Install Codex CLI or set THREADSHELF_CODEX_CLI to its executable path.");

    internal static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? PathExtensions()
            : [""];

        foreach (var directoryValue in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = directoryValue.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var fileName = Path.HasExtension(executableName)
                    ? executableName
                    : executableName + extension;
                try
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> PathExtensions()
    {
        var configured = Environment.GetEnvironmentVariable("PATHEXT");
        var values = string.IsNullOrWhiteSpace(configured)
            ? [".EXE", ".CMD", ".BAT", ".COM"]
            : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values
            .Prepend("")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsFile(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed class CodexInteractiveLauncher
{
    public CodexLaunchAvailability CheckAvailability(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return new(false, CodexLaunchProblem.WorkspaceMissing, "", null);
        }

        string fullWorkspace;
        try
        {
            fullWorkspace = Path.GetFullPath(workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(false, CodexLaunchProblem.WorkspaceNotFound, workspace, null);
        }

        if (!Directory.Exists(fullWorkspace))
        {
            return new(false, CodexLaunchProblem.WorkspaceNotFound, fullWorkspace, null);
        }

        var codexExecutable = CodexCliLocator.TryResolveExecutable();
        return codexExecutable is null
            ? new(false, CodexLaunchProblem.CliNotFound, fullWorkspace, null)
            : new(true, CodexLaunchProblem.None, fullWorkspace, codexExecutable);
    }

    public CodexLaunchPlan LaunchNewTask(string workspace) =>
        Start(CreateNewTaskPlan(workspace));

    public CodexLaunchPlan ResumeThread(string workspace, string threadId) =>
        Start(CreateResumePlan(workspace, threadId));

    public CodexLaunchPlan CreateNewTaskPlan(string workspace)
    {
        var availability = RequireAvailability(workspace);
        return BuildPlan(
            availability.Workspace,
            availability.CodexExecutable!,
            ["-C", availability.Workspace],
            ResolveWindowsTerminal());
    }

    public CodexLaunchPlan CreateResumePlan(string workspace, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexLaunchException(CodexLaunchProblem.ThreadIdMissing, "The thread ID is required.");
        }

        var availability = RequireAvailability(workspace);
        return BuildPlan(
            availability.Workspace,
            availability.CodexExecutable!,
            ["resume", "-C", availability.Workspace, threadId],
            ResolveWindowsTerminal());
    }

    public static CodexLaunchPlan BuildPlan(
        string workspace,
        string codexExecutable,
        IReadOnlyList<string> codexArguments,
        string? windowsTerminalExecutable = null)
    {
        if (windowsTerminalExecutable is null)
        {
            return new(
                codexExecutable,
                codexArguments.ToArray(),
                workspace,
                CodexTerminalKind.DirectConsole,
                codexExecutable);
        }

        return new(
            windowsTerminalExecutable,
            ["-d", workspace, codexExecutable, .. codexArguments],
            workspace,
            CodexTerminalKind.WindowsTerminal,
            codexExecutable);
    }

    private static CodexLaunchPlan Start(CodexLaunchPlan plan)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.Executable,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = plan.TerminalKind == CodexTerminalKind.DirectConsole,
            CreateNoWindow = false
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The terminal process did not start.");
            return plan;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
        {
            throw new CodexLaunchException(
                CodexLaunchProblem.None,
                $"Failed to start the Codex terminal: {ex.Message}",
                ex);
        }
    }

    private CodexLaunchAvailability RequireAvailability(string workspace)
    {
        var availability = CheckAvailability(workspace);
        if (availability.CanLaunch)
        {
            return availability;
        }

        var message = availability.Problem switch
        {
            CodexLaunchProblem.CliNotFound =>
                "Codex CLI was not found. Install Codex CLI or set THREADSHELF_CODEX_CLI to its executable path.",
            CodexLaunchProblem.WorkspaceMissing => "This task has no Codex workspace.",
            _ => $"The Codex workspace directory does not exist: {availability.Workspace}"
        };
        throw new CodexLaunchException(availability.Problem, message, detail: availability.Workspace);
    }

    private static string? ResolveWindowsTerminal() =>
        string.Equals(
            Environment.GetEnvironmentVariable("THREADSHELF_TERMINAL"),
            "direct",
            StringComparison.OrdinalIgnoreCase)
            ? null
            : CodexCliLocator.FindOnPath("wt");
}

public sealed class CodexLaunchException : InvalidOperationException
{
    public CodexLaunchException(
        CodexLaunchProblem problem,
        string message,
        Exception? innerException = null,
        string? detail = null)
        : base(message, innerException)
    {
        Problem = problem;
        Detail = detail;
    }

    public CodexLaunchProblem Problem { get; }
    public string? Detail { get; }
}
