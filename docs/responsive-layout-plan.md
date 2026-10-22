# Responsive Layout Plan (narrow / tiled / mobile-ready visuals)

> Working plan. Each task in section 6 is self-contained: it lists the files, starting state, steps, done criteria and risk, so it can be picked up or put down between sessions. Tick the checkbox (`[~]` = code complete, check pending) and add a line to the progress log (section 9) when a task lands.

---

## 1. Goal and scope

Make the TTM layout width-aware so it renders cleanly in a **narrow tiled pane** now and on **phone-sized terminals** later.

| In scope | Out of scope (for now) |
|---|---|
| Dash tab layout (`mode/dashboard.cs`) | Mobile access path (SSH / Termux / web bridge) |
| The layout containers (`layouts.cs`) | Touch / soft-keyboard input |
| The top tab/menu bar (`Program.cs`) | Other tabs (Tasks, Research, Notes, Stats, AI). The mechanism must allow them later (T10) |
| A layout-mode setting | |

### Confirmed decisions
| # | Decision | Source |
|---|---|---|
| D1 | Mode is picked **automatically by width**, with a **manual override** setting | User |
| D2 | Narrow default: **stack every row vertically** | User |
| D3 | Each widget may carry an optional **hide-when-limited** tag | User |
| D4 | Widgets **scale down** to some extent below a size threshold | User |
| D5 | At an **extremely small** size, show **one stack at a time** (paged) | User |
| D6 | Focus is visuals and the system that drives layout updates. Overhauling `LayoutView` is allowed | User |
| D7 | Existing call sites (`Layouts.Horizontal/Vertical/Large`) keep compiling unchanged | Plan (compatibility priority) |

### Open decisions (fill in before the noted task)
| # | Question | Needed by | Proposed default |
|---|---|---|---|
| O1 | Breakpoint numbers | T1 | Tiny < 40, Compact < 80, Medium < 140, Wide >= 140 columns. **Changed 2026-10-21 (user): Tiny < 60** |
| O2 | Which widgets get a hide tag, and at which tier | T6 | **Taken 2026-10-21:** the T6 table as proposed (not yet reviewed by the user) |
| O3 | Pager keys | T8 | **Taken 2026-10-21:** `Ctrl+PageUp` / `Ctrl+PageDown` as proposed (not yet reviewed by the user) |
| O4 | Short tab titles | T9 | **Taken 2026-10-21:** the T9 list as proposed (not yet reviewed by the user) |
| O5 | Drop the ASCII banner (`MainWindow.DefaultBackdrop`, 5 rows) in Tiny/Compact? | T9 | **Taken 2026-10-21:** hide it below Medium, as proposed (not yet reviewed by the user) |

---

## 2. Current code: how layout works today (the facts this plan builds on)

### 2.1 Dashboard composition (`mode/dashboard.cs`)
`DashWindow` builds five rows, each chained under the previous with `Y = Pos.Bottom(prev)`:

| Row var | Built with | Children |
|---|---|---|
| `meters` | `Layouts.Horizontal` | `SystemWidget`, `ProcessesWidget`, `NextUpWidget` |
| `work` | `Layouts.Horizontal` | `TodoWidget(amount: 8, textWidth: 40)`, `TasksWidget` |
| `recent` | `Layouts.Horizontal` | `ReportsWidget`, `NotesWidget`, `FreshdeskWidget`, `LinearWidget`, `LinksWidget` |
| `feeds` | `Layouts.Horizontal` | `HackerNewsWidget(10)`, `SessionsWidget`, `MailWidget`, `TeamsWidget` |
| `lower` | `Layouts.Horizontal` | `git` (= `Layouts.Vertical(contributions, history)`), `ScriptHistoryWidget`, `ReviewHistoryWidget`, `ProjectsWidget` |

Other facts:
- Scrolling is manual. `SizeContent()` sets the content size to the bottom of `lower` and runs on `ViewportChanged` and `SubViewsLaidOut`.
- Visibility toggles already exist. `ApplyToggles()` sets `teams.Visible` / `mail.Visible` from `UserSettings.ShowTeams/ShowMail` and is re-run through `this.OnShown(...)`. **The hide-tag system must compose with this. It must never turn a user-disabled widget back on.**
- There are leftover test controls at the top (`quit`, a `Bar` with a `testa` item, a welcome label). This plan does not touch them, but they sit above `meters` and cost vertical space on small screens.

### 2.2 `LayoutView` (`layouts.cs`)
- `Layouts.Horizontal/Vertical/Large` -> `Build(widgets, horizontal, width, gap)` -> `new LayoutView(horizontal, width, gap)` + `Place(widget)` per child.
- `_horizontal` is **readonly**. Direction is fixed at construction. **This is the core thing to change.**
- `Place()` writes each child X/Y **once** (`Pos.Right(_previous) + _gap` or `Pos.Bottom(_previous) + _gap`). Re-flowing needs a re-place pass over the children in their original order, so that order must be stored (today only `_previous` is kept).
- `_natural[widget]` stores the child cross-axis `Dim`. `Equalise()` sets the shorter children to `Dim.Fill()` so the row looks even, and skips hidden or collapsed children. A direction change swaps which axis `_natural` describes, so capture both axes at `Place()` time (`_naturalWidth`, `_naturalHeight`).
- `Resize()` re-equalises and bubbles to `SuperView as LayoutView`. This is reused unchanged.
- The container has its own collapse toggle (`AddCollapse` with the `>` and `|` glyphs).

### 2.3 Helpers in `ViewHandler.cs` to reuse
| Helper | Use in this plan |
|---|---|
| `ViewVisibility.OnShown(view, action)` | Re-apply responsive state when a tab becomes visible (its size may have changed while hidden) |
| `ViewMeasurement.Remeasure(view)` | Refresh `Dim.Auto` after a widget changes its own content width (T7) |
| `ViewLayoutRefresh.ResizeLayout(view)` | After a widget scales, ask the enclosing `LayoutView` to re-equalise |
| `ViewCollapse.AddCollapse / IsCollapsed` | Pattern for the per-view tag store (a static set keyed by reference). The hide logic must honour it: a collapsed widget stays collapsed |

### 2.4 Settings (`utility.cs` `UserSettings`, `settings.cs`)
- Keys are `public const string` fields with a `Defaults` dictionary. Access is through `UserSettings.Get/On/Set`.
- `PreferencesPage` already has a "Dashboard widgets" section built with `Settings.Toggle(...)` and an `OptionSelector` pattern (email provider). The layout-mode selector copies the `OptionSelector` pattern exactly.

### 2.5 Tab bar (`Program.cs`, `MainWindow`)
- `Bar bar` (horizontal, `Height = 1`) inside `menuWindow`. Each tab is a `MenuBarItem { Title = windows[i].Title }`, and the menus are `_Settings` and `_Notes` (`PopoverMenu`).
- At full titles the bar is about 70+ columns (Dash, Tasks, Todos, Stats, Scripts, AI, Git, Settings, Notes, plus padding), so it overflows below Compact.
- The banner (`LoadBackdrop()`) is 5 rows by about 37 columns, above the bar.

### 2.6 Widgets with hard-coded widths (`widget.cs`): what blocks shrinking
| Widget | Constant(s) | Effect today |
|---|---|---|
| `TodoWidget` | ctor `textWidth` (Dash passes 40), `FilePathTail = 30` | Fixed table column widths |
| `SystemWidget` | `ReadoutWidth 10`, `DiskWidth 14`, `BarWidth 12` | Two bars per line, about 50 columns |
| `ProcessesWidget` | `NameWidth 18` | Name column truncation |
| `HackerNewsWidget` | `TitleWidth 50` | Title truncation |
| `TeamsWidget`, `MailWidget` | `TextWidth 60` | Preview truncation |
| `FreshdeskWidget` | `WhoWidth 20`, `TitleWidth 60` (column `MaxWidth`) | Table width |
| `LinearWidget` | `KeyWidth 10`, `WhoWidth 20`, `TitleWidth 60` | Table width |
| `LastSessionWidget` | `NameWidth 12`, `BarWidth 12` | Fixed |
| `NextUpWidget` | `WrapWidth 120` | Wrap width |

These are `const`, so scaling means turning the ones needed into instance fields that `ApplySize` can change (T7).

---

## 3. Research: existing solutions this design borrows from

| Pattern | Where it comes from | What is taken |
|---|---|---|
| Breakpoints / size classes | CSS media queries, Bootstrap (sm/md/lg), Android/Material window size classes (Compact/Medium/Expanded) | A few **discrete tiers**. Layout logic switches on the tier, not on raw numbers |
| Container queries | CSS `@container` | Each `LayoutView` decides from **its own available width**, not the screen width. This is essential for tiles, splits and nested containers (`git` inside `lower`) |
| Flex-wrap / auto-stack | CSS flexbox `flex-wrap`, SwiftUI `ViewThatFits` | A row stacks when its children natural widths do not fit. `ViewThatFits` is the closest analogue: try horizontal, fall back to vertical |
| Responsive classes on widgets | Textual (Python TUI), whose CSS adds classes on resize | A per-widget tag that declares behaviour per tier (hide, scale) |
| Priority hiding | Responsive tables (priority columns), Material progressive disclosure | Optional `HideBelow` tier per widget |
| Pager / cards | Mobile card stacks, tmux/zellij single-pane zoom | Tiny tier shows one row at a time with a `< n/N >` header |
| Hamburger / overflow menu | Mobile nav bars, the VS Code narrow-window menu collapse | Tab bar collapses into one `PopoverMenu` |
| Hysteresis + debounce | Common in responsive JS resize handling | Stops flicker when the width sits on a breakpoint during a drag-resize |

Terminal.Gui v2 itself provides `Dim.Auto/Fill/Percent`, `Pos.Right/Bottom`, `ViewportChanged`, `SubViewsLaidOut` and `FrameChanged`, but **no responsive or flow layout**, so this plan builds a thin layer on those primitives. Before T3, check the current Terminal.Gui v2 docs (context7) for any newer flow-layout support, and use it in place of the custom layer if it exists.

---

## 4. Architecture

```
        Terminal resize / tab shown
                  |
        ResponsiveHost (one per host Window)    <- T5
          - reads LayoutMode override           <- T2
          - Breakpoints.Classify(width)         <- T1 (+hysteresis)
          - only acts if tier/width changed
                  | walks the tagged subtree
     +------------+----------------------+
     v            v                      v
 hide tags    IResponsive.ApplySize   LayoutView.Reflow(tier, width)
 (T4/T6)      (widgets, T7)           (containers, T3)
                  |
          host refresh (SizeContent, pager, bar)  <- T5/T8/T9
```

### 4.1 New types (`responsive.cs`)
```csharp
internal enum SizeClass { Tiny, Compact, Medium, Wide }

internal static class Breakpoints
{
    // Defaults from O1; overridable via UserSettings (T2).
    // Hysteresis: the tier only changes once the width is 2+ columns past the threshold.
    public static SizeClass Classify (int columns, SizeClass? previous = null);
}

internal sealed record ResponsiveTag (SizeClass? HideBelow = null, int? MinWidth = null, string? PageTitle = null);

// ConditionalWeakTable store, same style as ViewTableSelection in ViewHandler.cs.
internal static class ResponsiveTags
{
    public static T Responsive<T> (this T view, SizeClass? hideBelow = null, int? minWidth = null, string? pageTitle = null) where T : View;
    public static ResponsiveTag? GetResponsive (this View view);
}

internal interface IResponsive
{
    // Must be idempotent and cheap; called on every tier or width change.
    void ApplySize (SizeClass size, int availableWidth);
}
```

### 4.2 Compatibility rules
1. Untagged views behave exactly as today on Wide.
2. `Layouts.Horizontal/Vertical/Large` signatures are unchanged. Reflow is opt-in: only a `ResponsiveHost` calls `LayoutView.Reflow`, so pages without a host are unaffected.
3. Responsive hiding is tracked **separately** from user visibility. The host records the views it hid and only restores those. The `ShowTeams/ShowMail` logic stays authoritative: effective visible = userVisible AND NOT responsiveHidden. `ApplyToggles` sets a user-hidden flag that the host reads (T6).
4. Collapsed widgets (`IsCollapsed()`) are left as they are.
5. Widgets opt into scaling by implementing `IResponsive`. Widgets that do not implement it are only stacked or hidden.

---

## 5. Behaviour per tier (target spec)

| Tier (default cols) | Rows | Widgets | Pager | Tab bar | Banner |
|---|---|---|---|---|---|
| **Wide** (140+) | Horizontal (current look) | Natural size | Off | Full titles | Shown |
| **Medium** (80-139) | Horizontal if they fit, else stacked (per container) | Natural; `IResponsive` may trim | Off | Full titles | Shown |
| **Compact** (60-79) | All stacked; children use `Dim.Fill()` width | Scaled (`ApplySize`); HideBelow Wide/Medium hidden | Off | Short titles | Hidden (O5) |
| **Tiny** (under 60) | Stacked | Scaled to minimum; HideBelow Compact hidden | **On**: one row at a time | Single menu item | Hidden |

---

## 6. Tasks

Each task block has: **Files**, **Steps**, **Done when**, **Size** (S/M/L = rough amount of code touched), **Risk**, **Depends**.

### [x] T1: Core responsive types
- **Files:** new `responsive.cs`.
- **Steps:**
  1. Add `SizeClass` and `Breakpoints.Classify` (constants from O1, with the hysteresis rule from 4.1).
  2. Add `ResponsiveTag` + `ResponsiveTags` (a ConditionalWeakTable keyed by `View`, matching the `ViewTableSelection` style in `ViewHandler.cs`).
  3. Add `IResponsive`.
  4. Add a file header comment block in the same style as the other files.
- **Done when:** `dotnet build` passes and nothing calls the new types yet.
- **Size:** S | **Risk:** Low | **Depends:** none. **Needs O1.**
- **Implementation notes (done 2026-10-21):**
  - O1 was taken at its proposed defaults. The thresholds started as `const`s on `Breakpoints`. T2 renamed them so each is named after the tier that applies *below* it (`DefaultTiny = 40`, `DefaultCompact = 80`, `DefaultMedium = 140`), to match the `layout-breakpoints` JSON keys.
  - `SizeClass` is ordered smallest first (`Tiny, Compact, Medium, Wide`), so tiers can be compared directly (`size < tag.HideBelow`, `size <= SizeClass.Compact`).
  - `Classify(columns, previous)` first finds the raw tier. With no `previous`, or no change, that is the answer. Otherwise it re-classifies with the width moved `Hysteresis` (2) columns back toward the old tier, and only changes tier if the result still differs. In practice, growing past 80 switches to Medium at 82, and shrinking below 80 switches back to Compact at 77.
  - `ResponsiveTags` uses a `ConditionalWeakTable<View, ResponsiveTag>`, as `ViewTableSelection` does, so tags never keep a disposed view alive. `.Responsive(...)` returns the view so it chains in object-building code. Calling it again replaces the tag.
  - `IResponsive` is the single method `ApplySize(SizeClass, int availableWidth)`.
  - Verified: `dotnet build` passes with no warnings from `responsive.cs`. Nothing references the new types yet.

### [~] T2: Layout-mode setting (code complete; persistence check pending)
- **Files:** `utility.cs` (`UserSettings`), `settings.cs` (`PreferencesPage`).
- **Steps:**
  1. Add a `LayoutMode` key (value `layout-mode`) with default `auto` in `Defaults`. Values: `auto | wide | medium | compact | tiny`.
  2. Add an optional key `layout-breakpoints` (JsonObject with `tiny`, `compact`, `medium`). `Breakpoints` reads it and falls back to the constants.
  3. In `PreferencesPage`, add a Layout label + `OptionSelector`, copying the email-provider block (`Labels`, `Value = Array.IndexOf(...)`, `ValueChanged -> UserSettings.Set`).
  4. Add a static event `Breakpoints.OverrideChanged`, raised on set, so open hosts re-apply immediately (T5 subscribes).
- **Done when:** the value persists in `user-settings.json` across a restart (verified by reading the file).
- **Size:** S | **Risk:** Low | **Depends:** T1.
- **Implementation notes (2026-10-21):**
  - `utility.cs`: added `LayoutMode = "layout-mode"` (default `LayoutAuto = "auto"`), the `LayoutModes` array that the selector shows, and `LayoutBreakpoints = "layout-breakpoints"`. The breakpoints key has no default, so it only appears in the file when the user adds it.
  - `responsive.cs`: `Breakpoints.Tiny/Compact/Medium` are now properties. Each one reads its entry from `UserSettings.GetTable(LayoutBreakpoints)` and falls back to the `Default*` const, ignoring missing or non-positive values. `Breakpoints.Override` maps the setting to a `SizeClass?` (null = auto). `Breakpoints.SetMode(mode)` saves the setting and raises `OverrideChanged`.
  - `settings.cs`: a "Layout" `OptionSelector` sits below the Dashboard widgets toggles and copies the git-provider block. It calls `Breakpoints.SetMode`, not `UserSettings.Set`, so the event always fires.
  - Verified: build is clean. **Pending:** pick a mode in Settings, restart, and confirm `layout-mode` in `%APPDATA%\total-manager\user-settings.json`. This needs a manual run.

### [~] T3: `LayoutView` overhaul (code complete; visual check pending)
- **Files:** `layouts.cs`.
- **Steps:**
  1. Make `_horizontal` mutable. Keep the constructor and the `Layouts.*` factories unchanged.
  2. Keep an ordered `List<View> _order` (filled in `Place`). Replace the single `_previous` link with an `Arrange()` pass that rewrites X/Y for the visible children in order: horizontal uses `Pos.Right(prev)+gap`, vertical uses `Pos.Bottom(prev)+gap`. Skip hidden children so stacks leave no gaps.
  3. Store both natural dims per child (`_naturalWidth`, `_naturalHeight`) at `Place` time. `Equalise()` picks the cross-axis one from the current direction.
  4. Add `public bool Reflow(SizeClass size, int available)`:
     - Remember `_preferHorizontal` (the original direction).
     - If `_preferHorizontal`: go vertical when `size <= Compact`, or when the sum of natural child widths plus gaps exceeds `available` (container-query rule; use the widths measured at Wide).
     - When vertical, children get `Width = Dim.Fill()`. When going back, restore `_naturalWidth`.
     - Clear `_measured`, re-run `Arrange()` and `Equalise(remeasure: true)`, and return whether anything changed.
  5. Make the container `Width` follow the mode: the factory `width` argument when horizontal, `Dim.Fill()` when stacked.
  6. Nested containers: `Reflow` recurses into child `LayoutView`s with the child available width (container query). `git` inside `lower` is already vertical and stays vertical.
- **Done when:** a temporary debug key on Dash (removed in T10) that calls `Reflow(Compact, ...)` on every row stacks them, and `Reflow(Wide, ...)` restores the original look exactly (compare screenshots).
- **Size:** L | **Risk:** **High.** This is the core of the existing layout, and `Equalise` re-entrancy (`_busy`) plus the `Dim.Fill` / `Dim.Auto` interplay is subtle. Keep all new behaviour behind `Reflow`, so nothing changes when no host is attached.
- **Depends:** T1.
- **Implementation notes (2026-10-21):**
  - New state: `_preferHorizontal` (the direction from the factory, readonly), mutable `_horizontal`, `_width` (the factory width, restored when going back to horizontal), `_order` (children in `Place` order), `_naturalWidth` / `_naturalHeight` (both dims captured at `Place`), and `_sideBySideWidth` (each child's real width, recorded whenever `Reflow` runs while horizontal).
  - `Place()` still sets X/Y itself, so pages without a host lay out exactly as before. `_previous` is replaced by `_order.LastOrDefault()`.
  - `Arrange()` re-chains the **visible** children in `_order` (`Pos.Right` or `Pos.Bottom` + gap), so hidden widgets leave no gap. Only `Reflow` calls it, which keeps today's behaviour for un-hosted pages.
  - `Reflow(size, available)`: the container stays horizontal only if it prefers horizontal, the tier is above Compact, and the sum of the visible children's side-by-side widths plus gaps fits `available`. On a direction change it restores both natural dims on every child and sets the container `Width` to `_width` (horizontal) or `Dim.Fill()` (stacked). It then always runs `Arrange()`, clears `_measured`, re-equalises, and recurses into child `LayoutView`s: a stacked parent passes its inner width (minus border), and a horizontal parent passes the child's own width. It returns true if this container or any child changed direction.
  - `Equalise` is unchanged except that its cross-axis "natural" is now `Natural(v)`: the natural height when horizontal, `Dim.Fill()` for a row that Reflow stacked, and the natural width for a container that was built vertical (`git`). So a stacked row's widgets all fill the width, and `git` behaves as before.
  - Temporary debug key: **F12** on Dash toggled all five rows between `Reflow(Compact, ...)` and `Reflow(Wide, ...)`. **Removed in T10;** check with Settings > Layout = Compact / Auto instead.
  - Verified: build is clean. The only `layouts.cs` warnings are the two CS8601s in `SetAxis` that were already there. **Pending:** run `dotnet run`, set Layout = Compact (rows stack), set it back to Auto at full width (rows return), and compare with a screenshot taken before. `tuirec` is not installed, so this needs a manual run.

### [~] T4: Hide-tag mechanics (code complete; behaviour check needs T5)
- **Files:** `responsive.cs`.
- **Steps:**
  1. Add `static class ResponsiveVisibility` with a `HashSet<View>` of views the responsive system hid (reference equality, as in `ViewCollapse.Collapsed`), plus a user-hidden set.
  2. `Apply(View root, SizeClass size)`: for each tagged descendant, if `size < tag.HideBelow` and the view is visible, hide it and record it. Otherwise, if the system hid it, restore it.
  3. Never show a user-hidden view (T6 step 1).
  4. After any change, call `ResizeLayout()` on the parent so `Equalise` (which already skips non-visible children) recomputes.
- **Done when:** manual check: tag Teams with `HideBelow = Medium`, narrow the window and Teams disappears, widen it and Teams comes back. With ShowTeams off it never comes back.
- **Size:** S | **Risk:** Medium (interaction with `ApplyToggles`) | **Depends:** T1.
- **Implementation notes (2026-10-21):**
  - `ResponsiveVisibility` in `responsive.cs` keeps two reference-equality sets: `HiddenBySize` and `HiddenByUser`. A view is shown only when it is in neither set, which is the "userVisible AND NOT responsiveHidden" rule from 4.2.
  - `Apply(root, size)` walks every descendant. For each one with a `HideBelow` tag it adds the view to or removes it from `HiddenBySize`, then applies the combined result. Untagged views are never touched.
  - `SetUserHidden(view, hidden)` is the entry point that T6 will move `ApplyToggles` onto.
  - `Show()` only acts when `Visible` actually changes. After a change it calls `view.ResizeLayout()`, so the enclosing `LayoutView` re-equalises. Collapse state is never read or written, so a collapsed widget stays collapsed.
  - Nothing calls `Apply` yet, so the manual Teams check in "Done when" has to wait until T5 attaches a host.

### [~] T5: `ResponsiveHost` coordinator (code complete; resize check pending)
- **Files:** `responsive.cs`, `mode/dashboard.cs`.
- **Steps:**
  1. Add `internal sealed class ResponsiveHost` with `static ResponsiveHost Attach(View host, Action<SizeClass>? onChanged = null)`.
  2. Subscribe to `host.ViewportChanged`, `ViewVisibility.OnShown(host, ...)` and `Breakpoints.OverrideChanged`.
  3. Compute the tier from `Viewport.Width`, the override and hysteresis. If the tier or width changed, run in order: `ResponsiveVisibility.Apply` -> `IResponsive.ApplySize` on each implementing descendant -> `Reflow` on the top-level `LayoutView`s -> `onChanged`.
  4. Add a re-entrancy guard (the same `_busy` pattern as `LayoutView`) and coalescing: use the Terminal.Gui timeout API (about 50 ms) to collapse resize-drag bursts. Confirm the exact v2 API name in the docs first.
  5. In `DashWindow`: `ResponsiveHost.Attach(this, _ => SizeContent())`. `SizeContent` stays as is.
- **Done when:** dragging the terminal from wide down to 60 columns turns Dash into stacked rows with no right-edge clipping, and back again. Scrolling still reaches the bottom of `lower`.
- **Size:** M | **Risk:** Medium (layout feedback loops: `SubViewsLaidOut` -> `SizeContent` -> layout) | **Depends:** T1, T3, T4.
- **Implementation notes (2026-10-21):**
  - `ResponsiveHost` in `responsive.cs`. `Attach` subscribes to `ViewportChanged`, `OnShown` and `Breakpoints.OverrideChanged`, and unsubscribes from the static event on `Disposing`.
  - Coalescing uses `App.AddTimeout` (50 ms, returns false so it runs once), the same v2 API the System widget uses. `ViewportChanged` also fires on scroll, so a schedule only happens when the width changed or the call is forced (override change, page shown).
  - `Update` order: tier from `Breakpoints.Override ?? Classify(width, previous)` -> `ResponsiveVisibility.Apply` -> `IResponsive.ApplySize` on visible descendants -> `Reflow(size, width)` on each top-level `LayoutView` -> `onChanged` -> `SetNeedsLayout`. A `_busy` guard blocks re-entry.
  - `DashWindow` calls `ResponsiveHost.Attach (this, _ => SizeContent ())` right after the existing `SizeContent` hooks.
  - The F12 debug key still works, but the next width change overrides it.
  - Verified: build is clean, with no new warnings. **Pending:** drag the terminal from wide down to 60 columns and back; check there is no right-edge clipping and that scrolling reaches the bottom of `lower`. Then do T4's Teams check.

### [~] T6: Tag the Dash widgets (code complete; per-tier check pending)
- **Files:** `mode/dashboard.cs`.
- **Steps:**
  1. Change `ApplyToggles` to mark user-hidden state through `ResponsiveVisibility.SetUserHidden(view, bool)` instead of writing `Visible` directly, so T4 can compose with it.
  2. Add `.Responsive(...)` to each widget. Proposed starting point (O2, confirm or edit):

     | Widget | HideBelow | PageTitle (on the row) |
     |---|---|---|
     | System, Processes, NextUp | none | `meters` -> Meters |
     | Todo, Tasks | none | `work` -> Work |
     | Reports, Notes, Freshdesk, Linear | none | `recent` -> Recent |
     | Links | Compact | |
     | HackerNews | Compact | `feeds` -> Feeds |
     | Sessions, Mail, Teams | none | |
     | GitContributions | Compact (the heatmap needs width) | `lower` -> History |
     | GitHistory, ScriptHistory, ReviewHistory, Projects | none | |
  3. Put page titles on the five row containers (used by T8).
- **Done when:** the confirmed table is implemented and each tier visibly matches it.
- **Size:** S | **Risk:** Low | **Depends:** T4, T5. **Needs O2.**
- **Implementation notes (2026-10-21):**
  - O2: the proposed table went in unchanged, as the default. Edit the `.Responsive(...)` calls in `mode/dashboard.cs` to change it.
  - Hide tags: `LinksWidget`, `HackerNewsWidget` and `GitContributionsWidget` get `hideBelow: SizeClass.Compact`, so they hide in Tiny only. A tier "below Compact" is Tiny, which means these three stay visible at 40-79 columns. If they should go at Compact too, change the tag to `SizeClass.Medium`.
  - Page titles: `meters` -> Meters, `work` -> Work, `recent` -> Recent, `feeds` -> Feeds, `lower` -> History. Nothing reads them until T8.
  - `ApplyToggles` now calls `ResponsiveVisibility.SetUserHidden` for Teams and Mail instead of setting `Visible`, so a resize never brings back a widget the user switched off.
  - Verified: build is clean, with no new warnings. **Pending:** set Layout = Tiny in Settings and confirm that Links, Hacker News and Git contributions disappear and come back on Auto at full width. With ShowTeams off, cycle every Layout value and confirm Teams never appears (this also closes T4).

### [~] T7: Widget scaling (`IResponsive`), one sub-task per widget (code complete; 40-column check pending)
General recipe for each widget:
1. Turn the width `const`s used at runtime into private fields initialised from the const (keep the const as the Wide value).
2. Implement `ApplySize(size, width)`: compute the new widths (for example `TitleWidth = Math.Clamp(width - fixedColumns, MinTitle, TitleWidthConst)`), then re-apply the table column `MaxWidth`s or re-render the rows.
3. Only if something changed, call `this.Remeasure()` then `this.ResizeLayout()` (existing helpers).

| Sub-task | Widget | Plan |
|---|---|---|
| [~] T7a | `TodoWidget` | Shrink `TextWidth` toward about 20 and `FilePathTail` toward about 12; drop the path column on Tiny |
| [~] T7b | `HackerNewsWidget` | `TitleWidth` 50 -> fit; drop the date prefix on Tiny |
| [~] T7c | `FreshdeskWidget` | Title `MaxWidth` 60 -> fit; drop Who on Compact and below |
| [~] T7d | `LinearWidget` | Same as Freshdesk; keep Key |
| [~] T7e | `MailWidget`, `TeamsWidget` | `TextWidth` 60 -> fit |
| [~] T7f | `SystemWidget` | Compact: one meter per line (CPU, RAM, Disk, Batt stacked) instead of two columns; `BarWidth` 12 -> 8 on Tiny |
| [~] T7g | `ProcessesWidget` | `NameWidth` 18 -> 10 |
| [~] T7h | `NextUpWidget` | `WrapWidth` 120 -> available width |

- **Done when (each):** at 40 columns the widget fits inside its border with no clipping, and at Wide it is identical to before.
- **Size:** S each | **Risk:** Low-Medium (the table re-render paths differ per widget, so read each widget refresh method first) | **Depends:** T1 (T5 to see it live).
- **Implementation notes (2026-10-21):**
  - **When widgets scale:** only at Compact and Tiny (`ResponsiveWidth.Scaled`). At Medium and Wide every widget uses its original constants, so the full-size look is unchanged. This also stops a feedback loop: `LayoutView.Reflow` decides "side by side or stacked" from widths recorded while horizontal, and those must not shrink at Medium.
  - **Width given to widgets (changes T5):** `ResponsiveHost.ApplySizes` used to pass `Frame.Width`. That is useless for `Dim.Auto` widgets, which are as wide as their content. It now passes the host width minus the border of every container down to and including the widget: the inside of the widget's border when it fills the page, which is what a stacked row gives it.
  - **Shared helpers** in `responsive.cs`: `ResponsiveWidth.Scaled`, `Fit (available, reserved, min, max)` and `Cap (text, max)`.
  - **Recipe used:** each widget keeps the const as its full-size value, adds instance fields for the widths in use, keeps its data uncut (or cut to the full-size const), and cuts it when drawing. `ApplySize` returns early when nothing changed, otherwise it redraws through the widget's existing render path, which already calls `Remeasure` + `ResizeLayout`.
  - T7a `TodoWidget` (rule revised by the edge fix below): `room = width - 26`, which is Tag 6 + Modified 8 + Line 7 at their widest plus the 5 vertical lines. File gets up to half of `room` (at most 33, i.e. "..." + 30) plus one more line. Text gets the rest (up to the constructor width). On Tiny, or when File would be under 15 columns, File is dropped and Text gets all of `room`. `TodoTable.BuildSource` gained `showFile`, and `ApplyStyle` gained `showFile`/`fileMin`/`textMin`. It now clears `ColumnStyles` first because the indexes shift. The defaults keep the Todos tab (`mode/todo.cs`) unchanged.
  - T7b `HackerNewsWidget`: stories stored as (date, title). Compact: title = width - 10 (10 to 50). Tiny: the date prefix is dropped as well. Status lines are only cut when scaled.
  - T7c `FreshdeskWidget`: Compact and Tiny drop Who; the title is cut to width - 2 (10 to 60). Column styles are rebuilt on every `Show` because dropping Who moves Title to index 0.
  - T7d `LinearWidget`: same as Freshdesk, and ID stays (reserve `KeyWidth + 3`).
  - T7e `MailWidget`, `TeamsWidget`: rows stored as (prefix, text), with a null prefix for status lines. When scaled, each row's text gets the width minus its own prefix (10 to 60), and status lines are cut to the width. Sign-in prompts wrap to the same width.
  - T7f `SystemWidget`: Compact and Tiny put CPU, RAM, DISK and BATT one per line with every bar at `DiskWidth + 1`. Tiny uses 8-column bars. Bar quantising follows the bar width in use.
  - T7g `ProcessesWidget`: Name 18 -> 10 when scaled. The last sample is kept so a resize redraws without waiting for the 12 s timer.
  - T7h `NextUpWidget`: wrap = the available width (10 to 120) when scaled.
  - **Edge fix (2026-10-21, user report: "the edges of tables get cut off weird when in compact"):**
    - Cause: the table widgets size their `TableView` to `sum(column + 1)`. With vertical cell lines a table also draws a left-hand line, so it needs one column more, and its right-hand line and right corners were never drawn. At full size the widget hugs the table and its own border sits in that column, which hides the gap. In a stacked row the widget fills the width, so the gap showed.
    - Second cause: Todo's width budget was 2 columns short (it assumed Line was at most 5 wide and missed one line), and its minimum column widths could add up to more than the space available, so Text and Line ran past the border.
    - First fix (superseded): when scaled, only `TodoWidget`, `FreshdeskWidget` and `LinearWidget` set `_table.Width = Dim.Fill ()`.
    - **User follow-up: "not fixed, a ton of tables have same issue".** Every Dash table widget sizes its table the same way, and every widget in a stacked row is stretched, whether or not it implements `IResponsive`: Processes, Sessions, Git history, Notes, and the `FileHistoryWidget` family (Reports, Script history, Review history, Projects).
    - Final fix: `ViewTableWidth.FitWidth (this TableView, int cells)` in `ViewHandler.cs`. It sets `Width = Dim.Func (...)` once: the cell width while the widget is `Dim.Auto` (side by side, unchanged look), and the widget's viewport width once the widget has any other width (a stacked row's `Dim.Fill`). Because it is re-evaluated on every layout, it follows `Reflow` without the widget being told. It returns whether the cell width changed, which `ProcessesWidget` uses to decide on a remeasure. All 8 table sites in `widget.cs` call it, and no `_table.Width =` is left.
    - Verified with a scratch harness that compiles the real `layouts.cs`, `ViewHandler.cs` and `responsive.cs` (with a `UserSettings` stub): two table widgets in `Layouts.Horizontal`, then `Reflow (Wide)` and `Reflow (Compact)`. Stacked, both tables reach the widget border with right-hand line and corners drawn. Side by side, the drawing matches the original (the widget border sits in the table's last column, as before).
    - Verified headlessly with a scratch Terminal.Gui harness (`ansi` driver, `SetScreenSize`, `Driver.ToString ()`). Freshdesk drawn at 78/60/46 columns and Todo at 120/79/66/60/44/38/32 all end in the table's own right line inside the widget border. Before the fix, Freshdesk showed no right line and Todo overflowed at 78/60/44. The harness copies the widgets' column setup rather than running the real widgets, so a check in the real app is still needed. At 32 columns Todo's Text column is 1 wide; Tag/Modified/Line could be trimmed later if that matters.
  - Verified: `dotnet build` is clean, with only the 5 existing warnings. **Pending (manual):** at 40 columns (Layout = Compact) every widget fits inside its border with no clipping, and at Layout = Auto at full width every widget looks as it did before.

### [~] T8: Pager mode (Tiny) (code complete; paging check pending)
- **Files:** `responsive.cs` (a `ResponsivePager` helper), `mode/dashboard.cs`.
- **Steps:**
  1. `ResponsivePager(View host, IReadOnlyList<View> pages)` shows only the current page at `Y = 1`, with a 1-row header label of the form `< PageTitle i/n >`.
  2. Keys from O3, added with `AddCommand` + `KeyBindings.Add` (the same pattern as the scroll bindings in `DashWindow`). Avoid clashing with `PageUp/PageDown` (scroll) and `Ctrl+Home/End`.
  3. Mouse: clicking an arrow uses `MouseBindings` -> `Command.Accept`, as `AddCollapse` does.
  4. The host enables the pager on Tiny and disables it otherwise. The pager stores the original `Y = Pos.Bottom(prev)` chain and restores it.
  5. While paging, `SizeContent` measures only the current page.
- **Done when:** below 40 columns only one row shows, the keys and arrows cycle through all five rows, and widening restores the full stacked or horizontal layout.
- **Size:** M | **Risk:** Medium (focus handling when the focused row is hidden: move focus to the new page) | **Depends:** T5, T6. **Needs O3.**
- **Implementation notes (2026-10-21):**
  - `ResponsivePager` in `responsive.cs`. The constructor adds a hidden one-line header to the host: `<` label, title label (`PageTitle i/n`, from the T6 row tags, falling back to `Page i`), `>` label. The arrows use `MouseBindings` -> `Command.Accept` -> `Accepted`, like `AddCollapse`.
  - Keys (O3): `Ctrl+PageDown` next, `Ctrl+PageUp` previous, wrapping round. **Differs from step 2:** they are handled in the host's `KeyDown` instead of `AddCommand` + `KeyBindings`. The host's free `Command` values are already its scroll commands, and the keys should do nothing (fall through) when not paging. `PageUp/PageDown` and `Ctrl+Home/End` are untouched.
  - `SetEnabled (true)` saves each page's `Y` (the `Pos.Bottom` chain), hides every page but the current one and puts it at `Y = 1`. `SetEnabled (false)` restores every saved `Y`, shows all pages and hides the header. The rows have no `HideBelow` tag, so this does not clash with `ResponsiveVisibility`.
  - Focus: if a page being hidden held focus, the new page gets `SetFocus ()`.
  - `DashWindow`: the pager covers the five rows. The `ResponsiveHost` callback calls `pager.SetEnabled (size == SizeClass.Tiny)` before `SizeContent`. `SizeContent` measures `pager.Current` instead of `lower` while paging. `PageChanged` scrolls back to the top and re-sizes the content.
  - Verified: build is clean, with only the 5 existing warnings. **Pending (manual):** below 40 columns (or Layout = Tiny) only one row shows under the header. The keys and both arrows cycle through all five rows. Widening (or Layout = Auto) restores the full layout with the rows in their original order and no gap at the top.

### [~] T9: Responsive tab bar and banner (code complete; per-tier check pending)
- **Files:** `Program.cs` (`MainWindow`), `responsive.cs`.
- **Steps:**
  1. Keep `List<MenuBarItem> tabs` and the Settings/Notes items in fields.
  2. Short titles (O4, proposed): Dash -> D, Tasks -> Tk, Todos -> Td, Stats -> St, Scripts -> Sc, AI -> AI, Git -> G, Settings -> Set, Notes -> N. Hotkeys keep working.
  3. `ResponsiveHost.Attach(this, ApplyBar)` on `MainWindow`:
     - Medium and up: full titles.
     - Compact: short titles.
     - Tiny: hide all items and show one Menu `MenuBarItem` whose `PopoverMenu` lists every tab (reusing the same `Action` lambdas) plus the Settings and Notes sub-items.
  4. Banner (O5): hide the backdrop `Label` below Medium. Check that `menuWindow.Y = Pos.Bottom(title)` collapses when the label is invisible, and set `Y = 0` explicitly if it does not.
- **Done when:** at each tier the bar fits on one line, every tab is still reachable, and the banner behaves per O5.
- **Size:** M | **Risk:** Medium (rebuilding `MenuBarItem` + `PopoverMenu` in v2: check the docs before writing) | **Depends:** T1, T2, T5. **Needs O4, O5.**
- **Implementation notes (2026-10-21):**
  - **`ResponsiveHost.Attach` gained `applyToSubViews` (default true).** `MainWindow` passes false, so its host only works out the tier and calls back. Without it the main window's host would run `ResponsiveVisibility`/`ApplySize` over the Dash widgets at the main window's width, which is 2 columns wider than the Dash viewport. The two hosts could then disagree at a breakpoint.
  - `MainWindow`: a `ShortTitles` map keyed by the full title (O4 as proposed, with `_Set` and `_N` keeping the Settings/Notes hotkeys) and a `barItems` list of every bar item with its full title. The items are not rebuilt; the callback changes `Title` and `Visible` in place.
  - Tiny: every bar item is hidden and a `_Menu` `MenuBarItem` is shown. Its `PopoverMenu` lists every tab (fresh `MenuItem`s that share the tabs' `Action` lambdas), then the Settings pages, then the Notes pages (a second call to `Settings.MenuItems` / `Notes.MenuItems`, since a `MenuItem` can only sit in one menu). No separators, because the v2 separator API was not checked.
  - Banner (O5): `title.Visible = size >= Medium`, and `menuWindow.Y` is set to `0` / `Pos.Bottom(title)` outright, so it does not depend on how a hidden label's frame behaves.
  - Not done from step 1: the tabs were not moved into fields. Locals captured by the callback are enough.
  - Verified: build is clean, with only the 5 existing warnings. **Pending (manual):** at each tier the bar fits on one line (in particular, check that hidden `MenuBarItem`s leave no gap in the `Bar`). The `_Menu` popover opens every tab and page on Tiny, and the banner disappears below 80 columns with no blank rows above the bar.

### [x] T10: Generalise and document
- **Files:** `README.md`, this doc.
- **Steps:**
  1. Write a Making a page responsive section: `ResponsiveHost.Attach(window, onChanged)`, `.Responsive(hideBelow:, minWidth:, pageTitle:)`, implement `IResponsive` where needed, and use `Layouts.*` for rows.
  2. List the candidate pages and what each needs, for later: the Tasks/Research/Notes toolbars (`Pos.Right` button chains -> a wrapping `Layouts.Horizontal`), and Stats/AI (`Dim.Percent(50)` splits -> a reflowing container).
  3. Remove the temporary debug key added in T3.
- **Done when:** the README section exists and the debug code is gone.
- **Size:** S | **Risk:** Low | **Depends:** T5.
- **Implementation notes (2026-10-21):**
  - README: a new "Making a page responsive" section, covering the tiers table, the five steps (rows, host, tags, `IResponsive`, pager) and a "Pages not yet responsive" table. Every page in that table was read for its actual blocker: `Pos.Right` toolbars in Tasks, Research, the Notes editor, AI, Scripts and Todos, and `Dim.Percent` splits in Stats and Scripts.
  - Removed the F12 debug block from `mode/dashboard.cs` (T3). T3's visual check is now done with Settings > Layout (Compact stacks the rows, Auto at full width restores them) instead of F12.
  - Not in this task, so left alone: the stray test controls on Dash (the `bar`, `welcome` and `quitButton` locals in `DashWindow`).
  - Verified: build is clean, with only the 5 existing warnings; `F12`, `debugStacked` and `TEMPORARY` no longer appear in `mode/dashboard.cs`.

---

## 7. Dependency graph / suggested order

```
T1 -+- T2 ------------------+
    +- T3 -+                |
    +- T4 -+- T5 -+- T6 ----+- T8
    |      |      +- T9 (also needs T2)
    |      |      +- T10
    +- T7a..h (any time after T1; visible after T5)
```
Minimum useful milestone: **T1 -> T3 -> T4 -> T5** (Dash stacks in a narrow pane). Everything after that is incremental.

---

## 8. Verification (run per task; no automated tests unless named)

1. `dotnet build`, with no new warnings.
2. `dotnet run` in Windows Terminal, then resize through **160 -> 100 -> 60 -> 38 -> 30** columns (or split a pane). At each width check: no right-edge clipping, the tier behaviour matches section 5, scrolling reaches the last row, and focus is not lost.
3. Override check: Layout = Compact on a wide screen must stack. Auto follows the width. The setting persists across a restart.
4. Compatibility check: at Wide with Layout = Auto, Dash looks identical to `main` before the change (side-by-side screenshot).
5. User-toggle check: with ShowTeams off, resize across all tiers. Teams never reappears.

---

## 9. Progress log
| Date | Task | Notes |
|---|---|---|
| 2026-10-21 | - | Plan written |
| 2026-10-21 | T1 | Added `responsive.cs` (`SizeClass`, `Breakpoints` + hysteresis, `ResponsiveTag`/`ResponsiveTags`, `IResponsive`). O1 defaults accepted. Build clean |
| 2026-10-21 | T2 | Layout-mode + breakpoints settings, `Breakpoints.Override/SetMode/OverrideChanged`, Settings selector. Persistence check pending |
| 2026-10-21 | T3 | `LayoutView.Reflow` + `Arrange`, both natural dims, F12 debug key on Dash. Visual check pending |
| 2026-10-21 | T4 | `ResponsiveVisibility` (size-hidden and user-hidden sets). Behaviour check waits for T5 |
| 2026-10-21 | T5 | `ResponsiveHost` (width/override/shown triggers, 50 ms coalescing, re-entrancy guard), attached to Dash. Resize check pending |
| 2026-10-21 | T6 | O2 table applied as proposed (3 hide tags at Compact, 5 row page titles); toggles routed via `SetUserHidden`. README updated (Dash, `responsive.cs`, layout settings). Per-tier check pending |
| 2026-10-21 | T7 | T7a-h code complete: 9 widgets implement `IResponsive`, scale only at Compact/Tiny; `ResponsiveWidth` helpers; host now passes inner page width instead of `Frame.Width`. README updated. 40-column check pending |
| 2026-10-21 | T8 | `ResponsivePager` (header with arrows, Ctrl+PageUp/Down via KeyDown, Y-chain save/restore, focus handoff), enabled on Tiny by the Dash host. O3 taken as proposed. README updated (Dash scrolling keys). Paging check pending |
| 2026-10-21 | T9 | Short titles at Compact, single `_Menu` on Tiny, banner hidden below Medium; `ResponsiveHost.Attach(applyToSubViews: false)` for MainWindow. O4/O5 taken as proposed. README updated. Per-tier check pending |
| 2026-10-21 | T10 | README "Making a page responsive" section + not-yet-responsive pages table; F12 debug key removed. All code tasks done; manual checks for T2-T9 outstanding |
| 2026-10-21 | T7 fix | Compact table edges: scaled tables fill their widget (right-hand line was missing), Todo width split rewritten so columns never exceed the border. Verified headlessly; app check pending |
| 2026-10-21 | T7 fix 2 | All Dash tables (not just the 3 scaled ones) route their width through `ViewTableWidth.FitWidth`; spans the widget when stacked. Verified against the real LayoutView in a harness; app check pending |
| 2026-10-21 | (outside plan) | Dash tables no longer take the mouse wheel: `View.NoTableWheel ()` (ViewHandler.cs, next to `WheelRows`) removes the four wheel bindings from every TableView under the Dash, so the wheel falls through to `WheelScrolling`. Verified headlessly: with bindings the table ate the wheel, without them the page received it |
| 2026-10-21 | (outside plan) | Widened to every scrolling view on Dash: `NoTableWheel` replaced by `View.NoWheel ()`, which strips the 4 wheel bindings from every descendant (lists, markdown, tables) and marks it in `WheelScrolling.PassOn`, so views that scroll via `WheelScrolling.Handle` (`ScrollableView`, `ReadableMarkdown`) return false and the wheel bubbles. Verified headlessly with the real helper: wheel over table/list/ScrollableView scrolled the page 4 lines each, and none of the views moved |
| 2026-10-21 | (outside plan) | **User: "its still scrolling".** Cause: removing wheel bindings does not stick, because a TableView binds the wheel again when its source is set (the widgets set sources after `NoWheel` ran). The earlier probe set its sources first, so it missed this. Fix: `NoWheel` now subscribes every descendant's `MouseEvent` (raised before mouse bindings) plus `SubViewAdded` for later views. On a wheel event it scrolls the root via `WheelScrolling.Handle (root, ...)` and sets `Handled`. Verified headlessly with the real `DashWindow` and all its widgets at 220x25: every on-screen view, including tables whose wheel binding was back, scrolled the page |
| 2026-10-21 | (outside plan) | Tiny hides the Dash vertical scroll bar (`VisibilityMode = Manual`, `Visible = false`) in the `ResponsiveHost` callback; other tiers restore `Auto`. Verified headlessly with the real `DashWindow`: at 36 columns the bar is hidden and the viewport is 34 wide; at 70 columns the bar is shown |
| 2026-10-21 | (outside plan) | User: Tiny starts wider and Compact has no scroll bar. `Breakpoints.DefaultTiny` 40 -> 60 (60 chosen as the default; `layout-breakpoints.tiny` overrides it). The Dash scroll bar is hidden for `size <= Compact`. Section 5 and O1 updated; earlier notes that say "40 columns" describe the old threshold. Verified headlessly with the real `DashWindow`, resizing 120/90/75/65/60/55/45/65/90: bar shown at 120 and 90, hidden from 75 down; pager on at 55 and 45 (60 stays Compact because of the 2-column hysteresis); both restored when widening |
| 2026-10-21 | (outside plan) | User: table contents wrap instead of being cut. TableView has no multi-line cells, so `TableWrap.Expand` (responsive.cs) turns each item into one `WrapRow<T>` per wrapped line, with a word wrap that also splits over-long words such as paths. Only the `First` row shows the other cells, and `TableWrap.ItemAt` maps a display row back to the item. Used by Todo (Text, via `TodoTable.BuildWrappedSource`), Freshdesk and Linear (Title) when scaled; full size is one cut row per item as before. Freshdesk/Linear no longer cut titles at fetch time (Show cuts them at full size). Verified headlessly with the real widgets fed long rows through their private `Show`: wrapped at Compact with no clipping, and Todo's last display row (8 of 8) resolves to the second item. Not covered: tables that do not scale (Sessions, Git history, Notes, file-history widgets, Processes) still clip at the widget edge |
| 2026-10-21 | (outside plan) | User: a gap row between wrapped items. `TableWrap.Expand (..., gap: true)` (the default) adds a blank row between items, not after the last. It belongs to the item above, so `ItemAt` maps it to that item. Only wrapped tables are affected; full size is unchanged. Verified headlessly with the real widgets: blank row between items in Freshdesk, Linear and Todo; Todo rows 0-10 map to items 1,1,1,1,1,1,1,1(gap),2,2(gap),3 |

> Tasks are sized S/M/L by the amount of code touched, with a risk level. The plan deliberately gives no time estimates.
