# Icon brief, fourth edition

`New2` is cut and wired. This file now records where every icon renders and what is genuinely
still open, so nothing here asks for art that already exists.

Cut by `tools/convert-new.py` (the `NEW` folder) and `tools/convert-new2.py` (the `New2` folder).

## What is in the app

In `ui/src/assets/icons`, all referenced through `ui/src/lib/toolIcons.ts` unless noted:

| group | count | where it renders |
|---|---|---|
| `engines` | 8 | new-conversation tiles, dev tool rows, shortcuts |
| `sections` | 8 | Settings left nav |
| `tools` | 12 | tab headers, dashboard cards, dev tools, fallbacks |
| `ai` | 7 | chat header, avatars, dev tool rows |
| `actions` | 11 | refresh, add, search, copy, download, external, key, lock, pencil, trash across Settings, Profiles, Dev Tools and Dashboard; `close` in four modal headers |
| `mascot` | 2 | chat empty state, assistant avatar |
| `brand` | 2 | `logo` in the header mark, favicon and app icon; `logo-white` in the Windows tray |
| `objects` | 1 of 15 | `browser` on the dashboard hosts card |
| `states` | 0 of 4 | mapped, no home yet |

The 14 idle `objects` icons and 4 `states` icons stay mapped on purpose: every remaining slot for
them is 14 px or less, where a cut sticker turns into a blue smudge. They are waiting for a slot
with room, not redrawing.

## Landed in this batch

From `assets/AgAssets/New2`:

- `sections/appearance`: palette and brush, no plate. Settings left nav.
- `states/running`: play triangle on empty canvas. Mapped, still unplaced.
- `objects/package`: blue box, no warm cardboard. Mapped, still unplaced.
- `tools/document`: redrawn document with ink rules. Dev tool fallback and the `document` sticker.
- `actions/close`: thick X, wired in the Add Runner Profile, MCP & Skills, Git Diff and Project
  Workspace modal headers at 16 px.
- `tools/wifi`: three arcs, no slash. Settings, Remote Access, on state.
- `brand/logo-white`: flat white mark. `assets/tray.ico` is generated from it and the tray uses it,
  because the blue tile disappears against a dark taskbar.
- `assets/app-3d.png` and `assets/cli-3d.png`: replaced with the new plate.

## Still open

- `tools/wifi-off` is the older blue disc, so the on and off states in the Remote Access header do
  not sit together cleanly. The off mark is the one that needs redrawing now: same three arcs, no
  disc, one slash.
- `assets/installer/wizard-large.bmp` and `wizard-small.bmp` still carry the old hexagonal mark,
  and `AgentsHub.iss` does not reference them, so the installer currently shows no logo at all.
  Either wire them up with redrawn art or delete them.
- Nothing has been seen inside WebView2 yet. The evidence for this batch is the source files and a
  clean UI build.

## The spec these follow

Matches the earlier deliveries, so any later piece drops into the same set.

- Canvas 1024x1024, background flat `#050505`, subject at 76 to 84 percent, centred, equal margins.
- Fill `#3FB8F5`, deep shade `#0E6B9C`, one highlight `#9BE0FF` at most, ink `#0B2030`, white
  details.
- Continuous white outline 22 to 26 px wide with a 6 to 8 px ink line inside it, one unbroken
  trace around the silhouette.
- Minimum stroke 14 px so it survives being drawn at 20 to 24 px.
- No sparkles, dots, dashes, floating decorations, shadows, glow, plates, plates-with-borders,
  warm colours, text or multiple subjects.
- One icon per file, named as its path.
