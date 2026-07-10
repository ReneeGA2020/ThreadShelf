namespace ThreadShelf;

internal sealed record ThreadSourceSnapshot(
    IReadOnlyList<CodexThread> Threads,
    string CodexHome,
    string DataSource,
    bool SupportsNativeActions);

internal interface IThreadSource
{
    ThreadSourceSnapshot Load();
}

internal sealed class AppServerThreadSource : IThreadSource
{
    public ThreadSourceSnapshot Load()
    {
        var index = CodexAppServerClient.LoadThreadIndex();
        return new ThreadSourceSnapshot(
            index.Threads,
            index.CodexHome,
            "Codex CLI app-server",
            SupportsNativeActions: true);
    }
}
