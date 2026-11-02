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

            var dotnet = s.Projects.Where(p => p.Kind == "csproj").ToList();
            var web = s.Projects.Where(p => p.Kind == "angular" || p.Kind == "node").ToList();

            sb.AppendLine("## Projects");
            foreach (var p in s.Projects.Take(40))
            {
                string tags = string.Join(", ", new[] { p.Frameworks, p.IsTest ? "tests" : null, p.IsWeb && p.Kind == "csproj" ? "web" : null }.Where(x => !string.IsNullOrEmpty(x)));
                string dir = Path.GetDirectoryName(p.RelativePath)?.Replace('\\', '/');
                sb.Append("- `").Append(string.IsNullOrEmpty(dir) ? p.RelativePath : dir + "/").Append("` ").Append(p.Name);
                if (tags.Length > 0) sb.Append(" (").Append(tags).Append(')');
                if (p.KeyPackages.Count > 0) sb.Append(" — ").Append(string.Join(", ", p.KeyPackages.Take(6)));
                sb.AppendLine();
            }
            if (s.Projects.Count > 40) sb.AppendLine($"- …and {s.Projects.Count - 40} more");
            sb.AppendLine();

            sb.AppendLine("## Commands");
            if (dotnet.Count > 0)
            {
                string target = s.SolutionFile ?? "";
                sb.AppendLine($"- Build: `dotnet build {target} -v q -nologo`".Replace("  ", " "));
                if (dotnet.Any(p => p.IsTest))
                    sb.AppendLine($"- Test: `dotnet test {target} -v q --nologo` (one test: add `--filter FullyQualifiedName~Name`)".Replace("  ", " "));
            }
            foreach (var w in web.Where(w => w.Kind == "angular").Take(3))
            {
                string dir = Path.GetDirectoryName(w.RelativePath)?.Replace('\\', '/');
                sb.AppendLine($"- {w.Name}: `cd {(string.IsNullOrEmpty(dir) ? "." : dir)} && npx ng build` / `npx ng test --watch=false`");
            }
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

        /// <summary>Rough token estimate (~4 chars/token) to show the user what CLAUDE.md costs per request.</summary>
        public static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
    }
}
