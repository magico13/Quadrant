using Microsoft.AspNetCore.Mvc;
using Quadrant.RulesEngine;
using Quadrant.Server.Sessions;

namespace Quadrant.Server.Controllers;

/// <summary>
/// REST endpoints for a single hotseat game. The client polls /state after each
/// action to get the updated board. In a future version this is replaced by
/// SignalR push, but REST keeps the first playable loop simple and testable.
/// </summary>
[ApiController]
[Route("api/game")]
public class GameController : ControllerBase
{
    private readonly SessionManager _sessions;
    private readonly ShipCatalogService _catalog;

    public GameController(SessionManager sessions, ShipCatalogService catalog)
    {
        _sessions = sessions;
        _catalog = catalog;
    }

    /// <summary>Returns the ship catalog for the setup UI.</summary>
    [HttpGet("catalog")]
    public IActionResult GetCatalog()
    {
        return Ok(_catalog.All.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            family = s.Family,
            tier = s.Tier,
            speed = s.Speed,
            maneuverability = s.Maneuverability,
            hullPoints = s.HullPoints,
            ac = s.BaseAC,
            tl = s.BaseTL,
            shields = new { forward = s.Shields.Forward, starboard = s.Shields.Starboard, aft = s.Shields.Aft, port = s.Shields.Port },
            weapons = s.Weapons.Select(w => new { name = w.Name, arc = w.Arc, damage = w.Damage.ToString(), type = w.Type, speed = w.ProjectileSpeed, limitedFireUses = w.LimitedFireUses })
        }));
    }

    /// <summary>Creates a new hotseat game. Accepts optional ship selections; falls back to defaults.</summary>
    [HttpPost("start")]
    public IActionResult Start([FromBody] StartGameRequest? req)
    {
        List<ShipInstance> ships;
        try
        {
            ships = req?.Selections is { Count: > 0 }
                ? _catalog.CreateShips(req.Selections)
                : _catalog.CreateDefaultShips();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var session = _sessions.Create(Guid.NewGuid().ToString(), ships);
        return Ok(new { roomCode = session.RoomCode });
    }

    /// <summary>Returns the full current game state (ships, phase, round, order).</summary>
    [HttpGet("state/{roomCode}")]
    public IActionResult GetState(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        var state = session.State;
        var engine = session.Engine;
        foreach (var ship in state.Ships) { ship.InitializeWeaponUses(); ship.InitializeCrewRoles(); }

        // Phase-specific helper data for the client UI.
        object? options = null;
        if (state.Phase == CombatPhase.Helm && state.CurrentActor is not null && !engine.HasPendingFreeAttack)
            options = new { type = "moves", items = engine.GetValidMoves().Select(m => new { position = new { q = m.Position.Q, r = m.Position.R }, finalFacing = (int)m.FinalFacing, path = m.Path.Select(h => new { q = h.Position.Q, r = h.Position.R }).ToList() }) };
        else if (state.Phase == CombatPhase.Gunnery && state.CurrentActor is not null)
            options = new { type = "targets", items = engine.GetValidTargets() };

        var projectileViews = engine.GetProjectilePreviews().Select(preview =>
        {
            var p = preview.Projectile;
            var path = preview.Path;
            return new
            {
                id = p.Id, name = p.Name, teamId = p.TeamId, q = p.Position.Q, r = p.Position.R,
                facing = (int)p.Facing, targetShipId = p.TargetShipId,
                previewFacing = (int)(path.LastOrDefault()?.Facing ?? p.Facing),
                previewPath = path.Select(step => new { q = step.Position.Q, r = step.Position.R }).ToList()
            };
        }).ToList();

        var result = Ok(new
        {
            roomCode,
            phase = state.Phase,
            round = state.Round,
            actionOrder = state.ActionOrder,
            currentActorIndex = state.CurrentActorIndex,
            currentActorId = state.CurrentActor?.InstanceId,
            pendingStunt = engine.PendingStunt,
            freeAttack = engine.FreeAttackShipId is { } defenderId && state.GetShip(defenderId) is { } defender
                ? new { defenderId, targetId = state.CurrentActor?.InstanceId,
                    weapons = engine.GetFreeAttackWeapons().Select(i => new { index = i, name = defender.Weapons[i].Name, arc = defender.Weapons[i].Arc }),
                    gunners = defender.CrewRoles.Select((role, i) => (role, i)).Where(x => x.role == "Gunner")
                        .Select(x => new { index = x.i, bonus = defender.CrewBonus(x.i, "gunnery", "Gunner") }) }
                : null,
            firedWeaponIndices = engine.FiredWeaponIndices,
            winner = state.Winner,
            isComplete = state.IsComplete,
            ships = state.Ships.Select(s => new
            {
                id = s.InstanceId,
                name = state.DisplayName(s),
                tier = s.Definition.Tier,
                computersModifier = s.Definition.ComputersModifier,
                hasSensors = s.Definition.HasSensors,
                sensorRangeHexes = s.Definition.SensorRangeHexes,
                countermeasures = s.Definition.Countermeasures,
                targetedSystems = engine.GetTargetedSystems(s.InstanceId).Select(t => new { targetShipId = t.TargetShipId, system = t.System }),
                size = s.Definition.Size,
                teamId = s.TeamId,
                q = s.Position.Q,
                r = s.Position.R,
                facing = (int)s.Facing,
                hull = s.HullPoints,
                maxHull = s.MaxHullPoints,
                speed = s.Speed,
                maneuverability = s.Maneuverability,
                ac = s.CurrentAC,
                tl = s.CurrentTL,
                isDestroyed = s.IsDestroyed,
                isDisabled = s.IsDisabled,
                shields = new { forward = s.Shields.Forward, starboard = s.Shields.Starboard, aft = s.Shields.Aft, port = s.Shields.Port },
                maxShield = s.Definition.Shields.Forward, // client uses this for arc opacity scaling
                shieldCapacity = s.Definition.Shields.Forward + s.Definition.Shields.Starboard + s.Definition.Shields.Aft + s.Definition.Shields.Port,
                shieldDivertMax = Math.Max(0, Math.Min(s.Definition.PowerCorePcu / 20,
                    s.Definition.Shields.Forward + s.Definition.Shields.Starboard + s.Definition.Shields.Aft + s.Definition.Shields.Port -
                    s.Shields.Forward - s.Shields.Starboard - s.Shields.Aft - s.Shields.Port)),
                engineeringActionsUsed = s.EngineeringActionsUsed,
                diverted = new { engines = s.DivertEngines, weapons = s.DivertWeapons, science = s.DivertScience },
                heldSystem = s.Systems.HeldSystem,
                systems = Enum.GetValues<ShipSystem>().Select(system => new { system, condition = s.Systems.Get(system), effectiveCondition = s.Systems.GetEffective(system) }),
                crew = s.Definition.Crew.Select((c, i) =>
                {
                    string role = s.CrewRoles[i];
                    string? skill = role switch
                    {
                        "Pilot" => "piloting",
                        "Gunner" => "gunnery",
                        "Engineer" => "engineering",
                        "Science Officer" => "computers",
                        _ => null
                    };
                    return new { primaryRole = c.Role, role, ranks = c.Ranks, skills = c.Skills, actionUsed = s.CrewActionsUsed.Contains(i),
                        roleBonus = skill is null ? (int?)null : s.CrewBonus(i, skill, role),
                        scienceBonus = role == "Science Officer" ? engine.GetScienceOfficerCheckBonus(s, i) : (int?)null };
                }),
                helmAction = s.Definition.Crew.Count == 0 || s.CrewRoles.Contains("Pilot") ? "Pilot" : s.AvailableMinorCrew() >= 0 ? "Glide" : "None",
                gunneryAction = s.Definition.Crew.Count == 0 || s.AvailableGunner() >= 0 ? "Gunner" : !s.GunnerActionTaken && s.AvailableMinorCrew() >= 0 ? "Snap Shot" : "None",
                crewShotsRemaining = s.RemainingGunneryActions(),
                weapons = s.Weapons.Select((w, i) =>
                {
                    int linkedCount = LinkedMountCount(s.Weapons, w);
                    return new
                    {
                        name = w.Name,
                        displayName = linkedCount > 1 ? $"Linked {w.Name}" : w.Name,
                        linkedGroup = w.LinkedGroup,
                        linkedCount,
                        arc = w.Arc,
                        minRange = w.MinRange,
                        rangeIncrement = w.RangeIncrement,
                        maxFireRange = w.MaxFireRange,
                        damage = CombinedDamage(w.Damage, linkedCount).ToString(),
                        type = w.Type,
                        projectileSpeed = w.ProjectileSpeed,
                        limitedFireUses = w.LimitedFireUses,
                        usesRemaining = s.WeaponUsesRemaining[i]
                    };
                }),
                damagedSystems = s.DamagedSystems
            }),
            projectiles = projectileViews,
            options,
            // Return and clear pending events so the client only sees new ones.
            log = engine.LastEvents.Select(e => new { phase = e.Phase, shipId = e.ShipId, message = e.Message }).ToList()
        });

        engine.LastEvents.Clear();
        return result;
    }

    /// <summary>Starts combat (Setup → Engineering, round 1).</summary>
    [HttpPost("{roomCode}/start-combat")]
    public IActionResult StartCombat(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        bool ok = session.Engine.StartCombat();
        return ok ? Ok(new { ok }) : BadRequest(new { error = "Cannot start combat." });
    }

    /// <summary>Commit crew roles for all active ships at the start of the round.</summary>
    [HttpPost("{roomCode}/roles")]
    public IActionResult AssignRoles(string roomCode, [FromBody] RoleAssignmentsRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });
        if (req.Ships is null || req.Ships.Any(s => s.Roles is null) || req.Ships.Select(s => s.ShipId).Distinct().Count() != req.Ships.Count)
            return BadRequest(new { error = "Invalid role assignments." });
        var assignments = req.Ships.ToDictionary(s => s.ShipId, s => (IReadOnlyList<string>)s.Roles);
        var error = session.Engine.AssignRoles(assignments);
        return error is null ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    /// <summary>Advances out of the Engineering phase into Helm.</summary>
    [HttpPost("{roomCode}/engineering")]
    public IActionResult Engineering(string roomCode, [FromBody] EngineerAction action)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });
        var error = session.Engine.ApplyEngineering(action);
        return error is null ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    /// <summary>Helm: attempt a science officer action.</summary>
    [HttpPost("{roomCode}/science")]
    public IActionResult Science(string roomCode, [FromBody] ScienceAction action)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });
        var error = session.Engine.ApplyScience(action);
        return error is null ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    /// <summary>Advances out of the Engineering phase into Helm.</summary>
    [HttpPost("{roomCode}/end-engineering")]
    public IActionResult EndEngineering(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        session.Engine.SkipEngineering();
        return Ok(new { ok = true });
    }

    /// <summary>Helm: move the current actor to a specific hex/facing.</summary>
    [HttpPost("{roomCode}/move")]
    public IActionResult Move(string roomCode, [FromBody] MoveRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        var result = session.Engine.TryMoveTo(new Hex(req.Q, req.R), (Facing)req.Facing);
        if (result.IsValid) AdvanceUnavailableGunneryTurns(session.Engine);
        return result.IsValid
            ? Ok(new { ok = true })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Helm: skip the current actor's movement.</summary>
    [HttpPost("{roomCode}/skip-move")]
    public IActionResult SkipMove(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        session.Engine.SkipMove();
        AdvanceUnavailableGunneryTurns(session.Engine);
        return Ok(new { ok = true });
    }

    /// <summary>Helm: Turn In Place stunt — ship stays put but faces a new direction.</summary>
    [HttpPost("{roomCode}/turn-in-place")]
    public IActionResult TurnInPlace(string roomCode, [FromBody] TurnInPlaceRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        var result = session.Engine.TryTurnInPlace((Facing)req.Facing);
        if (result.IsValid) AdvanceUnavailableGunneryTurns(session.Engine);
        return result.IsValid
            ? Ok(new { ok = true })
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("{roomCode}/stunt")]
    public IActionResult StartStunt(string roomCode, [FromBody] StuntRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });
        var result = session.Engine.StartStunt(req.Stunt);
        return result.IsValid ? Ok(new { ok = true }) : BadRequest(new { error = result.Error });
    }

    [HttpPost("{roomCode}/free-attack")]
    public IActionResult FreeAttack(string roomCode, [FromBody] FreeAttackRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });
        var error = session.Engine.ResolveFreeAttack(req.WeaponIndex, req.GunnerIndex);
        if (error is not null) return BadRequest(new { error });
        AdvanceUnavailableGunneryTurns(session.Engine);
        return Ok(new { ok = true });
    }

    /// <summary>Gunnery: resolve one weapon's attack roll now; damage waits until the phase ends.</summary>
    [HttpPost("{roomCode}/fire")]
    public IActionResult Fire(string roomCode, [FromBody] FireShotRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        var result = session.Engine.ApplyFire(new FireAction(req.WeaponIndex, req.TargetShipId), req.Arc is null ? null : Enum.Parse<FiringArc>(req.Arc, true));
        if (result is not null)
            AdvanceUnavailableGunneryTurns(session.Engine);
        return Ok(new { ok = result is not null, result = result?.Description });
    }

    /// <summary>Gunnery: finish the current ship's firing choices.</summary>
    [HttpPost("{roomCode}/end-fire")]
    public IActionResult EndFire(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });
        session.Engine.EndGunneryTurn();
        AdvanceUnavailableGunneryTurns(session.Engine);
        return Ok(new { ok = true });
    }

    /// <summary>Gunnery: skip the current actor's firing.</summary>
    [HttpPost("{roomCode}/skip-fire")]
    public IActionResult SkipFire(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        session.Engine.SkipFire();
        AdvanceUnavailableGunneryTurns(session.Engine);
        return Ok(new { ok = true });
    }

    private static void AdvanceUnavailableGunneryTurns(CombatEngine engine)
    {
        while (engine.AutoEndGunneryTurnIfNoActions()) { }
    }

    private static int LinkedMountCount(IReadOnlyList<Weapon> weapons, Weapon weapon)
    {
        if (weapon.Type != WeaponType.DirectFire || string.IsNullOrWhiteSpace(weapon.LinkedGroup)) return 1;
        int count = weapons.Count(candidate => candidate.Type == WeaponType.DirectFire &&
            candidate.LinkedGroup == weapon.LinkedGroup && candidate.Arc == weapon.Arc &&
            candidate.Name == weapon.Name && candidate.Damage == weapon.Damage &&
            candidate.MinRange == weapon.MinRange && candidate.RangeIncrement == weapon.RangeIncrement &&
            candidate.LimitedFireUses == weapon.LimitedFireUses);
        return count > 1 ? count : 1;
    }

    private static DamageExpression CombinedDamage(DamageExpression damage, int mountCount)
        => damage with
        {
            DiceCount = damage.DiceCount * mountCount,
            FlatBonus = damage.FlatBonus * mountCount
        };
}

public sealed record StartGameRequest(List<ShipSelection>? Selections);
public sealed record MoveRequest(int Q, int R, int Facing);
public sealed record StuntRequest(string Stunt);
public sealed record FreeAttackRequest(int? WeaponIndex, int? GunnerIndex);
public sealed record TurnInPlaceRequest(int Facing);
public sealed record FireShotRequest(int WeaponIndex, string TargetShipId, string? Arc = null);
public sealed record RoleAssignmentsRequest(List<ShipRolesRequest> Ships);
public sealed record ShipRolesRequest(string ShipId, List<string> Roles);
