# Manual checks in Visual Studio

What the tests can't cover: things to click and look at in the experimental instance. One sitting, top to
bottom, takes about an hour. Tick the matching box in [PLAN.md](PLAN.md) as each one passes, and open an issue
for what doesn't.

**Before you start:** `ClaudeVS` → **Set as Startup Project** → **F5**. That opens the experimental instance
(`/rootsuffix Exp`). Open any solution there. Use a small one you don't mind Claude editing, and put it under
git so you can throw away the changes.

Tip: set the model to **Haiku** while checking, so the prompts cost cents.

## 1. The window and its commands (Phase 1.2)

| Do | Expect |
|---|---|
| **View → Other Windows → Claude Code** | The chat window opens |
| **Tools → Options → Claude Code** | The settings page, with the categories Chat, CLI, Token saving, Cost limits, Pricing… |
| **Tools → Claude Code** | The submenu, with *New Chat Window* and *Show Savings Report* among its commands |

## 2. A first conversation (Phase 1.3)

| Do | Expect |
|---|---|
| Send `hello` | The answer streams in, then the line under it: `✓ 1.2s · 12.4k tok · $0.004 · claude-haiku-…` |
| Look at the meter under the input box | The context %, session tokens, cost and cache % update after the answer |
| Ask: *add a comment at the top of Program.cs* | An `● Update(…)` row with red/green lines and a **Diff** button |
| Ask Claude to run `dir` | An Allow/Deny bar for the shell command |
| Ask for a long answer (*explain every file here*), then click **Stop** | It stops. The next prompt still gets an answer (the CLI resumes the session) |
| **Tools → Options → Claude Code → Diagnostics → Log raw CLI traffic** on | **Output → Claude Code** shows the raw lines of the next prompt (turn it off again: it's large) |

## 3. Core features (Phase 2)

| Do | Expect |
|---|---|
| Fill the context past the /compact threshold (or lower the threshold in Options), then click the **/compact** advice button | The conversation compacts. After the next turn the meter shows the smaller context |
| Select 5 lines → **Ask Claude About Selection** | The prompt gets the file and line range as a reference |
| The same with 500 lines, a whole file, and an unsaved file | Large selections go as a reference, not pasted. The unsaved file's current text is used |
| **Install Token-Saving Settings and Hooks** on the solution | `.claude/settings.json` and the hook are written. Afterwards Claude can't read `bin/` or `obj/` |

## 4. Themes (Phase 3)

Switch **Tools → Theme** between Light, Dark and Blue, in VS 2022 and VS 2026 if you have both.

Expect: every text, button, row and diff stays readable in each theme, with no black-on-dark or white-on-light.
Check the meter's colors, the agents panel, the menus and the permission bar.

## 5. Subagents (Phases 22–26)

Ask: *use two Explore agents to list the .cs files and the .json files*.

| Look at | Expect |
|---|---|
| The agents panel above the input box | Each agent's row: `Explore · haiku` (its model, the same as Claude's), then tokens, cost (`$0.004`), tools and time |
| Each `● Explore(…)` row in the chat, once the agent finishes | A line under it, such as `Done · haiku · 24.6k tok · $0.004 · 3 tools` |
| The line under the answer | `· 2 agents (31.0k tok)`, and the cost with the agents' part: `$0.020 ($0.012 agents)` |
| **Options → Warn at agent cost** set to `0.01`, then the same prompt | The rows turn amber, and their tooltip says what each cost so far |
| **Options → Stop agents at cost** set to `0.01`, then the same prompt | `⏹ Stopped “…” at $0.0…`, and Claude carries on |
| **Options → No forks** on, a new chat, then *use the Agent tool with subagent_type "fork" to say hi* | No fork runs. The Agent row fails with "Agent type 'fork' not found", and Claude says so |
| The sessions list (**History**) | Conversations with agents show `$0.42 ($0.12 agents) · 3h` |
| **Show Savings Report** | `Subagents:`, `by kind of agent:` and `by model:` lines |
| `/` → *Copy session summary* | A `Subagents by kind:` line |

Set the two agent cost options back to `0.50` and `0` afterwards.

## 6. Spend and tips (Phases 27–32)

Most of these show only once spend passes an amount. Set the amounts low for the check, and set them back afterwards.

| Do | Expect |
|---|---|
| Hover the line under any answer | A tooltip with input, output (and thinking), cache reads, cache writes, subagents and the total, in $ |
| The same line after a prompt sent at **High** effort | `· high effort` after the model |
| Hover the meter | `Spent in Visual Studio: today $… (next warning at $…) · this month $…`, then `Last 7 days: … · today $…` |
| **Options → Warn at daily cost** set to `0.01`, then any prompt | `💲 Today's … spend … has passed $0.01`, and the meter's cost turns amber with a ⚠ |
| **Options → Warn at monthly cost** and **… per solution** set to `0.01`, then any prompt | One warning for the month and one naming the solution |
| **Options → Ask before sending past daily cost** set to `0.01`, then type a prompt and send | A *Daily limit passed* box. Cancel leaves the prompt in the box; OK sends it, and the next prompt goes without asking |
| The same, but send `/compact` | No box: commands always go |
| On **Opus**, picked by hand, ask *what is 2+2?* | A one-time tip with what the same answer would have cost on Haiku |
| At **High** effort on Opus, ask a short question Claude thinks hard about | Possibly the heavy thinking tip (only when thinking was most of the answer) |
| Ask *explain async/await in depth* (a short prompt) | Possibly the long answer tip, once 4k written tokens come back |
| Resume a conversation from **History** last used over an hour ago, with a large context | The ℹ note that the cache expired, and a **/compact** button by the meter. Clicking it compacts before the first prompt |
| **Show Savings Report** | `This week:` under the month line (with last month and last week when logged), `Cache written again:` once a cache was rewritten, `Tool results:` with `MCP <server>` groups, `By effort:`, `Costliest conversations:`, and the router replay with costs |
| **Export Usage as CSV…** | The last columns are `session`, `effort`, `tools` and `turn` |
| **Save Savings Report as Markdown…** | A save dialog, then the `.md` file opens with the report in a fixed-width block |
| **Options → Diagnostics → Savings report period** set to `LastMonth`, then **Show Savings Report** | The title ends with last month's name, the period ends on its last day, and there is no `This month:` line |
| **Open Usage Log Folder** | File Explorer at `%LocalAppData%\claude-vs-tokensaver`, with `usage-<year>-<month>.jsonl` files. The Output pane says how many files and how big |
| **Tools → Options → Claude Code** | The cost warnings and limits under their own *Cost limits* category, with the values set before |

Set the cost amounts back afterwards: daily `20`, monthly `200`, per solution `0`, ask before sending `0`; and the report period to `All`.

## 7. Pace and long conversations (Phases 35–36)

The pace shows from the 3rd of the month and the pace warning from the 7th, with 3 days or more left.

| Do | Expect |
|---|---|
| Hover the meter (from the 3rd, with spend this month) | `this month $…, ~$… by <month end> at this pace (next warning at $…)` |
| **Options → Warn at monthly cost** set to a little above this month's spend (from the 7th), then a prompt | `💲 At this pace, this month's … spend … will reach ~$… by …, past your monthly warning at $…`, once. A prompt that passes the amount itself gives the usual warning instead. Restart Visual Studio and send again: no repeat |
| **Options → Ask before sending past conversation cost** set to `0.01`, then two prompts in one chat | The second asks in a *Conversation limit passed* box. Cancel leaves the prompt in the box; OK sends it, and the next goes without asking |
| The same, then **New chat** and two prompts | The second asks again (each conversation starts over) |
| The same, with **Ask before sending past daily cost** at `0.01` too | The daily box first; the conversation box only after OK |
| **Show Savings Report** | `By model:` rows with `$… per turn, cache …%`; `Conversations: …, $… on average (median $…)` above `Costliest conversations:`; `Past a conversation's 30th:` once a logged conversation has run past 30 turns |

Set the amounts back afterwards: monthly `200`, ask before sending past conversation cost `0`, past daily cost `0`.

## 8. Weeks and your conversations (Phases 37–38)

| Do | Expect |
|---|---|
| **Options → Warn at weekly cost** set to `0.01`, then a prompt | `💲 This week's Claude Code spend in Visual Studio has passed $0.01 …`, the meter's cost amber with a ⚠, and `this week $…` between today and this month in the meter's tooltip |
| The same option back at `0` | No `this week` in the tooltip |
| A conversation of more than 30 turns with a big context (e.g. resume a long one and keep going) | Once, the long conversation tip: its length, the turn's cost against its average, and /compact or New chat. Not on the same turn as the big-conversation tip |
| Hover the meter in a chat that has cost something (with 3 or more conversations in the usage log) | `This conversation $… · …× the median logged conversation ($… of …)`, or the median itself when under twice it |
| **Show Savings Report** (all solutions, period *All*, with turns before this month) | `By solution:` rows end with `; $… this month)` where a solution was used this month |
| **Export Usage as CSV…** | A last `turn` column: 1, 2, 3 … within each conversation |

Set the weekly warning back to `0` afterwards.

## 9. Claude Code 2.1.323 (Phase 39)

Needs Claude Code 2.1.323 or newer (`claude --version`).

| Do | Expect |
|---|---|
| In the open solution's `.claude/settings.local.json`, add a slow hook: `{"hooks":{"PreToolUse":[{"matcher":"Bash\|PowerShell","hooks":[{"type":"command","command":"sleep 75","timeout":120}]}]}}`, then ask Claude to run `echo hi` | After 30 s the activity strip adds `· PreToolUse hook running for 30s`, then `1m 00s`; the red "Not responding" never shows. Once the command has run, the hook part goes away |
| Remove the hook. Ask Claude to run `curl -s -H "Authorization: Bearer sk-ant-api03-Zx9Qw8Er7Ty6Ui5Op4As3Df2Gh1Jk0LzXcVbNm" https://example.invalid/` in Manual mode and click **Always allow …** | After the call: `ⓘ Claude Code allows Bash(curl …) until this session ends but didn't save it in .claude\settings.local.json …`, and the file has no such rule |
| The same with `curl -s https://example.invalid/` | No ⓘ note; the rule is in `.claude/settings.local.json` |
| **Options → Route with Haiku** on, open a new chat window, wait half a minute, send a prompt | Output → Claude Code: `Haiku router (… ms)` well under 4000 ms on the first prompt, not "gave no answer" |
| A new chat, before the first answer: look at the meter line | It ends in `(0/1.0M)`, not `(0/200.0k)` |

## 10. Turns that didn't pay off (Phase 40)

Needs **Options → Diagnostics → Keep local usage log** on. Use a context of 10k tokens or more (attach a big file),
or the rewrites are too small to count.

| Do | Expect |
|---|---|
| In one conversation: a prompt, wait 6 minutes, another prompt | **Show Savings Report**: under `Cache written again:` a line `after a pause: 1 ($…)` |
| In the same conversation, switch the model (e.g. Haiku → Sonnet) and send a prompt | The causes line adds `model switch: 1 ($…)`, after the pause |
| **Export Usage as CSV…** | A last `cache_rewrite` column: `pause` and `model` on those two turns, empty on the others |
| Ask Claude to run `sleep 25 && echo done`, allow it, and press **Stop** while it runs | The report adds `Ended in an error:          1 turn, $… (…% of the spend)` (a Stop before Claude's first answer reports no cost, so it isn't listed) |

## 11. Why turns failed, and when you spend (Phases 41–43)

Needs **Options → Diagnostics → Keep local usage log** on, and the turns from section 10.

| Do | Expect |
|---|---|
| **Show Savings Report** after the Stop in section 10 | The failed line ends `(…% of the spend; 1 stopped)` |
| **Export Usage as CSV…** | A last `end_reason` column: `aborted_streaming` (or `aborted_tools`) on the stopped turn, empty on the others. No ℹ note was shown after the Stop |
| Close Visual Studio. From a command prompt: `set CLAUDE_CODE_MAX_OUTPUT_TOKENS=60`, then start `devenv` from it. Model **Opus**, attach a big file and ask for a long answer | The answer is cut off with an error, then `ℹ This turn ended with an API error after costing $…: its tokens are paid for. …` (only when the turn cost $0.05 or more; attach more if not). The report's failed line adds `1 API error`. Start Visual Studio normally afterwards |
| **Show Savings Report**, period *All* | Under `Costliest conversations:` the rows add `(…)` with the models when more than one, how long it ran (`over … min`), and the cache rewrites and errors |
| The same report | Under the month and week lines: `Today:                      $… so far in … turns (yesterday $…)` |
| The same report, with turns logged over 14 days or more | A `By weekday:` list, Monday first, before `By day` |

## 12. The 1-hour cache and your usual turn (Phases 44–45)

Needs **Options → Diagnostics → Keep local usage log** on. The 1-hour cache is written when you're signed in with a
claude.ai plan; with an API key Claude Code writes the 5-minute one, and the cache line doesn't show.

| Do | Expect |
|---|---|
| Signed in with a claude.ai plan: a prompt with a big file attached, wait 10 minutes, another prompt in the same chat | **Show Savings Report**: `1-hour cache:               $… more to write, ≈ $… saved on 1 turn after a pause of 5–60 min (net ≈ $… saved)` |
| **Export Usage as CSV…** | A last `pause_s` column: about `600` on the second prompt, empty on the first turn of each conversation |
| With 20 or more turns logged, ask for something much bigger than usual (e.g. Opus, a big file, a long answer) | The turn footer adds `· …× your usual turn` after the cost, when the turn cost $0.05 or more and 5× the median. Not on ordinary turns. (The logged turns are read once per Visual Studio session, so today's turns count after a restart) |
| **Show Savings Report** with 10 or more turns that cost something | `Turns: median $…, 9 in 10 up to $…, dearest $…` just above `Costliest turns:` |

## 13. What you send, time and solutions (Phases 46–48)

Needs **Options → Diagnostics → Keep local usage log** on.

| Do | Expect |
|---|---|
| A prompt with a selection attached (select lines, **Ask Claude About Selection**), then one with nothing attached | **Export Usage as CSV…**: the last columns `prompt_chars`, `attached_lines`, `attached_files`; the selection's line count on the first, `0` on the second |
| **Show Savings Report** | `With attachments:           … turns, $… per turn (… lines on average); without: $… per turn` under the file references line |
| The same report | `Waiting on Claude:          … over … turns (… s per turn)` right under the `Period:` line |
| The same report | `By model:` and `By effort:` rows end with `, … s per turn)` |
| The same report, all solutions, with turns in two solutions or more | `By solution:` rows add `($… per turn, cache …%, … conversations` and, for a period reaching back before this month, `; $… this month)` |

## 14. A suggested next prompt (Phase 50)

| Do | Expect |
|---|---|
| Ask for something whose answer will end with an offer, e.g. *Make a notes.txt with two lines about LINQ, then ask me whether to add a third* | Once the turn ends, the empty input box shows `Yes, …   Tab ⇥` in grey instead of *Ask Claude to edit…* |
| Press **Tab** (or **→**) | The suggestion is in the box with the caret at its end, not sent; Enter sends it |
| Get a suggestion again, then type a letter and delete it | The grey text is gone and *Ask Claude to edit…* is back |
| An answer that ends without an offer (*Reply with the word PING*) | *Ask Claude to edit…*, and Tab moves the focus as before |
| **Shift+Tab** with a suggestion showing | Cycles the mode, as before; the suggestion stays |
| A prompt that asks for permission (Manual mode, a shell command) | No grey text while the permission card is open |
| **New chat** with a suggestion showing | *Ask Claude to edit…* |
| **Options → Chat → Suggest the next prompt** off, then the first row again | No suggestion |
| **Options → CLI → Ask Claude Code for suggestions** on, **New chat**, then any prompt | Claude Code's suggestion replaces the local one a second after the answer ends, also after answers with no offer (not after every turn) |
| With a screen reader (Narrator), Tab into the input box while a suggestion shows | It reads *Suggested prompt: … Press Tab to use it.* |

## 15. Manual mode and the suggestions' worth (Phases 51–52)

| Do | Expect |
|---|---|
| Pick **Manual** in the mode picker, **New chat**, then a prompt that edits a file | The edit asks first, and the picker still shows Manual after the answer. No note about the mode (unless your organization starts sessions in auto: then a note says Claude Code started in Auto mode, not Manual, the picker shows Auto, and the Output pane has `Mode: the CLI runs in auto, the chat asked for default`) |
| An answer that ends with *Should I go ahead and commit?* or *Want to see the diff?* | `Yes, commit   Tab ⇥` / `Yes, show me the diff   Tab ⇥` |
| An answer that ends with a numbered list and *Which do you prefer?* | `Option 1: …   Tab ⇥`, the first item's bold title or its text up to a dash or colon |
| With the usage log on: take one suggestion with **Tab** and send it, then type over the next one and send that | **Export Usage as CSV…**: the last column `suggestion` says `local taken`, then `local` |
| **Show Savings Report** | `Suggested prompts:          before … prompts: from the answer …, taken … (…%)` under the attachments line |
| **Options → CLI → Ask Claude Code for suggestions** on, **New chat**, two prompts | After the second, the Output pane says `Claude Code's own request (suggested prompt) billed with this turn: … tok, $…`; the meter's tooltip adds `Claude Code's own requests in this conversation: suggestions $… (in its cost, billed with the turn after them)`; the report's suggestions line adds Claude Code's count and `$… of the cost above`. The turn footer shows no agents for it |
| Then **/compact** | The Output pane says `(/compact summary)`; the tooltip adds `/compact $…`; the report has `Compacting:                 $… for 1 summary, of the cost above` |
| **New chat** after that | The tooltip no longer has the suggestions line |

## 16. Claude Code's own requests, and resumed conversations (Phase 53)

| Do | Expect |
|---|---|
| A conversation of a few turns, then **Sessions** → pick it again (resume), one short prompt | The turn footer shows that turn's cost (a cent or so), not the conversation's total; the meter's tooltip shows the conversation's whole cost; the Output pane's usage line and **Show Savings Report** count that turn at its own cost |
| **Options → CLI → Ask Claude Code for suggestions** on, **New chat**, a prompt that asks for a subagent (*Use an Explore agent to list the .cs files*), then another prompt | The first turn's footer shows the agent; the second's shows none, and the Output pane says `Claude Code's own request (suggested prompt) billed with this turn` for it; the report's *Subagents* line counts only the Explore agent |
| A Claude Code suggestion shows, then **New chat** before sending anything | The Output pane says `Claude Code's last suggestion in the conversation, never billed with a turn: ~$… estimated`; **Export Usage as CSV…** has a row with `suggestion` in `side`, no turn number and that cost; the report's suggestions line adds `(… estimated: a conversation's last, which no turn billed)`; the turn count is unchanged |
| With the option on, 20 or more Claude Code suggestions in one VS session, taking at most one with Tab | Once they cost $0.10 or more, the chat gives the tip `Claude Code's suggestions: you took … of … since Visual Studio started, and they cost $…`, once |

## 17. What to do after a failed turn (Phase 54)

| Do | Expect |
|---|---|
| Send a long prompt, then **Stop** (Esc) while Claude answers | The empty box says `Stopped · Tab puts your prompt back`; Tab puts the whole prompt in, not sent |
| **Options → CLI → Extra arguments** `--model claude-nonexistent-9-9`, **New chat**, any prompt (no tokens billed) | `That model isn't available: pick another in the model menu, then send it again · Tab puts your prompt back`. If Claude answers anyway, the model picker or the router replaced the model: skip this row. Clear the extra argument afterwards |
| With such a hint showing, type a letter and delete it | *Ask Claude to edit…* is back; the hint doesn't return |
| **Options → Chat → Suggest the next prompt** off, then Stop a turn again | The hint still shows |
| With **Ask Claude Code for suggestions** on, Stop a turn | The hint stays, even if Claude Code sends a suggestion afterwards |
| With Narrator, Tab into the box while a hint shows | It reads *The last request failed. Stopped. Press Tab to put in: …* |

## 18. Claude Code's settings, and answers cut off (Phase 55)

| Do | Expect |
|---|---|
| In the solution folder, `.claude/settings.local.json` with `{"maxEffortLevel": "medium"}`; **New chat**, effort set to **High**, any prompt | Once Claude Code starts, a note says your settings cap the effort at medium and the effort chip shows Medium; the effort menu greys out High, XHigh and Max with *Above your Claude Code settings' cap (medium, maxEffortLevel)*; the footer says `medium effort` |
| Same file, effort **Auto**, a prompt the router sends to Opus at high effort | The route note and the footer say medium, not high; the usage CSV's `effort` is `medium` |
| `{"promptSuggestionEnabled": false}` in the same file, **New chat**, an answer that ends with an offer | No grey suggestion; the Output pane says suggested next prompts are off. Stop a turn: the hint after a failed turn still shows |
| Remove those settings afterwards, **New chat** | The full effort menu and suggestions are back |
| **Options → CLI → Extra arguments** can't set the limit, so start VS from a command prompt with `set CLAUDE_CODE_MAX_OUTPUT_TOKENS=400`, then ask *Use one general-purpose agent to write a 700-word essay about LINQ and return it in full* | The agent's row says `cut off …×`; a turn footer says `· cut off …× ($… to go on)`; **Show Savings Report** has `Cut off, asked to go on:` |
| With `set ANTHROPIC_API_KEY=sk-ant-invalid` before starting VS, any prompt | The box says `Invalid API key: fix or unset ANTHROPIC_API_KEY, then send it again · Tab puts your prompt back`, and the chat's explanation names the key |

## 19. The effort Claude Code really uses, and settings that cost (Phase 56)

| Do | Expect |
|---|---|
| Model and effort on **Auto**, a simple prompt, then a hard one (*Refactor this class for thread safety…*) | The two footers show different efforts (e.g. `low effort`, then `high effort`); the second turn thinks noticeably more. The usage CSV's `effort` matches the footers |
| Effort picker **Low**, a prompt; then **Max**, the same prompt | The second footer says `max effort` and shows more thinking tokens |
| `.claude/settings.local.json` with `{"model": "opus", "alwaysThinkingEnabled": true, "env": {"DISABLE_PROMPT_CACHING": "1"}}`; `/` → **Settings that cost** | The chat lists the three, each with `(.claude/settings.local.json)` and what it does. Remove the file afterwards |
| With such a setting, **New chat**, a prompt, then the meter → context breakdown | Under *Tips*: `N Claude Code settings make turns dearer`; clicking it shows the list in the chat |
| No such settings, `/` → **Settings that cost** | `none of the settings known to make turns dearer is set` |
| With `set CLAUDE_CODE_MAX_OUTPUT_TOKENS=400` before starting VS, ask for long answers until going on has cost $0.05 | One token tip: `Answers cut off at the output limit … CLAUDE_CODE_MAX_OUTPUT_TOKENS is 400 …`; the usage CSV's last columns `output_retries` and `output_retry_cost_usd` are filled |

## 20. Claude Code 2.1.327: hook reasons and Add to Chat (Phase 57)

| Do | Expect |
|---|---|
| Mode **Manual**; in the solution folder, `.claude/settings.local.json` with a PreToolUse hook on `Bash` that prints `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"Check before running"}}`; **New chat**, *run `echo hi`* | The permission card shows `ⓘ PreToolUse hook (settings): Check before running` under Claude's description; Narrator reads it with the question. Remove the hook afterwards |
| A Bash call with no hook | No ⓘ line on the card |
| Solution Explorer: right-click a file → **Add to Claude Code Chat** | The chat opens; the attachment line shows the file `(reference)` |
| Select two files and a folder (Ctrl+click), right-click → **Add to Claude Code Chat**; then right-click a project and the solution | All of them are attached as references (the project and solution as their folders); send a prompt: it says `Relevant files: …` with paths only |
| Right-click a virtual item (a solution folder, *Dependencies*) | **Add to Claude Code Chat** is greyed out |

## 21. Hooks that decide, what references cost, and the setup check (Phase 58)

| Do | Expect |
|---|---|
| A PreToolUse hook on `Bash` in `.claude/settings.local.json` that prints `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"No writes here"}}`; **New chat**, *run `echo hi > a.txt`* | The Bash row ends `· denied by a hook` in red; its tooltip says *Its reason: No writes here* |
| The same hook with `"allow"` | The row says `· allowed by a hook` (gray), and no card was shown. Remove the hook afterwards |
| Attach a small file (**Attach Current File**) | After a moment the attachment line says `(reference, ~… tok if read)` |
| Solution Explorer → right-click a project → **Add to Claude Code Chat** | The line says `(reference, N files, ~… tok if read)`; when 20k+ it is amber and its tooltip explains; `bin`/`obj` are not counted |
| Right-click the solution of a large repository | The count ends at `5000+ files`, without freezing VS |
| **Install Token-Saving Settings and Hooks**, **New chat**, `/` → **Check token-saving setup** | *Token-saving setup: all in effect …* |
| `.claude/settings.local.json` with `{"bashOutputMaxChars": 200000}`, **New chat**, the meter → context breakdown | Under *Tips*: *Token-saving setup: 1 part isn't in effect*; clicking it lists `bashOutputMaxChars: 200,000 (.claude/settings.local.json)`. The Output pane says so at start |
| In `.claude/settings.json` change the hook's matcher to `Bash`, then check again | `quiet-build hook skips PowerShell`. Reinstall afterwards; it is fixed |
| A folder without the setup, `/` → **Check token-saving setup** | *not installed in this folder* |

## 22. What references, the quiet-build hook and reads really cost (Phase 59)

| Do | Expect |
|---|---|
| Right-click a project → **Add to Claude Code Chat**, wait for its size, then ask *What does the Program class do?* | Hover the turn's footer: `References: Claude read 1 of N files, ~… of ~… tok (…%)` under the cost lines |
| Attach a file and ask something that needs no file | `References: Claude read none of the 1 file (~… tok if read whole)` |
| With the token-saving setup installed, ask Claude to run `dotnet build`, Allow the quiet form; then **Show Savings Report** | `Quiet-build hook: 1 command made quiet (dotnet build 1), ~… tok came back`; **Export Usage as CSV…**: `quiet_runs` is `dotnet build:1`, `ref_ceiling_tokens` / `ref_read_tokens` filled on the earlier turns |
| After a few turns that read files, **Show Savings Report** | *Folders read most* lists the folders, largest first, with their share; a read under `obj` or `node_modules` (if any) says which `Read(./**/…/**)` rule to add |
| Set `{"bashOutputMaxChars": 200000}` in `.claude/settings.local.json`, **New chat**, `/` → **Check token-saving setup** | Under the problem: **Open .claude/settings.local.json** (opens it); no **Reinstall**, since that wouldn't fix it |
| Delete `.claude/hooks/trim-output.ps1` (the installer writes it again), check again | **Reinstall** shows; clicking it says what was written and to start a New chat; after a New chat the check says *all in effect* |
| The meter → context breakdown → *Token-saving setup: … isn't in effect* | Runs the check: the same message and links |

## 23. Reads and loud commands: from numbers to fixes (Phase 60)

| Do | Expect |
|---|---|
| A solution without the token-saving setup and with a built project; ask *Read obj/project.assets.json and tell me the package count* | After the turn, a token tip: `Claude read ~… tokens from …/obj, which holds build output …`, with **Add Read(./\*\*/obj/\*\*)** under it |
| Click **Add Read(./\*\*/obj/\*\*)** | *Added … to .claude/settings.json*; the file has the rule and your other settings. **New chat**, ask the same: Claude can't read it, and no tip |
| Ask Claude to read the same unchanged file twice in two turns, then **Show Savings Report** and **Export Usage as CSV…** | *Read again, unchanged: ~… tok in 1 turn*; the second turn's `repeat_read_tokens` is filled |
| With the setup installed, ask Claude to run `dotnet build -v detailed` | One tip: the command put ~… tokens into the context, *the quiet-build hook left it alone because it named a verbosity; ask Claude for the plain `dotnet build`*. No second, general large-output tip. The report has *Loud builds and logs: 1 (dotnet build 1)*, the CSV `loud_runs` |
| Without the setup, a large `git log` | The tip says to install the hook instead |
| After the above, `/` → **Copy session summary**, paste it | Lines *Reads by folder: …*, *References: …* (if something was attached), *Read again, unchanged: …*, *Builds and logs: …*. **New chat**, copy again: none of these lines |

## 24. Tips you can act on, and generated files (Phase 61)

| Do | Expect |
|---|---|
| A web project with a large `wwwroot/js/*.min.js`; ask Claude to read it | The build-output tip names `*.min.js files` with **Add Read(./\*\*/\*.min.js)** |
| With the setup installed, ask Claude to run `git log -n 400` | The loud tip with **Ask for the plain `git log`**; clicking it puts the request in the input box, nothing is sent; with text already typed, it goes on a new line after it |
| Ask Claude to read the same unchanged large file twice | The repeated-read tip with **Tell Claude it has …**; clicking it fills the box the same way |
| Token tips off (Options → Token saving), the same | The tips go to the Output pane, no links in the chat |
| **Export Usage as CSV…** | The last column `read_folders` lists folders with tokens, largest first |

## 25. Reads across solutions and agents (Phase 62)

| Do | Expect |
|---|---|
| Use the chat in two solutions that both have a `src` folder, then **Show Savings Report** over all solutions | *Folders read most* rows start with the solution's name (`Shop: src/Core`); a report for one solution has none |
| Right-click a large solution → **Add to Claude Code Chat** and send at once, before its size shows | The turn's footer tooltip still has the *References* line once the size is known |
| Ask Claude to read a file, then *Use an Explore agent to summarize that same file* | **Show Savings Report**: *Subagents re-reading: ~… tok*; **Copy session summary**: *Subagents re-reading what the conversation had*; the CSV's `agent_reread_tokens` |

## 26. Subagent re-reads, where you see them (Phase 63)

| Do | Expect |
|---|---|
| Ask Claude to read a large file (5k+ tokens), then *Use an Explore agent to find every public method in that file* | The agent's row in the Agents panel says `re-read ~…`; one token tip: *An Explore agent read … again, a file this conversation already has …* |
| Do it again in the same conversation | The row counts again; no second tip |
| **New chat**, a small file read twice the same way | The row says `re-read ~…`; no tip under 5k |

## 27. Models in Plan mode, and as Claude Code names them (Phases 67–70)

| Do | Expect |
|---|---|
| Model **Haiku**, mode **Plan**, ask *What does this solution do, in one sentence?* | The footer says `Haiku 5.5` (tooltip: `Model: claude-haiku-5-5`); no warning that another model answered |
| Model **Auto**, mode **Plan**, the same short question | The routing hint picks Haiku, and the footer says `Haiku 5.5` |
| Close the chat, reopen that conversation from its history, still Plan on Haiku, ask again | Still `Haiku 5.5` |
| Close VS, set `ANTHROPIC_DEFAULT_HAIKU_MODEL=claude-haiku-4-5-20251001`, start it again; Haiku, effort **High**, a short question | The footer says `Haiku 4.5`; its cost tooltip starts *at the prices Claude Code bills for claude-haiku-4-5-20251001*, and the total matches Claude Code's; one token tip: *Haiku 4.5 answered, and it takes no effort setting* |
| The same, a second question | No second tip |
| Still with the variable, open the model picker | *Haiku 4.5*, with `$1 / $5 per Mtok in / out` once a turn has run (no price before) |
| Still with it, open the effort picker | Every level says *Haiku 4.5 takes no effort: Claude Code drops it* |
| Still with it, a conversation with some context, type a prompt with **Haiku** picked | The price while typing is at Haiku 4.5's price |
| Still with it, ask *Use an Explore agent to list the files in this folder* | The agent's row says `Explore · Haiku 4.5`, and so does the line under its Agent row once done |
| Then switch to **Sonnet**, ask once more, `/` → **Copy session summary** | A `By model:` line under the cost, with Sonnet 5.5 and Haiku 4.5, most first |
| Close VS, clear the variable, start it again, **Show Savings Report** | The agents' cost of those turns is at Haiku 4.5's price, not a tenth of it |
| Open the model picker in a new chat, after the first answer | *Haiku 5.5* `$0.10 / $0.50 per Mtok in / out`, *Sonnet 5.5*, *Opus 5.5*, *Fable 5.2* with "2× the Opus price; Auto never picks it" |

## 28. Reads, suggestions and output limits (Phases 72–73)

| Do | Expect |
|---|---|
| Ask *Read README.md* (a file that ends in a newline) | The Read row says `Read N lines`, the line count VS shows for the file, not one more |
| Ask *Read the lines from 3 to the end of README.md* | `Read … of N lines (from line 3)`, N as above |
| Create an empty `empty.txt`, ask *Read empty.txt* | `Read an empty file` |
| Read a large file whole (5k+ tokens) | The token tip says *Claude read all N lines*, N as VS shows it |
| Options → **Ask Claude Code for suggestions** on (and **Suggest the next prompt**), New chat; a few turns | No grey suggestion that is only a placeholder such as `_(no suggestion)_` |
| Behind a gateway (`ANTHROPIC_BASE_URL`) whose model takes fewer output tokens than Claude Code asks for, send a prompt (skip without one) | The turn fails; the empty box says *The model takes at most N output tokens: set CLAUDE_CODE_MAX_OUTPUT_TOKENS=N (the link above sets it), then send it again · Tab puts your prompt back*; a link **Set CLAUDE_CODE_MAX_OUTPUT_TOKENS=N** under the turn |
| Click the link, then Tab and send | *Set … in .claude/settings.local.json. Claude Code restarts with it on your next prompt, in this conversation.*; `.claude/settings.local.json` has it under `env`, with whatever was there before; the answer comes, in the same conversation (the Output pane shows a new `claude` start with `--resume`) |

## 29. Release (Phase 5)

These need you rather than a check list:

- Screenshots for the README and the Marketplace: the meter, the routing hint and the setup dialog.
- Install the Release `.vsix` on a clean VS 2022 and VS 2026 (x64; ARM64 if you have one).
- Marketplace publishing: the publisher ID (variable `MARKETPLACE_PUBLISHER`), the `VS_MARKETPLACE_PAT` secret
  and the `marketplace` environment's reviewers, then publish.
- Collecting about 50 real prompts to tune the router (Phase 2.1) is ongoing work, not a sitting.
