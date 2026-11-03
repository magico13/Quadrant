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

    public GameController(SessionManager sessions) => _sessions = sessions;

    /// <summary>Creates a new hotseat game with the default 4-ship setup.</summary>
    [HttpPost("start")]
    public IActionResult Start()
    {
        var ships = ShipCatalog.CreateDefaultShips();
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

        // Phase-specific helper data for the client UI.
        object? options = null;
        if (state.Phase == CombatPhase.Helm && state.CurrentActor is not null)
            options = new { type = "moves", items = engine.GetValidMoves().Select(m => new { position = new { q = m.Position.Q, r = m.Position.R }, finalFacing = (int)m.FinalFacing }) };
        else if (state.Phase == CombatPhase.Gunnery && state.CurrentActor is not null)
            options = new { type = "targets", items = engine.GetValidTargets() };

        var result = Ok(new
        {
            roomCode,
            phase = state.Phase,
            round = state.Round,
            actionOrder = state.ActionOrder,
            currentActorIndex = state.CurrentActorIndex,
            currentActorId = state.CurrentActor?.InstanceId,
            winner = state.Winner,
            isComplete = state.IsComplete,
            ships = state.Ships.Select(s => new
            {
                id = s.InstanceId,
                name = s.Name,
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
                weapons = s.Weapons.Select(w => new { name = w.Name, arc = w.Arc, minRange = w.MinRange, maxRange = w.MaxRange, damage = w.Damage.ToString(), type = w.Type }),
                damagedSystems = s.DamagedSystems
            }),
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

    /// <summary>Engineering: repair hull on a ship (amount 0-3 typical).</summary>
    [HttpPost("{roomCode}/engineering")]
    public IActionResult Engineering(string roomCode, [FromBody] EngineeringRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        session.Engine.ApplyEngineering(req.ShipId, new RepairAction(req.Amount));
        return Ok(new { ok = true });
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
        return Ok(new { ok = true });
    }

    /// <summary>Gunnery: fire the current actor's weapons at chosen targets.</summary>
    [HttpPost("{roomCode}/fire")]
    public IActionResult Fire(string roomCode, [FromBody] FireRequest req)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        var actions = req.Shots.Select(s => new FireAction(s.WeaponName, s.TargetShipId)).ToList();
        var results = session.Engine.ApplyGunneryTurn(actions);
        return Ok(new { ok = true, results = results.Select(r => r.Description).ToList() });
    }

    /// <summary>Gunnery: skip the current actor's firing.</summary>
    [HttpPost("{roomCode}/skip-fire")]
    public IActionResult SkipFire(string roomCode)
    {
        var session = _sessions.Get(roomCode);
        if (session is null) return NotFound(new { error = "Game not found." });

        session.Engine.SkipFire();
        return Ok(new { ok = true });
    }
}

public sealed record EngineeringRequest(string ShipId, int Amount);
public sealed record MoveRequest(int Q, int R, int Facing);
public sealed record FireRequest(List<Shot> Shots);
public sealed record Shot(string WeaponName, string TargetShipId);
