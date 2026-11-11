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
| **Tools → Options → Claude Code** | The settings page, with the categories Chat, CLI, Token saving, Pricing… |
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
| **Export Usage as CSV…** | The last columns are `session`, `effort` and `tools` |

Set the cost amounts back afterwards: daily `20`, monthly `200`, per solution `0`, ask before sending `0`.

## 7. Release (Phase 5)

These need you rather than a check list:

- Screenshots for the README and the Marketplace: the meter, the routing hint and the setup dialog.
- Install the Release `.vsix` on a clean VS 2022 and VS 2026 (x64; ARM64 if you have one).
- Marketplace publishing: the publisher ID (variable `MARKETPLACE_PUBLISHER`), the `VS_MARKETPLACE_PAT` secret
  and the `marketplace` environment's reviewers, then publish.
- Collecting about 50 real prompts to tune the router (Phase 2.1) is ongoing work, not a sitting.
