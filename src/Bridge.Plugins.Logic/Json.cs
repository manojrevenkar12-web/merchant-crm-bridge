using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>A minimal JSON writer: the plug-in sandbox on .NET Framework has no System.Text.Json, and the
/// payloads are flat objects of strings.</summary>
public static class Json
{
    public static string Flat(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var f in fields)
        {
            if (!first) sb.Append(',');
            first = false;
            String(sb, f.Key);
            sb.Append(':');
            String(sb, f.Value);
        }
        return sb.Append('}').ToString();
    }

    /// <summary>{"merchantId":"…","changes":{…}}</summary>
    public static string WithChanges(string merchantId, IEnumerable<KeyValuePair<string, string>> changes)
    {
        var sb = new StringBuilder("{");
        String(sb, "merchantId");
        sb.Append(':');
        String(sb, merchantId);
        sb.Append(",\"changes\":");
        sb.Append(Flat(changes));
        return sb.Append('}').ToString();
    }

    private static void String(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
