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
        private static void Test_HaikuRouter_ParseAndRequest()
        {
            var d = HaikuRouter.Parse("{\"model\":\"sonnet\",\"effort\":\"medium\",\"reason\":\"Debugging a regression\"}");
            Eq(ModelChoice.Sonnet, d.Model);
            Eq(EffortChoice.Medium, d.Effort);
            Eq("Haiku router: Debugging a regression", d.Reason);
            Eq(EffortChoice.Medium, HaikuRouter.Parse("{\"model\":\"opus\"}").Effort, "missing effort = medium");
            Eq(null, HaikuRouter.Parse("{\"model\":\"gpt\"}"), "unknown model");
            Eq(null, HaikuRouter.Parse("not json"));
            Eq(null, HaikuRouter.Parse(""));

            string req = HaikuRouter.BuildRequest(new RouteRequest { Prompt = new string('x', 5000), AttachedFiles = 2, AttachedCodeLines = 40 });
            True(req.StartsWith("Request: 2 file(s) attached. 40 lines of code attached.\n"), req.Substring(0, 80));
            True(req.EndsWith("…(cut)") && req.Length < HaikuRouter.MaxPromptChars + 100, "prompt cut");

            string args = HaikuRouter.TrimmedArguments();
            foreach (var flag in new[] { "--system-prompt", "--tools \"\"", "--strict-mcp-config", "--disable-slash-commands", "--setting-sources \"\"", "--no-session-persistence", "--json-schema" })
                True(args.Contains(flag), "has " + flag);
        }

        private static void Test_Router_PlanModeSaysSonnet()
        {
            // Claude Code answers with Sonnet in plan mode when Haiku is asked for (2.1.316-2.1.323)
            var router = new ModelRouter();
            var simple = new RouteRequest { Prompt = "rename x to count" };
            Eq(ModelChoice.Haiku, router.Route(simple).Model, "outside plan mode: Haiku");
            simple.PlanMode = true;
            var d = router.Route(simple);
            Eq(ModelChoice.Sonnet, d.Model, "plan mode: Sonnet, what answers");
            True(d.Reason.EndsWith(ModelRouter.PlanModeNote), d.Reason);

            // The Haiku router's answer goes through the same step
            var fromHaiku = router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Haiku, Effort = EffortChoice.Low, Reason = "Haiku router: rename" }, new RouteRequest { PlanMode = true }, clearlyHard: false);
            Eq(ModelChoice.Sonnet, fromHaiku.Model, "Haiku router in plan mode");
            Eq(ModelChoice.Opus, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Opus, Reason = "r" }, new RouteRequest { PlanMode = true }, false).Model, "other models unchanged");

            // In a big Sonnet conversation it stays Sonnet without a second note
            var big = router.Route(new RouteRequest { Prompt = "rename x to count", PlanMode = true, CurrentModel = ModelChoice.Sonnet, ContextTokens = 80_000 });
            Eq(ModelChoice.Sonnet, big.Model, "big conversation");
            True(!big.Reason.Contains("kept"), "no cache note needed: " + big.Reason);

            // A manual pick
            Eq(ModelChoice.Sonnet, ModelRouter.AnsweredBy(ModelChoice.Haiku, planMode: true), "Haiku by hand in plan mode");
            Eq(null, ModelRouter.AnsweredBy(ModelChoice.Haiku, planMode: false), "not in plan mode");
            Eq(null, ModelRouter.AnsweredBy(ModelChoice.Opus, planMode: true), "Opus");
        }

        private static void Test_Router_StickinessAppliesToHaikuAnswers()
        {
            var router = new ModelRouter { StickyContextTokens = 30000 };
            var big = new RouteRequest { Prompt = "x", CurrentModel = ModelChoice.Sonnet, ContextTokens = 50000 };
            var down = router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Haiku, Effort = EffortChoice.Low, Reason = "Haiku router: rename" }, big, clearlyHard: false);
            Eq(ModelChoice.Sonnet, down.Model, "no downgrade in a big conversation");
            True(down.Reason.Contains("kept sonnet to reuse the prompt cache"), down.Reason);
            Eq(ModelChoice.Opus, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Opus, Effort = EffortChoice.High, Reason = "r" }, big, clearlyHard: true).Model, "clearly hard may upgrade");
            Eq(ModelChoice.Haiku, router.ApplyCacheStickiness(new RouteDecision { Model = ModelChoice.Haiku, Reason = "r" }, new RouteRequest { CurrentModel = ModelChoice.Sonnet, ContextTokens = 1000 }, false).Model, "small conversation: free to switch");
        }

        private static void Test_RouteText()
        {
            Eq("Auto → Sonnet, low effort", RouteText.Note(true, true, ModelChoice.Sonnet, EffortChoice.Low), "both on Auto");
            Eq("Auto → Haiku", RouteText.Note(true, false, ModelChoice.Haiku, EffortChoice.High), "only the model");
            Eq("Auto → extra high effort", RouteText.Note(false, true, ModelChoice.Opus, EffortChoice.XHigh), "only the effort");
            Eq(null, RouteText.Note(false, false, ModelChoice.Opus, EffortChoice.High), "nothing on Auto");
            Eq("Picked by the Haiku router: short question", RouteText.Tip(true, "Haiku router: short question"), "Haiku prefix dropped");
            Eq("Picked by the local router", RouteText.Tip(false, "default"), "no reason");
            Eq("Picked by the local router: mentions refactor", RouteText.Tip(false, "mentions refactor"), "local reason");
        }

        private static ModelRouter R => new ModelRouter();

        private static void Test_Router_Simple_GoesHaiku()
        {
            var d = R.Route(new RouteRequest { Prompt = "fix the typo in this comment" });
            Eq(ModelChoice.Haiku, d.Model, d.ToString());
        }

        private static void Test_Router_Normal_GoesSonnet()
        {
            var d = R.Route(new RouteRequest { Prompt = "Add an endpoint GET /api/prices/{symbol} to PricesController that returns the latest price from the repository, with a unit test." });
            Eq(ModelChoice.Sonnet, d.Model, d.ToString());
        }

        private static void Test_Router_ShortChangeRequest_NotLowEffort()
        {
            // From a real session: short, with typos, but it asks for code in several files.
            var d = R.Route(new RouteRequest { Prompt = "add more samplees and updated exitesd with a news samples" });
            Eq(ModelChoice.Sonnet, d.Model, d.ToString());
            Eq(EffortChoice.Medium, d.Effort, d.ToString());
            True(!d.Reason.Contains("short prompt"), d.ToString());
            // A short question with no change still counts as short.
            var q = R.Route(new RouteRequest { Prompt = "what does this method return?" });
            True(q.Reason.Contains("short prompt"), q.ToString());
        }

        private static void Test_Router_Hard_GoesOpus()
        {
            var d = R.Route(new RouteRequest { Prompt = "Investigate the root cause of the deadlock in the price ingestion pipeline and refactor the concurrency model across the solution." });
            Eq(ModelChoice.Opus, d.Model, d.ToString());
            Eq(EffortChoice.High, d.Effort, d.ToString());
        }

        private static void Test_Router_WholeWords_NoSubstringFalsePositives()
        {
            // "prefix"/"suffix" must not count as "fix", "airplane" not as "plan", "recreated" not as "create", "terror" not as "error".
            var d = R.Route(new RouteRequest { Prompt = "Show the prefix and suffix used by the airplane recreated terror sample" });
            True(!d.Reason.Contains("task keyword") && !d.Reason.Contains("hard keyword"), d.ToString());
        }

        private static void Test_Router_StemsAndEndings_StillMatch()
        {
            var hard = R.Route(new RouteRequest { Prompt = "We are optimizing this; investigating why the refactored code is slower" });
            True(hard.Reason.Contains("3 hard keyword"), hard.ToString());
            var med = R.Route(new RouteRequest { Prompt = "this fixes bugs and creates errors" });
            True(med.Reason.Contains("4 task keyword"), med.ToString()); // fixes, bugs, creates, errors
        }

        private static void Test_Router_Sticky_NoDowngradeInBigContext()
        {
            var d = R.Route(new RouteRequest { Prompt = "rename this variable", CurrentModel = ModelChoice.Opus, ContextTokens = 80_000 });
            Eq(ModelChoice.Opus, d.Model, d.ToString());
            True(d.Reason.Contains("cache"), d.Reason);
        }
    }
}
