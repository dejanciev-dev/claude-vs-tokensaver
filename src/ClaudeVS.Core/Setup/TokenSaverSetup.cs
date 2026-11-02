using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Setup
{
    public sealed class SetupResult
    {
        public List<string> Written { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
        public override string ToString() => $"Written: {string.Join(", ", Written)}; Skipped: {string.Join(", ", Skipped)}";
    }

    /// <summary>
    /// Writes project-level Claude Code configuration that cuts tokens without changing how you work:
    ///  1. <c>.claude/settings.json</c> deny rules so Claude never reads build output / dependencies.
    ///  2. <c>BASH_MAX_OUTPUT_LENGTH</c> so a runaway command cannot flood the context.
    ///  3. A PreToolUse hook that rewrites noisy commands (dotnet build/test, msbuild, git log) to quiet forms.
    /// Existing settings are merged, never replaced. Re-running is idempotent.
    /// </summary>
    public static class TokenSaverSetup
    {
        public const string HookFileName = "trim-output.ps1";
        public const string HookMarker = "claudevs-trim-output";
        public const int DefaultBashMaxOutput = 15000;

        public static readonly string[] DenyReadPatterns =
        {
            "Read(./**/bin/**)", "Read(./**/obj/**)", "Read(./**/node_modules/**)", "Read(./**/packages/**)",
            "Read(./**/dist/**)", "Read(./**/.vs/**)", "Read(./**/TestResults/**)", "Read(./**/*.min.js)",
            "Read(./**/*.min.css)", "Read(./**/*.map)", "Read(./**/*.dll)", "Read(./**/*.pdb)", "Read(./**/*.exe)",
            "Read(./**/.angular/**)", "Read(./**/coverage/**)",
        };

        public static SetupResult Apply(string rootDir, bool autoApproveRewrites = false)
        {
            var result = new SetupResult();
            string claudeDir = Path.Combine(rootDir, ".claude");
            string hooksDir = Path.Combine(claudeDir, "hooks");
            Directory.CreateDirectory(hooksDir);

            string hookPath = Path.Combine(hooksDir, HookFileName);
            string script = HookScript(autoApproveRewrites);
            if (!File.Exists(hookPath) || File.ReadAllText(hookPath) != script)
            {
                File.WriteAllText(hookPath, script, new UTF8Encoding(true)); // BOM: Windows PowerShell 5.1 needs it for non-ASCII
                result.Written.Add(".claude/hooks/" + HookFileName);
            }
            else result.Skipped.Add(".claude/hooks/" + HookFileName + " (up to date)");

            string settingsPath = Path.Combine(claudeDir, "settings.json");
            JObject settings = File.Exists(settingsPath) ? JObject.Parse(File.ReadAllText(settingsPath)) : new JObject();
            string before = settings.ToString(Formatting.Indented);
            MergeSettings(settings);
            string after = settings.ToString(Formatting.Indented);
            if (before != after)
            {
                File.WriteAllText(settingsPath, after + Environment.NewLine, new UTF8Encoding(false));
                result.Written.Add(".claude/settings.json");
            }
            else result.Skipped.Add(".claude/settings.json (up to date)");
            return result;
        }

        public static void MergeSettings(JObject settings)
        {
            // 1. permissions.deny
            // Only assign freshly created containers: re-assigning a parented JToken makes Newtonsoft clone it.
            var perms = GetOrAdd<JObject>(settings, "permissions");
            var deny = GetOrAdd<JArray>(perms, "deny");
            var existing = new HashSet<string>(deny.Select(t => (string)t), StringComparer.OrdinalIgnoreCase);
            foreach (var p in DenyReadPatterns) if (existing.Add(p)) deny.Add(p);

            // 2. env
            var env = GetOrAdd<JObject>(settings, "env");
            if (env["BASH_MAX_OUTPUT_LENGTH"] == null) env["BASH_MAX_OUTPUT_LENGTH"] = DefaultBashMaxOutput.ToString();

            // 3. hooks.PreToolUse (Bash)
            var hooks = GetOrAdd<JObject>(settings, "hooks");
            var pre = GetOrAdd<JArray>(hooks, "PreToolUse");
            bool present = pre.OfType<JObject>().SelectMany(m => (m["hooks"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                              .Any(h => ((string)h["command"] ?? "").Contains(HookFileName));
            if (!present)
            {
                pre.Add(new JObject
                {
                    ["matcher"] = "Bash",
                    ["hooks"] = new JArray(new JObject
                    {
                        ["type"] = "command",
                        ["command"] = "powershell -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/" + HookFileName + "\"",
                        ["timeout"] = 10,
                    }),
                });
            }
        }

        private static T GetOrAdd<T>(JObject parent, string name) where T : JContainer, new()
        {
            if (parent[name] is T existing) return existing;
            var created = new T();
            parent[name] = created;
            return created;
        }

        /// <summary>
        /// PreToolUse hook (Windows PowerShell 5.1 compatible). Reads the tool call from stdin and, for known
        /// noisy commands, returns <c>updatedInput</c> with a quieter command line. Anything else: exit 0, no output
        /// (= Claude Code proceeds normally). Rewrites use "ask" by default so you still approve them.
        /// </summary>
        public static string HookScript(bool autoApprove) => @"# " + HookMarker + @" — generated by Claude Code for Visual Studio. Safe to edit.
# PreToolUse hook: makes noisy shell commands quiet BEFORE they run, so less output enters the context.
$ErrorActionPreference = 'Stop'
$AutoApprove = $" + (autoApprove ? "true" : "false") + @"   # true = run rewritten commands without asking

try {
    $raw = [Console]::In.ReadToEnd()
    $evt = $raw | ConvertFrom-Json
} catch { exit 0 }

if ($evt.tool_name -ne 'Bash') { exit 0 }
$cmd = [string]$evt.tool_input.command
if ([string]::IsNullOrWhiteSpace($cmd)) { exit 0 }
# Leave piped / chained / already-tuned commands alone.
if ($cmd -match '[|;&]' -or $cmd -match '(\s-v(erbosity)?[\s:=])|(--verbosity)|(-clp:)') { exit 0 }

$new = $null
if ($cmd -match '^\s*dotnet\s+(build|publish|pack|restore|clean)\b') {
    $new = ""$cmd -v q -nologo""
}
elseif ($cmd -match '^\s*dotnet\s+test\b') {
    $new = ""$cmd -v q --nologo --logger `""console;verbosity=minimal`""""
}
elseif ($cmd -match '^\s*msbuild(\.exe)?\b') {
    $new = ""$cmd -v:q -nologo""
}
elseif ($cmd -match '^\s*git\s+log\b' -and $cmd -notmatch '(\s-n\s*\d)|(--max-count)|(\s-\d+)') {
    $new = ""$cmd -n 30""
}
elseif ($cmd -match '^\s*(npm|npx)\s+(run\s+)?test\b' -and $cmd -notmatch 'watch') {
    $new = ""$cmd -- --watch=false""
}

if ($null -eq $new) { exit 0 }

$input2 = @{}
$evt.tool_input.PSObject.Properties | ForEach-Object { $input2[$_.Name] = $_.Value }
$input2['command'] = $new

$decision = if ($AutoApprove) { 'allow' } else { 'ask' }
$out = @{
    hookSpecificOutput = @{
        hookEventName = 'PreToolUse'
        permissionDecision = $decision
        permissionDecisionReason = 'Quiet output to save tokens: ' + $new
        updatedInput = $input2
    }
}
$out | ConvertTo-Json -Depth 6 -Compress
exit 0
";
    }
}
