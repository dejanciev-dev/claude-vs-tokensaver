# Changelog

All notable changes to this project are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- **Long answers stream without slowing down:** each streamed piece draws only the part of the answer that changed
  (usually its last paragraph), not the whole answer again, and the links in the rest aren't looked at again.

### Added

- **Tables in Claude's answers are tables** (they were lines of pipes): a shaded header row, lines between columns,
  each column aligned as the separator row says, also while the rows arrive.
- **Bold and inline code while they stream:** a `**bold` or `` `code `` that hasn't closed yet shows as bold or code,
  not its markers; when the answer ends it is read as written.
- **Strikethrough:** `~~old~~` in an answer is struck through, not shown with its tildes (also in tables and while
  it streams).
- **Task lists:** `- [ ] add tests` and `- [x] build` show a box (☐, ☑ when done) instead of the bullet and brackets.
- **Web addresses are links** when Claude writes them out (`https://…` or `<https://…>`), without the full stop or
  bracket that ends the sentence around them.
- **Diffs in color:** a `diff` or `patch` code block shows added lines green, removed lines red and its `@@` and file
  lines as headers.

### Fixed

- **Code and links inside bold or italic text:** `` **`Validate`** `` showed its backticks inside bold text, and
  `**see [docs](url)**` its brackets; they are drawn as code and links, in bold.
- **Code blocks inside list items:** each line kept the indent of the fence (also in what Copy and Insert gave), and a
  fence under a nested item showed as text. The indent comes off, and the block is drawn indented with its item.

## [0.17.0] - 2026-11-25

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.332). This release keeps up with Claude Code
2.1.331 and 2.1.332, and brings over the VS Code fixes from those versions that were bugs here too.

### Highlights

- **Links to files in Claude's answers open them,** at the line and column they name, also a file path in backticks
  (`src/Login.cs:42`, as Claude Code writes references).
- **Failures that say what to do:** "Prompt is too long" names what takes the room; a Web Fetch the site refused is a
  failed row with its status; a proxy that cuts compressed responses names the setting that fixes it; a warning when
  Claude Code couldn't save the session.
- **Requests that don't get lost:** a chat out of view that waits for you says so in an info bar with *Show*; a
  request left open when Claude Code's process ends is closed with a note instead of taking an answer into nothing.
- **Smaller fixes from VS Code 2.1.331–2.1.332:** changes to a recalled prompt are kept, an empty settings file doesn't
  stop setup, your `spinnerVerbs` word the activity row, the **/** menu keeps what you typed.

The full list follows.

### Changed

- **Verified with Claude Code 2.1.332:** all 18 live tests pass. Still needs 2.1.318 or newer.
- **Two more live tests** (`--live-all`): a Web Fetch the site refuses is a failed row, and `get_settings` gives your
  `spinnerVerbs`, so a change in either shape shows up.

### Added

- **Links to files in Claude's answers open them** (only web links did before): at the line and column when the link
  names one (`src/Login.cs:42`, `#L5-10`, `:42:7`), with the caret left where it was when it doesn't; files without an
  extension, `file://` links and percent-encoded paths too; a folder opens in File Explorer. As VS Code's 2.1.332 fixes.
  A file path in backticks, as Claude Code writes references (`src/Login.cs:42`), is the same link when the file is
  there; other code stays code.
- **"Prompt is too long" says what takes the room** (as VS Code's 2.1.331 advice): the largest part you can change,
  from Claude Code's context breakdown (no model call), with what shrinks it: tool results, attached files, the
  conversation, memory files, MCP tools or skills.
- **Your `spinnerVerbs`:** the activity row's words ("Pondering…") follow Claude Code's `spinnerVerbs` setting, from
  your user, project or managed settings: `append` adds yours, `replace` uses only yours (as VS Code fixed in 2.1.332).
- **A chat out of view that waits for you says so:** when a chat window behind another tab (or auto-hidden) asks for
  a permission, a question, a plan or an edit review, an info bar in VS's main window names the chat and what it
  waits for, with *Show*. It goes once the chat is in view or the request is answered (as VS Code fixed in
  2.1.331–2.1.332).
- **A warning when Claude Code couldn't save the session** (2.1.332 reports it when the disk is full or its quota is
  used up): resuming it later may miss messages, so free some space, then run `/compact` to save the conversation as
  a summary. Said once per conversation, also when only Claude Code's own settings (`~/.claude.json`) weren't saved.
- **A proxy that cuts compressed responses:** a turn that ends in "ZlibError fetching" (before 2.1.332) or in
  2.1.332's words for it says to set `CLAUDE_CODE_DISABLE_RESPONSE_COMPRESSION=1` (Claude Code 2.1.331 and newer),
  instead of "The request failed on the way".

### Fixed

- **When Claude Code's process ends with a request open** (a permission, a question, a plan or an edit review), the
  permission bar goes and the cards can no longer be answered into nothing; one line says so and that sending your
  prompt again goes on (as VS Code fixed in 2.1.332).
- **Changes to a recalled prompt are kept** (as VS Code since 2.1.331): Ctrl+Up recalls a prompt, you change it, and
  Ctrl+Up or Ctrl+Down no longer drops the change; it comes back while you go on browsing. The prompts sent stay as
  they were.
- **A tool call with no name** (a custom endpoint or gateway can send one; 2.1.331 fails it with an error) shows as
  "Tool" with its error, live and on resume, instead of a row with a blank name.
- **An empty `.claude/settings.json`** (or one starting with a byte order mark) no longer stops setup or a new deny
  rule with a JSON error, as VS Code fixed in 2.1.332; a broken file is still left alone.
- **A Web Fetch the site refused** (404, 500…) is a failed row, *HTTP 404 Not Found*, instead of a green one: Claude
  Code doesn't flag it as an error. A redirect it didn't follow says *Not followed: 302 Found → …*. Live and on resume,
  as VS Code fixed in 2.1.332.
- **Picking a skill from the / button keeps what you typed,** as its argument (`/review your text`), instead of
  wiping it (as VS Code fixed in 2.1.332). A command already in the box is swapped, its arguments kept.
- **Links in a long answer no longer slow it down:** the answer is drawn again for every piece that streams in, and
  each file link looked at the disk each time; now once per piece of text, and a file Claude writes after it still links.

## [0.16.0] - 2026-11-23

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.330). This release keeps up with Claude Code
2.1.330 and says what to do when a disk fills up.

### Highlights

- **Claude Code 2.1.330:** the output-limit refusal says that updating Claude Code fixes it; a cloud provider or
  gateway your organization picks counts as one; a Grep pattern ripgrep refuses says what fixes it.
- **A full disk:** a failed turn, or a file the extension saves, says to free some space (and on which drive), instead
  of a raw error. A file that was there is never left half written.
- **The keyboard, as VS Code got it in 2.1.330:** question cards by arrow keys and Enter, an option to keep Esc from
  stopping Claude, and a held Enter that sends once.

The full list follows.

### Changed

- **The output-limit refusal** (a gateway's model that takes fewer output tokens) also says that updating Claude Code
  fixes it: 2.1.330 retries at the model's limit again, as 2.1.326–2.1.328 did. The link that sets
  `CLAUDE_CODE_MAX_OUTPUT_TOKENS` stays for 2.1.329.
- **A cloud provider or gateway your organization picks** (2.1.330 applies it at `claude auth login`, with no variable
  set) is seen from the turn's usage: the chat no longer advises raising the output limit there.
- **A Grep pattern ripgrep refuses** (2.1.330 reports it) shows as one error line that says what fixes it, such as
  "ripgrep refused the pattern: a line break needs multiline: true", instead of three lines of ripgrep's text.

### Added

- **Question cards by keyboard:** ↑ and ↓ move between a question's options (picking one, for a single choice), Enter
  picks the focused option and goes to the next question, and after the last one sends the answers; Enter in the
  *Other* box does the same with what you typed (as Claude Code 2.1.330 fixed in VS Code).
- **Answered questions show your answer**, one line per question ("Fruits: Apple, Banana"), under the question's tool
  row, also in a resumed conversation, where it showed only Claude Code's sentence before.
- **Session groups in your order:** ↑ and ↓ beside each group in a session's group picker move it, and the sessions
  list shows your groups in that order (as VS Code got in 2.1.330). Groups you never moved follow, by name.
- **Escape stops Claude** (Tools → Options → Claude Code, on by default): off, Esc in the input box no longer stops an
  answer, so a stray Esc can't cut a long turn short; the Stop button does. As VS Code's `claudeCode.escapeStopsClaude`
  (2.1.330).

### Fixed

- **A full disk** now says to free some space, instead of "The request failed" (2.1.330 reports it).
- **A full disk in the extension's own files** (bookmarks, session names, exports, the usage CSV, the savings report,
  setup's settings) says "The disk is full: free some space on C:, then try again." instead of Windows' error, and the file
  that was there stays as it was instead of being left half written.
- **A held Enter** sends once: it no longer runs the command just picked from the `/` list, or sends what you typed
  while it was held (as Claude Code 2.1.330 fixed in VS Code).
- **Attaching a selection or file from the editor** focuses the chat's input box only while the chat is still the
  active window, so a chat window that takes a moment to open no longer takes the keyboard from the editor you went
  back to (as Claude Code 2.1.330 fixed in VS Code).
- **A selection of whole lines** (to the start of the next line) tells Claude how many lines it has, not one more, and
  is inlined up to the limit (as Claude Code 2.1.330 fixed in VS Code).

## [0.15.0] - 2026-11-20

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.329). This release applies what you change
without losing the conversation: Claude Code restarts in place when it has to, and says when it has to.

### Highlights

- **Restart Claude Code, keep the conversation:** a `/` menu entry, and a notice with the same link when you edit
  `CLAUDE.md` or `.mcp.json` by hand (Claude Code reads them only when it starts). It keeps the prompt cache, so it
  costs nothing extra; an MCP server you turned off stays off, and what you allowed for the session stays allowed.
  Asked for during a turn, it waits for the turn to end. The notice also says how many tokens a `CLAUDE.md` change
  adds to every request.
- **Settings without a New chat:** a read-deny rule applies at once; *Reinstall* restarts in place for the rest.
- **Bedrock, Vertex and Foundry:** no advice to raise an output limit, and the cost tooltip says the cloud bills you.

The full list follows.

### Added

- **Restart Claude Code** (`/` menu): restarts it into the same conversation, so it reads the settings, `CLAUDE.md`
  and MCP servers you changed by hand. A running Claude Code doesn't read them again: a `CLAUDE.md` written
  mid-conversation reached Claude only after a restart. Not while Claude is on a turn. The next turn still
  reads the prompt cache, so a restart costs nothing extra. Asked for while Claude is on a turn, it
  restarts once the turn ends.
- **A notice when you edit `CLAUDE.md` or `.mcp.json` by hand** (the solution's, your own `~/.claude/CLAUDE.md`, or a file one imports with `@path`) during a conversation: Claude Code reads them only
  when it starts, so the notice says this conversation still has the old ones, with a *Restart Claude Code* link.
  Not for files Claude changed itself, and not for `.claude/settings*.json`, whose rules and hooks apply at once.
  It also says how many tokens the change adds to (or takes off) every request once read, as `CLAUDE.md` goes with each.

### Changed

- **Settings applied without a New chat:** a read-deny rule added from a tip says it applies at once (Claude Code reads
  its permission rules and hooks again while it runs), and *Reinstall* in the setup check restarts Claude Code into the
  same conversation for the shell output cap, which it reads only when it starts. The *Install Token-Saving Settings*
  dialog says which part applies when, instead of "Restart the chat (New)".
- **Amazon Bedrock, Google Vertex AI and Microsoft Foundry** (`CLAUDE_CODE_USE_BEDROCK`, `_VERTEX`, `_FOUNDRY`) count
  like a gateway: a low output limit isn't called costly there, and the cut-off tip doesn't say to raise it. When
  Claude Code says a turn was served by another provider than the Anthropic API, the cost tooltip says the costs are
  Claude Code's estimate at list prices, not your bill.

### Fixed

- **An MCP server you turned off** in *MCP servers…* came back on when Claude Code started again in the same
  conversation (after a crash, or *Restart Claude Code*): the switch lasts only as long as the process. It stays off
  now, until you turn it on or start a New chat.
- **What you allowed "for this session"** in a permission prompt was asked again after Claude Code started again in the
  same conversation (a crash, or *Restart Claude Code*): Claude Code keeps those rules in the process only. They
  carry over now.

## [0.14.0] - 2026-11-20

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.329). This release keeps up with Claude Code
2.1.329: reads counted as the editor counts them, and a turn that fails behind a gateway fixed in one click.

### Highlights

- **Output limits behind a gateway:** when the gateway's model takes fewer output tokens than Claude Code asks for,
  Claude Code 2.1.329 no longer retries lower and the turn fails. A link under it sets `CLAUDE_CODE_MAX_OUTPUT_TOKENS`
  to the limit the error names, and the conversation carries on from your next prompt. That limit is no longer
  called costly, and the output limit the chat names is the one Claude Code uses (its settings before Visual Studio's
  environment).
- **Reads counted right:** "Read 3 lines" for a three-line file, not 4; "Read an empty file"; and a read of part of a
  file gets its total from the file.

The full list follows.

### Fixed

- **A file that ends in a newline** was one line longer than it is: "Read 4 lines" for a three-line file, on the tool
  row and in the large-read tip. Claude Code counts the empty line after the last newline (2.1.329 fixed only its own
  view); the extension counts the lines you see in the editor now. An empty file says "Read an empty file", not "Read 1
  line". A read of part of a file ("Read 3 of 10 lines") gets the total from the file itself.
- **A suggested next prompt that was only a placeholder**, such as `_(no suggestion)_` (Claude Code before 2.1.329), is
  no longer offered.
- **An output limit the model refuses** ("max_tokens: 128000 > 64000, which is the maximum allowed …", behind a
  gateway whose model takes fewer output tokens) got only "The request failed". Claude Code 2.1.329 no longer retries
  it at the lower limit, so the hint says to set `CLAUDE_CODE_MAX_OUTPUT_TOKENS` to the limit the error names, and a
  link under the failed turn does it: it writes the variable into the project's `.claude/settings.local.json` and
  restarts Claude Code, which carries on in the same conversation from your next prompt (Tab puts the failed one back).
  When managed settings set the variable, the project's file can't override it: the hint says so and names the
  value they set, with no link.
- **Behind a gateway** (`ANTHROPIC_BASE_URL` in Claude Code's settings or Visual Studio's environment), a
  `CLAUDE_CODE_MAX_OUTPUT_TOKENS` under 8,000 was listed as a setting that makes turns dearer, and the tip after
  cut-off answers said to raise it, though it may be all the gateway's model takes. Neither says so there now.
- **The output limit Claude Code uses:** with `CLAUDE_CODE_MAX_OUTPUT_TOKENS` set both in Visual Studio's environment and
  in a settings file, the cut-off tip named the environment's value, but Claude Code uses the settings file's. It names
  that one now, and a low limit set only in Visual Studio's environment is listed under *Settings that cost* too.

### Changed

- Tested with Claude Code 2.1.329 (still needs 2.1.318 or newer).

## [0.13.0] - 2026-11-19

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.328). This release names and prices models the
way Claude Code does, from its own models list and bills, including models the four prices in Options don't cover.

### Highlights

- **Models by their real names:** `Haiku 5.5`, not `claude-haiku-5-5`, under each answer, on agent rows and in the
  model picker, which also shows each model's price per million tokens.
- **Older models priced right:** a model set in `settings.json`, by a hook or with `ANTHROPIC_DEFAULT_HAIKU_MODEL` (Haiku
  4.5 bills 10× Haiku 5.5) is priced at what Claude Code bills for it, in the footer, the price while you type and
  the savings report.
- **Effort that a model doesn't take:** the effort picker and a tip say when Claude Code uses a lower level, or none.
- **Cost by model** in Copy session summary.

The full list follows.

### Added

- **An effort the model doesn't take:** when the model that answered doesn't list the effort you picked (Opus 4.6 has
  no xhigh; Haiku 4.5 takes no effort at all), a tip says so once per conversation: Claude Code quietly uses the next
  level down (Opus 4.6: high), or none. From Claude Code's own models list.
- **Copy session summary** says what each model cost when the conversation used more than one (`By model: Sonnet 5.5
  $0.40 · Haiku 4.5 $0.05`): main agent and agents, as Claude Code billed them.

### Fixed

- **A model set outside the picker** (in `settings.json`, by a hook, an older ID such as Haiku 4.5) was priced at its
  family's current price: Haiku 4.5 at a tenth of what it bills. A model that isn't one of the four in Options →
  Pricing is now priced from what Claude Code says it cost, from its first turn on; the cost tooltip says so. The
  price goes into the usage log with the turn (`prices`), so the savings report prices it right in later VS sessions
  too.
- The price shown while you type ("~$0.02 to send") priced the alias's family, so with `ANTHROPIC_DEFAULT_HAIKU_MODEL`
  set it showed Haiku 5.5's price and a warm cache for Haiku 4.5. It prices the model the alias resolves to now, and
  two models of one family no longer count as sharing a cache.

### Changed

- The line under an answer names the model as Claude Code does (`Haiku 5.5`, not `claude-haiku-5-5`); the ID is in
  its tooltip.
- Agents say which model they ran on the same way (`Explore · Haiku 4.5`, not `haiku`): in the Agents panel and on
  the line under a finished Agent row, so an agent on an older or dearer model stands out.
- The model picker names each choice as Claude Code does (`Haiku 5.5`) and shows its price per million tokens in and
  out, from Options → Pricing. The effort picker marks a level the next turn's model doesn't take ("Opus 4.6 takes no
  xhigh: Claude Code uses high").

## [0.12.0] - 2026-11-18

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.328). This release shows where tokens go when
Claude reads and runs things, and turns that into fixes you can click.

### Highlights

- **What reading really costs:** attached references against what Claude read of them, the folders reads fill the
  context from, files read again (by Claude or by its subagents), and builds and logs that ran loud: in the footer,
  the Agents panel, the savings report, the CSV and the session summary.
- **Tips you can act on:** add a read-deny rule for build output or minified files, put the plain command or "you
  already have it" in the box, reinstall the token-saving setup or open the settings file that overrides it.
- **Check token-saving setup:** `/` → *Check token-saving setup* says whether the quiet-build hook, the read-deny rules
  and the output cap still apply.
- **Hooks you can see:** why a hook asks on the permission card, and `allowed by a hook` / `denied by a hook` on tool
  rows.
- **Add to Claude Code Chat** from Solution Explorer, with what each reference may cost before you send.
- **Haiku in Plan mode:** plan mode answered with Sonnet when Haiku was asked for, so Auto picked Sonnet there. Claude
  Code turns only the alias `haiku` into Sonnet in plan mode; the chat now asks for Haiku by its full ID, so plan mode
  answers with Haiku (a short plan-mode turn: $0.003 instead of $0.048).

The full list follows.

### Added
- **What references really cost**: after a turn with attached files or folders, the footer's tooltip says how much
  of them Claude read against reading them whole (`References: Claude read 2 of 12 files, ~8.0k of ~40.0k tok
  (20%)`), its subagents' reads included. The savings report adds a *References read* line, and the usage CSV
  `ref_ceiling_tokens` and `ref_read_tokens` columns. A large folder still being measured when you send counts too,
  once its size is known.
- **What the quiet-build hook did**: the savings report counts the commands the token-saving hook made quiet, by
  kind, and what their output brought in (`12 commands made quiet (dotnet build 9, git log 3), ~1.2k tok came
  back`); the usage CSV adds `quiet_runs` and `quiet_tokens`.
- **Folders read most**: the savings report lists the folders whose reads brought the most into the context (two
  levels below the working folder), and for build output or vendored code (`obj`, `node_modules`…) the read-deny
  rule that keeps Claude out. Reads outside the working folder are grouped; their paths are not logged. In a report
  over several solutions each row names its solution (`Shop: src/Core`), so the same folder name in two solutions
  isn't added up as one.
- **Keep Claude out of build output**: when a turn reads 2k+ tokens from build output or vendored code (`obj`,
  `node_modules`…), a token tip says so once per conversation, with **Add `Read(./**/obj/**)`**, which merges the
  rule into the project's `.claude/settings.json` (from the next New chat). *Folders read most* now finds such
  folders at any depth (`src/App/obj`), not only in the first two levels. Minified files and source maps
  (`*.min.js`, `*.min.css`, `*.map`) count too, with their own rule (`Read(./**/*.min.js)`).
- **Subagents re-reading what the conversation has**: a subagent starts without the conversation, so it reads
  files the main agent already read again. Those tokens are logged per turn: a *Subagents re-reading* line in the
  savings report, an `agent_reread_tokens` CSV column and a line in *Copy session summary*. In the Agents panel the
  agent's row says it (`re-read ~6.0k`), and once subagents have re-read 5k+ tokens a token tip says so, once per
  conversation: for files Claude already read, asking it directly is cheaper, or tell the agent which lines it needs.
- **Reads by folder in the CSV**: a `read_folders` column (`src/Core:40000;src/App/obj:2000`, largest first).
- **Tips you can act on**: the loud-command tip has **Ask for the plain `dotnet build`**, and the repeated-read tip
  **Tell Claude it has `Foo.cs`**; each puts the request into the input box (after anything typed), to edit or send.
- **Repeated reads over time**: tokens Claude spent re-reading files it already had, unchanged, are now logged per
  turn (until now only a tip in the chat): a *Read again, unchanged* line in the savings report and a
  `repeat_read_tokens` CSV column.
- **Loud builds and logs**: a build, test or `git log` that ran in full with 2k+ tokens of output (the quiet-build
  hook leaves commands with a verbosity, a pipe or a count alone) gets a token tip, once per conversation, saying why
  and that the plain command runs quiet (or to install the hook). The savings report counts them by kind, and the
  usage CSV adds `loud_runs` and `loud_tokens`.
- **Reads in the session summary**: *Copy session summary* adds what the conversation read and ran: reads by folder,
  the attached references read against their ceiling, repeated reads, and builds made quiet or run loud.
- **Fix from the setup check**: when *Check token-saving setup* finds problems, links under it **Reinstall** the
  setup for this folder (when that fixes one) and **Open** each settings file that overrides it.

- **Hook decisions on tool rows**: a call that one of your PreToolUse hooks allowed or denied without asking says
  `· allowed by a hook` or `· denied by a hook`, with the hook's reason in the tooltip, as rules already do.
- **What a reference may cost**: the attachment line says what each attached file or folder would cost if Claude
  read it whole (`src/Big.cs (reference, ~3.2k tok if read)`, `src (reference, 12 files, ~40.0k tok if read)`), in
  amber from 20k tokens. Only the path is sent, so it's a ceiling; folders count text files only, as the `@` picker
  lists them.
- **Check token-saving setup**: `/` → *Check token-saving setup* asks Claude Code for its settings (no model call)
  and says whether the token-saving setup still applies: the quiet-build hook for Bash and PowerShell (not dropped,
  not narrowed to one shell, hooks not turned off), the read-deny rules and the shell output cap (not raised in
  another settings file). Each problem says where to fix it; the context breakdown and the Output pane say when
  there are some.

- **Why a hook asks**: when one of your PreToolUse hooks asks for confirmation with a reason, the permission card
  shows it under Claude's description (`ⓘ PreToolUse hook (settings): …`), and screen readers hear it. Claude Code
  sends the reason since 2.1.327.
- **Add to Claude Code Chat** in Solution Explorer: right-click files, folders, projects or the solution (several at
  once too) and they are attached to the chat as references, paths only, like *Attach Current File*.
### Fixed
- **Plan mode answered with Sonnet when Haiku was asked for** (since Claude Code 2.1.316), so Auto picked Sonnet in
  Plan mode and a Haiku picked by hand was priced at Sonnet. Claude Code turns the alias `haiku` into Sonnet in plan
  mode, and only the alias: the chat now learns Haiku's full ID from Claude Code at start (`claude-haiku-5-5`) and
  asks for that, so plan mode answers with Haiku and Auto picks it there again.

## [0.11.0] - 2026-11-17

Still a **preview**, for Claude Code 2.1.318 or newer (tested with 2.1.326). This release makes the numbers right and
the effort switch work, and adds help for the moment between two prompts.

### Highlights

- **Effort changes now take effect:** switching the effort of a running conversation (the router's choice per
  prompt, or the effort picker) did nothing before: every turn ran at the effort the chat started with. The footer
  and the usage log now show the effort Claude Code really applies, under any cap from your settings or organization.
- **Costs counted where they belong:** a resumed conversation's first turn no longer takes the whole conversation's
  cost; Claude Code's own requests (its suggestions, the `/compact` summary) are no longer counted as subagents;
  answers cut off at the output limit show what going on cost.
- **Between two prompts:** a suggested next prompt in the empty box when an answer ends with an offer, and after a
  failed turn what to do next (Tab puts the prompt back). Both without a model call.
- **Settings that cost:** `/` → *Settings that cost* lists the Claude Code settings that make turns dearer, and the
  chat follows `maxEffortLevel` and `promptSuggestionEnabled`.
- **Spend across conversations and time:** weekly warnings and a month pace, this conversation against your
  others, long conversations, failed turns, the 1-hour cache, time waited and more in the savings report and the CSV.

The full list follows.

### Added
- **Settings that cost**: `/` → *Settings that cost* asks Claude Code for its settings (no model call) and says in
  the chat which make turns dearer, where each is set (managed, the project's, yours) and how to change it: a default
  model of Opus or Fable, thinking always on or a large `MAX_THINKING_TOKENS`, an explanatory output style, a high
  default effort, the prompt cache off, a low `CLAUDE_CODE_MAX_OUTPUT_TOKENS`, all project MCP servers on, subagents
  forced onto Opus. The context breakdown says when some are set.
- **Answers cut off at the output limit**: when Claude's or a subagent's answer hits the output token limit, Claude
  Code asks it to go on (since 2.1.325 a subagent gives its whole answer again, more briefly), and each retry reads
  the context again. The turn footer says so with what the retries cost (`· cut off 2× ($0.004 to go on)`), the
  agent's row says `cut off 2×`, and the savings report adds a *Cut off, asked to go on* line. Once going on has
  cost $0.05 or more in a conversation, a token tip says to ask for smaller steps, and names
  `CLAUDE_CODE_MAX_OUTPUT_TOKENS` when it is set low. The usage CSV gets the last columns `output_retries` and
  `output_retry_cost_usd`.
- **The effort cap from Claude Code's settings**: with `maxEffortLevel` set (by you, the project or your
  organization), Claude Code answers at no higher effort and says so only in its debug log. The chat now reads it
  when Claude Code starts (no model call): the effort picker greys out what is above it, the router stays under it,
  and the turn footer and the usage log show the effort really used. After each model or effort switch the chat
  also reads the effort Claude Code applies, which covers a cap your organization sets per model: the footer and
  the usage log show that one, and the chat says once when it isn't the effort asked for.
- **`promptSuggestionEnabled` in Claude Code's settings**: `false` turns the chat's suggested next prompt off too, so
  one switch covers the terminal and Visual Studio. Claude Code then makes no suggestions of its own either, even with
  *Ask Claude Code for suggestions* on (none are requested, so none are billed).
- **What to do after a failed turn**, in the empty input box, as Claude Code 2.1.326 does in the terminal: from why
  the turn failed (the error kind and status Claude Code sends, or its message), the grey text says what to do, and
  Tab puts the prompt in: the same prompt again after an overloaded or server error, a Stop, a rate or usage limit,
  a sign-in or a missing model (`The API is overloaded: try again · Tab puts your prompt back`); `/compact` when
  the conversation no longer fits; going on in smaller parts after the output limit. A usage-policy block offers no
  prompt, only rewinding the message or a New chat. Shown also with suggestions turned off; no model call. An
  invalid `ANTHROPIC_API_KEY` ("Invalid API key · Fix external API key") says to fix or unset the key, both here and
  in the explanation in the chat, instead of to sign in.
- **Token tip when Claude Code's suggestions don't pay**: with *Ask Claude Code for suggestions* on, once 20 or more
  have come since Visual Studio started, under 1 in 10 was taken and they cost $0.10 or more, the chat says so once,
  with how to turn them off (the suggestions read from the answer stay, at no cost).
- **Suggested prompts in the savings report**: how often a prompt went out while a suggested next prompt showed, and
  how often Tab or → took it, for each kind (`Suggested prompts: before 40 prompts: from the answer 30, taken 8
  (27%); Claude Code's 10, taken 4 (40%)`). The usage CSV gets a last `suggestion` column (`local`, `local taken`,
  `cli`, `cli taken`).
- **Suggested next prompt**: when an answer ends with an offer ("If you want separate PRs, tell me and I'll split
  them out"), the empty input box shows the reply in grey (`Yes, split them out   Tab ⇥`). Tab or → puts it into
  the box to edit or send; typing clears it. The offers it reads include "Should I go ahead and …?", "Happy to … if
  you'd like", "Want to see …?" (`Yes, show me …`), and a closing "Which do you prefer?" over a numbered list
  (`Option 1: Split into two PRs`). Read from the answer, with no model call (Options → Chat → *Suggest
  the next prompt*, on by default). Options → CLI → *Ask Claude Code for suggestions* (off by default) uses Claude
  Code's own prediction (`--prompt-suggestions`, 2.1.325) instead, at a model call per turn, billed with the next turn.
- **What Claude Code's own requests cost**: each suggestion (with *Ask Claude Code for suggestions* on) and each
  `/compact` summary is a request on the conversation's model that reads the whole conversation from the cache,
  billed with the turn after it. The meter's tooltip shows what they have cost in the conversation (`Claude Code's
  own requests in this conversation: suggestions $0.012, /compact $0.030`); the savings report adds the suggestions'
  cost to the suggested prompts line and a *Compacting* line, both part of the actual cost; the usage CSV gets the
  last columns `side` and `side_cost_usd`. The suggestion after a conversation's last turn, which no turn bills, is
  estimated and logged when the chat ends, as a row with no turn: its cost counts, the turn counts leave it out.
- **Month pace in the meter**: from the 3rd, the meter's tooltip says where this month's spend is heading at its pace
  so far (`this month $41.20, ~$137.33 by Nov 30 at this pace`), as the savings report's month line does.
- **Pace warning**: once a month, from the 7th and while 3 days or more are left, the chat says when this month's
  pace would pass the monthly cost warning (or the one per solution) by the month's end, while there is still time
  to slow down. Not repeated after restarting Visual Studio.
- **Weekly cost warning** (Options → Cost limits → *Warn at weekly cost (USD)*, off by default): said once each
  time this week's spend from Monday, all conversations together, passes another multiple. While it is on, the
  meter's tooltip shows the week's spend and the meter turns amber past it too.
- **Token tip for a long conversation**: once per conversation, past 30 turns with 50k tokens of context or more,
  when a turn cost $0.02 or more and 1.5 times the conversation's average turn: /compact, or a New chat for a new
  topic. Given instead of the big-conversation tip on the same turn.
- **Turn number in the usage CSV**: a last `turn` column, the turn's number in its conversation (empty for turns
  logged without a session id), so a spreadsheet can see how cost grows as a conversation goes on.
- **This conversation against your others**: the meter's tooltip sets the open conversation's cost against the
  median conversation in the usage log (`This conversation $2.40 · 12× the median logged conversation ($0.20 of
  32)`), once three or more are logged.
- **This month per solution**: in a savings report for all solutions that reaches back before this month, each
  *By solution* row adds what that solution has cost this month (`($9.00 this month)`).
- **Conversation limit** (Options → Cost limits → *Ask before sending past conversation cost (USD)*, off by
  default): once a chat window's conversation has cost the amount, sending a prompt asks first, like the daily limit.
  After a yes, not again in that conversation until the next multiple; a new chat starts again.
- **Long conversations in the savings report**: what the turns past a conversation's 30th cost, their share of the
  spend, and their cost per turn against the first 30 (`Past a conversation's 30th: $4.20 in 35 turns of 2
  conversations (12% of the spend), $0.120 per turn against $0.040 in the first 30`).

### Changed
- **Tested with Claude Code 2.1.326** (still needs 2.1.318 or newer). All live tests pass; plan mode still answers
  with Sonnet when Haiku is asked for.
- **More per model in the savings report**: each *By model* row adds the cost per turn and the cache hit rate (the
  main agent's tokens; subagents may run on other models).
- **Average conversation in the savings report**: above the costliest conversations, how many there were, what one
  cost on average and the median, and how many turns one ran (`Conversations: 32, $0.40 on average (median $0.12),
  6.2 turns on average`). A median well under the average means a few long conversations cost most.
- **Why the cache was written again, in the savings report**: under *Cache written again*, its turns by likely
  cause: after a pause longer than the cache lasts (5 minutes, or an hour once the conversation used the 1-hour
  cache), after a model switch, or other (a changed CLAUDE.md, MCP tools, settings or speed), each with what it cost
  (`after a pause: 3 ($0.20), model switch: 1 ($0.05), other: 1 ($0.04)`).
- **Cache rewrites in the usage CSV**: a last `cache_rewrite` column with the likely cause (`pause`, `model` or
  `other`) on the turns the savings report counts as having written the cache again, empty on the others.
- **End reason in the usage CSV**: a last `end_reason` column, why a turn that ended in an error ended, as Claude
  Code said it (`aborted_streaming` for a Stop, `api_error` …); empty on turns that finished.
- **What a failed turn cost, in the chat**: after a turn that ended in an error and cost $0.05 or more, a note says
  so (`This turn ended with an API error after costing $0.12: its tokens are paid for.`). Not after a Stop.
- **What the costliest conversations went through**: in the savings report, each *Costliest conversations* row adds
  its cache rewrites and the turns that ended in an error, when there were any (`(2 cache rewrites, 1 error)`),
  and the models its turns ran on when more than one (`opus 12, haiku 3`), and how long it ran from its first
  turn to its last (`over 2 h 10 min`).
- **Today in the savings report**: under the month and week lines, today's spend so far and its turns, with
  yesterday's to compare (`Today: $3.20 so far in 14 turns (yesterday $5.10)`).
- **By weekday in the savings report**: once the period's turns span 14 days or more, the turns, cost and cost per
  turn of each day of the week, Monday first.
- **Did the 1-hour cache pay, in the savings report**: when Claude Code wrote the 1-hour prompt cache (as it does on
  a claude.ai plan), what that cost above 5-minute writes, against what it saved on turns that came back after a
  pause of 5–60 minutes (`1-hour cache: $0.40 more to write, ≈ $1.20 saved on 6 turns after a pause of 5–60 min
  (net ≈ $0.80 saved)`).
- **Pause in the usage CSV**: a last `pause_s` column, the seconds from the previous turn's end in the same
  conversation to this turn's start, to see in a spreadsheet which pauses let the prompt cache expire.
- **A turn against your usual one**: the turn footer adds `· 6× your usual turn` when a turn cost five times the
  median turn in the usage log or more (and $0.05 or more), once 20 turns are logged.
- **Turn spread in the savings report**: above the costliest turns, what a turn usually costs (`Turns: median $0.03,
  9 in 10 up to $0.40, dearest $2.10`), with 10 or more turns that cost something.
- **What you sent, in the usage CSV**: `prompt_chars`, `attached_lines` and `attached_files` columns, the prompt's
  length and what was attached to it, logged since the first versions but not exported until now.
- **Attached context in the savings report**: what a turn sent with files or a selection attached costs, against
  one without (`With attachments: 40 turns, $0.120 per turn (210 lines on average); without: $0.050 per turn`).
- **Time per model and effort in the savings report**: each *By model* and *By effort* row adds how long its answers
  took on average (`14.2 s per turn`), so you can see what a higher effort costs in waiting as well as in money.
- **Time waited in the savings report**: under the period, how long you waited on Claude's answers in all
  (`Waiting on Claude: 3 h 20 min over 412 turns (29 s per turn)`).
- **Solutions side by side in the savings report**: each *By solution* row adds the cost per turn and the cache hit
  rate, and its conversations (`($0.080 per turn, cache 88%, 12 conversations; $9.00 this month)`); this month's
  part moves inside the same brackets.
- **Failed turns in the savings report**: what the turns that ended in an error cost (interrupted, a limit or an API
  error), and their share of the spend (`Ended in an error: 3 turns, $0.40 (1% of the spend)`), when they cost
  anything. The usage log now keeps why such a turn ended (`end`), and the line says it: `(1% of the spend; 2
  stopped, 1 API error)`.

### Fixed
- **Effort changes during a conversation did nothing**: the chat switched the effort of a running Claude Code with
  a setting Claude Code accepts but ignores, so every turn after the first ran at the effort the process started
  with (the router's choice per prompt and the effort picker included), while the footer and the usage log showed the
  one asked for. Measured with 2.1.326: launched at low and switched to max, a puzzle took the thinking of low (about
  90 tokens against 250–300). The switch now uses the setting Claude Code follows, and the live tests check it.
- **A resumed conversation's first turn cost all of it**: Claude Code's running total carries on after `--resume`,
  so the first turn of a conversation picked under Sessions counted everything the conversation had cost before, in
  the turn footer, the usage log, the savings report and today's, this week's and this month's spend. A turn's cost is
  now what Claude Code says it billed since the last result.
- **Suggestions and /compact were counted as subagents**: Claude Code bills its own requests, a suggested next prompt
  (with *Ask Claude Code for suggestions* on) and the summary `/compact` writes, with the next turn's cost, and only
  in its per-model totals. The meter took them for subagents, so the turn footer, the usage log and the savings
  report's *Subagents* line counted them there. They are now told apart: what no subagent message explains is
  Claude Code's own request, still in the turn's cost.
- **Manual stays Manual**: Manual mode was started with no `--permission-mode`, and Claude Code 2.1.326 can then
  start in auto mode (for some organizations, or while the organization's policy hasn't loaded). It is now asked for
  by name, and if Claude Code still starts in another mode, the chat says so, as for the other modes.
- **No tag as a suggested prompt**: before 2.1.326, Claude Code's suggestion could be a bare tag such as
  `<signal type="empty" />`; one made only of tags is now dropped instead of shown in the input box.
- **Blurry text in the chat's menus** (mode, model, +, actions, context breakdown, sessions, folder): the drop
  shadow was drawn on the menu itself, which turned its text into a bitmap. The shadow is now drawn behind the
  menu, with ClearType and display text formatting like Visual Studio's own menus.
- **Haiku router on Claude Code 2.1.323**: there the first request of every Claude Code process waits 5–7 s for its
  answer (0.7 s with 2.1.322), so the router's first route in each chat missed its 4 s limit and the local rules
  were used instead. The router now sends one short warm-up request when it starts (about $0.0002, counted with
  the first route in the usage log), and when it restarts after 25 routes; the first route then takes about 1 s.
- **No "Not responding" while a hook runs long**: a hook taking over a minute (one of your own `PreToolUse` hooks,
  say) showed the red "Not responding" notice. Claude Code 2.1.323 sends a heartbeat every 30 s while a hook runs;
  the activity strip now says `PreToolUse hook running for 1m 30s` instead.
- **"Always allow" that Claude Code didn't save**: since 2.1.323 Claude Code keeps a rule for the session only when
  it looks like it holds a secret, or when git tracks `.claude/settings.local.json`, though the card still offered
  to save it there. After the call, the chat now checks the settings file and says when the rule isn't in it.
- **Context meter before the first answer** assumed a 200K window; every current model has 1M, and Claude Code
  2.1.323 also assumes 1M for a model it doesn't know. The meter now starts from 1M until Claude Code reports the
  window.

## [0.10.0] - 2026-11-11

Still a **preview**, for Claude Code 2.1.318 or newer (checked up to 2.1.322). This release is about knowing where the
money goes and keeping it in check.

### Highlights

- **Limits you can set:** cost warnings per month and per solution as well as per conversation and day, an optional
  daily limit that asks before sending, and an amber meter once a warning has been passed. The cost options now
  have their own *Cost limits* category.
- **Why a turn cost what it did:** hover the line under an answer for its input, output, thinking, cache and
  subagent costs. New one-time tips cover a big conversation re-read for a small question, a short answer from Opus
  or Fable, heavy thinking at high effort, and a long answer to a short question.
- **Spend over time:** today, this month and the last 7 days in the meter's tooltip. The savings report adds this
  month against last month and this week against last week, the costliest conversations, cost by effort, tool
  results by tool, and what rewriting the cache cost. It can cover one period and be saved as Markdown.
- **History you keep:** the usage log is now one file per month, keeping 3 months by default.

The full list follows.

### Added
- **Monthly cost warning** (Options → *Warn at monthly cost (USD)*, default 200): said once each time this calendar
  month's spend in Visual Studio, all conversations together, passes another multiple. The month's earlier turns
  come from the usage log.
- **Costliest conversations in the savings report**: each turn now logs its Claude Code session id, so the report
  lists the five dearest conversations (start, cost, turns, solution, first prompt when logged) and the usage CSV
  gets a `session` column. Turns logged before this release have no id and are left out of the list.
- **Tool results in the savings report**: each turn logs the tokens its tool results added, per tool, and the report
  says which tools filled the context across conversations (`Tool results: Read 1.2M · Shell 410k · Search 120k …`).
- **Token tip when the conversation costs more than the question**: once per conversation, when reading a big
  conversation (50k tokens or more) back from the prompt cache cost most of a turn, with what it cost and how to
  start small (a New chat for a new topic, or /compact).
- **Token tip for a short answer from a dear model**: once per conversation, when Opus or Fable, picked by hand, gave
  a short answer with no tools and no subagents, with what the same tokens cost on Haiku.
- **Cost by effort in the savings report**: each turn logs the effort it was sent with (routed or picked by hand), and
  the report lists turns, cost, cost per turn and thinking per effort. The usage CSV gets an `effort` column.
- **What the Haiku router's own picks changed**: the savings report says, for prompts where it chose another model
  than the local rules, how many went to a cheaper or a dearer model and what that changed (the same tokens at both
  models' prices).
- **Spend so far in the meter's tooltip**: `Spent in Visual Studio: today $3.20 (next warning at $20.00) · this month
  $41.20 (next warning at $200.00), at API prices.`
- **Daily hard limit** (Options → *Ask before sending past daily cost (USD)*, off by default): once today's spend
  has passed the amount, sending a prompt asks first. After a yes, it doesn't ask again that day until the next
  multiple. Commands such as /compact always go.
- **This month against last month** in the savings report: the month line adds last month's cost up to the same
  day and the change, e.g. `(Oct 1–11: $9.00, +37%)`.
- **Amber cost in the meter**: the meter's cost turns amber, with ⚠, once today's or this month's spend has passed
  its warning amount.
- **Tool results in the usage CSV**: a `tools` column, e.g. `Read:1200;Shell:400`, in the style of `agent_types`.
- **Token tip for heavy thinking**: once per conversation, when at high effort or above thinking was most of a short
  answer's output, with what it cost and a lower effort to try.
- **Effort in the line under each answer**: `· high effort` after the model, when the turn was sent with an effort
  (routed or picked); nothing for Claude Code's default.
- **Router replay with costs**: in *Show Savings Report*, each prompt the current rules would route differently says
  what that changes at its logged tokens (`sonnet → haiku (≈ −$0.029)`), followed by a total of the cheaper and
  dearer picks.
- **Effort in the session summary**: Copy session summary adds turns and cost per effort, e.g. `By effort: low 1 turn
  $0.02 · high 2 turns $0.40 · default 1 turn $0.05`.
- **What a turn's cost was made of**: hovering the line under an answer shows its input, output, thinking, cache
  reads, cache writes and subagents, each in $, and the total.
- **Cache written again, in the savings report**: what turns that wrote the conversation to the cache again (after
  the first turn of a conversation: an expired cache, a model switch, a changed CLAUDE.md) cost more than reading it.
- **Token tip for a long answer to a short question**: once per conversation, when a prompt of up to 300 characters
  got 4k written tokens or more with no tool calls, with how to ask for less.
- **MCP tool results per server**: the session summary, the savings report and the CSV group MCP tool results by
  server (`MCP github`) instead of one `MCP`.
- **Last 7 days in the meter's tooltip**: `Last 7 days: Thu $3.10 · Fri – · … · today $2.00 ($6.30 in all)`, all
  conversations, from the usage log and the turns since.
- **This week against last week** in the savings report: `This week: $14.20 since Monday (last week by the same day:
  $11.00, +29%)`.
- **/compact first, on resume**: when a resumed conversation's cache has expired, a /compact button appears by the
  meter. Compacting reads the conversation once to summarize it, instead of the first prompt writing all of it to
  the cache again.
- **Monthly cost warning per solution** (Options → *Warn at monthly cost per solution (USD)*, off by default): said
  once each time this month's spend on the open solution passes another multiple, from the usage log and the turns
  since.
- **Usage history by month** (Options → Diagnostics → *Keep usage history (months)*, default 3): the usage log is
  now one file per month (`usage-2026-11.jsonl`), and months older than that are deleted when a new month starts,
  instead of one 5 MB archive that could drop last month's turns. The old `usage.jsonl` and `usage.1.jsonl` are
  still read, and deleted once older than the months kept.
- **Save Savings Report as Markdown…** (Tools → Claude Code): the same report as a `.md` file, to attach or share.
- **Savings report period** (Options → Diagnostics, default All): the report and its Markdown file can cover this
  month, last month or the last 30 days instead of everything logged.
- **Open Usage Log Folder** (Tools → Claude Code): opens it in File Explorer and says in the Output pane how many
  files and how much space it takes; the savings report's last line says the same.

### Changed
- **Cost limits in their own Options category**: the cost warnings (conversation, daily, monthly, per solution, agent),
  *Ask before sending past daily cost* and *Stop agents at cost* moved from *Token saving* to *Cost limits*. Values
  set before are kept.

## [0.9.0] - 2026-11-11

The first release on the Visual Studio Marketplace, as a **preview**. It runs Claude Code 2.1.318 or newer (checked
up to 2.1.322) and reads its streamed output, which is not a documented API and can change between Claude Code
releases.

### Highlights

- **Spend fewer tokens:** prompts are routed to Haiku, Sonnet or Opus by a local classifier, or optionally by a
  small Haiku call. Files and large selections go as references, and the prompt cache stays warm. A one-click lean
  `CLAUDE.md` and token-saving hooks are included, along with one-time token tips when something costs more than
  it needs to.
- **See what you spend:** a colored meter shows context, cost and cache hits, plus what the next prompt will
  cost. Under each answer is a line with its tokens, cost, model, thinking and cache savings. Warnings fire per
  conversation and per day.
- **Savings report:** compares your cost against the same tokens at Opus prices, and shows what the cache saved,
  this month with a projection, spend per day and per solution, the costliest turns, and a CSV export.
- **Subagents, priced:** each agent's cost and model in the agents panel and under its call. Subagent cost by kind
  and model in the report. An amber row past a set cost, an optional automatic stop, and a *No forks* option.
- **Sessions:** resume, rename, archive, fork and rewind, with each conversation's cost and its agents' part.
- **A chat that works like the Claude Code terminal:** tool rows with inline diffs and per-change review,
  question and plan cards, permission cards, Claude Code's own dialogs, images, bookmarks, export, and screen
  reader support.

The full list follows.

### Added
- **Stop agents at cost** (Options, off by default): a subagent whose requests pass the amount is stopped, and the
  chat says so (`⏹ Stopped “…” at $1.25: past your limit of $1.00 per agent`).
- **What subagents cost, per conversation in the sessions list**: `$0.42 ($0.12 agents) · 3h`, read from Claude
  Code's own session files and their subagents' files.
- **Subagent cost by model in the savings report**: `by model: opus $1.20 (≈ $0.72 on Sonnet) · sonnet $0.40`,
  showing what Opus agents would have cost on Sonnet.
- **A subagent that passes a set cost turns amber** in the agents panel (Options → *Warn at agent cost*, default
  $0.50), so a runaway agent can be stopped early. Its tooltip says what it cost so far.
- **Options → Claude Code → No forks**: Claude can't start forks, which copy the whole conversation
  (`CLAUDE_CODE_FORK_SUBAGENT=0`, checked with Claude Code 2.1.322). The fork cost tip now points to it.
- **A token tip when a fork cost $0.05 or more**: a fork starts with a copy of the whole conversation, so for a
  task that does not need it, an Explore agent or a plain search costs much less. Said once per conversation.
- **Subagents by kind in the session summary** (`Subagents by kind: Explore $0.400 · Fork $0.030`) and an
  `agent_types` column in the usage CSV, after `agent_tokens`: each kind's share of the turn's subagent cost
  (`Explore:0.75;general-purpose:0.25`).
- **Which model each subagent ran on**, in the agents panel (`Explore · haiku`) and under its Agent call
  (`Done · haiku · 24.6k tok · $0.042`).
- **Subagent cost by kind of agent in the savings report**: `by kind of agent: Explore $0.40 · general-purpose
  $0.25 · Fork $0.03`, costliest first. The usage log keeps each turn's split (`agentTypes`); turns from before it
  show as "not known".
- **What a subagent used, under its Agent call in the conversation**: `Done · 24.6k tok · $0.042 · 3 tools` once
  it finishes (background agents too), so it stays after the agents panel is cleared.
- **The subagents' share of a turn's cost** in the line under each answer: `$0.020 ($0.012 agents)`, from $0.001.
- **Subagent tokens in the line under each answer**: `· 2 agents (31.0k tok)`. The turn's own `tok` count is
  the main agent's; this is what its subagents used on top.
- **A token tip when subagents cost more than Claude itself** in a turn (and at least $0.05): fewer agents, a
  plain search instead, or an agent defined with `model: haiku`. Said once per conversation.
- **What each subagent cost, in the agents panel**: each agent's row adds its cost at API prices next to its
  tokens (`Read: Foo.cs · 20.1k tok · $0.042 · 3 tools · 0:23`), priced request by request as it works, at the
  prices in Options → Pricing.
- **Fable and fast mode in the savings report**: what Fable turns cost above the same tokens on Opus, and what fast
  mode cost above standard speed, each with its number of turns (shown only when there were any). The usage CSV
  gets a `fast_tokens` column after `cache_write_1h`. Also what Haiku turns over 100k tokens cost above Haiku's
  normal price.
- **More billing notes**, from Claude Code's /status at the start of each session (no model call): when an
  `ANTHROPIC_AUTH_TOKEN` signs Claude Code in instead of your claude.ai login (Claude Code's own session data
  does not show this), when both it and `ANTHROPIC_API_KEY` are set, and when a Claude Console organization pays
  (Claude Code 2.1.319 can switch to one when the claude.ai login ends).
- **Fable in the model picker** (Claude Code 2.1.321): pick *Fable* to run `--model fable` (Fable 5.2). Auto and
  the Haiku router never pick it. The picker and the line under the input box say "Fable: 2× the Opus price",
  from Options → Pricing, and the meter shows the model in amber.
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
- **Repeated read tip** (once per conversation): when Claude reads a whole file again that it already has (same
  content, not edited by Claude in between, no compaction since), the chat says the same text now sits in the
  context twice and suggests telling it "you already have Big.cs". Each repeat is logged in the Output pane.
- **Large command output tip** (once per conversation): after a shell command puts ~5k tokens or more into the
  context, the chat says so and, when the output-trimming hook is not installed in the folder, points to
  Tools → Claude Code → Install Token-Saving Settings and Hooks; with it, suggests asking for filtered output.
- **Conversation cost warning**: Options → Token saving → *Warn at conversation cost (USD)* (default 5, 0 = off).
  Each time a conversation passes another multiple ($5, $10…) at API prices, the chat says so once and suggests
  a New chat for the next task or /compact.
- **Thinking tokens in the footer**: `✓ 6.9s · 26k tok · $0.002 · claude-haiku-5-5 · 56 thinking`, from
  `output_tokens_details.thinking_tokens` (Claude Code 2.1.319). Thinking is billed as output, the dearest kind.
- **Cache share in the footer**: `· 60% cached`, the turn's input served from the prompt cache (a tenth of the
  price). A low number right after a model switch or a long pause explains a dearer answer.
- **Prompt size while typing**: from ~1k tokens the line under the input box shows the prompt's size
  (`~2.0k tokens`, after the Auto route); from ~8k it adds that a paste this large goes with every later request
  and suggests a file reference (`Ctrl+Alt+K, F`).
- **Base context tip** (once per conversation): when MCP tools or memory files take ~5k tokens or more of every
  request (skill listings ~10k), the chat names the biggest one and where to cut it: / → MCP servers…, / → Memory &
  instructions… with Generate Lean CLAUDE.md, or / → Skills…. From the CLI's context breakdown after each turn.
- **Copy session summary** (`/` → Context): turns, cost at API prices, tokens (input, output, thinking, cache
  read and write), cache hits and context now, as plain text for a ticket or a commit message.
- **File sizes in the `@` picker**: each file shows what reading it would cost, `src/Big.cs   ~3.2k tok`
  (about 4 bytes per token), so a huge file stands out before you mention it.
- **Resume note**: resuming a conversation idle for over an hour (longer than any prompt cache) with ~10k tokens
  of context or more says the first prompt sends it all at the full input price, and that a New chat is cheaper
  for a new task. The size comes from the last answer's usage in the session file.
- **Token tips option**: Options → Token saving → *Token tips* (on by default) turns off the token tips (large or
  repeated reads, command output floods, heavy MCP tools / memory / skills, costly output style, cold cache on
  resume) and the prompt cache line; they then go to the Output pane only. Billing, MCP server and permission
  mode notes always show.
- **Thinking in the savings report**: the usage log records each turn's thinking tokens (`thinking`), and the
  report adds `Thinking: 300k tokens ≈ $3.00 (60% of output tokens; …)`, priced at each turn's output price.
- **Where the tokens went**: the session summary adds `Tool results: Read 10.0k · Shell 3.0k · MCP 100`, the
  tokens each kind of tool result added to the conversation (main agent only; shell commands and searches grouped).
- **Costliest turns in the savings report**: the five dearest turns with time, cost, model and tokens, and the
  prompt when previews are logged (else the route reason), so you can see where the money went.
- **What the prompt cache saved, per answer**: the footer's cache share now says what it saved,
  `· 90% cached (saved $0.018)` (from $0.001): tokens read from the cache at the cache-read price instead of the
  full input price, minus the extra for writing them (1-hour writes cost 2x input), at the prices of the model
  that answered (Options → Pricing; API prices).
- **What the prompt cache saved, per conversation**: the meter's tooltip adds `Prompt cache: 75% of input read from
  the cache, saved $0.49 in this conversation (API prices).` (or, after the first turn, that writing it cost more
  than it saved so far).
- **What the prompt cache saved, in the savings report**: `Saved by the prompt cache: $1.59 (2.0M tokens read from
  the cache instead of sent in full)`, next to model routing and file references. The usage log now records
  1-hour cache writes (`cacheWrite1h`); older lines count as 5-minute writes.
- **What the prompt cache saved, in the session summary**: `Cache hits: 75% (saved $0.49)` in / → Copy session
  summary (from $0.01).
- **Cache-break note**: when the prompt cache is written again while it is still warm, the chat says so once per
  conversation, with what it cost and the likely cause: `The prompt cache was rebuilt while still warm: ~42.0k
  tokens of context were written again instead of read (~$0.10 more than reading them). The model changed from Opus
  to Sonnet, and each model has its own cache: …`, or, with the same model, that something at the start of the
  conversation changed (CLAUDE.md, MCP servers or tools, settings). Not after /compact or once the cache has
  expired (the cold-cache note covers that). A token tip; later breaks go to the Output pane.
- **Cost in the sessions list**: each past conversation shows what it cost next to its age, `$0.42 · 3h`, read
  from its session file and its subagents' files (each API message once, priced per model at Options → Pricing,
  1-hour cache writes at 2x input). Read in the background the first time the list opens, then kept until the
  file changes.
- **Daily cost warning**: Options → Token saving → *Warn at daily cost (USD)* (default 20): the chat says so once
  each time today's spend in Visual Studio, all conversations and chat windows together, passes another multiple
  (`Today's Claude Code spend in Visual Studio has passed $20.00 ($21.37 so far …)`). Today's turns from before
  Visual Studio started are read from the usage log and do not warn again. 0 turns it off.
- **Spend by day in the savings report**: `By day:` lists the last 14 days with turns, each with turns, tokens,
  cost, cache hits and what the cache saved, and a bar for the cost (the dearest day full). It replaces
  *Cache hit rate by day*.
- **Next prompt's price**: while you type in a conversation with context, the line under the input box adds what
  sending costs in input, `~$0.02 to send (context cached)`, and why it costs more when it does: `cache expired:
  ~100.0k tokens of context at the full price`, or `another model: … cached again` when the prompt would go to
  another model (picked, or routed). Priced at the model it goes to; the answer and tool calls come on top.
- **This month and each solution in the savings report**: `This month: $12.30 so far, ~$41.00 at this pace by
  Nov 30` (the projection from the 3rd of the month), and under the all-solutions summary `By solution:`, the
  turns and cost of each solution folder, dearest first.
- **Sessions list totals**: the list's footer says what the sessions shown cost together, `12 sessions, $41.20 in
  total (API prices)`, and a **Newest first / Costliest first** entry next to the filter sorts them by cost.
- **Export Usage as CSV…** (Tools → Claude Code): saves the usage log as a CSV file, one row per turn with time,
  solution, model, routing, tokens (input, output, thinking, cache read and write), cost, duration and, when
  logged, the prompt preview. UTF-8 for Excel; text that starts like a formula is kept as text.

### Changed
- In plan mode, Auto picks Sonnet where it would pick Haiku, because Claude Code answers with Sonnet there anyway.
  The line under the input box, its price, and the usage log and savings report now name the model that answers;
  a Haiku picked by hand shows "Plan mode: Sonnet answers" with the Sonnet price.
- Token estimates for text (the prompt's size and price under the input box, tool-result sizes, file sizes in the
  `@` picker, the thresholds for token tips) count ~3 characters per token instead of 4, and learn the real ratio
  from your turns. Measured with Claude Code 2.1.322, prose, code and logs all ran at 2.7-3.0, so the old
  estimates were about 30% low. What it learned is kept across Visual Studio restarts, in
  `%LocalAppData%\claude-vs-tokensaver\token-estimate.json` (two numbers, no prompt text).
- Verified with Claude Code 2.1.322 (all live tests). The extension still needs 2.1.318 or newer and only warns
  below that; the Output pane names both: `Claude Code 2.1.320 (needs 2.1.318, tested with 2.1.322)`. Plan mode
  still answers with Sonnet when Haiku is asked for.
- Typing `/` opens a list of commands above the input box instead of one long line of text under it: `/compact`
  and your own `.claude/commands`, one row each with what it does underneath and where it comes from on the right
  (skills are no longer in it: they stay in the `/` button menu under Skills). ↑ ↓ (and Page Up / Down)
  choose, Tab or Enter puts the command in the box for its arguments, Enter on a command typed in full sends it,
  Esc closes the list. The part after a folder matches too: `/pr` finds `git:pr`. Once
  you type arguments, the line under the box shows what the command does.
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
- **Subagent output on a resumed conversation's first turn** is now exact as well, from the session's last token
  reading kept here (`model-usage.json`). If the session ran elsewhere since, the streamed messages count as before.
- **Subagent output on a new conversation's first turn** is now exact (it was counted from the streamed message,
  which ends before the output does). A resumed conversation's first turn still counts it that way.
- The savings report counts subagents. Their cost was in the actual cost, but their tokens were missing from "same
  tokens at Opus prices" and from the cache savings, so routing savings came out low when agents ran. Each turn now
  logs its subagents' tokens per model; the report shows a `Subagents:` line, the CSV an `agent_tokens` column and
  the session summary the conversation's subagent tokens.
- Haiku requests with more than 100k tokens of input are priced at 5x, as Claude Code bills them (cache reads too),
  and a token tip says so once when a Haiku conversation nears 100k. The usage CSV gets `large_context_tokens`.
- Sonnet's cache reads are priced at $0.10 per million tokens, as Claude Code bills them, not $0.20. Costs and cache
  savings for Sonnet conversations come out right (a long Sonnet conversation is mostly cache reads). If Options
  had the old default saved, it is updated; a price you entered yourself is kept. All four default prices are now
  checked against Claude Code's own costs in a test.
- Stop while Claude is editing or writing a file no longer leaves Claude unsure whether the change was made. Claude
  Code then tells Claude the change "may have been written"; the extension reads the file and, when it can tell,
  says with your next prompt whether the change is in (quoting what the file holds), for edits approved with Allow
  or made without asking, as already for reviewed ones. A reviewed edit that Stop caught before it was written is
  now reported as not applied, where Claude was told nothing.
- A slash command with an invisible character in front (a zero-width space from autocomplete or a paste) runs as
  a command instead of going to Claude as text, as with leading spaces, tabs or line breaks (Claude Code 2.1.319).
- Stop right after applying part of a reviewed edit no longer leaves Claude in the dark. Claude Code then reports
  the edit as stopped and maybe not written, and skips the hook that tells Claude which changes you rejected,
  although the edit was written. If the file holds what you approved, Claude is told with your next prompt
  (sent ahead of it, not shown in the chat or history).
- MCP tools that load only when Claude uses them (Claude Code 2.1.319+, `isLoaded: false`) no longer count as
  "tokens of every request": the base context tip and the MCP servers list count loaded tools only, and the list
  says how many load on demand.
- Turns answered by Claude Fable (`claude-fable-5-2`, Claude Code 2.1.321) are priced as Fable, $8 / $40 per
  million tokens, instead of at Sonnet's prices, a quarter of the real cost. This covers the footer, meter,
  sessions list, next-prompt line, cache savings and savings report. Options → Pricing has a new *Fable price* row.
- Turns answered in fast mode are priced at fast mode's prices, twice the standard ones (`usage.speed`), in the
  meter, sessions list, cache savings and savings report. The footer says `· fast` (or `· partly fast` when Claude
  Code fell back to standard speed mid-turn, 2.1.321), and a prompt cache rebuilt because fast mode started or
  stopped says so: fast and standard speed have separate caches.
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
