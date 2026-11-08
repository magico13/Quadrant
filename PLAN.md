# Quadrant — Starfinder Starship Combat Game

## Running the Server (Docker)
Server runs in Docker so it can stay up in the background without confusing the agent harness. `wwwroot` is bind-mounted, so UI edits are picked up on browser refresh — no rebuild needed for client changes.

```bash
docker compose up -d --build   # build + start (rebuild only after C# changes)
curl http://localhost:5180/api/health
docker logs -f quadrant-server  # tail logs
docker compose restart server   # restart
docker compose down             # stop
```

Server listens on port **5180**. Ship and weapon definitions are loaded from JSON files at startup. Game sessions are stored in SQLite, with a Docker volume for persistence.

## Current State
- A playable hotseat combat loop is in place, with server-side rules, room codes, and a browser client.
- The ship and weapon catalogs are loaded from JSON, with a catalog API and a UI for selecting each team's fleet.
- Fifteen ship definitions are currently present. Limited Fire and tracking projectiles are implemented; remaining weapon property rules are tracked below.
- Content support targets Starfinder Society safe options. Optional material from other books is out of scope unless explicitly added later.
- Tracking projectiles now use catalog speed, acquire against TL, home each gunnery phase, and impact using normal shields/hull/critical rules.
- Ship editing, per-player access and hidden information, and real-time synchronization remain future work. The architecture and v1 scope below describe targets, not all current implementation.

## TODO — Completed
- [x] **Movement confirmation step (client).** Helm moves preview first: ghost ship + Confirm/Cancel panel. `game.js` (`pendingMove`, `setPendingMove()`, `confirmMove()`) + `hex.js` (`setGhost()`).
- [x] **Turn In Place stunt.** Facing picker on own-ship click during Helm. Clumsy –4, poor –2 AC/TL until next round. `TryTurnInPlace(Facing)` in `CombatEngine.cs`.
- [x] **Back Off and Flip and Burn stunts.** Piloting checks and stunt-specific movement are implemented. Flip and Burn permits movement up to half speed, including on failure; failure prevents rotation.
- [x] **Evade and Slide stunts.** Evade uses normal movement and modifies AC/TL according to its check; Slide moves diagonally on success or forward at up to half speed on failure.
- [x] **Barrel Roll stunt.** Large or smaller ships move up to half speed and swap port/starboard shield coverage and weapon arcs for Gunnery on success. Failure prevents the roll; failure by 5 or more also applies −4 AC/TL until next round. Includes hover description and persisted effects.
- [x] **Flyby stunt.** Activate Flyby, then choose a legal route through exactly one occupied enemy hex. The confirmed route selects the enemy; roll against that enemy's tier when confirming movement. Success avoids one free attack from that enemy and permits one selected firing arc (including turrets) to fire against any chosen enemy quadrant at effective range 1 during the following Gunnery phase. Failure uses normal free attacks and targeting. Friendly-occupied hexes can be crossed freely, but movement cannot end on any occupied hex. Flyby routes cannot enter a second enemy-occupied hex or re-enter the enemy hex. Tracking shots originate adjacent to the selected quadrant to apply the close-range firing rule to projectile travel and point defense. Includes hover text, quadrant selection and persistence.
- [x] **BFS turn rules fix.** Seed `sinceTurn = 0`; perfect ships can turn at any point; consecutive turn cap: perfect = 2, others = 1.
- [x] **Stale highlights on phase change.** `renderGunnery()` clears Helm-phase highlights, path, and ghost.
- [x] **Weapon-arc hex highlighting (client).** Per-increment range bands with opacity fading (3 bands: bright → medium → faint). Click a weapon chip to show its arc + range from current position/facing. `rangeBandHexes()` in `game.js`, per-band opacity in `hex.js`.
- [x] **Range-based attack penalty.** –2 per increment beyond the first, up to 10 increments max fire range. Canonical data from aonsrd.com (Short=5, Medium=10, Long=20). `Weapon.RangePenalty()`, applied in `CombatEngine.GetValidTargets()` and displayed as hit-chance badges + `[N hexes, –2]` in shot list.
- [x] **Auto-targeting.** On Gunnery entry, each weapon auto-selects its closest valid enemy. Click any enemy to retarget (assigns best weapon). Weapon chips for per-weapon control.
- [x] **Ship roster sidebar.** Team-colored ship list with hull readout, active-actor indicator, click-to-show-details + smooth center-on-click. Works in all phases.
- [x] **JSON catalog and fleet selection (uncommitted).** Load ships and weapons from files and choose each team's fleet before starting a hotseat game.
- [x] **Multiple teams and random placement (uncommitted).** Setup offers two to five teams; the server accepts any number of valid teams. Each ship acts independently and combat continues until at most one team remains. Team leaders are spread around the first team: each is placed a rolled 3d6+5 hexes from its leader, favoring comparable separation from the other leaders. Allies start within three hexes of an ally, with one random facing per team and no overlapping ships. Placement for three or more teams is a game-specific extension of the two-side rule.

## TODO — Next Up (Bigger Features)

### Hotseat rules completion (before multiplayer)
- [ ] Audit existing movement, attacks, critical damage and phase timing against the local `wiki_pages/Chapter 9_ Starships - Rules - Archives of Nethys_ Starfinder RPG Database.html` and document deliberate deviations.
- [ ] Complete crew, role-change, action-limit, skill, ship Resolve and minor-action rules; add crew casualties.
- [ ] Implement the remaining crew actions, stunts, system damage effects and engineering repairs described below. Scan follows multiplayer.
- [ ] **Computer ICM bonuses.** Track each computer node's bonus and uses per round; prompt before an eligible starship combat check to apply one available node. Multiple nodes can affect separate checks, but bonuses cannot stack on one check.
- [x] **Linked weapon batteries.** Group identical direct-fire mounts in the same arc; resolve each linked group as one gunnery action and check, with combined damage.
- [ ] Implement remaining weapon property rules in the priority order below.
- [ ] Playtest the completed hotseat combat loop; ship editing remains separate content work.

### Multiplayer
- [ ] **Player login / team assignment.** Keep team identity separate from player identity; initially assign control of teams, with shared teams and control of multiple teams possible later. Room codes remain the way to join.
- [ ] **Information visibility.** Offer a game option for public information or hidden information. In hidden mode, the server filters state per player; enemy details are revealed by combat or scanning. Hotseat remains public.
- [ ] **Real-time sync.** Replace REST polling with SignalR (or SSE) so both players see updates instantly without refresh.

### Alliances (Later)
- [ ] **Alliances between teams.** Allow two teams to become friendly when both agree; either team can end the alliance unilaterally. Decide how allied victories work when this feature is designed.
- Until then, cooperative players can share a team. Ships act independently, and team identity should remain separate from player identity so multiplayer can eventually support shared teams.

### Sensor / Scanning (after multiplayer)
- Ships remain visible on the grid; there is no fog of war or hidden ship position.
- [ ] **Scan action (Science Officer).** After multiplayer and its visibility option, take a Helm-phase action to reveal progressively more information about a visible ship according to the sensor rules. Track discoveries per observing team and target; public mode already exposes this information.

### Crew Actions & Stunts

Already implemented: Fly, all seven core stunts (Back Off, Barrel Roll, Evade, Flip and Burn, Flyby, Slide, Turn In Place), Divert, Hold It Together, Shoot, Balance, Target System, Glide and Snap Shot. Crew roles and action tracking exist, and some push restrictions are enforced; audit these implementations as the remaining rules are added.

- [ ] **Maneuver.** Add the pilot action to reduce distance between turns by 1 on a successful check; keep normal movement on failure.
- [ ] **Captain actions.** Add Demand, Encourage and Taunt, then Orders and Moving Speech once advanced crew prerequisites and Resolve are available. Track check bonuses, timed penalties and per-combat limits. Include available captain actions in the Gunnery auto-end decision so a captain can act after the ship fires.
- [ ] **Engineer actions.** Add Patch, including pooled successful actions and temporary system-condition effects, then Overpower and Quick Fix. Divert already restores shields; system patching is distinct from hull repair.
- [ ] **Gunner actions.** Add Fire at Will, then Broadside and Precise Targeting. Support multiple shots within one crew action and enforce per-weapon firing limits.
- [ ] **Advanced pilot actions.** Add Full Power and Audacious Gambit after movement parameters and Resolve are available.
- [ ] **Science officer actions.** Add Lock On and Improve Countermeasures with their rank and Resolve requirements. Scan is deferred until after multiplayer, as described above.
- [ ] **Ship Resolve and advanced prerequisites.** Each ship has a shared encounter Resolve pool using the NPC ship rule: tier / 5 + 3. Define integer rounding for fractional results when implementing it, persist and reset the pool per encounter, and spend it for eligible actions. Model the character levels and relevant skill ranks needed for advanced actions; allow Orders to grant two different role actions to one crew member.
- [ ] **Push actions.** Apply the chapter's restrictions consistently: a push action cannot use a malfunctioning or wrecked necessary system.
- [ ] **Verification.** For each batch, test eligibility, action consumption, rolls and failure outcomes, damaged systems, effect expiry and session restoration. Test movement interruptions and gunnery interactions for stunts.

### Weapon Special Tags
- [x] **Point.** Point weapons are capped at their first range increment. Tracking missiles are intercepted before damage; each eligible weapon checks once per round, automatically for a lone incoming missile, with a defend/pass choice when multiple missiles threaten the same arc.
- [x] **Broad Arc.** Allow firing into either adjacent arc at a –2 penalty, reflected in hit chance and attack resolution.
- [ ] **Array.** Fire at every target in one arc, with a –4 penalty on each gunnery check; roll damage once and apply it to each target. Allies in the arc are included. The weapon uses two mounts.
- [ ] **Tractor Beam.** After a hit, allow holding, pushing, or pulling an eligible target during Helm; support the target pilot's escape check. A locked beam cannot fire normally.
- [ ] **Irradiate (later).** Defer until crew damage is in scope; radiation damage to crew requires crew damage rules and should bypass shields/hull handling appropriately.
- [x] **Tracking projectiles.** Initial attack uses TL and range penalty; each later gunnery phase checks TL again and moves by listed speed. Misses destroy the projectile; impacts deal normal damage.
- [x] **Limited Fire.** Each mounted weapon with the property tracks its uses for the encounter; firing spends one use, and empty launchers cannot fire.

### Visual Polish (Flavor)
- [ ] **Ship movement animation.** Ships glide from start to end hex over ~300ms instead of teleporting.
- [ ] **Firing animation.** A line/beam from weapon arc to target, with a brief flash on hit. Damage numbers float up.
- [ ] **Critical damage effects.** Screen shake or red flash when a critical is scored. System icons appear on the ship.
- [ ] **Phase transition animations.** Brief overlay showing "HELM" / "GUNNERY" / "ENGINEERING" as phases change.

### Ship Data & Content
- [ ] **More ships.** Add SFS-safe Tier 1 and Tier 3 ships; defer optional material from other books.
- [ ] **Ship editor UI.** Form to create/edit custom ships (hull, shields, weapons, etc.) persisted to SQLite.
- [ ] **Weapon catalog.** Expand the SFS-safe weapon catalog and implement properties according to the priority list above; defer optional material from other books.

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
- Create → pick ships and teams → automatic random placement → start combat (manual placement remains future work)
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
- Scan and hidden information (after multiplayer)
- Crew damage
- Obstacles (asteroids, space stations)
- Full crew ability system
- NPC/AI ships (architecture supports it via controller abstraction)
- Multiplayer over network (hotseat first)

## AI Architecture (Future)
Ship controller is a field: `HumanController` or `AiController`. Orchestrator routes: human → wait for input, AI → call strategy. LLM piping possible by serializing state to JSON and prompting. Not v1.

## Rules Reference (Starfinder CRB pg. 316–326)
- Primary reference: [Archives of Nethys — Chapter 9: Starships](https://www.aonsrd.com/Rules.aspx?ID=179). Consult the relevant sections when designing, implementing, and testing starship rules; record intentional game-specific departures from those rules.
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

## Ship Roster
Ship definitions are maintained in `src/Quadrant.Server/ships/`. Keep additions within the SFS-safe content scope described above.

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
