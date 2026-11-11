using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Setup;
using ClaudeVS.Core.Tokens;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tests
{
    internal static partial class Program
    {
        private static void Test_ScreenReaderText_Plain()
        {
            Eq("Fix Use the real name", ScreenReaderText.Plain("# Fix\nUse the **real** name"));
            Eq("Steps: Run it (code block) Then see the docs and Program.cs.",
               ScreenReaderText.Plain("Steps:\n\n1. Run it\n```csharp\nvar x = 1;\n```\n- Then see [the docs](https://x.y) and `Program.cs`."));
            Eq("a b 1 2", ScreenReaderText.Plain("| a | b |\n|---|---|\n| 1 | 2 |"));
            Eq("quoted", ScreenReaderText.Plain("> quoted"));
            Eq("unclosed (code block)", ScreenReaderText.Plain("unclosed\n```\ncode never closed"));
            Eq("", ScreenReaderText.Plain(null));
        }

        private static void Test_ScreenReaderText_AnswerFinished()
        {
            Eq("Claude finished. Done.", ScreenReaderText.AnswerFinished("**Done.**"));
            Eq("Claude finished.", ScreenReaderText.AnswerFinished(""));
            string longAnswer = string.Join(" ", Enumerable.Repeat("This sentence is part of a long answer.", 20));
            string said = ScreenReaderText.AnswerFinished(longAnswer);
            True(said.EndsWith(ScreenReaderText.ReadAllHint), said);
            True(said.Length < ScreenReaderText.AnswerChars + 100, "short: " + said.Length);
            True(said.Contains("long answer. " + ScreenReaderText.ReadAllHint), "cut after a sentence: " + said);
            Eq("one two…", ScreenReaderText.Shorten("one two three", 9));
        }

        private static void Test_Markdown_Blocks()
        {
            string md = "# Title\n\nSome **bold** and `code`.\nNext line.\n\n- one\n  continued\n- two\n  1. nested\n\n> quoted\n\n---\n```csharp\nvar x = 1;\n\nvar y = 2;\n```\nafter";
            var b = ClaudeVS.Core.Markdown.MarkdownParser.Parse(md);
            string kinds = string.Join(",", b.Select(x => x.Kind + (x.Kind == ClaudeVS.Core.Markdown.MdBlockKind.ListItem ? x.Indent.ToString() : "")));
            Eq("Heading,Paragraph,ListItem0,ListItem0,ListItem1,Quote,Rule,Code,Paragraph", kinds, "block kinds");
            Eq(1, b[0].Level, "h1");
            Eq("Text:Some ,Bold:bold,Text: and ,Code:code,Text:.\nNext line.", string.Join(",", b[1].Inlines), "inlines keep line break");
            Eq("Text:one,Text: ,Text:continued", string.Join(",", b[2].Inlines), "list continuation");
            Eq(1, b[4].Number, "ordered number");
            Eq("csharp", b[7].Language, "fence lang");
            Eq("var x = 1;\n\nvar y = 2;", b[7].Code, "code keeps blank line");
            True(b[7].Closed, "closed fence");
        }

        private static void Test_Markdown_StreamingUnclosedFence()
        {
            var b = ClaudeVS.Core.Markdown.MarkdownParser.Parse("Here:\n```js\nconst a = 1;");
            Eq(2, b.Count, "para + code");
            Eq(false, b[1].Closed, "open while streaming");
            Eq("const a = 1;", b[1].Code, "partial code");
            // A longer closing fence closes; a shorter one does not.
            Eq(true, ClaudeVS.Core.Markdown.MarkdownParser.Parse("````\nx\n````")[0].Closed, "4-tick");
            Eq(false, ClaudeVS.Core.Markdown.MarkdownParser.Parse("````\nx\n```")[0].Closed, "short fence is content");
        }

        private static void Test_Markdown_Inlines()
        {
            string Render(string s) => string.Join(",", ClaudeVS.Core.Markdown.MarkdownParser.ParseInlines(s));
            Eq("Text:call ,Code:my_func(a_b),Text: now", Render("call `my_func(a_b)` now"), "code");
            Eq("Text:snake_case_name stays", Render("snake_case_name stays"), "underscores in words are not italic");
            Eq("Italic:it,Text: and ,Bold:bo", Render("*it* and __bo__"), "italic + bold");
            Eq("Text:see ,Link:docs", Render("see [docs](https://x.y/z)"), "link");
            Eq("Text:2 * 3 = 6", Render("2 * 3 = 6"), "lone star literal");
            Eq("Text:**unclosed", Render("**unclosed"), "unclosed bold literal");
            Eq("Text:*not italic*", Render("\\*not italic\\*"), "escapes");
        }

        private static void Test_PromptHistory_BrowseAndDraft()
        {
            var h = new ClaudeVS.Core.Chat.PromptHistory(max: 3);
            h.Add("a"); h.Add("b"); h.Add("b"); h.Add("c"); h.Add("d"); // dup skipped, "a" evicted
            Eq(3, h.Count, "max + dedupe");
            Eq("d", h.Previous("draft"), "newest first");
            Eq("c", h.Previous("ignored"), "older");
            Eq("b", h.Previous(null), "oldest");
            Eq(null, h.Previous(null), "nothing older");
            Eq("c", h.Next(), "newer");
            Eq("d", h.Next(), "newest");
            Eq("draft", h.Next(), "draft restored");
            Eq(null, h.Next(), "not browsing");
        }

        private static void Test_SlashCommands_LeadingSpaceAndInvisible()
        {
            // Claude Code 2.1.319 runs a command typed after a leading space, tab or line break; so do we, and the
            // list, the hint and sending agree. Invisible characters are never sent, so they don't hide a command.
            foreach (var t in new[] { "/compact", "  /compact", "\t/compact", "\n/compact", "​/compact", "﻿ /compact" })
            {
                True(SlashCommands.IsCommand(t), "command: " + Uri.EscapeDataString(t));
                Eq("compact", SlashCommands.TypedName(t), "typed name: " + Uri.EscapeDataString(t));
            }
            var cmds = new[] { new SlashCommand { Name = "compact", Source = "built-in" }, new SlashCommand { Name = "context", Source = "built-in" } };
            Eq("compact", SlashCommands.Match(cmds, "​ /comp").FirstOrDefault()?.Name, "list after invisible + space");
            foreach (var t in new[] { "compact /now", "a/b", "", null, "  ", "​" })
                True(!SlashCommands.IsCommand(t), "not a command: " + (t == null ? "null" : Uri.EscapeDataString(t)));
        }

        private static void Test_SlashCommands_DiscoverAndMatch()
        {
            string root = Path.Combine(Path.GetTempPath(), "cvs-cmd-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "sln", ".claude", "commands", "git"));
                Directory.CreateDirectory(Path.Combine(root, "home", ".claude", "commands"));
                File.WriteAllText(Path.Combine(root, "sln", ".claude", "commands", "review.md"), "---\ndescription: \"Review the diff\"\n---\nDo it.");
                File.WriteAllText(Path.Combine(root, "sln", ".claude", "commands", "git", "pr.md"), "# Open a PR\nbody");
                File.WriteAllText(Path.Combine(root, "home", ".claude", "commands", "review.md"), "user version");
                File.WriteAllText(Path.Combine(root, "home", ".claude", "commands", "standup.md"), "Write my standup");

                var all = ClaudeVS.Core.Chat.SlashCommands.Discover(Path.Combine(root, "sln"), Path.Combine(root, "home"));
                Eq("compact,git:pr,review,standup", string.Join(",", all.Select(c => c.Name).OrderBy(n => n)), "names");
                Eq("Review the diff", all.First(c => c.Name == "review").Description, "project wins, front matter");
                Eq("Open a PR", all.First(c => c.Name == "git:pr").Description, "first line");

                Eq("/review", string.Join(",", ClaudeVS.Core.Chat.SlashCommands.Match(all, "/rev")), "/review");
                Eq(4, ClaudeVS.Core.Chat.SlashCommands.Match(all, "/").Count, "all");
                Eq(0, ClaudeVS.Core.Chat.SlashCommands.Match(all, "review").Count, "needs slash");
                Eq("/git:pr", string.Join(",", ClaudeVS.Core.Chat.SlashCommands.Match(all, "/pr")), "the part after the folder");
                Eq("/review", string.Join(",", ClaudeVS.Core.Chat.SlashCommands.Match(all, "/review now")), "arguments after the name");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_SessionHistory_ListAndTitles()
        {
            Eq("C--Users-Dejan-Ciev-source-myrepos-claude-vs-tokensaver",
               ClaudeVS.Core.Chat.SessionHistory.EncodeProjectPath(@"C:\Users\Dejan Ciev\source\myrepos\claude-vs-tokensaver\"), "CLI folder naming");

            string root = Path.Combine(Path.GetTempPath(), "cvs-hist-" + Guid.NewGuid().ToString("N"));
            string sln = @"C:\work\My App";
            string folder = Path.Combine(root, ClaudeVS.Core.Chat.SessionHistory.EncodeProjectPath(sln));
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "s1.jsonl"),
                    "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":\"meta\"}}\n" +
                    "{\"type\":\"user\",\"message\":{\"content\":\"<command-name>/init</command-name>\"}}\n" +
                    "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Fix the\\nlogin bug\"}]}}\n");
                File.WriteAllText(Path.Combine(folder, "s2.jsonl"), "{\"type\":\"summary\",\"summary\":\"Price API refactor\"}\nnot json\n");
                File.WriteAllText(Path.Combine(folder, "empty.jsonl"), "{\"type\":\"system\"}\n");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "s1.jsonl"), DateTime.UtcNow.AddHours(-1));

                var list = ClaudeVS.Core.Chat.SessionHistory.List(root, sln);
                Eq("s2,s1", string.Join(",", list.Select(s => s.Id)), "newest first, empty skipped");
                Eq("Price API refactor", list[0].Title, "summary");
                Eq("Fix the login bug", list[1].Title, "first real prompt, one line");
                Eq(0, ClaudeVS.Core.Chat.SessionHistory.List(root, @"C:\other").Count, "other project");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_AtMention_Find()
        {
            var m = AtMention.Find("look at @Price", 14);
            Eq(8, m.Start, "start"); Eq("Price", m.Query, "query"); Eq(6, m.Length, "length");
            Eq("", AtMention.Find("@", 1).Query, "bare @ at start");
            Eq(null, AtMention.Find("mail me@host.com", 16), "e-mail ignored");
            Eq(null, AtMention.Find("@done and more", 14), "caret not in a mention");
            Eq("src/a", AtMention.Find("x @src/a", 8).Query, "slashes allowed");
        }

        private static void Test_FileIndex_SkipsNoiseAndRanks()
        {
            string root = Path.Combine(Path.GetTempPath(), "cvs-idx-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var f in new[] { "src/Services/PriceService.cs", "src/Api/PricesController.cs", "src/Api/OrderController.cs", "README.md",
                                          "src/bin/Debug/PriceService.dll", "node_modules/price/index.js", "wwwroot/lib/x.js" })
                {
                    string full = Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, "");
                }
                var idx = FileIndex.Build(root);
                Eq(4, idx.Count, "noise folders skipped (bin, node_modules, wwwroot/lib)");
                Eq("src/Api/PricesController.cs,src/Services/PriceService.cs", string.Join(",", idx.Search("price")), "both start with 'price': shorter path first");
                Eq("src/Services/PriceService.cs", idx.Search("service").FirstOrDefault(), "name contains beats path contains");
                Eq("src/Api/OrderController.cs", idx.Search("ordctl").FirstOrDefault(), "subsequence");
                Eq("src/Services/PriceService.cs", idx.Search("svc/pri").FirstOrDefault(), "path-segment subsequence");
                Eq("README.md", idx.Search("").First(), "empty query: top-level files first");
                Eq(0, idx.Search("zzz").Count, "no match");

                var capped = FileIndex.Build(root, maxFiles: 2);
                True(capped.Truncated && capped.Count == 2, "cap");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_ToolCallView()
        {
            Eq(("Update", "src/a.cs"), ToolCallView.Title("Edit", JObject.Parse("{\"file_path\":\"C:\\\\repo\\\\src\\\\a.cs\"}"), "C:/repo"), "path relative to the solution");
            Eq(("Bash", "cd x && cat > f <<'EOF' …"), ToolCallView.Title("Bash", new JObject { ["command"] = "cd x && cat > f <<'EOF'\nbody\nEOF" }), "first line of a command");
            Eq(("Search", "pattern: \"Foo\""), ToolCallView.Title("Grep", new JObject { ["pattern"] = "Foo" }), "grep");
            Eq(("Bash", "dotnet test"), ToolCallView.Title("Bash", new JObject { ["command"] = "cd \"C:/Users/me/repo\" && dotnet test" }, @"C:\Users\me\repo"), "cd to the working folder dropped");

            Eq("dotnet test", ToolCallView.WithoutCdToBase("cd \"C:/r\" && dotnet test", @"C:\r\"), "quoted, either slash style");
            Eq("ls", ToolCallView.WithoutCdToBase("cd '/c/r' && ls", "C:/r"), "git bash path");
            Eq("ls", ToolCallView.WithoutCdToBase("cd C:/r; ls", "c:/R"), "unquoted, semicolon, case");
            Eq("Get-ChildItem", ToolCallView.WithoutCdToBase("Set-Location \"C:\\r\"; Get-ChildItem", @"C:\r"), "PowerShell");
            Eq("cd \"C:/r/src\" && ls", ToolCallView.WithoutCdToBase("cd \"C:/r/src\" && ls", "C:/r"), "another folder stays");
            Eq("cd \"C:/r\"", ToolCallView.WithoutCdToBase("cd \"C:/r\"", "C:/r"), "a bare cd stays");
            Eq("ls && cd \"C:/r\" && ls", ToolCallView.WithoutCdToBase("ls && cd \"C:/r\" && ls", "C:/r"), "only at the start");
            Eq("cd \"C:/r\" && ls", ToolCallView.WithoutCdToBase("cd \"C:/r\" && ls", null), "no working folder");

            Eq("Read 2 files, ran 3 read-only commands", ToolCallView.LookSummary(2, 0, false, 3), "with read-only commands");
            Eq("Running 1 read-only command…", ToolCallView.LookSummary(0, 0, true, 1), "a command running");

            // From a real session: Claude reading with the shell.
            True(ToolCallView.IsReadOnlyCommand("git ls-files | head -50 && cat README.md | head -80"), "ls-files, cat, head");
            True(ToolCallView.IsReadOnlyCommand("sed -n 80,140p README.md; git ls-files | tail -30; cat src/LinqSamples/Samples/BatchingSamples.cs"), "sed -n");
            True(ToolCallView.IsReadOnlyCommand("cat tests/A.cs | head -40; grep -n \"Log\\|45\" tests/A.cs"), "grep with a quoted |");
            True(ToolCallView.IsReadOnlyCommand("cd \"C:/r\" && git log --oneline -5 2>&1"), "cd, git log, 2>&1");
            True(ToolCallView.IsReadOnlyCommand("git -C src status --short"), "git -C");
            True(ToolCallView.IsReadOnlyCommand("git branch --show-current"), "git branch listing");
            True(ToolCallView.IsReadOnlyCommand("find . -name '*.cs' 2>/dev/null | wc -l"), "find, /dev/null");
            True(ToolCallView.IsReadOnlyCommand("Get-ChildItem src | Select-Object Name"), "PowerShell listing");
            True(ToolCallView.IsReadOnlyCommand("grep -rn 'a > b' src"), "> inside quotes");

            True(!ToolCallView.IsReadOnlyCommand("cat a.cs > b.cs"), "redirect into a file");
            True(!ToolCallView.IsReadOnlyCommand("echo hi >> log.txt"), "append into a file");
            True(!ToolCallView.IsReadOnlyCommand("sed -i 's/a/b/' x.cs"), "sed -i");
            True(!ToolCallView.IsReadOnlyCommand("sed -ni 's/a/b/p' x.cs"), "sed -ni");
            True(!ToolCallView.IsReadOnlyCommand("cat > f.cs <<'EOF'"), "heredoc");
            True(!ToolCallView.IsReadOnlyCommand("ls && rm -rf bin"), "rm after ls");
            True(!ToolCallView.IsReadOnlyCommand("git ls-files | xargs rm"), "xargs");
            True(!ToolCallView.IsReadOnlyCommand("git checkout -b x"), "git checkout");
            True(!ToolCallView.IsReadOnlyCommand("git branch -D x"), "git branch -D");
            True(!ToolCallView.IsReadOnlyCommand("find . -name '*.tmp' -delete"), "find -delete");
            True(!ToolCallView.IsReadOnlyCommand("find . -exec rm {} ;"), "find -exec");
            True(!ToolCallView.IsReadOnlyCommand("cat $(ls)"), "command substitution");
            True(!ToolCallView.IsReadOnlyCommand("echo \"`whoami`\""), "backticks in double quotes");
            True(!ToolCallView.IsReadOnlyCommand("S=src && cat $S/a.cs"), "variable assignment");
            True(!ToolCallView.IsReadOnlyCommand("dotnet test"), "a build tool");
            True(!ToolCallView.IsReadOnlyCommand("sleep 5 &"), "background job");
            True(!ToolCallView.IsReadOnlyCommand("cat a\nrm b"), "two lines");
            True(!ToolCallView.IsReadOnlyCommand("cat 'unclosed"), "unclosed quote");

            Eq("Read 3 files, searched for 2 patterns", ToolCallView.LookSummary(3, 2, false), "reads and searches");
            Eq("Read 1 file", ToolCallView.LookSummary(1, 0, false), "one file");
            Eq("Searched for 1 pattern", ToolCallView.LookSummary(0, 1, false), "searches only");
            Eq("Reading 2 files, searching for 1 pattern…", ToolCallView.LookSummary(2, 1, true), "still running");
            True(ToolCallView.IsLookTool("Grep") && !ToolCallView.IsLookTool("Bash"), "look tools");

            Eq("(timeout 10m)", ToolCallView.RunNote("Bash", new JObject { ["command"] = "x", ["timeout"] = 600000 }), "timeout in minutes");
            Eq("(timeout 2m 30s, running in the background)", ToolCallView.RunNote("PowerShell", new JObject { ["timeout"] = 150000, ["run_in_background"] = true }), "timeout and background");
            Eq(null, ToolCallView.RunNote("Bash", new JObject { ["command"] = "x" }), "defaults: no note");
            Eq(null, ToolCallView.RunNote("Read", new JObject { ["timeout"] = 5000 }), "only shell tools");
            Eq("45s", ToolCallView.Duration(TimeSpan.FromSeconds(45)), "seconds");
            Eq("1h 30m", ToolCallView.Duration(TimeSpan.FromMinutes(90)), "hours");

            var bash = ToolCallView.Summarize("Bash", "l1\nl2\nl3\nl4\nl5\n", false);
            Eq("l1|l2|l3", string.Join("|", bash.Lines), "first three lines");
            Eq(2, bash.MoreLines, "… +2 lines");
            Eq("(No output)", ToolCallView.Summarize("PowerShell", "", false).Lines.Single(), "empty output");
            Eq("Read 3 lines", ToolCallView.Summarize("Read", "     1\ta\n     2\tb\n     3\tc", false).Lines.Single(), "read");
            var edit = InlineDiff.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"a.cs\",\"old_string\":\"x\",\"new_string\":\"y\\nz\"}"));
            Eq("Added 2 lines, removed 1 line", ToolCallView.Summarize("Edit", "The file a.cs has been updated successfully.", false, edit).Lines.Single(), "edit summary from the diff");
            var err = ToolCallView.Summarize("Bash", "Exit code 1\nboom", true);
            True(err.IsError && err.Lines[0] == "Exit code 1", "error shows its first lines");
            Eq("ok", string.Join("|", ToolCallView.Summarize("Bash", "ok\n<system-reminder>\nnote\n</system-reminder>", false).Lines), "system reminders are not output");
        }

        private static void Test_TimeAgo()
        {
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            string Ago(TimeSpan t) => ClaudeVS.Core.Chat.SessionHistory.TimeAgo(now - t, now);
            Eq("now", Ago(TimeSpan.FromSeconds(30)), "now");
            Eq("5m", Ago(TimeSpan.FromMinutes(5)), "minutes");
            Eq("3h", Ago(TimeSpan.FromHours(3.5)), "hours");
            Eq("2d", Ago(TimeSpan.FromDays(2)), "days");
            Eq("3w", Ago(TimeSpan.FromDays(27)), "weeks");
            Eq("1y", Ago(TimeSpan.FromDays(400)), "years");
        }

        private static void Test_FileIndex_Sizes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cvs-sizes-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "src"));
                File.WriteAllText(Path.Combine(dir, "src", "Big.cs"), new string('x', 12_800));
                File.WriteAllText(Path.Combine(dir, "src", "Empty.cs"), "");
                var index = FileIndex.Build(dir);
                var big = index.SearchWithSizes("big").Single();
                Eq("src/Big.cs", big.Path, "the path is what gets inserted");
                Eq("~4.3k tok", big.SizeLabel);
                Eq("src/Big.cs   ~4.3k tok", big.ToString(), "what the list shows");
                Eq("src/Empty.cs", index.SearchWithSizes("empty").Single().ToString(), "empty file: no size");
                Eq("", new FileHit { Path = "gone.cs" }.SizeLabel, "unreadable: no size");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        private static void Test_SlashPicker_Matching()
        {
            var cmds = new List<SlashCommand>
            {
                new SlashCommand { Name = "compact", Source = "built-in" },
                new SlashCommand { Name = "anthropic-skills:docs", Source = "skill" },
                new SlashCommand { Name = "anthropic-skills:docx", Source = "skill" },
                new SlashCommand { Name = "docs-review", Source = "project" },
            };
            Eq("/docs-review,/anthropic-skills:docs", string.Join(",", SlashCommands.Match(cmds, "/docs")), "whole-name matches first, then the part after the plugin");
            Eq("/anthropic-skills:docs,/anthropic-skills:docx", string.Join(",", SlashCommands.Match(cmds, "/anthropic-skills:doc")), "full name typed");
            Eq(4, SlashCommands.Match(cmds, "/").Count, "all for a bare slash");
            Eq("/compact,/docs-review", string.Join(",", SlashCommands.Match(SlashCommands.ForPicker(cmds), "/")), "the list while typing leaves skills out");

            Eq("anthropic-skills:", cmds[1].Prefix, "prefix");
            Eq("docs", cmds[1].ShortName, "short name");
            Eq("", cmds[0].Prefix, "no prefix");
            Eq("compact", cmds[0].ShortName, "short = name");
            Eq("Built-in", cmds[0].SourceLabel, "label");

            Eq("doc", SlashCommands.TypedName("  /doc"), "typing the name");
            Eq("", SlashCommands.TypedName("/"), "just the slash");
            Eq(null, SlashCommands.TypedName("/docs write a memo"), "arguments: no list");
            Eq(null, SlashCommands.TypedName("/docs\n"), "line break: no list");
            Eq(null, SlashCommands.TypedName("fix /this"), "not a command");
        }

        private static void Test_SessionTitle_Priority()
        {
            const string prompt = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"First prompt\"}}";
            const string ai = "{\"type\":\"ai-title\",\"aiTitle\":\"AI title\",\"sessionId\":\"s\"}";
            const string custom = "{\"type\":\"custom-title\",\"customTitle\":\"My name\",\"sessionId\":\"s\"}";
            var files = new[] { TempFile(prompt), TempFile(prompt, ai), TempFile(prompt, ai, custom, ai), TempFile("{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"content\":\"x\"}}") };
            try
            {
                Eq("First prompt", SessionHistory.ReadTitle(files[0]));
                Eq("AI title", SessionHistory.ReadTitle(files[1]));
                Eq("My name", SessionHistory.ReadTitle(files[2]), "custom title wins over a later AI title");
                Eq(null, SessionHistory.ReadTitle(files[3]), "nothing to resume");
            }
            finally { foreach (var f in files) File.Delete(f); }
        }

        private static void Test_SessionTitle_FoundInTailOfBigFile()
        {
            var lines = new List<string> { "{\"type\":\"user\",\"message\":{\"content\":\"First prompt\"}}" };
            string filler = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 2000) + "\"}]}}";
            for (int i = 0; i < 600; i++) lines.Add(filler); // ~1.2 MB: head and tail are read separately
            lines.Add("{\"type\":\"custom-title\",\"customTitle\":\"Named late\",\"sessionId\":\"s\"}");
            string f = TempFile(lines.ToArray());
            try { Eq("Named late", SessionHistory.ReadTitle(f)); }
            finally { File.Delete(f); }
        }

        private static void Test_SessionMeta_NameArchiveApply()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-meta-" + Guid.NewGuid().ToString("N"));
            var store = new SessionMetaStore(Path.Combine(dir, "sessions.json"));
            try
            {
                Eq(null, store.Get("a"));
                store.SetName("a", "  Login\nwork ");
                store.SetArchived("b", true);
                Eq("Login work", store.Get("a").Name);
                var list = store.Apply(new List<SessionInfo> { new SessionInfo { Id = "a", Title = "cli" }, new SessionInfo { Id = "b", Title = "cli b" }, new SessionInfo { Id = "c", Title = "cli c" } });
                Eq("Login work", list[0].Title);
                Eq("cli b", list[1].Title);
                True(list[1].Archived && !list[0].Archived && !list[2].Archived, "archived flag");
                store.SetName("a", "");
                Eq(null, store.Get("a"), "clearing the name forgets the session");
                store.SetArchived("b", false);
                True(!store.Get("b").Archived && store.Get("b").UnarchivedUtc != null, "unarchived, and remembered so auto-archive waits");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Test_SessionTranscript_Read()
        {
            string f = TempFile(
                "{\"type\":\"custom-title\",\"customTitle\":\"n\"}",
                "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"content\":\"How does login work?\"}}",
                "{\"type\":\"user\",\"uuid\":\"u0\",\"isMeta\":true,\"message\":{\"content\":\"meta\"}}",
                "{\"type\":\"user\",\"uuid\":\"u2\",\"message\":{\"content\":\"<local-command-caveat>x</local-command-caveat>\"}}",
                "{\"type\":\"assistant\",\"uuid\":\"a1\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a2\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Let me look.\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a3\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:/a/Login.cs\"}}]}}",
                "{\"type\":\"user\",\"uuid\":\"u3\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"0123456789\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a3\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"dup\",\"name\":\"Read\",\"input\":{}}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a4\",\"isSidechain\":true,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"subagent\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a5\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"It uses **JWT**.\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a6\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}}",
                "{\"type\":\"system\",\"uuid\":\"s1\",\"subtype\":\"compact_boundary\"}",
                "{\"type\":\"user\",\"uuid\":\"u4\",\"isCompactSummary\":true,\"message\":{\"content\":\"This session is being continued…\"}}",
                "{\"type\":\"user\",\"uuid\":\"u5\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}]}}",
                "not json",
                "{\"type\":\"user\",\"uuid\":\"u6\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Thanks\"}]}}");
            try
            {
                var t = SessionTranscript.Read(f);
                Eq("User|Answer|Tool|Answer|Compacted|User", string.Join("|", t.Items.Select(i => i.Kind)));
                Eq("How does login work?", t.Items[0].Text);
                Eq("u1", t.Items[0].Uuid, "prompt keeps its message id (for rewind)");
                Eq("Read: C:/a/Login.cs", t.Items[2].Text);
                Eq(10, t.Items[2].ResultChars, "result attached to its tool call");
                Eq("It uses **JWT**.\n\nDone.", t.Items[3].Text, "consecutive text blocks are one answer");

                var last2 = SessionTranscript.Read(f, maxItems: 2);
                Eq(4, last2.Skipped);
                Eq(TranscriptItemKind.Compacted, last2.Items[0].Kind);
            }
            finally { File.Delete(f); }
        }

        private static void Test_Export_Markdown()
        {
            var t = new SessionTranscript();
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = "How does login work?" });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = "Let me look." });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Tool, Text = "Read: C:/a/Login.cs", ResultChars = 8000 });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Tool, Text = "Bash: echo `x`", ResultChars = 0, IsError = true });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = "It uses **JWT**." });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.Compacted });
            t.Items.Add(new TranscriptItem { Kind = TranscriptItemKind.User, Text = "Thanks" });
            string md = ConversationExport.ToMarkdown(t, "Login\nquestions", new DateTime(2026, 11, 3, 10, 5, 0));
            string expected =
                "# Login questions\n\n*Exported from Claude Code in Visual Studio on 2026-11-03 10:05*\n" +
                "\n## You\n\nHow does login work?\n" +
                "\n## Claude\n\nLet me look.\n" +
                "\n- 🔧 `Read: C:/a/Login.cs` · ~2.7k tok\n- 🔧 `Bash: echo 'x'` · failed\n" +
                "\nIt uses **JWT**.\n" +
                "\n---\n\n*Conversation compacted here; Claude continued from a summary of the part above.*\n" +
                "\n## You\n\nThanks\n";
            Eq(expected, md);
            True(!ConversationExport.ToMarkdown(t, "x", DateTime.Now, includeTools: false).Contains("🔧"), "tools left out");
        }

        private static void Test_Export_FileName()
        {
            Eq("Fix - the bug.md", ConversationExport.FileName("Fix / the bug?"));
            Eq("claude-conversation.md", ConversationExport.FileName("  "));
            Eq(63, ConversationExport.FileName(new string('a', 100)).Length);
            True(SessionHistory.SessionFile(@"C:\p", @"C:\src\Shop", "abc").EndsWith(@"C--src-Shop\abc.jsonl"), "session file path");
        }

        private static void Test_Question_PreviewsParsedAndAnnotated()
        {
            var input = JObject.Parse("{\"questions\":[{\"question\":\"Layout?\",\"header\":\"Layout\",\"multiSelect\":false,\"options\":[" +
                "{\"label\":\"Grid\",\"description\":\"cards\",\"preview\":\"[ ][ ]\\n[ ][ ]\"},{\"label\":\"List\",\"description\":\"rows\"}]}]}");
            var q = AskUserQuestionTool.Parse(input).Single();
            Eq("[ ][ ]\n[ ][ ]", q.Options[0].Preview);
            Eq(null, q.Options[1].Preview);
            var withPreview = AskUserQuestionTool.BuildAnswer(input, new Dictionary<string, IReadOnlyList<string>> { ["Layout?"] = new[] { "Grid" } });
            Eq("Grid", (string)withPreview["answers"]["Layout?"]);
            Eq("[ ][ ]\n[ ][ ]", (string)withPreview.SelectToken("annotations['Layout?'].preview"));
            var without = AskUserQuestionTool.BuildAnswer(input, new Dictionary<string, IReadOnlyList<string>> { ["Layout?"] = new[] { "List" } });
            True(without["annotations"] == null, "no preview, no annotations");
        }

        private static void Test_Sanitizer_RemovesInvisible()
        {
            string hidden = string.Concat("ignore previous".Select(ch => char.ConvertFromUtf32(0xE0000 + ch))); // tag-character smuggling
            string input = "Fix​ the‮ bug" + hidden + "﻿.";
            Eq("Fix the bug.", PromptSanitizer.RemoveInvisible(input, out int removed));
            Eq(3 + "ignore previous".Length, removed);
            Eq("👩‍💻 کتاب‌ها", PromptSanitizer.RemoveInvisible("👩‍💻 کتاب‌ها", out removed), "ZWJ emoji and ZWNJ kept");
            Eq(0, removed);
            string plain = "nothing to do";
            True(ReferenceEquals(plain, PromptSanitizer.RemoveInvisible(plain, out _)), "no copy when clean");
        }

        private static void Test_Sanitizer_MarksLongPastes()
        {
            string log = "line1\nline2\nline3\nline4";
            Eq("Why does this fail?\n[Pasted text]\nline1\nline2\nline3\nline4\n[End of pasted text]\nThanks",
               PromptSanitizer.MarkPastes("Why does this fail?" + log + "Thanks", new[] { log }));
            Eq("short paste", PromptSanitizer.MarkPastes("short paste", new[] { "short" }), "short pastes are not marked");
            Eq("edited", PromptSanitizer.MarkPastes("edited", new[] { log }), "paste no longer in the text");
            True(PromptSanitizer.IsLongPaste(new string('x', 801)) && !PromptSanitizer.IsLongPaste(new string('x', 800)), "800-char limit");
        }

        private static void Test_Compaction_SummaryReadAndCleaned()
        {
            string summary = "This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion.\\n\\n<system-reminder>\\n<tools-used-before-summary>Read</tools-used-before-summary>\\n</system-reminder>\\n\\n## Summary\\n1. Fixed the login bug.";
            string f = TempFile(
                "{\"type\":\"user\",\"uuid\":\"u1\",\"message\":{\"content\":\"Fix login\"}}",
                "{\"type\":\"system\",\"uuid\":\"s1\",\"subtype\":\"compact_boundary\",\"compactMetadata\":{\"trigger\":\"auto\",\"preTokens\":697718}}",
                "{\"type\":\"user\",\"uuid\":\"u2\",\"isCompactSummary\":true,\"message\":{\"role\":\"user\",\"content\":\"" + summary + "\"}}",
                "{\"type\":\"user\",\"uuid\":\"u3\",\"message\":{\"content\":\"Next\"}}");
            try
            {
                var t = SessionTranscript.Read(f);
                Eq("User|Compacted|User", string.Join("|", t.Items.Select(i => i.Kind)));
                var c = t.Items[1];
                Eq(697718L, c.PreTokens);
                Eq("auto", c.Trigger);
                Eq("## Summary\n1. Fixed the login bug.", c.Text, "preamble and system-reminder removed");
                Eq(c.Text, SessionTranscript.ReadLastCompactSummary(f), "live read from the tail");
                True(ConversationExport.ToMarkdown(t, "x", DateTime.Now).Contains("<summary>Summary Claude continued from</summary>\n\n## Summary"), "export includes the summary");
                File.AppendAllText(f, "{\"type\":\"system\",\"uuid\":\"s2\",\"subtype\":\"compact_boundary\"}\n");
                Eq(null, SessionTranscript.ReadLastCompactSummary(f), "a new compaction whose summary is not written yet");
            }
            finally { File.Delete(f); }
        }

        private static void Test_Transcript_Timestamps()
        {
            string f = TempFile(
                "{\"type\":\"user\",\"uuid\":\"u1\",\"timestamp\":\"2026-11-02T09:42:22.258Z\",\"message\":{\"content\":\"Hi\"}}",
                "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-11-02T09:42:25.000Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}}",
                "{\"type\":\"assistant\",\"uuid\":\"a2\",\"timestamp\":\"2026-11-02T09:42:31.500Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"there\"}]}}",
                "{\"type\":\"user\",\"uuid\":\"u2\",\"message\":{\"content\":\"No time\"}}");
            try
            {
                var t = SessionTranscript.Read(f);
                Eq(new DateTime(2026, 11, 2, 9, 42, 22, 258, DateTimeKind.Utc), t.Items[0].TimestampUtc);
                Eq(DateTimeKind.Utc, t.Items[0].TimestampUtc.Value.Kind);
                Eq(new DateTime(2026, 11, 2, 9, 42, 31, 500, DateTimeKind.Utc), t.Items[1].TimestampUtc, "answer: time of its last part");
                Eq(null, t.Items[2].TimestampUtc);
            }
            finally { File.Delete(f); }
        }

        private static string Colored(string code, string lang) =>
            string.Join(" ", ClaudeVS.Core.Markdown.SyntaxHighlighter.Tokenize(code, lang).Select(s => s.Kind.ToString()[0] + ":" + code.Substring(s.Start, s.Length)));

        private static void Test_Syntax_CSharp()
        {
            Eq("K:public K:class K:int N:42 K:var S:\"a \\\" b\" C:// done", Colored("public class Foo { int x = 42; var s = \"a \\\" b\"; } // done", "csharp"));
            Eq("S:@\"C:\\x\"\"y\" C:/* multi\nline */", Colored("@\"C:\\x\"\"y\" /* multi\nline */", "cs"));
            Eq("", Colored("public class X", "text"), "plain text is not colored");
        }

        private static void Test_Syntax_OtherLanguages()
        {
            Eq("K:def S:'''doc''' C:# note K:return K:None", Colored("def f():\n    '''doc'''  # note\n    return None", "python"));
            Eq("K:SELECT K:from K:where S:'x' C:-- all", Colored("SELECT * from t where a = 'x' -- all", "sql"));
            Eq("T:<Project A:Sdk S:\"Microsoft.NET.Sdk\" T:> C:<!-- c --> T:</Project T:>", Colored("<Project Sdk=\"Microsoft.NET.Sdk\"><!-- c --></Project>", "xml"));
            Eq("S:\"a\" K:true S:\"b\" N:1.5", Colored("{\"a\": true, \"b\": 1.5}", "json"));
            Eq("K:const S:`t ${x}`", Colored("const s = `t ${x}`;", "ts"));
            Eq("S:\"s\" N:7 C:// c", Colored("x := \"s\" + 7 // c", "unknownlang"), "unknown languages still get strings, numbers and comments");
            Eq("K:$true K:-eq C:# c", Colored("$x = $true -eq $y # c", "powershell"));
            True(Colored("a.b(1)", "cs").Contains("N:1") && !Colored("x1 = 2", "cs").Contains("N:1"), "digits inside names are not numbers");
        }

        private static void Test_SessionMeta_GroupsAndAutoArchive()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-meta2-" + Guid.NewGuid().ToString("N"));
            var store = new SessionMetaStore(Path.Combine(dir, "sessions.json"));
            try
            {
                var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
                List<SessionInfo> List() => store.Apply(new List<SessionInfo>
                {
                    new SessionInfo { Id = "fresh", Title = "a", LastWriteUtc = now.AddDays(-2) },
                    new SessionInfo { Id = "old", Title = "b", LastWriteUtc = now.AddDays(-20) },
                }, autoArchiveDays: 14, nowUtc: now);

                var l = List();
                True(!l[0].Archived && l[1].Archived && l[1].AutoArchived, "20 days idle is auto-archived");
                True(!store.Apply(new List<SessionInfo> { new SessionInfo { Id = "old", LastWriteUtc = now.AddDays(-20) } }, 0, now)[0].Archived, "0 days = off");

                store.SetArchived("old", false); // unarchiving restarts the clock (UnarchivedUtc = real now, after 'now - 20 days')
                True(!List()[1].Archived, "unarchived stays visible");

                store.SetGroup("fresh", " Work ");
                store.SetGroup("old", "Work");
                store.SetGroup("x", "Personal");
                Eq("Personal|Work", string.Join("|", store.Groups()));
                Eq("Work", List()[0].Group);
                store.RenameGroup("work", "Client");
                Eq("Client", List()[1].Group, "rename is case-insensitive");
                store.RenameGroup("Personal", "");
                Eq(null, store.Get("x"), "deleting the group forgets a session with nothing else stored");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Test_RunningSessions_Registry()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "100.json"), "{\"pid\":100,\"sessionId\":\"s1\",\"cwd\":\"C:\\\\src\",\"status\":\"busy\",\"entrypoint\":\"claude-vscode\"}");
                File.WriteAllText(Path.Combine(dir, "200.json"), "{\"pid\":200,\"sessionId\":\"s2\",\"status\":\"idle\",\"entrypoint\":\"cli\"}");
                File.WriteAllText(Path.Combine(dir, "300.json"), "{ broken");
                File.WriteAllText(Path.Combine(dir, "100.abc.key"), "{}");
                var running = RunningSessions.Read(dir, pid => pid == 100);
                Eq(1, running.Count, "dead process and broken file skipped");
                Eq("s1", running[0].SessionId);
                Eq("VS Code", running[0].Where);
                Eq("working", running[0].StatusText);
                Eq("a terminal", new RunningSession { Entrypoint = "cli" }.Where);
                Eq("needs input", new RunningSession { Status = "waiting" }.StatusText);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Test_PlanComments_FromEdits()
        {
            string plan = "# Plan\n\n## Step 1\nAdd the API.\n\n## Step 2\nWrite tests.\nUpdate docs.";
            string edited = "# Plan\nKeep it small please.\n\n## Step 1\n  Add the API.\nUse minimal APIs, not controllers.\n\n## Step 2\nWrite tests.";
            var c = PlanComments.Extract(plan, edited);
            Eq(3, c.Count, string.Join(" | ", c.Select(x => x.After + ">" + (x.Text ?? "-" + x.Removed))));
            Eq("# Plan", c[0].After);
            Eq("Keep it small please.", c[0].Text);
            Eq("Add the API.", c[1].After, "re-indenting a line is not a change");
            Eq("Use minimal APIs, not controllers.", c[1].Text);
            Eq("Update docs.", c[2].Removed);
            Eq("Comments on the plan (from the plan document):\n- After \"# Plan\": Keep it small please.\n- After \"Add the API.\": Use minimal APIs, not controllers.\n- Remove: \"Update docs.\"",
               PlanComments.ToFeedback(c));
            Eq(0, PlanComments.Extract(plan, plan.Replace("\n", "\r\n")).Count, "line endings alone are no change");
            Eq(null, PlanComments.ToFeedback(new List<PlanComments.Comment>()));
        }

        private static void Test_Bookmark_Title()
        {
            Eq("Fix the login bug", Bookmark.MakeTitle("\n  Fix the login bug\nmore", "answer"));
            Eq("Summary", Bookmark.MakeTitle("", "## Summary\nText"));
            Eq("Bookmark", Bookmark.MakeTitle(null, null));
            Eq(80, Bookmark.MakeTitle(new string('x', 200), null).Length);
        }

        private static void Test_Bookmark_StoreAddListRemove()
        {
            string dir = Path.Combine(Path.GetTempPath(), "claudevs-bm-" + Guid.NewGuid().ToString("N"));
            var store = new BookmarkStore(Path.Combine(dir, "bookmarks.json"));
            try
            {
                Eq(0, store.List().Count, "empty when missing");
                var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
                var a = Bookmark.Create(@"C:\src\Shop", "s1", "How does checkout work?", "It uses **Stripe**.", t0);
                var b = Bookmark.Create(@"C:\src\Shop\", "s1", "Rename Foo", "Done.", t0.AddMinutes(5));
                var c = Bookmark.Create(@"C:\src\Other", "s2", "Hi", "Hello", t0);
                store.Add(a); store.Add(b); store.Add(c);
                var shop = store.List(@"c:\src\shop");
                Eq(2, shop.Count, "per solution, case and trailing slash ignored");
                Eq(b.Id, shop[0].Id, "newest first");
                Eq(3, store.List().Count);
                True(shop[1].Matches("stripe checkout"), "matches answer and prompt words");
                True(!shop[1].Matches("paypal"), "no match");
                True(store.Remove(a.Id), "removed");
                True(!store.Remove(a.Id), "second remove is a no-op");
                Eq(1, store.List(@"C:\src\Shop").Count);

                File.WriteAllText(store.Path, "{ not json");
                Eq(0, store.List().Count, "damaged file reads as empty");
                store.Add(a);
                Eq(1, store.List().Count, "and is replaced on the next save");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Test_Bookmark_LongAnswerIsCut()
        {
            var b = Bookmark.Create("d", null, "p", new string('a', Bookmark.MaxAnswerChars + 10), DateTime.UtcNow);
            True(b.Answer.Length < Bookmark.MaxAnswerChars + 20 && b.Answer.EndsWith("(cut)"), "cut");
        }
    }
}
