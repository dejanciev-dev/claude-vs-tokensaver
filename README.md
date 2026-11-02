# claude-vs-tokensaver

**Claude Code for Visual Studio — Token Saver**

Token-efficient Claude Code extension for Visual Studio 2022/2026 — smart model routing, context trimming and a live token meter.

> Community project, not affiliated with or endorsed by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic.

A lean Visual Studio extension (VS 2022 **17.x** and VS 2026 **18.x**, x64 + ARM64) that runs the
Claude Code CLI inside a tool window and is built around **spending fewer tokens**.

> Status: v0.1 scaffold. The core library is unit tested; the VSIX builds cleanly but has not yet
> been run end-to-end in the experimental instance.

## What it saves, and how

| Feature | Where | Why it saves tokens |
|---|---|---|
| **Auto model + effort routing** | `Core/Tokens/ModelRouter.cs` | A local classifier (no API call) sends simple prompts to Haiku/low, normal work to Sonnet, and only hard ones (refactor, deadlock, architecture…) to Opus. The live hint under the prompt shows the choice and the reason. |
| **Cache-aware routing** | `ModelRouter.StickyContextTokens` | The prompt cache is per model. Once the conversation is large (default 30k), routing never downgrades, because re-caching the whole context costs more than the cheaper model saves. |
| **Smart selection context** | `Core/Context/PromptContextBuilder.cs` | Small selections are inlined and dedented. Large ones are sent as `path:L10-200` references so Claude reads only that range. Whole files are never pasted. |
| **Attach file / errors** | Tools → Claude Code, `Ctrl+Alt+K, F` / `Ctrl+Alt+K, E` | *Attach Current File* sends only its path. *Attach Error List* sends errors only (no warnings), one `file:line` + message line each, capped at 20 and de-duplicated. |
| **Live token meter** | `Core/Tokens/TokenMeter.cs` | Shows context fill %, session tokens, cost and cache-hit rate. Suggests `/compact` at 60% and urges it at 85%. Flags huge tool outputs. |
| **Concise-answer prompt** | Options → *Concise answers* | A one-line `--append-system-prompt`. Output tokens cost about 5× input tokens. |
| **Lean CLAUDE.md generator** | `Core/Setup/ClaudeMdGenerator.cs` | Writes a short CLAUDE.md (projects, build/test commands, folders to skip) and shows its per-request token cost. It never overwrites yours: if one exists, it writes `CLAUDE.generated.md` for you to compare. |
| **Token-saving settings + hooks** | `Core/Setup/TokenSaverSetup.cs` | Merges into `.claude/settings.json`: `Read` deny rules for bin/obj/node_modules/packages/dist, `BASH_MAX_OUTPUT_LENGTH=15000`, and a PreToolUse hook (`.claude/hooks/trim-output.ps1`) that rewrites `dotnet build/test`, `msbuild`, `git log` and `npm test` into quiet forms before they run. |
| **One persistent CLI process per chat** | `Core/Cli/ClaudeSession.cs` | Keeps the prompt cache warm between prompts. Model and effort are switched live (`set_model`, `apply_flag_settings`), with no restart. |
| **Local usage log** | `Core/Tokens/UsageLog.cs` | One JSON line per turn (route decision, model used, tokens, cost, tokens avoided) in `%LocalAppData%\claude-vs-tokensaver\usage.jsonl`. Stays on your machine; used to tune routing and for the savings report. Turn off under Options → Diagnostics. |

## Layout

```
ClaudeVS.sln
src/ClaudeVS.Core/          netstandard2.0. All logic, no VS dependency, testable anywhere
  Cli/      ClaudeArgsBuilder, ClaudeSession (stream-json process), ClaudeStreamParser, ClaudeCliLocator
  Tokens/   TokenMeter, ModelRouter
  Context/  PromptContextBuilder
  Setup/    SolutionScanner, ClaudeMdGenerator, TokenSaverSetup
src/ClaudeVS/               net472 VSIX (AsyncPackage, tool window, commands, options)
tests/ClaudeVS.Core.Tests/  dependency-free test runner (dotnet run)
```

## How it talks to Claude Code

```
claude --print --input-format stream-json --output-format stream-json --verbose
       --include-partial-messages --permission-prompt-tool stdio
       [--model haiku|sonnet|opus] [--effort low..max] [--permission-mode acceptEdits] [--resume <id>]
```

- Prompts go to stdin as `{"type":"user","message":{...}}` lines.
- Interrupt, model switch and effort switch are `control_request` lines.
- Permission prompts arrive as `control_request/can_use_tool` and are answered from the Allow/Deny bar.
- Context fill is taken from the **last** entry of `result.usage.iterations`; the top-level usage sums every request in the turn and would overstate it.

## Build and run

Requirements: Visual Studio 2022 17.x or 2026 with the **Visual Studio extension development**
workload, and the Claude Code CLI installed and logged in (`claude` works in a terminal).

1. Open `ClaudeVS.sln`, set **ClaudeVS** as the startup project, and press **F5**. An experimental VS instance opens.
2. Open a solution, then go to **View → Other Windows → Claude Code** (or press `Ctrl+Alt+K, C`).
3. Go to **Tools → Claude Code → Install Token-Saving Settings and Hooks**, then **Generate Lean CLAUDE.md**.
4. To ask about code, select it and press `Ctrl+Alt+K, S` (or right-click → *Ask Claude About Selection*).
5. Settings are under **Tools → Options → Claude Code**.

The Release build produces `src/ClaudeVS/bin/Release/ClaudeVS.vsix`. The same file installs into both VS 2022 and VS 2026.

To run the core tests: `cd tests/ClaudeVS.Core.Tests && dotnet run`.

## Known gaps / roadmap

- The transcript is plain text. Next step: a Markdown view (WebView2 or FlowDocument) with diff rendering.
- Session history / resume picker (`--resume` is already supported in `ClaudeLaunchOptions`).
- `AskUserQuestion` / plan-approval cards. The permission channel is wired, but the cards are not built yet.
- Optional "ask Haiku to route" mode, for users who prefer accuracy over a local heuristic.
- Per-turn savings report: tokens avoided by references, hooks and routing.
- `trim-output.ps1` assumes Windows PowerShell. Claude Code under WSL would need a bash twin.

Credit: the CLI protocol notes were cross-checked against
[dliedke/ClaudeCodeExtension](https://github.com/dliedke/ClaudeCodeExtension) (MIT). No code was copied.

## Repository

- **Name:** `claude-vs-tokensaver`
- **Description:** Claude Code inside Visual Studio 2022 & 2026, built to spend fewer tokens: auto model/effort routing (Haiku→Sonnet→Opus), cache-aware sessions, smart selection context, live token/cost meter with /compact hints, and one-click lean CLAUDE.md + output-trimming hooks. Community project, not affiliated with Anthropic.
- **Topics:** `visual-studio` `visual-studio-extension` `vsix` `vs2022` `vs2026` `claude` `claude-code` `anthropic` `ai-assistant` `llm` `token-optimization` `prompt-caching` `developer-tools` `csharp` `dotnet`

## License

MIT — see [LICENSE](LICENSE).
