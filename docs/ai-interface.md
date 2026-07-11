# ThreadShelf AI, CLI, and MCP Guide

ThreadShelf ships a working automation surface in `ThreadShelf.Core`, `ThreadShelf.Cli`, and `ThreadShelf.Mcp`. Use it to read Codex threads and to change ThreadShelf-owned organization metadata without editing storage files directly.

## Safety model

Treat operations as three distinct permission classes:

| Class | Examples | Confirmation |
| --- | --- | --- |
| Read-only | list, get, search, list tags | none |
| ThreadShelf sidecar write | folder, notes, favorite, tags, batch organization | `confirmed: true` in MCP or `--yes` in CLI |
| Native Codex write | archive, unarchive, rename thread title | `confirmed: true`/`--yes`, and app-server support |

Deleting a tag is destructive because it removes the definition and every task reference. Confirm the exact tag name before running it.

Never edit `session_index.jsonl`, `sessions`, or `archived_sessions`. Do not edit `threadshelf.json` for normal operations either; use MCP or CLI so validation, atomic writes, and response envelopes stay intact.

## Start and configure the MCP server

Build once, then run the stdio server:

```powershell
dotnet build ThreadShelf.Mcp\ThreadShelf.Mcp.csproj
.\ThreadShelf.Mcp\bin\Debug\net10.0\ThreadShelf.Mcp.exe
```

A typical MCP client entry is:

```json
{
  "mcpServers": {
    "threadshelf": {
      "command": "E:\\ThreadShelf\\ThreadShelf.Mcp\\bin\\Debug\\net10.0\\ThreadShelf.Mcp.exe"
    }
  }
}
```

For a Codex project configuration, use the equivalent stdio entry:

```toml
[mcp_servers.threadshelf]
command = "E:\\ThreadShelf\\ThreadShelf.Mcp\\bin\\Debug\\net10.0\\ThreadShelf.Mcp.exe"
```

Set `CODEX_HOME` in the MCP process environment when it should use a non-default Codex home. For controlled fallback testing, also set `THREADSHELF_CODEX_CLI=C:\Windows\System32\where.exe`.

Codex desktop and Codex CLI are separate capabilities. A desktop-only installation uses local JSONL fallback. For the `app-server` provider, install the CLI from the [official Codex CLI documentation](https://learn.chatgpt.com/docs/codex/cli), verify `codex --version`, and restart the ThreadShelf host. The official npm package can be installed with `npm install -g @openai/codex@latest`; Windows `.cmd` and `.bat` shims are supported.

## Verify discovery with `tools/list`

After configuration, ask the MCP host to refresh tools. For a raw stdio check:

```powershell
'{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' |
  .\ThreadShelf.Mcp\bin\Debug\net10.0\ThreadShelf.Mcp.exe
```

The current catalog contains 15 tools. The response schemas returned by `tools/list` are authoritative. Add or edit tools through `ThreadShelf.Mcp/ToolCatalog.cs`, where every descriptor binds its schema directly to a method in `ToolHandlers.cs`; re-run discovery and the registry consistency test after either file changes.

## Tool catalog

All tools accept optional `codexHome`. Mutation tools require `confirmed: true`; a batch request with `dryRun: true` is read-only and does not.

| Tool | Required arguments | Optional arguments | Access |
| --- | --- | --- | --- |
| `threadshelf_list_threads` | — | `workspace`, `folder`, `tag`, `query`, `archived`, date bounds, `excludeThreadIds`, `fields`, `limit`, `refresh` | read |
| `threadshelf_get_thread` | `threadId` | `refresh` | read |
| `threadshelf_search_threads` | `query` | list filters, `fields`, `limit`, `refresh` | read |
| `threadshelf_update_thread_metadata` | `threadId`, `confirmed` | `folder`, `notes`, `favorite` | sidecar write |
| `threadshelf_move_thread` | `threadId`, `folder`, `confirmed` | — | sidecar write |
| `threadshelf_add_thread_tag` | `threadId`, `tag`, `confirmed` | — | sidecar write |
| `threadshelf_remove_thread_tag` | `threadId`, `tag`, `confirmed` | — | sidecar write |
| `threadshelf_batch_update_threads` | — | `tags`, `threads`, `dryRun`, `confirmed` | preview or atomic sidecar write |
| `threadshelf_list_tags` | — | — | read |
| `threadshelf_create_tag` | `name`, `confirmed` | `color`, `description` | sidecar write |
| `threadshelf_update_tag` | `name`, `confirmed` | `newName`, `color`, `description` | sidecar write |
| `threadshelf_delete_tag` | `name`, `confirmed` | — | destructive sidecar write |
| `threadshelf_archive_thread` | `threadId`, `confirmed` | — | native Codex write |
| `threadshelf_unarchive_thread` | `threadId`, `confirmed` | — | native Codex write |
| `threadshelf_rename_thread` | `threadId`, `title`, `confirmed` | — | native Codex write |

Folder filter values use stable internal keys: `__all`, `__favorites`, and `__unfiled`, or a literal folder name. An empty folder in a move/update clears the folder. Tag mutations require an existing global tag.

`workspace` is an exact, case-insensitive path match after trimming trailing `/` or `\`; it is not a text search. `updatedAfter`, `updatedBefore`, `createdAfter`, and `createdBefore` are exclusive boundaries and require an ISO 8601 timezone (`Z` or an explicit offset). A created-time filter excludes threads whose provider cannot supply `createdAt`. Use `fields` to request a compact projection, for example `["id", "title", "updatedAt", "tags"]`; omitting it preserves the complete response.

Long-lived MCP and UI processes reuse the in-memory provider thread index while re-reading ThreadShelf sidecar metadata. `source.loadedAt` identifies the provider load and `source.cached` reports a cache hit. Set `refresh: true` when fresh Codex archive/title/session state is required. Native archive and rename operations invalidate the index automatically. The one-command CLI does not write a disk cache.

## Recommended agent workflow

Always use read → confirm target → write → read:

1. Call `threadshelf_search_threads` or `threadshelf_list_threads` to identify candidates.
2. Call `threadshelf_get_thread` with the exact ID and inspect its current folder, tags, and source provider.
3. Explain the intended mutation and obtain confirmation when the host has not already granted it.
4. Call one mutation with `confirmed: true`.
5. Call `threadshelf_get_thread` again and verify the requested fields.

For multiple related changes, prefer `threadshelf_batch_update_threads`; it validates all IDs, tag definitions, colors, duplicate entries, tag references, and operation conflicts before writing the sidecar once. Preview first with `dryRun: true` when the target set is broad.

### Example: find → move → tag → verify

Search:

```json
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"threadshelf_search_threads","arguments":{"query":"release"}}}
```

Confirm the returned `threadId` with `threadshelf_get_thread`. List tags and create `ready` only if it does not exist:

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"threadshelf_list_tags","arguments":{}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"threadshelf_create_tag","arguments":{"name":"ready","color":"#1F883D","description":"Ready for review","confirmed":true}}}
```

Move and attach the tag:

```json
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"threadshelf_move_thread","arguments":{"threadId":"<exact-id>","folder":"Delivery","confirmed":true}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"threadshelf_add_thread_tag","arguments":{"threadId":"<exact-id>","tag":"ready","confirmed":true}}}
```

Verify:

```json
{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"threadshelf_get_thread","arguments":{"threadId":"<exact-id>"}}}
```

Do not create the tag blindly on every run: an existing name returns `tag_conflict`.

### Atomic batch example

```json
{
  "name": "threadshelf_batch_update_threads",
  "arguments": {
    "dryRun": true,
    "tags": [
      { "name": "ready", "color": "#1F883D", "description": "Ready for review" }
    ],
    "threads": [
      { "threadId": "<id-1>", "folder": "Delivery", "addTags": ["ready"] },
      { "threadId": "<id-2>", "folder": "References", "removeTags": ["blocked"] }
    ]
  }
}
```

The result includes each thread's before/after folder and tags plus `changed`. Re-submit with `dryRun: false, confirmed: true` to write. `addTags` and `removeTags` preserve unrelated tags; `setTags` explicitly replaces the complete set. The legacy `tags` property remains a compatibility alias for `setTags`. Mixing replacement and incremental operations, or adding and removing the same tag, is rejected. An empty folder clears it; an empty `setTags` array removes all tags.

## Responses and provider boundaries

The desktop UI's workspace link and interactive Resume/New task actions are not MCP or CLI tools. They do not change `source.provider`: the shared interactive launcher first validates the workspace (and thread ID for Resume), prefers a registered Codex desktop app, and falls back to a visible CLI process with structured arguments. Automation agents should continue to use the catalog above for organization and native app-server mutations; they must not imitate UI deep links as a successful MCP mutation.

MCP returns a standard MCP text content item whose text is a JSON envelope:

```json
{
  "ok": true,
  "data": {},
  "warnings": [],
  "source": {
    "provider": "app-server",
    "codexHome": "C:\\Users\\me\\.codex",
    "sidecarPath": "C:\\Users\\me\\.codex\\threadshelf\\threadshelf.json",
    "loadedAt": "2026-07-11T08:00:00Z",
    "cached": true
  }
}
```

Interpret `source.provider` before choosing a native action:

- `app-server`: native archive/unarchive/thread-title rename may be attempted.
- `local-files`: browsing and ThreadShelf sidecar writes work, but native actions return `native_action_unsupported`.

Warnings explain provider fallback. Never treat local JSONL presence as permission to move files or fabricate an archive state.

Errors use:

```json
{
  "ok": false,
  "error": {
    "code": "confirmation_required",
    "message": "Set confirmed to true to allow this mutation.",
    "retryable": false
  }
}
```

Handle these stable codes explicitly:

- `confirmation_required`: ask for or apply valid mutation confirmation.
- `thread_not_found`, `tag_not_found`, `tag_conflict`, `invalid_argument`: re-read targets/definitions and correct arguments.
- `native_action_unsupported`: remain in read/sidecar mode; do not emulate the native operation.
- `app_server_unavailable`: report the native failure; a later retry may work.
- `local_jsonl_read_failed`: the fallback files are inaccessible or temporarily locked; retry later or install Codex CLI and use `app-server`.
- `sidecar_read_failed`, `sidecar_write_failed`, `permission_denied`: stop writes and report the path/permission problem.

## CLI equivalent

The CLI uses the same command service and envelopes:

```powershell
dotnet run --project ThreadShelf.Cli -- threads list --json
dotnet run --project ThreadShelf.Cli -- threads list --workspace E:\\Widget --updated-after 2026-07-01T00:00:00Z --fields id,title,updatedAt,tags --format jsonl
dotnet run --project ThreadShelf.Cli -- threads get <id> --json
dotnet run --project ThreadShelf.Cli -- threads move <id> --folder Delivery --yes --json
dotnet run --project ThreadShelf.Cli -- threads tag add <id> ready --yes --json
dotnet run --project ThreadShelf.Cli -- native archive <id> --yes --json
```

`--format jsonl` writes one thread object per line for streaming scripts. Batch JSON uses the same `setTags`/`addTags`/`removeTags` and `dryRun` properties as MCP; `threads batch-update --file plan.json --dry-run` does not require `--yes`.

Run `dotnet run --project ThreadShelf.Cli -- --help` for the complete current command syntax.

## Smoke test

Run the skill-bundled smoke test. It creates and deletes a temporary `CODEX_HOME`, verifies `tools/list`, one read, the confirmation gate, tag creation, folder/tag sidecar writes, read-back, and an unsupported native action:

```powershell
powershell -ExecutionPolicy Bypass -File .\.codex\skills\thread-shelf-reactor\scripts\Test-ThreadShelfMcp.ps1
```

The test forces local fallback with `where.exe` and never touches the user's real Codex home.
