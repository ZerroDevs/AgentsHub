# Agents Hub v0.0.120

**Desktop control plane and workspace for AI coding agents, headless CLI runners, and free web models.**

Agents Hub coordinates coding agents across your machine in a single desktop application. Run isolated CLI engines, headless runners, and web-driven models without paid API subscriptions, with a shared chat transcript, tool approval pipeline, visual Git workstation, and Model Context Protocol (MCP) manager.

[![Version: v0.0.120](https://img.shields.io/badge/Version-v0.0.120-2563eb?style=flat-square)](https://github.com/ZerroDevs/AgentsHub/releases/latest)
[![GitHub Release](https://img.shields.io/github/v/release/ZerroDevs/AgentsHub?style=flat-square&color=2563eb&label=Release)](https://github.com/ZerroDevs/AgentsHub/releases/latest)
[![Download Setup](https://img.shields.io/badge/Download-Windows%20Installer%20(.exe)-10b981?style=flat-square&logo=windows)](https://github.com/ZerroDevs/AgentsHub/releases/latest)
[![License: CopyLeft](https://img.shields.io/badge/License-3fb950?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6?style=flat-square)]()
[![Security Policy](https://img.shields.io/badge/Security-Policy-blue?style=flat-square)](SECURITY.md)

---

## Core Capabilities

- **Free Web Models with File and Terminal Tools**: Drives DeepSeek and Qwen web interfaces inside offscreen browser sessions. The host injects a tool loop, giving web chat access to file reading, editing, diff patching, terminal commands, and MCP servers without an API key or subscription.
- **Headless and TUI CLI Management**: Integrates headless runners (OpenCode) via streaming JSONL event feeds and terminal TUIs (Vyce Code) via hidden Windows pseudoconsoles (ConPTY) with assistant-message turn tracking and interactive model switching.
- **Multi-Account Profile Isolation**: Runs separate Antigravity (`agy`) identities with sandboxed home directories (`VirtualHome`), isolated Windows Credential Manager storage, and independent local ports. Allows switching between multiple DeepSeek accounts with distinct session storage.
- **Visual Git Workstation**: Built-in commit tree graph with branch lanes, unified and side-by-side diff viewers, branch-from-commit, stash and worktree controls, and direct GitHub synchronization.
- **Local-First & Private**: Operates entirely on your local machine with zero telemetry endpoints, license servers, or external tracking. Internal APIs bind to loopback (`127.0.0.1`). Exported diagnostic logs scrub API keys, authorization tokens, user paths, and email addresses automatically.

---

## Supported Engines

| Engine | Execution Mode | Tools Supported | Highlights |
| :--- | :--- | :--- | :--- |
| **Antigravity (`agy`)** | CLI in pseudoconsole | Native | Multi-profile isolation, per-identity port allocation (`9339`+), sandboxed filesystem |
| **DeepSeek** | Offscreen browser session | Injected JSON loop | Free web account, reasoning/thinking blocks, image/file attachments, account switcher |
| **Qwen** | Offscreen browser session | Injected JSON loop | Free web interface for `chat.qwen.ai`, guest or authenticated sessions, reasoning blocks |
| **OpenCode** | Headless process | Native (`--auto`) | Streaming JSONL parser, session continuation, in-composer model chip with Zen free models, device login |
| **Vyce Code** | Hidden ConPTY console | Native TUI | Turn tracking via assistant message counts, interactive model picker via TUI command palette scraper |
| **Claude Code** | CLI in pseudoconsole | Native | Direct terminal runner and process supervision |
| **Custom CLIs** | System shell | Depends | Configurable working directories, environment isolation, and shell selection (PowerShell, Bash, CMD) |

---

## Tool Protocol & Prompt Management

### Injected Tool Protocol

For web chat models without native agent capabilities, Agents Hub provides an automated tool loop. The model outputs structured actions inside fenced blocks:

```json
{
  "action": "read_file",
  "path": "src/index.ts"
}
```

The host intercepts the request, checks permissions, executes the action against your local workspace, and returns the result into the conversation as the next turn.

Supported tool operations: `list_dir`, `read_file`, `write_file`, `apply_patch`, `search_text`, `run_command`, `move_file`, `delete_file`, `read_skill`, and `mcp_call`.

### Permission Gates

Destructive operations (writing files, applying diffs, running shell commands, executing MCP tools) display an inline preview card in the chat transcript with the exact path or command. Actions only proceed upon explicit confirmation, unless the active profile has **Full access** enabled.

### Sequential Prompt Queue

Submit follow-up thoughts or tasks while an engine is generating an answer or executing tools. Queued prompts appear directly above the composer with controls to edit or cancel them, and dispatch automatically in sequence once the current turn completes.

---

## Workspace Views

The main interface provides six dedicated views:

- **AI Agent Workspace (Chat IDE)**: Unified chat transcript with token streaming, expandable reasoning blocks, tool status rows (`Ran`, `Wrote`, `Explored`), engine switchers, model filter chips (OpenCode, Vyce, DeepSeek), image previews, and prompt queue controls.
- **Dashboard & Ports**: Central process supervisor displaying active port allocations, engine states, and quick-launch system terminals.
- **Visual Git & Repositories**: Commit history graph with branch lanes, working directory file status, side-by-side diff viewers, branch creation, stash and worktree managers, and GitHub repository integration.
- **AccountsHub (Profiles)**: Multi-identity manager for Antigravity profiles (isolated home paths, environment variables, credentials) and DeepSeek accounts.
- **Model Context Protocol (MCP) & Skills**: Discovery workstation for workspace and global MCP servers, tool schemas, prompts, resources, agent skills (`SKILL.md`), and custom instructions.
- **Settings & System Diagnostics**:
  - **Engine & Runtime Detection**: Automatic detection of installed tools (`agy`, `opencode`, `vyce`, `claude`, `aider`, `codex`, `git`, `node`, `python`) with background installation.
  - **Hardware & Network Telemetry**: On-demand hardware readings (CPU, RAM, app memory, network throughput) displayed in a compact status strip.
  - **Live Logs Console**: Tabular 4-column log stream with level filtering and search.
  - **Redacted Diagnostic Bundle**: One-click generation of sanitized troubleshooting logs with automatic secret and path scrubbing.
  - **LAN Remote Proxy**: Opt-in Wi-Fi companion proxy with QR code pairing for local network access.

---

## Security & Privacy

- **Local Operation**: No telemetry, analytics, or usage statistics leave your machine. Network traffic is strictly limited to the services and APIs you explicitly connect to.
- **Automated Secret Redaction**: Diagnostic log exports automatically scan for and scrub Bearer tokens, private keys, API key patterns (`sk-`, `ghp_`, `AKIA`, `xox`), user home directory paths, and email addresses.
- **Controlled Local Binding**: All internal web bridges and services bind to `127.0.0.1` by default. Network access via the LAN remote is strictly opt-in.
- **Action Approval Required**: File modifications and command runs require interactive approval before execution.

---

## System Requirements

- **Operating System**: Windows 10 / 11 (64-bit)
- **Version**: v0.0.120
- **Distribution**: Standalone desktop installer (`.exe`) and portable packages with bundled runtimes. No external framework or runtime installation required.

---

## License

Source-available, non-commercial, copyleft, trademark-protected. See [LICENSE](LICENSE).

Copyright © 2026 [ZerroDevs](https://github.com/ZerroDevs). All Rights Reserved.
