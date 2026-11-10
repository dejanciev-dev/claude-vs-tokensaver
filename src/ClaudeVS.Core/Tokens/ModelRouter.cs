using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeVS.Core.Cli;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>The line under a routed prompt and its tooltip.</summary>
    public static class RouteText
    {
        /// <summary>
        /// "Auto → Sonnet, low effort" when both were on Auto; "Auto → Sonnet" or "Auto → low effort" when only one
        /// was (the other is what you picked, shown in the footer).
        /// </summary>
        public static string Note(bool modelAuto, bool effortAuto, ModelChoice model, EffortChoice effort)
        {
            var parts = new List<string>();
            string m = model.ToCli();
            if (modelAuto && m != null) parts.Add(char.ToUpperInvariant(m[0]) + m.Substring(1));
            string e = effort.ToCli();
            if (effortAuto && e != null) parts.Add((e == "xhigh" ? "extra high" : e) + " effort");
            return parts.Count == 0 ? null : "Auto → " + string.Join(", ", parts);
        }

        /// <summary>"Picked by the Haiku router: asks for an explanation, no code changes" (the reason without its prefix).</summary>
        public static string Tip(bool byHaiku, string reason)
        {
            reason = (reason ?? "").Trim();
            if (reason.StartsWith("Haiku router", StringComparison.Ordinal)) reason = reason.Substring("Haiku router".Length).TrimStart(':', ' ');
            if (reason == "default") reason = "";
            string who = byHaiku ? "Picked by the Haiku router" : "Picked by the local router";
            return reason.Length == 0 ? who : who + ": " + reason;
        }
    }

    public sealed class RouteRequest
    {
        public string Prompt { get; set; } = "";
        /// <summary>Lines of code attached to the prompt (selection snippet etc.).</summary>
        public int AttachedCodeLines { get; set; }
        public int AttachedFiles { get; set; }
        /// <summary>Model the running session uses now (Auto = no session yet).</summary>
        public ModelChoice CurrentModel { get; set; } = ModelChoice.Auto;
        public EffortChoice CurrentEffort { get; set; } = EffortChoice.Auto;
        /// <summary>Current context fill — used to avoid cache-busting downgrades in a big conversation.</summary>
        public long ContextTokens { get; set; }
    }

    public sealed class RouteDecision
    {
        public ModelChoice Model { get; set; }
        public EffortChoice Effort { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; }
        public override string ToString() => $"{Model.ToCli()}/{Effort.ToCli()} (score {Score:0.0}): {Reason}";
    }

    /// <summary>
    /// Cheap, local, deterministic prompt classifier. No API call — spending tokens to save tokens
    /// rarely pays off for routing. Picks the smallest model/effort likely to do the job.
    ///
    /// Cache awareness: the prompt cache is per model. Switching model mid-conversation means the
    /// whole context is re-sent at cache-write price (1.25x input), so in a large conversation we
    /// never downgrade and only upgrade for clearly hard prompts.
    /// </summary>
    public sealed class ModelRouter
    {
        public long StickyContextTokens { get; set; } = 30_000;

        // Keywords match whole words (plus common endings: -s, -es, -ed, -d, -ing, -er).
        // A trailing '*' marks a stem that may be followed by any letters ("optimi*" → optimize, optimization).
        private static readonly Regex[] Hard = Keywords(
            "architect*", "design pattern", "redesign", "refactor*", "migrat*", "rewrit*",
            "race condition", "deadlock", "thread-safe", "thread safe", "concurren*", "memory leak", "performance",
            "optimi*", "root cause", "investigat*", "why does", "why is", "security", "vulnerab*", "algorithm",
            "across the solution", "whole solution", "entire solution", "all projects", "end-to-end", "plan");

        private static readonly Regex[] Medium = Keywords(
            "implement*", "add", "updat*", "extend*", "chang*", "modif*", "improv*", "enhanc*",
            "creat*", "write test", "unit test", "fix", "bug", "endpoint", "controller",
            "component", "service", "repositor*", "query", "stored procedure", "migration script", "error", "exception");

        private static readonly Regex[] Easy = Keywords(
            "renam*", "typo", "format*", "comment", "docstring", "xml doc", "summar*", "explain this", "what does",
            "what is", "translat*", "convert this", "regex", "spelling", "sort the", "reorder", "one-liner", "simple",
            "commit message", "add using", "remove unused");

        private static Regex[] Keywords(params string[] words) => words.Select(w =>
        {
            bool stem = w.EndsWith("*");
            string core = Regex.Escape(stem ? w.Substring(0, w.Length - 1) : w);
            string pattern = @"(?<![\w-])" + core + (stem ? @"[\w-]*" : @"(?:s|es|ed|d|ing|er)?(?![\w-])");
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }).ToArray();

        private static readonly Regex FilePath = new Regex(@"[\w\-./\\]+\.(cs|ts|tsx|js|html|scss|css|sql|json|xml|csproj|razor|cshtml|xaml|py|md)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public RouteDecision Route(RouteRequest r)
        {
            string p = (r.Prompt ?? "").ToLowerInvariant();
            var reasons = new List<string>();
            double score = 0;

            int hard = Hard.Count(k => k.IsMatch(p));
            int med = Medium.Count(k => k.IsMatch(p));
            int easy = Easy.Count(k => k.IsMatch(p));
            if (hard > 0) { score += Math.Min(hard, 3) * 2.5; reasons.Add($"{hard} hard keyword(s)"); }
            if (med > 0) { score += Math.Min(med, 3) * 0.75; reasons.Add($"{med} task keyword(s)"); }
            if (easy > 0) { score -= Math.Min(easy, 2) * 2.0; reasons.Add($"{easy} simple-task keyword(s)"); }

            int len = p.Length;
            if (len > 1500) { score += 2; reasons.Add("long prompt"); }
            else if (len > 500) { score += 1; }
            // Short is only a sign of an easy task when it asks for no change: "add more samples and update the
            // existing ones" is short but means writing code in several files.
            else if (len < 80 && hard == 0 && med == 0) { score -= 1; reasons.Add("short prompt"); }

            int files = FilePath.Matches(r.Prompt ?? "").Count + r.AttachedFiles;
            if (files >= 4) { score += 2; reasons.Add($"{files} files"); }
            else if (files >= 2) { score += 1; }

            if (r.AttachedCodeLines > 300) { score += 1.5; reasons.Add("large code attachment"); }
            else if (r.AttachedCodeLines > 0 && r.AttachedCodeLines <= 30 && hard == 0) { score -= 0.5; }

            ModelChoice model; EffortChoice effort;
            if (score <= -1.5) { model = ModelChoice.Haiku; effort = EffortChoice.Low; }
            else if (score < 3.5) { model = ModelChoice.Sonnet; effort = score < 1 ? EffortChoice.Low : EffortChoice.Medium; }
            else if (score < 6) { model = ModelChoice.Opus; effort = EffortChoice.Medium; }
            else { model = ModelChoice.Opus; effort = EffortChoice.High; }

            var decision = new RouteDecision
            {
                Model = model,
                Effort = effort,
                Score = score,
                Reason = reasons.Count == 0 ? "default" : string.Join(", ", reasons),
            };
            return ApplyCacheStickiness(decision, r, clearlyHard: score >= 6);
        }

        /// <summary>
        /// Cache stickiness: in a big conversation keep the current model (its prompt cache), except to upgrade for a
        /// clearly hard prompt. Used for the local rules and for the Haiku router's answers alike.
        /// </summary>
        public RouteDecision ApplyCacheStickiness(RouteDecision d, RouteRequest r, bool clearlyHard)
        {
            if (d == null || r == null) return d;
            if (r.CurrentModel == ModelChoice.Auto || r.ContextTokens < StickyContextTokens || d.Model == r.CurrentModel) return d;
            bool upgrade = Rank(d.Model) > Rank(r.CurrentModel);
            if (upgrade && clearlyHard) return d;
            d.Reason = (d.Reason == "default" ? "" : d.Reason + ", ") + $"kept {r.CurrentModel.ToCli()} to reuse the prompt cache ({TokenMeter.Format(r.ContextTokens)} context)";
            d.Model = r.CurrentModel;
            return d;
        }

        private static int Rank(ModelChoice m) => m == ModelChoice.Haiku ? 1 : m == ModelChoice.Sonnet ? 2 : m == ModelChoice.Opus ? 3 : m == ModelChoice.Fable ? 4 : 0;
    }
}
