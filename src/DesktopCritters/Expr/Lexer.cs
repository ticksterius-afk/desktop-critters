using System.Globalization;

namespace DesktopCritters.Expr;

public sealed class ExprSyntaxException : Exception
{
    public ExprSyntaxException(string message, string source, int position)
        : base($"{message} (at offset {position} in \"{source}\")") { }
}

internal enum TokenKind { Number, Ident, Operator, LParen, RParen, Comma, End }

internal readonly struct Token
{
    public readonly TokenKind Kind;
    public readonly string Text;
    public readonly double Number;
    public readonly int Position;

    public Token(TokenKind kind, string text, double number, int position)
    {
        Kind = kind; Text = text; Number = number; Position = position;
    }

    public bool Is(string op) => Kind == TokenKind.Operator && Text == op;
    public override string ToString() => Kind == TokenKind.End ? "<end>" : Text;
}

/// <summary>
/// Turns a formula string into tokens. The accepted surface is deliberately close
/// to JavaScript so expressions lifted straight out of a p5.js or Dwitter sketch
/// paste in with minimal editing.
/// </summary>
internal static class Lexer
{
    /// <summary>Longest-match-first, so "&lt;=" never lexes as "&lt;" then "=".</summary>
    private static readonly string[] Operators =
    {
        "<=", ">=", "==", "!=", "&&", "||",
        "+", "-", "*", "/", "%", "^", "<", ">", "!", "?", ":",
    };

    public static List<Token> Tokenize(string src)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < src.Length)
        {
            char c = src[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int start = i;
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) i++;

                // Scientific notation: 2e4, 1.5e-3
                if (i < src.Length && (src[i] == 'e' || src[i] == 'E'))
                {
                    int save = i;
                    i++;
                    if (i < src.Length && (src[i] == '+' || src[i] == '-')) i++;
                    if (i < src.Length && char.IsDigit(src[i]))
                    {
                        while (i < src.Length && char.IsDigit(src[i])) i++;
                    }
                    else
                    {
                        i = save; // not an exponent after all, e.g. "2e" then an identifier
                    }
                }

                string text = src[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new ExprSyntaxException($"Bad number '{text}'", src, start);

                tokens.Add(new Token(TokenKind.Number, text, value, start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Ident, src[start..i], 0, start));
                continue;
            }

            if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(", 0, i++)); continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")", 0, i++)); continue; }
            if (c == ',') { tokens.Add(new Token(TokenKind.Comma, ",", 0, i++)); continue; }

            string? op = null;
            foreach (var candidate in Operators)
            {
                if (i + candidate.Length <= src.Length && string.CompareOrdinal(src, i, candidate, 0, candidate.Length) == 0)
                {
                    op = candidate;
                    break;
                }
            }

            if (op is null)
                throw new ExprSyntaxException($"Unexpected character '{c}'", src, i);

            tokens.Add(new Token(TokenKind.Operator, op, 0, i));
            i += op.Length;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, 0, src.Length));
        return tokens;
    }
}
