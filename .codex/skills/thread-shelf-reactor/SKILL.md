---
name: thread-shelf-reactor
description: Develop and test the ThreadShelf WinUI Reactor repository, or use its MCP/CLI surface to organize Codex threads. Use for ThreadShelf UI, app-server integration, local JSONL fallback, sidecar metadata, localization, folders/tags, drag-and-drop, CLI/MCP tools, UI E2E, and requests to find, move, tag, annotate, favorite, batch-organize, archive, unarchive, or rename Codex threads through ThreadShelf.
---

# ThreadShelf Reactor

## Choose the workflow

- For repository changes, follow **Develop ThreadShelf**.
- For organizing existing tasks, follow **Use ThreadShelf MCP/CLI**.
- For MCP schemas, configuration, complete examples, and envelopes, read [`docs/ai-interface.md`](../../../docs/ai-interface.md).

## Develop ThreadShelf

Read only the files relevant to the change:

- `ThreadShelf.App/Program.cs` / `App.cs`: process entry and top-level Reactor composition.
- `ThreadShelf.App/State/ThreadShelfController.cs`: page state, Effects, selection/edit drafts, refresh, use-case calls, and async operation coordination.
- `ThreadShelf.App/Components/*.cs`: sidebar, thread list/rows, details, tag manager, rename dialog, AutomationIds, and UI helpers.
- `ThreadShelf.Core/Models/ThreadModels.cs` / `Queries/ThreadFilters.cs`: domain records, snapshot helpers, and pure filtering/project summaries.
- `ThreadShelf.Core/ThreadShelfRepository.cs`: provider fallback and snapshot assembly façade.
- `ThreadShelf.Core/Persistence/*.cs`: sidecar schema, normalization, atomic writes, project aliases, folders, and tag-reference migrations.
- `ThreadShelf.Core/Sources/*.cs`: Codex app-server and local JSONL thread providers.
- `ThreadShelf.Core/Native/*.cs`: native Codex mutations plus Explorer/URI system actions.
- `ThreadShelf.Core/ThreadShelfService.cs`: shared UI/CLI/MCP application use cases and validation semantics.
- `ThreadShelf.Core/CodexAppServerClient.cs`: `codex app-server` JSON-RPC and CLI resolution.
- `ThreadShelf.Core/CodexInteractiveLauncher.cs`: shared CLI discovery plus structured visible-terminal plans for new/resumed tasks.
- `ThreadShelf.Core/ThreadShelfCommands.cs`: transport contracts, command adapter, envelopes, and provider error mapping.
- `ThreadShelf.Cli/Program.cs` / `CommandDispatcher.cs` / `ParsedArgs.cs`: CLI bootstrap, dispatch, usage, and argument parsing.
- `ThreadShelf.Mcp/Program.cs` / `ThreadShelfMcpServer.cs`: stdio bootstrap and JSON-RPC server loop.
- `ThreadShelf.Mcp/ToolCatalog.cs` / `ToolHandlers.cs`: authoritative descriptor registry, input schemas, and bound handlers.
- `README.md` / `README.en.md`: user-facing capability and dependency wording.

Keep page state in the controller, rendering in component feature files, shared use-case semantics in `ThreadShelfService`, sidecar writes in `Persistence`, provider reads in `Sources`, native/system actions in `Native`, and public automation envelopes in the command adapter. Add MCP tools through the descriptor registry so catalog schemas and handlers remain bound by construction.

If Reactor behavior is unclear, inspect the local Reactor source at `E:\reactor-perf-lab\microsoft-ui-reactor`, especially `TESTING.md`, wrapper/event code, and `tests/Reactor.AppTests`.

## Use ThreadShelf MCP/CLI

Prefer MCP for AI hosts and CLI for terminal scripts. Do not edit Codex JSONL or the ThreadShelf sidecar directly.

1. Confirm the ThreadShelf tools are registered. Use MCP `tools/list`; expect 15 `threadshelf_*` tools. If unavailable, build/start `ThreadShelf.Mcp` and follow the configuration section in the AI guide.
2. Read before writing. Use `threadshelf_search_threads` or `threadshelf_list_threads`, then `threadshelf_get_thread` with the exact ID.
3. Distinguish permissions. Reads need no confirmation; sidecar and native mutations require `confirmed: true` in MCP or `--yes` in CLI. Ask before destructive tag deletion or native Codex writes unless the host already obtained confirmation.
4. Write through one tool. Prefer `threadshelf_batch_update_threads` for related multi-thread folder/tag changes because it validates the full request before one sidecar write.
5. Read back with `threadshelf_get_thread` or list/search and verify the requested fields.

Use this selection table:

| Intent | Tool |
| --- | --- |
| List/filter threads | `threadshelf_list_threads` |
| Find by text | `threadshelf_search_threads` |
| Confirm exact state | `threadshelf_get_thread` |
| Patch folder/notes/favorite | `threadshelf_update_thread_metadata` |
| Set or clear folder | `threadshelf_move_thread` |
| Add/remove one tag | `threadshelf_add_thread_tag` / `threadshelf_remove_thread_tag` |
| Atomically organize many threads | `threadshelf_batch_update_threads` |
| List/create/edit/delete definitions | `threadshelf_list_tags` / `threadshelf_create_tag` / `threadshelf_update_tag` / `threadshelf_delete_tag` |
| Native archive state | `threadshelf_archive_thread` / `threadshelf_unarchive_thread` |
| Native thread title | `threadshelf_rename_thread` |

For “find → move → add tag → verify,” search, get the exact ID, list/create the global tag if needed, move with confirmation, add the tag with confirmation, then get the thread again. Never invent an ID or create a duplicate tag blindly.

Interpret responses before proceeding:

- `source.provider = app-server`: native archive/unarchive/thread-title rename may be attempted.
- `source.provider = local-files`: reads and sidecar writes work; native operations must return `native_action_unsupported`.
- `confirmation_required`: obtain confirmation; do not silently retry with `confirmed: true`.
- `app_server_unavailable`: report the native failure; do not fake local success.
- `sidecar_*` or `permission_denied`: stop writes and report the affected path.

Run the isolated MCP smoke test after changing tools or docs:

```powershell
powershell -ExecutionPolicy Bypass -File .\.codex\skills\thread-shelf-reactor\scripts\Test-ThreadShelfMcp.ps1
```

## Codex integration rules

Treat `codex app-server` as optional.

- Honor `THREADSHELF_CODEX_CLI` only when it points to an existing executable.
- Keep desktop-app installation and CLI availability separate. The common Windows CLI path is `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`.
- Keep interactive CLI launch capability separate from app-server native actions. Pass `-C`, workspace, and session IDs through `ProcessStartInfo.ArgumentList`; never concatenate a shell command.
- Preserve fallback reads from `CODEX_HOME`, `session_index.jsonl`, `sessions`, and `archived_sessions` when app-server fails.
- Write folders, tags, notes, favorites, and project aliases only to `~/.codex/threadshelf/threadshelf.json`.
- Store UI language preference separately in `~/.codex/threadshelf/preferences.json`.
- Gate archive/unarchive/thread-title rename on app-server support. The current public schema has no project rename; use a clearly labeled ThreadShelf project alias and never move the workspace directory.

Force fallback in tests without touching real Codex data:

```powershell
$env:THREADSHELF_CODEX_CLI = "C:\Windows\System32\where.exe"
```

## UI interaction and save rules

- Keep row/title click selection separate from the small `ThreadDragHandle_*` drag source.
- Preserve stable AutomationIds and localized automation names/tooltips.
- Expect nested `ThreadRow_*` borders to be unreliable UIA anchors; prefer title, button, and drag-handle IDs.
- Keep folder buttons as drop targets; dropping on Unfiled clears the folder.
- Save tag toggles, tag definitions, favorites, archive actions, and drag moves immediately.
- Save folder and notes on Enter/lost focus.
- Migrate tag references and project-scoped folder references atomically on rename.
- Refresh snapshot, counts, filters, selection, and details after a mutation.
- Keep stable filter/workspace keys independent from localized display text.

## Build and E2E

Run unit tests and build x64:

```powershell
dotnet test tests\ThreadShelf.Tests\ThreadShelf.Tests.csproj
dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

For UI selection, drag/drop, tags, context menus, localization, or save timing, run `winapp ui` against a temporary `CODEX_HOME` or the repository demo fixture. Never point E2E at real Codex data unless explicitly requested.

- Use UIA `invoke` for selection/data-binding checks.
- Use real pointer input for click/drag behavior.
- For drag input, normalize against virtual-screen metrics, move in small steps, hover briefly, then release.
- Verify sidecar contents after writes and hash Codex JSONL when testing fallback safety.
- Confirm no `ThreadShelf` test process remains and no ignored build artifacts appear in `git status --short`.

## Git publishing and handoff

- After finishing branch-based work, switch the local checkout back to the repository's default branch once requested commits, pushes, PR creation, and issue updates have succeeded. Stay on the work branch only when the user explicitly asks.
- Require a clean worktree before switching. Never discard or hide unrelated changes to satisfy this cleanup rule, and verify the final branch with `git status -sb`.
