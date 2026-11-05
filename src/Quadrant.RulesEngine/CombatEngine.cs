namespace Quadrant.RulesEngine;

/// <summary>
/// An event emitted during combat resolution, used to build a log and notify clients.
/// </summary>
public sealed record CombatEvent(string Phase, string ShipId, string Message)
{
    public static CombatEvent Info(string phase, string shipId, string msg) => new(phase, shipId, msg);
}

public sealed record PendingHitSnapshot(string AttackerId, string TargetId, Weapon Weapon, AttackCheckResult Check, Quadrant Quadrant);
public sealed record PendingLaunchSnapshot(TrackingProjectile Projectile, AttackCheckResult Check);
public sealed record FiredWeaponSnapshot(string ShipId, int WeaponIndex);
public sealed record TargetArcSnapshot(string ShipId, string TargetId, FiringArc Arc);
public sealed record ShieldAllocation(int Forward, int Starboard, int Aft, int Port);
public sealed record EngineerAction(string ShipId, int CrewIndex, string Action, string Target, ShieldAllocation? Shields);
public sealed record CombatEngineSnapshot(
    List<PendingHitSnapshot> PendingHits, List<PendingLaunchSnapshot> PendingLaunches,
    List<FiredWeaponSnapshot> FiredWeapons, List<TargetArcSnapshot> TargetArcs,
    List<CombatEngine.MoveOption>? StuntMoves, string? PendingStunt,
    CombatEngine.MoveOption? CommittedMove, int MoveStep, Hex LastUnoccupied,
    Facing LastUnoccupiedFacing, string? FreeAttackShipId, List<CombatEvent> Events);

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
    private readonly List<(ShipInstance Attacker, ShipInstance Target, Weapon Weapon, AttackCheckResult Check, Quadrant Quadrant)> _pendingHits = [];
    private readonly List<(TrackingProjectile Projectile, AttackCheckResult Check)> _pendingLaunches = [];
    private readonly HashSet<(string ShipId, int WeaponIndex)> _firedWeapons = [];
    private readonly Dictionary<(string ShipId, string TargetId), FiringArc> _targetArcChoices = [];
    private List<MoveOption>? _stuntMoves;
    public string? PendingStunt { get; private set; }
    private MoveOption? _committedMove;
    private int _moveStep;
    private Hex _lastUnoccupied;
    private Facing _lastUnoccupiedFacing;
    public string? FreeAttackShipId { get; private set; }
    public bool HasPendingFreeAttack => FreeAttackShipId is not null;

    public IReadOnlyList<int> GetFreeAttackWeapons()
    {
        var defender = FreeAttackShipId is null ? null : _state.GetShip(FreeAttackShipId);
        if (defender is null || defender.IsDestroyed || defender.IsDisabled || CombatResolver.ComputeGunnerBonus(defender) == int.MinValue) return [];
        defender.InitializeCrewRoles();
        if (!defender.CrewRoles.Contains("Gunner")) return [];
        return defender.Weapons.Select((weapon, index) => (weapon, index))
            .Where(x => x.weapon.Type == WeaponType.DirectFire && defender.HasWeaponUses(x.index))
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
            defender.ConsumeWeaponUse(wi);
            var weapon = defender.Weapons[wi];
            int bonus = defender.CrewBonus(gunner, "gunnery", "Gunner") + defender.Systems.GetActionPenalty(ShipSystem.WeaponsArray) + defender.Systems.PowerCoreGlobalPenalty;
            int roll = _dice.RollD20();
            int total = roll + bonus;
            bool hit = roll == 20 || (roll != 1 && total >= target.CurrentAC);
            string description = $"{ShipLogName(defender)} fires {weapon.Name} at {ShipLogName(target)} as a free attack: Gunnery {CombatResolver.FormatCheck(roll, bonus, total)} vs AC {target.CurrentAC} — {(hit ? "HIT" : "MISS")}.";
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
            FreeAttackShipId = occupant.InstanceId;
            if (GetFreeAttackWeapons().Count > 0) return;
            FreeAttackShipId = null;
        }
        LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ShipLogName(ship)} moves to {ship.Position}, facing {ship.Facing}."));
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
        _pendingHits.Select(h => new PendingHitSnapshot(h.Attacker.InstanceId, h.Target.InstanceId, h.Weapon, h.Check, h.Quadrant)).ToList(),
        _pendingLaunches.Select(l => new PendingLaunchSnapshot(l.Projectile, l.Check)).ToList(),
        _firedWeapons.Select(w => new FiredWeaponSnapshot(w.ShipId, w.WeaponIndex)).ToList(),
        _targetArcChoices.Select(a => new TargetArcSnapshot(a.Key.ShipId, a.Key.TargetId, a.Value)).ToList(),
        _stuntMoves, PendingStunt, _committedMove, _moveStep, _lastUnoccupied, _lastUnoccupiedFacing,
        FreeAttackShipId, LastEvents.ToList());

    public void RestoreSnapshot(CombatEngineSnapshot snapshot)
    {
        foreach (var h in snapshot.PendingHits)
            _pendingHits.Add((_state.GetShip(h.AttackerId) ?? throw new InvalidDataException("Missing attacker in saved game."),
                _state.GetShip(h.TargetId) ?? throw new InvalidDataException("Missing target in saved game."), h.Weapon, h.Check, h.Quadrant));
        foreach (var l in snapshot.PendingLaunches) _pendingLaunches.Add((l.Projectile, l.Check));
        foreach (var w in snapshot.FiredWeapons) _firedWeapons.Add((w.ShipId, w.WeaponIndex));
        foreach (var a in snapshot.TargetArcs) _targetArcChoices.Add((a.ShipId, a.TargetId), a.Arc);
        _stuntMoves = snapshot.StuntMoves;
        PendingStunt = snapshot.PendingStunt;
        _committedMove = snapshot.CommittedMove;
        _moveStep = snapshot.MoveStep;
        _lastUnoccupied = snapshot.LastUnoccupied;
        _lastUnoccupiedFacing = snapshot.LastUnoccupiedFacing;
        FreeAttackShipId = snapshot.FreeAttackShipId;
        LastEvents.AddRange(snapshot.Events);
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
                ComputeActionOrder();
                LastEvents.Add(new CombatEvent("Helm", "", $"Round {_state.Round} — Helm phase. Order: {string.Join(" → ", _state.ActionOrder.Select(id => _state.GetShip(id)?.Name ?? id))}."));
                break;

            case CombatPhase.Helm:
                _state.Phase = CombatPhase.Gunnery;
                _state.CurrentActorIndex = 0;
                LastEvents.Add(new CombatEvent("Gunnery", "", "Gunnery phase."));
                break;

            case CombatPhase.Gunnery:
                var projectilesAlreadyInFlight = _state.Projectiles.ToList();
                var alreadyDestroyed = _state.Ships.Where(s => s.IsDestroyed).Select(s => s.InstanceId).ToHashSet();
                foreach (var projectile in projectilesAlreadyInFlight)
                {
                    var target = _state.GetShip(projectile.TargetShipId);
                    if (target is null || alreadyDestroyed.Contains(target.InstanceId))
                    {
                        LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId, $"{ProjectileLogName(projectile)} loses its target and is removed."));
                        _state.Projectiles.Remove(projectile);
                        continue;
                    }
                    int roll = _dice.RollD20();
                    ResolveProjectile(projectile, roll, projectile.GunnerBonus, target.CurrentTL);
                }
                foreach (var (projectile, check) in _pendingLaunches)
                {
                    _state.Projectiles.Add(projectile);
                    ResolveProjectile(projectile, check.Roll, check.Modifier, check.DefenseValue);
                }
                _pendingLaunches.Clear();
                foreach (var (attacker, target, weapon, check, quadrant) in _pendingHits)
                {
                    int rolledDamage = weapon.Damage.Roll(_dice, attacker.DivertWeapons);
                    bool wasDisabled = target.IsDisabled, wasDestroyed = target.IsDestroyed;
                    var damage = CombatResolver.ApplyDamage(target, quadrant, rolledDamage, check.Roll, _dice);
                    LastEvents.Add(new CombatEvent("Gunnery", attacker.InstanceId,
                        $"{ShipLogName(target)} takes damage from {ShipLogName(attacker)}'s {weapon.Name}: {damage.Description}."));
                    LogShipStatusChange(target, wasDisabled, wasDestroyed, "Gunnery");
                }
                _pendingHits.Clear();
                _firedWeapons.Clear();
                _targetArcChoices.Clear();
                // End of round — check for victory, then start next round.
                CheckVictory();
                if (!_state.IsComplete)
                {
                    _state.Round++;
                    _state.Phase = CombatPhase.CrewSetup;
                    foreach (var ship in _state.Ships)
                        ship.BeginRound();
                    LastEvents.Add(new CombatEvent("CrewSetup", "", $"Round {_state.Round} — assign crew roles."));
                }
                break;
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
    public MoveValidationResult TryTurnInPlace(Facing facing)
    {
        if (HasPendingFreeAttack) return MoveValidationResult.Fail("Resolve the free attack first.");
        if (PendingStunt is not null) return MoveValidationResult.Fail("Finish the selected stunt first.");
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm)
            return MoveValidationResult.Fail("No ship to move.");

        ship.InitializeCrewRoles();
        if (ship.Definition.Crew.Count > 0 && !ship.CrewRoles.Contains("Pilot"))
            return MoveValidationResult.Fail("Turning in place requires a pilot; Glide cannot perform stunts.");
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

        var target = _state.GetShip(action.TargetShipId);
        if (target is null || target.IsDestroyed || target.TeamId == ship.TeamId)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId, $"{ship.Name} has no valid target."));
            return null;
        }

        int targetRange = ship.Position.DistanceTo(target.Position);
        double targetAngle = ship.AngleFromFacing(target.Position);
        if (!weapon.CanTarget(targetRange, targetAngle)) return null;
        var arcKey = (ship.InstanceId, target.InstanceId);
        bool sharedBoundary = IsSharedArcBoundary(targetAngle);
        if (sharedBoundary)
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
                (weapon.Arc != FiringArc.AllArcs && weapon.Arc != selectedArc))) return null;
            if (selectedArc is not null) _targetArcChoices[arcKey] = selectedArc.Value;
        }
        else if (_targetArcChoices.TryGetValue(arcKey, out var selectedArc) && weapon.Arc != FiringArc.AllArcs && weapon.Arc != selectedArc)
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
        if (!_firedWeapons.Add((ship.InstanceId, action.WeaponIndex))) return null;
        ship.ConsumeWeaponUse(action.WeaponIndex);

        int? crewBonus = actingCrew >= 0 ? ship.CrewBonus(actingCrew, "gunnery", "Gunner") : null;
        if (crewBonus.HasValue)
        {
            int penalty = ship.Systems.GetActionPenalty(ShipSystem.WeaponsArray);
            crewBonus = penalty == int.MinValue ? int.MinValue : crewBonus.Value + penalty + ship.Systems.PowerCoreGlobalPenalty - (snapShot ? 2 : 0);
            if (snapShot) ship.CrewMinorActionsUsed.Add(actingCrew);
            else ship.CrewActionsUsed.Add(actingCrew);
            ship.GunnerActionTaken = true;
        }
        var check = CombatResolver.RollAttack(ship, target, weapon, _dice, gunnerBonus: crewBonus, rollDamage: false);
        if (!check.IsHit)
        {
            LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId,
                $"{ShipLogName(ship)} fires {weapon.Name} at {ShipLogName(target)}: {check.Description}"));
            return check;
        }

        LastEvents.Add(new CombatEvent("Gunnery", ship.InstanceId,
            $"{ShipLogName(ship)} fires {weapon.Name} at {ShipLogName(target)}: {check.Description}"));

        if (weapon.Type == WeaponType.Tracking)
        {
            var projectile = new TrackingProjectile
            {
                Id = $"projectile-{Guid.NewGuid():N}", Name = ProjectileDisplayName(weapon.Name), TeamId = ship.TeamId,
                OwnerShipId = ship.InstanceId, TargetShipId = target.InstanceId,
                Position = ship.Position, Facing = ship.Facing,
                Speed = weapon.ProjectileSpeed, GunnerBonus = (crewBonus ?? CombatResolver.ComputeGunnerBonus(ship)) + (snapShot ? 2 : 0), Weapon = weapon
            };
            _pendingLaunches.Add((projectile, check));
            return check with { Description = $"{check.Description} Missile acquired; it moves at the end of Gunnery." };
        }

        _pendingHits.Add((ship, target, weapon, check, target.QuadrantOf(ship.Position)));
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
    public MoveValidationResult StartStunt(string stunt)
    {
        if (HasPendingFreeAttack) return MoveValidationResult.Fail("Resolve the free attack first.");
        var ship = _state.CurrentActor;
        if (ship is null || _state.Phase != CombatPhase.Helm) return MoveValidationResult.Fail("No ship to move.");
        if (PendingStunt is not null) return MoveValidationResult.Fail("Finish the selected stunt first.");
        if (stunt is not ("Back Off" or "Flip and Burn")) return MoveValidationResult.Fail("Unknown stunt.");
        ship.InitializeCrewRoles();
        int pilot = ship.CrewRoles.IndexOf("Pilot");
        if (pilot < 0 || ship.CrewActionsUsed.Contains(pilot)) return MoveValidationResult.Fail("The stunt requires an available pilot.");
        if (ship.Systems.GetEffective(ShipSystem.Engines) is SystemCondition.Malfunctioning or SystemCondition.Wrecked)
            return MoveValidationResult.Fail("Stunts are push actions and require working engines.");
        if (stunt == "Back Off")
        {
            int aft = ((int)ship.Facing + 3) % 6;
            bool hasDestination = Enumerable.Range(1, ship.Speed / 2).Any(distance =>
                !_state.Ships.Any(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed && s.Position == ship.Position.Step(aft, distance)));
            if (!hasDestination) return MoveValidationResult.Fail("Back Off requires an unoccupied destination at least one hex aft.");
        }

        int penalty = ship.Systems.GetActionPenalty(ShipSystem.Engines);
        int roll = _dice.RollD20();
        int bonus = ship.CrewBonus(pilot, "piloting", "Pilot") + ship.SpeedPilotingAdjustment + (penalty == int.MinValue ? 0 : penalty) + ship.Systems.PowerCoreGlobalPenalty;
        int dc = (int)Math.Floor((stunt == "Back Off" ? 10 : 15) + 1.5 * ship.Definition.Tier);
        int total = roll + bonus;
        bool failedByFive = penalty == int.MinValue || total <= dc - 5;
        bool failed = penalty == int.MinValue || total < dc;
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
            $"{ShipLogName(ship)} attempts {stunt}: {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(stunt == "Back Off" && failedByFive ? "FAIL by 5 or more; AC and TL -4" : failed ? "FAIL" : "SUCCESS")}."));
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
            LastEvents.Add(new CombatEvent("Engineering", ship.InstanceId, $"{ShipLogName(ship)} attempts {action.Action}: power core wrecked — automatic failure."));
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
            $"{ShipLogName(ship)} attempts {action.Action} ({action.Target}): {CombatResolver.FormatCheck(roll, bonus, total)} vs DC {dc} — {(success ? "SUCCESS" : "FAIL")}.") );
        return null;
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
    private Dictionary<(Hex Pos, Facing Facing), List<PathStep>> _ComputeReachable(ShipInstance? requestedShip = null, Hex? allowOccupiedTarget = null)
    {
        var ship = requestedShip ?? _state.CurrentActor;
        if (ship is null || (requestedShip is null && _state.Phase != CombatPhase.Helm)) return new();

        ship.InitializeCrewRoles();
        bool glide = requestedShip is null && ship.Definition.Crew.Count > 0 && !ship.CrewRoles.Contains("Pilot");
        if (glide && ship.AvailableMinorCrew() < 0) return new();
        if (requestedShip is null && !glide && ship.Systems.GetEffective(ShipSystem.Engines) == SystemCondition.Wrecked) return new();
        int speed = glide ? ship.Speed / 2 : ship.Speed;
        int minBetweenTurns = ship.Definition.DistanceBetweenTurns + (glide ? 2 : 0);
        var occupied = new HashSet<Hex>(_state.Ships
            .Where(s => s.InstanceId != ship.InstanceId && !s.IsDestroyed)
            .Select(s => s.Position));

        // BFS state is (pos, facing, sinceTurn, consecutiveTurns); remaining movement rides along.
        // maxConsecutiveTurns: perfect maneuverability allows 2 turns at a single point; others allow 1.
        int maxConsecutiveTurns = minBetweenTurns == 0 ? 2 : 1;

        // Keep remaining speed in the state: a safer route may be longer but
        // still leave enough movement to reach the requested destination.
        var enemies = requestedShip is null
            ? _state.Ships.Where(s => s.TeamId != ship.TeamId && !s.IsDestroyed).Select(s => s.Position).ToHashSet()
            : new HashSet<Hex>();
        var seed = (Pos: ship.Position, Facing: ship.Facing, Remaining: speed, SinceTurn: 0, Turns: 0);
        var queue = new PriorityQueue<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns), (int Exposures, int Steps)>();
        var costs = new Dictionary<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns), (int Exposures, int Steps)> { [seed] = (0, 0) };
        var parents = new Dictionary<(Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns), (Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns)?> { [seed] = null };
        queue.Enqueue(seed, (0, 0));
        while (queue.TryDequeue(out var current, out var cost))
        {
            if (cost != costs[current]) continue;
            void Visit((Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns) next, bool moved)
            {
                var nextCost = (cost.Exposures + (moved && enemies.Contains(next.Pos) ? 1 : 0), cost.Steps + 1);
                if (costs.TryGetValue(next, out var previous) && previous.CompareTo(nextCost) <= 0) return;
                costs[next] = nextCost;
                parents[next] = current;
                queue.Enqueue(next, nextCost);
            }
            if (current.Remaining > 0)
                Visit((current.Pos.Neighbor((int)current.Facing), current.Facing, current.Remaining - 1,
                    Math.Min(minBetweenTurns, current.SinceTurn + 1), 0), true);
            if (current.Remaining < speed && current.Turns < maxConsecutiveTurns && (minBetweenTurns == 0 || current.SinceTurn >= minBetweenTurns))
                foreach (int delta in new[] { 1, -1 })
                    Visit((current.Pos, (Facing)(((int)current.Facing + delta + 6) % 6), current.Remaining, 0, current.Turns + 1), false);
        }

        var result = new Dictionary<(Hex Pos, Facing Facing), List<PathStep>>();
        var bestCosts = new Dictionary<(Hex Pos, Facing Facing), (int Exposures, int Steps)>();
        foreach (var entry in costs)
        {
            var stateKey = entry.Key;
            if (occupied.Contains(stateKey.Pos) && stateKey.Pos != allowOccupiedTarget) continue;
            var destination = (stateKey.Pos, stateKey.Facing);
            if (bestCosts.TryGetValue(destination, out var best) && best.CompareTo(entry.Value) <= 0) continue;
            var path = new List<PathStep>();
            (Hex Pos, Facing Facing, int Remaining, int SinceTurn, int Turns)? cursor = stateKey;
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
        => _stuntMoves ?? _ComputeReachable().Select(kv => new MoveOption(kv.Key.Pos, kv.Key.Facing, kv.Value)).ToList();

    /// <summary>Returns the next turn-limited route a tracking projectile can make toward its target.</summary>
    public List<PathStep> GetProjectilePreviewPath(TrackingProjectile projectile)
    {
        var target = _state.GetShip(projectile.TargetShipId);
        if (target is null) return [];
        var definition = new ShipDefinition
        {
            Id = projectile.Id, Name = projectile.Name, Speed = projectile.Speed,
            Maneuverability = Maneuverability.Perfect, HullPoints = 1,
            Shields = new QuadrantShields()
        };
        var projectileShip = definition.Instantiate(projectile.Id, projectile.Position, projectile.Facing, projectile.TeamId);
        var routes = _ComputeReachable(projectileShip, target.Position);
        var route = routes
            .Where(kv => kv.Value.Any(step => step.Position != projectile.Position))
            .OrderBy(kv => kv.Key.Pos.DistanceTo(target.Position))
            .ThenByDescending(kv => kv.Value.Zip(kv.Value.Skip(1)).Count(pair => pair.First.Position != pair.Second.Position))
            .ThenBy(kv => kv.Value.Count)
            .Select(kv => kv.Value)
            .FirstOrDefault();
        return route is null ? [] : route.Skip(1).ToList();
    }

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
            PendingStunt = null;
            _stuntMoves = null;
            LastEvents.Add(new CombatEvent("Helm", ship.InstanceId, $"{ShipLogName(ship)} begins {stunt}."));
            BeginCommittedMove(option);
            return MoveValidationResult.Ok(position, facing);
        }
        if (!_ComputeReachable().TryGetValue((position, facing), out var path))
            return MoveValidationResult.Fail("That hex/facing is not reachable with this ship's movement.");

        ship.InitializeCrewRoles();
        if (ship.Definition.Crew.Count > 0)
        {
            int pilot = ship.CrewRoles.IndexOf("Pilot");
            if (pilot >= 0) ship.CrewActionsUsed.Add(pilot);
            else
            {
                int crew = ship.AvailableMinorCrew();
                if (crew < 0) return MoveValidationResult.Fail("No crew member can glide.");
                ship.CrewMinorActionsUsed.Add(crew);
                ship.RoundAcTlModifier += ship.Definition.Crew[crew].Ranks;
            }
        }

        BeginCommittedMove(new MoveOption(position, facing, path));
        return MoveValidationResult.Ok(position, facing);
    }

    /// <summary>A weapon that the current actor can fire at a specific target.</summary>
    public sealed record TargetOption(string WeaponName, string TargetShipId, int Range, int HitChancePercent, int WeaponIndex, int RangePenalty, FiringArc Arc, bool ArcChoiceRequired, List<FiringArc> ArcChoices);

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
            if (!ship.HasWeaponUses(wi)) continue;
            var weapon = ship.Weapons[wi];
            if (weapon.Type == WeaponType.Tracking && weapon.ProjectileSpeed <= 0) continue;
            foreach (var target in _state.ActiveShips())
            {
                if (target.TeamId == ship.TeamId) continue; // no friendly fire in v1
                int range = ship.Position.DistanceTo(target.Position);
                double angle = ship.AngleFromFacing(target.Position);
                if (!weapon.CanTarget(range, angle)) continue;
                var choices = arcChoices.GetValueOrDefault(target.InstanceId) ?? [];
                if (choices.Count > 0 && _targetArcChoices.TryGetValue((ship.InstanceId, target.InstanceId), out var committedArc) && weapon.Arc != FiringArc.AllArcs && weapon.Arc != committedArc)
                    continue;

                int defenseValue = weapon.Type switch
                {
                    WeaponType.Tracking => target.CurrentTL,
                    _ => target.CurrentAC
                };
                int rangePenalty = weapon.RangePenalty(range);
                int effectiveBonus = bonus == int.MinValue ? bonus : bonus + rangePenalty;
                int hitChance = CombatResolver.HitChancePercent(effectiveBonus, defenseValue);
                bool choiceRequired = choices.Count > 1 && !_targetArcChoices.ContainsKey((ship.InstanceId, target.InstanceId));
                options.Add(new TargetOption(weapon.Name, target.InstanceId, range, hitChance, wi, rangePenalty, weapon.Arc, choiceRequired, choices));
            }
        }
        return options;
    }

    // ────────────────────────────────────────────────
    //  Internal helpers
    // ────────────────────────────────────────────────

    private string ShipLogName(ShipInstance ship)
        => $"{_state.DisplayName(ship)} (Team {ship.TeamId + 1})";

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

    private void ResolveProjectile(TrackingProjectile projectile, int roll, int checkBonus, int defense)
    {
        var target = _state.GetShip(projectile.TargetShipId);
        if (target is null)
        {
            _state.Projectiles.Remove(projectile);
            return;
        }
        int total = roll + checkBonus;
        if (roll == 1 || (roll != 20 && total < defense))
        {
            string ownerName = _state.GetShip(projectile.OwnerShipId) is { } owner ? ShipLogName(owner) : $"Team {projectile.TeamId + 1}";
            LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId,
                $"{ownerName}'s {ProjectileLogName(projectile)} tracking check {CombatResolver.FormatCheck(roll, checkBonus, total)} vs TL {defense} — MISS, projectile lost."));
            _state.Projectiles.Remove(projectile);
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
            bool wasDisabled = target.IsDisabled, wasDestroyed = target.IsDestroyed;
            var result = CombatResolver.ResolveProjectileImpact(projectile, target, _dice, roll, checkBonus, defense, impactFrom,
                _state.GetShip(projectile.OwnerShipId)?.DivertWeapons == true);
            string ownerName = _state.GetShip(projectile.OwnerShipId) is { } owner ? ShipLogName(owner) : $"Team {projectile.TeamId + 1}";
            LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId,
                $"{ownerName}'s {ProjectileLogName(projectile)} hits {ShipLogName(target)}: {result.Description}"));
            LogShipStatusChange(target, wasDisabled, wasDestroyed, "Gunnery");
            _state.Projectiles.Remove(projectile);
        }
        else
        {
            LastEvents.Add(new CombatEvent("Gunnery", projectile.OwnerShipId,
                $"{ProjectileLogName(projectile)} advances {moved} hexes toward {ShipLogName(target)}. Gunnery: {CombatResolver.FormatCheck(roll, checkBonus, total)} vs TL {defense}."));
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
