# AI Interface Design

ThreadShelf exposes an automation surface that is safe by default, scriptable
from terminals, and easy for AI assistants to discover through MCP. The current
implementation uses a shared `ThreadShelf.Core` command surface plus thin
`ThreadShelf.Cli` and `ThreadShelf.Mcp` executables that reuse the same command
handlers.

## Goals

- Let an assistant list, inspect, search, and filter Codex threads.
- Let an assistant update ThreadShelf-owned metadata: folder, tags, notes, and
  favorite state.
- Let an assistant manage global tag definitions.
- Let an assistant request native Codex actions only when the Codex CLI
  app-server supports them.
- Keep Codex-owned storage read-only except for native app-server operations.

## Data Model

Thread objects are read models composed from Codex app-server or local JSONL
fallback plus ThreadShelf sidecar metadata.

```json
{
  "id": "uuid",
  "title": "string",
  "updatedAt": "2026-07-09T00:00:00Z",
  "workspace": "string",
  "model": "string",
  "sourcePath": "string",
  "isArchived": false,
  "metadata": {
    "folder": "string",
    "tags": ["string"],
    "notes": "string",
    "favorite": false,
    "updatedAt": "2026-07-09T00:00:00Z"
  }
}
```

Tag definitions are global ThreadShelf records:

```json
{
  "name": "bug",
  "color": "#D1242F",
  "description": "Needs a fix"
}
```

## CLI Shape

Use `threadshelf` as the executable name. Every command accepts
`--codex-home <path>` and `--json`. Commands that mutate state accept
`--yes` for non-interactive confirmation.

| Command | Access | Description |
| --- | --- | --- |
| `threadshelf threads list` | read | List threads with optional filters. |
| `threadshelf threads get <id>` | read | Return one thread with metadata. |
| `threadshelf threads search <query>` | read | Search title, id, folder, tags, notes, workspace, source, and model. |
| `threadshelf threads update <id>` | write | Patch ThreadShelf metadata. |
| `threadshelf threads move <id> --folder <name>` | write | Set or clear the ThreadShelf folder. |
| `threadshelf threads tag add <id> <tag>` | write | Attach a global tag to a thread. |
| `threadshelf threads tag remove <id> <tag>` | write | Remove a tag from a thread. |
| `threadshelf tags list` | read | List global tags and usage counts. |
| `threadshelf tags create` | write | Create a global tag. |
| `threadshelf tags update <name>` | write | Rename or edit a global tag. |
| `threadshelf tags delete <name>` | destructive | Delete a tag definition and remove thread references. |
| `threadshelf native archive <id>` | native write | Archive through Codex app-server. |
| `threadshelf native unarchive <id>` | native write | Unarchive through Codex app-server. |
| `threadshelf native rename <id> --title <title>` | native write | Rename through Codex app-server. |

Example:

```powershell
threadshelf threads list --folder Work --tag bug --limit 50 --json
threadshelf threads update 018f... --favorite true --notes "Follow up" --json
threadshelf tags create --name bug --color "#D1242F" --description "Needs a fix" --yes --json
```

## MCP Tools

The MCP server should map one tool to one command handler. Tool names should be
stable and explicit:

- `threadshelf_list_threads`
- `threadshelf_get_thread`
- `threadshelf_search_threads`
- `threadshelf_update_thread_metadata`
- `threadshelf_move_thread`
- `threadshelf_add_thread_tag`
- `threadshelf_remove_thread_tag`
- `threadshelf_list_tags`
- `threadshelf_create_tag`
- `threadshelf_update_tag`
- `threadshelf_delete_tag`
- `threadshelf_archive_thread`
- `threadshelf_unarchive_thread`
- `threadshelf_rename_thread`

Each MCP response should return:

```json
{
  "ok": true,
  "data": {},
  "warnings": [],
  "source": {
    "provider": "app-server",
    "codexHome": "C:\\Users\\me\\.codex",
    "sidecarPath": "C:\\Users\\me\\.codex\\threadshelf\\threadshelf.json"
  }
}
```

## Permissions

Read-only operations can run without confirmation.

ThreadShelf metadata writes should require confirmation in MCP unless the host
has already granted write permission for the session. CLI users can pass
`--yes`.

Native Codex actions should always be marked separately because they call
`codex app-server` mutation methods.

Destructive operations require explicit confirmation:

- Delete tag definitions.
- Remove a tag from all thread references.
- Future delete-thread operations, if Codex exposes a supported native API.

ThreadShelf should never write folders, tags, notes, or favorite state into
Codex-owned JSONL files.

## Error Format

Every CLI JSON and MCP error should use a stable envelope:

```json
{
  "ok": false,
  "error": {
    "code": "tag_not_found",
    "message": "Tag 'bug' was not found.",
    "details": {
      "name": "bug"
    },
    "retryable": false
  }
}
```

Recommended error codes:

- `invalid_argument`
- `thread_not_found`
- `tag_not_found`
- `tag_conflict`
- `app_server_unavailable`
- `native_action_unsupported`
- `sidecar_read_failed`
- `sidecar_write_failed`
- `confirmation_required`
- `permission_denied`

## CLI vs MCP

CLI is the best first implementation because it is easy to test, script, and
ship with the desktop project. It also works for humans and CI jobs.

MCP is the best AI-native surface because tools have schemas and hosts can
apply confirmation policy before writes.

The implementation should put repository operations into a shared library,
then expose them through both:

```text
ThreadShelf.Core
  Repository, models, validation, command handlers
ThreadShelf.App
  WinUI Reactor desktop UI
ThreadShelf.Cli
  System.CommandLine executable
ThreadShelf.Mcp
  MCP server using the same handlers
```

## Implementation Status

The first implementation is present in:

- `ThreadShelf.Core`: repository, models, validation, command handlers, and
  stable JSON result/error envelopes.
- `ThreadShelf.Cli`: `threadshelf` executable commands with `--json`,
  `--codex-home`, and `--yes`.
- `ThreadShelf.Mcp`: stdio JSON-RPC MCP server with the stable tool names above
  and input schemas.
- `tests/ThreadShelf.Tests`: temporary `CODEX_HOME` tests covering list/search,
  metadata patch, tag rename migration, tag delete cleanup, fallback behavior,
  and unsupported native-action errors.

## MVP Plan

1. Move current repository/data records into `ThreadShelf.Core`.
2. Add command handlers that accept typed request records and return typed
   result envelopes.
3. Add `ThreadShelf.Cli` with read commands, then metadata write commands.
4. Add tag management commands, including rename migration and delete cleanup.
5. Add native app-server commands with unsupported-operation errors when the
   fallback provider is active.
6. Add an MCP server that exposes the same handlers with JSON schemas and
   confirmation metadata.
7. Add tests with a temporary `CODEX_HOME` covering list, search, metadata
   patch, tag rename migration, tag delete cleanup, and fallback behavior.
