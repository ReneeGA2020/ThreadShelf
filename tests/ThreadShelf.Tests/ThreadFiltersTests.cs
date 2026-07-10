using ThreadShelf;

namespace ThreadShelf.Tests;

public sealed class ThreadFiltersTests
{
    [Fact]
    public void BuildProjectSummaries_GroupsWorkspacesAndDisambiguatesMatchingNames()
    {
        var threads = new[]
        {
            Thread("one", @"E:\Work\ThreadShelf", "Planning"),
            Thread("two", @"E:\Work\ThreadShelf\", "Build"),
            Thread("three", @"D:\Archive\ThreadShelf", "Planning"),
            Thread("four", "", "Planning")
        };

        var projects = ThreadFilters.BuildProjectSummaries(threads);

        Assert.Collection(
            projects,
            project =>
            {
                Assert.Equal("ThreadShelf · Archive", project.Name);
                Assert.Equal(1, project.Count);
                Assert.Equal(@"D:\Archive\ThreadShelf", project.Key);
            },
            project =>
            {
                Assert.Equal("ThreadShelf · Work", project.Name);
                Assert.Equal(2, project.Count);
                Assert.Equal(@"E:\Work\ThreadShelf", project.Key);
            });
    }

    [Fact]
    public void Apply_ComposesProjectFolderTagAndSearchFilters()
    {
        var target = Thread("target", @"E:\Work\Alpha", "Planning", "ship Alpha") with
        {
            Metadata = new ThreadMetadata { Folder = "Planning", Tags = ["ready"] }
        };
        var threads = new[]
        {
            target,
            Thread("other-project", @"E:\Work\Beta", "Planning", "ship Alpha") with
            {
                Metadata = new ThreadMetadata { Folder = "Planning", Tags = ["ready"] }
            },
            Thread("other-folder", @"E:\Work\Alpha", "Build", "ship Alpha") with
            {
                Metadata = new ThreadMetadata { Folder = "Build", Tags = ["ready"] }
            },
            Thread("other-tag", @"E:\Work\Alpha", "Planning", "ship Alpha")
        };

        var filtered = ThreadFilters.Apply(
            threads,
            @"e:\work\alpha\",
            "Planning",
            "ship",
            "ready");

        Assert.Equal([target], filtered);
    }

    [Fact]
    public void FilterByProject_HandlesAllAndMissingWorkspaceScopes()
    {
        var projectThread = Thread("project", @"E:\Work\Alpha", "Planning");
        var noProjectThread = Thread("none", "", "Planning");
        var threads = new[] { projectThread, noProjectThread };

        Assert.Equal(threads, ThreadFilters.FilterByProject(threads, ThreadFilters.AllProjects));
        Assert.Equal([noProjectThread], ThreadFilters.FilterByProject(threads, ThreadFilters.NoProject));
    }

    private static CodexThread Thread(string id, string workspace, string folder, string? title = null) =>
        new()
        {
            Id = id,
            Title = title ?? id,
            Workspace = workspace,
            UpdatedAt = DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
            Metadata = new ThreadMetadata { Folder = folder }
        };
}
