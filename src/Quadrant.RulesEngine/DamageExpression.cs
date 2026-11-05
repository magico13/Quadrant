namespace Quadrant.RulesEngine;

/// <summary>
/// A structured dice expression, e.g. "2d8+3", "1d6", or "2d6x10". The engine rolls these
/// with an injectable RNG so combat is deterministic given a seed.
/// </summary>
public sealed record DamageExpression(int DiceCount, int Sides, int FlatBonus, int Multiplier = 1)
{
    /// <summary>
    /// Parses forms like "3d8+4", "1d6-1", "2d10", "2d6x10", or a flat number "5".
    /// </summary>
    public static DamageExpression From(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) throw new FormatException("Empty damage expression.");

        // Split off a trailing multiplier: "x N" or "× N"
        int multiplier = 1;
        var multMatch = System.Text.RegularExpressions.Regex.Match(t, @"\s*[x×]\s*(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (multMatch.Success)
        {
            multiplier = int.Parse(multMatch.Groups[1].Value);
            t = t[..multMatch.Index].Trim();
        }

        // Split off a trailing +/- bonus, if present.
        int opIdx = -1;
        for (int i = 1; i < t.Length; i++)
            if (t[i] is '+' or '-') { opIdx = i; break; }

        string dicePart = opIdx >= 0 ? t[..opIdx] : t;
        int bonus = 0;
        if (opIdx >= 0)
        {
            bonus = int.Parse(t[(opIdx + 1)..]);
            if (t[opIdx] == '-') bonus = -bonus;
        }

        // Parse the dice portion: "NdS" or a bare number.
        if (dicePart.Contains('d', StringComparison.OrdinalIgnoreCase))
        {
            var parts = dicePart.Split(['d', 'D'], 2);
            return new DamageExpression(int.Parse(parts[0]), int.Parse(parts[1]), bonus, multiplier);
        }

        // No dice — the whole thing is a flat value (possibly with sign).
        return new DamageExpression(0, 0, int.Parse(t), multiplier);
    }

    /// <summary>Rolls this expression using the provided dice roller.</summary>
    public int Roll(IDiceRoller dice, bool divertWeapons = false)
    {
        int total = 0;
        for (int i = 0; i < DiceCount; i++)
            total += divertWeapons ? Math.Max(2, dice.Roll(Sides)) : dice.Roll(Sides);
        if (Multiplier != 1) total *= Multiplier;
        return total + FlatBonus;
    }

    /// <summary>Minimum possible roll (all ones).</summary>
    public int Min => (DiceCount * Multiplier) + FlatBonus;

    /// <summary>Maximum possible roll (all maxes).</summary>
    public int Max => (DiceCount * Sides * Multiplier) + FlatBonus;

    public override string ToString()
    {
        if (DiceCount == 0) return FlatBonus.ToString();
        var mult = Multiplier != 1 ? $"x{Multiplier}" : "";
        var bonus = FlatBonus switch
        {
            0 => "",
            > 0 => $"+{FlatBonus}",
            _ => FlatBonus.ToString()
        };
        return $"{DiceCount}d{Sides}{mult}{bonus}";
    }
}
