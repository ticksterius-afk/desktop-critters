using System.Linq.Expressions;
using System.Reflection;

namespace DesktopCritters.Expr;

/// <summary>Runtime helpers that have no direct <see cref="Math"/> equivalent.</summary>
public static class Fn
{
    public static double Mag(double x, double y) => Math.Sqrt(x * x + y * y);
    public static double Dist(double x0, double y0, double x1, double y1) => Mag(x1 - x0, y1 - y0);
    public static double Fract(double x) => x - Math.Floor(x);
    public static double Saturate(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    public static double Sq(double x) => x * x;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Step(double edge, double x) => x < edge ? 0.0 : 1.0;

    public static double Smoothstep(double e0, double e1, double x)
    {
        double t = Saturate((x - e0) / (e1 - e0 == 0 ? 1e-9 : e1 - e0));
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Always-positive modulo, unlike the <c>%</c> operator.</summary>
    public static double Wrap(double x, double m)
    {
        if (m == 0) return 0;
        double r = x - m * Math.Floor(x / m);
        return r;
    }

    /// <summary>Maps <paramref name="x"/> from one range to another without clamping.</summary>
    public static double Map(double x, double a0, double a1, double b0, double b1)
        => b0 + (b1 - b0) * ((x - a0) / (a1 - a0 == 0 ? 1e-9 : a1 - a0));

    public static double Select(double cond, double a, double b) => cond != 0 ? a : b;
}

/// <summary>
/// The function and constant vocabulary available inside creature formulas.
/// Keyed by "name/arity" so <c>noise(x)</c> and <c>noise(x, y)</c> can coexist.
/// </summary>
internal static class Builtins
{
    public static readonly Dictionary<string, double> Constants = new(StringComparer.Ordinal)
    {
        ["PI"] = Math.PI,
        ["TAU"] = Math.Tau,
        ["TWO_PI"] = Math.Tau,
        ["HALF_PI"] = Math.PI / 2,
        ["QUARTER_PI"] = Math.PI / 4,
        ["E"] = Math.E,
        ["PHI"] = 1.618033988749895,
    };

    public static readonly Dictionary<string, Func<Expression[], Expression>> Functions = Build();

    /// <summary>Names accepted regardless of arity; used for friendlier error messages.</summary>
    public static readonly HashSet<string> KnownNames =
        new(Functions.Keys.Select(k => k[..k.IndexOf('/')]), StringComparer.Ordinal);

    private static Dictionary<string, Func<Expression[], Expression>> Build()
    {
        var map = new Dictionary<string, Func<Expression[], Expression>>(StringComparer.Ordinal);

        void Math1(string name, string method) => map[$"{name}/1"] = a => MathCall(method, a);
        void Math2(string name, string method) => map[$"{name}/2"] = a => MathCall(method, a);
        void Helper(string name, int arity, string method)
            => map[$"{name}/{arity}"] = a => Expression.Call(HelperMethod(method, arity), a);

        Math1("sin", nameof(Math.Sin));
        Math1("cos", nameof(Math.Cos));
        Math1("tan", nameof(Math.Tan));
        Math1("asin", nameof(Math.Asin));
        Math1("acos", nameof(Math.Acos));
        Math1("atan", nameof(Math.Atan));
        Math1("sinh", nameof(Math.Sinh));
        Math1("cosh", nameof(Math.Cosh));
        Math1("tanh", nameof(Math.Tanh));
        Math1("sqrt", nameof(Math.Sqrt));
        Math1("cbrt", nameof(Math.Cbrt));
        Math1("abs", nameof(Math.Abs));
        Math1("exp", nameof(Math.Exp));
        Math1("log", nameof(Math.Log));
        Math1("log2", nameof(Math.Log2));
        Math1("log10", nameof(Math.Log10));
        Math1("floor", nameof(Math.Floor));
        Math1("ceil", nameof(Math.Ceiling));
        Math1("round", nameof(Math.Round));

        Math2("atan2", nameof(Math.Atan2));
        Math2("pow", nameof(Math.Pow));
        Math2("min", nameof(Math.Min));
        Math2("max", nameof(Math.Max));

        // Math.Sign returns int; widen so every node in the tree stays double.
        map["sign/1"] = a => Expression.Convert(
            Expression.Call(typeof(Math).GetMethod(nameof(Math.Sign), new[] { typeof(double) })!, a),
            typeof(double));

        map["clamp/3"] = a => Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Clamp), new[] { typeof(double), typeof(double), typeof(double) })!, a);

        Helper("mag", 2, nameof(Fn.Mag));
        Helper("hypot", 2, nameof(Fn.Mag));
        Helper("dist", 4, nameof(Fn.Dist));
        Helper("fract", 1, nameof(Fn.Fract));
        Helper("saturate", 1, nameof(Fn.Saturate));
        Helper("sq", 1, nameof(Fn.Sq));
        Helper("lerp", 3, nameof(Fn.Lerp));
        Helper("mix", 3, nameof(Fn.Lerp));
        Helper("step", 2, nameof(Fn.Step));
        Helper("smoothstep", 3, nameof(Fn.Smoothstep));
        Helper("wrap", 2, nameof(Fn.Wrap));
        Helper("map", 5, nameof(Fn.Map));
        Helper("select", 3, nameof(Fn.Select));

        map["noise/1"] = a => Expression.Call(
            typeof(Noise).GetMethod(nameof(Noise.At), new[] { typeof(double) })!, a);
        map["noise/2"] = a => Expression.Call(
            typeof(Noise).GetMethod(nameof(Noise.At), new[] { typeof(double), typeof(double) })!, a);
        map["snoise/1"] = a => Expression.Call(
            typeof(Noise).GetMethod(nameof(Noise.Signed), new[] { typeof(double) })!, a);
        map["fbm/2"] = a => Expression.Call(
            typeof(Noise).GetMethod(nameof(Noise.Fbm), new[] { typeof(double), typeof(double) })!, a);

        return map;
    }

    private static Expression MathCall(string method, Expression[] args)
    {
        var types = new Type[args.Length];
        Array.Fill(types, typeof(double));
        var mi = typeof(Math).GetMethod(method, BindingFlags.Public | BindingFlags.Static, types)
                 ?? throw new InvalidOperationException($"Math.{method} with {args.Length} double args not found");
        return Expression.Call(mi, args);
    }

    private static MethodInfo HelperMethod(string method, int arity)
    {
        var types = new Type[arity];
        Array.Fill(types, typeof(double));
        return typeof(Fn).GetMethod(method, BindingFlags.Public | BindingFlags.Static, types)
               ?? throw new InvalidOperationException($"Fn.{method} with {arity} double args not found");
    }
}
