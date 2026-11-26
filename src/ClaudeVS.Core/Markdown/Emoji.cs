using System;
using System.Collections.Generic;

namespace ClaudeVS.Core.Markdown
{
    /// <summary>
    /// GitHub's emoji shortcodes Claude writes in answers (Phase 107): <c>:white_check_mark:</c> is ✅. Only the common
    /// ones; an unknown code stays as written, so <c>std::vector::size</c> or <c>10:30:00</c> are never touched.
    /// </summary>
    public static class Emoji
    {
        private static readonly Dictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["white_check_mark"] = "✅", ["heavy_check_mark"] = "✔️", ["ballot_box_with_check"] = "☑️", ["x"] = "❌",
            ["negative_squared_cross_mark"] = "❎", ["warning"] = "⚠️", ["no_entry"] = "⛔", ["no_entry_sign"] = "🚫",
            ["stop_sign"] = "🛑", ["information_source"] = "ℹ️", ["question"] = "❓", ["exclamation"] = "❗",
            ["heavy_exclamation_mark"] = "❗", ["bulb"] = "💡", ["rocket"] = "🚀", ["fire"] = "🔥", ["tada"] = "🎉",
            ["sparkles"] = "✨", ["star"] = "⭐", ["zap"] = "⚡", ["boom"] = "💥", ["bug"] = "🐛", ["memo"] = "📝",
            ["pencil"] = "📝", ["pencil2"] = "✏️", ["wrench"] = "🔧", ["hammer"] = "🔨", ["hammer_and_wrench"] = "🛠️",
            ["gear"] = "⚙️", ["lock"] = "🔒", ["unlock"] = "🔓", ["key"] = "🔑", ["construction"] = "🚧",
            ["package"] = "📦", ["books"] = "📚", ["book"] = "📖", ["link"] = "🔗", ["mag"] = "🔍", ["eyes"] = "👀",
            ["chart_with_upwards_trend"] = "📈", ["chart_with_downwards_trend"] = "📉", ["bar_chart"] = "📊",
            ["hourglass"] = "⌛", ["hourglass_flowing_sand"] = "⏳", ["stopwatch"] = "⏱️", ["calendar"] = "📅",
            ["thumbsup"] = "👍", ["+1"] = "👍", ["thumbsdown"] = "👎", ["-1"] = "👎", ["point_right"] = "👉",
            ["arrow_right"] = "➡️", ["arrow_left"] = "⬅️", ["arrow_up"] = "⬆️", ["arrow_down"] = "⬇️",
            ["heavy_plus_sign"] = "➕", ["heavy_minus_sign"] = "➖", ["recycle"] = "♻️", ["art"] = "🎨",
            ["red_circle"] = "🔴", ["orange_circle"] = "🟠", ["yellow_circle"] = "🟡", ["green_circle"] = "🟢",
            ["large_blue_circle"] = "🔵", ["blue_circle"] = "🔵", ["white_circle"] = "⚪", ["black_circle"] = "⚫",
            ["robot"] = "🤖", ["brain"] = "🧠", ["test_tube"] = "🧪", ["broom"] = "🧹", ["shield"] = "🛡️",
            ["smile"] = "😄", ["thinking"] = "🤔", ["100"] = "💯", ["pushpin"] = "📌", ["file_folder"] = "📁",
            ["page_facing_up"] = "📄", ["clipboard"] = "📋", ["bell"] = "🔔", ["new"] = "🆕",
        };

        /// <summary>The emoji for a shortcode without its colons, or null for one it doesn't know.</summary>
        public static string For(string code) => code != null && Codes.TryGetValue(code, out string e) ? e : null;
    }
}
