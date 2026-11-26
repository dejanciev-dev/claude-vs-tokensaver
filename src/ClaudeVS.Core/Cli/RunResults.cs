using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeVS.Core.Cli
{
    /// <summary>What a command's run came to, for a tool row's ⎿ line (Phase 106).</summary>
    public sealed class RunSummary
    {
        /// <summary>"✓ 12 passed", "✗ 2 failed · 10 passed · 1 skipped", "Build failed · 2 errors".</summary>
        public string Line;
        /// <summary>Something failed: the line is drawn as a failure.</summary>
        public bool Failed;
    }

    /// <summary>
    /// Reads a test run's or a build's own summary out of a command's output (Phase 106), so the row says how it
    /// went rather than its first lines.
    /// </summary>
    public static class RunResults
    {
        private static int N(Group g) => g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : 0;

        // dotnet test (VSTest), once per test project: "Failed!  - Failed:     2, Passed:    10, Skipped:     1, Total:    13"
        private static readonly Regex VsTest = new Regex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<f>\d+),\s+Passed:\s+(?<p>\d+),\s+Skipped:\s+(?<s>\d+),\s+Total:\s+\d+", RegexOptions.Compiled);
        // dotnet test with Microsoft.Testing.Platform: "Test summary: total: 13, failed: 2, succeeded: 10, skipped: 1"
        private static readonly Regex Mtp = new Regex(@"Test summary: total: \d+, failed: (?<f>\d+), succeeded: (?<p>\d+), skipped: (?<s>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Jest: "Tests:       2 failed, 1 skipped, 10 passed, 13 total"
        private static readonly Regex Jest = new Regex(@"^Tests:\s+(?<parts>.*?\d+ total)", RegexOptions.Compiled | RegexOptions.Multiline);
        // Vitest: "      Tests  2 failed | 10 passed | 1 skipped (13)"
        private static readonly Regex Vitest = new Regex(@"^\s*Tests\s+(?<parts>\d+ \w+(?: \| \d+ \w+)*)\s+\(\d+\)", RegexOptions.Compiled | RegexOptions.Multiline);
        // pytest: "==== 2 failed, 10 passed, 1 skipped in 0.12s ===="
        private static readonly Regex Pytest = new Regex(@"^=+ (?<parts>(?:\d+ \w+(?:, )?)+) in [\d.]+s", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex Part = new Regex(@"(?<n>\d+) (?<w>failed|passed|skipped|errors?|xfailed|xpassed|todo|pending)", RegexOptions.Compiled);
        // go test: "ok  	example.com/pkg	0.012s", "FAIL	example.com/pkg	0.010s"
        private static readonly Regex GoPackage = new Regex(@"^(?<r>ok|FAIL)\s+\S+\s+(?:[\d.]+s|\(cached\))", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>A test run's summary, or null when the output has none.</summary>
        public static RunSummary Tests(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            int failed = 0, passed = 0, skipped = 0;
            bool found = false;
            foreach (var re in new[] { VsTest, Mtp })
                foreach (Match m in re.Matches(output)) { found = true; failed += N(m.Groups["f"]); passed += N(m.Groups["p"]); skipped += N(m.Groups["s"]); }
            if (!found)
            {
                var m = Jest.Match(output);
                if (!m.Success) m = Vitest.Match(output);
                if (!m.Success) m = Pytest.Matches(output).Cast<Match>().LastOrDefault() ?? Match.Empty;
                if (m.Success)
                {
                    found = true;
                    foreach (Match p in Part.Matches(m.Groups["parts"].Value))
                    {
                        string w = p.Groups["w"].Value;
                        int n = N(p.Groups["n"]);
                        if (w == "passed" || w == "xfailed" || w == "xpassed") passed += n;
                        else if (w == "failed" || w.StartsWith("error", System.StringComparison.Ordinal)) failed += n;
                        else skipped += n;
                    }
                }
            }
            if (!found)
            {
                var packages = GoPackage.Matches(output).Cast<Match>().ToList();
                if (packages.Count == 0) return null;
                int ok = packages.Count(p => p.Groups["r"].Value == "ok"), bad = packages.Count - ok;
                string Pk(int n) => n == 1 ? "1 package" : n + " packages";
                return bad > 0
                    ? new RunSummary { Line = $"✗ {Pk(bad)} failed" + (ok > 0 ? $" · {ok} passed" : ""), Failed = true }
                    : new RunSummary { Line = $"✓ {Pk(ok)} passed" };
            }
            var parts = new List<string>();
            if (failed > 0) parts.Add($"{failed} failed");
            parts.Add($"{passed} passed");
            if (skipped > 0) parts.Add($"{skipped} skipped");
            return new RunSummary { Line = (failed > 0 ? "✗ " : "✓ ") + string.Join(" · ", parts), Failed = failed > 0 };
        }

        // MSBuild's classic end: "Build succeeded." / "Build FAILED." then "    3 Warning(s)" "    2 Error(s)"
        private static readonly Regex MsBuildEnd = new Regex(@"^\s*Build (?<r>succeeded|FAILED)\.\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex MsBuildCount = new Regex(@"^\s*(?<n>\d+) (?<w>Warning|Error)\(s\)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        // dotnet build's terminal logger: "Build succeeded with 3 warning(s) in 2.1s", "Build failed with 2 error(s) and 1 warning(s) in 1.0s"
        private static readonly Regex TerminalLogger = new Regex(@"^\s*Build (?<r>succeeded|failed)(?: with (?<parts>.*?))? in [\d.]+s", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
        private static readonly Regex LoggerPart = new Regex(@"(?<n>\d+) (?<w>error|warning)\(s\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // tsc: "Found 3 errors in 2 files." / "Found 1 error."
        private static readonly Regex Tsc = new Regex(@"^Found (?<n>\d+) errors?\b", RegexOptions.Compiled | RegexOptions.Multiline);
        // Cargo: "error: could not compile `app` (bin "app") due to 2 previous errors"; "Finished `dev` profile …"
        private static readonly Regex CargoFailed = new Regex(@"^error: could not compile `[^`]+`.*?(?:due to (?<n>\d+) previous errors?)?;?\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex CargoFinished = new Regex(@"^\s*Finished `?\w+`? (?:profile|target)", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>A build's summary ("Build succeeded · 3 warnings", "Build failed · 2 errors"), or null when there is none.</summary>
        public static RunSummary Build(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            string Count(int n, string w) => n == 1 ? "1 " + w : n + " " + w + "s";
            RunSummary Make(bool ok, int errors, int warnings)
            {
                var parts = new List<string> { ok ? "Build succeeded" : "Build failed" };
                if (errors > 0) parts.Add(Count(errors, "error"));
                if (warnings > 0) parts.Add(Count(warnings, "warning"));
                return new RunSummary { Line = string.Join(" · ", parts), Failed = !ok };
            }

            var logger = TerminalLogger.Matches(output).Cast<Match>().LastOrDefault();
            if (logger != null)
            {
                int e = 0, w = 0;
                foreach (Match p in LoggerPart.Matches(logger.Groups["parts"].Value))
                    if (p.Groups["w"].Value.ToLowerInvariant() == "error") e += N(p.Groups["n"]); else w += N(p.Groups["n"]);
                return Make(logger.Groups["r"].Value.ToLowerInvariant() == "succeeded", e, w);
            }
            var end = MsBuildEnd.Matches(output).Cast<Match>().LastOrDefault();
            if (end != null)
            {
                int e = 0, w = 0;
                foreach (Match c in MsBuildCount.Matches(output, end.Index))
                    if (c.Groups["w"].Value == "Error") e = N(c.Groups["n"]); else w = N(c.Groups["n"]);
                return Make(end.Groups["r"].Value == "succeeded", e, w);
            }
            var tsc = Tsc.Match(output);
            if (tsc.Success) return Make(false, N(tsc.Groups["n"]), 0);
            var cargo = CargoFailed.Match(output);
            if (cargo.Success) return Make(false, N(cargo.Groups["n"]), 0);
            if (CargoFinished.IsMatch(output)) return Make(true, 0, 0);
            return null;
        }
    }
}
