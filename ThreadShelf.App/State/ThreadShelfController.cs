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

internal sealed partial class ThreadShelfController : Component
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
        var threadService = UseMemo(() => new ThreadShelfService(new ThreadShelfRepository()));
        var interactiveLauncher = UseMemo(() => new CodexInteractiveLauncher());
        var preferenceStore = UseMemo(() => new AppPreferenceStore(threadService.CodexHome));
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
        var (loadError, setLoadError) = UseState("");
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
                var loaded = threadService.Load(forceRefresh: true);
                setSnapshot(loaded);
                setLoadError("");
                setStatus(DescribeLoad(loaded));
            }
            catch (Exception ex)
            {
                var message = DescribeLoadFailure(ex);
                setLoadError(message);
                if (snapshot is not null)
                {
                    setStatus(message);
                }
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

                var result = threadService.SaveThreadMetadata(thread.Id, metadata);
                setSnapshot(result.Snapshot);
                if (thread.Id.Equals(selectedThread?.Id, StringComparison.OrdinalIgnoreCase))
                {
                    setDraft(EditDraft.From(result.Data.Metadata));
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

            var metadata = selectedThread.Metadata with
            {
                Folder = draftToSave.Folder,
                Notes = draftToSave.Notes,
                Favorite = draftToSave.Favorite
            };
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

            try
            {
                var result = threadService.MoveThread(thread.Id, folder);
                setSnapshot(result.Snapshot);
                if (thread.Id.Equals(selectedThread?.Id, StringComparison.OrdinalIgnoreCase))
                {
                    setDraft(EditDraft.From(result.Data.Metadata));
                }

                var destination = result.Data.Metadata.Folder.Length == 0
                    ? T("Unfiled")
                    : result.Data.Metadata.Folder;
                setStatus(T("MovedThread", ThreadTitle(thread), destination));
            }
            catch (Exception ex)
            {
                setStatus(T("SaveFailed", ex.Message));
            }
        }

        void ToggleThreadTag(CodexThread thread, TagDefinition tag)
        {
            var hasTag = thread.Metadata.Tags.Any(name =>
                name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase));
            try
            {
                var result = threadService.SetThreadTag(thread.Id, tag.Name, !hasTag);
                setSnapshot(result.Snapshot);
                if (thread.Id.Equals(selectedThread?.Id, StringComparison.OrdinalIgnoreCase))
                {
                    setDraft(EditDraft.From(result.Data.Metadata));
                }

                setStatus(hasTag
                    ? T("RemovedTag", tag.Name.Trim(), ThreadTitle(thread))
                    : T("AddedTag", tag.Name.Trim(), ThreadTitle(thread)));
            }
            catch (Exception ex)
            {
                setStatus(T("SaveFailed", ex.Message));
            }
        }

        void SaveTagDefinition()
        {
            try
            {
                var result = threadService.SaveTagDefinition(
                    tagEditor.EditingName,
                    new TagDefinition
                    {
                        Name = tagEditor.Name,
                        Color = tagEditor.Color,
                        Description = tagEditor.Description
                    });
                var definition = result.Data;
                if (tagEditor.EditingName.Equals(selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    setSelectedTag(definition.Name);
                }

                setPendingDeleteTag("");
                setTagEditor(TagEditorDraft.Empty);
                setSnapshot(result.Snapshot);
                setStatus(tagEditor.EditingName.Length == 0
                    ? T("AddedTagDefinition", definition.Name)
                    : T("SavedTag", definition.Name));
            }
            catch (ThreadShelfValidationException ex) when (ex.Code == "tag_name_empty")
            {
                setStatus(T("TagSaveNameEmpty"));
            }
            catch (ThreadShelfValidationException ex) when (ex.Code == "tag_color_invalid")
            {
                setStatus(T("TagColorInvalid"));
            }
            catch (Exception ex)
            {
                setStatus(T("TagSaveFailed", ex.Message));
            }
        }

        void DeleteTagDefinition(string name)
        {
            try
            {
                var result = threadService.DeleteTag(name);
                var tagName = result.Data;
                if (tagName.Equals(selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    setSelectedTag("");
                }

                if (tagName.Equals(tagEditor.EditingName, StringComparison.OrdinalIgnoreCase))
                {
                    setTagEditor(TagEditorDraft.Empty);
                }

                setPendingDeleteTag("");
                setSnapshot(result.Snapshot);
                setStatus(T("DeletedTag", tagName));
            }
            catch (ThreadShelfValidationException ex) when (ex.Code == "tag_name_empty")
            {
                setStatus(T("TagDeleteNameEmpty"));
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
                var result = await Task.Run(() => threadService.SetArchived(thread.Id, archived));
                setSnapshot(result.Snapshot);
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
            try
            {
                var result = threadService.RenameThread(thread.Id, name);
                setSnapshot(result.Snapshot);
                setStatus(T("RenamedThread", result.Data.Title));
            }
            catch (ThreadShelfValidationException ex) when (ex.Code == "thread_title_empty")
            {
                setStatus(T("RenameTitleEmpty"));
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
                    var result = threadService.RenameProjectAlias(rename.Key, rename.Value);
                    setSnapshot(result.Snapshot);
                    setStatus(T("ProjectAliasRenamed", rename.CurrentName, rename.Value.Trim()));
                }
                else
                {
                    var result = threadService.RenameFolder(
                        selectedProject,
                        rename.Key,
                        rename.Value);
                    if (selectedFilter.Equals(rename.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        setSelectedFilter(rename.Value.Trim());
                    }

                    setSnapshot(result.Snapshot);
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
                ? loadError.Length == 0
                    ? T("LoadingCodexThreads")
                    : T("LoadFailedTitle")
                : T("IndexedThreads", threads.Count, DataSourceLabel(snapshot)))
            .Flex(shrink: 0);

        Element body;
        if (snapshot is null)
        {
            body = RenderLoading(loadError, Reload);
        }
        else
        {
            var sidebar = RenderSidebar(new SidebarProps(
                new SidebarData(
                    threads,
                    tags,
                    snapshot.ProjectAliases,
                    snapshot.SidecarPath),
                new SidebarSelection(
                    activePage,
                    selectedProject,
                    selectedFilter,
                    selectedTag,
                    languagePreference),
                new SidebarNavigationActions(
                    ShowThreads,
                    ShowTagManager,
                    SelectProject,
                    SelectFilter,
                    SelectTagFilter,
                    SelectLanguage),
                new SidebarMutationActions(
                    MoveThreadToFolder,
                    BeginProjectRename,
                    BeginFolderRename,
                    StartNewTask),
                interactiveLauncher));

            body = activePage.Equals(TagsPage, StringComparison.OrdinalIgnoreCase)
                ? FlexRow(
                    sidebar,
                    RenderTagManagerPage(new TagManagerProps(
                        new TagManagerState(
                            threads,
                            tags,
                            tagEditor,
                            pendingDeleteTag,
                            status),
                        new TagManagerActions(
                            setTagEditor,
                            setTagEditorColor,
                            SaveTagDefinition,
                            setPendingDeleteTag,
                            DeleteTagDefinition,
                            ShowThreads))))
                    with
                {
                    ColumnGap = 16,
                    AlignItems = FlexAlign.Stretch
                }
                : FlexRow(
                    sidebar,
                    RenderThreadList(new ThreadListProps(
                        new ThreadListData(filtered, tags, snapshot.ProjectAliases),
                        new ThreadListState(
                            selectedThread?.Id ?? "",
                            query,
                            snapshot.SupportsNativeActions,
                            pendingArchiveId,
                            status),
                        new ThreadListActions(
                            setQuery,
                            SelectThread,
                            ToggleArchived,
                            ResumeThread,
                            Reload),
                        interactiveLauncher)),
                    RenderDetails(new ThreadDetailsProps(
                        new ThreadDetailsState(
                            selectedThread,
                            activeDraft,
                            titleDraft,
                            tags,
                            snapshot.SupportsNativeActions,
                            pendingArchiveId),
                        new ThreadEditingActions(
                            setDraft,
                            setTitleDraft,
                            SaveDraft,
                            ToggleThreadTag),
                        new ThreadActionHandlers(
                            ShowTagManager,
                            ToggleArchived,
                            RenameThread,
                            ResumeThread,
                            setStatus),
                        interactiveLauncher)))
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
                RenderRenameDialog(new RenameDialogProps(
                    renameDraft,
                    setRenameDraft,
                    renameValueBuffer,
                    ApplyNavigationRename)))
            .Backdrop(BackdropKind.Mica);
    }

}
