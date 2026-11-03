using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tokens
{
    public sealed class UsageWindow
    {
        /// <summary>"five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", …</summary>
        public string Name { get; set; }
        /// <summary>0..1</summary>
        public double Utilization { get; set; }
        public DateTime? ResetsAtUtc { get; set; }
        /// <summary>"allowed", "allowed_warning" or "rejected".</summary>
        public string Status { get; set; }
    }

    public enum LimitLevel { None, Warning, Reached }

    /// <summary>
    /// Plan usage limits from the CLI's <c>rate_limit_event</c> (2.1.317): the limit that matters now
    /// (<see cref="RateLimitType"/>, <see cref="Status"/>, reset time) and every window's utilization.
    /// Only sent for claude.ai plans (Pro, Max); API-key users never get one.
    /// </summary>
    public sealed class RateLimitInfo
    {
        public string Status { get; set; }
        public string RateLimitType { get; set; }
        public DateTime? ResetsAtUtc { get; set; }
        public bool IsUsingOverage { get; set; }
        public List<UsageWindow> Windows { get; } = new List<UsageWindow>();

        /// <summary>A window this full shows the banner even before the CLI warns.</summary>
        public const double WarnAt = 0.8;

        public static RateLimitInfo Parse(JObject info)
        {
            var r = new RateLimitInfo
            {
                Status = (string)info?["status"],
                RateLimitType = (string)info?["rateLimitType"],
                ResetsAtUtc = Epoch(info?["resetsAt"]),
                IsUsingOverage = info?["isUsingOverage"]?.Type == JTokenType.Boolean && (bool)info["isUsingOverage"],
            };
            if (info?["unifiedWindows"] is JObject windows)
                foreach (var p in windows.Properties())
                    if (p.Value is JObject w)
                        r.Windows.Add(new UsageWindow
                        {
                            Name = p.Name,
                            Utilization = w["utilization"]?.Type == JTokenType.Float || w["utilization"]?.Type == JTokenType.Integer ? (double)w["utilization"] : 0,
                            ResetsAtUtc = Epoch(w["resetsAt"]),
                            Status = (string)w["status"],
                        });
            return r;
        }

        /// <summary>"5-hour limit", "Weekly limit", "Weekly Opus limit"… (VS Code's wording).</summary>
        public static string DisplayName(string window)
        {
            switch (window)
            {
                case "five_hour": return "5-hour limit";
                case "seven_day": return "Weekly limit";
                case "seven_day_opus": return "Weekly Opus limit";
                case "seven_day_sonnet": return "Weekly Sonnet limit";
                case "seven_day_overage_included":
                case "overage": return "Extra usage";
                default: return string.IsNullOrEmpty(window) ? "Usage limit" : window.Replace('_', ' ');
            }
        }

        /// <summary>
        /// The banner to show, or (None, null): the limit was reached, the CLI warns, or a window is at least 80% used.
        /// The window that is fullest (or the one the CLI names) is shown, with its reset time in local time.
        /// </summary>
        public (LimitLevel Level, string Text) Banner(DateTime nowUtc)
        {
            var named = Windows.FirstOrDefault(w => w.Name == RateLimitType);
            var fullest = Windows.OrderByDescending(w => w.Utilization).FirstOrDefault();

            if (Status == "rejected" || Windows.Any(w => w.Status == "rejected"))
            {
                var w = Windows.FirstOrDefault(x => x.Status == "rejected") ?? named;
                string name = DisplayName(w?.Name ?? RateLimitType);
                string text = $"{name} reached." + Resets(w?.ResetsAtUtc ?? ResetsAtUtc, nowUtc);
                if (IsUsingOverage) text += " Using extra usage.";
                return (LimitLevel.Reached, text);
            }

            var warn = Status == "allowed_warning" ? (named ?? fullest) : Windows.Where(w => w.Utilization >= WarnAt || w.Status == "allowed_warning").OrderByDescending(w => w.Utilization).FirstOrDefault();
            if (warn == null && Status != "allowed_warning") return (LimitLevel.None, null);
            string label = DisplayName(warn?.Name ?? RateLimitType);
            string used = warn != null && warn.Utilization > 0 ? $" {Math.Round(warn.Utilization * 100):0}% used." : " almost used up.";
            return (LimitLevel.Warning, label + used + Resets(warn?.ResetsAtUtc ?? ResetsAtUtc, nowUtc));
        }

        /// <summary>One line per window for a tooltip: "5-hour limit: 19% · resets at 15:00".</summary>
        public string Details(DateTime nowUtc) =>
            string.Join("\n", Windows.Select(w =>
                $"{DisplayName(w.Name)}: {Math.Round(w.Utilization * 100):0}%" + (w.ResetsAtUtc == null ? "" : " · resets at " + When(w.ResetsAtUtc.Value, nowUtc))));

        private static string Resets(DateTime? resetsUtc, DateTime nowUtc) =>
            resetsUtc == null ? "" : $" Resets at {When(resetsUtc.Value, nowUtc)}.";

        /// <summary>"3:10 PM" today, "tomorrow 3:10 PM", else "Fri 3:10 PM" (local time).</summary>
        public static string When(DateTime utc, DateTime nowUtc)
        {
            var local = utc.ToLocalTime();
            var today = nowUtc.ToLocalTime().Date;
            return local.Date == today ? local.ToString("t")
                 : local.Date == today.AddDays(1) ? "tomorrow " + local.ToString("t")
                 : local.ToString("ddd") + " " + local.ToString("t");
        }

        private static DateTime? Epoch(JToken t) =>
            t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && (double)t > 0
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double)t)
                : (DateTime?)null;
    }
}
