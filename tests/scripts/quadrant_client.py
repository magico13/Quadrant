"""Thin REST client for driving a Quadrant game through its HTTP API.

The server exposes a small set of endpoints under ``/api/game`` (see
``src/Quadrant.Server/Controllers/GameController.cs``). This wrapper keeps the
boilerplate in one place so scenario scripts can read state, act on the current
actor, and assert on the resulting board without repeating request plumbing.

Usage::

    from quadrant_client import QuadrantClient

    client = QuadrantClient()          # defaults to http://localhost:5180
    room = client.start_game()
    client.start_combat(room)
    state = client.state(room)
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any, Optional

import requests


# Facing indices match Hex.Directions (pointy-top axial), clockwise from NW.
FACING_NAMES = ["NW", "NE", "E", "SE", "SW", "W"]

# Axial deltas per facing index: (dq, dr).
DIRECTIONS = [(0, -1), (1, -1), (1, 0), (0, 1), (-1, 1), (-1, 0)]


def hex_distance(q1: int, r1: int, q2: int, r2: int) -> int:
    """Cube distance between two axial coordinates."""
    a = q1 - q2
    b = r1 - r2
    c = -(a + b)
    return max(abs(a), abs(b), abs(c))


def direction_to(q1: int, r1: int, q2: int, r2: int) -> int:
    """Clockwise facing index (0-5) from hex 1 toward hex 2.

    Mirrors ``Hex.DirectionTo`` in the rules engine: exact for adjacent hexes,
    nearest-by-angle otherwise.
    """
    x = q2 - q1
    z = r2 - r1
    y = -x - z
    for i, (dq, dr) in enumerate(DIRECTIONS):
        dy = -dq - dr
        if x == dq and y == dy and z == dr:
            return i
    px = math.sqrt(3) * (x + z / 2.0)
    py = 1.5 * z
    angle = math.atan2(py, px) * 180.0 / math.pi
    dir_angles = [-120, -60, 0, 60, 120, 180]
    best, best_diff = 0, float("inf")
    for i, a in enumerate(dir_angles):
        diff = abs(angle - a)
        if diff > 180:
            diff = 360 - diff
        if diff < best_diff:
            best, best_diff = i, diff
    return best


@dataclass
class QuadrantClient:
    """A stateless client bound to one server base URL."""

    base_url: str = "http://localhost:5180"
    timeout: float = 10.0
    _session: requests.Session = field(default_factory=requests.Session, repr=False)

    @property
    def api(self) -> str:
        return f"{self.base_url}/api/game"

    # -- low-level ---------------------------------------------------------
    def _get(self, path: str) -> Any:
        r = self._session.get(f"{self.api}{path}", timeout=self.timeout)
        r.raise_for_status()
        return r.json()

    def _post(self, path: str, body: Optional[dict] = None) -> Any:
        r = self._session.post(
            f"{self.api}{path}", json=body or {}, timeout=self.timeout
        )
        r.raise_for_status()
        return r.json()

    # -- game lifecycle ----------------------------------------------------
    def start_game(self) -> str:
        """Create a new hotseat game with the default 3-ship setup. Returns room code."""
        return self._post("/start")["roomCode"]

    def state(self, room: str) -> dict:
        """Full current game state (ships, phase, round, options, log)."""
        return self._get(f"/state/{room}")

    def start_combat(self, room: str) -> bool:
        return self._post(f"/{room}/start-combat")["ok"]

    # -- engineering -------------------------------------------------------
    def engineering(self, room: str, ship_id: str, amount: int = 0) -> dict:
        return self._post(f"/{room}/engineering", {"shipId": ship_id, "amount": amount})

    def end_engineering(self, room: str) -> dict:
        return self._post(f"/{room}/end-engineering")

    # -- helm --------------------------------------------------------------
    def move(self, room: str, q: int, r: int, facing: int) -> dict:
        return self._post(f"/{room}/move", {"q": q, "r": r, "facing": facing})

    def turn_in_place(self, room: str, facing: int) -> dict:
        return self._post(f"/{room}/turn-in-place", {"facing": facing})

    def skip_move(self, room: str) -> dict:
        return self._post(f"/{room}/skip-move")

    # -- gunnery -----------------------------------------------------------
    def fire(self, room: str, shots: list[dict]) -> dict:
        """shots: list of {"weaponIndex": int, "targetShipId": str}."""
        return self._post(f"/{room}/fire", {"shots": shots})

    def skip_fire(self, room: str) -> dict:
        return self._post(f"/{room}/skip-fire")

    # -- convenience -------------------------------------------------------
    def ship_by_id(self, room: str, ship_id: str) -> Optional[dict]:
        for s in self.state(room)["ships"]:
            if s["id"] == ship_id:
                return s
        return None

    def ship_by_name(self, room: str, name: str) -> Optional[dict]:
        for s in self.state(room)["ships"]:
            if s["name"] == name:
                return s
        return None
