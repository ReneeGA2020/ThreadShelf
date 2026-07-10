using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ThreadShelf;

using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run(_ => ReactorApp.OpenWindow(
        new WindowSpec
        {
            Title = "ThreadShelf",
            Width = 1240,
            Height = 800,
            MinWidth = 980,
            MinHeight = 560
        },
        () => new App()));

internal class App : Component
{
    private const string ThreadsPage = "threads";
    private const string TagsPage = "tags";

    private sealed record ThreadDragPayload(string ThreadId);
    private sealed record PendingProjectSelection(string Project, string Folder, string TargetThreadId);
    private enum RenameTargetKind
    {
        Project,
        Folder
    }

    private sealed record RenameDraft(RenameTargetKind Kind, string Key, string CurrentName, string Value);
    private sealed class RenameValueBuffer
    {
        public string Value { get; set; } = "";
    }
    private sealed class ArchiveOperationGate
    {
        public bool IsPending { get; set; }
    }

    private sealed record TagEditorDraft(string EditingName, string Name, string Color, string Description)
    {
        public static TagEditorDraft Empty { get; } = new("", "", TagDefinition.DefaultColor, "");
    }

    public override Element Render()
    {
        var repository = UseMemo(() => new ThreadShelfRepository());
        var interactiveLauncher = UseMemo(() => new CodexInteractiveLauncher());
        var preferenceStore = UseMemo(() => new AppPreferenceStore(repository.CodexHome));
        var initialLanguage = UseMemo(preferenceStore.LoadLanguagePreference);
        var (languagePreference, setLanguagePreference) = UseState(initialLanguage);
        UiText.ApplyLanguage(languagePreference);
        var (snapshot, setSnapshot) = UseState<ThreadShelfSnapshot?>(null);
        var (selectedProject, setSelectedProject) = UseState(ThreadFilters.AllProjects);
        var (selectedFilter, setSelectedFilter) = UseState(ThreadFilters.All);
        var (selectedTag, setSelectedTag) = UseState("");
        var (query, setQuery) = UseState("");
        var (selectedId, setSelectedId) = UseState("");
        var (draft, setDraft) = UseState<EditDraft?>(null);
        var (titleDraft, setTitleDraft) = UseState("");
        var (tagEditor, updateTagEditor) = UseReducer<TagEditorDraft>(TagEditorDraft.Empty);
        var (activePage, setActivePage) = UseState(ThreadsPage);
        var (pendingDeleteTag, setPendingDeleteTag) = UseState("");
        var (pendingProject, setPendingProject) = UseState<PendingProjectSelection?>(null);
        var (renameDraft, setRenameDraft) = UseState<RenameDraft?>(null);
        var (pendingArchiveId, setPendingArchiveId) = UseState("");
        var (status, setStatus) = UseState("");
        var archiveOperationGate = UseMemo(() => new ArchiveOperationGate());
        var renameValueBuffer = UseMemo(() => new RenameValueBuffer());
        var snapshotVersion = snapshot?.LoadedAt.UtcTicks ?? 0L;

        void setTagEditor(TagEditorDraft next)
        {
            updateTagEditor(_ => next);
        }

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
                setStatus(T("LoadFailed", ex.Message));
            }
        }

        void SelectLanguage(int index)
        {
            var next = LanguagePreferenceForIndex(index);
            try
            {
                preferenceStore.SaveLanguagePreference(next);
                setLanguagePreference(next);
                var culture = UiText.ResolveCulture(next);
                setStatus(UiText.Get(
                    "LanguageChanged",
                    culture,
                    LanguageDisplayName(next, culture)));
            }
            catch (Exception ex)
            {
                setStatus(T("LanguageSaveFailed", ex.Message));
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
                setSelectedId(snapshot.Threads.Count > 0 ? snapshot.Threads[0].Id : "");
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
        var filtered = ThreadFilters.Apply(
            threads,
            selectedProject,
            selectedFilter,
            query,
            selectedTag,
            snapshot?.ProjectAliases);
        var selectedThread = filtered.FirstOrDefault(thread =>
                thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? (filtered.Count > 0 ? filtered[0] : null);
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

        UseEffect(() =>
        {
            if (selectedProject is ThreadFilters.AllProjects or ThreadFilters.NoProject
                || threads.Any(thread => ThreadFilters.NormalizeProjectKey(thread.Workspace).Equals(
                    ThreadFilters.NormalizeProjectKey(selectedProject),
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            setSelectedProject(ThreadFilters.AllProjects);
        }, snapshotVersion, selectedProject);

        void SelectThread(CodexThread thread)
        {
            setSelectedId(thread.Id);
            setDraft(EditDraft.From(thread.Metadata));
            setTitleDraft(thread.Title);
        }

        UseEffect(() =>
        {
            if (pendingProject is null
                || pendingProject.TargetThreadId.Length > 0
                && !pendingProject.TargetThreadId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            setSelectedProject(pendingProject.Project);
            setSelectedFilter(pendingProject.Folder);
            setActivePage(ThreadsPage);
            setPendingProject(null);
        },
        pendingProject?.Project ?? "",
        pendingProject?.Folder ?? "",
        pendingProject?.TargetThreadId ?? "",
        selectedId);

        UseEffect(() =>
        {
            if (snapshot is null
                || !activePage.Equals(ThreadsPage, StringComparison.OrdinalIgnoreCase)
                || pendingProject is not null
                || filtered.Count == 0
                || filtered.Any(thread => thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            SelectThread(filtered[0]);
        },
        snapshotVersion,
        selectedProject,
        selectedFilter,
        selectedTag,
        query,
        selectedId,
        activePage,
        pendingProject?.TargetThreadId ?? "");

        void SelectProject(string project)
        {
            var projectThreads = ThreadFilters.FilterByProject(threads, project);
            var folderStillExists = selectedFilter is ThreadFilters.All
                or ThreadFilters.Active
                or ThreadFilters.Archived
                or ThreadFilters.Favorites
                or ThreadFilters.Unfiled
                || projectThreads.Any(thread => thread.DisplayFolder.Equals(
                    selectedFilter,
                    StringComparison.OrdinalIgnoreCase));
            var nextFilter = folderStillExists ? selectedFilter : ThreadFilters.All;
            var matches = ThreadFilters.Apply(threads, project, nextFilter, query, selectedTag);
            var nextThread = matches.Count > 0 ? matches[0] : null;

            // Stage selection before narrowing the list. Reactor preview.11 can retain
            // stale pooled resource keys during a same-pass remount (upstream #675).
            // This may be removable after ThreadShelf picks up the published fix.
            setPendingProject(new PendingProjectSelection(
                project,
                nextFilter,
                nextThread?.Id ?? ""));
            if (nextThread is not null
                && !nextThread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            {
                SelectThread(nextThread);
            }
        }

        void SelectFilter(string filter)
        {
            setSelectedFilter(filter);
            setActivePage(ThreadsPage);
            var matches = ThreadFilters.Apply(threads, selectedProject, filter, query, selectedTag);
            var next = matches.Count > 0 ? matches[0] : null;
            if (next is not null)
            {
                SelectThread(next);
            }
        }

        void SelectTagFilter(string tag)
        {
            setSelectedTag(tag);
            setActivePage(ThreadsPage);
            var matches = ThreadFilters.Apply(threads, selectedProject, selectedFilter, query, tag);
            var next = matches.Count > 0 ? matches[0] : null;
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
                setStatus(T("SaveFailed", ex.Message));
            }
        }

        void SaveDraft(EditDraft draftToSave)
        {
            if (selectedThread is null)
            {
                return;
            }

            var metadata = ThreadShelfRepository.MetadataFrom(draftToSave, selectedThread.Metadata.Tags);
            SaveThreadMetadata(selectedThread, metadata, T("MetadataSaved", ThreadTitle(selectedThread)));
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
            var destination = targetFolder.Length == 0 ? T("Unfiled") : targetFolder;
            SaveThreadMetadata(thread, metadata, T("MovedThread", ThreadTitle(thread), destination));
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
                ? [.. thread.Metadata.Tags.Where(name => !name.Equals(tagName, StringComparison.OrdinalIgnoreCase))]
                : thread.Metadata.Tags
                    .Concat([tagName])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

            SaveThreadMetadata(
                thread,
                thread.Metadata with { Tags = nextTags },
                hasTag
                    ? T("RemovedTag", tagName, ThreadTitle(thread))
                    : T("AddedTag", tagName, ThreadTitle(thread)));
        }

        void SaveTagDefinition()
        {
            var name = ThreadShelfRepository.NormalizeTagName(tagEditor.Name);
            if (name.Length == 0)
            {
                setStatus(T("TagSaveNameEmpty"));
                return;
            }

            if (!ThreadShelfRepository.IsValidTagColor(tagEditor.Color))
            {
                setStatus(T("TagColorInvalid"));
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
                    ? T("AddedTagDefinition", definition.Name)
                    : T("SavedTag", definition.Name));
            }
            catch (Exception ex)
            {
                setStatus(T("TagSaveFailed", ex.Message));
            }
        }

        void DeleteTagDefinition(string name)
        {
            var tagName = ThreadShelfRepository.NormalizeTagName(name);
            if (tagName.Length == 0)
            {
                setStatus(T("TagDeleteNameEmpty"));
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
                setStatus(T("DeletedTag", tagName));
            }
            catch (Exception ex)
            {
                setStatus(T("TagDeleteFailed", ex.Message));
            }
        }

        async void ToggleArchived(CodexThread thread, bool archived)
        {
            if (archiveOperationGate.IsPending)
            {
                return;
            }

            archiveOperationGate.IsPending = true;
            setPendingArchiveId(thread.Id);
            setStatus(archived
                ? T("Archiving", ThreadTitle(thread))
                : T("Unarchiving", ThreadTitle(thread)));

            try
            {
                var loaded = await Task.Run(() =>
                {
                    repository.SetArchived(thread.Id, archived);
                    return repository.Load();
                });
                setSnapshot(loaded);
                setStatus(archived
                    ? T("ArchivedThread", ThreadTitle(thread))
                    : T("UnarchivedThread", ThreadTitle(thread)));
            }
            catch (Exception ex)
            {
                setStatus(T("ArchiveActionFailed", ex.Message));
            }
            finally
            {
                archiveOperationGate.IsPending = false;
                setPendingArchiveId("");
            }
        }

        void RenameThread(CodexThread thread, string name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0)
            {
                setStatus(T("RenameTitleEmpty"));
                return;
            }

            try
            {
                repository.SetName(thread.Id, trimmed);
                Reload();
                setStatus(T("RenamedThread", trimmed));
            }
            catch (Exception ex)
            {
                setStatus(T("RenameFailed", ex.Message));
            }
        }

        void ApplyNavigationRename(RenameDraft rename)
        {
            if (snapshot is null)
            {
                return;
            }

            try
            {
                if (rename.Kind == RenameTargetKind.Project)
                {
                    repository.RenameProjectAlias(rename.Key, rename.Value, snapshot.Threads);
                    Reload();
                    setStatus(T("ProjectAliasRenamed", rename.CurrentName, rename.Value.Trim()));
                }
                else
                {
                    repository.RenameFolder(
                        selectedProject,
                        rename.Key,
                        rename.Value,
                        snapshot.Threads);
                    if (selectedFilter.Equals(rename.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        setSelectedFilter(rename.Value.Trim());
                    }

                    Reload();
                    setStatus(T("FolderRenamed", rename.CurrentName, rename.Value.Trim()));
                }
            }
            catch (ThreadShelfValidationException ex)
            {
                setStatus(RenameValidationMessage(ex));
            }
            catch (Exception ex)
            {
                setStatus(T("RenameFailed", ex.Message));
            }
            finally
            {
                setRenameDraft(null);
            }
        }

        void BeginProjectRename(ProjectSummary project)
        {
            renameValueBuffer.Value = project.Name;
            setRenameDraft(new RenameDraft(
                RenameTargetKind.Project,
                project.Key,
                project.Name,
                project.Name));
        }

        void BeginFolderRename(FolderSummary folder)
        {
            renameValueBuffer.Value = folder.Name;
            setRenameDraft(new RenameDraft(
                RenameTargetKind.Folder,
                folder.Name,
                folder.Name,
                folder.Name));
        }

        void StartNewTask(ProjectSummary project)
        {
            try
            {
                interactiveLauncher.LaunchNewTask(project.Workspace);
                setStatus(T("NewTaskStarted", project.Name));
            }
            catch (Exception ex)
            {
                setStatus(T("CodexLaunchFailed", CodexLaunchError(ex)));
            }
        }

        void ResumeThread(CodexThread thread)
        {
            try
            {
                interactiveLauncher.ResumeThread(thread.Workspace, thread.Id);
                setStatus(T("ResumeStarted", ThreadTitle(thread)));
            }
            catch (Exception ex)
            {
                setStatus(T("CodexLaunchFailed", CodexLaunchError(ex)));
            }
        }

        var titleBar = TitleBar("ThreadShelf")
            .Subtitle(snapshot is null
                ? T("LoadingCodexThreads")
                : T("IndexedThreads", threads.Count, DataSourceLabel(snapshot)))
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
                snapshot.ProjectAliases,
                activePage,
                ShowThreads,
                ShowTagManager,
                selectedProject,
                SelectProject,
                selectedFilter,
                SelectFilter,
                selectedTag,
                SelectTagFilter,
                MoveThreadToFolder,
                BeginProjectRename,
                BeginFolderRename,
                StartNewTask,
                interactiveLauncher,
                snapshot.SidecarPath,
                languagePreference,
                SelectLanguage);

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
                        snapshot.ProjectAliases,
                        selectedThread?.Id ?? "",
                        query,
                        setQuery,
                        SelectThread,
                        ToggleArchived,
                        snapshot.SupportsNativeActions,
                        pendingArchiveId,
                        ResumeThread,
                        interactiveLauncher,
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
                        pendingArchiveId,
                        ResumeThread,
                        interactiveLauncher,
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
                    .Flex(grow: 1, basis: 0),
                RenderRenameDialog(renameDraft, setRenameDraft, renameValueBuffer, ApplyNavigationRename))
            .Backdrop(BackdropKind.Mica);
    }

    private static ContentDialogElement RenderRenameDialog(
        RenameDraft? draft,
        Action<RenameDraft?> setDraft,
        RenameValueBuffer valueBuffer,
        Action<RenameDraft> submit)
    {
        var isProject = draft?.Kind == RenameTargetKind.Project;
        return ContentDialog(
                isProject ? T("RenameProject") : T("RenameFolder"),
                FlexColumn(
                    If(
                        isProject,
                        () => Caption(T("ProjectAliasNotice")).TextWrapping().Foreground(Theme.SecondaryText),
                        () => Caption(T("FolderRenameNotice")).TextWrapping().Foreground(Theme.SecondaryText)),
                    TextBox(
                            draft?.Value ?? "",
                            value =>
                            {
                                if (draft is not null)
                                {
                                    valueBuffer.Value = value;
                                    setDraft(draft with { Value = value });
                                }
                            },
                            placeholderText: T("NewName"),
                            header: T("NewName"))
                        .AutomationId("RenameInput"))
                with
                {
                    RowGap = 10
                },
                T("Rename")) with
        {
            IsOpen = draft is not null,
            SecondaryButtonText = T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(draft?.Value),
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary && draft is not null)
                {
                    submit(draft with { Value = valueBuffer.Value });
                }
                else
                {
                    setDraft(null);
                }
            }
        };
    }

    private static BorderElement RenderLoading(string status)
    {
        return Border(
            FlexColumn(
                ProgressRing().IsActive(),
                BodyLarge(T("LoadingThreadIndex")),
                Caption(status))
            with
            {
                RowGap = 12,
                AlignItems = FlexAlign.Center,
                JustifyContent = FlexJustify.Center
            })
        .Flex(grow: 1, basis: 0);
    }

    private static BorderElement RenderSidebar(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyList<TagDefinition> tags,
        IReadOnlyDictionary<string, string> projectAliases,
        string activePage,
        Action showThreads,
        Action showTagManager,
        string selectedProject,
        Action<string> selectProject,
        string selectedFilter,
        Action<string> selectFilter,
        string selectedTag,
        Action<string> selectTag,
        Action<ThreadDragPayload, string> moveThreadToFolder,
        Action<ProjectSummary> renameProject,
        Action<FolderSummary> renameFolder,
        Action<ProjectSummary> startNewTask,
        CodexInteractiveLauncher interactiveLauncher,
        string sidecarPath,
        string languagePreference,
        Action<int> selectLanguage)
    {
        var projects = ThreadFilters.BuildProjectSummaries(threads, projectAliases);
        var projectThreads = ThreadFilters.FilterByProject(threads, selectedProject);
        var folders = ThreadFilters.BuildFolderSummaries(projectThreads);
        var folderThreads = ThreadFilters.Apply(projectThreads, selectedFilter, "", "");
        var tagSummaries = ThreadFilters.BuildTagSummaries(folderThreads, tags);
        var favoriteCount = projectThreads.Count(thread => thread.Metadata.Favorite);
        var unfiledCount = projectThreads.Count(thread => string.IsNullOrWhiteSpace(thread.Metadata.Folder));
        var activeCount = projectThreads.Count(thread => !thread.IsArchived);
        var archivedCount = projectThreads.Count(thread => thread.IsArchived);
        var noProjectCount = threads.Count(thread =>
            ThreadFilters.NormalizeProjectKey(thread.Workspace).Length == 0);

        var projectButtons = projects
            .Select(project => ProjectFilterButton(
                project,
                selectedProject,
                selectProject,
                renameProject,
                startNewTask,
                interactiveLauncher))
            .ToArray();
        Element[] noProjectButtons = noProjectCount > 0
            ? [ProjectFilterButton(
                ThreadFilters.NoProject,
                T("CountLabel", T("NoProject"), noProjectCount),
                T("ThreadsWithoutWorkspace"),
                selectedProject,
                selectProject)
                .WithContextFlyout(MenuItems([
                    MenuItem(T("NewTask")) with
                    {
                        IsEnabled = false,
                        Description = T("WorkspaceMissingLaunch")
                    }
                ]))]
            : [];

        var folderButtons = folders
            .Select(folder => FolderFilterButton(
                folder,
                selectedProject,
                selectedFilter,
                selectFilter,
                moveThreadToFolder,
                renameFolder))
            .ToArray();

        var tagButtons = tagSummaries
            .Select(summary => TagFilterButton(summary, selectedTag, selectTag))
            .ToArray();

        return Border(
                FlexColumn(
                    BodyStrong(T("View")).Flex(shrink: 0),
                    ComboBox(LanguageOptions(), LanguagePreferenceIndex(languagePreference), selectLanguage)
                        .AutomationName(T("Language"))
                        .AutomationId("LanguageSelector")
                        .WithKey($"language-{UiText.NormalizeLanguagePreference(languagePreference)}")
                        .HAlign(HorizontalAlignment.Stretch)
                        .Flex(shrink: 0),
                    PageButton(ThreadsPage, T("Threads"), activePage, showThreads)
                        .AutomationId("View_Threads"),
                    PageButton(TagsPage, T("TagManager"), activePage, showTagManager)
                        .AutomationId("View_TagManager"),
                    Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 8, 0, 4),
                    ScrollViewer(
                        FlexColumn(
                            [BodyStrong(T("Projects")),
                            ProjectFilterButton(
                                ThreadFilters.AllProjects,
                                T("CountLabel", T("AllProjects"), threads.Count),
                                T("AllCodexWorkspaces"),
                                selectedProject,
                                selectProject)
                                .WithContextFlyout(MenuItems([
                                    MenuItem(T("NewTask")) with
                                    {
                                        IsEnabled = false,
                                        Description = T("AllProjectsCannotLaunch")
                                    }
                                ])),
                            .. projectButtons,
                            .. noProjectButtons,
                            Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 10, 0, 6),
                            BodyStrong(T("Folders")),
                            FilterButton(
                                ThreadFilters.All,
                                T("CountLabel", T("AllThreads"), projectThreads.Count),
                                selectedFilter,
                                selectFilter)
                                .AutomationId("Filter_All"),
                            FilterButton(
                                ThreadFilters.Active,
                                T("CountLabel", T("Unarchived"), activeCount),
                                selectedFilter,
                                selectFilter)
                                .AutomationId("Filter_Active"),
                            FilterButton(
                                ThreadFilters.Archived,
                                T("CountLabel", T("Archived"), archivedCount),
                                selectedFilter,
                                selectFilter)
                                .AutomationId("Filter_Archived"),
                            FilterButton(
                                ThreadFilters.Favorites,
                                T("CountLabel", T("Favorites"), favoriteCount),
                                selectedFilter,
                                selectFilter)
                                .AutomationId("Filter_Favorites"),
                            FilterButton(
                                ThreadFilters.Unfiled,
                                T("CountLabel", T("Unfiled"), unfiledCount),
                                selectedFilter,
                                selectFilter,
                                payload => moveThreadToFolder(payload, ""),
                                T("MoveToFolder", T("Unfiled")))
                                .AutomationId("Filter_Unfiled"),
                            .. folderButtons,
                            Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 10, 0, 6),
                            BodyStrong(T("Tags")),
                            TagFilterButton(null, folderThreads.Count, selectedTag, selectTag),
                            .. tagButtons]) with
                        {
                            RowGap = 6
                        })
                    .Flex(grow: 1, basis: 0),
                    Caption(T("Sidecar", sidecarPath))
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
            .Flex(basis: 260, shrink: 0);
    }

    private static ButtonElement PageButton(
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

    private static ButtonElement FilterButton(
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

    private static ButtonElement ProjectFilterButton(
        ProjectSummary project,
        string selectedProject,
        Action<string> selectProject,
        Action<ProjectSummary> renameProject,
        Action<ProjectSummary> startNewTask,
        CodexInteractiveLauncher interactiveLauncher)
    {
        var availability = interactiveLauncher.CheckAvailability(project.Workspace);
        return ProjectFilterButton(
            project.Key,
            T("CountLabel", project.Name, project.Count),
            project.Workspace,
            selectedProject,
            selectProject)
        .WithContextFlyout(MenuItems([
            MenuItem(T("NewTask"), () => startNewTask(project)) with
            {
                IsEnabled = availability.CanLaunch,
                Description = availability.CanLaunch
                    ? T("NewTaskToolTip", project.Name)
                    : CodexLaunchProblemText(availability.Problem, availability.Workspace)
            },
            MenuItem(T("Rename"), () => renameProject(project))
        ]));
    }

    private static ButtonElement FolderFilterButton(
        FolderSummary folder,
        string selectedProject,
        string selectedFilter,
        Action<string> selectFilter,
        Action<ThreadDragPayload, string> moveThreadToFolder,
        Action<FolderSummary> renameFolder)
    {
        var button = FilterButton(
            folder.Name,
            T("CountLabel", folder.Name, folder.Count),
            selectedFilter,
            selectFilter,
            payload => moveThreadToFolder(payload, folder.Name),
            T("MoveToFolder", folder.Name));
        return selectedProject.Equals(ThreadFilters.AllProjects, StringComparison.OrdinalIgnoreCase)
            ? button
            : button.WithContextFlyout(MenuItems([
                MenuItem(T("Rename"), () => renameFolder(folder))
            ]));
    }

    private static ButtonElement ProjectFilterButton(
        string value,
        string label,
        string tooltip,
        string selectedProject,
        Action<string> selectProject)
    {
        var automationToken = value switch
        {
            ThreadFilters.AllProjects => "All",
            ThreadFilters.NoProject => "NoProject",
            _ => AutomationToken(value)
        };
        var selected = value.Equals(selectedProject, StringComparison.OrdinalIgnoreCase);

        return FilterButton(value, label, selectedProject, selectProject)
            .AutomationName(T("ProjectAutomation", label))
            .AutomationId($"Project_{automationToken}")
            .ToolTip(tooltip)
            .WithKey($"project-{automationToken}-{(selected ? "selected" : "normal")}");
    }

    private static ButtonElement TagFilterButton(
        TagSummary? summary,
        int totalCount,
        string selectedTag,
        Action<string> selectTag)
    {
        if (summary is null)
        {
            var selected = string.IsNullOrWhiteSpace(selectedTag);
            return Button(T("CountLabel", T("AllTags"), totalCount), () => selectTag(""))
                .AutomationName(T("AllTags"))
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

    private static ButtonElement TagFilterButton(
        TagSummary summary,
        string selectedTag,
        Action<string> selectTag)
    {
        var tag = summary.Definition;
        var selected = tag.Name.Equals(selectedTag, StringComparison.OrdinalIgnoreCase);
        var foreground = ForegroundFor(tag.Color);

        return Button(T("CountLabel", tag.Name, summary.Count), () => selectTag(tag.Name))
            .AutomationName(T("Tag", tag.Name))
            .AutomationId($"TagFilter_{AutomationToken(tag.Name)}")
            .HAlign(HorizontalAlignment.Stretch)
            .ToolTip(string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description)
            .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Stretch)
            .Resources(resources => _ = selected
                    ? resources
                        .Set("ButtonBackground", tag.Color)
                        .Set("ButtonBackgroundPointerOver", tag.Color)
                        .Set("ButtonBackgroundPressed", tag.Color)
                        .Set("ButtonForeground", foreground)
                        .Set("ButtonForegroundPointerOver", foreground)
                        .Set("ButtonForegroundPressed", foreground)
                        .Set("ButtonBorderBrush", tag.Color)
                    : resources
                        .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                        .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .Set("ButtonForeground", Theme.PrimaryText)
                        .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .WithKey($"tag-filter-{AutomationToken(tag.Name)}-{(selected ? "selected" : "normal")}");
    }

    private static BorderElement RenderThreadList(
        IReadOnlyList<CodexThread> threads,
        IReadOnlyList<TagDefinition> tags,
        IReadOnlyDictionary<string, string> projectAliases,
        string selectedId,
        string query,
        Action<string> setQuery,
        Action<CodexThread> selectThread,
        Action<CodexThread, bool> setArchived,
        bool supportsNativeActions,
        string pendingArchiveId,
        Action<CodexThread> resumeThread,
        CodexInteractiveLauncher interactiveLauncher,
        Action reload,
        string status)
    {
        var tagLookup = tags.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        var selectedIndex = -1;
        for (var index = 0; index < threads.Count; index++)
        {
            if (threads[index].Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
                break;
            }
        }

        var list = (ListView<CodexThread>(
                threads,
                thread => thread.Id,
                (thread, _) => RenderThreadRow(
                    thread,
                    thread.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase),
                    tagLookup,
                    projectAliases,
                    selectThread,
                    setArchived,
                    supportsNativeActions,
                    pendingArchiveId,
                    resumeThread,
                    interactiveLauncher))
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
                        TextBox(query, setQuery, placeholderText: T("SearchPlaceholder"))
                            .AutomationName(T("ThreadSearch"))
                            .AutomationId("ThreadSearchBox")
                            .Flex(grow: 1, basis: 0),
                        Button(T("Refresh"), reload)
                            .AutomationName(T("Refresh"))
                            .AutomationId("RefreshButton")
                            .SubtleButton()
                            .Flex(shrink: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    },
                    FlexRow(
                        BodyStrong(T("ShownThreads", threads.Count)),
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
                                    BodyLarge(T("NoMatchingThreads")),
                                    Caption(T("ClearSearchHint")).Foreground(Theme.SecondaryText))
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

    private static BorderElement RenderThreadRow(
        CodexThread thread,
        bool selected,
        Dictionary<string, TagDefinition> tagLookup,
        IReadOnlyDictionary<string, string> projectAliases,
        Action<CodexThread> selectThread,
        Action<CodexThread, bool> setArchived,
        bool supportsNativeActions,
        string pendingArchiveId,
        Action<CodexThread> resumeThread,
        CodexInteractiveLauncher interactiveLauncher)
    {
        var status = thread.IsArchived ? T("Archived") : T("Unarchived");
        var source = string.IsNullOrWhiteSpace(thread.Source) ? thread.Originator : thread.Source;
        var location = string.IsNullOrWhiteSpace(thread.Workspace) ? source : thread.Workspace;
        var projectName = ProjectDisplayName(thread.Workspace, projectAliases);
        var visibleTags = thread.Metadata.Tags.Take(4).ToArray();
        var hiddenTagCount = Math.Max(0, thread.Metadata.Tags.Count - visibleTags.Length);

        return Border(
                FlexColumn(
                    FlexRow(
                        Caption("::")
                            .AutomationName(T("DragThreadToFolder", ThreadTitle(thread)))
                            .AutomationId($"ThreadDragHandle_{AutomationToken(thread.Id)}")
                            .ToolTip(T("DragToFolder"))
                            .Foreground(Theme.SecondaryText)
                            .Width(18)
                            .HAlign(HorizontalAlignment.Center)
                            .OnDragStart(() => new ThreadDragPayload(thread.Id), DragOperations.Move)
                            .Flex(shrink: 0),
                        BodyStrong(ThreadTitle(thread))
                            .AutomationId($"ThreadTitle_{AutomationToken(thread.Id)}")
                            .OnPointerPressed((_, _) => selectThread(thread))
                            .OnTapped((_, _) => selectThread(thread))
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1)
                            .Flex(grow: 1, basis: 0),
                        ArchiveStatusButton(
                                thread,
                                status,
                                setArchived,
                                supportsNativeActions,
                                pendingArchiveId)
                            .Flex(shrink: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    },
                    FlexRow(
                        Caption(thread.UpdatedLocal).Foreground(Theme.SecondaryText),
                        Caption(projectName)
                            .ToolTip(string.IsNullOrWhiteSpace(thread.Workspace) ? T("NoCodexWorkspace") : thread.Workspace)
                            .Foreground(Theme.SecondaryText),
                        Caption(FolderDisplayName(thread)).Foreground(Theme.SecondaryText),
                        If(thread.Metadata.Favorite, () => Caption(T("Favorite")).Foreground(Theme.SystemAttention), () => Empty()))
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
                    FlexRow(
                        Caption(string.IsNullOrWhiteSpace(location) ? T("NoCodexWorkspace") : location)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1)
                            .Foreground(Theme.TertiaryText)
                            .Flex(grow: 1, basis: 0),
                        CardResumeButton(thread, selectThread, resumeThread, interactiveLauncher)
                            .Flex(shrink: 0))
                    with
                    {
                        ColumnGap = 8,
                        AlignItems = FlexAlign.Center
                    })
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
            .WithKey(thread.Id);
    }

    private static BorderElement RenderDetails(
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
        string pendingArchiveId,
        Action<CodexThread> resumeThread,
        CodexInteractiveLauncher interactiveLauncher,
        Action<string> setStatus)
    {
        if (thread is null || draft is null)
        {
            return Border(
                    FlexColumn(
                        BodyLarge(T("NoThreadSelected")),
                        Caption(T("SelectThreadHint")).Foreground(Theme.SecondaryText))
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
                setStatus(T("OpenedCodexLink"));
            }
            catch (Exception ex)
            {
                setStatus(T("OpenFailed", ex.Message));
            }
        }

        void RevealJsonl()
        {
            try
            {
                ThreadShelfRepository.RevealFile(thread.SourcePath);
                setStatus(T("OpenedSessionLocation"));
            }
            catch (Exception ex)
            {
                setStatus(T("RevealFailed", ex.Message));
            }
        }

        void ToggleArchive()
        {
            setArchived(thread, !thread.IsArchived);
        }

        void Resume()
        {
            resumeThread(thread);
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
                    BodyStrong(T("Thread")).Flex(shrink: 0),
                    If(
                        supportsNativeActions,
                        () => TextBox(
                                titleDraft,
                                setTitleDraft,
                                placeholderText: T("CodexTitle"),
                                header: T("CodexTitle"))
                            .AutomationId("ThreadTitleTextBox")
                            .OnLostFocus((sender, _) => RenameFromSender(sender))
                            .OnKeyDown(RenameOnEnter)
                            .Flex(shrink: 0),
                        () => BodyLarge(ThreadTitle(thread))
                            .TextWrapping()
                            .Flex(shrink: 0)),
                    MetadataLine(T("Updated"), thread.UpdatedLocal),
                    MetadataLine(T("ThreadId"), thread.Id),
                    MetadataLine(T("Workspace"), EmptyText(thread.Workspace)),
                    MetadataLine(T("Model"), EmptyText(thread.Model)),
                    MetadataLine(T("State"), thread.IsArchived ? T("Archived") : T("Unarchived")),
                    Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 4, 0, 4),
                    CheckBox(
                            draft.Favorite,
                            value => SaveMetadataDraft(draft with { Favorite = value }),
                            T("Favorite"))
                        .AutomationId("FavoriteCheckBox")
                        .Flex(shrink: 0),
                    TextBox(
                            draft.Folder,
                            value => setDraft(draft with { Folder = value }),
                            placeholderText: T("Folder"),
                            header: T("Folder"))
                        .AutomationId("FolderTextBox")
                        .OnLostFocus((sender, _) => SaveFolderFromSender(sender))
                        .OnKeyDown((sender, args) => SaveOnEnter(sender, args, SaveFolderFromSender))
                        .Flex(shrink: 0),
                    RenderThreadTagSection(thread, tags, toggleThreadTag, openTagManager)
                        .Flex(shrink: 0),
                    TextBox(
                            draft.Notes,
                            value => setDraft(draft with { Notes = value }),
                            placeholderText: T("Notes"),
                            header: T("Notes"))
                        .AutomationId("NotesTextBox")
                        .OnLostFocus((sender, _) => SaveNotesFromSender(sender))
                        .AcceptsReturn()
                        .TextWrapping()
                        .MinHeight(118)
                        .Flex(shrink: 0),
                    DetailsButton(thread.IsArchived ? T("Unarchive") : T("Archive"), ToggleArchive)
                        .AutomationName(ArchiveAutomationName(thread, supportsNativeActions, pendingArchiveId))
                        .AutomationId("ArchiveToggleButton")
                        .ToolTip(ArchiveToolTip(thread, supportsNativeActions, pendingArchiveId))
                        .IsEnabled(supportsNativeActions && pendingArchiveId.Length == 0)
                        .HAlign(HorizontalAlignment.Stretch)
                        .Flex(shrink: 0),
                    DetailsButton(T("Resume"), Resume)
                        .AutomationName(ResumeAutomationName(interactiveLauncher, thread))
                        .AutomationId("ResumeThreadButton")
                        .ToolTip(ResumeToolTip(interactiveLauncher, thread))
                        .IsEnabled(interactiveLauncher.CheckAvailability(thread.Workspace).CanLaunch)
                        .HAlign(HorizontalAlignment.Stretch)
                        .Flex(shrink: 0),
                    FlexRow(
                        Button(T("Open"), OpenInCodex)
                            .AutomationName(T("Open"))
                            .AutomationId("OpenInCodexButton")
                            .SubtleButton()
                            .Flex(grow: 1, basis: 0),
                        Button(T("Reveal"), RevealJsonl)
                            .AutomationName(T("Reveal"))
                            .AutomationId("RevealFileButton")
                            .SubtleButton()
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

    private static FlexElement RenderThreadTagSection(
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
                BodyStrong(T("ThreadTags")),
                If(
                    tags.Count == 0,
                    () => Caption(T("NoGlobalTags")).Foreground(Theme.SecondaryText),
                    () => FlexColumn(
                            If(
                                selectedButtons.Length == 0,
                                () => Caption(T("NoTagsSelected")).Foreground(Theme.SecondaryText),
                                () => FlexRow(selectedButtons) with
                                {
                                    ColumnGap = 6,
                                    RowGap = 6,
                                    AlignItems = FlexAlign.Center,
                                    Wrap = FlexWrap.Wrap
                                }),
                            If(
                                availableButtons.Length == 0,
                                () => Empty(),
                                () => FlexColumn(
                                        BodyStrong(T("AddTags")),
                                        FlexRow(availableButtons) with
                                        {
                                            ColumnGap = 6,
                                            RowGap = 6,
                                            AlignItems = FlexAlign.Center,
                                            Wrap = FlexWrap.Wrap
                                        })
                                    with
                                {
                                    RowGap = 6
                                }))
                        with
                    {
                        RowGap = 8
                    }),
                DetailsButton(T("ManageTags"), openTagManager)
                    .AutomationName(T("OpenTagManager"))
                    .AutomationId("OpenTagManagerButton")
                    .HAlign(HorizontalAlignment.Stretch))
            with
        {
            RowGap = 8
        };
    }

    private static ButtonElement DetailsButton(string label, Action action)
    {
        return Button(label, action)
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
    }

    private static ButtonElement CardResumeButton(
        CodexThread thread,
        Action<CodexThread> selectThread,
        Action<CodexThread> resumeThread,
        CodexInteractiveLauncher interactiveLauncher)
    {
        var availability = interactiveLauncher.CheckAvailability(thread.Workspace);
        return Button($"▶  {T("Resume")}", () =>
                {
                    selectThread(thread);
                    resumeThread(thread);
                })
            .AutomationName(ResumeAutomationName(interactiveLauncher, thread))
            .AutomationId($"ResumeThreadButton_{AutomationToken(thread.Id)}")
            .ToolTip(availability.CanLaunch
                ? T("ResumeToolTip")
                : CodexLaunchProblemText(availability.Problem, availability.Workspace))
            .IsEnabled(availability.CanLaunch)
            .Set(button =>
            {
                button.MinHeight = 26;
                button.Width = 86;
                button.Padding = new Thickness(8, 3, 9, 3);
                button.CornerRadius = new CornerRadius(13);
                button.BorderThickness = new Thickness(1);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
            })
            .Resources(resources => resources
                .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBackgroundPointerOver", Theme.ControlFillSecondary)
                .Set("ButtonBackgroundPressed", Theme.ControlFillTertiary)
                .Set("ButtonBackgroundDisabled", Theme.Ref("SubtleFillColorDisabledBrush"))
                .Set("ButtonForeground", Theme.AccentText)
                .Set("ButtonForegroundPointerOver", Theme.AccentText)
                .Set("ButtonForegroundPressed", Theme.AccentText)
                .Set("ButtonForegroundDisabled", Theme.DisabledText)
                .Set("ButtonBorderBrush", Theme.AccentTertiary)
                .Set("ButtonBorderBrushPointerOver", Theme.Accent)
                .Set("ButtonBorderBrushPressed", Theme.AccentSecondary)
                .Set("ButtonBorderBrushDisabled", Theme.ControlStroke));
    }

    private static ButtonElement ArchiveStatusButton(
        CodexThread thread,
        string status,
        Action<CodexThread, bool> setArchived,
        bool supportsNativeActions,
        string pendingArchiveId)
    {
        var background = thread.IsArchived
            ? Theme.SystemNeutralBackground
            : Theme.SystemSuccessBackground;

        return Button(status, () => setArchived(thread, !thread.IsArchived))
            .AutomationName(ArchiveAutomationName(thread, supportsNativeActions, pendingArchiveId))
            .AutomationId($"ArchiveStatus_{AutomationToken(thread.Id)}")
            .ToolTip(ArchiveToolTip(thread, supportsNativeActions, pendingArchiveId))
            .IsEnabled(supportsNativeActions && pendingArchiveId.Length == 0)
            .Set(button =>
            {
                button.MinHeight = 0;
                button.Padding = new Thickness(7, 2, 7, 2);
                button.CornerRadius = new CornerRadius(4);
                button.BorderThickness = new Thickness(1);
            })
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBackgroundPointerOver", background)
                .Set("ButtonBackgroundPressed", background)
                .Set("ButtonBackgroundDisabled", Theme.Ref("ControlFillColorDisabledBrush"))
                .Set("ButtonForeground", Theme.PrimaryText)
                .Set("ButtonForegroundPointerOver", Theme.PrimaryText)
                .Set("ButtonForegroundPressed", Theme.PrimaryText)
                .Set("ButtonForegroundDisabled", Theme.Ref("TextFillColorDisabledBrush"))
                .Set("ButtonBorderBrush", background)
                .Set("ButtonBorderBrushPointerOver", Theme.ControlStrokeSecondary)
                .Set("ButtonBorderBrushPressed", Theme.PrimaryText)
                .Set("ButtonBorderBrushDisabled", Theme.Ref("ControlStrokeColorDefaultBrush")));
    }

    private static string ArchiveAutomationName(
        CodexThread thread,
        bool supportsNativeActions,
        string pendingArchiveId)
    {
        var state = thread.IsArchived ? T("Archived") : T("Unarchived");
        var title = ThreadTitle(thread);
        if (!supportsNativeActions)
        {
            return T("ArchiveActionUnavailableAutomation", state, title);
        }

        if (pendingArchiveId.Equals(thread.Id, StringComparison.OrdinalIgnoreCase))
        {
            return thread.IsArchived
                ? T("UnarchivingAutomation", title)
                : T("ArchivingAutomation", title);
        }

        if (pendingArchiveId.Length > 0)
        {
            return T("ArchiveActionBusyAutomation", state, title);
        }

        return thread.IsArchived
            ? T("ArchivedAutomation", title)
            : T("ArchiveAutomation", title);
    }

    private static string ArchiveToolTip(
        CodexThread thread,
        bool supportsNativeActions,
        string pendingArchiveId)
    {
        if (!supportsNativeActions)
        {
            return T("ArchiveActionUnavailable");
        }

        if (pendingArchiveId.Equals(thread.Id, StringComparison.OrdinalIgnoreCase))
        {
            return thread.IsArchived ? T("UnarchivingToolTip") : T("ArchivingToolTip");
        }

        if (pendingArchiveId.Length > 0)
        {
            return T("ArchiveActionBusy");
        }

        return thread.IsArchived ? T("UnarchiveToolTip") : T("ArchiveToolTip");
    }

    private static BorderElement RenderTagManagerPage(
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
                        BodyStrong(T("TagManager")).Flex(grow: 1, basis: 0),
                        Button(T("NewTag"), () =>
                            {
                                setPendingDeleteTag("");
                                setTagEditor(TagEditorDraft.Empty);
                            })
                            .AutomationName(T("NewTag"))
                            .AutomationId("TagManagerNew")
                            .SubtleButton()
                            .Flex(shrink: 0),
                        Button(T("Back"), close)
                            .AutomationName(T("Back"))
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
                                        BodyLarge(T("NoTags")),
                                        Caption(T("CreateTagHint")).Foreground(Theme.SecondaryText))
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
                                    BodyStrong(tagEditor.EditingName.Length == 0 ? T("NewTag") : T("EditTag")),
                                    TextBox(
                                            tagEditor.Name,
                                            value => setTagEditor(tagEditor with { Name = value }),
                                            placeholderText: T("TagName"),
                                            header: T("Name"))
                                        .AutomationId("TagEditorName")
                                        .Flex(shrink: 0),
                                    RenderTagColorPicker(tagEditor, setTagEditorColor),
                                    TextBox(
                                            tagEditor.Description,
                                            value => setTagEditor(tagEditor with { Description = value }),
                                            placeholderText: T("Description"),
                                            header: T("Description"))
                                        .AutomationId("TagEditorDescription")
                                        .Flex(shrink: 0),
                                    FlexRow(
                                        Button(tagEditor.EditingName.Length == 0 ? T("Create") : T("Save"), saveTagDefinition)
                                            .AutomationName(tagEditor.EditingName.Length == 0 ? T("CreateTag") : T("SaveTag"))
                                            .AutomationId("TagEditorSave")
                                            .AccentButton()
                                            .Flex(grow: 1, basis: 0),
                                        Button(T("Cancel"), () => setTagEditor(TagEditorDraft.Empty))
                                            .AutomationName(T("CancelTagEdit"))
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

    private static FlexElement RenderTagColorPicker(
        TagEditorDraft tagEditor,
        Action<TagEditorDraft, global::Windows.UI.Color> setTagEditorColor)
    {
        var normalized = ThreadShelfRepository.NormalizeTagColor(tagEditor.Color);
        return FlexColumn(
                FlexRow(
                    BodyStrong(T("Color")).Flex(grow: 1, basis: 0),
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
                    .AutomationName(T("Color"))
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

    private static ButtonElement TagToggleButton(TagDefinition tag, bool selected, Action toggle)
    {
        var foreground = ForegroundFor(tag.Color);
        return Button(tag.Name, toggle)
            .AutomationName(selected ? T("RemoveTag", tag.Name) : T("AddTag", tag.Name))
            .AutomationId($"ThreadTagToggle_{AutomationToken(tag.Name)}")
            .ToolTip(string.IsNullOrWhiteSpace(tag.Description) ? tag.Name : tag.Description)
            .Resources(resources => _ = selected
                    ? resources
                        .Set("ButtonBackground", tag.Color)
                        .Set("ButtonBackgroundPointerOver", tag.Color)
                        .Set("ButtonBackgroundPressed", tag.Color)
                        .Set("ButtonForeground", foreground)
                        .Set("ButtonForegroundPointerOver", foreground)
                        .Set("ButtonForegroundPressed", foreground)
                        .Set("ButtonBorderBrush", tag.Color)
                    : resources
                        .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                        .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .Set("ButtonForeground", Theme.PrimaryText)
                        .Set("ButtonBorderBrush", tag.Color))
            .WithKey($"tag-toggle-{AutomationToken(tag.Name)}-{(selected ? "selected" : "normal")}");
    }

    private static BorderElement TagCatalogRow(
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
                        Caption(T("ThreadCount", summary.Count))
                            .Foreground(Theme.TertiaryText))
                    with
                    {
                        RowGap = 2
                    }).Flex(grow: 1, basis: 0),
                    Button(T("Edit"), () =>
                        {
                            setPendingDeleteTag("");
                            setTagEditor(new TagEditorDraft(
                                tag.Name,
                                tag.Name,
                                tag.Color,
                                tag.Description));
                        })
                        .AutomationName(T("EditTag"))
                        .AutomationId($"TagEdit_{AutomationToken(tag.Name)}")
                        .SubtleButton()
                        .Flex(shrink: 0),
                    Button(confirmingDelete ? T("Confirm") : T("Delete"), () =>
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
                        .AutomationName(confirmingDelete ? T("ConfirmDeleteTag", tag.Name) : T("DeleteTag", tag.Name))
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

    private static BorderElement TagBadge(TagDefinition tag)
    {
        return Border(
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
    }

    private static BorderElement CompactTagBadge(TagDefinition tag)
    {
        return Border(
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
    }

    private static FlexElement MetadataLine(string label, string value)
    {
        return FlexRow(
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
    }

    private static BorderElement Pill(string text, ThemeRef background)
    {
        return Border(Caption(text).Foreground(Theme.PrimaryText))
            .Padding(7, 2)
            .CornerRadius(4)
            .Background(background);
    }

    private static string DescribeLoad(ThreadShelfSnapshot snapshot)
    {
        var description = T("LoadedThreads", snapshot.Threads.Count, DataSourceLabel(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.LoadWarning))
        {
            return description;
        }

        var separator = snapshot.LoadWarning.IndexOf(": ", StringComparison.Ordinal);
        var detail = separator >= 0 ? snapshot.LoadWarning[(separator + 2)..] : snapshot.LoadWarning;
        return $"{description}; {T("ProviderUnavailableWarning", detail)}";
    }

    private static string T(string key, params object?[] args) => UiText.Get(key, args);

    private static string[] LanguageOptions() =>
        [T("SystemDefault"), T("English"), T("SimplifiedChinese")];

    private static int LanguagePreferenceIndex(string preference) =>
        UiText.NormalizeLanguagePreference(preference) switch
        {
            UiText.EnglishLanguage => 1,
            UiText.SimplifiedChineseLanguage => 2,
            _ => 0
        };

    private static string LanguagePreferenceForIndex(int index) =>
        index switch
        {
            1 => UiText.EnglishLanguage,
            2 => UiText.SimplifiedChineseLanguage,
            _ => UiText.SystemLanguage
        };

    private static string LanguageDisplayName(string preference, System.Globalization.CultureInfo culture) =>
        UiText.NormalizeLanguagePreference(preference) switch
        {
            UiText.EnglishLanguage => UiText.Get("English", culture),
            UiText.SimplifiedChineseLanguage => UiText.Get("SimplifiedChinese", culture),
            _ => UiText.Get("SystemDefault", culture)
        };

    private static string ThreadTitle(CodexThread thread) =>
        string.IsNullOrWhiteSpace(thread.Title) ? T("UntitledThread") : thread.Title.Trim();

    private static string FolderDisplayName(CodexThread thread) =>
        string.IsNullOrWhiteSpace(thread.Metadata.Folder) ? T("Unfiled") : thread.Metadata.Folder.Trim();

    private static string ProjectDisplayName(
        string? workspace,
        IReadOnlyDictionary<string, string> projectAliases) =>
        string.IsNullOrWhiteSpace(ThreadFilters.NormalizeProjectKey(workspace))
            ? T("NoProject")
            : ThreadFilters.ProjectDisplayNameForWorkspace(workspace, projectAliases);

    private static string DataSourceLabel(ThreadShelfSnapshot snapshot) =>
        snapshot.SupportsNativeActions ? T("DataSourceAppServer") : T("DataSourceLocal");

    private static string RenameValidationMessage(ThreadShelfValidationException exception) =>
        exception.Code switch
        {
            "rename_name_empty" => T("RenameNameEmpty"),
            "rename_name_conflict" => T("RenameNameConflict"),
            "project_not_found" => T("ProjectNotFound"),
            "folder_not_found" => T("FolderNotFound"),
            _ => T("RenameFailed", exception.Message)
        };

    private static string ResumeToolTip(CodexInteractiveLauncher launcher, CodexThread thread)
    {
        var availability = launcher.CheckAvailability(thread.Workspace);
        return availability.CanLaunch
            ? T("ResumeToolTip")
            : CodexLaunchProblemText(availability.Problem, availability.Workspace);
    }

    private static string ResumeAutomationName(CodexInteractiveLauncher launcher, CodexThread thread)
    {
        var availability = launcher.CheckAvailability(thread.Workspace);
        return availability.CanLaunch
            ? T("ResumeAutomation", ThreadTitle(thread))
            : T(
                "ResumeUnavailableAutomation",
                ThreadTitle(thread),
                CodexLaunchProblemText(availability.Problem, availability.Workspace));
    }

    private static string CodexLaunchError(Exception exception) =>
        exception is CodexLaunchException launchException
            ? launchException.Problem == CodexLaunchProblem.None
                ? T("TerminalStartFailed", launchException.InnerException?.Message ?? launchException.Message)
                : CodexLaunchProblemText(launchException.Problem, launchException.Detail ?? "")
            : exception.Message;

    private static string CodexLaunchProblemText(CodexLaunchProblem problem, string workspace) =>
        problem switch
        {
            CodexLaunchProblem.CliNotFound => T("CodexCliNotFound"),
            CodexLaunchProblem.WorkspaceMissing => T("WorkspaceMissingLaunch"),
            CodexLaunchProblem.WorkspaceNotFound => T("WorkspaceNotFoundLaunch", workspace),
            CodexLaunchProblem.ThreadIdMissing => T("ThreadIdMissingLaunch"),
            _ => T("CodexLaunchUnavailable")
        };

    private static bool SameMetadata(ThreadMetadata left, ThreadMetadata right)
    {
        return string.Equals(left.Folder, right.Folder, StringComparison.Ordinal)
        && string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
        && left.Favorite == right.Favorite
        && left.Tags.SequenceEqual(right.Tags, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameTagEditorIdentity(TagEditorDraft current, TagEditorDraft rendered)
    {
        return string.Equals(current.EditingName, rendered.EditingName, StringComparison.Ordinal)
        && string.Equals(current.Name, rendered.Name, StringComparison.Ordinal);
    }

    private static string ForegroundFor(string color)
    {
        var normalized = ThreadShelfRepository.NormalizeTagColor(color);
        var red = Convert.ToInt32(normalized.Substring(1, 2), 16);
        var green = Convert.ToInt32(normalized.Substring(3, 2), 16);
        var blue = Convert.ToInt32(normalized.Substring(5, 2), 16);
        var luminance = ((0.299 * red) + (0.587 * green) + (0.114 * blue)) / 255;
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

    private static string TagColorFromWinUIColor(global::Windows.UI.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string AutomationToken(string value)
    {
        var chars = (value ?? "")
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var token = new string(chars).Trim('_');
        return token.Length == 0 ? "Empty" : token;
    }

    private static string EmptyText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
