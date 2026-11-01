# Security review

Date: 2026-10-31 · Version: 2.0.0 · Full detail: `SECURITY_AUDIT_2026-10-31.md` · Model: `SECURITY_MODEL.md`

Scope: the local Agents Hub repository, its Windows desktop app and macOS/Linux headless server, the
loopback services and the opt-in LAN gateway. No deployed URL was tested. Ownership confirmed by the
request. No compliance regime applies.

## Verdict

All critical and high findings are fixed and verified. Nineteen findings were fixed, two are accepted
with a review date, and one (Authenticode signing) is an open release-process item, not a code defect.
No vulnerable dependency, committed secret or live token in the tree was found on re-scan.

## Findings

| ID | Severity | Where | Evidence | Impact | Fix | Status |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| AH-01 | Critical | `MainForm.cs`, `CliWebviewForm.cs`, `HudTerminalForm.cs`, `Helpers/WebViewSecurity.cs` | Bridge stayed attached after top-level navigation | A chat link could land an attacker origin on the privileged action set | Navigation and new windows are cancelled; external links open in the browser; DevTools and permissions denied | Fixed |
| AH-02 | High | `ui/src/components/chat/MarkdownRenderer.tsx` | Href used verbatim | `javascript:` or `data:` reply executes in the app origin | `safeHref` allows only http, https, mailto and in-page anchors | Fixed |
| AH-03 | Critical | `Services/CliWebTerminalServer.cs`, `ui/terminal.html` | Unauthenticated loopback WebSocket accepted any Origin | Any website could type into a live terminal | Loopback-only socket, loopback Origin, per-session token, fixed asset allowlist, no wildcard CORS | Fixed |
| AH-04 | High | `Services/ClaudeOpenRouterProxyService.cs`, `Services/CliManagerService.cs` | Stored keys used when the caller sent none or a wrong one | Any local process or page could spend provider credits | Caller must present a configured key; no key means 401; CORS removed | Fixed |
| AH-05 | High | `Services/AntigravityProfileService.cs` | Sanitizer kept dots; `..` passed | Delete or write outside the profiles folder | Dot-only, trailing-dot and device names rejected; exact-segment check on every profile operation | Fixed |
| AH-06 | High | `Helpers/AppPaths.cs`, `AgentsHub.csproj`, `tools/BuildTargets.ps1`, `AgentsHub.iss` | Live Google OAuth JWT in `bin/Release/.../Profiles` | A publish or zip could ship an account credential | Profiles moved to `%LOCALAPPDATA%`; project, zip and installer exclude them; the stale copy was relocated | Fixed |
| AH-07 | High | `Services/UpdateService.cs`, `Services/WebBridge/SettingsActions.cs` | Renderer-supplied URL was downloaded and executed | Arbitrary code execution | HTTPS only, exact GitHub host allowlist, `.exe` suffix, SHA-256 when published, 512 MB cap | Fixed |
| AH-08 | High | `Services/LanProxyService.cs`, `Services/WebBridge/LanActions.cs`, `Services/WebBridge/DashboardWebBridge.cs`, `MainForm.cs` | Gateway had no auth and forwarded the terminal by default | Live terminal exposed to the Wi-Fi network | Per-start pairing token, HttpOnly cookie, Host check, no direct ports by default | Fixed |
| AH-09 | Medium | `src/server/transport.ts` | Empty token matched every client | Fail-open sidecar default | Empty token matches nothing; constant-time compare | Fixed |
| AH-10 | Medium | `Helpers/WebViewSecurity.cs` and the three WebView forms | `AreDevToolsEnabled = true` | Bridge driven from the console | DevTools disabled on every app WebView | Fixed |
| AH-11 | Medium | `ui/src/services/webviewBridge.ts` | Browser fallback returned fabricated state | LAN users saw fiction as product state | Mock path compiled only in dev; release rejects with an honest error | Fixed |
| AH-12 | Medium | `ui/src/services/webviewBridge.ts` | Any `message` event accepted | A framing page could spoof bridge responses | Event origin must equal the page origin | Fixed |
| AH-13 | Medium | `Services/McpSkillsService.cs`, `Services/WorkspaceSnapshotService.cs` | Renderer paths and snapshot ids joined without containment | Read, write or delete outside the chosen root | Skill filename allowlist, resolved skills-root check, snapshot id charset and length check | Fixed |
| AH-14 | Low | `Services/CliWebTerminalServer.cs`, `Services/LanProxyService.cs` | Wildcard CORS, source maps, unvalidated theme values | Minor information exposure and injection surface | CORS removed, fixed asset list, theme and accent validated | Fixed |
| AH-15 | Low | `ui/hub-offline.html`, `CliWebviewForm.cs` | Page name and action did not match the handler | Offline state was a blank page and dead button | Page exists and posts `start_hub` | Fixed |
| AH-16 | Low | legacy `ui` tree, `Helpers/InstalledClients.cs`, `Services/SoundpackAudioService.cs`, `Services/GitHubService.cs`, `Services/DependencyCheckerService.cs`, `.editorconfig`, settings views | Superseded IP-rotator UI and VPN entries still shipped | Removed features stayed visible and 216 dead files shipped | Legacy tree deleted; VPN/IP-rotation strings removed | Fixed |
| AH-17 | Info | `ai_config.json`, GitHub auth, Pocket Commander config | Tokens stored as plain JSON | Same-user read only; ACL is the boundary | Accepted; DPAPI is a follow-up | Accepted, review 2027-01-31 |
| AH-18 | Info | `Services/PocketCommanderService.cs` | 6-digit pairing PIN, no attempt limit | Brute force needs the bot to be found first | Accepted; rate limit or longer PIN is a follow-up | Accepted, review 2027-01-31 |
| AH-19 | Info | `AgentsHub.iss`, build scripts | No Authenticode step | SmartScreen warning; publisher unverified | Sign installer and portable binary before public release | Open |
| AH-20 | Low | `Helpers/WebViewSecurity.cs`, `Services/WebBridge/DashboardWebBridge.cs` | Frame navigation and message sender were unchecked | Defence in depth behind AH-01; no iframes or raw HTML today | `FrameNavigationStarting` cancelled; bridge requires same-origin sender | Fixed |
| AH-21 | Medium | `Services/McpSkillsService.cs` | `SKILL.md` filename check accepted `..` | Write outside a skill folder | Resolved path must stay under `.agents/skills` or `.gemini/skills` | Fixed |
| AH-22 | Low | `Services/LanProxyService.cs` | Token condition skipped when the token was empty | Remote caller served unpaired if startup left it unset | Empty token now gets the pairing page | Fixed |

## Verification

| Check | Result |
| :--- | :--- |
| `dotnet build AgentsHub.csproj -c Debug` | 0 errors, 35 pre-existing style warnings, both TFMs |
| `dotnet test tests/AgentsHub.Security.Tests` | 50 passed, 0 failed |
| `cd ui && npm run build` | TypeScript and Vite production build succeed |
| `bun run typecheck` | 0 errors |
| `bun run tools/pcheck.ts` | All script pairings and path checks pass |
| gitleaks (history) | 4 hits, all test fixtures or minified xterm |
| gitleaks (working tree, after the move) | 15 hits, all xterm false positives; 0 JWT |
| OSV-Scanner, `dotnet list package --vulnerable`, npm/bun audit | 0 vulnerable packages |
| Semgrep | 17 path-combine warnings, all triaged into AH-05/AH-13/AH-21 |
| Sidecar token runtime probe | Wrong 401, missing 401, empty-token engine 401, correct token reaches upgrade |
| Parser and redaction probes | Pass |

Not run: GUI click-through, the 17-check harness that needs installed CLIs, a Release publish, and a
real macOS/Linux first run. Those need a desktop session, real accounts or another machine.

## Post-upstream re-apply (2026-11-01)

The five commits merged after this review were audited as a diff: 274 files, 18 of them code, with no
new bridge action, dangerous sink, dependency or asset type. Semgrep reported 0 findings on the
changed code and gitleaks found no secret in the diff. The stashed audit work was re-applied on top
of `ffeec76`; every fix is present and the build, UI bundle, typecheck and pcheck pass again. Detail
is in section 8 of `SECURITY_AUDIT_2026-10-31.md`.

## Release blockers left

1. Authenticode signing for the installer and binaries (AH-19).
2. A manual GUI smoke test on Windows with a real profile.
3. A Release publish and installer build on a clean machine.
4. First-run verification on macOS and Linux headless mode.
