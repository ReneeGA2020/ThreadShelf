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
    private sealed record TagManagerState(
        IReadOnlyList<CodexThread> Threads,
        IReadOnlyList<TagDefinition> Tags,
        TagEditorDraft TagEditor,
        string PendingDeleteTag,
        string Status);

    private sealed record TagManagerActions(
        Action<TagEditorDraft> SetTagEditor,
        Action<TagEditorDraft, global::Windows.UI.Color> SetTagEditorColor,
        Action SaveTagDefinition,
        Action<string> SetPendingDeleteTag,
        Action<string> DeleteTagDefinition,
        Action Close);

    private sealed record TagManagerProps(
        TagManagerState State,
        TagManagerActions Actions);

    private static BorderElement RenderTagManagerPage(TagManagerProps props)
    {
        var (threads, tags, tagEditor, pendingDeleteTag, status) = props.State;
        var (
            setTagEditor,
            setTagEditorColor,
            saveTagDefinition,
            setPendingDeleteTag,
            deleteTagDefinition,
            close) = props.Actions;
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

}
