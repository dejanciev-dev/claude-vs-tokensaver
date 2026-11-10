using System;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// An Edit or Write that Stop ended while it ran. Claude Code (2.1.322) then tells Claude the call "was stopped while
    /// running … its changes may have been written", even when they were, or were not. The file says which; the chat
    /// passes that on with the next prompt (<see cref="Cli.ClaudeSession.AddNextPromptNote"/>).
    /// </summary>
    public static class StoppedEdit
    {
        /// <summary>In the error result of a call that Stop ended while it ran (seen with 2.1.322).</summary>
        public const string Marker = "stopped while running";

        public enum State { Unknown, Written, NotWritten }

        public static bool IsStopped(bool isError, string resultText) =>
            isError && resultText != null && resultText.IndexOf(Marker, StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Whether the call's change is in <paramref name="now"/> (the file after Stop; null when it does not exist).
        /// <paramref name="before"/> is the extension's copy from when the call arrived, which a fast write can beat, so
        /// an exact match only confirms; what decides is whether the old and new text are in the file.
        /// </summary>
        public static State Check(string toolName, JObject input, string before, string now)
        {
            if (input == null) return State.Unknown;
            if (toolName == "Write")
            {
                string content = (string)input["content"];
                if (content == null || now == null) return now == null && before == null ? State.NotWritten : State.Unknown;
                if (now == content) return State.Written;
                return before != null && now == before ? State.NotWritten : State.Unknown;
            }
            if (toolName != "Edit" || now == null) return State.Unknown;
            string oldS = (string)input["old_string"] ?? "", newS = (string)input["new_string"] ?? "";
            if (oldS.Length == 0 || oldS == newS) return State.Unknown;
            if (before != null && EditReview.ProposedFile(toolName, input, before) is string expected && now == expected) return State.Written;
            bool hasOld = now.IndexOf(oldS, StringComparison.Ordinal) >= 0;
            if (newS.Length == 0) return hasOld ? State.NotWritten : State.Written; // a deletion
            bool hasNew = now.IndexOf(newS, StringComparison.Ordinal) >= 0;
            bool newHoldsOld = newS.IndexOf(oldS, StringComparison.Ordinal) >= 0;
            if (hasNew && (!hasOld || newHoldsOld)) return State.Written;
            if (hasOld && !hasNew) return State.NotWritten;
            return State.Unknown;
        }

        /// <summary>
        /// What Claude is told with the next prompt; null when the file does not say. It names who checked and shows what
        /// the file holds now: a bare "it was written" was not believed in a live run (Haiku, 2.1.322).
        /// </summary>
        public static string Note(string toolName, string fileName, State state, JObject input = null)
        {
            if (state == State.Unknown) return null;
            string file = string.IsNullOrEmpty(fileName) ? "the file" : fileName;
            string tool = toolName ?? "edit";
            string checkedBy = $"Visual Studio read {file} after the stop, so this is the file's actual state: your {tool} call that was stopped while running";
            string newS = (string)input?["new_string"], oldS = (string)input?["old_string"];
            if (tool == "Write")
                return state == State.Written ? $"{checkedBy} was written in full; {file} holds the content you sent." : $"{checkedBy} was not written; {file} is unchanged.";
            if (state == State.Written)
                return $"{checkedBy} was applied in full" + (newS != null && oldS != null ? $": {file} now has {Quote(newS)} where {Quote(oldS)} was." : ".");
            return $"{checkedBy} was not applied" + (oldS != null ? $": {file} still has {Quote(oldS)}." : "; the file is unchanged.");
        }

        /// <summary>"…" around a short excerpt (line breaks as \n, at most 120 characters).</summary>
        private static string Quote(string s)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", "\\n");
            return "\"" + (s.Length <= 120 ? s : s.Substring(0, 117) + "…") + "\"";
        }
    }
}
