using System.Linq.Expressions;

namespace DesktopCritters.Expr;

/// <summary>
/// A single formula compiled over a fixed list of named slots. Used wherever the
/// config wants one number out of one expression -- motion paths, for instance --
/// without the machinery of a full creature program.
/// </summary>
public sealed class ScalarProgram
{
    private readonly Func<double[], double> _fn;
    public string Source { get; }

    private ScalarProgram(Func<double[], double> fn, string source)
    {
        _fn = fn;
        Source = source;
    }

    /// <summary>Slot <c>i</c> of the register array is bound to <c>names[i]</c>.</summary>
    public static ScalarProgram Compile(string source, params string[] names)
    {
        var registers = Expression.Parameter(typeof(double[]), "r");

        var slots = new Dictionary<string, Expression>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
            slots[names[i]] = Expression.ArrayAccess(registers, Expression.Constant(i));

        var body = ExprCompiler.Parse(source, name => slots.TryGetValue(name, out var e) ? e : null);
        return new ScalarProgram(Expression.Lambda<Func<double[], double>>(body, registers).Compile(), source);
    }

    public double Evaluate(double[] registers) => _fn(registers);
}
