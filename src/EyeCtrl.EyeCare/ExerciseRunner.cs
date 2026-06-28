namespace EyeCtrl.EyeCare;

using EyeCtrl.EyeCare.Exercises;

/// <summary>Owns round-robin exercise selection (never the same twice in a row).</summary>
public sealed class ExerciseRunner
{
    private readonly IExercise[] _pool;
    private readonly Random _rng;
    private int _lastIndex = -1;

    /// <param name="pool">The exercise pool to rotate through.</param>
    /// <param name="seed">Seed for deterministic selection (tests).</param>
    public ExerciseRunner(IExercise[] pool, int seed)
    {
        _pool = pool;
        _rng = new Random(seed);
    }

    /// <summary>Returns an exercise, never the same as the previous call.</summary>
    public IExercise NextExercise()
    {
        if (_pool.Length == 1) return _pool[0];

        int idx;
        do { idx = _rng.Next(_pool.Length); } while (idx == _lastIndex);
        _lastIndex = idx;
        return _pool[idx];
    }
}
