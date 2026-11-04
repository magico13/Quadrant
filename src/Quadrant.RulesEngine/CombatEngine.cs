namespace Quadrant.RulesEngine;

/// <summary>
/// An event emitted during combat resolution, used to build a log and notify clients.
/// </summary>
public sealed record CombatEvent(string Phase, string ShipId, string Message)
{
    public static CombatEvent Info(string phase, string shipId, string msg) => new(phase, shipId, msg);
}

/// <summary>
/// The authoritative combat engine. Owns the game state and advances it through
/// the Engineering → Helm → Gunnery loop. All mutations go through this class so
/// the server can validate player intents before applying them.
///
/// This is a pure logic class: no I/O, no networking. It takes a GameState and an
/// IDiceRoller, and produces events as it resolves actions.
/// </summary>
public sealed class CombatEngine
{
    private readonly GameState _state;
    private readonly IDiceRoller _dice;

    /// <summary>Events emitted during the last action resolution (for logging/broadcast).</summary>
    public List<CombatEvent> LastEvents { get; } = [];

    public CombatEngine(GameState state, IDiceRoller dice)
    {
        _state = state;
        _dice = dice;
    }

    public GameState State => _state;

    // ────────────────────────────────────────────────
    //  Round / Phase lifecycle
    // ────────────────────────────────────────────────

    /// <summary>
    /// Starts combat from the Setup phase. Validates that ships are placed,
    /// then begins round 1 with the Engineering phase.
    /// </summary>
    public bool StartCombat()
    {
        if (_state.Phase != CombatPhase.Setup) return false;
        if (_state.Ships.Count < 2) return false;

        _state.Phase = CombatPhase.Engineering;
        _state.Round = 1;
        LastEvents.Add(new CombatEvent("Start", "", "Combat begins! Round 1."));
        return true;
    }

    /// <summary>
    /// Advances to the next phase. Called after all ships have acted in the current phase.
    /// </summary>
    public void AdvancePhase()
    {
        switch (_state.Phase)
        {
            case CombatPhase.Engineering:
                _state.Phase = CombatPhase.Helm;
                ComputeActionOrder();
                LastEvents.Add(new CombatEvent("Helm", "", $"Round {_state.Round} — Helm phase. Order: {string.Join(" → ", _state.ActionOrder.Select(id => _state.GetShip(id)?.Name ?? id))}."));
                break;

            case CombatPhase.Helm:
                _state.Phase = CombatPhase.Gunnery;
                _state.CurrentActorIndex = 0;
                LastEvents.Add(new CombatEvent("Gunnery", "", "Gunnery phase."));
                break;

            case CombatPhase.Gunnery:
                // End of round — check for victory, then start next round.
                CheckVictory();
                if (!_state.IsComplete)
                {
                    _state.Round++;
                    _state.Phase = CombatPhase.Engineering;
                    foreach (var ship in _state.Ships)
                        ship.BeginRound();
                    LastEvents.Add(new CombatEvent("Engineering", "", $"Round {_state.Round} — Engineering phase."));
                }
                break;
        }
    }

    /// <summary>
    /// Computes the helm/gunnery action order for this round. Pilots roll a Piloting
    /// check; lowest result moves first. Ships with no pilot roll 0.
    /// </summary>
    private void ComputeActionOrder()
    {
        var results = new List<(string Id, int Roll)>();

        foreach (var ship in _state.Ships)
        {
            if (ship.IsDestroyed || ship.IsDisabled) continue;

            // Piloting check: 1d20 + speed mod + maneuverability mod + system penalties.
            int roll = _dice.RollD20();
            int bonus = ship.Definition.SpeedPilotingModifier
                      + ship.Definition.ManeuverabilityPilotingModifier
                      + ship.Systems.GetActionPenalty(ShipSystem.Engines)
                      + ship.Systems.PowerCoreGlobalPenalty;

            // If engines are wrecked, the pilot can't act (auto-fail → treat as 0).
            if (ship.Systems.Get(ShipSystem.Engines) == SystemCondition.Wrecked)
                roll = 0;
            else
                roll += bonus;

            results.Add((ship.InstanceId, roll));
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} piloting check: {roll}."));
        }

        // Sort ascending (lowest moves first). Ties broken by fewer Piloting ranks.
        _state.ActionOrder = results
            .OrderBy(r => r.Roll)
            .ThenBy(r => _state.GetShip(r.Id)?.Definition.PilotingRanks ?? 0)
            .Select(r => r.Id)
            .ToList();

        _state.CurrentActorIndex = 0;
    }

    // ────────────────────────────────────────────────
    //  Helm phase: movement
    // ────────────────────────────────────────────────

    /// <summary>
    /// Applies a move action for the current actor. Returns validation result.
    /// On success, advances to the next actor.
    /// </summary>
    public MoveValidationResult ApplyMove(MoveAction action)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        var result = MovementValidator.Validate(ship, action, _state);
        if (!result.IsValid)
            return result;

        // Apply the move.
        ship.Position = result.FinalPosition!.Value;
        ship.Facing = result.FinalFacing ?? ship.Facing;
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} moves to {ship.Position}, facing {ship.Facing}."));

        AdvanceActor();
        return result;
    }

    /// <summary>Skips the current actor's movement (stays in place).</summary>
    public void SkipMove()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm) return;

        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} holds position."));
        AdvanceActor();
    }

    /// <summary>
    /// Turn In Place stunt: the ship does not move but turns to face any direction.
    /// Clumsy ships take –4 AC/TL; poor ships take –2 AC/TL until next round.
    /// This replaces ordinary movement for the round.
    /// </summary>
    public MoveValidationResult TryTurnInPlace(Facing facing)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        // Turning to the direction already faced is a no-op: no stunt, no penalty.
        if (facing == ship.Facing)
        {
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} holds position."));
            AdvanceActor();
            return MoveValidationResult.Ok(ship.Position, facing);
        }

        ship.Facing = facing;
        ship.TurnedInPlaceThisRound = true;
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} turns in place to face {facing}."));
        AdvanceActor();
        return MoveValidationResult.Ok(ship.Position, facing);
    }

    // ────────────────────────────────────────────────
    //  Gunnery phase: firing
    // ────────────────────────────────────────────────

    /// <summary>
    /// Applies a fire action for the current actor. Resolves the attack and applies damage.
    /// </summary>
    public AttackResult? ApplyFire(FireAction action)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery)
            return null;

        if (action.WeaponIndex < 0 || action.WeaponIndex >= ship.Weapons.Count)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} has no weapon at index {action.WeaponIndex}."));
            return null;
        }
        var weapon = ship.Weapons[action.WeaponIndex];

        var target = _state.GetShip(action.TargetShipId);
        if (target is null || target.IsDestroyed)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} has no valid target."));
            return null;
        }

        var result = CombatResolver.ResolveAttack(ship, target, weapon, _dice);
        LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, result.Description));

        // In v1 each ship fires all its weapons in sequence during its turn.
        // For now, one fire action per call; the client sends multiple if needed.
        return result;
    }

    /// <summary>
    /// Resolves the current actor's full gunnery turn: fires all specified weapons
    /// in sequence, then advances to the next actor. This is the primary method
    /// the server calls when a player submits their firing decisions.
    /// </summary>
    public List<AttackResult> ApplyGunneryTurn(IReadOnlyList<FireAction> actions)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery)
            return [];

        var results = new List<AttackResult>();
        foreach (var action in actions)
        {
            var result = ApplyFire(action);
            if (result is not null)
                results.Add(result);
        }

        if (actions.Count == 0)
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} does not fire."));

        AdvanceActor();
        return results;
    }

    /// <summary>Skips the current actor's firing (no weapons or choosing not to fire).</summary>
    public void SkipFire()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery) return;

        LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} does not fire."));
        AdvanceActor();
    }

    // ────────────────────────────────────────────────
    //  Engineering phase (v1: simple repair or skip)
    // ────────────────────────────────────────────────

    /// <summary>
    /// Applies an engineering action for a ship. In v1 this is just hull repair.
    /// </summary>
    public void ApplyEngineering(string shipId, RepairAction action)
    {
        var ship = _state.GetShip(shipId);
        if (ship is null || _state.Phase != CombatPhase.Engineering) return;

        if (action.Amount > 0)
        {
            int repaired = Math.Min(action.Amount, ship.Definition.HullPoints - ship.HullPoints);
            ship.HullPoints += repaired;
            LastEvents.Add(new CombatEvent("Engineering", shipId, $"{ship.Name} repairs {repaired} hull."));
        }
    }

    /// <summary>
    /// Advances out of the Engineering phase once all ships have acted (or chosen to skip).
    /// </summary>
    public void SkipEngineering()
    {
        if (_state.Phase != CombatPhase.Engineering) return;
        AdvancePhase();
    }

    // ────────────────────────────────────────────────
    //  Query helpers (for client UI: valid moves/targets)
    // ────────────────────────────────────────────────

    /// <summary>A valid destination for the current actor's helm move.</summary>
    /// <param name="Path">The hexes the ship travels through, from its start hex to <see cref="Position"/>.</param>
    public sealed record PathStep(Hex Position, Facing Facing);
    public sealed record MoveOption(Hex Position, Facing FinalFacing, IReadOnlyList<PathStep> Path);

    /// <summary>
    /// Computes all reachable (position, facing) outcomes for the current actor's movement,
    /// along with a representative route (the shortest hex path) for each outcome.
    /// BFS over (position, facing, remaining-movement, hexes-since-last-turn) states,
    /// enforcing speed, distance-between-turns, grid bounds, and no ending on an occupied hex.
    /// Passing through occupied hexes is allowed; only the final position must be clear.
    /// </summary>
    private Dictionary<(Hex Pos, Facing Facing), List<PathStep>> _ComputeReachable()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm) return new();

        int speed = ship.Speed;
        int minBetweenTurns = ship.Definition.DistanceBetweenTurns;
        var occupied = new HashSet<Hex>(_state.Ships
            .Where(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed)
            .Select(s => s.Position));

        // BFS state is (pos, facing, sinceTurn, consecutiveTurns); remaining movement rides along.
        // maxConsecutiveTurns: perfect maneuverability allows 2 turns at a single point; others allow 1.
        int maxConsecutiveTurns = minBetweenTurns == 0 ? 2 : 1;

        var visited = new HashSet<(Hex, Facing, int, int)>();
        var parent = new Dictionary<(Hex, Facing, int, int), (Hex, Facing, int, int)?>();
        var queue = new Queue<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int ConsecTurns)>();

        // Seed sinceTurn = 0: you must move minBetweenTurns hexes before your first turn.
        // Turning without moving is only possible via the Turn In Place stunt (separate action).
        var seed = (ship.Position, ship.Facing, 0, 0);
        queue.Enqueue((ship.Position, ship.Facing, speed, 0, 0));
        visited.Add(seed);
        parent[seed] = null;

        while (queue.Count > 0)
        {
            var (pos, facing, remaining, sinceTurn, consecTurns) = queue.Dequeue();
            var stateKey = (pos, facing, sinceTurn, consecTurns);

            // Move forward one hex in the current facing.
            if (remaining > 0)
            {
                var nextPos = pos.Neighbor((int)facing);
                int ns = sinceTurn + 1;
                var nextKey = (nextPos, facing, ns, 0);
                if (visited.Add(nextKey))
                {
                    parent[nextKey] = stateKey;
                    queue.Enqueue((nextPos, facing, remaining - 1, ns, 0));
                }
            }

            // Turn in place: ±60°, gated by distance-between-turns and consecutive-turn cap.
            if (consecTurns < maxConsecutiveTurns && (minBetweenTurns == 0 || sinceTurn >= minBetweenTurns))
            {
                foreach (var d in new[] { 1, -1 })
                {
                    var nf = (Facing)(((int)facing + d + 6) % 6);
                    var turnKey = (pos, nf, 0, consecTurns + 1);
                    if (visited.Add(turnKey))
                    {
                        parent[turnKey] = stateKey;
                        queue.Enqueue((pos, nf, remaining, 0, consecTurns + 1));
                    }
                }
            }
        }

        // For each reachable (pos, facing) outcome, keep the shortest route that reaches it.
        // Each step records the hex AND the facing while on it, so in-place turns are preserved
        // and the client can render where the ship turned.
        var result = new Dictionary<(Hex Pos, Facing Facing), List<PathStep>>();
        foreach (var stateKey in parent.Keys)
        {
            var (pos, facing, _, _) = stateKey;
            if (occupied.Contains(pos)) continue; // can't end on an occupied hex

            // Walk the parent chain back to the seed to get the route.
            var raw = new List<(Hex Pos, Facing Facing)>();
            (Hex, Facing, int, int)? cur = stateKey;
            while (cur != null)
            {
                raw.Add((cur.Value.Item1, cur.Value.Item2));
                cur = parent[cur.Value];
            }
            raw.Reverse();

            var path = new List<PathStep>(raw.Count);
            foreach (var (p, f) in raw)
                path.Add(new PathStep(p, f));

            if (!result.TryGetValue((pos, facing), out var existing) || path.Count < existing.Count)
                result[(pos, facing)] = path;
        }

        return result;
    }

    /// <summary>All valid (position, facing) outcomes for the current actor's helm move, with routes.</summary>
    public List<MoveOption> GetValidMoves()
        => _ComputeReachable().Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value)).ToList();

    /// <summary>
    /// Moves the current actor to a specific (position, facing) if it is reachable.
    /// The reachability set already enforces all movement rules, so a hit means valid.
    /// </summary>
    public MoveValidationResult TryMoveTo(Hex position, Facing facing)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        if (!_ComputeReachable().ContainsKey((position, facing)))
            return MoveValidationResult.Fail("That hex/facing is not reachable with this ship's movement.");

        ship.Position = position;
        ship.Facing = facing;
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} moves to {position}, facing {facing}."));
        AdvanceActor();
        return MoveValidationResult.Ok(position, facing);
    }

    /// <summary>A weapon that the current actor can fire at a specific target.</summary>
    public sealed record TargetOption(string WeaponName, string TargetShipId, int Range, int HitChancePercent, int WeaponIndex, int RangePenalty);

    /// <summary>
    /// Computes all valid (weapon, target) pairs for the current actor during Gunnery.
    /// </summary>
    public List<TargetOption> GetValidTargets()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery) return [];

        int bonus = CombatResolver.ComputeGunnerBonus(ship);
        var options = new List<TargetOption>();
        for (int wi = 0; wi < ship.Weapons.Count; wi++)
        {
            var weapon = ship.Weapons[wi];
            foreach (var target in _state.ActiveShips())
            {
                if (target.TeamId == ship.TeamId) continue; // no friendly fire in v1
                int range = ship.Position.DistanceTo(target.Position);
                double angle = ship.AngleFromFacing(target.Position);
                if (!weapon.CanTarget(range, angle)) continue;

                int defenseValue = weapon.Type switch
                {
                    WeaponType.Tracking => target.CurrentTL,
                    _ => target.CurrentAC
                };
                int rangePenalty = weapon.RangePenalty(range);
                int effectiveBonus = bonus + rangePenalty;
                int hitChance = CombatResolver.HitChancePercent(effectiveBonus, defenseValue);
                options.Add(new TargetOption(weapon.Name, target.InstanceId, range, hitChance, wi, rangePenalty));
            }
        }
        return options;
    }

    // ────────────────────────────────────────────────
    //  Internal helpers
    // ────────────────────────────────────────────────

    /// <summary>Advances to the next actor; if all actors done, advances the phase.</summary>
    private void AdvanceActor()
    {
        _state.CurrentActorIndex++;
        if (_state.CurrentActorIndex >= _state.ActionOrder.Count)
            AdvancePhase();
    }

    /// <summary>Checks if one side has been fully defeated.</summary>
    private void CheckVictory()
    {
        var teams = _state.Ships.Select(s => s.TeamId).Distinct().ToList();
        foreach (var team in teams)
        {
            bool allDown = _state.IsSideDefeated(s => s.TeamId == team);
            if (allDown)
            {
                int winnerTeam = teams.First(t => t != team);
                _state.Phase = CombatPhase.Complete;
                _state.Winner = $"Team {winnerTeam + 1}";
                LastEvents.Add(new CombatEvent("Complete", "", $"{_state.Winner} wins! Team {team + 1} is destroyed."));
                return;
            }
        }

        // If all ships are down (mutual destruction), it's a draw.
        if (_state.Ships.All(s => s.IsDestroyed || s.IsDisabled))
        {
            _state.Phase = CombatPhase.Complete;
            _state.Winner = "Mutual destruction";
            LastEvents.Add(new CombatEvent("Complete", "", "All ships destroyed — mutual destruction."));
        }
    }
}
