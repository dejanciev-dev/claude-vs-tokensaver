using System;
using System.Globalization;
using ClaudeVS.Core.Tokens;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>
    /// When a stored claude.ai sign-in ends. The CLI puts it in every <c>result</c> message as
    /// <c>credential_expires_at</c> (2.1.318, e.g. "2026-11-21T13:48:10.889Z"); API-key users get none.
    /// The terminal warns from 10 days out, but <c>-p</c> only prints that to stderr in text mode, so we say it ourselves.
    /// </summary>
    public static class SignInExpiry
    {
        /// <summary>The terminal's threshold.</summary>
        public const int WarnDays = 10;

        /// <summary>What renews the sign-in (opens the browser).</summary>
        public const string LoginCommand = "claude auth login";

        /// <summary>The expiry time in UTC, or null when the field is missing, null or unreadable.</summary>
        public static DateTime? Parse(JToken value)
        {
            if (value == null) return null;
            if (value.Type == JTokenType.Date) return ((DateTime)value).ToUniversalTime();
            if (value.Type != JTokenType.String) return null;
            return DateTime.TryParse((string)value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null;
        }

        /// <summary>
        /// The notice to show, or (None, null) when the sign-in ends more than <see cref="WarnDays"/> days from now.
        /// Warning while it is still valid, Reached once it has ended. Times in local time.
        /// </summary>
        public static (LimitLevel Level, string Text) Notice(DateTime? expiresUtc, DateTime nowUtc)
        {
            if (expiresUtc == null) return (LimitLevel.None, null);
            var left = expiresUtc.Value - nowUtc;
            if (left <= TimeSpan.Zero) return (LimitLevel.Reached, "Your Claude sign-in has expired.");
            if (left > TimeSpan.FromDays(WarnDays)) return (LimitLevel.None, null);

            var local = expiresUtc.Value.ToLocalTime();
            var today = nowUtc.ToLocalTime().Date;
            string when;
            if (local.Date == today) when = "today at " + local.ToString("t", CultureInfo.CurrentCulture);
            else if (local.Date == today.AddDays(1)) when = "tomorrow at " + local.ToString("t", CultureInfo.CurrentCulture);
            else
            {
                int days = (local.Date - today).Days;
                when = $"in {days} days ({local.ToString("MMM d", CultureInfo.CurrentCulture)})";
            }
            return (LimitLevel.Warning, $"Your Claude sign-in ends {when}.");
        }

        /// <summary>
        /// True for the CLI's error when a stored claude.ai login has reached its time limit (2.1.318 names
        /// <c>claude auth login</c> and <c>claude setup-token</c> in <c>-p</c>).
        /// </summary>
        public static bool IsExpiredError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            string e = error.ToLowerInvariant();
            if (e.Contains(LoginCommand) || e.Contains("claude setup-token")) return true;
            return e.Contains("expired") && (e.Contains("login") || e.Contains("sign-in") || e.Contains("signed in") || e.Contains("oauth"));
        }
    }
}
