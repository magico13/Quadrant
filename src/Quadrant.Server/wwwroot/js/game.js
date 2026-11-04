// Quadrant — interactive game client.
// Hotseat play against the REST API: start a game, then drive Engineering → Helm → Gunnery.

const canvas = document.getElementById('hex-canvas');
const grid = new HexGrid(canvas);

const logEl = document.getElementById('log');
const actionPanel = document.getElementById('action-panel');
const phaseEl = document.getElementById('phase-indicator');
const roundEl = document.getElementById('round-indicator');
const roomEl = document.getElementById('room-code');
const shipDetailsEl = document.getElementById('ship-details');
const tooltipEl = document.getElementById('ship-tooltip');

let roomCode = null;
let state = null;          // last fetched game state
let selectedShots = new Map(); // gunnery: weaponIndex → targetShipId (one target per weapon instance)
let activeWeapon = null;   // currently selected weapon in gunnery targeting
let detailsShipId = null;  // ship currently shown in the details panel
let pendingMove = null;    // helm: { option, actorId } — move previewed, awaiting confirmation
let refreshGunnery = null; // module-scope handle to the current gunnery refresh fn

// ────────────────────────────────────────────────
//  API helpers
// ────────────────────────────────────────────────

async function api(path, body, method = 'POST') {
    // POSTs always send a JSON content type + body; empty-body POSTs without one
    // fall through to the SPA fallback and return HTML instead of JSON.
    const opts = { method };
    if (method === 'POST') {
        opts.headers = { 'Content-Type': 'application/json' };
        opts.body = JSON.stringify(body ?? {});
    }
    const res = await fetch(`/api/game${path}`, opts);
    if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
    return res.json();
}

async function fetchState() {
    state = await api(`/state/${roomCode}`, null, 'GET');
    for (const e of state.log) addLog(e.phase, e.message);
    render();
}

// ────────────────────────────────────────────────
//  New game
// ────────────────────────────────────────────────

async function newGame() {
    // Opening a fresh game goes through the fleet setup overlay first.
    showSetup();
}

// Actually create the game on the server with the chosen fleet (or defaults).
async function launchGame(selections) {
    const res = await api('/start', { selections });
    roomCode = res.roomCode;
    selectedShots.clear();
    activeWeapon = null;
    detailsShipId = null;
    hideShipDetails();
    logEl.innerHTML = '';
    hideSetup();
    history.replaceState(null, '', `/${roomCode}`);
    await fetchState();
}

document.getElementById('btn-new-game').addEventListener('click', () => {
    newGame().catch(err => addLog('error', `Failed to start game: ${err.message}`));
});

// ────────────────────────────────────────────────
//  Fleet setup overlay
// ────────────────────────────────────────────────

let catalog = [];            // ship definitions from /catalog
let fleet = [[], []]; // one array of ship IDs per team

const setupOverlay = document.getElementById('setup-overlay');
const catalogListEl = document.getElementById('catalog-list');
const launchBtn = document.getElementById('btn-setup-launch');

async function showSetup() {
    fleet = [[], []];
    try {
        catalog = await api('/catalog', null, 'GET');
    } catch (err) {
        addLog('error', `Could not load ship catalog: ${err.message}`);
        return;
    }
    renderCatalogList();
    renderTeamColumns();
    setupOverlay.classList.remove('hidden');
}

function hideSetup() {
    setupOverlay.classList.add('hidden');
}

function renderCatalogList() {
    catalogListEl.innerHTML = catalog.map(s => `
        <div class="catalog-card">
            <div class="cc-top">
                <span class="cc-name">${s.name}</span>
                <span class="cc-tier">Tier ${s.tier}</span>
            </div>
            <div class="cc-stats">SPD ${s.speed} · HP ${s.hullPoints} · AC ${s.ac} · ${s.maneuverability}</div>
            <div class="cc-weapons">${s.weapons.map(w => `${w.name} (${w.arc})`).join(', ') || 'no weapons'}</div>
            <div class="cc-add">
                ${fleet.map((_, team) => `<button data-team="${team}" data-ship="${s.id}" style="color:${teamColor(team)}">+ Team ${team + 1}</button>`).join('')}
            </div>
        </div>`).join('');

    catalogListEl.querySelectorAll('.cc-add button').forEach(btn => {
        btn.addEventListener('click', () => {
            addToFleet(parseInt(btn.dataset.team, 10), btn.dataset.ship);
        });
    });
}

function renderTeamColumns() {
    document.getElementById('team-list').innerHTML = fleet.map((ships, team) => `
        <div class="team-col">
            <h3><span class="roster-dot" style="background:${teamColor(team)}"></span> Team ${team + 1}
                ${team >= 2 ? `<button class="ts-remove" data-remove-team="${team}" title="Remove team">×</button>` : ''}
            </h3>
            <div class="team-ships">${ships.length ? ships.map((id, i) => {
                const ship = catalog.find(c => c.id === id);
                return `<div class="team-ship-row"><span class="ts-name">${ship ? ship.name : id}</span>
                    <button class="ts-remove" data-team="${team}" data-idx="${i}" title="Remove ship">×</button></div>`;
            }).join('') : '<div class="hint">No ships yet</div>'}</div>
        </div>`).join('');
    document.querySelectorAll('.ts-remove[data-idx]').forEach(btn => btn.addEventListener('click', () => {
        fleet[Number(btn.dataset.team)].splice(Number(btn.dataset.idx), 1);
        renderTeamColumns();
    }));
    document.querySelectorAll('[data-remove-team]').forEach(btn => btn.addEventListener('click', () => {
        fleet.splice(Number(btn.dataset.removeTeam), 1);
        renderCatalogList();
        renderTeamColumns();
    }));
    document.getElementById('btn-add-team').disabled = fleet.length >= 5;
    launchBtn.disabled = fleet.some(ships => ships.length === 0);
}

function addToFleet(teamId, shipId) {
    fleet[teamId].push(shipId);
    renderTeamColumns();
}

document.getElementById('btn-add-team').addEventListener('click', () => {
    if (fleet.length >= 5) return;
    fleet.push([]);
    renderCatalogList();
    renderTeamColumns();
});

// Flatten the per-team arrays into the [{shipId, teamId}] shape the API expects.
function buildSelections() {
    const sel = [];
    for (let team = 0; team < fleet.length; team++)
        for (const shipId of fleet[team])
            sel.push({ shipId, teamId: team });
    return sel;
}

launchBtn.addEventListener('click', () => {
    launchGame(buildSelections()).catch(err => addLog('error', `Failed to start game: ${err.message}`));
});

document.getElementById('btn-setup-defaults').addEventListener('click', () => {
    launchGame([]).catch(err => addLog('error', `Failed to start game: ${err.message}`));
});

// ────────────────────────────────────────────────
//  Render
// ────────────────────────────────────────────────

function render() {
    if (!state) return;

    phaseEl.textContent = state.phase;
    roundEl.textContent = `Round ${state.round}`;
    roomEl.textContent = roomCode || '';

    // Feed ships to the grid.
    const ships = state.ships.map(s => ({
        id: s.id, name: s.name, teamId: s.teamId, q: s.q, r: s.r, facing: s.facing,
        hull: s.hull, maxHull: s.maxHull, shields: s.shields, maxShield: s.maxShield,
    }));
    grid.setShips(ships);

    renderRoster();

    // Refresh the details panel if a ship is still selected.
    if (detailsShipId) {
        const s = state.ships.find(x => x.id === detailsShipId);
        if (s) showShipDetails(s); else hideShipDetails();
    }

    // A helm preview only survives within the same actor's turn.
    if (pendingMove && (state.phase !== 'Helm' || pendingMove.actorId !== state.currentActorId)) {
        pendingMove = null;
        grid.setGhost(null);
        grid.setPath(null);
    }

    switch (state.phase) {
        case 'Setup': renderSetup(); break;
        case 'Engineering': renderEngineering(); break;
        case 'Helm': renderHelm(); break;
        case 'Gunnery': renderGunnery(); break;
        default: renderComplete();
    }
}

function currentActor() {
    return state.ships.find(s => s.id === state.currentActorId);
}

// ── Ship Roster ──
const rosterEl = document.getElementById('ship-roster');

function renderRoster() {
    if (!state || !state.ships) { rosterEl.innerHTML = ''; return; }
    const isCombat = ['Engineering', 'Helm', 'Gunnery'].includes(state.phase);
    rosterEl.innerHTML = state.ships.map(s => {
        const active = s.id === state.currentActorId ? ' active' : '';
        const selected = s.id === detailsShipId ? ' selected' : '';
        const hp = isCombat ? ` <span class="roster-hp">${s.hull}/${s.maxHull}</span>` : '';
        return `<div class="roster-entry${active}${selected}" style="--team-color:${teamColor(s.teamId)}" data-ship="${s.id}">
            <span class="roster-dot"></span>
            <span class="roster-name">${s.name} <small>(Team ${s.teamId + 1})</small></span>${hp}
        </div>`;
    }).join('');

    rosterEl.querySelectorAll('.roster-entry').forEach(el => {
        el.addEventListener('click', () => {
            const ship = state.ships.find(s => s.id === el.dataset.ship);
            if (ship) { detailsShipId = ship.id; showShipDetails(ship); grid.centerOn(ship.q, ship.r); }
        });
    });
}

// ── Setup ──
function renderSetup() {
    grid.setHighlights([]);
    grid.setTargets([]);
    actionPanel.innerHTML = `
        <div class="hint">Ships are placed automatically. Click "Begin Combat" to start.</div>
        <button class="btn btn-primary" id="btn-begin">Begin Combat</button>`;
    document.getElementById('btn-begin').addEventListener('click', async () => {
        await api(`/${roomCode}/start-combat`);
        await fetchState();
    });
}

// ── Engineering (v1: simple hull repair) ──
function renderEngineering() {
    grid.setHighlights([]);
    grid.setTargets([]);

    const damaged = state.ships.filter(s => !s.isDestroyed && s.hull < s.maxHull);

    if (damaged.length === 0) {
        actionPanel.innerHTML = `
            <div class="actor-name">Engineering</div>
            <div class="hint">No hull damage to repair.</div>
            <button class="btn btn-primary" id="btn-end-eng">End Engineering</button>`;
        document.getElementById('btn-end-eng').addEventListener('click', async () => {
            await api(`/${roomCode}/end-engineering`);
            await fetchState();
        });
        return;
    }

    actionPanel.innerHTML = `
        <div class="actor-name">Engineering</div>
        <div class="hint">Repair up to 3 hull points per ship this round.</div>
        ${damaged.map(s => `
            <div class="eng-row">
                <label>${s.name} — ${s.hull}/${s.maxHull} hull</label>
                <input type="number" id="eng-${s.id}" min="0" max="${Math.min(3, s.maxHull - s.hull)}" value="0">
            </div>`).join('')}
        <button class="btn btn-primary" id="btn-end-eng">End Engineering</button>`;

    document.getElementById('btn-end-eng').addEventListener('click', async () => {
        for (const s of damaged) {
            const amount = parseInt(document.getElementById(`eng-${s.id}`).value, 10) || 0;
            if (amount > 0) await api(`/${roomCode}/engineering`, { shipId: s.id, amount });
        }
        await api(`/${roomCode}/end-engineering`);
        await fetchState();
    });
}

// ── Helm ──
function renderHelm() {
    const actor = currentActor();
    if (!actor) { grid.setHighlights([]); grid.setTargets([]); grid.setGhost(null); grid.setPath(null); actionPanel.innerHTML = ''; return; }

    // Highlight every reachable hex (union over facings).
    grid.setHighlights((state.options?.items || []).map(m => m.position));
    grid.setTargets([]);

    const facingNames = ['NW', 'NE', 'E', 'SE', 'SW', 'W'];
    const pending = pendingMove?.option;
    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(actor.teamId)}">${actor.name}</div>
        ${pending
            ? `<div class="hint">${pending.isTurnInPlace
                    ? `Turning in place to face ${facingNames[pending.finalFacing]}.`
                    : `Moving to (${pending.position.q}, ${pending.position.r}), facing ${facingNames[pending.finalFacing]}.`} Click another hex or your ship to change, or confirm.</div>
               <button class="btn btn-primary" id="btn-confirm-move">Confirm Move</button>
               <button class="btn btn-ghost" id="btn-cancel-move">Cancel</button>`
            : `<div class="hint">Click a highlighted hex to preview a move. Click your own ship to turn in place. If several facings are possible, you'll be asked which.</div>
               <button class="btn btn-ghost" id="btn-hold">Hold Position</button>`}`;

    if (pending) {
        document.getElementById('btn-confirm-move').addEventListener('click', confirmMove);
        document.getElementById('btn-cancel-move').addEventListener('click', () => setPendingMove(null));
    } else {
        document.getElementById('btn-hold').addEventListener('click', async () => {
            await api(`/${roomCode}/skip-move`);
            await fetchState();
        });
    }
}

// Preview (or clear) a helm move. The ghost ship shows where the actor would end up.
function setPendingMove(m) {
    const actor = currentActor();
    pendingMove = m && actor ? { option: m, actorId: actor.id } : null;
    grid.setGhost(pendingMove
        ? { q: m.position.q, r: m.position.r, facing: m.finalFacing, teamId: actor.teamId, name: actor.name }
        : null);
    grid.setPath(pendingMove ? m.path : null);
    renderHelm();
}

async function confirmMove() {
    const m = pendingMove?.option;
    if (!m) return;
    try {
        if (m.isTurnInPlace) {
            await api(`/${roomCode}/turn-in-place`, { facing: m.finalFacing });
        } else {
            await api(`/${roomCode}/move`, { q: m.position.q, r: m.position.r, facing: m.finalFacing });
        }
        pendingMove = null;
        grid.setGhost(null);
        grid.setPath(null);
        await fetchState();
    } catch (err) {
        addLog('error', `Move failed: ${err.message}`);
    }
}

// Clockwise direction index (0-5) from one hex to another, or -1 if the same.
// Mirrors Hex.DirectionTo in the rules engine so arc math matches the server.
function directionTo(from, to) {
    const x = to.q - from.q;
    const z = to.r - from.r;
    const y = -x - z;
    for (let i = 0; i < 6; i++) {
        const d = Hex.DIRECTIONS[i];
        if (x === d.dq && y === -d.dq - d.dr && z === d.dr) return i;
    }
    // Not adjacent — nearest direction by angle (pointy-top).
    const px = Math.sqrt(3) * (x + z / 2);
    const py = 1.5 * z;
    const angleDeg = Math.atan2(py, px) * 180 / Math.PI;
    const dirAngles = [-120, -60, 0, 60, 120, 180];
    let best = 0, bestDist = Infinity;
    for (let i = 0; i < 6; i++) {
        let diff = Math.abs(angleDeg - dirAngles[i]);
        if (diff > 180) diff = 360 - diff;
        if (diff < bestDist) { bestDist = diff; best = i; }
    }
    return best;
}

// Angle-based arc coverage check. Mirrors Weapon.ArcCovers on the server.
// angle is in degrees from facing: 0 = ahead, positive = starboard (clockwise),
// negative = port (counter-clockwise). Boundaries are inclusive — a hex on the
// boundary line belongs to both adjacent arcs. EPS guards against floating-point
// drift so e.g. atan2 returning 29.999… for a true 30° hex still counts as ≥30.
const ARC_EPS = 0.01; // degrees
function arcCovers(arc, angle) {
    switch (arc) {
        case 'AllArcs': return true;
        case 'Forward': return angle >= -30 - ARC_EPS && angle <= 30 + ARC_EPS;
        case 'Starboard': return angle >= 30 - ARC_EPS && angle <= 150 + ARC_EPS;
        case 'Aft': return angle >= 150 - ARC_EPS || angle <= -150 + ARC_EPS;
        case 'Port': return angle >= -150 - ARC_EPS && angle <= -30 + ARC_EPS;
        default: return false;
    }
}

// Continuous angle in degrees from a ship's facing to a relative hex offset.
// Mirrors ShipInstance.AngleFromFacing on the server.
function angleFromFacing(facing, relQ, relR) {
    const x = relQ, z = relR;
    if (x === 0 && z === 0) return 0;
    const px = Math.sqrt(3) * (x + z / 2);
    const py = 1.5 * z;
    const angleDeg = Math.atan2(py, px) * 180 / Math.PI;
    // Facing direction angles: NW=-120, NE=-60, E=0, SE=60, SW=120, W=180
    const facingAngles = [-120, -60, 0, 60, 120, 180];
    let diff = angleDeg - facingAngles[facing];
    while (diff > 180) diff -= 360;
    while (diff < -180) diff += 360;
    return diff;
}

// Hexes within a weapon's range band [minR, maxR] AND inside its firing arc.
// Visual only — used to show where the weapon can reach during Gunnery.
// Each hex is tagged with `band` (1 = first increment, 2 = second, ...) so the
// renderer can fade each successive increment.
function rangeBandHexes(ship, minR, maxR, arc, inc) {
    const out = [];
    for (let r = -maxR; r <= maxR; r++) {
        for (let q = -maxR; q <= maxR; q++) {
            if (q === 0 && r === 0) continue; // the ship's own hex
            const d = Hex.distance({ q: 0, r: 0 }, { q, r });
            if (d < minR || d > maxR) continue;
            const angle = angleFromFacing(ship.facing, q, r);
            if (!arcCovers(arc, angle)) continue;
            out.push({ q: ship.q + q, r: ship.r + r, band: Math.ceil(d / (inc || 1)) });
        }
    }
    return out;
}

// ── Gunnery ──
function renderGunnery() {
    const actor = currentActor();
    if (!actor) { grid.setHighlights([]); grid.setTargets([]); actionPanel.innerHTML = ''; return; }

    // Clear any Helm-phase highlights/path/ghost.
    grid.setHighlights([]);
    grid.setPath(null);
    grid.setGhost(null);

    // Reset selection when the actor changes.
    selectedShots.clear();
    activeWeapon = null;

    const options = state.options?.items || [];

    // Group valid targets by weapon index so each instance gets its own chip.
    const weapons = new Map(); // weaponIndex → [options]
    for (const o of options) {
        if (!weapons.has(o.weaponIndex)) weapons.set(o.weaponIndex, []);
        weapons.get(o.weaponIndex).push(o);
    }

    // Auto-target: pre-select the closest valid enemy for each weapon.
    for (const [widx, opts] of weapons) {
        if (!opts.length) continue;
        const closest = opts.reduce((best, o) => o.range < best.range ? o : best);
        selectedShots.set(widx, closest.targetShipId);
    }

    const chips = [...weapons.entries()].map(([widx]) => {
        const w = actor.weapons?.[widx];
        if (!w) return '';
        const arcLabel = w.arc && w.arc !== 'AllArcs' ? ` (${w.arc})` : '';
        const dmg = ` · ${w.damage}`;
        return `<button class="weapon-chip" data-weapon="${widx}">${w.name}${arcLabel}${dmg}</button>`;
    }).join('');

    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(actor.teamId)}">${actor.name}</div>
        <div class="hint">Each weapon auto-targets its closest enemy in range — click an enemy to retarget, or pick a weapon above to see its range.</div>
        <div class="weapon-chips">${chips || '<span class="hint">No weapons can fire.</span>'}</div>
        <div id="target-info" class="target-info"></div>
        <button class="btn btn-primary" id="btn-fire" disabled>Fire</button>
        <button class="btn btn-ghost" id="btn-skip-fire">Skip Firing</button>`;

    const fireBtn = document.getElementById('btn-fire');
    const targetInfo = document.getElementById('target-info');

    function refresh() {
        // Highlight the targets reachable by the active weapon.
        if (activeWeapon !== null) {
            const opts = weapons.get(activeWeapon) || [];
            grid.setTargets(opts.map(o => [o.targetShipId, o.hitChancePercent]));
            // Show this specific weapon's range band as a visual aid (not clickable).
            const w = actor.weapons?.[activeWeapon];
            if (w) {
                const inc = w.rangeIncrement ?? 5;
                const maxVis = inc * 3; // show up to 3 increments
                const hexes = rangeBandHexes(actor, w.minRange ?? 1, maxVis, w.arc, inc);
                grid.setRangeHexes(hexes);
            }
            const label = w ? `${w.name}${w.arc && w.arc !== 'AllArcs' ? ` (${w.arc})` : ''}` : activeWeapon;
            targetInfo.innerHTML = opts.length
                ? `<span class="hint">Click a highlighted enemy to set ${label}'s target.</span>`
                : `<span class="hint">${label} has no targets in range.</span>`;
        } else {
            grid.setTargets([]);
            grid.setHighlights([]);
            targetInfo.innerHTML = '<span class="hint">Select a weapon above.</span>';
        }

        // Show current assignments.
        const assigned = [...selectedShots.entries()];
        if (assigned.length) {
            targetInfo.innerHTML += `<div class="shot-list">${assigned.map(([widx, tid]) => {
                const w = actor.weapons?.[widx];
                const label = w ? `${w.name}${w.arc && w.arc !== 'AllArcs' ? ` (${w.arc})` : ''}` : widx;
                const t = state.ships.find(s => s.id === tid);
                const opt = (weapons.get(widx) || []).find(o => o.targetShipId === tid);
                const rangeStr = opt ? ` [${opt.range} hex${opt.range !== 1 ? 'es' : ''}${opt.rangePenalty ? `, ${opt.rangePenalty}` : ''}]` : '';
                return `<span class="shot-item">${label} → ${t?.name ?? '?'}${rangeStr}</span>`;
            }).join('')}</div>`;
        }

        fireBtn.disabled = assigned.length === 0;
    }

    refreshGunnery = refresh; // expose for the ship-click dispatcher

    actionPanel.querySelectorAll('.weapon-chip').forEach(chip => {
        chip.addEventListener('click', () => {
            const idx = parseInt(chip.dataset.weapon, 10);
            activeWeapon = (activeWeapon === idx) ? null : idx;
            actionPanel.querySelectorAll('.weapon-chip').forEach(c =>
                c.classList.toggle('active', parseInt(c.dataset.weapon, 10) === activeWeapon));
            refresh();
        });
    });

    refresh();

    document.getElementById('btn-fire').addEventListener('click', async () => {
        const shots = [...selectedShots.entries()].map(([weaponIndex, targetShipId]) => ({ weaponIndex, targetShipId }));
        await api(`/${roomCode}/fire`, { shots });
        await fetchState();
    });

    document.getElementById('btn-skip-fire').addEventListener('click', async () => {
        await api(`/${roomCode}/skip-fire`);
        await fetchState();
    });
}

// ── Complete ──
function renderComplete() {
    grid.setHighlights([]);
    grid.setTargets([]);
    actionPanel.innerHTML = `
        <div class="winner-banner">${state.winner || 'Combat over'}</div>
        <button class="btn btn-primary" id="btn-again">Play Again</button>`;
    document.getElementById('btn-again').addEventListener('click', () => {
        newGame().catch(err => addLog('error', `Failed to start game: ${err.message}`));
    });
}

// ────────────────────────────────────────────────
//  Ship details panel (click a ship)
// ────────────────────────────────────────────────

// Weapon currently pinned in the details panel (click a row to keep its range
// highlighted after the mouse leaves). { shipId, weaponIndex } or null.
let pinnedWeapon = null;

// Highlight the hexes a specific weapon can reach from its ship's current
// position/facing. Uses setRangeHexesOnly so it layers over Helm move previews.
function showWeaponRange(ship, weaponIndex) {
    const w = (ship.weapons || [])[weaponIndex];
    if (!w) return;
    const inc = w.rangeIncrement ?? 5;
    const maxVis = inc * 3; // show up to 3 increments
    grid.setRangeHexesOnly(rangeBandHexes(ship, w.minRange ?? 1, maxVis, w.arc, inc));
}

function clearWeaponRange() {
    grid.setRangeHexesOnly([]);
}

function showShipDetails(ship) {
    const shieldTotal = Object.values(ship.shields || {}).reduce((a, b) => a + b, 0);
    const shieldMax = (ship.maxShield ?? 0) * 4;
    const arcs = ['forward', 'starboard', 'aft', 'port'];

    // Reset the pin if it no longer refers to this ship.
    if (pinnedWeapon && pinnedWeapon.shipId !== ship.id) {
        pinnedWeapon = null;
        clearWeaponRange();
    }

    const weaponsHtml = (ship.weapons || []).map((w, i) => `
        <div class="detail-weapon dw-hoverable" data-weapon-index="${i}">
            <span class="dw-name">${w.name}</span>
            <span class="dw-meta">${w.arc} · inc ${w.rangeIncrement ?? 5} · ${w.damage}${w.type ? ` · ${w.type}` : ''}</span>
        </div>`).join('');

    const systemsHtml = (ship.damagedSystems || []).length
        ? `<div class="detail-section">Damaged Systems</div><ul class="detail-list">${ship.damagedSystems.map(s => `<li>${s.system} — ${s.condition}</li>`).join('')}</ul>`
        : '';

    shipDetailsEl.innerHTML = `
        <div class="sd-header" style="--team-color:${teamColor(ship.teamId)}">
            <span class="sd-name">${ship.name}</span>
            <button class="btn btn-ghost sd-close" title="Close">×</button>
        </div>
        <div class="detail-grid">
            <div class="detail-stat"><span>Hull</span><b>${ship.hull}/${ship.maxHull}</b></div>
            <div class="detail-stat"><span>Shields</span><b>${shieldTotal}/${shieldMax}</b></div>
            <div class="detail-stat"><span>AC</span><b>${ship.ac}</b></div>
            <div class="detail-stat"><span>TL</span><b>${ship.tl}</b></div>
            <div class="detail-stat"><span>Speed</span><b>${ship.speed}</b></div>
            <div class="detail-stat"><span>Maneuver</span><b>${ship.maneuverability}</b></div>
        </div>
        <div class="detail-section">Shield Arcs</div>
        <div class="shield-arcs">${arcs.map(a => `
            <div class="sa-row"><span>${a[0].toUpperCase() + a.slice(1)}</span><b>${ship.shields?.[a] ?? 0}</b></div>`).join('')}</div>
        <div class="detail-section">Weapons</div>
        ${weaponsHtml || '<div class="hint">No weapons.</div>'}
        ${systemsHtml}
        ${(ship.isDisabled || ship.isDestroyed) ? `<div class="ship-status">${ship.isDestroyed ? 'DESTROYED' : 'DISABLED'}</div>` : ''}`;

    shipDetailsEl.classList.remove('hidden');
    shipDetailsEl.querySelector('.sd-close').addEventListener('click', () => {
        detailsShipId = null;
        pinnedWeapon = null;
        clearWeaponRange();
        hideShipDetails();
    });

    // Hover a weapon row to preview its range; click to pin it.
    const rows = shipDetailsEl.querySelectorAll('.detail-weapon');
    rows.forEach(row => {
        const idx = parseInt(row.dataset.weaponIndex, 10);
        row.addEventListener('mouseenter', () => showWeaponRange(ship, idx));
        row.addEventListener('mouseleave', () => {
            if (pinnedWeapon && pinnedWeapon.shipId === ship.id) {
                showWeaponRange(ship, pinnedWeapon.weaponIndex); // restore pin
            } else {
                clearWeaponRange();
            }
        });
        row.addEventListener('click', () => {
            if (pinnedWeapon && pinnedWeapon.shipId === ship.id && pinnedWeapon.weaponIndex === idx) {
                pinnedWeapon = null; // toggle off
                clearWeaponRange();
                rows.forEach(r => r.classList.remove('dw-pinned'));
            } else {
                pinnedWeapon = { shipId: ship.id, weaponIndex: idx };
                showWeaponRange(ship, idx);
                rows.forEach(r => r.classList.toggle('dw-pinned', parseInt(r.dataset.weaponIndex, 10) === idx));
            }
        });
    });

    // Restore a pinned range if this ship is already pinned.
    if (pinnedWeapon && pinnedWeapon.shipId === ship.id) {
        showWeaponRange(ship, pinnedWeapon.weaponIndex);
        rows.forEach(r => r.classList.toggle('dw-pinned', parseInt(r.dataset.weaponIndex, 10) === pinnedWeapon.weaponIndex));
    }

    renderRoster();
}

function hideShipDetails() {
    shipDetailsEl.classList.add('hidden');
    renderRoster();
}

// ────────────────────────────────────────────────
//  Hover tooltip
// ────────────────────────────────────────────────

grid.onShipHover = (ship, x, y) => {
    if (!ship) { tooltipEl.classList.add('hidden'); return; }
    const shieldTotal = Object.values(ship.shields || {}).reduce((a, b) => a + b, 0);
    tooltipEl.innerHTML = `<b>${ship.name}</b><span>Hull ${ship.hull}/${ship.maxHull} · Shields ${shieldTotal}</span>`;
    tooltipEl.style.left = `${x + 14}px`;
    tooltipEl.style.top = `${y + 14}px`;
    tooltipEl.classList.remove('hidden');
};

// ────────────────────────────────────────────────
//  Ship click (single dispatcher)
// ────────────────────────────────────────────────

grid.onShipClick = (ship) => {
    if (!state || state.isComplete) return;

    // During Helm, clicking your own ship offers the Turn In Place stunt.
    if (state.phase === 'Helm' && ship.id === state.currentActorId) {
        showTurnInPlacePicker(ship);
        return;
    }

    // During Gunnery, clicking an enemy retargets a weapon at it.
    if (state.phase === 'Gunnery') {
        const options = state.options?.items || [];
        if (activeWeapon !== null) {
            // Specific weapon selected — use it if it can reach this target.
            const opt = options.find(o => o.weaponIndex === activeWeapon && o.targetShipId === ship.id);
            if (opt) {
                selectedShots.set(activeWeapon, ship.id);
                refreshGunnery?.();
            }
        } else {
            // No weapon selected — assign the closest-range weapon that can hit this target.
            const candidates = options.filter(o => o.targetShipId === ship.id);
            if (candidates.length) {
                const best = candidates.reduce((a, b) => a.range < b.range ? a : b);
                selectedShots.set(best.weaponIndex, ship.id);
                activeWeapon = best.weaponIndex;
                actionPanel.querySelectorAll('.weapon-chip').forEach(c =>
                    c.classList.toggle('active', parseInt(c.dataset.weapon, 10) === activeWeapon));
                refreshGunnery?.();
            }
        }
        return;
    }

    // Otherwise: show details.
    detailsShipId = ship.id;
    const full = state.ships.find(s => s.id === ship.id);
    if (full) showShipDetails(full);
};

// ────────────────────────────────────────────────
//  Hex click handling (phase-aware)
// ────────────────────────────────────────────────

grid.onHexClick = (q, r) => {
    if (!state || state.isComplete) return;

    if (state.phase === 'Helm') {
        const options = state.options?.items || [];
        const matches = options.filter(m => m.position.q === q && m.position.r === r);
        if (matches.length === 0) { setPendingMove(null); return; } // empty hex → clear preview

        if (matches.length === 1) {
            // Clicking the previewed hex again cancels it.
            const same = pendingMove && matches[0].finalFacing === pendingMove.option.finalFacing;
            setPendingMove(same ? null : matches[0]);
        } else {
            showMoveFacingPicker(matches);
        }
    }
};

// Shared facing picker: six buttons laid out as a hex ring, each in the direction it names.
// options: [{ facing, hold? }] — hold dims the button (a no-op choice). onPick(facing) fires on selection.
const FACING_NAMES = ['NW', 'NE', 'E', 'SE', 'SW', 'W'];
const FACING_RING_POS = [
    { left: 25, top: 13 },   // NW
    { left: 75, top: 13 },   // NE
    { left: 100, top: 50 },  // E
    { left: 75, top: 87 },   // SE
    { left: 25, top: 87 },   // SW
    { left: 0, top: 50 }     // W
];

function showFacingPicker(title, options, onPick, caption) {
    const overlay = document.createElement('div');
    overlay.className = 'facing-picker';
    overlay.innerHTML = `
        <div class="facing-picker-title">${title}</div>
        <div class="facing-ring">
            ${options.map(o => {
                const hold = o.hold ? ' hold' : '';
                return `<button class="btn btn-primary facing-btn${hold}" data-f="${o.facing}" style="left:${FACING_RING_POS[o.facing].left}%;top:${FACING_RING_POS[o.facing].top}%">${FACING_NAMES[o.facing]}</button>`;
            }).join('')}
        </div>
        ${caption ? `<div class="facing-caption">${caption}</div>` : ''}`;
    document.body.appendChild(overlay);

    overlay.querySelectorAll('.facing-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const facing = parseInt(btn.dataset.f, 10);
            overlay.remove();
            onPick(facing);
        });
    });

    // Click outside to dismiss
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) overlay.remove();
    });
}

function showMoveFacingPicker(matches) {
    showFacingPicker('Choose facing (preview, then confirm):',
        matches.map(m => ({ facing: m.finalFacing })),
        facing => setPendingMove(matches.find(m => m.finalFacing === facing)));
}

function showTurnInPlacePicker(ship) {
    showFacingPicker('Turn In Place (stunt):',
        FACING_NAMES.map((_, i) => ({ facing: i, hold: i === ship.facing })),
        facing => setPendingMove({
            position: { q: ship.q, r: ship.r },
            finalFacing: facing,
            isTurnInPlace: true
        }),
        'Dimmed = already facing (no stunt penalty)');
}

// ────────────────────────────────────────────────
//  Log
// ────────────────────────────────────────────────

function addLog(phase, message) {
    const entry = document.createElement('div');
    const cls = ['helm', 'gunnery', 'engineering', 'complete'].includes(phase.toLowerCase())
        ? phase.toLowerCase() : 'info';
    entry.className = `log-entry ${cls}`;
    entry.textContent = message;
    logEl.appendChild(entry);
    logEl.scrollTop = logEl.scrollHeight;
}

// ────────────────────────────────────────────────
//  Boot
// ────────────────────────────────────────────────

(function boot() {
    const path = window.location.pathname;
    // Support /{roomCode} URLs to join an existing game directly.
    const segments = path.split('/').filter(Boolean);
    if (segments.length === 1 && /^[A-Z0-9]{4,8}$/i.test(segments[0])) {
        roomCode = segments[0].toUpperCase();
        fetchState().catch(err => {
            addLog('error', `Could not load game ${roomCode}: ${err.message}`);
        });
    } else {
        newGame().catch(err => {
            addLog('error', `Could not start a game: ${err.message}. Is the server running?`);
        });
    }
})();
