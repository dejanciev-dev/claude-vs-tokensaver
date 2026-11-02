# claude-vs-tokensaver — Development Plan

Plan for taking the v0.1 scaffold to a published Marketplace extension.
Tick the boxes as you go. Each phase ends with something you can run.

---

## 0. Status today (v0.1 scaffold)

| Area | State |
|---|---|
| `ClaudeVS.Core` (CLI session, parser, router, meter, context, setup) | ✅ Written, 16/16 tests pass |
| `ClaudeVS` VSIX (package, tool window, commands, options) | ✅ Builds clean (0 warnings); not yet run in the experimental instance |
| Real end-to-end run against `claude` CLI | ❌ Not done |
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
- [ ] Verify `set_model` / `apply_flag_settings` take effect mid-session (check `init`/result `modelUsage`)
- [x] Show the model actually used per turn in the footer

### 2.2 Token meter
- [ ] Confirm context % matches the CLI's own `/context` output (±2%)
- [ ] Confirm cost resets correctly after interrupt/relaunch
- [ ] Make the `/compact` advice button work end-to-end; meter drops after `compact_boundary`
- [x] Persist usage per solution: one line per turn in `%LocalAppData%\claude-vs-tokensaver\usage.jsonl` (`UsageLog`); summary logged to the Output pane on session start

### 2.3 Smart context
- [ ] Test **Ask Claude About Selection** for: small selection, 500-line selection, whole-file selection, unsaved file
- [x] Add "Attach current file as reference" (path only, no content) — Ctrl+Alt+K, F
- [x] Add "Attach error list" — only errors, file:line + message, max 20 — Ctrl+Alt+K, E

### 2.4 Setup commands
- [ ] Run **Install Token-Saving Settings and Hooks** on a real solution
- [ ] **Verify the hook:** ask Claude to run `dotnet build` — the executed command must contain `-v q -nologo`
  - If `updatedInput` is ignored → fall back to a `deny` with message "use: `<quiet command>`"
- [ ] Verify Claude refuses to read `bin/` / `obj/` after install
- [ ] Run **Generate Lean CLAUDE.md** on 2–3 different solutions (API, Angular+API, legacy .NET Framework) and fix scanner gaps

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
- [ ] Collapsible tool-call rows (`⚙ Read: src/A.cs`) with result size in tokens
- [ ] File edits: "Open diff" link using `IVsDifferenceService`
- [ ] Theme check: Light, Dark, Blue in VS 2022 and VS 2026
- [ ] Prompt box: history (Ctrl+Up/Down), `@` file picker, `/` command list
- [ ] Questions & plan approval cards (`AskUserQuestion`, `ExitPlanMode` over `can_use_tool`)

**Done when:** a colleague can use it without explanation. Tag `v0.3.0`.

---

## Phase 4 — Sessions and savings report (1 week)

- [ ] Session history picker: read `~/.claude/projects/<encoded-path>/*.jsonl`, list title/date/tokens, resume with `--resume`
- [ ] Multiple chat tabs (one `ClaudeSession` each)
- [x] **Savings report** (Tools > Claude Code > *Show Savings Report*, written to Output > Claude Code; a richer panel can come with Phase 3 UI):
  - tokens avoided by references (from `BuiltPrompt.CharsAvoided`)
  - turns routed to Haiku/Sonnet vs Opus and estimated $ saved
  - cache hit rate trend
- [ ] Optional "Ask Haiku to route" mode (one cheap call) — compare accuracy with the local router

**Done when:** the report shows real savings numbers on your own usage. Tag `v0.4.0`.

---

## Phase 5 — Release (2–3 days)

- [ ] Icon (128×128 PNG) + preview image, add to manifest
- [ ] Screenshots for README and Marketplace (meter, routing hint, setup dialog)
- [ ] Test install of the Release `.vsix` on clean VS 2022 **and** VS 2026 (x64; ARM64 if available)
- [x] GitHub Actions: build on `windows-latest`, run Core tests, upload `.vsix` artifact (`.github/workflows/build.yml`)
- [ ] `publishManifest.json` + `VsixPublisher.exe` publish step (manual approval)
- [x] CHANGELOG.md, issue templates, CONTRIBUTING.md
- [ ] Publish to Visual Studio Marketplace as **Claude Code for Visual Studio — Token Saver**

Tag `v1.0.0`.

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
