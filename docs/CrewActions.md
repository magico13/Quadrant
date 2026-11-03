# Starfinder Crew Actions Reference

Crew actions available during each phase of starship combat. Sourced from the user's [SpaceInitiative](https://github.com/) website, which references the [Starfinder SRD](http://www.starfindersrd.com/game-mastering/starship-combat/) and [Starfinder FAQ](http://paizo.com/starfinder/faq#v5748eaic9w55).

**Notation:**
- **T** = ship tier
- **ET** = enemy ship tier
- **Push** = can spend 1 Resolve to improve the result (typically -2 penalty becomes -4, or success threshold improves)
- **lvl N / N ranks** = requires the crew member to have at least N ranks in the relevant skill

---

## Engineering Phase

### Open Crew Actions (occur first, any crew member)

| Action | Description |
|--------|-------------|
| **Erratic Maneuvering** | +1 circumstance bonus to AC and TL against an opponent that performs a Flyby stunt this round. |
| **Feign Disaster** | Fake distress signal. Enemy captain/science officer must make Computers DC 10+1.5×T or take -2 penalty on scans, lock-on, and Piloting within 1 hex. Cannot be reused. |
| **Prioritize Calculation** | For one crew station using the ship's computer bonus, add an extra +1 to the bonus. |
| **Range Finding** | One crew member (gunner, pilot, or science officer) gains +1 bonus to an attack or skill check of their choice. |
| **Ready Weapon System** | Choose one weapon; if not fired this round and fired next round, gain +1 damage (+2 if tier 6+). |

### Engineer

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Divert** | DC 10+1.5×T Engineering | Boost a system: +2 speed, +2 science officer bonus, treat 1s as 2s on damage rolls, or restore 5% of PCU shields (distributed as desired). |
| **Hold It Together** | DC 15+1.5×T Engineering | Critical damage is 2 steps less severe for the round. Not affected by Power Core critical damage. |
| **Patch** | DC 10/15/20+1.5×T (1/2/3 actions) | Reduce a critical effect by one step. Not affected by Power Core critical damage. |
| **Overpower** *(6 ranks, Push)* | Spend 1 Resolve, DC 15+1.5×T Engineering | Divert power to 3 systems instead of 1. |
| **Quick Fix** *(12 ranks)* | Spend 1 Resolve, DC 20+1.5×T Engineering | Remove critical damage from 1 system for 1 hour. |

### Magic Officer

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Precognition** | DC 10+1.5×T Mysticism | +2 circumstance bonus to the Piloting check that determines piloting order. |
| **Scrying** | DC 5+1.5×ET+Countermeasures Mysticism | Functions as the Science Officer's Scan action but uses a Mysticism check. |
| **Eldritch Shot** *(Push)* | DC 10+1.5×T Mysticism | One weapon: treat its range as 5 hexes longer. |
| **Mystic Haze** *(6 ranks, Push)* | DC 20+1.5×T Mysticism | +1 bonus to AC. Enemy scan/lock-on actions must roll twice and take the lower result. |
| **Psychic Currents** *(12 ranks)* | Spend 1 Resolve, DC 20+1.5×T Mysticism | Reduce minimum turn distance by 1 (minimum 0). |

### Chief Mate

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Maintenance Panel Access** | DC 10+1.5×T Acrobatics/Athletics | Divert power to 2 systems instead of 1, or Overpower 4 systems instead of 3. Fail by 10+: all engineer actions take -2. |
| **Targeting Aid** *(6 ranks, Push)* | Spend 1 Resolve, DC 20+1.5×T Acrobatics/Athletics | One gunner can make Fire at Will or Broadside with no penalty. Fail by 10+: that gunner takes -2 to attacks. |

---

## Helm Phase

### Pilot

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Fly** | No check | Move up to Speed, turning as allowed by maneuverability. |
| **Maneuver** | DC 15+1.5×T Piloting | Move up to Speed; on success, reduce turn distance by 1 (min 0). |
| **Stunt** *(Push)* | See individual stunts below | Perform a stunt. |
| **Full Power** *(6 ranks, Push)* | Spend 1 Resolve | Move at 1.5× speed but turning distance increased by 2. |
| **Audacious Gambit** *(12 ranks, Push)* | Spend 1 Resolve, DC 20+1.5×T Piloting | Move up to Speed, reduce turns by 2, and don't provoke free attacks. Fail: move as normal. |

#### Stunts (Pilot)

| Stunt | DC | Effect | Failure |
|-------|-----|--------|---------|
| **Back Off** | DC 10+1.5×T Piloting | Move half speed backward without changing facing. | Fail: move back 1 hex. Fail by 5+: no movement, -4 to AC/TL. |
| **Barrel Roll** *(Large or smaller)* | DC 10+1.5×T Piloting | Move half speed and flip upside down, swapping Port/Starboard shield & guns until the start of next round. | Fail: move half, do not roll. Fail by 5+: move half, -4 AC/TL. |
| **Evade** | DC 10+1.5×T Piloting | Move as normal; +2 circumstance bonus to AC/TL. | Fail: move as normal. Fail by 5+: move as normal, -2 AC/TL. |
| **Flip and Burn** | DC 15+1.5×T Piloting | Move straight forward up to half speed, rotate 180°. | Fail: move half speed but do not rotate. |
| **Flyby** | DC 15+1.5×T Piloting | Move normal speed through 1 occupied hex without provoking a free attack. Next Gunnery phase, any 1 arc can fire at any other arc at close range. | Fail: move as above, no special attack, provoke free attack. |
| **Slide** | DC 10+1.5×T Piloting | Move up to speed in forward-port or forward-starboard without changing facing. | Fail: move forward up to half speed without turning. |
| **Turn in Place** | No check | Face any direction instead of moving. Clumsy: -4 AC/TL. Poor: -2 AC/TL. | — |

### Science Officer

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Balance** | DC 10+1.5×T Computers | Reassign shields from one quadrant to another (or average all). Each arc must retain at least 10% of remaining SP. |
| **Scan** | DC 5+1.5×ET+Countermeasures Computers | Learn more information about the target ship. |
| **Target System** *(Push)* | DC 5+1.5×ET+Countermeasures Computers | Next attack against this target crits on 19–20. If it crits, affects the chosen system. |
| **Lock On** *(6 ranks, Push)* | Spend 1 Resolve, DC 5+1.5×ET+Countermeasures Computers | +2 to Gunnery checks against the target. |
| **Improve Countermeasures** *(12 ranks, Push)* | Spend 1 Resolve, DC 5+1.5×ET+Countermeasures Computers | Target's gunners have disadvantage on attacks. |

### Chief Mate (Helm)

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Manual Realignment** | DC 10+1.5×T Acrobatics/Athletics | Gain +1 additional piece of info when the Science Officer uses Scan this turn. Fail by 10+: no scan can be attempted. |
| **Hard Turn** *(Push)* | DC 10+1.5×T Acrobatics/Athletics | Make one turn with maneuverability improved by 1 step. Fail by 10+: maneuverability reduced by 1 step. |
| **Maximize Speed** *(Push)* | Spend 1 Resolve, DC 25+1.5×T Acrobatics/Athletics | Increase speed by 2 (cumulative with other increases). |

### Minor Actions (Helm)

| Action | Role | Effect |
|--------|------|--------|
| **Glide** | Pilot | Move half speed, increase turn distance by 2. Can add Piloting ranks to AC/TL. |
| **Quick Rescan** | Science Officer | Requires a previous Scan on the target. DC 5+1.5×T+Countermeasures Computers to receive one piece of info. |
| **Visual Identification** | Science Officer | DC 10+1.5×T Computers/Engineering/Perception to learn the first unknown piece of info about a target. |

---

## Gunnery Phase

### Gunner

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Shoot** | No check | Fire a single weapon. If turret, can fire in any arc. |
| **Fire at Will** *(Push)* | — | Can fire two weapons regardless of arc with -4 on the attacks. |
| **Broadside** *(lvl 6, Push)* | Spend 1 Resolve | Fire all weapons in an arc (including turrets) at -2 attack. |
| **Precise Targeting** *(lvl 12, Push)* | Spend 1 Resolve | Attack 1 target with 1 weapon; if hit and target has no shields, deal critical damage. |

### Minor Actions (Gunnery)

| Action | Role | Effect |
|--------|------|--------|
| **Snap Shot** | Gunner | Fire one weapon at -2 attack if no other weapons have been fired this round. |

---

## Captain Actions (available in any phase)

| Action | DC / Cost | Effect |
|--------|-----------|--------|
| **Demand** | DC 15+1.5×T Intimidate | +4 to one check. Once per combat per person. |
| **Encourage** | DC 10 (same skill) or DC 15 Diplomacy | +2 to one check (Aid Another). |
| **Taunt** *(Push)* | DC 15+1.5×ET Bluff/Intimidate | Give -2 for 1d4 rounds on one phase (-4 on Push). Once per ship per combat. |
| **Orders** *(lvl 6, Push)* | Spend 1 Resolve, DC 15+1.5×T | Grant an additional action to one crew member. |
| **Moving Speech** *(lvl 12, Push)* | Spend 1 Resolve, DC 20+1.5×T Diplomacy | Allies can roll twice and take the better result. |

Resolve Points: NPC starships have RP equal to tier divided by 5, plus 3. For our purposes we can just use this formula for all ships. Resolve points are not yet available to use.

---

## Implementation Notes for Quadrant

- **Phase gating**: Engineering actions are only available during the Engineering phase; Helm actions during Helm; Gunnery actions during Gunnery. Captain actions are available in any phase.
- **Push mechanic**: Push actions (indicated in an action’s heading) are difficult to perform but can yield greater results. You can’t perform a push action if the necessary system is malfunctioning or wrecked as noted in Critical Damage Conditions
- **Rank requirements**: Actions marked with rank prerequisites (6 ranks, 12 ranks) require the crew member to have sufficient skill ranks. For v1 with simplified crew stats, we can gate these behind a ship tier or a boolean flag.
- **DC scaling**: Most DCs scale with `1.5 × T` (ship tier). Some use enemy tier (`ET`) and countermeasures. The formula is: `base + 1.5 * tier`, rounded down.
- **Target System** is the action that expands crit range to 19–20 — this is what we noted in the critical damage tests as a deferred feature.
- **Divert** options map to system boosts: speed, science officer bonus, damage (1s→2s), or shield recharge (5% PCU).
