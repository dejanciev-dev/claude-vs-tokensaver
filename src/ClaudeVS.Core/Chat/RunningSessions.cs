using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Chat
{
    /// <summary>A Claude Code process that has a conversation open right now.</summary>
    public sealed class RunningSession
    {
        public int Pid { get; set; }
        public string SessionId { get; set; }
        public string Cwd { get; set; }
        /// <summary>"busy", "idle", or a waiting state, as the CLI reports it.</summary>
        public string Status { get; set; }
        /// <summary>"cli", "claude-vscode", "claude-desktop", "sdk-cli", …</summary>
        public string Entrypoint { get; set; }

        /// <summary>"a terminal", "VS Code", "Claude Desktop", "another app".</summary>
        public string Where
        {
            get
            {
                string e = (Entrypoint ?? "").ToLowerInvariant();
                if (e == "cli") return "a terminal";
                if (e.Contains("vscode")) return "VS Code";
                if (e.Contains("jetbrains")) return "JetBrains";
                if (e.Contains("desktop")) return "Claude Desktop";
                return "another app";
            }
        }

        /// <summary>"working", "needs input" or "idle", for the session list.</summary>
        public string StatusText
        {
            get
            {
                string s = (Status ?? "").ToLowerInvariant();
                if (s == "busy" || s == "working" || s == "running") return "working";
                if (s.Contains("wait") || s.Contains("input") || s.Contains("action")) return "needs input";
                return "idle";
            }
        }
    }

    /// <summary>
    /// Reads Claude Code's registry of running sessions, <c>~/.claude/sessions/&lt;pid&gt;.json</c> (2.1.317): one file
    /// per process with its session id, folder, entrypoint and status. Entries whose process is gone are ignored
    /// (a crash can leave them behind). Read-only.
    /// </summary>
    public static class RunningSessions
    {
        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

        public static List<RunningSession> Read(string folder = null, Func<int, bool> isAlive = null)
        {
            var result = new List<RunningSession>();
            folder = folder ?? DefaultFolder;
            isAlive = isAlive ?? ProcessAlive;
            string[] files;
            try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : new string[0]; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return result; }
            foreach (var f in files)
            {
                JObject o;
                try { o = JObject.Parse(File.ReadAllText(f)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { continue; }
                int pid = o["pid"]?.Type == JTokenType.Integer ? (int)o["pid"] : 0;
                string id = (string)o["sessionId"];
                if (pid <= 0 || string.IsNullOrEmpty(id) || !isAlive(pid)) continue;
                result.Add(new RunningSession { Pid = pid, SessionId = id, Cwd = (string)o["cwd"], Status = (string)o["status"], Entrypoint = (string)o["entrypoint"] });
            }
            return result;
        }

        private static bool ProcessAlive(int pid)
        {
            try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) return !p.HasExited; }
            catch (ArgumentException) { return false; }          // no such process
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; } // exists, but we may not query it
        }
    }
}
