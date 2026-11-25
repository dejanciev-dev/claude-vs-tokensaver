using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// The few of Claude Code's own settings the chat follows, as Claude Code merged them from every source (managed,
    /// the project's local and shared, the user's): the control request <c>get_settings</c> (no model call; verified with
    /// 2.1.326) answers <c>effective</c> (the merged settings), <c>sources</c> and <c>applied</c> (the model and effort in
    /// use, after any cap).
    /// </summary>
    public sealed class ClaudeSettings
    {
        /// <summary>
        /// <c>maxEffortLevel</c>: Claude Code answers at no higher effort ("Effort 'high' exceeds the cap … using 'medium'"
        /// in its debug log, nothing in the stream). Null when not set.
        /// </summary>
        public EffortChoice? MaxEffort { get; set; }
        /// <summary><c>promptSuggestionEnabled</c>; null when not set.</summary>
        public bool? PromptSuggestionEnabled { get; set; }
        /// <summary>The settings that make turns dearer (<see cref="SettingsCheck"/>); empty when none.</summary>
        public System.Collections.Generic.List<CostlySetting> Costly { get; set; } = new System.Collections.Generic.List<CostlySetting>();
        /// <summary><c>applied.effort</c>: the effort Claude Code uses now ("medium"); null when not sent.</summary>
        public string AppliedEffort { get; set; }
        /// <summary>The whole <c>get_settings</c> answer, for checks that need more (<see cref="SetupCheck"/>).</summary>
        public JObject Response { get; set; }

        /// <summary>Claude Code talks to a gateway (<see cref="SettingsCheck.BehindGateway"/>).</summary>
        public bool BehindGateway { get; set; }

        public static ClaudeSettings Parse(JObject response)
        {
            var effective = response?["effective"] as JObject;
            return new ClaudeSettings
            {
                Response = response,
                BehindGateway = SettingsCheck.BehindGateway(response),
                MaxEffort = Effort((string)effective?["maxEffortLevel"]),
                PromptSuggestionEnabled = effective?["promptSuggestionEnabled"]?.Type == JTokenType.Boolean ? (bool?)(bool)effective["promptSuggestionEnabled"] : null,
                AppliedEffort = response?.SelectToken("applied.effort")?.Type == JTokenType.String ? (string)response.SelectToken("applied.effort") : null,
                Costly = SettingsCheck.Find(response),
                SpinnerVerbs = effective?["spinnerVerbs"],
            };
        }

        /// <summary>Claude Code's <c>spinnerVerbs</c> as set (the merged settings), or null; see <see cref="Verbs"/>.</summary>
        public JToken SpinnerVerbs { get; set; }

        /// <summary>
        /// The words the activity row picks from ("Pondering…"), with Claude Code's <c>spinnerVerbs</c>
        /// (<c>{ "mode": "append" | "replace", "verbs": [...] }</c>, from 2.1.332's schema; VS Code 2.1.332 fixed it
        /// being ignored from user and managed settings): <c>append</c> adds yours to <paramref name="defaults"/>,
        /// <c>replace</c> uses only yours. No setting, no verbs, or a shape it doesn't know: the defaults.
        /// </summary>
        public static string[] Verbs(JToken setting, string[] defaults)
        {
            if (!(setting is JObject s) || !(s["verbs"] is JArray list)) return defaults;
            var mine = list.Where(t => t.Type == JTokenType.String).Select(t => ((string)t).Trim()).Where(v => v.Length > 0 && v.Length <= 60).Distinct().ToArray();
            if (mine.Length == 0) return defaults;
            string mode = (string)s["mode"];
            if (mode == "replace") return mine;
            if (mode == "append" || mode == null) return defaults.Concat(mine.Where(v => !defaults.Contains(v))).ToArray();
            return defaults;
        }

        /// <summary>An effort level's CLI name ("low" … "max") as a choice; null when unknown.</summary>
        public static EffortChoice? Effort(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "low": return EffortChoice.Low;
                case "medium": return EffortChoice.Medium;
                case "high": return EffortChoice.High;
                case "xhigh": return EffortChoice.XHigh;
                case "max": return EffortChoice.Max;
                default: return null;
            }
        }

        /// <summary><paramref name="effort"/>, no higher than <paramref name="cap"/>; Auto (Claude Code's default, which it caps itself) stays.</summary>
        public static EffortChoice Cap(EffortChoice effort, EffortChoice? cap) =>
            cap is EffortChoice c && effort != EffortChoice.Auto && effort > c ? c : effort;

        /// <summary>The same for an effort's CLI name, as logged ("high" → "medium"); null and unknown names stay.</summary>
        public static string Cap(string effort, EffortChoice? cap) =>
            Effort(effort) is EffortChoice e ? Cap(e, cap).ToCli() : effort;

        /// <summary>Is this effort above the cap (for the effort menu)?</summary>
        public static bool Above(EffortChoice effort, EffortChoice? cap) => Cap(effort, cap) != effort;
    }
}
