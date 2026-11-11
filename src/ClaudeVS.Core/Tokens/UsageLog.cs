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
        /// <summary>Claude Code's session id (the result's <c>session_id</c>): turns of one conversation share it; null in older lines.</summary>
        [JsonProperty("session", NullValueHandling = NullValueHandling.Ignore)] public string SessionId { get; set; }

        // Routing
        [JsonProperty("routed")] public bool Routed { get; set; }
        [JsonProperty("routeModel")] public string RouteModel { get; set; }
        [JsonProperty("routeEffort")] public string RouteEffort { get; set; }
        [JsonProperty("routeScore")] public double? RouteScore { get; set; }
        [JsonProperty("routeReason")] public string RouteReason { get; set; }
        /// <summary>
        /// The effort the turn was sent with ("low" … "max"), routed or picked by hand; null when Claude Code's own
        /// default applied (and in older lines, where <see cref="RouteEffort"/> may say it for routed turns).
        /// </summary>
        [JsonProperty("effort", NullValueHandling = NullValueHandling.Ignore)] public string Effort { get; set; }
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
        /// Tokens (estimated from characters) the main agent's tool results added to the conversation in this turn, per
        /// tool group ("Read", "Shell", "Search", "MCP github" …; <see cref="TokenMeter.ToolGroup"/>); null when none (and in older lines).
        /// </summary>
        [JsonProperty("tools", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, long> ToolResultTokens { get; set; }

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
    /// Append-only JSON-lines log of completed turns, kept locally (never sent anywhere), one file per local month
    /// (<c>usage-2026-11.jsonl</c> next to <see cref="Path"/>). Older versions wrote <see cref="Path"/> itself and moved it to
    /// <see cref="ArchivePath"/> at 5 MB: both are still read, first. With <see cref="KeepMonths"/> set, files older than
    /// that many months are deleted (the old two once last written before then). Feeds router tuning and the savings
    /// report. Thread-safe within one process.
    /// </summary>
    public sealed class UsageLog
    {
        private static readonly object Gate = new object();
        private DateTime _prunedFor; // the month the last prune ran in

        public UsageLog(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>The base name: the folder and name the monthly files are made from; the log of older versions.</summary>
        public string Path { get; }
        /// <summary>Where older versions moved <see cref="Path"/> when it passed 5 MB.</summary>
        public string ArchivePath => System.IO.Path.ChangeExtension(Path, ".1" + System.IO.Path.GetExtension(Path));
        public string Folder => System.IO.Path.GetDirectoryName(Path);

        /// <summary>How many months to keep, this one included; 0 or less keeps everything.</summary>
        public int KeepMonths { get; set; }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "usage.jsonl");

        /// <summary>The file for the local month of <paramref name="utc"/>: "usage-2026-11.jsonl".</summary>
        public string MonthPath(DateTime utc)
        {
            var local = utc.ToLocalTime();
            return System.IO.Path.Combine(Folder, System.IO.Path.GetFileNameWithoutExtension(Path)
                + "-" + local.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + System.IO.Path.GetExtension(Path));
        }

        /// <summary>The monthly files there are, oldest first.</summary>
        public IReadOnlyList<string> MonthFiles()
        {
            if (!Directory.Exists(Folder)) return new string[0];
            string stem = System.IO.Path.GetFileNameWithoutExtension(Path), ext = System.IO.Path.GetExtension(Path);
            return Directory.GetFiles(Folder, stem + "-*" + ext)
                .Where(f => MonthOf(f) != null)
                .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The first day of the month a monthly file is for; null for another file.</summary>
        private DateTime? MonthOf(string file)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(file), stem = System.IO.Path.GetFileNameWithoutExtension(Path) + "-";
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return null;
            return DateTime.TryParseExact(name.Substring(stem.Length), "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var month) ? month : (DateTime?)null;
        }

        /// <summary>Every file the log reads from, oldest first: the old two, then the monthly ones.</summary>
        public IReadOnlyList<string> Files()
        {
            var files = new List<string>();
            if (File.Exists(ArchivePath)) files.Add(ArchivePath);
            if (File.Exists(Path)) files.Add(Path);
            files.AddRange(MonthFiles());
            return files;
        }

        /// <summary>The size of all of them, in bytes.</summary>
        public long SizeBytes() { lock (Gate) return Files().Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }); }

        public bool HasAny => Files().Count > 0;

        /// <summary>"C:\…\claude-vs-tokensaver: 3 files, 1.2 MB, the last 3 months kept"; for the report's last line and the Open Usage Log Folder command.</summary>
        public string Describe()
        {
            int files = Files().Count;
            string size = FormatBytes(SizeBytes());
            string kept = KeepMonths > 0 ? $"the last {KeepMonths} {(KeepMonths == 1 ? "month" : "months")} kept" : "everything kept";
            return $"{Folder}: {files} {(files == 1 ? "file" : "files")}, {size}, {kept}";
        }

        /// <summary>"512 B", "12.3 KB", "1.2 MB".</summary>
        public static string FormatBytes(long bytes)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (bytes < 1024) return bytes.ToString(c) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", c) + " KB";
            return (bytes / (1024.0 * 1024)).ToString("0.0", c) + " MB";
        }

        public void Append(UsageRecord record)
        {
            if (record == null) return;
            string line = JsonConvert.SerializeObject(record, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(MonthPath(record.Timestamp), line + "\n");
                var month = new DateTime(record.Timestamp.ToLocalTime().Year, record.Timestamp.ToLocalTime().Month, 1);
                if (KeepMonths > 0 && month != _prunedFor) { _prunedFor = month; Prune(record.Timestamp); }
            }
        }

        /// <summary>
        /// Deletes what is older than <see cref="KeepMonths"/> months before the month of <paramref name="nowUtc"/>: monthly
        /// files of earlier months, and the old two once last written before the first month kept. Nothing with 0.
        /// </summary>
        public int Prune(DateTime nowUtc)
        {
            if (KeepMonths <= 0) return 0;
            var local = nowUtc.ToLocalTime();
            var firstKept = new DateTime(local.Year, local.Month, 1).AddMonths(-(KeepMonths - 1));
            int deleted = 0;
            lock (Gate)
            {
                foreach (var f in MonthFiles())
                    if (MonthOf(f) < firstKept && TryDelete(f)) deleted++;
                foreach (var f in new[] { ArchivePath, Path })
                    if (File.Exists(f) && File.GetLastWriteTime(f) < firstKept && TryDelete(f)) deleted++;
            }
            return deleted;
        }

        private static bool TryDelete(string file)
        {
            try { File.Delete(file); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>All readable records, oldest file first; malformed lines (e.g. a partial write) are skipped.</summary>
        public IReadOnlyList<UsageRecord> ReadAll()
        {
            var list = new List<UsageRecord>();
            var lines = new List<string>();
            lock (Gate)
                foreach (var f in Files())
                    try { lines.AddRange(File.ReadAllLines(f)); }
                    catch (FileNotFoundException) { }
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
