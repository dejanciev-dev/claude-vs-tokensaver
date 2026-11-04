using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    // Data for the CLI's own dialogs, read with control requests that make no model call (verified with 2.1.318):
    // get_status, list_permission_rules, get_hooks_listing, get_memory_dialog, get_usage, and the initialize answer
    // (output styles). Captures in Fixtures/cli-2.1.318-dialogs.jsonl.

    /// <summary>One row of /status: "Session" · "Version" · "2.1.318".</summary>
    public sealed class StatusRow
    {
        public string Section { get; set; }
        public string Label { get; set; }
        public string Value { get; set; }

        public static List<StatusRow> Parse(JObject response) =>
            ((response?["sections"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .SelectMany(s => ((s["rows"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    .Select(r => new StatusRow { Section = (string)s["title"], Label = (string)r["label"], Value = (string)r["value"] }))
                .Where(r => !string.IsNullOrEmpty(r.Label))
                .ToList();
    }

    /// <summary>Which settings file a rule or hook comes from, and where that file is.</summary>
    public static class SettingsSources
    {
        public static string Label(string source)
        {
            switch (source)
            {
                case "userSettings": return "User settings";
                case "projectSettings": return "Project settings";
                case "localSettings": return "Local project settings";
                case "policySettings": return "Managed policy";
                case "flagSettings": return "Command line";
                case "cliArg": return "Command line";
                case "session": return "This session";
                default: return source;
            }
        }

        /// <summary>The settings file for a source, or null when it is not a file we can open.</summary>
        public static string PathFor(string source, string projectDir, string userHome = null)
        {
            string home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (source)
            {
                case "userSettings": return Path.Combine(home, ".claude", "settings.json");
                case "projectSettings": return string.IsNullOrEmpty(projectDir) ? null : Path.Combine(projectDir, ".claude", "settings.json");
                case "localSettings": return string.IsNullOrEmpty(projectDir) ? null : Path.Combine(projectDir, ".claude", "settings.local.json");
                default: return null;
            }
        }
    }

    public sealed class PermissionRuleRow
    {
        /// <summary>"allow", "ask" or "deny".</summary>
        public string Behavior { get; set; }
        public string Source { get; set; }
        public string Rule { get; set; }
        /// <summary>Plain-language reading, e.g. "Any Bash command starting with git push"; null when the CLI has none.</summary>
        public string Description { get; set; }

        public static List<PermissionRuleRow> Parse(JObject response) =>
            ((response?.SelectToken("state.rules") as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Select(r => new PermissionRuleRow
                {
                    Behavior = (string)r["behavior"],
                    Source = (string)r["source"],
                    Rule = (string)r["rule"],
                    Description = r["description"] is JObject d ? ((string)d["prefix"] ?? "") + ((string)d["emphasis"] ?? "") + ((string)d["suffix"] ?? "") : (string)r["description"],
                })
                .Where(r => !string.IsNullOrEmpty(r.Rule))
                .ToList();

        public static int Order(string behavior) => behavior == "deny" ? 0 : behavior == "ask" ? 1 : 2;
    }

    public sealed class HookRow
    {
        public string Event { get; set; }
        public string Matcher { get; set; }
        public string Source { get; set; }
        public string SourceLabel { get; set; }
        /// <summary>"command", "prompt", "http", …</summary>
        public string Type { get; set; }
        public string Text { get; set; }

        public static List<HookRow> Parse(JObject response) =>
            ((response?["hooks"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Select(h => new HookRow
                {
                    Event = (string)h["event"],
                    Matcher = (string)h["matcher"],
                    Source = (string)h["source"],
                    SourceLabel = (string)h["sourceLabel"],
                    Type = (string)h["type"],
                    Text = (string)h["displayText"] ?? (string)h["commandText"],
                })
                .ToList();
    }

    public sealed class MemoryEntry
    {
        /// <summary>"user", "project", "auto" (folder) or "memory" (one saved memory).</summary>
        public string Kind { get; set; }
        public string Path { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public bool Exists { get; set; }
        public bool IsFolder { get; set; }
        public DateTime? Modified { get; set; }

        public sealed class Dialog
        {
            public List<MemoryEntry> Entries { get; } = new List<MemoryEntry>();
            /// <summary>"on", "off", … for auto memory; null when the CLI does not say.</summary>
            public string AutoMemoryStatus { get; set; }
        }

        public static Dialog Parse(JObject response)
        {
            var d = new Dialog { AutoMemoryStatus = (string)response?.SelectToken("auto_memory.status") };
            IEnumerable<JObject> Items(string key) => (response?[key] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
            foreach (var f in Items("files"))
                d.Entries.Add(new MemoryEntry { Kind = (string)f["kind"], Path = (string)f["path"], Label = (string)f["label"], Description = (string)f["description"], Exists = f.Value<bool?>("exists") ?? true });
            foreach (var f in Items("memories"))
            {
                double? ms = f.Value<double?>("modified_ms");
                d.Entries.Add(new MemoryEntry
                {
                    Kind = "memory", Path = (string)f["path"], Label = (string)f["name"], Description = (string)f["description"], Exists = true,
                    Modified = ms.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms.Value).UtcDateTime : (DateTime?)null,
                });
            }
            foreach (var f in Items("folders"))
                d.Entries.Add(new MemoryEntry { Kind = (string)f["kind"], Path = (string)f["path"], Label = (string)f["label"], Description = (string)f["description"], Exists = true, IsFolder = true });
            return d;
        }
    }

    public sealed class UsageLimit
    {
        /// <summary>"session", "weekly_all", "weekly_opus", …</summary>
        public string Kind { get; set; }
        public int Percent { get; set; }
        public DateTime? ResetsAt { get; set; }
        public bool IsActive { get; set; }
        public string Severity { get; set; }

        public string Name
        {
            get
            {
                switch (Kind)
                {
                    case "session": return "5-hour limit";
                    case "weekly_all": return "Weekly limit (all models)";
                    default:
                        if (Kind != null && Kind.StartsWith("weekly_")) return "Weekly limit (" + Kind.Substring(7).Replace('_', ' ') + ")";
                        return Kind ?? "Limit";
                }
            }
        }
    }

    public sealed class UsageInfo
    {
        public string Plan { get; set; }
        public List<UsageLimit> Limits { get; } = new List<UsageLimit>();
        public double SessionCostUsd { get; set; }
        public long SessionApiMs { get; set; }
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public bool ExtraUsageEnabled { get; set; }

        public static UsageInfo Parse(JObject r)
        {
            var u = new UsageInfo
            {
                Plan = (string)r?["subscription_type"],
                SessionCostUsd = r?.SelectToken("session.total_cost_usd")?.Value<double?>() ?? 0,
                SessionApiMs = r?.SelectToken("session.total_api_duration_ms")?.Value<long?>() ?? 0,
                LinesAdded = r?.SelectToken("session.total_lines_added")?.Value<int?>() ?? 0,
                LinesRemoved = r?.SelectToken("session.total_lines_removed")?.Value<int?>() ?? 0,
                ExtraUsageEnabled = r?.SelectToken("rate_limits.extra_usage.is_enabled")?.Value<bool?>() ?? false,
            };
            foreach (var l in (r?.SelectToken("rate_limits.limits") as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                DateTime? resets = null;
                var token = l["resets_at"];
                if (token != null && token.Type == JTokenType.Date) resets = token.Value<DateTime>().ToUniversalTime();
                else if (DateTimeOffset.TryParse((string)token, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dto)) resets = dto.UtcDateTime;
                u.Limits.Add(new UsageLimit
                {
                    Kind = (string)l["kind"],
                    Percent = l.Value<int?>("percent") ?? 0,
                    ResetsAt = resets,
                    IsActive = l.Value<bool?>("is_active") ?? false,
                    Severity = (string)l["severity"],
                });
            }
            return u;
        }

        /// <summary>"Pro", "Max", … from the CLI's subscription type.</summary>
        public string PlanName => string.IsNullOrEmpty(Plan) ? "API key or unknown plan" : char.ToUpperInvariant(Plan[0]) + Plan.Substring(1);
    }

    /// <summary>Output styles from the initialize answer: <c>output_style</c> and <c>available_output_styles</c>.</summary>
    public sealed class OutputStyles
    {
        public string Current { get; set; }
        public List<string> Available { get; } = new List<string>();

        public static OutputStyles Parse(JObject initialize)
        {
            if (initialize == null) return null;
            var s = new OutputStyles { Current = (string)initialize["output_style"] ?? "default" };
            if (initialize["available_output_styles"] is JArray a) s.Available.AddRange(a.Values<string>().Where(x => !string.IsNullOrEmpty(x)));
            if (!s.Available.Contains(s.Current)) s.Available.Insert(0, s.Current);
            return s;
        }

        /// <summary>What the built-in styles do (custom styles show their name only).</summary>
        public static string Describe(string style)
        {
            switch ((style ?? "").ToLowerInvariant())
            {
                case "default": return "Claude Code's normal style for software work";
                case "explanatory": return "Adds insights about the code and the choices made (longer answers)";
                case "learning": return "Explains and leaves small parts for you to write (longer answers)";
                case "concise": return "Shorter answers";
                case "proactive": return "Takes more initiative";
                default: return "Custom style";
            }
        }
    }
}
