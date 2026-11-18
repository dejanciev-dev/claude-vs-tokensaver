using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeVS.Core.Setup;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// Is the token-saving setup (<see cref="TokenSaverSetup"/>) still in effect? Like the problem list Claude Code
    /// 2.1.327 added to <c>/status</c>, but for what the extension installed, from <c>get_settings</c> (no model call).
    /// Verified with 2.1.327: <c>effective</c> joins the hook lists and deny rules of every source and takes a single
    /// value such as <c>bashOutputMaxChars</c> from the highest-precedence source; <c>sources</c> says which file sets it.
    /// </summary>
    public static class SetupCheck
    {
        /// <summary>
        /// What is wrong with the setup in <paramref name="rootDir"/>, each with where to fix it. Empty when all is in
        /// effect; null when the setup was never installed there (nothing to check).
        /// </summary>
        public static List<CostlySetting> Find(JObject getSettingsResponse, string rootDir)
        {
            if (!TokenSaverSetup.IsInstalled(rootDir)) return null;
            var problems = new List<CostlySetting>();
            var effective = getSettingsResponse?["effective"] as JObject ?? new JObject();
            const string reinstall = "Tools → Claude Code → Install Token-Saving Settings and Hooks puts it back";

            // 1. The quiet-build hook: in the merged hooks, with a matcher that takes both shells, and hooks not off.
            var ours = ((effective.SelectToken("hooks.PreToolUse") as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Where(m => ((m["hooks"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    .Any(h => ((string)h["command"] ?? "").IndexOf(TokenSaverSetup.HookFileName, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            if (ours.Count == 0)
                problems.Add(new CostlySetting
                {
                    Setting = "quiet-build hook missing",
                    Where = ".claude/settings.json",
                    Why = $"no PreToolUse hook runs {TokenSaverSetup.HookFileName}, so dotnet build, msbuild and git log send their full output. {reinstall}",
                });
            else
            {
                foreach (var shell in new[] { "Bash", "PowerShell" })
                    if (!ours.Any(m => MatcherMatches((string)m["matcher"], shell)))
                        problems.Add(new CostlySetting
                        {
                            Setting = $"quiet-build hook skips {shell}",
                            Where = ".claude/settings.json",
                            Why = $"its matcher \"{(string)ours[0]["matcher"]}\" doesn't match {shell}, so commands run through {shell} keep their full output. Set it to \"{TokenSaverSetup.ShellMatcher}\"",
                        });
            }
            if (effective["disableAllHooks"]?.Type == JTokenType.Boolean && (bool)effective["disableAllHooks"])
                problems.Add(new CostlySetting
                {
                    Setting = "disableAllHooks: true",
                    Where = SettingsCheck.Where(SourceOf(getSettingsResponse, "disableAllHooks")) ?? "Claude Code's settings",
                    Why = "no hook runs, the quiet-build hook included. Remove it to make builds quiet again",
                });
            string script = Path.Combine(rootDir, ".claude", "hooks", TokenSaverSetup.HookFileName);
            if (ours.Count > 0 && !File.Exists(script))
                problems.Add(new CostlySetting { Setting = "hook script missing", Where = ".claude/hooks", Why = $"{TokenSaverSetup.HookFileName} is gone, so the hook fails on every shell command. {reinstall}" });

            // 2. The read-deny rules (joined from every source, so one missing was removed from the project's file).
            var deny = new HashSet<string>(((effective.SelectToken("permissions.deny") as JArray) ?? new JArray()).Select(t => (string)t), StringComparer.OrdinalIgnoreCase);
            var missing = TokenSaverSetup.DenyReadPatterns.Where(p => !deny.Contains(p)).ToList();
            if (missing.Count > 0)
                problems.Add(new CostlySetting
                {
                    Setting = $"{missing.Count} of {TokenSaverSetup.DenyReadPatterns.Length} read-deny rules missing",
                    Where = ".claude/settings.json",
                    Why = $"Claude may read {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? ", …" : "")} (build output and generated files). {reinstall}",
                });

            // 3. The shell output cap: missing, or raised by a source above the project's.
            var cap = effective[TokenSaverSetup.OutputMaxCharsKey];
            if (cap == null || cap.Type == JTokenType.Null)
                problems.Add(new CostlySetting
                {
                    Setting = $"{TokenSaverSetup.OutputMaxCharsKey} not set",
                    Where = ".claude/settings.json",
                    Why = $"shell output reaches Claude uncapped. {reinstall}",
                });
            else if ((cap.Type == JTokenType.Integer || cap.Type == JTokenType.Float) && (double)cap > TokenSaverSetup.DefaultBashMaxOutput)
                problems.Add(new CostlySetting
                {
                    Setting = $"{TokenSaverSetup.OutputMaxCharsKey}: {(long)(double)cap:N0}",
                    Where = SettingsCheck.Where(SourceOf(getSettingsResponse, TokenSaverSetup.OutputMaxCharsKey)) ?? "Claude Code's settings",
                    Why = $"long shell output reaches Claude up to that many characters, above the setup's {TokenSaverSetup.DefaultBashMaxOutput:N0}. Lower or remove it there",
                });
            return problems;
        }

        /// <summary>
        /// Does a hook matcher take this tool? Empty or "*" takes every tool; otherwise a name, a "|" list or a regular
        /// expression, matched against the whole tool name.
        /// </summary>
        public static bool MatcherMatches(string matcher, string tool)
        {
            if (string.IsNullOrWhiteSpace(matcher) || matcher.Trim() == "*") return true;
            try { return Regex.IsMatch(tool, "^(?:" + matcher.Trim() + ")$"); }
            catch (ArgumentException) { return string.Equals(matcher.Trim(), tool, StringComparison.Ordinal); }
        }

        /// <summary>The highest-precedence source that sets <paramref name="key"/> ("localSettings"…); null when none.</summary>
        private static string SourceOf(JObject response, string key)
        {
            string[] precedence = { "policySettings", "localSettings", "projectSettings", "userSettings" };
            var sources = (response?["sources"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            foreach (var name in precedence)
                if (sources.FirstOrDefault(s => (string)s["source"] == name)?["settings"]?[key] is JToken v && v.Type != JTokenType.Null) return name;
            return null;
        }

        /// <summary>
        /// The settings files the problems name, as full paths to open (".claude/settings.local.json" under
        /// <paramref name="rootDir"/>, "~/.claude/settings.json" under <paramref name="userHome"/>), each once, in order;
        /// managed settings and folders are left out.
        /// </summary>
        public static List<string> FilesToOpen(IEnumerable<CostlySetting> problems, string rootDir, string userHome)
        {
            var files = new List<string>();
            foreach (var p in problems ?? Enumerable.Empty<CostlySetting>())
            {
                string where = p?.Where;
                string path = null;
                if (where == ".claude/settings.json" || where == ".claude/settings.local.json")
                    path = string.IsNullOrEmpty(rootDir) ? null : Path.Combine(rootDir, ".claude", Path.GetFileName(where));
                else if (where == "~/.claude/settings.json")
                    path = string.IsNullOrEmpty(userHome) ? null : Path.Combine(userHome, ".claude", "settings.json");
                if (path != null && !files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
            }
            return files;
        }

        /// <summary>True when reinstalling the setup fixes at least one problem (it rewrites the project's files only).</summary>
        public static bool ReinstallHelps(IEnumerable<CostlySetting> problems) =>
            (problems ?? Enumerable.Empty<CostlySetting>()).Any(p => p.Where == ".claude/settings.json" || p.Where == ".claude/hooks");

        public static string Text(IReadOnlyCollection<CostlySetting> problems)
        {
            if (problems == null)
                return "Token-saving setup: not installed in this folder. Tools → Claude Code → Install Token-Saving Settings and Hooks adds it.";
            if (problems.Count == 0)
                return "Token-saving setup: all in effect (the quiet-build hook for Bash and PowerShell, the read-deny rules, the shell output cap).";
            return "Token-saving setup: " + problems.Count + (problems.Count == 1 ? " problem" : " problems") + ":\n" + string.Join("\n", problems.Select(p => "• " + p));
        }
    }
}
