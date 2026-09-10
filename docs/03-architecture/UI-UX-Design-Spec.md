# UI/UX Design Specification & Style Guide

**Product:** Enlist.Portal
**Framework:** Blazor Server + MudBlazor 8.6.0
**Document status:** Derived from the current implementation. Originally written 2026-09-03; **substantially revised 2026-09-07** after the terminology rename (Machine→Agent, Assignment→Application Policy, Tag→Tag), the portal restructure from five tabs to three, and the visual pass that replaced master-detail tables with expand-in-place cards. This is both a description of the shipped UI and a style guide for extending it consistently.

---

## 1. Brand

- **Product name in the UI:** "enList Portal", rendered in `MainLayout.razor`'s app bar and followed by the current section — see §3.
- **Brand mark:** "iron canary" wordmark logo, `wwwroot/media/iron-canary-logo.png` — the real wordmark asset (blue mark + gray text), not a text approximation. Rendered via `<MudImage Height="36" Width="78" ObjectFit="ObjectFit.Contain">`.
- **Lineage:** the branding is deliberately carried over from a prior internal tool ("enList v2") rather than invented fresh. The header *layout* is not — see §3, which departs from it.

## 2. Theme

Only `PaletteDark` is customized, plus a global border radius. Everything else (typography, shadows, elevation) is MudBlazor's own default, unchanged.

```csharp
PaletteDark = new PaletteDark
{
    Background = "#1a1a27",
    Surface = "#242435",
    AppbarBackground = "#1c1c28",
    Primary = "#0088D0",
    TextPrimary = "#f5f5f7",
    TextSecondary = "#9a9ab0",
},
LayoutProperties = new LayoutProperties
{
    DefaultBorderRadius = "12px",
}
```

`IsDarkMode="true"` is forced — the portal does not offer a light-mode toggle. **This is load-bearing:** conventions below (notably the palette-variable technique in §2.3) assume a dark ground and are not automatically safe if a light mode is ever added.

`DefaultBorderRadius = "12px"` is what rounds dialogs, cards and buttons globally rather than each component opting in.

**`Primary = #0088D0`** is sampled directly from the logo PNG's dominant blue, so buttons, selected nav, tab underlines and switches pick up the same blue as the "iron" mark. It replaced MudBlazor's stock purple.

**`AppbarBackground = #1c1c28`** is not arbitrary: it is the one background color keeping *both* colors baked into the logo PNG legible at once — the blue mark (~rgb(0,128,192), measured 4.07:1 against this color) and the gray wordmark (~rgb(160,160,160), measured 6.75:1). Against the theme's *former* Primary purple both were nearly invisible (1.38:1 / 2.29:1), which is the original reason the app bar does not simply use `Primary`.

### 2.1 Color usage — a known pitfall

`MudText`/`MudChip`'s `Color` parameter selects a **theme accent color** (`Color.Primary`, `Color.Secondary`, `Color.Info`, etc.), not a semantic "muted text" token. `Color.Secondary` in particular resolves to MudBlazor's default pink/magenta accent (`rgb(255,64,129)`), which reads as jarring, hard-to-read "red" text on this dark background — **it is not a substitute for a dimmed/caption gray**, and is not used anywhere in this portal.

**Convention:** every de-emphasized caption line (timestamps, service/job/application descriptions, "none"/"none currently" placeholders) uses `Color="Color.Info"` (a legible light blue), never `Color.Secondary`. For genuinely muted gray, omit `Color` entirely on a `Typo.caption`/`Typo.body2` element.

This document previously asserted `Color.Secondary` "is not used anywhere in this portal". That was not verified when written, and was false: three components still used it — the cron-override hint in the policy edit dialog, the selected-file line in the upload dialog, and the "none" tag placeholder on the Agents page. All three rendered as the harsh pink this convention exists to avoid. They are now `Color.Info`. **The grep is one line; run it rather than trusting this sentence:** `grep -rn 'Color="Color.Secondary"' src/Enlist.Portal/Components/`

### 2.2 Color as a semantic signal — established conventions

| Color | Used for |
|---|---|
| `Color.Success` (green) | A `Running`/`Enabled` state chip; the agent scheduling switch when on; a Start button's fill when the target is stopped. |
| `Color.Error` (red) | A `Failed` state chip; the `conflict` chip; a Stop button's fill when the target is running. |
| `Color.Warning` (amber) | A `Restarting`/`Disabled` state chip; the "stale report" and "zero rules / zero agents" warning icons; a Disable button's fill. |
| `Color.Default` (gray) | A `Stopped` state chip — deliberately neutral, not alarming. Also the version chip. |
| `Color.Primary` (logo blue `#0088D0`) | Broad-scope and primary actions, and the `Service` type badge. |
| `Color.Info` (light blue) | De-emphasized caption/secondary text — see §2.1 — and, via its lighten variant, the `Job` type badge. |

### 2.3 Reaching a "lighten"/"darken" shade

MudBlazor's `Color` enum exposes only base swatches — there is no `Color.PrimaryLighten` or `Color.InfoLighten`. The lighten/darken shades exist only as **palette CSS variables** generated at runtime by `MudThemeProvider`: `--mud-palette-info-lighten`, `--mud-palette-tertiary-darken`, and so on.

To use one, set the base `Color` for semantics and override through an inline `Style`:

```razor
<MudChip T="string" Variant="Variant.Outlined" Color="Color.Info"
         Style="color: var(--mud-palette-info-lighten); border-color: var(--mud-palette-info-lighten);" />
```

Three things to know:

- **Inline style beats MudChip's own class rules** on specificity, so this works without `!important`.
- **An outlined chip needs both `color` and `border-color`.** `color` paints the tag and — via `currentColor` on the icon SVG — the icon too; `border-color` paints the ring. Setting only one produces a mismatched chip.
- **Read the variable, don't hardcode the hex.** `var(--mud-palette-info-lighten)` follows the theme if the palette is ever restyled; `#5CADFF` does not.

These variables are generated at runtime and are *not* present in the static `MudBlazor.min.css`, so the only reliable way to check a value is to read the computed style on the running page.

## 3. Layout Shell

`Components/Layout/MainLayout.razor`:

```
MudAppBar (fixed, AppbarBackground)
  [logo] [ "enList Portal" / <Section> ] [spacer] [NavMenu ≥ Md | hamburger MudMenu < Md ]
MudMainContent
  MudContainer (MaxWidth.ExtraLarge)
    @Body
```

**There is no navigation drawer.** Navigation lives in the app bar itself. This replaced an earlier responsive `MudDrawer`; the drawer's job on narrow viewports is now done by a `MudMenu` behind a hamburger, switched via a pair of `MudHidden` blocks (`Breakpoint.SmAndDown`, one inverted).

**The breadcrumb is the page title.** Individual pages carry no large heading of their own — `MainLayout` appends `/ <Section>` after the brand text, derived from the current URI by `SectionFor(uri)` and refreshed on `NavigationManager.LocationChanged`. Adding a new top-level page means adding a case there, not adding an `<h1>` to the page.

### 3.1 Navigation (`NavMenu.razor`)

Three links, fixed order:

| Order | Tag | Route | Icon |
|---|---|---|---|
| 1 | Applications | `/` (also `/applications`) | `Icons.Material.Filled.Apps` |
| 2 | Agents | `/agents` | `Icons.Material.Filled.Dns` |
| 3 | Logs | `/logs` | `Icons.Material.Filled.Article` |

Applications is the landing page and first nav item — the fleet-wide "what's running" view is the primary task, ahead of the agent-centric view.

`NavMenu` renders plain `<NavLink>` elements inside a `<nav class="enlist-nav">`, styled by `NavMenu.razor.css`, **not** `MudNavLink` — MudBlazor's nav components assume a drawer context. Its `Vertical` parameter switches between the app-bar row and the stacked form used inside the narrow-viewport `MudMenu`.

Packages and Application Policies are no longer top-level destinations. They are reached per-application, from an application's own card: `/applications/{AppName}/policy` and `/applications/{AppName}/packages`.

### 3.2 Scoped CSS — two gotchas that cost real debugging time

**The bundle must be linked or no `.razor.css` applies at all.** `Components/App.razor` must contain:

```razor
<link rel="stylesheet" href="@Assets["Enlist.Portal.styles.css"]" />
```

Without it every scoped stylesheet in the project silently does nothing — including ones that used to work, because nothing errors. This was an actual defect: `ReconnectModal.razor.css` had been dead for some time before it was noticed.

**`::deep` needs a scope-attributed plain-HTML ancestor.** Blazor stamps the scope attribute (`b-xxxxxxxxxx`) on plain HTML elements a component renders, **not** on the elements a child *component* renders internally. So `::deep .foo` only matches if the `::deep` is anchored to a plain element the component itself emitted. This is why the markup deliberately wraps regions in otherwise-pointless `<nav class="enlist-nav">`, `<div class="enlist-brand">` and `<div class="app-list-wrap">` elements — those wrappers exist to carry the scope.

## 4. Page-Level Patterns

### 4.1 Expand-in-place cards (Applications, Agents)

Both pages present a vertical list of **rounded cards**, one per application or agent, each expanding in place to reveal detail. There is no selection model and no separate detail region.

**These are CSS Grid `<div>`s, not `MudTable`.** That is a deliberate, hard-won choice: a table cell's `border-radius` does **not** reliably clip a painted background even with `overflow: hidden`, so rounded row corners were unachievable in `MudTable` — repeated attempts produced visibly square corners. Ordinary block-level elements clip correctly. The layout lives in `Applications.razor.css` / `Agents.razor.css`:

```css
.app-card { background: var(--mud-palette-surface); border-radius: 12px; overflow: hidden; }
.app-card-row { display: grid; grid-template-columns: 48px 1fr 220px 110px; align-items: center; }
```

If you are verifying rounded corners or any other *painted* result, screenshot it. `document.elementFromPoint` tests hit-testing, not paint, and will happily report success on a square corner.

**Toolbar.** Each list page's controls are absolutely positioned over a `padding-top` reserved on `.app-list-wrap`: a search field top-left (`.app-search`), and `MudFab` actions top-right (`.app-toolbar-right`) — upload (`CloudUpload`) and refresh (`Refresh`) on Applications. Refresh re-runs the page's own `LoadAsync`.

**Search filters into the sub-lists.** The Applications search box matches application names *and* the names of services and jobs within them. A card whose contents match auto-expands, and matches are highlighted with **`MudHighlighter`** — MudBlazor's built-in component. Do not hand-roll a highlighter.

### 4.2 The expanded panels

- `Components/Panels/RunningInstancesTable.razor` — Applications' expanded content. One row per (service-or-job × agent): Name / Type / Agent / Qualified via / State / action. Also renders the per-agent log chips that open `LogTailView`.
- `Components/Panels/AgentRunningApplicationsTable.razor` — Agents' expanded content. Deliberately **observational only**, with no action buttons: an agent's page tells you what is running there, and actions belong with the application.

Both are `MudTable` (a table is right here — these are genuine tabular rows with sortable columns and no rounded-corner requirement).

**Every column but the first carries a fixed pixel width**, e.g. `<MudTh Style="width:280px;">` on the header AND the matching `MudTd`. This is not cosmetic tidying. Each expanded card renders its OWN `MudTable`, and a `MudTable` sizes columns to its own content — so two cards open at once will visibly disagree about where a column starts, purely because one holds a longer application name or one extra chip. It reads as broken alignment and is the first thing anyone notices.

This defect has now been fixed twice: once for the per-service action buttons drifting between cards, and again on the Agents panel when a `container` chip widened one card's State cell and desynchronised it from its neighbour. **If you add a column or a chip to any per-card table, give the column a fixed width.**

**Convention:** any name cell with a description renders it as a second line, `Typo.caption Color="Color.Info"`, directly beneath the name.

### 4.3 The Service/Job type badge

`Components/Shared/InstanceTypeBadge.razor` is the single definition, used by both `RunningInstancesTable` and `ApplicationContentsDialog`. Use it anywhere a service and a job appear in the same list — these two surfaces had already drifted apart once (a colored chip in one, bare text in the other) before it was centralized.

| Type | Icon | Color | Why |
|---|---|---|---|
| `Service` | `Autorenew` (looping arrows) | `Color.Primary` | Runs **continuously** |
| `Job` | `Schedule` (clock) | `Color.Info` → `--mud-palette-info-lighten` (§2.3) | Runs **on a schedule**, idle in between |

The icons are the point: they encode the same distinction that drives the state vocabulary in §7, so the badge reinforces an existing idea rather than introducing a new one. Any value that is neither `Service` nor `Job` (the `Policy conflict` pseudo-row's em dash) renders as plain text — a badge there would imply an instance kind the row does not have.

### 4.4 Tooltips on potentially-disabled elements

**Never use the native `title="..."` attribute on an element that can become `disabled`.** Browsers do not dispatch hover events to a disabled element, so the tooltip silently never appears — an actual defect fixed across several pages during development.

**Convention:** wrap the control in `<MudTooltip Text="...">`, whose own container element receives the hover regardless of the child's `disabled` state. For a tooltip that should appear *only* when disabled, pass `Disabled="@(!isActuallyDisabled)"` on the `MudTooltip` rather than conditionally omitting the wrapper.

### 4.5 Toggles must look like toggles

A state that can be flipped uses a visible **`MudSwitch`** with an accompanying state chip, not a small icon button — an icon toggle was tried and users could not tell the control existed, or which state it was in. The agent scheduling control (`Agents.razor`) is the reference implementation: a `MudSwitch` colored `Color.Success` when on, immediately followed by an `Enabled`/`Disabled` chip, with the tag inline beside it rather than pushed to the far side of the row.

## 5. Command-Gating Conventions

Every button sending an imperative, fire-and-forget command (per-service Start/Stop, per-job Enable/Disable) is disabled — not merely relabeled — whenever the command would have nowhere to go:

- The agent is **stale** (no report within the 5-minute threshold), or
- The application itself isn't `Running` on that agent, so no runner process exists to receive it.

Every such disabled button carries a `MudTooltip` saying *which* of those two it is (§4.4).

**Declarative** state changes — an application policy's desired state, an agent's scheduling flag — are never gated this way. They only write intent to the database, which is always meaningful regardless of whether the target agent is currently reachable.

## 6. Confirmation Dialogs

Destructive or broad-fanout actions use `DialogService.ShowMessageBox(...)`, and the message always names the concrete blast radius rather than asking a generic "are you sure?":

- **Delete** (agent, policy rule, package): states plainly what will happen — and, for an agent, that it will simply reappear on next agent contact, since deletion is registry cleanup rather than data destruction.
- **Disabling a policy rule or an agent's scheduling** (`Services/ApplicationPolicyStateToggler.cs`): enumerates every agent/application actually affected right now, with an explicit caveat that *future* matching agents are affected too — a tag selector's membership is not fixed.

## 7. Status Chip Vocabulary

| Displayed text | Meaning |
|---|---|
| `Running` / `Stopped` / `Starting` / `Restarting` / `Failed` | Application-level state, verbatim from `ApplicationState`. |
| `Running` / `Stopped` / `Starting` / `Stopping` / `Faulted` | Service-level state, verbatim from the runner's `RunnerState`. |
| `Enabled` / `Disabled` | **Job**-level state. Deliberately *not* Running/Paused: a job is scheduled, and idle between firings, so "Running" would wrongly suggest a process is active right now. A job also reads `Disabled` whenever its application isn't running — a job that cannot fire is disabled regardless of which setting caused it. The wire protocol still uses Start/Stop; only the tag follows this framing. |
| `Online` / `Offline` | Derived client-side from an agent's `LastSeenUtc` vs. a 5-minute threshold — not a value the API returns. |
| `Stale` (warning-triangle icon) | The underlying report is older than that same threshold: "this is a last-known value, not confirmed current." Distinct from Offline, which describes the agent rather than one reading. |
| `conflict` (filled, `Color.Error`) | Two or more enabled policy rules disagree for the same agent. See §7.1. |

### 7.1 Conflict is shown at three levels

A policy conflict means the agent will report the application `Failed` rather than resolving it, so it is surfaced wherever a user might otherwise believe the application is fine:

1. **Application card** — a `conflict` chip whose tooltip names the affected agents.
2. **Policy screen** — the offending rules are flagged where they can actually be fixed.
3. **Running-instances table** — the agent's services and jobs are **replaced** by a single "Policy conflict" row. This matters: on a conflict the agent returns before staging anything, so whatever services/jobs its last report listed are stale leftovers from a previous successful run. Listing them would show jobs as `Enabled` that cannot possibly fire.

All three read from one shared definition, `Services/PolicyConflictDetector.cs`. Do not re-derive conflict logic locally — three divergent copies existed before it was consolidated.

### 7.2 Warn on zero

An application with **0 policy rules**, or a policy matching **0 agents**, shows a `WarningAmber` icon rather than a bare "0". Both are silently-does-nothing states that look identical to healthy ones at a glance.

## 8. Responsiveness

- The application/agent card grid has a `min-width` and scrolls horizontally **inside its own container** (`.app-list { overflow-x: auto; }`) rather than letting the page body scroll sideways.
- `MudTable` inside the expanded panels sets a `Breakpoint` so rows collapse to a stacked layout on narrow viewports.
- Navigation collapses from the app-bar row into a hamburger `MudMenu` below `Breakpoint.SmAndDown` (§3).
- `MudContainer MaxWidth="MaxWidth.ExtraLarge"` caps content width on very wide viewports.

## 9. Iconography

Material icons throughout (`Icons.Material.Filled.*`), consistent per concept:

| Concept | Icon |
|---|---|
| Applications | `Apps` |
| Agents | `Dns` |
| Logs | `Article` |
| Policy rules | `Rule` |
| Packages | `Inventory2` |
| Service (type badge) | `Autorenew` |
| Job (type badge) | `Schedule` |
| Upload a package | `CloudUpload` |
| Refresh | `Refresh` |
| Edit tags | `Tag` |
| Delete | `Delete` |
| Start | `PlayArrow` |
| Stop | `Stop` |
| Stale / zero-rules / zero-agents warning | `WarningAmber` |

## 10. Verifying a MudBlazor API before using it

MudBlazor's docs and its actual released surface drift, and a wrong parameter name is a *compile* error at best and a silently-ignored attribute at worst. The convention in this project is to check the real assembly for the version actually referenced:

```bash
# Properties on a component, from the shipped XML docs
grep -o 'P:MudBlazor.MudChip`1\.[A-Za-z]*' \
  ~/.nuget/packages/mudblazor/8.6.0/lib/net8.0/MudBlazor.xml | sort -u
```

This caught `ShowPrevButton` vs. the assumed `ShowPreviousButton`, and confirmed `MudChip<T>` really does expose `Icon`/`IconColor` before §4.3 was built on it. For anything computed at runtime — palette variables especially (§2.3) — read the computed style on the running page instead; it is the only authority.
