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

To check the Core code against the real CLI (costs a few cents, never runs in CI):

```
dotnet run --project tests/ClaudeVS.Core.Tests -- --live [path-for-raw-output.txt]
```

It runs two short turns in read-only plan mode in an empty temp folder. To capture and answer permission
requests (questions, plan approval), use `--live-ask <rawPath> <PermissionMode> "<prompt>"`. `--live-edit-review` has Haiku make a two-line edit, applies one line and checks that Claude was told (about
$0.005). `--live-setup` installs the token-saving settings into a temp .NET project and checks the quiet `dotnet build`, the
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
  method to `Program.cs` and it is picked up automatically.

## Pull requests

- Branch from `main` as `feature/<phase>-<topic>`, for example `feature/p3-tool-rows`.
- Keep the build at zero warnings. CI builds with `/warnaserror`.
- VS threading rules apply. Call `ThreadHelper.ThrowIfNotOnUIThread()` in methods that touch the UI or DTE.
- Add or update tests for Core changes, and tick the matching boxes in `docs/PLAN.md`.
- Add a line under **Unreleased** in `CHANGELOG.md` for anything a user would notice.
- Describe how you tested, including whether you ran it in the experimental instance.

## Releasing

1. Set the new version in `src/ClaudeVS/source.extension.vsixmanifest` (`Identity Version`).
2. In `CHANGELOG.md`, rename **Unreleased** to `[x.y.z] - YYYY-MM-DD` and add a new empty **Unreleased** section above it.
3. Merge to `main`, then tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.
4. The *Release* workflow checks that the tag matches the manifest version, builds, runs the tests and creates a
   **draft** GitHub Release with the `.vsix` and that version's changelog notes. Review the draft and publish it.
5. Publishing the release starts *Publish to Marketplace* (`.github/workflows/publish.yml`). It waits for approval
   in the `marketplace` environment, then uploads that release's `.vsix` with `marketplace/publish.ps1`
   (Marketplace page text: `marketplace/overview.md`). Pre-releases are not published; run the workflow by hand
   with a tag to publish one anyway.

One-time Marketplace setup:
- Create a publisher at <https://marketplace.visualstudio.com/manage> and set its ID as the repository variable
  `MARKETPLACE_PUBLISHER`.
- Create an Azure DevOps personal access token (organization: *All accessible organizations*, scope:
  *Marketplace → Manage*) and save it as the secret `VS_MARKETPLACE_PAT`.
- Create the environment `marketplace` (Settings → Environments) with yourself as a required reviewer.

To check a local build without publishing:
`./marketplace/publish.ps1 -Vsix src/ClaudeVS/bin/Release/ClaudeVS.vsix -Publisher <id> -DryRun`.

## Reporting bugs

Use the bug report template. Include the VS version, the `claude --version` output and the lines from
**Output → Claude Code**. For protocol problems, also turn on *Options → Claude Code → Diagnostics →
Log raw CLI traffic*, and remove any code or secrets before you post.
