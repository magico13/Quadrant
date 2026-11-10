// Quadrant client bootstrap: shared state, REST calls, phase routing, and map event dispatch.
// Phase renderers and ship UI live in the sibling game-*.js classic scripts.

const canvas = document.getElementById('hex-canvas');
const grid = new HexGrid(canvas);

const logEl = document.getElementById('log');
const actionPanel = document.getElementById('action-panel');
const phaseEl = document.getElementById('phase-indicator');
const roundEl = document.getElementById('round-indicator');
const roomEl = document.getElementById('room-code');
const shipDetailsEl = document.getElementById('ship-details');
const systemTargetDialog = document.getElementById('system-target-dialog');
const balanceDialog = document.getElementById('balance-dialog');
const divertDialog = document.getElementById('divert-dialog');
const damageReportEl = document.getElementById('damage-report');
let damageReportOpen = false;
let reportShipId = null;

const tooltipEl = document.getElementById('ship-tooltip');

let roomCode = null;
let state = null;          // last fetched game state
let selectedShots = new Map(); // gunnery: weaponIndex → targetShipId (one target per weapon instance)
let activeWeapon = null;   // currently selected weapon in gunnery targeting
let selectedTargetArcs = new Map();
let detailsShipId = null;  // ship currently shown in the details panel
let lastActiveShipId = null;
let pendingMove = null;    // helm: { option, actorId } — move previewed, awaiting confirmation
let pendingSystemTarget = null; // Local preview; popup confirmation spends an action.
let pendingShieldBalance = null; // Local shield transfer preview until confirmed.
let pendingDivert = null; // Local Divert selection until confirmed.
let refreshGunnery = null; // module-scope handle to the current gunnery refresh fn
let renderedLogCount = 0;

function formatArc(arc) {
    return arc === 'AllArcs' ? 'Turret' : arc;
}

function assignGunneryTarget(weaponIndex, targetId) {
    const fired = new Set(state.firedWeaponIndices || []);
    const limit = currentActor()?.crewShotsRemaining ?? 0;
    const pending = [...selectedShots.keys()].filter(i => !fired.has(i) && i !== weaponIndex);
    while (pending.length >= limit && pending.length) selectedShots.delete(pending.shift());
    if (limit > 0) selectedShots.set(weaponIndex, targetId);
}

function formatTier(tier) {
    if (Math.abs(tier - 1 / 3) < 0.01) return '1/3';
    if (tier === 0.5) return '1/2';
    if (tier === 0.25) return '1/4';
    return String(tier);
}

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
    // The API sends the full persisted battle log; append only entries the
    // current page has not rendered yet.
    if (state.log.length < renderedLogCount) {
        logEl.innerHTML = '';
        renderedLogCount = 0;
    }
    for (const e of state.log.slice(renderedLogCount)) addLog(e.phase, e.message);
    renderedLogCount = state.log.length;
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
    clearSystemTargeting();
    clearShieldBalancing();
    selectedShots.clear();
    selectedTargetArcs.clear();
    activeWeapon = null;
    detailsShipId = null;
    lastActiveShipId = null;
    hideShipDetails();
    damageReportOpen = false;
    reportShipId = null;
    damageReportEl.classList.add('hidden');
    logEl.innerHTML = '';
    renderedLogCount = 0;
    hideSetup();
    history.replaceState(null, '', `/${roomCode}`);
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
    if (pendingSystemTarget && !canContinueSystemTargeting()) clearSystemTargeting();
    if (pendingShieldBalance && !canContinueShieldBalancing()) clearShieldBalancing();
    if (pendingDivert && !canContinueDivert()) clearDivert();

    phaseEl.textContent = state.phase === 'CrewSetup' ? 'Crew Setup' : state.phase;
    roundEl.textContent = `Round ${state.round}`;
    roomEl.textContent = roomCode || '';

    if (state.currentActorId) {
        if (detailsShipId && lastActiveShipId && lastActiveShipId !== state.currentActorId)
            detailsShipId = state.currentActorId;
        if (damageReportOpen && lastActiveShipId && lastActiveShipId !== state.currentActorId)
            reportShipId = state.currentActorId;
        lastActiveShipId = state.currentActorId;
    }

    // Feed ships to the grid.
    const ships = state.ships.map(s => ({
        id: s.id, name: s.name, teamId: s.teamId, q: s.q, r: s.r, facing: s.facing,
        hull: s.hull, maxHull: s.maxHull, shields: s.shields, maxShield: s.maxShield,
        isDisabled: s.isDisabled, isDestroyed: s.isDestroyed,
        barrelRolledThisRound: s.barrelRolledThisRound,
        isActive: s.id === state.currentActorId && ['Helm', 'Gunnery'].includes(state.phase),
    }));
    grid.setShips(ships);
    grid.setProjectiles(state.projectiles || []);

    renderRoster();

    renderDamageReport();
    if (state.pendingPointDefense) {
        renderPointDefense(state.pendingPointDefense);
        return;
    }

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
        case 'CrewSetup': renderRoles(); break;
        case 'Engineering':
            renderEngineering();
            if (pendingDivert) renderDivertDialog();
            break;
        case 'Helm': renderHelm(); break;
        case 'Gunnery': renderGunnery(); break;
        default: renderComplete();
    }
}

function renderPointDefense(prompt) {
    grid.setHighlights([]);
    grid.setTargets([]);
    grid.setGhost(null);
    grid.setPath(null);
    const defender = state.ships.find(ship => ship.id === prompt.targetShipId);
    const missiles = prompt.missiles || [];
    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(defender?.teamId ?? 0)}">Point Defense: ${prompt.targetName}</div>
        <div class="hint">${missiles.length > 1
            ? `Multiple missiles are reaching the ${prompt.arc} arc. Choose which to defend, or pass on one. If only one remains, its available Point weapons will fire automatically.`
            : `A missile is reaching the ${prompt.arc} arc. Its available Point weapons will fire automatically.`}</div>
        <div class="weapon-chips">${missiles.map(missile => `
            <div class="target-info">
                <strong>${missile.name}</strong> · speed ${missile.speed}
                <div class="hint">Point weapons: ${missile.weapons.map(weapon => `${weapon.name} (+${weapon.bonus})`).join(', ')}</div>
                ${missiles.length > 1 ? `<button class="btn btn-primary" data-point-defense="${missile.projectileId}" data-attempt="true">Defend this missile</button>
                <button class="btn btn-ghost" data-point-defense="${missile.projectileId}" data-attempt="false">Pass on this missile</button>` : ''}
            </div>`).join('')}</div>`;
    if (missiles.length === 1) {
        const missile = missiles[0];
        actionPanel.querySelectorAll('[data-point-defense]').forEach(button => button.remove());
        const controls = document.createElement('div');
        controls.innerHTML = `<button class="btn btn-primary" id="btn-point-defense">Resolve Point Defense</button>`;
        actionPanel.appendChild(controls);
        document.getElementById('btn-point-defense').addEventListener('click', () => resolvePointDefense(missile.projectileId, true));
    } else {
        actionPanel.querySelectorAll('[data-point-defense]').forEach(button => button.addEventListener('click', () =>
            resolvePointDefense(button.dataset.pointDefense, button.dataset.attempt === 'true')));
    }
}

async function resolvePointDefense(projectileId, attempt) {
    try {
        await api(`/${roomCode}/point-defense`, { projectileId, attempt });
        await fetchState();
    } catch (err) { addLog('error', `Point defense failed: ${err.message}`); }
}

function currentActor() {
    return state.ships.find(s => s.id === state.currentActorId);
}

document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && systemTargetDialog.open) return;
    if (event.key === 'Escape' && balanceDialog.open) return;
    if (event.key === 'Escape' && divertDialog.open) return;
    if (event.key === 'Escape' && pendingSystemTarget && !pendingSystemTarget.submitting) {
        event.preventDefault();
        clearSystemTargeting();
        renderHelm();
        return;
    }
    if (event.key === 'Escape' && pendingShieldBalance && !pendingShieldBalance.submitting) {
        event.preventDefault();
        clearShieldBalancing();
        renderHelm();
        return;
    }
    if (event.key !== 'Escape' || shipDetailsEl.classList.contains('hidden') || damageReportOpen) return;
    shipDetailsEl.querySelector('.sd-close')?.click();
});

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
    if (!state) return;
    if (pendingSystemTarget) {
        chooseSystemTarget(state.ships.find(s => s.id === ship.id) || ship);
        return;
    }
    detailsShipId = ship.id;
    const selected = state.ships.find(s => s.id === ship.id);
    if (selected) showShipDetails(selected);
    if (state.isComplete) return;
    if (state.freeAttack) return;

    // During Helm, clicking your own ship offers the Turn In Place stunt.
    if (state.phase === 'Helm' && ship.id === state.currentActorId) {
        if (state.pendingStunt) {
            const option = (state.options?.items || []).find(m => m.position.q === ship.q && m.position.r === ship.r);
            if (option) setPendingMove(option);
        } else if (currentActor()?.canTurnInPlace) showTurnInPlacePicker(ship);
        return;
    }

    // During Gunnery, clicking an enemy retargets a weapon at it.
    if (state.phase === 'Gunnery') {
        const options = state.options?.items || [];
        if (activeWeapon !== null) {
            // Specific weapon selected — use it if it can reach this target.
            const opt = options.find(o => o.weaponIndex === activeWeapon && o.targetShipId === ship.id);
            if (opt) {
                if (opt.arcChoiceRequired && !selectedTargetArcs.has(ship.id)) {
                    const choices = opt.arcChoices || [];
                    if (choices.length === 1) selectedTargetArcs.set(ship.id, choices[0]);
                }
                assignGunneryTarget(activeWeapon, ship.id);
                refreshGunnery?.();
            }
        } else {
            // No weapon selected — assign the closest-range weapon that can hit this target.
            const candidates = options.filter(o => o.targetShipId === ship.id);
            if (candidates.length) {
                const boundary = candidates.find(o => o.arcChoiceRequired);
                if (boundary && !selectedTargetArcs.has(ship.id) && boundary.arcChoices.length > 1) {
                    activeWeapon = boundary.weaponIndex;
                    refreshGunnery?.();
                    return;
                }
                const legal = selectedTargetArcs.has(ship.id)
                    ? candidates.filter(o => !o.arcChoiceRequired || o.arc === selectedTargetArcs.get(ship.id))
                    : candidates;
                const best = legal.reduce((a, b) => a.range < b.range ? a : b);
                assignGunneryTarget(best.weaponIndex, ship.id);
                activeWeapon = best.weaponIndex;
                actionPanel.querySelectorAll('.weapon-chip').forEach(c =>
                    c.classList.toggle('active', parseInt(c.dataset.weapon, 10) === activeWeapon));
                refreshGunnery?.();
            }
        }
        return;
    }

};

// ────────────────────────────────────────────────
//  Hex click handling (phase-aware)
// ────────────────────────────────────────────────

grid.onHexClick = (q, r) => {
    if (!state || state.isComplete) return;
    if (pendingSystemTarget) return;
    if (state.freeAttack) return;

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
