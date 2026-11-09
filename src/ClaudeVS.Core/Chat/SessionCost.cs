using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Tokens;

namespace ClaudeVS.Core.Chat
{
    /// <summary>What a past conversation used: the sum of its API requests' usage, priced per request's model.</summary>
    public sealed class SessionCostInfo
    {
        public int Requests { get; set; }
        public TokenUsage Usage { get; set; }
        public double CostUsd { get; set; }

        /// <summary>"$0.42" for the sessions list; null under half a cent.</summary>
        public string Short() => CostUsd >= 0.005 ? "$" + CostUsd.ToString(CostUsd >= 100 ? "0" : "0.00", CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Reads what a session cost from its CLI session file and its subagents' files (&lt;id&gt;/subagents/*.jsonl).
    /// The CLI writes one line per content block, each with the message's usage, so each message id counts once.
    /// At API prices (Options → Pricing): a 1-hour cache write at 2x input, a 5-minute one at the cache-write price.
    /// Results are kept per file until it changes, since the files can be large.
    /// </summary>
    public static class SessionCost
    {
        private sealed class Entry { public DateTime LastWriteUtc; public long Size; public SessionCostInfo Info; }
        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The cost read earlier for this file, when it has not changed since; else null.</summary>
        public static SessionCostInfo Cached(string file, DateTime lastWriteUtc, long size) =>
            file != null && Cache.TryGetValue(file, out var e) && e.LastWriteUtc == lastWriteUtc && e.Size == size ? e.Info : null;

        /// <summary>Reads the cost (or takes it from the cache when the file has not changed). Never throws.</summary>
        public static SessionCostInfo Get(string file, PriceTable prices)
        {
            FileInfo f;
            try { f = new FileInfo(file); if (!f.Exists) return null; }
            catch (Exception) { return null; }
            var hit = Cached(file, f.LastWriteTimeUtc, f.Length);
            if (hit != null) return hit;
            var info = Read(file, prices);
            Cache[file] = new Entry { LastWriteUtc = f.LastWriteTimeUtc, Size = f.Length, Info = info };
            return info;
        }

        /// <summary>Reads the session file and its subagents' files; malformed lines are skipped.</summary>
        public static SessionCostInfo Read(string file, PriceTable prices)
        {
            prices = prices ?? new PriceTable();
            var info = new SessionCostInfo();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            AddFile(file, prices, info, seen);
            try
            {
                string sub = Path.Combine(Path.ChangeExtension(file, null), "subagents");
                if (Directory.Exists(sub))
                    foreach (var s in Directory.GetFiles(sub, "*.jsonl")) AddFile(s, prices, info, seen);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return info;
        }

        private static void AddFile(string file, PriceTable prices, SessionCostInfo info, System.Collections.Generic.HashSet<string> seen)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue; // cheap filter before parsing
                        var o = SessionHistory.ParseLine(line);
                        if (o == null || (string)o["type"] != "assistant") continue;
                        var msg = o["message"];
                        string model = (string)msg?["model"];
                        string id = (string)msg?["id"] ?? (string)o["requestId"] ?? (string)o["uuid"];
                        if (id != null && !seen.Add(id)) continue;
                        var u = TokenUsage.From(msg?["usage"]);
                        if (u.IsEmpty) continue; // "<synthetic>" messages (errors, interruptions) cost nothing
                        info.Requests++;
                        info.Usage += u;
                        info.CostUsd += (prices.For(model) ?? prices.Sonnet).Cost(u);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
