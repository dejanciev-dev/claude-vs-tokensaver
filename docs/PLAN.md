# claude-vs-tokensaver — Development Plan

Plan for taking the v0.1 scaffold to a published Marketplace extension.
Tick the boxes as you go. Each phase ends with something you can run.

---

## 0. Status today (v0.1 scaffold)

| Area | State |
|---|---|
| `ClaudeVS.Core` (CLI session, parser, router, meter, context, setup) | ✅ Written, 16/16 tests pass |
| `ClaudeVS` VSIX (package, tool window, commands, options) | ✅ Builds clean (0 warnings); not yet run in the experimental instance |
| Real end-to-end run against `claude` CLI | ⚠️ Core verified against CLI 2.1.316 (`--live` smoke test): sessions, streaming, usage, cost, cache OK; Haiku routing works in acceptEdits ($0.002 vs $0.042 per turn on Sonnet); in plan mode the CLI answers with Sonnet even when Haiku is requested (still so in 2.1.319, where all live smoke tests pass). VS UI not run yet |
| Markdown transcript, diffs, history, question cards | ❌ Not started |

---

## Phase 1 — Get it building and running (1–2 days)

**Goal:** F5 opens the experimental instance, the chat window appears, one prompt gets an answer.

### 1.1 Environment
- [ ] Visual Studio 2022 17.14+ (or VS 2026) with workload **Visual Studio extension development**
- [ ] .NET 8 SDK (for the Core tests)
- [ ] Claude Code CLI installed and logged in — `claude --version` and `claude -p "hi"` work in a terminal
- [x] Create the GitHub repo `claude-vs-tokensaver`, push the scaffold, replace `OWNER` in `source.extension.vsixmanifest`

### 1.2 First build
- [x] Open `ClaudeVS.sln`, restore NuGet
- [x] Pin `Microsoft.VSSDK.BuildTools` to the version that restored (17.14.2142)
- [x] Fix compile errors in `src/ClaudeVS` (expected: usings, analyzer warnings `VSTHRD*`)
- [ ] `ClaudeVS` → **Set as Startup Project** → F5
- [ ] Check: **View → Other Windows → Claude Code** opens the window
- [ ] Check: **Tools → Options → Claude Code** shows the settings page
- [ ] Check: **Tools → Claude Code** submenu shows both commands

### 1.3 First conversation
- [ ] Open any solution in the experimental instance and send "hello"
- [ ] Verify the streamed text appears, then the `✓ … tok · $…` footer
- [ ] Verify the meter updates (context %, session tokens, cost, cache %)
- [ ] Ask for a file edit → the Allow/Deny bar appears for shell commands
- [ ] Stop button interrupts a long answer; next prompt still works (auto `--resume`)
- [ ] Turn on **Tools → Options → Debugging → Output → "Claude Code" pane** (add one if missing, see 2.5) to see raw CLI lines

**Done when:** a full prompt → answer → edit cycle works in VS 2022. Tag `v0.1.1`.

---

## Phase 2 — Make the core features solid (1 week)

### 2.1 Model routing
- [x] Log every route decision (prompt length, score, reason, chosen model) to the output pane
- [ ] Collect ~50 real prompts from your own work (turn on Options > Diagnostics > *Log prompt preview*); tune keyword lists and thresholds in `ModelRouter.cs`; check the effect with the router replay in *Show Savings Report*
- [ ] Add those prompts as test cases in `tests/ClaudeVS.Core.Tests`
- [x] Verify `set_model` / `apply_flag_settings` take effect mid-session (`--live-session`, 2.1.318: after `set_model sonnet` the next turn was answered by claude-sonnet-5-5; the effort change was accepted, but the answer does not show which effort ran)
- [x] Show the model actually used per turn in the footer

### 2.2 Token meter
- [x] Confirm context % matches the CLI's own `/context` output (±2%): same token count in `--live-session` (2.1.318). The meter now also takes the CLI's `get_context_usage` number after each turn
- [x] Confirm cost resets correctly after interrupt/relaunch: after a `--resume` relaunch the CLI's cost restarts at 0 and the meter keeps adding up (`--live-session`)
- [ ] Make the `/compact` advice button work end-to-end; meter drops after `compact_boundary` — the meter part is verified (`--live-session`; it showed 0 until the next turn, now the CLI's measured size), the button itself still needs a click in VS
- [x] Persist usage per solution: one line per turn in `%LocalAppData%\claude-vs-tokensaver\usage.jsonl` (`UsageLog`); summary logged to the Output pane on session start

### 2.3 Smart context
- [ ] Test **Ask Claude About Selection** for: small selection, 500-line selection, whole-file selection, unsaved file
- [x] Add "Attach current file as reference" (path only, no content) — Ctrl+Alt+K, F
- [x] Add "Attach error list" — only errors, file:line + message, max 20 — Ctrl+Alt+K, E

### 2.4 Setup commands
- [ ] Run **Install Token-Saving Settings and Hooks** on a real solution
- [x] **Verify the hook:** ask Claude to run `dotnet build` — the executed command must contain `-v q -nologo` (`--live-setup`, 2.1.318). Found: on Windows Claude runs it with the **PowerShell** tool, so the Bash-only hook never ran; the matcher is now `Bash|PowerShell`, and `updatedInput` is honored
  - If `updatedInput` is ignored → fall back to a `deny` with message "use: `<quiet command>`"
- [x] Verify Claude refuses to read `bin/` / `obj/` after install (`--live-setup`: "File is in a directory that is denied by your permission settings"). Also found: `BASH_MAX_OUTPUT_LENGTH` no longer caps what Claude receives; the installer now sets `bashOutputMaxChars` (Bash and PowerShell), verified with a 25 KB output
- [x] Run **Generate Lean CLAUDE.md** on 2–3 different solutions (API, Angular+API, legacy .NET Framework) and fix scanner gaps. Ran on five with `--scan <folder>` (read-only): microservices API + Angular, Clean Architecture API + React, ASP.NET Core with npm bundling, a .NET Framework 4.x solution (Web Forms, MVC, WCF, VB) and a repo with sample projects outside its solution. Fixed: framework from `Directory.Build.props`; .NET Framework (`v4.8` → `net48`, MSBuild + vstest commands instead of `dotnet build`, `packages.config`, web/test detection from project type GUIDs and assembly references); `.vbproj` / `.fsproj`; projects the solution doesn't list (one summary line, left out of the commands) and ones it lists from outside the folder; React / Vue / Next / Svelte front ends with `npm run build` / `npm test`; tooling-only `package.json` dropped; a `package.json` beside a .NET project gets a command, not a second project line. The MSBuild / vstest commands are not run yet (no .NET Framework targeting packs here)

### 2.5 Diagnostics
- [x] Add an Output window pane "Claude Code" (`IVsOutputWindow`) with raw stdin/stdout lines behind an option
- [x] Friendly error when `claude` is not found / not logged in (`ClaudeErrorHints`)

**Done when:** you use it for a full working day without switching to the terminal. Tag `v0.2.0`.

---

## Phase 3 — UI that people want to use (1–2 weeks)

- [x] Replace the plain `TextBox` transcript with a rich view (Option A: `TranscriptView` + `MarkdownParser`, re-rendered while streaming)
  - Option A: WPF `FlowDocument` + simple Markdown (headings, code blocks, lists) — no extra dependency
  - Option B: WebView2 + markdown-it — richer, heavier
- [x] Code blocks: **Copy** and **Insert at cursor** buttons (insert is one undo step)
- [x] Collapsible tool-call rows (`⚙ Read: src/A.cs`) with result size in tokens (input shown when expanded; results of 5k+ tokens in bold)
- [x] File edits: **Diff** button on Edit/MultiEdit/Write rows using `IVsDifferenceService` (before rebuilt by undoing the edit; Write uses a snapshot)
- [ ] Theme check: Light, Dark, Blue in VS 2022 and VS 2026
- [x] Prompt box: history (Ctrl+Up/Down), `/` command hints incl. custom `.claude/commands`, `@` file picker (attaches the file as a reference)
- [x] Questions & plan approval cards (`AskUserQuestion`, `ExitPlanMode` over `can_use_tool`) — request shapes and the answer format verified against CLI 2.1.316

**Done when:** a colleague can use it without explanation. Tag `v0.3.0`.

---

## Phase 4 — Sessions and savings report (1 week)

- [x] Session history picker: read `~/.claude/projects/<encoded-path>/*.jsonl`, list title/date/size, resume with `--resume` (**History** button)
- [x] Multiple chat windows (one `ClaudeSession` each) — Tools > Claude Code > *New Chat Window*, `Ctrl+Alt+K, N`
- [x] **Savings report** (Tools > Claude Code > *Show Savings Report*, written to Output > Claude Code; a richer panel can come with Phase 3 UI):
  - tokens avoided by references (from `BuiltPrompt.CharsAvoided`)
  - turns routed to Haiku/Sonnet vs Opus and estimated $ saved
  - cache hit rate trend
- [x] Optional "Ask Haiku to route" mode (one cheap call) — compare accuracy with the local router. Option *Route with Haiku* (off). A separate Claude Code process trimmed to a classifier (`--system-prompt`, `--tools ""`, `--strict-mcp-config`, `--disable-slash-commands`, `--setting-sources ""`, `--no-session-persistence`, `--json-schema`; `--bare` is not usable because it ignores subscription sign-in). Measured with 2.1.318: ~1.3k tokens, ~14 s to start (so it starts with the chat), then 1–2 s and ~$0.0001 per route. Falls back to the local rules after 4 s. The usage log records both decisions and the savings report shows how often they agree; first live run: same model 4 of 4, effort differed once

**Done when:** the report shows real savings numbers on your own usage. Tag `v0.4.0`.

---

## Phase 5 — Release (2–3 days)

- [x] Icon (128×128 PNG) + preview image, add to manifest
- [ ] Screenshots for README and Marketplace (meter, routing hint, setup dialog)
- [ ] Test install of the Release `.vsix` on clean VS 2022 **and** VS 2026 (x64; ARM64 if available)
- [x] GitHub Actions: build on `windows-latest`, run Core tests, upload `.vsix` artifact (`.github/workflows/build.yml`)
- [x] Tag-triggered release workflow: `v*` tag → build, test, **draft** GitHub Release with the `.vsix` and CHANGELOG notes
- [ ] `publishManifest.json` + `VsixPublisher.exe` publish step (manual approval) — prepared: `marketplace/` (manifest, overview, `publish.ps1`) and `.github/workflows/publish.yml` (runs when a release is published, waits for approval in the `marketplace` environment). Still needs the publisher ID (variable `MARKETPLACE_PUBLISHER`), the `VS_MARKETPLACE_PAT` secret and the environment's reviewers
- [x] CHANGELOG.md, issue templates, CONTRIBUTING.md
- [ ] Publish to Visual Studio Marketplace as **Claude Code for Visual Studio — Token Saver**

Tag `v1.0.0`.

---

## Phase 6 — Feature parity with the VS Code Claude Code panel

CLI capabilities were probed with control requests, which make no model call (Claude Code 2.1.316):
`set_permission_mode` ✅, `set_max_thinking_tokens` ✅, `get_context_usage` ✅, `rewind_files` exists but
needs file checkpointing ("File rewinding is not enabled").

- [x] Modes: Manual / Edit automatically / Plan / Auto, mode chip, Shift+Tab, switch mid-session
- [x] Model chip with effort and an extended-thinking toggle
- [x] `/` actions palette with filter (context, model, mode, session, custom commands, settings)
- [x] `+` menu: upload from computer, add context, attach current file / Error List, browse the web
- [x] Header with session title, sessions and new-session icons; sessions popover with search and ages
- [x] Send turns into Stop while answering; Esc stops
- [x] Empty-session message (own icon; no Anthropic logo or mascot)
- [x] Bookmarks: bookmark button under each answer, bookmarks side panel
- [x] Sessions: rename (`--name`) and archive; show the resumed conversation's earlier messages
  (verified with 2.1.317: `--name` writes `custom-title` entries; the CLI's own titles are `ai-title` entries)
- [x] Export conversation (Markdown), from the CLI's session file (verified with 2.1.317: `--resume` in print
  mode keeps the session id, so the file holds the whole conversation)
- [x] Context usage breakdown from `get_context_usage` in the meter (response format captured from 2.1.317 in
  `Fixtures/cli-2.1.317-context-usage.jsonl`)
- [x] Rewind (enable file checkpointing, then `rewind_files`). Verified with 2.1.317: the env var
  `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=1` turns it on; the CLI keeps the `uuid` we put on a user message,
  which `rewind_files` takes; a dry run lists files and line counts. Files only, the conversation is not rewound.
- [ ] Voice input (Windows dictation) — **on hold**
- [x] Images: paste screenshots into the prompt (send as image content). Verified with 2.1.317: base64 `image`
  blocks in a stream-json user message work. Scaled to 1568 px (the API's own limit), PNG or JPEG under 5 MB.
- Not possible locally: the "Web" sessions tab (cloud sessions are not exposed to the local CLI)

---

## Phase 7 — More of the VS Code panel (from the Claude Code CHANGELOG up to 2.1.317)

Compared against the `[VSCode]` entries; the version in brackets is where the VS Code panel got it.

**Small**
- [x] Copy response button under each answer, and a `/copy` action (2.1.277)
- [x] Option previews in question cards: show `AskUserQuestion`'s `preview` (2.1.286); the chosen preview goes back in `annotations`
- [x] Strip invisible Unicode formatting and tag characters from prompts, with a notice; mark pastes over 800 characters (2.1.280)
- [x] Compaction as a collapsible card with the summary (2.1.69); also in redrawn sessions and exports
- [x] "Not responding" status when the CLI has sent nothing for 60 s during a turn (2.1.83)
- [x] A message sent while Claude is working waits at the bottom until Claude starts on it (2.1.275)
- [x] Optional message timestamps, with a date line where the day changes (2.1.284 / 2.1.290)
- [x] Syntax coloring in code blocks (2.1.311): C#, JS/TS, Java/Kotlin, C/C++, Go, Rust, Python, PowerShell, shell, SQL, JSON, YAML, CSS, XML/HTML

**Medium (token saving first)**
- [x] Usage-limit banner with the reset time (2.1.84 / 2.1.312), from the CLI's rate-limit events
- [x] Skills dialog with token estimates and on/off (2.1.280); MCP servers dialog (2.1.70 / 2.1.261). Read with `get_skills_dialog` / `mcp_status`; skills set with `claude edit-skill-overrides --json` then `reload_skills` (where it writes not verified: the CLI refuses inside a Claude Code session); servers with `mcp_toggle` / `mcp_reconnect`
- [x] Fork a session (`--fork-session`, 2.1.19; verified with 2.1.317: new id and file, history kept, original unchanged); Esc twice opens a rewind picker (2.1.83)
- [x] Session list: auto-archive inactive sessions, status filters, groups, marker for sessions open elsewhere (2.1.229–2.1.265). "Open elsewhere" comes from the CLI's registry `~/.claude/sessions/<pid>.json` (live processes only); filter is All / Open / Closed
- [x] Plan as a full Markdown document with comments (2.1.70): opened in the VS editor; added and deleted lines become feedback on Request changes
- [x] Focus view: hide tool rows behind a per-turn summary (2.1.221); here one summary row per run of consecutive tool calls, live "running …"

**Large**
- [x] Subagents: agent map, background tasks, Run in background, Stop per agent (2.1.269–2.1.287). From the CLI's `task_started` / `task_progress` / `task_updated` / `task_notification` / `background_tasks_changed` system messages; Stop is `stop_task {task_id}`, Run in background is `background_tasks {tool_use_id}` (verified with 2.1.318, capture in `Fixtures/cli-2.1.318-agents.jsonl`). When a background task finishes, the CLI starts a turn by itself (`init` comes at the start of every turn); the panel says so and keeps the per-prompt bookkeeping in step
- [x] Accept or reject each change in the diff (2.1.275). Here a review card in the chat (Manual mode, 2+ changes) with Open diff for the whole file. The accepted changes go back as `updatedInput` (the CLI writes exactly that, verified with 2.1.318); since its tool result then only says "updated successfully", a PostToolUse hook registered with `initialize` adds which changes were rejected as `additionalContext` (`hook_callback`, verified with 2.1.318: Claude reported the kept line and did not retry)
- [x] On-demand diagnostics: Claude reads the Error List when it wants, through an MCP server in the extension (2.1.285). An in-process "sdk" server declared in `--mcp-config`; the CLI sends its JSON-RPC as `mcp_message` control requests (verified with 2.1.318, including a real `tools/call`). Tool `mcp__visualstudio__get_error_list`, read-only, in `--allowedTools`
- [x] Dialogs: Status, Permission rules, Hooks, Memory/Instructions, Output styles, Account & usage (2.1.120–2.1.280). Control requests `get_status`, `list_permission_rules`, `get_hooks_listing`, `get_memory_dialog`, `get_usage`; output styles from the `initialize` answer, set with `update_settings {source: "localSettings"}` like /config (verified with 2.1.318, capture in `Fixtures/cli-2.1.318-dialogs.jsonl`). Rules and hooks are read-only here (the CLI has no request to change them); each opens its settings file. Note: project-level allow rules were not listed by the CLI in an untrusted temp folder
- [x] Accessibility: screen reader announcements, "Focus last message" command (2.1.236 / 2.1.268 / 2.1.274). A UIA live region (`LiveRegionChanged`, only raised when a screen reader listens); Focus Last Message is a VS command (`Ctrl+Alt+K, M`) and a `/` action. Not yet tried with Narrator / NVDA in the experimental instance

Left out: Claude in Chrome, Remote Control, plugin marketplace, gateway sign-in, Python venv activation, LaTeX,
telemetry and editor-group settings, reopen closed tab, continue after reload.

---

## Phase 8 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.300–2.1.318)

Entries that change what the extension sees from the CLI.

- [x] **Sign-in expiry notice** (2.1.318). The stream-json `result` carries `credential_expires_at` (seen in
      `Fixtures/cli-2.1.318-thinking.jsonl`); the parser ignores it today. Read it into `TurnCompleted`, show
      "Sign-in ends in N days" in the meter from 10 days out (the terminal's threshold, which `-p` only prints to
      stderr in text mode). When the login has expired, the CLI's stderr error names `claude auth login` and
      `claude setup-token`: turn that into a clickable "Run `claude auth login`" instead of a plain error line
- [x] **Warn when the CLI is older than verified**. Nothing checks the version, but much depends on 2.1.316–2.1.318
      (`stop_task`, `update_settings`, `init` every turn, checkpoint variable, `~/.claude/sessions/<pid>.json`).
      Read `claude_code_version` from `init`; below 2.1.318 show one line: "Some features need Claude Code
      2.1.318+ — run `claude update`". Auto-resume (`ClaudeSession.EnsureRunning`) also gains from resume fixes in
      2.1.300 (after compaction), 2.1.311 (transcript order) and 2.1.318 (`--continue` losing history)
- [x] **Fork subagents** (2.1.302: on by default in `-p`). `Agent` calls may now carry `subagent_type: "fork"`,
      which `ToolCallView` and the agents panel would label `fork(…)`. Capture a fixture, give it a friendly
      label ("Fork"), and check the agents panel handles it. Note: a subagent's own Agent calls now run in the
      foreground by default in `-p`

---

## Phase 9 — Claude Code 2.1.319 (no changelog; checked by running it)

The cached and attached changelogs stop at 2.1.318, so 2.1.319 was checked with the live smoke tests and fresh
captures compared with the 2.1.316–2.1.318 fixtures. All live tests pass (`--live-dialogs`, `--live-mcp`,
`--live-edit-review`, `--live-haiku-router`, `--live-setup`, `--live-session`, `--live` in acceptEdits). Plan mode
still answers with Sonnet when Haiku is asked for (`--live` in plan mode), as in 2.1.316.

- [x] Note in Options and in the status table that plan mode still answers with Sonnet on 2.1.319
- [x] **"Thought for" from the CLI**. Assistant messages with a thinking block now carry `thinking_duration_ms`
      (e.g. 209 ms in a 6.9 s turn; capture in `Fixtures/cli-2.1.319-thinking-duration.jsonl`). It arrives just
      before the block's `content_block_stop`, so the strip can use it in place of its own timer, which also
      counts the gap before the next block. Keep the timer for CLIs that do not send it
- Not adopted: `result.origin` (`{"kind":"task-notification", …}`) marks a turn the CLI started after a background
  task finished. It only arrives at the end of the turn, so the guess at `init` (no prompt of ours waiting) is
  still needed, and the fork capture shows the two agree

---

## Phase 10 — Use the CLI's structured tool results (2.1.319)

Tool-result messages carry more than the text Claude sees: `tool_use_result` (the tool's structured output) and
`tool_result_meta` (how the call was approved). Captures in `Fixtures/cli-2.1.319-edit-write.jsonl` and
`Fixtures/cli-2.1.319-permission-decisions.jsonl`.

- [x] **Exact diffs for Edit / Write**. `tool_use_result.originalFile` is the file before the change (`type:
      "create"` for a new file). When it differs from the copy we read as the call arrived (a fast tool can beat
      it), it replaces that copy for the lines under the row, the Diff button and Files changed
- [x] **Why a call ran without asking**. `tool_result_meta[].permission_decision` (`decision: "accept"`, `source:
      "config"`, `reason_type`: `mode` for acceptEdits, `rule` for an allow rule or `--allowedTools`,
      `subcommandResults` for a shell command whose parts all match rules). Shell and file-change rows say
      "allowed by a rule"; shell commands run on the mode alone say "allowed by the mode" (edits in Accept edits
      mode stay quiet, it would be on every one)
- [x] **Multi-select answers verified**: `--live-ask` answers through `AskUserQuestionTool.BuildAnswer`; with
      2.1.319 the CLI passed "Apple, Banana" to Claude, the same form as its own dialog
- [x] **Answer cut off at the output limit**: the CLI ends the turn as an error result (`terminal_reason:
      "api_error"`, "exceeded the N output token maximum"); the chat now explains it, and any end reason other
      than `completed` is logged in the Output pane
- [x] **Exact ⎿ lines for Read, Glob and Grep** from `tool_use_result`: Read's `file.numLines/startLine/
      totalLines` ("Read 20 of 301 lines (from line 10)"), Glob's and Grep's `numFiles` ("Found 2 files"), Grep
      content's `numLines`. Capture: `Fixtures/cli-2.1.319-tool-data.jsonl`
- [x] **Skills in the `/` menu**: names in both `skills` and `slash_commands` of `init` (runnable by you; built-in
      terminal commands and Claude-only skills stay out). Verified that a project skill runs from stream-json
- [x] **Subagents per turn**: `subagent_stats` in the result: "· 2 agents" in the footer; refused (depth,
      concurrency, budget), failed or system-stopped agents get one line in the chat
- [x] **Why a call was blocked**: a rejected `config` decision in `tool_result_meta` (a deny rule `Bash(echo *)`
      gave `reason_type: "subcommandResults"`, `non_execution_kind: "permission-rule"`): the row says "denied by a
      rule" (or "by the mode") in red. Capture: `Fixtures/cli-2.1.319-deny-rule.jsonl`
- [x] **Extra usage in the limit banner**: `rate_limit_event`'s `overageStatus` / `overageDisabledReason`. A reached
      limit says "Extra usage is off (no credits left)." when nothing carries you past it; the tooltip lists it

---

## Phase 11 — Session notes and token tips

Things that change what a session costs or does without you seeing them. The CLI's `init` (every turn) says how
the session is billed, which MCP servers are up, which permission mode and output style apply; tool results say
how much of a file was read. Each note is said once per conversation (`SessionNotices`, `ℹ` in the chat, and the
Output pane).

- [x] **Billing**: `apiKeySource` other than `none` (e.g. `ANTHROPIC_API_KEY`) means an API key pays per token, not
      the claude.ai plan
- [x] **MCP servers needing attention**: `mcp_servers[].status` `needs-auth` / `failed` (2.1.319 also sends
      `pending` while connecting: not mentioned; a later init says if it failed)
- [x] **Permission mode not the one asked for**: `permissionMode` differs from the mode the chat started with.
      Checked on a process's first init only (approving a plan leaves plan mode on purpose); the picker follows
- [x] **Output style that adds tokens**: Explanatory and Learning add explanation to every answer (`output_style`;
      Concise and custom styles get no note)
- [x] **Whole large file read**: a tip, once per conversation, when Claude reads all of a large file (Read's
      `numLines == totalLines` from line 1, ≥ 8k tokens); tool results now know their subagent (`parent_tool_use_id`)
- [x] **Commands that print nothing by design**: Bash `noOutputExpected` → "Done" instead of "(No output)". The
      CLI's "Shell cwd was reset to …" note (2.1.319 puts it in the result text, even for `mkdir`) is not output.
      Capture: `Fixtures/cli-2.1.319-no-output.jsonl`

---

## Phase 12 — More token tips

Help that saves tokens, from what the extension already sees (usage, tool calls, results).

- [x] **Prompt cache timer**: usage says whether the cache was written for 5 minutes or 1 hour
      (`cache_creation.ephemeral_1h_input_tokens`; 2.1.319 on a claude.ai plan used 1 hour), and each request keeps
      it again. While idle, a line under the meter says when a 1-hour cache is about to expire, then that it has, with
      what the next prompt costs
- [x] **Repeated read**: Claude reads a file again that it already has, with no change in between (`ReadTracker`:
      whole reads only, same content hash, no Edit / Write of it and no compaction since; main agent only)
- [x] **Large command output**: point to the output-trimming hook when it is not installed (≥ 5k tokens from a
      main-agent shell command; `TokenSaverSetup.IsInstalled` looks for the hook's marker)
- [x] **Conversation cost warning**: an option to be told once when a conversation passes an amount
      (*Warn at conversation cost (USD)*, default 5: once per multiple, from the CLI's `total_cost_usd`)

---

## Phase 13 — See what each answer and prompt costs

- [x] **Thinking tokens in the footer**: `· 1.2k thinking` from `output_tokens_details.thinking_tokens`
- [x] **Cache share in the footer**: `· 94% cached`, the turn's input served from the prompt cache (from 1k input tokens)
- [x] **Prompt size**: the input box shows `~N tokens` for a long prompt; a huge paste suggests a file reference
      (from 1k / 8k tokens, on the route line under the input box)
- [x] **Base context tip**: MCP tools or memory files that take a big slice of every request (from
      `get_context_usage`: MCP tools / memory files ≥ 5k, skill listings ≥ 10k; the biggest, with where to cut it)
- [x] **Copy session summary**: `/` → tokens, cost, cache, turns as text (`SessionSummary`, from the meter)
- [x] **File sizes in the `@` picker**: each file's size in tokens (`FileIndex.SearchWithSizes`, read for the hits only)
- [x] **Resume note**: a session idle past its cache lifetime sends everything at full price on the next prompt
      (idle over 1 hour, from the file's last write; context from the last main answer's usage in the session file)

---

## Phase 14 — Tips under control, and where the tokens went

- [x] **Token tips option**: Options → Token saving → *Token tips* (on) turns off all token tips and the prompt cache
      line; they then go to the Output pane only. Billing, MCP server and permission mode notes stay
- [x] **Thinking tokens in the usage log and savings report** (`thinking` in each log line, written only when
      above 0; the report prices it at each turn's output price)
- [x] **Tool results by tool**: the session summary says which tools filled the context (`TokenMeter.ResultTokensByTool`)
- [x] **Costliest turns in the savings report**: the five dearest turns, with model, tokens and prompt (if logged;
      else the route reason)

---

## Phase 15 — What the prompt cache saved

Each token read from the cache costs the cache-read price instead of the full input price; writing it costs extra
(5-minute write: the cache-write price, 1.25x input; 1-hour write: 2x input, which Claude Code uses on a claude.ai
plan). Saved = reads × (input − cache read) − 5-minute writes × (cache write − input) − 1-hour writes × input, at
the prices of the model that answered (`ModelPrice.CacheSavedUsd`).

- [x] **Footer**: `· 90% cached (saved $0.018)` per answer (from $0.001)
- [ ] **Meter**: the conversation's total in the meter's tooltip
- [ ] **Savings report**: `Saved by the prompt cache` next to routing and file references
- [ ] **Session summary**: the total in / → Copy session summary

---

## Working in Visual Studio — daily loop

1. Core logic change → edit in `ClaudeVS.Core` → run tests (`ClaudeVS.Core.Tests` → Set as Startup → Ctrl+F5, or `dotnet run` in its folder).
2. UI / VS change → `ClaudeVS` as startup → F5 (experimental instance, `/rootsuffix Exp`).
3. Broken experimental instance → run *Reset the Visual Studio 2022 Experimental Instance* from the Start menu.
4. Keep VS-specific code thin; anything testable goes into `Core`.

## Branching

- `main` — always builds
- `feature/<phase>-<topic>` — e.g. `feature/p2-router-tuning`
- One PR per checklist group; tick the boxes in this file in the same PR

## Risks to watch

| Risk | Mitigation |
|---|---|
| CLI stream-json protocol changes between Claude Code versions | Parser ignores unknown fields; add a test per new event shape; log raw lines |
| PreToolUse `updatedInput` behaviour differs from expectation | Verify in 2.4; fallback = deny with suggested quiet command |
| Model switch busts prompt cache and costs more | Sticky routing (already in router); measure in savings report |
| VS 2026 API differences | Build and smoke-test on both versions every phase |
| Trademark/affiliation confusion | Keep "community project, not affiliated with Anthropic" in README + Marketplace |
