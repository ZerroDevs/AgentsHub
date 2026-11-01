# Security Model

Last reviewed: 2026-10-31 · Applies to: this repository

This file tells a reviewer or scanner what Agents Hub trusts, which properties must hold, and which
findings are reportable. It is policy context, not a disclosure policy; report vulnerabilities through
`SECURITY.md`.

## System and Scope

Agents Hub is a single-user desktop control plane for AI coding agents. The C# host owns a WebView2
UI, spawns CLIs and ConPTY terminals, and exposes a JSON action bridge to the UI. A Bun sidecar runs
tasks and terminals for the host over a token-authenticated loopback WebSocket. The app also runs a
headless server mode on macOS and Linux (`Program.RunUnixHost`).

Exposure is local by default. Three loopback HTTP/WebSocket services exist (terminal, Claude
translation proxy, sidecar); one opt-in LAN gateway can bind `0.0.0.0` when the user turns it on. No
hosted service, public URL or multi-tenant data store is part of this repository.

Assets: provider credentials, per-profile Antigravity OAuth credentials and virtual homes, the user's
workspace and git remotes, the ability to launch processes, and the LAN pairing token.

## Threat Model and Trust Boundaries

| Boundary | Trusted side | Untrusted side |
|---|---|---|
| WebView message bridge | The mapped `app.agentshub.local` / `theme.agentshub.local` pages | Any other origin; model output rendered into chat |
| Loopback HTTP/WS | The desktop process that generated the token | Web pages, other local processes |
| LAN gateway | A device holding the pairing cookie or token | The rest of the LAN; DNS-rebinding pages |
| Sidecar WS | The host presenting the startup token | Every other socket |
| Model tool calls | The approval card the user clicks | Model text, repository content, MCP responses |
| Release assets | `https://github.com/ZerroDevs/AgentsHub` release assets | Any other URL or redirect target |

Attacker-controlled inputs are model output, repository files, MCP responses, HTTP request paths,
query values and headers on the loopback listeners, the LAN gateway's request stream, and update
metadata from GitHub. The same user's other processes are not a boundary: they can read the profile.

## Security Invariants

- **INV-1** A WebView holding the message bridge never displays a non-app origin. Enforced by:
  `WebViewSecurity.Apply` in `Helpers/WebViewSecurity.cs` (dashboard, CLI workspace, HUD).
- **INV-2** Model output cannot become a `javascript:`, `data:` or `file:` link. Enforced by:
  `safeHref` in `ui/src/components/chat/MarkdownRenderer.tsx`.
- **INV-3** No loopback listener accepts a non-loopback socket, except the LAN gateway, which
  requires its pairing token. Enforced by: `CliWebTerminalServer.HandleRequestAsync` and
  `LanProxyService.HandleClientAsync`.
- **INV-4** The terminal WebSocket requires both a loopback `Origin` and a per-session token.
  Enforced by: `CliWebTerminalServer.IsAllowedOrigin` and `CliWebTerminalServer.HasValidToken`.
- **INV-5** The translation proxy never spends a stored key for an unauthenticated caller.
  Enforced by: `ClaudeOpenRouterProxyService.HandleMessagesRequestAsync`.
- **INV-6** A profile name resolves to exactly one directory under the profiles root. Enforced by:
  `AntigravityProfileService.IsSafeProfileName`.
- **INV-7** Credentials and virtual homes live under the user's local application data, never in the
  install or publish directory. Enforced by: `AppPaths.AntigravityProfilesDir` plus the `Profiles`
  exclusions in `AgentsHub.csproj`, `tools/BuildTargets.ps1` and `AgentsHub.iss`.
- **INV-8** An installer is downloaded only over HTTPS from a GitHub release host and is hashed
  against the published digest when one exists. Enforced by: `UpdateService.IsTrustedReleaseUrl` and
  `UpdateService.DownloadAndInstallAsync`.
- **INV-9** The sidecar accepts no client when its token is empty, and compares tokens in constant
  time. Enforced by: `tokenMatch` and `safeEqual` in `src/server/transport.ts`.
- **INV-10** Skill content and workspace snapshots stay inside their own roots. Enforced by:
  `McpSkillsService.SaveSkillContentAsync`, `McpSkillsService.DeleteProjectSkill` and
  `WorkspaceSnapshotService`.
- **INV-11** DevTools are disabled on every app-owned WebView. Enforced by: `WebViewSecurity.Apply`
  and the three WebView forms.

## Finding Classes

| Class | Invariant | Reportable | Severity context |
|---|---|---|---|
| A non-app origin can invoke a bridge action (write, run, terminal) | INV-1, INV-11 | Yes | Critical when it reaches file write or command execution |
| An unauthenticated network or browser path reaches a terminal, shell or file write | INV-3, INV-4 | Yes | Critical from the LAN; high from loopback without a token |
| Path traversal from a profile, skill, snapshot, repository or attachment name | INV-6, INV-10 | Yes | High when it reaches delete or write outside the chosen root |
| Credentials in a build artifact, log, diagnostic export or git history | INV-7 | Yes | High; critical if the token is live and broadly readable |
| Execution of an unverified downloaded binary | INV-8 | Yes | High |
| Cross-origin `postMessage` accepted as a bridge response | — | Yes | High in a release build; the dev-only mock is not reportable |
| Self-XSS that needs a payload pasted into DevTools | — | No | DevTools are disabled in release WebViews |
| A user typing a command into their own terminal or approving full access | — | No | The user is the owner of the session |
| A bug in a third-party CLI (`claude`, `opencode`, `agy`, `vyce`) | — | No | Except where Agents Hub passes it unsanitised input that changes behaviour |
| Denial of service by an approved task exhausting the user's own CPU or disk | — | No | The user approved the task |

## Out of Scope, Exclusions, and Accepted Risk

- The desktop UI on macOS and Linux: the shipped desktop host is Windows-only; those platforms run
  the headless server. UI defects there are not security findings.
- Accepted risk: API keys and bot tokens are stored as plain JSON under the user's roaming profile;
  the OS user ACL is the boundary. Review by: 2027-01-31.
- Accepted risk: the installer and portable binary are not Authenticode-signed, so SmartScreen warns
  and the updater cannot verify the publisher. Review by: 2027-01-31.
- Accepted risk: opt-in raw TCP forwarders have no per-port authentication. They are empty by default
  and only exist if the user selects one. Review by: 2027-01-31.

## Known Limitations and Compensating Controls

A process running as the same user can read the profile and inject into the app; INV-1 through INV-11
do not defend against that. The LAN dashboard serves the static UI shell, but bridge actions are not
available there, so it shows errors rather than live agent state. The Claude proxy's `/health` route
is unauthenticated but returns only status and port.
