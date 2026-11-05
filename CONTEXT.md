# CONTEXT

Architecture facts that outlive a single turn. Agents Hub is a .NET 10 host (`AgentsHub.csproj`,
targets `net10.0-windows` and `net10.0`) plus a React UI and a Bun sidecar.

## Layers

- **UI** (`ui/`, React + Vite): renders chat, transcripts, profiles. Talks to the host only through
  `webviewBridge.invoke(...)` messages, handled by `Services/WebBridge/*.cs`.
- **Host** (`MainForm.cs`, `Services/`): owns profiles, credentials, CLIs, terminal sessions.
  `DashboardWebBridge` routes UI actions; events go out with `PostEventToUi`.
- **Sidecar** (`sidecar/`, Bun/TypeScript): runs chat turns for the CLI engines. `send_chat` reaches
  `SessionManager.executeAgyTurn`, which spawns `agy --output-format stream-json` and maps NDJSON to
  `chat_token` / `tool_start` / `tool_output` / `session_status` events.

## Antigravity profile model

- Profiles live in `%LOCALAPPDATA%\AgentsHub\Profiles\Antigravity\<name>`; each has a `VirtualHome`
  whose `.gemini` holds the credentials a turn reads.
- `AntigravityProfileService` owns the profile store: `PrepareVirtualHome`, `ApplyProfile`,
  `ExportCurrentSession`, `SyncVirtualHomeToProfile`, plus the machine-wide `gemini:antigravity`
  Windows Credential Manager entry.
- Sidecar turns set `USERPROFILE`/`HOME`/`APPDATA`/`LOCALAPPDATA` to the VirtualHome
  (`buildScopedEnvironment`), so credentials must exist there to be used.

## Engine selection

- `engine` on `send_chat` is omitted for Antigravity (`agy`), `"vyce"`, `"opencode"`, `"deepseek"`,
  `"qwen"` select their own paths. Unknown engines fall through to the CLI path.
- Chat turns for CLI engines are headless: stdin is ignored, so nothing interactive can be answered
  mid-turn.
- `engine` is absent for an Antigravity session, so the UI must treat an unset engine as `agy`.
  Checking `engine === 'agy'` alone hides every Antigravity-only affordance.
- Antigravity sign-in is surfaced by `antigravity_auth_state` on session open, `antigravity_signin`
  for the URL, and `antigravity_submit_code` for a pasted code. The code never travels through
  `send_chat`: the turn path only accepts it when the text happens to match a Google code shape.

## Design constraints

- Lock objects are `System.Threading.Lock`, not `object` (IDE0330 is on in the build). The `lock`
  statement is unchanged; only the field type moves. Nothing here uses `Monitor.Wait`/`Pulse`, which
  `Lock` does not provide.
- Windows-first APIs sit behind `OperatingSystem.IsWindows()` and have POSIX paths
  (`ConPtySession.StartPosixSession`, `PlatformHelper`).
- ConPTY note: the child of `ConPtySession` writes prompts to the inherited console handle, not to
  the pseudo-console output pipe. Read a redirected file instead of `OutputDataReceived` when the
  child's prompt text matters. Stdin through the pseudo-console does reach the child.
