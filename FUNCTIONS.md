# FUNCTIONS

Reusable pieces. Check this file before adding a duplicate.

## Chat and engines

| Name | File | Purpose |
|---|---|---|
| `DashboardWebBridge.HandleSendChatAsync` | `Services/WebBridge/ChatActions.cs` | Routes one chat message to the right engine; owns the Antigravity sign-in gate |
| `LooksLikeAntigravityAuthCode` | `Services/WebBridge/ChatActions.cs` | True for `4/...` or `4%2F...` code shapes |
| `BuildAntigravitySignInText` | `Services/WebBridge/ChatActions.cs` | Chat copy plus sign-in URL for an unsigned profile |
| `HandleAntigravityAuthStateAsync` | `Services/WebBridge/ChatActions.cs` | Answers `antigravity_auth_state`: signed in, waiting, sign-in URL |
| `HandleAntigravitySignInAsync` | `Services/WebBridge/ChatActions.cs` | Answers `antigravity_signin`: starts the prompt, returns its URL |
| `HandleAntigravitySubmitCodeAsync` | `Services/WebBridge/ChatActions.cs` | Answers `antigravity_submit_code`: writes a pasted code into the waiting prompt, or starts a fresh link when it expired |
| `AntigravityLoginService.IsSignedIn` | `Services/AntigravityLoginService.cs` | Saved profile credentials or live credentials exist |
| `AntigravityLoginService.StartAsync` | `Services/AntigravityLoginService.cs` | Starts agy's OAuth prompt and returns its URL |
| `AntigravityLoginService.SubmitCodeAsync` | `Services/AntigravityLoginService.cs` | Writes a pasted code into the prompt and reports the result |
| `AntigravityLoginService.Cancel` | `Services/AntigravityLoginService.cs` | Stops a waiting sign-in |
| `SessionManager.executeAgyTurn` | `sidecar/session/sessionManager.ts` | Headless agy turn, NDJSON to UI events |
| `SessionManager.buildScopedEnvironment` | `sidecar/session/sessionManager.ts` | Per-profile env with VirtualHome as home |

## Profiles and credentials

| Name | File | Purpose |
|---|---|---|
| `AntigravityProfileService.PrepareVirtualHome` | `Services/AntigravityProfileService.cs` | Seeds a profile's isolated home |
| `AntigravityProfileService.SyncVirtualHomeToProfile` | `Services/AntigravityProfileService.cs` | Folds VirtualHome credentials back into the profile |
| `AntigravityProfileService.HasValidSavedCredentials` | `Services/AntigravityProfileService.cs` | Static presence check for a profile's credentials |
| `AntigravityProfileService.HasValidActiveCredentials` | `Services/AntigravityProfileService.cs` | Static presence check for live credentials |
| `AntigravityProfileService.ReadWindowsCredential` / `WriteWindowsCredential` | `Services/AntigravityProfileService.cs` | `gemini:antigravity` credential store |

## Terminals

| Name | File | Purpose |
|---|---|---|
| `ConPtySession` | `Services/ConPty/ConPtySession.cs` | Pseudo-terminal process with `Write`, `Stop`, `Exited` |
| `PtyRunner` | `sidecar/tools/ptyRunner.ts` | Sidecar-side interactive runner (pipes, not a real pty) |

## UI helpers

| Name | File | Purpose |
|---|---|---|
| `sendPrompt` / `dispatch` | `ui/src/services/chatController.ts` | Builds the `send_chat` payload |
| `MarkdownRenderer` | `ui/src/components/chat/MarkdownRenderer.tsx` | Renders chat markdown, including links |
| `profileForDispatch` | `ui/src/lib/profileName.ts` | Profile id to send, or undefined for active |
| `ChatInput` Antigravity block | `ui/src/components/chat/ChatInput.tsx` | Sign-in steps, Sign in button, code paste box above the composer; hidden unless the host reports no token |
