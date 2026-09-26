# TTM (Total Manager) — Architecture

> Companion to `README.md`. The README is the user guide (tabs, settings, commands); this
> document is the implementation map — layering, data flow, key mechanisms and the security
> model. Like the app, it is a work in progress: files move and split as features land.

**Status:** WIP. Matches the tree at commit `71a2c78`.

---

## 1. The big picture

TTM is a **single-window terminal dashboard** on **Terminal.Gui v2** (.NET 10). One process,
one UI loop. Everything is a `Terminal.Gui.View` composed into a `MainWindow`; there is no
client/server split, no background worker process — concurrency is *within* the process
(background tasks that marshal back onto the UI loop).

```
                      ┌────────────────────────────────────────────┐
                      │              Program.cs                    │
                      │  Env → UserSettings → Engine → NotesSync →  │
                      │  Theme → Application.Run<MainWindow>       │
                      └───────────────────────┬────────────────────┘
                                              │ owns
                    ┌─────────────────────────▼───────────────────────┐
                    │                    MainWindow                   │
                    │  banner + top Bar; tab windows + menu pages     │
                    └──────────────┬──────────────────┬───────────────┘
                                   │                  │
              ┌────────────────────▼──────┐   ┌───────▼───────────────────┐
              │  mode/*.cs  tab windows   │   │ settings.cs / mode/notes  │
              │  Dash, Tasks, Todos,      │   │ menu pages (Settings/     │
              │  Stats, Scripts, AI, Git  │   │ Notes), opened like tabs  │
              └────────────────────┬──────┘   └───────────────────────────┘
                                   │ compose
                    ┌──────────────▼──────────────────────────────┐
                    │            widget.cs (Widgets)             │
                    │  meters · feeds · tables · launchers · chat │
                    └──────────────┬──────────────────────────────┘
                                   │ use
   ┌───────────────┬───────────────┼────────────────┬──────────────────────┐
   ▼               ▼               ▼                ▼                      ▼
ViewHandler      layouts.cs      utility.cs     sql/engine.cs         auth.cs
OnShown/Rem-     LayoutView     Env, UserSet-   SQLite notes DB      AuthStore
easure/Collapse  equaliser      tings, Auto-    (schema v7)         PBKDF2·HKDF·
/scroll helpers                 Refresh, Api-                        AES-GCM
                                Cache, Chat-
                                Provider, Con-
                                textPolicy,
                                Graph/GmailAuth,
                                AI tool sets
```

The dependency direction is strict and one-way: `Program.cs → mode/* + settings + notes
pages → widget.cs → { ViewHandler, layouts.cs, utility.cs, sql, auth, datasync }`. Lower
layers never reach back up into the UI; `widget.cs` references `mode/` and `settings.cs`
only through a couple of narrow delegates (`Mode.Notes.OpenInEditor`).

---

## 2. Startup sequence (`Program.cs`)

`Program.cs` is a top-level program: each `StartupStep()` line is a deliberate ordering.

| # | Step | What it does |
|---|---|---|
| 1 | `Env.Load ()` | Reads `.env` into the process environment (`utility.cs`). Real env vars win; blanks ignored. |
| 2 | `UserSettings.Load ()` | Loads/creates `%APPDATA%\total-manager\user-settings.json`, merges defaults, migrates legacy shapes, creates the data folders, saves back. |
| 3 | `Engine.Init ()` | Creates `total-manager.db` beside the settings, WAL mode, migrates to schema version 7, wires the `notes_touch_modified` trigger. |
| 4 | `NotesSync.Run ()` | One-shot reconciliation of the notes folder with the `notes` table (see §10). |
| 5 | `Theme.Register ()` / `Apply ()` / `Restore ()` | Injects the custom theme JSON into the Terminal.Gui config system, pushes it to the static facades, re-applies the saved theme. |
| 6 | `Application.Create ().Run<MainWindow> ()` | Builds the root window (banner, tab bar, every tab and page) and enters the UI loop. |

`MainWindow` (in `Program.cs`) is the composition root:

- A **top `Bar`** holds one `MenuBarItem` per tab plus the `Settings` and `Notes` popover menus.
- **Tabs** — one `Window` per top-level tab (`DashWindow`, `TasksWindow`, `TodoTestWindow`,
  `ClaudeStatsTestWindow`, `ScriptsWindow`, `AiWindow`, `GitWindow`), each constructed with
  `Pos.Left(menuWindow), Pos.Bottom(menuWindow)` so it fills the space under the menu.
- **Menu pages** — `Settings.Pages(...)` (`settings.cs`) and `Notes.Pages(...)`
  (`mode/notes.cs`) return more windows registered the same way. Every window lives in one
  `List<View> windows`; a tab menu item calls
  `Handler.ResetVisibility(windows)` then shows + focuses its own window. That `Handler`
  helper (in `ViewHandler.cs`) is the tab switcher — tabs are just visibility toggles.
- Global `using` directives make the Terminal.Gui API surface and `Sql` namespace visible
  everywhere.

---

## 3. Layering and file map

### 3.1 Root — infrastructure

| File | Layer | Contents (types) |
|---|---|---|
| `utility.cs` | Services | `Env`, `GraphAuth`, `GmailAuth`, `Wire`, `UserSettings`, `AutoRefresh`, `ChatProvider`, `ContextPolicy`, `ApiCache`, `TerminalTools`, `RetryingChatClient`, `FileTools`, `MathTools`, `TranscriptTools` |
| `widget.cs` | Widgets + pure-I/O readers | 20+ `View` widgets, `TodoScanner`/`TodoTable`/`TodoHit`, `ClaudeTranscripts`/`ClaudeStats`/`ClaudeSession`, `GpuSampler`, `ReadableMarkdown` |
| `ViewHandler.cs` | View plumbing | `Handler`, `ViewVisibility`, `ViewMeasurement`, `WheelScrolling`, `ScrollableView`, `ViewLayoutRefresh`, `ViewCollapse`, `ViewTableSelection`, `ViewTableWheel` |
| `layouts.cs` | Layout | `Layouts` (factory), `LayoutView` (equalising container) |
| `sql/engine.cs` | Storage | `Engine` (SQLite bootstrap + migrations) |
| `constants.cs` | Storage | `Constants` (notes/research table schema) |
| `settings.cs` | UI | `Settings`, `ThemePage`, `PreferencesPage`, `UserPage` |
| `theme.cs` | UI | `Theme` (custom theme injection) |
| `auth.cs` | Security | `AuthStore`, `AuthKey`, `AuthEntry`, `AuthKdf`, `AuthFile` |
| `authui.cs` | Security/UI | `AuthPrompt` |
| `datasync.cs` | Sync | `RemoteKind`, `GitCredential`, `PendingCredential`, `SyncRemote`, `DataSync`, `SyncRunner` |
| `editorkeys.cs` | UI | `EditorKeys` (VS Code-style bindings) |
| `scripts.cs` | UI | `ScriptsWindow` |

### 3.2 `mode/` — the tab windows (the composition layer)

| File | Type | Composes |
|---|---|---|
| `dashboard.cs` | `DashWindow` | Five `Layouts` bands: meters (system/processes/next-up), work (todo/tasks), recent (reports/notes/freshdesk/links), feeds (news/sessions/mail/teams), lower (git/scripts/reviews/projects). Applies `show-teams`/`show-mail` toggles on show. The whole dash scrolls. |
| `task.cs` | `TaskItem`, `TaskStore`, `TasksWidget`, `TasksWindow` | Tasks tab: JSON-backed task table with add/complete/edit/trash UI. |
| `todo.cs` | `TodoTestWindow` | Todos tab: runs `TodoScanner` off the UI thread, opens hits in a syntax-highlighted editor dialog. |
| `claude-stats.cs` | `ClaudeStatsTestWindow` | Stats tab: `SessionsWidget` + `LastSessionWidget` + 14-day token bar graph + top-5 tools table. |
| `notes.cs` | `Notes`, `EditorPage`, `DocumentsPage`, `DocumentationBuilder`, `ResearchPage` | Notes menu pages; the editor writes the note file *and* upserts the SQLite row on every save. |
| `sync.cs` | `NotesSync` | Folder→DB reconciliation, once at launch and after a sync Download. |
| `ai.cs` | `AiWindow` | AI tab: provider/URL/model/key pickers feeding `ChatWidget`, plus `SkillsWidget` and `ReviewHistoryWidget`. **Unreliable — avoid.** |
| `git.cs` | `GitWindow` | Git tab: `GitContributionsWidget` (contributions grid) + `GitHistoryWidget` (recent pushes). |

### 3.3 The widgets (`widget.cs`)

Roughly grouped top-to-bottom, each family sits next to its helpers:

1. **TODO tooling** — `TodoHit`, `TodoScanner` (pure-I/O regex file walker), `TodoTable` (shared table shape), `TodoWidget`.
2. **Meters** — `NextUpWidget`, `SystemWidget` (P/Invoke `kernel32` CPU/RAM/disk/battery), `GpuSampler` (PDH `\GPU Engine(*)\Utilization Percentage`), `ProcessLoad`, `ProcessesWidget`.
3. **HTTP feeds** — `HackerNewsWidget`, `TeamsWidget` (Graph), `MailWidget` (Graph *or* Gmail), `FreshdeskTicket`, `FreshdeskWidget`.
4. **Claude stats** — `ClaudeStats`, `ClaudeTranscripts` (pure-I/O reader of `~/.claude/projects/**/*.jsonl`, **must run off the UI thread**), `ClaudeSession`, `SessionsWidget`, `LastSessionWidget`.
5. **Launchers** — `SkillsWidget` (one button per skill `.md`), `LinksWidget` (bookmark buttons).
6. **Execution** — `TerminalWidget` (batched, thread-safe output `Editor`), `ScriptsWidget` (runs `.ps1`, `Output` event).
7. **Chat** — `ChatWidget` (streaming chat; see §8).
8. **Git** — `GitPush`, `GitContributionsWidget` (53-week grid, GitHub GraphQL or Codeberg REST), `GitHistoryWidget`.
9. **File history** — `FileRun`, `FileHistoryWidget` (recent files in a folder) and its four thin subclasses: `ScriptHistoryWidget`, `ReviewHistoryWidget`, `ProjectsWidget`, `ReportsWidget`.
10. **Notes** — `NoteRow`, `NotesWidget` (recent notes from SQLite).
11. **Rendering** — `ReadableMarkdown` (a `Markdown` that scrolls 4 lines per wheel notch).

Every widget follows the same conventions: `Title` + rounded border + `AddCollapse()`,
constructor `(Pos? x, Pos? y, Dim? width, Dim? height, …)`, an `OnShown` kickoff, `Ui()`
marshalling back to the UI loop, self-sizing + `Remeasure`/`ResizeLayout` after a render,
and `Disposing` cleanup of timers/CTS.

---

## 4. How widgets get data (four refresh mechanisms)

| Mechanism | Used by | How it works |
|---|---|---|
| **Lazy load on show** | `TodoWidget`, `SessionsWidget`, `LastSessionWidget` (once), `NextUpWidget`, `SkillsWidget`, `LinksWidget`, `ScriptsWidget`, `FileHistory*` (every show) | `this.OnShown(...)` (`ViewHandler.cs`) observes visibility across the whole `SuperView` chain and fires a fire-and-forget `LoadAsync()`/`Reload()`. Gates (`_started`/`_fed`/`_scannedOnce`) make it run once when it must. |
| **`AutoRefresh.RefreshEvery`** | `HackerNews`, `Teams`, `Mail`, `Freshdesk` | `utility.cs`: adds a 60 s `AddTimeout` on `Initialized`, removes it on `Disposing`, and only ticks while `AutoRefresh.Showing(view)` — i.e. every ancestor is `Visible`. A `_loading` flag prevents overlapping loads. |
| **Own `AddTimeout` timers** | `SystemWidget` (1.8 s), `ProcessesWidget` (12 s), `TerminalWidget`/`ChatWidget` (150 ms batched flush) | High-frequency local sampling, each guarded by `AutoRefresh.Showing` so hidden tabs go quiet. `ProcessesWidget` samples on a background thread and marshals back with a custom `InvokeScheduler`. |
| **Events (push)** | `ScriptsWidget.Output → TerminalWidget.Write`, `SkillsWidget.Picked → ChatWidget.AddSkill`, table `Accepted` events | UI-driven; nothing scheduled. |

Heavy I/O is kept off the UI loop: `Task.Run(TodoScanner.Scan)`,
`Task.Run(ClaudeTranscripts.Collect)`, `Task.Run(ProcessesWidget sample)`. Results come back
through `App?.Invoke(...)`.

After data lands, sizing-sensitive widgets call the `Remeasure()` + `ResizeLayout()` pair so
auto-sized panels re-tune and the enclosing `LayoutView` re-equalises.

---

## 5. Layout system (`layouts.cs`, `ViewHandler.cs`)

- **`LayoutView`** is the composition primitive: `Place(widget)` laps children along one axis
  (`Pos.Right`/`Pos.Bottom` + gap) and **equalises the cross-axis** — a shorter child is given
  `Dim.Fill()` so siblings match the tallest/widest natural measurement (re-measured on layout;
  collapsed/hidden children are skipped). `Resize()` cascades to parent `LayoutView`s.
- **`Layouts`** is the static factory: `Horizontal(...)`, `Vertical(...)`, `Large(...)`.
  `DashWindow` is its only consumer today.
- **Collapse** — `AddCollapse()` pins a 1-cell `*`/`+` toggle to a widget's top-left; it parks
  the widget's other subviews, strips the border and shrinks it to near-nothing. A static
  `ReferenceEqualityComparer` set tracks collapsed state; layout and timers both honour it.
- **`AutoRefresh.Showing`** treats collapsed as hidden, so collapsed widgets stop polling.
- **Scrolling** — `WheelScrolling.Handle` (4 lines/notch) is wired into `ScrollableView`,
  `ReadableMarkdown`, and each tab window; `ViewTableWheel` widens `TableView` rows-per-notch;
  `ScrollableView` binds PageUp/PageDown/Ctrl+arrows; the Dash adds Home/End.
- **Tables** start with no row selected (`ViewTableSelection.SetSource`), which removes the
  accidental "first row highlighted" behaviour.

---

## 6. Persistence model

Everything lives under two roots, deliberately separated by trust:

| Root | Path | Contents | Synced? |
|---|---|---|---|
| **The vault** | `%APPDATA%\total-manager\` (`~/.local/share/` on Linux) | `user-settings.json`, `tasks.json`, `database\total-manager.db`, `notes\`, `scripts\`, `reports\`, `reviews\`, `skills\`, `projects\`, `cache\`, `_backup\` | **Yes** (git-replicated, minus exclusions) |
| **Credentials** | `%LOCALAPPDATA%\total-manager\credentials.json` (Linux: `~/.local/share/total-manager/`) | encrypted `AuthStore` | **Never** — outside the vault by design, so a token can't be pushed |

Storage formats:

- **`user-settings.json`** — flat JSON object of `string → JsonNode?` (strings, numbers,
  arrays, objects), written indented. `UserSettings` merges defaults with
  `_values.TryAdd` so new defaults appear in old files; every mutation persists immediately;
  API accessors are `Get / GetInt / GetArray / GetTable / Set / SetTable / On`.
- **`tasks.json`** — a JSON array of `TaskItem` records, managed by `TaskStore`.
- **SQLite** (`sql/engine.cs`) — `total-manager.db`, WAL mode, foreign keys on, pooled
  connections. `Engine` exposes `Init()` (startup, tolerant of failure via a public `Error`
  property) and `Connect()` (throws if `Init` failed). Migrations are versioned steps inside
  one transaction, keyed off `PRAGMA user_version`, currently **schema version 7**:
  1. `notes` table (columns from `Constants.NotesColumns`, `filepath` = PK)
  2. `notes_touch_modified` trigger (recreated on later migrations)
  3. rebuild `notes` (schema format change)
  4. recreate trigger
  5. `notes.deleted` column (soft-delete tombstone)
  6. `research` table (`name`, `url`, `created`)
  7. `research.category` column

The `notes` schema is the app's declared contract — `constants.cs` holds table names and
column maps, so schema lives beside the code that reads it rather than in the migration.

---

## 7. Charting the flows

### 7.1 Notes life-cycle (the most important data path)

Notes are **files first, rows second**. The on-disk `notes\` folder is the source of truth;
the SQLite `notes` table is an index/cache with `title`, `tags`, `content`, `modified`
(by trigger on every UPDATE) and `deleted` soft-delete tombstones.

```
Save in EditorPage
   │  writes file ──► DocumentationBuilder.Store(filepath, values)
   │                     │ parse leading [key: value] front-matter + body
   │                     ▼
   │              UPSERT INTO notes … ON CONFLICT (filepath) DO UPDATE (deleted = 0)
   ▼
launch / post-Download
   NotesSync.Run()  ── reconciles folder ⇄ table in one transaction:
      fresh file with row   → refresh if it differs (Refreshed)
      row marked deleted    → un-delete + restore column text (Restored)
      row with no file      → match a stray by content (Moved) or name, else tombstone (Deleted)
      file with no row      → insert (Imported)
```

`DocumentsPage` groups rows by folder; deleting a row deletes the file *and* calls
`NotesSync.MarkDeleted(path)` so the tombstone (and the other machines that sync it) still
see the delete.

### 7.2 Data sync (`datasync.cs`) — last action wins, never merges

Gated behind **Settings > User** (a credential page, not a preference). Remote kinds:
`Https/Http` (need a credential), `Ssh/Local` (never prompt).

| Direction | Git sequence | Net effect |
|---|---|---|
| **Download** | `fetch` + `reset --hard FETCH_HEAD` + `clean -fd` | Local is overwritten to match the remote. A `_backup\<timestamp>\` copy is taken first; pools are cleared; stale `-wal`/`-shm` sidecars dropped; then settings, engine and notes are reloaded. |
| **Update** | `checkpoint(TRUNCATE)` + `add -A` + `commit` + `push --force` | Remote is overwritten to match local. DB is checkpointed so the WAL is folded into the committed file. |

Excluded from sync (via a generated `.gitignore` in the vault): `cache/`, `_backup/`,
`*.db-wal`, `*.db-shm`. `.gitattributes` is pinned to `* -text` and `core.autocrlf false`
so Windows/Linux round-trips never rewrite line endings.

**Credential handling** is the security-critical part:

- Git always runs with `GIT_TERMINAL_PROMPT=0` / `GIT_ASKPASS=echo` (fail instead of prompt).
- Credentials are injected **through the environment, never argv or `.git/config`**:
  `GIT_CONFIG_COUNT` + `GIT_CONFIG_KEY_i`/`GIT_CONFIG_VALUE_i` pairs set a blank
  `credential.helper` and `http.{origin}.extraHeader = "Authorization: Basic …"` (username
  defaults to `x-access-token`). Optional per-host `sslVerify=false` when
  `sync-insecure-tls` is set.
- A pasted URL with an embedded `user:token` is stripped on **Apply**: the token is captured
  into `PendingCredential` (memory only, single use, doesn't survive a restart) because the
  URL is itself stored inside the synced vault and would otherwise be pushed in plaintext.
  The next sync that needs a credential asks for the passphrase and commits the token to
  `AuthStore`.
- Output is redacted (`://user@` patterns) so tokens can't leak into the log widget.

### 7.3 Authentication (`auth.cs`)

`AuthStore` is a generic encrypted key–value store, not git-specific:

```
passphrase ──PBKDF2-SHA256 (600k iters, random salt)──► 32-byte master
                                                          │ HKDF split (info: "ttm-verify" / "ttm-encrypt")
                                                    ┌─────┴──────┐
                                                    ▼            ▼
                                               verifier      encryption key
                                          (stored, compared  (never stored)
                                           with FixedTimeEquals)
```

Entries are **AES-256-GCM** (12-byte nonce, 16-byte tag) with the **namespaced id as
associated data** — ciphertext can't be relabelled to another id (`git:github.com` →
`git:git.lan`). Keys are never cached; the passphrase is re-entered per action. `Change`
re-encrypts everything under a new passphrase; `Reset` (only after an explicit destructive
confirm) destroys everything, because the old key can't be recovered. The UI (`authui.cs`)
is kept separate from the store.

**Provider auth** avoids storing secrets wherever possible:

- **Microsoft Graph** (`GraphAuth`) — MSAL public client, `Chat.Read`/`Mail.Read`; silent
  acquire first, device-code flow as fallback (the code is rendered into the widget); the
  MSAL token cache (`ttm.msal.cache` in the user profile) handles persistence — the app
  never writes the token.
- **Gmail** (`GmailAuth`) — the long-lived refresh token lives in `.env`; each use trades it
  for a short-lived access token over HTTP. Credentials exist only in env vars.
- **Git sync** — the *only* place the app persists a third-party credential itself, via `AuthStore`.

### 7.4 AI subsystem (`utility.cs` + `ChatWidget`)

**Provider neutrality.** Everything downstream speaks `Microsoft.Extensions.AI`
(`IChatClient`, `AITool`, `ChatMessage`, …). `ChatProvider.Create(provider, url, model, key)`
is the single factory:

| Provider | Wire format | Key |
|---|---|---|
| `anthropic` | Anthropic SDK → `.AsIChatClient(model)` | `ANTHROPIC_API_KEY` |
| `openai` | OpenAI-compatible client | `OPENAI_API_KEY` (loopback endpoints skip the key) |
| `ollama`, `lmstudio` | OpenAI-compatible, loopback | none |

The chat pipeline (built in `ChatWidget`):
`.AsBuilder().UseFunctionInvocation(…).Use(next => new RetryingChatClient(next)).Build()`
— `RetryingChatClient` retries up to 3 times only while nothing has streamed yet, only for
transient errors (`ChatProvider.IsTransient`), with linear 1 s backoff.

**Tool system.** Tools are `AIFunctionFactory.Create` wrappers over `[Description]` C# methods,
grouped into four static sets:

| Set | Exposed tools | Gating |
|---|---|---|
| `FileTools` | `ReadFile`, `ListDirectory`, `SearchFiles`, `FindFiles`, `FileOutline`, `FindTodos`, `WriteReport`, `WriteReview`, `AppendReview` | Root confinement (`FileTools.Roots` from `file-roots` setting) + symlink resolution + secret denylist (`.env*`, `id_rsa`, `.ssh`, key files…) + write allowance limited to `reports/` and `reviews/` |
| `TerminalTools` | `run_command` (read-only PowerShell) | Allow-list of ~45 read-only cmdlets + forbidden-token blacklist + `ApprovalRequiredAIFunction` (per-call user approval dialog) + 20 s timeout + 20 kB output cap — "gated twice: an allow-list here, and the user's own approval" |
| `MathTools` | `Calculate`, `Statistics` | Hand-written recursive-descent expression evaluator (no eval) |
| `TranscriptTools` | `TokenUsage` | Reads `ClaudeTranscripts` data |

Tool registration in `ChatWidget`: base set = `FileTools + MathTools + TranscriptTools`;
adds `HostedWebSearchTool` (Anthropic SDK) when the provider hosts tools; then
`TerminalTools`. System instructions are composed from `FileTools.Instructions()` +
`TerminalTools.Instructions()`.

**Context budget.** `ContextPolicy` keeps the transcript inside token budgets:
`Estimate` (provider-reported count or chars/4) → `ApplyCaching` (Anthropic TTL cache
controls on the oldest and penultimate messages) → `CompactAsync` (summarise-and-splice a
cut prefix, keeping the last 12 messages and never cutting mid-tool-exchange) → trim stale
tool results. Tunables: `ai-compact-tokens` (120k default), `ai-trim-tokens` (200k).

**Status:** the AI tab and `ChatWidget` are WIP and explicitly unreliable — avoid in daily use.

---

## 8. Cross-cutting conventions

- **No `#region`s**; files are organised by feature family with XML doc comments. Big files
  (`widget.cs`, `utility.cs`) group a family's data records *before* its widgets.
- **Everything is `internal`** except the `Sql` namespace surface; no public API to speak of.
- **One-command views**: `TableView` `Accepted` (Enter/double-click), `Button.Accepted`, and
  `ListView.Accepted` are the standard "open/run this" wiring. Browsers launch via
  `Process.Start` with `UseShellExecute`.
- **Widget sizing** self-adapts: content is measured, `Dim.Auto()` re-triggered, then
  `Remeasure`/`ResizeLayout` — so a table grows to fit its rows without a fixed height.
- **Persistence** for user choices is immediate (write-through on change) except the sync
  repo URL, which deliberately has an Apply button because it can carry a secret.
- **Dev conveniences** are pinned: `.env` searches upward from the output dir; `FileRoots`
  default includes `%USERPROFILE%\.claude`, the vault itself, and a hard-coded `C:\dev` root.

---

## 9. Security model, summarised

| Surface | Defence |
|---|---|
| `.env` secrets | Git-ignored; env-var-first precedence; the README + `.env.example` carry heavy warnings |
| Vault contents | Git-replicated to a *private* remote; embedded tokens stripped from URLs on entry |
| Sync credentials | Never on argv/config; env-injected; stored only in `AuthStore` outside the vault |
| `AuthStore` | PBKDF2 600k → HKDF split → AES-256-GCM; ids as associated data; keys never cached; buffers zeroed; verifier compared in constant time |
| AI file tools | Root confinement + symlink resolution + secret denylist; writes only into `reports/`/`reviews/` |
| AI terminal tool | Read-only cmdlet allow-list + forbidden-token blacklist + human approval per call + timeout/caps |
| Plaintext paths | None: every credential path is either env-injected, MSAL/Gmail-managed, or encrypted in `AuthStore` |

---

## 10. Known gaps / where to be careful (from README, reframed for architecture)

- **`ChatWidget` / AI tab** — functional but unreliable, slow, token-hungry. Do not depend on it.
- **`mode/tasks.cs`** — empty leftover file.
- **Window names** — `TodoTestWindow`, `ClaudeStatsTestWindow` are production tabs despite the
  `Test` suffix; renaming them would touch `Program.cs` and README.
- **Dash tab** — stray test controls remain.
- **Tests** — none. The pure-I/O readers (`TodoScanner`, `ClaudeTranscripts`, `AuthStore`,
  `DataSync` logic) are the natural seams to test first.
- **Docs** — README (usage) and this file (architecture) are the only tracked docs.

---

## 11. Design notes / invariants worth preserving

1. **Files first, rows second** — the notes folder is the source of truth; the database is a
   rebuilt index. Never make the DB the source of truth for note *content*.
2. **The vault is replicated; credentials are not.** Anything written inside
   `%APPDATA%\total-manager\` can end up on a git remote. New secret-holding code must use
   `AuthStore` (outside the vault), MSAL-managed caches, or `.env`.
3. **Never merge the sync.** Download/Update are last-action-wins by design; merging is out
   of scope.
4. **UI loop discipline** — file walks, transcript parsing, git runs, PDH sampling and HTTP
   loads run off the UI thread and marshal results back with `App.Invoke`; timers must honour
   `AutoRefresh.Showing` and clean up in `Disposing`.
5. **Tool-gating is layered** — any new AI tool should be read-only by default, confined to
   declared roots, allowed into the tool set only through the group `Tools` lists, and
   reviewed against `TerminalTools`' allow-list/forbidden-token pattern.
6. **Schema changes are migrations** — bump `Engine.TargetSchemaVersion`, add a versioned
   step to `Migrate`, and keep column definitions in `constants.cs` (the trigger and column
   adds are re-run idempotently).