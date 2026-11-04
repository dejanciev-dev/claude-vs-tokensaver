using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClaudeVS.Core.Cli;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Context
{
    /// <summary>
    /// The extension's own MCP server: lets Claude read Visual Studio's Error List when it wants to (for example
    /// after an edit), instead of the user attaching it or Claude running a full, verbose build.
    /// </summary>
    public static class IdeMcpServer
    {
        public const string ServerName = "visualstudio";

        /// <summary><paramref name="readErrorList"/> returns every Error List row; <paramref name="solutionDir"/> shortens paths.</summary>
        public static SdkMcpServer Create(Func<Task<IReadOnlyList<BuildError>>> readErrorList, Func<string> solutionDir)
        {
            if (readErrorList == null) throw new ArgumentNullException(nameof(readErrorList));
            return new SdkMcpServer(ServerName).Add(ErrorListTool.Create(readErrorList, solutionDir));
        }

        /// <summary>Short label for the transcript, e.g. "Error List" for <c>mcp__visualstudio__get_error_list</c>; null for other tools.</summary>
        public static string DisplayName(string qualifiedToolName) =>
            qualifiedToolName == "mcp__" + ServerName + "__" + ErrorListTool.Name ? "Error List" : null;
    }

    public static class ErrorListTool
    {
        public const string Name = "get_error_list";
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;

        public static McpTool Create(Func<Task<IReadOnlyList<BuildError>>> readErrorList, Func<string> solutionDir) => new McpTool
        {
            Name = Name,
            ReadOnly = true,
            Description =
                "Errors and warnings in Visual Studio's Error List for the open solution (compiler, analyzers and live " +
                "IntelliSense), one line each. Use it after editing code to check that it compiles, instead of running " +
                "a full build. It can take a few seconds to catch up after files change.",
            InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""severity"": { ""type"": ""string"", ""enum"": [""error"", ""warning"", ""all""], ""description"": ""error (default): errors only; warning: errors and warnings; all: also messages"" },
    ""file"": { ""type"": ""string"", ""description"": ""Only rows whose file path contains this text"" },
    ""limit"": { ""type"": ""integer"", ""description"": ""At most this many rows (default 50, max 200)"" }
  }
}"),
            Handler = async args =>
            {
                var rows = await readErrorList().ConfigureAwait(false);
                return McpToolResult.Ok(Format(rows, solutionDir?.Invoke(), args));
            },
        };

        /// <summary>
        /// The tool's answer: a count line, then <c>path:line:col severity CODE: message (Project)</c> rows, errors first.
        /// Duplicates are removed (the same diagnostic often comes from both the build and IntelliSense).
        /// </summary>
        public static string Format(IEnumerable<BuildError> rows, string solutionDir, JObject args)
        {
            string severity = ((string)args?["severity"] ?? "error").Trim().ToLowerInvariant();
            ErrorSeverity worst = severity == "all" ? ErrorSeverity.Message : severity == "warning" || severity == "warnings" ? ErrorSeverity.Warning : ErrorSeverity.Error;
            string file = ((string)args?["file"])?.Trim().Replace('\\', '/');
            int limit = Math.Max(1, Math.Min(MaxLimit, args?.Value<int?>("limit") ?? DefaultLimit));

            var all = (rows ?? Enumerable.Empty<BuildError>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Message))
                .GroupBy(e => (e.FilePath ?? "") + "|" + e.Line + "|" + e.Severity + "|" + (e.Code ?? "") + "|" + e.Message.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            int errors = all.Count(e => e.Severity == ErrorSeverity.Error);
            int warnings = all.Count(e => e.Severity == ErrorSeverity.Warning);

            var shown = all
                .Where(e => e.Severity <= worst)
                .Where(e => string.IsNullOrEmpty(file) || (e.FilePath ?? "").Replace('\\', '/').IndexOf(file, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(e => e.Severity)
                .ThenBy(e => e.FilePath ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Line)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("Error List: ").Append(Count(errors, "error")).Append(", ").Append(Count(warnings, "warning")).Append('.');
            if (!string.IsNullOrEmpty(file)) sb.Append(" Matching \"").Append(file).Append("\": ").Append(shown.Count).Append('.');
            if (shown.Count == 0)
            {
                if (worst == ErrorSeverity.Error && warnings > 0 && string.IsNullOrEmpty(file)) sb.Append(" Pass severity \"warning\" to see the warnings.");
                return sb.ToString();
            }
            if (shown.Count > limit) sb.Append(" Showing the first ").Append(limit).Append('.');
            sb.Append('\n');
            foreach (var e in shown.Take(limit))
            {
                if (!string.IsNullOrEmpty(e.FilePath))
                {
                    sb.Append(PromptContextBuilder.Relative(solutionDir, e.FilePath));
                    if (e.Line > 0) sb.Append(':').Append(e.Line);
                    if (e.Line > 0 && e.Column > 0) sb.Append(':').Append(e.Column);
                    sb.Append(' ');
                }
                sb.Append(e.Severity == ErrorSeverity.Error ? "error" : e.Severity == ErrorSeverity.Warning ? "warning" : "message").Append(' ');
                if (!string.IsNullOrEmpty(e.Code)) sb.Append(e.Code).Append(": ");
                sb.Append(e.Message.Trim().Replace("\r", " ").Replace("\n", " "));
                if (!string.IsNullOrEmpty(e.Project)) sb.Append(" (").Append(e.Project).Append(')');
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");
    }
}
