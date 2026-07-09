---
name: thread-shelf-reactor
description: Project-specific workflow for building, modifying, and testing the ThreadShelf WinUI Reactor app. Use when working in this repository on Codex thread management, Codex CLI app-server integration, local JSONL fallback, sidecar metadata, folder/tag UX, drag-and-drop behavior, or winapp UI E2E tests.
---

# ThreadShelf Reactor

## Workflow

Start by reading the relevant files instead of guessing:

- `ThreadShelf.App/App.cs` for WinUI Reactor UI, selection, drag/drop, tag editor, filters, and details behavior.
- `ThreadShelf.Core/ThreadShelfData.cs` for data loading, local fallback, sidecar schema, tag normalization, filtering, and metadata persistence.
- `ThreadShelf.Core/CodexAppServerClient.cs` for Codex CLI `app-server` JSON-RPC integration.
- `README.md` for user-facing capability and dependency wording.
- If Reactor behavior is unclear and the source is available locally, inspect `E:\reactor-perf-lab\microsoft-ui-reactor`, especially `TESTING.md`, `tests/Reactor.AppTests`, and wrapper/event implementation files.

Keep changes scoped to the app's existing pattern: single-file Reactor UI in `ThreadShelf.App/App.cs`, repository/data logic in `ThreadShelf.Core/ThreadShelfData.cs`, and Codex native protocol logic in `ThreadShelf.Core/CodexAppServerClient.cs`.

## Codex Integration Rules

Treat `codex app-server` as an optional native provider, not as guaranteed desktop-app functionality.

- Use `THREADSHELF_CODEX_CLI` only when it points to an existing executable. A nonexistent override is ignored by the current resolver.
- On Windows, the CLI installer commonly exposes `codex.exe` under `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`; the Store GUI app and CLI availability are separate concerns.
- If app-server fails, preserve local JSONL fallback behavior. The fallback reads `CODEX_HOME`, `session_index.jsonl`, `sessions`, and `archived_sessions`, then writes only ThreadShelf's sidecar metadata.
- Do not write folders, tags, notes, or favorites into Codex-owned files. Persist them in `~/.codex/threadshelf/threadshelf.json`.
- Native actions such as rename and archive/unarchive must be gated on app-server support.

For tests that need local fallback even when a real Codex CLI is installed, set:

```powershell
$env:THREADSHELF_CODEX_CLI = "C:\Windows\System32\where.exe"
```

`where.exe app-server` exits unsuccessfully, so the app exercises fallback without touching real Codex data.

## UI Interaction Rules

Be careful with drag sources in WinUI Reactor lists.

- Do not bind `OnDragStart` to the whole row or to the main title text if the row must remain selectable.
- Use a small dedicated drag handle, for example `ThreadDragHandle_*`, and leave the rest of the row as normal click/selection surface.
- Add explicit `OnPointerPressed` and `OnTapped` selection handlers to row/title content when custom row templates make `ListView` selection unreliable.
- Expect `ThreadRow_*` automation IDs on nested row borders to be hard to find through UIA; title and handle text blocks are more reliable E2E anchors.
- Keep folder buttons as drop targets. Dropping on `Unfiled` should clear the folder immediately.

For GitHub-style tags:

- Model global tag definitions with `name`, `color`, and `description`.
- Normalize tag names and colors before saving.
- When renaming a tag definition, migrate existing thread metadata references.
- Prefer selectable tag chips/buttons over comma-separated free text.
- Let search/filter use the global tag model, not ad hoc string parsing.

## Save Behavior

Persist edits on clear user completion events.

- Folder and notes fields should save on lost focus and Enter where appropriate.
- Tag toggles, tag definition edits, favorite changes, and drag-to-folder moves should save immediately.
- Keep the UI snapshot in sync after save so selected-row highlight, details fields, and counts update without requiring refresh.

## Build And E2E

Build x64 before UI E2E:

```powershell
dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

Use `winapp ui` for automation when available. Prefer a temporary `CODEX_HOME` with fake sessions and sidecar data; never point E2E tests at the user's real Codex home unless explicitly requested.

For WinUI pointer behavior:

- UIA `invoke` can validate the selection/data-binding chain by invoking the `ListItem` ancestor.
- Real click and drag behavior needs SendInput-style pointer input. Plain UIA invoke is not enough for drag/drop.
- Use virtual-screen metrics (`SM_XVIRTUALSCREEN`, `SM_YVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, `SM_CYVIRTUALSCREEN`) when converting screen coordinates for `SendInput`; primary-screen-only normalization can miss on multi-monitor or scaled displays.
- For drag/drop, move in small steps after mouse down, pause briefly, hover over the target, then release.

Useful validation targets:

- Click a thread title and assert `FolderTextBox` changes to that thread's folder.
- Drag `ThreadDragHandle_<id>` to `Filter_Unfiled` and assert the sidecar folder value becomes empty.
- Add/edit/rename a global tag and assert thread tag references migrate.

## Verification Checklist

Before finishing a ThreadShelf change:

- Run `dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64`.
- If the change touches UI selection, drag/drop, tags, or save timing, run a `winapp ui` E2E check with temporary `CODEX_HOME`.
- Confirm no `ThreadShelf` test process remains.
- Confirm `git status --short` does not include ignored build artifacts.
