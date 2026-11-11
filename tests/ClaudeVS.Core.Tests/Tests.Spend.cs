using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClaudeVS.Core.Chat;
using ClaudeVS.Core.Cli;
using ClaudeVS.Core.Context;
using ClaudeVS.Core.Setup;
using ClaudeVS.Core.Tokens;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Tests
{
    internal static partial class Program
    {
        private static void Test_FastMode_PricedAtTwice()
        {
            var prices = new PriceTable(); // Opus 5.5: 4 / 20; fast mode: 8 / 40
            var fast = TokenUsage.From(JObject.Parse("{\"input_tokens\":1000,\"output_tokens\":1000,\"speed\":\"fast\"}"));
            var std = TokenUsage.From(JObject.Parse("{\"input_tokens\":1000,\"output_tokens\":1000,\"speed\":\"standard\"}"));
            Eq(2000L, fast.FastTokens, "all of a fast request");
            Eq(0L, std.FastTokens, "standard");
            Eq(0.048, Math.Round(prices.Opus.Cost(fast), 6), "fast Opus: 1k in $0.008 + 1k out $0.04");
            Eq(0.024, Math.Round(prices.Opus.Cost(std), 6), "standard Opus");
            Eq(0.072, Math.Round(prices.Opus.Cost(fast + std), 6), "a turn that fell back to standard speed: each part at its price");

            // Real 2.1.322 result usage (Fable, standard): unchanged, to the CLI's own total_cost_usd
            var real = TokenUsage.From(JObject.Parse("{\"input_tokens\":2,\"cache_creation_input_tokens\":701,\"cache_read_input_tokens\":0,\"output_tokens\":5,\"cache_creation\":{\"ephemeral_1h_input_tokens\":701,\"ephemeral_5m_input_tokens\":0},\"speed\":\"standard\"}"));
            Eq(0.011432, Math.Round(prices.Fable.Cost(real), 6), "CLI total_cost_usd");

            // Cache savings double too (every price doubles)
            var cached = TokenUsage.From(JObject.Parse("{\"input_tokens\":10,\"output_tokens\":10,\"cache_read_input_tokens\":100000,\"speed\":\"fast\"}"));
            Eq(0.76, Math.Round(prices.Opus.CacheSavedUsd(cached), 6), "100k reads × (4 − 0.2) × 2");

            // Usage log: written only when fast; priced at twice
            var rec = new UsageRecord { ModelUsed = "claude-opus-5-5", InputTokens = 1000, OutputTokens = 1000 };
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(rec).Contains("fastTokens"), "not written for standard turns");
            Eq(0.024, Math.Round(prices.Opus.Cost(rec), 6), "standard record");
            rec.FastTokens = 2000;
            True(Newtonsoft.Json.JsonConvert.SerializeObject(rec).Contains("\"fastTokens\":2000"), "written for fast turns");
            Eq(0.048, Math.Round(prices.Opus.Cost(rec), 6), "fast record");

            // Footer
            True(TurnFooter.Extras(fast).Contains("fast") && !TurnFooter.Extras(fast).Contains("partly"), TurnFooter.Extras(fast));
            True(TurnFooter.Extras(fast + std).Contains("partly fast"), TurnFooter.Extras(fast + std));
            True(!TurnFooter.Extras(std).Contains("fast"), "standard: nothing");

            // Cache break: fast and standard speed have separate caches
            var w = new CacheBreakWatch();
            var t0 = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
            TokenUsage U(long read, long write, bool isFast) => new TokenUsage { Input = 5, CacheRead = read, CacheCreation = write, Output = 100, FastTokens = isFast ? 5 + read + write + 100 : 0 };
            True(w.Observe(U(0, 40_000, false), "claude-opus-5-5", "m1", t0, prices) == null, "first request");
            var b = w.Observe(U(0, 40_100, true), "claude-opus-5-5", "m2", t0.AddMinutes(1), prices);
            True(b != null && b.SpeedChanged && b.NowFast && !b.ModelChanged, "fast mode started while warm");
            True(b.Note().Contains("Fast mode started") && b.Note().Contains("separate caches"), b.Note());
            True(Math.Abs(b.ExtraUsd - 40_005 * (5 - 0.2) * 2 / 1e6) < 1e-9, "the rewrite at fast prices: " + b.ExtraUsd);
            var back = w.Observe(U(0, 40_200, false), "claude-opus-5-5", "m3", t0.AddMinutes(2), prices);
            True(back != null && back.SpeedChanged && !back.NowFast && back.Note().Contains("fell back to standard speed"), back?.Note());
        }

        private static void Test_Fable_ManualChoiceOnly()
        {
            Eq("fable", ModelChoice.Fable.ToCli(), "CLI alias");
            True(ClaudeArgsBuilder.Build(new ClaudeLaunchOptions { Model = ModelChoice.Fable }).Contains("--model fable"), "launch arg");

            // Picked by hand in a big conversation: the routers' cheaper picks keep Fable for its cache, as for any model
            var router = new ModelRouter { StickyContextTokens = 30000 };
            var big = new RouteRequest { Prompt = "x", CurrentModel = ModelChoice.Fable, ContextTokens = 50000 };
            Eq(ModelChoice.Fable, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Opus, Effort = EffortChoice.High, Reason = "r" }, big, clearlyHard: true).Model, "Opus is no upgrade from Fable");

            // The routers never pick it
            foreach (var p in new[] { "rename x", "refactor the whole architecture to fix this deadlock across services", "explain this" })
                True(new ModelRouter().Route(new RouteRequest { Prompt = p }).Model != ModelChoice.Fable, p);
            Eq(null, HaikuRouter.Parse("{\"model\":\"fable\",\"effort\":\"high\",\"reason\":\"r\"}"), "Haiku router answer 'fable' is not taken");

            // What it costs next to Opus, where you pick it
            var prices = new PriceTable();
            Eq("2× the Opus price", prices.VersusOpus("fable"), "fable");
            Eq(null, prices.VersusOpus("opus"), "opus");
            Eq(null, prices.VersusOpus("sonnet"), "cheaper");
            Eq(null, prices.VersusOpus(null), "auto");
            Eq("2.5× the Opus price", PriceTable.FromStrings(null, null, null, "10, 50, 12.5, 0.25").VersusOpus("claude-fable-5-2"), "custom price");
        }

        private static void Test_SavingsReport_HaikuRouter()
        {
            var records = new[]
            {
                new UsageRecord { Routed = true, Router = "haiku", RouteModel = "sonnet", LocalModel = "sonnet", ModelUsed = "claude-sonnet-5-5", CostUsd = 0.02, RouterCostUsd = 0.0001, RouterMs = 1500 },
                new UsageRecord { Routed = true, Router = "haiku", RouteModel = "haiku", LocalModel = "sonnet", ModelUsed = "claude-haiku-5-5", CostUsd = 0.002, RouterCostUsd = 0.0001, RouterMs = 2500 },
                new UsageRecord { Routed = true, RouteModel = "sonnet", ModelUsed = "claude-sonnet-5-5", CostUsd = 0.01, RouterCostUsd = 0.0001, RouterMs = 4000 }, // timed out: local rules
            };
            var rep = SavingsReport.Build(records, new PriceTable());
            Eq(2, rep.HaikuRouted);
            Eq(1, rep.HaikuAgreed);
            Eq(0.0003, Math.Round(rep.RouterCostUsd, 6));
            Eq(0.0323, Math.Round(rep.ActualCostUsd, 6), "router cost counted in the actual cost");
            Eq(2666L, rep.RouterAverageMs);
            string text = rep.ToText();
            True(text.Contains("Haiku router:               2 prompts, same model as the local rules 50% of the time"), text);
            Eq(0, rep.HaikuCheaper, "the second went to Haiku instead of Sonnet, but logged no tokens to price");
            Eq(0, rep.HaikuDearer, "none dearer");

            // With tokens: Sonnet instead of Haiku costs more, Haiku instead of Opus less
            var priced = new[]
            {
                new UsageRecord { Router = "haiku", RouteModel = "sonnet", LocalModel = "haiku", ModelUsed = "claude-sonnet-5-5", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Router = "haiku", RouteModel = "haiku", LocalModel = "opus", ModelUsed = "claude-haiku-5-5", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Router = "haiku", RouteModel = "haiku", LocalModel = "opus", ModelUsed = null, InputTokens = 10_000, OutputTokens = 1_000 }, // no model reported: the routed one
            };
            var p = SavingsReport.Build(priced, new PriceTable());
            Eq(1, p.HaikuDearer, "dearer");
            // Sonnet 10k×2 + 1k×10 = $0.030; Haiku 10k×0.1 + 1k×0.5 = $0.0015; Opus 10k×4 + 1k×20 = $0.060
            True(Math.Abs(p.HaikuDearerExtraUsd - 0.0285) < 1e-9, "Sonnet less Haiku: " + p.HaikuDearerExtraUsd);
            Eq(2, p.HaikuCheaper, "cheaper");
            True(Math.Abs(p.HaikuCheaperSavedUsd - 2 * 0.0585) < 1e-9, "Opus less Haiku, twice: " + p.HaikuCheaperSavedUsd);
            True(p.ToText().Contains("where they differed: 2 to a cheaper model (≈ $0.12 less), 1 to a dearer one (≈ $0.03 more), at the same tokens"), p.ToText());
            Eq(null, SavingsReport.Build(records.Take(1), new PriceTable()).HaikuDifferedLine(), "always agreed");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord { Routed = true });
            True(!json.Contains("router") && !json.Contains("localModel"), "local-only records stay as before: " + json);
        }

        private static void Test_UsageLog_RoundTripAndSummary()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cvs-usage-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "usage.jsonl");
            try
            {
                var log = new UsageLog(path);
                Eq(false, log.HasAny, "nothing yet");
                log.Append(new UsageRecord { Solution = @"C:\a", ModelUsed = "haiku", InputTokens = 100, CacheReadTokens = 900, OutputTokens = 50, CostUsd = 0.01, CharsAvoided = 4000, Routed = true, RouteScore = -2 });
                log.Append(new UsageRecord { Solution = @"C:\a", ModelUsed = "opus", InputTokens = 1000, OutputTokens = 200, CostUsd = 0.10 });
                log.Append(new UsageRecord { Solution = @"C:\b", ModelUsed = "sonnet", InputTokens = 10, CostUsd = 1 });
                File.AppendAllText(log.MonthPath(DateTime.UtcNow), "{ partial line\n");

                var all = log.ReadAll();
                Eq(3, all.Count, "malformed line skipped");
                Eq(-2.0, all[0].RouteScore, "route score round-trips");
                True(log.SizeBytes() > 0 && log.HasAny, "size");

                var a = UsageSummary.From(all, @"c:\A");
                Eq(2, a.Turns, "turns for solution a (case-insensitive)");
                True(Math.Abs(a.CostUsd - 0.11) < 1e-9, "cost " + a.CostUsd);
                Eq(1334L, a.TokensAvoided, "4,000 chars at 3 per token");
                Eq(1, a.TurnsByModel["haiku"], "by model");
                True(Math.Abs(a.CacheHitPercent - 45) < 0.01, "cache % " + a.CacheHitPercent); // 900 / 2000
                Eq(3, UsageSummary.From(all).Turns, "all solutions");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        private static void Test_UsageLog_MonthlyFilesAndKeepMonths()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cvs-months-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "usage.jsonl");
            DateTime At(int year, int month) => new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            try
            {
                var log = new UsageLog(path);
                // What older versions left: the 5 MB archive and the current file, read first
                Directory.CreateDirectory(dir);
                File.WriteAllText(log.ArchivePath, "{\"ts\":\"2026-06-01T10:00:00Z\",\"cost\":1}\n");
                File.WriteAllText(path, "{\"ts\":\"2026-08-01T10:00:00Z\",\"cost\":2}\n");
                File.SetLastWriteTime(log.ArchivePath, new DateTime(2026, 6, 1));
                File.SetLastWriteTime(path, new DateTime(2026, 9, 2));

                log.Append(new UsageRecord { Timestamp = At(2026, 9), CostUsd = 3 });
                log.Append(new UsageRecord { Timestamp = At(2026, 10), CostUsd = 4 });
                log.Append(new UsageRecord { Timestamp = At(2026, 11), CostUsd = 5 });
                Eq(Path.Combine(dir, "usage-2026-11.jsonl"), log.MonthPath(At(2026, 11)), "one file per month");
                Eq("usage-2026-09.jsonl,usage-2026-10.jsonl,usage-2026-11.jsonl", string.Join(",", log.MonthFiles().Select(Path.GetFileName)), "oldest first");
                Eq("1,2,3,4,5", string.Join(",", log.ReadAll().Select(r => r.CostUsd)), "the old two first, then the months");

                log.KeepMonths = 2; // November and October
                Eq(3, log.Prune(At(2026, 11)), "September's file, the archive (June) and the old file (last written Sep 2)");
                Eq("4,5", string.Join(",", log.ReadAll().Select(r => r.CostUsd)));

                // Appending in a new month prunes by itself
                log.Append(new UsageRecord { Timestamp = At(2026, 12), CostUsd = 6 });
                Eq("5,6", string.Join(",", log.ReadAll().Select(r => r.CostUsd)), "October went with December's first turn");

                log.KeepMonths = 0;
                Eq(0, log.Prune(At(2027, 6)), "0 keeps everything");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        private static void Test_Savings_Thinking()
        {
            var prices = new PriceTable(); // Sonnet output $10, Haiku output $0.5 per MTok
            var recs = new[]
            {
                new UsageRecord { ModelUsed = "claude-sonnet-5-5", OutputTokens = 400_000, ThinkingTokens = 300_000, CostUsd = 4 },
                new UsageRecord { ModelUsed = "claude-haiku-5-5", OutputTokens = 100_000, ThinkingTokens = 0, CostUsd = 0.1 },
            };
            var r = SavingsReport.Build(recs, prices);
            Eq(300_000L, r.ThinkingTokens);
            True(Math.Abs(r.ThinkingCostUsd - 3.0) < 1e-9, "priced at each turn's output price " + r.ThinkingCostUsd);
            True(r.ToText().Contains("Thinking:                   300k tokens ≈ $3.00  (60% of output tokens;") || r.ToText().Contains("Thinking:                   300.0k tokens ≈ $3.00  (60% of output tokens;"), r.ToText());
            True(!SavingsReport.Build(new[] { new UsageRecord { ModelUsed = "claude-haiku-5-5", OutputTokens = 10 } }, prices).ToText().Contains("Thinking:"), "no thinking logged: no line");

            // Older log lines have no "thinking"; it is written only when there is some.
            var old = Newtonsoft.Json.JsonConvert.DeserializeObject<UsageRecord>("{\"model\":\"m\",\"out\":5}");
            Eq(0L, old.ThinkingTokens, "old line");
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord()).Contains("\"thinking\""), "0 is left out");
        }

        private static void Test_Savings_PromptCache()
        {
            var prices = new PriceTable(); // Sonnet: input 2, cache write 2.5, cache read 0.1; Haiku: 0.1, 0.125, 0.01
            var recs = new[]
            {
                new UsageRecord { ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 1_000_000, CacheWriteTokens = 100_000, CacheWrite1hTokens = 100_000 }, // 1.90 - 0.20
                new UsageRecord { ModelUsed = "claude-sonnet-5-5", CacheWriteTokens = 200_000 },                                                         // older line: 5-minute, -0.10
                new UsageRecord { ModelUsed = "claude-haiku-5-5", CacheReadTokens = 1_000_000 },                                                         // 0.09
            };
            var r = SavingsReport.Build(recs, prices);
            True(Math.Abs(r.CacheSavedUsd - 1.69) < 1e-9, "1.70 - 0.10 + 0.09 = " + r.CacheSavedUsd);
            Eq(2_000_000L, r.CacheReadTokens);
            True(r.ToText().Contains("Saved by the prompt cache:  $1.69  (2.0M tokens read from the cache instead of sent in full)"), r.ToText());

            var line = Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord { CacheWriteTokens = 5, CacheWrite1hTokens = 5 });
            True(line.Contains("\"cacheWrite1h\":5"), line);
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord()).Contains("cacheWrite1h"), "0 left out");
        }

        private static void Test_Savings_Costliest()
        {
            var t = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
            var recs = Enumerable.Range(1, 7).Select(i => new UsageRecord
            {
                Timestamp = t.AddMinutes(i), ModelUsed = i == 7 ? "claude-opus-5-5" : "claude-sonnet-5-5", CostUsd = i * 0.1, InputTokens = i * 1000,
                PromptPreview = i == 7 ? "refactor the whole\nbilling module across all projects and update every test" : null,
                RouteReason = i == 6 ? "long prompt" : null,
            }).ToList();
            var r = SavingsReport.Build(recs, new PriceTable());
            Eq("0.7|0.6|0.5|0.4|0.3", string.Join("|", r.Costliest.Select(x => x.CostUsd.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))), "top five, dearest first");
            string text = r.ToText();
            True(text.Contains("Costliest turns:"), text);
            True(text.Contains("opus") && text.Contains("“refactor the whole billing module across all projects and u…”"), "prompt on one line, cut: " + text);
            True(text.Contains("(long prompt)"), "route reason when no prompt was logged");
            True(!SavingsReport.Build(recs.Take(1), new PriceTable()).ToText().Contains("Costliest turns:"), "one turn: no list");
        }

        private static void Test_Savings_RoutingAndReferences()
        {
            var prices = new PriceTable(); // Opus 4/20, Sonnet 2/10, Haiku 0.1/0.5 per MTok
            var recs = new[]
            {
                // Sonnet turn: 1M input, 0.1M output → actual $3; at Opus prices $6.
                new UsageRecord { Solution = "s", ModelUsed = "claude-sonnet-5-5", Routed = true, InputTokens = 1_000_000, OutputTokens = 100_000, CostUsd = 3, CharsAvoided = 4_000_000 },
                // Opus turn counts the same in both.
                new UsageRecord { Solution = "s", ModelUsed = "claude-opus-5-5", InputTokens = 100_000, CostUsd = 0.4 },
                new UsageRecord { Solution = "other", ModelUsed = "claude-haiku-5-5", InputTokens = 1, CostUsd = 99 },
            };
            var r = SavingsReport.Build(recs, prices, "s");
            Eq(2, r.Turns, "filtered by solution");
            True(Math.Abs(r.ActualCostUsd - 3.4) < 1e-9, "actual " + r.ActualCostUsd);
            True(Math.Abs(r.OpusEquivalentUsd - 6.4) < 1e-9, "opus eq " + r.OpusEquivalentUsd);
            True(Math.Abs(r.RoutingSavedUsd - 3.0) < 1e-9, "saved " + r.RoutingSavedUsd);
            Eq(1_333_334L, r.TokensAvoided, "tokens avoided (3 chars per token)");
            True(Math.Abs(r.ReferencesSavedUsd - 1_333_334 * 2.0 / 1e6) < 1e-9, "refs priced at sonnet input " + r.ReferencesSavedUsd);
            True(r.ToText().Contains("Saved by model routing:     $3.00  (47%)"), r.ToText());
        }

        private static void Test_Pricing_ParseAndFamily()
        {
            var p = ModelPrice.Parse("4, 20, 5, 0.2");
            Eq(0.2, p.CacheRead, "parse");
            Eq(null, ModelPrice.Parse("4, 20"), "too few");
            Eq(null, ModelPrice.Parse("a,b,c,d"), "not numbers");
            Eq("sonnet", PriceTable.Family("claude-sonnet-5-5"), "family");
            Eq(null, PriceTable.Family("gpt"), "unknown");
            Eq(4.0, PriceTable.FromStrings("bad", null, null).Opus.Input, "falls back to default");

            // Fable 5.2 (2.1.321): its own family, not priced as Sonnet
            var prices = new PriceTable();
            Eq("fable", PriceTable.Family("claude-fable-5-2"), "fable family");
            Eq("fable", PriceTable.Family("fable"), "fable alias");
            Eq(40.0, prices.For("claude-fable-5-2").Output, "fable output price");
            Eq(8.0, PriceTable.FromStrings(null, null, null).Fable.Input, "fable default");
            Eq(9.0, PriceTable.FromStrings(null, null, null, "9, 45, 11.25, 0.2").Fable.Input, "fable from options");
            var u = new TokenUsage { Input = 1_000, Output = 1_000, CacheRead = 100_000 };
            Eq(0.068, Math.Round(prices.For("claude-fable-5-2").Cost(u), 6), "fable turn: 1k in $0.008 + 1k out $0.04 + 100k read $0.02");
        }

        private static void Test_RouterReplay_FindsChangedDecisions()
        {
            var recs = new[]
            {
                new UsageRecord { Routed = true, RouteModel = "sonnet", PromptPreview = "fix the typo in this comment" }, // router now says haiku
                new UsageRecord { Routed = true, RouteModel = "haiku", PromptPreview = "fix the typo in this comment" },  // unchanged
                new UsageRecord { Routed = true, RouteModel = "opus" },                                                  // no preview: skipped
            };
            var diffs = RouterReplay.Compare(recs, R, out int replayed);
            Eq(2, replayed, "only records with preview");
            Eq(1, diffs.Count, "one changed");
            Eq("haiku", diffs[0].NowModel, "now haiku");
            Eq(200, UsageRecord.Preview(new string('x', 500)).Length, "preview capped");
            Eq(null, diffs[0].CostChangeUsd, "no prices: not priced");
            Eq("", diffs[0].CostChangeText(), "no text");

            // Priced: Sonnet 10k in + 1k out = $0.030, the same on Haiku $0.0015
            var priced = new[]
            {
                new UsageRecord { Routed = true, RouteModel = "sonnet", ModelUsed = "claude-sonnet-5-5", PromptPreview = "fix the typo in this comment", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Routed = true, RouteModel = "sonnet", PromptPreview = "fix the typo in this comment", InputTokens = 10_000, OutputTokens = 1_000 }, // no model: the routed one
            };
            var p = RouterReplay.Compare(priced, R, new PriceTable(), out _);
            Eq(2, p.Count, "both now Haiku");
            True(Math.Abs(p[0].CostChangeUsd.Value + 0.0285) < 1e-9, "cheaper: " + p[0].CostChangeUsd);
            Eq(" (≈ −$0.029)", p[0].CostChangeText());
            Eq("At the logged tokens: 2 cheaper (≈ $0.06 less), net ≈ $0.06 less.", RouterReplay.CostSummary(p));
            var mixed = new List<ReplayDiff> { new ReplayDiff { CostChangeUsd = -0.40 }, new ReplayDiff { CostChangeUsd = 0.10 }, new ReplayDiff { CostChangeUsd = null } };
            Eq("At the logged tokens: 1 cheaper (≈ $0.40 less), 1 dearer (≈ $0.10 more), net ≈ $0.30 less.", RouterReplay.CostSummary(mixed));
            Eq(" (≈ +$0.100)", mixed[1].CostChangeText());
            Eq(null, RouterReplay.CostSummary(diffs), "none priced");
        }

        private static void Test_SessionCost_FromSessionFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudevs-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "s1", "subagents"));
            string file = Path.Combine(root, "s1.jsonl");
            string Asst(string id, string model, string usage) =>
                "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model + "\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"usage\":" + usage + "}}";
            // Opus: 1M read + 1M 1-hour write + 1M output; repeated for its second content block.
            string big = "{\"input_tokens\":0,\"cache_read_input_tokens\":1000000,\"cache_creation_input_tokens\":1000000,\"cache_creation\":{\"ephemeral_1h_input_tokens\":1000000,\"ephemeral_5m_input_tokens\":0},\"output_tokens\":1000000}";
            File.WriteAllLines(file, new[]
            {
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
                Asst("m1", "claude-opus-5-5", big),
                Asst("m1", "claude-opus-5-5", big),
                Asst("m2", "<synthetic>", "{\"input_tokens\":0,\"output_tokens\":0}"),
                "not json",
                // A subagent's message (isSidechain) in the main file, should one be there: Haiku, 100k input, not over the tier: $0.01
                Asst("m4", "claude-haiku-5-5", "{\"input_tokens\":100000,\"output_tokens\":0}").Replace("{\"type\":\"assistant\",", "{\"type\":\"assistant\",\"isSidechain\":true,"),
            });
            // A subagent on Haiku: one request with 1M input, over the 100k tier: 5x (-e.50 per Mtok).
            File.WriteAllLines(Path.Combine(root, "s1", "subagents", "agent-a.jsonl"), new[] { Asst("m3", "claude-haiku-5-5", "{\"input_tokens\":1000000,\"output_tokens\":0}") });
            try
            {
                var info = SessionCost.Read(file, new PriceTable());
                Eq(3, info.Requests, "each message once; synthetic skipped; subagents counted");
                True(Math.Abs(info.CostUsd - (0.2 + 8 + 20 + 0.5 + 0.01)) < 1e-9, "opus read 0.2 + 1h write 2x4 + output 20, haiku input 0.5 (over 100k), sidechain 0.01: " + info.CostUsd);
                True(Math.Abs(info.AgentCostUsd - 0.51) < 1e-9, "the subagents: their file and the sidechain line: " + info.AgentCostUsd);
                Eq("$28.71 ($0.51 agents)", info.Short(), "short, with the agents' part");
                Eq("$0.42", new SessionCostInfo { CostUsd = 0.42, AgentCostUsd = 0.004 }.Short(), "agents under half a cent: not shown");
                Eq(null, new SessionCostInfo { CostUsd = 0.004 }.Short(), "under half a cent");
                Eq("$123", new SessionCostInfo { CostUsd = 123.4 }.Short(), "no cents from $100");

                var f = new FileInfo(file);
                Eq(null, SessionCost.Cached(file, f.LastWriteTimeUtc, f.Length), "not read through Get yet");
                var got = SessionCost.Get(file, new PriceTable());
                True(ReferenceEquals(got, SessionCost.Cached(file, f.LastWriteTimeUtc, f.Length)), "kept until the file changes");
                Eq(null, SessionCost.Cached(file, f.LastWriteTimeUtc, f.Length + 1), "a changed file is read again");

                var listed = new SessionInfo { FilePath = file, LastWriteUtc = f.LastWriteTimeUtc, SizeBytes = f.Length };
                var unread = new SessionInfo { FilePath = Path.Combine(root, "s2.jsonl"), LastWriteUtc = f.LastWriteTimeUtc, SizeBytes = 1 };
                Eq("1 session, $28.71 in total (API prices)", SessionCost.TotalLine(new[] { listed }), "footer");
                Eq("Reading what the sessions cost…", SessionCost.TotalLine(new[] { listed, unread }), "one not read yet");
                Eq(null, SessionCost.TotalLine(new SessionInfo[0]), "no sessions");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Test_SavingsReport_CacheRewrites()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                // a: the first turn writes (not a rewrite), the second reads, the third writes 50k again (1-hour) after a break
                new UsageRecord { Timestamp = t, SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheWriteTokens = 40_000, CacheWrite1hTokens = 40_000 },
                new UsageRecord { Timestamp = t.AddMinutes(5), SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 40_000, CacheWriteTokens = 2_000 },
                new UsageRecord { Timestamp = t.AddHours(3), SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 3_000, CacheWriteTokens = 50_000, CacheWrite1hTokens = 50_000 },
                // b: a 5-minute rewrite on Opus
                new UsageRecord { Timestamp = t, SessionId = "b", ModelUsed = "claude-opus-5-5", InputTokens = 10 },
                new UsageRecord { Timestamp = t.AddMinutes(1), SessionId = "b", ModelUsed = "claude-opus-5-5", CacheWriteTokens = 20_000 },
                // no session id: not judged; a small write: not counted
                new UsageRecord { Timestamp = t, CacheWriteTokens = 90_000 },
                new UsageRecord { Timestamp = t.AddMinutes(9), SessionId = "a", ModelUsed = "claude-sonnet-5-5", CacheWriteTokens = 9_000 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq(2, rep.CacheRewriteTurns, "a's third turn and b's second");
            Eq(70_000L, rep.CacheRewriteTokens, "tokens");
            // Sonnet 1-hour: 50k × (2×2 − 0.1) = $0.195; Opus 5-minute: 20k × (5 − 0.2) = $0.096
            True(Math.Abs(rep.CacheRewriteExtraUsd - 0.291) < 1e-9, "extra: " + rep.CacheRewriteExtraUsd);
            True(rep.ToText().Contains("Cache written again:        $0.29 more than reading it  (2 turns, 70.0k tokens:"), rep.ToText());
            Eq(0, SavingsReport.Build(recs.Take(2), new PriceTable()).CacheRewriteTurns, "none");
            True(!SavingsReport.Build(recs.Take(2), new PriceTable()).ToText().Contains("Cache written again"), "no line");
        }

        private static void Test_SavingsReport_ByEffort()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, Effort = "high", CostUsd = 0.30, ThinkingTokens = 12_000 },
                new UsageRecord { Timestamp = t, Effort = "high", CostUsd = 0.10, RouterCostUsd = 0.001, ThinkingTokens = 3_000 },
                new UsageRecord { Timestamp = t, Effort = "low", CostUsd = 0.02 },
                new UsageRecord { Timestamp = t, RouteEffort = "medium", CostUsd = 0.05 }, // older routed line: its route effort
                new UsageRecord { Timestamp = t, CostUsd = 0.07 },                         // Claude Code's default
                new UsageRecord { Timestamp = t, Effort = "max", CostUsd = 1.00, ThinkingTokens = 40_000 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq("low,medium,high,max,default", string.Join(",", rep.ByEffort.Select(b => b.Effort)), "low to max, then the default");
            var high = rep.ByEffort[2];
            Eq(2, high.Turns, "turns");
            True(Math.Abs(high.CostUsd - 0.401) < 1e-9, "router cost included: " + high.CostUsd);
            Eq(15_000L, high.ThinkingTokens, "thinking");
            string text = rep.ToText();
            True(text.Contains("By effort:\r\n") || text.Contains("By effort:\n"), text);
            True(text.Contains("  high        2 turns  $    0.40  ($0.201 per turn)  15.0k thinking"), text);
            True(text.Contains("  default     1 turns  $    0.07  ($0.070 per turn)\r\n") || text.Contains("  default     1 turns  $    0.07  ($0.070 per turn)\n"), text);
            Eq(0, SavingsReport.Build(new[] { recs[4] }, new PriceTable()).ByEffort.Count, "no effort known: no list");
        }

        private static void Test_SavingsReport_ToolResults()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, CostUsd = 0.1, ToolResultTokens = new Dictionary<string, long> { ["Read"] = 900_000, ["Shell"] = 50_000 } },
                new UsageRecord { Timestamp = t, CostUsd = 0.1, ToolResultTokens = new Dictionary<string, long> { ["Read"] = 300_000, ["Search"] = 120_000, ["MCP"] = 4_000, ["WebFetch"] = 3_000, ["Agent"] = 2_000, ["Edit"] = 1_000 } },
                new UsageRecord { Timestamp = t, CostUsd = 0.1 }, // an older line
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq("Read 1.2M · Search 120.0k · Shell 50.0k · MCP 4.0k · WebFetch 3.0k · other 3.0k", rep.ToolResultsLine());
            True(rep.ToText().Contains("Tool results:               Read 1.2M"), rep.ToText());
            Eq(null, SavingsReport.Build(new[] { recs[2] }, new PriceTable()).ToolResultsLine(), "none logged");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(recs[0], new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            True(json.Contains("\"tools\":{\"Read\":900000,\"Shell\":50000}"), json);
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(recs[2], new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }).Contains("tools"), "left out when none");
        }

        private static void Test_SavingsReport_Month_And_Solutions()
        {
            DateTime At(int month, int day) => new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = At(10, 30), Solution = @"C:\src\old", CostUsd = 50 },              // last month
                new UsageRecord { Timestamp = At(11, 2), Solution = @"C:\src\shop\", CostUsd = 6, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = At(11, 9), Solution = @"c:\src\SHOP", CostUsd = 2.5 },
                new UsageRecord { Timestamp = At(11, 9), Solution = null, CostUsd = 1 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11, 10));
            True(Math.Abs(rep.MonthToDateUsd - 10) < 1e-9, "November so far, router included: " + rep.MonthToDateUsd);
            True(rep.MonthProjectedUsd.HasValue && Math.Abs(rep.MonthProjectedUsd.Value - 30) < 1e-9, "$10 in 10 days → $30 in 30: " + rep.MonthProjectedUsd);
            True(rep.MonthLine().Contains("so far") && rep.MonthLine().Contains("at this pace by"), rep.MonthLine());

            Eq(3, rep.BySolution.Count, "one folder whatever its case or trailing slash, plus none");
            Eq("old", rep.BySolution[0].Name, "dearest first");
            Eq("shop", rep.BySolution[1].Name, "folder name");
            Eq(3, rep.BySolution.Sum(b => b.Turns) - 1, "all turns: 4");
            Eq("(no folder)", rep.BySolution[2].Name, "no folder");
            True(rep.ToText().Contains("By solution:"), "in the all-solutions report");
            True(!SavingsReport.Build(recs, new PriceTable(), solution: @"C:\src\old", nowUtc: At(11, 10)).ToText().Contains("By solution:"), "not in one solution's report");

            var early = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11, 2));
            True(early.MonthToDateUsd > 0 && early.MonthProjectedUsd == null, "no projection on day 2");
            True(!early.MonthLine().Contains("pace"), early.MonthLine());
            Eq(null, SavingsReport.Build(recs, new PriceTable(), nowUtc: At(12, 5)).MonthLine(), "nothing in December yet");
        }

        private static void Test_SavingsReport_CostliestConversations()
        {
            var t = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, SessionId = "a", Solution = @"C:\src\shop", CostUsd = 0.40, PromptPreview = null },
                new UsageRecord { Timestamp = t.AddMinutes(5), SessionId = "a", Solution = @"C:\src\shop", CostUsd = 0.50, PromptPreview = "fix the\ncart total", RouterCostUsd = 0.10 },
                new UsageRecord { Timestamp = t.AddMinutes(1), SessionId = "b", Solution = @"C:\src\blog", CostUsd = 0.70 },
                new UsageRecord { Timestamp = t.AddMinutes(2), CostUsd = 9 },                // older line: no session id
                new UsageRecord { Timestamp = t.AddMinutes(3), SessionId = "c", CostUsd = 0 }, // nothing spent
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq("a,b", string.Join(",", rep.CostliestConversations.Select(b => b.SessionId)), "dearest first; no id and $0 left out");
            var a = rep.CostliestConversations[0];
            Eq(2, a.Turns, "turns");
            True(Math.Abs(a.CostUsd - 1.0) < 1e-9, "router cost included: " + a.CostUsd);
            Eq(t, a.Started, "its first turn");
            Eq("fix the\ncart total", a.FirstPrompt, "the first prompt logged");
            string text = rep.ToText();
            True(text.Contains("Costliest conversations:"), text);
            True(text.Contains("$  1.00     2 turns  shop  “fix the cart total”"), text);
            True(text.Contains("$  0.70     1 turn   blog"), text);
            True(!SavingsReport.Build(recs, new PriceTable(), solution: @"C:\src\shop").ToText().Contains("Costliest conversations:"), "one conversation: no list");
            Eq("shop", SolutionBreakdown.NameOf(@"C:\src\shop\"), "folder name");
        }

        private static void Test_SavingsReport_Week()
        {
            DateTime At(int day) => new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            // Wednesday Nov 11: this week from Mon Nov 9; last week Mon Nov 2 to Wed Nov 4
            var recs = new[]
            {
                new UsageRecord { Timestamp = At(2), CostUsd = 4 },
                new UsageRecord { Timestamp = At(4), CostUsd = 6.5, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = At(5), CostUsd = 100 },  // last Thursday: after the same weekday
                new UsageRecord { Timestamp = At(8), CostUsd = 50 },   // Sunday: last week
                new UsageRecord { Timestamp = At(9), CostUsd = 10 },
                new UsageRecord { Timestamp = At(11), CostUsd = 4.2 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11));
            Eq(new DateTime(2026, 11, 9), rep.WeekStart, "Monday");
            Eq("This week:                  $14.20 since Monday (last week by the same day: $11.00, +29%)", rep.WeekLine());
            True(rep.ToText().Contains(rep.WeekLine()), "in the report");
            Eq(new DateTime(2026, 11, 9), SavingsReport.Build(recs, new PriceTable(), nowUtc: At(9)).WeekStart, "on Monday itself");
            Eq(new DateTime(2026, 11, 9), SavingsReport.Build(recs, new PriceTable(), nowUtc: At(15)).WeekStart, "on Sunday");
            Eq("This week:                  $10.00 since Monday (last week by the same day: $4.00, +150%)", SavingsReport.Build(recs, new PriceTable(), nowUtc: At(9)).WeekLine());
            Eq("This week:                  $10.00 since Monday", SavingsReport.Build(recs.Skip(4).Take(1), new PriceTable(), nowUtc: At(9)).WeekLine(), "nothing last week");
            Eq(null, SavingsReport.Build(recs.Take(2), new PriceTable(), nowUtc: At(11)).WeekLine(), "nothing this week");
        }

        private static void Test_SavingsReport_LastMonth()
        {
            DateTime At(int month, int day) => new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = At(10, 3), CostUsd = 6, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = At(10, 11), CostUsd = 2.5 },
                new UsageRecord { Timestamp = At(10, 12), CostUsd = 100 },   // after the same day: not compared
                new UsageRecord { Timestamp = At(11, 2), CostUsd = 12.33 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable(), nowUtc: At(11, 11));
            Eq(new DateTime(2026, 10, 11), rep.LastMonthToDay, "Oct 1–11");
            True(Math.Abs(rep.LastMonthToDateUsd - 9) < 1e-9, "router cost included: " + rep.LastMonthToDateUsd);
            True(rep.LastMonthText().EndsWith(" 1–11: $9.00, +37%)"), rep.LastMonthText());
            True(rep.MonthLine().EndsWith(rep.LastMonthText()) && rep.MonthLine().Contains("at this pace"), rep.MonthLine());

            var lower = SavingsReport.Build(new[] { recs[0], new UsageRecord { Timestamp = At(11, 1), CostUsd = 3.25 } }, new PriceTable(), nowUtc: At(11, 11));
            True(lower.LastMonthText().EndsWith("$6.50, −50%)"), lower.LastMonthText());
            Eq(null, SavingsReport.Build(new[] { recs[3] }, new PriceTable(), nowUtc: At(11, 11)).LastMonthText(), "nothing last month");

            // March 31 against February: its last day
            var march = SavingsReport.Build(new[] { new UsageRecord { Timestamp = At(3, 30), CostUsd = 1 } }, new PriceTable(), nowUtc: At(3, 31));
            Eq(new DateTime(2026, 2, 28), march.LastMonthToDay, "February is shorter");
            // January against December of the year before
            Eq(new DateTime(2025, 12, 15), SavingsReport.Build(recs, new PriceTable(), nowUtc: new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Local).ToUniversalTime()).LastMonthToDay, "across the year");
        }

        private static void Test_Prices_MatchTheCliCosts()
        {
            // Two real turns per model with Claude Code 2.1.322 (trimmed: no tools, a ~4.5k-token system prompt cached
            // for 1 hour); the default prices must give the CLI's own costUSD for the same tokens
            var prices = new PriceTable();
            var seen = new List<string>();
            foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-model-costs.jsonl")).Where(l => l.Trim().Length > 0))
            {
                foreach (var kv in (JObject)JObject.Parse(line)["modelUsage"])
                {
                    var m = (JObject)kv.Value;
                    var u = new TokenUsage
                    {
                        Input = (long)m["inputTokens"], Output = (long)m["outputTokens"], CacheRead = (long)m["cacheReadInputTokens"],
                        CacheCreation = (long)m["cacheCreationInputTokens"], CacheCreation1h = (long)m["cacheCreationEphemeral1hInputTokens"],
                    };
                    double ours = prices.For(kv.Key).Cost(u), cli = (double)m["costUSD"];
                    True(Math.Abs(ours - cli) < 1e-9, $"{kv.Key}: ours ${ours:0.0000000} vs the CLI's ${cli:0.0000000}");
                    seen.Add(PriceTable.Family(kv.Key));
                }
            }
            Eq("haiku,sonnet,opus,fable", string.Join(",", seen), "all four families checked");
            Eq(0.1, prices.Sonnet.CacheRead, "Sonnet 5.5 cache reads: $0.10");

            // A Sonnet price saved with the old default is upgraded; one you typed stays
            Eq(PriceTable.DefaultSonnet, PriceTable.UpgradeStored("2, 10, 2.5, 0.2"), "old default");
            Eq(PriceTable.DefaultSonnet, PriceTable.UpgradeStored("2,10,2.5,0.20"), "old default, other spacing");
            Eq("3, 15, 3.75, 0.3", PriceTable.UpgradeStored("3, 15, 3.75, 0.3"), "your own price");
            Eq("junk", PriceTable.UpgradeStored("junk"), "unreadable: left alone");
        }

        private static void Test_SubagentUsage_FromRealFork()
        {
            // Replay the fork capture (2.1.319) through the parser and the meter
            var parser = new ClaudeStreamParser();
            var events = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .SelectMany(l => parser.Parse(l)).ToList();
            var sub = events.First(e => e.Kind == ClaudeEventKind.AssistantUsage && e.IsSubagent);
            True(sub.ParentToolUseId != null && sub.ParentToolUseId.StartsWith("toolu"), "the subagent's Agent call: " + sub.ParentToolUseId);
            var done = events.Last(e => e.Kind == ClaudeEventKind.TurnCompleted);
            True(done.ModelUsage.ContainsKey("claude-haiku-5-5") && done.ModelUsage["claude-haiku-5-5"].CostUsd > 0, "modelUsage parsed");

            // First turn this meter sees: from the subagent's streamed message (input and cache exact)
            var m = new TokenMeter();
            foreach (var e in events) m.Observe(e);
            var first = m.LastTurnSubagents["claude-haiku-5-5"];
            Eq(23_917L, first.CacheRead, "subagent cache reads");
            Eq(462L, first.CacheCreation, "subagent cache writes");
            Eq(4L, first.Input, "subagent input");
            True(m.LastTurnSubagentCostUsd > 0, "priced");
            Eq(first.Total, m.SessionSubagentTokens, "conversation total");

            // The same run as a new session (not resumed): modelUsage counts from 0, so the first turn is exact, output too
            var fresh = new TokenMeter();
            fresh.FreshSession();
            foreach (var e in events) fresh.Observe(e);
            var exactFirst = fresh.LastTurnSubagents["claude-haiku-5-5"];
            Eq(131L, exactFirst.Output, "output exact (the streamed copy said 2)");
            Eq(4L, exactFirst.Input); Eq(23_917L, exactFirst.CacheRead); Eq(462L, exactFirst.CacheCreation);
            True(first.Output < exactFirst.Output, "resumed: streamed, output low");
            // Resumed, with the session's last reading kept here: the growth from it agrees with the streamed input and
            // cache, so the output is exact too (the fork run's reading before the turn was 0)
            var resumed = new TokenMeter();
            resumed.ResumedSession(new Dictionary<string, TokenUsage> { ["claude-haiku-5-5"] = default });
            foreach (var e in events) resumed.Observe(e);
            Eq(131L, resumed.LastTurnSubagents["claude-haiku-5-5"].Output, "resumed with a reading: exact");

            // A reading the session has outgrown elsewhere (the terminal): the growth takes that work too, so it does not
            // agree with the streamed messages and they count instead
            var stale = new SubagentUsageTracker();
            var main = new TokenUsage { Input = 4, Output = 200, CacheRead = 30_000 };
            stale.ResumeFrom(new Dictionary<string, TokenUsage> { ["m"] = new TokenUsage { Input = 100, Output = 1000, CacheRead = 50_000 } });
            stale.ObserveSubagent(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, MessageId = "s1", Model = "m", Usage = new TokenUsage { Input = 3, Output = 1, CacheRead = 9_000 } });
            var elsewhere = stale.TurnCompleted(new Dictionary<string, ModelUsageEntry> { ["m"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 100 + 4 + 3 + 50, Output = 1000 + 200 + 90 + 400, CacheRead = 50_000 + 30_000 + 9_000 + 20_000 } } }, "m", main);
            Eq(1L, elsewhere["m"].Output, "streamed: the reading was out of date");
            Eq(9_000L, elsewhere["m"].CacheRead);
            var current = new SubagentUsageTracker();
            current.ResumeFrom(new Dictionary<string, TokenUsage> { ["m"] = new TokenUsage { Input = 100, Output = 1000, CacheRead = 50_000 } });
            current.ObserveSubagent(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, MessageId = "s1", Model = "m", Usage = new TokenUsage { Input = 3, Output = 1, CacheRead = 9_000 } });
            var agreed = current.TurnCompleted(new Dictionary<string, ModelUsageEntry> { ["m"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 100 + 4 + 3, Output = 1000 + 200 + 90, CacheRead = 50_000 + 30_000 + 9_000 } } }, "m", main);
            Eq(90L, agreed["m"].Output, "current reading: exact output");

            // The readings file: kept per session, read back, the oldest dropped past the limit
            string readingsPath = Path.Combine(Path.GetTempPath(), "cvs-model-usage-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new ModelUsageReadings(readingsPath);
                Eq(null, store.Get("s"), "none yet");
                store.Save("s", done.ModelUsage);
                var back = store.Get("s")["claude-haiku-5-5"];
                Eq(done.ModelUsage["claude-haiku-5-5"].Usage.Output, back.Output); Eq(done.ModelUsage["claude-haiku-5-5"].Usage.CacheCreation1h, back.CacheCreation1h);
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < ModelUsageReadings.MaxSessions; i++) store.Save("x" + i, done.ModelUsage, t0.AddMinutes(i));
                Eq(null, store.Get("x0"), "the oldest dropped");
                True(store.Get("s") != null && store.Get("x1") != null, "the rest kept");
            }
            finally { try { File.Delete(readingsPath); } catch (IOException) { } }

            // task_notification's total_tokens is not the API's count: 24,649 against 24,514, so it cannot fix the output
            long reported = (long)events.Last(e => e.Kind == ClaudeEventKind.Task && (string)e.Payload["subtype"] == "task_notification").Payload["usage"]["total_tokens"];
            Eq(24_649L, reported); Eq(24_514L, exactFirst.Total);

            // A later turn: modelUsage's growth less the main agent's usage, exact (output too)
            var tr = new SubagentUsageTracker();
            var u1 = new Dictionary<string, ModelUsageEntry> { ["claude-haiku-5-5"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 4, Output = 204, CacheRead = 38_169, CacheCreation = 12_175 } } };
            Eq(0, tr.TurnCompleted(u1, "claude-haiku-5-5", new TokenUsage { Input = 4, Output = 204, CacheRead = 38_169, CacheCreation = 12_175 }).Count, "a turn without agents");
            var u2 = new Dictionary<string, ModelUsageEntry> { ["claude-haiku-5-5"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 12, Output = 539, CacheRead = 100_255, CacheCreation = 24_812 } } };
            var agents = tr.TurnCompleted(u2, "claude-haiku-5-5", new TokenUsage { Input = 4, Output = 204, CacheRead = 38_169, CacheCreation = 12_175 });
            var a = agents["claude-haiku-5-5"];
            Eq(4L, a.Input); Eq(131L, a.Output, "output exact"); Eq(23_917L, a.CacheRead); Eq(462L, a.CacheCreation);

            // A subagent on another model, and a fresh process whose counts start again
            var u3 = new Dictionary<string, ModelUsageEntry>
            {
                ["claude-haiku-5-5"] = u2["claude-haiku-5-5"],
                ["claude-sonnet-5-5"] = new ModelUsageEntry { Usage = new TokenUsage { Input = 10, Output = 50, CacheRead = 5_000 } },
            };
            var other = tr.TurnCompleted(u3, "claude-haiku-5-5", default);
            Eq(1, other.Count, "only the Sonnet agent grew");
            Eq(5_060L, other["claude-sonnet-5-5"].Total);

            // The report: Opus equivalent and cache savings take the agents too
            var prices = new PriceTable();
            var rec = new UsageRecord { ModelUsed = "claude-haiku-5-5", CacheReadTokens = 1_000_000, CostUsd = 0.01 + 0.1,
                Agents = new List<AgentTokens> { new AgentTokens { Model = "claude-sonnet-5-5", CacheReadTokens = 1_000_000 } } };
            var rep = SavingsReport.Build(new[] { rec }, prices);
            True(Math.Abs(rep.OpusEquivalentUsd - (0.2 + 0.2)) < 1e-9, "main 1M reads at Opus + agent 1M reads at Opus: " + rep.OpusEquivalentUsd);
            True(Math.Abs(rep.CacheSavedUsd - ((0.1 - 0.01) + (2.0 - 0.1))) < 1e-9, "both agents' cache savings: " + rep.CacheSavedUsd);
            Eq(2_000_000L, rep.CacheReadTokens, "reads of both");
            Eq(1, rep.AgentTurns);
            True(rep.ToText().Contains("Subagents:                  1.0M tokens ≈ $0.10  (1 turn; included above)"), rep.ToText());
            Eq(1_000_000L, rec.AgentTokensTotal);
            True(Newtonsoft.Json.JsonConvert.SerializeObject(rec).Contains("\"agents\":[{\"model\":\"claude-sonnet-5-5\""), "logged");
            True(!Newtonsoft.Json.JsonConvert.SerializeObject(new UsageRecord()).Contains("agents"), "none: not written");
            Eq(null, rep.AgentByTypeLine(), "no kinds logged: no line");

            // By kind of agent: the fork run is all Fork, its share is the whole exact agent cost
            var forkTypes = fresh.LastTurnAgentTypes;
            Eq(1, forkTypes.Count); Eq("Fork", forkTypes[0].Type); Eq(1.0, forkTypes[0].Share);
            True(Math.Abs(forkTypes[0].CostUsd - fresh.LastTurnSubagentCostUsd) < 1e-12, "the exact cost, not the streamed one");

            // Two kinds in one turn: shares from their priced requests; a type from task_started when the call had none
            var split = new AgentTypeSplit();
            Func<string, TokenUsage, double> perToken = (model, u) => u.Total; // whole numbers: exact shares
            split.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Agent", ToolUseId = "a", ToolInput = new JObject { ["subagent_type"] = "Explore" } }, perToken);
            split.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolName = "Agent", ToolUseId = "b", ToolInput = new JObject() }, perToken);
            split.Observe(new ClaudeEvent { Kind = ClaudeEventKind.Task, Payload = JObject.Parse("{\"type\":\"system\",\"subtype\":\"task_started\",\"tool_use_id\":\"b\",\"subagent_type\":\"general-purpose\"}") }, perToken);
            ClaudeEvent Req(string call, string id, long tokens) => new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, IsSubagent = true, ParentToolUseId = call, MessageId = id, Model = "claude-sonnet-5-5", Usage = new TokenUsage { CacheRead = tokens } };
            split.Observe(Req("a", "m1", 100_000), perToken);
            split.Observe(Req("a", "m1", 300_000), perToken); // the same message again: its last copy counts
            split.Observe(Req("b", "m2", 100_000), perToken);
            var kinds = split.TurnCompleted(0.80);
            Eq("Explore", kinds[0].Type, "costliest first"); Eq(0.75, kinds[0].Share); True(Math.Abs(kinds[0].CostUsd - 0.60) < 1e-12, "Explore: $0.60");
            Eq("Agent", ToolCallView.AgentLabel(null)); Eq("general-purpose", kinds[1].Type, "from task_started"); True(Math.Abs(kinds[1].CostUsd - 0.20) < 1e-12, "general-purpose: $0.20");
            Eq(null, split.TurnCompleted(0.5), "the next turn: none ran");

            // The report: each turn's agent cost at its prices, split by its shares; older turns under "not known"
            var typed = new UsageRecord { ModelUsed = "claude-haiku-5-5", CostUsd = 0.1, AgentTypes = kinds,
                Agents = new List<AgentTokens> { new AgentTokens { Model = "claude-sonnet-5-5", CacheReadTokens = 1_000_000 } } };
            var both = SavingsReport.Build(new[] { rec, typed }, prices);
            True(Math.Abs(both.AgentCostByType["Explore"] - 0.075) < 1e-12 && Math.Abs(both.AgentCostByType["general-purpose"] - 0.025) < 1e-12
                && Math.Abs(both.AgentCostByType[SavingsReport.NotKnownType] - 0.10) < 1e-12, "split at the report's prices");
            True(both.AgentByTypeLine().StartsWith("Explore $") && both.AgentByTypeLine().Contains(" · general-purpose $") && both.AgentByTypeLine().EndsWith(" · not known $" + 0.10.ToString("0.00")), both.AgentByTypeLine());
            True(both.ToText().Contains("  by kind of agent:         Explore"), both.ToText());
            // By model: each agent at its own model's price; the Opus ones also at Sonnet's
            var opusAgent = new UsageRecord { ModelUsed = "claude-haiku-5-5", CostUsd = 0.2,
                Agents = new List<AgentTokens> { new AgentTokens { Model = "claude-opus-5-5", CacheReadTokens = 1_000_000 } } };
            var byModel = SavingsReport.Build(new[] { rec, opusAgent }, prices);
            True(Math.Abs(byModel.AgentCostByModel["opus"] - 0.2) < 1e-12 && Math.Abs(byModel.AgentCostByModel["sonnet"] - 0.1) < 1e-12, "opus $0.20, sonnet $0.10");
            True(Math.Abs(byModel.OpusAgentsOnSonnetUsd - 0.1) < 1e-12, "the Opus agent's reads at Sonnet prices");
            True(byModel.AgentByModelLine().StartsWith("opus $") && byModel.AgentByModelLine().Contains(" on Sonnet) · sonnet $"), byModel.AgentByModelLine());
            True(byModel.ToText().Contains("  by model:                 opus $"), byModel.ToText());
            Eq(null, SavingsReport.Build(new[] { new UsageRecord { ModelUsed = "claude-haiku-5-5", CostUsd = 0.01 } }, prices).AgentByModelLine(), "no agents");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(typed);
            True(json.Contains("\"agentTypes\":[{\"type\":\"Explore\",\"share\":0.75}"), json);
            Eq(0.75, Newtonsoft.Json.JsonConvert.DeserializeObject<UsageRecord>(json).AgentTypes[0].Share, "read back");
        }

        private static void Test_TurnUsage_IsMainAgentOnly()
        {
            // The fork capture (2.1.319): the result's usage is the main agent's; modelUsage and the cost add the
            // subagent. So a turn's long-context share must not take subagent requests (they are not in its tokens);
            // their tier is priced where their tokens are counted: the sessions list, per request.
            var result = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.319-fork.jsonl"))
                .Select(l => l.Trim()).Where(l => l.StartsWith("{")).Select(JObject.Parse).Last(o => (string)o["type"] == "result");
            var main = TokenUsage.From(result["usage"]);
            var all = (JObject)((JObject)result["modelUsage"]).Properties().Single().Value;
            True((long)all["cacheReadInputTokens"] > main.CacheRead, $"subagent reads not in the turn's usage: {main.CacheRead} < {all["cacheReadInputTokens"]}");
            True((long)all["outputTokens"] > main.Output, "nor its output");

            var m = new TokenMeter();
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "main", Usage = new TokenUsage { CacheRead = 50_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "sub", IsSubagent = true, Usage = new TokenUsage { CacheRead = 150_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { CacheRead = 50_000, Output = 10 } });
            Eq(0L, m.LastTurn.LargeContextTokens, "a subagent request over 100k does not count in the main agent's turn");
        }

        private static void Test_Prices_HaikuOver100k()
        {
            // Real turns with 2.1.322: a 74k-token prompt then a short one (under the tier), and the same with 111k
            // (over it). Each turn is one request; the CLI's total grows by exactly what our price gives for it.
            var haiku = new PriceTable().Haiku;
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-haiku-over-100k.jsonl")).Where(l => l.Trim().Length > 0).Select(JObject.Parse).ToList();
            Eq(4, lines.Count);
            for (int i = 0; i < 4; i += 2)
            {
                double before = 0;
                for (int k = i; k < i + 2; k++)
                {
                    var u = TokenUsage.From(lines[k]["usage"]).AsRequest();
                    double cli = (double)lines[k]["total_cost_usd"] - before, ours = haiku.Cost(u);
                    True(Math.Abs(ours - cli) < 1e-9, $"turn with {u.ContextTokens:N0} input tokens: ours ${ours:0.0000000}, the CLI's ${cli:0.0000000}");
                    Eq(u.ContextTokens > 100_000, u.LargeContextTokens > 0, "over 100k: " + u.ContextTokens);
                    before = (double)lines[k]["total_cost_usd"];
                }
            }

            // Only Haiku has the tier; 100,000 exactly is still the normal price
            var big = new TokenUsage { CacheRead = 150_000, Output = 10 }.AsRequest();
            Eq(new PriceTable().Sonnet.Cost(new TokenUsage { CacheRead = 150_000, Output = 10 }), new PriceTable().Sonnet.Cost(big), "Sonnet: no tier");
            Eq(1.0, haiku.RequestMultiplier(100_000), "100k exactly: normal");
            Eq(5.0, haiku.RequestMultiplier(100_001), "above: 5x");

            // A turn's sum: only the requests over 100k count (two requests: 60k and 120k of context)
            var m = new TokenMeter();
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "a", Usage = new TokenUsage { CacheRead = 60_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "b", Usage = new TokenUsage { CacheRead = 120_000, Output = 10 } });
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.AssistantUsage, Model = "claude-haiku-5-5", MessageId = "b", Usage = new TokenUsage { CacheRead = 120_000, Output = 20 } }); // same message again
            m.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { CacheRead = 180_000, Output = 30 } });
            Eq(120_020L, m.LastTurn.LargeContextTokens, "the 120k request, its last copy");
            double turnCost = haiku.Cost(m.LastTurn);
            True(Math.Abs(turnCost - (60_000 * 0.01 + 10 * 0.5 + (120_000 * 0.01 + 20 * 0.5) * 5) / 1e6) < 1e-6, "priced near per request: " + turnCost);

            // The next prompt's price and the tip
            var cache = new CacheWindow();
            cache.Use(new TokenUsage { CacheRead = 95_000, CacheCreation = 1000, CacheCreation1h = 1000 }, DateTime.UtcNow);
            var over = NextPromptPrice.Estimate(cache, "claude-haiku-5-5", "claude-haiku-5-5", 10_000, new PriceTable(), DateTime.UtcNow);
            True(Math.Abs(over.InputUsd - (96_000 * 0.01 + 10_000 * 0.2) * 5 / 1e6) < 1e-9, "96k cached + 10k prompt passes 100k: 5x: " + over.InputUsd);
            Eq(null, SessionNotices.HaikuLongContext("claude-haiku-5-5", 89_999), "below 90k: no tip");
            True(SessionNotices.HaikuLongContext("claude-haiku-5-5", 92_000)?.Contains("5x its prices") == true, "near 100k: tip");
            Eq(null, SessionNotices.HaikuLongContext("claude-sonnet-5-5", 150_000), "not Haiku: no tip");

            // Reading a big conversation from the cache: most of a short turn's cost
            var prices = new PriceTable();
            var shortTurn = new TokenUsage { Input = 20, Output = 300, CacheRead = 150_000, CacheCreation = 400 };
            // Opus: reads $0.030, the turn 150k×0.2 + 20×4 + 300×20 + 400×5 per Mtok = $0.0381
            Eq("Reading this conversation (~150.4k tokens) back from the cache cost $0.03 of that turn's $0.04: every request sends the whole conversation again. "
                + "For a new topic, a New chat (or /clear) starts small; /compact keeps a summary of this one.",
                SessionNotices.PriceyContext("claude-opus-5-5", shortTurn, 150_400, prices));
            Eq(null, SessionNotices.PriceyContext("claude-sonnet-5-5", shortTurn, 150_400, prices), "Sonnet: $0.015 of reads, too little");
            Eq(null, SessionNotices.PriceyContext("claude-opus-5-5", shortTurn, 40_000, prices), "a small context after /compact");
            var longAnswer = new TokenUsage { Input = 20, Output = 3_000, CacheRead = 150_000, CacheCreation = 4_000 };
            Eq(null, SessionNotices.PriceyContext("claude-opus-5-5", longAnswer, 154_000, prices), "the answer itself cost most: $0.03 of $0.11");
            True(SessionNotices.PriceyContext("claude-fable-5-2", longAnswer, 154_000, prices) == null, "Fable: reads $0.03 of $0.21");
            True(SessionNotices.PriceyContext(null, new TokenUsage { CacheRead = 400_000, Output = 100 }, 100_000, prices)?.Contains("$0.04 of that turn's $0.04") == true, "unknown model: Sonnet prices; several requests in a turn");

            // A short answer with no tools on Opus / Fable picked by hand: what Haiku would have charged
            var quick = new TokenUsage { Input = 3_000, Output = 400, CacheRead = 20_000, CacheCreation = 1_000 };
            // Opus: 3000×4 + 400×20 + 20000×0.2 + 1000×5 = $0.029; Haiku: 3000×0.1 + 400×0.5 + 20000×0.01 + 1000×0.125 = $0.000825
            Eq("That short answer on Opus cost $0.03; the same tokens on Haiku cost about $0.001. For quick questions, Auto in the model picker (or Haiku) is cheaper. "
                + "A new model writes the conversation to its own cache once, so switch at the start of a task.",
                SessionNotices.OverkillModel("claude-opus-5-5", quick, 0, false, prices));
            True(SessionNotices.OverkillModel("claude-fable-5-2", quick, 0, false, prices)?.StartsWith("That short answer on Fable cost $0.05") == true, "Fable");
            Eq(null, SessionNotices.OverkillModel("claude-sonnet-5-5", quick, 0, false, prices), "Sonnet: no tip");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", quick, 1, false, prices), "a tool call: real work");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", quick, 0, true, prices), "subagents: real work");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", new TokenUsage { Input = 3_000, Output = 2_000, CacheRead = 20_000 }, 0, false, prices), "a long answer");
            Eq(null, SessionNotices.OverkillModel("claude-opus-5-5", new TokenUsage { Input = 10, Output = 200, CacheRead = 20_000 }, 0, false, prices), "too cheap to matter: $0.008");
            var toolMeter = new TokenMeter();
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t1", ToolName = "Read" });
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.ToolUse, ToolUseId = "t2", ToolName = "Read", IsSubagent = true });
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            Eq(1, toolMeter.LastTurnToolCalls, "the main agent's tool calls");
            toolMeter.Observe(new ClaudeEvent { Kind = ClaudeEventKind.TurnCompleted, Usage = new TokenUsage { Input = 10 } });
            Eq(0, toolMeter.LastTurnToolCalls, "counted per turn");

            // A long written answer to a short prompt
            var essay = new TokenUsage { Input = 300, Output = 5_500, Thinking = 300 };
            Eq("That answer ran to ~5.2k tokens ($0.05) for a short question. Saying how much you need (\"in five lines\", \"just the code\") keeps answers shorter, and so does the Concise output style (/ → Output style…).",
                SessionNotices.LongAnswer("claude-sonnet-5-5", 120, essay, 0, prices), "Sonnet: 5200 × $10 per Mtok");
            Eq(null, SessionNotices.LongAnswer("claude-sonnet-5-5", 900, essay, 0, prices), "a long prompt asked for it");
            Eq(null, SessionNotices.LongAnswer("claude-sonnet-5-5", 120, essay, 2, prices), "tool calls: edits are output too");
            Eq(null, SessionNotices.LongAnswer("claude-sonnet-5-5", 0, essay, 0, prices), "not our prompt");
            Eq(null, SessionNotices.LongAnswer("claude-haiku-5-5", 120, essay, 0, prices), "Haiku: $0.003");
            Eq(null, SessionNotices.LongAnswer("claude-opus-5-5", 120, new TokenUsage { Output = 9_000, Thinking = 6_000 }, 0, prices), "mostly thinking: 3000 written");

            // Thinking that was most of a short answer at high effort or above
            var thought = new TokenUsage { Input = 500, Output = 6_400, Thinking = 6_000, CacheRead = 20_000 };
            Eq("Claude thought for ~6.0k tokens ($0.12) to write a short answer at high effort. For questions like this, a lower effort in the effort picker (Medium, or Auto) thinks less.",
                SessionNotices.HeavyThinking("claude-opus-5-5", "high", thought, prices), "Opus: 6000 × $20 per Mtok");
            True(SessionNotices.HeavyThinking("claude-opus-5-5", "MAX", thought, prices)?.Contains("at max effort") == true && SessionNotices.HeavyThinking("claude-opus-5-5", "max", thought, prices).Contains("(High, or Auto)"), "max: try High");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", "medium", thought, prices), "medium: no tip");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", null, thought, prices), "Claude Code's default: not ours to judge");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", "high", new TokenUsage { Output = 7_000, Thinking = 6_000 }, prices), "a long answer: 1000 tokens");
            Eq(null, SessionNotices.HeavyThinking("claude-opus-5-5", "high", new TokenUsage { Output = 2_500, Thinking = 1_900 }, prices), "little thinking");
            Eq(null, SessionNotices.HeavyThinking("claude-haiku-5-5", "high", thought, prices), "Haiku: $0.003, too cheap");
            True(SessionNotices.HeavyThinking("claude-sonnet-5-5", "xhigh", thought, prices)?.Contains("($0.06)") == true, "Sonnet: 6000 × $10");

            // Subagents costing more than the main agent: the turn's cost includes them
            True(SessionNotices.SubagentsDominate(0.30, 0.50)?.Contains("model: haiku") == true, "agents $0.30 of $0.50: tip");
            Eq(null, SessionNotices.SubagentsDominate(0.20, 0.50), "agents $0.20 of $0.50: the main agent cost more");
            Eq(null, SessionNotices.SubagentsDominate(0.04, 0.045), "too little to matter");
            Eq(null, SessionNotices.SubagentsDominate(0, 1), "no agents");
            Eq(null, SessionNotices.SubagentsDominate(0.30, 0), "no cost from the CLI");

            // A costly fork, once finished: the tip, without naming a switch that is not settled
            var costlyFork = new AgentTask { TaskType = "local_agent", SubagentType = ToolCallView.ForkType, Status = AgentTaskStatus.Completed, CostUsd = 0.08 };
            string forkTip = SessionNotices.ForkCost(costlyFork);
            True(forkTip?.Contains("Explore agent") == true && forkTip.Contains("No forks"), forkTip);

            // No forks: CLAUDE_CODE_FORK_SUBAGENT=0. Captured with 2.1.322: asked for a fork, the Agent call fails and
            // Claude answers without one (with the default, the same prompt ran a fork)
            Eq("0", new ClaudeLaunchOptions { NoForks = true }.Environment()["CLAUDE_CODE_FORK_SUBAGENT"]);
            Eq(0, new ClaudeLaunchOptions().Environment().Count, "nothing set by default");
            Eq("1", new ClaudeLaunchOptions { EnableFileCheckpointing = true }.Environment()["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"]);
            var offEvents = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.322-fork-off.jsonl")).SelectMany(l => new ClaudeStreamParser().Parse(l)).ToList();
            var forkCall = offEvents.Single(e => e.Kind == ClaudeEventKind.ToolUse);
            Eq(ToolCallView.ForkType, (string)forkCall.ToolInput["subagent_type"], "Claude asked for a fork");
            var refused = offEvents.Single(e => e.Kind == ClaudeEventKind.ToolResult);
            True(refused.IsError && refused.ResultText.StartsWith("Agent type 'fork' not found"), refused.ResultText);
            True(!offEvents.Any(e => e.Kind == ClaudeEventKind.AssistantUsage && e.IsSubagent), "no subagent ran");
            True(offEvents.Last().Kind == ClaudeEventKind.TurnCompleted && !offEvents.Last().IsError, "the turn still finished");
            Eq(null, SessionNotices.ForkCost(new AgentTask { TaskType = "local_agent", SubagentType = ToolCallView.ForkType, CostUsd = 0.08 }), "still running");
            Eq(null, SessionNotices.ForkCost(new AgentTask { TaskType = "local_agent", SubagentType = ToolCallView.ForkType, Status = AgentTaskStatus.Completed, CostUsd = 0.02 }), "cheap");
            Eq(null, SessionNotices.ForkCost(new AgentTask { TaskType = "local_agent", SubagentType = "Explore", Status = AgentTaskStatus.Completed, CostUsd = 0.08 }), "not a fork");
            Eq(null, SessionNotices.ForkCost(null));
        }

        private static void Test_SavingsReport_FableAndFastExtra()
        {
            var prices = new PriceTable(); // Opus 4 / 20, Fable 8 / 40; fast mode 2x
            var t = DateTime.UtcNow.AddHours(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, ModelUsed = "claude-fable-5-2", InputTokens = 1000, OutputTokens = 1000, CostUsd = 0.048 },
                new UsageRecord { Timestamp = t, ModelUsed = "claude-opus-5-5", InputTokens = 1000, OutputTokens = 1000, FastTokens = 2000, CostUsd = 0.048 },
                new UsageRecord { Timestamp = t, ModelUsed = "claude-sonnet-5-5", InputTokens = 1000, OutputTokens = 1000, CostUsd = 0.012 },
            };
            var r = SavingsReport.Build(recs, prices);
            Eq(1, r.FableTurns);
            True(Math.Abs(r.FableExtraUsd - 0.024) < 1e-9, "Fable: $0.048 vs $0.024 on Opus: " + r.FableExtraUsd);
            Eq(1, r.FastTurns);
            True(Math.Abs(r.FastExtraUsd - 0.024) < 1e-9, "fast: $0.048 vs $0.024 at standard speed: " + r.FastExtraUsd);
            string text = r.ToText();
            True(text.Contains("Paid extra for Fable:       $0.02  (1 turn, above the same tokens on Opus)"), text);
            True(text.Contains("Paid extra for fast mode:   $0.02  (1 turn, above standard speed)"), text);

            var plain = SavingsReport.Build(recs.Skip(2), prices).ToText();
            True(!plain.Contains("Paid extra"), "no Fable or fast turns: no lines");

            // Haiku over 100k tokens: 1M cache-read tokens, all from requests over the tier: $0.05 instead of $0.01
            var big = new UsageRecord { Timestamp = t, ModelUsed = "claude-haiku-5-5", CacheReadTokens = 1_000_000, LargeContextTokens = 1_000_000, CostUsd = 0.05 };
            var small = new UsageRecord { Timestamp = t, ModelUsed = "claude-haiku-5-5", CacheReadTokens = 50_000, CostUsd = 0.0005 };
            var lr = SavingsReport.Build(new[] { big, small }, prices);
            Eq(1, lr.LongContextTurns, "only the turn over 100k");
            True(Math.Abs(lr.LongContextExtraUsd - 0.04) < 1e-9, "5x - 1x on $0.01: " + lr.LongContextExtraUsd);
            True(lr.ToText().Contains("Paid extra over 100k:       $0.04  (1 Haiku turn over 100k tokens, above Haiku's normal price; /compact keeps it under)"), lr.ToText());
            True(!SavingsReport.Build(new[] { small }, prices).ToText().Contains("over 100k"), "none over 100k: no line");
            var sonnetBig = new UsageRecord { Timestamp = t, ModelUsed = "claude-sonnet-5-5", CacheReadTokens = 1_000_000, LargeContextTokens = 1_000_000 };
            Eq(0, SavingsReport.Build(new[] { sonnetBig }, prices).LongContextTurns, "Sonnet has no such tier");
        }

        private static void Test_SavingsReportText_FullAndMarkdown()
        {
            var t = DateTime.UtcNow.AddDays(-1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = t, Solution = @"C:\src\shop", ModelUsed = "claude-sonnet-5-5", CostUsd = 0.5, Routed = true, RouteModel = "sonnet", PromptPreview = "fix the typo in this comment", InputTokens = 10_000, OutputTokens = 1_000 },
                new UsageRecord { Timestamp = t, Solution = @"C:\src\blog", ModelUsed = "claude-haiku-5-5", CostUsd = 0.1 },
            };
            string text = SavingsReportText.Full(recs, new PriceTable(), @"C:\src\shop", DateTime.UtcNow, @"C:\logs");
            True(text.StartsWith(@"Claude Code — savings report for C:\src\shop"), text);
            True(text.Contains("All solutions: 2 turns, $0.60 actual"), "the all-solutions summary for a solution's report");
            True(text.Contains("Router replay: 1 of 1 logged prompts would route differently") && text.Contains("sonnet → haiku (≈ −$0.029)"), text);
            True(text.TrimEnd().EndsWith(@"Log: C:\logs"), "the log folder last");
            True(!SavingsReportText.Full(recs, new PriceTable(), null, DateTime.UtcNow).Contains("All solutions:"), "all solutions: no second summary");
            True(SavingsReportText.Full(new UsageRecord[0], new PriceTable(), null, DateTime.UtcNow).Contains("no prompt previews logged"), "empty log");

            var now = new DateTime(2026, 11, 11, 13, 5, 0, DateTimeKind.Local).ToUniversalTime();
            Eq("# Claude Code savings report\n\nMade 2026-11-11 13:05, at API prices.\n\n```text\nline 1\n  line 2\n```\n", SavingsReportText.Markdown("line 1\r\n  line 2\r\n", now));
            True(SavingsReportText.Markdown("a ``` b\n````", now).Contains("\n`````text\n"), "fenced past the longest backtick run");
        }

        private static void Test_SavingsReportText_Period()
        {
            DateTime At(int month, int day) => new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var now = At(11, 11);
            var recs = new[]
            {
                new UsageRecord { Timestamp = At(9, 20), CostUsd = 7 },
                new UsageRecord { Timestamp = At(10, 5), CostUsd = 2 },
                new UsageRecord { Timestamp = At(10, 20), CostUsd = 3, Routed = true, RouteModel = "sonnet", ModelUsed = "claude-sonnet-5-5", PromptPreview = "fix the typo in this comment", InputTokens = 10_000 },
                new UsageRecord { Timestamp = At(11, 2), CostUsd = 4 },
            };
            var (from, until, name) = SavingsReportText.Range(ReportPeriod.LastMonth, now);
            Eq(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), from, "from Oct 1");
            Eq(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), until, "until Nov 1");
            Eq("October 2026", name);
            Eq("November 2026", SavingsReportText.Range(ReportPeriod.ThisMonth, now).Name);
            Eq(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), SavingsReportText.Range(ReportPeriod.Last30Days, now).FromUtc, "30 days, today included");
            Eq(null, SavingsReportText.Range(ReportPeriod.All, now).Name, "everything");

            string last = SavingsReportText.Full(recs, new PriceTable(), null, now, period: ReportPeriod.LastMonth);
            True(last.StartsWith("Claude Code — savings report (all solutions), October 2026"), last);
            True(last.Contains("to 2026-10-31 · 2 turns") && last.Contains("Actual cost:                $5.00"), last);
            True(!last.Contains("This month:") && !last.Contains("This week:"), "no 'so far' lines for a month that ended");
            True(last.Contains("Router replay: 1 of 1"), "the replay over the same turns");

            string month = SavingsReportText.Full(recs, new PriceTable(), null, now, period: ReportPeriod.ThisMonth);
            True(month.Contains("Actual cost:                $4.00") && month.Contains("This month:                 $4.00 so far"), month);
            True(month.Contains("(Oct 1–11: $2.00") || month.Contains("1–11: $2.00"), "last month still compared: " + month);
            True(month.Contains("Router replay: no prompt previews logged"), "October's preview is outside");

            True(SavingsReportText.Full(recs, new PriceTable(), null, At(12, 25), period: ReportPeriod.ThisMonth).Contains("No turns in this period."), "an empty period");
            True(SavingsReportText.Full(recs, new PriceTable(), null, now).Contains("Actual cost:                $16.00"), "all");
        }

        private static void Test_UsageCsv()
        {
            var t = new DateTime(2026, 11, 9, 9, 30, 0, DateTimeKind.Local).ToUniversalTime();
            var recs = new[]
            {
                new UsageRecord { Timestamp = t.AddMinutes(5), Solution = @"C:\src\b", ModelUsed = "claude-haiku-5-5", CostUsd = 0.0012345, PromptPreview = "=SUM(A1) say \"hi\", ok" },
                new UsageRecord { Timestamp = t, Solution = @"C:\src\a\", ModelUsed = "claude-opus-5-5", Routed = true, InputTokens = 5, OutputTokens = 1200, ThinkingTokens = 300,
                    CacheReadTokens = 9000, CacheWriteTokens = 100, CacheWrite1hTokens = 100, FastTokens = 10405, CostUsd = 0.25, RouterCostUsd = 0.0001, DurationMs = 4200, CharsAvoided = 400, RouteReason = "multi-file" },
            };
            var sw = new StringWriter();
            Eq(2, UsageCsv.Write(recs, sw), "rows");
            var lines = sw.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            Eq(string.Join(",", UsageCsv.Columns), lines[0], "header");
            Eq(t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + @",C:\src\a\,claude-opus-5-5,1,,,,5,1200,300,9000,100,100,10405,0,0,,0.25,0.0001,4200,0,134,multi-file,,,,", lines[1], "oldest first, invariant numbers; fast_tokens, large_context_tokens, agent_tokens and agent_types after the cache columns");
            True(lines[2].EndsWith(",0,,0.001235,0,0,0,0,,\"'=SUM(A1) say \"\"hi\"\", ok\",,,"), "quoted, formula-guarded prompt: " + lines[2]);
            Eq("", lines[3], "ends with a line break");
            Eq("session,effort,tools", string.Join(",", UsageCsv.Columns.Skip(UsageCsv.Columns.Length - 3)), "session id, effort and tools last");
            var withSession = new StringWriter();
            UsageCsv.Write(new[] { new UsageRecord { Timestamp = t, SessionId = "abc-123", Effort = "high", ToolResultTokens = new Dictionary<string, long> { ["Shell"] = 400, ["Read"] = 1200, ["Edit"] = 0 } } }, withSession);
            True(withSession.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[1].EndsWith(",,abc-123,high,Read:1200;Shell:400"), "after the prompt: " + withSession);

            var one = new StringWriter();
            Eq(1, UsageCsv.Write(recs, one, solution: @"c:\SRC\a"), "one solution, any case or trailing slash");
            Eq("Explore:0.75;general-purpose:0.25", UsageCsv.AgentTypes(new List<AgentTypeCost> { new AgentTypeCost { Type = "Explore", Share = 0.75 }, new AgentTypeCost { Type = "general-purpose", Share = 0.25 } }), "agent_types");
            Eq("Fork:0.3333", UsageCsv.AgentTypes(new List<AgentTypeCost> { new AgentTypeCost { Type = "Fork", Share = 1.0 / 3 } }), "4 decimals");
            Eq("", UsageCsv.AgentTypes(null), "none");
            Eq("plain", UsageCsv.Text("plain"), "no quotes needed");
            Eq("\" padded\"", UsageCsv.Text(" padded"), "leading space kept by quoting");
            Eq("'-1", UsageCsv.Text("-1"), "minus guarded");
        }

        private static void Test_SavingsReport_ByDay()
        {
            var d1 = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var d2 = d1.AddDays(1);
            var recs = new[]
            {
                new UsageRecord { Timestamp = d1, ModelUsed = "claude-sonnet-5-5", CostUsd = 1, InputTokens = 100, CacheReadTokens = 900 },
                new UsageRecord { Timestamp = d1.AddHours(1), ModelUsed = "claude-sonnet-5-5", CostUsd = 1, RouterCostUsd = 0.5, OutputTokens = 1000 },
                new UsageRecord { Timestamp = d2, ModelUsed = "claude-sonnet-5-5", CostUsd = 5, CacheReadTokens = 1_000_000 },
            };
            var rep = SavingsReport.Build(recs, new PriceTable());
            Eq(2, rep.ByDay.Count, "two days");
            Eq(2, rep.ByDay[0].Turns, "turns on day 1");
            Eq(2000L, rep.ByDay[0].Tokens, "tokens on day 1");
            True(Math.Abs(rep.ByDay[0].CostUsd - 2.5) < 1e-9, "router cost included: " + rep.ByDay[0].CostUsd);
            Eq(90.0, rep.ByDay[0].CacheHitPercent, "900 of 1000 input from the cache");
            True(Math.Abs(rep.ByDay[1].CacheSavedUsd - 1.9) < 1e-9, "1M read on Sonnet saves $1.90: " + rep.ByDay[1].CacheSavedUsd);

            string text = rep.ToText();
            var lines = text.Replace("\r", "").Split('\n');
            True(lines.Contains("By day:"), text);
            string day2 = lines.Single(l => l.StartsWith("  " + d2.ToLocalTime().ToString("yyyy-MM-dd")) && l.Contains(" turns "));
            True(day2.EndsWith(new string('█', 20)), "the dearest day has the full bar: " + day2);
            True(day2.Contains("1 turns") && day2.Contains("cache 100%"), day2);
            string day1 = lines.Single(l => l.StartsWith("  " + d1.ToLocalTime().ToString("yyyy-MM-dd")) && l.Contains(" turns "));
            True(day1.EndsWith(" " + new string('█', 10)), "half the cost, half the bar: " + day1);
            Eq(1, SavingsReport.Build(recs.Take(1), new PriceTable()).ByDay.Count, "one day");
            True(!SavingsReport.Build(recs.Take(1), new PriceTable()).ToText().Contains("By day"), "no table for a single day");
        }

        private static void Test_DailySpendWatch()
        {
            // Local noon, so "today" is the same day in any time zone's local view of these instants.
            var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var w = new SpendWatch(SpendPeriod.Day, 20, start);
            w.Load(new[]
            {
                new UsageRecord { Timestamp = start.AddHours(-1), CostUsd = 21, RouterCostUsd = 0.5 },
                new UsageRecord { Timestamp = start.AddDays(-1), CostUsd = 100 },          // yesterday
                new UsageRecord { Timestamp = start.AddMinutes(1), CostUsd = 3 },           // added by Add already
            });
            True(Math.Abs(w.Spent - 21.5) < 1e-9, "today's turns before the watch, with router cost: " + w.Spent);
            Eq(null, w.Add(1, start.AddMinutes(1)), "$20 was passed before loading: no warning again");
            string warn = w.Add(18, start.AddMinutes(2));
            True(warn != null && warn.Contains("passed $40.00") && warn.Contains("($40.50 so far"), warn);
            Eq(null, w.Add(1, start.AddMinutes(3)), "once per step");
            Eq(null, w.Add(double.NaN, start.AddMinutes(4)), "NaN adds nothing");

            Eq(null, w.Add(5, start.AddDays(1)), "a new day starts from 0");
            True(Math.Abs(w.Spent - 5) < 1e-9, "new day: " + w.Spent);
            True(w.Add(15, start.AddDays(1).AddMinutes(1))?.Contains("passed $20.00") == true, "first step of the new day");

            var off = new SpendWatch(SpendPeriod.Day, 0, start);
            Eq(null, off.Add(1000, start), "0 turns it off");
        }

        private static void Test_SolutionSpendWatch()
        {
            var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var w = new SpendWatch(SpendPeriod.Month, 50, start, solution: @"C:\src\shop");
            w.Load(new[]
            {
                new UsageRecord { Timestamp = start.AddDays(-2), Solution = @"c:\SRC\shop\", CostUsd = 30 },  // any case, trailing slash
                new UsageRecord { Timestamp = start.AddDays(-2), Solution = @"C:\src\blog", CostUsd = 500 },  // another solution
                new UsageRecord { Timestamp = start.AddDays(-1), CostUsd = 500 },                              // no folder
            });
            True(Math.Abs(w.Spent - 30) < 1e-9, "only the shop's: " + w.Spent);
            Eq("This month's Claude Code spend on shop has passed $50.00 ($52.00 so far in its conversations, at API prices). Tools → Claude Code → Show Savings Report lists the costliest turns.",
                w.Add(22, start.AddMinutes(1)));
            Eq(null, w.Add(1, start.AddMinutes(2)), "once per step");
        }

        private static void Test_SpendHistory()
        {
            DateTime At(int day, int hour = 12) => new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var h = new SpendHistory(At(11, 9)); // made on Wednesday Nov 11
            Eq(null, h.LastDaysLine(At(11)), "nothing spent");
            h.Load(new[]
            {
                new UsageRecord { Timestamp = At(5), CostUsd = 3.1 },                       // Thursday
                new UsageRecord { Timestamp = At(9), CostUsd = 1, RouterCostUsd = 0.2 },    // Monday
                new UsageRecord { Timestamp = At(11, 8), CostUsd = 0.5 },                   // today, before the history
                new UsageRecord { Timestamp = At(11, 10), CostUsd = 99 },                   // after: added by Add already
                new UsageRecord { Timestamp = At(4), CostUsd = 50 },                        // 7 days ago: not shown
            });
            h.Add(1.5, At(11, 10));
            h.Add(double.NaN, At(11, 11));
            Eq("Last 7 days: Thu $3.10 · Fri – · Sat – · Sun – · Mon $1.20 · Tue – · today $2.00 ($6.30 in all)", h.LastDaysLine(At(11, 13)));
            True(h.LastDaysLine(At(12)).EndsWith("Wed $2.00 · today – ($3.20 in all)"), "the next day, Thursday out of the window: " + h.LastDaysLine(At(12)));
            True(Math.Abs(h.On(new DateTime(2026, 11, 4)) - 50) < 1e-9, "kept, just not shown");
        }

        private static void Test_SpendLimit()
        {
            var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var l = new SpendLimit(50);
            Eq(null, l.Ask(49.99, now), "below the limit");
            string q = l.Ask(52.3, now);
            Eq("Today's Claude Code spend in Visual Studio is $52.30, past your daily limit of $50.00 (at API prices; Tools → Options → Claude Code → Ask before sending past daily cost).\n\n"
                + "Send this prompt anyway? You won't be asked again today until $100.00.", q);
            Eq(q, l.Ask(52.3, now.AddMinutes(1)), "asked again until you say yes");
            l.Allow(52.3, now);
            Eq(null, l.Ask(80, now.AddMinutes(2)), "allowed up to the next multiple");
            True(l.Ask(101, now.AddMinutes(3))?.Contains("until $150.00") == true, "past $100: asked again");
            l.Allow(101, now.AddMinutes(3));
            True(l.Ask(60, now.AddDays(1)) != null, "a new day: asked again");
            Eq(null, new SpendLimit(0).Ask(1000, now), "0: never asks");
            Eq(null, l.Ask(double.NaN, now), "no spend known");
        }

        private static void Test_MonthlySpendWatch()
        {
            // Local noon on the 9th: the 1st and the 30th of the month before are in other months in any time zone.
            var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var w = new SpendWatch(SpendPeriod.Month, 200, start);
            w.Load(new[]
            {
                new UsageRecord { Timestamp = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), CostUsd = 150 },
                new UsageRecord { Timestamp = start.AddDays(-1), CostUsd = 60, RouterCostUsd = 0.25 },
                new UsageRecord { Timestamp = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), CostUsd = 500 }, // last month
            });
            True(Math.Abs(w.Spent - 210.25) < 1e-9, "this month's turns before the watch: " + w.Spent);
            Eq(null, w.Add(5, start.AddMinutes(1)), "$200 was passed before loading");
            string warn = w.Add(190, start.AddDays(3));
            True(warn != null && warn.StartsWith("This month's Claude Code spend") && warn.Contains("passed $400.00") && warn.Contains("($405.25 so far"), warn);
            Eq(null, w.Add(1, start.AddDays(4)), "once per step");
            var december = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            Eq(null, w.Add(10, december), "a new month starts from 0");
            True(Math.Abs(w.Spent - 10) < 1e-9, "new month: " + w.Spent);
            True(w.Add(195, december.AddDays(1))?.Contains("passed $200.00") == true, "first step of the new month");

            True(new SpendWatch(SpendPeriod.Day, 20, start).Add(25, start)?.StartsWith("Today's") == true, "the daily text");

            // The meter's tooltip line
            var d = new SpendWatch(SpendPeriod.Day, 20, start);
            var m = new SpendWatch(SpendPeriod.Month, 200, start);
            Eq(null, SpendWatch.BudgetLine(d, m, start), "nothing spent yet");
            m.Load(new[] { new UsageRecord { Timestamp = start.AddDays(-2), CostUsd = 38 } });
            d.Add(3.2, start.AddMinutes(1)); m.Add(3.2, start.AddMinutes(1));
            Eq("Spent in Visual Studio: today $3.20 (next warning at $20.00) · this month $41.20 (next warning at $200.00), at API prices.", SpendWatch.BudgetLine(d, m, start.AddMinutes(2)));
            d.Add(20, start.AddMinutes(3));
            Eq("today $23.20 (next warning at $40.00)", d.Line(start.AddMinutes(3)), "past a step: the next one");
            Eq("today $0.00 (next warning at $20.00)", d.Line(start.AddDays(1)), "a new day before its first turn");
            True(d.IsPastWarning(start.AddMinutes(3)), "$23.20 past $20: amber meter");
            True(!m.IsPastWarning(start.AddMinutes(3)), "$61.20 of $200 this month");
            True(!d.IsPastWarning(start.AddDays(1)), "a new day");
            d.StepUsd = 0;
            Eq("today $23.20", d.Line(start.AddMinutes(3)), "warning off");
            True(!d.IsPastWarning(start.AddMinutes(3)), "warning off: never amber");
        }

        private static void Test_CostWatch()
        {
            var w = new CostWatch(5);
            Eq(null, w.Check(4.99), "below the step");
            Eq("This conversation has passed $5.00 ($5.12 so far at API prices). A New chat for the next task, or /compact, keeps the next prompts cheaper.", w.Check(5.12), "first step");
            Eq(null, w.Check(7.80), "same step: said already");
            True(w.Check(11.02)?.StartsWith("This conversation has passed $10.00 ($11.02") == true, "next step");
            Eq(null, w.Check(14.00), "still the second step");
            w.Reset();
            True(w.Check(5.00) != null, "a new conversation starts over");
            Eq(null, new CostWatch(0).Check(100), "0 turns it off");
            Eq(null, new CostWatch(5).Check(double.NaN), "no cost known");
        }
    }
}
