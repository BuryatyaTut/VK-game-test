using System.Text;

namespace WukongRunner.Core;

// Steam's text KeyValues format: quoted/unquoted tokens, escapes, objects and // comments.
public sealed class SteamVdf
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SteamVdf> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static SteamVdf Parse(string text)
    {
        var tokens = Tokenize(text).ToArray();
        var index = 0;
        var result = Read(false, 0);
        if (index != tokens.Length) throw new InvalidDataException("Unexpected VDF trailing tokens.");
        return result;

        SteamVdf Read(bool nested, int depth)
        {
            if (depth > 32) throw new InvalidDataException("VDF nesting is too deep.");
            var node = new SteamVdf();
            while (index < tokens.Length)
            {
                var key = tokens[index++];
                if (key == "}")
                { if (!nested) throw new InvalidDataException("Unexpected VDF closing brace."); return node; }
                if (key == "{" || index >= tokens.Length) throw new InvalidDataException("Incomplete VDF key/value.");
                if (node.Values.ContainsKey(key) || node.Children.ContainsKey(key)) throw new InvalidDataException("Duplicate VDF key: " + key);
                var value = tokens[index++];
                if (value == "{") node.Children.Add(key, Read(true, depth + 1));
                else if (value == "}") throw new InvalidDataException("Missing VDF value.");
                else node.Values.Add(key, value);
            }
            if (nested) throw new InvalidDataException("Unclosed VDF object.");
            return node;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == '\uFEFF') { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (text[i] is '{' or '}') { yield return text[i++].ToString(); continue; }
            var word = new StringBuilder();
            if (text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '"') i++;
                    word.Append(text[i++]);
                }
                if (i == text.Length) throw new InvalidDataException("Unclosed VDF string.");
                i++;
            }
            else while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}')) word.Append(text[i++]);
            yield return word.ToString();
        }
    }
}
