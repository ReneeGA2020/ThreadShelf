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

internal sealed partial class ThreadShelfController
{
    private sealed record SidebarData(
        IReadOnlyList<CodexThread> Threads,
        IReadOnlyList<TagDefinition> Tags,
        IReadOnlyDictionary<string, string> ProjectAliases,
        string SidecarPath);

    private sealed record SidebarSelection(
        string ActivePage,
        string SelectedProject,
        string SelectedFilter,
        string SelectedTag,
        string LanguagePreference);

    private sealed record SidebarNavigationActions(
        Action ShowThreads,
        Action ShowTagManager,
        Action<string> SelectProject,
        Action<string> SelectFilter,
        Action<string> SelectTag,
        Action<int> SelectLanguage);

    private sealed record SidebarMutationActions(
        Action<ThreadDragPayload, string> MoveThreadToFolder,
        Action<ProjectSummary> RenameProject,
        Action<FolderSummary> RenameFolder,
        Action<ProjectSummary> StartNewTask);

    private sealed record SidebarProps(
        SidebarData Data,
        SidebarSelection Selection,
        SidebarNavigationActions Navigation,
        SidebarMutationActions Mutations,
        CodexInteractiveLauncher InteractiveLauncher);

    private static BorderElement RenderSidebar(SidebarProps props)
    {
        var (threads, tags, projectAliases, sidecarPath) = props.Data;
        var (
            activePage,
            selectedProject,
            selectedFilter,
            selectedTag,
            languagePreference) = props.Selection;
        var (
            showThreads,
            showTagManager,
            selectProject,
            selectFilter,
            selectTag,
            selectLanguage) = props.Navigation;
        var (
            moveThreadToFolder,
            renameProject,
            renameFolder,
            startNewTask) = props.Mutations;
        var interactiveLauncher = props.InteractiveLauncher;
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
        var availability = interactiveLauncher.CheckNewTaskAvailability(project.Workspace);
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

}
