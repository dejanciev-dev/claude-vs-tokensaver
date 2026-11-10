using System;
using System.Collections.Generic;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// Spots Claude reading a whole file again that it already has: the same file, read whole both times, the same
    /// content (so not after your own edits), no Edit / Write of it in between, and no compaction (which drops the
    /// earlier copy from the context). Only the main agent counts: a subagent has its own context.
    /// </summary>
    public sealed class ReadTracker
    {
        private readonly Dictionary<string, string> _pendingReads = new Dictionary<string, string>(StringComparer.Ordinal); // tool_use_id → path
        private readonly Dictionary<string, int> _contentOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // path → hash of what was read

        /// <summary>Tokens spent on repeated reads in this conversation.</summary>
        public long RepeatedTokens { get; private set; }

        /// <summary>Feeds one event; returns the repeated read's file and size when this result repeats one, else null.</summary>
        public (string Path, int Tokens)? Observe(ClaudeEvent ev)
        {
            switch (ev.Kind)
            {
                case ClaudeEventKind.ToolUse when !ev.IsSubagent:
                {
                    string path = (string)ev.ToolInput?["file_path"] ?? (string)ev.ToolInput?["notebook_path"];
                    if (string.IsNullOrEmpty(path)) return null;
                    if (ev.ToolName == "Read")
                    {
                        // Part of a file (offset / limit) is a different read: not compared.
                        bool whole = ev.ToolInput["offset"] == null && ev.ToolInput["limit"] == null && ev.ToolInput["pages"] == null;
                        if (whole && ev.ToolUseId != null) _pendingReads[ev.ToolUseId] = path;
                    }
                    else if (ev.ToolName == "Edit" || ev.ToolName == "MultiEdit" || ev.ToolName == "Write" || ev.ToolName == "NotebookEdit")
                        _contentOf.Remove(path); // changed: the next read is new
                    return null;
                }
                case ClaudeEventKind.ToolResult when !ev.IsSubagent:
                {
                    if (ev.ToolUseId == null || !_pendingReads.TryGetValue(ev.ToolUseId, out var path)) return null;
                    _pendingReads.Remove(ev.ToolUseId);
                    if (ev.IsError || string.IsNullOrEmpty(ev.ResultText)) return null;
                    int hash = StringComparer.Ordinal.GetHashCode(ev.ResultText);
                    bool repeat = _contentOf.TryGetValue(path, out var before) && before == hash;
                    _contentOf[path] = hash;
                    if (!repeat) return null;
                    int tokens = TokenEstimate.FromChars(ev.ResultChars);
                    RepeatedTokens += tokens;
                    return (path, tokens);
                }
                case ClaudeEventKind.Compacted:
                    _contentOf.Clear(); // the earlier copies left the context
                    return null;
                default:
                    return null;
            }
        }

        public void Reset() { _pendingReads.Clear(); _contentOf.Clear(); RepeatedTokens = 0; }

        /// <summary>The tip for a repeated read, worth giving from about 2k tokens; null for small files.</summary>
        public static string Tip(string path, int tokens)
        {
            if (tokens < 2000) return null;
            string name = System.IO.Path.GetFileName(path);
            return $"Claude read {name} again (~{TokenMeter.Format(tokens)} tokens), unchanged since it last read it, so the same text now sits in the context twice. "
                + $"Telling it \"you already have {name}\" avoids paying for it again.";
        }
    }
}
