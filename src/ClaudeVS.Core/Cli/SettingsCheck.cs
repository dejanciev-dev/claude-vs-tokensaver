using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>One Claude Code setting that changes what turns cost.</summary>
    public sealed class CostlySetting
    {
        /// <summary>"model: opus", "env MAX_THINKING_TOKENS=32000".</summary>
        public string Setting { get; set; }
        /// <summary>Where it is set: "~/.claude/settings.json", ".claude/settings.local.json", "managed settings" …</summary>
        public string Where { get; set; }
        /// <summary>What it does to the cost, and how to change it.</summary>
        public string Why { get; set; }

        public override string ToString() => $"{Setting} ({Where}): {Why}";
    }

    /// <summary>
    /// The Claude Code settings that make turns dearer, from <c>get_settings</c>' <c>sources</c> (no model call). Only
    /// keys known to matter are checked: <c>get_settings</c> keeps every key as written, known or not (2.1.326). The
    /// chat's own switches come as <c>flagSettings</c> and are left out; of the others the highest-precedence source wins
    /// (managed, then the project's local, the project's, the user's).
    /// </summary>
    public static class SettingsCheck
    {
        private static readonly string[] Precedence = { "policySettings", "localSettings", "projectSettings", "userSettings" };

        /// <summary>The source name <see cref="EnvAsUsed"/> gives a variable from the environment Claude Code starts with.</summary>
        public const string ProcessEnvironment = "environment";

        /// <summary>
        /// The value Claude Code uses for an environment variable, and where it comes from: its settings' <c>env</c> win
        /// over the environment it starts with (probed with 2.1.329: <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> 8000 in the
        /// environment and 100 in <c>.claude/settings.local.json</c> cut answers off at 100, and the other way round
        /// didn't), the highest-precedence source first (managed, local, project, user). Then
        /// <paramref name="processEnv"/> (Visual Studio's environment, by default), as <see cref="ProcessEnvironment"/>.
        /// </summary>
        public static (string Value, string Source) EnvAsUsed(JObject getSettingsResponse, string name, Func<string, string> processEnv = null)
        {
            var sources = (getSettingsResponse?["sources"] as JArray)?.OfType<JObject>()
                .Where(s => Array.IndexOf(Precedence, (string)s["source"]) >= 0)
                .OrderBy(s => Array.IndexOf(Precedence, (string)s["source"]));
            foreach (var s in sources ?? Enumerable.Empty<JObject>())
                if (s.SelectToken("settings.env") is JObject env && env[name] != null && env[name].Type != JTokenType.Null) return ((string)env[name], (string)s["source"]);
            string fromProcess = (processEnv ?? Environment.GetEnvironmentVariable)(name);
            return string.IsNullOrEmpty(fromProcess) ? (null, null) : (fromProcess, ProcessEnvironment);
        }

        /// <summary>The output limit Claude Code uses (<see cref="EnvAsUsed"/>), or null when none is set or it isn't a number.</summary>
        public static int? OutputLimit(JObject getSettingsResponse, Func<string, string> processEnv = null) =>
            int.TryParse(EnvAsUsed(getSettingsResponse, "CLAUDE_CODE_MAX_OUTPUT_TOKENS", processEnv).Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;

        public static string Where(string source)
        {
            switch (source)
            {
                case "policySettings": return "managed settings";
                case "localSettings": return ".claude/settings.local.json";
                case "projectSettings": return ".claude/settings.json";
                case "userSettings": return "~/.claude/settings.json";
                case ProcessEnvironment: return "Visual Studio's environment";
                default: return source;
            }
        }

        /// <summary>
        /// True when Claude Code talks to a gateway rather than the Anthropic API: <c>ANTHROPIC_BASE_URL</c> in its
        /// settings' <c>env</c> (any source, or <c>effective.env</c>) or in the environment it was started with
        /// (<paramref name="processEnv"/>; Visual Studio's, by default). A gateway's model may take fewer output tokens
        /// than Claude Code asks for, so a low <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> may be all it takes.
        /// </summary>
        public static bool BehindGateway(JObject getSettingsResponse, Func<string, string> processEnv = null)
        {
            const string name = "ANTHROPIC_BASE_URL";
            if (!string.IsNullOrWhiteSpace((processEnv ?? Environment.GetEnvironmentVariable)(name))) return true;
            if (getSettingsResponse?.SelectToken("effective.env." + name) is JToken e && !string.IsNullOrWhiteSpace((string)e)) return true;
            return (getSettingsResponse?["sources"] as JArray)?.OfType<JObject>()
                .Any(s => s.SelectToken("settings.env." + name) is JToken v && v.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)v)) == true;
        }

        public static List<CostlySetting> Find(JObject getSettingsResponse, Func<string, string> processEnv = null)
        {
            var found = new List<CostlySetting>();
            var sources = (getSettingsResponse?["sources"] as JArray)?.OfType<JObject>()
                .Where(s => Array.IndexOf(Precedence, (string)s["source"]) >= 0)
                .OrderBy(s => Array.IndexOf(Precedence, (string)s["source"])).ToList() ?? new List<JObject>();

            // The value a key has in the highest-precedence source that sets it, and that source
            (JToken Value, string Source) Get(string key)
            {
                foreach (var s in sources)
                    if (s["settings"] is JObject set && set[key] != null && set[key].Type != JTokenType.Null) return (set[key], (string)s["source"]);
                return (null, null);
            }
            (string Value, string Source) Env(string name)
            {
                foreach (var s in sources)
                    if (s.SelectToken("settings.env") is JObject env && env[name] != null) return ((string)env[name], (string)s["source"]);
                return (null, null);
            }
            void Add(string setting, string source, string why) => found.Add(new CostlySetting { Setting = setting, Where = Where(source), Why = why });

            var model = Get("model");
            string m = (string)model.Value;
            if (Dear(m))
                Add("model: " + m, model.Source, $"turns the chat doesn't route (and the terminal) start on {Family(m)}, the dearest model. The chat's model picker overrides it; set a cheaper default, or remove it");
            var envModel = Env("ANTHROPIC_MODEL");
            if (Dear(envModel.Value))
                Add("env ANTHROPIC_MODEL=" + envModel.Value, envModel.Source, $"the default model is {Family(envModel.Value)}, the dearest; the chat's model picker overrides it");
            var subModel = Env("CLAUDE_CODE_SUBAGENT_MODEL");
            if (Dear(subModel.Value))
                Add("env CLAUDE_CODE_SUBAGENT_MODEL=" + subModel.Value, subModel.Source, $"every subagent runs on {Family(subModel.Value)}, whatever the conversation's model; remove it to let them follow the conversation");

            var thinking = Get("alwaysThinkingEnabled");
            if (thinking.Value?.Type == JTokenType.Boolean && (bool)thinking.Value)
                Add("alwaysThinkingEnabled: true", thinking.Source, "every turn thinks first, billed as output (the dearest tokens). The chat's Thinking switch turns it off per conversation");
            var budget = Env("MAX_THINKING_TOKENS");
            if (int.TryParse(budget.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) && b >= 16000)
                Add("env MAX_THINKING_TOKENS=" + budget.Value, budget.Source, "a large thinking budget: a turn can think that much, billed as output. Lower it, or remove it");

            var style = Get("outputStyle");
            string st = (string)style.Value;
            if (st != null && (st.Equals("Explanatory", StringComparison.OrdinalIgnoreCase) || st.Equals("Learning", StringComparison.OrdinalIgnoreCase)))
                Add("outputStyle: " + st, style.Source, "every answer adds explanations, so answers run longer. The default style answers shorter");

            var effort = Get("effortLevel");
            if (ClaudeSettings.Effort((string)effort.Value) is EffortChoice e && e >= EffortChoice.High)
                Add("effortLevel: " + (string)effort.Value, effort.Source, "turns the chat doesn't route (and the terminal) run at high effort or more: more thinking and longer answers. The chat's effort picker overrides it");

            var caching = Env("DISABLE_PROMPT_CACHING");
            if (caching.Value == "1" || string.Equals(caching.Value, "true", StringComparison.OrdinalIgnoreCase))
                Add("env DISABLE_PROMPT_CACHING=" + caching.Value, caching.Source, "the prompt cache is off: every turn sends the whole conversation at the full input price instead of a tenth. Remove it");

            var output = EnvAsUsed(getSettingsResponse, "CLAUDE_CODE_MAX_OUTPUT_TOKENS", processEnv); // Visual Studio's environment too: Claude Code gets it
            // Behind a gateway it may be the most its model takes (Phase 73's link sets it from the model's refusal)
            if (int.TryParse(output.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) && o < LowOutputLimit && !BehindGateway(getSettingsResponse, processEnv))
                Add("env CLAUDE_CODE_MAX_OUTPUT_TOKENS=" + output.Value, output.Source, "a low output limit: long answers are cut off and Claude Code asks for the rest, each time reading the context again. Raise it, or remove it");

            var mcp = Get("enableAllProjectMcpServers");
            if (mcp.Value?.Type == JTokenType.Boolean && (bool)mcp.Value)
                Add("enableAllProjectMcpServers: true", mcp.Source, "every MCP server in the project's .mcp.json starts, and their tools' descriptions are sent with each request. Turn off the ones you don't use (/ → MCP servers…)");
            return found;
        }

        /// <summary>Below this, <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> cuts off ordinary answers (Claude Code's own default is far higher).</summary>
        public const int LowOutputLimit = 8000;

        private static bool Dear(string model)
        {
            string f = Tokens.PriceTable.Family(model);
            return f == "opus" || f == "fable";
        }

        private static string Family(string model)
        {
            string f = Tokens.PriceTable.Family(model) ?? model;
            return f.Length == 0 ? f : char.ToUpperInvariant(f[0]) + f.Substring(1);
        }

        /// <summary>The check as text for the chat: one line per setting, or that none raises the cost.</summary>
        public static string Text(IReadOnlyCollection<CostlySetting> found)
        {
            if (found == null || found.Count == 0)
                return "Claude Code settings that cost: none of the settings known to make turns dearer is set (managed, project or yours).";
            return "Claude Code settings that cost:\n" + string.Join("\n", found.Select(f => "• " + f));
        }
    }
}
