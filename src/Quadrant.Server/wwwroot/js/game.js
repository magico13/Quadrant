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
    const grouped = new Map();
    for (const ship of [...catalog].sort((a, b) =>
        (a.family || 'Other').localeCompare(b.family || 'Other') ||
        a.tier - b.tier || a.name.localeCompare(b.name))) {
        const family = ship.family || 'Other';
        if (!grouped.has(family)) grouped.set(family, []);
        grouped.get(family).push(ship);
    }
    catalogListEl.innerHTML = [...grouped].map(([family, ships]) => `
        <h4 class="catalog-family">${family}</h4>
        ${ships.map(s => `
        <div class="catalog-card">
            <div class="cc-top">
                <span class="cc-name">${s.name}</span>
                <span class="cc-tier">Tier ${formatTier(s.tier)}</span>
            </div>
            <div class="cc-stats">SPD ${s.speed} · HP ${s.hullPoints} · AC ${s.ac} · ${s.maneuverability}</div>
            <div class="cc-weapons">${s.weapons.map(w => `${w.name} (${formatArc(w.arc)})`).join(', ') || 'no weapons'}</div>
            <div class="cc-add">
                ${fleet.map((_, team) => `<button data-team="${team}" data-ship="${s.id}" style="color:${teamColor(team)}">+ Team ${team + 1}</button>`).join('')}
            </div>
        </div>`).join('')}`).join('');

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

// ── Ship Roster ──
const rosterEl = document.getElementById('ship-roster');

function renderRoster() {
    if (!state || !state.ships) { rosterEl.innerHTML = ''; return; }
    const isCombat = ['CrewSetup', 'Engineering', 'Helm', 'Gunnery'].includes(state.phase);
    const entries = [
        ...state.ships.map(s => ({ ...s, entryType: 'ship' })),
        ...(state.projectiles || []).map(p => ({ ...p, entryType: 'projectile' }))
    ];
    rosterEl.innerHTML = entries.map(s => {
        if (s.entryType === 'projectile') {
            return `<div class="roster-entry roster-projectile" style="--team-color:${teamColor(s.teamId)}" data-projectile="${s.id}">
                <span class="roster-dot"></span>
                <span class="roster-name">${s.name} <small>(Team ${s.teamId + 1} · in flight)</small></span>
            </div>`;
        }
        const active = s.id === state.currentActorId ? ' active' : '';
        const selected = s.id === detailsShipId ? ' selected' : '';
        const inactive = s.isDisabled || s.isDestroyed ? ' inactive' : '';
        const hp = isCombat ? ` <span class="roster-hp">${s.hull}/${s.maxHull}</span>` : '';
        return `<div class="roster-entry${active}${selected}${inactive}" style="--team-color:${teamColor(s.teamId)}" data-ship="${s.id}">
            <span class="roster-dot"></span>
            <span class="roster-name"><span class="roster-ship-name${s.barrelRolledThisRound ? ' barrel-rolled' : ''}">${s.name}</span> <small>(Team ${s.teamId + 1})</small></span>${hp}
        </div>`;
    }).join('');

    rosterEl.querySelectorAll('.roster-entry').forEach(el => {
        el.addEventListener('click', () => {
            const ship = state.ships.find(s => s.id === el.dataset.ship);
            if (ship && pendingSystemTarget) { chooseSystemTarget(ship); return; }
            if (ship) { detailsShipId = ship.id; showShipDetails(ship); grid.centerOn(ship.q, ship.r); }
            const projectile = (state.projectiles || []).find(p => p.id === el.dataset.projectile);
            if (projectile) grid.centerOn(projectile.q, projectile.r);
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

// ── Engineering ──
function renderRoles() {
    grid.setHighlights([]);
    grid.setTargets([]);
    const roles = ['Captain', 'Engineer', 'Gunner', 'Pilot', 'Science Officer'];
    const active = state.ships.filter(s => !s.isDestroyed && !s.isDisabled);
    actionPanel.innerHTML = `
        <div class="actor-name">Crew Roles</div>
        <div class="hint">Choose roles for this round. Previous assignments are kept until changed.</div>
        <div class="crew-roles-list">
        ${active.map(ship => `<div class="detail-section" style="color:${teamColor(ship.teamId)}">${ship.name} (Team ${ship.teamId + 1})</div>
            ${(ship.crew || []).map((crew, index) => `<div class="eng-row">
                <label for="role-${ship.id}-${index}">${crew.primaryRole} ${index + 1} · ${crew.ranks} ranks</label>
                <select id="role-${ship.id}-${index}" data-role-ship="${ship.id}">
                    ${roles.map(role => `<option value="${role}" ${crew.role === role ? 'selected' : ''}>${role}</option>`).join('')}
                </select>
            </div>`).join('') || '<div class="hint">No crew recorded.</div>'}`).join('')}
        </div>
        <div id="roles-error" class="hint"></div>
        <button class="btn btn-primary" id="btn-confirm-roles">Confirm Roles</button>`;
    document.getElementById('btn-confirm-roles').addEventListener('click', async () => {
        const ships = active.map(ship => ({ shipId: ship.id, roles: [...actionPanel.querySelectorAll(`[data-role-ship="${ship.id}"]`)].map(select => select.value) }));
        try {
            await api(`/${roomCode}/roles`, { ships });
            await fetchState();
        } catch (err) {
            document.getElementById('roles-error').textContent = `Could not save roles: ${err.message}`;
        }
    });
}

function renderEngineering() {
    grid.setHighlights([]);
    grid.setTargets([]);
    const ships = state.ships.filter(s => !s.isDestroyed && !s.isDisabled);
    actionPanel.innerHTML = `
        <div class="actor-name">Engineering</div>
        <div class="hint">Each engineer can take one action. Divert and Hold It Together can each be attempted once per ship this round.</div>
        ${ships.map(ship => {
            const engineers = (ship.crew || []).map((crew, i) => ({ ...crew, i })).filter(c => c.role === 'Engineer' && !c.actionUsed);
            const used = new Set(ship.engineeringActionsUsed || []);
            const options = [];
            if (!used.has('Hold It Together')) for (const system of (ship.systems || []).filter(system => system.system !== 'WeaponsArray' && system.condition !== 'None'))
                options.push(`<option value="Hold It Together|${system.system}">Hold It Together — ${system.system} (${system.condition})</option>`);
            if (!used.has('Hold It Together')) for (const arc of (ship.weaponArcs || []).filter(arc => arc.condition !== 'None'))
                options.push(`<option value="Hold It Together|WeaponsArray|${arc.installedArc}">Hold It Together — ${arc.arc} weapons (${arc.condition})</option>`);
            return `<div class="detail-section" style="color:${teamColor(ship.teamId)}">${ship.name} (Team ${ship.teamId + 1})</div>
                ${engineers.length ? `<div class="eng-row"><label>Engineer</label><select id="engineer-${ship.id}">${engineers.map(c => `<option value="${c.i}">${c.primaryRole} ${c.i + 1} (${c.roleBonus >= 0 ? '+' : ''}${c.roleBonus})</option>`).join('')}</select></div>
                ${!used.has('Divert') ? `<button class="btn btn-ghost" data-divert-ship="${ship.id}">Divert Power</button>` : ''}
                ${options.length ? `<div class="eng-row"><label>Hold action</label><select id="engineering-action-${ship.id}">${options.join('')}</select></div>
                    <button class="btn btn-ghost" data-engineering-ship="${ship.id}">Attempt Hold It Together</button>
                    <div class="hint" id="engineering-error-${ship.id}"></div>` : ''}
                ${used.has('Divert') && !options.length ? '<div class="hint">No available engineer action.</div>' : ''}` : '<div class="hint">No available engineer action.</div>'}`;
        }).join('')}
        <button class="btn btn-primary" id="btn-end-eng">End Engineering</button>`;
    for (const ship of ships) {
        const select = document.getElementById(`engineering-action-${ship.id}`);
        const memoryKey = `quadrant:${roomCode}:engineering:${ship.id}`;
        const engineerSelect = document.getElementById(`engineer-${ship.id}`);
        if (!engineerSelect) continue;
        const remembered = localStorage.getItem(memoryKey);
        if (select && remembered && [...select.options].some(option => option.value === remembered)) select.value = remembered;
        const holdButton = actionPanel.querySelector(`[data-engineering-ship="${ship.id}"]`);
        const divertButton = actionPanel.querySelector(`[data-divert-ship="${ship.id}"]`);
        const update = () => {
            const crew = ship.crew[Number(engineerSelect.value)];
            if (holdButton) holdButton.textContent = `Attempt Hold It Together (${crew.engineeringChances.hold}% success)`;
            if (divertButton) divertButton.textContent = `Divert Power (${crew.engineeringChances.divert}% success)`;
        };
        select?.addEventListener('change', () => localStorage.setItem(memoryKey, select.value));
        engineerSelect.addEventListener('change', update);
        update();
        divertButton?.addEventListener('click', () => startDivert(ship.id, Number(engineerSelect.value)));
        holdButton?.addEventListener('click', async () => {
            const [action, target, weaponArc] = select.value.split('|');
            try {
                await api(`/${roomCode}/engineering`, { shipId: ship.id, crewIndex: Number(engineerSelect.value), action, target, weaponArc });
                await fetchState();
            } catch (err) { document.getElementById(`engineering-error-${ship.id}`).textContent = `Could not attempt action: ${err.message}`; }
        });
    }
    document.getElementById('btn-end-eng').addEventListener('click', async () => {
        await api(`/${roomCode}/end-engineering`);
        await fetchState();
    });
}

const DIVERT_ARCS = ['forward', 'starboard', 'aft', 'port'];

function divertShip() {
    return pendingDivert ? state?.ships.find(ship => ship.id === pendingDivert.shipId) : null;
}

function canContinueDivert() {
    const pending = pendingDivert;
    const ship = divertShip();
    const engineer = ship?.crew?.[pending?.crewIndex];
    return !!pending && !!ship && !!engineer && state.phase === 'Engineering' && !state.isComplete &&
        state.round === pending.round && !ship.isDestroyed && !ship.isDisabled && engineer.role === 'Engineer' &&
        !engineer.actionUsed && !(ship.engineeringActionsUsed || []).includes('Divert');
}

function clearDivert() {
    pendingDivert = null;
    if (divertDialog.open) divertDialog.close();
}

function startDivert(shipId, crewIndex) {
    const ship = state?.ships.find(candidate => candidate.id === shipId);
    if (state?.phase !== 'Engineering' || !ship || ship.isDestroyed || ship.isDisabled ||
        ship.crew[crewIndex]?.role !== 'Engineer' || ship.crew[crewIndex]?.actionUsed ||
        (ship.engineeringActionsUsed || []).includes('Divert')) return;
    pendingDivert = { shipId, crewIndex, round: state.round, target: null, selectedShieldArcs: [],
        shields: Object.fromEntries(DIVERT_ARCS.map(arc => [arc, 0])), submitting: false, error: '' };
    detailsShipId = null;
    hideShipDetails();
    renderDivertDialog();
}

function splitDivertShieldPoints(ship, selectedArcs) {
    const allocation = Object.fromEntries(DIVERT_ARCS.map(arc => [arc, 0]));
    let remaining = Math.max(0, ship.shieldDivertMax ?? 0);
    const capacities = Object.fromEntries(DIVERT_ARCS.map(arc => [arc, remaining]));
    while (remaining > 0) {
        const eligible = DIVERT_ARCS.filter(arc => selectedArcs.includes(arc) && allocation[arc] < capacities[arc]);
        if (!eligible.length) break;
        eligible.sort((a, b) => allocation[a] - allocation[b] || DIVERT_ARCS.indexOf(a) - DIVERT_ARCS.indexOf(b));
        allocation[eligible[0]]++;
        remaining--;
    }
    return allocation;
}

function adjustDivertShieldPoints(ship, selectedArcs, arc, requested) {
    const allocation = Object.fromEntries(DIVERT_ARCS.map(candidate => [candidate, 0]));
    const total = Math.max(0, ship.shieldDivertMax ?? 0);
    const capacities = Object.fromEntries(DIVERT_ARCS.map(candidate => [candidate, total]));
    allocation[arc] = Math.min(total, Math.max(0, Math.floor(requested)));
    let remaining = total - allocation[arc];
    while (remaining > 0) {
        const eligible = DIVERT_ARCS.filter(candidate => candidate !== arc && selectedArcs.includes(candidate) && allocation[candidate] < capacities[candidate]);
        if (!eligible.length) break;
        eligible.sort((a, b) => allocation[a] - allocation[b] || DIVERT_ARCS.indexOf(a) - DIVERT_ARCS.indexOf(b));
        allocation[eligible[0]]++;
        remaining--;
    }
    return allocation;
}

function renderDivertDialog() {
    if (!canContinueDivert()) {
        clearDivert();
        renderEngineering();
        return;
    }
    const pending = pendingDivert;
    const ship = divertShip();
    const engineer = ship.crew[pending.crewIndex];
    const chance = engineer.engineeringChances?.divert ?? null;
    const shieldMax = ship.shieldDivertMax ?? 0;
    const assigned = DIVERT_ARCS.reduce((sum, arc) => sum + (pending.shields[arc] || 0), 0);
    const allocationValid = pending.target !== 'Shields' ||
        (pending.selectedShieldArcs.length > 0 && assigned === shieldMax && DIVERT_ARCS.every(arc => pending.shields[arc] >= 0));
    divertDialog.innerHTML = `<div class="sd-header"><span class="sd-name" id="divert-title">Divert Power · ${reportEscape(ship.name)}</span><button class="btn btn-ghost sd-close" id="btn-close-divert" aria-label="Close Divert dialog" ${pending.submitting ? 'disabled' : ''}>×</button></div>
        ${ShipSchematic.render(ship, { mode: 'divert', selectedTarget: pending.target, selectedShieldArcs: pending.selectedShieldArcs,
            shieldAllocations: pending.shields, shieldDivertMax: shieldMax, allocatedShieldPoints: assigned })}
        <div class="system-picker-actions divert-dialog-actions"><button class="btn btn-primary" id="btn-confirm-divert" ${pending.submitting || !pending.target || !allocationValid ? 'disabled' : ''}>${pending.submitting ? 'Attempting…' : `Confirm Divert${chance != null ? ` (${chance}%)` : ''}`}</button><button class="btn btn-ghost" id="btn-cancel-divert" ${pending.submitting ? 'disabled' : ''}>Cancel</button></div>
        <div class="hint" id="divert-error" role="status">${reportEscape(pending.error)}</div>`;
    ShipSchematic.bind(divertDialog, ship, {
        mode: 'divert', selectedTarget: pending.target,
        shieldDivertMax: shieldMax, allocatedShieldPoints: assigned,
        onTargetSelect: target => {
            pending.target = target;
            pending.selectedShieldArcs = [];
            pending.shields = Object.fromEntries(DIVERT_ARCS.map(arc => [arc, 0]));
            pending.error = '';
            renderDivertDialog();
        },
        onShieldToggle: arc => {
            const selected = new Set(pending.selectedShieldArcs);
            if (pending.target === 'Shields' && selected.has(arc)) selected.delete(arc);
            else selected.add(arc);
            pending.selectedShieldArcs = DIVERT_ARCS.filter(candidate => selected.has(candidate));
            pending.target = pending.selectedShieldArcs.length ? 'Shields' : null;
            pending.shields = splitDivertShieldPoints(ship, pending.selectedShieldArcs);
            pending.error = '';
            renderDivertDialog();
        },
        onShieldAmount: (arc, value) => {
            pending.shields = adjustDivertShieldPoints(ship, pending.selectedShieldArcs, arc, value);
            pending.error = '';
            renderDivertDialog();
        }
    });
    document.getElementById('btn-confirm-divert').addEventListener('click', confirmDivert);
    document.getElementById('btn-close-divert').addEventListener('click', () => { clearDivert(); renderEngineering(); });
    document.getElementById('btn-cancel-divert').addEventListener('click', () => { clearDivert(); renderEngineering(); });
    divertDialog.oncancel = event => {
        if (pendingDivert?.submitting) { event.preventDefault(); return; }
        clearDivert();
        renderEngineering();
    };
    if (!divertDialog.open) divertDialog.showModal();
}

async function confirmDivert() {
    if (!canContinueDivert() || pendingDivert.submitting) return;
    const pending = pendingDivert;
    if (!pending.target) return;
    const shields = pending.target === 'Shields' ? Object.fromEntries(DIVERT_ARCS.map(arc => [arc, pending.shields[arc] || 0])) : null;
    if (pending.target === 'Shields' && DIVERT_ARCS.reduce((sum, arc) => sum + shields[arc], 0) !== (divertShip()?.shieldDivertMax ?? 0)) return;
    pending.submitting = true;
    pending.error = '';
    renderDivertDialog();
    try {
        await api(`/${roomCode}/engineering`, { shipId: pending.shipId, crewIndex: pending.crewIndex, action: 'Divert', target: pending.target, shields });
    } catch (err) {
        pending.submitting = false;
        pending.error = `Could not divert power: ${err.message}`;
        renderDivertDialog();
        return;
    }
    clearDivert();
    try { await fetchState(); }
    catch (err) { renderEngineering(); addLog('error', `Divert was submitted, but state could not refresh: ${err.message}`); }
}

// Target System previews are local; only confirmation in the system popup rolls.
function systemTargetLabel(system) {
    return { PowerCore: 'Core', Engines: 'Engines', LifeSupport: 'Life Support', Sensors: 'Sensors', WeaponsArray: 'Weapons' }[system] || system;
}

function availableScienceOfficers(ship) {
    return (ship?.crew || []).map((crew, index) => ({ ...crew, index }))
        .filter(crew => crew.role === 'Science Officer' && !crew.actionUsed);
}

function targetSystemUnavailableReason(ship) {
    if (!ship.hasSensors) return 'Target System requires sensors.';
    const sensors = (ship.systems || []).find(s => s.system === 'Sensors');
    if (['Malfunctioning', 'Wrecked'].includes(sensors?.effectiveCondition ?? sensors?.condition))
        return 'Target System is a push action and requires functioning sensors.';
    return '';
}

function systemTargetEnemies(ship) {
    const range = ship.sensorRangeHexes || 0;
    return state.ships.filter(target => target.teamId !== ship.teamId && !target.isDestroyed &&
        !(ship.targetedSystems || []).some(lock => lock.targetShipId === target.id) &&
        (!range || Hex.distance(ship, target) <= range * 5));
}

function canContinueSystemTargeting() {
    const pending = pendingSystemTarget;
    const actor = currentActor();
    return pending && actor && state.phase === 'Helm' && !state.freeAttack && !state.isComplete &&
        actor.id === pending.shipId && state.round === pending.round && !actor.isDisabled && !actor.isDestroyed &&
        !targetSystemUnavailableReason(actor) && availableScienceOfficers(actor).some(c => c.index === pending.crewIndex);
}

function balanceSensorsWrecked(ship) {
    const sensors = (ship.systems || []).find(s => s.system === 'Sensors');
    return ['Wrecked'].includes(sensors?.effectiveCondition ?? sensors?.condition);
}

function maxShieldTransfer(ship, sourceQuadrant) {
    const total = QUADRANTS.reduce((sum, q) => sum + Math.max(0, ship.shields[q.toLowerCase()] ?? 0), 0);
    const minimum = Math.ceil(total / 10);
    return Math.max(0, (ship.shields[sourceQuadrant.toLowerCase()] ?? 0) - minimum);
}

function canContinueShieldBalancing() {
    const pending = pendingShieldBalance;
    const actor = currentActor();
    return pending && actor && state.phase === 'Helm' && !state.freeAttack && !state.isComplete &&
        actor.id === pending.shipId && state.round === pending.round && !actor.isDisabled && !actor.isDestroyed &&
        !actor.crew[pending.crewIndex]?.actionUsed && !balanceSensorsWrecked(actor);
}

function clearSystemTargeting() {
    pendingSystemTarget = null;
    if (systemTargetDialog.open) systemTargetDialog.close();
    grid.setTargets([]);
    grid.setSelectedGunneryTarget(null, null);
}

function clearShieldBalancing() {
    pendingShieldBalance = null;
    if (balanceDialog.open) balanceDialog.close();
    grid.setSelectedGunneryTarget(null, null);
}

// ── Balance Shields ──
const QUADRANTS = ['Forward', 'Starboard', 'Aft', 'Port'];

// Update the Balance Shields button label with the selected officer's success chance.
function updateChance() {
    const actor = currentActor();
    const crewSelect = document.getElementById('science-crew');
    const button = document.getElementById('btn-balance-shields');
    if (!actor || !crewSelect || !button) return;
    const chance = actor.crew[Number(crewSelect.value)]?.scienceChances?.balance ?? null;
    button.textContent = `Balance Shields${chance != null ? ` (${chance}%)` : ''}`;
}

function startShieldBalancing(crewIndex) {
    const actor = currentActor();
    if (!actor || state.phase !== 'Helm' || state.freeAttack || balanceSensorsWrecked(actor) ||
        !availableScienceOfficers(actor).some(c => c.index === crewIndex)) return;
    pendingShieldBalance = { shipId: actor.id, crewIndex, round: state.round, sourceQuadrant: null, destinationQuadrant: null, amount: 0, balanceEvenly: false, submitting: false, error: '' };
    detailsShipId = null;
    pinnedWeapon = null;
    clearWeaponRange();
    hideShipDetails();
    renderHelm();
}

function shieldBalancePreview(ship) {
    const pending = pendingShieldBalance;
    const shields = { ...ship.shields };
    if (pending.balanceEvenly) {
        const total = QUADRANTS.reduce((sum, q) => sum + Math.max(0, ship.shields[q.toLowerCase()] ?? 0), 0);
        const each = Math.floor(total / 4);
        const remainder = total % 4;
        QUADRANTS.forEach((q, i) => { shields[q.toLowerCase()] = each + (i === 0 ? remainder : 0); });
    } else if (pending.sourceQuadrant && pending.destinationQuadrant) {
        const src = pending.sourceQuadrant.toLowerCase();
        const dst = pending.destinationQuadrant.toLowerCase();
        const current = Math.max(0, ship.shields[src] ?? 0);
        const amount = Math.min(current, Math.max(0, pending.amount));
        shields[src] = current - amount;
        shields[dst] = (ship.shields[dst] ?? 0) + amount;
    }
    return shields;
}

function renderShieldBalancing() {
    if (!canContinueShieldBalancing()) { clearShieldBalancing(); renderHelm(); return; }
    const actor = currentActor();
    const pending = pendingShieldBalance;
    const crew = actor.crew[pending.crewIndex];
    const chance = crew?.scienceChances?.balance ?? null;
    const preview = shieldBalancePreview(actor);
    grid.setHighlights([]);
    grid.setGhost(null);
    grid.setPath(null);
    grid.setRangeHexes([]);
    grid.setTargets([]);
    grid.setSelectedGunneryTarget(actor.id, actor.id);
    actionPanel.innerHTML = `<div class="actor-name" style="color:${teamColor(actor.teamId)}">${reportEscape(actor.name)}</div>
        <div class="detail-section">Balance Shields</div>
        <div class="hint">Officer ${pending.crewIndex + 1} · Choose arcs in the dialog, then confirm.</div>`;
    openBalanceDialog(actor, preview);
}

function openBalanceDialog(ship, preview) {
    const pending = pendingShieldBalance;
    const chance = ship.crew[pending.crewIndex]?.scienceChances?.balance ?? null;
    const evenPreview = pending.balanceEvenly ? Object.fromEntries(QUADRANTS.map(q => [q.toLowerCase(), preview[q.toLowerCase()]])) : null;
    const maxTransfer = pending.sourceQuadrant ? maxShieldTransfer(ship, pending.sourceQuadrant) : 0;
    const hasTransfer = !pending.balanceEvenly && pending.sourceQuadrant && pending.destinationQuadrant && maxTransfer > 0;
    balanceDialog.innerHTML = `<div class="sd-header"><span class="sd-name" id="balance-title">Balance Shields · ${reportEscape(ship.name)}</span><button class="btn btn-ghost sd-close" id="btn-close-balance" aria-label="Close balance dialog">×</button></div>
        ${ShipSchematic.render(ship, { mode: 'balance', sourceQuadrant: pending.sourceQuadrant, destinationQuadrant: pending.destinationQuadrant, amount: pending.amount, balanceEvenly: pending.balanceEvenly, evenPreview })}
        ${hasTransfer ? `<div class="balance-transfer-control"><label for="balance-amount">Shield points to move <input id="balance-amount" type="number" min="1" max="${maxTransfer}" value="${pending.amount}" ${pending.submitting ? 'disabled' : ''}></label><input id="balance-slider" type="range" min="1" max="${maxTransfer}" value="${pending.amount}" aria-label="Shield points to move" ${pending.submitting ? 'disabled' : ''}><div class="hint">Max ${maxTransfer} SP · Source minimum ${Math.ceil(QUADRANTS.reduce((sum, q) => sum + Math.max(0, ship.shields[q.toLowerCase()] ?? 0), 0) / 10)} SP</div></div>` : ''}
        <div class="system-picker-actions balance-dialog-actions">
            <button class="btn btn-primary" id="btn-confirm-balance" ${pending.submitting || (!pending.balanceEvenly && (!pending.sourceQuadrant || !pending.destinationQuadrant || pending.amount <= 0)) ? 'disabled' : ''}>${pending.submitting ? 'Attempting…' : `Confirm Balance${chance != null ? ` (${chance}%)` : ''}`}</button>
            <button class="btn btn-ghost" id="btn-reset-balance" ${pending.submitting ? 'disabled' : ''}>Reset</button>
        </div>
        <div class="hint" id="balance-error" role="status">${reportEscape(pending.error)}</div>`;
    document.getElementById('btn-confirm-balance').addEventListener('click', confirmShieldBalance);
    document.getElementById('btn-reset-balance').addEventListener('click', () => {
        pending.sourceQuadrant = null; pending.destinationQuadrant = null; pending.amount = 0; pending.balanceEvenly = false; pending.error = '';
        renderShieldBalancing();
    });
    const setTransfer = value => {
        pending.amount = Math.min(maxTransfer, Math.max(1, Number(value) || 1));
        renderShieldBalancing();
    };
    document.getElementById('balance-amount')?.addEventListener('change', event => setTransfer(event.target.value));
    document.getElementById('balance-slider')?.addEventListener('change', event => setTransfer(event.target.value));
    ShipSchematic.bind(balanceDialog, ship, { mode: 'balance', sourceQuadrant: pending.sourceQuadrant, destinationQuadrant: pending.destinationQuadrant, amount: pending.amount, balanceEvenly: pending.balanceEvenly, evenPreview,
        onArcSelect: arc => {
            if (pending.submitting) return;
            const a = arc.charAt(0).toUpperCase() + arc.slice(1);
            pending.balanceEvenly = false;
            if (a === pending.sourceQuadrant) {
                pending.sourceQuadrant = null; pending.destinationQuadrant = null; pending.amount = 0;
            } else if (!pending.sourceQuadrant || a === pending.destinationQuadrant) {
                pending.sourceQuadrant = a; pending.destinationQuadrant = null; pending.amount = maxShieldTransfer(ship, a);
            } else if (!pending.destinationQuadrant) {
                pending.destinationQuadrant = a; pending.amount = maxShieldTransfer(ship, pending.sourceQuadrant);
            } else {
                pending.sourceQuadrant = a; pending.destinationQuadrant = null; pending.amount = maxShieldTransfer(ship, a);
            }
            pending.error = '';
            renderShieldBalancing();
        },
        onBalanceEvenly: () => {
            if (pending.submitting) return;
            pending.balanceEvenly = true; pending.sourceQuadrant = null; pending.destinationQuadrant = null; pending.amount = 0; pending.error = '';
            renderShieldBalancing();
        } });
    document.getElementById('btn-close-balance').addEventListener('click', () => { clearShieldBalancing(); renderHelm(); });
    balanceDialog.oncancel = event => {
        if (pendingShieldBalance?.submitting) { event.preventDefault(); return; }
        clearShieldBalancing();
        renderHelm();
    };
    if (!balanceDialog.open) balanceDialog.showModal();
}

async function confirmShieldBalance() {
    if (!canContinueShieldBalancing() || pendingShieldBalance.submitting) return;
    const pending = pendingShieldBalance;
    const actor = currentActor();
    if (!pending.balanceEvenly && (!pending.sourceQuadrant || !pending.destinationQuadrant || pending.amount <= 0)) return;
    pending.submitting = true;
    pending.error = '';
    renderShieldBalancing();
    try {
        await api(`/${roomCode}/science`, pending.balanceEvenly
            ? { shipId: pending.shipId, crewIndex: pending.crewIndex, action: 'Balance', balanceEvenly: true }
            : { shipId: pending.shipId, crewIndex: pending.crewIndex, action: 'Balance', fromQuadrant: pending.sourceQuadrant, toQuadrant: pending.destinationQuadrant, amount: Math.max(0, pending.amount) });
    } catch (err) {
        pending.submitting = false;
        pending.error = `Could not balance shields: ${err.message}`;
        renderShieldBalancing();
        return;
    }
    clearShieldBalancing();
    try { await fetchState(); }
    catch (err) { renderHelm(); addLog('error', `The action was submitted, but state could not refresh: ${err.message}`); }
}

function startSystemTargeting(crewIndex) {
    const actor = currentActor();
    if (!actor || state.phase !== 'Helm' || state.freeAttack || targetSystemUnavailableReason(actor) ||
        !availableScienceOfficers(actor).some(c => c.index === crewIndex)) return;
    pendingSystemTarget = { shipId: actor.id, crewIndex, round: state.round, targetShipId: null, system: null, submitting: false, error: '' };
    detailsShipId = null;
    pinnedWeapon = null;
    clearWeaponRange();
    hideShipDetails();
    renderHelm();
}

function renderSystemTargeting() {
    if (!canContinueSystemTargeting()) { clearSystemTargeting(); renderHelm(); return; }
    const actor = currentActor();
    const pending = pendingSystemTarget;
    const officers = availableScienceOfficers(actor);
    const enemies = systemTargetEnemies(actor);
    const target = enemies.find(t => t.id === pending.targetShipId);
    if (!target) { pending.targetShipId = null; pending.system = null; }
    const chance = target ? actor.crew[pending.crewIndex].scienceChances.targets[target.id] : null;
    grid.setHighlights([]);
    grid.setGhost(null);
    grid.setPath(null);
    grid.setRangeHexes([]);
    grid.setTargets(enemies.map(t => [t.id, actor.crew[pending.crewIndex].scienceChances.targets[t.id]]));
    grid.setSelectedGunneryTarget(target ? actor.id : null, target?.id || null);
    actionPanel.innerHTML = `<div class="actor-name" style="color:${teamColor(actor.teamId)}">${reportEscape(actor.name)}</div>
        <div class="detail-section">Target System (Push)</div>
        <div class="hint">Choose a highlighted enemy, select a system in its popup, then confirm there to roll.</div>
        <div class="eng-row"><label for="target-science-crew">Science Officer</label><select id="target-science-crew" ${pending.submitting ? 'disabled' : ''}>${officers.map(c => `<option value="${c.index}" ${c.index === pending.crewIndex ? 'selected' : ''}>Officer ${c.index + 1} (${c.scienceBonus >= 0 ? '+' : ''}${c.scienceBonus})</option>`).join('')}</select></div>
        <div class="hint">${target ? `${reportEscape(actor.name)} → ${reportEscape(target.name)} · ${systemTargetLabel(pending.system)}` : 'Click a highlighted enemy on the map, then choose its system.'}</div>
        ${target ? `<div class="hint">${Hex.distance(actor, target)} hexes · ${chance}% success${pending.system === 'WeaponsArray' ? ' · Weapon arc is determined randomly if critical damage occurs.' : ''}</div>
            <button class="btn btn-ghost" id="btn-change-target-system" ${pending.submitting ? 'disabled' : ''}>Change System</button>` : ''}
        <div class="hint">${actor.sensorRangeHexes ? `Maximum sensor range: ${actor.sensorRangeHexes * 5} hexes.` : ''} ${enemies.length ? 'Click another highlighted enemy to change the target.' : 'No unlocked enemies in sensor range.'}</div>
        <button class="btn btn-ghost" id="btn-cancel-target-system" ${pending.submitting ? 'disabled' : ''}>Cancel Targeting</button>
        <div class="hint" id="target-system-error" role="status">${reportEscape(pending.error)}</div>`;
    document.getElementById('target-science-crew').addEventListener('change', event => {
        pending.crewIndex = Number(event.target.value);
        pending.error = '';
        renderSystemTargeting();
    });
    document.getElementById('btn-change-target-system')?.addEventListener('click', () => chooseSystemTarget(target));
    document.getElementById('btn-cancel-target-system').addEventListener('click', () => { clearSystemTargeting(); renderHelm(); });
}

function chooseSystemTarget(ship) {
    if (!pendingSystemTarget || pendingSystemTarget.submitting) return;
    if (!canContinueSystemTargeting()) { clearSystemTargeting(); render(); return; }
    if (!systemTargetEnemies(currentActor()).some(t => t.id === ship.id)) {
        pendingSystemTarget.error = 'Choose an unlocked enemy within sensor range.';
        renderSystemTargeting();
        return;
    }
    const actor = currentActor();
    let selection = pendingSystemTarget.targetShipId === ship.id ? pendingSystemTarget.system : null;
    systemTargetDialog.innerHTML = `<div class="sd-header"><span class="sd-name" id="system-target-title">Choose a system · ${reportEscape(ship.name)}</span><button class="btn btn-ghost sd-close" id="btn-close-system-picker" aria-label="Close system picker">×</button></div>
        ${ShipSchematic.render(ship, { mode: 'target', selectedSystem: selection })}
        <div class="system-picker-actions"><button class="btn btn-primary" id="btn-confirm-system-target" ${selection ? '' : 'disabled'}>Confirm Target System${actor.crew[pendingSystemTarget.crewIndex]?.scienceChances?.targets[ship.id] != null ? ` (${actor.crew[pendingSystemTarget.crewIndex].scienceChances.targets[ship.id]}%)` : ''}</button><button class="btn btn-ghost" id="btn-cancel-system-picker">Cancel</button></div>
        <div class="hint" id="system-target-error" role="status">${reportEscape(pendingSystemTarget.error)}</div>`;
    ShipSchematic.bind(systemTargetDialog, ship, { mode: 'target', selectedSystem: selection, onSelect: system => {
        selection = system;
        document.getElementById('btn-confirm-system-target').disabled = false;
    } });
    document.getElementById('btn-close-system-picker').addEventListener('click', () => systemTargetDialog.close());
    document.getElementById('btn-cancel-system-picker').addEventListener('click', () => systemTargetDialog.close());
    document.getElementById('btn-confirm-system-target').addEventListener('click', () => {
        if (!selection || !canContinueSystemTargeting()) { clearSystemTargeting(); render(); return; }
        if (!systemTargetEnemies(currentActor()).some(t => t.id === ship.id)) {
            systemTargetDialog.close();
            pendingSystemTarget.error = 'That ship is no longer an available target.';
            renderSystemTargeting();
            return;
        }
        pendingSystemTarget.targetShipId = ship.id;
        pendingSystemTarget.system = selection;
        pendingSystemTarget.error = '';
        confirmSystemTarget();
    });
    systemTargetDialog.oncancel = event => {
        if (pendingSystemTarget?.submitting) event.preventDefault();
    };
    if (!systemTargetDialog.open) systemTargetDialog.showModal();
}

async function confirmSystemTarget() {
    if (!canContinueSystemTargeting() || pendingSystemTarget.submitting) return;
    const pending = pendingSystemTarget;
    if (!pending.system || !systemTargetEnemies(currentActor()).some(t => t.id === pending.targetShipId)) return;
    pending.submitting = true;
    pending.error = '';
    const confirmButton = document.getElementById('btn-confirm-system-target');
    if (confirmButton) { confirmButton.disabled = true; confirmButton.textContent = 'Attempting…'; }
    document.getElementById('btn-cancel-system-picker')?.setAttribute('disabled', '');
    document.getElementById('btn-close-system-picker')?.setAttribute('disabled', '');
    try {
        await api(`/${roomCode}/science`, { shipId: pending.shipId, crewIndex: pending.crewIndex, action: 'Target System', targetShipId: pending.targetShipId, system: pending.system });
    } catch (err) {
        pending.submitting = false;
        pending.error = `Could not target system: ${err.message}`;
        if (confirmButton) { confirmButton.disabled = false; confirmButton.textContent = 'Retry Target System'; }
        document.getElementById('btn-cancel-system-picker')?.removeAttribute('disabled');
        document.getElementById('btn-close-system-picker')?.removeAttribute('disabled');
        const error = document.getElementById('system-target-error');
        if (error) error.textContent = pending.error;
        return;
    }
    systemTargetDialog.close();
    clearSystemTargeting();
    try { await fetchState(); }
    catch (err) { renderHelm(); addLog('error', `The action was submitted, but state could not refresh: ${err.message}`); }
}

// ── Helm ──
function renderHelm() {
    const actor = currentActor();
    if (!actor) { grid.setHighlights([]); grid.setTargets([]); grid.setGhost(null); grid.setPath(null); actionPanel.innerHTML = ''; return; }
    if (pendingSystemTarget) { renderSystemTargeting(); return; }
    if (pendingShieldBalance) { renderShieldBalancing(); return; }

    if (state.freeAttack) {
        const attack = state.freeAttack;
        const defender = state.ships.find(s => s.id === attack.defenderId);
        grid.setHighlights([]);
        grid.setTargets([]);
        grid.setGhost(null);
        grid.setPath(null);
        actionPanel.innerHTML = `<div class="actor-name" style="color:${teamColor(defender.teamId)}">${defender.name} (Team ${defender.teamId + 1})</div>
            <div class="hint">${actor.name} is passing through this ship. Choose a gunner and direct-fire weapon for a free attack against its aft shields, or pass.</div>
            <label>Gunner <select id="free-gunner">${attack.gunners.map((g, i) => `<option value="${g.index}">Gunner ${i + 1} (${g.bonus >= 0 ? '+' : ''}${g.bonus})</option>`).join('')}</select></label>
            <label>Weapon <select id="free-weapon">${attack.weapons.map(w => `<option value="${w.index}">${w.name} (${formatArc(w.arc)})</option>`).join('')}</select></label>
            <button class="btn btn-primary" id="btn-free-fire">Fire Free Attack</button>
            <button class="btn btn-ghost" id="btn-free-pass">Pass</button>`;
        async function resolveFreeAttack(fire) {
            try {
                await api(`/${roomCode}/free-attack`, { weaponIndex: fire ? Number(document.getElementById('free-weapon').value) : null,
                    gunnerIndex: fire ? Number(document.getElementById('free-gunner').value) : null });
                await fetchState();
            } catch (err) { addLog('error', `Free attack failed: ${err.message}`); }
        }
        document.getElementById('btn-free-fire').addEventListener('click', () => resolveFreeAttack(true));
        document.getElementById('btn-free-pass').addEventListener('click', () => resolveFreeAttack(false));
        return;
    }

    // Restore a movement preview after leaving science targeting.
    grid.setGhost(pendingMove ? { ...pendingMove.option.position, facing: pendingMove.option.finalFacing, teamId: actor.teamId, name: actor.name } : null);
    grid.setPath(pendingMove?.option.path || null);
    // Highlight every reachable hex (union over facings).
    const moves = state.options?.items || [];
    grid.setHighlights(moves.filter(m => !m.requiresManeuver).map(m => m.position),
        moves.filter(m => m.requiresManeuver).map(m => m.position));
    grid.setTargets([]);

    const facingNames = ['NW', 'NE', 'E', 'SE', 'SW', 'W'];
    const pending = pendingMove?.option;
    const flybyEnemy = state.pendingStunt === 'Flyby' && pending
        ? state.ships.find(ship => ship.teamId !== actor.teamId && !ship.isDestroyed &&
            pending.path?.some(step => step.q === ship.q && step.r === ship.r))
        : null;
    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(actor.teamId)}">${actor.name}</div>
        ${actor.maneuverSucceeded != null ? `<div class="hint">Maneuver ${actor.maneuverSucceeded ? 'succeeded: improved turns available' : 'failed: normal movement only'}. Choose and confirm your move; stunts are unavailable.</div>` : ''}
        ${actor.helmAction === 'Glide' ? '<div class="hint" title="Move up to half speed; +2 distance between turns; add Piloting ranks to AC/TL until next round. Uses a minor crew action.">Glide: half speed, +2 distance between turns; uses a minor crew action.</div>' : actor.helmAction === 'None' ? '<div class="hint">No crew member can move this ship.</div>' : ''}
        ${pending
            ? `<div class="hint">${pending.isTurnInPlace
                    ? `Turning in place to face ${facingNames[pending.finalFacing]}.`
                    : `Moving to (${pending.position.q}, ${pending.position.r}), facing ${facingNames[pending.finalFacing]}.${flybyEnemy ? ` Flyby against ${flybyEnemy.name}.` : ''}`} Click another hex or your ship to change, or confirm.</div>
               ${pending.requiresManeuver ? `<div class="hint">This move requires Maneuver. Attempting it uses the pilot's action and prevents stunts, even if you cancel the move afterward.</div><button class="btn btn-primary" id="btn-attempt-maneuver">Attempt Maneuver (${actor.maneuverChance}%)</button>` : '<button class="btn btn-primary" id="btn-confirm-move">Confirm Move</button>'}
               <button class="btn btn-ghost" id="btn-cancel-move">Cancel</button>`
            : state.pendingStunt
                ? `<div class="hint">${state.pendingStunt}: choose a highlighted destination to preview the result.</div>${(state.options?.items || []).some(m => m.position.q === actor.q && m.position.r === actor.r) ? '<button class="btn btn-ghost" id="btn-stunt-hold">Preview current hex</button>' : ''}`
                : `<div class="hint"><span title="Move up to full speed with normal turns.">Click a highlighted hex to preview a move.</span>${actor.maneuverChance != null ? ' Faded blue hexes and facings require a Maneuver check.' : ''}${actor.canTurnInPlace ? ' <span title="Turn to any facing without moving. Clumsy: −4 AC/TL until next round. Poor: −2 AC/TL until next round.">Click your own ship to turn in place.</span>' : ''}</div>
                   <button class="btn btn-ghost" id="btn-hold" title="Stay in the current hex and keep the current facing.">Hold Position</button>
                   ${actor.stuntChances?.backOff != null ? `<div class="detail-section">Stunts</div><button class="btn btn-ghost" data-stunt="Back Off" title="Move backward up to half speed without turning. Failure: move back 1 hex. Fail by 5+: no movement; −4 AC/TL until next round.">Back Off (${actor.stuntChances.backOff}%)</button> <button class="btn btn-ghost" data-stunt="Flip and Burn" title="Move forward up to half speed without turning, then rotate 180°. Failure: move forward up to half speed without rotating.">Flip and Burn (${actor.stuntChances.flipAndBurn}%)</button> <button class="btn btn-ghost" data-stunt="Evade" title="Move up to full speed with normal turns; +2 AC/TL until next round. Failure: normal movement; no bonus. Fail by 5+: normal movement; −2 AC/TL until next round.">Evade (${actor.stuntChances.evade}%)</button> <button class="btn btn-ghost" data-stunt="Slide" title="Move diagonally forward to port or starboard up to full speed without changing facing. Failure: move forward up to half speed without turning.">Slide (${actor.stuntChances.slide}%)</button> ${actor.stuntChances.barrelRoll != null ? `<button class="btn btn-ghost" data-stunt="Barrel Roll" title="Move up to half speed with normal turns; swap port/starboard shields and weapons for Gunnery. Failure: move half speed; no swap. Fail by 5+: move half speed; no swap; −4 AC/TL until next round.">Barrel Roll (${actor.stuntChances.barrelRoll}%)</button>` : ''}` : ''}`}`;

    if (!pending && !state.pendingStunt && actor.stuntChances?.backOff != null) {
        actionPanel.innerHTML += ` <button class="btn btn-ghost" data-stunt="Flyby" title="Move up to full speed with normal turns through one enemy; avoid its free attack and fire one weapon arc at range 1 against any quadrant during Gunnery. Failure: normal movement, free attack and targeting.">Flyby</button>`;
    }

    const officers = availableScienceOfficers(actor);
    const locks = actor.targetedSystems || [];
    if (officers.length || locks.length) {
        const enemies = systemTargetEnemies(actor);
        const unavailable = targetSystemUnavailableReason(actor);
        const balanceUnavailable = balanceSensorsWrecked(actor);
        actionPanel.innerHTML += `<div class="detail-section">Science Officer Actions</div>
            ${locks.length ? `<div class="hint">Targeted: ${locks.map(lock => `${reportEscape(state.ships.find(target => target.id === lock.targetShipId)?.name || 'Unknown ship')} — ${systemTargetLabel(lock.system)}`).join('; ')}</div>` : ''}
            ${officers.length ? `
                <div class="eng-row"><label for="science-crew">Science Officer</label><select id="science-crew">${officers.map(c => `<option value="${c.index}">Officer ${c.index + 1} (${c.scienceBonus >= 0 ? '+' : ''}${c.scienceBonus})</option>`).join('')}</select></div>
                <div class="science-action-buttons">
                    <button class="btn btn-ghost" id="btn-target-system" title="Choose an enemy and system on the map. Confirm in the popup to roll." ${unavailable || !enemies.length ? 'disabled' : ''}>Target System (Push)</button>
                    ${balanceUnavailable ? '' : '<button class="btn btn-ghost" id="btn-balance-shields" title="Choose shield arcs and a transfer, then confirm in the ship diagram to roll.">Balance Shields</button>'}
                </div>
                ${unavailable ? `<div class="hint">${unavailable}</div>` : (!enemies.length ? '<div class="hint">No unlocked enemies in sensor range.</div>' : '')}
                ${balanceUnavailable ? '<div class="hint">Wrecked sensors prohibit Balance Shields.</div>' : ''}
                <div class="hint" id="science-error"></div>` : '<div class="hint">No Science Officer action available this round.</div>'}`;
        if (officers.length) {
            const crewSelect = document.getElementById('science-crew');
            crewSelect.addEventListener('change', updateChance);
            document.getElementById('btn-target-system').addEventListener('click', () => startSystemTargeting(Number(crewSelect.value)));
            document.getElementById('btn-balance-shields')?.addEventListener('click', () => startShieldBalancing(Number(crewSelect.value)));
            updateChance();
        }
    }

    if (pending) {
        document.getElementById('btn-confirm-move')?.addEventListener('click', confirmMove);
        document.getElementById('btn-attempt-maneuver')?.addEventListener('click', attemptManeuver);
        document.getElementById('btn-cancel-move').addEventListener('click', () => setPendingMove(null));
    } else {
        document.getElementById('btn-hold')?.addEventListener('click', async () => {
            await api(`/${roomCode}/skip-move`);
            await fetchState();
        });
        document.getElementById('btn-stunt-hold')?.addEventListener('click', () => {
            const option = (state.options?.items || []).find(m => m.position.q === actor.q && m.position.r === actor.r);
            if (option) setPendingMove(option);
        });
        actionPanel.querySelectorAll('[data-stunt]').forEach(button => button.addEventListener('click', async () => {
            try {
                await api(`/${roomCode}/stunt`, { stunt: button.dataset.stunt });
                await fetchState();
            } catch (err) { addLog('error', `Stunt failed: ${err.message}`); }
        }));
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
    if (detailsShipId && pinnedWeapon?.shipId === actor?.id) {
        const ship = state.ships.find(s => s.id === actor.id);
        if (ship) showWeaponRange(ship, pinnedWeapon.weaponIndex);
    }
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

async function attemptManeuver() {
    const m = pendingMove?.option;
    if (!m?.requiresManeuver) return;
    try {
        const response = await api(`/${roomCode}/maneuver`, { q: m.position.q, r: m.position.r, facing: m.finalFacing });
        pendingMove = null;
        grid.setGhost(null);
        grid.setPath(null);
        await fetchState();
        if (response.succeeded) {
            const option = (state.options?.items || []).find(o => o.position.q === m.position.q && o.position.r === m.position.r && o.finalFacing === m.finalFacing);
            if (option) setPendingMove(option);
        }
    } catch (err) { addLog('error', `Maneuver failed: ${err.message}`); }
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
function rangeBandHexes(ship, minR, maxR, arc, inc, broadArc = false) {
    const out = [];
    for (let r = -maxR; r <= maxR; r++) {
        for (let q = -maxR; q <= maxR; q++) {
            if (q === 0 && r === 0) continue; // the ship's own hex
            const d = Hex.distance({ q: 0, r: 0 }, { q, r });
            if (d < minR || d > maxR) continue;
            const angle = angleFromFacing(ship.facing, q, r);
            const inPrimaryArc = arcCovers(arc, angle);
            if (!inPrimaryArc && !(broadArc && broadArcCovers(arc, angle))) continue;
            // The adjacent-arc penalty equals one additional range increment.
            out.push({ q: ship.q + q, r: ship.r + r, band: Math.ceil(d / (inc || 1)) + (inPrimaryArc ? 0 : 1) });
        }
    }
    return out;
}

function broadArcCovers(arc, angle) {
    switch (arc) {
        case 'Forward': return arcCovers('Port', angle) || arcCovers('Starboard', angle);
        case 'Port':
        case 'Starboard': return arcCovers('Forward', angle) || arcCovers('Aft', angle);
        case 'Aft': return arcCovers('Port', angle) || arcCovers('Starboard', angle);
        default: return false;
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

    // Preserve the current weapon selection between individual shots, but reset
    // assignments when the acting ship changes.
    const actorChanged = renderGunnery.actorId !== actor.id;
    renderGunnery.actorId = actor.id;
    if (actorChanged) {
        selectedShots.clear();
        selectedTargetArcs.clear();
        activeWeapon = null;
    }
    if (renderGunnery.round !== state.round) {
        selectedShots.clear();
        selectedTargetArcs.clear();
        activeWeapon = null;
        renderGunnery.round = state.round;
    }

    const options = state.options?.items || [];

    // Group valid targets by weapon index so each instance gets its own chip.
    const weapons = new Map(); // weaponIndex → [options]
    for (const o of options) {
        if (!weapons.has(o.weaponIndex)) weapons.set(o.weaponIndex, []);
        weapons.get(o.weaponIndex).push(o);
    }

    const fired = new Set(state.firedWeaponIndices || []);
    // Preserve valid assignments, up to the number of remaining crew actions.
    for (const [widx, target] of selectedShots) {
        if (!fired.has(widx) && !(weapons.get(widx) || []).some(o => o.targetShipId === target))
            selectedShots.delete(widx);
    }
    for (const [widx, opts] of weapons) {
        if (!opts.length) continue;
        if (selectedShots.has(widx)) continue;
        if ([...selectedShots.keys()].filter(i => !fired.has(i)).length >= actor.crewShotsRemaining) break;
        const eligible = opts.filter(o => !o.arcChoiceRequired || !selectedTargetArcs.has(o.targetShipId) ||
            o.arc === 'AllArcs' || o.broadArc || o.arc === selectedTargetArcs.get(o.targetShipId));
        if (!eligible.length) continue;
        const closest = eligible.reduce((best, o) => o.range < best.range ? o : best);
        selectedShots.set(widx, closest.targetShipId);
    }

    const usableIndices = [...weapons.keys()].filter(i => !fired.has(i));
    if (activeWeapon === null || fired.has(activeWeapon) || !weapons.has(activeWeapon))
        activeWeapon = usableIndices[0] ?? null;

    const emptyAmmoIndices = (actor.weapons || []).map((weapon, index) =>
        weapon.limitedFireUses > 0 && weapon.usesRemaining === 0 ? index : null).filter(index => index !== null);
    const chipIndices = new Set([...weapons.keys(), ...fired, ...emptyAmmoIndices]);
    for (const widx of [...chipIndices]) {
        const weapon = actor.weapons?.[widx];
        if (weapon?.linkedCount > 1 && weapon.linkedGroup && actor.weapons.findIndex(w => w.linkedGroup === weapon.linkedGroup) !== widx)
            chipIndices.delete(widx);
    }
    const chips = [...chipIndices].sort((a, b) => a - b).map(widx => {
        const w = actor.weapons?.[widx];
        if (!w) return '';
        const arcLabel = w.arc && w.arc !== 'AllArcs' ? ` (${w.arc}${w.broadArc ? ' + Broad Arc' : ''})` : '';
        const dmg = ` · ${w.damage}`;
        const ammo = w.limitedFireUses > 0 ? ` · ${w.usesRemaining}/${w.limitedFireUses}` : '';
        const exhausted = w.limitedFireUses > 0 && w.usesRemaining === 0;
        const isFired = fired.has(widx);
        const broadArcTitle = w.broadArc ? 'Can also fire into adjacent arcs at −2.' : '';
        const array = (actor.weaponArcs || []).find(a => a.arc === w.arc);
        const wrecked = array?.effectiveCondition === 'Wrecked';
        const condition = array?.condition && array.condition !== 'None' ? ` · ${array.condition}${array.condition !== array.effectiveCondition ? ` (held: ${array.effectiveCondition === 'None' ? 'Healthy' : array.effectiveCondition})` : ''}` : '';
        return `<button class="weapon-chip${activeWeapon === widx ? ' active' : ''}${isFired ? ' fired' : ''}" data-weapon="${widx}" title="${isFired ? 'Already fired this round' : exhausted ? 'Out of ammunition' : wrecked ? 'This weapon array is wrecked' : broadArcTitle}" ${(exhausted || isFired || wrecked || !weapons.has(widx)) ? 'disabled' : ''}>${w.displayName || w.name}${arcLabel}${dmg}${ammo}${condition}${isFired ? ' · FIRED' : ''}</button>`;
    }).join('');

    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(actor.teamId)}">${actor.name}</div>
        <div class="hint">Choose a weapon and target, then fire it. Hits are resolved in order; damage waits until all ships finish Gunnery.</div>
        ${actor.barrelRollActive ? '<div class="hint">Barrel Roll: port and starboard shields and weapon arcs are swapped for this Gunnery phase.</div>' : ''}
        <div class="hint">Up to ${Math.min(actor.crewShotsRemaining, weapons.size)} shot${Math.min(actor.crewShotsRemaining, weapons.size) === 1 ? '' : 's'} remaining (${actor.crewShotsRemaining} available crew action${actor.crewShotsRemaining === 1 ? '' : 's'}).</div>
        ${actor.gunneryAction === 'Snap Shot' ? '<div class="hint">Snap Shot: one weapon, with a −2 gunnery penalty; uses a minor crew action.</div>' : ''}
        <div id="arc-choice" class="target-info"></div>
        <div class="weapon-chips">${chips || '<span class="hint">No weapons can fire.</span>'}</div>
        <div id="target-info" class="target-info"></div>
        <button class="btn btn-primary" id="btn-fire" disabled>Fire</button>
        <button class="btn btn-ghost" id="btn-end-fire">End Gunnery Turn</button>`;

    const fireBtn = document.getElementById('btn-fire');
    const targetInfo = document.getElementById('target-info');
    const arcChoiceEl = document.getElementById('arc-choice');

    function refresh() {
        // Highlight the targets reachable by the active weapon.
        if (activeWeapon !== null) {
            const opts = weapons.get(activeWeapon) || [];
            grid.setTargets(opts.map(o => [o.targetShipId, o.arcHitChances?.[selectedTargetArcs.get(o.targetShipId)] ?? o.hitChancePercent]));
            const activeArcOption = opts.find(o => o.targetShipId === selectedShots.get(activeWeapon) && o.arcChoiceRequired);
            if (activeArcOption) {
                const selected = selectedTargetArcs.get(activeArcOption.targetShipId);
                arcChoiceEl.innerHTML = `<span class="hint">${activeArcOption.flyby ? 'Flyby: choose the firing arc for this turret.' : 'This target is on an arc boundary. Choose one firing arc for all attacks against it this round.'}</span><div class="weapon-chips">${activeArcOption.arcChoices.map(arc => `<button class="weapon-chip arc-choice${selected === arc ? ' active' : ''}" data-arc="${arc}" data-target="${activeArcOption.targetShipId}">${arc}${activeArcOption.arcHitChances?.[arc] != null ? ` · ${activeArcOption.arcHitChances[arc]}%` : ''}</button>`).join('')}</div>`;
                arcChoiceEl.querySelectorAll('[data-arc]').forEach(button => button.addEventListener('click', () => {
                    selectedTargetArcs.set(button.dataset.target, button.dataset.arc);
                    refresh();
                }));
            } else {
                arcChoiceEl.innerHTML = '';
            }
            // Show this specific weapon's range band as a visual aid (not clickable).
            const w = actor.weapons?.[activeWeapon];
            if (opts.some(o => o.flyby && o.targetShipId === selectedShots.get(activeWeapon))) {
                grid.setRangeHexes([]);
            } else if (w) {
                const inc = w.rangeIncrement ?? 5;
                const maxVis = Math.min(w.maxRange ?? inc * 10, inc * 3); // show up to 3 increments, capped at max range
                const hexes = rangeBandHexes(actor, w.minRange ?? 1, maxVis, w.arc, inc, w.broadArc);
                grid.setRangeHexes(hexes);
            }
            const label = w ? `${w.name}${w.arc && w.arc !== 'AllArcs' ? ` (${w.arc}${w.broadArc ? ' + Broad Arc' : ''})` : ''}` : activeWeapon;
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
                const label = w ? `${w.name}${w.arc && w.arc !== 'AllArcs' ? ` (${w.arc}${w.broadArc ? ' + Broad Arc' : ''})` : ''}` : widx;
                const t = state.ships.find(s => s.id === tid);
                const opt = (weapons.get(widx) || []).find(o => o.targetShipId === tid);
                const penalties = opt ? [opt.rangePenalty && `${opt.rangePenalty} range`, opt.arcPenalty && `${opt.arcPenalty} arc`].filter(Boolean).join(', ') : '';
                const rangeStr = opt ? ` [${opt.range} hex${opt.range !== 1 ? 'es' : ''}${penalties ? `, ${penalties}` : ''}]` : '';
                const status = fired.has(widx) ? ' · fired' : '';
                return `<span class="shot-item">${label} → ${t?.name ?? '?'}${rangeStr}${status}</span>`;
            }).join('')}</div>`;
        }

        const targetId = activeWeapon === null ? null : selectedShots.get(activeWeapon);
        const activeOption = activeWeapon === null ? null : (weapons.get(activeWeapon) || []).find(o => o.targetShipId === targetId);
        if (activeOption?.flyby) {
            targetInfo.innerHTML += `<label>Flyby target quadrant <select id="flyby-quadrant">${['Forward', 'Starboard', 'Aft', 'Port'].map(q => `<option value="${q}">${q}</option>`).join('')}</select></label><div class="hint">Effective range: 1 hex. The first shot commits the firing arc for Flyby.</div>`;
        }
        const missingArcChoice = !!activeOption?.arcChoiceRequired && !selectedTargetArcs.has(targetId);
        grid.setSelectedGunneryTarget(targetId ? actor.id : null, targetId);
        fireBtn.disabled = activeWeapon === null || !targetId || fired.has(activeWeapon) || missingArcChoice;
    }

    refreshGunnery = refresh; // expose for the ship-click dispatcher

    actionPanel.querySelectorAll('.weapon-chip:not(:disabled)').forEach(chip => {
        chip.addEventListener('click', () => {
            const idx = parseInt(chip.dataset.weapon, 10);
            activeWeapon = (activeWeapon === idx) ? null : idx;
            if (activeWeapon !== null && !selectedShots.has(idx)) {
                const opts = weapons.get(idx) || [];
                const eligible = opts.filter(o => !o.arcChoiceRequired || !selectedTargetArcs.has(o.targetShipId) ||
                    o.arc === 'AllArcs' || o.broadArc || o.arc === selectedTargetArcs.get(o.targetShipId));
                if (eligible.length) assignGunneryTarget(idx, eligible.reduce((best, o) => o.range < best.range ? o : best).targetShipId);
            }
            actionPanel.querySelectorAll('.weapon-chip').forEach(c =>
                c.classList.toggle('active', parseInt(c.dataset.weapon, 10) === activeWeapon));
            refresh();
        });
    });

    refresh();

    document.getElementById('btn-fire').addEventListener('click', async () => {
        if (activeWeapon === null) return;
        const targetShipId = selectedShots.get(activeWeapon);
        if (!targetShipId) return;
        const option = (weapons.get(activeWeapon) || []).find(o => o.targetShipId === targetShipId);
        const arc = option?.arcChoiceRequired ? selectedTargetArcs.get(targetShipId) : undefined;
        if (option?.arcChoiceRequired && !arc) return;
        const targetQuadrant = option?.flyby ? document.getElementById('flyby-quadrant')?.value : undefined;
        const response = await api(`/${roomCode}/fire`, { weaponIndex: activeWeapon, targetShipId, arc, targetQuadrant });
        if (!response.ok) addLog('error', 'Weapon could not fire.');
        await fetchState();
    });

    document.getElementById('btn-end-fire').addEventListener('click', async () => {
        await api(`/${roomCode}/end-fire`);
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
    const maxVis = Math.min(w.maxRange ?? inc * 10, inc * 3); // show up to 3 increments, capped at max range
    let perspective = ship;
    if (state?.phase === 'Helm' && pendingMove?.actorId === ship.id) {
        perspective = {
            ...ship,
            q: pendingMove.option.position.q,
            r: pendingMove.option.position.r,
            facing: pendingMove.option.finalFacing
        };
    }
    grid.setRangeHexesOnly(rangeBandHexes(perspective, w.minRange ?? 1, maxVis, w.arc, inc, w.broadArc));
}

function clearWeaponRange() {
    grid.setRangeHexesOnly([]);
}

function showShipDetails(ship) {
    reportShipId = ship.id;
    renderDamageReport();
    const shieldTotal = Object.values(ship.shields || {}).reduce((a, b) => a + b, 0);
    const shieldMax = ship.shieldCapacity ?? (ship.maxShield ?? 0) * 4;

    // Reset the pin if it no longer refers to this ship.
    if (pinnedWeapon && pinnedWeapon.shipId !== ship.id) {
        pinnedWeapon = null;
        clearWeaponRange();
    }

    const shipWeapons = ship.weapons || [];
    const weaponsHtml = shipWeapons.map((w, i) => ({ w, i }))
        .filter(({ w, i }) => (w.linkedCount || 1) <= 1 || !w.linkedGroup || shipWeapons.findIndex(candidate => candidate.linkedGroup === w.linkedGroup) === i)
        .map(({ w, i }) => `
        <div class="detail-weapon dw-hoverable" data-weapon-index="${i}">
            <span class="dw-name">${w.displayName || w.name}${w.linkedCount > 1 ? ` (${w.linkedCount} mounts)` : ''}</span>
            <span class="dw-meta">${formatArc(w.arc)}${w.broadArc ? ' + Broad Arc (−2 adjacent)' : ''} · range ${w.rangeIncrement ?? 5}/${w.maxRange ?? (w.rangeIncrement ?? 5) * 10} · ${w.damage}${w.type ? ` · ${w.type}` : ''}${w.limitedFireUses > 0 ? ` · ${w.usesRemaining}/${w.limitedFireUses} shots` : ''}</span>
        </div>`).join('');

    const crewHtml = (ship.crew || []).map(crew => {
        const bonus = crew.roleBonus == null ? '' : ` ${crew.roleBonus >= 0 ? '+' : ''}${crew.roleBonus}`;
        return `<li>${crew.role}${bonus}</li>`;
    }).join('');

    shipDetailsEl.innerHTML = `
        <div class="sd-header" style="--team-color:${teamColor(ship.teamId)}">
            <span class="sd-name">${ship.name}</span>
            <div class="sd-actions">
            <button class="btn btn-ghost damage-report-button" id="btn-damage-report" aria-controls="damage-report" aria-expanded="${damageReportOpen}">Damage Report</button>
            <button class="btn btn-ghost sd-close" title="Close" aria-label="Close ship details">×</button>
            </div>
        </div>
        ${(ship.isDisabled || ship.isDestroyed) ? `<div class="ship-status">${ship.isDestroyed ? 'DESTROYED' : 'DISABLED'}</div>` : ''}
        ${ShipSchematic.render(ship)}
        <details class="ship-specifications" open><summary>Ship specifications & crew</summary>
        <div class="detail-grid">
            <div class="detail-stat"><span>Tier</span><b>${formatTier(ship.tier)}</b></div>
            <div class="detail-stat"><span>Size</span><b>${ship.size ?? '—'}</b></div>
            <div class="detail-stat"><span>Hull</span><b>${ship.hull}/${ship.maxHull}</b></div>
            <div class="detail-stat"><span>Shields</span><b>${shieldTotal}/${shieldMax}</b></div>
            <div class="detail-stat"><span>AC</span><b>${ship.ac}</b></div>
            <div class="detail-stat"><span>TL</span><b>${ship.tl}</b></div>
            <div class="detail-stat"><span>Speed</span><b>${ship.speed}</b></div>
            <div class="detail-stat"><span>Maneuver</span><b>${ship.maneuverability}</b></div>
        </div>
        <div class="detail-section">Weapons</div>
        ${weaponsHtml || '<div class="hint">No weapons.</div>'}
        <div class="detail-section">Crew</div>
        ${crewHtml ? `<ul class="detail-list detail-crew">${crewHtml}</ul>` : '<div class="hint">No crew recorded.</div>'}
        </details>`;

    ShipSchematic.bind(shipDetailsEl, ship);

    shipDetailsEl.querySelector('#btn-damage-report').addEventListener('click', () => {
        damageReportOpen = !damageReportOpen;
        renderDamageReport();
    });
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
                const restricted = o.requiresManeuver ? ' maneuver-required' : '';
                return `<button class="btn btn-primary facing-btn${hold}${restricted}" data-f="${o.facing}" style="left:${FACING_RING_POS[o.facing].left}%;top:${FACING_RING_POS[o.facing].top}%"${o.requiresManeuver ? ' title="Requires a Maneuver check"' : ''}>${FACING_NAMES[o.facing]}</button>`;
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
        matches.map(m => ({ facing: m.finalFacing, requiresManeuver: m.requiresManeuver })),
        facing => setPendingMove(matches.find(m => m.finalFacing === facing)),
        matches.some(m => m.requiresManeuver) ? 'Faded facings require a Maneuver check.' : undefined);
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


// Frozen received outcomes; opening/closing this window never changes ship selection.
function reportEscape(value) {
    return String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function reportLabel(value) {
    return value === 'None' ? 'Healthy' : String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
}

function renderDamageReport() {
    shipDetailsEl.querySelector('#btn-damage-report')?.setAttribute('aria-expanded', String(damageReportOpen));
    damageReportEl.classList.toggle('hidden', !damageReportOpen);
    if (!damageReportOpen) return;
    const ship = state?.ships.find(s => s.id === (detailsShipId || reportShipId));
    const events = ship?.damageReport || [];
    const hits = events.filter(e => !e.shotDown);
    const changedRows = [];
    const changeRow = (label, before, after, loss = null) =>
        `<tr><td>${reportEscape(label)}</td><td>${reportEscape(before)} → ${reportEscape(after)}${loss ? ` <strong>(−${loss})</strong>` : ''}</td></tr>`;
    // Split a resource's summary when a repair/reallocation occurred between hits.
    // Each arrow then describes damage only, even across Engineering and Helm.
    function resourceRows(label, affected, value, loss = null) {
        let group = [];
        function flush() {
            if (!group.length) return;
            changedRows.push(changeRow(label, reportLabel(value(group[0].before)),
                reportLabel(value(group[group.length - 1].after)), loss ? group.reduce((sum, e) => sum + loss(e), 0) : null));
        }
        for (const e of affected) {
            if (group.length && value(group[group.length - 1].after) !== value(e.before)) {
                flush();
                group = [];
            }
            group.push(e);
        }
        flush();
    }
    for (const arc of ['Forward', 'Starboard', 'Aft', 'Port']) {
        resourceRows(`${arc} shields`, hits.filter(e => e.shieldDamage > 0 && e.quadrant === arc),
            snapshot => snapshot.shields[arc], e => e.shieldDamage);
    }
    resourceRows('Hull', hits.filter(e => e.hullDamage > 0), snapshot => snapshot.hull, e => e.hullDamage);
    for (const system of ['LifeSupport', 'Sensors', 'Engines', 'PowerCore']) {
        resourceRows(reportLabel(system), hits.filter(e => e.before.systems[system] !== e.after.systems[system]),
            snapshot => snapshot.systems[system]);
    }
    for (const arc of ['Forward', 'Starboard', 'Aft', 'Port']) {
        resourceRows(`${arc} weapons`, hits.filter(e => (e.before.weaponArcs?.[arc] ?? 'None') !== (e.after.weaponArcs?.[arc] ?? 'None')),
            snapshot => snapshot.weaponArcs?.[arc] ?? 'None');
    }
    const status = snapshot => snapshot.isDestroyed ? 'Destroyed' : snapshot.isDisabled ? 'Disabled' : 'Operational';
    const statusHits = hits.filter(e => status(e.before) !== status(e.after));
    if (statusHits.length) changedRows.push(changeRow('Status', status(statusHits[0].before), status(statusHits[statusHits.length - 1].after)));
    const eventRows = events.map(e => {
        const effects = [];
        if (e.shotDown) effects.push(`Shot down · ${e.defenseWeaponArc ? `${formatArc(e.defenseWeaponArc)} ` : ''}${e.defenseWeapon}`);
        else {
            if (e.shieldDamage) effects.push(`${e.quadrant} shields −${e.shieldDamage}`);
            if (e.hullDamage) effects.push(`Hull −${e.hullDamage}`);
            if (e.stoppedDamage) effects.push(`DT stopped ${e.stoppedDamage}`);
            for (const crit of e.criticalEffects || []) effects.push(crit.system
                ? `${reportLabel(crit.system)}${crit.weaponArc ? ` (${crit.weaponArc})` : ''} → ${reportLabel(crit.condition)}` : 'Crew injury · manual resolution');
            if (status(e.before) !== status(e.after)) effects.push(status(e.after));
            if (!effects.length) effects.push('No damage');
        }
        return `<li class="damage-event"><div>${reportEscape(e.attackerName)} · ${reportEscape(e.weapon)}</div>
            <div class="damage-event-effects">${effects.map(reportEscape).join(' · ')}</div></li>`;
    }).join('');
    const period = state?.damageReportRound != null ? ` · Since R${state.damageReportRound} Gunnery`
        : events.length ? ` · R${events[0].round} ${events[0].phase}` : '';
    damageReportEl.innerHTML = `
        <div class="sd-header" style="--team-color:${teamColor(ship?.teamId ?? 0)}">
            <span class="sd-name">${reportEscape(ship?.name || 'Select a ship')}</span>
            <button class="btn btn-ghost sd-close" aria-label="Close damage report">×</button>
        </div>
        <div class="detail-section">Damage Report${period}</div>
        ${changedRows.length ? `<table class="damage-changes"><thead><tr><th>Change</th><th>Before → After</th></tr></thead><tbody>${changedRows.join('')}</tbody></table>` : ''}
        ${eventRows ? `<ol class="damage-events">${eventRows}</ol>` : '<div class="hint">No damage recorded.</div>'}`;
    damageReportEl.querySelector('.sd-close').addEventListener('click', () => {
        damageReportOpen = false;
        renderDamageReport();
    });
}
