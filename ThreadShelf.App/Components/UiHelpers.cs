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

    private static FlexElement WorkspaceMetadataLine(string? workspace, Action<string> setStatus)
    {
        var availability = ThreadShelfSystemActions.CheckFolderAvailability(workspace);

        void OpenWorkspace()
        {
            try
            {
                var plan = ThreadShelfSystemActions.OpenFolder(workspace);
                setStatus(T("WorkspaceOpened", plan.Arguments[0]));
            }
            catch (FolderOpenException ex)
            {
                setStatus(T("WorkspaceOpenFailed", FolderOpenProblemText(ex.Problem, ex.Path)));
            }
            catch (Exception ex)
            {
                setStatus(T("WorkspaceOpenFailed", ex.Message));
            }
        }

        var problemText = FolderOpenProblemText(availability.Problem, availability.Path);
        var line = FlexRow(
            Caption(T("Workspace")).Foreground(Theme.SecondaryText).Width(76).Flex(shrink: 0),
            HyperlinkButton(EmptyText(workspace ?? ""), onClick: OpenWorkspace)
                .AutomationId("WorkspaceFolderLink")
                .AutomationName(availability.CanOpen
                    ? T("WorkspaceOpenAutomation", availability.Path)
                    : T("WorkspaceOpenUnavailableAutomation", problemText))
                .ToolTip(availability.CanOpen ? T("WorkspaceOpenToolTip") : problemText)
                .IsEnabled(availability.CanOpen)
                .TextLink()
                .Set(button =>
                {
                    button.HorizontalContentAlignment = HorizontalAlignment.Left;
                    button.Padding = new Thickness(0);
                    button.MinHeight = 0;
                })
                .Flex(grow: 1, basis: 0))
        with
        {
            ColumnGap = 8,
            AlignItems = FlexAlign.Center
        };

        return availability.CanOpen
            ? line
            : FlexColumn(
                    line,
                    Caption(problemText)
                        .Foreground(Theme.SystemAttention)
                        .TextWrapping()
                        .Margin(84, 0, 0, 0))
                with
                {
                    RowGap = 2
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
        var availability = launcher.CheckResumeAvailability(thread.Workspace, thread.Id);
        return availability.CanLaunch
            ? T("ResumeToolTip")
            : CodexLaunchProblemText(availability.Problem, availability.Workspace);
    }

    private static string ResumeAutomationName(CodexInteractiveLauncher launcher, CodexThread thread)
    {
        var availability = launcher.CheckResumeAvailability(thread.Workspace, thread.Id);
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
                ? T("CodexStartFailed", launchException.InnerException?.Message ?? launchException.Message)
                : CodexLaunchProblemText(launchException.Problem, launchException.Detail ?? "")
            : exception.Message;

    private static string CodexLaunchProblemText(CodexLaunchProblem problem, string workspace) =>
        problem switch
        {
            CodexLaunchProblem.CodexUnavailable => T("CodexProviderNotFound"),
            CodexLaunchProblem.WorkspaceMissing => T("WorkspaceMissingLaunch"),
            CodexLaunchProblem.WorkspaceNotFound => T("WorkspaceNotFoundLaunch", workspace),
            CodexLaunchProblem.ThreadIdMissing => T("ThreadIdMissingLaunch"),
            _ => T("CodexLaunchUnavailable")
        };

    private static string FolderOpenProblemText(FolderOpenProblem problem, string path) =>
        problem switch
        {
            FolderOpenProblem.PathMissing => T("WorkspacePathMissing"),
            FolderOpenProblem.DirectoryNotFound => T("WorkspaceDirectoryNotFound", path),
            FolderOpenProblem.StartFailed => T("WorkspaceExplorerStartFailed"),
            _ => T("WorkspaceOpenUnavailable")
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
