ThreadShelf is a local conversation organizer for OpenAI Codex threads.

It adds folders, tags, notes, and search-friendly metadata on top of Codex's
existing thread history. ThreadShelf does not modify Codex's internal storage;
it keeps its own sidecar index.

## Current shape

- Uses the Codex CLI `codex app-server` command when available to read Codex's
  native thread index, including thread titles, previews, workspaces, archive
  state, and session file paths.
- Falls back to read-only local import from `~/.codex/session_index.jsonl`,
  `sessions`, and `archived_sessions` if the CLI app-server cannot be started.
- Stores ThreadShelf metadata in
  `~/.codex/threadshelf/threadshelf.json`.
- Supports folder filters, drag-to-folder moves, favorites, global colored tags
  with descriptions, tag/note search, native archive/unarchive and rename
  through the CLI app-server, and `codex://threads/{id}` deep links back into
  the Codex app.
- Does not write folders, tags, notes, or favorites into Codex-owned state.

## Run

```powershell
dotnet run --project src
```

## Codex CLI dependency

Codex app-server is the right public protocol for this integration, but the
documented command lives under the Codex CLI surface: `codex app-server`.
Installing the Codex desktop app does not necessarily mean a shell-accessible
CLI command is present or usable.

ThreadShelf therefore treats app-server as an optional native provider:

- If a usable Codex CLI is found, ThreadShelf enables native list,
  archive/unarchive, and rename operations.
- If the CLI app-server is unavailable, ThreadShelf falls back to local JSONL
  import and only writes its own sidecar metadata.

On Windows, the CLI installer commonly places the visible command at
`%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`. You can override the
executable used by ThreadShelf with `THREADSHELF_CODEX_CLI`.
