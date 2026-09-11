# Total Manager - Project Description

## Overview

**Total Manager (TTM)** is a powerful **Terminal User Interface (TUI) application** built with **Terminal.Gui v2**, designed as a comprehensive productivity hub for developers and knowledge workers. It combines multiple workspace modes into a single terminal-based environment, providing everything from code management and AI-assisted development to system monitoring and documentation.

---

## What It Does

### Core Functionality

Total Manager serves as an **all-in-one terminal workspace** that brings together:

- **Document Management** - Store, search, and edit markdown documents with persistent storage
- **AI Chat Integration** - Multi-provider AI assistants (Anthropic, OpenAI, local models) for coding assistance
- **Task & Todo Tracking** - Scan and manage TODO/FIXME markers across your codebase
- **System Monitoring** - Real-time CPU/RAM metrics, file activity, and process information
- **GitHub Integration** - View recent pushes, contributions, and sync repositories
- **Communication Widgets** - Microsoft Teams messages and Gmail inbox previews
- **Knowledge Base** - Personal wiki with notes, documents, reviews, and skills tracking
- **Scripting Hub** - Run PowerShell scripts from the terminal with live output
- **Helpdesk Integration** - Freshdesk ticket management directly in terminal
- **AI Analytics** - Claude Code usage statistics and token tracking

---

## Application Architecture

### Terminal.Gui v2 Implementation

The app leverages **Terminal.Gui v2.6.0-develop.61** with the dedicated **Editor package (v2.5.7)**, implementing modern TUI patterns:

- **Instance-based lifecycle**: `Application.Create().Run<T>().Dispose()`
- **Declarative layout**: Position and sizing via `Pos`/`Dim` (no hardcoded coordinates)
- **Window-based views**: Subclassing `Window` for full-screen panes
- **Split screen navigation**: Tabbed interface with menu-driven switching

### Database Storage

Uses **SQLite** for persistent document storage:
- Single database file in `%APPDATA%\total-manager\database\`
- Schema version 4 with automatic migrations
- Tables keyed by filepath for notes/documents
- Triggers for automatic timestamp updates

### Configuration System

Multi-layered configuration approach:
1. **Environment variables** (`.env` file - git-ignored)
2. **User settings JSON** (`%APPDATA%\total-manager\user-settings.json`)
3. **Theme system** with 10+ built-in themes plus custom TTM Midnight/Paper

---

## Workspace Modes

### Main Dashboard (Dash Tab)
The primary landing page featuring:
- GitHub contributions calendar heatmap
- Recent push history table
- TODO scanner widget showing markers across the codebase
- System monitoring panel (CPU/RAM meters)
- Freshdesk tickets preview
- Reports folder activity
- Teams and Gmail widgets (optional, togglable)

### Notes Tab
Document management interface with:
- **Editor Page** - Full-text editor with Open/Save buttons, syntax highlighting, line numbers
- **Documents Page** - File browser showing all saved documents with metadata
- Support for custom parameters beyond the base schema (filepath, title, content, created, modified, tags)

### AI Tab
AI assistant integration featuring:
- Multi-provider support (Anthropic, OpenAI, Ollama, LM Studio, local endpoints)
- Conversation history with scrollable transcript
- Skills library - attach markdown files as persistent context
- Review history panel for previous AI evaluations
- API key management per session

### Git Tab
Version control oversight:
- GitHub contributions grid (53x7 year view)
- Recent push history table with commit details
- Provider selection for GitHub/Codeberg/Forgejo

### Scripts Tab
PowerShell automation hub:
- Folder browser for script location
- Script list as executable buttons
- Output history panel showing recent runs
- Full output capture and scrolling

### Tasks Tab
Task management workspace (placeholder mode):
- TODO tracking interface
- Rescan functionality across project root
- In-app editor with syntax highlighting

### Claude Tab
Claude Code usage analytics:
- Session transcript analysis from `.claude` directory
- Token usage breakdown by session
- Tool call statistics (top 5 tools ranked)
- Daily token consumption bar graph (14-day window)
- Sessions table with detailed metrics

### Settings Menu
Configuration pages accessible via menu:
- **Theme Page** - Browse and select from 10+ themes (built-in + custom)
- **Preferences Page** - Persistent user settings including:
  - AI provider, URL, model, API key storage
  - Git provider selection (GitHub/Codeberg)
  - Email provider (Gmail)
  - Teams sign-in credentials
  - Freshdesk domain and API key
  - GitHub/Codeberg authentication tokens
  - Toggle options for Teams/Gmail widgets
  - Scripting folder path
  - Reports folder path

---

## Widget Library

The app ships with **20+ reusable widgets** in `widget.cs`:

| Widget | Purpose |
|--------|---------|
| `TodoWidget` | TODO scanner panel (row count + text width) |
| `SystemWidget` | CPU/RAM meters (30% width, live updates) |
| `TeamsWidget` | Microsoft Teams messages table |
| `MailWidget` | Gmail inbox preview with sender/subject |
| `FreshdeskTicket` | Helpdesk ticket two-column table |
| `ClaudeSession` | Per-session token spend by bucket |
| `LastSessionWidget` | Most recent session's tokens |
| `HackerNewsWidget` | HN stories list (Enter to open) |
| `GitContributionsWidget` | Year heatmap grid |
| `GitHistoryWidget` | Push history table |
| `ScriptsWidget` | Script buttons with output panel |
| `FileRunWidget` | Recent files in folder |
| `FileHistoryWidget` | File activity timeline |
| `NotesWidget` | Saved documents list |
| `ChatWidget` | AI chat interface (provider/model/url) |
| `SkillsWidget` | Skills library picker |
| `ReviewHistoryWidget` | AI evaluation history |
| `ReportsWidget` | Reports folder file browser |
| `LinksWidget` | Bookmark button grid |

---

## Project Structure

```
C:\dev\ttm/
├── Program.cs           # Main entry point, MainWindow root view
├── AGENTS.md            # Terminal.Gui v2 patterns & gotchas
├── CLAUDE.md            # Quick pointer to AGENTS.md
├── README.md            # Basic project overview
├── ttm.csproj           # .NET 10.0 project file
├── .env                 # API keys (git-ignored)
│
├── widget.cs            # 20+ reusable widgets (~200KB)
├── ViewHandler.cs       # Collapse/expand widget logic
├── layouts.cs           # Horizontal/Vertical container layouts
├── utility.cs           # Shared helpers (Env, GraphAuth, Cache)
├── ai.cs                # AI tab window definition
├── theme.cs             # Theme registration (TTM Midnight/Paper)
├── settings.cs          # Theme and preferences pages
├── git.cs               # Git history window
├── scripts.cs           # Scripts runner window
├── constants.cs         # Database schema definitions
│
├── sql/
│   └── engine.cs        # SQLite initialization & migrations
│
├── mode/
│   ├── dashboard.cs     # Dash tab (main landing)
│   ├── notes.cs         # Notes tab (documents + editor)
│   ├── claude.cs        # Claude stats (test/demo window)
│   ├── tasks.cs         # Tasks tab (todo tracking)
│   └── ...
│
├── .backup/             # Version history backups
├── .claude/             # Claude-specific settings
├── .history/            # Environment snapshots
```

---

## Technology Stack

| Component | Technology |
|-----------|------------|
| **UI Framework** | Terminal.Gui v2.6.0-develop.61 |
| **Editor Package** | Terminal.Gui.Editor v2.5.7 |
| **Runtime** | .NET 10.0 (net10.0) |
| **Database** | SQLite (Microsoft.Data.Sqlite) |
| **AI Clients** | Anthropic SDK, Microsoft.Extensions.AI.OpenAI |
| **Authentication** | MSAL (Microsoft Authentication Library) |
| **JSON Processing** | System.Text.Json |

---

## Key Features

### 🤖 AI-Assisted Development
- Multiple LLM provider support with session-specific overrides
- Skills system for persistent context injection
- Token usage tracking and analytics

### 📝 Knowledge Management
- Markdown-first document storage
- Custom schema parameters (tags, custom fields)
- File-based persistence with SQLite indexing

### 🔍 Code Analysis
- TODO/FIXME scanner with in-app navigation
- Line/column precision marking
- Syntax-highlighted editing on hover

### 📊 Real-Time Monitoring
- CPU/RAM meters with live updates
- File system activity tracking
- GitHub contributions heatmap

### 🔐 Security-First Configuration
- `.env` file (git-ignored) for secrets
- Session-specific API key overrides
- Encrypted MSAL token cache
- No credentials in source control

---

## Getting Started

```bash
# Build the project
dotnet build

# Run the application
dotnet run

# Exit: Press Esc or click Quit button
```

**Required setup:**
1. Set API keys in `.env` (Anthropic, OpenAI, etc.)
2. Configure Microsoft Graph app registration for Teams/Gmail
3. Set GitHub token for push history

**Optional — custom banner:** the ASCII art shown above the tab bar can be replaced
without rebuilding. In `.env` set either:

```
BACKDROP_FILE=C:/path/to/banner.txt     # a text file containing the art verbatim (preferred)
BACKDROP=line one\nline two              # inline, with \n for line breaks
```

`BACKDROP_FILE` wins if both are set; if neither is set (or the file is unreadable) the
built-in banner is used.

---

## Design Philosophy

Total Manager follows a **widget-based architecture** where:

- Every UI component is a standalone, composable widget
- Widgets live in `widget.cs` and are instantiated across modes
- Modes (tabs) are Window views that hold widgets in layouts
- Collapsible widgets for space management
- Declarative positioning with `Pos.Right()`, `Dim.Fill()`, etc.

---

## Terminal.Gui v2 Gotchas Handled

The implementation correctly follows v2 patterns:
- ✅ Instance-based lifecycle (no static `Application.Init()`)
- ✅ Split namespaces (`Terminal.Gui.App`, `.Views`, `.ViewBase`, `.Input`, `.Configuration`)
- ✅ No `Clicked` event - uses `Accepted`/`Accepting`
- ✅ Declarative layout with `Pos`/`Dim`
- ✅ Button shadows optimized (performance-critical)
- ✅ Dialog button order: last added = default
- ✅ Typed views expose `.Value` property

---

## Learning Resources

- **Terminal.Gui v2 Docs**: https://tui-cs.github.io/Terminal.Gui/
- **Build Guide**: https://github.com/tui-cs/Terminal.Gui/blob/develop/.claude/tasks/build-app.md
- **Common Patterns**: https://github.com/tui-cs/Terminal.Gui/blob/develop/.claude/cookbook/common-patterns.md
- **Config System**: https://github.com/tui-cs/Terminal.Gui/blob/develop/docfx/docs/config.md

---

## Summary

**Total Manager is a developer productivity hub for the terminal** that combines code management, AI assistance, documentation, system monitoring, and communication into a single cohesive interface. It's designed for knowledge workers who want everything they need in their terminal without switching contexts to external tools.