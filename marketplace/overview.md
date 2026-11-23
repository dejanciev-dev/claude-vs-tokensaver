# Claude Code for Visual Studio — Token Saver

Claude Code inside Visual Studio 2022 and 2026, built around **spending fewer tokens**. It picks the cheapest
model likely to handle each prompt, sends file *references* instead of file contents, keeps the prompt cache
warm, and shows what every turn costs.

> Community project, not affiliated with or endorsed by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic.

> **Preview.** The extension reads Claude Code's streamed output, which is not a documented API and can change
> between Claude Code releases. It is tested with Claude Code 2.1.318–2.1.330. If something breaks after a
> `claude update`, please [open an issue](https://github.com/dejanciev-dev/claude-vs-tokensaver/issues).

![Claude Code chat in Visual Studio](https://raw.githubusercontent.com/dejanciev-dev/claude-vs-tokensaver/main/src/ClaudeVS/Resources/preview.png)

## Requirements

- Visual Studio 2022 (17.x) or 2026 (18.x), x64 or ARM64
- [Claude Code](https://claude.com/product/claude-code) 2.1.318 or newer, installed and logged in, so that `claude` works in a terminal (`claude update` updates it)

## Spend fewer tokens

- **Auto model and effort routing.** A local classifier (no API call) sends simple prompts to Haiku, normal work
  to Sonnet, and only hard ones to Opus. The hint under the prompt box shows the choice and the reason before you send.
- **Cache-aware routing.** Once a conversation is large, routing stays on one model, because re-caching the whole
  context would cost more than it saves.
- **Smart selection context.** Small selections are inlined; large ones are sent as `path:L10-200` references.
  Whole files are never pasted.
- **File references and a compact Error List.** Attach the current file, pick one with `@`, or right-click files and folders in Solution Explorer (paths only).
  Errors go as one `file:line` + message line each, capped at 20. Claude can also read the Error List itself.
- **One warm process per chat**, so the prompt cache is reused between prompts.
- **Lean `CLAUDE.md` generator** and **token-saving settings and hooks** that block `bin/obj/node_modules`, cap
  shell output and rewrite `dotnet build`, `msbuild`, `git log` and `npm test` into their quiet forms.

## See what you spend

- **Live token meter:** context fill %, session tokens and cost, cache-hit rate, with `/compact` advice, plus
  what the next prompt will cost while you type. Its tooltip shows today, this month and the last 7 days.
- **A line under each answer** with its tokens, its cost (and its subagents' part), the model that actually answered,
  its effort, thinking tokens and what the prompt cache saved. Hover it for what the cost was made of.
- **Cost warnings** per conversation, per day, per month and per solution, an optional daily limit that asks before
  sending, and one-time **token tips** when something costs more than it needs to.
- **Savings report:** actual cost vs the same tokens at Opus prices, what the cache saved, this month with a
  projection and last month, this week and last week, spend per day, per solution, per model and per effort, the
  costliest turns and conversations, and what rewriting the cache cost. For one period if you like, saved as
  Markdown, or exported as CSV.
- **Settings that cost:** `/` → *Settings that cost* lists the Claude Code settings that make turns dearer (a default
  model of Opus, thinking always on, the prompt cache off …), with where each is set and how to change it.
  *Check token-saving setup* says whether the quiet-build hook, the read-deny rules and the output cap still apply.
- **Where reading goes:** how much of the attached references Claude read, the folders reads fill the context from,
  files read again (by Claude or its subagents) and builds that ran loud, with tips you can act on in one click: add
  a read-deny rule for build output, or put the plain command in the box.
- **Agents panel** for subagents and background tasks, with Stop and Run in background. It shows each agent's
  model and cost, turns a row amber past a cost you set, and can stop an agent automatically.
- **Models as Claude Code names and bills them:** `Haiku 5.5` under each answer, on agent rows and in the model
  picker (with its price per million tokens). A model the prices in Options don't cover (an older one, or one set with
  `ANTHROPIC_DEFAULT_HAIKU_MODEL`) is priced at what Claude Code bills for it, and the effort picker says when a
  model doesn't take a level.
- **Sessions list** with each conversation's cost.

## Chat

A suggested next prompt in the empty box when an answer ends with an offer, and after a failed turn what to do next
(Tab takes it; no model call), a full disk included. Behind a gateway whose model takes fewer output tokens, one click sets the limit it
names and the conversation carries on. Markdown answers with syntax-colored code blocks (Copy, Insert), diffs for Claude's edits and per-change review,
question cards (by keyboard too) and plan cards, Manual / Edit automatically / Plan / Auto modes, sessions with resume, rename, fork and groups,
restart in place (Claude Code reads a `CLAUDE.md` or `.mcp.json` you edited, and the conversation goes on),
rewind, images, bookmarks, export, Claude Code dialogs (status, usage, memory, output style, rules, hooks) and
screen reader announcements.

## First run

1. Open a solution, then **View → Other Windows → Claude Code** (`Ctrl+Alt+K, C`).
2. **Tools → Claude Code → Install Token-Saving Settings and Hooks** (once per solution).
3. **Tools → Claude Code → Generate Lean CLAUDE.md** (once per solution).
4. Select code and press `Ctrl+Alt+K, S`, or type a prompt and press **Enter**.

## Support

The extension is free and stays free. If it saves you money on tokens, you can rate it on the Marketplace,
or [open an issue](https://github.com/dejanciev-dev/claude-vs-tokensaver/issues) with what to improve.

Source, documentation and issues: [github.com/dejanciev-dev/claude-vs-tokensaver](https://github.com/dejanciev-dev/claude-vs-tokensaver)
