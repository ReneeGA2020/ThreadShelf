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
    private sealed record ThreadDetailsState(
        CodexThread? Thread,
        EditDraft? Draft,
        string TitleDraft,
        IReadOnlyList<TagDefinition> Tags,
        bool SupportsNativeActions,
        string PendingArchiveId);

    private sealed record ThreadEditingActions(
        Action<EditDraft?> SetDraft,
        Action<string> SetTitleDraft,
        Action<EditDraft> SaveMetadata,
        Action<CodexThread, TagDefinition> ToggleThreadTag);

    private sealed record ThreadActionHandlers(
        Action OpenTagManager,
        Action<CodexThread, bool> SetArchived,
        Action<CodexThread, string> RenameThread,
        Action<CodexThread> ResumeThread,
        Action<string> SetStatus);

    private sealed record ThreadDetailsProps(
        ThreadDetailsState State,
        ThreadEditingActions Editing,
        ThreadActionHandlers Actions,
        CodexInteractiveLauncher InteractiveLauncher);

    private static BorderElement RenderDetails(ThreadDetailsProps props)
    {
        var (
            thread,
            draft,
            titleDraft,
            tags,
            supportsNativeActions,
            pendingArchiveId) = props.State;
        var (setDraft, setTitleDraft, saveMetadata, toggleThreadTag) = props.Editing;
        var (openTagManager, setArchived, renameThread, resumeThread, setStatus) = props.Actions;
        var interactiveLauncher = props.InteractiveLauncher;
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

        void RevealJsonl()
        {
            try
            {
                ThreadShelfSystemActions.RevealFile(thread.SourcePath);
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
                    WorkspaceMetadataLine(thread.Workspace, setStatus),
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
                        .IsEnabled(interactiveLauncher.CheckResumeAvailability(thread.Workspace, thread.Id).CanLaunch)
                        .HAlign(HorizontalAlignment.Stretch)
                        .Flex(shrink: 0),
                    Button(T("Reveal"), RevealJsonl)
                        .AutomationName(T("Reveal"))
                        .AutomationId("RevealFileButton")
                        .SubtleButton()
                        .HAlign(HorizontalAlignment.Stretch)
                        .Flex(shrink: 0))
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
        var availability = interactiveLauncher.CheckResumeAvailability(thread.Workspace, thread.Id);
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

}
