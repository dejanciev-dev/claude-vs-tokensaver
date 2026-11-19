using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>One model Claude Code offers, from the <c>initialize</c> answer's <c>models</c> list.</summary>
    public sealed class ModelInfo
    {
        /// <summary>What <c>--model</c> or <c>set_model</c> takes: an alias ("haiku", "default") or a full ID.</summary>
        public string Value { get; set; }
        /// <summary>The full ID the API answers with ("claude-haiku-5-5").</summary>
        public string Id { get; set; }
        /// <summary>"Haiku 5.5"; for "default", "Default (recommended)".</summary>
        public string DisplayName { get; set; }
        /// <summary>
        /// "low" … "max" as Claude Code lists them; empty for a model without effort (Haiku 4.5); null when Claude Code
        /// lists no levels at all (an older one).
        /// </summary>
        public IReadOnlyList<string> EffortLevels { get; set; }
    }

    /// <summary>
    /// The models Claude Code offers, as its <c>initialize</c> answer lists them (2.1.328: <c>value</c>,
    /// <c>resolvedModel</c>, <c>displayName</c>, <c>supportedEffortLevels</c>): what an alias resolves to, what to call a
    /// model ID, and which effort levels it takes.
    /// </summary>
    public sealed class ModelCatalog
    {
        private readonly List<ModelInfo> _models;

        private ModelCatalog(List<ModelInfo> models) { _models = models; }

        public IReadOnlyList<ModelInfo> Models => _models;

        /// <summary>The catalog from an <c>initialize</c> answer; null when it has no models list (an older Claude Code).</summary>
        public static ModelCatalog Parse(JObject initializeResponse)
        {
            if (!(initializeResponse?["models"] is JArray models)) return null;
            var list = new List<ModelInfo>();
            foreach (var m in models.OfType<JObject>())
            {
                string value = Str(m["value"]);
                if (value == null) continue;
                list.Add(new ModelInfo
                {
                    Value = value,
                    Id = Str(m["resolvedModel"]) ?? value,
                    DisplayName = Str(m["displayName"]),
                    EffortLevels = m["supportedEffortLevels"] is JArray levels
                        ? levels.Select(l => Str(l)?.ToLowerInvariant()).Where(l => l != null).ToList()
                        : null,
                });
            }
            // A Claude Code that lists levels lists none for a model without effort (2.1.328: Haiku 4.5, no supportsEffort)
            if (list.Any(m => m.EffortLevels != null))
                foreach (var m in list.Where(m => m.EffortLevels == null)) m.EffortLevels = new string[0];
            return list.Count == 0 ? null : new ModelCatalog(list);
        }

        private static string Str(JToken t) => t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t) ? ((string)t).Trim() : null;

        /// <summary>"claude-opus-5-5" for "claude-opus-5-5[1m]": the 1M-context suffix Claude Code adds isn't part of the API's ID.</summary>
        public static string BareId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            int b = id.IndexOf('[');
            return b > 0 ? id.Substring(0, b) : id;
        }

        /// <summary>The full ID <paramref name="alias"/> resolves to; null when not listed, or when it is a full ID already.</summary>
        public string ResolvedId(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return null;
            var m = _models.FirstOrDefault(x => string.Equals(x.Value, alias, StringComparison.OrdinalIgnoreCase));
            return m == null || string.Equals(m.Id, alias, StringComparison.OrdinalIgnoreCase) ? null : m.Id;
        }

        /// <summary>
        /// The entry for a model ID (or alias) the API or a setting names; the model's own entry ("Haiku 5.5") before an
        /// alias that resolves to it ("Default (recommended)"). Null when not listed.
        /// </summary>
        public ModelInfo Find(string model)
        {
            string id = BareId(model);
            if (id == null) return null;
            bool Same(string a) => string.Equals(a, id, StringComparison.OrdinalIgnoreCase);
            return _models.FirstOrDefault(m => Same(m.Value) && m.Value != "default")
                ?? _models.FirstOrDefault(m => Same(m.Id) && m.Value != "default")
                ?? _models.FirstOrDefault(m => Same(m.Id) || Same(m.Value));
        }

        /// <summary>"Haiku 5.5" for "claude-haiku-5-5", as Claude Code names it; null when not listed.</summary>
        public string DisplayName(string model)
        {
            var m = Find(model);
            if (m?.DisplayName == null || m.Value == "default") return null; // "Default (recommended)" names a choice, not a model
            return m.DisplayName;
        }

        /// <summary>
        /// Whether <paramref name="model"/> takes <paramref name="effort"/> ("low" … "max"); null when the catalog doesn't
        /// say (the model or its levels not listed, or no effort).
        /// </summary>
        /// <summary>The effort levels from lowest to highest.</summary>
        public static readonly string[] EffortOrder = { "low", "medium", "high", "xhigh", "max" };

        /// <summary>
        /// The effort Claude Code applies when <paramref name="model"/> is sent <paramref name="effort"/>: the effort itself
        /// when the model takes it, else the highest level it takes below that, else "" for none (2.1.328's
        /// <c>get_settings</c>: Opus 4.6 sent xhigh applies high, Haiku 4.5 sent high applies none, with no error).
        /// Null when the catalog doesn't say.
        /// </summary>
        public string EffortApplied(string model, string effort)
        {
            var ok = SupportsEffort(model, effort);
            if (ok == null) return null;
            string e = effort.Trim().ToLowerInvariant();
            if (ok == true) return e;
            var levels = Find(model).EffortLevels;
            int at = Array.IndexOf(EffortOrder, e);
            for (int i = (at < 0 ? EffortOrder.Length : at) - 1; i >= 0; i--)
                if (levels.Contains(EffortOrder[i])) return EffortOrder[i];
            return "";
        }

        public bool? SupportsEffort(string model, string effort)
        {
            if (string.IsNullOrWhiteSpace(effort)) return null;
            var levels = Find(model)?.EffortLevels;
            if (levels == null) return null;
            return levels.Contains(effort.Trim().ToLowerInvariant());
        }
    }
}
