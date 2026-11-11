using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>One completed turn, as written to the local usage log.</summary>
    public sealed class UsageRecord
    {
        [JsonProperty("ts")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        [JsonProperty("solution")] public string Solution { get; set; }

        // Routing
        [JsonProperty("routed")] public bool Routed { get; set; }
        [JsonProperty("routeModel")] public string RouteModel { get; set; }
        [JsonProperty("routeEffort")] public string RouteEffort { get; set; }
        [JsonProperty("routeScore")] public double? RouteScore { get; set; }
        [JsonProperty("routeReason")] public string RouteReason { get; set; }
        [JsonProperty("promptChars")] public int PromptChars { get; set; }
        [JsonProperty("attachedLines")] public int AttachedLines { get; set; }
        [JsonProperty("attachedFiles")] public int AttachedFiles { get; set; }
        /// <summary>"haiku" when the Haiku router decided; null for the local rules.</summary>
        [JsonProperty("router", NullValueHandling = NullValueHandling.Ignore)] public string Router { get; set; }
        /// <summary>What the local rules would have picked (logged with the Haiku router, to compare them).</summary>
        [JsonProperty("localModel", NullValueHandling = NullValueHandling.Ignore)] public string LocalModel { get; set; }
        [JsonProperty("localEffort", NullValueHandling = NullValueHandling.Ignore)] public string LocalEffort { get; set; }
        /// <summary>What asking the Haiku router cost, and how long it took (also when it timed out).</summary>
        [JsonProperty("routerCost", DefaultValueHandling = DefaultValueHandling.Ignore)] public double RouterCostUsd { get; set; }
        [JsonProperty("routerMs", DefaultValueHandling = DefaultValueHandling.Ignore)] public long RouterMs { get; set; }
        /// <summary>First characters of the prompt. Only written when the user opts in; enables router replay.</summary>
        [JsonProperty("prompt")] public string PromptPreview { get; set; }

        public const int PromptPreviewLength = 200;

        public static string Preview(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return null;
            string p = prompt.Trim();
            return p.Length <= PromptPreviewLength ? p : p.Substring(0, PromptPreviewLength);
        }

        // Outcome. The token counts are the main agent's (the result's usage); subagents' requests are not in them,
        // while CostUsd (the CLI's total_cost_usd) includes them at what they were billed, long-context tier and all
        // (seen in Fixtures/cli-2.1.319-fork.jsonl). So LargeContextTokens counts main-agent requests only, on purpose.
        [JsonProperty("model")] public string ModelUsed { get; set; }
        [JsonProperty("in")] public long InputTokens { get; set; }
        [JsonProperty("out")] public long OutputTokens { get; set; }
        [JsonProperty("cacheRead")] public long CacheReadTokens { get; set; }
        [JsonProperty("cacheWrite")] public long CacheWriteTokens { get; set; }
        /// <summary>Of <see cref="CacheWriteTokens"/>: written with the 1-hour lifetime (2x input); 0 in older lines.</summary>
        [JsonProperty("cacheWrite1h", DefaultValueHandling = DefaultValueHandling.Ignore)] public long CacheWrite1hTokens { get; set; }
        /// <summary>Of <see cref="OutputTokens"/>: thinking (Claude Code 2.1.319 reports it); 0 in older lines.</summary>
        [JsonProperty("thinking", DefaultValueHandling = DefaultValueHandling.Ignore)] public long ThinkingTokens { get; set; }
        /// <summary>Of <see cref="TotalTokens"/>: tokens of requests answered in fast mode (2x prices); 0 in older lines.</summary>
        [JsonProperty("fastTokens", DefaultValueHandling = DefaultValueHandling.Ignore)] public long FastTokens { get; set; }
        [JsonIgnore] public double SpeedMultiplier => ModelPrice.SpeedMultiplier(FastTokens, TotalTokens);
        /// <summary>Of <see cref="TotalTokens"/>: tokens of requests over 100k input tokens (Haiku bills those 5x); 0 in older lines.</summary>
        [JsonProperty("largeContextTokens", DefaultValueHandling = DefaultValueHandling.Ignore)] public long LargeContextTokens { get; set; }
        [JsonProperty("cost")] public double CostUsd { get; set; }
        [JsonProperty("ms")] public long DurationMs { get; set; }
        [JsonProperty("error")] public bool IsError { get; set; }

        /// <summary>Characters of file content not sent thanks to references (≈ chars / TokenEstimate.CharsPerToken tokens).</summary>
        [JsonProperty("charsAvoided")] public long CharsAvoided { get; set; }

        [JsonIgnore] public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

        /// <summary>
        /// The turn's subagents, one entry per model (<see cref="SubagentUsageTracker"/>); null when none (and in
        /// older lines). Not in the token counts above, which are the main agent's; already in <see cref="CostUsd"/>.
        /// </summary>
        [JsonProperty("agents", NullValueHandling = NullValueHandling.Ignore)] public List<AgentTokens> Agents { get; set; }

        [JsonIgnore] public long AgentTokensTotal => Agents?.Sum(a => a.Total) ?? 0;

        /// <summary>
        /// How the turn's subagent cost split by kind of agent (<see cref="AgentTypeSplit"/>): shares, costliest first;
        /// null when none ran (and in older lines).
        /// </summary>
        [JsonProperty("agentTypes", NullValueHandling = NullValueHandling.Ignore)] public List<AgentTypeCost> AgentTypes { get; set; }
    }

    /// <summary>One model's subagent tokens in a logged turn.</summary>
    public sealed class AgentTokens
    {
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("in")] public long InputTokens { get; set; }
        [JsonProperty("out")] public long OutputTokens { get; set; }
        [JsonProperty("cacheRead")] public long CacheReadTokens { get; set; }
        [JsonProperty("cacheWrite")] public long CacheWriteTokens { get; set; }
        [JsonProperty("cacheWrite1h", DefaultValueHandling = DefaultValueHandling.Ignore)] public long CacheWrite1hTokens { get; set; }

        [JsonIgnore] public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

        public Cli.TokenUsage ToUsage() => new Cli.TokenUsage
        {
            Input = InputTokens, Output = OutputTokens, CacheRead = CacheReadTokens, CacheCreation = CacheWriteTokens, CacheCreation1h = CacheWrite1hTokens,
        };

        /// <summary>Null when there were none.</summary>
        public static List<AgentTokens> From(IReadOnlyDictionary<string, Cli.TokenUsage> perModel) =>
            perModel == null || perModel.Count == 0 ? null : perModel.Select(kv => new AgentTokens
            {
                Model = kv.Key, InputTokens = kv.Value.Input, OutputTokens = kv.Value.Output, CacheReadTokens = kv.Value.CacheRead,
                CacheWriteTokens = kv.Value.CacheCreation, CacheWrite1hTokens = kv.Value.CacheCreation1h,
            }).ToList();
    }

    public sealed class UsageSummary
    {
        public int Turns { get; set; }
        public double CostUsd { get; set; }
        public long TotalTokens { get; set; }
        public long TokensAvoided { get; set; }
        public double CacheHitPercent { get; set; }
        /// <summary>Turns per model actually used (e.g. "claude-haiku-…" → 12).</summary>
        public Dictionary<string, int> TurnsByModel { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public DateTime? Since { get; set; }

        public override string ToString()
        {
            if (Turns == 0) return "no turns recorded yet";
            string models = string.Join(", ", TurnsByModel.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
            return $"{Turns} turns, ${CostUsd:0.00}, {TokenMeter.Format(TotalTokens)} tok, cache {CacheHitPercent:0}%, "
                 + $"~{TokenMeter.Format(TokensAvoided)} tok avoided by references"
                 + (models.Length > 0 ? $" | {models}" : "")
                 + (Since.HasValue ? $" | since {Since.Value.ToLocalTime():yyyy-MM-dd}" : "");
        }

        public static UsageSummary From(IEnumerable<UsageRecord> records, string solution = null)
        {
            var s = new UsageSummary();
            long input = 0, cacheRead = 0;
            foreach (var r in records ?? Enumerable.Empty<UsageRecord>())
            {
                if (solution != null && !string.Equals(r.Solution, solution, StringComparison.OrdinalIgnoreCase)) continue;
                s.Turns++;
                s.CostUsd += r.CostUsd;
                s.TotalTokens += r.TotalTokens;
                s.TokensAvoided += TokenEstimate.FromChars(r.CharsAvoided);
                input += r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens;
                cacheRead += r.CacheReadTokens;
                if (!string.IsNullOrEmpty(r.ModelUsed))
                    s.TurnsByModel[r.ModelUsed] = s.TurnsByModel.TryGetValue(r.ModelUsed, out int n) ? n + 1 : 1;
                if (!s.Since.HasValue || r.Timestamp < s.Since.Value) s.Since = r.Timestamp;
            }
            s.CacheHitPercent = input > 0 ? 100.0 * cacheRead / input : 0;
            return s;
        }
    }

    /// <summary>
    /// Append-only JSON-lines log of completed turns, kept locally (never sent anywhere).
    /// Feeds router tuning and the savings report. Thread-safe within one process.
    /// </summary>
    public sealed class UsageLog
    {
        private static readonly object Gate = new object();

        public UsageLog(string path, long maxBytes = DefaultMaxBytes)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            MaxBytes = maxBytes;
        }

        /// <summary>When the log passes this size it is moved to <see cref="ArchivePath"/> (replacing the previous archive).</summary>
        public const long DefaultMaxBytes = 5 * 1024 * 1024;

        public string Path { get; }
        public long MaxBytes { get; }
        public string ArchivePath => System.IO.Path.ChangeExtension(Path, ".1" + System.IO.Path.GetExtension(Path));

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "usage.jsonl");

        public void Append(UsageRecord record)
        {
            if (record == null) return;
            string line = JsonConvert.SerializeObject(record, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                if (MaxBytes > 0 && File.Exists(Path) && new FileInfo(Path).Length >= MaxBytes)
                {
                    if (File.Exists(ArchivePath)) File.Delete(ArchivePath);
                    File.Move(Path, ArchivePath);
                }
                File.AppendAllText(Path, line + "\n");
            }
        }

        /// <summary>All readable records, archive first; malformed lines (e.g. a partial write) are skipped.</summary>
        public IReadOnlyList<UsageRecord> ReadAll()
        {
            var list = new List<UsageRecord>();
            var lines = new List<string>();
            lock (Gate)
            {
                if (File.Exists(ArchivePath)) lines.AddRange(File.ReadAllLines(ArchivePath));
                if (File.Exists(Path)) lines.AddRange(File.ReadAllLines(Path));
            }
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                try { var r = JsonConvert.DeserializeObject<UsageRecord>(l); if (r != null) list.Add(r); }
                catch (JsonException) { }
            }
            return list;
        }
    }
}
