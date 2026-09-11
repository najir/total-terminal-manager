# Total Manager (TTM)

> **Status: WORK IN PROGRESS.** This app is under active development. Tabs, widgets, settings keys and file layout change often. Expect leftover test controls, placeholder pages and rough edges.

> **Avoid the AI tab and the chat widget.** They are not working reliably and are not efficient (slow, token-hungry, error-prone). Do not depend on them for anything yet. This README describes them only so the code stays navigable.

A single-window **terminal dashboard** built with **Terminal.Gui v2** on **.NET 10**. It gathers system meters, TODO scanning, tasks, notes, Freshdesk tickets, GitHub/Codeberg activity, Hacker News, Teams/Gmail previews, PowerShell script running and Claude Code usage stats into one tabbed TUI.

---

## Quick start

| Step | Command / action |
|---|---|
| Prerequisite | .NET SDK 10.0.x (`global.json` pins `10.0.100`, rolls forward to latest major) |
| Secrets | Copy `.env.example` to `.env` and fill only the keys you need (see [Configuration](#configuration)) |
| Build | `dotnet build` |
| Run | `dotnet run` |
| Quit | `Esc`, or the **Quit** button on the Dash tab |

```bash
cp .env.example .env   # then edit .env
dotnet build
dotnet run
```

> **Terminal.Gui v2 warning for contributors:** v2 is a rewrite and most pre-2025 examples are v1 and will not compile. Read `AGENTS.md` (local, git-ignored) before touching UI code.

---

## Tech stack

| Component | Package / version |
|---|---|
| UI framework | `Terminal.Gui` 2.6.0-develop.61 |
| Text editor control | `Terminal.Gui.Editor` 2.5.7 |
| Runtime | .NET 10 (`net10.0`), C# latest, nullable + implicit usings on |
| Local database | SQLite via `Microsoft.Data.Sqlite` 10.0.11 |
| AI clients | `Anthropic` 12.44.0, `Microsoft.Extensions.AI` + `.OpenAI` 10.9.0 |
| Microsoft Graph auth | `Microsoft.Identity.Client` + `.Extensions.Msal` 4.88.0 |

---

## Tabs (top bar, left to right)

| Tab | Source | What it shows | State |
|---|---|---|---|
| **Dash** | `mode/dashboard.cs` | Landing page. Rows of widgets: meters (System, Processes, Next Up), work (Todo, Tasks), recent (Reports, Notes, Freshdesk, Links), feeds (Hacker News, Claude Sessions, Mail, Teams), lower (Git contributions + history, Script history, Review history, Projects). Scrolls vertically. | Usable. Still contains stray test buttons and labels. |
| **Tasks** | `mode/task.cs` | Task list stored in `tasks.json`. Add, complete, trash. | Usable |
| **Todos** | `mode/todo.cs` | Scans a root folder for `TODO` markers, lists them, opens the file. | Usable |
| **Stats** | `mode/claude-stats.cs` | Claude Code usage from `~/.claude/projects` transcripts: totals, 14-day token graph, top 5 tools, per-session table. | Usable |
| **Scripts** | `scripts.cs` | Folder box, one button per `.ps1`, live output terminal, recent-output list. | Usable |
| **AI** | `mode/ai.cs` | Provider/URL/model/key picker over a streaming chat box, skills picker, review history. | **Not working / inefficient. Avoid.** |
| **Git** | `mode/git.cs` | Year contributions heatmap and recent push table. | Usable |

## Menus (top bar, right side)

| Menu | Page | Source | Purpose |
|---|---|---|---|
| **Settings** | Theme | `settings.cs` | Pick a theme; Enter applies it app-wide and persists it. |
| **Settings** | Preferences | `settings.cs` | Edit user-settings keys: providers, folders, toggles, tokens. |
| **Notes** | Editor | `mode/notes.cs` | Text editor with Open/Save, background colour picker, VS Code-style line keys. |
| **Notes** | Documents | `mode/notes.cs` | List of stored notes from the SQLite `notes` table. |

---

## Source files

### Root

| File | Lines | Role |
|---|---|---|
| `Program.cs` | 150 | Entry point. Loads `.env`, user settings, opens SQLite, syncs notes, registers themes, builds `MainWindow` (banner + tab bar + all tabs/pages). |
| `widget.cs` | 4377 | Every reusable widget (see [Widget library](#widget-library)). |
| `utility.cs` | 2281 | Shared services: `Env`, `GraphAuth`, `GmailAuth`, `UserSettings`, `AutoRefresh`, `ApiCache`, `ChatProvider`, `ContextPolicy`, `RetryingChatClient`, and the AI tool sets (`TerminalTools`, `FileTools`, `MathTools`, `TranscriptTools`). |
| `ViewHandler.cs` | 416 | View helpers: visibility switching, `Dim.Auto` re-measure, wheel scrolling, `ScrollableView`, collapse toggle, TableView selection/wheel tweaks. |
| `layouts.cs` | 168 | `Layouts.Horizontal` / `Layouts.Vertical` containers that equalise widgets across the other axis. |
| `settings.cs` | 332 | Theme page and Preferences page plus their menu items. |
| `theme.cs` | 175 | Runtime theme JSON (custom themes), apply and restore of the saved theme. |
| `editorkeys.cs` | 230 | VS Code-style line editing for `Editor` (see [Editor keys](#editor-keys)). |
| `scripts.cs` | 61 | The Scripts tab window. |
| `constants.cs` | 23 | `notes` table name and column definitions. |
| `ttm.csproj` | | Project file. Excludes `_archive/**` and `.backup/**` from compilation. |
| `global.json` | | Pins .NET SDK 10.0.100 with `latestMajor` roll-forward. |
| `.editorconfig` | | UTF-8, 4-space C#, 2-space JSON/MD/csproj. |
| `.env.example` | | Documented template for every secret the app reads. |
| `.gitignore` | | Ignores secrets, build output, IDE files, AI-assistant files, backups. |

### `mode/`

| File | Lines | Role |
|---|---|---|
| `dashboard.cs` | 151 | `DashWindow`: arranges the dashboard widgets in rows. |
| `task.cs` | 391 | `TaskItem`, `TaskStore` (`tasks.json`), `TasksWidget`, `TasksWindow`. |
| `tasks.cs` | 1 | **Empty file.** Leftover; safe to ignore. |
| `todo.cs` | 275 | `TodoTestWindow`: the Todos tab. |
| `claude-stats.cs` | 302 | `ClaudeStatsTestWindow`: the Stats tab. |
| `notes.cs` | 847 | `Notes` menu wiring, `EditorPage`, `DocumentsPage`, `DocumentationBuilder`. |
| `sync.cs` | 326 | `NotesSync`: reconciles the notes folder with the `notes` table once per launch (moved / deleted / restored / refreshed / imported counts). |
| `ai.cs` | 98 | `AiWindow`: the AI tab. **Avoid.** |
| `git.cs` | 26 | `GitWindow`: the Git tab. |

### `sql/`

| File | Lines | Role |
|---|---|---|
| `engine.cs` | 196 | `Engine`: creates `total-manager.db`, WAL mode, migrations to schema version 5, `notes_touch_modified` trigger. |

---

## Widget library (`widget.cs`)

| Widget | Used on | Purpose |
|---|---|---|
| `SystemWidget` | Dash | Live CPU and RAM meters. |
| `ProcessesWidget` | Dash | Heaviest processes: name, RAM, CPU, GPU (`GpuSampler`). |
| `NextUpWidget` | Dash | Contents of `next-up.txt`, re-read whenever the tab is shown. |
| `TodoWidget` | Dash | Oldest TODO markers under the `file-roots` folders. |
| `TasksWidget` (`mode/task.cs`) | Dash, Tasks | Task table backed by `tasks.json`. |
| `ReportsWidget` | Dash | Recent files in the reports folder; `.md` renders as a document. |
| `NotesWidget` | Dash | Most recently modified notes; Enter opens in the Notes editor. |
| `FreshdeskWidget` | Dash | Recent Freshdesk tickets (who, title). Timer refresh while visible. |
| `LinksWidget` | Dash | One button per bookmark in the `links` setting; opens the browser. |
| `HackerNewsWidget` | Dash | Newest HN stories; Enter opens the story. |
| `SessionsWidget` | Dash, Stats | Per-session Claude Code token spend. |
| `LastSessionWidget` | Stats | Newest session's tokens split into four usage buckets. |
| `MailWidget` | Dash (toggle) | Last few inbox emails via Gmail or Microsoft Graph. |
| `TeamsWidget` | Dash (toggle) | Last few Teams messages across all chats. |
| `GitContributionsWidget` | Dash, Git | Year heatmap in GitHub profile style. |
| `GitHistoryWidget` | Dash, Git | Recent pushes from GitHub or Codeberg. |
| `ScriptsWidget` | Scripts | One button per `.ps1` in the folder. |
| `TerminalWidget` | Scripts | Live line-by-line output of the running script. |
| `ScriptHistoryWidget` | Dash, Scripts | Recent script outputs from the reports folder. |
| `ReviewHistoryWidget` | Dash, AI | Recent AI evaluations from the reviews folder. |
| `ProjectsWidget` | Dash | Project documents from the projects folder. |
| `FileHistoryWidget` | (base class) | Generic "recent files in a folder" list; Enter opens read-only. |
| `SkillsWidget` | AI | One button per `.md` in the skills folder; hands the path to the chat. |
| `ChatWidget` | AI | Streaming chat against any `ChatProvider`. **Avoid.** |
| `ReadableMarkdown` | Reports, Notes | Markdown view scrolling at the app's wheel step. |
| `TodoScanner` / `TodoTable` | Todos, Dash | Pure-I/O TODO finder and its table rendering. |
| `ClaudeTranscripts` | Stats | Reads `~/.claude/projects/**/*.jsonl` off the UI loop. |

---

## Configuration

### `.env` (git-ignored, read by `Env.Load`)

| Key | Used by | Notes |
|---|---|---|
| `ANTHROPIC_API_KEY` | AI tab (anthropic provider) | Avoid the AI tab for now. |
| `OPENAI_API_KEY` | AI tab (openai provider, or any OpenAI-compatible URL) | Avoid the AI tab for now. |
| `TEAMS_CLIENT_ID`, `TEAMS_TENANT_ID` | `TeamsWidget`, `MailWidget` (microsoft) | Entra app registration; MSAL token cache. |
| `GMAIL_CLIENT_ID`, `GMAIL_CLIENT_SECRET`, `GMAIL_REFRESH_TOKEN` | `MailWidget` when `email-provider` = `gmail` | |
| `GITHUB_USER`, `GITHUB_TOKEN` | `GitHistoryWidget`, `GitContributionsWidget` | Token is a read-scoped PAT. |
| `CODEBERG_USER`, `CODEBERG_TOKEN` | Same widgets when `git-provider` = `codeberg` | |
| `FRESHDESK_DOMAIN`, `FRESHDESK_API_KEY` | `FreshdeskWidget` | |
| `BACKDROP_FILE` / `BACKDROP` | `MainWindow` banner | File wins over inline; inline uses `\n` for line breaks. |

Rules: a value already in the real environment wins over `.env`; blank values are ignored.

### User settings (`%APPDATA%\total-manager\user-settings.json`)

| Key | Default | Meaning |
|---|---|---|
| `theme` | `Default` | Theme name applied at startup. |
| `email-provider` | `microsoft` | `microsoft` or `gmail`. |
| `git-provider` | `github` | `github` or `codeberg`. |
| `show-teams`, `show-mail` | `true` | Toggle the Teams / Mail widgets on Dash. |
| `file-roots` | | Folders scanned by the Todo widget and the AI file tools. |
| `links` | Codeberg, Hacker News | Bookmark table for `LinksWidget`. |
| `editor-background` | `""` | `#RRGGBB` override for the Notes editor. |
| `ai-provider`, `ai-url`, `ai-model` | | AI tab defaults. Avoid. |
| `ai-compact-tokens`, `ai-trim-tokens` | 120000 / 200000 | Chat context compaction thresholds. Avoid. |
| `next-up-model` | `""` | Reserved for the Next Up widget. |

### App data folder (`%APPDATA%\total-manager\`)

| Path | Contents |
|---|---|
| `user-settings.json` | Settings above. |
| `tasks.json` | Tasks tab store. |
| `database\total-manager.db` | SQLite notes database (schema v5, WAL). |
| `notes\` | Note files, synced into the `notes` table at launch. |
| `scripts\` | Default PowerShell scripts folder. |
| `reports\` | Script outputs, shown by Reports / Script history widgets. |
| `reviews\` | AI evaluations (AI tab). |
| `skills\` | `.md` skill files (AI tab). |
| `projects\` | Project documents for the Projects widget. |
| `cache\` | `ApiCache` responses. |

---

## Themes (`theme.cs`)

| Theme | Origin |
|---|---|
| Default and other Terminal.Gui built-ins | Terminal.Gui |
| TTM Midnight | Custom |
| TTM Paper | Custom (light) |
| Catppuccin Frappe / Macchiato / Mocha | Custom |
| Ayu Dark | Custom |

Pick under **Settings > Theme**. Button shadows are forced off for performance.

## Editor keys (`editorkeys.cs`, Notes editor)

| Key | Action |
|---|---|
| `Ctrl+C` / `Ctrl+X` with no selection | Copy / cut the whole current line |
| `Ctrl+V` after a line copy/cut | Paste as a whole line |
| `Ctrl+Shift+K` | Delete line |
| `Ctrl+Enter` | Insert line below |
| `Ctrl+Shift+Enter` | Insert line above |

## Dash scrolling

| Key | Action |
|---|---|
| `PageUp` / `PageDown` | Page |
| `Ctrl+Up` / `Ctrl+Down` | One line |
| `Ctrl+Home` / `Ctrl+End` | Top / bottom |
| Mouse wheel | Scroll |

---

## Local-only folders (git-ignored)

| Path | Purpose |
|---|---|
| `.env` | Real secrets. Never commit. |
| `.backup/` | Timestamped copies of files before edits. Excluded from build. |
| `_archive/` | Dated archive of removed material. Excluded from build. |
| `.history/` | Editor-generated file history. |
| `.claude/`, `AGENTS.md`, `CLAUDE.md`, `.cursorrules`, `.windsurfrules`, `.aider.md` | AI-assistant instructions. `AGENTS.md` holds the Terminal.Gui v2 patterns. |
| `banner.txt` | Optional custom banner referenced by `BACKDROP_FILE`. |
| `bin/`, `obj/` | Build output. |

---

## Known gaps / WIP list

| Area | Note |
|---|---|
| AI tab and `ChatWidget` | Not working reliably, slow and token-hungry. **Do not use.** |
| Dash tab | Leftover test controls (`abc`, `ab2c`, `testa`, `test2`, "Welcome to Terminal.Gui v3!" label). |
| `mode/tasks.cs` | Empty file. |
| Window class names | `TodoTestWindow` and `ClaudeStatsTestWindow` are production tabs despite the `Test` suffix. |
| Tests | None. |
| Docs | This README is the only project documentation tracked in git. |

---

## References

| Resource | URL |
|---|---|
| Terminal.Gui v2 docs | https://tui-cs.github.io/Terminal.Gui/ |
| Build guide | https://github.com/tui-cs/Terminal.Gui/blob/develop/.claude/tasks/build-app.md |
| Common patterns | https://github.com/tui-cs/Terminal.Gui/blob/develop/.claude/cookbook/common-patterns.md |
| Config system | https://github.com/tui-cs/Terminal.Gui/blob/develop/docfx/docs/config.md |
