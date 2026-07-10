using System.Diagnostics;

namespace ThreadShelf;

internal interface IThreadNativeActions
{
    void SetArchived(string threadId, bool archived);
    void SetName(string threadId, string name);
}

internal sealed class CodexThreadNativeActions : IThreadNativeActions
{
    public void SetArchived(string threadId, bool archived) =>
        CodexAppServerClient.SetThreadArchived(threadId, archived);

    public void SetName(string threadId, string name) =>
        CodexAppServerClient.SetThreadName(threadId, name.Trim());
}

public static class ThreadShelfSystemActions
{
    public static FolderOpenAvailability CheckFolderAvailability(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(false, FolderOpenProblem.PathMissing, "");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(false, FolderOpenProblem.DirectoryNotFound, path);
        }

        return Directory.Exists(fullPath)
            ? new(true, FolderOpenProblem.None, fullPath)
            : new(false, FolderOpenProblem.DirectoryNotFound, fullPath);
    }

    public static FolderOpenPlan CreateOpenFolderPlan(string? path)
    {
        var availability = CheckFolderAvailability(path);
        if (!availability.CanOpen)
        {
            throw new FolderOpenException(availability.Problem, availability.Path);
        }

        return new("explorer.exe", [availability.Path]);
    }

    public static FolderOpenPlan OpenFolder(string? path)
    {
        var plan = CreateOpenFolderPlan(path);
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.Executable,
            UseShellExecute = false
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows File Explorer did not start.");
            return plan;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
        {
            throw new FolderOpenException(FolderOpenProblem.StartFailed, plan.Arguments[0], ex);
        }
    }

    public static void RevealFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"/select,{Path.GetFullPath(path)}");
        Process.Start(startInfo);
    }
}

public enum FolderOpenProblem
{
    None,
    PathMissing,
    DirectoryNotFound,
    StartFailed
}

public sealed record FolderOpenAvailability(bool CanOpen, FolderOpenProblem Problem, string Path);

public sealed record FolderOpenPlan(string Executable, IReadOnlyList<string> Arguments);

public sealed class FolderOpenException : InvalidOperationException
{
    public FolderOpenException(FolderOpenProblem problem, string path, Exception? innerException = null)
        : base(problem switch
        {
            FolderOpenProblem.PathMissing => "The workspace path is missing.",
            FolderOpenProblem.DirectoryNotFound => $"The workspace directory does not exist: {path}",
            _ => $"Windows File Explorer could not open the workspace: {path}"
        }, innerException)
    {
        Problem = problem;
        Path = path;
    }

    public FolderOpenProblem Problem { get; }
    public string Path { get; }
}
