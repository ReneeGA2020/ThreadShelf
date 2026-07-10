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
    private sealed record ThreadListData(
        IReadOnlyList<CodexThread> Threads,
        IReadOnlyList<TagDefinition> Tags,
        IReadOnlyDictionary<string, string> ProjectAliases);

    private sealed record ThreadListState(
        string SelectedId,
        string Query,
        bool SupportsNativeActions,
        string PendingArchiveId,
        string Status);

    private sealed record ThreadListActions(
        Action<string> SetQuery,
        Action<CodexThread> SelectThread,
        Action<CodexThread, bool> SetArchived,
        Action<CodexThread> ResumeThread,
        Action Reload);

    private sealed record ThreadListProps(
        ThreadListData Data,
        ThreadListState State,
        ThreadListActions Actions,
        CodexInteractiveLauncher InteractiveLauncher);

    private static BorderElement RenderThreadList(ThreadListProps props)
    {
        var (threads, tags, projectAliases) = props.Data;
        var (
            selectedId,
            query,
            supportsNativeActions,
            pendingArchiveId,
            status) = props.State;
        var (setQuery, selectThread, setArchived, resumeThread, reload) = props.Actions;
        var interactiveLauncher = props.InteractiveLauncher;
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

}
