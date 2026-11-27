# Contributing

Thanks for helping. This is a community project, not affiliated with Anthropic.

## Prerequisites

- Visual Studio 2022 17.14+ or Visual Studio 2026, with the **Visual Studio extension development** workload
- .NET 8 SDK (for the Core tests)
- Claude Code CLI installed and logged in (`claude --version` works in a terminal)

## Build and test

```
dotnet test tests/ClaudeVS.Core.Tests        # Core tests (exit code 0 = all passed)
msbuild ClaudeVS.sln /restore /p:Configuration=Release /warnaserror
```

Or both at once, from any shell: `pwsh ./build.ps1` (`-NoTests` to build only). It finds MSBuild with `vswhere`, so it
works outside a Developer prompt, Git Bash included (`powershell -ExecutionPolicy Bypass -File build.ps1`).

To check the Core code against the real CLI (costs real money, never runs in CI), run every live test at once:

```
dotnet run --project tests/ClaudeVS.Core.Tests -- --live-all
```

It runs them one after another, each in an empty temp folder and asking for Haiku, then prints a summary with each
test's cost and the total (22 tests, about $0.23 and 6 minutes with Claude Code 2.1.332; a single `--live-…` test
prints its cost too). The exit code is 0 only when all pass. `live (Plan)` checks that plan mode still answers with
Haiku: Claude Code turns the alias `haiku` into Sonnet there, so the session asks for Haiku by its full ID. It does so
for four turns: two in one process, one after the process was killed (restarted with `--resume`), one in a new session
that resumes the conversation.

When prices may have changed (a new model or a Claude Code update), run `--live-prices`: two short turns on each of
Haiku, Sonnet, Opus and Fable, each turn's usage priced with the default prices next to what Claude Code billed
(about $0.18, not part of `--live-all`). A FAIL line shows which model is off and by how much; update `PriceTable`
and the prices fixture. It ends with Haiku 4.5, a model Options doesn't price: the price learned from its first turn
must give the second turn's bill. `--live-prices older` runs only that (about $0.01).

After a Claude Code update that touches subagents, run `--live-reads [rawPath]` (about $0.01, not part of
`--live-all`): Haiku reads a large file, then an Explore agent reads it again; it checks that a subagent's tool
results carry its Agent call and that the agent's read counts as a re-read of the conversation's file.
`--live-models [rawPath]` (about $0.06) has Haiku answer with an Explore agent on Sonnet: each model's billed cost adds
up to Claude Code's total, the agent is named `Sonnet 5.5`, and Copy session summary has its `By model:` line.
`--live-env` (about $0.002, in `--live-all`) writes `CLAUDE_CODE_MAX_OUTPUT_TOKENS=100` into a project's
`.claude/settings.local.json`, as the output-limit link does: the running process keeps its limit, and after
`StopProcess` the next prompt resumes the same conversation and is cut off at the new one.
`--live-restart` (about $0.002, in `--live-all`) does the same for *Restart Claude Code*: a `CLAUDE.md` (and a file it imports) written
mid-conversation isn't read by the running process, and is after the restart, in the same conversation.
`--live-mcp-restart` (no cost, in `--live-all`) turns the Error List MCP server off, restarts in place and checks it
is still off, and that a server added to `.mcp.json` meanwhile is listed only after the restart.
`--live-session-rules` (about $0.005, in `--live-all`) allows `mkdir -p cvs-folder` for the session in a permission
prompt, and checks Claude Code doesn't ask again: in the same process, and after a restart in place.
`--live-links` (about $0.003, in `--live-all`) has Haiku say where a method of a small project is, without saying
how; a reference in the answer must link, as the chat parses it, to that file and line.
`--live-markdown` (about $0.003, in `--live-all`) has Haiku write a task list, a struck word, a table with a `<br>`
in a cell, a diff block and a `[!NOTE]`; the chat's parser must find each.
`--live-ansi` (about $0.003, in `--live-all`) has Haiku run a `printf` that prints a green word; Claude Code passes
the escape codes through, and the chat must clean them to "green plain" with "green" in green.
`--live-tools` (about $0.004, in `--live-all`; needs the .NET SDK) makes a two-test MSTest project, one test failing,
and has Haiku run `dotnet build` and `dotnet test`; their rows must read "Build succeeded" and "✗ 1 failed · 1 passed".
`--live-git` (about $0.005, in `--live-all`; needs git) makes a repository with a local remote and one changed file
and has Haiku run `git status`, `git commit -am`, `git push` and `git log --oneline`; their rows must read
"1 modified", "Committed … · 1 file · +1", "Pushed main" and "2 commits".

Each test also runs on its own. `--live [path-for-raw-output.txt] [mode]` runs two short turns, in Accept Edits
unless a mode is named (`Plan` checks that plan mode keeps Haiku, also after a restart and a resume). To capture and answer permission
requests (questions, plan approval), use `--live-ask <rawPath> <PermissionMode> "<prompt>"`. `--live-edit-review` has Haiku make a two-line edit, applies one line and checks that Claude was told (about
$0.005); `--live-edit-review-interrupt [rawPath]` does the same but presses Stop right after approving, then asks
Claude what happened (about $0.007); `--live-edit-stop-allow [rawPath]` allows the edit as proposed instead, then
checks that the file is read after the stop and that Claude believes the note (about $0.007). `--live-long-prompt` sends 60,000- and 200,000-character prompts and checks
that their last line arrives (about $0.02). `--live-prompt-tokens` measures what prose, code and log-like prompts
really cost in tokens and checks that the extension's estimate learns it (about $0.01). `--live-setup` installs the token-saving settings into a temp .NET project and checks the quiet `dotnet build`, the
`bin/` block and the output cap (about $0.005; needs the .NET SDK). `--live-session` checks the meter against
`/context`, a mid-session model switch, cost across a relaunch and `/compact` (about $0.05). `--live-haiku-router` warms up the trimmed Haiku router and routes four prompts next to the local rules (about $0.001). `--live-dialogs` reads every dialog and sets an output style in a temp folder (no prompt, no cost). `--live-mcp` starts the CLI
with the in-process Error List MCP server, checks that it connects and prints the context breakdown
(`get_context_usage`); `--live-mcp <rawPath>` also saves the raw lines (no prompt, no cost). Note that in plan
mode the CLI also saves the plan under `~/.claude/plans`. If a new CLI version changes the
event format, add a **sanitized** capture under `tests/ClaudeVS.Core.Tests/Fixtures`, with personal data removed
(init details, rate-limit info, IDs). See `cli-2.1.316-turn.jsonl` for an example.

To see what **Generate Lean CLAUDE.md** would write for a folder, without writing anything, run
`dotnet run --project tests/ClaudeVS.Core.Tests -- --scan <solution folder>` (no CLI, no cost).

To try the extension, set `ClaudeVS` as the startup project and press F5. This opens the
experimental instance (`/rootsuffix Exp`). If that instance breaks, run *Reset the Visual Studio
Experimental Instance* from the Start menu.

## Where code goes

- `src/ClaudeVS.Core` (netstandard2.0): all logic that does not need Visual Studio, such as the CLI
  session, parser, router, meter, context building, Markdown and the usage log. **Add tests here.**
- `src/ClaudeVS` (net472 VSIX): thin VS-specific code, such as the package, commands, tool window and options.
- `tests/ClaudeVS.Core.Tests`: a dependency-free runner. Add a `private static void Test_Something()`
  method to the `Tests.<Area>.cs` file it belongs to (Spend, Meter, Router, Notices, Cli, Chat, Context; each a
  part of the `Program` class) and it is picked up automatically. `Program.cs` holds only the runner and `Eq` / `True`.

## Pull requests

- Branch from `main` as `feature/<phase>-<topic>`, for example `feature/p3-tool-rows`.
- Keep the build at zero warnings. CI builds with `/warnaserror`.
- VS threading rules apply. Call `ThreadHelper.ThrowIfNotOnUIThread()` in methods that touch the UI or DTE.
- Add or update tests for Core changes, and tick the matching boxes in `docs/PLAN.md`.
- Add a line under **Unreleased** in `CHANGELOG.md` for anything a user would notice.
- Describe how you tested, including whether you ran it in the experimental instance.

## Releasing

See [docs/RELEASING.md](docs/RELEASING.md): the version in three places, the changelog, a tag, a draft release you
publish, and the Marketplace upload you approve. It also covers the one-time setup (a publisher, a PAT for one
organization, the `marketplace` environment), the token's expiry and uploading by hand.

## Reporting bugs

Use the bug report template. Include the VS version, the `claude --version` output and the lines from
**Output → Claude Code**. For protocol problems, also turn on *Options → Claude Code → Diagnostics →
Log raw CLI traffic*, and remove any code or secrets before you post.
