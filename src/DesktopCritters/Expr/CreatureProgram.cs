using System.Linq.Expressions;

namespace DesktopCritters.Expr;

/// <summary>One named intermediate in a creature formula, e.g. <c>k = (4+cos(y))*cos(i)</c>.</summary>
public sealed record VarDef(string Name, string Expr);

/// <summary>
/// The formula half of a creature: everything needed to turn a point index and a
/// time into a local-space coordinate.
/// </summary>
public sealed class CreatureFormula
{
    public string Name { get; init; } = "unnamed";
    public int Points { get; init; } = 20000;

    /// <summary>Evaluated in order; each becomes a local visible to later ones.</summary>
    public IReadOnlyList<VarDef> Vars { get; init; } = Array.Empty<VarDef>();

    public string Px { get; init; } = "0";
    public string Py { get; init; } = "0";

    /// <summary>Per-point intensity. Constant 1 unless the creature wants shading.</summary>
    public string Weight { get; init; } = "1";

    /// <summary>Radians per second of body-cycle advance at speed 1.</summary>
    public double BodyRate { get; init; } = Math.PI / 80 * 60;

    /// <summary>Degrees to rotate local space so that +X points the way the creature swims.</summary>
    public double Facing { get; init; }

    /// <summary>Which extent the configured <c>size</c> measures: forward | span | max.</summary>
    public string SizeAxis { get; init; } = "forward";

    public IReadOnlyDictionary<string, double> Params { get; init; } = new Dictionary<string, double>();
}

/// <summary>
/// A creature formula compiled to delegates.
///
/// Two things make this fast enough to run a 20,000-point creature every frame:
///
/// 1. The formula is emitted as LINQ expression trees and JIT-compiled, so a point
///    costs one delegate call rather than a tree-walk over every node.
///
/// 2. Vars that do not depend on <c>t</c> are hoisted. In a typical creature most of
///    the work -- the spine parameterisation, the point scatter, the distance field --
///    is a function of the point index alone, so it is evaluated once at load into a
///    per-point cache and only the genuinely time-varying tail runs per frame. For the
///    stock fish that removes about two thirds of the trigonometry from the hot loop.
///
/// Registers are passed in by the caller, so several critters can share one compiled
/// body while each keeps its own parameter values and its own cache.
/// </summary>
public sealed class CreatureProgram
{
    public const int RegIndex = 0;   // i -- point index, 0..n-1
    public const int RegTime = 1;    // t -- body phase in radians
    public const int RegPointCount = 2;  // n -- total point count
    public const int RegOutX = 3;
    public const int RegOutY = 4;
    public const int RegOutWeight = 5;
    public const int FirstParamSlot = 6;

    public string Name { get; }
    public int RegisterCount { get; }
    public IReadOnlyDictionary<string, int> ParamSlots { get; }
    public IReadOnlyDictionary<string, double> ParamDefaults { get; }
    public double BodyRate { get; }
    public double FacingRadians { get; }
    public int DefaultPoints { get; }

    /// <summary>
    /// Which measured extent the configured <c>size</c> refers to. A fish is longest
    /// along the way it swims, but a bird's wingspan is several times its body length,
    /// so "300 pixels" has to mean different axes for the two.
    /// </summary>
    public string SizeAxis { get; }

    /// <summary>How many time-invariant values are cached per point.</summary>
    public int CachedPerPoint { get; }

    /// <summary>Names of the hoisted vars, for diagnostics.</summary>
    public IReadOnlyList<string> CachedNames { get; }

    /// <summary>(registers, cache, rowOffset) -> fills the cache row for one point.</summary>
    private readonly Action<double[], double[], int>? _runStatic;

    /// <summary>(registers, cache, rowOffset) -> computes the outputs for one point.</summary>
    private readonly Action<double[], double[], int> _runDynamic;

    private CreatureProgram(
        string name,
        Action<double[], double[], int>? runStatic,
        Action<double[], double[], int> runDynamic,
        int registerCount,
        Dictionary<string, int> paramSlots,
        IReadOnlyDictionary<string, double> defaults,
        double bodyRate, double facingDegrees, int defaultPoints,
        List<string> cachedNames, string sizeAxis)
    {
        SizeAxis = sizeAxis;
        Name = name;
        _runStatic = runStatic;
        _runDynamic = runDynamic;
        RegisterCount = registerCount;
        ParamSlots = paramSlots;
        ParamDefaults = defaults;
        BodyRate = bodyRate;
        FacingRadians = facingDegrees * Math.PI / 180.0;
        DefaultPoints = defaultPoints;
        CachedNames = cachedNames;
        CachedPerPoint = cachedNames.Count;
    }

    public static CreatureProgram Compile(CreatureFormula formula)
    {
        // ---- names available to formulas -----------------------------------
        var inputNames = new HashSet<string>(StringComparer.Ordinal) { "i", "t", "n" };

        var paramSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextSlot = FirstParamSlot;
        foreach (var name in formula.Params.Keys)
        {
            if (inputNames.Contains(name))
                throw new InvalidOperationException($"Creature '{formula.Name}': parameter '{name}' shadows a built-in input");
            paramSlots[name] = nextSlot++;
        }

        // ---- which vars actually depend on time? ---------------------------
        // A var is time-varying if it mentions `t` or mentions another time-varying
        // var. Vars may only reference earlier ones, so one forward pass is enough.
        var timeVarying = new HashSet<string>(StringComparer.Ordinal) { "t" };
        var declared = new HashSet<string>(inputNames.Concat(paramSlots.Keys), StringComparer.Ordinal);

        var staticVars = new List<VarDef>();
        var dynamicVars = new List<VarDef>();

        // Names read by the per-frame pass. Only these need a cache slot; a static var
        // that merely feeds other static vars can stay a local in the hoisted pass.
        var readPerFrame = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in formula.Vars)
        {
            if (declared.Contains(def.Name))
                throw new InvalidOperationException($"Creature '{formula.Name}': '{def.Name}' is declared twice (or shadows an input)");

            var referenced = ReferencedNames(formula.Name, def.Name, def.Expr, declared);
            bool isDynamic = referenced.Overlaps(timeVarying);

            if (isDynamic)
            {
                timeVarying.Add(def.Name);
                dynamicVars.Add(def);
                readPerFrame.UnionWith(referenced);
            }
            else
            {
                staticVars.Add(def);
            }

            declared.Add(def.Name);
        }

        foreach (var output in new[] { formula.Px, formula.Py, formula.Weight })
            readPerFrame.UnionWith(ReferencedNames(formula.Name, "output", output, declared));

        var cached = staticVars.Where(v => readPerFrame.Contains(v.Name)).Select(v => v.Name).ToList();

        var cacheSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < cached.Count; k++)
            cacheSlots[cached[k]] = k;

        // ---- emit the hoisted pass ------------------------------------------
        Action<double[], double[], int>? runStatic = null;
        if (cacheSlots.Count > 0)
            runStatic = EmitStatic(formula, staticVars, paramSlots, cacheSlots);

        // ---- emit the per-frame pass -----------------------------------------
        var runDynamic = EmitDynamic(formula, dynamicVars, paramSlots, cacheSlots);

        return new CreatureProgram(
            formula.Name, runStatic, runDynamic, nextSlot, paramSlots, formula.Params,
            formula.BodyRate, formula.Facing, formula.Points, cached, formula.SizeAxis);
    }

    /// <summary>
    /// Parses an expression purely to discover which known names it mentions.
    /// Cheap (load time only) and far more reliable than pattern-matching the text,
    /// which would trip over identifiers such as <c>turn</c> that merely contain "t".
    /// </summary>
    private static HashSet<string> ReferencedNames(string creature, string field, string source, HashSet<string> known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            ExprCompiler.Parse(source, name =>
            {
                if (!known.Contains(name))
                    return null;   // fall through to constants, or a clear "unknown name" error
                seen.Add(name);
                return Expression.Constant(0.0);
            });
        }
        catch (ExprSyntaxException ex)
        {
            throw new InvalidOperationException($"Creature '{creature}', field '{field}': {ex.Message}", ex);
        }
        return seen;
    }

    private static Action<double[], double[], int> EmitStatic(
        CreatureFormula formula, List<VarDef> staticVars,
        Dictionary<string, int> paramSlots, Dictionary<string, int> cacheSlots)
    {
        var registers = Expression.Parameter(typeof(double[]), "r");
        var cache = Expression.Parameter(typeof(double[]), "c");
        var offset = Expression.Parameter(typeof(int), "o");

        var scope = BaseScope(registers, paramSlots);
        var locals = new List<ParameterExpression>();
        var body = new List<Expression>();

        foreach (var def in staticVars)
        {
            var compiled = ParseOrThrow(formula.Name, def.Name, def.Expr, n => scope.GetValueOrDefault(n));
            var local = Expression.Variable(typeof(double), def.Name);

            locals.Add(local);
            body.Add(Expression.Assign(local, compiled));
            if (cacheSlots.TryGetValue(def.Name, out int slot))
                body.Add(Expression.Assign(CacheSlot(cache, offset, slot), local));
            scope[def.Name] = local;
        }

        return Expression.Lambda<Action<double[], double[], int>>(
            Expression.Block(locals, body), registers, cache, offset).Compile();
    }

    private static Action<double[], double[], int> EmitDynamic(
        CreatureFormula formula, List<VarDef> dynamicVars,
        Dictionary<string, int> paramSlots, Dictionary<string, int> cacheSlots)
    {
        var registers = Expression.Parameter(typeof(double[]), "r");
        var cache = Expression.Parameter(typeof(double[]), "c");
        var offset = Expression.Parameter(typeof(int), "o");

        var scope = BaseScope(registers, paramSlots);

        // Hoisted vars are read straight out of the cache row.
        foreach (var (name, slot) in cacheSlots)
            scope[name] = CacheSlot(cache, offset, slot);

        var locals = new List<ParameterExpression>();
        var body = new List<Expression>();

        foreach (var def in dynamicVars)
        {
            var compiled = ParseOrThrow(formula.Name, def.Name, def.Expr, n => scope.GetValueOrDefault(n));
            var local = Expression.Variable(typeof(double), def.Name);

            locals.Add(local);
            body.Add(Expression.Assign(local, compiled));
            scope[def.Name] = local;
        }

        Expression Resolve(string n) => scope.GetValueOrDefault(n)!;

        body.Add(Expression.Assign(Slot(registers, RegOutX), ParseOrThrow(formula.Name, "px", formula.Px, Resolve)));
        body.Add(Expression.Assign(Slot(registers, RegOutY), ParseOrThrow(formula.Name, "py", formula.Py, Resolve)));
        body.Add(Expression.Assign(Slot(registers, RegOutWeight), ParseOrThrow(formula.Name, "weight", formula.Weight, Resolve)));

        return Expression.Lambda<Action<double[], double[], int>>(
            Expression.Block(locals, body), registers, cache, offset).Compile();
    }

    private static Dictionary<string, Expression> BaseScope(ParameterExpression registers, Dictionary<string, int> paramSlots)
    {
        var scope = new Dictionary<string, Expression>(StringComparer.Ordinal)
        {
            ["i"] = Slot(registers, RegIndex),
            ["t"] = Slot(registers, RegTime),
            ["n"] = Slot(registers, RegPointCount),
        };
        foreach (var (name, slot) in paramSlots)
            scope[name] = Slot(registers, slot);
        return scope;
    }

    private static Expression ParseOrThrow(string creature, string field, string source, SymbolResolver resolve)
    {
        try
        {
            return ExprCompiler.Parse(source, resolve);
        }
        catch (ExprSyntaxException ex)
        {
            throw new InvalidOperationException($"Creature '{creature}', field '{field}': {ex.Message}", ex);
        }
    }

    private static Expression Slot(ParameterExpression registers, int index)
        => Expression.ArrayAccess(registers, Expression.Constant(index));

    private static Expression CacheSlot(ParameterExpression cache, ParameterExpression offset, int slot)
        => Expression.ArrayAccess(cache, Expression.Add(offset, Expression.Constant(slot)));

    // -----------------------------------------------------------------------

    /// <summary>Fresh register bank seeded with this creature's parameter defaults.</summary>
    public double[] NewRegisters(int points)
    {
        var registers = new double[RegisterCount];
        registers[RegPointCount] = points;
        foreach (var (name, slot) in ParamSlots)
            registers[slot] = ParamDefaults.TryGetValue(name, out double v) ? v : 0.0;
        return registers;
    }

    public double[] NewCache(int points) => new double[(long)points * CachedPerPoint <= int.MaxValue
        ? points * CachedPerPoint
        : throw new InvalidOperationException($"Creature '{Name}': {points} points is too many to cache")];

    /// <summary>
    /// Evaluates every time-invariant var once per point. Must be called after the
    /// parameter registers are set, and again if any of them change.
    /// </summary>
    public void BuildCache(double[] registers, int points, double[] cache)
    {
        if (_runStatic is null) return;

        for (int i = 0; i < points; i++)
        {
            registers[RegIndex] = i;
            _runStatic(registers, cache, i * CachedPerPoint);
        }
    }

    /// <summary>Evaluates one point. Results land in the OutX/OutY/OutWeight slots.</summary>
    public void Evaluate(double[] registers, double[] cache, int index, double time)
    {
        registers[RegIndex] = index;
        registers[RegTime] = time;
        _runDynamic(registers, cache, index * CachedPerPoint);
    }

    /// <summary>Picks the half-extent that <c>size</c> is measured against.</summary>
    public double ReferenceHalfExtent(double halfWidth, double halfHeight) => SizeAxis.ToLowerInvariant() switch
    {
        "span" or "y" => halfHeight,
        "max" or "largest" => Math.Max(halfWidth, halfHeight),
        _ => halfWidth,
    };

    /// <summary>
    /// Samples the point cloud across a full body cycle to find its local extent.
    /// Needed to scale a creature to a requested pixel length and to size the
    /// dirty rectangle without guessing.
    /// </summary>
    public (double HalfWidth, double HalfHeight) MeasureExtent(
        double[] registers, double[] cache, int points, int phaseSamples = 24)
    {
        double maxX = 1e-6, maxY = 1e-6;
        double cos = Math.Cos(FacingRadians), sin = Math.Sin(FacingRadians);

        // Sampling every point at every phase is wasteful; a decimated sweep lands
        // within a percent or two and keeps startup instant.
        int stride = Math.Max(1, points / 2000);

        for (int p = 0; p < phaseSamples; p++)
        {
            double time = p * (Math.Tau / phaseSamples);
            for (int i = 0; i < points; i += stride)
            {
                Evaluate(registers, cache, i, time);
                double x = registers[RegOutX], y = registers[RegOutY];
                if (double.IsNaN(x) || double.IsNaN(y)) continue;

                double rx = x * cos - y * sin;
                double ry = x * sin + y * cos;

                if (Math.Abs(rx) > maxX) maxX = Math.Abs(rx);
                if (Math.Abs(ry) > maxY) maxY = Math.Abs(ry);
            }
        }

        return (maxX, maxY);
    }
}
