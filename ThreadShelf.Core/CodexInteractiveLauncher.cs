using System.Diagnostics;
using System.Security;

using Microsoft.Win32;

namespace ThreadShelf;

public enum CodexLaunchProblem
{
    None,
    CodexUnavailable,
    WorkspaceMissing,
    WorkspaceNotFound,
    ThreadIdMissing
}

public enum CodexLaunchProvider
{
    None,
    DesktopApp,
    Cli
}

public enum CodexTerminalKind
{
    None,
    WindowsTerminal,
    DirectConsole
}

public sealed record CodexLaunchAvailability(
    bool CanLaunch,
    CodexLaunchProblem Problem,
    string Workspace,
    CodexLaunchProvider Provider,
    string? CodexExecutable);

public sealed record CodexLaunchPlan(
    CodexLaunchProvider Provider,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    CodexTerminalKind TerminalKind,
    string? CodexExecutable);

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

public static class CodexDesktopLocator
{
    private const string AppModelPackagesKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (Registry.ClassesRoot.OpenSubKey("codex") is not null)
            {
                return true;
            }

            using var packages = Registry.CurrentUser.OpenSubKey(AppModelPackagesKey);
            if (packages is null)
            {
                return false;
            }

            foreach (var packageName in packages.GetSubKeyNames())
            {
                using var associations = packages.OpenSubKey(
                    $@"{packageName}\App\Capabilities\URLAssociations");
                if (associations?.GetValue("codex") is string value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or SecurityException)
        {
        }

        return false;
    }
}

public sealed class CodexInteractiveLauncher
{
    private readonly Func<bool> _desktopAppAvailable;
    private readonly Func<string?> _cliExecutable;
    private readonly Func<string?> _windowsTerminalExecutable;
    private readonly Action<CodexLaunchPlan> _start;

    public CodexInteractiveLauncher(
        Func<bool>? desktopAppAvailable = null,
        Func<string?>? cliExecutable = null,
        Func<string?>? windowsTerminalExecutable = null,
        Action<CodexLaunchPlan>? start = null)
    {
        _desktopAppAvailable = desktopAppAvailable ?? CodexDesktopLocator.IsAvailable;
        _cliExecutable = cliExecutable ?? CodexCliLocator.TryResolveExecutable;
        _windowsTerminalExecutable = windowsTerminalExecutable ?? ResolveWindowsTerminal;
        _start = start ?? Start;
    }

    public CodexLaunchAvailability CheckAvailability(string? workspace) =>
        CheckNewTaskAvailability(workspace);

    public CodexLaunchAvailability CheckNewTaskAvailability(string? workspace) =>
        CheckWorkspaceAndProvider(workspace);

    public CodexLaunchAvailability CheckResumeAvailability(string? workspace, string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new(
                false,
                CodexLaunchProblem.ThreadIdMissing,
                NormalizeWorkspaceForDetail(workspace),
                CodexLaunchProvider.None,
                null);
        }

        return CheckWorkspaceAndProvider(workspace);
    }

    public CodexLaunchPlan LaunchNewTask(string workspace)
    {
        var plan = CreateNewTaskPlan(workspace);
        _start(plan);
        return plan;
    }

    public CodexLaunchPlan ResumeThread(string workspace, string threadId)
    {
        var plan = CreateResumePlan(workspace, threadId);
        _start(plan);
        return plan;
    }

    public CodexLaunchPlan CreateNewTaskPlan(string workspace)
    {
        var availability = RequireAvailability(CheckNewTaskAvailability(workspace));
        return availability.Provider == CodexLaunchProvider.DesktopApp
            ? DesktopPlan(
                $"codex://threads/new?path={Uri.EscapeDataString(availability.Workspace)}",
                availability.Workspace)
            : BuildPlan(
                availability.Workspace,
                availability.CodexExecutable!,
                ["-C", availability.Workspace],
                _windowsTerminalExecutable());
    }

    public CodexLaunchPlan CreateResumePlan(string workspace, string threadId)
    {
        var availability = RequireAvailability(CheckResumeAvailability(workspace, threadId));
        return availability.Provider == CodexLaunchProvider.DesktopApp
            ? DesktopPlan(
                $"codex://threads/{Uri.EscapeDataString(threadId)}",
                availability.Workspace)
            : BuildPlan(
                availability.Workspace,
                availability.CodexExecutable!,
                ["resume", "-C", availability.Workspace, threadId],
                _windowsTerminalExecutable());
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
                CodexLaunchProvider.Cli,
                codexExecutable,
                codexArguments.ToArray(),
                workspace,
                CodexTerminalKind.DirectConsole,
                codexExecutable);
        }

        return new(
            CodexLaunchProvider.Cli,
            windowsTerminalExecutable,
            ["-d", workspace, codexExecutable, .. codexArguments],
            workspace,
            CodexTerminalKind.WindowsTerminal,
            codexExecutable);
    }

    private CodexLaunchAvailability CheckWorkspaceAndProvider(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return new(
                false,
                CodexLaunchProblem.WorkspaceMissing,
                "",
                CodexLaunchProvider.None,
                null);
        }

        string fullWorkspace;
        try
        {
            fullWorkspace = Path.GetFullPath(workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(
                false,
                CodexLaunchProblem.WorkspaceNotFound,
                workspace,
                CodexLaunchProvider.None,
                null);
        }

        if (!Directory.Exists(fullWorkspace))
        {
            return new(
                false,
                CodexLaunchProblem.WorkspaceNotFound,
                fullWorkspace,
                CodexLaunchProvider.None,
                null);
        }

        if (_desktopAppAvailable())
        {
            return new(
                true,
                CodexLaunchProblem.None,
                fullWorkspace,
                CodexLaunchProvider.DesktopApp,
                null);
        }

        var codexExecutable = _cliExecutable();
        return codexExecutable is null
            ? new(
                false,
                CodexLaunchProblem.CodexUnavailable,
                fullWorkspace,
                CodexLaunchProvider.None,
                null)
            : new(
                true,
                CodexLaunchProblem.None,
                fullWorkspace,
                CodexLaunchProvider.Cli,
                codexExecutable);
    }

    private static CodexLaunchPlan DesktopPlan(string uri, string workspace) =>
        new(
            CodexLaunchProvider.DesktopApp,
            uri,
            [],
            workspace,
            CodexTerminalKind.None,
            null);

    private static void Start(CodexLaunchPlan plan)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.Executable,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = plan.Provider == CodexLaunchProvider.DesktopApp
                || plan.TerminalKind == CodexTerminalKind.DirectConsole,
            CreateNoWindow = false
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Codex process did not start.");
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
        {
            throw new CodexLaunchException(
                CodexLaunchProblem.None,
                $"Failed to start Codex: {ex.Message}",
                ex);
        }
    }

    private static CodexLaunchAvailability RequireAvailability(CodexLaunchAvailability availability)
    {
        if (availability.CanLaunch)
        {
            return availability;
        }

        var message = availability.Problem switch
        {
            CodexLaunchProblem.CodexUnavailable =>
                "Neither the Codex desktop app nor Codex CLI is available.",
            CodexLaunchProblem.WorkspaceMissing => "This task has no Codex workspace.",
            CodexLaunchProblem.ThreadIdMissing => "The thread ID is required.",
            _ => $"The Codex workspace directory does not exist: {availability.Workspace}"
        };
        throw new CodexLaunchException(availability.Problem, message, detail: availability.Workspace);
    }

    private static string NormalizeWorkspaceForDetail(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return workspace;
        }
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
