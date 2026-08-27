using System.Linq.Expressions;

namespace DesktopCritters.Expr;

/// <summary>Resolves a bare identifier to an expression, or null if unknown.</summary>
public delegate Expression? SymbolResolver(string name);

/// <summary>
/// Precedence-climbing parser that emits LINQ expression trees rather than an
/// interpreter AST. The trees get <c>.Compile()</c>d to real delegates, so a
/// 20,000-point creature costs one delegate call per point instead of tens of
/// thousands of virtual node visits -- roughly two orders of magnitude cheaper,
/// which is what makes formula-driven rendering viable at all.
///
/// Grammar notes, chosen to match JavaScript so p5.js sketches port cleanly:
///   * <c>%</c> is fmod with the sign of the dividend (same as JS, not Python).
///   * <c>^</c> is exponentiation, NOT bitwise xor, and binds tighter than unary
///     minus so <c>-2^2</c> is <c>-4</c>.
///   * comparisons and <c>&amp;&amp;</c>/<c>||</c> yield 1.0 or 0.0, so they compose
///     with arithmetic: <c>(x &gt; 0) * 5</c> works.
/// </summary>
public static class ExprCompiler
{
    private static readonly Expression Zero = Expression.Constant(0.0);
    private static readonly Expression One = Expression.Constant(1.0);

    public static Expression Parse(string source, SymbolResolver resolve)
    {
        var parser = new Parser(Lexer.Tokenize(source), source, resolve);
        var result = parser.ParseTernary();
        parser.ExpectEnd();
        return result;
    }

    /// <summary>Compiles a formula that depends on nothing but constants.</summary>
    public static double EvaluateConstant(string source)
    {
        var body = Parse(source, _ => null);
        return Expression.Lambda<Func<double>>(body).Compile()();
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly string _source;
        private readonly SymbolResolver _resolve;
        private int _pos;

        public Parser(List<Token> tokens, string source, SymbolResolver resolve)
        {
            _tokens = tokens; _source = source; _resolve = resolve;
        }

        private Token Peek() => _tokens[_pos];
        private Token Next() => _tokens[_pos++];

        public void ExpectEnd()
        {
            if (Peek().Kind != TokenKind.End)
                throw new ExprSyntaxException($"Unexpected '{Peek()}' after end of expression", _source, Peek().Position);
        }

        /// <summary>Binary precedence; 0 means "not a binary operator".</summary>
        private static int Precedence(string op) => op switch
        {
            "||" => 1,
            "&&" => 2,
            "==" or "!=" => 3,
            "<" or "<=" or ">" or ">=" => 4,
            "+" or "-" => 5,
            "*" or "/" or "%" => 6,
            _ => 0,
        };

        public Expression ParseTernary()
        {
            var condition = ParseBinary(1);
            if (!Peek().Is("?"))
                return condition;

            Next();
            var whenTrue = ParseTernary();

            if (!Peek().Is(":"))
                throw new ExprSyntaxException("Expected ':' in ternary", _source, Peek().Position);
            Next();

            var whenFalse = ParseTernary();
            return Expression.Condition(ToBool(condition), whenTrue, whenFalse);
        }

        private Expression ParseBinary(int minPrecedence)
        {
            var left = ParseUnary();

            while (true)
            {
                var token = Peek();
                if (token.Kind != TokenKind.Operator)
                    break;

                int precedence = Precedence(token.Text);
                if (precedence == 0 || precedence < minPrecedence)
                    break;

                Next();
                var right = ParseBinary(precedence + 1); // every binary operator is left-associative
                left = MakeBinary(token, left, right);
            }

            return left;
        }

        private Expression ParseUnary()
        {
            var token = Peek();

            if (token.Is("-")) { Next(); return Expression.Negate(ParseUnary()); }
            if (token.Is("+")) { Next(); return ParseUnary(); }
            if (token.Is("!")) { Next(); return FromBool(Expression.Not(ToBool(ParseUnary()))); }

            return ParsePower();
        }

        private Expression ParsePower()
        {
            var value = ParsePrimary();
            if (!Peek().Is("^"))
                return value;

            Next();
            // Right-associative, and the exponent goes through ParseUnary so 2^-1 parses.
            var exponent = ParseUnary();
            return Expression.Call(
                typeof(Math).GetMethod(nameof(Math.Pow), new[] { typeof(double), typeof(double) })!,
                value, exponent);
        }

        private Expression ParsePrimary()
        {
            var token = Next();

            switch (token.Kind)
            {
                case TokenKind.Number:
                    return Expression.Constant(token.Number);

                case TokenKind.LParen:
                {
                    var inner = ParseTernary();
                    if (Peek().Kind != TokenKind.RParen)
                        throw new ExprSyntaxException("Expected ')'", _source, Peek().Position);
                    Next();
                    return inner;
                }

                case TokenKind.Ident:
                    return Peek().Kind == TokenKind.LParen
                        ? ParseCall(token)
                        : ResolveIdentifier(token);

                default:
                    throw new ExprSyntaxException($"Unexpected '{token}'", _source, token.Position);
            }
        }

        private Expression ParseCall(Token name)
        {
            Next(); // consume '('
            var args = new List<Expression>();

            if (Peek().Kind != TokenKind.RParen)
            {
                while (true)
                {
                    args.Add(ParseTernary());
                    if (Peek().Kind == TokenKind.Comma) { Next(); continue; }
                    break;
                }
            }

            if (Peek().Kind != TokenKind.RParen)
                throw new ExprSyntaxException($"Expected ')' closing {name.Text}(", _source, Peek().Position);
            Next();

            string key = $"{name.Text}/{args.Count}";
            if (Builtins.Functions.TryGetValue(key, out var emit))
                return emit(args.ToArray());

            string detail = Builtins.KnownNames.Contains(name.Text)
                ? $"'{name.Text}' does not take {args.Count} argument(s)"
                : $"Unknown function '{name.Text}'";
            throw new ExprSyntaxException(detail, _source, name.Position);
        }

        private Expression ResolveIdentifier(Token token)
        {
            var resolved = _resolve(token.Text);
            if (resolved is not null)
                return resolved;

            if (Builtins.Constants.TryGetValue(token.Text, out double constant))
                return Expression.Constant(constant);

            throw new ExprSyntaxException($"Unknown name '{token.Text}'", _source, token.Position);
        }

        private Expression MakeBinary(Token op, Expression l, Expression r) => op.Text switch
        {
            "+" => Expression.Add(l, r),
            "-" => Expression.Subtract(l, r),
            "*" => Expression.Multiply(l, r),
            "/" => Expression.Divide(l, r),
            "%" => Expression.Modulo(l, r),
            "<" => FromBool(Expression.LessThan(l, r)),
            "<=" => FromBool(Expression.LessThanOrEqual(l, r)),
            ">" => FromBool(Expression.GreaterThan(l, r)),
            ">=" => FromBool(Expression.GreaterThanOrEqual(l, r)),
            "==" => FromBool(Expression.Equal(l, r)),
            "!=" => FromBool(Expression.NotEqual(l, r)),
            "&&" => FromBool(Expression.AndAlso(ToBool(l), ToBool(r))),
            "||" => FromBool(Expression.OrElse(ToBool(l), ToBool(r))),
            _ => throw new ExprSyntaxException($"Unsupported operator '{op.Text}'", _source, op.Position),
        };
    }

    /// <summary>
    /// Double to bool. Written as <c>abs(x) &gt; 0</c> rather than <c>x != 0</c> so
    /// that NaN is falsy, matching JavaScript truthiness.
    /// </summary>
    private static Expression ToBool(Expression value)
    {
        if (value.Type == typeof(bool))
            return value;

        var abs = Expression.Call(typeof(Math).GetMethod(nameof(Math.Abs), new[] { typeof(double) })!, value);
        return Expression.GreaterThan(abs, Zero);
    }

    private static Expression FromBool(Expression condition) => Expression.Condition(condition, One, Zero);
}
