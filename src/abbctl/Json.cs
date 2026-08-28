using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AbbCtl
{
    /// <summary>
    /// Minimal JSON serializer for --json output (objects as
    /// Dictionary&lt;string, object&gt;, arrays as IEnumerable, plus primitives).
    /// Avoids external dependencies.
    /// </summary>
    internal static class Json
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        public static void Print(object value)
        {
            Console.WriteLine(Serialize(value));
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (value is string s) { WriteString(sb, s); return; }
            if (value is int || value is long || value is short || value is byte)
            {
                sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is float || value is double || value is decimal)
            {
                sb.Append(Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (value is IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Ordered string-keyed map so JSON output has stable field order.</summary>
    internal sealed class JObj : Dictionary<string, object>
    {
    }
}
