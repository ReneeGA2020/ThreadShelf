using ThreadShelf;

namespace ThreadShelf.Tests;

public sealed class ThreadShelfMcpCatalogTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "threadshelf_list_threads",
        "threadshelf_get_thread",
        "threadshelf_search_threads",
        "threadshelf_update_thread_metadata",
        "threadshelf_move_thread",
        "threadshelf_add_thread_tag",
        "threadshelf_remove_thread_tag",
        "threadshelf_batch_update_threads",
        "threadshelf_list_tags",
        "threadshelf_create_tag",
        "threadshelf_update_tag",
        "threadshelf_delete_tag",
        "threadshelf_archive_thread",
        "threadshelf_unarchive_thread",
        "threadshelf_rename_thread"
    ];

    [Fact]
    public void DescriptorRegistryBindsEveryPublishedToolToOneHandler()
    {
        var handlers = new ThreadShelfToolHandlers(new ThreadShelfCommandService());
        var registry = new ThreadShelfMcpToolRegistry(handlers);
        var descriptors = registry.Descriptors;

        Assert.Equal(15, descriptors.Count);
        Assert.Equal(ExpectedToolNames, descriptors.Select(descriptor => descriptor.Name));
        Assert.Equal(
            descriptors.Count,
            descriptors.Select(descriptor => descriptor.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(descriptors.Count, registry.Catalog().Length);
        Assert.All(descriptors, descriptor =>
        {
            Assert.NotNull(descriptor.Handler);
            Assert.NotNull(descriptor.Properties);
            Assert.NotNull(descriptor.Required);
        });
    }

    [Fact]
    public void AgentFacingSchemasExposeStructuredReadsAndSafeBatchUpdates()
    {
        var registry = new ThreadShelfMcpToolRegistry(
            new ThreadShelfToolHandlers(new ThreadShelfCommandService()));
        var list = registry.Descriptors.Single(descriptor =>
            descriptor.Name == "threadshelf_list_threads");
        Assert.Contains("workspace", list.Properties.Keys);
        Assert.Contains("updatedAfter", list.Properties.Keys);
        Assert.Contains("createdAfter", list.Properties.Keys);
        Assert.Contains("excludeThreadIds", list.Properties.Keys);
        Assert.Contains("fields", list.Properties.Keys);
        Assert.Contains("refresh", list.Properties.Keys);

        var batch = registry.Descriptors.Single(descriptor =>
            descriptor.Name == "threadshelf_batch_update_threads");
        Assert.Contains("dryRun", batch.Properties.Keys);
        Assert.Empty(batch.Required);
    }
}
