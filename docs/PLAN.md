# claude-vs-tokensaver — Development Plan

Plan for taking the v0.1 scaffold to a published Marketplace extension.
Tick the boxes as you go. Each phase ends with something you can run.

---

## 0. Status today (v0.1 scaffold)

| Area | State |
|---|---|
| `ClaudeVS.Core` (CLI session, parser, router, meter, context, setup) | ✅ Written, 16/16 tests pass |
| `ClaudeVS` VSIX (package, tool window, commands, options) | ✅ Builds clean (0 warnings); not yet run in the experimental instance |
| Real end-to-end run against `claude` CLI | ⚠️ Core verified against CLI 2.1.316 (`--live` smoke test): sessions, streaming, usage, cost, cache OK; Haiku routing works in acceptEdits ($0.002 vs $0.042 per turn on Sonnet); plan mode answered with Sonnet until Phase 67 asked for Haiku by its full ID (all live smoke tests pass with 2.1.328, plan mode included). VS UI not run yet |
| Markdown transcript, diffs, history, question cards | ❌ Not started |

---

## Phase 1 — Get it building and running (1–2 days)

**Goal:** F5 opens the experimental instance, the chat window appears, one prompt gets an answer.

The checks left in Phases 1–5, and the newer ones to look at in VS, are on one page: [MANUAL-CHECKS.md](MANUAL-CHECKS.md).

### 1.1 Environment
- [ ] Visual Studio 2022 17.14+ (or VS 2026) with workload **Visual Studio extension development**
- [x] .NET 8 SDK (for the Core tests) — the Core tests run here (2026-11-11)
- [x] Claude Code CLI installed and logged in — `claude --version` and `claude -p "hi"` work in a terminal (2.1.322, 2026-11-11)
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
- [x] Version 0.9.0, released as a Marketplace **preview** (`<Preview>true</Preview>` in `source.extension.vsixmanifest`;
      `publishManifest.json` has no such field). CHANGELOG `[0.9.0]` opens with highlights, which the release workflow
      puts in the GitHub release; `marketplace/overview.md` says it is a preview and lists the cost features
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
- [x] **Meter**: the conversation's total in the meter's tooltip (`TokenMeter.CacheSavedLine`)
- [x] **Savings report**: `Saved by the prompt cache` next to routing and file references (the usage log records
      `cacheWrite1h`; older lines count as 5-minute writes)
- [x] **Session summary**: the total in / → Copy session summary (`Cache hits: 75% (saved $0.49)`, from $0.01)

---

## Phase 16 — Where the money goes, across conversations

- [x] **Cache-break note**: a main-agent request that reads less than half of the previous request's context from
      the cache while it is still warm (no compaction in between, ≥ 10k tokens missed) wrote the conversation to the
      cache again. Once per conversation the chat says how many tokens and what that cost more than reading them
      (at the write price: 2x input for a 1-hour write), and the likely cause: the model changed (each model has its
      own cache), else something at the start of the conversation (CLAUDE.md, MCP tools, settings) (`CacheBreakWatch`;
      later ones go to the Output pane)
- [x] **Cost in the sessions list**: each past conversation shows what it cost, `$0.42 · 3h` (`SessionCost`: its session
      file and its subagents' files, each API message once, per model at Options → Pricing; read in the background and
      kept until the file changes)
- [x] **Daily cost warning**: *Warn at daily cost (USD)*, default 20: once per multiple, today's spend in all chat windows
      (`DailyCostWatch`: today's earlier turns from the usage log set the starting step, then each turn's cost)
- [x] **Spend by day in the savings report**: turns, tokens, cost, cache hits and cache savings per day, with a bar for
      the cost (the dearest day full), the last 14 days with turns (`SavingsReport.ByDay`; replaces *Cache hit rate by day*)

---

## Phase 17 — Spend at a glance

- [x] **Next prompt's price**: while typing in a conversation with context, the line under the input box adds
      `~$0.02 to send (context cached)`, or why it costs more: the cache expired, or the prompt goes to another model
      (which has no cache for it). The least the prompt costs: its first request's input (context at the cache-read
      price when warm, else written again; the prompt written to the cache), not the output or later tool calls
      (`NextPromptPrice`)
- [x] **Month and solutions in the savings report**: `This month: $12.30 so far, ~$41.00 at this pace by Nov 30`
      (the projection from day 3); for all solutions, `By solution:` with turns and cost per folder, dearest first
      (top 10; `SavingsReport.MonthLine`, `SolutionsText`)
- [x] **Sessions list totals**: the footer says `12 sessions, $41.20 in total (API prices)` for the sessions shown
      (`SessionCost.TotalLine`); a **Newest first / Costliest first** entry next to the filter switches the order
- [x] **Usage CSV export**: Tools → Claude Code → *Export Usage as CSV…*, one row per logged turn (all solutions,
      oldest first; UTF-8 with BOM for Excel, invariant numbers, formula-guarded text cells; `UsageCsv`)

---

## Phase 18 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.319–2.1.322)

The changelog now covers 2.1.319 (Phase 9 had to check it by running it) up to 2.1.322, the version installed
here. Entries that change what the extension sees, prices or sends. Terminal-only, cloud, gateway, Remote Control
and desktop entries are left out.

- [x] **Fable prices** (2.1.321: Claude Fable 5.2, `claude-fable-5-2`, $8 / $40 per Mtok, cache read $0.20).
      `PriceTable.Family` knew only opus / sonnet / haiku, and unknown models fall back to Sonnet's prices, so a
      Fable turn showed a quarter of its cost in the footer, meter, sessions list, next-prompt line, cache savings
      and savings report. Now a `fable` family (default `8, 40, 10, 0.2`, cache write 1.25x input) with an
      Options → Pricing row. The savings report's "Opus equivalent" stays at Opus prices, so Fable turns lower
      the routing savings, which is right: Fable costs twice as much as Opus
- [x] **Fable in the model picker** (2.1.321: Fable 5.2 is the default `fable` alias; saved Fable 5.1 picks move to
      it). `ModelChoice.Fable` (last, so stored choices keep their meaning) runs `--model fable`; neither router
      picks it (the Haiku router's schema has no `fable`, and such an answer is dropped). The picker entry and the
      line under the input box say "Fable: 2× the Opus price" (`PriceTable.VersusOpus`, from Options → Pricing).
      The meter shows Fable in amber; the cache-break note gets "model changed" from the family. Verified with
      2.1.322: `--model fable` is answered by `claude-fable-5-2`, and `PriceTable` gives the CLI's own
      `total_cost_usd` exactly ($0.011432 for 2 in, 5 out, 701 1-hour cache-write tokens)
- [x] **Fast mode** (2.1.321: when fast serving fails it now drops to standard speed on the same model first).
      Found while verifying Fable: the result has `usage.speed` ("standard") and `fast_mode_state` ("off", with
      `fast_mode_disabled_reason: "sdk_opt_in_required"` in `-p`), so the extension's sessions run at standard speed
      unless fast mode is opted into. The API's fast mode page (2026-11): Opus 5.5 $8 / $40 and Fable 5.2 $16 / $80,
      2x standard, with the cache multipliers on top (so every token type doubles); `usage.speed` says which speed
      ran; fast and standard speed do not share cached prefixes. Now `TokenUsage.FastTokens` (from `usage.speed`,
      summed apart because a turn can fall back mid-way) prices fast tokens at `ModelPrice.FastMultiplier` (2x) in
      the meter, sessions list, cache savings and savings report; the usage log records `fastTokens` (only when
      above 0); the footer says `· fast` or `· partly fast`; the cache-break note names fast mode starting or
      stopping as the cause. Not captured live: no fast turn could be run from `-p` without the opt-in
- [x] **Verify with 2.1.322 and raise `CliVersion.Tested`** from 2.1.318. All live tests pass with 2.1.322 (about
      $0.17 in all): `--live` in acceptEdits (Haiku, $0.002 then $0.004, cache reused), `--live-session` (meter =
      `/context` to the token, `set_model`, cost across `--resume`, `/compact`), `--live-dialogs`, `--live-mcp`,
      `--live-edit-review`, `--live-haiku-router` (4 routes, $0.0007; the router and the local rules agree on the
      model 3 of 4), `--live-setup`, and `--live-ask` (multi-select answered "Apple, Banana"). `--live` in plan mode
      still answers with Sonnet when Haiku is asked for (both turns `claude-sonnet-5-5`), as since 2.1.316. 
      `CliVersion` now has two numbers: `Required` (2.1.318, below it the chat warns once) and `Tested` (2.1.322,
      shown in the Output pane). Raising one number for both would have warned users on 2.1.318–2.1.321 about
      features that work there
- [x] **Base context tip after the `/context` changes** (2.1.319: MCP tools that load on demand share one row per
      server until called; 2.1.321: tool definitions no longer overcounted without exact counts). Captured with
      2.1.322 (`--live-mcp` now prints the breakdown; sanitized in `Fixtures/cli-2.1.322-context-usage-mcp.jsonl`):
      a new `MCP tools (deferred)` category (`kind: "deferred"`), and each `mcpTools[]` entry has `isLoaded`. The
      extension's own Error List tool is deferred (53 tokens, `isLoaded: false`). `ContextUsage` counted every listed
      tool as in the window, so the "MCP tools take ~N tokens of every request" tip and the MCP servers footer
      overstated deferred servers. Now `McpToolTokens` counts loaded tools only (a missing `isLoaded`, as in
      2.1.317, counts as loaded) and `McpDeferredTool*` the rest; the MCP servers footer says how many load on
      demand. The meter's breakdown already listed deferred categories apart. Also found: the CLI's `percentage`
      is rounded to a whole number (25,426 / 1M = 2.54% shows as 3), which explains the meter's 2.5% vs `/context`'s
      3.0% in `--live-session`; the token counts match exactly
- [x] **Edit-review hook after an interrupt** (2.1.319: PostToolUse hooks now run, with up to 10 s, on tool calls
      that finish after a new message interrupts the turn). By the code: a `hook_callback` is answered whenever it
      comes, with the note for its own `tool_use_id`, and never reaches the chat, so a late one cannot land on a new
      turn. The extension never interrupts with a new message (Stop sends `interrupt`; prompts wait), so the
      changelog's path does not arise here. What did: `--live-edit-review-interrupt` (new; Stop right after a
      reviewed approval) showed that with 2.1.322 Stop ends the Edit as `aborted_tools` with "stopped while running…
      it may have" changed the file, and **no** PostToolUse hook runs, although the reviewed edit was written. Claude
      then did not know the edit was in, nor which change was rejected. Now a note still waiting when the turn ends
      is kept (`ClaudeSession.TakeStrandedNotes`) and sent ahead of the next prompt as its own text block
      (`[Note from the editor] …`), only if the file holds exactly what the review approved (a check from the UI;
      a late hook takes the note first, so it is given once). Resume and the sessions list leave such blocks out.
      Live: Claude then answered "Line 4 stayed as "delta" … line 2 was written". Not covered: an edit approved
      without review (Allow), where Claude is told the same uncertain thing; the file is in its approved state
- [x] **Long prompts** (VS Code 2.1.319: prompts over 50,000 characters were cut off silently). No limit found:
      `--live-long-prompt` (new) sent 60,000 and 200,000 characters through stream-json with 2.1.322, each ending
      in a unique code word, and both came back (input 48k and 101k tokens). The extension's prompt box has no
      length cap and sends the text unchanged, so nothing to change; the huge-paste suggestion (from ~8k tokens)
      stays. Side finding: this numbered filler ran ~2.7 characters per token, so `PromptSize.Estimate`'s
      chars / 4 undercounted it by about a third (prose and code too, as Phase 19 measured: 2.97 and 2.75). First run used plan
      mode by mistake and was answered by Sonnet ($0.47); the test now runs in Accept Edits on Haiku
- [x] **Billing note: Console sign-in** (2.1.319: when the claude.ai login ends, Claude Code can continue on the
      `ant auth login` sign-in, which bills a Claude Console organization). Also (VS Code 2.1.322) say once when both
      `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_API_KEY` are set. Probed with 2.1.322 and **fake** values (no model
      call is billed: the CLI gets a 401): `init.apiKeySource` is `ANTHROPIC_API_KEY` for a key, still
      `ANTHROPIC_API_KEY` with both, but `none` for an auth token alone, the same as a claude.ai sign-in, so the
      existing billing note missed tokens. `init` has nothing else on it; in `-p` the CLI's only warning goes to
      stderr and is about connectors. The /status rows (`get_status`, no model call) do tell: "Auth token:
      ANTHROPIC_AUTH_TOKEN", "API key: ANTHROPIC_API_KEY", or "Login method" for a sign-in. Now each process's first
      init reads /status once and `SessionNotices.Auth` says: both set (the API key pays; unset one), a token
      alone (billed to whoever issued it), or a "Login method" naming the Console (billed to that organization).
      Not captured: the `ant auth login` fallback itself (`ant` is not installed here), so the Console rule
      matches on the word only
- [x] **Slash commands after a leading space** (2.1.319 fixed them being sent as plain text). Leading spaces were
      already fine here: sending trims the prompt, and the list and hint used `TrimStart`. But the send path decided
      "command or prompt" before removing invisible characters, which happens later: `​/compact` (a zero-width
      space from autocomplete or a paste) was routed as a prompt, wrapped with context, and so reached Claude as
      text. Now `SlashCommands.IsCommand` (invisible characters out, then leading whitespace) decides for sending,
      the hint and the command list alike, and a command goes out from its `/`

---

## Phase 19 — Loose ends from Phase 18

Things Phase 18 turned up, picked on 2026-11-10.

- [x] **Stop after a plain Allow edit.** Phase 18 fixed Stop during a reviewed edit. An edit approved with Allow, or
      run without asking in Accept Edits, gets the same "stopped while running … may have been written" result with
      no hook. Now any Edit / Write result with that text (`StoppedEdit.IsStopped`, an error result) makes the chat
      read the file: `StoppedEdit.Check` says Written, NotWritten or Unknown, from whether the old and new text are
      in the file (our "before" copy can be taken after a fast write, so an exact match only confirms). When it
      knows, Claude is told with the next prompt (`ClaudeSession.AddNextPromptNote`, the same `[Note from the
      editor]` block; a reviewed-edit note for the same call takes precedence). Live (`--live-edit-stop-allow`,
      new): a bare "its change is in the file" was **not** believed ("I never got a result back, so I can't
      confirm"); the note now says Visual Studio read the file after the stop and quotes what it has where the old
      text was, and Claude answered "Yes, every change you asked for is in place: line 2 now reads BETA …"
- [x] **Better prompt-size estimate.** `PromptSize.Estimate` counts chars / 4; dense text ran at ~2.7 characters
      per token in `--live-long-prompt`. Learn the ratio from real turns, so the size and "to send" lines under the
      input box come closer. Measured first (`--live-prompt-tokens`, new; 2.1.322, Haiku 5.5): 8,000 characters of
      prose took 2,711 tokens (2.97 per token), code 2,903 (2.75), numbered lines 2,944 (2.71), so chars / 4 was
      ~30% low for all text, not only dense text. The CLI's own `userMessageTokens` is no help: it grew by ~2,000
      for each, i.e. it is chars / 4 too. A turn adds ~30 tokens of framing besides the prompt; the second turn of a
      process added ~2,000 once. Now `TokenEstimate` (3.0 to start) replaces every chars / 4 in the extension
      (prompt size, next-prompt price, tool-result sizes, `@` picker, tips, activity strip, savings report, CSV), and
      `PromptTokenLearner` in `TokenMeter` refines it from real turns: text-only prompts of 2,000+ characters that
      start a turn, from the third turn of a process, not across a compaction, 1.5-6 characters per token, weighted
      (the start counts as 6,000 tokens of evidence). Live, on the same events: learned 2.97, 2.77, 2.73 → 2.89; the
      estimates came within 2-7% of the real count (before: 27-32% low). Not kept across Visual Studio restarts
- [x] **Fable and fast mode in reports.** The savings report and the CSV don't show what Fable or fast mode added.
      Add what they cost above Opus / standard speed to the report, and a `fast_tokens` column to the CSV. Now the
      report has `Paid extra for Fable: $… (N turns, above the same tokens on Opus)` and `Paid extra for fast mode:
      $… (N turns, above standard speed)`, each only when there were such turns, at Options → Pricing
      (`SavingsReport.FableExtraUsd`, `FastExtraUsd`); the CSV has `fast_tokens` after `cache_write_1h`
- [x] **One command for all live tests.** `--live-all`: every live test in order, never in plan mode by mistake,
      with the total cost at the end (replaces the shell script used in Phase 18). `LiveSmoke.RunAll` runs the 12
      live tests (a throw counts as a failure) and prints PASS / FAIL, cost and time per test, then the total; exit
      code 0 only when all pass. Every test now makes its sessions with `NewSession`, which adds each turn's share of
      the CLI's `total_cost_usd` to `LiveSmoke.Spent` (the Haiku router's own process is added apart); a single
      `--live-…` test prints it too. Found on the first run: after `--resume` the CLI **carries on** the session's
      total (2.1.322: $0.0518 before the relaunch, $0.0539 after a $0.0021 turn), so counting per process counted
      `--live-session` twice; the count is now kept per Claude session id. `TokenMeter` was right either way (a
      continuing total adds no offset), only its comment said the total restarts at 0. `--live` now defaults to
      Accept Edits (it was plan mode, where the CLI answers with Sonnet, ~20x the cost); `--live raw Plan` still
      checks that, with a warning. `--live-ask` now fails when no question was asked. With 2.1.322: 12 of 12
      passed, $0.18, 4.6 min

---

## Phase 20 — Prices that match the bill, and plan mode

Picked on 2026-11-10.

- [x] **Check prices against the CLI.** The API reference lists Sonnet 5.5 cache reads at $0.10 per Mtok; the
      default is $0.20. Compare every default (Opus, Sonnet, Haiku, Fable) with Claude Code's own `costUSD` from
      real turns, fix what differs, and keep the captures as a test. Two turns per model with 2.1.322 (trimmed
      process, a ~4.5k-token system prompt cached for 1 hour, $0.19 in all): Haiku, Opus and Fable match the CLI to
      the last digit; Sonnet came out $0.0005684 high, exactly 5,684 cache-read tokens × $0.10, so its cache read is
      $0.10. `DefaultSonnet` is now `2, 10, 2.5, 0.1`; a Sonnet price saved in Options that equals the old default
      is upgraded when Options load (`PriceTable.UpgradeStored`; Visual Studio saves every value on OK, so a saved
      old default was not chosen), a price you typed stays. The captures are `Fixtures/cli-2.1.322-model-costs.jsonl`
      and `Test_Prices_MatchTheCliCosts` prices them. Not checked: 5-minute cache writes (Claude Code on a
      claude.ai plan writes for 1 hour) and Haiku above 100k tokens (next item)
- [x] **Haiku price above 100k tokens.** Haiku 5.5 costs $0.50 / $2.50 for prompts over 100k tokens (5x); a 200k-
      character turn cost $0.09 instead of ~$0.02. Price that tier, and tip when a Haiku conversation nears 100k.
      Measured with 2.1.322 (`Fixtures/cli-2.1.322-haiku-over-100k.jsonl`, $0.13): a request with more than 100k
      input tokens (input + cache reads + cache writes) is billed at 5x every price, cache reads too ($0.05); 74k
      matched the normal prices. The tier is per request, so `TokenUsage.LargeContextTokens` counts tokens of
      requests over 100k (`AsRequest`; `TokenMeter` sums them per turn from each message's usage), and
      `ModelPrice.LongContextFrom` / `LongContextMultiplier` (Haiku only) price that share. Sessions list, cache-break
      note and next-prompt price use each request's own size. The usage log and CSV record `largeContextTokens` /
      `large_context_tokens`. A tip, once per conversation, at 90k tokens of Haiku context
- [x] **Plan mode: say Sonnet before sending.** In plan mode Claude Code answers with Sonnet even when Auto picks
      Haiku, but the route hint and the "to send" price under the box say Haiku. Make them say Sonnet there, and
      keep the savings report from counting such turns as routed to Haiku. Done in the router, so everything after
      it agrees: `RouteRequest.PlanMode`, and `ApplyCacheStickiness` (the step both the local rules and the Haiku
      router's answers go through) turns a Haiku pick into Sonnet with the reason "Sonnet: plan mode answers with
      Sonnet". The hint, the price, the model switch sent to the CLI and the usage log's `routeModel` then all say
      Sonnet, so the savings report and router replay count it as Sonnet, and the after-the-fact "asked for
      haiku, but sonnet answered" warning no longer comes up for Auto. A Haiku picked by hand stays as picked, but
      the line under the box says "Plan mode: Sonnet answers, not Haiku" and prices the prompt at Sonnet
      (`ModelRouter.AnsweredBy`); switching modes refreshes that line. Not run live: the CLI side is unchanged
- [x] **Keep the learned token ratio.** The characters-per-token value learned in Phase 19 starts again at 3.0 on
      every Visual Studio restart; save it next to the usage log and load it at start. `TokenEstimate.UseStore`
      (called when the package starts) loads `%LocalAppData%\claude-vs-tokensaver\token-estimate.json` and saves the
      evidence there after each learned sample (characters and tokens seen, written to a temp file and swapped in;
      no prompt text). A missing, unreadable or implausible file (outside 1.5-6 characters per token) leaves 3.0;
      saved evidence over the cap is scaled down so new turns still move it. Listed under Privacy in the README

---

## Phase 21 — Price checks and loose ends from Phase 20

Picked on 2026-11-11.

- [x] **Live price check command.** Phases 18 and 20 checked prices against Claude Code's own costs with ad-hoc
      shell probes. Make it `--live-prices`: two turns per model, compared with the prices in use (~$0.19), kept
      out of `--live-all` because of its cost. `LiveSmoke.RunPrices`: a trimmed process per model (the Haiku
      router's flags, a ~4.5k-token system prompt), the first turn writes the cache and the second reads it; each
      turn's usage priced with `PriceTable` against the growth of `total_cost_usd`. With 2.1.322: all 8 turns
      matched to the last digit (Sonnet with its $0.10 cache read), $0.18
- [x] **Long-context extra in the report.** The savings report shows what Fable and fast mode cost extra, but not
      what Haiku requests over 100k tokens cost above the normal Haiku price. Add that line. `Paid extra over 100k:
      $… (N Haiku turns over 100k tokens, above Haiku's normal price; /compact keeps it under)`, only when there
      were such turns (`SavingsReport.LongContextExtraUsd`: each turn's cost less the same at the normal price, from
      the logged `largeContextTokens`)
- [x] **Stopped reviewed edit not written.** After Stop, a reviewed edit's note goes with the next prompt only if
      the file holds what was approved; if it was not written, Claude hears nothing. Fall back to the plain file
      check, so Claude is told it was not applied. `AddNextPromptNote` no longer drops the file check's note for a
      call with a reviewed note: it keeps it as that note's `Fallback`, which `TakeStrandedNotes` sends when the
      reviewed edit is not in the file ("was not applied: c.cs still has …"); when the file check cannot tell
      either, still nothing. Unit-tested only: in every live run so far the edit was written before Stop landed
- [x] **Subagents in the long-context count.** The meter counts requests over 100k only for the main agent; Haiku
      subagents with big contexts are billed 5x too. Count their requests where the turn's tokens include them.
      Checked against the fork capture (2.1.319): the result's `usage` is the **main agent's only** (cache read
      38,169) while `modelUsage` and `total_cost_usd` add the subagent (62,086). So the turn's tokens do not
      include subagents, and counting their requests there would be wrong; main-agent-only is right. Their tier is
      priced where their tokens are counted (the sessions list, per request; `AsRequest` since Phase 20), and the
      logged cost is the CLI's own total, which bills them correctly. Now a test pins this down
      (`Test_TurnUsage_IsMainAgentOnly`) and `UsageRecord` says so. Found on the way, for a later phase: the
      savings report compares that cost (with subagents) against "the same tokens at Opus prices" from main-agent
      tokens only, so routing savings come out low when subagents ran, and cache savings leave out subagent reads

---

## Phase 22 — What subagents cost

Picked on 2026-11-11.

- [x] **Subagents in the savings report.** Found in Phase 21: the report compares the logged cost (with subagents)
      against Opus prices for main-agent tokens only. Log subagent tokens per turn (by model) and count them in the
      report, the CSV and the session summary. The result's `modelUsage` (now parsed: `ModelUsageEntry`) is
      cumulative per model, subagents included; `SubagentUsageTracker` takes its growth over a turn less the main
      agent's usage (exact). After `--resume` the CLI carries `modelUsage` on (checked with 2.1.322: cache writes
      728 → 785 for a 57-token turn), so on a meter's first turn, with no earlier reading, it adds up the
      subagents' streamed messages instead (they carry `parent_tool_use_id`, now on the event; input and cache
      exact, output low: the message streams before it ends, 2 of 131 in the fork capture). The usage log keeps
      `agents: [{model, in, out, cacheRead, cacheWrite, cacheWrite1h}]` per turn; the report adds them to "same
      tokens at Opus prices" (an Opus main turn's own cost less its agents), to cache savings and the hit rate,
      and says `Subagents: … tokens ≈ $… (N turns; included above)`; the CSV has `agent_tokens`; the session
      summary a `Subagents:` line. Replayed from the real fork capture in `Test_SubagentUsage_FromRealFork`
- [x] **Cost per agent in the agents panel.** The panel shows each subagent's status but not what it cost: price
      its requests as they arrive and show it on its row. Each subagent message (`parent_tool_use_id` = its Agent
      call) is priced as one request at Options → Pricing, the last copy of a message kept; the row adds it after
      its tokens (`· $0.042`, `<$0.001` when tiny). Replayed from the fork capture in `Test_AgentTasks_Fork_RealCli_2_1_319`
- [x] **Tip when subagents dominate.** A one-time token tip when subagents cost more than the main agent in a turn,
      with how to keep them cheaper. After a turn whose subagents cost at least $0.05 and more than the rest of
      the turn (`total_cost_usd` less their price): one agent instead of several, a plain search, `model: haiku`
      in `.claude/agents`. `SessionNotices.SubagentsDominate`
- [x] **Subagent tokens in the footer.** The line under each answer says `· 2 agents`; add what they used, e.g.
      `· 2 agents (31k tok)`. From the meter's `LastTurnSubagentTotal`; a background agent from an earlier turn
      that worked in this one shows as `· agents 31.0k tok`. `TurnFooter.Agents`

---

## Phase 23 — Agent costs where you look

Picked on 2026-11-11.

- [x] **Agent cost in the footer.** The turn's cost includes its subagents: split it, e.g. `$0.020 ($0.012 agents)`,
      priced as the agents panel prices them. Shown from $0.001, never more than the turn, and not when the CLI sent
      no cost. `TurnFooter.Cost`
- [x] **Cost on the Agent tool row.** The `● Agent(…)` row in the conversation gets the agent's tokens and cost on its
      ⎿ line when it finishes, so they stay after the panel is cleared. On `task_notification`, under the row its
      `tool_use_id` drew: `Done · 24.6k tok · $0.042 · 3 tools`. `ToolCallView.AgentUsageLine`
- [x] **Report by agent type.** Log each subagent's type (Explore, general-purpose, fork, a custom agent) with its
      tokens, and list the cost per type in the savings report, costliest first. Logged as each kind's share of the
      turn's subagent cost (`agentTypes: [{type, share}]`), from its requests priced as they stream; the shares split
      the exact cost, since streamed output runs low. The report: `  by kind of agent: Explore $0.40 · …`, older turns
      under "not known". `AgentTypeSplit`, `AgentRequestCosts` (shared with the agents panel)
- [x] **Exact first-turn agent output.** On a meter's first turn subagent output is counted low (a message streams
      before it ends): correct it with the task's final `total_tokens` from `task_notification`. Not done that way:
      in the fork capture `total_tokens` is 24,649 while the API counted 24,514, so it would add twice the missing
      output. Instead a new session (no `--resume`) counts `modelUsage` from 0, and its first reading is exactly the
      main agent plus its subagents (8 = 4 + 4 input, 335 = 204 + 131 output): `TokenMeter.FreshSession`. Only a
      resumed conversation's first turn still adds up the streamed messages

---

## Phase 24 — Agent details, and the last uncounted output

Picked on 2026-11-11.

- [x] **Exact resumed first turn.** Keep each session's last `modelUsage` reading, so a resumed conversation's first
      turn counts subagent output exactly too (today only a new conversation's does). Kept in `model-usage.json`
      (the latest 300 sessions; `ModelUsageReadings`). The session may have run elsewhere since (the terminal), so the
      growth from a kept reading counts only when it agrees with the streamed messages on input and cache, which are
      exact in both; otherwise the streamed messages count, as before. `SubagentUsageTracker.ResumeFrom`
- [x] **Agent model on its row.** The agents panel row and the line under the Agent call say which model the agent
      ran on (e.g. `Explore · haiku`), since that decides most of its cost. From its requests' `model`, the last one
      seen (a request before `task_started` counts); the family name, or the id for another model. `AgentTask.ModelShort`
- [x] **Kinds in summary and CSV.** The session summary gets a `Subagents by kind:` line, the usage CSV an
      `agent_types` column (e.g. `Explore:0.75;general-purpose:0.25`). The summary sums each turn's split of the exact
      subagent cost (`TokenMeter.SessionAgentCostByType`); the column has the shares, 4 decimals, after `agent_tokens`
- [x] **Fork cost tip.** A one-time tip when a fork (a subagent that starts with a copy of the whole conversation)
      costs much more than a fresh agent would, with how to avoid forks (check first whether Claude Code has a
      switch for them; say only what is verified). No switch is named: `CLAUDE_CODE_FORK_SUBAGENT=0` is reported to
      turn forks off by some and not to by others, none of it official. What the capture shows is said instead: a fork's
      first request reads the same 23,917 cached tokens as the main agent's next one. Once, when a finished fork cost
      $0.05 or more: ask for an Explore agent or a plain search when the task does not need the conversation.
      `SessionNotices.ForkCost`

---

## Phase 25 — Keep subagent costs in check

Picked on 2026-11-11.

- [x] **Settle the fork switch.** Run Claude Code 2.1.322 once with `CLAUDE_CODE_FORK_SUBAGENT=0` on a tiny Haiku
      prompt and capture whether it still forks. If it works, the fork tip names it and Options gets a *No forks*
      checkbox; if not, record that. It works: asked for a fork, the Agent call fails ("Agent type 'fork' not found.
      Available agents: claude, Explore, general-purpose, …") and Claude answers without one; with the default the
      same prompt ran a fork (Fixtures/cli-2.1.322-fork-off.jsonl, scrubbed). Also seen: in 2.1.322 an Agent call
      without `subagent_type` runs as general-purpose, not as a fork, and "fork" is not in `init`'s agent list.
      `ClaudeLaunchOptions.NoForks`, `Environment()`
- [x] **Live cost warning on a row.** An agent's row in the panel turns amber once it passes a set cost (Options,
      default $0.50) while it runs, so a runaway agent can be stopped early. *Warn at agent cost (USD)*; the row's
      details turn amber (the meter's amber), its tooltip says `Cost $0.620 so far, past your warning at $0.50: Stop
      ends it.`, and screen readers hear it once as it passes. `AgentTask.CostsOver`, `CostWarning`
- [x] **Agent models in the report.** The savings report lists subagent cost per model (Haiku, Sonnet, Opus agents)
      and what the Opus agents would have cost on Sonnet. `  by model: opus $1.20 (≈ $0.72 on Sonnet) · sonnet $0.40`,
      costliest first, each agent at its own model's price from the logged `agents`. `SavingsReport.AgentByModelLine`
- [x] **Agent cost in the sessions list.** Each conversation shows its subagent cost next to its total, from the
      usage log. Taken from the CLI's own session files instead: the cost was already read from them, subagents'
      files (`<id>/subagents/*.jsonl`) included, while the usage log has no session ids. `$0.42 ($0.12 agents) · 3h`,
      the agents' part from half a cent; a message marked `isSidechain` in the main file counts as theirs too.
      `SessionCostInfo.AgentCostUsd`

---

## Phase 26 — A newer CLI, agents that stop, and the checks left to do

Picked on 2026-11-11.

- [x] **Keep up with the CLI.** Check for a Claude Code release newer than the installed 2.1.322 and read its
      changelog; add here what affects the extension, verified by running it. None on 2026-11-11: npm's `latest` and
      `next` are 2.1.322 (published 2026-11-10), `stable` is 2.1.311; 2.1.319–2.1.322 were covered in Phase 18. Nothing
      to add
- [x] **Auto-stop runaway agents.** An optional hard limit (Options, off by default): an agent whose requests pass it
      is stopped, and the chat says so. *Stop agents at cost (USD)*, 0 = never; checked as each request is priced, each
      agent stopped once with the panel's Stop (`stop_task`): `⏹ Stopped “…” at $1.25: past your limit of $1.00 per
      agent …`. `SessionNotices.AgentAutoStop`
- [x] ~~**Explore tip for read-only agents.**~~ Dropped (2026-11-11). A one-time tip when a general-purpose agent on Opus or Sonnet only read,
      searched and listed files: an Explore agent does that for less (check which model Explore runs on first).
      Checked with 2.1.322 under a Sonnet main agent: Explore ran on Sonnet too (its messages and `modelUsage` show
      only claude-sonnet-5-5; $0.10), so it is not cheaper by model, and the tip as planned would be wrong. Not built
- [x] **Manual check list.** The unchecked VS checks of Phases 1–5 on one short page (steps and what to look for), to
      go through in the experimental instance in one sitting. [MANUAL-CHECKS.md](MANUAL-CHECKS.md), with the subagent
      features of Phases 22–26 too (not seen in VS yet)

---

## Phase 27 — Spend across conversations

Picked on 2026-11-11.

- [x] **Monthly cost warning.** *Warn at monthly cost (USD)*: like the daily warning, once per multiple of the amount,
      this calendar month's spend in all chat windows, the month's earlier turns read from the usage log. Default
      $200. The daily watch became `SpendWatch` with a `SpendPeriod` (Day, Month); one of each is shared by all windows
- [x] **Session id in the usage log.** Each turn logs its session id; the savings report lists the costliest
      conversations (date, turns, cost, first prompt when logged) and the CSV gets a `session` column. From the
      result's `session_id` (`session` in the log); five at most, with the folder in the all-solutions report; older
      lines without an id are left out. The CSV column is the last one. `SavingsReport.CostliestConversations`
- [x] **Tool results in the report.** Each turn logs the tokens its main agent's tool results added, per tool; the
      report says which tools filled the context across conversations (Read, Shell, Search …). Logged as `tools`
      (the meter's groups, tokens estimated from characters; `TokenMeter.LastTurnResultTokensByTool`); the report's
      `Tool results:` line names the five largest, then "other". Not in the CSV (one column per tool would be messy)
- [x] **Pricey context tip.** A one-time tip when re-reading a large conversation from the cache costs most of a
      turn (a big context, a small question): a new chat for a new topic, or /compact. From 50k tokens of context,
      when the main agent's cache reads cost $0.02 or more and 60% of its cost in the turn, at the answering model's
      prices (so mostly Opus and Fable, or a turn of many requests). `SessionNotices.PriceyContext`

---

## Phase 28 — Model choice that pays

Picked on 2026-11-11.

- [x] **Overkill-model tip.** A one-time tip when a model you picked (Opus or Fable) gave a short answer with no tools,
      with what the same tokens cost on Haiku. Up to 600 output tokens, no main-agent tool call
      (`TokenMeter.LastTurnToolCalls`) and no subagents, from $0.02, when Haiku would cost a third or less; not when
      Auto picked the model. The tip says a new model writes the conversation to its cache once. `SessionNotices.OverkillModel`
- [x] **Effort in the report.** Log the effort each turn was sent with; the report lists turns, cost and thinking per
      effort. `effort` in the log (null for Claude Code's default; older routed lines fall back to `routeEffort`);
      `By effort:` from low to max, then "default", with the cost per turn; an `effort` column ends the CSV.
      `SavingsReport.ByEffort`
- [x] **Router accuracy line.** For prompts the Haiku router decided against the local rules: how often it chose a
      cheaper or a dearer model, and what that changed (the same tokens at the other model's prices). Under the
      `Haiku router:` line: `where they differed: 2 to a cheaper model (≈ $0.12 less), 1 to a dearer one (≈ $0.03
      more)`, the main agent's tokens only (subagents pick their own models). `SavingsReport.HaikuDifferedLine`
- [x] **Budget left in the meter.** The meter's tooltip shows today's and this month's spend against the warnings,
      e.g. `Today $3.20 (warning at $20.00)`. As `Spent in Visual Studio: today $3.20 (next warning at $20.00) · this
      month $41.20 (next warning at $200.00), at API prices.`, from the shared watches (the usage log read once, the
      line shows it from the next refresh); without the amount when a warning is off. `SpendWatch.BudgetLine`

---

## Phase 29 — Limits you can rely on

Picked on 2026-11-11.

- [x] **Daily hard limit.** Optional (off by default): once today's spend passes an amount, sending a prompt asks
      first. *Ask before sending past daily cost (USD)*: an OK / Cancel box (Cancel keeps the prompt in the box);
      after OK, not again today until the next multiple. Typed prompts only: /compact and other commands always go.
      Right after Visual Studio starts, before the usage log has loaded, today's spend is read from it directly.
      `SpendLimit`
- [x] **Month vs last month.** The report's month line adds last month's cost by the same day and the change.
      `(Oct 1–11: $9.00, +37%)`, up to last month's last day when it is shorter; left out when nothing was logged
      then. `SavingsReport.LastMonthText`
- [x] **Amber meter past a warning.** The meter's cost turns amber once today's spend passes the daily warning.
      Also past the monthly warning; with a ⚠ in front, so it does not rely on color alone (amber is close to Claude
      orange). Redrawn with the meter, after each turn. `SpendWatch.IsPastWarning`
- [x] **Tool results in the CSV.** One `tools` column (`Read:1200;Shell:400`), like `agent_types`. Largest first,
      the last column. `UsageCsv.Tools`

---

## Phase 30 — Thinking and effort that pay

Picked on 2026-11-11.

- [x] **Heavy thinking tip.** A one-time tip when thinking was most of a short answer's output at high effort or
      above, with what it cost and a lower effort to try. At an effort we sent (high, xhigh, max; not Claude Code's
      default): 2k thinking tokens or more, 70% of the output, $0.02 or more at the output price, at most 800 other
      output tokens. Suggests Medium after high, else High. `SessionNotices.HeavyThinking`
- [x] **Effort in the footer.** The line under each answer names the effort when one was set (`· high`). As
      `· high effort` after the model (a bare "high" read unclear), from the turn's usage record. `TurnFooter.Effort`
- [x] **Router replay by cost.** The router replay in the savings report adds what the current rules' different
      picks would have cost, at the logged tokens. Each row `sonnet → haiku (≈ −$0.029)`, then `At the logged tokens:
      12 cheaper (≈ $0.40 less), 3 dearer (≈ $0.10 more), net ≈ $0.30 less.`; main-agent tokens, at Options →
      Pricing. `ReplayDiff.CostChangeUsd`, `RouterReplay.CostSummary`
- [x] **Effort in the session summary.** Copy session summary gets turns and cost per effort. `By effort: low 1 turn
      $0.02 · high 2 turns $0.40 · default 1 turn $0.05`, low to max, then the default; left out when no turn had an
      effort set. `TokenMeter.SessionByEffort`

---

## Phase 31 — Why a turn cost what it did

Picked on 2026-11-11.

- [x] **Cost breakdown tooltip.** The cost in the line under an answer gets a tooltip with what made it up: input,
      output, thinking, cache reads, cache writes, subagents. On the whole line; the main agent's tokens at the
      answering model's price (1-hour writes at 2× input, fast mode and long context as a multiple), and Claude Code's
      own figure when the sum is a cent or more off. `TurnFooter.CostBreakdown`
- [x] **Cache rewrites in the report.** What writing the conversation to the cache again cost: turns that wrote a big
      context instead of reading it (expired or broken cache). A logged turn after the first of its session that wrote
      10k tokens or more, and more than it read; priced as the writes less the same tokens read from the cache. Turns
      without a session id are not judged. `Cache written again:` in the report; `SavingsReport.IsCacheRewrite`
- [x] **Long answer tip.** A one-time tip when a short prompt got a very long answer, with how to ask for less. A
      prompt of up to 300 characters, 4k output tokens or more besides thinking, $0.02 or more, and no tool calls (an
      edit's new text is output too). Names the Concise output style. `SessionNotices.LongAnswer`
- [x] **MCP results per server.** Tool results from MCP tools grouped per server (`MCP github`) instead of one `MCP`.
      From Claude Code's `mcp__<server>__<tool>` names (the extension's Error List tool is `MCP visualstudio`); older
      log lines keep `MCP`. `TokenMeter.ToolGroup`

---

## Phase 32 — Spend over time

Picked on 2026-11-11.

- [x] **Last 7 days in the meter's tooltip.** Each day's spend, from the usage log. Under the spend line, oldest
      first, "–" for a day without turns; the log read once with the spend watches, then each turn added.
      `SpendHistory.LastDaysLine`
- [x] **Week vs last week.** The savings report adds this week's spend against last week's by the same day. Weeks
      start on Monday; under the month line; the comparison left out when nothing was logged then.
      `SavingsReport.WeekLine`
- [x] **Compact first on resume.** The note on resuming a conversation whose cache has expired gets a /compact button.
      The button is the meter's advice button (the note itself is chat text), shown with the note while token tips are
      on, until the first turn redraws the advice. Compacting reads the conversation once at the input price and
      leaves a summary; without it the first prompt writes all of it to the cache again (2× input for a 1-hour cache).
      `CacheWindow.ResumeCompactAdvice`
- [x] **Per-solution monthly warning.** Optional: a warning when this solution's spend this month passes an amount.
      *Warn at monthly cost per solution (USD)*, 0 by default; a `SpendWatch` with a solution folder per folder (any
      case or trailing slash), made on its first turn, its earlier turns this month loaded in the background

---

## Phase 33 — Upkeep before the next release

Picked on 2026-11-11. The version comes last, after Phase 34, so its notes cover both.

- [x] **Manual checks for Phases 27–32.** The VS-only parts (limit dialog, amber meter, tooltips, /compact on resume,
      new tips) in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 6, *Spend and tips*, with low amounts to trigger
      the warnings and the values to set back; the Phase 33–34 report commands and Options category added with them
- [x] **Cost options grouped.** The cost warnings and limits in their own *Cost limits* category in Options. Seven
      options; only the category changed, and the page stores values by property name, so they are kept
- [x] **Split the big test file.** `tests/ClaudeVS.Core.Tests/Program.cs` into one file per area, no behavior change.
      `Program` is a partial class: `Program.cs` keeps the runner (80 lines), each test and helper moved unchanged
      into `Tests.Spend / Meter / Router / Notices / Cli / Chat / Context.cs` by a script, in its old order; the
      section marker comments are gone. The same 181 tests pass; a sorted line comparison shows only the new headers
- [x] **Version 0.10.0.** Version bump; the Unreleased changelog becomes the 0.10.0 notes. Manifest, assembly and
      package registration at 0.10.0, still a preview; `## [0.10.0]` with highlights (the Release workflow takes the
      notes from there) and the Marketplace overview updated. After merging: `git tag v0.10.0 && git push origin v0.10.0`

---

## Phase 34 — Keep the history

Picked on 2026-11-11.

- [x] **Keep more usage history.** An option for how many months of usage to keep (default 3), as monthly files,
      instead of one 5 MB archive. `usage-2026-11.jsonl` per local month (by the turn's time), read after the old
      `usage.1.jsonl` and `usage.jsonl`; *Keep usage history (months)* (0 = all) prunes on a new month's first turn:
      monthly files before the first kept month, and the old two once last written before it. `UsageLog.Prune`
- [x] **Report as Markdown file.** Save the savings report as a Markdown file. Tools → Claude Code → *Save Savings
      Report as Markdown…*: a heading, the time, and the report in a fenced block (its columns are space-aligned);
      the log's folder left out. The report text moved to Core for both commands (`SavingsReportText`)
- [x] **Report period picker.** Run the report for this month, last month or the last 30 days, not only everything.
      As an Options dropdown (*Savings report period*), used by both report commands; a dialog would need its own
      theming. The title names the period; the month and week lines still compare up to today, and are left out for
      last month. `ReportPeriod`, `SavingsReportText.Range`
- [x] **Usage log size in Options.** Where the log is and how big; a command to open its folder. An Options
      description is fixed text, so it names the folder and points to the command; the size is in Tools → Claude Code
      → *Open Usage Log Folder* (Output pane, then File Explorer) and on the report's last line. `UsageLog.Describe`

---

## Phase 35 — Where the month is heading

Planned on 2026-11-12.

- [x] **Month pace in the meter.** The meter's tooltip adds where this month is heading at its pace so far, as the
      report's month line does (from the 3rd). In the spend line: `this month $41.20, ~$137.33 by Nov 30 at this pace
      (next warning at $200.00)`; the month's spend over its local days, today included. `SpendWatch.PaceUsd`
- [x] **Pace warning.** Once a month, a note when this month's pace would pass the monthly cost warning before the
      month ends, while there is still time to slow down. From the 7th, with 3 days or more left; against the next
      multiple of the warning, for the per-solution watch too; not after a turn that passed an amount (that warning
      says more). Already past when the usage log loads counts as said. `SpendWatch.PaceWarning`
- [x] **More per model in the report.** The *By model* rows add the cost per turn and the cache hit rate.
      `opus  2 turns  $0.80  (67% of turns, $0.400 per turn, cache 90%)`; the hit rate from the main agent's tokens,
      as the family is the main agent's model. `ModelBreakdown.CostPerTurnUsd`, `CacheHitPercent`
- [x] **Average conversation.** The report says how many conversations there were and what one costs on average
      (and the median), above the costliest ones. `Conversations: 32, $0.40 on average (median $0.12), 6.2 turns on
      average`, from the logged session ids (conversations that cost nothing left out, as in the list); shown with two
      or more. `SavingsReport.ConversationsLine`

---

## Phase 36 — Conversations that grow

Planned on 2026-11-12.

- [x] **Conversation hard limit.** Optional (off by default): once this conversation's cost passes an amount, sending
      a prompt asks first, like the daily limit. *Ask before sending past conversation cost (USD)* in *Cost limits*;
      the meter's conversation cost, one limit per chat window, reset with the notices on a new conversation; the
      daily limit is asked first. `SpendLimit` with `perConversation`
- [x] **Long conversations in the report.** What the turns past the 30th of a conversation cost, and per turn
      against the earlier ones: the price of not starting anew or compacting. `Past a conversation's 30th:` under the
      cache lines, with the share of the period's spend; turns counted per logged session id (a /compact keeps the
      count, as the report cannot see it), the first 30 of all conversations to compare with.
      `SavingsReport.LongConversationsLine`
- [x] **Manual checks for Phases 35–36.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 7,
      *Pace and long conversations*: the pace in the tooltip, the pace warning and its no-repeat after a restart, the
      conversation limit box (with New chat and with the daily limit), and the new report lines; Release is now 8

---

## Phase 37 — Weeks and long chats

Planned on 2026-11-12.

- [x] **Weekly cost warning.** Optional (off by default): like the daily and monthly warnings, once per multiple of
      an amount, this week's spend from Monday. *Warn at weekly cost (USD)*, 0 by default; `SpendPeriod.Week`, the
      week's earlier turns from the usage log; while on, `this week $…` in the meter's tooltip and the amber meter
      past it. `SpendWatch.BudgetLine` takes the week
- [x] **Long conversation tip.** A one-time tip once a conversation runs past 30 turns with a big context: what a
      turn costs now against its average, and /compact or a New chat. From 50k tokens of context, a turn of $0.02 or
      more and 1.5× the average; the chat window's turns (a resumed conversation counts from the resume). The
      pricey-context tip waits a turn when this one is given. `SessionNotices.LongConversation`
- [x] **Turn number in the CSV.** A `turn` column: the turn's number in its conversation, from the logged session ids.
      The last column; counted over the exported rows by time (a conversation stays in one solution, so exporting
      one solution keeps the numbers); empty without an id. `UsageCsv.Columns`

---

## Phase 38 — Your conversation against the others

Planned on 2026-11-12.

- [x] **Conversation against your median.** The meter's tooltip puts this conversation's cost next to the median
      logged conversation. Under the last 7 days; `12× the median …` from twice the median, else the median itself;
      from 3 other logged conversations (the open one left out by its id), read with the spend watches once per
      Visual Studio session. `ConversationStats`
- [x] **This month per solution.** The report's *By solution* rows add this month's spend. `($9.00 this month)` after
      the period's cost, router included; only when the period reaches today and starts before this month (else
      it is the same figure, or not this month at all), and not on rows with nothing this month.
      `SolutionBreakdown.MonthUsd`
- [x] **Manual checks for Phases 37–38.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 8,
      *Weeks and your conversations*: the weekly warning (amber, tooltip), the long conversation tip, the median line,
      the per-solution month and the CSV's `turn`; Release is now 9

---

## Phase 39 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.323)

Checked on 2026-11-13 with 2.1.323 installed: the whole 2.1.323 section read, the entries that touch what the
extension sends or reads probed with stream-json the way the extension runs it, and the live tests run. Terminal,
cloud, Remote Control, Claude Tag, gateway, sandbox and plugin-authoring entries are left out.

- [x] **Router warm-up request** (found while verifying, not in the changelog). In 2.1.323 the first request of every
      process waits 5–7 s for its first byte (plain `claude -p`: 5.5 s and 6.4 s against 0.7–0.8 s with 2.1.322;
      later requests in the same process ~0.5 s), so `--live-haiku-router`'s first route took 6.4 s and the chat's
      first route would miss its 4 s limit and fall back to the local rules. `HaikuRouter.WarmUp` now sends one
      short route request (not counted as a route, its cost counted), so the slow request happens while idle; it
      writes the prompt cache the first real route would have written anyway. The main chat's first answer is
      ~6 s slower too, which the extension can't help. Measured with `--live-haiku-router` (now 25 s of warm-up): first route 1.25 s (was 6.4 s), 4 routes
      $0.00072 (was $0.00053), so the warm-up costs ~$0.0002. The process restarted after 25 routes is warmed too
- [x] **Hook heartbeat** (2.1.323: a progress heartbeat every 30 s while hooks run long in `claude -p`). It comes as
      `tool_progress` with `heartbeat: true`, `tool_name` the hook event (`PreToolUse`) and `parent_tool_use_id` the
      tool call. The parser ignored it, so a hook running past 60 s showed "Not responding". Now a `HookRunning`
      event: the activity strip says which hook runs and for how long, and the stall timer restarts (any parsed
      event does). Capture: `Fixtures/cli-2.1.323-hook-heartbeat.jsonl` (a hook callback answered after 65 s)
- [x] **"Always allow" that wasn't saved** (2.1.323: a rule that looks like it holds a secret, or would go to a
      `settings.local.json` that git tracks, lasts for the session only). The CLI still suggests
      `destination: "localSettings"` and reports the decision as `user_temporary` either way, so the card promised
      a save that didn't happen. After the tool result, the chat checks the settings file and, when the rule is not
      there, says it holds for this session only. `PermissionPrompt.MissingRules` / `SettingsPath`. Verified with
      2.1.323: in a repo where git tracks `settings.local.json` the file stayed `{}`; untracked, the rule was written
      ~2 s before the tool result, so checking at the result is safe
- [x] **1M context before the first answer** (2.1.323: an unknown model ID is assumed to have 1M, not 200K; every
      current model reports `contextWindow: 1000000`). The meter's fallback before the CLI reports a window was
      200K. Now 1M
- [x] **Verify with 2.1.323 and raise `CliVersion.Tested`.** With the warm-up, all 12 live tests pass with 2.1.323
      ($0.17, 4.3 min; the router's first route 1.3 s). `--live` in plan mode still answers with Sonnet when Haiku is
      asked for, as since 2.1.316. `CliVersion.Tested` is 2.1.323 (`Required` stays 2.1.318); README, Marketplace
      overview, CONTRIBUTING and the plan-mode notes say so. The VS-only parts are in
      [MANUAL-CHECKS.md](MANUAL-CHECKS.md), section 9; Release is now 10

Checked, nothing to change:
- `CLAUDE_CODE_DISABLE_AGENT_PROGRESS_SUMMARIES`: in `-p` a subagent's `task_progress` only carries the
  tool-derived "Running …" text, with or without it, and the cost was within run-to-run noise. No option
- Live Bash output (VS Code panel): not in stream-json; a foreground command is a `task_started` `local_bash` task
  (since 2.1.318) and then its result. Nothing to show
- Writes to `.git` no longer offer "don't ask again": the request now suggests `addDirectories` for the session,
  which the card already leaves out (it offers `addRules` only)
- `init.plugin_errors`: absent when there are none; not captured with an error
- Code Mode (opt-in, experimental), the Fable limit message in `-p`, usage-credit menus: not seen from here

---

## Phase 40 — Turns that didn't pay off

Planned on 2026-11-13.

- [x] **Why the cache was written again.** The report's *Cache written again* line splits its turns by likely cause:
      after a pause longer than the cache's lifetime (5 minutes, or 1 hour once the conversation wrote the 1-hour
      cache), after a model switch, or else (a changed CLAUDE.md, MCP tools, settings or speed). `after a pause: 3
      ($0.20), model switch: 1 ($0.05), other: 1 ($0.04)` under the line; the pause from the previous turn's end to
      this turn's start (its logged time less its duration). `SavingsReport.RewriteCause`, `CacheRewriteCausesLine`
- [x] **Cache rewrites in the CSV.** A `cache_rewrite` column: the cause (`pause`, `model`, `other`) on the turns the
      report counts as written again, empty on the others. The last column; judged over the exported rows by
      conversation, as `turn` is. `UsageCsv.Columns`
- [x] **Failed turns in the report.** What the turns that ended in an error cost (interrupted, a limit, an API
      error): `Ended in an error: 3 turns, $0.40 (1% of the spend)` under the cache lines; shown when they cost
      something. `SavingsReport.FailedTurnsLine`
- [x] **Manual checks for Phase 40.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 10,
      *Turns that didn't pay off*: the causes line after a pause and a model switch, the CSV column and the failed
      turns line; Release is now 11. Probed with 2.1.323: Stop (`interrupt`) ends the turn with `is_error: true`
      (`error_during_execution`), but a Stop before the first answer reports `total_cost_usd: 0`, so the check stops
      a running command

---

## Phase 41 — Why turns failed

Planned on 2026-11-13. Probed with 2.1.323: a Stop ends the turn with `terminal_reason: "aborted_streaming"`
(`subtype: "error_during_execution"`); the captures also hold `api_error`.

- [x] **Why a turn ended, in the usage log.** An `end` field on turns that ended in an error: the result's
      `terminal_reason`, or its `subtype` when Claude Code sends none. The report's failed line says why:
      `Ended in an error: 3 turns, $0.40 (1% of the spend; 2 stopped, 1 API error)`; `aborted_*` reads "stopped",
      `api_error` "API error", `max_turns` / `error_max_turns` "turn limit", `error_max_budget_usd` "budget limit",
      others as sent. `UsageRecord.EndReason`, `SavingsReport.EndReasonText`
- [x] **End reason in the CSV.** An `end_reason` column after `cache_rewrite`: the logged `end`, empty on turns that
      finished. `UsageCsv.Columns`
- [x] **Failed turn cost in the chat.** After a turn that ended in an error other than a Stop and cost $0.05 or more:
      `ℹ This turn ended with an API error after costing $0.12: its tokens are paid for. Sending again starts from
      the conversation as it is.` A Stop is your own choice and gets no note. `SessionNotices.FailedTurnCost`

---

## Phase 42 — What the costliest conversations went through

Planned on 2026-11-13.

- [x] **Rewrites and errors per conversation.** Each *Costliest conversations* row adds its cache rewrites and the
      turns that ended in an error, when there are any: `(2 cache rewrites, 1 error)`. From
      `SavingsReport.CacheRewriteCauses` and the logged errors. `ConversationBreakdown.CacheRewrites`, `Errors`
- [x] **Models per conversation.** Each row adds the models its turns ran on, by turns, when more than one:
      `opus 12, haiku 3`. `ConversationBreakdown.Models`
- [x] **How long a conversation ran.** Each row adds the time from its first turn's start to its last turn's end:
      `over 2 h 10 min`, `over 25 min`, `over 3 days` from 2 days; left out under a minute. `ConversationBreakdown.Span`

---

## Phase 43 — When you spend

Planned on 2026-11-13.

- [x] **Today in the report.** Under the month and week lines, for a period up to now: `Today: $3.20 so far in 14
      turns (yesterday $5.10)`; all of today's turns in scope whatever the period, as the month line; yesterday
      only when it cost something. `SavingsReport.TodayLine`
- [x] **By weekday.** A *By weekday* list in the report once the period spans 14 days or more: turns, cost and
      cost per turn per local weekday, Monday first, the days without turns left out. `SavingsReport.ByWeekday`
- [x] **Manual checks for Phases 41–43.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 11,
      *Why turns failed, and when you spend*: the end reason after a Stop and in the CSV, the failed turn note,
      the conversation rows, today's line and the weekdays; Release is now 12

---

## Phase 44 — Did the 1-hour cache pay?

Planned on 2026-11-13. Claude Code writes the 1-hour cache on a claude.ai plan (2.1.319): 2× the input price to
write, against 1.25× for the 5-minute one, so it pays only when you come back within the hour.

- [x] **1-hour cache in the report.** What writing the 1-hour cache cost above 5-minute writes, against what it
      saved on turns that came 5–60 minutes after the previous one of their conversation and read the cache (with
      the 5-minute cache those turns would have written it again): `1-hour cache: $0.40 more to write, ≈ $1.20 saved
      on 6 turns after a pause of 5–60 min (net ≈ $0.80 saved)`; shown when any turn wrote it. The pause as for the
      rewrite causes. `SavingsReport.OneHourCacheLine`, `SavingsReport.PauseBefore`
- [x] **Pause in the CSV.** A `pause_s` column after `end_reason`: seconds from the previous turn's end in the same
      conversation to this turn's start; empty on a conversation's first turn and without a session id.
      `UsageCsv.Columns`

---

## Phase 45 — Turns against your usual

Planned on 2026-11-13.

- [x] **Turn against your usual.** The turn footer adds `· 6× your usual turn` when a turn cost 5× or more the
      median logged turn and $0.05 or more; from 20 logged turns that cost something, read with the spend watches
      once per Visual Studio session. `ConversationStats.TurnMedian`, `TurnNote`
- [x] **Turn spread in the report.** Above the costliest turns: `Turns: median $0.03, 9 in 10 up to $0.40, dearest
      $2.10`, from the turns that cost something; shown with 10 or more. `SavingsReport.TurnsLine`
- [x] **Manual checks for Phases 44–45.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 12,
      *The 1-hour cache and your usual turn*: the cache line, the CSV's `pause_s`, the footer marker and the turn
      spread; Release is now 13

---

## Phase 46 — What you send

Planned on 2026-11-13. The usage log has held the prompt's length and the attached lines and files since Phase 2;
neither the CSV nor the report shows them.

- [x] **Prompt and attachments in the CSV.** `prompt_chars`, `attached_lines` and `attached_files` columns after
      `pause_s`. `UsageCsv.Columns`
- [x] **Attached context in the report.** Turns sent with files or a selection attached, against those without:
      `With attachments:          40 turns, $0.120 per turn (210 lines on average); without: $0.050 per turn`;
      shown when both kinds have turns. `SavingsReport.AttachmentsLine`

---

## Phase 47 — Time per turn

Planned on 2026-11-13.

- [x] **Time per model.** The *By model* rows add the average answer time: `(…, cache 90%, 14.2 s per turn)`; from
      the logged durations, turns logged without one left out. `ModelBreakdown.AverageSeconds`
- [x] **Time per effort.** The *By effort* rows add the same: higher effort thinks longer, and this shows how much.
      `EffortBreakdown.AverageSeconds`
- [x] **Time waited.** `Waiting on Claude:          3 h 20 min over 412 turns (29 s per turn)` under the period line
      (`Period: …`). `SavingsReport.WaitLine`

---

## Phase 48 — Solutions side by side

Planned on 2026-11-13.

- [x] **Cost per turn and cache per solution.** The *By solution* rows add the cost per turn and the cache hit rate
      (the main agent's tokens, as per model): `($0.080 per turn, cache 88%; $9.00 this month)`. `SolutionBreakdown.CostPerTurnUsd`,
      `CacheHitPercent`
- [x] **Conversations per solution.** The rows add the conversations (logged session ids): `12 conversations`.
      `SolutionBreakdown.Conversations`
- [x] **Manual checks for Phases 46–48.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 13,
      *What you send, time and solutions*: the CSV columns, the attachments line, the times per model and effort,
      the time waited and the solution rows; Release is now 14

## Phase 49 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.325)

Checked on 2026-11-16 with 2.1.325 installed (2.1.324 was not released). The 2.1.325 section read for what the
extension sends or reads; terminal, cloud, Remote Control, Claude Tag, gateway and plugin entries are left out.

- [x] **Sandboxed Bash asks by default** (2.1.325, unless `sandbox.autoAllowBashIfSandboxed: true`). The extension
      doesn't turn the sandbox on, so nothing to change
- [x] **`claude -p` waits for account data on a clean machine** (2.1.325: a first run can start a few seconds
      later). The router's warm-up already happens while idle: `--live-haiku-router` warm routes 0.7–1.1 s, under the
      4 s limit
- [x] **Plan mode and Haiku** (2.1.325 fixes subagents being moved to the plan-mode model under `haiku`). The main
      session is unchanged: `--live raw.jsonl Plan` still answers with Sonnet when Haiku is asked for, so Auto keeps
      picking Sonnet in Plan mode
- [x] **"Always allow" offered less often** (2.1.325, IDE and SDK hosts: a rule alone is offered only when saving it
      would allow that command again). Probed with `--live-ask` in Manual mode: `mkdir d && echo x > d/a.txt` came
      with no `permission_suggestions`; `curl …`, `mkdir d2` and `echo x > f` still got an `addRules` suggestion. The
      card already hides **Always allow** when none comes. Capture: `Fixtures/cli-2.1.325-permission-suggestions.jsonl`,
      `Test_PermissionPrompt_RealCli_Suggestions_2_1_325`
- [x] **Verify with 2.1.325 and raise `CliVersion.Tested`.** All 12 live tests pass with 2.1.325 ($0.18, 1.6 min);
      203 offline tests pass. `CliVersion.Tested` is 2.1.325 (`Required` stays 2.1.318); README, Marketplace
      overview, CONTRIBUTING and the plan-mode notes say so

## Phase 50 — A suggested next prompt

Planned on 2026-11-16. When a turn ends, Claude's answer often ends with the obvious next step ("The title says 33,
which is wrong", "If you want separate PRs, tell me"), but the input box only says *Ask Claude to edit…*. Show a
suggestion there instead. By default it comes from a local rule at no token cost; the CLI's own suggestions are
opt-in, since they cost tokens the stream doesn't report.

- [x] **Does the CLI send one?** Yes, with `--prompt-suggestions true` (2.1.325 `--help`: "In print/SDK mode, emits a
      prompt_suggestion message after each turn"): `{"type":"prompt_suggestion","suggestion":"add the C# example",…}`
      comes 0.6–0.8 s *after* the turn's `result`. Not after every turn: 3 of 6 turns over three runs got none, so the
      box falls back to the local rule. Works with all the flags the extension sends and with stdin kept open. Its
      cost is not in `total_cost_usd` (two suggestions: $0.00486 for the session, none: $0.00484), but turn 2 reported
      about $0.0003 more with the flag on than off (Haiku, a small conversation). `--live-suggestions` (not in
      `--live-all`); capture: `Fixtures/cli-2.1.325-prompt-suggestion.jsonl`
- [x] **Read it in Core.** The parser surfaces the CLI's suggestion when there is one (only sent when the option
      below is on); otherwise a local rule takes the answer's closing offer ("If you want …, tell me", "I can …") and
      turns it into a short reply. The rule makes no model call, so no extra tokens. Tests from the capture and from
      real closing paragraphs. `ClaudeEventKind.PromptSuggestion`, `ClaudeLaunchOptions.PromptSuggestions`,
      `NextPrompt.FromAnswer` (looks at the last 3 sentences of the last paragraph; up to 90 characters; "you" in
      the offer becomes "me"). `--live-suggestions` now goes through the option and the parser
- [x] **Grey text in the input box.** The suggestion replaces the placeholder once a turn ends; **Tab** or **→**
      puts it into the box to edit or send, and typing clears it. Never sent on its own; hidden while a permission
      or question card is open, and on a new conversation. `ClaudeChatControl.NextPrompt.cs`: shown as
      `Yes, …   Tab ⇥`, not while busy, waiting on a card or over typed text; Claude Code's suggestion replaces the
      local one when it comes; screen readers get it as the box's help text. Builds clean; not yet run in VS
- [x] **Options** (Options → Claude Code): *Suggest the next prompt* (Chat) turns the grey text on or off (on by
      default, local rule only). *Ask Claude Code for suggestions* (CLI) adds `--prompt-suggestions true` (off by
      default, and only with the first on; its description says each one is a model call whose cost isn't in the
      turn's cost). Applies to new sessions, like the other CLI options. README settings and shortcuts, CHANGELOG
- [x] **Manual checks for Phase 50.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 14,
      *A suggested next prompt*; Release is now 15

## Phase 51 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.326)

Checked on 2026-11-17 with 2.1.326 installed. The 2.1.326 section read for what the extension sends or reads;
terminal, cloud, Remote Control, Claude Tag, gateway, sandbox and plugin entries are left out.

- [x] **Manual stays Manual** (2.1.326: sessions can start in auto mode by default for some organizations, and "in
      auto mode, not Manual mode, when your organization's policy hasn't loaded"). Manual is launched with no
      `--permission-mode`, so it would start in auto there with no word. Send `--permission-mode default` for it, and
      say so when the CLI still starts in another mode, as for the other modes. Here both start in `default` (no
      flag and the flag), so the flag changes nothing where auto isn't the default. `PermissionModeChoice.ToCli`
      gives "default"; `SessionNotices.PermissionMode` no longer skips Manual
- [x] **No bare-tag suggestions** (2.1.326 fixes "the suggested next prompt sometimes being a bare tag, such as
      `<signal type="empty" />`"). The parser drops a `prompt_suggestion` that is only tags, so an older CLI can't
      show one in the input box. A tag among words (`use <T> here`) is kept
- [x] **Nothing to change for the rest.** The startup warning for an `--allowedTools` entry that "doesn't look like a
      complete rule": none in the stream for the extension's MCP tool names (`mcp__visualstudio__…`), nor for a broken
      `Bash(git`. Hooks whose `"decision": "block"` now holds in more cases: the extension's hooks print none.
      `--bare` limits: not used. PowerShell asking about more commands: the permission card shows them as any other.
      Probed with stream-json the way the extension runs it, 2.1.326, nothing on stderr either
- [x] **Verify with 2.1.326 and raise `CliVersion.Tested`.** All 12 live tests pass with 2.1.326 ($0.25, 4.4 min);
      205 offline tests pass. `--live raw.jsonl Plan` still answers with Sonnet. `CliVersion.Tested` is 2.1.326
      (`Required` stays 2.1.318); README, Marketplace overview, CONTRIBUTING and the plan-mode notes say so

## Phase 52 — Suggestions that pay their way

Planned on 2026-11-17. Phase 50 shows a suggested next prompt; is it used, and what do Claude Code's own cost? The
2.1.326 debug log (`--debug-file`) shows each one as a forked request on the conversation's model:
`Forked agent [prompt_suggestion] finished: … totalUsage: input=506 output=16 cacheRead=28058 cacheCreate=133`, so it
reads the whole conversation from the cache ($0.00036 there, an eighth of the $0.0028 turn), and none of it is in
`total_cost_usd`.

- [x] **A better local rule.** `NextPrompt.FromAnswer` takes more closing forms: "Should I go ahead and …?", "Happy to
      … if you'd like", "Want to …?", and a last paragraph that asks which of a numbered list you want ("Which do you
      prefer?" under `1. …` `2. …` → `Option 1: …`). Tests from real closing paragraphs. "Want to see …?" becomes
      `Yes, show me …`; the option is the item's bold start, or its text up to a dash, colon or full stop; the
      question may also come just before the list; a list that doesn't start at 1, or has no "Which …?", is left alone
- [x] **How often suggestions are used.** The next turn's usage record says which suggestion was showing when its
      prompt was sent (`local` or `cli`) and whether it was taken (Tab or →, edited or not). The savings report
      says how often each kind was shown and taken; the CSV gets a `suggestion` column (`local`, `local taken`,
      `cli`, `cli taken`). `UsageRecord.Suggestion`, `SuggestionTaken`; `SavingsReport.SuggestionsLine`;
      `UsageCsv.Suggestion`. Shown means it was in the box, so not one that came while you typed; typing over it
      still counts it as shown. Builds clean; not yet run in VS
- [x] **What Claude Code's suggestions cost.** An estimate for each one that comes: a request on the turn's model
      that reads the conversation's context from the cache, plus about 500 input, 150 cache-write and 20 output tokens
      (from the debug log). The meter's tooltip shows the conversation's total, marked as an estimate; the next
      turn's record keeps it, and the savings report adds it to the suggestions line, outside the totals.
      `SuggestionCost.EstimateUsd` (within 5% of the measured one; Haiku over 100k at 5x), `UsageRecord.SuggestionCostUsd`
      (`suggestionCost`), `SavingsReport.CliSuggestionsUsd`. Counted also when one comes while you type and isn't
      shown; only the ones that come, since a turn with none may still have made the call. Each one goes to the
      Output pane. Option description and README say so. Builds clean; not yet run in VS
- [x] **Manual checks for Phases 51–52.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 15,
      *Manual mode and the suggestions' worth*: Manual stays Manual, the new closing offers, the CSV column, the
      report line and the cost estimate in the tooltip; Release is now 16

## Phase 53 — Side requests, counted where they belong

Planned on 2026-11-17. A probe with 2.1.326 (two Haiku turns and `/compact`, `--prompt-suggestions true`,
`--debug-file`) shows Phases 50 and 52 had it wrong: Claude Code *does* bill its suggestions in `total_cost_usd`, one
result late. The growth of `modelUsage` over turn 2, less turn 2's own `usage`, is exactly the suggestion after turn 1
in the debug log (input 506, output 4, cache read 27,517, cache write 52). The `/compact` result's growth is the
compact request (`[reactive-compact]`) plus the suggestion after turn 2, which was made but never sent to the stream
(the next prompt was already in). The 2.1.325 capture shows the same. So today:
the meter counts these side requests as **subagents** (in the meter, the usage log's `agents` and the report), and
Phase 52's estimate adds them a second time, saying "not in the costs above". Only a suggestion after a
conversation's last turn is never reported.

- [x] **Side requests apart from subagents.** On the main model, the part of `modelUsage`'s growth that no streamed
      subagent message explains is a side request: the summary on a turn that compacted (`compact_boundary`), else
      Claude Code's suggestions. Kept out of the subagent counts; its cost stays in the turn's. Tests from the
      2.1.326 probe (sanitized capture) and the 2.1.325 one. `SubagentUsageTracker.LastSide`; `TokenMeter.LastTurnSide`,
      `LastTurnSideCostUsd`, `LastTurnSideIsCompact`, `SessionSuggestionsUsd`, `SessionCompactUsd`. A `/compact` turn
      has no main request, so its model is the conversation's (or `modelUsage`'s only one). Output a subagent's
      stream didn't show goes to the side request when there is one (streamed output can be low); input and cache
      split exactly. Capture: `Fixtures/cli-2.1.326-side-requests.jsonl` (results, the suggestion and the compact
      boundary only, no paths)
- [x] **Measured, not estimated.** The usage record keeps the side request's tokens and cost (`side`), replacing
      Phase 52's estimate; the meter's tooltip and the savings report show suggestions and compaction as part of the
      cost ("of the cost above"), and the CSV gets their cost. `UsageRecord.SideKind` (`suggestion` or `compact`),
      `SideTokens`, `SideCostUsd`; `SavingsReport.CliSuggestionsUsd`, `CompactLine`; `SuggestionCost.Line`; CSV
      `side`, `side_cost_usd`; each one in the Output pane. The record's token counts stay the main agent's; its
      cost includes the side request, as Claude Code billed it. Option description, README, CHANGELOG and the
      Phase 52 manual check corrected. Builds clean; not yet run in VS
- [x] **A tip when Claude Code's suggestions don't pay.** With *Ask Claude Code for suggestions* on, once 20 or more
      came and under 1 in 10 was taken while they cost $0.10 or more, the chat says so once per VS session, with what
      they cost and how to turn them off. `SuggestionCost.Tip` (`TipAfter`, `TipTakenBelow`, `TipFromUsd`); counted
      over all chat windows since VS started: those that came, those taken with Tab or →, and the measured cost (also
      of ones made but never sent). A token tip, so Options → Token tips off sends it to the Output pane. Builds
      clean; not yet run in VS
- [x] **A resumed conversation's first turn** (found while checking the next item). After `--resume`, the new
      process's `total_cost_usd` and `modelUsage` carry on the earlier processes', the last suggestion included, and
      the chat starts a new meter for a conversation picked under Sessions: so its first turn took the conversation's
      whole cost so far as its own (in the footer, the usage log, the report and today's and this month's spend).
      Every result also says what was billed since the last one, per model with its cost
      (`model_usage_since_last_result`, in the captures since 2.1.316): that is now the turn's cost, and the growth the
      subagent and side split start from, exact without an earlier reading. `ClaudeEvent.ModelUsageSinceLastResult`,
      `TokenMeter.LastTurnCostBilled`; spend adds the turn's cost. Capture: `Fixtures/cli-2.1.326-resume-after-
      suggestion.jsonl` (a turn, its suggestion, then the resumed turn: $0.00048 billed, $0.0043 carried)
- [x] **The last suggestion.** The one after a conversation's last turn is never reported: when a chat ends (New
      chat, another conversation, closing the window) after a suggestion came, its estimated cost is logged as a
      record with no turn, which the turn counts leave out. Checked: a resumed process bills it only in the carried
      total, which the turn costs now leave out (item above), so it isn't counted twice. `UsageRecord.NoTurn`,
      `LastSuggestion`; `SavingsReport.UnbilledSuggestionsUsd` (in the actual cost and the month, week and day
      spend, not in any turn count, per-model row or cache-rewrite check); `UsageSummary` and `ConversationStats`
      count no turn for it; the CSV row has no turn number. A suggestion Claude Code made but never sent (the next
      prompt was already in) can't be seen, so isn't logged. Builds clean; not yet run in VS
- [x] **Manual checks for Phase 53.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 16,
      *Claude Code's own requests, and resumed conversations*: a resumed turn's cost, a suggestion that isn't a
      subagent, the last unbilled suggestion and the tip; Release is now 17

## Phase 54 — What to do after a failed turn

Planned on 2026-11-17, from the Claude Code changelog: 2.1.326 "Added a hint in the empty prompt that says what to
do after a request fails", and 2.1.325's line under a policy block ("run `/rewind` and change the message, or run
`/clear`"). In the chat, a failed turn leaves the input box saying *Ask Claude to edit…*. A probe with an unknown
model (no tokens billed) shows what a failed turn sends: a synthetic assistant message with `"error":
"model_not_found"`, then a result with `terminal_reason: "api_error"`, `api_error_status: 404` and the message as
`result`. The Agent SDK's error kinds: `authentication_failed`, `billing_error`, `rate_limit`, `invalid_request`,
`server_error`, `max_output_tokens`, `unknown`.

- [x] **Read why it failed.** The parser keeps the assistant message's `error` kind and the result's
      `api_error_status` on the turn's result. Capture: `Fixtures/cli-2.1.326-unknown-model.jsonl`.
      `ClaudeEvent.ErrorKind`, `ApiErrorStatus`; a subagent's error isn't the turn's, and the kind doesn't carry over
      to the next turn
- [x] **The next step in the input box.** After a failed turn, the grey text says what to do, from the kind, the
      status and the message (no model call): try the same prompt again after an overloaded or server error, a
      stop or a limit (**Tab** puts it back); `/compact` when the prompt is too long; go on in shorter parts after
      the output limit; rewind or a new chat after a policy block; sign in, add credits or pick another model when
      that is the cause. Typing clears it, as a suggestion. `ClaudeErrorHints.NextStep` (`NextStep.Display`:
      `… · Tab puts your prompt back`, `… · Tab: /compact`, or the hint alone); `ClaudeChatControl.ShowNextStep`.
      Shown also with *Suggest the next prompt* off; Claude Code's own suggestion doesn't replace it; not logged as a
      suggestion; screen readers get it as the box's help text. Builds clean; not yet run in VS
- [x] **Manual checks for Phase 54.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 17,
      *What to do after a failed turn*: Stop, an unknown model, typing over it, with suggestions off, beside Claude
      Code's suggestions, and Narrator; Release is now 18

## Phase 55 — Settings Claude Code honours, and turns that run out of room

Planned on 2026-11-17, from the Claude Code changelog (2.1.325) and Phase 54's open ends. Probed with 2.1.326, the
way the extension runs it:
- `maxEffortLevel: "medium"` in a project's `.claude/settings.json`, `--effort high`: Claude Code answers at medium
  and says so only in its debug log ("Effort 'high' exceeds the cap … using 'medium'"); nothing in the stream
- `CLAUDE_CODE_MAX_OUTPUT_TOKENS=400`, a subagent asked for a 700-word essay: each cut-off is followed by a
  synthetic user message (`isSynthetic: true`), for the subagent (`parent_tool_use_id`) and for the main agent:
  "Output token limit hit. Resume directly — …" or (2.1.325) "Output token limit hit. Your caller does not receive
  an answer in pieces. … give the whole answer again, more briefly"
- An invalid `ANTHROPIC_API_KEY`: `"error": "authentication_failed"`, status 401, "Invalid API key · Fix external
  API key"; an empty `CLAUDE_CONFIG_DIR`: `authentication_failed`, no status, "Not logged in · Please run /login"

- [x] **Respect the effort cap.** Read `maxEffortLevel` from Claude Code's settings (managed, the project's local and
      shared, the user's). The effort picker offers nothing above it, the router doesn't pick above it, and the
      footer and the usage log say the effort really used. Read with the control request `get_settings` (no model
      call; 2.1.326 answers `effective`, the merged settings, `sources` and `applied`, the effort in use), once per
      process start, so no settings files are read here. `ClaudeSettings` (`Parse`, `Cap`, `Above`),
      `ClaudeSession.GetSettingsAsync`, `ClaudeChatControl.Settings.cs`. A choice above the cap is set down to it,
      with a note; the first prompt's effort is capped in the log when the answer comes after it went out. Builds
      clean; not yet run in VS
- [x] **Honour `promptSuggestionEnabled`.** `false` in Claude Code's settings turns the extension's suggested next
      prompt off too (Options decide when the setting isn't there); *Ask Claude Code for suggestions* then sends
      nothing either. Probed: with the setting `false`, `--prompt-suggestions true` gets no suggestion and the debug log
      has no `source=prompt_suggestion` request, so the flag needs no change. Read with `get_settings` at each process
      start (`ClaudeSettings.PromptSuggestionEnabled`); the step after a failed turn still shows. Builds clean; not
      yet run in VS
- [x] **Answers cut off at the output limit.** Count Claude Code's retries after a cut-off, the main agent's and each
      subagent's, and what the retried requests cost: in the turn footer, the agent's row, the usage log and the
      savings report. Capture: `Fixtures/cli-2.1.326-output-limit-retries.jsonl` (6 retries: 3 for the subagent, 3
      for the main agent, over two results). `ClaudeEventKind.OutputLimitRetry` (a synthetic user message starting
      "Output token limit hit."); `TokenMeter.LastTurnOutputRetries`, `LastTurnOutputRetryCostUsd` (the request that
      answers each, at its model's price; a subagent's streamed output can be low); `AgentTask.OutputRetries`;
      `UsageRecord.OutputRetries`, `OutputRetryCostUsd`; `SavingsReport.OutputRetriesLine`; `TurnFooter.Retries`.
      Builds clean; not yet run in VS
- [x] **Failed-turn texts from real errors.** The invalid key and the signed-out captures as tests; an invalid API
      key gets its own step (fix or unset `ANTHROPIC_API_KEY`) instead of "sign in again". Captures:
      `Fixtures/cli-2.1.326-invalid-api-key.jsonl`, `cli-2.1.326-not-logged-in.jsonl`. `ClaudeErrorHints.Explain` says
      the same; the older "Invalid API key · Please run /login" still reads as a sign-in. Found on the way: Phase 54's
      unknown-model test parsed each line with a new parser, so the kind was lost and only the text matched; it now
      keeps one
- [x] **Manual checks for Phase 55.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 18,
      *Claude Code's settings, and answers cut off*: the effort cap in the menu, the router and the footer,
      `promptSuggestionEnabled`, a cut-off agent and an invalid API key; Release is now 19

## Phase 56 — The effort Claude Code really uses, and settings that cost

Planned on 2026-11-17. Probing `get_settings`' `applied.effort` with 2.1.326 found a bug from Phase 1: the effort
switch on a running process (`apply_flag_settings` with `{"effort": …}`) is accepted but changes nothing. The same
puzzle on Haiku thought 250–300 tokens launched at `--effort max`, about 90 launched at `low`, and about 90 launched at
`low` then switched to max that way. Switched with `{"effortLevel": "max"}` (the settings key) it thought 218 and
311, and `applied.effort` said `max` (with `effort` it still said `low`). So since Phase 1, every effort after the
process's launch (the router's per prompt, and the picker's) ran at the launch effort, while the footer and the log
said the one asked for. Other findings: `--effort` at launch wins over a project's `effortLevel`; `get_settings`
keeps setting keys as written (even unknown ones), so the settings check must know the real keys.

- [x] **Effort switches that take effect.** `apply_flag_settings` sends `effortLevel`; after a switch, `applied.effort`
      says it took. `--live-session` checks it: `ok effort switched mid-session: applied.effort = medium`, the rest
      passing as before ($0.06). `ClaudeSession.EffortSettingKey`
- [x] **Effort as Claude Code applied it.** After launch and after each switch, read `applied.effort`: the footer and
      the usage log show it (it also covers caps the settings don't show), and a difference from the one asked for is
      said once. `ClaudeSession.AppliedEffort` (from every `get_settings`; re-read after `set_model` and
      `apply_flag_settings`; reset when a process starts); the chat writes it into the prompt's usage record when it
      goes out (`ConfirmEffort`), or at the result for a process's first prompt (`EffortUsed`, else the cap).
      `--live-session` checks the property. Builds clean; not yet run in VS
- [x] **Settings that cost.** A check of the Claude Code settings that change what turns cost, from `get_settings`
      (no model call): where each is set, what it does and how to change it (a default model of Opus or Fable,
      thinking always on, a long output style, fast mode, prompt caching off, `MAX_THINKING_TOKENS`, a low
      `CLAUDE_CODE_MAX_OUTPUT_TOKENS`, all project MCP servers on). In the context breakdown and as a `/` command.
      `SettingsCheck.Find` reads `sources` (managed, then local, project, user; the chat's own `flagSettings` left
      out), known keys only; also `ANTHROPIC_MODEL`, `CLAUDE_CODE_SUBAGENT_MODEL` on Opus or Fable, and
      `effortLevel` high or more. Fast mode left out: the debug log says it isn't available in this kind of session.
      `ClaudeSettings.Costly`; `/` → *Settings that cost* (starts Claude Code if needed); the breakdown's *Tips*
      count them. Builds clean; not yet run in VS
- [x] **Tip after costly cut-offs.** Once per conversation, when answers cut off at the output limit cost $0.05 or
      more: ask for smaller steps, and name `CLAUDE_CODE_MAX_OUTPUT_TOKENS` when it is set low.
      `SessionNotices.CutOffTip` (`CutOffTipFromUsd`; "low" is under `SettingsCheck.LowOutputLimit`, 8000); the limit
      from Visual Studio's environment or Claude Code's settings. A token tip. Builds clean; not yet run in VS
- [x] **Retries in the CSV.** `output_retries` and `output_retry_cost_usd` columns, last (the count always, the cost
      when there is one)
- [x] **Manual checks for Phase 56.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 19, *The
      effort Claude Code really uses, and settings that cost*: routed and picked efforts that change the thinking,
      the settings check and its breakdown line, the cut-off tip and the CSV columns; Release is now 20

## Release 0.11.0

- [x] **Version 0.11.0.** 0.10.0 was prepared on 2026-11-11 but never tagged; this release takes everything since
      (Phases 35–56). Manifest, assembly and package registration at 0.11.0, still a preview; the Unreleased changelog
      becomes `## [0.11.0]` with highlights (the Release workflow takes the notes from there), and the Marketplace
      overview adds the settings check and the suggested next prompt. After merging: `git tag v0.11.0 && git push
      origin v0.11.0`, which builds the VSIX and makes a draft GitHub release; publishing it starts the Marketplace
      upload, which waits for approval

## Phase 57 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.327)

Checked on 2026-11-18 with 2.1.327 installed. The 2.1.327 section read for what the extension sends or reads;
terminal, cloud, Remote Control, gateway, sandbox, Artifact and plugin entries are left out. Probed with stream-json
the way the extension runs it (Haiku, two runs, under $0.02).

- [x] **The hook's reason on the permission card** (2.1.327's VS Code fix: "the permission prompt not showing the
      reason that a PreToolUse hook gives when it asks for confirmation"). The `can_use_tool` request carries it:
      `decision_reason`, `decision_reason_type: "hook"`, `decision_reason_hook_event: "PreToolUse"`,
      `decision_reason_hook_source: "settings"`. The card shows it under Claude's description and screen readers hear
      it; other reasons ("This command requires approval", type `other`) say nothing and stay hidden. Capture:
      `Fixtures/cli-2.1.327-hook-ask-reason.jsonl`, `Test_PermissionPrompt_RealCli_HookReason_2_1_327`;
      `PermissionPrompt.HookReason`. Builds clean; not yet run in VS
- [x] **Add to Chat from Solution Explorer** (2.1.327's VS Code "Add to Chat" in the Explorer's right-click menu).
      Right-click files, folders or projects in Solution Explorer → *Add to Claude Code Chat* attaches them as
      references, like *Attach Current File*: paths only (relative to the working folder), no contents. VS-only.
      `VsContext.GetSolutionExplorerSelection` (a file's path, a project's or the solution's folder; items not on
      disk left out); on file, folder, project, solution and multi-selection menus; menu resource version 2 so VS
      reloads the menus. Builds clean; not yet run in VS
- [x] **Verify with 2.1.327 and raise `CliVersion.Tested`.** All 12 live tests pass with 2.1.327 ($0.19, 6.3 min;
      the router's warm routes under 4 s, 41 s including warm-up); 217 offline tests pass. `--live raw.jsonl Plan`
      still answers with Sonnet. `CliVersion.Tested` is 2.1.327 (`Required` stays 2.1.318); README, Marketplace
      overview, CONTRIBUTING and the plan-mode notes say so
- [x] **Manual checks for Phase 57.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 20,
      *Claude Code 2.1.327: hook reasons and Add to Chat*: the hook's reason on the card (and with Narrator), Add to
      Claude Code Chat on files, several items, projects, the solution and virtual items; Release is now 21

Checked, nothing to change:
- Cut-off retries (2.1.327: subagent answers that hit the output limit twice come back whole more often; a reply cut
  at 32,000 tokens on an unrecognized model goes on at 128,000): with `CLAUDE_CODE_MAX_OUTPUT_TOKENS=400` a subagent's
  retry still starts "Output token limit hit.", which the counting matches. The run cut off once, so the "twice" path
  was not seen
- The Bash tool's diff of changed files: not in stream-json (`tool_use_result` has stdout, stderr and flags only), so
  *Files changed* keeps its own
- `claude -p` going on after 10 s without a model's settings, and the first subagent on a model waiting up to 10 s
  for them: the router's warm-up happens while idle, and the 60 s stall timer is far off
- Shell start-up files may now run before a session's first Bash command is approved, even if it is refused
  (`CLAUDE_CODE_DISABLE_SHELL_HEAD_START=1` turns it off). The command itself still waits for the card; set the
  variable before starting Visual Studio to keep it off
- PowerShell asking about one more command, `ExitWorktree` asking before removing a worktree: the card shows them as
  any other tool (`Allow ExitWorktree?`)
- Headless resume after a killed process, transcripts read again when they end early, `https_proxy` as a bare
  `host:port` without a terminal: fixes the extension gets as it is
- [VSCode] tools removed from the IDE connection (`openFile`, `saveDocument`, `getOpenEditors`): the extension's MCP
  server has only `get_error_list`

## Phase 58 — Hooks that decide, what references cost, and a setup that still works

Planned on 2026-11-18, after Phase 57. Probed with 2.1.327: a PreToolUse hook that answers `deny` (or `allow`)
decides without asking; the result's `tool_result_meta` says `permission_decision: {decision: "reject" | "accept",
source: "hook", reason_type: "hook"}`, and a denied call's result reads "PreToolUse:Bash hook error: <reason>". The
tool row reads only `source: "config"`, so it says nothing about hooks.

- [x] **Hook decisions on tool rows.** `· denied by a hook` (red, the hook's reason in the tooltip) and `· allowed by a
      hook` on shell commands and file changes, as for rules. Capture: `Fixtures/cli-2.1.327-hook-decisions.jsonl`,
      `Test_Parser_HookDecisions_RealCli_2_1_327`; `ClaudeStreamParser.HookDecision`, `ToolCallView.HookDenyReason`.
      Builds clean; not yet run in VS
- [x] **What a reference may cost.** The attachment line says what Claude would read for each reference if it read it
      whole: `~3.2k tok` for a file, `12 files, ~40k tok` for a folder or project (source files only, `bin`/`obj`/
      `node_modules`… left out, counted up to a limit). A reference costs nothing until read, so it is a ceiling, not a
      charge; large ones (over 20k) in amber, so attaching a whole project from Solution Explorer isn't a surprise.
      `ReferenceSize.Measure` (the `@` picker's `FileIndex` walk, binaries left out, up to 5,000 files: `5000+
      files`); measured off the UI thread once per attachment; the tooltip explains the amber. Builds clean; not yet
      run in VS
- [x] **Check the token-saving setup.** Like the problem list 2.1.327 added to `/status`: after *Install Token-Saving
      Settings and Hooks*, check from `get_settings` (no model call) that the read-deny rules, `bashOutputMaxChars` and
      the quiet-build hook are still in effect, and that the hook's matcher still matches `Bash` and `PowerShell`.
      Missing or overridden parts are listed with where to fix them; in the context breakdown and as a `/` command.
      Probed with 2.1.327 (no model call): `effective` joins hook lists and deny rules from every source and takes
      `bashOutputMaxChars` from the highest one (a local 200,000 over the project's 15,000). `SetupCheck.Find`
      (null when not installed; also `disableAllHooks` and a missing script), `ClaudeSettings.Response`; `/` →
      *Check token-saving setup*, a *Tips* line in the breakdown, the Output pane at start. Builds clean; not yet
      run in VS
- [x] **Cut off twice: capture and test.** 2.1.327: subagent answers that hit the output limit twice "now more often
      come back whole or say up front that they are partial". Capture a subagent cut off twice
      (`CLAUDE_CODE_MAX_OUTPUT_TOKENS`), check the retry count and cost, and that a partial answer's text is kept.
      Not reproduced: in three runs (limits 400 and 150) the subagent was cut off once at most, or the main agent was
      cut off before it started one. Captured instead with 150: the main answer cut off three times, then the turn
      fails ("API Error: Claude's response exceeded the 150 output token maximum", `terminal_reason: api_error`).
      Already handled; now tested: the three retries count and are priced in the failed turn, and the box says what
      to do. `Fixtures/cli-2.1.327-cut-off-then-failed.jsonl` (init line left out),
      `Test_Meter_CutOffThenFailed_RealCli_2_1_327`. The retry text still starts "Output token limit hit."
- [x] **Manual checks for Phase 58.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 21, *Hooks
      that decide, what references cost, and the setup check*: hook deny and allow on rows, reference sizes for a file,
      a project and a large solution, the setup check fine, raised, narrowed and not installed; Release is now 22

## Phase 59 — What references, the quiet-build hook and reads really cost

Planned on 2026-11-18, after Phase 58. Phase 58 shows what a reference *may* cost; this phase measures what Claude
really read, what the token-saving hook did, and where reads go. `--live-setup` with a raw log (2.1.327, $0.005): with
the hook asking (its default), the permission request carries the quiet command and the hook's reason ("Quiet output
to save tokens: dotnet build -v q -nologo"), while the `tool_use` keeps Claude's command; with auto-approve the result
says `source: "hook"` (Phase 58). Full against quiet output measured on this repository: a small build 450 against 82
characters, `git log` 100,490 against 5,823; it depends on the repository, so the hook's saving is shown as counts
and what came back, not as a guessed total.

- [x] **What references really cost.** After a turn with attached references: how much of them Claude actually read
      against what reading them whole would have cost (`read 2 of 12 files, ~8.0k of ~40.0k tok`), in the footer's
      tooltip, the usage log and the savings report. `TurnFileActivity` (reads of the main agent and its subagents).
      `TurnFileActivity.ReadUnder`, `TurnFooter.References`, `SavingsReport.ReferencesReadLine`; `UsageRecord`
      `refFiles`, `refCeiling`, `refReadFiles`, `refRead` (the paths are not logged); CSV `ref_ceiling_tokens`,
      `ref_read_tokens`, last. Only references measured by the time the prompt goes out count. Builds clean; not yet
      run in VS
- [x] **What the quiet-build hook did.** Count the commands the hook made quiet, by kind, and the tokens they returned:
      the usage log, the CSV and a savings report line (`12 commands made quiet: dotnet build 9, git log 3 · ~1.2k tok
      came back`). `TurnFileActivity.QuietKind` (the script's rules in C#); quiet when the hook asked with its reason
      or allowed it (auto-approve), not when refused. `UsageRecord.QuietRuns` / `QuietTokens`, CSV `quiet_runs`
      (`dotnet build:2;git log:1`), `quiet_tokens`; `SavingsReport.QuietRunsLine`. Capture:
      `Fixtures/cli-2.1.327-quiet-build-hook.jsonl` from `--live-setup`, `Test_QuietRuns_RealCli_2_1_327`. Builds
      clean; not yet run in VS
- [x] **Folders Claude reads most.** The usage log keeps each turn's read tokens by folder (two levels below the
      working folder); the savings report lists the top folders, and points out generated or vendored ones (`bin`,
      `obj`, `node_modules`, `packages`, `dist`, `wwwroot/lib`, `*.Designer.cs`-only…) with a read-deny rule to add.
      `UsageRecord.ReadFolders` (`readFolders`), `TurnFileActivity.ByFolder` (reads outside the working folder
      grouped as one, no paths kept), `SavingsReport.TopReadFolders` (top 5) and `NoiseRule` (the scanner's noise
      folders; `*.Designer.cs` left out, as it isn't a folder). Builds clean; not yet run in VS
- [x] **Fix from the setup check.** When the setup check finds a problem, the breakdown's line and the chat's message
      offer **Reinstall** (the installer for this folder, then a New chat) and **Open file** for the settings file that
      overrides it. The breakdown's line runs the check, whose message carries the links (`TranscriptView.AppendActions`).
      **Reinstall** only when it fixes something (`SetupCheck.ReinstallHelps`: the project's file or the script; an
      override in `settings.local.json` or yours needs **Open**), with the option for auto-approved rewrites; then
      says to start a New chat. `SetupCheck.FilesToOpen` (each once, managed settings left out). Builds clean; not yet
      run in VS
- [x] **Manual checks for Phase 59.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 22, *What
      references, the quiet-build hook and reads really cost*: the footer's references line, the quiet-build line and
      CSV columns, the folders section, Open and Reinstall from the setup check; Release is now 23

## Phase 60 — Reads and loud commands: from numbers to fixes

Planned on 2026-11-18, after Phase 59. Phase 59 logs where reads go and what the quiet-build hook did; this phase
turns that into fixes in the chat and keeps what was only a passing tip.

- [x] **Add a deny rule from a tip.** When a turn's reads brought 2k+ tokens from build output or vendored code
      (`obj`, `node_modules`… as in *Folders read most*), once per conversation: a tip naming the folder and what it
      cost, with **Add `Read(./**/obj/**)`**, which merges the rule into the project's `.claude/settings.json` (not
      when a settings file already has it). `SessionNotices.NoiseReadTip` (folders with the same rule added up),
      `TokenSaverSetup.HasDenyRule` / `AddDenyRule`; with token tips off, the tip goes to the Output pane, no link.
      Found on the way: Phase 59's `ByFolder` cut every path at two levels, so `src/App/obj/…` became `src/App` and
      build output below the second level was never flagged; it now ends a folder at a noise folder at any depth.
      Builds clean; not yet run in VS
- [x] **Repeated reads over time.** The usage log keeps each turn's tokens from re-reading an unchanged file
      (`ReadTracker`, until now only a tip); a savings report line and a `repeat_read_tokens` CSV column.
      `UsageRecord.RepeatReadTokens` (`repeatRead`: the tracker's growth since the last logged turn, reset with the
      conversation), `SavingsReport.RepeatReadsLine`. Builds clean; not yet run in VS
- [x] **Loud commands the hook skipped.** Builds, tests and `git log` that the hook leaves alone (a verbosity given,
      a pipe or chain, a count) and that brought 2k+ tokens: counted by kind in the usage log, the report and the CSV,
      and once per conversation a tip with the form that runs quiet (the plain command). `TurnFileActivity.BaseKind`,
      `LoudRuns` / `LoudTokens` / `LastLoud`, `LoudTip` (why: piped or chained, a count, a verbosity, watch mode, or
      run as written; without the hook: install it). The tip replaces the general large-output tip for that command.
      `UsageRecord.LoudRuns` (`loud`), CSV `loud_runs`, `loud_tokens`; `SavingsReport.LoudRunsLine`. Builds clean;
      not yet run in VS
- [x] **Reads in the session summary.** *Copy session summary* adds the conversation's reads: the top folders, the
      attached references read against their ceiling, repeated reads, and commands made quiet or left loud.
      `ConversationReads` (each turn's usage record added, also with the usage log off; reset with the
      conversation), `SessionSummary.Format(…, reads)`. Builds clean; not yet run in VS
- [x] **Manual checks for Phase 60.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 23, *Reads
      and loud commands: from numbers to fixes*: the build-output tip and its rule, repeated reads in the report and
      the CSV, the loud-command tip with and without the hook, the session summary's lines; Release is now 24

## Phase 61 — Tips you can act on, and generated files

Planned on 2026-11-18, after Phase 60, on the same branch. Phase 60's tips say what to do; this phase makes that a
click, and covers generated files the setup's deny rules name but the build-output tip missed.

- [x] **Generated files in the build-output tip.** Reads of minified and source-map files (`*.min.js`, `*.min.css`,
      `*.map`, as in the setup's deny rules) count toward the tip, with their file rule (`Read(./**/*.min.js)`), not
      only build-output folders. `SessionNotices.GeneratedFileRule`; `NoiseReadTip(folders, files)` takes the turn's
      reads by file; one inside a build-output folder counts for the folder only. Builds clean; not yet run in VS
- [x] **Prompt-box links on the tips.** The loud-command tip gets **Ask for the plain `dotnet build`**, and the
      repeated-read tip **Tell Claude it has `Foo.cs`**: each puts the request into the input box (after anything
      typed), to edit or send; nothing is sent by itself. `TurnFileActivity.PlainCommandRequest`,
      `ReadTracker.AlreadyHaveRequest`; the chat's `TipAction` / `PutInPrompt`. Not with token tips off, and the loud
      tip's link not without the hook (the plain command would be loud too). Builds clean; not yet run in VS
- [x] **Reads by folder in the CSV.** A `read_folders` column (`src/Core:40000;src/App/obj:2000`, largest first),
      last, in the same form as `tools`
- [x] **Manual checks for Phase 61.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 24, *Tips
      you can act on, and generated files*: the minified-file tip, the prompt-box links (and none with tips off), the
      `read_folders` column; Release is now 25

## Phase 62 — Reads, counted right across solutions and agents

Planned on 2026-11-18, after Phase 61, on the same branch. Going over Phases 59–61 found two gaps, and one cost
they don't show yet.

- [x] **Folders per solution.** *Folders read most* adds up folders by their path below the working folder, so in a
      report over all solutions `src/Core` of one solution and `src/Core` of another become one row. When the
      period's reads come from more than one solution, each row says which (`ClaudeVS: src/Core`). Solutions told
      apart by their folder (a trailing slash ignored), named by `SolutionBreakdown.NameOf`; the deny-rule hint still
      applies to such rows
- [x] **References measured late.** A reference still being measured when the prompt goes out (a large folder) was
      left out of *References read*; its size, measured in the background, now counts when the turn is logged.
      `UsageRecord.RefPending` (not logged); sending keeps the cache slot for it, and logging adds its size and takes
      it out of the cache (unless it is attached again), so a later attach measures it afresh. Builds clean; not yet
      run in VS
- [x] **Subagents re-reading what the conversation has.** Tokens a subagent spends reading a file the main agent
      already read in this conversation (a subagent starts without that context, so it reads it again): per turn in
      the usage log, a savings report line and a CSV column, and in the session summary.
      `TurnFileActivity.SubagentRereadTokens` (the main agent's reads kept across turns, cleared by a compaction and
      `ResetConversation`), `UsageRecord.SubagentRereadTokens` (`agentReread`), CSV `agent_reread_tokens`,
      `SavingsReport.SubagentRereadsLine`, `ConversationReads`. Builds clean; not yet run in VS
- [x] **Manual checks for Phase 62.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 25, *Reads
      across solutions and agents*: folder rows by solution, a reference sent before its size showed, subagent
      re-reads in the report, the summary and the CSV; Release is now 26

## Phase 63 — Subagent re-reads, where you see them

Planned on 2026-11-18, after Phase 62, on the same branch. Phase 62 logs what subagents re-read of the conversation;
this phase shows it while it happens.

- [x] **On the agent's row.** The Agents panel's row says how much of the conversation's files the agent read again
      (`re-read ~6.0k`). The parser gives a subagent's tool results their Agent call (`ParentToolUseId`), as it does
      for usage and cut-off retries. `TurnFileActivity.LastReread`, `AgentTask.RereadTokens`,
      `AgentTaskList.AddReread` / `SubagentTypeOf`. Builds clean; not yet run in VS
- [x] **A tip once it adds up.** Once per conversation, when subagents have re-read 5k+ tokens of files the
      conversation already had: which agent and file, and that a subagent starts without the conversation, so for
      files Claude already read, asking it directly (or naming the lines the agent needs) is cheaper.
      `SessionNotices.SubagentRereadTip` (the conversation's total; "An Explore agent", "A fork"); reset with the
      conversation. Builds clean; not yet run in VS
- [x] **Manual checks for Phase 63.** The VS-only parts in [MANUAL-CHECKS.md](MANUAL-CHECKS.md). Section 26,
      *Subagent re-reads, where you see them*: the agent's row and the tip, once per conversation, none under 5k;
      Release is now 27

## Phase 64 — Check the reads against the real CLI, and release 0.12.0

Planned on 2026-11-18, after Phase 63, on the same branch. Phases 59–63 count reads and subagent re-reads from
hand-made events and two captures; one live run checks what they assume. Then the unreleased work since 0.11.0
(Phases 57–63) becomes 0.12.0.

- [x] **`--live-reads`.** Haiku reads a ~6k-token file, then an Explore agent reads it again: a subagent's tool
      results carry its Agent call, the paths match, and the agent's read counts as a re-read on its task. Not in
      `--live-all` (about $0.02). Passes with 2.1.327 ($0.010, 39 s): 1 subagent tool result, with its Agent call; the
      agent's read of the file, ~9.7k tokens, counted as a re-read on the Explore task. Found on the way: asked
      plainly, 2.1.327 started the Explore agent in the background (`is_backgrounded: true`) and ended the turn at
      once ("The Explore agent is still running"), so the agent's reads come after the turn's footer and count in
      the turn Claude Code starts with its answer. The test asks for `run_in_background false` and also waits for the
      agent's task to end. CONTRIBUTING says when to run it
- [x] **Version 0.12.0.** Manifest, assembly and package registration at 0.12.0, still a preview; the Unreleased
      changelog becomes `## [0.12.0]` with highlights, and the Marketplace overview adds what's new. Tagging stays
      yours: after merging, `git tag v0.12.0 && git push origin v0.12.0`, which builds the VSIX and makes a draft
      GitHub release (the notes from `## [0.12.0]`); publishing it starts the Marketplace upload, which waits for
      approval. The three batches of *Added* entries are one list now; the overview adds the setup check and where
      reading goes

## Phase 65 — Tidy up what Phases 59–64 added

Planned on 2026-11-18, after 0.12.0 was prepared, on the same branch. Phases 59–64 added six lines to the savings
report, folder lists to each usage line and seven CSV columns; this phase keeps that readable and the tests easy to
extend. (Checked first: a turn Claude Code starts when a background agent finishes already gets its own usage
record, so a background agent's reads are logged.)

- [x] **One section in the report.** *References read*, *Read again*, *Subagents re-reading*, *Quiet-build hook*,
      *Loud builds and logs* and *Folders read most* were spread over the report; they become one *Reads and
      commands* section after *Tool results*, shown only when one of them has something to say.
      `SavingsReport.AppendReadsSection` (values in the report's column). Not yet released, so 0.12.0's notes stay as
      they are
- [x] **Shorter usage lines.** A turn's `readFolders` keeps its 10 largest folders and adds the rest up as
      `(other)`, so a turn that reads all over a large repository doesn't write a long line.
      `TurnFileActivity.Capped`; the build-output tip still sees every folder (small ones add up). Builds clean
- [x] **CSV tests by column name.** The CSV tests compare row endings (`…,0,,,,,`) and count columns from the end, so
      each new column meant fixing seven of them by hand; they read cells by the header's name instead.
      `CsvRows` (quotes undone; a row with a different cell count than the header fails) and `ColumnsInOrder` (the
      order check allows new columns after); the first row's leading columns are still compared as text up to
      `attached_files`, and all later ones must be empty or 0. Checked by adding a column that writes nothing on a
      plain turn: no test to change

## Phase 66 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.328)

Checked on 2026-11-19 with 2.1.328 installed. The 2.1.328 section read for what the extension sends or reads (from
the CHANGELOG you attached); Code Mode, self-hosted runner, terminal, `/plugin`, cloud, Remote Control and gateway entries
are left out.

- [x] **Verify with 2.1.328 and raise `CliVersion.Tested`.** All 12 live tests pass with 2.1.328 ($0.26, 4.9 min;
      the router's 4 routes $0.0008, 37 s with warm-up), and `--live-reads` (an Explore agent's ~9.7k-token re-read,
      on its task); 232 offline tests pass. `--live raw.jsonl Plan` still answers with Sonnet. `CliVersion.Tested` is
      2.1.328 (`Required` stays 2.1.318); README, Marketplace overview, CONTRIBUTING and the plan-mode notes say so

Checked, nothing to change:
- Glob and Grep "counting an error as results" (a VS Code panel fix): probed with a folder that doesn't exist, both
  results come with `is_error: true` ("Path does not exist", "Directory does not exist"), and the tool row shows an
  error, not "Found …"
- Token counts "for subagents counting a retried reply twice": the agent rows show Claude Code's own counts, so they
  are right from 2.1.328 on with nothing to change here
- `CLAUDE_CODE_DISABLE_DEFERRED_TOOL_RESUME` (stops `--resume` from running a tool call that a hook deferred): the
  extension's hooks never defer
- Subagents in `claude -p` waiting for their own subagents, and counting background ones, with
  `CLAUDE_CODE_FORK_SUBAGENT=0`: the extension doesn't set it
- Managed `PostToolUse` hooks no longer firing for refused calls; `"onFailure": "block"` for `mcp_tool` hooks: not
  used by the extension's setup
- The usage-credits error that named a rate limit: the failed-turn hint checks the error kind (`billing_error`) and
  credit wording before rate limits, so the corrected text only makes it surer

## Phase 67 — Haiku in Plan mode

Planned on 2026-11-19, on the same branch. Since 2.1.316 the chat says plan mode answers with Sonnet when Haiku is
asked for, and Auto picks Sonnet in Plan mode. Found in the 2.1.328 binary: Claude Code turns the alias `haiku` into
`sonnet` in plan mode (as it turns `opusplan` into `opus`), on purpose, and only for the alias. Probed with 2.1.328:
`--permission-mode plan --model haiku` answered with claude-sonnet-5-5, `--model claude-haiku-5-5` with
claude-haiku-5-5, and `set_model` to `claude-haiku-5-5` in plan mode kept Haiku too. The `initialize` answer lists each
alias with its `resolvedModel` (`haiku` → `claude-haiku-5-5`), so the ID can be learned rather than written into the
extension.

- [x] **Haiku's full ID.** `ClaudeSession` reads `resolvedModel` for `haiku` from the `initialize` answer; a process
      started on Haiku switches to that ID (`set_model`) before its first prompt, and a switch to Haiku sends it, so
      plan mode keeps Haiku. `--live raw.jsonl Plan` checks it: both turns answered by claude-haiku-5-5 ($0.0053 for
      two turns, against $0.088 on Sonnet just before). `ClaudeSession.ResolvedModelId`, `PinModelIdAsync`,
      `ModelArgAsync`; the initialize request now always goes first (its hook only with `EditNotesHook`; the first
      run of `--live`, which doesn't set that, still got Sonnet). `live (Plan)` is in `--live-all` now: 13 of 13 pass with 2.1.328 ($0.19, 5.3 min). Capture:
      `Fixtures/cli-2.1.328-initialize-models.json` (only the models list), `Test_ResolvedModelId_RealCli_2_1_328`
- [x] **Auto picks Haiku in Plan mode again.** The router's plan-mode rule (Haiku → Sonnet) and the "plan mode answers
      with Sonnet" notes go; README, Options and the 0.12.0 notes say plan mode keeps Haiku. The model-mismatch warning
      stays, for a Claude Code that does it another way. Gone: `ModelRouter.PlanModeNote` and `AnsweredBy`, the hint
      "Plan mode: Sonnet answers, not Haiku" and its Sonnet price, the README's known limitation and the Options
      note; 0.12.0 gets a highlight and a *Fixed* entry. Builds clean; not yet run in VS

## Phase 68 — Models as Claude Code names and prices them

Planned on 2026-11-19, after Phase 67. Claude Code's `initialize` answer lists every model it offers: alias, full ID,
display name and the effort levels it supports. Phase 67 read only Haiku's ID from it. This phase uses the rest, prices
a model that isn't one of the four current ones, checks that plan mode keeps Haiku after a resume or a restart, and
writes down the VS checks for Phases 67 and 68. Fast mode stays out: the chat has no fast-mode switch, and the footer
already says when a turn ran fast.

- [x] **Model facts from Claude Code.** `ModelCatalog` reads the `initialize` models list (alias, `resolvedModel`,
      `displayName`, `supportedEffortLevels`). The footer names the model that answered as Claude Code does
      (`Haiku 5.5`, not `claude-haiku-5-5`; the ID stays in the tooltip). When that model doesn't list the effort the
      turn was sent with, a tip says so once per conversation. `ClaudeSession.Models` (kept across restarts;
      `ResolvedModelId` reads it now), `ModelCatalog.DisplayName` / `IsCurrent` / `SupportsEffort` (a model without
      levels in a list that has them takes no effort: Haiku 4.5), `SessionNotices.UnsupportedEffort`. Checked against
      the 2.1.328 capture: `Test_ModelCatalog_RealCli_2_1_328`. Builds clean; not yet run in VS
- [x] **Unknown model IDs priced.** A model set outside the picker (`settings.json`, a hook, an older ID such as
      `claude-haiku-4-5-20251001`) got its family's current price: Haiku 4.5 at Haiku 5.5's price, a tenth of what it
      bills. A model that isn't one of the four current IDs is priced from Claude Code's own `costUSD` for it
      (`model_usage_since_last_result`), and named by its display name. `PriceTable.Learn` (the family's price scaled
      to costUSD; not from a fast or long-context turn, nor under $0.0005), kept for the VS session. The current IDs
      are `PriceTable.PricedIds`, the four the default prices are for, not what the aliases resolve to:
      `ANTHROPIC_DEFAULT_HAIKU_MODEL` can point `haiku` at Haiku 4.5 (a first version took them from the models list;
      changed before the PR). The meter learns before it prices
      the turn's subagents; the cost tooltip says "at the prices Claude Code bills for …". The test runner forgets
      learned prices before each test. `Test_PriceTable_LearnsOlderModels`. Builds clean; not yet run in VS
- [x] **Haiku pin on resume and restart.** A resumed conversation, and a process restarted after a crash or a stop,
      ask for Haiku by its full ID in plan mode too. `--live raw.jsonl Plan` also ends the process after the first
      turn and checks that the next turn, after `--resume`, is answered by Haiku. Nothing to change in the session:
      every process start sends `initialize` first and pins Haiku again before its first prompt. The test now runs two
      more plan-mode turns: one after killing the process (restarted with `--resume`), one in a new session resumed
      from the conversation's id. Passes with 2.1.328 ($0.0096 for four turns): 3 processes, each sent `set_model`
      `claude-haiku-5-5`, each turn answered by claude-haiku-5-5. Also gone: a "plan mode answers with Sonnet" comment
      left in `--live-long-prompt`
- [x] **Manual checks for Phases 67 and 68.** A section in [MANUAL-CHECKS.md](MANUAL-CHECKS.md): Haiku and Auto in
      plan mode, the footer's model name, the effort tip, a resumed conversation; Release moves down one. Section 27,
      *Models in Plan mode, and as Claude Code names them*: Haiku 4.5 through `ANTHROPIC_DEFAULT_HAIKU_MODEL` checks
      the name, the learned price and the effort tip at once; Release is now 28

## Phase 69 — Older models, seen and priced everywhere

Planned on 2026-11-19, after Phase 68, on the same branch. Phase 68 names and prices a model that isn't one of the
four current ones in the footer, and only while VS runs. This phase checks what Claude Code really does with an effort
such a model doesn't take, and carries the names and prices to the Agents panel, the model picker and the savings
report.

- [x] **Probe effort on Haiku 4.5.** A short live probe: Haiku 4.5 sent effort High. Does Claude Code accept it,
      drop it, or fail the turn? The effort tip says what really happens. Probed with 2.1.328: `claude -p` on
      `claude-haiku-4-5-20251001` with `--effort` low, high and max all answered, no error, no warning ($0.057 for the
      first turn, its 1-hour cache write at Haiku 4.5's $1 / $5, then $0.014 each); `get_settings` says what it applied:
      Haiku 4.5 sent high → effort `null`, Opus 4.6 sent xhigh → `high`, Haiku 5.5 sent high → `high`. So the next level
      down that the model takes, or none. `ModelCatalog.EffortApplied` predicts it; the tip names it ("Claude Code uses
      high instead", "drops the high effort you picked"). Capture: `Fixtures/cli-2.1.328-effort-applied.json`
- [x] **Agent rows name the model.** The Agents panel and the agent's row say `Haiku 4.5`, not `haiku`, from
      Claude Code's models list, so an agent on an older or dearer model stands out. `AgentTask.ModelName` (the family
      when the model isn't listed), `ToolCallView.AgentUsageLine(t, models)`. Builds clean; not yet run in VS
- [x] **Picker shows names and prices.** The model picker shows Claude Code's name for each choice (`Haiku 5.5`)
      and its price per million tokens from Options → Pricing. `ModelPickerText` (`$0.10 / $0.50 per Mtok in / out`;
      Fable keeps "2× the Opus price; Auto never picks it"). A choice that resolves to a model Options doesn't price
      (`ANTHROPIC_DEFAULT_HAIKU_MODEL`) shows its name and no price until one is learned. Before the first prompt the
      names are plain (`Haiku`). Builds clean; not yet run in VS
- [x] **Report prices older models.** The usage log keeps what Claude Code billed per model, so the savings report and
      the CSV price an older model right in later VS sessions too. Each turn logs the learned prices of its models
      (main and agents) that Options doesn't price: `UsageRecord.Prices` (`"prices": {"claude-haiku-4-5-20251001":
      "1, 5, 1.25, 0.1"}`), `PriceTable.LearnedPrices`. `SavingsReport.Build` takes them back first
      (`PriceTable.Remember`, oldest first; a price learned in this VS session wins). The CSV needed nothing: its costs
      are the ones Claude Code billed. `Test_SavingsReport_LoggedPricesForOlderModels`. Builds clean; not yet run in VS

## Phase 70 — The model you really get, before and after the turn

Planned on 2026-11-19, after Phase 69, on the same branch. Phases 68–69 name and price the model that answered. This
phase uses the same facts before a turn (the effort picker, Auto's price hint) and after a conversation (Copy session
summary), and checks an older model's learned price against Claude Code's bill.

- [x] **Effort picker knows the model.** A level the current model doesn't take is marked in the effort picker
      ("Opus 4.6 takes no xhigh: Claude Code uses high"), by the rule Phase 69's probe confirmed. The model is the
      choice's resolved ID, or on Auto the last answer's (`ModelPickerText.NextModel`); the level stays selectable,
      only a settings cap disables one. `ModelPickerText.EffortNote`. Builds clean; not yet run in VS
- [x] **Auto's estimates use real IDs.** With `ANTHROPIC_DEFAULT_HAIKU_MODEL` (or another) pointing an alias at
      another model, the price hint while typing prices the model the alias resolves to, not the alias's family. The
      hint (Auto's and a hand-picked model's) resolves the alias through the models list first. Found on the way:
      `NextPromptPrice` called the cache warm for any model of the same family; two full IDs must match now (Haiku 4.5
      doesn't read Haiku 5.5's cache), an alias still matches by family. Tests in `Test_NextPromptPrice`. Builds
      clean; not yet run in VS
- [x] **Session summary per model.** Copy session summary lists what each model cost (main agent and agents, Claude
      Code's costUSD), named as Claude Code names them. `TokenMeter.SessionCostByModel` (reset with the conversation),
      a `By model:` line under the cost, only when more than one model was billed (one model: the cost line says it
      already). `SessionSummary.Format(…, models)`; `Test_SessionSummary`. Builds clean; not yet run in VS
- [x] **Live price check, older model.** `--live-prices` adds Haiku 4.5: its learned price, from the first turn, gives
      Claude Code's cost for the second. Started with `--model claude-haiku-4-5-20251001`; `--live-prices older` runs
      only it. Passes with 2.1.328 ($0.0104): learned `1, 5, 1.25, 0.1` from a turn of input, output and a 1-hour cache
      write, and the second turn (a cache read) priced with it came to Claude Code's $0.0010459 exactly

## Phase 71 — Tidy up, check two models live, and prepare 0.13.0

Planned on 2026-11-19, after Phase 70, on the same branch; the last phase of this run. Phases 68–70 grew
`SavingsReport.cs` and repeated a fixture load across tests; one live run checks a conversation on two models; then
the unreleased work (Phases 68–70) becomes 0.13.0.

- [x] **Tidy up Phases 68–70.** The learned prices move out of `SavingsReport.cs` into their own file; the tests load
      the 2.1.328 models list through one helper. `PriceTable` is partial: `Tokens/PriceTable.Learned.cs` (101 lines;
      `SavingsReport.cs` back to 1,563). `Models_2_1_328()` / `Initialize_2_1_328()` in the test runner replace five
      copies of the fixture load. No behaviour change: 237 tests pass, builds clean
- [x] **Live two-model test.** `--live-models`: Sonnet answers and an Explore agent runs on Haiku. Each model's billed
      cost, the agent's model name and the summary's `By model:` line, against the real CLI. Turned around to keep it
      cheap: Haiku answers and the Explore agent runs on Sonnet (the Agent tool's `model`); a Sonnet main turn would
      write ~30k tokens of cache at Sonnet's price. Passes with 2.1.328 ($0.061): billed claude-haiku-5-5 $0.0064 and
      claude-sonnet-5-5 $0.0543, together Claude Code's total; the agent's task on claude-sonnet-5-5, named
      `Sonnet 5.5`; `By model: Sonnet 5.5 $0.054 · Haiku 5.5 $0.006`. Not in `--live-all`. Also gone: CONTRIBUTING's
      "`Plan` checks the known Sonnet case", left from before Phase 67
- [x] **Version 0.13.0.** Manifest, assembly and package registration at 0.13.0, still a preview; the Unreleased
      changelog becomes `## [0.13.0]` with highlights, and the Marketplace overview adds what's new. Tagging stays
      yours, after 0.12.0's: `git tag v0.12.0 <0.12.0's commit>` first if it isn't tagged yet, then after merging
      `git tag v0.13.0 && git push origin v0.13.0`. Four highlights (names, older models priced, effort, cost by
      model); the overview's *See what you spend* adds a bullet. The Release VSIX builds

## Phase 72 — Keep up with the CLI (from the Claude Code CHANGELOG 2.1.329)

Checked on 2026-11-20 with 2.1.329 installed. The 2.1.329 section read for what the extension sends or reads (from
the CHANGELOG on GitHub); Code Mode, self-hosted runner, terminal, `/mcp`, `/code-review`, cloud, Remote Control,
gateway, macOS and VS Code entries are left out.

- [x] **Reads of a file that ends in a newline.** 2.1.329 fixes "Read and @-mentions showing one line too many for
      files that end in a newline", but only in its own view: probed with 2.1.329, a three-line file read whole comes
      back as `numLines: 4, totalLines: 4`, its `content` ending in `\n`. The tool row says "Read 4 lines". Count the
      lines as Claude Code shows them now; the large-read tip (`numLines == totalLines`) still fires, but said one line too
      many. `ToolCallView.ReadLines` takes the line after a final newline off both counts when the read reaches the
      end (its `content` says so); the tool row and the tip use it. Tests in `Test_ToolData_Shapes` and
      `Test_SessionNotices_WholeFileRead`
- [ ] **No placeholder suggestions.** 2.1.329 fixes "prompt suggestions sometimes showing placeholder text such as
      `_(no suggestion)_`". Before 2.1.329 the suggested next prompt could be such a placeholder; drop it as the bare
      tags are dropped (Phase 51)
- [ ] **Output limit a gateway's model refuses.** 2.1.329 removes 2.1.326's automatic retry at the limit named in a
      `max_tokens` error ("max_tokens: 128000 > 64000, which is the maximum allowed number of output tokens for …"):
      behind a gateway whose model takes fewer output tokens, the turn now fails. The failed-turn hint says to set
      `CLAUDE_CODE_MAX_OUTPUT_TOKENS` to the limit the error names. Not probed: first-party Claude Code clamps the
      setting to the model's own maximum (`CLAUDE_CODE_MAX_OUTPUT_TOKENS=500000` on Haiku ran with 128000)
- [ ] **Verify with 2.1.329 and raise `CliVersion.Tested`.** All live tests with 2.1.329, the offline tests, and
      `CliVersion.Tested` 2.1.329 in the code and the docs

Checked, nothing to change:
- `/status` naming the model that `haiku` uses in plan mode: still Sonnet for the alias; Phase 67's full-ID pin is
  what keeps Haiku, and `--live raw.jsonl Plan` checks it
- Headless rewind and fork waiting for a message sent right after a model change: the fix is in Claude Code; the
  extension's `rewind_files` and `--fork-session` send nothing that needs to change
- Stop and SubagentStop hooks listing a Monitor as `monitor` in `background_tasks`: the extension's hooks don't read
  `background_tasks`; the Agents panel reads the stream's `background_tasks_changed`, which is unchanged
- `autoCompactWindow` and `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE` fixes: the extension sets neither and doesn't start from a
  pre-warmed process
- The Agent tool's `effort` for named teammates: the extension doesn't run agent teams

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
