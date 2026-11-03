# Quadrant — Starfinder Starship Combat Game

## TODO — Next Up
- [ ] **Movement confirmation step (client).** Movement should require a separate confirm action so you can preview/undo your move until you confirm — lets you experiment with facings and different destinations without locking them in, and guards against misclicks. Currently `doMove()` POSTs to `/{room}/move` immediately on hex/facing selection.
- [ ] **Range-based attack penalty.** Verify whether Starfinder applies a penalty to attacks based on range (longer shots) and whether the engine accounts for it. Check the hit-chance calc in `CombatEngine.GetValidTargets` / gunnery targeting against the rules. (The Rules Reference below already lists "− range penalty" in the Gunnery Check line, so this is likely intended but may not be implemented.)

## Concept
A browser-playable Starfinder starship combat game for friends. Hex-grid tactical ship combat with the three-phase round loop (Engineering → Helm → Gunnery), AC/TL checks, quadrant shields, hull damage, and critical system failures.

## Tech Stack
- **Backend:** C# / ASP.NET Core + SignalR (authoritative server)
- **Rules engine:** Pure C# library — no I/O, fully unit-testable
- **Client:** Vanilla JS + HTML5 Canvas (hex grid rendering, input collection)
- **Ship data:** Server-persisted (SQLite), editable via client form; pre-populated with a few ships

## Architecture

```
Browser Client (JS/Canvas)
    │  SignalR (WebSocket)
    ▼
Game Server (ASP.NET Core + SignalR)
├── Session Manager (6-char room codes, player connections)
├── Game Orchestrator (one per session, phase state machine)
├── Rules Engine (pure lib: movement, attacks, damage, criticals)
└── Ship Repository (persistence, CRUD)
```

### Key Principles
1. **Deterministic engine** — same inputs + dice = same output. Enables testing and replay.
2. **Server-authoritative** — client sends intents, server validates and resolves.
3. **Data-driven ships** — DB records, not hardcoded. Easy to add new ships.
4. **Phase state machine** — explicit states, no hidden logic. Makes multiplayer turn-taking trivial later.

## Session Management
- Server hosts many concurrent game sessions
- 6-char room codes (consonant-heavy alphabet: `ABCDEFGHJKMNPQRSTUVWXYZ23456789`)
- Create → pick ships → place on grid (manual or random 3d6+5) → start combat
- Rematch: same ships, new placement. New Game: reselect ships.

## v1 Scope

### In
| Feature | Details |
|---------|---------|
| Hex grid | 15×15 pointy-top, configurable size |
| Ship data | Server-persisted, editable via form, pre-populated |
| Helm phase | Piloting check (server rolls), movement with turns respecting speed + maneuverability |
| Gunnery phase | Range/arc checks, gunnery check vs AC, damage to shields → hull |
| Engineering phase | Placeholder (skip or simple repair) |
| Damage | Quadrant shields first, overflow to hull, damage threshold |
| Criticals | d% table, system conditions (glitching/malfunctioning/wrecked), penalties on checks |
| Win/lose | 0 hull = disabled, >2× hull damage = destroyed |
| Hotseat | Single screen, alternating control, both players see all rolls |

### Out (Deferred)
- Stunts (Back Off, Barrel Roll, Evade, Flip and Burn, Flyby, Slide, Turn in Place)
- Captain / Science Officer actions
- Tracking weapon projectiles (missiles)
- Crew damage
- Obstacles (asteroids, space stations)
- Full crew ability system
- NPC/AI ships (architecture supports it via controller abstraction)
- Multiplayer over network (hotseat first)

## AI Architecture (Future)
Ship controller is a field: `HumanController` or `AiController`. Orchestrator routes: human → wait for input, AI → call strategy. LLM piping possible by serializing state to JSON and prompting. Not v1.

## Rules Reference (Starfinder CRB pg. 316–326)
- **Roles:** Captain, Engineer, Gunner, Pilot, Science Officer
- **Phases:** Engineering → Helm → Gunnery (repeat until one side destroyed/flees)
- **Movement:** Speed in hexes, turns = 60° each, gated by maneuverability distance-between-turns
- **Gunnery Check:** 1d20 + BAB/Piloting + Dex + bonuses − range penalty vs AC (direct) or TL (tracking)
- **AC:** 10 + pilot Piloting ranks + armor bonus + size modifier + stunt modifiers
- **TL:** 10 + pilot Piloting ranks + countermeasures + size modifier − armor penalty + stunt modifiers
- **Damage:** Shields (per quadrant: Forward 60°, Starboard 120°, Aft 60°, Port 120°) → Hull. Damage Threshold absorbs small hits.
- **Criticals:** Natural 20 (or 19–20 if targeted) OR cumulative hull damage crosses Critical Threshold multiple. Roll d% for system. Conditions: Glitching (−2), Malfunctioning (−4, no push), Wrecked (auto-fail).
- **Speed modifiers to Piloting:** ≤4: +2, 6: +1, 8–10: 0, 12: −1, ≥14: −2
- **Maneuverability:** Clumsy(4,−2), Poor(3,−1), Average(2,0), Good(1,+1), Perfect(0,+2)

## Project Structure (Planned)
```
Quadrant.sln
├── src/
│   ├── Quadrant.RulesEngine/     # Pure domain logic, no dependencies
│   │   ├── Models/               # Ship, Hex, Weapon, Phase, etc.
│   │   ├── Movement/             # Path validation, turn rules
│   │   ├── Combat/               # Attack resolution, damage, criticals
│   │   └── Phases/               # Phase state machine
│   ├── Quadrant.Server/          # ASP.NET Core + SignalR
│   │   ├── Hubs/                 # GameHub (SignalR)
│   │   ├── Sessions/             # Session manager, orchestrator
│   │   ├── Data/                 # EF Core / SQLite for ships
│   │   └── wwwroot/              # Static client files
│   │       ├── index.html
│   │       ├── js/               # Game client JS
│   │       └── css/
│   └── Quadrant.Client.Tests/    # (optional, or test in RulesEngine)
├── tests/
│   └── Quadrant.RulesEngine.Tests/
└── PLAN.md
```

## Ship Roster (Current)
Based on published Starfinder Society / Pact Worlds ships. All Tier 2.

| Ship | Size | Speed | Maneuverability | Hull | CT | AC | TL | Shields (F/S/A/P) | Weapons |
|------|------|-------|-----------------|------|----|----|----|--------------------|---------|
| CompEnt AEV | Tiny | 8 | Perfect (0) | 20 | 4 | 15 | 12 | 3/2/3/2 | Mining Laser 2d6 (Fwd, 1-4) |
| Pegasus | Medium | 10 | Good (1) | 55 | 11 | 12 | 12 | 10/10/10/10 | Light Laser 2d4 ×3 (Fwd/Port/Sb, 1-6), Coilgun 4d4 (Turret, 1-8) |
| Gorgon | Large | 4 | Average (2) | 150 | 30 | 10 | 10 | 5/5/5/5 | Heavy Laser 4d8 (Fwd, 1-12) |

Default hotseat: Team 0 = Pegasus + AEV (East), Team 1 = Gorgon (West).
No Damage Threshold on any ship (all "—" in published stats).

## Ship Data Model (Conceptual)
```json
{
  "id": "pegasus",
  "name": "Pegasus",
  "tier": 2,
  "speed": 10,
  "maneuverability": "Good",
  "hullPoints": 55,
  "damageThreshold": 0,
  "criticalThreshold": 11,
  "baseAC": 12,
  "baseTL": 12,
  "shields": { "forward": 10, "starboard": 10, "aft": 10, "port": 10 },
  "weapons": [
    { "name": "Light Laser Cannon", "arc": "Forward", "range": [1, 6], "damage": "2d4", "type": "direct" },
    { "name": "Coilgun", "arc": "AllArcs", "range": [1, 8], "damage": "4d4", "type": "direct" }
  ]
}
```

## Dice Handling
- Server rolls all dice (authoritative)
- In hotseat: both players see all rolls
- Results broadcast as events to clients
