using System.Text;

namespace RepoShelf.Core;

/// <summary>
/// Tokenizer for mixed Chinese/English text. Latin runs become lowercase
/// whole-word tokens; CJK runs become bigrams (single CJK chars stay
/// unigrams); everything else is a separator. The same tokenizer runs at
/// index time and query time; tokens are space-joined for FTS5 (unicode61
/// sees them as terms). Verified by tests — never assume FTS5's default
/// tokenizer segments Chinese.
/// </summary>
public static class Tokenizer
{
    private enum Kind { Cjk, Word }

    private static Kind? Classify(char c)
    {
        // CJK ranges (BMP): U+3400–U+4DBF (Ext A), U+4E00–U+9FFF (Unified), U+F900–U+FAFF (Compat).
        if (c is >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '豈' and <= '﫿')
        {
            return Kind.Cjk;
        }
        if (char.IsAsciiLetterOrDigit(c) || c == '_')
        {
            return Kind.Word;
        }
        return null;
    }

    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }
        var run = new StringBuilder();
        Kind? runKind = null;

        void Flush()
        {
            if (run.Length == 0)
            {
                return;
            }
            if (runKind == Kind.Word)
            {
                tokens.Add(run.ToString().ToLowerInvariant());
            }
            else if (runKind == Kind.Cjk)
            {
                if (run.Length == 1)
                {
                    tokens.Add(run.ToString());
                }
                else
                {
                    for (var i = 0; i < run.Length - 1; i++)
                    {
                        tokens.Add(run.ToString(i, 2));
                    }
                }
            }
            run.Clear();
        }

        foreach (var c in text)
        {
            var kind = Classify(c);
            if (kind is null)
            {
                Flush();
                runKind = null;
                continue;
            }
            if (runKind != kind)
            {
                Flush();
                runKind = kind;
            }
            run.Append(c);
        }
        Flush();
        return tokens;
    }

    /// <summary>Index-time: tokenized text joined with spaces for FTS5.</summary>
    public static string TokenizeForIndex(string? text) => string.Join(' ', Tokenize(text));

    /// <summary>Query-time: unique tokens (AND semantics need no duplicates).</summary>
    public static List<string> TokenizeQuery(string? query) => Tokenize(query).Distinct().ToList();
}
