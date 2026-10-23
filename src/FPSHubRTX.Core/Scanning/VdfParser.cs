using System.Text;

namespace FPSHubRTX.Core.Scanning;

/// <summary>A node in Valve's KeyValues (VDF/ACF) text format: either a string value or a set of children.</summary>
public sealed class VdfNode
{
    public string? Value { get; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string? value = null) => Value = value;

    public bool IsObject => Value is null;

    public VdfNode? this[string key] => Children.TryGetValue(key, out var n) ? n : null;

    public string? GetString(string key) => this[key]?.Value;

    /// <summary>Case-insensitive path lookup, e.g. node.Path("AppState", "name").</summary>
    public VdfNode? Path(params string[] keys)
    {
        VdfNode? current = this;
        foreach (var k in keys)
        {
            current = current?[k];
            if (current is null) return null;
        }
        return current;
    }
}

/// <summary>
/// Tolerant parser for Valve KeyValues text (libraryfolders.vdf, appmanifest_*.acf).
/// Handles quoted/unquoted tokens, escape sequences, // comments and conditional tags ([$WIN32]).
/// Malformed input yields a partial tree rather than an exception.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text);
        var root = new VdfNode();
        var index = 0;
        ParseObject(tokens, ref index, root, depth: 0);
        return root;
    }

    private static void ParseObject(List<Token> tokens, ref int i, VdfNode target, int depth)
    {
        if (depth > 64) return; // protect against pathological nesting
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Close)
            {
                i++;
                return;
            }
            if (t.Kind == TokenKind.Open)
            {
                // Stray brace without key: skip its content.
                i++;
                ParseObject(tokens, ref i, new VdfNode(), depth + 1);
                continue;
            }

            var key = t.Text;
            i++;
            if (i >= tokens.Count) return;
            var next = tokens[i];
            if (next.Kind == TokenKind.Open)
            {
                i++;
                var child = new VdfNode();
                ParseObject(tokens, ref i, child, depth + 1);
                Merge(target, key, child);
            }
            else if (next.Kind == TokenKind.String)
            {
                i++;
                target.Children[key] = new VdfNode(next.Text);
            }
            else
            {
                // key followed by '}' — malformed; let the outer loop close the object.
            }
        }
    }

    private static void Merge(VdfNode target, string key, VdfNode child)
    {
        if (target.Children.TryGetValue(key, out var existing) && existing.IsObject)
        {
            foreach (var kv in child.Children) existing.Children[kv.Key] = kv.Value;
        }
        else
        {
            target.Children[key] = child;
        }
    }

    private enum TokenKind { String, Open, Close }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{') { tokens.Add(new Token(TokenKind.Open, "{")); i++; continue; }
            if (c == '}') { tokens.Add(new Token(TokenKind.Close, "}")); i++; continue; }
            if (c == '[')
            {
                // Conditional like [$WIN32] — ignored.
                while (i < text.Length && text[i] != ']') i++;
                i++;
                continue;
            }
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var e = text[i + 1];
                        switch (e)
                        {
                            case '\\': sb.Append('\\'); i += 2; continue;
                            case '"': sb.Append('"'); i += 2; continue;
                            case 'n': sb.Append('\n'); i += 2; continue;
                            case 't': sb.Append('\t'); i += 2; continue;
                            default: sb.Append('\\'); i++; continue;
                        }
                    }
                    sb.Append(text[i]);
                    i++;
                }
                i++; // closing quote
                tokens.Add(new Token(TokenKind.String, sb.ToString()));
                continue;
            }

            // Unquoted token.
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"') i++;
            tokens.Add(new Token(TokenKind.String, text[start..i]));
        }
        return tokens;
    }
}
