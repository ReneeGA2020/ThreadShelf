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
    private sealed record RenameDialogProps(
        RenameDraft? Draft,
        Action<RenameDraft?> SetDraft,
        RenameValueBuffer ValueBuffer,
        Action<RenameDraft> Submit);

    private static ContentDialogElement RenderRenameDialog(RenameDialogProps props)
    {
        var (draft, setDraft, valueBuffer, submit) = props;
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

}
