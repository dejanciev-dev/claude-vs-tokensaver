using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    public enum AgentTaskStatus { Running, Completed, Failed, Stopped }

    /// <summary>
    /// A subagent or a background shell command, as the CLI reports it in <c>task_*</c> system messages.
    /// </summary>
    public sealed class AgentTask
    {
        public string TaskId { get; set; }
        /// <summary>The Agent (or Bash) tool call that started it; <c>background_tasks</c> takes this id.</summary>
        public string ToolUseId { get; set; }
        /// <summary>The agent that started this task (a subagent's own shell command or agent), or null.</summary>
        public string ParentTaskId { get; set; }
        public string Description { get; set; }
        /// <summary>"general-purpose", "Explore", … for agents; null for shell commands.</summary>
        public string SubagentType { get; set; }
        /// <summary>"local_agent", "local_bash", …</summary>
        public string TaskType { get; set; }
        public string Prompt { get; set; }
        public bool IsBackgrounded { get; set; }
        public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Running;
        /// <summary>What it is doing now, e.g. "Running Sleep for 15 seconds" (task_progress).</summary>
        public string Activity { get; set; }
        public string LastToolName { get; set; }
        public long TotalTokens { get; set; }
        public int ToolUses { get; set; }
        /// <summary>Final summary (task_notification): the agent's answer or the command's description.</summary>
        public string Summary { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        /// <summary>Duration from the CLI's usage, when it has sent one.</summary>
        public long? ReportedDurationMs { get; set; }

        public bool IsAgent => TaskType == "local_agent" || SubagentType != null;
        public bool IsRunning => Status == AgentTaskStatus.Running;
        /// <summary>A running foreground agent started by Claude itself: it can be moved to the background.</summary>
        public bool CanMoveToBackground => IsRunning && IsAgent && !IsBackgrounded && ParentTaskId == null && ToolUseId != null;

        public TimeSpan Elapsed(DateTime nowUtc)
        {
            if (EndedUtc.HasValue) return EndedUtc.Value - StartedUtc;
            var local = nowUtc - StartedUtc;
            return ReportedDurationMs.HasValue && TimeSpan.FromMilliseconds(ReportedDurationMs.Value) > local ? TimeSpan.FromMilliseconds(ReportedDurationMs.Value) : local;
        }
    }

    /// <summary>
    /// The session's subagents and background commands, built from the CLI's system messages (verified with Claude
    /// Code 2.1.318): <c>task_started</c>, <c>task_progress</c> (activity and usage), <c>task_updated</c> (a patch:
    /// <c>is_backgrounded</c>, <c>status</c>, <c>end_time</c>), <c>task_notification</c> (final status and summary)
    /// and <c>background_tasks_changed</c> (the running background tasks). Not thread-safe; use it on one thread.
    /// </summary>
    public sealed class AgentTaskList
    {
        private readonly List<AgentTask> _tasks = new List<AgentTask>();

        public IReadOnlyList<AgentTask> Tasks => _tasks;
        public int RunningCount => _tasks.Count(t => t.IsRunning);
        public bool IsEmpty => _tasks.Count == 0;

        public AgentTask Find(string taskId) => taskId == null ? null : _tasks.FirstOrDefault(t => t.TaskId == taskId);

        public static bool IsTaskMessage(JObject json)
        {
            if ((string)json?["type"] != "system") return false;
            switch ((string)json["subtype"])
            {
                case "task_started": case "task_progress": case "task_updated": case "task_notification": case "background_tasks_changed": return true;
                default: return false;
            }
        }

        /// <summary>Applies one system message; true when something changed.</summary>
        public bool Apply(JObject json, DateTime? nowUtc = null)
        {
            if (!IsTaskMessage(json)) return false;
            DateTime now = nowUtc ?? DateTime.UtcNow;
            string subtype = (string)json["subtype"];
            if (subtype == "background_tasks_changed") return ApplyBackgroundList(json["tasks"] as JArray, now);

            string id = (string)json["task_id"];
            if (string.IsNullOrEmpty(id)) return false;
            var t = Find(id);
            if (t == null)
            {
                t = new AgentTask { TaskId = id, StartedUtc = now };
                _tasks.Add(t);
            }
            Fill(t, json);
            switch (subtype)
            {
                case "task_started":
                    t.IsBackgrounded = json.Value<bool?>("is_backgrounded") ?? t.IsBackgrounded;
                    t.Prompt = (string)json["prompt"] ?? t.Prompt;
                    break;
                case "task_progress":
                    t.Activity = (string)json["description"] ?? t.Activity; // here the field holds the current step
                    t.LastToolName = (string)json["last_tool_name"] ?? t.LastToolName;
                    ApplyUsage(t, json["usage"] as JObject);
                    break;
                case "task_updated":
                    if (json["patch"] is JObject patch)
                    {
                        if (patch["is_backgrounded"] != null) t.IsBackgrounded = patch.Value<bool>("is_backgrounded");
                        if (patch["description"] != null) t.Description = (string)patch["description"];
                        if (patch["status"] != null) t.Status = ParseStatus((string)patch["status"]);
                        if (patch["end_time"] != null) t.EndedUtc = FromUnixMs(patch.Value<long?>("end_time")) ?? now;
                    }
                    break;
                case "task_notification":
                    t.Status = ParseStatus((string)json["status"]);
                    string summary = (string)json["summary"];
                    if (!string.IsNullOrWhiteSpace(summary) && summary != t.Description) t.Summary = summary.Trim();
                    ApplyUsage(t, json["usage"] as JObject);
                    break;
            }
            if (!t.IsRunning && t.EndedUtc == null) t.EndedUtc = now;
            if (!t.IsRunning) t.Activity = null;
            return true;
        }

        /// <summary>Adds running background tasks we have not seen start (e.g. after the window was reopened).</summary>
        private bool ApplyBackgroundList(JArray list, DateTime now)
        {
            bool changed = false;
            foreach (var o in list?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string id = (string)o["task_id"];
                if (string.IsNullOrEmpty(id) || Find(id) != null) continue;
                var t = new AgentTask { TaskId = id, StartedUtc = now, IsBackgrounded = true };
                Fill(t, o);
                _tasks.Add(t);
                changed = true;
            }
            return changed;
        }

        private static void Fill(AgentTask t, JObject json)
        {
            t.ToolUseId = (string)json["tool_use_id"] ?? t.ToolUseId;
            t.ParentTaskId = (string)json["parent_task_id"] ?? t.ParentTaskId;
            t.SubagentType = (string)json["subagent_type"] ?? t.SubagentType;
            t.TaskType = (string)json["task_type"] ?? t.TaskType;
            if ((string)json["subtype"] != "task_progress" && (string)json["subtype"] != "task_notification")
                t.Description = (string)json["description"] ?? t.Description;
        }

        private static void ApplyUsage(AgentTask t, JObject usage)
        {
            if (usage == null) return;
            t.TotalTokens = usage.Value<long?>("total_tokens") ?? t.TotalTokens;
            t.ToolUses = usage.Value<int?>("tool_uses") ?? t.ToolUses;
            t.ReportedDurationMs = usage.Value<long?>("duration_ms") ?? t.ReportedDurationMs;
        }

        public static AgentTaskStatus ParseStatus(string status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "completed": case "success": return AgentTaskStatus.Completed;
                case "failed": case "error": return AgentTaskStatus.Failed;
                case "killed": case "stopped": case "cancelled": case "canceled": return AgentTaskStatus.Stopped;
                default: return AgentTaskStatus.Running;
            }
        }

        private static DateTime? FromUnixMs(long? ms) => ms.HasValue && ms.Value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).UtcDateTime : (DateTime?)null;

        /// <summary>Tasks in start order, each child right under its parent, with its depth.</summary>
        public List<(AgentTask Task, int Depth)> Tree()
        {
            var result = new List<(AgentTask, int)>();
            var ids = new HashSet<string>(_tasks.Select(t => t.TaskId));
            void Add(AgentTask t, int depth)
            {
                result.Add((t, depth));
                foreach (var c in _tasks.Where(c => c.ParentTaskId == t.TaskId && c != t)) if (depth < 8) Add(c, depth + 1);
            }
            foreach (var root in _tasks.Where(t => t.ParentTaskId == null || !ids.Contains(t.ParentTaskId))) Add(root, 0);
            return result;
        }

        /// <summary>Drops finished tasks whose whole family has finished.</summary>
        public bool RemoveFinished()
        {
            int before = _tasks.Count;
            var keep = new HashSet<string>();
            foreach (var t in _tasks.Where(t => t.IsRunning))
                for (var x = t; x != null && keep.Add(x.TaskId); x = Find(x.ParentTaskId)) { }
            _tasks.RemoveAll(t => !keep.Contains(t.TaskId));
            return _tasks.Count != before;
        }

        public void Clear() => _tasks.Clear();

        /// <summary>"2 agents running · 1 done", for the panel's header.</summary>
        public string Headline()
        {
            int running = _tasks.Count(t => t.IsRunning);
            int done = _tasks.Count - running;
            int agents = _tasks.Count(t => t.IsRunning && t.IsAgent);
            int shells = running - agents;
            var parts = new List<string>();
            if (agents > 0) parts.Add(agents + (agents == 1 ? " agent" : " agents") + " running");
            if (shells > 0) parts.Add(shells + (shells == 1 ? " command" : " commands") + " running");
            if (done > 0) parts.Add(done + " done");
            return parts.Count == 0 ? "No tasks" : string.Join(" · ", parts);
        }
    }
}
