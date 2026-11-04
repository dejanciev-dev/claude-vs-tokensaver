using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Core.Cli
{
    /// <summary>What a tool call returns: text for Claude, or an error message.</summary>
    public sealed class McpToolResult
    {
        public string Text { get; set; }
        public bool IsError { get; set; }

        public static McpToolResult Ok(string text) => new McpToolResult { Text = text };
        public static McpToolResult Error(string text) => new McpToolResult { Text = text, IsError = true };
    }

    public sealed class McpTool
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>JSON Schema of the arguments (an object schema).</summary>
        public JObject InputSchema { get; set; } = new JObject { ["type"] = "object", ["properties"] = new JObject() };
        /// <summary>Only reads: allowed without a permission prompt (<c>--allowedTools</c>).</summary>
        public bool ReadOnly { get; set; }
        public Func<JObject, Task<McpToolResult>> Handler { get; set; }
    }

    /// <summary>
    /// An MCP server that lives inside this process. Claude Code calls it over the stream-json channel we already
    /// have: it is declared in <c>--mcp-config</c> as <c>{"type":"sdk","name":…}</c>, and each JSON-RPC message
    /// arrives as a <c>control_request</c> with subtype <c>mcp_message</c>; the answer goes back in a
    /// <c>control_response</c> as <c>{"mcp_response": …}</c> (verified with Claude Code 2.1.318: initialize,
    /// notifications/initialized, tools/list and tools/call; no <c>initialize</c> control request needed).
    /// </summary>
    public sealed class SdkMcpServer
    {
        private readonly List<McpTool> _tools = new List<McpTool>();

        public SdkMcpServer(string name, string version = "1.0")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A server name is required.", nameof(name));
            Name = name;
            Version = version;
        }

        public string Name { get; }
        public string Version { get; }
        public IReadOnlyList<McpTool> Tools => _tools;

        public SdkMcpServer Add(McpTool tool)
        {
            if (tool?.Name == null || tool.Handler == null) throw new ArgumentException("A tool needs a name and a handler.", nameof(tool));
            _tools.Add(tool);
            return this;
        }

        /// <summary>The name Claude sees: <c>mcp__server__tool</c>.</summary>
        public string QualifiedName(McpTool tool) => "mcp__" + Name + "__" + tool.Name;

        /// <summary>Value for <c>--mcp-config</c>; it adds to the user's own servers rather than replacing them.</summary>
        public static string ConfigJson(IEnumerable<SdkMcpServer> servers)
        {
            var map = new JObject();
            foreach (var s in servers ?? Enumerable.Empty<SdkMcpServer>())
                map[s.Name] = new JObject { ["type"] = "sdk", ["name"] = s.Name };
            return new JObject { ["mcpServers"] = map }.ToString(Formatting.None);
        }

        /// <summary>Answers one JSON-RPC message. Notifications get an empty result (the CLI expects a reply).</summary>
        public async Task<JObject> HandleAsync(JObject message)
        {
            JToken id = message?["id"];
            string method = (string)message?["method"];
            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, new JObject
                        {
                            ["protocolVersion"] = (string)message.SelectToken("params.protocolVersion") ?? "2025-06-18",
                            ["capabilities"] = new JObject { ["tools"] = new JObject() },
                            ["serverInfo"] = new JObject { ["name"] = Name, ["version"] = Version },
                        });
                    case "ping":
                        return Result(id, new JObject());
                    case "tools/list":
                        return Result(id, new JObject { ["tools"] = new JArray(_tools.Select(Describe)) });
                    case "tools/call":
                        return Result(id, await CallAsync(message["params"] as JObject).ConfigureAwait(false));
                    default:
                        if (method != null && method.StartsWith("notifications/", StringComparison.Ordinal)) return Result(id, new JObject());
                        return Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (ArgumentException ex) { return Error(id, -32602, ex.Message); }
        }

        private async Task<JObject> CallAsync(JObject p)
        {
            string name = (string)p?["name"];
            var tool = _tools.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException("Unknown tool: " + name);
            McpToolResult r;
            try { r = await tool.Handler(p["arguments"] as JObject ?? new JObject()).ConfigureAwait(false) ?? McpToolResult.Ok(""); }
            catch (Exception ex) { r = McpToolResult.Error(tool.Name + " failed: " + ex.Message); }
            var result = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = r.Text ?? "" }) };
            if (r.IsError) result["isError"] = true;
            return result;
        }

        private static JObject Describe(McpTool t)
        {
            var o = new JObject { ["name"] = t.Name, ["description"] = t.Description ?? "", ["inputSchema"] = t.InputSchema };
            if (t.ReadOnly) o["annotations"] = new JObject { ["readOnlyHint"] = true };
            return o;
        }

        private static JObject Result(JToken id, JObject result)
        {
            var o = new JObject { ["jsonrpc"] = "2.0" };
            if (id != null) o["id"] = id.DeepClone();
            o["result"] = result;
            return o;
        }

        private static JObject Error(JToken id, int code, string message)
        {
            var o = new JObject { ["jsonrpc"] = "2.0" };
            if (id != null) o["id"] = id.DeepClone();
            o["error"] = new JObject { ["code"] = code, ["message"] = message };
            return o;
        }
    }
}
