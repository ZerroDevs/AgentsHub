# HANDOFF

State of the Agents Hub work as of 30 Oct 2026. Written so the next session can continue
without re-deriving anything.

---

## 0. 05 Nov 2026: Antigravity sign-in from chat

**Done this turn.** A chat turn for an unsigned Antigravity profile used to run `agy` headless with
stdin ignored: the CLI printed its OAuth URL, waited for a code nobody could type, and exited with
no reply. Now:

- `Services/AntigravityLoginService.cs` (new) starts `agy` in a pseudo-console with its output
  redirected to `VirtualHome\.gemini\antigravity-login.out`. Stdin stays a terminal, which is the
  only stdin that prompt reads; the file is what makes the prompt visible, because the console
  pipe never receives it (measured with `cmd /c echo`).
- `Services/WebBridge/ChatActions.cs` checks sign-in before dispatch for the agy engine only. Not
  signed in: the turn is not sent, and the chat gets the URL plus three steps. A message shaped
  like a Google code (`4/...` or `4%2F...`) while that login is waiting is written into the
  prompt instead of being sent to the model.

**Verified.** `dotnet build` clean on `net10.0-windows` and `net10.0`. The real service class,
run against a throwaway profile, reported `signedInBefore=False`, extracted the Google URL, and
answered a bogus code with `token exchange failed` after agy logged `submitting manually-entered
auth code`. A real Google sign-in was not completed here: that needs a human at the consent screen.

**Open.** A code pasted after agy's 60 second window cannot be reused; the chat asks for a new
link. Expired tokens still look signed in (presence check, not expiry check), so that case falls
back to the old silent wait.

### 0b. 06 Nov 2026: IDE0330 cleared

Eight `private readonly object` fields exist only to be locked, so the analyzer asked for
`System.Threading.Lock`. Changed the field type in `AntigravityLoginService`, `DeepSeekAccounts`,
`McpClient` (two), `LlamaCppService` (two), `LocalHardware` and `OpenCodeEngine`. The `lock` blocks
are untouched. Build warnings went 39 to 31 on `net10.0-windows`, 0 errors on both targets.

### 0a. 06 Nov 2026: the sign-in notice now appears before a prompt is sent

The first cut only reacted to `send_chat`, so an unsigned profile looked ready: nothing in the
session said a word until a prompt was sent, and the browser opened with no explanation.

- `ui/src/views/AgentChatView.tsx` probes `antigravity_auth_state` as soon as a session opens (and
  when the session or profile changes), and feeds `antigravitySignIn` to the composer.
- `ui/src/components/chat/ChatInput.tsx` renders the sign-in block above the composer with the
  three steps, a **Sign in** button, and its own paste box plus **Submit code**. The block used to
  require `engine === 'agy'`, but an ordinary session sends no engine at all, so it never matched;
  an unset engine is agy and now counts.
- `Services/WebBridge/ChatActions.cs`: new `antigravity_submit_code` action. A pasted code from the
  composer's box goes straight into the waiting prompt instead of being shape-matched on its way
  through the turn path; a code whose sign-in has expired starts a fresh link and says so.
  `antigravity_auth_state` also reports `waiting`.

**Verified.** `dotnet build` clean on `net10.0-windows` (0 errors), `ui` typecheck clean, `ui`
`npm run build` produced `ui/dist`. A completed Google sign-in still needs a human at the consent
screen; that end of the flow is unverified here.

---

## 1. Where things stand

Three engines answer in the chat: the Antigravity CLI, DeepSeek and Qwen (both through WebView2),
and now Vyce Code, which runs in a hidden console. The CLI catalogue was rebuilt around detection,
install and launch. A health panel and a redacted log bundle exist so failures stop requiring a
`grep` in `%TEMP%`.

**Nothing is half-built.** Every engine in the catalogue that claims chat support can answer.

### Repo state

```
HEAD  64bd1b3  feat: add DependencyCheckerService for runtime and tool detection
      c976833  feat(engines): health panel, log bundle, and a real CLI catalogue
      c8f82cd  feat(qwen): third engine behind a WebView2, still under construction
```

Uncommitted:

```
 M Services/DashboardWebBridge.cs          vyce branch, vyce_stop, vyce stop on abort_task
 M Services/DeepSeek/DeepSeekEngine.cs     cancellation support (Stop)
 M Services/Qwen/QwenEngine.cs             conversation id, protocol reminder, result budget, health
 M Services/DependencyCheckerService.cs    Vyce row claims chat support on Windows
 M MainForm.cs                             Vyce console is killed on shutdown
 M tools/DeepSeekToolTest/Program.cs       --tty, --inject, --vyce --twice, --redact
 M tools/DeepSeekToolTest/*.csproj         compiles QwenTools, LogBundleService, Vyce, ConPty
 M ui/src/...  engine union gains 'vyce'; Vyce entry in the engine picker; composer chip
?? Services/Vyce/VyceEngine.cs             chat turn, session-file reading, detection
?? Services/Vyce/VyceTty.cs                hidden console + keystroke injection
?? HANDOFF.md
```

Everything above builds on both target frameworks, `ui` compiles, and the tool suite passes.

---

## 2. What was built

### Vyce in chat (`Services/Vyce/`)

The CLI is a full-screen TUI with no headless mode (`--help` offers only `-v`, `-l`, `--logout`,
`-h`), so the old pseudo-console approach could never work. Two files replace it:

- **`VyceTty.cs`** creates a *real* console, hidden at creation (`CREATE_NEW_CONSOLE`, `SW_HIDE`),
  and types into it. Keys go in as Win32 `KEY_EVENT` records through `WriteConsoleInputW`. Because
  `AttachConsole` only succeeds while the caller has no console, every batch attaches, writes,
  detaches. The input handle is opened as `CONIN$`, not `GetStdHandle`: attaching does **not**
  rewrite this process's stored standard handles, so `GetStdHandle` still points at whatever the
  host inherited and writing there types into nothing.
- **`VyceEngine.cs`** sends the prompt, then reads the answer out of `~/.vyce/sessions/*.json`
  instead of scraping the ANSI screen. A turn is recognised by the **assistant message count**
  growing or a new session file appearing. Counting matters: asking the model to repeat itself
  produces identical text, and a text comparison called that turn unfinished. Stop sends Esc. The
  console is reused between turns, so the conversation survives, and killed when the app exits.

Four things had to be right before a turn was reliable:

- **The idle countdown starts on the first answer of this turn, not at submit time.** Arming it at
  submit made a turn look finished 2.5 s in whenever the file already held an answer, so the turn
  ended on the previous answer, or on a half-written file, and reported that Vyce had not accepted
  the prompt while the CLI was answering.
- **A new chat resets the baseline.** The CLI writes a fresh file per chat; comparing the new file
  against the old chat's counts hides its first answer.
- **The composer must be idle before typing.** Keys typed while the previous turn is still settling
  land in the box and are never submitted, which stalls the turn. The gate reads the CLI's own
  placeholder (`queue a follow-up`), waits up to 8 s, then types anyway.
- **Text and Enter go in separately.** One batch let a warm console see the Enter before the text.

A final read is retried up to 4 s before an empty one is believed, and the prompt match ignores
whitespace so a wrapped line still counts as on screen.

Wired through: the engine picker (`Use Vyce`), the sidebar icon and tooltip, the composer chip,
`abort_task` and the dedicated `vyce_stop`.

**Models.** Vyce has no command-line flag for the model, so the picker is driven the way a user
drives it. `ListModelsAsync` sends ctrl+p (the command palette; a bare `/` is file completion, not
a command list), types `model`, presses Enter, waits for the filter box, then scrapes the list out of
the rendered screen. `SetModelAsync` opens the same screen, types enough of the id to leave one row,
and presses Enter. Both take the console lock, so a switch cannot land in the middle of a turn.
Endpoints: `vyce_models` (list), `vyce_model` (switch), and `vyce_model_current`, which answers
what is answering from the session file in ~15 ms and never starts a console. `VyceModelChip.tsx`
in the composer is the UI for all three, shaped like the Gemini selector: a borderless footer chip
with the current id, a 330 px panel with the active row ticked, unavailable rows dimmed as
"Coming soon", and an error line instead of a silent failure. The chip's label comes from
`vyce_model_current` on mount and from the panel on open; only opening the panel drives the menu.

Both menu calls refuse while a turn is running (`TurnInProgress`) instead of queueing behind the
console for up to three minutes.

Cold start sends `/home` first. A restored chat does not append to its session file, so without that
every launch after the first answered nothing and reported nothing.

### Why the pseudo-console failed

A ConPTY child inherits this process's standard handles. With the host's stdout on a pipe, `node`
inside the pseudo-console reported `process.stdin.isTTY === undefined` and even its own output went
to the parent's pipe. Reproduced from a console-less host too, so it is the wiring, not the harness.
`tools/DeepSeekToolTest --tty` is that probe and stays in the suite.

### OpenCode in chat (`Services/OpenCode/`)

The easy case next to Vyce: OpenCode has a real headless mode, so there is no console, no screen
scraping and no prompt retry.

- **`OpenCodeEngine.cs`** runs `opencode run --format json [--model p/m] [--session ses_…] [--auto]
  [--file path] <prompt>` per turn and reads the JSONL it prints. `text` events carry a completed
  part, `tool_use` a finished call, `step_finish` the tokens and cost, `error` a provider failure.
  Every line carries the session id, which is stored on the session and passed back as `--session`.
- **`ParseEvent` is separate from the process** and covered by the harness against real event lines,
  because the shapes are the part most likely to change under us.
- **Stdin is redirected and closed.** A headless run still looks at stdin, and an inherited handle
  that never closes leaves the CLI waiting instead of answering. That cost one debugging round.
- **Tools need `--auto`.** A non-interactive run rejects every permission it cannot ask about, so
  the profile's Full access switch is what maps to it. Without it the CLI refuses the calls and says
  why in its answer.
- `opencode models` feeds the composer chip; the pick is stored per session because `--model` is a
  per-turn flag, not a CLI setting. `chatController` passes it through verbatim (agy's catalogue
  would mangle `provider/model`).
- **The chip filters the list** by `All`, `Free` (the nine `-free` Zen models), and provider, because
  211 rows with no grouping is not a picker. Free rows carry a `free` tag.
- **A chat with no pick sends a free model.** The CLI's own default on this machine is routed through
  the user's local proxy, which rejects an unkeyed call (`Missing bearer or basic authentication`),
  so a turn with no `--model` failed before it started. `DefaultModelAsync` picks the first free
  model from the CLI's own list. The panel writes that pick onto the session when it opens, so the
  chip and the turn agree.
- **Sign-in lives in the panel.** `GetAuthAsync` reads `opencode auth export` and keeps only labels
  and method names, never credential values. `BeginLoginAsync` runs
  `opencode auth login opencode --method device` (the command OpenCode Console's own "Connect" page
  shows), strips the ANSI from its output, and returns the code and the page. The panel shows the
  code with a copy button, opens the page, and polls `opencode_auth` every 2 s until the CLI exits;
  a sign-in forced a re-read, so the footer flips to "Signed in" without a restart. Cancel kills the
  process. `auth login` without `--method` refuses to run non-interactively, which is why the
  watchdog parses the flow instead of driving a TUI.

### Queued prompts (`ui/src/services/chatController.ts`)

Every engine queues the same way, because the queue sits above the dispatch rather than inside any
engine. A send during a running turn is appended to the session's `queuedPrompts` instead of being
dropped, and a watcher polls that session every 250 ms: once it stops being busy, 1.5 s later the
oldest prompt goes out through the normal `sendPrompt` path and the next one waits for that turn.
A turn that is stopped, fails, or finishes late all free the queue.

Each row in the composer carries a pencil and an ×. The pencil puts that prompt and its attachments
back in the textarea and removes it from the queue, so it is edited rather than sent twice.

The store strips `queuedPrompts` before persisting, so a reload cannot leave rows with no watcher
behind them.

The composer must stay usable for any of this to work: the textarea has no `disabled` while a turn
runs, the placeholder says typing queues a follow-up, and Enter queues. The round button is Stop
during a turn, so a second, smaller button appears beside it whenever there is text to queue.

### Qwen web engine (`Services/Qwen/`)

- `QwenEngine.cs` drives `chat.qwen.ai` in a hidden WebView2, one turn at a time.
- `QwenTools.cs` owns everything specific to a *rendered* page: bare action objects, the language
  label and line-number gutter, and an unclosed object held back while streaming. Execution,
  approval, describe and preview delegate to the DeepSeek layer.
- Tool loop: `tool` JSON blocks → `QwenTools.Extract` → approval → execute → results back to the model.
- `ApprovalRequested` is wired to the **same** `deepseek_approval_request` card DeepSeek uses.
- Reports `tool_start` / `tool_output`, so a Qwen turn shows `Wrote` / `Explored` rows.
- Primes per **conversation**, not per workspace: the page path is compared with the path the primer
  was sent in, so a new chat re-primes.
- `ProtocolReminder` rides on every prompt; tool results are capped at 4000 per result, 12000 per turn.
- Conversation id: navigates to `/c/<id>` before a turn when the session has one, reports the id back
  through `conversation_started` afterwards. **If Qwen's path is not `/c/<id>`, nothing is captured
  and behaviour is unchanged** — the failure mode is silence.

### DeepSeek engine

Restored byte-identical to `408dfbf` after a regression, then extended minimally:

- `Stop()` + a linked `CancellationTokenSource` per turn. Before this, a DeepSeek turn could not be
  interrupted at all.
- A CDP retry on `COMException` in `EvalAsync` ("Class not registered" during renderer swaps).
- The host window is a `ToolWindowHost` (below).

**Diff against `408dfbf` is now: the tool-window swap, three removed `ShowInTaskbar` lines, the
retry, and the stop plumbing.** Nothing else in that engine was touched.

### Windows that Alt+Tab cannot see (`Services/Platform/ToolWindowHost.cs`)

`WS_EX_TOOLWINDOW` set in `CreateParams`, `WS_EX_APPWINDOW` cleared, `ShowWithoutActivation`.
Both web engines park their host at `-32000` so Chromium keeps rendering; the windows are simply not
listed. `ShowInTaskbar` is never toggled — that toggle was what left `WS_EX_APPWINDOW` stuck on.

### CLI catalogue (`Services/DependencyCheckerService.cs`)

Current ids: `aider, antigravity, claude, codex, crush, git, node, opencode, python, qwen-code, vyce`.

`ChatSupported` is set for **antigravity**, **opencode** and, on Windows only, for **vyce**.

- Removed **Cloudflare WARP** with the IP rotation feature it served; the whole quota-reset path,
  `ProtonVpnService` and `scripts/fixquota-command.js` are gone with it.
- Removed **Hub** and **Proton** — from the catalogue *and* from a hardcoded fallback list in
  `useSettingsStore` that was seeding the panel with stale rows and invented versions. That list is
  now empty.
- Added **Vyce Code**, **Qwen Code**, **Crush**, **Aider**, plus Codex and OpenCode.
- **Kimi Code** was removed at the user's request. It only ever had a vendor-site button, because no
  package name could be verified.
- `ChatSupported` is set for **antigravity** and, on Windows only, for **vyce**. Every other agent row
  shows a **Not used** chip, and the group carries a line saying the runtimes are required while the
  agents are optional.

### Diagnostics

- **Health panel** (Settings → Engines): `engine_health` returns passive state only — it never
  navigates a page or probes a token. Qwen reports initialized / page / primed-in / last turn / last
  error. DeepSeek reports only what it tracks.
- **Log bundle** (`Services/LogBundleService.cs`): one plain-text file from `%TEMP%\agentshub-*.log`,
  redacting bearer headers, `sk-`/`ghp_`/`AKIA`/`xox` shapes, JSON credential fields, query-style
  tokens, `C:\Users\<name>` paths and e-mail addresses. `--redact` in the harness proves it.
- `open_log_bundle_location` reveals the file (`explorer /select`, `open -R`, `xdg-open`).
- A Vyce turn that never lands writes the hidden console's screen into `agentshub-vyce.log`, so
  "it was on the wrong screen" is answerable from the log alone.

### Install feedback

The install buttons were never broken — **nothing subscribed** to `dependency_install_progress` or
`dependency_install_complete`. Now the store tracks `installingId` and a last-line map, the button
disables and reads "Installing...", the live line appears under the row, and a 180 s safety net
clears a stuck button.

### README

Rewritten around the sidecar and the free engines. Local-model routing and the OpenRouter/Groq
gateway were **deliberately removed** from the document — they were removed from the product earlier
and must not reappear.

---

## 3. Verified vs unverified

| claim | status |
|---|---|
| Both target frameworks build, 0 errors | verified |
| `ui && npm run build` (tsc + vite) | verified |
| `tools/DeepSeekToolTest` → 17 PASS | verified |
| `--redact` → nothing survives | verified |
| Qwen parsing (rendered, mid-stream, debris) | verified in harness |
| `/pcheck` → 11 passed, 0 failures | verified |
| Vyce cold turn answers (`--vyce`, unique token) | verified, repeatedly |
| Vyce model list read from the picker (`--models`) | verified, real ids |
| Vyce model switch changes the answering model (`--models <root> <id> --ask`) | verified, and switched back |
| Model label without starting a console (`vyce_model_current`) | verified: 15-18 ms, `console running=False` |
| The chip is in the built UI bundle | verified (`vyce_model_current` present in `ui/dist`) |
| Prompt queued during a turn, sent after it ends | **unverified in the app**: compiled and wired, never clicked |
| OpenCode process starts and its JSONL is parsed | verified: provider error surfaced in one line, no hang |
| OpenCode model list | verified: 212 `provider/model` ids |
| OpenCode session continued with `--session` | verified at CLI level and in the harness (`--twice`) |
| OpenCode event parser (text, tool, error, usage, junk) | verified in the harness, 5 checks |
| OpenCode answering a prompt end to end | verified: free model, unique token, second turn remembers it |
| OpenCode default model for a fresh chat | verified: `opencode/deepseek-v4.1-flash-free`, from the CLI's list |
| OpenCode auth read (`--opencode-auth`) | verified: active credential is the device account, no secret read |
| OpenCode device sign-in parsed from the real CLI (`--opencode-login`) | verified: code, page URL, process waiting, then cancelled |
| The Sign in button, filters and free tags in the running app | **unverified**: compiled and built, never clicked |
| Vyce second turn reuses the console and remembers the first | verified (`--twice`), twice in a row |
| A repeated answer still counts as a turn | verified: the repeat probe returns the same token |
| A new chat's first answer is seen against its own file | verified: cold turn after a leftover session |
| Vyce injection itself (`--inject` into `cmd`, into raw-mode Node) | verified |
| Vyce leaves no stray `node` processes | verified |
| DeepSeek stop works live | **unverified** |
| Qwen conversation resume (`/c/<id>`) | **unverified** |
| Vyce inside the running app (engine picker to first answer) | **unverified**: wired and compiled, never clicked |
| The model chip in the composer (`VyceModelChip`) | **unverified** in the app; the bridge calls behind it are verified |
| Vyce on macOS/Linux (`script` terminal route) | **unverified**, see §4 |

The unverified rows need a running app. Watch `%TEMP%\agentshub-qwen.log` for `opened conversation`.

---

## 4. Limits of the Vyce chat support

- **Windows only, for now.** The injector is Win32 console calls. The Unix branch runs the CLI under
  `script` (a real terminal) and writes the prompt to its stdin. It is written and compiled, and it
  has never been executed. Do not mark it verified until someone runs `--vyce` on macOS or Linux.
- **The TUI must be readable but is never read.** Everything comes from the session JSON. If a
  provider changes that schema, `ReadSession` returns empty and the turn reports that it was not
  accepted, which is the honest answer.
- **The prompt is typed as one line.** A newline would submit half a message, so `\n` becomes a
  space. Pasting multi-line text into a Vyce chat will be flattened.
- **The screen is not shown.** Model and answer are driven from the app; mode, sign-in and approval
  still need a real terminal. A user who needs those must open Vyce themselves.
- **The model list is scraped, not published.** If Vyce rewords the picker, `ParseModelPicker`
  returns empty and the chip says so instead of offering wrong names.
- **Repeating the exact previous prompt produced no new turn** in testing: the transcript stayed
  identical and the CLI never appended. Detection there is unreliable by nature, so a user who
  repeats a message may see `Vyce did not accept the prompt`. Probe with a unique token
  (`--vyce` does this by default) and do not conclude the engine is broken from a repeated prompt.
- **A session left mid-answer is restored on the next launch.** Cold start sends `/home` to avoid
  that, so the in-app conversation begins in the app's own chat rather than in whatever the CLI
  restored. The prompt is retyped once only if the screen does not show it after about a second.
- **Readiness is read from the composer's placeholder text.** If the CLI rewords it, the wait times
  out after 8 s and the turn is typed anyway. Marked `shortcut:` in `WaitForIdleComposer`.
- **A turn the CLI never answers runs to the 180 s ceiling.** Nothing cheaper distinguishes a slow
  model from a lost keypress; the log gets a screen dump after 10 s with no content.

### OpenCode

- **The stored API key in `auth.json` expired** (created Aug 21) and its owner is not the active
  credential: `auth list` shows both `API key` and `Default`, and the device account (`Default`) is
  the one in use. Anything routed through the user's local proxy still fails without that proxy's
  own key, which is why paid `anthropic/*`, `openai/*` and `opencode/*` rows in the picker do not
  answer. Free `opencode/*` models do.
- **The free list is not uniform.** `opencode/beam-free` is region-blocked here (403), while
  `deepseek-v4.1-flash-free`, `mimo-v2.6-flash-free` and `glm-5.3-flash-free` answer. The default
  comes from a short ordered preference list guarded by the CLI's current catalogue, so a row that
  disappears falls through to another free model rather than a stale id.
- **Sign-in is the device flow only.** The CLI also accepts a pasted key (`--method key`), but its
  `--answer` field name is not published and a wrong guess writes nothing, so the panel offers the
  flow the Console page documents instead.
- **The device code expires.** If the user never approves it, the CLI exits non-zero and the footer
  says the sign-in did not finish; pressing Try again starts a fresh code.
- **Text arrives per completed part**, not token by token, because that is what `--format json`
  emits. A long answer appears in chunks.
- **Tools are all-or-nothing.** A headless run cannot ask for permission, so Full access on the
  profile means `--auto` and everything else means the CLI refuses the calls. There is no per-action
  approval card on this route.
- **One turn at a time.** The engine is a singleton with a semaphore, the same shape as Vyce; a
  second chat waits its turn rather than starting a second process.
- True token streaming would mean the HTTP route through `opencode serve`, which the repo already
  talks to in `OpenCodeWatcherService`. That is a different integration, not a tweak.

---

## 5. Next steps, in priority order

1. **Confirm the unverified rows by restarting the app**: DeepSeek stop, Qwen resume, a first Vyce
   answer from the picker, and switching models from the composer chip mid-chat.
2. **Run the Vyce route on macOS or Linux** and either confirm or delete the `script` branch.
3. **First-run wizard** — detect engines, sign in, pick a workspace.
4. **Session export / search / per-session model memory** — three small, independent pieces.
5. **In-app selector check** — when a provider rewords its UI, report which selectors still match,
   inside the existing WebView2 (never a second Chromium).
6. **UI unit tests** — there is no JS test runner. `appendChatToken` appending full text is what
   produced the `tooltool` bug, and nothing covers it. Adding Vitest is a real decision, not a patch.

---

## 6. Facts that will bite you

- **The transcript is append-only.** `appendChatToken` appends; anything streamed once is permanent.
  That is why `chat_final` exists (it *replaces* the last assistant message) and why `Visible` holds
  back an unclosed object rather than filtering after the fact.
- **`DeepSeekTools.cs` is DeepSeek's.** Qwen-specific parsing belongs in `QwenTools.cs`. Editing the
  shared file broke a working engine once already.
- **`TokenJs` is embedded in three page scripts** as `const token = {{TokenJs}}`. Changing its return
  shape is an API change: an object broke the page's own auth call and DeepSeek stopped answering.
- **`ShowInTaskbar` must never be toggled** on a host window — see `ToolWindowHost`.
- **A ConPTY child inherits the host's standard handles**, so a Node CLI inside one sees no TTY even
  when the console is real. `--tty` proves it in one command. Anything needing a TTY has to be given
  a real console, not a pseudo one.
- **`AttachConsole` does not rewrite standard handles.** Open `CONIN$`/`CONOUT$` after attaching, or
  the writes go to whatever the process inherited and fail silently in the child.
- **Kill the Vyce process tree on exit or it outlives the app** and holds the session file the next
  launch wants. `MainForm` shutdown and the harness both do this.
- **`/` in a Vyce chat is file completion, not commands.** The command list is ctrl+p. Typing `/model`
  and expecting a menu is what made the first attempts look like a broken picker.
- **The model picker commits on Enter, and only on one row.** Filter with a prefix that leaves a
  single match; a filter that leaves several rows leaves the picker open, and Enter there does
  nothing useful. `SetModelAsync` reads the marker back rather than assuming it worked.
- **The model menu is refused during a turn, not queued.** The console carries one thing at a time;
  waiting on it would leave the picker spinning for a whole answer.
- **A queued prompt has no watcher after a reload.** It is deliberately not persisted; persisting it
  would show rows nothing would ever send. Closing the window drops them.
- **Queued prompts go out one at a time, in order.** Dispatching one sets the session to `thinking`
  through `addUserPrompt`, which is what stops the next one leaving immediately.
- **The web message handler runs on the window thread.** Any engine call that sleeps must be wrapped
  in `Task.Run` at the bridge, or the whole app stops responding while a console starts. The model
  list and the switch do this; the first version did not, and that was the freeze on the chip.
- **The CLI writes its session file before it finishes with the turn on screen.** The file is the
  answer, the screen is only the readiness signal; never treat "file changed" as "TUI is free".
- **A turn is not its text.** Two turns can produce the same answer, so acceptance and completion
  are counted in assistant messages, never compared as strings.
- **The model a chip shows and the model a turn uses are the same fact.** Both read the `model`
  field of the session file; there is no separate setting in this app to drift from it.
- **Never delete `obj/`.** It holds the generated global-usings file; deleting it produces a wall of
  "`Path`/`File`/`Task` not found" errors in the editor.
- **A shared const that is both C# and JavaScript has consumers on both sides.** Audit before editing.
- **`//n` is not `\n`.** A literal survived in the tool-results message for hours.
- **The editor can be wrong.** If it reports errors that `dotnet build -t:Rebuild` does not, check
  whether the file is new (stale project snapshot) or the tab holds an older buffer.

---

## 7. Commands

```bash
dotnet build AgentsHub.csproj -c Debug                       # both targets
dotnet build AgentsHub.csproj -c Debug -f net10.0            # headless only
dotnet build AgentsHub.csproj -c Debug -t:Rebuild            # when the editor disagrees

cd tools/DeepSeekToolTest
dotnet run -c Debug                     # 17 checks
dotnet run -c Debug -- --parse          # Qwen parsing
dotnet run -c Debug -- --redact         # log redaction
dotnet run -c Debug -- --guide <root>   # what the engines prime with
dotnet run -c Debug -- --vyce <root>    # Vyce end-to-end, unique token per run
dotnet run -c Debug -- --vyce <root> --twice    # second turn in the same console
dotnet run -c Debug -- --models <root>          # what models the picker offers
dotnet run -c Debug -- --models <root> <id>     # switch, then read the marker back
dotnet run -c Debug -- --models <root> <id> --ask   # switch and make that model answer
dotnet run -c Debug -- --screen                 # free-form screen probe, see Program.cs
dotnet run -c Debug -- --tty [command]  # what a child sees inside this app's pseudo-console
dotnet run -c Debug -- --inject "cmd.exe /k" [delayMs] [text]   # the injector alone

cd ui && npm run build                   # tsc + vite, after any ui/src change
bun run tools/pcheck.ts                  # 11 cross-platform checks
```

dotnet run -c Debug -- --opencode <root> [provider/model]   # one headless turn
dotnet run -c Debug -- --opencode <root> [model] --twice     # and a second in the same session
dotnet run -c Debug -- --opencode-models                     # what --model accepts

Logs: `%TEMP%\agentshub-{deepseek,qwen,vyce,opencode,permissions,spawns}.log`

---

## 8. Open decisions

- **Vyce on Unix**: confirm the `script` route, or drop the branch and say Windows-only in the panel.
- **Vyce input rhythm**: a prompt missing from the screen is retyped once. A finer signal would
  need a notification hook the TUI does not have.
- **Vyce model memory**: the CLI keeps the model in its own config, so a switch persists across
  sessions. The app reads the current one on open; it does not store a copy per chat.
- **Log bundles** currently land in `%TEMP%`. Desktop or Documents would be easier to find.
- **UI tests**: adopt Vitest, or keep relying on the C# harness?

---

## 9. Privacy

The tree was audited: no API keys, tokens, private keys, credential stores, session logs or personal
absolute paths in tracked files. The redaction fixture uses `C:\Users\someone`, not a real account.
Keep it that way — `pcheck` verifies portability, **not** privacy.
