namespace Quadrant.RulesEngine;

/// <summary>
/// Abstraction over dice rolling so the engine is deterministic given a seed,
/// and so tests can inject fixed results. The server uses a real RNG; tests
/// can use a seeded or scripted one.
/// </summary>
public interface IDiceRoller
{
    /// <summary>Rolls a single d20 (1-20).</summary>
    int RollD20();

    /// <summary>Rolls a die with the given number of sides (minimum 1).</summary>
    int Roll(int sides);

    /// <summary>Rolls multiple dice and sums them.</summary>
    int RollMultiple(int count, int sides);

    /// <summary>Rolls a d% (1-100) for critical damage system selection.</summary>
    int RollPercent();
}

/// <summary>
/// Default implementation using .NET's Random. The server creates one per game
/// with a seed so combat is reproducible.
/// </summary>
public sealed class DiceRoller : IDiceRoller
{
    private readonly Random _rng;

    public DiceRoller(int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public int RollD20() => _rng.Next(1, 21);

    public int Roll(int sides) => _rng.Next(1, sides + 1);

    public int RollMultiple(int count, int sides)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
            total += _rng.Next(1, sides + 1);
        return total;
    }

    public int RollPercent() => _rng.Next(1, 101);
}
