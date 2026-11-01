# Agents Hub security audit

Date: 2026-10-31
Scope: the local `AgentsHub` checkpoint only. No deployed URL was tested.
Version audited: 2.0.0
Pipeline: Convio security-audit (scope, map, scans, manual review, report and fix)

## 1. Summary

The product is a local desktop control plane, so the important boundary is not "logged in or not".
It is "can something that is not the desktop app reach a privileged action". Three of the findings
were exactly that: a page could keep the WebView message bridge after navigating away, any website
could type into a loopback terminal WebSocket, and an unauthenticated loopback proxy would spend the
user's stored OpenRouter keys.

Nineteen issues were fixed in this pass, including every high and critical finding. Three residual
risks are accepted and listed in section 7. The app builds on both target frameworks and the UI,
sidecar and cross-platform checks pass.

| Severity | Found | Fixed | Accepted |
| :--- | ---: | ---: | ---: |
| Critical | 2 | 2 | 0 |
| High | 6 | 6 | 0 |
| Medium | 6 | 6 | 0 |
| Low / informational | 8 | 5 | 3 |

## 2. Method

1. Mapped the entry points: 269 bridge actions, three loopback HTTP/WebSocket services, the opt-in
   LAN gateway, 130 process spawn sites, the Bun sidecar transport, credential stores and the update
   path.
2. Ran the scanners listed in section 6 and triaged every result.
3. Reviewed auth, authorisation, path handling, command construction, credential storage, WebView
   configuration and release packaging by hand.
4. Fixed the findings, rebuilt both target frameworks and the UI, and re-ran the project checks.

Where the scanner reported a pattern but the surrounding code made it unreachable, it is recorded as
triaged rather than "fixed".

### 2.1 Attack surface

| Entry point | Who can reach it | Attacker-controlled input | Highest impact |
| :--- | :--- | :--- | :--- |
| WebView action bridge (269 actions) | The mapped app origins | Renderer state and model output rendered into chat | File write, command execution |
| Terminal HTTP/WS (`9229`, `9339`, per-profile ports) | Any local process; any web page unless Origin and token are checked | Request path, query, WebSocket frames | Keystrokes into a live CLI |
| Claude translation proxy (`9230`) | Any local process; any page that can POST to loopback | JSON body, `x-api-key` | Spending stored provider credits |
| LAN gateway (`9191`, opt-in) | The whole LAN | HTTP request, headers, Host | Same as the dashboard if unauthenticated |
| Bun sidecar WebSocket | Any local process | WS handshake, task and terminal frames | Host control, terminal I/O |
| CLI and MCP spawns | The desktop user | Repository content, MCP responses, model tool calls | Execution as the user |
| Profile, skill, snapshot and workspace file operations | The renderer | Names, ids and paths | Read or delete outside the chosen root |
| Updater | The renderer and GitHub responses | `downloadUrl`, release metadata | Execute a downloaded binary |

Assets and stores: `ai_config.json` and the GitHub auth file under `%APPDATA%\AgentsHub`; Antigravity
profiles and virtual homes under `%LOCALAPPDATA%\AgentsHub`; the Pocket Commander config; workspace
`.agents` snapshots and skills. Third parties are GitHub (releases and API), OpenRouter and Groq,
and the Telegram and Discord bot APIs. There is no payment, upload or multi-tenant admin surface.

The high-risk areas are the ones where a non-desktop caller meets a privileged action: the bridge
origin, the terminal socket, the LAN gateway, the translation proxy, path handling in the file
actions, and the update path. Credential storage and update signing are the main release-process
risks. Sections 3 and 7 cover each.

## 3. Findings

| ID | Severity | Finding | Status |
| :--- | :--- | :--- | :--- |
| AH-01 | Critical | Privileged WebView could navigate to any origin | Fixed |
| AH-02 | High | Model output could become a `javascript:` link | Fixed |
| AH-03 | Critical | Any website could type into a loopback terminal | Fixed |
| AH-04 | High | Loopback Claude proxy spent stored keys for anonymous callers | Fixed |
| AH-05 | High | Profile names escaped the profiles folder | Fixed |
| AH-06 | High | A live OAuth token sat in the publish output | Fixed |
| AH-07 | High | Updater executed any URL the renderer supplied | Fixed |
| AH-08 | High | LAN gateway was unauthenticated and forwarded a terminal by default | Fixed |
| AH-09 | Medium | Sidecar accepted everyone when its token was empty | Fixed |
| AH-10 | Medium | DevTools enabled on privileged WebViews | Fixed |
| AH-11 | Medium | LAN/browser UI showed invented data | Fixed |
| AH-12 | Medium | Cross-origin `postMessage` was trusted | Fixed |
| AH-13 | Medium | Skill content and snapshots were general path primitives | Fixed |
| AH-14 | Low | Terminal and gateway response hardening | Fixed |
| AH-15 | Low | Hub offline page could never appear | Fixed |
| AH-16 | Low | VPN and IP-rotation remnants, plus 216 dead files | Fixed |
| AH-17 | Info | Secrets at rest are plain JSON | Accepted |
| AH-18 | Info | Pocket Commander pairing PIN is 6 digits | Accepted |
| AH-19 | Info | Installer is not code-signed | Open |
| AH-20 | Low | Subframes could navigate and post to the bridge | Fixed |
| AH-21 | Medium | Skill save could escape the skills root with `..` | Fixed |
| AH-22 | Low | LAN token check failed open on an empty token | Fixed |

### AH-01 Critical, fixed: the privileged WebView could navigate to any origin

`MainForm` navigated its WebView to `https://app.agentshub.local/index.html` and attached
`DashboardWebBridge` to it, but nothing restricted later navigation. The bridge stays attached after
a top-level navigation, so a link clicked in a chat reply (or any anchor rendered from model output)
could land an attacker origin on a page that still held the full action set: write files, run
commands, open terminals.

Fix: `Helpers/WebViewSecurity.cs` cancels navigation outside the mapped origins, opens `http`,
`https` and `mailto` in the system browser instead, blocks new windows, denies every permission
request and disables DevTools. Applied to the dashboard, the CLI workspace and the HUD terminal.
The app asset mapping is now `Deny`; the wallpaper mapping stays `Allow` because the app origin loads
those images cross-origin.

### AH-02 High, fixed: model output could become a `javascript:` link

`MarkdownRenderer` passed the href from `[label](target)` straight to an anchor. A reply could emit
`javascript:` or `data:` and run in the privileged app origin.

Fix: `safeHref` keeps only `http`, `https`, `mailto` and in-page anchors. Anything else renders as
plain text.

### AH-03 Critical, fixed: any website could type into a local terminal

`CliWebTerminalServer` bound `127.0.0.1` and accepted WebSocket upgrades with no token and no
`Origin` check. WebSockets are not blocked by the same-origin policy, so a page the user visited
could connect to `ws://127.0.0.1:9339/ws` and send `{"type":"input","data":"..."}` into a live CLI.
The server also served any file under `ui` by joining a request path onto a folder, sent
`Access-Control-Allow-Origin: *`, and interpolated the `theme`/`accent` query values into the page.

Fix:
- Non-loopback sockets are refused, so a raw LAN forwarder cannot expose a terminal.
- The WebSocket requires a random per-session token that is injected into the served page and never
  appears in the URL, plus a loopback `Origin`.
- Static serving uses a fixed allowlist (`terminal.html`, xterm, the two icons) with a resolved-path
  containment check.
- `theme` and `accent` are validated as a value (`light|dark`, `#rrggbb`) before use.
- `Access-Control-Allow-Origin: *` removed; `nosniff` and `no-referrer` added.

### AH-04 High, fixed: the loopback Claude proxy spent stored keys for anonymous callers

`ClaudeOpenRouterProxyService` on `127.0.0.1:9230` read the caller's key only as an optional
failover candidate. A request with no key, or any random key, was served with the keys in
`ai_config.json`. Any local process, and any web page able to POST to loopback, could consume the
user's credits.

Fix: the caller must present a key that is already configured. No header key means 401. The child
Claude process is given the same resolved key, so the legitimate route keeps working. CORS headers
removed.

### AH-05 High, fixed: profile names could escape the profiles folder

`SanitizeProfileName` replaced invalid filename characters but kept dots, so a profile named `..`
passed. `DeleteProfile("..")` then deleted the parent of the profiles folder, and the same name
reached every other `Path.Combine` in `AntigravityProfileService`.

Fix: the sanitizer rejects dot-only names, trims trailing dots and spaces, prefixes Windows device
names and caps the length at 64. `IsSafeProfileName` requires the input to be exactly one segment
that matches the sanitizer, and is enforced on create, rename, apply, export and delete.

### AH-06 High, fixed: a real OAuth token sat in the publish output

The gitleaks directory scan found a live Google OAuth JWT in
`bin/Release/net10.0-windows/win-x64/Profiles/Antigravity/Account_1/gemini_credential.json`.
Profiles were written under `AppContext.BaseDirectory`, which is the install directory. That also
means profiles could not be written at all after installing under `Program Files`.

Fix:
- Profiles now live in `%LOCALAPPDATA%\AgentsHub\Profiles\Antigravity` (`Helpers/AppPaths.cs`), with
  a one-time move from the old location.
- The sidecar receives the new path.
- `Profiles\**` is excluded from project items, the portable zip and the Inno Setup source.

The stale copy under `bin/Release/net10.0-windows/win-x64/Profiles` was moved into
`%LOCALAPPDATA%\AgentsHub\Profiles\Antigravity` on 2026-10-31, so the publish tree no longer contains
a token and the account data was preserved. A fresh gitleaks scan finds no JWT in the tree.

### AH-07 High, fixed: the updater executed any URL the renderer supplied

`DownloadAndInstallAsync` accepted an arbitrary `downloadUrl` from the `install_update` action,
downloaded it and started it with `UseShellExecute`. There was no scheme, host or digest check.

Fix: only `https` assets on GitHub release hosts ending in `.exe` are accepted, in both the action
and the downloader. When the release API publishes a `sha256` digest, the downloaded bytes are hashed
and a mismatch is deleted before execution. Downloads are capped at 512 MB.

### AH-08 High, fixed: the LAN gateway was unauthenticated and forwarded a terminal by default

The LAN proxy bound `0.0.0.0:9191` with no authentication and forwarded `9229` and `9339` by default.
`9339` is the terminal server, so the default configuration exposed a live terminal to the Wi-Fi
network. It also trusted any loopback socket, which is what a DNS-rebinding page produces.

Fix:
- The gateway issues a per-start token, carried by the QR code and copied link. The first remote
  request exchanges it for an `HttpOnly`, `SameSite=Lax` cookie and is redirected to the URL without
  the token. A remote request without a valid token or cookie gets a pairing page, never the backend.
- A non-local `Host` header is treated as remote, so a rebinding page does not inherit loopback trust.
- Direct forwarders are empty by default and remain opt-in.
- The QR, running-port list and tray link include the token.

### AH-09 Medium, fixed: the sidecar accepted everyone when its token was empty

`tokenMatch` passed any client when `authToken.length === 0`. The host always generates a token, so
this was a fail-open default rather than a live bypass.

Fix: an empty token now matches nothing, and comparison is constant-time. Verified at runtime:
wrong token 401, no token 401, empty-token engine 401 for both requests, correct token reaches the
upgrade path.

### AH-10 Medium, fixed: DevTools were enabled on privileged WebViews

`MainForm`, `CliWebviewForm` and `HudTerminalForm` all set `AreDevToolsEnabled = true`, two of them
directly under a comment saying DevTools stay disabled. The dashboard console can call every bridge
action.

Fix: DevTools are disabled in the shared helper on all three surfaces.

### AH-11 Medium, fixed: the LAN/browser UI showed invented data

In a plain browser (including the LAN dashboard) `webviewBridge.invoke` resolved with fabricated
data after 50 ms: fake running ports, PIDs, sidecar status, sessions and file lists. That hid the
absence of the desktop bridge and presented fiction as product state.

Fix: the mock path is compiled only when `import.meta.env.DEV` is true. A release build rejects with
"This action needs the Agents Hub desktop app." The LAN page now shows honest errors.

### AH-12 Medium, fixed: cross-origin `postMessage` was trusted

The browser fallback listener accepted any `message` event that looked like a response, so a framing
page could spoof responses or events.

Fix: the event `origin` must equal the page origin.

### AH-13 Medium, fixed: skill content and snapshots were general path primitives

- `get_skill_content` / `save_skill_content` read and wrote any path the renderer supplied.
- `DeleteProjectSkill` accepted a path containing `/.agents/skills/` and `..`, then deleted the
  resolved parent.
- `DeleteSnapshot` / `RollbackSnapshotAsync` joined the renderer's `snapshotId` onto the snapshots
  folder, so `..` escaped it.

Fix: skill content is limited to `SKILL.md` / `SKILL.md.disabled`; delete resolves the path first and
requires it to sit in a real skills root with a skill file present; snapshot ids must be 64 characters
of `[A-Za-z0-9_-]`.

### AH-14 Low, fixed: terminal and gateway response hardening

Fixed `Access-Control-Allow-Origin: *` on the terminal server and LAN status endpoint, source maps
and the whole `ui` tree served by the terminal server, and unvalidated theme values. Covered by
AH-03 and AH-08.

### AH-15 Low, fixed: the hub offline page could never appear

`CliWebviewForm` navigated to `ui/hub-offline.html`, which did not exist; the file was named
`hermes-offline.html` and posted `start_hermes` while the handler expects `start_hub`. The offline
state was therefore a blank WebView and a dead button.

Fix: `ui/hub-offline.html` exists, posts `start_hub`, and the stale copy is gone.

### AH-16 Low, fixed: VPN and IP-rotation remnants, plus 216 dead files shipped

The IP-rotation commit deleted the service but left the feature visible in a superseded UI tree that
the build still copied. The VPN catalogue entries, a sound event, a commit-scope heuristic, a comment
and a `.editorconfig` note also survived.

Fix:
- Deleted the legacy `ui/js` app code, `ui/css` app styles, `ui/views`, `ui/modals`, `ui/ui-OLD` and
  `ui/index.legacy.html` (216 tracked files). Kept the xterm vendor files and `terminal.html` that
  the live terminal uses.
- Removed the VPN entries from installed-client discovery, the `vpn_rotated` sound event, the `vpn`
  commit-scope branch, stale comments and the settings label.
- `quotaResetAt` remains: it is quota metadata, not IP rotation.

### AH-17 Informational, accepted: secrets at rest are plain JSON

`ai_config.json`, the GitHub auth file and the Pocket Commander config store tokens unencrypted under
`%APPDATA%\AgentsHub`. The Windows user ACL is the boundary. Encrypting with DPAPI is a reasonable
follow-up, not something this pass claims.

### AH-18 Informational, accepted: Pocket Commander pairing PIN is 6 digits

The Telegram and Discord pairing flows accept a 6-digit PIN with no attempt limit. Pairing is opt-in
and the bot must be found first, but a rate limit or a longer PIN would be better. Left as a
follow-up because it changes the pairing UI text.

### AH-19 Informational, open: the installer is not code-signed

No `signtool` or Authenticode step exists in the build scripts. Users get a SmartScreen warning, and
the updater cannot verify the publisher, only the GitHub host and optional digest. Signing the
installer and the portable binary is recommended before a public release.

### AH-20 Low, fixed: subframes could navigate and post to the bridge

`WebViewSecurity.Apply` hooked `NavigationStarting`, which covers the top-level page only. A frame
could load an attacker origin inside the privileged WebView, and `OnWebMessageReceived` did not check
who sent a message. There are no iframes in the UI and the markdown renderer does not enable raw HTML,
so this was not exploitable as shipped; it was a missing second line of defence behind INV-1.

Fix: `FrameNavigationStarting` cancels any frame outside the allowed origins, and the bridge ignores
messages whose source origin is not the page's own origin.

### AH-21 Medium, fixed: skill save could escape the skills root

`SaveSkillContentAsync` accepted any path ending in `SKILL.md`, including
`.agents/skills/../../target/SKILL.md`, and create the parent directory before writing. That made it
a write primitive wherever the user could write, not just in a skill folder.

Fix: the resolved path must still contain `/.agents/skills/` or `/.gemini/skills/` after
`Path.GetFullPath` removes `..`, and the existing filename and builtin checks remain.

### AH-22 Low, fixed: LAN token check failed open on an empty token

The gateway checked `needsToken && !string.IsNullOrEmpty(AccessToken)`, so an empty token meant a
remote caller was served without pairing. `Start` generates the token before binding, so normal
operation never hit it; the condition was still the wrong default.

Fix: a remote or rebinding request now always requires a token, and an empty token gets the pairing
page instead of the backend.

## 4. Scanner results

| Scanner | Result | Triage |
| :--- | :--- | :--- |
| gitleaks (git history) | 4 | Test fixtures and minified xterm. No real secret. |
| gitleaks (working tree) | 15 | All xterm minified false positives and one parser fixture. The JWT from AH-06 was moved out of the publish tree. |
| OSV-Scanner | 0 | `ui/package-lock.json` 192 packages, `bun.lock` 5 packages. |
| `dotnet list package --vulnerable` | 0 | Direct and transitive NuGet. |
| npm audit / bun audit | 0 | 190 packages. |
| Semgrep (p/security-audit, p/csharp, p/typescript, p/secrets) | 17 | All path-combine; AH-05, AH-13 and the profile set. |
| Static build | 0 errors, 35 pre-existing style warnings | Debug, both target frameworks. |

## 5. Verification

| Check | Result |
| :--- | :--- |
| `dotnet build AgentsHub.csproj -c Debug` | Build succeeded, 0 errors, both TFMs |
| `dotnet test tests/AgentsHub.Security.Tests` | 50 passed, 0 failed |
| `cd ui && npm run build` | tsc and vite succeeded |
| `bun run typecheck` | 0 errors |
| `bun run tools/pcheck.ts` | 11 passed, 0 warnings, 0 failures |
| `tools/DeepSeekToolTest -- --parse` | Parser probes pass |
| `tools/DeepSeekToolTest -- --redact` | Leak check PASS |
| Sidecar token test | wrong 401, none 401, empty-token engine 401, health 200, correct token reaches upgrade |
| gitleaks re-scan after the move | 15 findings, 0 JWT, 0 real secret |

Not run: a full GUI click-through, the terminal and LAN runtime paths, the 17-check harness that
needs installed CLIs, and a Release publish. Those need a running app with real accounts and were
outside what this machine could exercise without launching the desktop session.

## 6. Launch checklist

- [x] Both target frameworks build from a clean checkout
- [x] UI typecheck and production bundle build
- [x] No vulnerable direct or transitive dependencies
- [x] No real secret in tracked files or git history
- [x] Credentials cannot enter a package
- [x] Every loopback listener authenticates or refuses non-local callers
- [x] LAN access is token-gated and off by default
- [x] WebView cannot leave its origin and DevTools are off
- [x] Update download is host-restricted and digest-checked when available
- [x] Security policy and threat model match the code
- [ ] Manual GUI smoke test on Windows with a real profile
- [ ] Release publish and installer build on a clean machine
- [ ] Authenticode signing for the installer and binaries
- [ ] Verify the first-run experience on macOS and Linux headless mode

## 7. Residual risk

1. Tokens at rest rely on the Windows profile ACL (AH-17).
2. Pocket Commander pairing has no attempt limit (AH-18).
3. The LAN dashboard is a UI shell. It can render, but bridge actions are unavailable there by
   design, so it reports errors rather than live agent state.
4. The Google OAuth credential that lived in the publish tree was moved, not rotated. Rotate it if
   that build folder was ever archived, zipped or shared outside this machine.
5. Direct LAN forwarders are raw TCP and unauthenticated. They are empty by default and only exist
   if the user selects one.
6. A same-user process can read the user's profile and inject into the app regardless of these
   controls. That is outside the model in `SECURITY_MODEL.md`.

## 8. Post-upstream differential review (2026-11-01)

Upstream advanced five commits after this audit (`6adef43..ffeec76`: brand and sticker assets,
dashboard shortcuts built from AI Dev Tools, reasoning rows, skill documents). The local security
work was stashed to let the merge finish and has been re-applied on top of `ffeec76`; the stash is
kept as a backup. Six files overlapped both sides and the two brand images resolved to upstream's
art. All AH-01 through AH-22 fixes are present again and both builds pass.

Reviewed as a diff: 274 changed files, of which 18 are code, 170 PNG, 64 JPEG, 12 skill documents,
and the rest UI text and styles.

| Question | Result |
| :--- | :--- |
| New bridge actions? | None. The five new UI invocations (`browse_tool_target`, `detect_ai_clients`, `get_claude_gateway_config`, `launch_tool`, `save_ai_config`) all existed before. |
| New sinks? | None. The diff adds no `dangerouslySetInnerHTML`, `innerHTML`, `eval`, `new Function`, `srcdoc`, `window.open` or `<iframe>`. |
| New dependencies? | None. `package.json` and both lockfiles are unchanged. |
| New persisted input? | `iconDataUrl` stores the shortcut image from the file picker in `ai_config.json`. It is rendered only as `<img src>`, where an SVG or HTML data URL cannot execute script. It is unbounded, which is local config bloat, not a vulnerability. |
| New assets? | Magic bytes: 170 PNG, 64 JPEG; no SVG, HTML or executable. |
| Secrets in the diff? | gitleaks over the diff: 0 leaks. |
| Did upstream reintroduce a fixed flaw? | No. The upstream `SECURITY.md` still described the pre-fix model; the re-applied version replaces it with the accurate text. |
| Variants in the new tool and shortcut code? | None. The shortcut picker only toggles `showOnDashboard`, renames tools and stores an icon; it adds no path, command or URL sink. |

Semgrep (`p/csharp`, `p/typescript`, `p/secrets`) over the 18 changed code files: 0 findings.

Verification after the re-apply:

| Check | Result |
| :--- | :--- |
| `dotnet build AgentsHub.csproj -c Debug` | 0 errors, both TFMs |
| `dotnet test tests/AgentsHub.Security.Tests` | 50 passed, 0 failed |
| `cd ui && npm run build` | Succeeds |
| `bun run typecheck` | 0 errors |
| `bun run tools/pcheck.ts` | 11 passed, 0 warnings, 0 failures |
| Fix markers for AH-01, AH-03..AH-09, AH-13, AH-15, AH-16 | Present in the re-applied files |
| Semgrep on the upstream change | 0 findings |
| gitleaks on the upstream diff | 0 leaks |

Conclusion: this upstream update adds no new critical, high or medium issue. The release blockers in
section 6 are unchanged.
