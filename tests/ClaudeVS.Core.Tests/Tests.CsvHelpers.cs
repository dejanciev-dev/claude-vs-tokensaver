using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeVS.Core.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// The usage CSV's rows as header name → cell, quotes undone ("a ""b""" → a "b"), so tests read cells by the
        /// column's name instead of counting commas: a new column doesn't break them.
        /// </summary>
        private static List<Dictionary<string, string>> CsvRows(string csv)
        {
            var lines = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); lines.Add(row); row = new List<string>(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); lines.Add(row); }

            var result = new List<Dictionary<string, string>>();
            if (lines.Count == 0) return result;
            var header = lines[0];
            for (int r = 1; r < lines.Count; r++)
            {
                if (lines[r].Count != header.Count) throw new InvalidOperationException($"row {r} has {lines[r].Count} cells, the header {header.Count}");
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int k = 0; k < header.Count; k++) d[header[k]] = lines[r][k];
                result.Add(d);
            }
            return result;
        }

        /// <summary>True when <paramref name="names"/> are all columns, in this order (others may come between and after).</summary>
        private static bool ColumnsInOrder(IReadOnlyList<string> columns, params string[] names)
        {
            int at = -1;
            foreach (var n in names)
            {
                int i = -1;
                for (int k = at + 1; k < columns.Count; k++) if (columns[k] == n) { i = k; break; }
                if (i < 0) return false;
                at = i;
            }
            return true;
        }
    }
}
