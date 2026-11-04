# Test / scenario scripts

Python helpers that drive a running Quadrant server through its HTTP API to put
a game into a specific state, so you can then open the room in a browser and
inspect the UI (e.g. weapon range highlighting) without clicking through every
phase by hand.

## Setup

```bash
cd <repo root>
python3 -m venv .venv
.venv/bin/pip install -r tests/scripts/requirements.txt
```

The server must be running (`docker compose up -d --build`), reachable at
`http://localhost:5180`.

## Scripts

- **`quadrant_client.py`** — thin REST client for the `/api/game` endpoints.
  Import it from your own scripts; it also exposes hex math helpers
  (`hex_distance`, `direction_to`) that mirror the rules engine.

- **`setup_gunnery.py`** — starts a fresh game, ends Engineering, and plays the
  Helm phase, moving Pegasus as close to Gorgon as its legal moves allow, landing
  the game in Gunnery. Prints the room code and a `URL=` line you can open in a
  browser.

```bash
.venv/bin/python tests/scripts/setup_gunnery.py
# ...
# ROOM=3MM7YY
# URL=http://localhost:5180/3MM7YY
```

### Why the Helm phase is handled adaptively

Ship action order is randomized each round by piloting rolls, so a script cannot
assume who moves first. `setup_gunnery.py` instead loops over the Helm phase,
reading `currentActorId` from state on every iteration: when Pegasus is up it
moves toward Gorgon; everyone else skips. Random starting positions may prevent Pegasus from reaching firing position in one round. The loop exits as soon as the phase
advances to Gunnery.

### Getting a specific ship's gunnery turn

The script leaves the game at the *first* Gunnery actor, which may not be
Pegasus. To advance until a particular ship is up, skip firing for the others:

```python
from quadrant_client import QuadrantClient
c = QuadrantClient()
room = "3MM7YY"
for _ in range(6):
    st = c.state(room)
    if st["phase"] != "Gunnery": break
    if st.get("currentActorId") == next(s["id"] for s in st["ships"] if s["name"] == "Pegasus"):
        break
    c.skip_fire(room)
```
