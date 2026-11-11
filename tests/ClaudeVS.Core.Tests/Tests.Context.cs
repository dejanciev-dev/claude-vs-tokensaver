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
        private static void Test_StoppedEdit_FileSaysWhetherWritten()
        {
            // The result text Claude Code 2.1.322 sends for an Edit that Stop ended while it ran
            const string stopped = "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). STOP what you are doing and wait for the user to tell you how to proceed. Correction to the text above: this call was stopped while running, not refused before it ran. It may have run in part or in full, and its changes may have been written.";
            True(StoppedEdit.IsStopped(true, stopped), "stopped");
            True(!StoppedEdit.IsStopped(false, stopped), "not an error: not stopped");
            True(!StoppedEdit.IsStopped(true, "The user doesn't want to proceed with this tool use. The tool use was rejected"), "a plain Deny is not a stop");

            var edit = JObject.Parse("{\"old_string\":\"beta\\ngamma\\ndelta\",\"new_string\":\"BETA\\ngamma\\nDELTA\"}");
            const string before = "alpha\nbeta\ngamma\ndelta\n", after = "alpha\nBETA\ngamma\nDELTA\n";
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", edit, before, after), "written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Edit", edit, before, before), "not written");
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", edit, after, after), "our copy was taken after the write: still written");
            Eq(StoppedEdit.State.Unknown, StoppedEdit.Check("Edit", edit, before, "something else\n"), "changed by something else: unknown");
            Eq(StoppedEdit.State.Unknown, StoppedEdit.Check("Edit", edit, before, null), "file gone");
            var grow = JObject.Parse("{\"old_string\":\"x = 1;\",\"new_string\":\"x = 1;\\ny = 2;\"}"); // new text holds the old
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", grow, "a\nx = 1;\nb\n", "a\nx = 1;\ny = 2;\nb\n"), "insertion written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Edit", grow, "a\nx = 1;\nb\n", "a\nx = 1;\nb\n"), "insertion not written");
            var del = JObject.Parse("{\"old_string\":\"// old\\n\",\"new_string\":\"\"}");
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Edit", del, "a\n// old\nb\n", "a\nb\n"), "deletion written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Edit", del, "a\n// old\nb\n", "a\n// old\nb\n"), "deletion not written");

            var write = JObject.Parse("{\"content\":\"new file\\n\"}");
            Eq(StoppedEdit.State.Written, StoppedEdit.Check("Write", write, null, "new file\n"), "write written");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Write", write, null, null), "new file never created");
            Eq(StoppedEdit.State.NotWritten, StoppedEdit.Check("Write", write, "old\n", "old\n"), "overwrite not written");

            Eq(null, StoppedEdit.Note("Edit", "a.cs", StoppedEdit.State.Unknown), "unknown: say nothing");
            string wn = StoppedEdit.Note("Edit", "notes.txt", StoppedEdit.State.Written, edit);
            True(wn.Contains("Visual Studio read notes.txt after the stop") && wn.Contains("applied in full") && wn.Contains(@"""BETA\ngamma\nDELTA"" where ""beta\ngamma\ndelta"" was"), wn);
            True(StoppedEdit.Note("Write", "b.txt", StoppedEdit.State.NotWritten).Contains("was not written; b.txt is unchanged"), "not-written note");
            True(StoppedEdit.Note("Edit", "notes.txt", StoppedEdit.State.NotWritten, edit).Contains(@"still has ""beta\ngamma\ndelta"""), "edit not applied");

            // The session queues it for the next prompt; a reviewed-edit note for the same call wins
            var s = new ClaudeSession(new ClaudeLaunchOptions());
            s.AddNextPromptNote("toolu_1", "About the stopped Edit of a.cs: its change is in the file; it was written before the stop.");
            string next = s.TakeStrandedNotes();
            True(ClaudeSession.IsEditorNote(next) && next.Contains("a.cs"), next);
            Eq(null, s.TakeStrandedNotes(), "given once");
            s.SetToolResultNote("toolu_2", "Rejected: line 4.", "b.cs", () => true);
            s.AddNextPromptNote("toolu_2", "About the stopped Edit of b.cs: its change is in the file.");
            s.MarkNotesStranded();
            string reviewed = s.TakeStrandedNotes();
            True(reviewed.Contains("Rejected: line 4.") && !reviewed.Contains("its change is in the file"), reviewed);

            // A reviewed edit that Stop caught before it was written: the reviewed note does not apply, the file check
            // says "not applied", and that is what Claude hears (before, nothing)
            s.SetToolResultNote("toolu_3", "Rejected: line 9.", "c.cs", () => false);
            s.AddNextPromptNote("toolu_3", StoppedEdit.Note("Edit", "c.cs", StoppedEdit.State.NotWritten, JObject.Parse("{\"old_string\":\"a\",\"new_string\":\"b\"}")));
            s.MarkNotesStranded();
            string notWritten = s.TakeStrandedNotes();
            True(notWritten != null && notWritten.Contains("was not applied: c.cs still has \"a\"") && !notWritten.Contains("Rejected: line 9."), notWritten);
            Eq(null, s.TakeStrandedNotes(), "given once");

            // Not written and the file says nothing either (Unknown): still nothing, rather than a guess
            s.SetToolResultNote("toolu_4", "Rejected: line 2.", "d.cs", () => false);
            s.AddNextPromptNote("toolu_4", StoppedEdit.Note("Edit", "d.cs", StoppedEdit.State.Unknown));
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "unknown: nothing");
        }

        private static void Test_EditNote_AfterStop_GoesWithNextPrompt()
        {
            var s = new ClaudeSession(new ClaudeLaunchOptions { EditNotesHook = true });
            bool written = true;
            s.SetToolResultNote("toolu_1", "Rejected: line 4.", "notes.txt", () => written);
            Eq(null, s.TakeStrandedNotes(), "the turn is still running: the hook may come");

            s.MarkNotesStranded(); // Stop: the turn ended, no hook ran
            string note = s.TakeStrandedNotes();
            True(note != null && ClaudeSession.IsEditorNote(note) && note.Contains("notes.txt") && note.Contains("Rejected: line 4."), note);
            Eq(null, s.TakeStrandedNotes(), "given once");

            written = false; // stopped before the change was written: nothing to say
            s.SetToolResultNote("toolu_2", "Rejected: line 9.", "a.cs", () => written);
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "not written");

            s.SetToolResultNote("toolu_3", "Rejected: line 2.", "b.cs", () => throw new System.IO.IOException("locked"));
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "a check that fails counts as not written");

            s.SetToolResultNote("toolu_4", "Rejected.", "c.cs"); // no check: dropped, never guessed
            s.MarkNotesStranded();
            Eq(null, s.TakeStrandedNotes(), "without a check");

            // A block of its own after any images; history and titles leave it out
            var msg = JObject.Parse(ClaudeSession.BuildUserMessage("did it work?", null, null, ClaudeSession.EditorNotePrefix + " x"));
            var blocks = (JArray)msg.SelectToken("message.content");
            Eq(2, blocks.Count, "note + prompt");
            True(ClaudeSession.IsEditorNote((string)blocks[0]["text"]) && (string)blocks[1]["text"] == "did it work?", msg.ToString());
            Eq(1, ((JArray)JObject.Parse(ClaudeSession.BuildUserMessage("p", null)).SelectToken("message.content")).Count, "no note: unchanged");

            // The session file keeps the user message as sent: resume and the sessions list show only the prompt
            string file = Path.Combine(Path.GetTempPath(), "cvs-note-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                var line = JObject.Parse(ClaudeSession.BuildUserMessage("did it work?", null, null, ClaudeSession.EditorNotePrefix + " About the stopped edit…"));
                line["uuid"] = "u1"; line["sessionId"] = "s1"; line["timestamp"] = "2026-11-10T13:00:00Z";
                File.WriteAllText(file, line.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                Eq("did it work?", SessionHistory.ReadTitle(file), "title");
                var prompt = SessionTranscript.Read(file).Items.FirstOrDefault(i => i.Kind == TranscriptItemKind.User);
                Eq("did it work?", prompt?.Text, "redrawn prompt");
            }
            finally { File.Delete(file); }
        }

        private static void Test_EditReview_Edit_PartialApply()
        {
            string file = "header\nalpha\nbeta\ngamma\ndelta\n";
            var input = JObject.Parse("{\"file_path\":\"C:/x/notes.txt\",\"old_string\":\"beta\\ngamma\\ndelta\",\"new_string\":\"BETA\\ngamma\\nDELTA\",\"replace_all\":false}");
            var r = EditReview.Create("Edit", input, file);
            Eq(2, r.Hunks.Count);
            Eq("line 3 (−1 +1)", r.Hunks[0].Label, "file line of old_string + offset");
            Eq("line 5 (−1 +1)", r.Hunks[1].Label);
            Eq("BETA\ngamma\nDELTA", (string)r.BuildInput()["new_string"], "all accepted = unchanged");
            Eq(null, r.NoteForClaude());

            r.Hunks[1].Accepted = false;
            var built = r.BuildInput();
            Eq("BETA\ngamma\ndelta", (string)built["new_string"]);
            Eq("beta\ngamma\ndelta", (string)built["old_string"]);
            Eq(false, (bool)built["replace_all"], "other fields kept");
            string note = r.NoteForClaude();
            True(note.StartsWith("The user reviewed this edit and applied only 1 of 2 changes."), note);
            True(note.Contains("- line 5 (−1 +1): kept\n    delta\n  instead of\n    DELTA\n"), note);
            Eq("BETA\ngamma\nDELTA", (string)input["new_string"], "original input not modified");
        }

        private static void Test_EditReview_Write_InsertDeleteAndCrlf()
        {
            string file = "a\r\nb\r\nc\r\nd\r\n";
            var input = JObject.Parse("{\"file_path\":\"f.txt\",\"content\":\"a\\nNEW\\nb\\nc\\n\"}"); // inserts after a, deletes d
            var r = EditReview.Create("Write", input, file);
            Eq(2, r.Hunks.Count, "line endings alone are not changes");
            Eq("after line 1 (+1)", r.Hunks[0].Label);
            Eq("line 4 (−1)", r.Hunks[1].Label);
            Eq("a\nNEW\nb\nc\n", (string)r.BuildInput()["content"], "all accepted = exactly Claude's content");
            r.Hunks[1].Accepted = false; // keep d, in the proposal's line endings
            Eq("a\nNEW\nb\nc\nd\n", (string)r.BuildInput()["content"]);
            r.Hunks[1].Accepted = true; r.Hunks[0].Accepted = false;
            Eq("a\nb\nc\n", (string)r.BuildInput()["content"]);

            var crlf = EditReview.Create("Write", JObject.Parse("{\"content\":\"A\\r\\nb\\r\\nC\\r\\n\"}"), "a\nb\nc\n");
            crlf.Hunks[1].Accepted = false;
            Eq("A\r\nb\r\nc\r\n", (string)crlf.BuildInput()["content"], "kept lines take CRLF from the proposal");
            True(r.NoteForClaude().Contains("kept\n    (nothing)\n  instead of\n    NEW\n"), r.NoteForClaude());

            Eq(null, EditReview.Create("Write", JObject.Parse("{\"content\":\"same\"}"), "same"), "no change, no review");
            Eq(null, EditReview.Create("Bash", JObject.Parse("{\"command\":\"ls\"}"), null), "not a file tool");
            Eq(1, EditReview.Create("Write", JObject.Parse("{\"content\":\"x\\ny\"}"), null).Hunks.Count, "new file = one change");
        }

        private static void Test_EditReview_ProposedFile()
        {
            var input = JObject.Parse("{\"old_string\":\"b\",\"new_string\":\"B\"}");
            Eq("aBcb", EditReview.ProposedFile("Edit", input, "abcb"));
            input["replace_all"] = true;
            Eq("aBcB", EditReview.ProposedFile("Edit", input, "abcb"));
            Eq(null, EditReview.ProposedFile("Edit", input, "xyz"), "old_string not found");
        }

        private static void Test_Context_ErrorsAndFileReference()
        {
            var errors = new List<BuildError>();
            for (int i = 1; i <= 25; i++) errors.Add(new BuildError { FilePath = @"C:\sln\src\A.cs", Line = i, Code = "CS0103", Message = $"The name 'x{i}' does not exist" });
            errors.Add(new BuildError { FilePath = @"C:\sln\src\A.cs", Line = 1, Code = "CS0103", Message = "The name 'x1' does not exist" }); // duplicate
            errors.Add(new BuildError { Message = "multi\nline" });

            var b = new PromptContextBuilder(new ContextSettings { MaxErrors = 20 })
                .Build("fix these", @"C:\sln", extraFiles: new[] { @"C:\sln\src\B.cs" }, errors: errors);
            Eq(20, b.AttachedErrors, "capped");
            True(b.Text.Contains("Build errors (first 20 of 26):"), b.Text);
            True(b.Text.Contains("- `src/A.cs:L3` CS0103: The name 'x3' does not exist"), b.Text);
            True(b.Text.Contains("Relevant files: `src/B.cs`"), b.Text);
            True(b.Text.TrimEnd().EndsWith("fix these"), "prompt last");

            Eq("", PromptContextBuilder.FormatErrors(null, null, 20, out int n), "no errors");
            Eq(0, n, "count");
            True(PromptContextBuilder.FormatErrors(new[] { new BuildError { Message = "multi\nline" } }, null, 20, out _).Contains("- multi line\n"), "one line per error");
        }

        private static void Test_InlineDiff_Edit()
        {
            var input = JObject.Parse("{\"file_path\":\"C:\\\\work\\\\a.cs\",\"old_string\":\"a\\nb\\nc\\nd\",\"new_string\":\"a\\nB\\nc\\nd\\ne\"}");
            var d = InlineDiff.FromToolUse("Edit", input);
            Eq("C:\\work\\a.cs", d.FilePath, "path");
            Eq(" a|-b|+B| c| d|+e", string.Join("|", d.Lines), "removed before added, context kept");
            Eq(2, d.Added, "added");
            Eq(1, d.Removed, "removed");
            Eq(null, InlineDiff.FromToolUse("Read", input), "Read changes nothing");
        }

        private static void Test_InlineDiff_LineNumbers()
        {
            var input = JObject.Parse("{\"file_path\":\"a.cs\",\"old_string\":\"b\\nc\",\"new_string\":\"b\\nC\\nc2\"}");
            var d = InlineDiff.FromToolUse("Edit", input, "a\nb\nc\nd\n");
            Eq("2: b|3:-c|3:+C|4:+c2", string.Join("|", d.Lines.Select(l => l.Number + ":" + l)), "numbers from where old_string starts");
            var unknown = InlineDiff.FromToolUse("Edit", input, null);
            True(unknown.Lines.All(l => l.Number == null), "no file content: no numbers");
            var fresh = InlineDiff.FromToolUse("Write", JObject.Parse("{\"file_path\":\"n.txt\",\"content\":\"x\\ny\"}"));
            Eq("1,2", string.Join(",", fresh.Lines.Select(l => l.Number)), "a new file is numbered from 1");
        }

        private static void Test_TurnChanges()
        {
            var files = new Dictionary<string, string> { [@"C:\r\a.cs"] = "x\ny\nz\n", [@"C:\r\new.cs"] = "1\n2\n", [@"C:\r\same.cs"] = "s\n" };
            var t = new TurnChanges();
            t.Record(@"C:\r\a.cs", "x\nz\n");
            t.Record(@"C:\R\A.cs", "ignored: the first copy counts");
            t.Record(@"C:\r\new.cs", "");
            t.Record(@"C:\r\same.cs", "s\r\n");
            t.Record(@"C:\r\gone.cs", "old\n");
            var list = t.Collect(p => files.TryGetValue(p, out var c) ? c : "", p => files.ContainsKey(p));
            Eq(@"C:\r\a.cs|C:\r\new.cs|C:\r\gone.cs", string.Join("|", list.Select(f => f.Path)), "in order, unchanged left out");
            True(list[0].Added == 1 && list[0].Removed == 0 && !list[0].IsNew, "changed file counts");
            True(list[1].IsNew && list[1].Added == 2, "new file");
            True(list[2].IsDeleted, "deleted file");
            Eq(@"C:\r\src\b.cs", TurnChanges.FullPath("src/b.cs", @"C:\r"), "relative to the solution, OS separators");
            Eq(@"C:\x\y.cs", TurnChanges.FullPath("C:/x/y.cs", @"C:\r"), "a heredoc's forward-slash path");
        }

        private static void Test_InlineDiff_Write()
        {
            var input = JObject.Parse("{\"file_path\":\"x.txt\",\"content\":\"1\\n2\\n3\\n4\\n5\\n6\\n7\\n8\\nnine\\n10\\n\"}");
            var fresh = InlineDiff.FromToolUse("Write", input);
            True(fresh.IsNewFile && fresh.Added == 10 && fresh.Removed == 0, "new file: all added");
            var changed = InlineDiff.FromToolUse("Write", input, "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
            Eq(" 7| 8|-9|+nine| 10", string.Join("|", changed.Lines), "only the change with 2 context lines; the start is dropped");
        }

        private static void Test_InlineDiff_Heredoc()
        {
            string cmd = "cd \"C:/Users/me/repo\" && cat > src/Samples/WindowSamples.cs <<'EOF'\nnamespace X;\n\npublic static class W { }\nEOF\ncat > tests/T.cs <<'EOF'\nclass T { }\nEOF\ndotnet test";
            var d = InlineDiff.FromToolUse("Bash", new JObject { ["command"] = cmd });
            Eq("C:/Users/me/repo/src/Samples/WindowSamples.cs", d.FilePath, "first file, relative to the cd");
            Eq(3, d.Added, "the heredoc body only");
            True(!d.IsAppend, "replace");
            var tagFirst = InlineDiff.FromHeredoc("cat <<EOF >> \"/tmp/log.txt\"\nline\nEOF");
            Eq("/tmp/log.txt", tagFirst.FilePath, "tag before the redirect, quoted path");
            True(tagFirst.IsAppend && tagFirst.Added == 1, "append");
            Eq(null, InlineDiff.FromHeredoc("git commit -F - <<'EOF'\nmsg\nEOF"), "a heredoc that is not written to a file");
        }

        private static void Test_EditDiff_ReconstructBefore()
        {
            var edit = FileChange.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"C:/a.cs\",\"old_string\":\"int x = 1;\",\"new_string\":\"int x = 2;\"}"));
            Eq("C:/a.cs", edit.FilePath, "path");
            Eq("a\nint x = 1;\nb", edit.ReconstructBefore("a\nint x = 2;\nb"), "single edit undone");
            Eq(null, edit.ReconstructBefore("a\nint x = 3;\nb"), "file changed since: null");
            Eq(null, edit.ReconstructBefore("int x = 2; int x = 2;"), "ambiguous: null");

            var multi = FileChange.FromToolUse("MultiEdit", JObject.Parse(
                "{\"file_path\":\"b.cs\",\"edits\":[{\"old_string\":\"foo\",\"new_string\":\"bar\",\"replace_all\":true},{\"old_string\":\"bar()\",\"new_string\":\"baz()\"}]}"));
            // before: "foo(); foo; x" → all foo→bar: "bar(); bar; x" → bar()→baz(): "baz(); bar; x"
            Eq("foo(); foo; x", multi.ReconstructBefore("baz(); bar; x"), "multi edit undone in reverse order");

            var lf = FileChange.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"e.cs\",\"old_string\":\"a\\nb\",\"new_string\":\"a\\nc\"}"));
            Eq("x\r\na\r\nb\r\ny", lf.ReconstructBefore("x\r\na\r\nc\r\ny"), "LF edit text on a CRLF file");

            Eq(true, FileChange.FromToolUse("Write", JObject.Parse("{\"file_path\":\"c.cs\",\"content\":\"x\"}")).IsWrite, "write");
            Eq(null, FileChange.FromToolUse("Write", JObject.Parse("{\"file_path\":\"c.cs\"}")).ReconstructBefore("x"), "write can't be rebuilt");
            Eq(null, FileChange.FromToolUse("Read", JObject.Parse("{\"file_path\":\"c.cs\"}")), "read is not a change");
            Eq(null, FileChange.FromToolUse("Edit", JObject.Parse("{\"file_path\":\"d.cs\",\"old_string\":\"gone\",\"new_string\":\"\"}")).ReconstructBefore("abc"), "deletion: null");
        }

        private static void Test_Context_SmallSelectionInlinedAndDedented()
        {
            var b = new PromptContextBuilder();
            var r = b.Build("Explain", @"C:\repo", new EditorSelection { FilePath = @"C:\repo\src\A.cs", StartLine = 10, EndLine = 11, Text = "        var x = 1;\n        return x;", Language = "csharp" });
            True(r.Text.Contains("`src/A.cs:L10-11`") || r.Text.Contains("A.cs:L10-11"), r.Text);
            True(r.Text.Contains("var x = 1;\nreturn x;"), "dedent: " + r.Text);
            Eq(2, r.InlinedLines);
        }

        private static void Test_Context_LargeSelectionReferenced()
        {
            var b = new PromptContextBuilder(new ContextSettings { InlineMaxLines = 5 });
            string text = string.Join("\n", Enumerable.Range(1, 50).Select(i => "line" + i));
            var r = b.Build("Review", "/repo", new EditorSelection { FilePath = "/repo/B.cs", StartLine = 1, EndLine = 50, Text = text });
            True(r.Text.Contains("not inlined") && !r.Text.Contains("line25"), r.Text);
            Eq(0, r.InlinedLines);
        }

        private static string TempFile(params string[] lines)
        {
            string f = Path.Combine(Path.GetTempPath(), "claudevs-sess-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(f, string.Join("\n", lines) + "\n");
            return f;
        }

        private static void Test_Setup_MergeIsIdempotentAndKeepsUserSettings()
        {
            var s = JObject.Parse("{\"permissions\":{\"deny\":[\"Read(./secrets/**)\"],\"allow\":[\"Bash(git status)\"]},\"model\":\"opus\"}");
            TokenSaverSetup.MergeSettings(s);
            string once = s.ToString();
            TokenSaverSetup.MergeSettings(s);
            Eq(once, s.ToString(), "idempotent");
            Eq("opus", (string)s["model"]);
            True(((JArray)s["permissions"]["deny"]).Any(t => (string)t == "Read(./secrets/**)"), "kept user deny");
            True(((JArray)s["permissions"]["deny"]).Any(t => (string)t == "Read(./**/bin/**)"), "added bin deny");
            Eq(1, ((JArray)s["hooks"]["PreToolUse"]).Count);
            Eq("Bash|PowerShell", (string)s["hooks"]["PreToolUse"][0]["matcher"], "both shells");
            Eq("15000", (string)s["env"]["BASH_MAX_OUTPUT_LENGTH"]);
            Eq(15000, (int)s[TokenSaverSetup.OutputMaxCharsKey]);
        }

        private static void Test_Setup_UpgradesBashOnlyHookAndKeepsUserCap()
        {
            // As installed by an earlier version: Bash-only matcher; the user picked their own output cap.
            var s = JObject.Parse("{\"bashOutputMaxChars\":8000,\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"powershell -File \\\"$CLAUDE_PROJECT_DIR/.claude/hooks/trim-output.ps1\\\"\"}]},{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"other.sh\"}]}]}}");
            TokenSaverSetup.MergeSettings(s);
            var pre = (JArray)s["hooks"]["PreToolUse"];
            Eq(2, pre.Count, "no duplicate hook added");
            Eq("Bash|PowerShell", (string)pre[0]["matcher"], "ours upgraded");
            Eq("Bash", (string)pre[1]["matcher"], "someone else's hook left alone");
            Eq(8000, (int)s["bashOutputMaxChars"], "user's cap kept");
            True(TokenSaverSetup.HookScript(false).Contains("$evt.tool_name -ne 'PowerShell'"), "script accepts PowerShell");
        }

        private static void Test_Scanner_And_ClaudeMd()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Api"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "Api.Tests"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Api", "bin"));
            Directory.CreateDirectory(Path.Combine(root, "web"));
            File.WriteAllText(Path.Combine(root, "Shop.sln"), "");
            File.WriteAllText(Path.Combine(root, "src", "Api", "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"8.0.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "src", "Api", "bin", "Junk.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "tests", "Api.Tests", "Api.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "web", "package.json"), "{\"name\":\"shop-web\",\"dependencies\":{\"@angular/core\":\"^19.0.0\",\"rxjs\":\"~7.8.0\"}}");
            try
            {
                var info = SolutionScanner.Scan(root);
                Eq(3, info.Projects.Count, "bin/ skipped");
                True(info.Projects.Single(p => p.Name == "Api").IsWeb, "web sdk");
                True(info.Projects.Single(p => p.Name == "Api.Tests").IsTest, "test project");
                string md = ClaudeMdGenerator.Generate(info);
                True(md.Contains("dotnet test Shop.sln"), md);
                True(md.Contains("Angular 19.0.0"), md);
                True(ClaudeMdGenerator.EstimateTokens(md) < 600, "CLAUDE.md stays small: " + ClaudeMdGenerator.EstimateTokens(md));

                var res = TokenSaverSetup.Apply(root);
                True(File.Exists(Path.Combine(root, ".claude", "hooks", TokenSaverSetup.HookFileName)), "hook written");
                var res2 = TokenSaverSetup.Apply(root);
                Eq(0, res2.Written.Count, "second apply writes nothing");
            }
            finally { Directory.Delete(root, true); }
        }

        // Gaps found by running the generator on real solutions (Phase 2.4).
        private static void Test_Scanner_LegacyFramework()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            void Write(string rel, string text) { string f = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text); }
            const string ns = "xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"";
            Write("Shop.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Shop.Web\", \"Shop.Web\\Shop.Web.csproj\", \"{1}\"\r\nEndProject\r\n"
                            + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Shop.Tests\", \"Shop.Tests\\Shop.Tests.csproj\", \"{2}\"\r\nEndProject\r\n"
                            + "Project(\"{F184B08F-C81C-45F6-A57F-5ABD9991F28F}\") = \"Reports\", \"Reports\\Reports.vbproj\", \"{3}\"\r\nEndProject\r\n");
            Write("Shop.Web/Shop.Web.csproj", $"<Project ToolsVersion=\"15.0\" {ns}><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><ProjectTypeGuids>{{349c5851-65df-11da-9384-00065b846f21}};{{fae04ec0-301f-11d3-bf4b-00c04f79efbc}}</ProjectTypeGuids></PropertyGroup></Project>");
            Write("Shop.Web/packages.config", "<packages><package id=\"EntityFramework\" version=\"6.4.4\" /><package id=\"log4net\" version=\"2.0.8\" /></packages>");
            Write("Shop.Tests/Shop.Tests.csproj", $"<Project {ns}><PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup><ItemGroup><Reference Include=\"nunit.framework, Version=3.13.0.0\" /></ItemGroup></Project>");
            Write("Reports/Reports.vbproj", $"<Project {ns}><PropertyGroup><TargetFrameworkVersion>v4.5.2</TargetFrameworkVersion></PropertyGroup></Project>");
            try
            {
                var info = SolutionScanner.Scan(root);
                var web = info.Projects.Single(p => p.Name == "Shop.Web");
                True(web.IsLegacy && web.IsWeb && web.UsesPackagesConfig, "legacy web project");
                Eq("net472", web.Frameworks, "framework moniker");
                True(web.KeyPackages.Contains("EntityFramework") && web.KeyPackages.Contains("log4net"), "packages.config read");
                True(info.Projects.Single(p => p.Name == "Shop.Tests").IsTest, "test project from an assembly reference");
                True(info.Projects.Any(p => p.Name == "Reports"), ".vbproj found");

                string md = ClaudeMdGenerator.Generate(info);
                True(md.Contains("`msbuild Shop.sln -restore -p:RestorePackagesConfig=true -v:q -nologo`"), md);
                True(!md.Contains("Build: `dotnet build"), "no dotnet build for .NET Framework: " + md);
                True(md.Contains("`vstest.console.exe Shop.Tests/bin/Debug/Shop.Tests.dll`"), md);
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_Scanner_SolutionMembership_And_FrontEnds()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            void Write(string rel, string text) { string f = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text); }
            Write("App.slnx", "<Solution><Project Path=\"src/Api/Api.csproj\" /><Project Path=\"../Shared/Shared.csproj\" /></Solution>");
            Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
            Write("src/Api/package.json", "{\"name\":\"api-assets\",\"devDependencies\":{\"esbuild\":\"^0.25.0\"},\"scripts\":{\"build\":\"node build.js\"}}");
            Write("samples/Old/Old.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            Write("web/package.json", "{\"name\":\"app-web\",\"scripts\":{\"build\":\"vite build\",\"test\":\"vitest run\",\"lint\":\"eslint .\"},\"dependencies\":{\"react\":\"19.0.0\"},\"devDependencies\":{\"vitest\":\"3.0.0\"}}");
            Write("package.json", "{\"devDependencies\":{\"esbuild\":\"^0.25.0\"}}");
            try
            {
                var info = SolutionScanner.Scan(root);
                Eq("net10.0", info.Projects.Single(p => p.Name == "Api").Frameworks, "framework from Directory.Build.props");
                True(!info.Projects.Single(p => p.Name == "Old").InSolution, "project the solution doesn't list");
                Eq("../Shared/Shared.csproj", info.ExternalProjects.Single(), "external project");
                True(!info.Projects.Any(p => p.RelativePath == "package.json"), "tooling-only package.json dropped");
                Eq("React 19.0.0", info.Projects.Single(p => p.Name == "app-web").Frameworks, "React detected");

                string md = ClaudeMdGenerator.Generate(info);
                True(md.Contains("Not in App.slnx (not built by it): `samples/` (1)"), md);
                True(md.Contains("outside this folder: `../Shared/Shared.csproj`"), md);
                True(md.Contains("app-web: `cd web && npm run build` / `npm test`"), md);
                True(md.Contains("api-assets: `cd src/Api && npm run build`"), md);
                True(!md.Contains("` api-assets"), "package.json beside a .NET project is not listed as a second project: " + md);
                True(!md.Contains("Old"), "projects outside the solution are not listed one by one");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
