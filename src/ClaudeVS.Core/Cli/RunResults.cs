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
        /// <summary>Lines under the summary (Phase 111): the failing tests' names, "+4 more" after three.</summary>
        public IReadOnlyList<string> Details = new string[0];
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
            return new RunSummary { Line = (failed > 0 ? "✗ " : "✓ ") + string.Join(" · ", parts), Failed = failed > 0, Details = failed > 0 ? Shown(FailedTests(output)) : new string[0] };
        }

        // ESLint: "✖ 5 problems (3 errors, 2 warnings)"
        private static readonly Regex EsLint = new Regex(@"^\s*✖ \d+ problems? \((?<e>\d+) errors?, (?<w>\d+) warnings?\)", RegexOptions.Compiled | RegexOptions.Multiline);
        // Ruff: "Found 3 errors." (tsc says "Found 3 errors in 2 files.", so a .py path must be in the output), "All checks passed!"
        private static readonly Regex Ruff = new Regex(@"^Found (?<n>\d+) errors?\.\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex PyPath = new Regex(@"\.pyi?:\d+:\d+", RegexOptions.Compiled);
        private static readonly Regex RuffClean = new Regex(@"^All checks passed!\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        // Pylint: "Your code has been rated at 7.50/10 (previous run: 7.00/10, +0.50)"
        private static readonly Regex Pylint = new Regex(@"^Your code has been rated at (?<r>-?[\d.]+)/10", RegexOptions.Compiled | RegexOptions.Multiline);
        // dotnet format --verify-no-changes: "C:\a\P.cs(3,1): error WHITESPACE: Fix whitespace formatting. … [C:\a\a.csproj]"
        private static readonly Regex DotnetFormat = new Regex(@"^(?<f>.+?)\(\d+,\d+\): (?:error|warning) (?:WHITESPACE|FINALNEWLINE|CHARSET|ENDOFLINE|IMPORTS)\b", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// A linter's summary (Phase 111): "✗ 3 errors · 2 warnings", "⚠ 2 warnings", "✓ No problems", "Rated 7.50/10",
        /// "✗ 4 formatting problems in 2 files"; null when the output has none.
        /// </summary>
        public static RunSummary Lint(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            string Count(int n, string w) => n == 1 ? "1 " + w : n + " " + w + "s";
            RunSummary Problems(int errors, int warnings) =>
                errors > 0 ? new RunSummary { Line = "✗ " + Count(errors, "error") + (warnings > 0 ? " · " + Count(warnings, "warning") : ""), Failed = true }
                : warnings > 0 ? new RunSummary { Line = "⚠ " + Count(warnings, "warning") }
                : new RunSummary { Line = "✓ No problems" };

            var es = EsLint.Matches(output).Cast<Match>().LastOrDefault();
            if (es != null) return Problems(N(es.Groups["e"]), N(es.Groups["w"]));
            var ruff = Ruff.Match(output);
            if (ruff.Success && PyPath.IsMatch(output)) return Problems(N(ruff.Groups["n"]), 0);
            if (RuffClean.IsMatch(output)) return Problems(0, 0);
            var pylint = Pylint.Match(output);
            if (pylint.Success) return new RunSummary { Line = "Rated " + pylint.Groups["r"].Value + "/10" };
            var format = DotnetFormat.Matches(output).Cast<Match>().ToList();
            if (format.Count > 0)
            {
                int files = format.Select(m => m.Groups["f"].Value.Trim()).Distinct(System.StringComparer.OrdinalIgnoreCase).Count();
                return new RunSummary { Line = "✗ " + Count(format.Count, "formatting problem") + " in " + Count(files, "file"), Failed = true };
            }
            return null;
        }

        // A failing test's own line, per runner (Phase 111)
        private static readonly Regex[] FailedTest =
        {
            new Regex(@"^\s+Failed (?<n>.+?) \[[^\]]*\]\s*$", RegexOptions.Compiled | RegexOptions.Multiline),          // VSTest: "  Failed AddsWrong [35 ms]"
            new Regex(@"^\s*failed (?<n>.+?) \([\d.]+m?s\)\s*$", RegexOptions.Compiled | RegexOptions.Multiline),      // Microsoft.Testing.Platform: "failed AddsWrong (35ms)"
            new Regex(@"^FAILED (?<n>\S+)", RegexOptions.Compiled | RegexOptions.Multiline),                            // pytest: "FAILED tests/test_a.py::test_x - assert …"
            new Regex(@"^\s*● (?<n>.+ › .+?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline),                     // Jest: "● Suite › name"
            new Regex(@"^\s*(?:FAIL|×)\s+(?<n>\S+ > .+?)(?: \d+ms)?\s*$", RegexOptions.Compiled | RegexOptions.Multiline), // Vitest: "FAIL  src/a.test.ts > suite > name"
            new Regex(@"^\s*--- FAIL: (?<n>\S+)", RegexOptions.Compiled | RegexOptions.Multiline),                     // go test: "--- FAIL: TestX (0.00s)"
        };

        /// <summary>The failing tests' names in a run's output, each once, in the order they first appear (Phase 111).</summary>
        public static List<string> FailedTests(string output)
        {
            var found = new List<(int At, string Name)>();
            if (string.IsNullOrEmpty(output)) return new List<string>();
            foreach (var re in FailedTest)
                foreach (Match m in re.Matches(output)) found.Add((m.Index, m.Groups["n"].Value.Trim()));
            return found.OrderBy(f => f.At).Select(f => f.Name).Where(n => n.Length > 0).Distinct().ToList();
        }

        private const int ShownTests = 3;

        private static IReadOnlyList<string> Shown(List<string> names)
        {
            if (names.Count <= ShownTests + 1) return names; // "+1 more" would take the line the name needs
            var shown = names.Take(ShownTests).ToList();
            shown.Add($"+{names.Count - ShownTests} more");
            return shown;
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
