# Changelog

All notable changes to this project are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Context breakdown:** click the meter (or / → Context usage) to see what fills the context window: system
  prompt, tools, memory files, skills, messages (split into tool results, tool calls, your and Claude's messages),
  reserved space, plus tips such as compacting when tool results dominate. It comes from the CLI's
  `get_context_usage`, refreshed after each answer, with no model call.
- **Export conversation** (/ → Export conversation…): saves the whole conversation as Markdown, including
  messages from before a resume or a compaction, and opens it in Visual Studio. Tool calls are listed as one
  line each (with result size), never their full output.
- **Resumed sessions show their history:** earlier prompts, answers and tool calls are read from the CLI's
  session file and redrawn (the last 300 items). Earlier answers can be bookmarked too.
- **Rename sessions:** click the title (or / → Rename session), or hover a session in the list. The name is
  passed to the CLI with `--name`, so its own `/resume` picker shows it too.
- **Archive sessions:** hide sessions from the list; "Show archived sessions" brings them back.
- **Bookmarks:** a star under each answer saves it (prompt and answer) to a local file. The bookmarks
  panel (header icon, or / → Bookmarks) lists them for the solution with search; clicking one scrolls to the
  answer, or shows the saved copy when it is from another conversation. Hover a row to remove it.
- **Question cards:** when Claude asks a question (AskUserQuestion), the chat shows its options as
  choices with descriptions plus an "Other" field, and sends your answer back to Claude.
- **Plan approval cards:** when Claude presents a plan (ExitPlanMode), the chat renders it and offers
  **Approve** or **Request changes** with feedback. After approval the CLI leaves plan mode and asks
  permission for edits as usual.
- Warning when the CLI answers with a different model than the one requested. Verified with Claude Code
  2.1.316: in acceptEdits mode (the default) Haiku answers as routed ($0.002 for a short turn); in plan
  mode the CLI answers with Sonnet even when Haiku is requested ($0.042 for the same turn).
- Live smoke test against the real CLI (`--live`, opt-in) and a parser regression test built from
  real Claude Code 2.1.316 output.
- **Diff** button on Claude's file edits (Edit, MultiEdit, Write) opens Visual Studio's diff window.
  The "before" side is rebuilt by undoing the edit on the current file; if that is not possible, only
  the changed text is compared.
- Release workflow: pushing a `v*` tag builds, tests and creates a draft GitHub Release with the `.vsix`.
- `@` file picker: type `@` and part of a file name to attach a solution file as a path reference
  (bin/obj/node_modules and similar folders are skipped).
- **New Chat Window** (`Ctrl+Alt+K, N`): several independent chats, each with its own conversation.
  Editor commands go to the chat used last.
- Extension icon and preview image.
- **History** button: resume an earlier conversation in the same solution (`claude --resume`).
- Tool calls appear as collapsible rows showing the call, its input when expanded, and the result
  size in tokens; results of 5k+ tokens are shown in bold.
- Prompt history with `Ctrl+Up` / `Ctrl+Down` (an unsent draft is kept).
- Typing `/` lists matching commands, including custom ones from `.claude/commands` (project and user).
- Chat answers are rendered as Markdown (headings, lists, quotes, inline code, links) while they stream.
- Code blocks have **Copy** and **Insert at cursor** buttons; an insert is a single undo step.
- **Show Savings Report** (Tools → Claude Code): actual cost vs the same tokens at Opus prices,
  tokens saved by file references, cost per model and daily cache-hit rate.
- Editable price table (Options → Pricing).
- Optional prompt preview in the usage log, used by the report to replay logged prompts through
  the current router (off by default).
- Local usage log (`%LocalAppData%\claude-vs-tokensaver\usage.jsonl`), rotated at 5 MB.
- **Attach Current File as Reference** (`Ctrl+Alt+K, F`) and **Attach Error List** (`Ctrl+Alt+K, E`).
- "Claude Code" pane in the Output window: route decisions, errors, optional raw CLI traffic.
- Plain-language hints for common errors (not logged in, limits, rate limits, overload,
  context too long, CLI not found).
- The footer under each answer shows the model that actually answered.
- GitHub Actions build: Release build with warnings as errors, Core tests, `.vsix` artifact.

### Changed
- Model router matches keywords as whole words ("prefix" no longer counts as "fix").
- `dotnet test` runs the Core test runner instead of silently reporting zero tests.

### Fixed
- Output from a stopped conversation (after **New**, **History** or a solution change) no longer appears in
  the new one, including a spurious "claude exited with code -1".
- Streamed text that arrives right after a tool call no longer appears above that tool row.
- Sending a prompt while Claude is still answering no longer loses or mislabels a turn in the usage log.
- **New**, **History** and solution changes no longer freeze the window while the old process stops.
- The mouse wheel scrolls the chat even when the pointer is over a code block.
- Chat windows restored by Visual Studio at startup get the right title and receive editor commands.
- **Attach Current File as Reference** could stay disabled until another Claude command was used.
- Build errors from the `Task` alias and VS threading analyzer warnings.

## [0.1.0]

### Added
- Initial scaffold: chat tool window running the Claude Code CLI in stream-json mode, automatic
  model and effort routing, live token meter, smart selection context, lean CLAUDE.md generator,
  token-saving settings and hooks installer.
