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
    public static void OpenThreadInCodex(string threadId)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"codex://threads/{Uri.EscapeDataString(threadId)}",
            UseShellExecute = true
        });
    }

    public static void RevealFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
