# TTM Architecture

This is the implementation map for Total Manager. See [README.md](README.md) for setup and
feature usage. The project is a work in progress; verify details against the code when making
changes.

## At a glance

TTM is a single-process, single-window terminal dashboard built with Terminal.Gui v2 on .NET 10.
The UI runs on one event loop. I/O and heavier work run asynchronously and marshal results back
to that loop.

```text
Program.cs (startup and composition root)
  └── MainWindow (tabs and menu pages)
        ├── mode/*.cs (tab windows)
        ├── settings.cs, mode/notes.cs (menu pages)
        └── widget.cs (reusable views)
              ├── ViewHandler.cs, layouts.cs (UI infrastructure)
              ├── utility.cs (settings, auth, APIs, AI services)
              ├── sql/engine.cs, constants.cs (SQLite)
              └── auth.cs, datasync.cs (credentials and replication)
```

## Startup and navigation

`Program.cs` initializes shared services in this order:

1. `Env.Load()` loads `.env` values without overriding existing environment variables.
2. `UserSettings.Load()` loads defaults and preferences, then creates the app data folders.
3. `Engine.Init()` opens SQLite, enables WAL, and runs schema migrations.
4. `NotesSync.Run()` reconciles note files with the database.
5. `Theme.Register()`, `Apply()`, and `Restore()` set up the selected theme.
6. `Application.Run<MainWindow>()` creates the UI and enters the event loop.

`MainWindow` in `Program.cs` owns the banner, menu bar, tab windows, and menu pages. Switching
pages hides the other views, shows the selected view, and gives it focus. Tabs are visibility
switches, not separate application instances.

## Code map

| Area | Files | Responsibility |
|---|---|---|
| App composition | `Program.cs`, `mode/*.cs` | Startup, tabs, and page-level composition. |
| Reusable UI | `widget.cs` | Dashboard widgets, feeds, meters, tables, script output, history, and chat. |
| UI helpers | `ViewHandler.cs`, `layouts.cs`, `editorkeys.cs` | Visibility, scrolling, sizing, collapse controls, layout, and editor keys. |
| Settings and themes | `utility.cs`, `settings.cs`, `theme.cs` | Environment, persisted preferences, settings pages, and themes. |
| Notes and storage | `mode/notes.cs`, `mode/sync.cs`, `sql/engine.cs`, `constants.cs` | Note editing, indexing, reconciliation, SQLite schema and migrations. |
| Credentials and sync | `auth.cs`, `authui.cs`, `datasync.cs` | Encrypted credential storage, credential prompts, and git-backed data sync. |
| Scripts | `scripts.cs` | Scripts tab and wiring for script execution/output. |
| AI services | `utility.cs`, `widget.cs`, `mode/ai.cs` | Chat providers, context policy, tools, and the AI tab. The AI tab is WIP; see README. |

### Tab windows

| File | Main view | Role |
|---|---|---|
| `mode/dashboard.cs` | `DashWindow` | Combines meters, tasks, notes, feeds, and history widgets. |
| `mode/task.cs` | `TasksWindow` | Tasks stored in `tasks.json`. |
| `mode/todo.cs` | `TodoTestWindow` | Scans configured roots for TODO markers. |
| `mode/claude-stats.cs` | `ClaudeStatsTestWindow` | Claude Code transcript and token statistics. |
| `scripts.cs` | `ScriptsWindow` | Runs PowerShell scripts and displays output/history. |
| `mode/ai.cs` | `AiWindow` | Chat, skills, and review history. WIP. |
| `mode/git.cs` | `GitWindow` | Contribution graph and recent push history. |

Settings and Notes pages are created by `Settings.Pages()` and `Notes.Pages()` and are opened
from the menu bar.

## UI and data loading

Widgets use four loading patterns:

| Pattern | Use | Behavior |
|---|---|---|
| Load on show | TODO scan, transcript stats, files, links, scripts | `OnShown` triggers initial or repeat loading when the view becomes visible. |
| Periodic refresh | Hacker News, Teams, Mail, Freshdesk | `AutoRefresh.RefreshEvery()` schedules refreshes while the view and its ancestors are visible. |
| Local timers | System/process meters and buffered terminal output | `AddTimeout()` drives sampling or output batching; timers are removed when views are disposed. |
| Events | Script output, skill selection, row/button actions | Views send results to other views through events or delegates. |

Long-running scans and process sampling run in background tasks. Results are applied on the UI
loop through `App.Invoke()`. Widgets that change size remeasure themselves and refresh their
containing `LayoutView`.

`Layouts.Horizontal()` and `Layouts.Vertical()` are used by the dashboard to place widgets and
equalize their cross-axis size. `ViewHandler.cs` provides shared visibility, scrolling, table
selection, remeasurement, and collapse helpers. Wheel scrolling is generally four lines per
notch; `ScrollableView` also handles page and Ctrl+arrow navigation.

## Data and persistence

| Store | Location | Contents | Replicated by data sync? |
|---|---|---|---|
| App data | `%APPDATA%\\total-manager\\` (Linux: `~/.local/share/total-manager/`) | Settings, tasks, notes, SQLite database, scripts, reports, reviews, skills, projects, cache, backups. | Yes, except configured exclusions. |
| Credentials | `%LOCALAPPDATA%\\total-manager\\credentials.json` (Linux: `~/.local/share/total-manager/credentials.json`) | Encrypted credentials managed by `AuthStore`. | No. Kept outside the synced folder. |

`UserSettings` stores preferences as an indented JSON object and saves changes immediately.
Tasks are a JSON array managed by `TaskStore`. `Engine` owns the SQLite connection setup and
versioned migrations; the current schema version is **7**.

### Notes lifecycle

Note files are the source of truth. SQLite indexes their content and metadata for the Documents
page and dashboard.

```text
EditorPage.Save
  ├── writes the note file
  └── DocumentationBuilder.Convert
        └── parses optional [key: value] front matter and upserts the notes row

Startup / after sync download
  └── NotesSync.Run reconciles files and rows
        ├── imports unindexed files
        ├── refreshes rows changed on disk
        ├── matches moved files
        └── marks missing files deleted
```

The Documents page groups non-deleted rows by folder. Opening a row routes its path to the Notes
editor. Deleting a note removes the file and marks its row deleted so the deletion can sync.

### Data sync

Sync is last-action-wins; it does not merge.

| Action | Git operations | Result |
|---|---|---|
| Download | `fetch`, `reset --hard`, `clean -fd` | Replaces local data with remote data; creates a backup first. |
| Update | SQLite WAL checkpoint, `add`, `commit`, `push --force` | Replaces remote data with the local vault. |

The generated `.gitignore` excludes `cache/`, `_backup/`, and SQLite WAL/SHM sidecars. Git is
configured not to rewrite line endings. After Download, settings, SQLite, and note indexing are
reloaded; restart the app so already-open views refresh.

## Credentials and integrations

### Credential handling

`AuthStore` stores credentials outside the replicated app-data folder:

```text
passphrase ── PBKDF2-SHA256 (600,000 iterations) ──► key material
                         └── HKDF ──► verifier + AES-256-GCM encryption key
```

Each entry uses AES-GCM with its credential ID as associated data. The verifier is compared in
constant time; encryption keys are not persisted or cached. Changing the passphrase re-encrypts
entries; resetting it requires explicit confirmation and removes entries.

| Integration | Authentication |
|---|---|
| Microsoft Graph | MSAL silent token acquisition, then device-code flow; MSAL manages its token cache. |
| Gmail | Refresh token from `.env`, exchanged for a short-lived access token. |
| Git sync | Credentials encrypted by `AuthStore`; injected into the git child process environment, not command arguments or `.git/config`. |
| GitHub, Codeberg, Freshdesk, AI providers | Tokens/API keys supplied through `.env` or the AI tab as documented in README. |

### AI subsystem

`ChatProvider` creates an `IChatClient` for Anthropic, OpenAI-compatible services, Ollama, or
LM Studio. `ChatWidget` adds function invocation, transient-error retries, and context
compaction/caching through `RetryingChatClient` and `ContextPolicy`.

| Tool group | Scope and safeguards |
|---|---|
| `FileTools` | Reads confined to configured roots; resolves symlinks and rejects sensitive paths. Writes are limited to reports and reviews. |
| `TerminalTools` | Read-only PowerShell allow-list, forbidden-command checks, explicit user approval, timeout, and output cap. |
| `MathTools` | Calculator and statistics functions; arithmetic is parsed rather than evaluated as code. |
| `TranscriptTools` | Reads Claude Code usage statistics. |

The AI tab and chat are not reliable yet. Do not depend on them for important work; see README.

## Security boundaries

| Boundary | Rule |
|---|---|
| Replicated app data | Treat everything under the app-data folder as potentially pushed to the configured remote. Never store plaintext credentials there. |
| Sync credentials | Keep outside the vault; avoid command-line arguments and repository config; redact output. |
| AI file access | Restrict access to configured roots and deny sensitive files; keep writes scoped to reports/reviews. |
| AI terminal access | Allow-list commands and require user approval before execution. |

## Conventions and maintenance

- **UI thread:** run slow I/O off-loop; marshal view updates back through `App.Invoke()`.
- **Timers:** pause refresh work while hidden and remove timers when views are disposed.
- **Persistence:** add database changes as migrations and increment `Engine.TargetSchemaVersion`.
- **Notes:** preserve the file-first model; SQLite is an index, not the source of note content.
- **Sync:** preserve the no-merge, last-action-wins behavior unless the sync design is intentionally changed.
- **Secrets:** use `.env`, an external provider-managed cache, or `AuthStore`; never add secrets to synced settings.

## Tests and current gaps

Run the xUnit note-pipeline test with:

```bash
dotnet test tests/TTM.Tests
```

The test uses `TTM_DATA` to point app storage at a temporary directory. It covers save/index,
startup reconciliation, document-list inclusion, and reopening the saved file. It does not
instantiate the Terminal.Gui UI or automate its Save dialog.

Other known gaps: the AI tab is WIP; `mode/tasks.cs` is empty; some production windows retain a
`Test` suffix in their type names; the dashboard has leftover test controls. See README for the
user-facing WIP list.
