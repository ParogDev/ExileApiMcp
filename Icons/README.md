# ExileApiMcp icon set

Icons for the MCP server identity, its tools, apps/resources and prompts (MCP 2026-07-28 `icons`).
Hosts show them in tool lists, app tabs and connector settings, usually at 16-48 px.

Each icon is an SVG in two variants: `<Name>-light.svg` is drawn for a **light host background**,
`<Name>-dark.svg` for a dark one. `Icons.cs` holds the same SVGs as `data:image/svg+xml;base64,...`
constants (`IconSet.<Name>Light` / `IconSet.<Name>Dark`) plus `IconSet.<Name>`, the two-entry
`IList<Icon>` with `Theme = "light"` / `"dark"`, `MimeType = "image/svg+xml"`, `Sizes = ["any"]`.

## The set

| Icon | Used for | Drawing |
|---|---|---|
| `Server` | Server identity (`Implementation.Icons`) | Viewfinder brackets around a reticle ring with a live dot: the overlay lens through which an agent watches the game. |
| `PlayerStats` | Player stats app / `show_player_stats`, `stats_*` tools | A figure beside three stat bars; the middle bar is the selected stat. |
| `DataExplorer` | Live object-model tree / `show_data_explorer`, `explore_object` | A root node with two children on elbow connectors; the lower child is the selected node. |
| `MemoryView` | Read-only memory and struct viewer / `show_memory_view`, `memory_*` | A chip with eight pins and a highlighted cell. |
| `HudPerformance` | Frame timeline and performance app / `hud_health_report`, `hud_plugin_perf` | Frame-time bars on a baseline with one spike. |
| `Timeline` | Cross-layer event timeline | Three lanes of events (server state, UI, area/stats) crossed by one clock cursor. |
| `Guide` | Guided experiments and the in-game guide card / `guided_experiment`, `await_change`, `highlight` | A compass with the needle pointing north-east. |
| `Knowledge` | Knowledge packs / `knowledge` tool and `exile://knowledge/...` | A closed book with a bookmark. |

## Design rules

- 24x24 viewBox, content inside the 2..22 square (2 px margin), so a 16 px render keeps a 1 px margin.
- One stroke weight (2) with round caps and joins; corner radius 1-2 on rectangles. Filled shapes have no stroke.
- Two colours only. Foreground: `#1F2328` (light variant) / `#E6EDF3` (dark). Accent: `#C96A0F` (light) / `#F2A33C` (dark).
  The accent marks the one "live" or "selected" element per icon and is used at most once per icon.
- Every icon must read at 16 px: at that size one grid unit is 0.67 px, so no detail thinner than 2 units,
  no outlined shape smaller than 5 units across (outlined boxes collapse: use a filled block instead).
- No text, no gradients, no filters, no external references, no scripts, no `currentColor` (hosts render the
  data URI as an image, where `currentColor` is black on both themes). Under 1 KB each.
- Original artwork: nothing that imitates Path of Exile or Grinding Gear Games branding.

## Using the icons

```csharp
// Server identity
o.ServerInfo = new Implementation { Name = "ExileApi MCP", Title = "Path of Exile HUD", Version = Version, Icons = IconSet.Server };

// Programmatic tool / resource / prompt creation (both themes)
McpServerTool.Create(method, new McpServerToolCreateOptions { Icons = IconSet.PlayerStats });

// Attribute form takes one URI (pick the theme most hosts use, or both via the options above)
[McpServerTool(Name = "show_player_stats", IconSource = IconSet.PlayerStatsLight)]
```

## Regenerating

The SVG files are the source; `Icons.cs` is generated from them. After editing or adding an SVG pair, run
from this folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\regen.ps1
```

It base64-encodes every `<Name>-light.svg` / `<Name>-dark.svg` pair (UTF-8, trailing newline stripped) and
rewrites `Icons.cs` with `<Name>Light`, `<Name>Dark` and `<Name>` members, known icons first and any new ones
after them in name order. Then `dotnet build` the project. To check a change at the sizes hosts use, open the
SVGs in a browser at 16, 24 and 48 px on white and on `#0D1117`; for a true 16 px check draw the SVG onto a
16x16 canvas and show it with `image-rendering: pixelated`, because screenshots of a scaled window hide
the real rasterization.
