# Security Policy

Agents Hub handles sensitive credentials, developer tool execution, process orchestration, and local network communication. We take the security of your tools, API keys, and system integrity seriously.

---

## Supported Versions

Security patches and vulnerability updates are provided for the current active release line:

| Version | Supported | Status |
| :--- | :--- | :--- |
| **2.0.x** | Yes | Current active release line. |
| **< 2.0.0** | No | Legacy preview releases. Please upgrade to the latest release. |

---

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

If you discover a security vulnerability or potential exposure in Agents Hub:

1. **GitHub Security Advisory**: Submit a private report via [GitHub Private Vulnerability Reporting](https://github.com/ZerroDevs/AgentsHub/security/advisories/new).
2. **Direct Email**: If Private Vulnerability Reporting is unavailable, send an email to the project maintainers via the contact details on the [ZerroDevs GitHub Profile](https://github.com/ZerroDevs).

### What to Include in Your Report

To help us investigate and resolve the issue quickly, please provide:
- A clear description of the vulnerability and its potential impact.
- Step-by-step instructions or a minimal proof of concept to reproduce the issue.
- The version of Agents Hub and Windows OS version (e.g., Windows 11 23H2 x64).
- Any affected components (e.g., ConPTY terminal, LAN proxy, WebView2 IPC bridge, credential store).
- Redact all personal tokens, API keys, and sensitive paths from your logs or screenshots.

### Response Timeline

- **Acknowledgment**: Within 48 hours of receipt.
- **Assessment & Triage**: We will confirm the issue, evaluate severity, and keep you updated on progress.
- **Remediation**: A patched release will be published, followed by a public advisory crediting your discovery (unless you prefer to remain anonymous).

---

## Security Architecture & Threat Model

Agents Hub incorporates strict security boundaries across its subsystems:

### 1. Secret & Credential Storage
- **No Secrets in Code**: API keys, tokens, and credentials are never stored in the source repository or binary assets.
- **User Profile ACL**: Custom provider API keys and model configurations are saved to `%AppData%\AgentsHub\ai_config.json`, and per-profile Antigravity credentials to `%LocalAppData%\AgentsHub\Profiles`, both under the user's own profile ACL. Encryption at rest (DPAPI) is a tracked follow-up, not a current claim.
- **No Credentials in Build Output**: Profile folders are excluded from the project, the portable zip and the installer, so a publish directory that was used as a run directory cannot ship an OAuth token.
- **Pre-Commit Secret Scanner**: Integrated pattern matching scans uncommitted and staged files before Git commits for OpenAI keys, Anthropic tokens, AWS credentials, GitHub PATs, private keys, SSH keys, and shell injection hazards.

### 2. IPC & WebView2 Bridge Boundary
- **Message Validation**: Communications between the WebView2 interface and the C# host (`DashboardWebBridge.cs`) pass through strict JSON schema parsing and whitelist validation.
- **Explicit RPC Actions**: Only predefined actions can be invoked; arbitrary code injection through the bridge is blocked.
- **Origin Lock**: The dashboard WebView is pinned to `app.agentshub.local` and `theme.agentshub.local`. Any other navigation is cancelled and opened in the default browser, so a link in model output cannot land an attacker origin on a page that still holds the message bridge.
- **Link Filtering**: Chat links render only `http`, `https`, `mailto` and in-page anchors. `javascript:`, `data:` and `file:` targets never become an `href`.
- **DevTools Off**: DevTools are disabled on every app-owned WebView, so the bridge cannot be driven from a console.

### 3. Process Execution & Shell Isolation
- **Antigravity VirtualHome**: Each profile's environment variables (`USERPROFILE`, `HOME`, `APPDATA`, `LOCALAPPDATA`) are redirected to an isolated sandbox directory to prevent cross-account profile contamination.
- **Tree-Aware Process Termination**: When stopping an agent or engine, process trees are traversed and killed via native job objects and PID trees to prevent background orphan processes.
- **Elevated Shell Guards**: Launching Administrator shells or modifying Windows Firewall rules prompts standard Windows User Account Control (UAC) elevation dialogs.

### 4. Network Exposure & Localhost Binding
- **Strict Localhost Default**: The service ports (`9119`, `9229`, `9230`, `9339`+) bind to `127.0.0.1`. Only the opt-in LAN gateway binds `0.0.0.0`.
- **Terminal Server Is Loopback Only**: The HTTP/WebSocket terminal rejects any non-loopback socket, checks the WebSocket `Origin`, and requires a per-session random token that only the served page receives. It serves a fixed asset list, not a path-joined folder.
- **Claude Translation Proxy Requires a Key**: `/v1/messages` on `127.0.0.1:9230` refuses anonymous callers and callers presenting a key the install does not know, so a local page or process cannot spend the stored OpenRouter credits.
- **Sidecar Fails Closed**: The Bun engine refuses every WebSocket unless its startup token matches; an engine started with no token accepts no client.
- **Opt-In LAN Broadcast**: The LAN reverse proxy (`9191`) binds to `0.0.0.0` only when explicitly toggled on by the user. Turning off LAN broadcast immediately closes the public listener and resets links to `127.0.0.1`.
- **LAN Pairing Token**: A remote device must arrive from the QR or copied link, which carries a per-start token. The gateway trades it for an `HttpOnly`, `SameSite=Lax` cookie and then drops it from the URL. A non-local `Host` header is treated as remote, so DNS rebinding does not inherit loopback trust.
- **No Direct Ports by Default**: Raw TCP forwarders are opt-in and empty by default; they carry no authentication and are never bound unless the user selects them.

### 5. Update Integrity
- **Trusted Hosts Only**: The updater installs from an HTTPS asset on GitHub's release hosts and refuses any other scheme or host, even when the renderer supplies the URL.
- **Digest Check**: When the release API publishes a SHA-256 for the asset, the downloaded bytes are hashed and a mismatch is discarded before execution.
- **Size Cap**: Downloads stop at 512 MB.
