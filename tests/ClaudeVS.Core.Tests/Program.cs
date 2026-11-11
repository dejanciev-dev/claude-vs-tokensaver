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
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--live-all") return LiveSmoke.RunAll();
            if (args.Length > 0 && args[0].StartsWith("--live", StringComparison.Ordinal))
            {
                int code = RunOneLive(args);
                Console.WriteLine($"live cost: ${LiveSmoke.Spent:0.0000} (the CLI's total_cost_usd)");
                return code;
            }
            if (args.Length > 1 && args[0] == "--scan")
            {
                // Read-only: prints the CLAUDE.md that Generate Lean CLAUDE.md would write for that folder.
                string md = ClaudeMdGenerator.Generate(SolutionScanner.Scan(args[1]));
                Console.WriteLine(md);
                Console.WriteLine($"~{ClaudeMdGenerator.EstimateTokens(md)} tokens");
                return 0;
            }
            return RunUnitTests();
        }

        /// <summary>One `--live-…` test (see LiveSmoke); 2 = skipped or unknown switch.</summary>
        private static int RunOneLive(string[] args)
        {
            if (args.Length >= 4 && args[0] == "--live-ask")
                return LiveSmoke.RunInteractive(args[1], (PermissionModeChoice)Enum.Parse(typeof(PermissionModeChoice), args[2], true), args[3]);
            if (args.Length > 0 && args[0] == "--live-mcp") return LiveSmoke.RunMcp(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-haiku-router") return LiveSmoke.RunHaikuRouter();
            if (args.Length > 0 && args[0] == "--live-session") return LiveSmoke.RunSession();
            if (args.Length > 0 && args[0] == "--live-setup") return LiveSmoke.RunSetup(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-dialogs") return LiveSmoke.RunDialogs();
            if (args.Length > 0 && args[0] == "--live-edit-review") return LiveSmoke.RunEditReview();
            if (args.Length > 0 && args[0] == "--live-long-prompt") return LiveSmoke.RunLongPrompt();
            if (args.Length > 0 && args[0] == "--live-prices") return LiveSmoke.RunPrices();
            if (args.Length > 0 && args[0] == "--live-prompt-tokens") return LiveSmoke.RunPromptTokens();
            if (args.Length > 0 && args[0] == "--live-edit-review-interrupt") return LiveSmoke.RunEditReviewInterrupted(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--live-edit-stop-allow") return LiveSmoke.RunEditReviewInterrupted(args.Length > 1 ? args[1] : null, allowPlain: true);
            if (args.Length > 0 && args[0] == "--live")
                return LiveSmoke.Run(args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? (PermissionModeChoice)Enum.Parse(typeof(PermissionModeChoice), args[2], true) : PermissionModeChoice.AcceptEdits,
                    args.Length > 3 ? int.Parse(args[3]) : 2);
            Console.WriteLine("Unknown live test " + args[0] + "; --live-all runs them all.");
            return 2;
        }

        private static int RunUnitTests()
        {
            var tests = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test_")).ToList();
            int failed = 0;
            foreach (var t in tests)
            {
                try { t.Invoke(null, null); Console.WriteLine("PASS " + t.Name); }
                catch (TargetInvocationException ex) { failed++; Console.WriteLine("FAIL " + t.Name + ": " + ex.InnerException?.Message); }
            }
            Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
            return failed == 0 ? 0 : 1;
        }

        private static void Eq<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected <{expected}> got <{actual}>");
        }
        private static void True(bool c, string what) { if (!c) throw new Exception(what); }
    }
}
