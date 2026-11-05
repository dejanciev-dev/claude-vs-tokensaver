# claude-vs-tokensaver

**Claude Code for Visual Studio — Token Saver**

[![Build](https://github.com/dejanciev-dev/claude-vs-tokensaver/actions/workflows/build.yml/badge.svg)](https://github.com/dejanciev-dev/claude-vs-tokensaver/actions/workflows/build.yml)
![VS 2022 | 2026](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-5C2D91)
![License: MIT](https://img.shields.io/badge/license-MIT-blue)

Claude Code inside Visual Studio 2022 and 2026, built around **spending fewer tokens**. It picks the cheapest
model likely to handle each prompt, sends file *references* instead of file contents, keeps the prompt cache
warm, and shows what every turn costs.

> Community project, not affiliated with or endorsed by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic.

## Contents

- [Status](#status)
- [Why this exists](#why-this-exists)
- [Features](#features)
- [How it works](#how-it-works)
- [Install and first run](#install-and-first-run)
- [Commands and shortcuts](#commands-and-shortcuts)
- [Settings](#settings)
- [Privacy: what stays on your machine](#privacy-what-stays-on-your-machine)
- [Project layout](#project-layout)
- [Building and testing](#building-and-testing)
- [Known limitations](#known-limitations)
- [Contributing, changelog and license](#contributing-changelog-and-license)

## Status

Pre-release (v0.1 → v0.2).

| Layer | State |
|---|---|
| Core library (CLI session, parser, router, meter, context, Markdown, usage log) | ✅ 68 automated tests, plus regression tests built from real CLI output |
| Core against the real `claude` CLI | ✅ Verified with Claude Code 2.1.316 via the opt-in live test: streaming, usage, cost, prompt cache, question and plan approval |
| Visual Studio extension (UI) | ⚠️ Builds with zero warnings in CI, but not yet run in the experimental instance |

## Why this exists

Claude Code is billed by tokens, and most tokens in a coding session are not your question. They come from
three places:

1. **Context.** Every request re-sends the conversation, files Claude has read, tool output and `CLAUDE.md`.
   Pasting a whole file "for context" is paid on every later turn too.
2. **Model choice.** Haiku is far cheaper than Opus, but most extensions use one model for everything.
   A short real turn measured with this extension cost **$0.002 on Haiku vs $0.042 on Sonnet**.
3. **Output.** Output tokens cost about 5× input tokens, so long explanations and restated code add up quickly.

This extension attacks all three, and shows the numbers so you can see the effect.

## Features

### Spend fewer tokens

| Feature | What it does |
|---|---|
| **Auto model and effort routing** | A local classifier (no API call) sends simple prompts to Haiku, normal work to Sonnet, and only hard ones (refactor, deadlock, architecture…) to Opus. The hint under the prompt box shows the choice and the reason before you send. |
| **Route with Haiku** (optional, off) | Instead of the local keyword rules, a small Haiku call picks model and effort for each Auto prompt, with a short reason: about $0.0001 and 1–2 s per prompt. It runs in a separate, trimmed Claude Code process (no tools, no project files, ~1.3k tokens instead of ~25k), started when the chat opens because it takes ~14 s to start. No answer within 4 s: the local rules decide. The savings report shows how often both agree and what the router cost. |
| **Cache-aware routing** | The prompt cache is per model. Once a conversation is large (30k tokens by default), routing never switches to a cheaper model, because re-caching the whole context would cost more than it saves. |
| **Smart selection context** | Small selections are inlined and dedented. Large ones are sent as `path:L10-200` references, so Claude reads only that range. Whole files are never pasted. |
| **File references** | **Attach Current File** and the **`@` file picker** send only a path. Claude reads what it needs. |
| **Compact error list** | **Attach Error List** sends errors only (no warnings), one `file:line` + message line each, de-duplicated and capped at 20. |
| **Claude reads the Error List itself** | A read-only `get_error_list` tool (an MCP server inside the extension) lets Claude check errors and warnings after an edit instead of running a verbose full build. One line per row, errors first; its listing costs almost nothing until Claude uses it. |
| **One warm process per chat** | The `claude` process stays alive between prompts, so the prompt cache is reused (verified: 18k cache-read tokens on the second turn). Model and effort switch live without a restart. |
| **Concise answers** | A one-line system prompt asks for short answers and only the changed code. |
| **Lean `CLAUDE.md` generator** | Writes a short `CLAUDE.md` (projects, build/test commands, folders to skip) and shows its token cost per request. It never overwrites yours. |
| **Token-saving settings and hooks** | Merges into `.claude/settings.json`: blocks reading `bin/obj/node_modules/packages/dist`, caps shell output (`bashOutputMaxChars`), and adds a hook (Bash and PowerShell) that rewrites `dotnet build/test`, `msbuild`, `git log` and `npm test` into their quiet forms. |

### See what you spend

| Feature | What it does |
|---|---|
| **Activity strip** | While Claude works, a strip above the input box shows what it is doing, in color with pulsing dots: *Thinking* (with the estimated thinking tokens), *Writing*, *Running · dotnet test*, *Editing*, *Waiting for you*, and how long the turn has taken. |
| **Live token meter** | Context fill %, session tokens and cost, cache-hit rate and the current model, colored: context green / amber / red at the /compact thresholds, cache hits green when high. Suggests `/compact` at 60% context and urges it at 85%. Click it for a breakdown of what fills the context (system prompt, tools, memory files, tool results…), from the CLI with no model call. |
| **Per-turn footer** | `✓ 3.1s · 12.4k tok · $0.004 · claude-haiku-5-5`, showing the model that *actually* answered. |
| **Model mismatch warning** | If the CLI answers with a different model than the one requested (for example in plan mode), the chat tells you. |
| **Agents panel** | Claude's subagents and background commands above the input box, as a tree: what each is doing, its tokens, tool calls and time, and its result. **Background** lets Claude carry on while an agent works; **Stop** ends one agent (and what it started) without stopping Claude. A subagent's own tool calls are marked `↳` in the conversation. |
| **Tool rows with sizes** | Every tool call is a collapsible row showing its input and the size of its result in tokens. Results of 5k+ tokens are in bold, because those fill the context. |
| **Savings report** | Actual cost vs the same tokens at Opus prices, tokens saved by references, cost per model, cache-hit rate by day. |
| **Router replay** | With the opt-in prompt preview, the report re-runs your real prompts through the current router rules and lists those that would now route differently. |

### Chat

| Feature | What it does |
|---|---|
| **Markdown answers** | Headings, lists, quotes, inline code and links, rendered while the answer streams. |
| **Code blocks** | **Copy** and **Insert** (at the cursor, one undo step). |
| **Diff for Claude's edits** | Rows for Edit / MultiEdit / Write have a **Diff** button that opens Visual Studio's diff window. |
| **Review each change** | In Manual mode, an edit or write with several changes asks with a card listing each one (− and + lines) with a check box. **Apply** only the checked ones, **Reject all**, or **Open diff** for the whole file. Claude is told which changes you rejected, so it neither assumes they are in the file nor applies them again. |
| **Question cards** | When Claude asks a question, you get its options (plus "Other") and your answer goes back to Claude. |
| **Plan approval cards** | Plans render as Markdown with **Approve** or **Request changes** (with feedback). |
| **Permission bar** | Other tool calls that need approval show **Allow** / **Deny**. |
| **Sessions** | Resume an earlier conversation for the same solution (`claude --resume`); its earlier prompts, answers and tool calls are redrawn. Click the title to rename a session (also passed to the CLI as `--name`); hover a session in the list to rename or archive it. |
| **Images** | Paste a screenshot (`Ctrl+V`) or pick image files with **+ → Upload** and they are sent with the prompt. Thumbnails show above the input box with their token cost (≈ width × height / 750); images are scaled to 1568 px first, since the API would scale larger ones down anyway. |
| **Rewind** | Hover one of your messages → **Rewind** puts the files Claude changed since that prompt back the way they were. You see the list of files first; the conversation stays as it is. |
| **Export** | `/` → *Export conversation…* saves the whole conversation (also what came before a resume or a compaction) as Markdown: your prompts, Claude's answers, and one line per tool call. |
| **Bookmarks** | Star any answer to keep it. The bookmarks panel lists them per solution with search; clicking one scrolls to it, or shows the saved copy if it came from another conversation. |
| **Claude Code dialogs** | `/` → *Claude Code*: **Status**, **Account & usage** (plan, every usage limit with its reset time, this session's cost), **Memory & instructions** (CLAUDE.md files and saved memories, with what each costs per request; click to open or create), **Output style** (switch for this project), **Permission rules** and **Hooks** (with the settings file each comes from). Read from Claude Code with no model call. |
| **Screen readers** | Announcements through a UIA live region when Claude finishes (with the start of the answer), asks for permission, has a question or a plan, wants an edit reviewed, stops responding, or hits a usage limit. **Focus last message** (`Ctrl+Alt+K, M`) puts the keyboard at the start of Claude's last answer; `Esc` returns to the prompt box. |
| **Several chat windows** | Each window has its own conversation, meter and model. |
| **Prompt box helpers** | `Ctrl+Up/Down` prompt history; `/` lists commands, including your own `.claude/commands`; `@` picks a file. |
| **Diagnostics** | An **Output → Claude Code** pane with route decisions, errors and plain-language hints (not logged in, rate limited, context too long…), plus optional raw CLI traffic. |

## How it works

### Architecture

All logic lives in a Visual Studio-independent library, `ClaudeVS.Core`, which is tested on its own. The
extension is a thin WPF layer on top of it. The real work is done by the official `claude` CLI, which the
extension runs as a child process and talks to with JSON lines.

```mermaid
flowchart LR
    subgraph VS["Visual Studio"]
        direction TB
        Editor["Editor, Error List,<br/>Solution"]
        Commands["Commands<br/>Ctrl+Alt+K, …"]
        Chat["Chat tool window<br/>transcript · meter · cards"]
        Output["Output pane<br/>Claude Code"]
    end

    subgraph Core["ClaudeVS.Core (no VS dependency)"]
        direction TB
        Context["PromptContextBuilder<br/>FileIndex · EditDiff"]
        Router["ModelRouter"]
        Session["ClaudeSession"]
        Parser["ClaudeStreamParser"]
        Meter["TokenMeter"]
        Usage["UsageLog ·<br/>SavingsReport"]
    end

    CLI["claude CLI<br/>stream-json mode"]
    API[("Anthropic API")]
    Disk[("usage.jsonl<br/>(local only)")]

    Editor --> Commands --> Chat
    Chat --> Context --> Session
    Chat --> Router --> Session
    Session -- "stdin: prompts,<br/>control requests" --> CLI
    CLI -- "stdout: events" --> Parser --> Chat
    Parser --> Meter --> Chat
    Chat --> Usage --> Disk
    CLI <--> API
    Chat --> Output
```

### Life of one prompt

```mermaid
sequenceDiagram
    autonumber
    actor You
    participant Chat as Chat window
    participant Ctx as PromptContextBuilder
    participant Router as ModelRouter
    participant Session as ClaudeSession
    participant CLI as claude CLI

    You->>Chat: prompt + selection / @file / errors
    Chat->>Ctx: build the smallest prompt
    Ctx-->>Chat: inlined snippet or path:L10-200 references
    Chat->>Router: classify (keywords, length, files, context size)
    Router-->>Chat: e.g. haiku / low, with the reason
    Chat->>Session: set_model / effort (only if changed)
    Session->>CLI: user message as one JSON line on stdin
    loop streaming
        CLI-->>Session: text deltas, tool calls, tool results
        Session-->>Chat: events in order: Markdown, tool rows, Diff buttons
    end
    opt Claude asks a question, shows a plan or needs permission
        CLI-->>Chat: control_request (can_use_tool)
        Chat-->>You: question card, plan card or Allow/Deny
        You->>Chat: answer
        Chat->>CLI: control_response
    end
    CLI-->>Session: result (usage, cost, model)
    Session-->>Chat: footer, meter, /compact advice
    Chat->>Chat: append one line to usage.jsonl
```

### How the router decides

The router is a fast, local scoring function, so routing itself never costs tokens.

```mermaid
flowchart TD
    P["Prompt"] --> S["Score it<br/>+2.5 per hard keyword (refactor, deadlock, architecture…, max 3)<br/>+0.75 per task keyword (implement, fix, endpoint…, max 3)<br/>−2 per simple keyword (rename, typo, explain this…, max 2)<br/>± prompt length · + files mentioned · + large code attachment"]
    S --> T{"score"}
    T -- "≤ −1.5" --> H["Haiku · low"]
    T -- "< 3.5" --> SO["Sonnet · low/medium"]
    T -- "< 6" --> OM["Opus · medium"]
    T -- "≥ 6" --> OH["Opus · high"]
    H & SO & OM & OH --> C{"Conversation ≥ 30k tokens<br/>and a different model?"}
    C -- "no" --> USE["Use it"]
    C -- "yes" --> K["Keep the current model to reuse the prompt cache<br/>(unless it is an upgrade with score ≥ 6)"]
```

Keywords match whole words, so "prefix" does not count as "fix". You can override the choice at any time with
the model and effort boxes in the toolbar.

### Talking to the CLI

```
claude --print --input-format stream-json --output-format stream-json --verbose
       --include-partial-messages --permission-prompt-tool stdio
       [--model haiku|sonnet|opus] [--effort low..max] [--permission-mode acceptEdits|plan|…]
       [--resume <session-id>] [--append-system-prompt "…"]
```

- Prompts go to stdin as `{"type":"user","message":{…}}` lines. Interrupt, model switch and effort switch are
  `control_request` lines.
- Tool permissions arrive as `control_request` / `can_use_tool`. Questions are answered by allowing the call
  with `updatedInput` plus an `answers` map. Plans are approved by allowing `ExitPlanMode`, or sent back with
  feedback by denying it.
- Context fill is taken from the **last** request of the turn. The top-level usage sums every request in the
  turn and would overstate it.
- Unknown event types (status lines, rate-limit events…) are ignored, so newer CLI versions keep working.

## Install and first run

Requirements:
- Visual Studio 2022 (17.x) or 2026 (18.x), x64 or ARM64
- [Claude Code](https://claude.com/product/claude-code) installed and logged in, so that `claude` works in a terminal

There is no Marketplace release yet. Either install the `.vsix` from a
[GitHub release](https://github.com/dejanciev-dev/claude-vs-tokensaver/releases) or from a CI build's
artifacts, or build it yourself (see below).

First run:
1. Open a solution, then **View → Other Windows → Claude Code** (`Ctrl+Alt+K, C`).
2. **Tools → Claude Code → Install Token-Saving Settings and Hooks** (once per solution).
3. **Tools → Claude Code → Generate Lean CLAUDE.md** (once per solution).
4. Select code and press `Ctrl+Alt+K, S`, or type a prompt and press **Enter**.

## Commands and shortcuts

| Command | Where | Shortcut |
|---|---|---|
| Open the chat | View → Other Windows · Tools → Claude Code | `Ctrl+Alt+K, C` |
| New chat window | Tools → Claude Code | `Ctrl+Alt+K, N` |
| Ask Claude about the selection | Editor right-click | `Ctrl+Alt+K, S` |
| Attach current file as a reference | Tools → Claude Code · editor right-click | `Ctrl+Alt+K, F` |
| Attach Error List | Tools → Claude Code | `Ctrl+Alt+K, E` |
| Focus last message (keyboard to Claude's last answer; `Esc` goes back) | Tools → Claude Code · `/` | `Ctrl+Alt+K, M` |
| Show Savings Report | Tools → Claude Code | |
| Generate Lean CLAUDE.md | Tools → Claude Code | |
| Install Token-Saving Settings and Hooks | Tools → Claude Code | |

In the prompt box:

| Key | Action |
|---|---|
| `Enter` / `Shift+Enter` | Send / new line |
| `Ctrl+Up` / `Ctrl+Down` | Previous / next prompt (your unsent text is kept) |
| `@` + name | Pick a solution file to attach (`↑ ↓`, `Enter` or `Tab`, `Esc`) |
| `/` | Show matching commands, including `.claude/commands/*.md` |
| `Esc` | Remove attachments |

Editor commands go to the chat window you used last.

## Settings

**Tools → Options → Claude Code → General**

| Category | Setting | Default | Notes |
|---|---|---|---|
| CLI | Claude executable path | *(auto)* | Finds `claude.exe` / `claude.cmd` automatically. |
| CLI | Permission mode | AcceptEdits | Edits are applied; shell commands ask. In **Plan** mode the CLI answered with Sonnet even when Haiku was requested. |
| Chat | Archive inactive sessions after (days) | 14 | Unused sessions move to the Archived group of the sessions list; 0 = never. |
| Chat | Focus view | Off | Consecutive tool calls collapse into one summary row; also / → Focus view. |
| Chat | Screen reader announcements | On | Narrator, NVDA or JAWS announce finished answers (their start), permission requests, questions, plans, edits to review, stalls and usage limits. Nothing is sent without a screen reader. |
| Chat | Show message timestamps | On | Time above each prompt and at the end of each answer line; a date line where the day changes. |
| CLI | File checkpoints (rewind) | On | Claude Code keeps a copy of each file before Claude changes it, so prompts can be rewound. Applies to new sessions. |
| CLI | Extra arguments | | Appended to the `claude` command line, e.g. `--add-dir ..\shared`. |
| Token saving | Concise answers | On | One-line brevity prompt. |
| Token saving | Default model | Auto | Auto = route each prompt. |
| Token saving | Route with Haiku | Off | A Haiku call picks model and effort instead of the local rules (~$0.0001, 1–2 s per prompt). |
| Token saving | Inline selection up to (lines) | 60 | Larger selections become references. |
| Token saving | Keep model above context (tokens) | 30000 | Cache-aware routing threshold. |
| Token saving | Suggest / Urge /compact at (%) | 60 / 85 | Meter advice. |
| Token saving | Auto-approve quiet rewrites | Off | Hook installer: run rewritten build/test commands without asking. |
| Token saving | Claude can read the Error List | On | The `get_error_list` tool, for new sessions. |
| Token saving | Max attached errors | 20 | For Attach Error List. |
| Pricing | Opus / Sonnet / Haiku price | 2026-10 list prices | `input, output, cache write, cache read` in USD per million tokens. Used only for the savings estimate. |
| Diagnostics | Keep local usage log | On | See Privacy. |
| Diagnostics | Log prompt preview (for router tuning) | Off | Stores the first 200 characters of each prompt locally. |
| Diagnostics | Log raw CLI traffic | Off | Every JSON line in Output → Claude Code. Large; for debugging. |

## Privacy: what stays on your machine

- **Prompts and code** go only to the `claude` CLI, which sends them to Anthropic exactly as Claude Code does
  in a terminal. The extension has no server and sends nothing anywhere else. The one exception is that
  unexpected errors in background work are reported through Visual Studio's own fault reporting
  (`FileAndForget`), which follows your VS privacy settings.
- **Usage log**: `%LocalAppData%\claude-vs-tokensaver\usage.jsonl`, one line per turn with the model, tokens,
  cost and route decision. It never leaves your machine, rotates at 5 MB and can be turned off.
- **Prompt preview** (off by default): the first 200 characters of each prompt, stored in that same local log.
- **Conversation history** is the CLI's own (`~/.claude/projects`). The sessions list only reads it.
- **Session names and archive flags**: `%LocalAppData%\claude-vs-tokensaver\sessions.json`. Local only.
- **Bookmarks**: `%LocalAppData%\claude-vs-tokensaver\bookmarks.json`, the prompt and answer text of each starred answer. Local only.

## Project layout

```
ClaudeVS.sln
src/ClaudeVS.Core/            netstandard2.0, all logic, no Visual Studio dependency
  Cli/        ClaudeSession (process + stream-json), ClaudeStreamParser, ClaudeOptions/ArgsBuilder,
              ClaudeCliLocator, ClaudeErrorHints, InteractiveRequests (questions, plans)
  Tokens/     ModelRouter, TokenMeter, ContextUsage, UsageLog, SavingsReport (+ price table, router replay)
  Context/    PromptContextBuilder, FileIndex (@ picker), EditDiff (Diff button)
  Chat/       PromptHistory, SlashCommands, SessionHistory, SessionTranscript, SessionMeta, ConversationExport, Bookmarks
  Markdown/   MarkdownParser (streaming-tolerant subset)
  Setup/      SolutionScanner, ClaudeMdGenerator, TokenSaverSetup
src/ClaudeVS/                 net472 VSIX: package, commands, tool window, options
  ToolWindow/ ClaudeChatControl, TranscriptView (FlowDocument rendering, cards)
  Commands/   ClaudeCommands, VsContext (DTE: selection, errors, diff, insert)
  Diagnostics/ClaudeOutputPane
tests/ClaudeVS.Core.Tests/    dependency-free test runner, live CLI harness, real-output fixtures
.github/                      CI build, tag-triggered release, Marketplace publish, issue templates
marketplace/                  Marketplace publish manifest, overview page, publish.ps1
docs/PLAN.md                  roadmap and status
```

## Building and testing

Requirements: Visual Studio 2022 17.14+ or 2026 with the **Visual Studio extension development** workload, and
the .NET 8 SDK.

```
dotnet test tests/ClaudeVS.Core.Tests                                   # 35 offline tests
msbuild ClaudeVS.sln /restore /p:Configuration=Release /warnaserror     # → src/ClaudeVS/bin/Release/ClaudeVS.vsix
```

- **Debug in VS:** set **ClaudeVS** as the startup project and press **F5** to open the experimental instance.
- **Live check against your CLI** (opt-in, costs a few cents, never runs in CI):
  `dotnet run --project tests/ClaudeVS.Core.Tests -- --live`. `--live-edit-review` checks a partly applied edit (about $0.005). `--live-dialogs` reads every dialog (no cost). `--live-setup` checks the installed hook and deny rules (about $0.005), `--live-session` the meter, model switching and /compact (about $0.05). `--live-haiku-router` routes four prompts with the Haiku router (about $0.001). `--live-mcp` checks that the CLI connects to the
  Error List server, with no prompt and no cost.
- **CI** builds every push and PR with warnings as errors, runs the tests and uploads the `.vsix`. Pushing a
  `v*` tag creates a draft GitHub Release; publishing it starts the Marketplace upload, after approval.

## Known limitations

- **The VS UI has not been run yet.** Everything builds and the Core is verified against the real CLI, but the
  first F5 session may still turn up UI issues. Please [open an issue](https://github.com/dejanciev-dev/claude-vs-tokensaver/issues).
- **Plan mode ignores Haiku.** With Claude Code 2.1.316, plan mode answered with Sonnet even when Haiku was
  requested. The chat warns you when this happens.
- **Resume** redraws at most the last 300 items (prompts, answers, tool calls) of a conversation. Redrawn tool
  rows have no Diff button.
- **Renaming a running session** shows the new name here at once, but the CLI only records it (`--name`) the next
  time that session's process starts. Archiving is local to this extension; the CLI's own `/resume` still lists the session.
- **Rewind** restores files only; Claude still remembers its edits, so tell it what you rewound. Rewinding a prompt from
  an earlier session (after a resume) has not been verified yet; if the CLI has no checkpoint for it, the chat says so.
- **Write diffs** compare against a copy taken when the tool call arrives. If the CLI writes faster, the diff can look empty.
- Several answers to one multi-select question are sent joined with ", ". That format is not verified against the CLI yet.
- The output-trimming hook (`trim-output.ps1`) assumes Windows PowerShell. Claude Code under WSL would need a bash version.
- No Marketplace release yet.

## Contributing, changelog and license

- [CONTRIBUTING.md](CONTRIBUTING.md): setup, where code goes, PR checklist, live tests, releasing
- [CHANGELOG.md](CHANGELOG.md): what changed
- [docs/PLAN.md](docs/PLAN.md): roadmap and status
- Credit: the CLI protocol notes were cross-checked against
  [dliedke/ClaudeCodeExtension](https://github.com/dliedke/ClaudeCodeExtension) (MIT). No code was copied.

MIT. See [LICENSE](LICENSE).
