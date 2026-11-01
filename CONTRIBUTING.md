# Contributing to Agents Hub

Thank you for your interest in contributing to Agents Hub! This document provides instructions and guidelines for opening issues, developing features, and submitting pull requests.

---

## Code of Conduct & Expectations

- **Focused Changes**: Keep pull requests focused on a single feature, bug fix, or optimization.
- **Maintain High Quality**: All C# code must build cleanly with `dotnet build`, with **0 errors** and no new warnings.
- **No Fluff or AI Slop**: Keep code comments and user interface copy clear, direct, and human. Avoid generic AI buzzwords or filler prose.
- **Iconography**: Sticker artwork lives in `ui/src/assets/icons/<group>/` and is imported through `ui/src/lib/toolIcons.ts`. Use `lucide-react` for small glyphs that must follow the text colour. No emoji in interface controls.
- **Zero Secrets Policy**: Never commit API keys, personal access tokens, credentials, or private machine paths to version control. Profile data belongs under `%LOCALAPPDATA%\AgentsHub`, never in the repository or a publish folder.

---

## Development Setup

### Prerequisites
- **Operating System**: Windows 10 or 11 (x64) for the desktop app. The engine sidecar and the UI build also run on macOS and Linux.
- **.NET SDK**: [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Node.js**: [Node.js 18+](https://nodejs.org/) with npm, used to build the React UI
- **Bun**: [Bun 1.x](https://bun.sh/) for the TypeScript sidecar engine and the cross-platform checks
- **Git**: [Git for Windows](https://git-scm.com/)
- **Inno Setup 6**: [Inno Setup](https://jrsoftware.org/isdl.php), only needed to compile installers

### Getting Started

1. **Fork and Clone** the repository:
   ```powershell
   git clone https://github.com/ZerroDevs/AgentsHub.git
   cd AgentsHub
   ```

2. **Build the UI first**. The app maps `ui/dist` into the WebView, and `dotnet run` without a build shows a blank window:
   ```powershell
   cd ui
   npm install
   npm run build
   cd ..
   ```

3. **Restore dependencies and build the app**:
   ```powershell
   dotnet build AgentsHub.csproj
   ```

4. **Run in development mode**:
   ```powershell
   dotnet run
   ```
   For UI work, `cd ui && npm run dev` gives you hot reload in a browser. The desktop bridge is absent there, so actions that need the host report that they need the app.

---

## Project Structure & Architecture

- **`Program.cs` / `MainForm.cs`**: Single-instance mutex guard, WinForms shell, WebView2 initialization, system tray icon and state mark, and global hotkey hooks (`RegisterHotKey`).
- **`CliWebviewForm.cs`**: Detached standalone workspace window with dual-pane split view support.
- **`HudTerminalForm.cs`**: Quake-style drop-down sliding overlay terminal.
- **`Helpers/`**: Cross-cutting helpers, including `AppPaths` (user data locations), `NavigationPolicy` and `WebViewSecurity` (what a privileged WebView may load).
- **`Services/`**: Background services and business logic:
  - `DashboardWebBridge.cs`: Central bidirectional JSON-RPC bridge between WebView2 JavaScript and C#.
  - `CliManagerService.cs`: Manages agent execution, PIDs, and ConPTY pseudoconsole streams.
  - `LanProxyService.cs`: Opt-in LAN gateway for phones and other devices, guarded by a pairing token.
  - `CliWebTerminalServer.cs`: Loopback terminal pages and their WebSocket, guarded by a per-endpoint token.
  - `AntigravityProfileService.cs`: Multi-account credential isolation and VirtualHome directory sandboxing.
  - `ClaudeOpenRouterProxyService.cs`: Anthropic Messages to OpenAI translation gateway proxy.
- **`src/`**: Bun sidecar engine (`engine.ts`, `server/`, `session/`, `tools/`), built by `build.ts` for all six architectures.
- **`ui/`**: React and TypeScript single-page app served inside WebView2:
  - `src/views/`: Screens such as `DashboardView.tsx`, `SettingsView.tsx`, `ProfilesView.tsx`.
  - `src/components/`: Panels, modals and shared controls.
  - `src/lib/toolIcons.ts`: Every sticker and brand mark, mapped to one place.
  - `src/assets/icons/`: Cut artwork, one file per icon.
  - `dist/`: Vite build output, which is what the app actually loads.
- **`tests/AgentsHub.Security.Tests/`**: xUnit project covering the navigation policy, profile names, update URLs and the other pure security helpers.
- **`tools/`**: Build, packaging and asset scripts, plus `pcheck.ts` for cross-platform checks.

---

## Coding Standards

### C# / Backend
- **Modern C# 12+ Features**: Use collection expressions (`[...]`), pattern matching, primary constructors, and expression-bodied members where appropriate.
- **Safe JSON Parsing**: In `DashboardWebBridge.cs`, use `JsonElement.TryGetProperty()` and guard against missing or malformed fields.
- **Process Safety**: Always ensure process termination uses process tree walking or job objects so that child CLI workers are never orphaned.
- **Trust Boundaries**: Anything a page, a model reply, or a loopback caller controls is untrusted input. Validate paths before use, compare tokens in constant time, and prefer failing closed.
- **Warnings**: Verify that `dotnet build` outputs no new warnings.

### Frontend (TypeScript / React)
- **TypeScript and React**: Write typed components. Keep new dependencies out unless the feature genuinely needs one.
- **Relative Assets**: `vite.config.ts` sets `base: './'` because the UI is served from a file-backed virtual host. Import assets through a module instead of writing an absolute `/icons/...` URL.
- **Styling**: Use Tailwind utilities and the theme variables (`var(--bg-card)`, `var(--text-main)`, `var(--accent)`), not hardcoded colors, so light and dark both work.
- **Bridge Calls**: Go through `src/services/webviewBridge.ts`. Actions that need the host must show an error in a plain browser rather than invented data.

---

## Before You Open a Pull Request

```powershell
dotnet build AgentsHub.csproj                 # both target frameworks
dotnet test tests/AgentsHub.Security.Tests    # security helpers
cd ui; npm run build; cd ..                   # UI compiles and bundles
bun run typecheck                              # sidecar types
bun run tools/pcheck.ts                        # cross-platform script and path checks
```

There is no CI on this repository yet, so these checks are what a reviewer has to go on. Run them and paste the results.

---

## Commit Guidelines

We follow the [Conventional Commits](https://www.conventionalcommits.org/) specification:

```text
<type>(<scope>): <short imperative description>

[optional body explaining intent and motivation]
```

### Commit Types
- `feat`: A new user-facing feature or capability.
- `fix`: A bug fix or correction.
- `refactor`: Code change that neither fixes a bug nor adds a feature.
- `perf`: Performance improvement.
- `docs`: Documentation updates or additions.
- `style`: Formatting, spacing, or CSS styling adjustments.
- `chore`: Maintenance tasks, build script updates, or dependency version bumps.

---

## Pull Request Process

1. **Create a Feature Branch**:
   ```powershell
   git checkout -b feat/my-new-feature
   ```

2. **Test Your Changes**:
   - Run the check list above.
   - Exercise the screen you touched in WebView2, in both light and dark.
   - For anything touching the bridge, the terminal server, the LAN gateway or the update path, describe how you tested it, because those paths have no automated coverage.

3. **Submit the Pull Request**:
   - Provide a clear summary of what the PR accomplishes and why.
   - Include screenshots or GIFs for visual changes where applicable.
   - Note any configuration or registry changes introduced.

---

## Security Inquiries

For vulnerability disclosures or sensitive security concerns, please refer to [SECURITY.md](SECURITY.md) instead of creating a public issue.
