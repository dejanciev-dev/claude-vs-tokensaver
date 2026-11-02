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
        public string Kind { get; set; }          // "csproj", "angular", "node", "sqlproj"
        public string Frameworks { get; set; }
        public bool IsTest { get; set; }
        public bool IsWeb { get; set; }
        public List<string> KeyPackages { get; } = new List<string>();
    }

    public sealed class SolutionInfo
    {
        public string RootDir { get; set; }
        public string SolutionFile { get; set; }
        public List<ProjectInfo> Projects { get; } = new List<ProjectInfo>();
        public bool HasGit { get; set; }
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
        };

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
                    if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase))
                        info.Projects.Add(ReadMsBuildProject(rootDir, file));
                    else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = ReadPackageJson(rootDir, file);
                        if (p != null) info.Projects.Add(p);
                    }
                }
                catch (Exception) { /* unreadable project: skip, never fail the scan */ }
            }
            return info;
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
                Frameworks = Val("TargetFrameworks") ?? Val("TargetFramework") ?? Val("TargetFrameworkVersion"),
            };
            string sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
            p.IsWeb = sdk.IndexOf("Web", StringComparison.OrdinalIgnoreCase) >= 0;

            var refs = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                          .Select(e => (string)e.Attribute("Include") ?? (string)e.Attribute("Update")).Where(s => s != null).ToList();
            p.IsTest = refs.Any(r => r.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || r.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
                                   || r.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || r == "Microsoft.NET.Test.Sdk")
                       || Regex.IsMatch(p.Name, @"\.(Tests?|UnitTests|IntegrationTests)$", RegexOptions.IgnoreCase);
            foreach (var r in refs)
            {
                var hit = InterestingPackages.FirstOrDefault(k => r.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (hit != null && !p.KeyPackages.Contains(hit.TrimEnd('.'))) p.KeyPackages.Add(hit.TrimEnd('.'));
            }
            return p;
        }

        private static ProjectInfo ReadPackageJson(string root, string file)
        {
            var json = JObject.Parse(File.ReadAllText(file));
            var deps = new List<string>();
            foreach (var key in new[] { "dependencies", "devDependencies" })
                if (json[key] is JObject o) deps.AddRange(o.Properties().Select(x => x.Name));
            bool angular = deps.Contains("@angular/core");
            if (!angular && deps.Count == 0) return null;
            var p = new ProjectInfo
            {
                Name = (string)json["name"] ?? Path.GetFileName(Path.GetDirectoryName(file)),
                RelativePath = Context.PromptContextBuilder.Relative(root, file),
                Kind = angular ? "angular" : "node",
                Frameworks = angular ? "Angular " + ((string)json.SelectToken("dependencies['@angular/core']") ?? "").TrimStart('^', '~') : null,
                IsWeb = angular,
            };
            foreach (var d in new[] { "rxjs", "@ngrx/store", "jest", "karma", "@playwright/test", "tailwindcss", "@angular/material" })
                if (deps.Contains(d)) p.KeyPackages.Add(d);
            return p;
        }
    }
}
