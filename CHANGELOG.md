# Changelog

All notable changes to this project are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
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
- **Tested with Claude Code 2.1.323** (still needs 2.1.318 or newer). All live tests pass; plan mode still answers
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
- **Failed turns in the savings report**: what the turns that ended in an error cost (interrupted, a limit or an API
  error), and their share of the spend (`Ended in an error: 3 turns, $0.40 (1% of the spend)`), when they cost
  anything. The usage log now keeps why such a turn ended (`end`), and the line says it: `(1% of the spend; 2
  stopped, 1 API error)`.

### Fixed
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
