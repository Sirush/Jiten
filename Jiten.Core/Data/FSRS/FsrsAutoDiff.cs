namespace Jiten.Core.Data.FSRS;

internal enum AdOp : byte
{
    Leaf, Add, Sub, Mul, Div, Neg, Exp, Log, Pow, Min, Max, Clamp
}

internal sealed class AdTape
{
    private double[] _values;
    private byte[] _ops;
    private int[] _parentA;
    private int[] _parentB;
    private double[] _clampLo;
    private double[] _clampHi;
    private int _count;
    private readonly int[] _paramNodes;

    public AdTape(int capacity = 512, int parameterCount = 21)
    {
        _paramNodes = new int[parameterCount];
        _values = new double[capacity];
        _ops = new byte[capacity];
        _parentA = new int[capacity];
        _parentB = new int[capacity];
        _clampLo = new double[capacity];
        _clampHi = new double[capacity];
        Reset();
    }

    public void Reset()
    {
        _count = 0;
        Array.Fill(_paramNodes, -1);
    }

    private void Grow()
    {
        var cap = _values.Length * 2;
        Array.Resize(ref _values, cap);
        Array.Resize(ref _ops, cap);
        Array.Resize(ref _parentA, cap);
        Array.Resize(ref _parentB, cap);
        Array.Resize(ref _clampLo, cap);
        Array.Resize(ref _clampHi, cap);
    }

    private int Push(double value, AdOp op, int a, int b)
    {
        if (_count == _values.Length) Grow();
        var i = _count++;
        _values[i] = value;
        _ops[i] = (byte)op;
        _parentA[i] = a;
        _parentB[i] = b;
        return i;
    }

    public Var Const(double value) => new(Push(value, AdOp.Leaf, -1, -1), this);

    public Var Param(int index, double value)
    {
        var i = Push(value, AdOp.Leaf, -1, -1);
        _paramNodes[index] = i;
        return new Var(i, this);
    }

    public Var[] LoadParams(double[] parameters)
    {
        var vars = new Var[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            vars[i] = Param(i, parameters[i]);
        return vars;
    }

    internal double Value(int i) => _values[i];

    /// <summary>Replaces a node's forward value while keeping its gradient path (implicit-function lifts).</summary>
    internal Var WithValue(Var v, double value)
    {
        _values[v.Index] = value;
        return v;
    }

    internal Var Binary(AdOp op, Var a, Var b, double value) =>
        new(Push(value, op, a.Index, b.Index), this);

    internal Var Unary(AdOp op, Var a, double value) =>
        new(Push(value, op, a.Index, -1), this);

    internal Var ClampNode(Var a, double value, double lo, double hi)
    {
        var i = Push(value, AdOp.Clamp, a.Index, -1);
        _clampLo[i] = lo;
        _clampHi[i] = hi;
        return new Var(i, this);
    }

    public double[] Backward(Var output)
    {
        var n = _count;
        var grad = new double[n];
        grad[output.Index] = 1.0;

        for (var i = n - 1; i >= 0; i--)
        {
            var g = grad[i];
            if (g == 0) continue;

            var op = (AdOp)_ops[i];
            var a = _parentA[i];
            var b = _parentB[i];

            switch (op)
            {
                case AdOp.Leaf:
                    break;
                case AdOp.Add:
                    grad[a] += g;
                    grad[b] += g;
                    break;
                case AdOp.Sub:
                    grad[a] += g;
                    grad[b] -= g;
                    break;
                case AdOp.Mul:
                    grad[a] += g * _values[b];
                    grad[b] += g * _values[a];
                    break;
                case AdOp.Div:
                    var bv = _values[b];
                    grad[a] += g / bv;
                    grad[b] -= g * _values[a] / (bv * bv);
                    break;
                case AdOp.Neg:
                    grad[a] -= g;
                    break;
                case AdOp.Exp:
                    grad[a] += g * _values[i];
                    break;
                case AdOp.Log:
                    grad[a] += g / _values[a];
                    break;
                case AdOp.Pow:
                    var baseVal = _values[a];
                    if (baseVal > 0)
                    {
                        grad[a] += g * _values[b] * Math.Pow(baseVal, _values[b] - 1);
                        grad[b] += g * _values[i] * Math.Log(baseVal);
                    }
                    break;
                case AdOp.Min:
                    if (_values[a] <= _values[b])
                        grad[a] += g;
                    else
                        grad[b] += g;
                    break;
                case AdOp.Max:
                    if (_values[a] >= _values[b])
                        grad[a] += g;
                    else
                        grad[b] += g;
                    break;
                case AdOp.Clamp:
                    if (_values[a] >= _clampLo[i] && _values[a] <= _clampHi[i])
                        grad[a] += g;
                    break;
            }
        }

        var result = new double[_paramNodes.Length];
        for (var i = 0; i < _paramNodes.Length; i++)
        {
            if (_paramNodes[i] >= 0)
                result[i] = grad[_paramNodes[i]];
        }
        return result;
    }
}

internal readonly struct Var
{
    internal readonly int Index;
    internal readonly AdTape Tape;

    internal Var(int index, AdTape tape)
    {
        Index = index;
        Tape = tape;
    }

    public double Value => Tape.Value(Index);

    public static Var operator +(Var a, Var b) =>
        a.Tape.Binary(AdOp.Add, a, b, a.Value + b.Value);
    public static Var operator +(Var a, double b) => a + a.Tape.Const(b);
    public static Var operator +(double a, Var b) => b.Tape.Const(a) + b;

    public static Var operator -(Var a, Var b) =>
        a.Tape.Binary(AdOp.Sub, a, b, a.Value - b.Value);
    public static Var operator -(Var a, double b) => a - a.Tape.Const(b);
    public static Var operator -(double a, Var b) => b.Tape.Const(a) - b;

    public static Var operator *(Var a, Var b) =>
        a.Tape.Binary(AdOp.Mul, a, b, a.Value * b.Value);
    public static Var operator *(Var a, double b) => a * a.Tape.Const(b);
    public static Var operator *(double a, Var b) => b.Tape.Const(a) * b;

    public static Var operator /(Var a, Var b) =>
        a.Tape.Binary(AdOp.Div, a, b, a.Value / b.Value);
    public static Var operator /(Var a, double b) => a / a.Tape.Const(b);
    public static Var operator /(double a, Var b) => b.Tape.Const(a) / b;

    public static Var operator -(Var a) =>
        a.Tape.Unary(AdOp.Neg, a, -a.Value);

    public static Var Exp(Var a) => a.Tape.Unary(AdOp.Exp, a, Math.Exp(a.Value));
    public static Var Log(Var a) => a.Tape.Unary(AdOp.Log, a, Math.Log(a.Value));

    public static Var Pow(Var a, Var b) =>
        a.Tape.Binary(AdOp.Pow, a, b, Math.Pow(a.Value, b.Value));
    public static Var Pow(Var a, double b) => Pow(a, a.Tape.Const(b));
    public static Var Pow(double a, Var b) => Pow(b.Tape.Const(a), b);

    public static Var Min(Var a, Var b) =>
        a.Tape.Binary(AdOp.Min, a, b, Math.Min(a.Value, b.Value));

    public static Var Max(Var a, Var b) =>
        a.Tape.Binary(AdOp.Max, a, b, Math.Max(a.Value, b.Value));
    public static Var Max(Var a, double b) => Max(a, a.Tape.Const(b));

    public static Var Clamp(Var a, double lo, double hi) =>
        a.Tape.ClampNode(a, Math.Clamp(a.Value, lo, hi), lo, hi);
}
