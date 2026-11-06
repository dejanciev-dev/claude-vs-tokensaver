# Changelog

All notable changes to this project are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Activity strip** above the input box while Claude works, like the Claude Code terminal:
  `✻ Imagining… (1m 05s · ↓ 9.5k tokens · thinking)` with a tip underneath. The spinner, the verb and a moving
  line take the color of what Claude is doing (thinking in orange, writing in blue, tools in amber with the
  command, "Waiting for you" in green). The token count is an estimate (the CLI's thinking estimates plus streamed
  text and tool input). Follows Windows' "Show animations" setting. Once Claude stops thinking, the strip says for
  how long: `· thought for 4s` (the CLI's own measure, `thinking_duration_ms`, from Claude Code 2.1.319;
  timed by the extension with older versions).
- **Permission card** instead of the one-line Allow/Deny bar: a plain question ("Run this command?"), Claude's
  description, the command or file in a code box, and **Allow**, **Always allow `Bash(npm test *)`** (the CLI's
  own suggested rule, saved in `.claude/settings.local.json`; verified with 2.1.318), **Deny**, and a box to tell
  Claude what to do instead. A long command shows its first 6 lines and "… +N lines": up to 25 lines open in the
  card (**Show less** folds them), a longer one opens in a read-only editor tab, so Allow / Deny stay in view. The
  answer shows under the tool row as "✔ Allowed" or "✖ Denied".
- **Colored meter**: context % green, amber or red at the /compact thresholds (the bar too), cache hits green
  when most input comes from the cache, the cost in orange, the model colored by family and capitalized ("Sonnet").
- **Tool calls look like the Claude Code terminal**: `● Update(src/a.cs)`, `● Bash(dotnet test)`,
  `● Read(…)`, `● Search(pattern: "…")` (paths relative to the solution), then a `⎿` line with the result:
  "Added 13 lines, removed 2 lines", "Read 120 lines", "Wrote 78 lines to X.cs", or the first three lines of a
  command's output and "… +N lines". Errors in red. Clicking the row shows the input and the whole output (up to
  20,000 characters) at full height, without a scroll box of its own. Claude's text between tool calls starts with a `●` too.
  Back-to-back reads and searches fold into one line, `● Read 3 files, searched for 2 patterns` ("Reading…" while
  they run), which opens on click; a single one keeps its own row. Shell commands that only read (`cat`, `head`,
  `sed -n`, `grep`, `git log`…, with no redirect into a file) join it: `Read 2 files, ran 3 read-only commands`. A command Claude gave a timeout or ran in the
  background says so under its row: `⎿ (timeout 10m)`.
- **Changes in the chat**: each file change shows its lines under the tool row like the terminal: a line-number
  gutter, `-` / `+` on full-width red / green rows, syntax colors from the file's extension, two unchanged lines
  around each change. A long change shows its first 12 lines and "… +N lines (click to see all)" opens the rest
  in place (no scroll box of its own, so the mouse wheel always scrolls the chat). Covers Edit / MultiEdit (numbered from where the change is in the file),
  Write (compared with the file as it was before) and shell commands that write a file with a heredoc
  (`cat > file <<'EOF'`, the usual way on Windows), including after `cd "dir" &&`. A change that fails is hidden.
  Above the lines: **Open diff** (Visual Studio's diff window, now also for files written by shell heredocs) and
  **Open file**.
- **Files changed** after each answer: every file the turn added, changed or deleted, with `+added −removed`; the
  path opens the file and **Diff** opens Visual Studio's diff of the file before the answer against now (several
  edits to one file show as one diff).
- **Working folder** under the title (for example `linq-samples` or `linq-samples/src/App`): the folder Claude
  Code runs in, which relative paths in the chat are based on. Click it to choose another folder (a subfolder or
  another repository, with Visual Studio's folder picker), go back to the solution folder, open it in File Explorer
  or copy its path. A new folder starts a new conversation, since Claude Code keeps conversations per folder; the
  sessions list, `@` files and custom commands follow the folder.
- **Generate Lean CLAUDE.md** knows more solution shapes: .NET Framework solutions get MSBuild and vstest
  commands (`dotnet build` can't build them), with packages from `packages.config` and web and test projects
  recognized; the target framework is read from `Directory.Build.props`; VB and F# projects are listed; React,
  Vue, Next.js and Svelte front ends get their `npm run build` / `npm test` commands. Projects the solution
  doesn't build (samples, test inputs) are summed up in one line, and projects it includes from outside the
  folder are named.
- **Route with Haiku** (Tools → Options → Claude Code → Token saving, off by default): with the model on Auto,
  a small Haiku call picks model and effort for each prompt instead of the local keyword rules, with a short
  reason (about $0.0001 and 1–2 s per prompt). It runs in a separate Claude Code process trimmed to a classifier
  (no tools, no project files, ~1.3k tokens), started when the chat opens. Without an answer in 4 s the local
  rules decide. The usage log keeps both decisions; the savings report shows how often they agree and what the
  router cost (included in the actual cost).
- **Screen readers:** Narrator, NVDA and JAWS announce when Claude finishes (with the start of the answer),
  asks for permission, has a question or a plan, wants an edit reviewed, stops responding, or hits a usage
  limit (option *Screen reader announcements*, on). **Focus Last Message** (Tools → Claude Code, `Ctrl+Alt+K, M`,
  or / → Focus last message) puts the keyboard at the start of Claude's last answer; `Esc` returns to the
  prompt box. The prompt box, the conversation, the meter and the agents panel header now have names, and the
  agents panel opens and closes with Enter or Space.
- **Claude Code dialogs** (/ → Claude Code): **Status** (version, account, model, setting sources; click a row
  to copy it), **Account & usage** (plan, each usage limit with its reset time, this session's cost and lines
  changed), **Memory & instructions** (CLAUDE.md files and saved memories with their token cost per request;
  click to open, or to create a missing file), **Output style** (switch for this project, saved in
  `.claude/settings.local.json`), **Permission rules** (deny / ask / allow) and **Hooks**, each opening the
  settings file it comes from. All read from Claude Code with no model call.
- **Review each change of an edit:** in Manual mode, when Claude wants to edit or write a file with several
  changes, a card lists each one with its removed and added lines and a check box. **Apply** only the checked
  changes, **Reject all**, or **Open diff** to see the whole file in Visual Studio's diff window. Claude is told
  which changes were rejected, in the same tool result, so it does not assume they are in the file or redo
  them. Edits with a single change keep the Allow / Deny bar.
- **Agents panel:** Claude's subagents and background commands show above the input box as a tree, each
  with what it is doing now ("Running Read: Foo.cs"), its tokens, tool calls and time, then its result.
  **Background** moves a running agent to the background so Claude carries on; **Stop** ends one agent and what
  it started, without stopping Claude. Finished ones leave at the next prompt (or **Clear finished**). A
  subagent's own tool calls are marked `↳` in the conversation, and when Claude continues by itself after a
  background task finishes, a line says which one. Once everything is done the panel folds to one line
  (`▸ ✓ 5 done · Clear finished`); each task is one line, with its status and time on the right.
- **Claude reads the Error List itself** (Tools → Options → Claude Code → Token saving, on by default): a
  read-only `get_error_list` tool, served by an MCP server inside the extension, lets Claude check errors and
  warnings after an edit instead of running a full build. It answers one line per row (`path:line:col error CODE:
  message (Project)`), errors first, filtered by severity or file and capped at 50 rows; it runs without a
  permission prompt and shows as "Error List" in the conversation. MCP tools are loaded on demand, so the tool
  costs almost nothing until Claude uses it.
- **Focus view** (/ → Focus view, or Tools → Options → Claude Code → Chat): consecutive tool calls collapse into
  one row, such as "⚙ 4 tool calls · Read ×3, Edit · ~6.2k tok", showing what is running; expand it for each call.
- **Plan as a document:** the plan card's **Open as document** opens the plan in the editor (Markdown preview
  included). Type comments anywhere or delete lines; **Request changes** sends them to Claude, each comment
  quoted with the plan line it follows, together with the feedback box.
- **Sessions list:** groups (hover → Move to group…, type a name to create one; rename or delete groups there),
  a ring and "open in VS Code · working" for sessions open elsewhere (read from Claude Code's running-session
  registry), an All / Open / Closed filter, Unarchive all, and automatic archiving of sessions unused for 14 days
  (option *Archive inactive sessions after (days)*, 0 = never).
- **Fork a session:** hover a session in the list → Fork, or / → Fork this session. The next prompt continues in
  a new session with the same history (`--fork-session`); the original stays as it was.
- **Rewind picker:** press Esc twice in the prompt box (or / → Rewind…) to pick one of your prompts and put the
  files back to before it.
- **Skills and MCP servers lists** (/ → Skills… / MCP servers…, or click those rows in the context breakdown):
  each skill with its source and what its listing costs on every request, and each MCP server with its status.
  Click to turn one on or off; failed servers can be reconnected. Skills are changed with Claude Code's own
  `edit-skill-overrides` and reloaded into the running session.
- **Usage-limit banner** (Pro/Max plans): under the meter, "5-hour limit 86% used. Resets at 3:10 PM." from 80%
  use or when Claude Code warns, and "… reached" when a limit stops you; the tooltip and the context breakdown
  list every window (5-hour, weekly, per model). From the CLI's `rate_limit_event`.
- **Syntax coloring in code blocks:** keywords, strings, comments and numbers (tags and attributes for XML/HTML)
  in light or dark colors to match the VS theme; code stays selectable and long lines scroll sideways.
- **Message timestamps** (Tools → Options → Claude Code → Chat, on by default): a time above each prompt and at
  the end of each answer's line, with a date line where the day changes; resumed sessions use their saved times.
- **Not responding:** when Claude is working and the CLI has sent nothing for 60 seconds (and is not waiting
  for your answer), a red line above the input says so, and that Esc stops it.
- **Queued messages:** a message sent while Claude is answering waits above the input box ("Queued · …") and
  moves into the conversation when Claude starts on it.
- **Compaction card:** a compaction shows the context size before it and a **Show summary** link that expands
  the summary Claude continues from (read from the CLI's session file). Also in resumed sessions and exports.
- **Invisible characters removed from prompts:** Unicode tag characters (which can hide instructions), bidi
  overrides, zero-width spaces and similar are stripped from pasted text with a notice, and from every prompt
  before it is sent. Long pastes (over 800 characters or 3+ lines) are marked "[Pasted text]" for Claude.
- **Copy response:** a copy button under each answer (also for redrawn answers), and / → Copy last response.
- **Option previews in question cards:** when Claude's question options carry a preview (a mockup or code
  snippet), it shows under the options for the hovered or chosen option, and the chosen preview is sent back
  with the answer, as the CLI's own dialog does.
- **Images:** paste a screenshot or copied image files with `Ctrl+V`, or choose image files under + → Upload from
  computer; they are sent with the prompt as image content (up to 10). Thumbnails with their estimated token cost
  show above the input box and in your message. Images are scaled to 1568 px on the long edge and sent as PNG,
  or as JPEG when the PNG would pass the 5 MB limit.
- **Rewind:** hover one of your messages and click Rewind to put the files Claude changed since that prompt back
  the way they were. A dry run shows the files and line counts first. Uses Claude Code's file checkpoints
  (new option *File checkpoints (rewind)*, on by default). The conversation itself is not rewound.
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
- Plain-language hints for common errors (not logged in, limits, rate limits, overload, an answer cut off at the output token limit,
  context too long, CLI not found).
- The footer under each answer shows the model that actually answered.
- GitHub Actions build: Release build with warnings as errors, Core tests, `.vsix` artifact.
- **Sign-in expiry notice** under the meter: "⚠ Your Claude sign-in ends in 4 days (Nov 21). Sign in", from
  10 days before a stored claude.ai sign-in ends (the CLI's `credential_expires_at`, 2.1.318; API-key users see
  nothing), and in red once it has expired or the CLI refuses with an expired-login error. **Sign in** opens a
  console running `claude auth login`. The not-logged-in hint names `claude auth login` too.
- **Old CLI warning**: when Claude Code reports a version older than 2.1.318 (the one this extension is tested
  with; `claude_code_version` in `init`), the chat says so once, with `claude update`. Every version is logged
  once in the Claude Code output pane.
- **Fork subagents** (on by default in `-p` since 2.1.302) show as `● Fork(Count to three)`, tagged "fork of this
  chat · background" in the agents panel. A background agent's ⎿ line says "Running in the background" instead
  of the CLI's internal "Async agent launched…" metadata (verified with 2.1.319, capture in
  `Fixtures/cli-2.1.319-fork.jsonl`).
- **Why a call ran without asking**: a shell command or file change that your Claude Code settings let through
  says so on its row, `● Bash(npm test) · allowed by a rule`, with a tooltip pointing to Permission rules. A shell
  command run on the permission mode alone says "allowed by the mode"; edits in Accept edits mode stay quiet.
  From the result's `tool_result_meta` (Claude Code 2.1.319).
- **Exact ⎿ lines for reads and searches** from the tool's structured result (Claude Code 2.1.319): `Read 20 of
  301 lines (from line 10)` when Claude reads part of a file, `Found 2 files` for Glob and Grep's file lists
  (counted lines before), and the Grep content mode's own line count.
- **Skills in the `/` menu and autocomplete**: the skills Claude Code reports in `init` that you can run yourself
  (project, user and plugin skills, e.g. `/anthropic-skills:pdf`) join your own commands. Picking one puts
  `/name ` in the prompt box for its arguments. Verified with 2.1.319: a project skill sent as `/hello-check
  banana` ran.
- **Subagents in the per-turn footer**: `✓ 12.0s · 48k tok · $0.031 · claude-sonnet-5-5 · 2 agents`. When Claude
  Code refuses subagents (too deeply nested, too many at once, over the budget), one fails, or the system stops
  one, the chat says so in one line. From `subagent_stats` in the result (Claude Code 2.1.319).
- **Why a call was blocked**: a call your Claude Code settings block says so on its row, `● Bash(echo hi) ·
  denied by a rule` in red, with a tooltip pointing to Permission rules ("denied by the mode" when the permission
  mode does not allow it). From the result's `tool_result_meta` (Claude Code 2.1.319).
- **Extra usage in the limit banner**: once a plan limit is reached, the banner says whether extra usage carries
  you past it ("Using extra usage.") or not ("Extra usage is off (no credits left)."), and its tooltip lists it
  with the windows. From `overageStatus` / `overageDisabledReason` in `rate_limit_event`.
- **Session notes** (`ℹ` in the chat, once per conversation, also in the Output pane) for things that change what
  a session costs or does without you seeing them, from Claude Code's `init`:
  - **Billing**: when Claude Code uses an API key (an `ANTHROPIC_API_KEY` in the environment, `apiKeyHelper`, a
    Console login) instead of your claude.ai plan, the chat says the session is billed per token to that key.
  - **MCP servers**: servers that need you to sign in (`needs-auth`, e.g. a claude.ai connector) or could not
    connect are named, since Claude can't use their tools; the note points to / → MCP servers….
  - **Permission mode**: when Claude Code starts in another mode than the one picked (e.g. Auto where the account
    or provider does not offer it), the chat says so and the mode picker shows the real mode. With Manual picked,
    your settings' default mode applies and the picker follows it quietly.
  - **Output style**: with Explanatory or Learning on, every answer carries more explanation and costs more
    output tokens; the note says so and points to / → Output style… (Concise uses the fewest).
  - **Whole large file read** (a token tip, once per conversation): when Claude reads all of a large file
    (~8k tokens or more), the chat says what it costs, since it goes with every request from then on, and
    suggests naming the method or lines you mean. A subagent's reads don't count (they stay out of this context).
- A shell command that prints nothing by design (`mkdir`, `git add`…) says `⎿ Done` instead of `(No output)`
  (`noOutputExpected`, Claude Code 2.1.319), and the CLI's own "Shell cwd was reset to …" note no longer shows up
  as a command's output on the ⎿ line.
- **Prompt cache timer**: while Claude is idle, a line under the meter says `⏱ Prompt cache expires in 4 min: …`
  in the last minutes of a 1-hour cache, then `Prompt cache expired (idle over an hour): the next prompt sends
  ~40k tokens of context at the full input price`. The lifetime comes from the usage (`ephemeral_1h` or `5m`),
  and every request keeps the cache again; small contexts get no note.

### Changed
- Tool rows open and close by clicking the row itself; the round expand buttons are gone.
- The agents panel shows only the first line of a background command (a heredoc no longer fills the panel).
- A command's leading `cd "<working folder>" &&` is left out of `Bash(...)` rows and the agents panel (the
  folder is shown under the title); a `cd` to any other folder stays.
- The line under a routed prompt reads `Auto → Sonnet, low effort` (was `auto → sonnet/low`), with the model in
  its meter color and who picked it and why on hover.
- Model router matches keywords as whole words ("prefix" no longer counts as "fix").
- Model router: add, update, extend, change, modify, improve and enhance count as task words, and the "short
  prompt" discount only applies to prompts that ask for no change. "add more samples and update the existing
  ones" now gets Sonnet at medium effort instead of low.
- `dotnet test` runs the Core test runner instead of silently reporting zero tests.

### Fixed
- Diffs of Claude's edits and writes are exact even when the tool runs before the extension reads the file:
  the result's `originalFile` (Claude Code 2.1.319) replaces that copy for the red / green lines, the Diff button
  and Files changed. The Output pane notes each correction.
- The conversation keeps following new output when it grows after it was added (a tool row filling in its
  result) or when the chat gets shorter (the permission card or agents panel opening below it). Scrolling up
  still stops the following.
- **Quiet-build hook on Windows:** Claude Code runs commands with its PowerShell tool there, so the installed hook
  (Bash only) never made `dotnet build` quiet. It now covers Bash and PowerShell; run *Install Token-Saving
  Settings and Hooks* again to update an existing install.
- **Shell output cap:** `BASH_MAX_OUTPUT_LENGTH` no longer limits what Claude receives. The installer now also sets
  `bashOutputMaxChars` (15,000 characters; Bash and PowerShell); larger output is saved to a file and Claude
  gets a short preview.
- **Meter after /compact** showed 0 tokens until the next answer; it now shows the size Claude Code measures
  (`get_context_usage`, read after every answer), so it also matches the CLI's own `/context`.
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
