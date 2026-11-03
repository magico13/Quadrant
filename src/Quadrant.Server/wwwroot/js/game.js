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
let selectedShots = new Map(); // gunnery: weaponName → targetShipId (one target per weapon)
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
    const res = await api('/start', { p1: 'Player 1', p2: 'Player 2' });
    roomCode = res.roomCode;
    selectedShots.clear();
    activeWeapon = null;
    detailsShipId = null;
    hideShipDetails();
    logEl.innerHTML = '';
    await fetchState();
}

document.getElementById('btn-new-game').addEventListener('click', () => {
    newGame().catch(err => addLog('error', `Failed to start game: ${err.message}`));
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
        <div class="actor-name p${actor.teamId + 1}">${actor.name}</div>
        ${pending
            ? `<div class="hint">Moving to (${pending.position.q}, ${pending.position.r}), facing ${facingNames[pending.finalFacing]}. Click another hex to change, or confirm.</div>
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
        await api(`/${roomCode}/move`, { q: m.position.q, r: m.position.r, facing: m.finalFacing });
        pendingMove = null;
        grid.setGhost(null);
        grid.setPath(null);
        await fetchState();
    } catch (err) {
        addLog('error', `Move failed: ${err.message}`);
    }
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

    // Group valid targets by weapon so we can show one chip per weapon.
    const weapons = new Map(); // weaponName → [options]
    for (const o of options) {
        if (!weapons.has(o.weaponName)) weapons.set(o.weaponName, []);
        weapons.get(o.weaponName).push(o);
    }

    const chips = [...weapons.entries()].map(([wname]) => {
        const w = actor.weapons?.find(w => w.name === wname);
        const dmg = w ? ` · ${w.damage}` : '';
        return `<button class="weapon-chip" data-weapon="${wname}">${wname}${dmg}</button>`;
    }).join('');

    actionPanel.innerHTML = `
        <div class="actor-name p${actor.teamId + 1}">${actor.name}</div>
        <div class="hint">Select a weapon, then click an enemy to target it. Each weapon fires once per turn.</div>
        <div class="weapon-chips">${chips || '<span class="hint">No weapons can fire.</span>'}</div>
        <div id="target-info" class="target-info"></div>
        <button class="btn btn-primary" id="btn-fire" disabled>Fire</button>
        <button class="btn btn-ghost" id="btn-skip-fire">Skip Firing</button>`;

    const fireBtn = document.getElementById('btn-fire');
    const targetInfo = document.getElementById('target-info');

    function refresh() {
        // Highlight the targets reachable by the active weapon.
        if (activeWeapon) {
            const opts = weapons.get(activeWeapon) || [];
            grid.setTargets(opts.map(o => [o.targetShipId, o.hitChancePercent]));
            targetInfo.innerHTML = opts.length
                ? `<span class="hint">Click a highlighted enemy to set ${activeWeapon}'s target.</span>`
                : `<span class="hint">${activeWeapon} has no targets in range.</span>`;
        } else {
            grid.setTargets([]);
            targetInfo.innerHTML = '<span class="hint">Select a weapon above.</span>';
        }

        // Show current assignments.
        const assigned = [...selectedShots.entries()];
        if (assigned.length) {
            targetInfo.innerHTML += `<div class="shot-list">${assigned.map(([w, tid]) => {
                const t = state.ships.find(s => s.id === tid);
                return `<span class="shot-item">${w} → ${t?.name ?? '?'}</span>`;
            }).join('')}</div>`;
        }

        fireBtn.disabled = assigned.length === 0;
    }

    refreshGunnery = refresh; // expose for the ship-click dispatcher

    actionPanel.querySelectorAll('.weapon-chip').forEach(chip => {
        chip.addEventListener('click', () => {
            activeWeapon = (activeWeapon === chip.dataset.weapon) ? null : chip.dataset.weapon;
            actionPanel.querySelectorAll('.weapon-chip').forEach(c =>
                c.classList.toggle('active', c.dataset.weapon === activeWeapon));
            refresh();
        });
    });

    refresh();

    document.getElementById('btn-fire').addEventListener('click', async () => {
        const shots = [...selectedShots.entries()].map(([weaponName, targetShipId]) => ({ weaponName, targetShipId }));
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

function showShipDetails(ship) {
    const shieldTotal = Object.values(ship.shields || {}).reduce((a, b) => a + b, 0);
    const shieldMax = (ship.maxShield ?? 0) * 4;
    const arcs = ['forward', 'starboard', 'aft', 'port'];

    const weaponsHtml = (ship.weapons || []).map(w => `
        <div class="detail-weapon">
            <span class="dw-name">${w.name}</span>
            <span class="dw-meta">${w.arc} · ${w.minRange}–${w.maxRange} · ${w.damage}${w.type ? ` · ${w.type}` : ''}</span>
        </div>`).join('');

    const systemsHtml = (ship.damagedSystems || []).length
        ? `<div class="detail-section">Damaged Systems</div><ul class="detail-list">${ship.damagedSystems.map(s => `<li>${s.system} — ${s.condition}</li>`).join('')}</ul>`
        : '';

    shipDetailsEl.innerHTML = `
        <div class="sd-header p${ship.teamId + 1}">
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
        hideShipDetails();
    });
}

function hideShipDetails() {
    shipDetailsEl.classList.add('hidden');
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

    // During Gunnery with a weapon selected, clicking an enemy targets it.
    if (state.phase === 'Gunnery' && activeWeapon) {
        const options = state.options?.items || [];
        const opt = options.find(o => o.weaponName === activeWeapon && o.targetShipId === ship.id);
        if (opt) {
            selectedShots.set(activeWeapon, ship.id);
            refreshGunnery?.(); // update chips/target-info without resetting selection
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
            showFacingPicker(matches);
        }
    }
};

function showFacingPicker(matches) {
    const names = ['NW', 'NE', 'E', 'SE', 'SW', 'W'];
    const overlay = document.createElement('div');
    overlay.className = 'facing-picker';
    overlay.innerHTML = `
        <div class="facing-picker-title">Choose facing (preview, then confirm):</div>
        ${matches.map((m, i) => `<button class="btn btn-primary facing-btn" data-idx="${i}">${names[m.finalFacing]}</button>`).join('')}`;
    document.body.appendChild(overlay);

    overlay.querySelectorAll('.facing-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const idx = parseInt(btn.dataset.idx, 10);
            overlay.remove();
            setPendingMove(matches[idx]);
        });
    });

    // Click outside to dismiss
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) overlay.remove();
    });
}

async function showTurnInPlacePicker(ship) {
    const names = ['NW', 'NE', 'E', 'SE', 'SW', 'W'];
    const overlay = document.createElement('div');
    overlay.className = 'facing-picker';
    overlay.innerHTML = `
        <div class="facing-picker-title">Turn In Place (stunt):</div>
        ${names.map((n, i) => `<button class="btn btn-primary facing-btn" data-f="${i}">${n}</button>`).join('')}`;
    document.body.appendChild(overlay);

    overlay.querySelectorAll('.facing-btn').forEach(btn => {
        btn.addEventListener('click', async () => {
            const facing = parseInt(btn.dataset.f, 10);
            overlay.remove();
            try {
                await api(`/${roomCode}/turn-in-place`, { facing });
                await fetchState();
            } catch (err) {
                addLog('error', err.message);
            }
        });
    });

    // Click outside to dismiss
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) overlay.remove();
    });
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

newGame().catch(err => {
    addLog('error', `Could not start a game: ${err.message}. Is the server running?`);
});
