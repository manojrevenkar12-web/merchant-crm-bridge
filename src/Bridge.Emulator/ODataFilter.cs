using System.Globalization;

namespace Bridge.Emulator;

/// <summary>The part of OData $filter the bridge uses: comparisons, and/or/not, parentheses,
/// contains/startswith, null, numbers, strings, guids and dates.</summary>
public static class ODataFilter
{
    public static Func<Row, bool> Parse(string text, TableMeta table)
    {
        var p = new Parser(Tokenize(text), table);
        var f = p.Or();
        if (!p.AtEnd) throw DataverseException.Invalid($"$filter: unexpected '{p.Peek}'.");
        return f;
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or ',') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '\'')
            {
                var sb = new System.Text.StringBuilder("'");
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                    if (s[i] == '\'') { i++; break; }
                    sb.Append(s[i++]);
                }
                tokens.Add(sb.ToString());
                continue;
            }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not '(' and not ')' and not ',') i++;
            tokens.Add(s[start..i]);
        }
        return tokens;
    }

    private sealed class Parser(List<string> tokens, TableMeta table)
    {
        private int _i;
        public bool AtEnd => _i >= tokens.Count;
        public string Peek => AtEnd ? "<end>" : tokens[_i];
        private string Next() => AtEnd ? throw DataverseException.Invalid("$filter ended early.") : tokens[_i++];
        private bool Accept(string t) { if (!AtEnd && string.Equals(tokens[_i], t, StringComparison.OrdinalIgnoreCase)) { _i++; return true; } return false; }
        private void Expect(string t) { if (!Accept(t)) throw DataverseException.Invalid($"$filter: expected '{t}' but found '{Peek}'."); }

        public Func<Row, bool> Or()
        {
            var left = And();
            while (Accept("or")) { var l = left; var r = And(); left = row => l(row) || r(row); }
            return left;
        }

        private Func<Row, bool> And()
        {
            var left = Not();
            while (Accept("and")) { var l = left; var r = Not(); left = row => l(row) && r(row); }
            return left;
        }

        private Func<Row, bool> Not()
        {
            if (Accept("not")) { var inner = Not(); return row => !inner(row); }
            return Primary();
        }

        private Func<Row, bool> Primary()
        {
            if (Accept("(")) { var e = Or(); Expect(")"); return e; }
            var name = Next();
            if (name is "contains" or "startswith" or "endswith")
            {
                Expect("(");
                var column = Column(Next());
                Expect(",");
                var literal = Next();
                Expect(")");
                if (!literal.StartsWith('\'')) throw DataverseException.Invalid($"{name} needs a string.");
                var needle = literal[1..];
                return row =>
                {
                    var v = row.Values.GetValueOrDefault(column) as string;
                    if (v is null) return false;
                    return name switch
                    {
                        "contains" => v.Contains(needle, StringComparison.OrdinalIgnoreCase),
                        "startswith" => v.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
                        _ => v.EndsWith(needle, StringComparison.OrdinalIgnoreCase),
                    };
                };
            }
            var col = Column(name);
            var op = Next().ToLowerInvariant();
            var value = Literal(Next(), col);
            return op switch
            {
                "eq" => row => Compare(row.Values.GetValueOrDefault(col), value) == 0,
                "ne" => row => Compare(row.Values.GetValueOrDefault(col), value) != 0,
                "gt" => row => Ordered(row, col, value, c => c > 0),
                "ge" => row => Ordered(row, col, value, c => c >= 0),
                "lt" => row => Ordered(row, col, value, c => c < 0),
                "le" => row => Ordered(row, col, value, c => c <= 0),
                _ => throw DataverseException.Invalid($"$filter: unknown operator '{op}'."),
            };
        }

        private static bool Ordered(Row row, string col, object? value, Func<int, bool> test)
        {
            var v = row.Values.GetValueOrDefault(col);
            return v is not null && value is not null && test(Compare(v, value));
        }

        private string Column(string name)
        {
            if (!table.HasColumn(name)) throw new DataverseException(400, "0x80060888", $"Could not find a property named '{name}' on type 'Microsoft.Dynamics.CRM.{table.LogicalName}'.");
            return name;
        }

        private object? Literal(string token, string column)
        {
            if (token == "null") return null;
            if (token.StartsWith('\'')) return token[1..];
            if (token is "true" or "false") return token == "true";
            if (Guid.TryParse(token, out var g)) return g;
            var type = table.Columns.TryGetValue(column, out var meta) ? meta.Type : column == "versionnumber" ? "bigint" : column.EndsWith("on", StringComparison.Ordinal) ? "datetime" : "string";
            if (type == "datetime" && DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
            if (decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return n;
            throw DataverseException.Invalid($"$filter: cannot read '{token}'.");
        }
    }

    public static int Compare(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
        if (a is DateTimeOffset da && b is DateTimeOffset db) return da.CompareTo(db);
        if (a is Guid ga && b is Guid gb) return ga.CompareTo(gb);
        if (a is bool ba && b is bool bb) return ba.CompareTo(bb);
        return string.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumber(object o) => o is int or long or decimal or double;
}
