#!/usr/bin/env python3
"""Drive a fresh Quadrant game into the Gunnery phase with Pegasus set up to
fire at Gorgon, then print the room code and resulting state.

The helm action order is randomized each round (piloting rolls), so this script
does NOT assume who acts first. Instead it loops over the Helm phase, reading
``currentActorId`` from state on every iteration: when Pegasus is up it moves to
a hex two away from Gorgon and faces it; everyone else skips. The loop ends as
soon as the phase advances to Gunnery.

Run with the venv::

    .venv/bin/python tests/scripts/setup_gunnery.py

Prints ``ROOM=<code>`` on the last line so it can be captured by a shell or
piped into a browser URL (``http://localhost:5180/<code>``).
"""

from __future__ import annotations

import sys

from quadrant_client import QuadrantClient, FACING_NAMES, direction_to, hex_distance


def end_engineering(client: QuadrantClient, room: str) -> None:
    """Spend 0 engineering on every ship, then close the phase."""
    state = client.state(room)
    for ship in state["ships"]:
        client.engineering(room, ship["id"], amount=0)
    client.end_engineering(room)


def run_helm(client: QuadrantClient, room: str, actor_name: str, target_name: str) -> None:
    """Advance through the Helm phase.

    ``actor_name`` moves to a hex two away from ``target_name`` and faces it;
    all other ships skip. Repeats until the phase leaves Helm.
    """
    guard = 0
    while True:
        state = client.state(room)
        if state["phase"] != "Helm":
            return
        guard += 1
        if guard > 20:
            raise RuntimeError("Helm phase did not terminate (possible loop).")

        actor_id = state["currentActorId"]
        actor = next(s for s in state["ships"] if s["id"] == actor_id)

        if actor["name"] != actor_name:
            client.skip_move(room)
            continue

        target = next(s for s in state["ships"] if s["name"] == target_name)
        # Sit two hexes west of the target, facing it (East).
        dest_q, dest_r = target["q"] - 2, target["r"]
        facing = direction_to(dest_q, dest_r, target["q"], target["r"])

        res = client.move(room, dest_q, dest_r, facing)
        ok = res.get("ok", True) if isinstance(res, dict) else True
        print(
            f"  [helm] {actor['name']} -> ({dest_q},{dest_r}) "
            f"facing={FACING_NAMES[facing]} (dist to {target_name}="
            f"{hex_distance(dest_q, dest_r, target['q'], target['r'])}) ok={ok}"
        )


def main() -> int:
    client = QuadrantClient()

    room = client.start_game()
    print(f"Room: {room}")

    client.start_combat(room)
    end_engineering(client, room)

    print("Helm phase:")
    run_helm(client, room, actor_name="Pegasus", target_name="Gorgon")

    state = client.state(room)
    print(f"\nPhase: {state['phase']}, Round: {state['round']}, "
          f"Actor: {state.get('currentActorId')}")
    for s in state["ships"]:
        print(f"  {s['name']} at ({s['q']},{s['r']}) facing={FACING_NAMES[s['facing']]}")

    options = (state.get("options") or {}).get("items", [])
    print(f"Gunnery options for actor:")
    for o in options:
        print(
            f"  idx={o['weaponIndex']} {o['weaponName']} -> "
            f"{o['targetShipId']} range={o['range']} hit%={o['hitChancePercent']}"
        )

    if state["phase"] != "Gunnery":
        print("ERROR: expected Gunnery phase.", file=sys.stderr)
        return 1

    print(f"\nROOM={room}")
    print(f"URL=http://localhost:5180/{room}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
