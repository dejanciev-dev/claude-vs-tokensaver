using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeVS.Core.Cli;
using Newtonsoft.Json;

namespace ClaudeVS.Core.Tokens
{
    /// <summary>
    /// Each session's last <c>modelUsage</c> reading (cumulative tokens per model), kept in a local JSON file. After
    /// <c>--resume</c> the CLI carries <c>modelUsage</c> on from there, so a resumed conversation's first turn can take
    /// its growth too (<see cref="SubagentUsageTracker.ResumeFrom"/>). Only token counts; the latest
    /// <see cref="MaxSessions"/> sessions are kept. Every call re-reads the file. Thread-safe within one process.
    /// </summary>
    public sealed class ModelUsageReadings
    {
        public const int MaxSessions = 300;
        private static readonly object Gate = new object();

        public ModelUsageReadings(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

        public string Path { get; }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-vs-tokensaver", "model-usage.json");

        /// <summary>The session's last reading; null when none was kept.</summary>
        public Dictionary<string, TokenUsage> Get(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (Gate) return Read().TryGetValue(sessionId, out var r) ? r.Models?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) : null;
        }

        /// <summary>Keeps a turn's reading for the session (nothing when it has none).</summary>
        public void Save(string sessionId, Dictionary<string, ModelUsageEntry> modelUsage, DateTime? nowUtc = null)
        {
            if (string.IsNullOrEmpty(sessionId) || modelUsage == null || modelUsage.Count == 0) return;
            lock (Gate)
            {
                var all = Read();
                all[sessionId] = new Reading
                {
                    Utc = nowUtc ?? DateTime.UtcNow,
                    Models = modelUsage.ToDictionary(kv => kv.Key, kv => kv.Value.Usage, StringComparer.Ordinal),
                };
                foreach (var old in all.OrderByDescending(kv => kv.Value.Utc).Skip(MaxSessions).Select(kv => kv.Key).ToList()) all.Remove(old);
                Write(all);
            }
        }

        private sealed class Reading
        {
            [JsonProperty("utc")] public DateTime Utc { get; set; }
            [JsonProperty("models")] public Dictionary<string, TokenUsage> Models { get; set; }
        }

        private Dictionary<string, Reading> Read()
        {
            try
            {
                if (!File.Exists(Path)) return new Dictionary<string, Reading>();
                var d = JsonConvert.DeserializeObject<Dictionary<string, Reading>>(File.ReadAllText(Path));
                return d?.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, Reading>();
            }
            catch (JsonException) { return new Dictionary<string, Reading>(); }
        }

        private void Write(Dictionary<string, Reading> all)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }));
            if (File.Exists(Path)) File.Replace(tmp, Path, null);
            else File.Move(tmp, Path);
        }
    }
}
