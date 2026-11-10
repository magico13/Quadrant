namespace Quadrant.RulesEngine;

/// <summary>
/// An event emitted during combat resolution, used to build a log and notify clients.
/// </summary>
public sealed record CombatEvent(string Phase, string ShipId, string Message)
{
    public static CombatEvent Info(string phase, string shipId, string msg) => new(phase, shipId, msg);
}

public sealed record PendingHitSnapshot(string AttackerId, string TargetId, Weapon Weapon, AttackCheckResult Check, Quadrant Quadrant, ShipSystem? TargetedSystem = null, long Order = 0);
public sealed record PendingLaunchSnapshot(TrackingProjectile Projectile, AttackCheckResult Check, long Order = 0);
public sealed record PendingProjectileImpactSnapshot(TrackingProjectile Projectile, int Roll, int CheckBonus, int Defense, Hex ImpactFrom, long Order = 0);
public sealed record PointDefenseUsedSnapshot(string ShipId, int WeaponIndex);
public sealed record PointDefenseWeaponOption(int WeaponIndex, string Name, int Bonus);
public sealed record PointDefenseMissileOption(string ProjectileId, string Name, int Speed, IReadOnlyList<PointDefenseWeaponOption> Weapons);
public sealed record PointDefensePrompt(string TargetShipId, FiringArc Arc, IReadOnlyList<PointDefenseMissileOption> Missiles);
public sealed record FiredWeaponSnapshot(string ShipId, int WeaponIndex);
public sealed record TargetArcSnapshot(string ShipId, string TargetId, FiringArc Arc);
public sealed record ShieldAllocation(int Forward, int Starboard, int Aft, int Port);
public sealed record EngineerAction(string ShipId, int CrewIndex, string Action, string Target, ShieldAllocation? Shields);
public sealed record ScienceAction(string ShipId, int CrewIndex, string Action, string TargetShipId = "", string System = "", string FromQuadrant = "", string ToQuadrant = "", int Amount = 0, bool BalanceEvenly = false);
public sealed record TargetedSystemSnapshot(string ShipId, string TargetShipId, ShipSystem System);
public sealed record CombatEngineSnapshot(
    List<PendingHitSnapshot> PendingHits, List<PendingLaunchSnapshot> PendingLaunches,
    List<FiredWeaponSnapshot> FiredWeapons, List<TargetArcSnapshot> TargetArcs,
    List<CombatEngine.MoveOption>? StuntMoves, string? PendingStunt,
    CombatEngine.MoveOption? CommittedMove, int MoveStep, Hex LastUnoccupied,
    Facing LastUnoccupiedFacing, string? FreeAttackShipId, List<CombatEvent> Events,
    List<TargetedSystemSnapshot>? TargetedSystems = null,
    List<PendingProjectileImpactSnapshot>? PendingImpacts = null,
    List<PointDefenseUsedSnapshot>? PointDefenseUsed = null,
    List<string>? PassedPointDefenseProjectiles = null,
    bool GunneryResolutionPrepared = false);

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
    private readonly List<(ShipInstance Attacker, ShipInstance Target, Weapon Weapon, AttackCheckResult Check, Quadrant Quadrant, ShipSystem? TargetedSystem, long Order)> _pendingHits = [];
    private readonly List<(TrackingProjectile Projectile, AttackCheckResult Check, long Order)> _pendingLaunches = [];
    private readonly List<PendingProjectileImpactSnapshot> _pendingProjectileImpacts = [];
    private readonly HashSet<(string ShipId, int WeaponIndex)> _firedWeapons = [];
    private readonly HashSet<(string ShipId, int WeaponIndex)> _pointDefenseUsed = [];
    private readonly HashSet<string> _passedPointDefenseProjectiles = [];
    private bool _gunneryResolutionPrepared;
    private long _nextAttackOrder;
    private readonly Dictionary<(string ShipId, string TargetId), FiringArc> _targetArcChoices = [];
    private readonly Dictionary<(string ShipId, string TargetId), ShipSystem> _targetedSystems = [];
    private List<MoveOption>? _stuntMoves;
    public string? PendingStunt { get; private set; }
    private MoveOption? _committedMove;
    private int _moveStep;
    private Hex _lastUnoccupied;
    private Facing _lastUnoccupiedFacing;
    public string? FreeAttackShipId { get; private set; }
    public bool HasPendingFreeAttack => FreeAttackShipId is not null;

    public PointDefensePrompt? PendingPointDefense => _gunneryResolutionPrepared ? GetPointDefensePrompt() : null;

    public IReadOnlyList<TargetedSystemSnapshot> GetTargetedSystems(string shipId)
        => _targetedSystems.Where(t => t.Key.ShipId == shipId)
            .Select(t => new TargetedSystemSnapshot(t.Key.ShipId, t.Key.TargetId, t.Value)).ToList();

    public IReadOnlyList<int> GetFreeAttackWeapons()
    {
        var defender = FreeAttackShipId is null ? null : _state.GetShip(FreeAttackShipId);
        if (defender is null || defender.IsDestroyed || defender.IsDisabled || CombatResolver.ComputeGunnerBonus(defender) == int.MinValue) return [];
        defender.InitializeCrewRoles();
        if (!defender.CrewRoles.Contains("Gunner")) return [];
        return defender.Weapons.Select((weapon, index) => (weapon, index))
            .Where(x => x.weapon.Type == WeaponType.DirectFire && defender.HasWeaponUses(x.index) &&
                LinkedMountIndices(defender, x.index).All(i => defender.HasWeaponUses(i)))
            .Where(x => !EarlierLinkedMount(defender, x.index))
            .Select(x => x.index).ToList();
    }

    public string? ResolveFreeAttack(int? weaponIndex, int? gunnerIndex = null)
    {
        var defender = FreeAttackShipId is null ? null : _state.GetShip(FreeAttackShipId);
        var target = _state.CurrentActor;
        if (defender is null || target is null || _committedMove is null) return "No free attack is pending.";
        if (weaponIndex is { } wi)
        {
            if (!GetFreeAttackWeapons().Contains(wi)) return "That weapon cannot be used for this free attack.";
            int gunner = gunnerIndex ?? defender.CrewRoles.IndexOf("Gunner");
            if (gunner < 0 || gunner >= defender.CrewRoles.Count || defender.CrewRoles[gunner] != "Gunner") return "Select a gunner for the free attack.";
            var mountIndices = LinkedMountIndices(defender, wi);
            var weapon = CombineLinkedWeapon(defender, wi, mountIndices);
            foreach (var index in mountIndices) defender.ConsumeWeaponUse(index);
            int bonus = defender.CrewBonus(gunner, "gunnery", "Gunner") + defender.Systems.GetActionPenalty(ShipSystem.WeaponsArray) + defender.Systems.PowerCoreGlobalPenalty;
            int roll = _dice.RollD20();
            int total = roll + bonus;
            bool hit = roll == 20 || (roll != 1 && total >= target.CurrentAC);
            string description = $"{ShipLogName(defender)} → {ShipLogName(target)} · {weapon.Name} (free attack): {CombatResolver.FormatCheck(roll, bonus, total)} vs AC {target.CurrentAC} — {(hit ? "HIT" : "MISS")}.";
            if (hit)
            {
                bool wasDisabled = target.IsDisabled, wasDestroyed = target.IsDestroyed;
                var damage = CombatResolver.ApplyDamage(target, Quadrant.Aft, weapon.Damage.Roll(_dice), roll, _dice);
                description += $" {damage.Description}.";
                LastEvents.Add(new CombatEvent("Helm", defender.InstanceId, description));
                LogShipStatusChange(target, wasDisabled, wasDestroyed, "Helm");
            }
            else LastEvents.Add(new CombatEvent("Helm", defender.InstanceId, description));
        }
        FreeAttackShipId = null;
        ContinueCommittedMove();
        return null;
    }

    private void ContinueCommittedMove()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _committedMove is null) return;
        if (ship.IsDisabled || ship.IsDestroyed)
        {
            ship.Position = _lastUnoccupied;
            ship.Facing = _lastUnoccupiedFacing;
            _committedMove = null;
            AdvanceActor();
            return;
        }
        while (++_moveStep < _committedMove.Path.Count)
        {
            var step = _committedMove.Path[_moveStep];
            bool moved = ship.Position != step.Position;
            ship.Position = step.Position;
            ship.Facing = step.Facing;
            if (!moved) continue;
            var occupant = _state.Ships.FirstOrDefault(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed && s.Position == step.Position);
            if (occupant is null) { _lastUnoccupied = step.Position; _lastUnoccupiedFacing = step.Facing; continue; }
            if (occupant.TeamId == ship.TeamId) continue;
            if (occupant.InstanceId == ship.FlybyTargetId && !ship.FlybyCrossed)
            {
                ship.FlybyCrossed = true;
                if (ship.FlybySucceeded) continue;
            }
            FreeAttackShipId = occupant.InstanceId;
            if (GetFreeAttackWeapons().Count > 0) return;
            FreeAttackShipId = null;
        }
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ShipLogName(ship)} moves to {ship.Position} · {ship.Facing}."));
        _committedMove = null;
        AdvanceActor();
    }

    private void BeginCommittedMove(MoveOption option)
    {
        _committedMove = option;
        _moveStep = 0;
        _lastUnoccupied = _state.CurrentActor!.Position;
        _lastUnoccupiedFacing = _state.CurrentActor.Facing;
        ContinueCommittedMove();
    }

    /// <summary>Events emitted during the last action resolution (for logging/broadcast).</summary>
    public List<CombatEvent> LastEvents { get; } = [];

    public CombatEngine(GameState state, IDiceRoller dice)
    {
        _state = state;
        _dice = dice;
    }

    public GameState State => _state;

    public CombatEngineSnapshot CaptureSnapshot() => new(
        _pendingHits.Select(h => new PendingHitSnapshot(h.Attacker.InstanceId, h.Target.InstanceId, h.Weapon, h.Check, h.Quadrant, h.TargetedSystem, h.Order)).ToList(),
        _pendingLaunches.Select(l => new PendingLaunchSnapshot(l.Projectile, l.Check, l.Order)).ToList(),
        _firedWeapons.Select(w => new FiredWeaponSnapshot(w.ShipId, w.WeaponIndex)).ToList(),
        _targetArcChoices.Select(a => new TargetArcSnapshot(a.Key.ShipId, a.Key.TargetId, a.Value)).ToList(),
        _stuntMoves, PendingStunt, _committedMove, _moveStep, _lastUnoccupied, _lastUnoccupiedFacing,
        FreeAttackShipId, LastEvents.ToList(),
        _targetedSystems.Select(t => new TargetedSystemSnapshot(t.Key.ShipId, t.Key.TargetId, t.Value)).ToList(),
        _pendingProjectileImpacts.ToList(),
        _pointDefenseUsed.Select(x => new PointDefenseUsedSnapshot(x.ShipId, x.WeaponIndex)).ToList(),
        _passedPointDefenseProjectiles.ToList(), _gunneryResolutionPrepared);

    public void RestoreSnapshot(CombatEngineSnapshot snapshot)
    {
        foreach (var h in snapshot.PendingHits)
            _pendingHits.Add((_state.GetShip(h.AttackerId) ?? throw new InvalidDataException("Missing attacker in saved game."),
                _state.GetShip(h.TargetId) ?? throw new InvalidDataException("Missing target in saved game."), h.Weapon, h.Check, h.Quadrant, h.TargetedSystem, h.Order));
        foreach (var l in snapshot.PendingLaunches) _pendingLaunches.Add((l.Projectile, l.Check, l.Order));
        _pendingProjectileImpacts.AddRange(snapshot.PendingImpacts ?? []);
        _nextAttackOrder = _pendingHits.Select(h => h.Order).Concat(_pendingLaunches.Select(l => l.Order)).Concat(_pendingProjectileImpacts.Select(i => i.Order)).DefaultIfEmpty(0).Max();
        foreach (var w in snapshot.FiredWeapons) _firedWeapons.Add((w.ShipId, w.WeaponIndex));
        foreach (var w in snapshot.PointDefenseUsed ?? []) _pointDefenseUsed.Add((w.ShipId, w.WeaponIndex));
        foreach (var id in snapshot.PassedPointDefenseProjectiles ?? []) _passedPointDefenseProjectiles.Add(id);
        _gunneryResolutionPrepared = snapshot.GunneryResolutionPrepared;
        foreach (var a in snapshot.TargetArcs) _targetArcChoices.Add((a.ShipId, a.TargetId), a.Arc);
        foreach (var target in snapshot.TargetedSystems ?? []) _targetedSystems.Add((target.ShipId, target.TargetShipId), target.System);
        _stuntMoves = snapshot.StuntMoves;
        PendingStunt = snapshot.PendingStunt;
        _committedMove = snapshot.CommittedMove;
        _moveStep = snapshot.MoveStep;
        _lastUnoccupied = snapshot.LastUnoccupied;
        _lastUnoccupiedFacing = snapshot.LastUnoccupiedFacing;
        FreeAttackShipId = snapshot.FreeAttackShipId;
        LastEvents.AddRange(snapshot.Events);

        var pendingImpactIds = _pendingProjectileImpacts.Select(impact => impact.Projectile.Id).ToHashSet();
        _state.Projectiles.RemoveAll(projectile => !pendingImpactIds.Contains(projectile.Id) &&
            _state.GetShip(projectile.TargetShipId)?.Position == projectile.Position);
    }

    public IReadOnlyList<int> FiredWeaponIndices
        => _state.CurrentActor is { } actor
            ? _firedWeapons.Where(x => x.ShipId == actor.InstanceId).Select(x => x.WeaponIndex).ToList()
            : [];

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
        if (_state.Ships.Select(s => s.TeamId).Distinct().Count() < 2) return false;

        foreach (var ship in _state.Ships) ship.InitializeCrewRoles();
        _state.Phase = CombatPhase.CrewSetup;
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
                DriftDisabledShips();
                ComputeActionOrder();
                LastEvents.Add(new CombatEvent("Helm", "", $"Round {_state.Round} — Helm phase. Order: {string.Join(" → ", _state.ActionOrder.Select(id => _state.GetShip(id)?.Name ?? id))}."));
                break;

            case CombatPhase.Helm:
                _state.Phase = CombatPhase.Gunnery;
                foreach (var ship in _state.Ships) ship.BarrelRollActive = ship.BarrelRolledThisRound;
                _state.CurrentActorIndex = 0;
                LastEvents.Add(new CombatEvent("Gunnery", "", "Gunnery phase."));
                break;

            case CombatPhase.Gunnery:
                if (!_gunneryResolutionPrepared)
                    PrepareGunneryResolution();
                if (ResolveAutomaticPointDefense()) return;
                FinishGunneryResolution();
                break;
        }
    }

    /// <summary>Drift disabled ships once at Helm entry, before active ships move.</summary>
    private void DriftDisabledShips()
    {
        foreach (var ship in _state.Ships.Where(s => s.IsDisabled && !s.IsDestroyed && s.LastDriftRound != _state.Round))
        {
            ship.DriftFacing ??= ship.Facing; // Backward compatibility for saved games.
            ship.LastDriftRound = _state.Round;
            var start = ship.Position;
            var destination = start;
            int distance = Math.Max(0, ship.Definition.Speed / 2);
            var occupied = _state.Ships.Where(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed)
                .Select(s => s.Position).ToHashSet();
            // Ships can pass through occupied hexes, but cannot end there.
            for (int step = 1; step <= distance; step++)
            {
                var candidate = start.Step((int)ship.DriftFacing.Value, step);
                if (!occupied.Contains(candidate)) destination = candidate;
            }
            ship.Position = destination;
            int moved = start.DistanceTo(destination);
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
                $"{ShipLogName(ship)} drifts {moved} hex{(moved == 1 ? "" : "es")} to {destination}, toward {ship.DriftFacing}."));
        }
    }

    /// <summary>Validates and commits the active ships' role assignments together.</summary>
    public string? AssignRoles(IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
    {
        if (_state.Phase != CombatPhase.CrewSetup) return "Crew roles can only change at the start of a round.";
        var ships = _state.ActiveShips().ToList();
        if (assignments.Count != ships.Count || ships.Any(s => !assignments.ContainsKey(s.InstanceId)))
            return "Provide roles for every active ship.";
        string[] allowed = ["Captain", "Engineer", "Gunner", "Pilot", "Science Officer"];
        foreach (var ship in ships)
        {
            var roles = assignments[ship.InstanceId];
            if (roles.Count != ship.Definition.Crew.Count || roles.Any(role => !allowed.Contains(role)))
                return $"Invalid crew roles for {ship.Name}.";
            if (roles.Count(role => role == "Pilot") > 1 || roles.Count(role => role == "Captain") > 1)
                return $"{ship.Name} can have only one pilot and one captain.";
        }
        foreach (var ship in ships)
        {
            ship.CrewRoles.Clear();
            ship.CrewRoles.AddRange(assignments[ship.InstanceId]);
            int publishedPilotRanks = ship.Definition.Crew.FirstOrDefault(c => c.Role == "Pilot")?.Ranks ?? 0;
            int pilotIndex = ship.CrewRoles.IndexOf("Pilot");
            int currentPilotRanks = pilotIndex >= 0 ? ship.Definition.Crew[pilotIndex].Ranks : 0;
            ship.RoundAcTlModifier += currentPilotRanks - publishedPilotRanks;
        }
        _state.Phase = CombatPhase.Engineering;
        LastEvents.Add(new CombatEvent("Engineering", "", $"Round {_state.Round} — Engineering phase."));
        return null;
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

            ship.InitializeCrewRoles();
            int pilot = ship.CrewRoles.IndexOf("Pilot");
            if (ship.Definition.Crew.Count > 0 && pilot < 0)
            {
                results.Add((ship.InstanceId, 0));
                LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} has no pilot; initiative is 0."));
                continue;
            }

            // Piloting check: 1d20 + speed mod + maneuverability mod + system penalties.
            int roll = _dice.RollD20();
            int bonus = (pilot >= 0 ? ship.CrewBonus(pilot, "piloting", "Pilot") : 0)
                      + (pilot >= 0 ? 0 : ship.Definition.SpeedPilotingModifier
                      + ship.Definition.ManeuverabilityPilotingModifier
                      )
                      + ship.SpeedPilotingAdjustment
                      + ship.Systems.GetActionPenalty(ShipSystem.Engines)
                      + ship.Systems.PowerCoreGlobalPenalty;

            // If engines are wrecked, the pilot can't act (auto-fail → treat as 0).
            if (ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked)
                roll = 0;
            else
                roll += bonus;

            results.Add((ship.InstanceId, roll));
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} piloting check: {roll}."));
        }

        // Sort ascending (lowest moves first). Ties broken by fewer Piloting ranks.
        _state.ActionOrder = results
            .OrderBy(r => r.Roll)
            .ThenBy(r => { var s = _state.GetShip(r.Id); int p = s?.CrewRoles.IndexOf("Pilot") ?? -1; return p >= 0 ? s!.Definition.Crew[p].Ranks : 0; })
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
        if (HasPendingFreeAttack) return MoveValidationResult.Fail("Resolve the free attack first.");
        if (PendingStunt is not null) return MoveValidationResult.Fail("Finish the selected stunt first.");
        if (_state.CurrentActor?.ManeuverSucceeded is not null) return MoveValidationResult.Fail("Finish the Maneuver using a movement destination.");
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        ship.InitializeCrewRoles();
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        if (ship.Definition.Crew.Count > 0 && pilot < 0)
            return MoveValidationResult.Fail("Use a Glide movement when no pilot is assigned.");
        if (ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked)
            return MoveValidationResult.Fail("The engines are wrecked; pilot actions automatically fail.");

        var result = MovementValidator.Validate(ship, action, _state);
        if (!result.IsValid)
            return result;

        if (pilot >= 0) ship.CrewActionsUsed.Add(pilot);

        var path = new List<PathStep> { new(ship.Position, ship.Facing) };
        var pos = ship.Position;
        var facing = ship.Facing;
        foreach (var segment in action.Segments)
        {
            for (int i = 0; i < segment.HexesToMove; i++) { pos = pos.Neighbor(facing); path.Add(new PathStep(pos, facing)); }
            if (segment.NewFacing is { } next && next != facing) { facing = next; path.Add(new PathStep(pos, facing)); }
        }
        BeginCommittedMove(new MoveOption(result.FinalPosition!.Value, result.FinalFacing ?? ship.Facing, path));
        return result;
    }

    /// <summary>Skips the current actor's movement (stays in place).</summary>
    public void SkipMove()
    {
        if (PendingStunt is not null || HasPendingFreeAttack) return;
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
    public bool CanTurnInPlace(ShipInstance ship)
    {
        ship.InitializeCrewRoles();
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        return ship.ManeuverSucceeded is null &&
            (ship.Definition.Crew.Count == 0 || (pilot >= 0 && !ship.CrewActionsUsed.Contains(pilot))) &&
            ship.Systems.GetEffective(ShipSystem.Engines) is not (SystemCondition.Malfunctioning or SystemCondition.Wrecked);
    }

    public MoveValidationResult TryTurnInPlace(Facing facing)
    {
        if (_state.CurrentActor?.ManeuverSucceeded is not null) return MoveValidationResult.Fail("Maneuver has already been attempted; stunts are unavailable.");
        if (HasPendingFreeAttack) return MoveValidationResult.Fail("Resolve the free attack first.");
        if (PendingStunt is not null) return MoveValidationResult.Fail("Finish the selected stunt first.");
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        ship.InitializeCrewRoles();
        if (ship.Definition.Crew.Count > 0 && !ship.CrewRoles.Contains("Pilot"))
            return MoveValidationResult.Fail("Turning in place requires a pilot; Glide cannot perform stunts.");
        if (ship.Definition.Crew.Count > 0 && ship.CrewActionsUsed.Contains(ship.CrewRoles.IndexOf("Pilot")))
            return MoveValidationResult.Fail("Turning in place requires an available pilot.");
        if (ship.Systems.GetEffective(ShipSystem.Engines) is SystemCondition.Malfunctioning or SystemCondition.Wrecked)
            return MoveValidationResult.Fail("Turning in place is a push action and requires working engines.");

        // Turning to the direction already faced is a no-op: no stunt, no penalty.
        if (facing == ship.Facing)
        {
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ship.Name} holds position."));
            AdvanceActor();
            return MoveValidationResult.Ok(ship.Position, facing);
        }

        ship.Facing = facing;
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        if (pilot >= 0) ship.CrewActionsUsed.Add(pilot);
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
    public AttackCheckResult? ApplyFire(FireAction action, FiringArc? chosenArc = null)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery)
            return null;

        int gunner = ship.AvailableGunner();
        bool snapShot = ship.Definition.Crew.Count > 0 && gunner < 0;
        int actingCrew = snapShot ? ship.AvailableMinorCrew() : gunner;
        if (snapShot && (ship.GunnerActionTaken || actingCrew < 0)) return null;

        if (action.WeaponIndex < 0 || action.WeaponIndex >= ship.Weapons.Count)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} has no weapon at index {action.WeaponIndex}."));
            return null;
        }
        var weapon = ship.Weapons[action.WeaponIndex];
        var mountIndices = LinkedMountIndices(ship, action.WeaponIndex);
        var attackWeapon = CombineLinkedWeapon(ship, action.WeaponIndex, mountIndices);

        var target = _state.GetShip(action.TargetShipId);
        if (target is null || target.IsDestroyed || target.TeamId == ship.TeamId)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} has no valid target."));
            return null;
        }

        int targetRange = ship.Position.DistanceTo(target.Position);
        double targetAngle = ship.AngleFromFacing(target.Position);
        bool flyby = IsFlybyTarget(ship, target);
        FiringArc? flybyArc = null;
        if (flyby)
        {
            if (!FlybyArcAllowed(ship, weapon) || !weapon.CanFireAtCloseRange || weapon.MaxFireRange < 1 ||
                action.TargetQuadrant is null || !Enum.IsDefined(action.TargetQuadrant.Value)) return null;
            flybyArc = ship.FlybyFiringArc ?? (weapon.Arc == FiringArc.AllArcs ? chosenArc : weapon.Arc);
            if (flybyArc is null || flybyArc == FiringArc.AllArcs || !Enum.IsDefined(flybyArc.Value)) return null;
        }
        else if (action.TargetQuadrant is not null || !weapon.CanTarget(targetRange, targetAngle)) return null;
        if (mountIndices.Any(i => !ship.HasWeaponUses(i)))
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ShipLogName(ship)}'s {attackWeapon.Name} is out of ammunition."));
            return null;
        }
        if (mountIndices.Any(i => _firedWeapons.Contains((ship.InstanceId, i)))) return null;
        var arcKey = (ship.InstanceId, target.InstanceId);
        bool sharedBoundary = IsSharedArcBoundary(targetAngle);
        if (!flyby && sharedBoundary)
        {
            var availableArcs = ship.Weapons.Select((w, i) => (Weapon: w, Index: i))
                .Where(x => !_firedWeapons.Contains((ship.InstanceId, x.Index)) && ship.HasWeaponUses(x.Index) && x.Weapon.Arc != FiringArc.AllArcs && x.Weapon.CanTarget(targetRange, targetAngle))
                .Select(x => x.Weapon)
                .Select(w => w.Arc).Distinct().ToList();
            bool alreadyChosen = _targetArcChoices.TryGetValue(arcKey, out var existingArc);
            FiringArc? selectedArc = alreadyChosen ? existingArc : chosenArc;
            if (selectedArc is null && availableArcs.Count == 1) selectedArc = availableArcs[0];
            if (selectedArc is null && (weapon.Arc != FiringArc.AllArcs || availableArcs.Count > 0)) return null;
            if (selectedArc is not null && ((!alreadyChosen && !availableArcs.Contains(selectedArc.Value)) ||
                (weapon.Arc != FiringArc.AllArcs && !weapon.BroadArc && weapon.Arc != selectedArc))) return null;
            if (selectedArc is not null) _targetArcChoices[arcKey] = selectedArc.Value;
        }
        else if (!flyby && _targetArcChoices.TryGetValue(arcKey, out var selectedArc) && weapon.Arc != FiringArc.AllArcs && !weapon.BroadArc && weapon.Arc != selectedArc)
        {
            return null;
        }

        if (!ship.HasWeaponUses(action.WeaponIndex))
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ShipLogName(ship)}'s {weapon.Name} is out of ammunition."));
            return null;
        }

        if (weapon.Type == WeaponType.Tracking && weapon.ProjectileSpeed <= 0)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{weapon.Name} has no projectile speed configured."));
            return null;
        }
        foreach (int index in mountIndices)
        {
            if (!_firedWeapons.Add((ship.InstanceId, index))) return null;
            ship.ConsumeWeaponUse(index);
        }
        if (flyby) ship.FlybyFiringArc = flybyArc;

        int? crewBonus = actingCrew >= 0 ? ship.CrewBonus(actingCrew, "gunnery", "Gunner") : null;
        if (crewBonus.HasValue)
        {
            int penalty = ship.Systems.GetActionPenalty(ShipSystem.WeaponsArray);
            crewBonus = penalty == int.MinValue ? int.MinValue : crewBonus.Value + penalty + ship.Systems.PowerCoreGlobalPenalty - (snapShot ? 2 : 0);
            if (snapShot) ship.CrewMinorActionsUsed.Add(actingCrew);
            else ship.CrewActionsUsed.Add(actingCrew);
            ship.GunnerActionTaken = true;
        }
        var check = CombatResolver.RollAttack(ship, target, attackWeapon, _dice, gunnerBonus: crewBonus, rollDamage: false, flyby: flyby);
        if (!check.IsHit)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId,
                $"{ShipLogName(ship)} → {ShipLogName(target)} · {weapon.Name}: {check.Description}"));
            return check;
        }

        LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId,
            $"{ShipLogName(ship)} → {ShipLogName(target)} · {weapon.Name}: {check.Description}"));

        if (weapon.Type == WeaponType.Tracking)
        {
            // Flyby fires as if adjacent to the selected quadrant, including
            // projectile travel and the target's point-defense coverage.
            int flybyDirection = ((int)target.Facing + (action.TargetQuadrant switch
            {
                Quadrant.Starboard => 1,
                Quadrant.Aft => 3,
                Quadrant.Port => 5,
                _ => 0
            })) % Hex.DirectionCount;
            var projectile = new TrackingProjectile
            {
                Id = $"projectile-{Guid.NewGuid():N}", Name = ProjectileDisplayName(weapon.Name), TeamId = ship.TeamId,
                OwnerShipId = ship.InstanceId, TargetShipId = target.InstanceId,
                Position = flyby ? target.Position.Step(flybyDirection, 1) : ship.Position,
                Facing = flyby ? (Facing)((flybyDirection + 3) % Hex.DirectionCount) : TrackingLaunchFacing(ship, target, weapon),
                Speed = weapon.ProjectileSpeed, GunnerBonus = (crewBonus ?? CombatResolver.ComputeGunnerBonus(ship)) + (snapShot ? 2 : 0), Weapon = weapon,
                FlybyQuadrant = flyby ? action.TargetQuadrant : null
            };
            _pendingLaunches.Add((projectile, check, ++_nextAttackOrder));
            return check with { Description = $"{check.Description} Missile acquired; it moves at the end of Gunnery." };
        }

        _pendingHits.Add((ship, target, attackWeapon, check, flyby ? target.ShieldQuadrantFor(action.TargetQuadrant!.Value) : target.QuadrantOf(ship.Position), null, ++_nextAttackOrder));
        return check with { Description = $"{check.Description} Damage effects apply at the end of Gunnery." };
    }

    /// <summary>
    /// Resolves the supplied weapons in sequence for compatibility with callers
    /// that submit a whole turn at once. Interactive play uses ApplyFire once per
    /// weapon, then EndGunneryTurn when targeting choices are complete.
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
                results.Add(result.IsHit
                    ? AttackResult.Success(result.CheckTotal, result.DamageRolled, 0, 0, false, null, null, result.Description)
                    : AttackResult.Miss(result.CheckTotal, result.DefenseValue, result.Description));
        }

        if (actions.Count == 0)
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} does not fire."));

        EndGunneryTurn();
        return results;
    }

    /// <summary>Locks in this ship's remaining gunnery choices and advances to the next ship.</summary>
    public void EndGunneryTurn()
    {
        if (_state.Phase != CombatPhase.Gunnery || _state.CurrentActor is null) return;
        AdvanceActor();
    }

    /// <summary>Ends the current turn when no implemented gunnery action remains.</summary>
    public bool AutoEndGunneryTurnIfNoActions()
    {
        if (_state.Phase != CombatPhase.Gunnery || _state.CurrentActor is null || GetValidTargets().Count != 0)
            return false;
        EndGunneryTurn();
        return true;
    }

    /// <summary>Attempt Back Off and move directly aft without changing facing.</summary>
    private static int StuntBonus(ShipInstance ship, int pilot) =>
        ship.CrewBonus(pilot, "piloting", "Pilot") + ship.SpeedPilotingAdjustment +
        ship.Systems.GetActionPenalty(ShipSystem.Engines) + ship.Systems.PowerCoreGlobalPenalty;

    private static int StuntDc(ShipInstance ship, string stunt) =>
        (int)Math.Floor((stunt == "Flip and Burn" ? 15 : 10) + 1.5 * ship.Definition.Tier);

    public int? GetStuntSuccessChance(ShipInstance ship, string stunt, ShipInstance? target = null)
    {
        if (stunt == "Barrel Roll" && !ship.CanBarrelRoll) return null;
        ship.InitializeCrewRoles();
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        if (pilot < 0 || ship.CrewActionsUsed.Contains(pilot) ||
            ship.Systems.GetEffective(ShipSystem.Engines) is SystemCondition.Malfunctioning or SystemCondition.Wrecked)
            return null;
        if (stunt == "Flyby" && (target is null || target.IsDestroyed || target.TeamId == ship.TeamId ||
            ship.Position.DistanceTo(target.Position) >= ship.Speed)) return null;
        int dc = stunt == "Flyby" ? (int)Math.Floor(15 + 1.5 * target!.Definition.Tier) : StuntDc(ship, stunt);
        return Math.Clamp(21 - dc + StuntBonus(ship, pilot), 0, 20) * 5;
    }

    public MoveValidationResult StartStunt(string stunt)
    {
        if (_state.CurrentActor?.ManeuverSucceeded is not null) return MoveValidationResult.Fail("Maneuver has already been attempted; stunts are unavailable.");
        if (HasPendingFreeAttack) return MoveValidationResult.Fail("Resolve the free attack first.");
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm) return MoveValidationResult.Fail("No ship to move.");
        if (PendingStunt is not null) return MoveValidationResult.Fail("Finish the selected stunt first.");
        if (stunt is not ("Back Off" or "Flip and Burn" or "Evade" or "Slide" or "Barrel Roll" or "Flyby")) return MoveValidationResult.Fail("Unknown stunt.");
        if (stunt == "Barrel Roll" && !ship.CanBarrelRoll)
            return MoveValidationResult.Fail("Barrel Roll requires a ship of Large size or smaller with a known size.");
        ship.InitializeCrewRoles();
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        if (pilot < 0 || ship.CrewActionsUsed.Contains(pilot)) return MoveValidationResult.Fail("The stunt requires an available pilot.");
        if (ship.Systems.GetEffective(ShipSystem.Engines) is SystemCondition.Malfunctioning or SystemCondition.Wrecked)
            return MoveValidationResult.Fail("Stunts are push actions and require working engines.");
        if (stunt == "Flyby")
        {
            var routes = _ComputeReachable(flyby: true);
            if (routes.Count == 0) return MoveValidationResult.Fail("No legal Flyby path crosses one enemy and ends in an unoccupied hex.");
            _stuntMoves = routes.Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value)).ToList();
            PendingStunt = stunt;
            return MoveValidationResult.Ok(ship.Position, ship.Facing);
        }
        if (stunt == "Back Off")
        {
            int aft = ((int)ship.Facing + 3) % 6;
            bool hasDestination = Enumerable.Range(1, ship.Speed / 2).Any(distance =>
                !_state.Ships.Any(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed && s.Position == ship.Position.Step(aft, distance)));
            if (!hasDestination) return MoveValidationResult.Fail("Back Off requires an unoccupied destination at least one hex aft.");
        }

        int penalty = ship.Systems.GetActionPenalty(ShipSystem.Engines);
        int roll = _dice.RollD20();
        int bonus = StuntBonus(ship, pilot);
        int dc = StuntDc(ship, stunt);
        int total = roll + bonus;
        bool failedByFive = penalty == int.MinValue || total <= dc - 5;
        bool failed = penalty == int.MinValue || total < dc;
        if (stunt == "Barrel Roll")
        {
            int halfSpeed = ship.Speed / 2;
            _stuntMoves = _ComputeReachable(speedLimit: halfSpeed, requireFullSpeed: failed)
                .Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value)).ToList();
            if (_stuntMoves.Count == 0)
                _stuntMoves.Add(new MoveOption(ship.Position, ship.Facing, [new PathStep(ship.Position, ship.Facing)]));
            ship.BarrelRolledThisRound = !failed;
            if (failedByFive) ship.RoundAcTlModifier -= 4;
            ship.CrewActionsUsed.Add(pilot);
            PendingStunt = stunt;
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
                $"{ShipLogName(ship)} · Barrel Roll: {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(failedByFive ? "FAIL by 5 or more; AC and TL -4" : failed ? "FAIL; no roll" : "SUCCESS; port and starboard swap during Gunnery")}."));
            return MoveValidationResult.Ok(ship.Position, ship.Facing);
        }
        if (stunt == "Evade")
        {
            _stuntMoves = _ComputeReachable().Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value)).ToList();
            int modifier = failedByFive ? -2 : failed ? 0 : 2;
            ship.RoundAcTlModifier += modifier;
            ship.CrewActionsUsed.Add(pilot);
            PendingStunt = stunt;
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
                $"{ShipLogName(ship)} · Evade: {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(failed ? "FAIL" : "SUCCESS")}; AC and TL {modifier:+0;-0;0}."));
            return MoveValidationResult.Ok(ship.Position, ship.Facing);
        }
        if (stunt == "Slide")
        {
            int[] directions = failed ? [(int)ship.Facing] : [((int)ship.Facing + 5) % 6, ((int)ship.Facing + 1) % 6];
            int limit = failed ? ship.Speed / 2 : ship.Speed;
            var blocked = _state.Ships.Where(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed).Select(s => s.Position).ToHashSet();
            _stuntMoves = directions.SelectMany(direction => Enumerable.Range(0, limit + 1)
                .Select(distance => new MoveOption(ship.Position.Step(direction, distance), ship.Facing,
                    Enumerable.Range(0, distance + 1).Select(step => new PathStep(ship.Position.Step(direction, step), ship.Facing)).ToList())))
                .Where(option => !blocked.Contains(option.Position))
                .DistinctBy(option => (option.Position, option.FinalFacing)).ToList();
            ship.CrewActionsUsed.Add(pilot);
            PendingStunt = stunt;
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
                $"{ShipLogName(ship)} · Slide: {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(failed ? "FAIL" : "SUCCESS")}."));
            return MoveValidationResult.Ok(ship.Position, ship.Facing);
        }
        int half = ship.Speed / 2;
        int minimum = stunt == "Back Off" ? (failedByFive ? 0 : 1) : 0;
        int maximum = stunt == "Back Off" ? (failedByFive ? 0 : failed ? 1 : half) : half;
        int direction = stunt == "Back Off" ? ((int)ship.Facing + 3) % 6 : (int)ship.Facing;
        Facing endFacing = stunt == "Flip and Burn" && !failed ? (Facing)(((int)ship.Facing + 3) % 6) : ship.Facing;
        var occupied = _state.Ships.Where(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed).Select(s => s.Position).ToHashSet();
        _stuntMoves = Enumerable.Range(minimum, Math.Max(0, maximum - minimum + 1))
            .Select(distance => new MoveOption(ship.Position.Step(direction, distance), endFacing,
                Enumerable.Range(0, distance + 1).Select(step => new PathStep(ship.Position.Step(direction, step), step == distance ? endFacing : ship.Facing)).ToList()))
            .Where(option => !occupied.Contains(option.Position)).ToList();
        if (_stuntMoves.Count == 0)
            _stuntMoves.Add(new MoveOption(ship.Position, ship.Facing, [new PathStep(ship.Position, ship.Facing)]));
        if (stunt == "Back Off" && failedByFive) ship.RoundAcTlModifier -= 4;
        ship.CrewActionsUsed.Add(pilot);
        PendingStunt = stunt;
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
            $"{ShipLogName(ship)} · {stunt}: {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(stunt == "Back Off" && failedByFive ? "FAIL by 5 or more; AC and TL -4" : failed ? "FAIL" : "SUCCESS")}."));
        return MoveValidationResult.Ok(ship.Position, ship.Facing);
    }

    /// <summary>Skips the current actor's firing (no weapons or choosing not to fire).</summary>
    public void SkipFire()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery) return;

        LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} does not fire."));
        EndGunneryTurn();
    }

    // ────────────────────────────────────────────────
    //  Engineering phase
    // ────────────────────────────────────────────────

    private static int CheckChance(int bonus, int dc) => Math.Clamp(21 - dc + bonus, 0, 20) * 5;

    public int GetEngineeringSuccessChance(ShipInstance ship, int crewIndex, string action)
    {
        int penalty = action == "Hold It Together" ? 0 : ship.Systems.GetActionPenalty(ShipSystem.PowerCore);
        if (penalty == int.MinValue) return 0;
        int dc = (int)Math.Floor((action == "Divert" ? 10 : 15) + 1.5 * ship.Definition.Tier);
        return CheckChance(ship.CrewBonus(crewIndex, "engineering", "Engineer") + penalty, dc);
    }

    public int GetScienceSuccessChance(ShipInstance ship, int crewIndex, ShipInstance? target = null)
    {
        int penalty = ship.Systems.GetActionPenalty(ShipSystem.Sensors);
        if (penalty == int.MinValue || (target is not null &&
            ship.Systems.GetEffective(ShipSystem.Sensors) is SystemCondition.Malfunctioning or SystemCondition.Wrecked)) return 0;
        int range = target is null ? 0 : ship.Position.DistanceTo(target.Position);
        int sensorRange = ship.Definition.SensorRangeHexes;
        int rangePenalty = target is not null && sensorRange > 0 ? -2 * Math.Max(0, (range + sensorRange - 1) / sensorRange - 1) : 0;
        int dc = target is null ? (int)Math.Floor(10 + 1.5 * ship.Definition.Tier)
            : (int)Math.Floor(5 + 1.5 * target.Definition.Tier) + target.Definition.Countermeasures;
        return CheckChance(GetScienceOfficerCheckBonus(ship, crewIndex) + penalty + ship.Systems.PowerCoreGlobalPenalty + rangePenalty, dc);
    }

    /// <summary>Attempt one engineering action, consuming that engineer's action even on failure.</summary>
    public string? ApplyEngineering(EngineerAction action)
    {
        if (_state.Phase != CombatPhase.Engineering) return "Engineering actions are only available during Engineering.";
        var ship = _state.GetShip(action.ShipId);
        if (ship is null || ship.IsDestroyed || ship.IsDisabled) return "This ship cannot take engineering actions.";
        ship.InitializeCrewRoles();
        if (action.CrewIndex < 0 || action.CrewIndex >= ship.CrewRoles.Count || ship.CrewRoles[action.CrewIndex] != "Engineer" || ship.CrewActionsUsed.Contains(action.CrewIndex))
            return "Select an engineer who has not acted this round.";
        if (action.Action is not ("Divert" or "Hold It Together")) return "Unknown engineering action.";
        if (ship.EngineeringActionsUsed.Contains(action.Action)) return $"{action.Action} has already been attempted this round.";

        ShipSystem heldSystem = default;
        if (action.Action == "Hold It Together" && (!Enum.TryParse(action.Target, true, out heldSystem) || !Enum.IsDefined(heldSystem)))
            return "Select a valid system to hold together.";
        if (action.Action == "Divert" && action.Target is not ("Engines" or "Science" or "Weapons" or "Shields"))
            return "Select a valid system for Divert.";
        if (action.Action == "Divert" && action.Target == "Shields")
        {
            var a = action.Shields;
            int max = ship.Definition.Shields.Forward + ship.Definition.Shields.Starboard + ship.Definition.Shields.Aft + ship.Definition.Shields.Port;
            int current = ship.Shields.Forward + ship.Shields.Starboard + ship.Shields.Aft + ship.Shields.Port;
            int restore = Math.Max(0, Math.Min(ship.Definition.PowerCorePcu / 20, max - current));
            if (a is null || a.Forward < 0 || a.Starboard < 0 || a.Aft < 0 || a.Port < 0 ||
                (long)a.Forward + a.Starboard + a.Aft + a.Port != restore)
                return $"Distribute exactly {restore} shield points across the four quadrants.";
        }

        ship.CrewActionsUsed.Add(action.CrewIndex);
        ship.EngineeringActionsUsed.Add(action.Action);
        int penalty = action.Action == "Hold It Together" ? 0 : ship.Systems.GetActionPenalty(ShipSystem.PowerCore);
        if (penalty == int.MinValue)
        {
            LastEvents.Add(new CombatEvent("Engineering", ship.InstanceId, $"{ShipLogName(ship)} · {action.Action}: power core wrecked — automatic failure."));
            return null;
        }
        int bonus = ship.CrewBonus(action.CrewIndex, "engineering", "Engineer") + penalty;
        int roll = _dice.RollD20();
        int total = roll + bonus;
        int dc = (int)Math.Floor((action.Action == "Divert" ? 10 : 15) + 1.5 * ship.Definition.Tier);
        bool success = total >= dc;
        if (success && action.Action == "Hold It Together") ship.Systems.HeldSystem = heldSystem;
        if (success && action.Action == "Divert")
        {
            switch (action.Target)
            {
                case "Engines": ship.DivertEngines = true; break;
                case "Weapons": ship.DivertWeapons = true; break;
                case "Science": ship.DivertScience = true; break;
                case "Shields":
                    var a = action.Shields!;
                    int max = ship.Definition.Shields.Forward + ship.Definition.Shields.Starboard + ship.Definition.Shields.Aft + ship.Definition.Shields.Port;
                    ship.Shields.Restore(Quadrant.Forward, a.Forward, max);
                    ship.Shields.Restore(Quadrant.Starboard, a.Starboard, max);
                    ship.Shields.Restore(Quadrant.Aft, a.Aft, max);
                    ship.Shields.Restore(Quadrant.Port, a.Port, max);
                    break;
            }
        }
        LastEvents.Add(new CombatEvent("Engineering", ship.InstanceId,
            $"{ShipLogName(ship)} · {action.Action} ({action.Target}): {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(success ? "SUCCESS" : "FAIL")}.") );
        return null;
    }

    /// <summary>Attempt one science officer action during Helm.</summary>
    public string? ApplyScience(ScienceAction action)
    {
        if (_state.Phase != CombatPhase.Helm) return "Science officer actions are only available during Helm.";
        var ship = _state.GetShip(action.ShipId);
        if (ship is null || ship.IsDestroyed || ship.IsDisabled) return "This ship cannot take science officer actions.";
        ship.InitializeCrewRoles();
        if (action.CrewIndex < 0 || action.CrewIndex >= ship.CrewRoles.Count ||
            ship.CrewRoles[action.CrewIndex] != "Science Officer" || ship.CrewActionsUsed.Contains(action.CrewIndex))
            return "Select a science officer who has not acted this round.";
        if (action.Action is not ("Balance" or "Target System")) return "Unknown science officer action.";

        var checkSystem = ship.Systems.GetEffective(ShipSystem.Sensors);
        if (action.Action == "Target System")
        {
            if (!ship.Definition.HasSensors) return "Target System requires sensors.";
            if (checkSystem is SystemCondition.Malfunctioning or SystemCondition.Wrecked)
                return "Target System is a push action and requires functioning sensors.";
            var target = _state.GetShip(action.TargetShipId);
            if (target is null || target.IsDestroyed || target.TeamId == ship.TeamId) return "Select a valid enemy target.";
            if (!Enum.TryParse<ShipSystem>(action.System, true, out var selectedSystem) || !Enum.IsDefined(selectedSystem))
                return "Select a system to target.";
            if (_targetedSystems.ContainsKey((ship.InstanceId, target.InstanceId)))
                return "A system is already targeted on that enemy; the lock remains until an attack hits.";

            int range = ship.Position.DistanceTo(target.Position);
            int sensorRange = ship.Definition.SensorRangeHexes;
            if (sensorRange > 0 && range > sensorRange * 5)
                return $"Target System is out of active sensor range ({sensorRange * 5} hexes).";

            ship.CrewActionsUsed.Add(action.CrewIndex);
            int rangePenalty = sensorRange > 0 ? -2 * Math.Max(0, (range + sensorRange - 1) / sensorRange - 1) : 0;
            int bonus = GetScienceOfficerCheckBonus(ship, action.CrewIndex) + rangePenalty;
            int penalty = ship.Systems.GetActionPenalty(ShipSystem.Sensors);
            int dc = (int)Math.Floor(5 + 1.5 * target.Definition.Tier) + target.Definition.Countermeasures;
            ResolveScienceCheck(ship, action, bonus, penalty, dc, success =>
            {
                if (success) _targetedSystems[(ship.InstanceId, target.InstanceId)] = selectedSystem;
                return success ? $"targets {ShipLogName(target)}'s {selectedSystem}." : $"fails to target {ShipLogName(target)}'s {selectedSystem}.";
            }, rangePenalty);
            return null;
        }

        var shields = ship.Shields;
        int totalShields = shields.Forward + shields.Starboard + shields.Aft + shields.Port;
        int minimum = (totalShields + 9) / 10;
        ShieldAllocation allocation;
        if (action.BalanceEvenly)
        {
            int each = totalShields / 4;
            int remainder = totalShields % 4;
            allocation = new ShieldAllocation(each + remainder, each, each, each);
        }
        else
        {
            if (!Enum.TryParse<Quadrant>(action.FromQuadrant, true, out var from) || !Enum.IsDefined(from) ||
                !Enum.TryParse<Quadrant>(action.ToQuadrant, true, out var to) || !Enum.IsDefined(to) ||
                from == to || action.Amount <= 0)
                return "Choose a source quadrant, a different destination quadrant, and a positive number of shield points.";
            int[] values = [shields.Forward, shields.Starboard, shields.Aft, shields.Port];
            if (values[(int)from] - action.Amount < minimum)
                return $"The source quadrant must retain at least {minimum} shield points after rebalancing.";
            values[(int)from] -= action.Amount;
            values[(int)to] += action.Amount;
            allocation = new ShieldAllocation(values[0], values[1], values[2], values[3]);
        }
        if ((long)allocation.Forward + allocation.Starboard + allocation.Aft + allocation.Port != totalShields)
            return $"Reallocate all {totalShields} shield points.";
        if (action.BalanceEvenly && (allocation.Forward < minimum || allocation.Starboard < minimum ||
            allocation.Aft < minimum || allocation.Port < minimum))
            return $"Even distribution requires at least {minimum} shield points in each quadrant.";

        ship.CrewActionsUsed.Add(action.CrewIndex);
        int balanceBonus = GetScienceOfficerCheckBonus(ship, action.CrewIndex);
        int balancePenalty = ship.Systems.GetActionPenalty(ShipSystem.Sensors);
        int balanceDc = (int)Math.Floor(10 + 1.5 * ship.Definition.Tier);
        ResolveScienceCheck(ship, action, balanceBonus, balancePenalty, balanceDc, success =>
        {
            if (success)
            {
                shields.Forward = allocation.Forward;
                shields.Starboard = allocation.Starboard;
                shields.Aft = allocation.Aft;
                shields.Port = allocation.Port;
            }
            return success ? "balances its shields." : "fails to balance its shields.";
        });
        return null;
    }

    public int GetScienceOfficerCheckBonus(ShipInstance ship, int crewIndex)
    {
        bool hasListedSkill = ship.Definition.Crew[crewIndex].Skills.ContainsKey("computers") ||
            ship.Definition.Crew.Any(c => c.Role == "Science Officer" && c.Skills.ContainsKey("computers"));
        int bonus = ship.CrewBonus(crewIndex, "computers", "Science Officer");
        return hasListedSkill ? bonus : bonus + ship.Definition.ComputersModifier;
    }

    private void ResolveScienceCheck(ShipInstance ship, ScienceAction action, int bonus, int systemPenalty, int dc, Func<bool, string> describe, int rangePenalty = 0)
    {
        if (systemPenalty == int.MinValue)
        {
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ShipLogName(ship)} · {action.Action}: sensors wrecked — automatic failure."));
            return;
        }
        bonus += systemPenalty + ship.Systems.PowerCoreGlobalPenalty;
        int roll = _dice.RollD20();
        int total = roll + bonus;
        bool success = total >= dc;
        string check = rangePenalty < 0
            ? $"{roll}{(bonus - rangePenalty < 0 ? "" : "+")}{bonus - rangePenalty}{rangePenalty}={total}"
            : CombatResolver.FormatCheck(roll, bonus, total);
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
            $"{ShipLogName(ship)} · {action.Action}: Computers {check} vs DC {dc} — {(success ? "SUCCESS" : "FAIL")}, {describe(success)}"));
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
    public sealed record MoveOption(Hex Position, Facing FinalFacing, IReadOnlyList<PathStep> Path, bool RequiresManeuver = false);

    /// <summary>
    /// Computes all reachable (position, facing) outcomes for the current actor's movement,
    /// along with a representative route (the shortest hex path) for each outcome.
    /// BFS over (position, facing, remaining-movement, hexes-since-last-turn) states,
    /// enforcing speed, distance-between-turns, grid bounds, and no ending on an occupied hex.
    /// Passing through occupied hexes is allowed; only the final position must be clear.
    /// </summary>
    private Dictionary<(Hex Pos, Facing Facing), List<PathStep>> _ComputeReachable(ShipInstance? requestedShip = null, Hex? allowOccupiedTarget = null, int? speedLimit = null, bool requireFullSpeed = false, bool flyby = false, int turnReduction = 0)
    {
        var ship = requestedShip ?? _state.CurrentActor;
        if (ship is null || (requestedShip is null && _state.Phase != CombatPhase.Helm)) return new();

        ship.InitializeCrewRoles();
        bool glide = requestedShip is null && ship.Definition.Crew.Count > 0 &&
            (!ship.CrewRoles.Contains("Pilot") || ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked);
        if (glide && ship.AvailableGlideCrew() < 0) return new();
        if (requestedShip is null && !glide && ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked) return new();
        int speed = speedLimit ?? (glide ? ship.Speed / 2 : ship.Speed);
        int minBetweenTurns = Math.Max(0, ship.Definition.DistanceBetweenTurns + (glide ? 2 : 0) - turnReduction);
        var occupied = new HashSet<Hex>(_state.Ships
            .Where(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed)
            .Select(s => s.Position));

        // BFS state is (pos, facing, sinceTurn, consecutiveTurns); remaining movement rides along.
        // maxConsecutiveTurns: perfect maneuverability allows 2 turns at a single point; others allow 1.
        int maxConsecutiveTurns = minBetweenTurns == 0 && ship.Maneuverability == Maneuverability.Perfect ? 2 : 1;

        // Keep remaining speed in the state: a safer route may be longer but
        // still leave enough movement to reach the requested destination.
        var enemies = requestedShip is null
            ? _state.Ships.Where(s => s.TeamId != ship.TeamId && !s.IsDestroyed).Select(s => s.Position).ToHashSet()
            : new HashSet<Hex>();
        var seed = (Pos: ship.Position, Facing: ship.Facing, Remaining: speed, SinceTurn: 0, Turns: 0, Crossed: false);
        var queue = new PriorityQueue<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns, bool Crossed), (int Exposures, int Steps)>();
        var costs = new Dictionary<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns, bool Crossed), (int Exposures, int Steps)> { [seed] = (0, 0) };
        var parents = new Dictionary<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns, bool Crossed), (Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns, bool Crossed)?> { [seed] = null };
        queue.Enqueue(seed, (0, 0));
        while (queue.TryDequeue(out var current, out var cost))
        {
            if (cost != costs[current]) continue;
            void Visit((Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns, bool Crossed) next, bool moved)
            {
                if (flyby && moved && enemies.Contains(next.Pos))
                {
                    // Friendly crossings are normal movement. Flyby permits
                    // one enemy entry, with no second entry into an enemy hex.
                    if (current.Crossed) return;
                    next.Crossed = true;
                }
                var nextCost = (cost.Exposures + (moved && enemies.Contains(next.Pos) ? 1 : 0), cost.Steps + 1);
                if (costs.TryGetValue(next, out var previous) && previous.CompareTo(nextCost) <= 0) return;
                costs[next] = nextCost;
                parents[next] = current;
                queue.Enqueue(next, nextCost);
            }
            if (current.Remaining > 0)
                Visit((current.Pos.Neighbor((int)current.Facing), current.Facing, current.Remaining - 1,
                    Math.Min(minBetweenTurns, current.SinceTurn + 1), 0, current.Crossed), true);
            if (current.Remaining < speed && current.Turns < maxConsecutiveTurns && (minBetweenTurns == 0 || current.SinceTurn >= minBetweenTurns))
                foreach (int delta in new[] { 1, -1 })
                    Visit((current.Pos, (Facing)(((int)current.Facing + delta + 6) % 6), current.Remaining, 0, current.Turns + 1, current.Crossed), false);
        }

        var result = new Dictionary<(Hex Pos, Facing Facing), List<PathStep>>();
        var bestCosts = new Dictionary<(Hex Pos, Facing Facing), (int Exposures, int Steps)>();
        foreach (var entry in costs)
        {
            var stateKey = entry.Key;
            if (requireFullSpeed && stateKey.Remaining != 0) continue;
            if (flyby && !stateKey.Crossed) continue;
            if (occupied.Contains(stateKey.Pos) && stateKey.Pos != allowOccupiedTarget) continue;
            var destination = (stateKey.Pos, stateKey.Facing);
            if (bestCosts.TryGetValue(destination, out var best) && best.CompareTo(entry.Value) <= 0) continue;
            var path = new List<PathStep>();
            (Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns, bool Crossed)? cursor = stateKey;
            while (cursor is { } step)
            {
                path.Add(new PathStep(step.Pos, step.Facing));
                cursor = parents[step];
            }
            path.Reverse();
            result[destination] = path;
            bestCosts[destination] = entry.Value;
        }
        return result;
    }

    /// <summary>All valid (position, facing) outcomes for the current actor's helm move, with routes.</summary>
    public List<MoveOption> GetValidMoves()
        => _stuntMoves ?? _ComputeReachable(turnReduction: _state.CurrentActor?.ManeuverSucceeded == true ? 1 : 0)
            .Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value)).ToList();

    public int? GetManeuverChance(ShipInstance ship)
    {
        ship.InitializeCrewRoles();
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        if (pilot < 0 || ship.ManeuverSucceeded is not null || ship.CrewActionsUsed.Contains(pilot) ||
            ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked || ship.Definition.DistanceBetweenTurns == 0)
            return null;
        return Math.Clamp(21 - (int)Math.Floor(15 + 1.5 * ship.Definition.Tier) + StuntBonus(ship, pilot), 0, 20) * 5;
    }

    public List<MoveOption> GetMovementPreviews()
    {
        var normal = GetValidMoves();
        var ship = _state.CurrentActor;
        if (ship is null || PendingStunt is not null || HasPendingFreeAttack || GetManeuverChance(ship) is null) return normal;
        var known = normal.Select(m => (m.Position, m.FinalFacing)).ToHashSet();
        normal.AddRange(_ComputeReachable(turnReduction: 1).Where(kv => !known.Contains(kv.Key))
            .Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value, true)));
        return normal;
    }

    public string? AttemptManeuver(Hex destination, Facing facing)
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm || PendingStunt is not null || HasPendingFreeAttack)
            return "Maneuver is unavailable right now.";
        if (GetManeuverChance(ship) is null) return "No available pilot can attempt Maneuver.";
        if (!GetMovementPreviews().Any(m => m.Position == destination && m.FinalFacing == facing && m.RequiresManeuver))
            return "Select a destination and facing that requires Maneuver.";
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        int roll = _dice.RollD20(), bonus = StuntBonus(ship, pilot);
        int dc = (int)Math.Floor(15 + 1.5 * ship.Definition.Tier);
        ship.ManeuverSucceeded = roll + bonus >= dc;
        ship.CrewActionsUsed.Add(pilot);
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
            $"{ShipLogName(ship)} · Maneuver: {CombatResolver.FormatCheck(roll, bonus, roll + bonus)} vs DC {dc} — {(ship.ManeuverSucceeded.Value ? "SUCCESS; distance between turns reduced by 1" : "FAIL; normal movement")}."));
        return null;
    }

    /// <summary>Returns the next turn-limited route a tracking projectile can make toward its target.</summary>
    public List<PathStep> GetProjectilePreviewPath(TrackingProjectile projectile)
    {
        var target = _state.GetShip(projectile.TargetShipId);
        if (target is null || projectile.Position == target.Position || projectile.Speed <= 0) return [];

        // A projectile moves forward first, then can turn up to twice at each
        // hex. Search position and facing to find a shortest turn-valid route.
        var start = (Pos: projectile.Position.Neighbor(projectile.Facing), Facing: projectile.Facing);
        var queue = new PriorityQueue<(Hex Pos, Facing Facing), (int Estimate, double LineError, int Turns, long Order)>();
        var costs = new Dictionary<(Hex Pos, Facing Facing), (int Distance, double LineError, int Turns)>();
        var parents = new Dictionary<(Hex Pos, Facing Facing), ((Hex Pos, Facing Facing) Previous, int Turn)?>();
        long order = 0;

        // Axial coordinates mapped to a pointy-top plane (without the common
        // sqrt(3) factor). This cross product ranks equally short routes by
        // how closely they follow the direct line to the target.
        double LineError(Hex pos)
        {
            double dx = target.Position.Q - projectile.Position.Q + (target.Position.R - projectile.Position.R) / 2.0;
            double dy = (target.Position.R - projectile.Position.R) * Math.Sqrt(3) / 2;
            double px = pos.Q - projectile.Position.Q + (pos.R - projectile.Position.R) / 2.0;
            double py = (pos.R - projectile.Position.R) * Math.Sqrt(3) / 2;
            return Math.Abs(dx * py - dy * px);
        }

        var startCost = (Distance: 1, LineError: LineError(start.Pos), Turns: 0);
        costs[start] = startCost;
        parents[start] = null;
        queue.Enqueue(start, (1 + start.Pos.DistanceTo(target.Position), startCost.LineError, 0, order++));
        (Hex Pos, Facing Facing)? goal = null;
        while (queue.TryDequeue(out var current, out var priority))
        {
            var cost = costs[current];
            if (priority.Estimate != cost.Distance + current.Pos.DistanceTo(target.Position) ||
                priority.LineError != cost.LineError || priority.Turns != cost.Turns) continue;
            if (current.Pos == target.Position) { goal = current; break; }

            foreach (int turn in new[] { 0, -1, 1, -2, 2 })
            {
                var facing = (Facing)(((int)current.Facing + turn + Hex.DirectionCount) % Hex.DirectionCount);
                var next = (Pos: current.Pos.Neighbor(facing), Facing: facing);
                var nextCost = (Distance: cost.Distance + 1, LineError: cost.LineError + LineError(next.Pos), Turns: cost.Turns + Math.Abs(turn));
                if (costs.TryGetValue(next, out var previous) && previous.CompareTo(nextCost) <= 0) continue;
                costs[next] = nextCost;
                parents[next] = (current, turn);
                queue.Enqueue(next, (nextCost.Distance + next.Pos.DistanceTo(target.Position), nextCost.LineError, nextCost.Turns, order++));
            }
        }
        if (goal is null) return [];

        var transitions = new List<((Hex Pos, Facing Facing) State, int Turn)>();
        var cursor = goal.Value;
        while (parents[cursor] is { } parent)
        {
            transitions.Add((cursor, parent.Turn));
            cursor = parent.Previous;
        }
        transitions.Reverse();
        var path = new List<PathStep> { new(start.Pos, start.Facing) };
        var position = start.Pos;
        var currentFacing = start.Facing;
        int moved = 1;
        foreach (var transition in transitions)
        {
            // Turns after the last hex are included so the next round starts
            // with the heading selected for this route.
            for (int i = 0; i < Math.Abs(transition.Turn); i++)
            {
                currentFacing = (Facing)(((int)currentFacing + Math.Sign(transition.Turn) + Hex.DirectionCount) % Hex.DirectionCount);
                path.Add(new PathStep(position, currentFacing));
            }
            if (moved == projectile.Speed) break;
            position = transition.State.Pos;
            path.Add(new PathStep(position, currentFacing));
            moved++;
        }

        // Preserve the existing rule that a projectile cannot finish its move
        // on another ship, while allowing it to pass through occupied hexes.
        var occupied = _state.Ships.Where(s => !s.IsDestroyed && s.InstanceId != target.InstanceId)
            .Select(s => s.Position).ToHashSet();
        while (path.Count > 0 && occupied.Contains(path[^1].Position)) path.RemoveAt(path.Count - 1);
        return path;
    }

    /// <summary>Projectiles and paths currently useful to render on the combat map.</summary>
    public List<TrackingProjectilePreview> GetProjectilePreviews()
    {
        var previews = new List<TrackingProjectilePreview>();
        foreach (var projectile in _state.Projectiles)
        {
            previews.Add(new TrackingProjectilePreview(projectile, GetProjectilePreviewPath(projectile)));
        }

        // A successful launch is pending until Gunnery resolves, but show its predicted
        // movement immediately so players can see whether the missile can reach its target.
        if (_state.Phase == CombatPhase.Gunnery)
            previews.AddRange(_pendingLaunches.Select(launch =>
                new TrackingProjectilePreview(launch.Projectile, GetProjectilePreviewPath(launch.Projectile))));

        return previews;
    }

    public sealed record TrackingProjectilePreview(TrackingProjectile Projectile, IReadOnlyList<PathStep> Path);

    /// <summary>
    /// Moves the current actor to a specific (position, facing) if it is reachable.
    /// The reachability set already enforces all movement rules, so a hit means valid.
    /// </summary>
    public MoveValidationResult TryMoveTo(Hex position, Facing facing)
    {
        if (HasPendingFreeAttack) return MoveValidationResult.Fail("Resolve the free attack first.");
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        if (PendingStunt is not null)
        {
            var option = _stuntMoves?.FirstOrDefault(option => option.Position == position && option.FinalFacing == facing);
            if (option is null)
                return MoveValidationResult.Fail("That destination is not allowed by the stunt.");
            string stunt = PendingStunt;
            if (stunt == "Flyby")
            {
                var crossings = option.Path.Zip(option.Path.Skip(1))
                    .Where(pair => pair.First.Position != pair.Second.Position)
                    .SelectMany(pair => _state.Ships.Where(s => s.TeamId != ship.TeamId && !s.IsDestroyed && s.Position == pair.Second.Position))
                    .ToList();
                if (crossings.Count != 1)
                    return MoveValidationResult.Fail("Flyby must cross exactly one enemy-occupied hex; friendly crossings are unrestricted.");
                var target = crossings[0];
                int pilot = ship.CrewRoles.IndexOf("Pilot");
                if (pilot < 0 || ship.CrewActionsUsed.Contains(pilot) ||
                    ship.Systems.GetEffective(ShipSystem.Engines) is SystemCondition.Malfunctioning or SystemCondition.Wrecked)
                    return MoveValidationResult.Fail("Flyby requires an available pilot and functioning engines.");
                int roll = _dice.RollD20();
                int bonus = StuntBonus(ship, pilot);
                int dc = (int)Math.Floor(15 + 1.5 * target.Definition.Tier);
                ship.FlybyTargetId = target.InstanceId;
                ship.FlybySucceeded = roll + bonus >= dc;
                ship.CrewActionsUsed.Add(pilot);
                LastEvents.Add(new CombatEvent("Helm", ship.InstanceId,
                    $"{ShipLogName(ship)} · Flyby against {ShipLogName(target)}: {CombatResolver.FormatCheck(roll, bonus, roll + bonus)} vs DC {dc} — {(ship.FlybySucceeded ? "SUCCESS" : "FAIL; normal free attack and targeting apply")}."));
            }
            PendingStunt = null;
            _stuntMoves = null;
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ShipLogName(ship)} begins {stunt}."));
            BeginCommittedMove(option);
            return MoveValidationResult.Ok(position, facing);
        }
        if (!_ComputeReachable(turnReduction: ship.ManeuverSucceeded == true ? 1 : 0).TryGetValue((position, facing), out var path))
            return MoveValidationResult.Fail("That hex/facing is not reachable with this ship's movement.");

        ship.InitializeCrewRoles();
        if (ship.Definition.Crew.Count > 0)
        {
            int pilot = ship.CrewRoles.IndexOf("Pilot");
            bool glide = pilot < 0 || ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked;
            if (!glide) ship.CrewActionsUsed.Add(pilot);
            else
            {
                int crew = ship.AvailableGlideCrew();
                if (crew < 0) return MoveValidationResult.Fail("No crew member can glide.");
                ship.CrewMinorActionsUsed.Add(crew);
                ship.RoundAcTlModifier += ship.Definition.Crew[crew].Ranks;
            }
        }

        BeginCommittedMove(new MoveOption(position, facing, path));
        return MoveValidationResult.Ok(position, facing);
    }

    /// <summary>A weapon that the current actor can fire at a specific target.</summary>
    public sealed record TargetOption(string WeaponName, string TargetShipId, int Range, int HitChancePercent, int WeaponIndex, int RangePenalty, int ArcPenalty, FiringArc Arc, bool BroadArc, bool ArcChoiceRequired, List<FiringArc> ArcChoices, bool Flyby = false);

    private static bool IsFlybyTarget(ShipInstance ship, ShipInstance target)
        => ship.FlybySucceeded && ship.FlybyCrossed && ship.FlybyTargetId == target.InstanceId;

    private static bool FlybyArcAllowed(ShipInstance ship, Weapon weapon)
        => ship.FlybyFiringArc is null || weapon.Arc == FiringArc.AllArcs || weapon.Arc == ship.FlybyFiringArc;

    /// <summary>
    /// Computes all valid, unfired, available (weapon, target) pairs for the current actor during Gunnery.
    /// </summary>
    public List<TargetOption> GetValidTargets()
    {
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Gunnery) return [];

        int bonus = CombatResolver.ComputeGunnerBonus(ship);
        if (bonus == int.MinValue) return [];
        if (ship.Definition.Crew.Count > 0)
        {
            int gunner = ship.AvailableGunner();
            bool snap = gunner < 0;
            int crew = snap ? ship.AvailableMinorCrew() : gunner;
            if (crew < 0 || (snap && ship.GunnerActionTaken)) return [];
            bonus = ship.CrewBonus(crew, "gunnery", "Gunner") + ship.Systems.GetActionPenalty(ShipSystem.WeaponsArray)
                + ship.Systems.PowerCoreGlobalPenalty - (snap ? 2 : 0);
        }
        var options = new List<TargetOption>();
        var arcChoices = new Dictionary<string, List<FiringArc>>();
        foreach (var target in _state.ActiveShips().Where(target => target.TeamId != ship.TeamId))
        {
            double angle = ship.AngleFromFacing(target.Position);
            if (IsSharedArcBoundary(angle))
                arcChoices[target.InstanceId] = ship.Weapons.Select((weapon, index) => (Weapon: weapon, Index: index))
                    .Where(x => !_firedWeapons.Contains((ship.InstanceId, x.Index)) && ship.HasWeaponUses(x.Index) &&
                                x.Weapon.Arc != FiringArc.AllArcs && x.Weapon.CanTarget(ship.Position.DistanceTo(target.Position), angle))
                    .Select(x => x.Weapon.Arc).Distinct().ToList();
        }
        for (int wi = 0; wi < ship.Weapons.Count; wi++)
        {
            if (_firedWeapons.Contains((ship.InstanceId, wi))) continue;
            if (EarlierLinkedMount(ship, wi)) continue;
            if (!ship.HasWeaponUses(wi)) continue;
            var weapon = ship.Weapons[wi];
            var mountIndices = LinkedMountIndices(ship, wi);
            if (mountIndices.Any(i => !ship.HasWeaponUses(i) || _firedWeapons.Contains((ship.InstanceId, i)))) continue;
            if (weapon.Type == WeaponType.Tracking && weapon.ProjectileSpeed <= 0) continue;
            foreach (var target in _state.ActiveShips())
            {
                if (target.TeamId == ship.TeamId) continue; // no friendly fire in v1
                if (IsFlybyTarget(ship, target))
                {
                    if (!FlybyArcAllowed(ship, weapon) || !weapon.CanFireAtCloseRange || weapon.MaxFireRange < 1) continue;
                    bool chooseArc = weapon.Arc == FiringArc.AllArcs && ship.FlybyFiringArc is null;
                    int defense = weapon.Type == WeaponType.Tracking ? target.CurrentTL : target.CurrentAC;
                    options.Add(new TargetOption(CombineLinkedWeapon(ship, wi, mountIndices).Name, target.InstanceId, 1,
                        CombatResolver.HitChancePercent(bonus, defense), wi, 0, 0, weapon.Arc, weapon.BroadArc,
                        chooseArc, chooseArc ? [FiringArc.Forward, FiringArc.Starboard, FiringArc.Aft, FiringArc.Port] : [], true));
                    continue;
                }
                int range = ship.Position.DistanceTo(target.Position);
                double angle = ship.AngleFromFacing(target.Position);
                if (!weapon.CanTarget(range, angle)) continue;
                var choices = arcChoices.GetValueOrDefault(target.InstanceId) ?? [];
                if (choices.Count > 0 && _targetArcChoices.TryGetValue((ship.InstanceId, target.InstanceId), out var committedArc) && weapon.Arc != FiringArc.AllArcs && !weapon.BroadArc && weapon.Arc != committedArc)
                    continue;

                int defenseValue = weapon.Type switch
                {
                    WeaponType.Tracking => target.CurrentTL,
                    _ => target.CurrentAC
                };
                int rangePenalty = weapon.RangePenalty(range);
                int arcPenalty = weapon.ArcPenalty(angle);
                int effectiveBonus = bonus == int.MinValue ? bonus : bonus + rangePenalty + arcPenalty;
                int hitChance = CombatResolver.HitChancePercent(effectiveBonus, defenseValue);
                bool choiceRequired = choices.Count > 1 && !_targetArcChoices.ContainsKey((ship.InstanceId, target.InstanceId));
                options.Add(new TargetOption(CombineLinkedWeapon(ship, wi, mountIndices).Name, target.InstanceId, range, hitChance, wi, rangePenalty, arcPenalty, weapon.Arc, weapon.BroadArc, choiceRequired, choices));
            }
        }
        return options;
    }

    // ────────────────────────────────────────────────
    //  Internal helpers
    // ────────────────────────────────────────────────

    private static List<int> LinkedMountIndices(ShipInstance ship, int weaponIndex)
    {
        var weapon = ship.Weapons[weaponIndex];
        if (weapon.Type != WeaponType.DirectFire || string.IsNullOrWhiteSpace(weapon.LinkedGroup))
            return [weaponIndex];

        var matching = ship.Weapons.Select((candidate, index) => (candidate, index))
            .Where(x => x.candidate.Type == WeaponType.DirectFire &&
                x.candidate.LinkedGroup == weapon.LinkedGroup && x.candidate.Arc == weapon.Arc &&
                x.candidate.BroadArc == weapon.BroadArc &&
                x.candidate.Name == weapon.Name && x.candidate.Damage == weapon.Damage &&
                x.candidate.MinRange == weapon.MinRange && x.candidate.RangeIncrement == weapon.RangeIncrement &&
                x.candidate.LimitedFireUses == weapon.LimitedFireUses)
            .Select(x => x.index).ToList();
        return matching.Count > 1 ? matching : [weaponIndex];
    }

    private static bool EarlierLinkedMount(ShipInstance ship, int weaponIndex)
        => LinkedMountIndices(ship, weaponIndex).First() != weaponIndex;

    private static Weapon CombineLinkedWeapon(ShipInstance ship, int weaponIndex, IReadOnlyList<int> mountIndices)
    {
        var weapon = ship.Weapons[weaponIndex];
        if (mountIndices.Count < 2) return weapon;
        return weapon with
        {
            Name = $"{weapon.Name} battery ({mountIndices.Count})",
            Damage = weapon.Damage with
            {
                DiceCount = weapon.Damage.DiceCount * mountIndices.Count,
                FlatBonus = weapon.Damage.FlatBonus * mountIndices.Count
            }
        };
    }

    private static Facing TrackingLaunchFacing(ShipInstance attacker, ShipInstance target, Weapon weapon)
    {
        int shipFacing = (int)attacker.Facing;
        IEnumerable<int> possibleFacings = weapon.Arc switch
        {
            FiringArc.Forward => [shipFacing],
            FiringArc.Starboard => [(shipFacing + 1) % Hex.DirectionCount, (shipFacing + 2) % Hex.DirectionCount],
            FiringArc.Aft => [(shipFacing + 3) % Hex.DirectionCount],
            FiringArc.Port => [(shipFacing + 4) % Hex.DirectionCount, (shipFacing + 5) % Hex.DirectionCount],
            FiringArc.AllArcs => Enumerable.Range(0, Hex.DirectionCount),
            _ => [shipFacing]
        };

        int directionToTarget = attacker.Position.DirectionTo(target.Position);
        return (Facing)possibleFacings.MinBy(facing =>
        {
            int difference = Math.Abs(facing - directionToTarget);
            return Math.Min(difference, Hex.DirectionCount - difference);
        });
    }

    private string ShipLogName(ShipInstance ship)
        => $"{_state.DisplayName(ship)} (T{ship.TeamId + 1})";

    private void LogShipStatusChange(ShipInstance ship, bool wasDisabled, bool wasDestroyed, string phase)
    {
        if (!wasDestroyed && ship.IsDestroyed)
            LastEvents.Add(new CombatEvent(phase, ship.InstanceId, $"{ShipLogName(ship)} is destroyed!"));
        else if (!wasDisabled && ship.IsDisabled)
            LastEvents.Add(new CombatEvent(phase, ship.InstanceId, $"{ShipLogName(ship)} is disabled!"));
    }

    private string ProjectileLogName(TrackingProjectile projectile)
        => $"{projectile.Name} (Team {projectile.TeamId + 1})";

    private static bool IsSharedArcBoundary(double angle)
        => Math.Abs(Math.Abs(angle) - 30) <= 0.01 || Math.Abs(Math.Abs(angle) - 150) <= 0.01;

    private static string ProjectileDisplayName(string weaponName)
        => weaponName.EndsWith(" Launcher", StringComparison.OrdinalIgnoreCase)
            ? weaponName[..^" Launcher".Length]
            : weaponName;

    private void ResolveProjectile(TrackingProjectile projectile, int roll, int checkBonus, int defense, long order)
    {
        var target = _state.GetShip(projectile.TargetShipId);
        if (target is null)
        {
            _state.Projectiles.RemoveAll(candidate => candidate.Id == projectile.Id);
            return;
        }
        int total = roll + checkBonus;
        if (roll == 1 || (roll != 20 && total < defense))
        {
            string ownerName = _state.GetShip(projectile.OwnerShipId) is { } owner ? ShipLogName(owner) : $"Team {projectile.TeamId + 1}";
            LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId,
                $"{ownerName}'s {ProjectileLogName(projectile)} tracking check {CombatResolver.FormatCheck(roll, checkBonus, total)} vs TL {defense} — MISS, projectile lost."));
            _state.Projectiles.RemoveAll(candidate => candidate.Id == projectile.Id);
            return;
        }

        Hex impactFrom = projectile.Position;
        var path = GetProjectilePreviewPath(projectile);
        int moved = 0;
        foreach (var next in path)
        {
            if (projectile.Position != next.Position) moved++;
            projectile.Facing = next.Facing;
            projectile.Position = next.Position;
            if (projectile.Position == target.Position) break;
        }

        if (projectile.Position == target.Position)
        {
            _pendingProjectileImpacts.Add(new PendingProjectileImpactSnapshot(projectile, roll, checkBonus, defense, impactFrom, order));
        }
        else
        {
            LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId,
                $"{ProjectileLogName(projectile)} advances {moved} hexes toward {ShipLogName(target)}. Gunnery: {CombatResolver.FormatCheck(roll, checkBonus, total)} vs TL {defense}."));
        }
    }

    private void PrepareGunneryResolution()
    {
        var alreadyDestroyed = _state.Ships.Where(s => s.IsDestroyed).Select(s => s.InstanceId).ToHashSet();
        foreach (var projectile in _state.Projectiles.ToList())
        {
            var target = _state.GetShip(projectile.TargetShipId);
            if (target is null || alreadyDestroyed.Contains(target.InstanceId))
            {
                LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId, $"{ProjectileLogName(projectile)} loses its target and is removed."));
                _state.Projectiles.RemoveAll(candidate => candidate.Id == projectile.Id);
                continue;
            }
            ResolveProjectile(projectile, _dice.RollD20(), projectile.GunnerBonus, target.CurrentTL, ++_nextAttackOrder);
        }
        foreach (var (projectile, check, order) in _pendingLaunches)
        {
            _state.Projectiles.Add(projectile);
            ResolveProjectile(projectile, check.Roll, check.Modifier, check.DefenseValue, order);
        }
        _pendingLaunches.Clear();
        _gunneryResolutionPrepared = true;
    }

    /// <summary>Pending point-defense choice, if multiple missiles share an eligible ship arc.</summary>
    private PointDefensePrompt? GetPointDefensePrompt()
    {
        foreach (var group in GetPointDefenseGroups())
        {
            if (group.Impacts.Count < 2) continue;
            var target = _state.GetShip(group.TargetShipId)!;
            var missiles = group.Impacts.Select(impact => new PointDefenseMissileOption(
                impact.Projectile.Id, ProjectileLogName(impact.Projectile), impact.Projectile.Speed,
                GetAvailablePointDefenseWeapons(impact).Select(index => new PointDefenseWeaponOption(index,
                    target.Weapons[index].Name, target.Weapons[index].PointDefenseBonus)).ToList())).ToList();
            return new PointDefensePrompt(group.TargetShipId, group.Arc, missiles);
        }
        return null;
    }

    private sealed record PointDefenseGroup(string TargetShipId, FiringArc Arc, List<PendingProjectileImpactSnapshot> Impacts);

    private List<PointDefenseGroup> GetPointDefenseGroups()
        => _pendingProjectileImpacts
            .Where(impact => !_passedPointDefenseProjectiles.Contains(impact.Projectile.Id) && GetAvailablePointDefenseWeapons(impact).Count > 0)
            .GroupBy(impact =>
            {
                var target = _state.GetShip(impact.Projectile.TargetShipId)!;
                var quadrant = target.PhysicalQuadrantOf(impact.ImpactFrom);
                return (impact.Projectile.TargetShipId, Arc: FiringArcFor(quadrant));
            })
            .Select(group => new PointDefenseGroup(group.Key.TargetShipId, group.Key.Arc, group.ToList()))
            .ToList();

    private List<int> GetAvailablePointDefenseWeapons(PendingProjectileImpactSnapshot impact)
    {
        var target = _state.GetShip(impact.Projectile.TargetShipId);
        if (target is null || target.IsDestroyed) return [];
        double angle = target.AngleFromFacing(impact.ImpactFrom);
        return target.Weapons.Select((weapon, index) => (weapon, index))
            .Where(x => x.weapon.PointDefenseBonus > 0 && x.weapon.CoversAngle(angle) &&
                !_pointDefenseUsed.Contains((target.InstanceId, x.index)) && target.HasWeaponUses(x.index))
            .Select(x => x.index).ToList();
    }

    private static FiringArc FiringArcFor(Quadrant quadrant) => quadrant switch
    {
        Quadrant.Forward => FiringArc.Forward,
        Quadrant.Starboard => FiringArc.Starboard,
        Quadrant.Aft => FiringArc.Aft,
        Quadrant.Port => FiringArc.Port,
        _ => FiringArc.Forward
    };

    private bool ResolveAutomaticPointDefense()
    {
        while (true)
        {
            var groups = GetPointDefenseGroups();
            var prompt = groups.FirstOrDefault(group => group.Impacts.Count > 1);
            if (prompt is not null) return true;
            var single = groups.FirstOrDefault();
            if (single is null) return false;
            var impact = single.Impacts[0];
            ResolvePointDefenseAttempts(impact, GetAvailablePointDefenseWeapons(impact));
            _passedPointDefenseProjectiles.Add(impact.Projectile.Id);
        }
    }

    public string? ResolvePointDefense(string projectileId, bool attempt)
    {
        var prompt = PendingPointDefense;
        if (prompt is null) return "No point-defense choice is pending.";
        var missile = prompt.Missiles.FirstOrDefault(option => option.ProjectileId == projectileId);
        if (missile is null) return "That missile is not part of the pending point-defense choice.";
        var impact = _pendingProjectileImpacts.FirstOrDefault(item => item.Projectile.Id == projectileId);
        if (impact is null) return "That missile is no longer awaiting damage.";
        if (attempt) ResolvePointDefenseAttempts(impact, missile.Weapons.Select(weapon => weapon.WeaponIndex).ToList());
        _passedPointDefenseProjectiles.Add(projectileId);
        AdvancePhase();
        return null;
    }

    private void ResolvePointDefenseAttempts(PendingProjectileImpactSnapshot impact, IReadOnlyList<int> weaponIndices)
    {
        var target = _state.GetShip(impact.Projectile.TargetShipId);
        if (target is null) return;
        int dc = 10 + impact.Projectile.Speed;
        foreach (int weaponIndex in weaponIndices)
        {
            if (weaponIndex < 0 || weaponIndex >= target.Weapons.Count || !target.HasWeaponUses(weaponIndex) ||
                !_pointDefenseUsed.Add((target.InstanceId, weaponIndex))) continue;
            var weapon = target.Weapons[weaponIndex];
            target.ConsumeWeaponUse(weaponIndex);
            int roll = _dice.RollD20();
            int total = roll + weapon.PointDefenseBonus;
            bool hit = roll == 20 || (roll != 1 && total >= dc);
            LastEvents.Add(new CombatEvent("Gunnery", target.InstanceId,
                $"{ShipLogName(target)} uses {weapon.Name} for point defense against {ProjectileLogName(impact.Projectile)}: {CombatResolver.FormatCheck(roll, weapon.PointDefenseBonus, total)} vs DC {dc} — {(hit ? "HIT, projectile destroyed." : "MISS.")}"));
            if (!hit) continue;
            _pendingProjectileImpacts.Remove(impact);
            _state.Projectiles.RemoveAll(projectile => projectile.Id == impact.Projectile.Id);
            break;
        }
    }

    private void FinishGunneryResolution()
    {
        // A newly launched projectile occupies its attack's place only if it reaches
        // the target this round. Existing projectiles impact after this round's shots.
        foreach (var entry in _pendingHits.Select((h, i) => (h.Order, Index: i, Impact: (PendingProjectileImpactSnapshot?)null))
            .Concat(_pendingProjectileImpacts.Select(i => (i.Order, Index: -1, Impact: (PendingProjectileImpactSnapshot?)i)))
            .OrderBy(e => e.Order))
        {
            if (entry.Impact is { } impact)
            {
                var projectile = impact.Projectile;
                var target = _state.GetShip(projectile.TargetShipId);
                if (target is null || target.IsDestroyed) continue;
                ShipSystem? targetedSystem = _targetedSystems.Remove((projectile.OwnerShipId, target.InstanceId), out var chosenSystem) ? chosenSystem : null;
                bool wasDisabled = target.IsDisabled, wasDestroyed = target.IsDestroyed;
                var result = CombatResolver.ResolveProjectileImpact(projectile, target, _dice, impact.Roll, impact.CheckBonus, impact.Defense, impact.ImpactFrom,
                    _state.GetShip(projectile.OwnerShipId)?.DivertWeapons == true, targetedSystem);
                string ownerName = _state.GetShip(projectile.OwnerShipId) is { } owner ? ShipLogName(owner) : $"Team {projectile.TeamId + 1}";
                LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId,
                    $"{ownerName}'s {ProjectileLogName(projectile)} hits {ShipLogName(target)}: {result.Description}"));
                LogShipStatusChange(target, wasDisabled, wasDestroyed, "Gunnery");
                _state.Projectiles.RemoveAll(candidate => candidate.Id == projectile.Id);
            }
            else
            {
                var (attacker, target, weapon, check, quadrant, savedTargetedSystem, _) = _pendingHits[entry.Index];
                ShipSystem? targetedSystem = savedTargetedSystem ?? (_targetedSystems.Remove((attacker.InstanceId, target.InstanceId), out var chosenSystem) ? chosenSystem : null);
                int rolledDamage = weapon.Damage.Roll(_dice, attacker.DivertWeapons);
                bool wasDisabled = target.IsDisabled, wasDestroyed = target.IsDestroyed;
                var damage = CombatResolver.ApplyDamage(target, quadrant, rolledDamage, check.Roll, _dice, targetedSystem);
                LastEvents.Add(new CombatEvent("Gunnery", attacker.InstanceId,
                    $"{ShipLogName(attacker)} → {ShipLogName(target)} · {weapon.Name}: {damage.Description}."));
                LogShipStatusChange(target, wasDisabled, wasDestroyed, "Gunnery");
            }
        }
        _pendingProjectileImpacts.Clear();
        _pendingHits.Clear();
        _firedWeapons.Clear();
        _targetArcChoices.Clear();
        _pointDefenseUsed.Clear();
        _passedPointDefenseProjectiles.Clear();
        _gunneryResolutionPrepared = false;
        _nextAttackOrder = 0;
        CheckVictory();
        if (!_state.IsComplete)
        {
            _state.Round++;
            _state.Phase = CombatPhase.CrewSetup;
            foreach (var ship in _state.Ships) ship.BeginRound();
            LastEvents.Add(new CombatEvent("CrewSetup", "", $"Round {_state.Round} — assign crew roles."));
        }
    }

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
        var activeTeams = _state.ActiveShips().Select(s => s.TeamId).Distinct().ToList();
        if (activeTeams.Count > 1) return;
        _state.Phase = CombatPhase.Complete;
        _state.Winner = activeTeams.Count == 0 ? "Mutual destruction" : $"Team {activeTeams[0] + 1}";
        LastEvents.Add(new CombatEvent("Complete", "", activeTeams.Count == 0
            ? "All ships disabled or destroyed — mutual destruction."
            : $"{_state.Winner} wins!"));
    }
}
