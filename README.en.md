# ThreadShelf

English · [简体中文](README.md)

ThreadShelf is a local Windows task organizer for Codex users. It groups existing Codex sessions by project, folder, and tag, then adds search, favorites, notes, archive actions, and rename shortcuts. It is designed for individuals and developers whose task history is growing and who do not want to upload private conversations to another service.

ThreadShelf does not take over or rewrite Codex session files. Folders, tags, notes, favorites, and project display aliases live in a separate sidecar. Archive, unarchive, and thread-title changes are performed only through the public Codex CLI `app-server` protocol.

![ThreadShelf main window using entirely fictional demo data](docs/assets/threadshelf-main.png)

## Highlights

- Group Codex projects by their real workspace and organize tasks into project-scoped ThreadShelf folders.
- Drag tasks to folders; dropping on Unfiled clears the folder immediately.
- Search titles, projects, folders, tags, notes, IDs, models, and archive state.
- Manage global colored tags with descriptions; tag renames migrate task references.
- Edit notes, favorites, and Codex thread titles; archive or unarchive from the task card.
- Assign a ThreadShelf-only project alias or atomically rename a folder within the current project.
- Start a new interactive Codex task from a project or resume an existing session from its card.
- Use Simplified Chinese or English: follow the system by default, or persist a manual choice.
- Use CLI and MCP automation to organize tasks safely from scripts and AI assistants.

## Requirements

- Windows 10 version 1809 (build 17763) or newer.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) when building from source.
- Optional: an installed, executable Codex CLI. The Codex desktop app and Codex CLI are separate installation surfaces; installing only the desktop app does not guarantee that `codex.exe` exists.

The app uses a self-contained Windows App SDK build, so a separate Windows App Runtime installation is not required.

## Install and run

```powershell
git clone https://github.com/ReneeGA2020/ThreadShelf.git
cd ThreadShelf
dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
dotnet run --project ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

You can also run the built executable directly:

```powershell
.\ThreadShelf.App\bin\x64\Debug\net10.0-windows10.0.22621.0\ThreadShelf.App.exe
```

On ARM64 devices, replace `x64` with `ARM64`.

## First launch

1. Start ThreadShelf. It tries `codex app-server` first and automatically switches to read-only local JSONL import when the provider cannot start.
2. Choose System default, English, or Simplified Chinese in the upper-left selector. A manual choice is saved in a separate preference file.
3. Choose a project and folder on the left, a task in the center, and edit its folder, tags, notes, or favorite state on the right.
4. Use the status button in the task card to archive or unarchive. When app-server is unavailable, the button is disabled and explains why.
5. Right-click an ordinary project or folder to rename it. A project rename is a ThreadShelf display alias; it never renames a Codex project or disk directory.

## Everyday workflows

### Organize tasks

- Click a task title to select it, or drag its dedicated `::` handle to a folder.
- Set favorite, folder, tags, and notes in the details pane. Tags, favorites, and drag operations save immediately; folder and notes save on Enter or lost focus.
- Combine search with project, folder, and tag filters. Unarchived and Archived views update immediately after a state change.

### Open, create, and resume Codex tasks

- Open uses a `codex://threads/<id>` deep link to show the task in Codex.
- Right-click an ordinary project and choose New task to open a visible terminal in its real workspace, equivalent to `codex -C <workspace>`.
- Resume opens a visible terminal with structured arguments equivalent to `codex resume -C <workspace> <session-id>`.
- A project display alias is never used as a file-system path.
- Windows Terminal is preferred when available; otherwise ThreadShelf starts Codex directly in a visible console. Missing CLI or invalid workspace entries are disabled with a reason.
- The optional hover `+` was evaluated as a pointer experiment. Keeping the row hit target fixed and the context menu as the baseline was more stable, so the hover button is not currently shown.

### Manage tags

Open Tag manager to create, edit, rename, or delete global tags. Deleting a tag also removes task references, so confirm the target tag first.

## Data, privacy, and backup

Paths are under `%USERPROFILE%\.codex` by default, or under the directory selected by `CODEX_HOME`.

| Data | Path | ThreadShelf behavior |
| --- | --- | --- |
| Codex session index | `session_index.jsonl` | Read-only in fallback mode |
| Codex sessions | `sessions/`, `archived_sessions/` | Read-only in fallback mode; never written |
| ThreadShelf metadata | `threadshelf/threadshelf.json` | Stores folders, tags, notes, favorites, and project aliases |
| ThreadShelf preferences | `threadshelf/preferences.json` | Stores the manual language choice |

Back up the `threadshelf` directory to preserve ThreadShelf-owned data. The sidecar may contain private notes and thread IDs, so protect it like Codex history. ThreadShelf does not send this data over the network.

## Codex provider and fallback

| Capability | app-server available | Local JSONL fallback |
| --- | --- | --- |
| Browse, search, and filter tasks | Yes | Yes |
| Folders, tags, notes, favorites, project aliases | Yes (ThreadShelf sidecar write) | Yes (ThreadShelf sidecar write) |
| Reveal a session file | Yes | Yes, when the file exists |
| Archive, unarchive, rename a thread title | Yes (native Codex action) | No; controls are disabled |
| Start/resume an interactive CLI session | Available when the CLI executable and workspace are valid | Independent of app-server capability |

ThreadShelf resolves the CLI in this order: a valid `THREADSHELF_CODEX_CLI`, the common Windows installation location, then `codex` on `PATH`. The override must point to an existing executable.

```powershell
$env:THREADSHELF_CODEX_CLI = "$env:LOCALAPPDATA\Programs\OpenAI\Codex\bin\codex.exe"
dotnet run --project ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

## Demo data and screenshot reproduction

This script uses only the repository's fake app-server and `artifacts/demo-codex-home`. It does not read or modify real Codex data:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-ThreadShelfDemo.ps1 -Language en-US
```

The script builds the app, creates fictional projects, tasks, tags, and notes, and launches ThreadShelf. The README screenshot is generated from this fixture and can be updated safely.

## Advanced: CLI and MCP

```powershell
dotnet run --project ThreadShelf.Cli -- threads list --json
dotnet run --project ThreadShelf.Cli -- threads search "follow up" --json
dotnet run --project ThreadShelf.Cli -- threads batch-update --file organization.json --yes --json
dotnet run --project ThreadShelf.Mcp
```

See the [AI/CLI/MCP guide](docs/ai-interface.md) for tools, parameters, permissions, success/error envelopes, and safety boundaries. Normal automation should use MCP or CLI instead of editing the sidecar directly, and it must never edit Codex JSONL.

## Troubleshooting

### Codex CLI cannot be found

Confirm that `codex --version` works, or point `THREADSHELF_CODEX_CLI` to a real `codex.exe`. If only the Microsoft Store/desktop app is installed, local fallback can still browse tasks, but native actions are disabled.

### No tasks appear

Check that `CODEX_HOME` points to the intended directory and that `session_index.jsonl`, `sessions`, or `archived_sessions` exists. Clear search, switch to All projects / All threads, and click Refresh.

### Archive or rename is unavailable

These are native app-server writes. Read the status message at the bottom of the window and verify that the CLI can start `codex app-server`. ThreadShelf never fakes a local success state.

### A project rename does not appear in Codex desktop

The current public app-server protocol has no project-rename method, so ThreadShelf uses a display alias. It does not change a Codex desktop project, CLI workspace, or disk directory.

### Where is my data?

The sidecar path is shown at the lower left of the window. The language preference is in `preferences.json` beside it.

## Uninstall

1. Close ThreadShelf and remove the cloned or published application directory.
2. To remove ThreadShelf metadata too, back it up first, then delete `%USERPROFILE%\.codex\threadshelf` (or `$env:CODEX_HOME\threadshelf`).
3. Do not delete `sessions`, `archived_sessions`, or `session_index.jsonl`; they belong to Codex.

## Development checks

```powershell
dotnet test tests\ThreadShelf.Tests\ThreadShelf.Tests.csproj
dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

Licensed under the [MIT License](LICENSE).
