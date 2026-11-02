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

## Reporting bugs

Use the bug report template. Include the VS version, the `claude --version` output and the lines from
**Output → Claude Code**. For protocol problems, also turn on *Options → Claude Code → Diagnostics →
Log raw CLI traffic*, and remove any code or secrets before you post.
