using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Setup
{
    public sealed class ProjectInfo
    {
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string Kind { get; set; }          // "csproj" (any .NET project: .csproj/.vbproj/.fsproj), "angular", "node", "sqlproj"
        public string Frameworks { get; set; }
        public bool IsTest { get; set; }
        public bool IsWeb { get; set; }
        /// <summary>Old-style (non-SDK) .NET Framework project: <c>dotnet build</c> can't build it, MSBuild can.</summary>
        public bool IsLegacy { get; set; }
        public bool UsesPackagesConfig { get; set; }
        /// <summary>False for a .NET project the solution file does not list (samples, test inputs, leftovers).</summary>
        public bool InSolution { get; set; } = true;
        /// <summary>npm scripts worth a command line: "build", "test".</summary>
        public List<string> Scripts { get; } = new List<string>();
        public List<string> KeyPackages { get; } = new List<string>();
    }

    public sealed class SolutionInfo
    {
        public string RootDir { get; set; }
        public string SolutionFile { get; set; }
        public List<ProjectInfo> Projects { get; } = new List<ProjectInfo>();
        public bool HasGit { get; set; }
        /// <summary>Projects the solution lists from outside <see cref="RootDir"/>, as written there (e.g. <c>../Shared/Shared.csproj</c>).</summary>
        public List<string> ExternalProjects { get; } = new List<string>();
    }

    /// <summary>Fast, read-only scan of a solution folder (no MSBuild evaluation).</summary>
    public static class SolutionScanner
    {
        /// <summary>Folders that are never worth Claude's tokens.</summary>
        public static readonly string[] NoiseFolders =
        {
            "bin", "obj", ".vs", "packages", "node_modules", "dist", ".angular", "TestResults", "artifacts", ".git", "wwwroot/lib", "coverage",
        };

        private static readonly string[] InterestingPackages =
        {
            "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Dapper", "MediatR", "AutoMapper", "FluentValidation",
            "Serilog", "Azure.", "Microsoft.Azure.", "Hangfire", "MassTransit", "Polly", "Swashbuckle", "Newtonsoft.Json",
            "xunit", "NUnit", "MSTest", "Moq", "NSubstitute", "FluentAssertions", "Microsoft.Data.SqlClient", "Refit", "SignalR",
            // .NET Framework era
            "EntityFramework", "Microsoft.AspNet.Mvc", "Microsoft.AspNet.WebApi", "log4net", "Autofac", "Ninject", "Unity", "Elmah",
        };

        private const string WebAppProjectGuid = "349c5851-65df-11da-9384-00065b846f21";
        private const string TestProjectGuid = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";

        public static SolutionInfo Scan(string rootDir, int maxProjects = 200)
        {
            var info = new SolutionInfo { RootDir = rootDir, HasGit = Directory.Exists(Path.Combine(rootDir, ".git")) };
            info.SolutionFile = Directory.EnumerateFiles(rootDir, "*.sln").Concat(Directory.EnumerateFiles(rootDir, "*.slnx")).Select(Path.GetFileName).FirstOrDefault();

            foreach (var file in EnumerateFiles(rootDir, depth: 0, maxDepth: 6))
            {
                if (info.Projects.Count >= maxProjects) break;
                string name = Path.GetFileName(file);
                try
                {
                    if (IsMsBuildProject(name))
                        info.Projects.Add(ReadMsBuildProject(rootDir, file));
                    else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = ReadPackageJson(rootDir, file);
                        if (p != null) info.Projects.Add(p);
                    }
                }
                catch (Exception) { /* unreadable project: skip, never fail the scan */ }
            }
            ApplySolution(info);
            return info;
        }

        private static bool IsMsBuildProject(string name) =>
            new[] { ".csproj", ".vbproj", ".fsproj", ".sqlproj" }.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>Project paths listed in a .sln / .slnx, as written there (relative to the solution).</summary>
        public static List<string> ReadSolutionProjects(string solutionPath)
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return XDocument.Load(solutionPath).Descendants().Where(e => e.Name.LocalName == "Project")
                                .Select(e => (string)e.Attribute("Path")).Where(p => p != null && IsMsBuildProject(p)).ToList();
            return Regex.Matches(File.ReadAllText(solutionPath), @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline)
                        .Cast<Match>().Select(m => m.Groups[1].Value).Where(IsMsBuildProject).ToList();
        }

        /// <summary>Marks .NET projects the solution doesn't list, and collects the ones it lists from outside the folder.
        /// An empty or unreadable solution changes nothing.</summary>
        private static void ApplySolution(SolutionInfo info)
        {
            if (info.SolutionFile == null) return;
            List<string> listed;
            try { listed = ReadSolutionProjects(Path.Combine(info.RootDir, info.SolutionFile)); } catch (Exception) { return; }
            if (listed.Count == 0) return;

            string root = Path.GetFullPath(info.RootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var inSolution = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in listed)
            {
                string normalized = rel.Replace('\\', '/');
                string full = Path.GetFullPath(Path.Combine(info.RootDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) inSolution.Add(full);
                else info.ExternalProjects.Add(normalized);
            }
            foreach (var p in info.Projects.Where(p => p.Kind == "csproj" || p.Kind == "sqlproj"))
                p.InSolution = inSolution.Contains(Path.GetFullPath(Path.Combine(info.RootDir, p.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        }

        private static IEnumerable<string> EnumerateFiles(string dir, int depth, int maxDepth)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir).ToList(); } catch { yield break; }
            foreach (var f in files) yield return f;
            if (depth >= maxDepth) yield break;

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir).ToList(); } catch { yield break; }
            foreach (var d in dirs)
            {
                string n = Path.GetFileName(d);
                if (NoiseFolders.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) || n.StartsWith(".")) continue;
                foreach (var f in EnumerateFiles(d, depth + 1, maxDepth)) yield return f;
            }
        }

        private static ProjectInfo ReadMsBuildProject(string root, string file)
        {
            var doc = XDocument.Load(file);
            string Val(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim();
            var p = new ProjectInfo
            {
                Name = Path.GetFileNameWithoutExtension(file),
                RelativePath = Context.PromptContextBuilder.Relative(root, file),
                Kind = file.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase) ? "sqlproj" : "csproj",
            };
            string sdk = doc.Root?.Attribute("Sdk")?.Value
                         ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") != null)?.Attribute("Sdk")?.Value;
            string tfv = Val("TargetFrameworkVersion");
            p.IsLegacy = sdk == null && tfv != null && p.Kind == "csproj";
            p.Frameworks = Val("TargetFrameworks") ?? Val("TargetFramework")
                           ?? (tfv != null ? "net" + tfv.TrimStart('v', 'V').Replace(".", "") : null)
                           ?? (sdk != null ? FromDirectoryBuildProps(root, Path.GetDirectoryName(file)) : null);
            string typeGuids = Val("ProjectTypeGuids") ?? "";
            p.IsWeb = (sdk ?? "").IndexOf("Web", StringComparison.OrdinalIgnoreCase) >= 0
                      || typeGuids.IndexOf(WebAppProjectGuid, StringComparison.OrdinalIgnoreCase) >= 0;

            var refs = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                          .Select(e => (string)e.Attribute("Include") ?? (string)e.Attribute("Update")).Where(s => s != null).ToList();
            string packagesConfig = Path.Combine(Path.GetDirectoryName(file), "packages.config");
            if (File.Exists(packagesConfig))
            {
                p.UsesPackagesConfig = true;
                try { refs.AddRange(XDocument.Load(packagesConfig).Descendants("package").Select(e => (string)e.Attribute("id")).Where(s => s != null)); }
                catch (Exception) { /* malformed packages.config: ignore */ }
            }
            // Assembly references: .NET Framework test projects often reference the test framework directly ("nunit.framework, Version=…").
            var assemblyRefs = doc.Descendants().Where(e => e.Name.LocalName == "Reference")
                                  .Select(e => ((string)e.Attribute("Include") ?? "").Split(',')[0].Trim()).ToList();
            p.IsTest = refs.Any(r => r.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || r.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
                                   || r.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || r == "Microsoft.NET.Test.Sdk")
                       || assemblyRefs.Any(r => r.StartsWith("Microsoft.VisualStudio.QualityTools.UnitTestFramework", StringComparison.OrdinalIgnoreCase)
                                             || r.StartsWith("nunit.framework", StringComparison.OrdinalIgnoreCase) || r.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
                       || typeGuids.IndexOf(TestProjectGuid, StringComparison.OrdinalIgnoreCase) >= 0
                       || Regex.IsMatch(p.Name, @"\.(Tests?|UnitTests|IntegrationTests)$", RegexOptions.IgnoreCase);
            foreach (var r in refs)
            {
                var hit = InterestingPackages.FirstOrDefault(k => r.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (hit != null && !p.KeyPackages.Contains(hit.TrimEnd('.'))) p.KeyPackages.Add(hit.TrimEnd('.'));
            }
            return p;
        }

        /// <summary>TargetFramework(s) from the nearest Directory.Build.props between the project folder and the root.</summary>
        private static string FromDirectoryBuildProps(string root, string dir)
        {
            string top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                string props = Path.Combine(d.FullName, "Directory.Build.props");
                if (File.Exists(props))
                {
                    try
                    {
                        var e = XDocument.Load(props).Descendants().FirstOrDefault(x => x.Name.LocalName == "TargetFrameworks" || x.Name.LocalName == "TargetFramework");
                        if (e != null && !string.IsNullOrWhiteSpace(e.Value) && !e.Value.Contains("$(")) return e.Value.Trim();
                    }
                    catch (Exception) { /* unreadable props: keep looking up */ }
                }
                if (string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), top, StringComparison.OrdinalIgnoreCase)) break;
            }
            return null;
        }

        private static ProjectInfo ReadPackageJson(string root, string file)
        {
            var json = JObject.Parse(File.ReadAllText(file));
            var deps = new List<string>();
            foreach (var key in new[] { "dependencies", "devDependencies" })
                if (json[key] is JObject o) deps.AddRange(o.Properties().Select(x => x.Name));
            bool angular = deps.Contains("@angular/core");
            var scripts = (json["scripts"] as JObject)?.Properties().Select(x => x.Name).Where(n => n == "build" || n == "test").ToList() ?? new List<string>();
            // Front-end framework, first match wins (Next before React: a Next app also depends on react).
            var framework = new[] { ("@angular/core", "Angular"), ("next", "Next.js"), ("react", "React"), ("vue", "Vue"), ("svelte", "Svelte") }
                            .FirstOrDefault(f => deps.Contains(f.Item1));
            // No framework and nothing to build or test: tooling (an esbuild devDependency at the root, …), not a project.
            if (framework.Item1 == null && scripts.Count == 0) return null;
            var p = new ProjectInfo
            {
                Name = (string)json["name"] ?? Path.GetFileName(Path.GetDirectoryName(file)),
                RelativePath = Context.PromptContextBuilder.Relative(root, file),
                Kind = angular ? "angular" : "node",
                Frameworks = framework.Item1 == null ? null : (framework.Item2 + " " + Version(json, framework.Item1)).Trim(),
                IsWeb = framework.Item1 != null,
            };
            p.Scripts.AddRange(scripts);
            foreach (var d in new[] { "rxjs", "@ngrx/store", "jest", "karma", "vitest", "@playwright/test", "tailwindcss", "@angular/material" })
                if (deps.Contains(d)) p.KeyPackages.Add(d);
            if (!p.KeyPackages.Contains("tailwindcss") && deps.Contains("@tailwindcss/vite")) p.KeyPackages.Add("tailwindcss");
            return p;
        }

        private static string Version(JObject json, string package) =>
            ((string)json["dependencies"]?[package] ?? (string)json["devDependencies"]?[package] ?? "").TrimStart('^', '~', '>', '=', ' ').Trim();
    }
}
