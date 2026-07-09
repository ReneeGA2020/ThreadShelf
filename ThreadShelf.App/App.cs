using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ThreadShelf;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run(_ =>
{
    ReactorApp.OpenWindow(
        new WindowSpec
        {
            Title = "ThreadShelf",
            Width = 1180,
            Height = 760,
            MinWidth = 980,
            MinHeight = 560
        },
        () => new App());
});

class App : Component
{
    private const string ThreadsPage = "threads";
    private const string TagsPage = "tags";

    private sealed record ThreadDragPayload(string ThreadId);
    private sealed record TagEditorDraft(string EditingName, string Name, string Color, string Description)
    {
        public static TagEditorDraft Empty { get; } = new("", "", TagDefinition.DefaultColor, "");
    }

    public override Element Render()
    {
        var repository = UseMemo(() => new ThreadShelfRepository());
        var (snapshot, setSnapshot) = UseState<ThreadShelfSnapshot?>(null);
        var (selectedFilter, setSelectedFilter) = UseState(ThreadFilters.All);
        var (selectedTag, setSelectedTag) = UseState("");
        var (query, setQuery) = UseState("");
        var (selectedId, setSelectedId) = UseState("");
        var (draft, setDraft) = UseState<EditDraft?>(null);
        var (titleDraft, setTitleDraft) = UseState("");
        var (tagEditor, updateTagEditor) = UseReducer<TagEditorDraft>(TagEditorDraft.Empty);
        var (activePage, setActivePage) = UseState(ThreadsPage);
        var (pendingDeleteTag, setPendingDeleteTag) = UseState("");
        var (status, setStatus) = UseState("");
        var snapshotVersion = snapshot?.LoadedAt.UtcTicks ?? 0L;

        void setTagEditor(TagEditorDraft next) =>
            updateTagEditor(_ => next);

        void setTagEditorColor(TagEditorDraft renderedDraft, global::Windows.UI.Color color)
        {
            var nextColor = TagColorFromWinUIColor(color);
            updateTagEditor(current =>
                SameTagEditorIdentity(current, renderedDraft)
                    ? current with { Color = nextColor }
                    : current);
        }

        void Reload()
        {
            try
            {
                var loaded = repository.Load();
                setSnapshot(loaded);
                setStatus(DescribeLoad(loaded));
            }
            catch (Exception ex)
            {
                setStatus($"Load failed: {ex.Message}");
            }
        }

        UseEffect(() =>
        {
            if (snapshot is null)
            {
                Reload();
            }
        }, snapshot is null);

        UseEffect(() =>
        {
            if (snapshot is null)
            {
                return;
            }

            var selectedStillExists = snapshot.Threads.Any(thread =>
                thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));

            if (!selectedStillExists)
            {
                setSelectedId(snapshot.Threads.FirstOrDefault()?.Id ?? "");
            }
        }, snapshotVersion, selectedId);

        UseEffect(() =>
        {
            if (snapshot is null || selectedId.Length == 0)
            {
                return;
            }

            var selected = snapshot.Threads.FirstOrDefault(thread =>
                thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
            {
                setDraft(EditDraft.From(selected.Metadata));
                setTitleDraft(selected.Title);
            }
        }, selectedId, snapshotVersion);

        var threads = snapshot?.Threads ?? [];
        var tags = snapshot?.Tags ?? [];
        var filtered = ThreadFilters.Apply(threads, selectedFilter, query, selectedTag);
        var selectedThread = filtered.FirstOrDefault(thread =>
                thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? filtered.FirstOrDefault()
            ?? threads.FirstOrDefault(thread =>
                thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? threads.FirstOrDefault();
        var activeDraft = selectedThread is null
            ? null
            : selectedThread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)
                ? draft ?? EditDraft.From(selectedThread.Metadata)
                : EditDraft.From(selectedThread.Metadata);

        UseEffect(() =>
        {
            if (selectedTag.Length > 0
                && !tags.Any(tag => tag.Name.Equals(selectedTag, StringComparison.OrdinalIgnoreCase)))
            {
                setSelectedTag("");
            }
        }, snapshotVersion, selectedTag);

        void SelectThread(CodexThread thread)
        {
            setSelectedId(thread.Id);
            setDraft(EditDraft.From(thread.Metadata));
            setTitleDraft(thread.Title);
        }

        UseEffect(() =>
        {
            if (snapshot is null
                || !activePage.Equals(ThreadsPage, StringComparison.OrdinalIgnoreCase)
                || filtered.Count == 0
                || filtered.Any(thread => thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            SelectThread(filtered[0]);
        }, snapshotVersion, selectedFilter, selectedTag, query, selectedId, activePage);

        void SelectFilter(string filter)
        {
            setSelectedFilter(filter);
            setActivePage(ThreadsPage);
            var next = ThreadFilters.Apply(threads, filter, query, selectedTag).FirstOrDefault();
            if (next is not null)
            {
                SelectThread(next);
            }
        }

        void SelectTagFilter(string tag)
        {
            setSelectedTag(tag);
            setActivePage(ThreadsPage);
            var next = ThreadFilters.Apply(threads, selectedFilter, query, tag).FirstOrDefault();
            if (next is not null)
            {
                SelectThread(next);
            }
        }

        void ShowThreads()
        {
            setActivePage(ThreadsPage);
        }

        void ShowTagManager()
        {
            setActivePage(TagsPage);
        }

        void SaveThreadMetadata(CodexThread thread, ThreadMetadata metadata, string successMessage)
        {
            if (snapshot is null)
            {
                return;
            }

            try
            {
                if (SameMetadata(thread.Metadata, metadata))
                {
                    if (thread.Id.Equals(selectedThread?.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        setDraft(EditDraft.From(metadata));
                    }

                    return;
                }

                repository.SaveMetadata(thread.Id, metadata);
                setSnapshot(snapshot.WithMetadata(thread.Id, metadata));
                if (thread.Id.Equals(selectedThread?.Id, StringComparison.OrdinalIgnoreCase))
                {
                    setDraft(EditDraft.From(metadata));
                }

                setStatus(successMessage);
            }
            catch (Exception ex)
            {
                setStatus($"Save failed: {ex.Message}");
            }
        }

        void SaveDraft(EditDraft draftToSave)
        {
            if (selectedThread is null)
            {
                return;
            }

            var metadata = ThreadShelfRepository.MetadataFrom(draftToSave, selectedThread.Metadata.Tags);
            SaveThreadMetadata(selectedThread, metadata, $"Saved metadata for {selectedThread.DisplayTitle}");
        }

        void MoveThreadToFolder(ThreadDragPayload payload, string folder)
        {
            if (snapshot is null)
            {
                return;
            }

            var thread = snapshot.Threads.FirstOrDefault(candidate =>
                candidate.Id.Equals(payload.ThreadId, StringComparison.OrdinalIgnoreCase));
            if (thread is null)
            {
                return;
            }

            var targetFolder = (folder ?? "").Trim();
            var metadata = thread.Metadata with { Folder = targetFolder };
            var destination = targetFolder.Length == 0 ? "Unfiled" : targetFolder;
            SaveThreadMetadata(thread, metadata, $"Moved {thread.DisplayTitle} to {destination}");
        }

        void ToggleThreadTag(CodexThread thread, TagDefinition tag)
        {
            var tagName = ThreadShelfRepository.NormalizeTagName(tag.Name);
            if (tagName.Length == 0)
            {
                return;
            }

            var hasTag = thread.Metadata.Tags.Any(name =>
                name.Equals(tagName, StringComparison.OrdinalIgnoreCase));
            var nextTags = hasTag
                ? thread.Metadata.Tags
                    .Where(name => !name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : thread.Metadata.Tags
                    .Concat([tagName])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

            SaveThreadMetadata(
                thread,
                thread.Metadata with { Tags = nextTags },
                hasTag
                    ? $"Removed tag {tagName} from {thread.DisplayTitle}"
                    : $"Added tag {tagName} to {thread.DisplayTitle}");
        }

        void SaveTagDefinition()
        {
            var name = ThreadShelfRepository.NormalizeTagName(tagEditor.Name);
            if (name.Length == 0)
            {
                setStatus("Tag save failed: name cannot be empty");
                return;
            }

            if (!ThreadShelfRepository.IsValidTagColor(tagEditor.Color))
            {
                setStatus("Tag save failed: color must be #RRGGBB");
                return;
            }

            try
            {
                var definition = new TagDefinition
                {
                    Name = name,
                    Color = ThreadShelfRepository.NormalizeTagColor(tagEditor.Color),
                    Description = tagEditor.Description
                };
                repository.SaveTagDefinition(tagEditor.EditingName, definition);
                if (tagEditor.EditingName.Equals(selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    setSelectedTag(definition.Name);
                }

                setPendingDeleteTag("");
                setTagEditor(TagEditorDraft.Empty);
                Reload();
                setStatus(tagEditor.EditingName.Length == 0
                    ? $"Added tag {definition.Name}"
                    : $"Saved tag {definition.Name}");
            }
            catch (Exception ex)
            {
                setStatus($"Tag save failed: {ex.Message}");
            }
        }

        void DeleteTagDefinition(string name)
        {
            var tagName = ThreadShelfRepository.NormalizeTagName(name);
            if (tagName.Length == 0)
            {
                setStatus("Tag delete failed: name cannot be empty");
                return;
            }

            try
            {
                repository.DeleteTagDefinition(tagName);
                if (tagName.Equals(selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    setSelectedTag("");
                }

                if (tagName.Equals(tagEditor.EditingName, StringComparison.OrdinalIgnoreCase))
                {
                    setTagEditor(TagEditorDraft.Empty);
                }

                setPendingDeleteTag("");
                Reload();
                setStatus($"Deleted tag {tagName}");
            }
            catch (Exception ex)
            {
                setStatus($"Tag delete failed: {ex.Message}");
            }
        }

        void ToggleArchived(CodexThread thread, bool archived)
        {
            try
            {
                repository.SetArchived(thread.Id, archived);
                Reload();
                setStatus(archived
                    ? $"Archived {thread.DisplayTitle}"
                    : $"Unarchived {thread.DisplayTitle}");
            }
            catch (Exception ex)
            {
                setStatus($"Archive action failed: {ex.Message}");
            }
        }

        void RenameThread(CodexThread thread, string name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0)
            {
                setStatus("Rename failed: title cannot be empty");
                return;
            }

            try
            {
                repository.SetName(thread.Id, trimmed);
                Reload();
                setStatus($"Renamed thread to {trimmed}");
            }
            catch (Exception ex)
            {
                setStatus($"Rename failed: {ex.Message}");
            }
        }

        var titleBar = TitleBar("ThreadShelf")
            .Subtitle(snapshot is null
                ? "Loading Codex threads"
                : $"{threads.Count:N0} indexed threads via {snapshot.DataSource}")
            .Flex(shrink: 0);

        Element body;
        if (snapshot is null)
        {
            body = RenderLoading(status);
        }
        else
        {
            var sidebar = RenderSidebar(
                threads,
                tags,
                activePage,
                ShowThreads,
                ShowTagManager,
                selectedFilter,
                SelectFilter,
                selectedTag,
                SelectTagFilter,
                MoveThreadToFolder,
                snapshot.SidecarPath);

            body = activePage.Equals(TagsPage, StringComparison.OrdinalIgnoreCase)
                ? FlexRow(
                    sidebar,
                    RenderTagManagerPage(
                        threads,
                        tags,
                        tagEditor,
                        setTagEditor,
                        setTagEditorColor,
                        SaveTagDefinition,
                        pendingDeleteTag,
                        setPendingDeleteTag,
                        DeleteTagDefinition,
                        ShowThreads,
                        status))
                    with
                    {
                        ColumnGap = 16,
                        AlignItems = FlexAlign.Stretch
                    }
                : FlexRow(
                    sidebar,
                    RenderThreadList(
                        filtered,
                        tags,
                        selectedThread?.Id ?? "",
                        query,
                        setQuery,
                        SelectThread,
                        Reload,
                        status),
                    RenderDetails(
                        selectedThread,
                        activeDraft,
                        titleDraft,
                        setDraft,
                        setTitleDraft,
                        SaveDraft,
                        tags,
                        ToggleThreadTag,
                        ShowTagManager,
                        ToggleArchived,
                        RenameThread,
                        snapshot.SupportsNativeActions,
                        setStatus))
                    with
                    {
                        ColumnGap = 16,
                        AlignItems = FlexAlign.Stretch
                    };
        }

        return FlexColumn(
                titleBar,
                Border(body)
                    .Padding(20)
                    .Flex(grow: 1, basis: 0))
            .Backdrop(BackdropKind.Mica);
    }

    private static Element RenderLoading(string status) =>
        Border(
            FlexColumn(
                ProgressRing().IsActive(),
                BodyLarge("Loading Codex thread index"),
                Caption(status))
            with
            {
                RowGap = 12,
                AlignItems = FlexAlign.Center,
                JustifyContent = FlexJustify.Center
            })
        .Flex(grow: 1, basis: 0);

    private static Element RenderSidebar(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyList<TagDefinition> tags,
        string activePage,
        Action showThreads,
        Action showTagManager,
        string selectedFilter,
        Action<string> selectFilter,
        string selectedTag,
        Action<string> selectTag,
        Action<ThreadDragPayload, string> moveThreadToFolder,
        string sidecarPath)
    {
        var folders = ThreadFilters.BuildFolderSummaries(threads);
        var tagSummaries = ThreadFilters.BuildTagSummaries(threads, tags);
        var favoriteCount = threads.Count(thread => thread.Metadata.Favorite);
        var unfiledCount = threads.Count(thread => string.IsNullOrWhiteSpace(thread.Metadata.Folder));

        var folderButtons = folders
            .Select(folder => FilterButton(
                folder.Name,
                $"{folder.Name} ({folder.Count:N0})",
                selectedFilter,
                selectFilter,
                payload => moveThreadToFolder(payload, folder.Name),
                $"Move to {folder.Name}"))
            .ToArray();

        var tagButtons = tagSummaries
            .Select(summary => TagFilterButton(summary, selectedTag, selectTag))
            .ToArray();

        return Border(
                FlexColumn(
                    BodyStrong("View").Flex(shrink: 0),
                    PageButton(ThreadsPage, "Threads", activePage, showThreads)
                        .AutomationId("View_Threads"),
                    PageButton(TagsPage, "Tag manager", activePage, showTagManager)
                        .AutomationId("View_TagManager"),
                    Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 8, 0, 4),
                    BodyStrong("Folders").Flex(shrink: 0),
                    FilterButton(ThreadFilters.All, $"All ({threads.Count:N0})", selectedFilter, selectFilter)
                        .AutomationId("Filter_All"),
                    FilterButton(ThreadFilters.Favorites, $"Favorites ({favoriteCount:N0})", selectedFilter, selectFilter)
                        .AutomationId("Filter_Favorites"),
                    FilterButton(
                        ThreadFilters.Unfiled,
                        $"Unfiled ({unfiledCount:N0})",
                        selectedFilter,
                        selectFilter,
                        payload => moveThreadToFolder(payload, ""),
                        "Move to Unfiled")
                        .AutomationId("Filter_Unfiled"),
                    Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 8, 0, 4),
                    ScrollViewer(
                        FlexColumn(
                            [.. folderButtons,
                            Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 10, 0, 6),
                            BodyStrong("Tags"),
                            TagFilterButton(null, threads.Count, selectedTag, selectTag),
                            .. tagButtons]) with
                        {
                            RowGap = 6
                        })
                    .Flex(grow: 1, basis: 0),
                    Caption($"Sidecar: {sidecarPath}")
                        .TextWrapping()
                        .Foreground(Theme.SecondaryText)
                        .Flex(shrink: 0))
                with
                {
                    RowGap = 8
                })
            .Padding(14)
            .CornerRadius(8)
            .Background(Theme.LayerFill)
            .WithBorder(Theme.CardStroke, 1)
            .Flex(basis: 230, shrink: 0);
    }

    private static Element PageButton(
        string value,
        string label,
        string activePage,
        Action select)
    {
        var selected = value.Equals(activePage, StringComparison.OrdinalIgnoreCase);
        return Button(label, select)
            .AutomationName(label)
            .HAlign(HorizontalAlignment.Stretch)
            .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Stretch)
            .Resources(resources => resources
                .Set(
                    "ButtonBackground",
                    selected
                        ? Theme.Ref("AccentFillColorDefaultBrush")
                        : Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set(
                    "ButtonBackgroundPointerOver",
                    selected
                        ? Theme.Ref("AccentFillColorSecondaryBrush")
                        : Theme.Ref("SubtleFillColorSecondaryBrush"))
                .Set(
                    "ButtonBackgroundPressed",
                    selected
                        ? Theme.Ref("AccentFillColorTertiaryBrush")
                        : Theme.Ref("SubtleFillColorTertiaryBrush"))
                .Set(
                    "ButtonForeground",
                    selected
                        ? Theme.Ref("TextOnAccentFillColorPrimaryBrush")
                        : Theme.PrimaryText)
                .Set(
                    "ButtonForegroundPointerOver",
                    selected
                        ? Theme.Ref("TextOnAccentFillColorPrimaryBrush")
                        : Theme.PrimaryText)
                .Set(
                    "ButtonForegroundPressed",
                    selected
                        ? Theme.Ref("TextOnAccentFillColorSecondaryBrush")
                        : Theme.PrimaryText)
                .Set(
                    "ButtonBorderBrush",
                    selected
                        ? Theme.Ref("AccentFillColorDefaultBrush")
                        : Theme.Ref("SubtleFillColorTransparentBrush")))
            .WithKey($"page-{value}-{(selected ? "selected" : "normal")}");
    }

    private static Element FilterButton(
        string value,
        string label,
        string selectedFilter,
        Action<string> selectFilter,
        Action<ThreadDragPayload>? dropThread = null,
        string dropCaption = "")
    {
        var selected = value.Equals(selectedFilter, StringComparison.OrdinalIgnoreCase);

        // AccentButton/SubtleButton are ApplyStyle helpers and only run at mount;
        // selection needs update-time resource overrides.
        var button = Button(label, () => selectFilter(value))
            .AutomationName(label)
            .AutomationId($"Filter_{AutomationToken(value)}")
            .HAlign(HorizontalAlignment.Stretch)
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            })
            .Resources(resources => resources
                .Set(
                    "ButtonBackground",
                    selected
                        ? Theme.Ref("AccentFillColorDefaultBrush")
                        : Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set(
                    "ButtonBackgroundPointerOver",
                    selected
                        ? Theme.Ref("AccentFillColorSecondaryBrush")
                        : Theme.Ref("SubtleFillColorSecondaryBrush"))
                .Set(
                    "ButtonBackgroundPressed",
                    selected
                        ? Theme.Ref("AccentFillColorTertiaryBrush")
                        : Theme.Ref("SubtleFillColorTertiaryBrush"))
                .Set(
                    "ButtonForeground",
                    selected
                        ? Theme.Ref("TextOnAccentFillColorPrimaryBrush")
                        : Theme.PrimaryText)
                .Set(
                    "ButtonForegroundPointerOver",
                    selected
                        ? Theme.Ref("TextOnAccentFillColorPrimaryBrush")
                        : Theme.PrimaryText)
                .Set(
                    "ButtonForegroundPressed",
                    selected
                        ? Theme.Ref("TextOnAccentFillColorSecondaryBrush")
                        : Theme.PrimaryText)
                .Set(
                    "ButtonBorderBrush",
                    selected
                        ? Theme.Ref("AccentFillColorDefaultBrush")
                        : Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set(
                    "ButtonBorderBrushPointerOver",
                    selected
                        ? Theme.Ref("AccentFillColorSecondaryBrush")
                        : Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set(
                    "ButtonBorderBrushPressed",
                    selected
                        ? Theme.Ref("AccentFillColorTertiaryBrush")
                        : Theme.Ref("SubtleFillColorTransparentBrush")))
            .WithKey($"filter-{AutomationToken(value)}-{(selected ? "selected" : "normal")}");

        return dropThread is null
            ? button
            : button
                .OnDrop((ThreadDragPayload payload) => dropThread(payload), DragOperations.Move)
                .OnDragOver(args => args.UIOverride.Caption = dropCaption);
    }

    private static Element TagFilterButton(
        TagSummary? summary,
        int totalCount,
        string selectedTag,
        Action<string> selectTag)
    {
        if (summary is null)
        {
            var selected = string.IsNullOrWhiteSpace(selectedTag);
            return Button($"All tags ({totalCount:N0})", () => selectTag(""))
                .AutomationName("All tags")
                .AutomationId("TagFilter_All")
                .HAlign(HorizontalAlignment.Stretch)
                .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Stretch)
                .Resources(resources => resources
                    .Set(
                        "ButtonBackground",
                        selected
                            ? Theme.Ref("AccentFillColorDefaultBrush")
                            : Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set(
                        "ButtonBackgroundPointerOver",
                        selected
                            ? Theme.Ref("AccentFillColorSecondaryBrush")
                            : Theme.Ref("SubtleFillColorSecondaryBrush"))
                    .Set(
                        "ButtonBackgroundPressed",
                        selected
                            ? Theme.Ref("AccentFillColorTertiaryBrush")
                            : Theme.Ref("SubtleFillColorTertiaryBrush"))
                    .Set(
                        "ButtonForeground",
                        selected
                            ? Theme.Ref("TextOnAccentFillColorPrimaryBrush")
                            : Theme.PrimaryText)
                    .Set(
                        "ButtonForegroundPointerOver",
                        selected
                            ? Theme.Ref("TextOnAccentFillColorPrimaryBrush")
                            : Theme.PrimaryText)
                    .Set(
                        "ButtonForegroundPressed",
                        selected
                            ? Theme.Ref("TextOnAccentFillColorSecondaryBrush")
                            : Theme.PrimaryText)
                    .Set(
                        "ButtonBorderBrush",
                        selected
                            ? Theme.Ref("AccentFillColorDefaultBrush")
                            : Theme.Ref("SubtleFillColorTransparentBrush")))
                .WithKey($"tag-filter-all-{(selected ? "selected" : "normal")}");
        }

        return TagFilterButton(summary, selectedTag, selectTag);
    }

    private static Element TagFilterButton(
        TagSummary summary,
        string selectedTag,
        Action<string> selectTag)
    {
        var tag = summary.Definition;
        var selected = tag.Name.Equals(selectedTag, StringComparison.OrdinalIgnoreCase);
        var foreground = ForegroundFor(tag.Color);

        return Button($"{tag.Name} ({summary.Count:N0})", () => selectTag(tag.Name))
            .AutomationName($"Tag {tag.Name}")
            .AutomationId($"TagFilter_{AutomationToken(tag.Name)}")
            .HAlign(HorizontalAlignment.Stretch)
            .ToolTip(string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description)
            .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Stretch)
            .Resources(resources =>
            {
                if (selected)
                {
                    resources
                        .Set("ButtonBackground", tag.Color)
                        .Set("ButtonBackgroundPointerOver", tag.Color)
                        .Set("ButtonBackgroundPressed", tag.Color)
                        .Set("ButtonForeground", foreground)
                        .Set("ButtonForegroundPointerOver", foreground)
                        .Set("ButtonForegroundPressed", foreground)
                        .Set("ButtonBorderBrush", tag.Color);
                }
                else
                {
                    resources
                        .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                        .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .Set("ButtonForeground", Theme.PrimaryText)
                        .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"));
                }
            })
            .WithKey($"tag-filter-{AutomationToken(tag.Name)}-{(selected ? "selected" : "normal")}");
    }

    private static Element RenderThreadList(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyList<TagDefinition> tags,
        string selectedId,
        string query,
        Action<string> setQuery,
        Action<CodexThread> selectThread,
        Action reload,
        string status)
    {
        var tagLookup = tags.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        var selectedIndex = threads
            .Select((thread, index) => (thread, index))
            .FirstOrDefault(pair => pair.thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            .index;

        if (selectedIndex == 0 && threads.FirstOrDefault()?.Id != selectedId)
        {
            selectedIndex = -1;
        }

        var list = (ListView<CodexThread>(
                threads,
                thread => thread.Id,
                (thread, _) => RenderThreadRow(
                    thread,
                    thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase),
                    tagLookup,
                    selectThread))
            with
            {
                SelectedIndex = selectedIndex
            })
            .SelectedIndexChanged(index =>
            {
                if (index >= 0 && index < threads.Count)
                {
                    selectThread(threads[index]);
                }
            })
            .ItemClick(selectThread);

        return Border(
                FlexColumn(
                    FlexRow(
                        TextBox(query, setQuery, placeholderText: "Search title, folder, tag, note, id")
                            .AutomationName("Thread search")
                            .AutomationId("ThreadSearchBox")
                            .Flex(grow: 1, basis: 0),
                        Button("Refresh", reload)
                            .AutomationId("RefreshButton")
                            .SubtleButton()
                            .Flex(shrink: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    },
                    FlexRow(
                        BodyStrong($"{threads.Count:N0} shown"),
                        Caption(status).Foreground(Theme.SecondaryText).Flex(grow: 1, basis: 0))
                    with
                    {
                        ColumnGap = 10,
                        AlignItems = FlexAlign.Center
                    },
                    If(
                        threads.Count == 0,
                        () => Border(
                                FlexColumn(
                                    BodyLarge("No matching threads"),
                                    Caption("Clear the search or select another folder.").Foreground(Theme.SecondaryText))
                                with
                                {
                                    RowGap = 8,
                                    AlignItems = FlexAlign.Center,
                                    JustifyContent = FlexJustify.Center
                                })
                            .Flex(grow: 1, basis: 0),
                        () => list
                            .Set(control => control.SelectionMode = ListViewSelectionMode.Single)
                            .Flex(grow: 1, basis: 0)))
                with
                {
                    RowGap = 12
                })
            .Padding(14)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .Flex(grow: 1, basis: 0);
    }

    private static Element RenderThreadRow(
        CodexThread thread,
        bool selected,
        IReadOnlyDictionary<string, TagDefinition> tagLookup,
        Action<CodexThread> selectThread)
    {
        var status = thread.IsArchived ? "Archived" : "Active";
        var source = string.IsNullOrWhiteSpace(thread.Source) ? thread.Originator : thread.Source;
        var location = string.IsNullOrWhiteSpace(thread.Workspace) ? source : thread.Workspace;
        var visibleTags = thread.Metadata.Tags.Take(4).ToArray();
        var hiddenTagCount = Math.Max(0, thread.Metadata.Tags.Count - visibleTags.Length);

        return Border(
                FlexColumn(
                    FlexRow(
                        Caption("::")
                            .AutomationName($"Drag {thread.DisplayTitle} to folder")
                            .AutomationId($"ThreadDragHandle_{AutomationToken(thread.Id)}")
                            .ToolTip("Drag to folder")
                            .Foreground(Theme.SecondaryText)
                            .Width(18)
                            .HAlign(HorizontalAlignment.Center)
                            .OnDragStart(() => new ThreadDragPayload(thread.Id), DragOperations.Move)
                            .Flex(shrink: 0),
                        BodyStrong(thread.DisplayTitle)
                            .AutomationId($"ThreadTitle_{AutomationToken(thread.Id)}")
                            .OnPointerPressed((_, _) => selectThread(thread))
                            .OnTapped((_, _) => selectThread(thread))
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1)
                            .Flex(grow: 1, basis: 0),
                        Pill(status, thread.IsArchived ? Theme.SystemNeutralBackground : Theme.SystemSuccessBackground)
                            .Flex(shrink: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    },
                    FlexRow(
                        Caption(thread.UpdatedLocal).Foreground(Theme.SecondaryText),
                        Caption(thread.DisplayFolder).Foreground(Theme.SecondaryText),
                        If(thread.Metadata.Favorite, () => Caption("Favorite").Foreground(Theme.SystemAttention), () => Empty()))
                    with
                    {
                        ColumnGap = 10,
                        AlignItems = FlexAlign.Center
                    },
                    If(
                        visibleTags.Length > 0,
                        () => FlexRow(
                                [.. visibleTags.Select(name => CompactTagBadge(
                                    tagLookup.TryGetValue(name, out var definition)
                                        ? definition
                                        : new TagDefinition { Name = name })),
                                If(hiddenTagCount > 0, () => Caption($"+{hiddenTagCount}").Foreground(Theme.SecondaryText), () => Empty())]) with
                            {
                                ColumnGap = 5,
                                RowGap = 4,
                                AlignItems = FlexAlign.Center,
                                Wrap = FlexWrap.Wrap
                            },
                        () => Empty()),
                    If(
                        !string.IsNullOrWhiteSpace(location),
                        () => Caption(location)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1)
                            .Foreground(Theme.TertiaryText),
                        () => Empty()))
                with
                {
                    RowGap = 5
                })
            .Padding(10)
            .CornerRadius(6)
            .Background(selected ? Theme.ControlFillSecondary : Theme.CardBackground)
            .WithBorder(selected ? Theme.Accent : Theme.CardStroke, selected ? 2 : 1)
            .Margin(0, 0, 0, 6)
            .AutomationId($"ThreadRow_{AutomationToken(thread.Id)}")
            .OnPointerPressed((_, _) => selectThread(thread))
            .OnTapped((_, _) => selectThread(thread))
            .WithKey(thread.Id);
    }

    private static Element RenderDetails(
        CodexThread? thread,
        EditDraft? draft,
        string titleDraft,
        Action<EditDraft?> setDraft,
        Action<string> setTitleDraft,
        Action<EditDraft> saveMetadata,
        IReadOnlyList<TagDefinition> tags,
        Action<CodexThread, TagDefinition> toggleThreadTag,
        Action openTagManager,
        Action<CodexThread, bool> setArchived,
        Action<CodexThread, string> renameThread,
        bool supportsNativeActions,
        Action<string> setStatus)
    {
        if (thread is null || draft is null)
        {
            return Border(
                    FlexColumn(
                        BodyLarge("No thread selected"),
                        Caption("Select a thread from the list.").Foreground(Theme.SecondaryText))
                    with
                    {
                        RowGap = 8,
                        AlignItems = FlexAlign.Center,
                        JustifyContent = FlexJustify.Center
                    })
                .Padding(16)
                .CornerRadius(8)
                .Background(Theme.LayerFill)
                .WithBorder(Theme.CardStroke, 1)
                .Flex(basis: 360, shrink: 0);
        }

        void OpenInCodex()
        {
            try
            {
                ThreadShelfRepository.OpenThreadInCodex(thread.Id);
                setStatus("Opened Codex deep link");
            }
            catch (Exception ex)
            {
                setStatus($"Open failed: {ex.Message}");
            }
        }

        void RevealJsonl()
        {
            try
            {
                ThreadShelfRepository.RevealFile(thread.SourcePath);
                setStatus("Opened session file location");
            }
            catch (Exception ex)
            {
                setStatus($"Reveal failed: {ex.Message}");
            }
        }

        void ToggleArchive()
        {
            setArchived(thread, !thread.IsArchived);
        }

        void RenameIfChanged(string value)
        {
            var trimmed = value.Trim();
            if (supportsNativeActions
                && trimmed.Length > 0
                && !trimmed.Equals(thread.Title.Trim(), StringComparison.Ordinal))
            {
                renameThread(thread, trimmed);
            }
        }

        void RenameFromSender(object sender)
        {
            RenameIfChanged(sender is TextBox textBox ? textBox.Text : titleDraft);
        }

        void RenameOnEnter(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            RenameFromSender(sender);
        }

        void SaveMetadataDraft(EditDraft nextDraft)
        {
            setDraft(nextDraft);
            saveMetadata(nextDraft);
        }

        void SaveFolderFromSender(object sender)
        {
            SaveMetadataDraft(draft with { Folder = sender is TextBox textBox ? textBox.Text : draft.Folder });
        }

        void SaveNotesFromSender(object sender)
        {
            SaveMetadataDraft(draft with { Notes = sender is TextBox textBox ? textBox.Text : draft.Notes });
        }

        void SaveOnEnter(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args, Action<object> save)
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            save(sender);
        }

        return Border(
                (ScrollViewer(
                    FlexColumn(
                    BodyStrong("Thread").Flex(shrink: 0),
                    If(
                        supportsNativeActions,
                        () => TextBox(
                                titleDraft,
                                setTitleDraft,
                                placeholderText: "Codex title",
                                header: "Codex title")
                            .AutomationId("ThreadTitleTextBox")
                            .OnLostFocus((sender, _) => RenameFromSender(sender))
                            .OnKeyDown(RenameOnEnter)
                            .Flex(shrink: 0),
                        () => BodyLarge(thread.DisplayTitle)
                            .TextWrapping()
                            .Flex(shrink: 0)),
                    MetadataLine("Updated", thread.UpdatedLocal),
                    MetadataLine("Thread ID", thread.Id),
                    MetadataLine("Workspace", EmptyText(thread.Workspace)),
                    MetadataLine("Model", EmptyText(thread.Model)),
                    MetadataLine("State", thread.IsArchived ? "Archived" : "Active"),
                    Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 4, 0, 4),
                    CheckBox(
                            draft.Favorite,
                            value => SaveMetadataDraft(draft with { Favorite = value }),
                            "Favorite")
                        .AutomationId("FavoriteCheckBox")
                        .Flex(shrink: 0),
                    TextBox(
                            draft.Folder,
                            value => setDraft(draft with { Folder = value }),
                            placeholderText: "Folder",
                            header: "Folder")
                        .AutomationId("FolderTextBox")
                        .OnLostFocus((sender, _) => SaveFolderFromSender(sender))
                        .OnKeyDown((sender, args) => SaveOnEnter(sender, args, SaveFolderFromSender))
                        .Flex(shrink: 0),
                    RenderThreadTagSection(thread, tags, toggleThreadTag, openTagManager)
                        .Flex(shrink: 0),
                    TextBox(
                            draft.Notes,
                            value => setDraft(draft with { Notes = value }),
                            placeholderText: "Notes",
                            header: "Notes")
                        .AutomationId("NotesTextBox")
                        .OnLostFocus((sender, _) => SaveNotesFromSender(sender))
                        .AcceptsReturn()
                        .TextWrapping()
                        .MinHeight(118)
                        .Flex(shrink: 0),
                    If(
                        supportsNativeActions,
                        () => DetailsButton(thread.IsArchived ? "Unarchive" : "Archive", ToggleArchive)
                            .AutomationName(thread.IsArchived ? "Unarchive thread" : "Archive thread")
                            .AutomationId("ArchiveToggleButton")
                            .HAlign(HorizontalAlignment.Stretch)
                            .Flex(shrink: 0),
                        () => Empty()),
                    FlexRow(
                        DetailsButton("Open", OpenInCodex)
                            .AutomationId("OpenInCodexButton")
                            .Flex(grow: 1, basis: 0),
                        DetailsButton("Reveal", RevealJsonl)
                            .AutomationId("RevealFileButton")
                            .Flex(grow: 1, basis: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    })
                with
                {
                    RowGap = 10
                }) with
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                })
                .AutomationId("DetailsScrollViewer")
                .Flex(grow: 1, basis: 0))
            .Padding(16)
            .CornerRadius(8)
            .Background(Theme.LayerFill)
            .WithBorder(Theme.CardStroke, 1)
            .Flex(basis: 430, shrink: 0);
    }

    private static Element RenderThreadTagSection(
        CodexThread thread,
        IReadOnlyList<TagDefinition> tags,
        Action<CodexThread, TagDefinition> toggleThreadTag,
        Action openTagManager)
    {
        var selectedTags = tags
            .Where(tag => thread.Metadata.Tags.Any(name => name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var availableTags = tags
            .Where(tag => !thread.Metadata.Tags.Any(name => name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var selectedButtons = selectedTags
            .Select(tag => TagToggleButton(
                tag,
                selected: true,
                () => toggleThreadTag(thread, tag)))
            .ToArray();
        var availableButtons = availableTags
            .Select(tag => TagToggleButton(
                tag,
                selected: false,
                () => toggleThreadTag(thread, tag)))
            .ToArray();

        return FlexColumn(
                BodyStrong("Thread tags"),
                If(
                    tags.Count == 0,
                    () => Caption("No global tags").Foreground(Theme.SecondaryText),
                    () => FlexColumn(
                            If(
                                selectedButtons.Length == 0,
                                () => Caption("No tags selected").Foreground(Theme.SecondaryText),
                                () => ScrollViewer(
                                        FlexRow(selectedButtons) with
                                        {
                                            ColumnGap = 6,
                                            RowGap = 6,
                                            AlignItems = FlexAlign.Center,
                                            Wrap = FlexWrap.Wrap
                                        })
                                    .MaxHeight(74)),
                            If(
                                availableButtons.Length == 0,
                                () => Empty(),
                                () => FlexColumn(
                                        BodyStrong("Add tags"),
                                        ScrollViewer(
                                                FlexRow(availableButtons) with
                                                {
                                                    ColumnGap = 6,
                                                    RowGap = 6,
                                                    AlignItems = FlexAlign.Center,
                                                    Wrap = FlexWrap.Wrap
                                                })
                                            .MaxHeight(92))
                                    with
                                    {
                                        RowGap = 6
                                    }))
                        with
                        {
                            RowGap = 8
                        }),
                DetailsButton("Manage tags", openTagManager)
                    .AutomationName("Open tag manager")
                    .AutomationId("OpenTagManagerButton")
                    .HAlign(HorizontalAlignment.Stretch))
            with
            {
                RowGap = 8
            };
    }

    private static ButtonElement DetailsButton(string label, Action action) =>
        Button(label, action)
            .AutomationName(label)
            .Set(button =>
            {
                button.BorderThickness = new Thickness(1);
                button.MinHeight = 32;
                button.Padding = new Thickness(12, 5, 12, 6);
            })
            .Resources(resources => resources
                .Set("ButtonBackground", Theme.Ref("ControlFillColorDefaultBrush"))
                .Set("ButtonBackgroundPointerOver", Theme.Ref("ControlFillColorSecondaryBrush"))
                .Set("ButtonBackgroundPressed", Theme.Ref("ControlFillColorTertiaryBrush"))
                .Set("ButtonBackgroundDisabled", Theme.Ref("SubtleFillColorDisabledBrush"))
                .Set("ButtonForeground", Theme.PrimaryText)
                .Set("ButtonForegroundPointerOver", Theme.PrimaryText)
                .Set("ButtonForegroundPressed", Theme.PrimaryText)
                .Set("ButtonForegroundDisabled", Theme.Ref("TextFillColorDisabledBrush"))
                .Set("ButtonBorderBrush", Theme.Ref("ControlStrongStrokeColorDefaultBrush"))
                .Set("ButtonBorderBrushPointerOver", Theme.Ref("ControlStrongStrokeColorDefaultBrush"))
                .Set("ButtonBorderBrushPressed", Theme.ControlStrokeSecondary)
                .Set("ButtonBorderBrushDisabled", Theme.Ref("ControlStrokeColorDefaultBrush")));

    private static Element RenderTagManagerPage(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyList<TagDefinition> tags,
        TagEditorDraft tagEditor,
        Action<TagEditorDraft> setTagEditor,
        Action<TagEditorDraft, global::Windows.UI.Color> setTagEditorColor,
        Action saveTagDefinition,
        string pendingDeleteTag,
        Action<string> setPendingDeleteTag,
        Action<string> deleteTagDefinition,
        Action close,
        string status)
    {
        var summaries = ThreadFilters.BuildTagSummaries(threads, tags);
        var rows = summaries
            .Select(summary => TagCatalogRow(
                summary,
                tagEditor,
                setTagEditor,
                pendingDeleteTag,
                setPendingDeleteTag,
                deleteTagDefinition))
            .ToArray();

        return Border(
                FlexColumn(
                    FlexRow(
                        BodyStrong("Tag manager").Flex(grow: 1, basis: 0),
                        Button("New tag", () =>
                            {
                                setPendingDeleteTag("");
                                setTagEditor(TagEditorDraft.Empty);
                            })
                            .AutomationId("TagManagerNew")
                            .SubtleButton()
                            .Flex(shrink: 0),
                        Button("Back", close)
                            .AutomationId("TagManagerBack")
                            .SubtleButton()
                            .Flex(shrink: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    },
                    FlexRow(
                        If(
                            rows.Length == 0,
                            () => Border(
                                    FlexColumn(
                                        BodyLarge("No tags"),
                                        Caption("Create a tag to use it on threads.").Foreground(Theme.SecondaryText))
                                    with
                                    {
                                        RowGap = 8,
                                        AlignItems = FlexAlign.Center,
                                        JustifyContent = FlexJustify.Center
                                    })
                                .Flex(grow: 1, basis: 0),
                            () => ScrollViewer(
                                    FlexColumn(rows) with
                                    {
                                        RowGap = 8
                                    })
                                .Flex(grow: 1, basis: 0)),
                        Border(
                                FlexColumn(
                                    BodyStrong(tagEditor.EditingName.Length == 0 ? "New tag" : "Edit tag"),
                                    TextBox(
                                            tagEditor.Name,
                                            value => setTagEditor(tagEditor with { Name = value }),
                                            placeholderText: "tag name",
                                            header: "Name")
                                        .AutomationId("TagEditorName")
                                        .Flex(shrink: 0),
                                    RenderTagColorPicker(tagEditor, setTagEditorColor),
                                    TextBox(
                                            tagEditor.Description,
                                            value => setTagEditor(tagEditor with { Description = value }),
                                            placeholderText: "Description",
                                            header: "Description")
                                        .AutomationId("TagEditorDescription")
                                        .Flex(shrink: 0),
                                    FlexRow(
                                        Button(tagEditor.EditingName.Length == 0 ? "Create" : "Save", saveTagDefinition)
                                            .AutomationName(tagEditor.EditingName.Length == 0 ? "Create tag" : "Save tag")
                                            .AutomationId("TagEditorSave")
                                            .AccentButton()
                                            .Flex(grow: 1, basis: 0),
                                        Button("Cancel", () => setTagEditor(TagEditorDraft.Empty))
                                            .AutomationName("Cancel tag edit")
                                            .AutomationId("TagEditorCancel")
                                            .SubtleButton()
                                            .Flex(shrink: 0))
                                    with
                                    {
                                        ColumnGap = 8,
                                        AlignItems = FlexAlign.Center
                                    })
                                with
                                {
                                    RowGap = 10
                                })
                            .Padding(12)
                            .CornerRadius(8)
                            .Background(Theme.LayerFill)
                            .WithBorder(Theme.CardStroke, 1)
                            .Flex(basis: 330, shrink: 0))
                    with
                    {
                        ColumnGap = 14,
                        AlignItems = FlexAlign.Stretch
                    },
                    Caption(status).Foreground(Theme.SecondaryText).Flex(shrink: 0))
                with
                {
                    RowGap = 12
                })
            .Padding(14)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .Flex(grow: 1, basis: 0);
    }

    private static Element RenderTagColorPicker(
        TagEditorDraft tagEditor,
        Action<TagEditorDraft, global::Windows.UI.Color> setTagEditorColor)
    {
        var normalized = ThreadShelfRepository.NormalizeTagColor(tagEditor.Color);
        return FlexColumn(
                FlexRow(
                    BodyStrong("Color").Flex(grow: 1, basis: 0),
                    Border(Empty())
                        .Size(30, 30)
                        .CornerRadius(4)
                        .Background(normalized)
                        .WithBorder(Theme.CardStroke, 1)
                        .Flex(shrink: 0),
                    Caption(normalized)
                        .AutomationId("TagEditorColorValue")
                        .Foreground(Theme.SecondaryText)
                        .Flex(shrink: 0))
                with
                {
                    ColumnGap = 8,
                    AlignItems = FlexAlign.Center
                },
                ColorPicker(
                        TagColorToWinUIColor(tagEditor.Color),
                        color => setTagEditorColor(tagEditor, color))
                    .AutomationId("TagEditorColor")
                    .AlphaEnabled(false)
                    .ColorChannelTextInputVisible(false)
                    .HexInputVisible(false)
                    .MoreButtonVisible(false)
                    .HAlign(HorizontalAlignment.Stretch)
                    .Flex(shrink: 0))
            with
            {
                RowGap = 8
            };
    }

    private static Element TagToggleButton(TagDefinition tag, bool selected, Action toggle)
    {
        var foreground = ForegroundFor(tag.Color);
        return Button(tag.Name, toggle)
            .AutomationName(selected ? $"Remove tag {tag.Name}" : $"Add tag {tag.Name}")
            .AutomationId($"ThreadTagToggle_{AutomationToken(tag.Name)}")
            .ToolTip(string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description)
            .Resources(resources =>
            {
                if (selected)
                {
                    resources
                        .Set("ButtonBackground", tag.Color)
                        .Set("ButtonBackgroundPointerOver", tag.Color)
                        .Set("ButtonBackgroundPressed", tag.Color)
                        .Set("ButtonForeground", foreground)
                        .Set("ButtonForegroundPointerOver", foreground)
                        .Set("ButtonForegroundPressed", foreground)
                        .Set("ButtonBorderBrush", tag.Color);
                }
                else
                {
                    resources
                        .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                        .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .Set("ButtonForeground", Theme.PrimaryText)
                        .Set("ButtonBorderBrush", tag.Color);
                }
            })
            .WithKey($"tag-toggle-{AutomationToken(tag.Name)}-{(selected ? "selected" : "normal")}");
    }

    private static Element TagCatalogRow(
        TagSummary summary,
        TagEditorDraft tagEditor,
        Action<TagEditorDraft> setTagEditor,
        string pendingDeleteTag,
        Action<string> setPendingDeleteTag,
        Action<string> deleteTagDefinition)
    {
        var tag = summary.Definition;
        var editing = tagEditor.EditingName.Equals(tag.Name, StringComparison.OrdinalIgnoreCase);
        var confirmingDelete = pendingDeleteTag.Equals(tag.Name, StringComparison.OrdinalIgnoreCase);
        var description = string.IsNullOrWhiteSpace(tag.Description) ? "-" : tag.Description;

        return Border(
                FlexRow(
                    TagBadge(tag).Flex(shrink: 0),
                    (FlexColumn(
                        Caption(description)
                            .Foreground(Theme.SecondaryText)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1),
                        Caption($"{summary.Count:N0} threads")
                            .Foreground(Theme.TertiaryText))
                    with
                    {
                        RowGap = 2
                    }).Flex(grow: 1, basis: 0),
                    Button("Edit", () =>
                        {
                            setPendingDeleteTag("");
                            setTagEditor(new TagEditorDraft(
                                tag.Name,
                                tag.Name,
                                tag.Color,
                                tag.Description));
                        })
                        .AutomationId($"TagEdit_{AutomationToken(tag.Name)}")
                        .SubtleButton()
                        .Flex(shrink: 0),
                    Button(confirmingDelete ? "Confirm" : "Delete", () =>
                        {
                            if (confirmingDelete)
                            {
                                deleteTagDefinition(tag.Name);
                            }
                            else
                            {
                                setPendingDeleteTag(tag.Name);
                            }
                        })
                        .AutomationName(confirmingDelete ? $"Confirm delete tag {tag.Name}" : $"Delete tag {tag.Name}")
                        .AutomationId($"TagDelete_{AutomationToken(tag.Name)}")
                        .SubtleButton()
                        .Flex(shrink: 0))
                with
                {
                    ColumnGap = 8,
                    AlignItems = FlexAlign.Center
                })
            .Padding(6)
            .CornerRadius(6)
            .Background(editing ? Theme.ControlFillSecondary : Theme.LayerFill)
            .WithBorder(editing ? Theme.Accent : Theme.CardStroke, editing ? 2 : 1)
            .WithKey($"tag-catalog-{AutomationToken(tag.Name)}-{(editing ? "editing" : "normal")}-{(confirmingDelete ? "delete" : "idle")}");
    }

    private static Element TagBadge(TagDefinition tag) =>
        Border(
                Caption(tag.Name)
                    .Foreground(ForegroundFor(tag.Color))
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1))
            .Padding(7, 2)
            .CornerRadius(4)
            .Background(tag.Color)
            .WithBorder(tag.Color, 1)
            .MaxWidth(148)
            .ToolTip(string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description);

    private static Element CompactTagBadge(TagDefinition tag) =>
        Border(
                Caption(tag.Name)
                    .Foreground(ForegroundFor(tag.Color))
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1))
            .Padding(6, 1)
            .CornerRadius(4)
            .Background(tag.Color)
            .WithBorder(tag.Color, 1)
            .MaxWidth(120)
            .ToolTip(string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description);

    private static Element MetadataLine(string label, string value) =>
        FlexRow(
            Caption(label).Foreground(Theme.SecondaryText).Width(76).Flex(shrink: 0),
            Caption(value)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(1)
                .Foreground(Theme.SecondaryText)
                .Flex(grow: 1, basis: 0))
        with
        {
            ColumnGap = 8,
            AlignItems = FlexAlign.Center
        };

    private static Element Pill(string text, ThemeRef background) =>
        Border(Caption(text).Foreground(Theme.PrimaryText))
            .Padding(7, 2)
            .CornerRadius(4)
            .Background(background);

    private static string DescribeLoad(ThreadShelfSnapshot snapshot)
    {
        var description = $"Loaded {snapshot.Threads.Count:N0} Codex threads via {snapshot.DataSource}";
        return string.IsNullOrWhiteSpace(snapshot.LoadWarning)
            ? description
            : $"{description}; {snapshot.LoadWarning}";
    }

    private static bool SameMetadata(ThreadMetadata left, ThreadMetadata right) =>
        string.Equals(left.Folder, right.Folder, StringComparison.Ordinal)
        && string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
        && left.Favorite == right.Favorite
        && left.Tags.SequenceEqual(right.Tags, StringComparer.OrdinalIgnoreCase);

    private static bool SameTagEditorIdentity(TagEditorDraft current, TagEditorDraft rendered) =>
        string.Equals(current.EditingName, rendered.EditingName, StringComparison.Ordinal)
        && string.Equals(current.Name, rendered.Name, StringComparison.Ordinal);

    private static string ForegroundFor(string color)
    {
        var normalized = ThreadShelfRepository.NormalizeTagColor(color);
        var red = Convert.ToInt32(normalized.Substring(1, 2), 16);
        var green = Convert.ToInt32(normalized.Substring(3, 2), 16);
        var blue = Convert.ToInt32(normalized.Substring(5, 2), 16);
        var luminance = (0.299 * red + 0.587 * green + 0.114 * blue) / 255;
        return luminance > 0.58 ? "#1F2328" : "#FFFFFF";
    }

    private static global::Windows.UI.Color TagColorToWinUIColor(string color)
    {
        var normalized = ThreadShelfRepository.NormalizeTagColor(color);
        return global::Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private static string TagColorFromWinUIColor(global::Windows.UI.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string AutomationToken(string value)
    {
        var chars = (value ?? "")
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var token = new string(chars).Trim('_');
        return token.Length == 0 ? "Empty" : token;
    }

    private static string EmptyText(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}
