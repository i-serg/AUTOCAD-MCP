#if NETFRAMEWORK
// Minimal, dependency-free stand-in for System.Text.Json.Nodes on .NET Framework 4.8
// (AutoCAD 2021–2024). It implements only the API surface this plugin uses, so the
// .NET Framework build stays a single DLL with no NuGet dependencies that could clash
// with assembly versions already loaded into the AutoCAD process.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Infomatik.AutoCAD.Mcp.Json;

internal abstract class JsonNode
{
    public virtual JsonNode? this[string key]
    {
        get => throw new InvalidOperationException($"'{GetType().Name}' is not a JSON object.");
        set => throw new InvalidOperationException($"'{GetType().Name}' is not a JSON object.");
    }

    public T GetValue<T>()
    {
        if (this is JsonValue v && v.TryGetValue<T>(out var value)) return value;
        throw new InvalidOperationException($"The JSON value cannot be converted to {typeof(T).Name}.");
    }

    public abstract JsonNode DeepClone();

    public string ToJsonString()
    {
        var sb = new StringBuilder();
        WriteTo(sb);
        return sb.ToString();
    }

    public override string ToString() => ToJsonString();

    internal abstract void WriteTo(StringBuilder sb);

    public static JsonNode? Parse(string json) => new Parser(json).ParseDocument();

    public static implicit operator JsonNode(string value) => JsonValue.FromString(value);
    public static implicit operator JsonNode(bool value) => JsonValue.FromBool(value);
    public static implicit operator JsonNode(int value) => JsonValue.FromNumber(value.ToString(CultureInfo.InvariantCulture));
    public static implicit operator JsonNode(long value) => JsonValue.FromNumber(value.ToString(CultureInfo.InvariantCulture));
    public static implicit operator JsonNode(double value) => JsonValue.FromNumber(value.ToString("R", CultureInfo.InvariantCulture));

    // ---- serialization helpers ----

    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c); // non-ASCII stays readable; output is UTF-8
                    break;
            }
        }
        sb.Append('"');
    }

    // ---- parser ----

    private sealed class Parser
    {
        private const int MaxDepth = 64;
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s; }

        public JsonNode? ParseDocument()
        {
            SkipWs();
            var node = ParseValue(0);
            SkipWs();
            if (_i != _s.Length) throw Error("Unexpected trailing characters");
            return node;
        }

        private JsonNode? ParseValue(int depth)
        {
            if (depth > MaxDepth) throw Error("Maximum nesting depth exceeded");
            if (_i >= _s.Length) throw Error("Unexpected end of input");
            var c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return JsonValue.FromString(ParseString());
                case 't': Expect("true"); return JsonValue.FromBool(true);
                case 'f': Expect("false"); return JsonValue.FromBool(false);
                case 'n': Expect("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return JsonValue.FromNumber(ParseNumber());
                    throw Error($"Unexpected character '{c}'");
            }
        }

        private JsonObject ParseObject(int depth)
        {
            var obj = new JsonObject();
            _i++; // {
            SkipWs();
            if (Peek() == '}') { _i++; return obj; }
            while (true)
            {
                SkipWs();
                if (Peek() != '"') throw Error("Expected property name");
                var key = ParseString();
                SkipWs();
                if (Peek() != ':') throw Error("Expected ':'");
                _i++;
                SkipWs();
                obj[key] = ParseValue(depth + 1);
                SkipWs();
                var c = Peek();
                _i++;
                if (c == ',') continue;
                if (c == '}') return obj;
                throw Error("Expected ',' or '}'");
            }
        }

        private JsonArray ParseArray(int depth)
        {
            var arr = new JsonArray();
            _i++; // [
            SkipWs();
            if (Peek() == ']') { _i++; return arr; }
            while (true)
            {
                SkipWs();
                arr.Add(ParseValue(depth + 1));
                SkipWs();
                var c = Peek();
                _i++;
                if (c == ',') continue;
                if (c == ']') return arr;
                throw Error("Expected ',' or ']'");
            }
        }

        private string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw Error("Unterminated string");
                var c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw Error("Control character in string");
                if (c != '\\') { sb.Append(c); continue; }
                if (_i >= _s.Length) throw Error("Unterminated escape");
                var e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length ||
                            !int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw Error("Invalid \\u escape");
                        sb.Append((char)code); // surrogate pairs arrive as two escapes and recombine naturally
                        _i += 4;
                        break;
                    default: throw Error($"Invalid escape '\\{e}'");
                }
            }
        }

        private string ParseNumber()
        {
            var start = _i;
            if (Peek() == '-') _i++;
            if (!IsDigit(Peek())) throw Error("Invalid number");
            if (Peek() == '0') _i++;
            else while (IsDigit(Peek())) _i++;
            if (Peek() == '.')
            {
                _i++;
                if (!IsDigit(Peek())) throw Error("Invalid number");
                while (IsDigit(Peek())) _i++;
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                _i++;
                if (Peek() == '+' || Peek() == '-') _i++;
                if (!IsDigit(Peek())) throw Error("Invalid number");
                while (IsDigit(Peek())) _i++;
            }
            return _s.Substring(start, _i - start);
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0) throw Error($"Expected '{literal}'");
            _i += literal.Length;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void SkipWs()
        {
            while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
        }

        private FormatException Error(string message) => new($"{message} at position {_i}.");
    }
}

internal sealed class JsonObject : JsonNode, IEnumerable<KeyValuePair<string, JsonNode?>>
{
    // Preserve insertion order, like System.Text.Json.
    private readonly List<KeyValuePair<string, JsonNode?>> _items = new();

    public override JsonNode? this[string key]
    {
        get
        {
            foreach (var kv in _items) if (kv.Key == key) return kv.Value;
            return null;
        }
        set
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Key != key) continue;
                _items[i] = new KeyValuePair<string, JsonNode?>(key, value);
                return;
            }
            _items.Add(new KeyValuePair<string, JsonNode?>(key, value));
        }
    }

    public int Count => _items.Count;

    public override JsonNode DeepClone()
    {
        var o = new JsonObject();
        foreach (var kv in _items) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    internal override void WriteTo(StringBuilder sb)
    {
        sb.Append('{');
        for (int i = 0; i < _items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteString(sb, _items[i].Key);
            sb.Append(':');
            if (_items[i].Value is null) sb.Append("null"); else _items[i].Value!.WriteTo(sb);
        }
        sb.Append('}');
    }

    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class JsonArray : JsonNode, IEnumerable<JsonNode?>
{
    private readonly List<JsonNode?> _items = new();

    public JsonArray() { }
    public JsonArray(params JsonNode?[] items) { _items.AddRange(items); }

    public void Add(JsonNode? item) => _items.Add(item);
    public int Count => _items.Count;
    public JsonNode? this[int index] => _items[index];

    public override JsonNode DeepClone()
    {
        var a = new JsonArray();
        foreach (var item in _items) a.Add(item?.DeepClone());
        return a;
    }

    internal override void WriteTo(StringBuilder sb)
    {
        sb.Append('[');
        for (int i = 0; i < _items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (_items[i] is null) sb.Append("null"); else _items[i]!.WriteTo(sb);
        }
        sb.Append(']');
    }

    public IEnumerator<JsonNode?> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class JsonValue : JsonNode
{
    private enum Kind { String, Number, Bool }

    private readonly Kind _kind;
    private readonly string? _text;   // string value, or raw number token
    private readonly bool _bool;

    private JsonValue(Kind kind, string? text, bool b) { _kind = kind; _text = text; _bool = b; }

    internal static JsonValue FromString(string s) => new(Kind.String, s ?? throw new ArgumentNullException(nameof(s)), false);
    internal static JsonValue FromNumber(string raw) => new(Kind.Number, raw, false);
    internal static JsonValue FromBool(bool b) => new(Kind.Bool, null, b);

    public bool TryGetValue<T>(out T value)
    {
        object? result = null;
        var t = typeof(T);
        if (t == typeof(string))
        {
            if (_kind == Kind.String) result = _text;
        }
        else if (t == typeof(bool))
        {
            if (_kind == Kind.Bool) result = _bool;
        }
        else if (_kind == Kind.Number)
        {
            const NumberStyles ns = NumberStyles.Float;
            var ci = CultureInfo.InvariantCulture;
            if (t == typeof(double) && double.TryParse(_text, ns, ci, out var d)) result = d;
            else if (t == typeof(int) && int.TryParse(_text, NumberStyles.Integer, ci, out var i)) result = i;
            else if (t == typeof(long) && long.TryParse(_text, NumberStyles.Integer, ci, out var l)) result = l;
            else if (t == typeof(decimal) && decimal.TryParse(_text, ns, ci, out var m)) result = m;
        }

        if (result is null) { value = default!; return false; }
        value = (T)result;
        return true;
    }

    public override JsonNode DeepClone() => new JsonValue(_kind, _text, _bool);

    internal override void WriteTo(StringBuilder sb)
    {
        switch (_kind)
        {
            case Kind.String: WriteString(sb, _text!); break;
            case Kind.Number: sb.Append(_text); break;
            default: sb.Append(_bool ? "true" : "false"); break;
        }
    }
}
#endif
