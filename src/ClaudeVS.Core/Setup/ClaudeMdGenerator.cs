using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeVS.Core.Setup
{
    /// <summary>
    /// Generates a deliberately short CLAUDE.md. CLAUDE.md is loaded into EVERY request, so each
    /// line costs tokens forever — we keep it to facts Claude would otherwise spend tool calls finding
    /// (build/test commands, project map, folders to skip).
    /// </summary>
    public static class ClaudeMdGenerator
    {
        public const string FileName = "CLAUDE.md";

        public static string Generate(SolutionInfo s)
        {
            var sb = new StringBuilder();
            string title = s.SolutionFile != null ? Path.GetFileNameWithoutExtension(s.SolutionFile) : new DirectoryInfo(s.RootDir).Name;
            sb.AppendLine($"# {title}");
            sb.AppendLine();

            var outside = s.Projects.Where(p => !p.InSolution).ToList();
            var listed = s.Projects.Where(p => p.InSolution).ToList();
            var dotnet = listed.Where(p => p.Kind == "csproj").ToList();
            var web = listed.Where(p => p.Kind == "angular" || p.Kind == "node").ToList();
            // A package.json next to a .NET project (bundling for a web app) gets a command, not a second project line.
            var dotnetDirs = new HashSet<string>(dotnet.Select(p => Dir(p.RelativePath)), System.StringComparer.OrdinalIgnoreCase);
            var shown = listed.Where(p => !((p.Kind == "node") && dotnetDirs.Contains(Dir(p.RelativePath)))).ToList();

            sb.AppendLine("## Projects");
            foreach (var p in shown.Take(40))
            {
                string tags = string.Join(", ", new[] { p.Frameworks, p.IsTest ? "tests" : null, p.IsWeb && p.Kind == "csproj" ? "web" : null }.Where(x => !string.IsNullOrEmpty(x)));
                string dir = Dir(p.RelativePath);
                sb.Append("- `").Append(string.IsNullOrEmpty(dir) ? p.RelativePath : dir + "/").Append("` ").Append(p.Name);
                if (tags.Length > 0) sb.Append(" (").Append(tags).Append(')');
                if (p.KeyPackages.Count > 0) sb.Append(" — ").Append(string.Join(", ", p.KeyPackages.Take(6)));
                sb.AppendLine();
            }
            if (shown.Count > 40) sb.AppendLine($"- …and {shown.Count - 40} more");
            if (s.ExternalProjects.Count > 0)
                sb.AppendLine("- Also in the solution, outside this folder: " + string.Join(", ", s.ExternalProjects.Take(8).Select(x => "`" + x + "`"))
                              + (s.ExternalProjects.Count > 8 ? $" …and {s.ExternalProjects.Count - 8} more" : ""));
            if (outside.Count > 0)
            {
                // Grouped by parent folder: samples/test inputs are usually a handful of sibling projects.
                var groups = outside.GroupBy(p => Dir(Dir(p.RelativePath))).Select(g => $"`{(g.Key.Length == 0 ? "." : g.Key)}/` ({g.Count()})");
                sb.AppendLine($"- Not in {s.SolutionFile} (not built by it): " + string.Join(", ", groups.Take(5)));
            }
            sb.AppendLine();

            sb.AppendLine("## Commands");
            if (dotnet.Count > 0)
            {
                string target = s.SolutionFile ?? (dotnet.Count == 1 ? dotnet[0].RelativePath : "");
                if (dotnet.Any(p => p.IsLegacy))
                {
                    string restore = dotnet.Any(p => p.UsesPackagesConfig) ? " -p:RestorePackagesConfig=true" : "";
                    sb.AppendLine($"- Build: `msbuild {target} -restore{restore} -v:q -nologo` (.NET Framework projects: `dotnet build` can't build them; MSBuild.exe is in the VS install: `vswhere -latest -find MSBuild\\**\\Bin\\MSBuild.exe`)".Replace("  ", " "));
                    foreach (var t in dotnet.Where(p => p.IsTest).Take(3))
                        sb.AppendLine(t.IsLegacy
                            ? $"- Test {t.Name}: build, then `vstest.console.exe {Dir(t.RelativePath)}/bin/Debug/{t.Name}.dll` (one test: `/Tests:Name`)".Replace(" /bin", " bin")
                            : $"- Test {t.Name}: `dotnet test {t.RelativePath} -v q --nologo`");
                }
                else
                {
                    sb.AppendLine($"- Build: `dotnet build {target} -v q -nologo`".Replace("  ", " "));
                    if (dotnet.Any(p => p.IsTest))
                        sb.AppendLine($"- Test: `dotnet test {target} -v q --nologo` (one test: add `--filter FullyQualifiedName~Name`)".Replace("  ", " "));
                }
            }
            foreach (var w in web.Where(w => w.Kind == "angular").Take(3))
                sb.AppendLine($"- {w.Name}: `cd {DirOrDot(w.RelativePath)} && npx ng build` / `npx ng test --watch=false`");
            foreach (var w in web.Where(w => w.Kind == "node" && w.Scripts.Count > 0).Take(4))
                sb.AppendLine($"- {w.Name}: `cd {DirOrDot(w.RelativePath)} && " + string.Join("` / `", w.Scripts.Select(x => x == "test" ? "npm test" : "npm run " + x)) + "`");
            sb.AppendLine();

            sb.AppendLine("## Working rules (token budget)");
            sb.AppendLine("- Never read `bin/`, `obj/`, `node_modules/`, `packages/`, `dist/`, `*.min.js`, generated `*.Designer.cs` or migrations snapshots unless asked.");
            sb.AppendLine("- Prefer Grep/Glob to locate code, then Read only the needed line range.");
            sb.AppendLine("- Keep answers short: show diffs/edits, skip restating unchanged code.");
            sb.AppendLine("- Build/test with quiet verbosity; read only the errors.");
            sb.AppendLine();
            sb.AppendLine("## Conventions");
            sb.AppendLine("<!-- Add 3–5 lines max: naming, layering, error handling. Every line here is paid on every request. -->");
            return sb.ToString();
        }

        private static string Dir(string relativePath) => Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";

        private static string DirOrDot(string relativePath) { string d = Dir(relativePath); return d.Length == 0 ? "." : d; }

        /// <summary>Rough token estimate (~4 chars/token) to show the user what CLAUDE.md costs per request.</summary>
        public static int EstimateTokens(string text) => Tokens.TokenEstimate.FromText(text);
    }
}
