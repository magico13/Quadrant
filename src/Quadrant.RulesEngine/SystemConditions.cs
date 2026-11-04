using System.Text.Json.Serialization;

namespace Quadrant.RulesEngine;

/// <summary>
/// Severity of a critical damage condition on a ship system.
/// </summary>
public enum SystemCondition
{
    None,         // No damage
    Glitching,    // -2 penalty to actions using this system
    Malfunctioning, // -4 penalty; can't use "push" actions; power core affects all actions
    Wrecked       // Actions auto-fail; power core wrecks affect all crew
}

/// <summary>
/// The five key systems that can take critical damage.
/// </summary>
public enum ShipSystem
{
    LifeSupport,   // Affects all captain actions
    Sensors,       // Affects all science officer actions
    WeaponsArray,  // Affects gunner actions in the damaged arc
    Engines,       // Affects all pilot actions
    PowerCore      // Affects engineer actions; when malfunctioning/wrecked affects ALL crew
}

/// <summary>
/// Tracks critical damage conditions on a ship's systems. Each system can be in
/// one of four states (None → Glitching → Malfunctioning → Wrecked). When a
/// system is hit again, its condition escalates by one step.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class SystemConditions
{
    private readonly Dictionary<ShipSystem, SystemCondition> _conditions = new();
    public Dictionary<ShipSystem, SystemCondition> PersistedConditions => _conditions;

    /// <summary>Gets the current condition of a system (None if undamaged).</summary>
    public SystemCondition Get(ShipSystem system)
        => _conditions.TryGetValue(system, out var cond) ? cond : SystemCondition.None;

    /// <summary>
    /// Applies critical damage to a system, escalating its condition by one step.
    /// Returns the new condition.
    /// </summary>
    public SystemCondition ApplyCritical(ShipSystem system)
    {
        var current = Get(system);
        var next = current switch
        {
            SystemCondition.None => SystemCondition.Glitching,
            SystemCondition.Glitching => SystemCondition.Malfunctioning,
            SystemCondition.Malfunctioning => SystemCondition.Wrecked,
            SystemCondition.Wrecked => SystemCondition.Wrecked, // stays wrecked
            _ => SystemCondition.None
        };
        _conditions[system] = next;
        return next;
    }

    /// <summary>Repairs a system back to None (after an Engineering check).</summary>
    public void Repair(ShipSystem system)
        => _conditions[system] = SystemCondition.None;

    /// <summary>
    /// Gets the penalty this system's condition imposes on crew actions.
    /// Glitching: -2, Malfunctioning: -4, Wrecked: auto-fail (represented as int.MinValue).
    /// </summary>
    public int GetActionPenalty(ShipSystem system) => Get(system) switch
    {
        SystemCondition.None => 0,
        SystemCondition.Glitching => -2,
        SystemCondition.Malfunctioning => -4,
        SystemCondition.Wrecked => int.MinValue, // auto-fail
        _ => 0
    };

    /// <summary>
    /// Gets the global penalty from a malfunctioning/wrecked power core, which
    /// affects ALL crew actions (not just engineer actions).
    /// </summary>
    public int PowerCoreGlobalPenalty => Get(ShipSystem.PowerCore) switch
    {
        SystemCondition.Malfunctioning => -2,
        SystemCondition.Wrecked => -4,
        _ => 0
    };

    /// <summary>Whether any system is currently in a damaged state.</summary>
    public bool HasAnyDamage => _conditions.Values.Any(c => c != SystemCondition.None);

    /// <summary>Returns all systems that have a non-None condition.</summary>
    public IEnumerable<(ShipSystem System, SystemCondition Condition)> DamagedSystems()
        => _conditions.Where(kv => kv.Value != SystemCondition.None)
                      .Select(kv => (kv.Key, kv.Value));
}
