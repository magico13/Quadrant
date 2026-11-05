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
let selectedTargetArcs = new Map();
let detailsShipId = null;  // ship currently shown in the details panel
let pendingMove = null;    // helm: { option, actorId } — move previewed, awaiting confirmation
let refreshGunnery = null; // module-scope handle to the current gunnery refresh fn

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
    selectedTargetArcs.clear();
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
                <span class="cc-tier">Tier ${formatTier(s.tier)}</span>
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

    phaseEl.textContent = state.phase === 'CrewSetup' ? 'Crew Setup' : state.phase;
    roundEl.textContent = `Round ${state.round}`;
    roomEl.textContent = roomCode || '';

    // Feed ships to the grid.
    const ships = state.ships.map(s => ({
        id: s.id, name: s.name, teamId: s.teamId, q: s.q, r: s.r, facing: s.facing,
        hull: s.hull, maxHull: s.maxHull, shields: s.shields, maxShield: s.maxShield,
    }));
    grid.setShips(ships);
    grid.setProjectiles(state.projectiles || []);

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
        case 'CrewSetup': renderRoles(); break;
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
            if (!used.has('Divert')) for (const target of ['Engines', 'Science', 'Weapons', 'Shields'])
                options.push(`<option value="Divert|${target}">Divert — ${target}</option>`);
            if (!used.has('Hold It Together')) for (const system of ship.systems || [])
                options.push(`<option value="Hold It Together|${system.system}">Hold It Together — ${system.system} (${system.condition})</option>`);
            return `<div class="detail-section" style="color:${teamColor(ship.teamId)}">${ship.name} (Team ${ship.teamId + 1})</div>
                ${engineers.length && options.length ? `<div class="eng-row"><label>Engineer</label><select id="engineer-${ship.id}">${engineers.map(c => `<option value="${c.i}">${c.primaryRole} ${c.i + 1} (${c.roleBonus >= 0 ? '+' : ''}${c.roleBonus})</option>`).join('')}</select></div>
                <div class="eng-row"><label>Action</label><select id="engineering-action-${ship.id}">${options.join('')}</select></div>
                <div id="shield-allocation-${ship.id}" hidden><div class="hint">Distribute ${ship.shieldDivertMax} shield points (total capacity ${ship.shieldCapacity}).</div>
                ${['forward', 'starboard', 'aft', 'port'].map(q => `<div class="eng-row"><label>${q[0].toUpperCase() + q.slice(1)} (${ship.shields[q]} current)</label><input type="number" id="shield-${ship.id}-${q}" min="0" max="${ship.shieldDivertMax}" value="0"></div>`).join('')}</div>
                <button class="btn btn-ghost" data-engineering-ship="${ship.id}">Attempt Action</button>
                <div class="hint" id="engineering-error-${ship.id}"></div>` : '<div class="hint">No available engineer action.</div>'}`;
        }).join('')}
        <button class="btn btn-primary" id="btn-end-eng">End Engineering</button>`;
    for (const ship of ships) {
        const select = document.getElementById(`engineering-action-${ship.id}`);
        if (!select) continue;
        const memoryKey = `quadrant:${roomCode}:engineering:${ship.id}`;
        const remembered = localStorage.getItem(memoryKey);
        if (remembered && [...select.options].some(option => option.value === remembered))
            select.value = remembered;
        const shieldPanel = document.getElementById(`shield-allocation-${ship.id}`);
        const update = () => { shieldPanel.hidden = select.value !== 'Divert|Shields'; };
        select.addEventListener('change', update);
        select.addEventListener('change', () => localStorage.setItem(memoryKey, select.value));
        update();
        actionPanel.querySelector(`[data-engineering-ship="${ship.id}"]`).addEventListener('click', async () => {
            const [action, target] = select.value.split('|');
            const shields = target === 'Shields' ? Object.fromEntries(['forward', 'starboard', 'aft', 'port']
                .map(q => [q, Number(document.getElementById(`shield-${ship.id}-${q}`).value)])) : null;
            try {
                await api(`/${roomCode}/engineering`, { shipId: ship.id, crewIndex: Number(document.getElementById(`engineer-${ship.id}`).value), action, target, shields });
                await fetchState();
            } catch (err) { document.getElementById(`engineering-error-${ship.id}`).textContent = `Could not attempt action: ${err.message}`; }
        });
    }
    document.getElementById('btn-end-eng').addEventListener('click', async () => {
        await api(`/${roomCode}/end-engineering`);
        await fetchState();
    });
}

// ── Helm ──
function renderHelm() {
    const actor = currentActor();
    if (!actor) { grid.setHighlights([]); grid.setTargets([]); grid.setGhost(null); grid.setPath(null); actionPanel.innerHTML = ''; return; }

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
            <label>Weapon <select id="free-weapon">${attack.weapons.map(w => `<option value="${w.index}">${w.name} (${w.arc})</option>`).join('')}</select></label>
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

    // Highlight every reachable hex (union over facings).
    grid.setHighlights((state.options?.items || []).map(m => m.position));
    grid.setTargets([]);

    const facingNames = ['NW', 'NE', 'E', 'SE', 'SW', 'W'];
    const pending = pendingMove?.option;
    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(actor.teamId)}">${actor.name}</div>
        ${actor.helmAction === 'Glide' ? '<div class="hint">Glide: half speed, +2 distance between turns; uses a minor crew action.</div>' : actor.helmAction === 'None' ? '<div class="hint">No crew member can move this ship.</div>' : ''}
        ${pending
            ? `<div class="hint">${pending.isTurnInPlace
                    ? `Turning in place to face ${facingNames[pending.finalFacing]}.`
                    : `Moving to (${pending.position.q}, ${pending.position.r}), facing ${facingNames[pending.finalFacing]}.`} Click another hex or your ship to change, or confirm.</div>
               <button class="btn btn-primary" id="btn-confirm-move">Confirm Move</button>
               <button class="btn btn-ghost" id="btn-cancel-move">Cancel</button>`
            : state.pendingStunt
                ? `<div class="hint">${state.pendingStunt}: choose a highlighted destination to preview the result.</div>${(state.options?.items || []).some(m => m.position.q === actor.q && m.position.r === actor.r) ? '<button class="btn btn-ghost" id="btn-stunt-hold">Preview current hex</button>' : ''}`
                : `<div class="hint">Click a highlighted hex to preview a move.${actor.helmAction === 'Pilot' ? ' Click your own ship to turn in place.' : ''}</div>
                   <button class="btn btn-ghost" id="btn-hold">Hold Position</button>
                   ${actor.helmAction === 'Pilot' ? '<div class="detail-section">Stunts</div><button class="btn btn-ghost" data-stunt="Back Off">Back Off</button> <button class="btn btn-ghost" data-stunt="Flip and Burn">Flip and Burn</button>' : ''}`}`;

    const scienceShips = state.ships.filter(ship => !ship.isDestroyed && !ship.isDisabled &&
        ((ship.crew || []).some(crew => crew.role === 'Science Officer' && !crew.actionUsed) || (ship.targetedSystems || []).length > 0));
    if (scienceShips.length) {
        actionPanel.innerHTML += `<div class="detail-section">Science Officer Actions</div>
            <div class="hint">Science actions use one available Science Officer's action. Target System locks remain until that ship hits the selected enemy.</div>
            ${scienceShips.map(ship => {
                const officers = ship.crew.map((crew, i) => ({ ...crew, i })).filter(crew => crew.role === 'Science Officer' && !crew.actionUsed);
                const pending = ship.targetedSystems || [];
                const pendingText = pending.length
                    ? `<div class="hint">Targeted: ${pending.map(lock => `${state.ships.find(target => target.id === lock.targetShipId)?.name || 'Unknown ship'} — ${lock.system}`).join('; ')}</div>` : '';
                const enemies = state.ships.filter(target => target.teamId !== ship.teamId && !target.isDestroyed);
                const sensorRange = ship.sensorRangeHexes || 0;
                const availableEnemies = enemies.filter(target => {
                    const distance = Hex.distance({ q: ship.q, r: ship.r }, { q: target.q, r: target.r });
                    return !pending.some(lock => lock.targetShipId === target.id) && (!sensorRange || distance <= sensorRange * 5);
                });
                const actionControls = officers.length ? `
                    <div class="eng-row"><label>Science Officer</label><select id="science-crew-${ship.id}">${officers.map(c => `<option value="${c.i}">Officer ${c.i + 1} (${c.scienceBonus >= 0 ? '+' : ''}${c.scienceBonus})</option>`).join('')}</select></div>
                    <div class="eng-row"><label>Action</label><select id="science-action-${ship.id}">
                        <option value="Balance">Balance Shields</option>
                        ${ship.hasSensors && availableEnemies.length ? '<option value="Target System">Target System (Push)</option>' : ''}
                    </select></div>
                    <div id="science-balance-${ship.id}">
                        <div class="eng-row"><label>Distribution</label><select id="science-balance-mode-${ship.id}"><option value="shift">Move points between quadrants</option><option value="even">Distribute evenly</option></select></div>
                        <div id="science-shift-${ship.id}">
                            <div class="eng-row"><label>From</label><select id="science-from-${ship.id}">${['Forward','Starboard','Aft','Port'].map(q => `<option value="${q}">${q} (${ship.shields[q.toLowerCase()]})</option>`).join('')}</select></div>
                            <div class="eng-row"><label>To</label><select id="science-to-${ship.id}">${['Forward','Starboard','Aft','Port'].map(q => `<option value="${q}">${q}</option>`).join('')}</select></div>
                            <div class="eng-row"><label>Shield points</label><input type="number" id="science-amount-${ship.id}" min="1" value="1"></div>
                        </div>
                        <div class="hint">Each quadrant must retain at least ${Math.ceil((ship.shields.forward + ship.shields.starboard + ship.shields.aft + ship.shields.port) / 10)} SP.</div>
                    </div>
                    <div id="science-target-${ship.id}" hidden>
                        <div class="eng-row"><label>Enemy</label><select id="science-enemy-${ship.id}">${availableEnemies.map(target => {
                            const distance = Hex.distance({ q: ship.q, r: ship.r }, { q: target.q, r: target.r });
                            const rangePenalty = sensorRange ? -2 * Math.max(0, Math.ceil(distance / sensorRange) - 1) : 0;
                            return `<option value="${target.id}">${target.name} — ${distance} hex${distance === 1 ? '' : 'es'}${rangePenalty ? `, sensor check ${rangePenalty}` : ''} (+${target.countermeasures} countermeasures)</option>`;
                        }).join('')}</select></div>
                        <div class="eng-row"><label>System</label><select id="science-system-${ship.id}"><option value="PowerCore">Core</option><option value="Engines">Engines</option><option value="LifeSupport">Life Support</option><option value="Sensors">Sensors</option><option value="WeaponsArray">Weapons</option></select></div>
                        <div class="hint">The listed Computers bonus already includes ship system modifiers. ${sensorRange ? `Active sensor range: ${sensorRange} hexes per increment, maximum ${sensorRange * 5} hexes; checks take –2 per increment beyond the first.` : 'No sensor range is listed for this ship.'}</div>
                    </div>
                    <button class="btn btn-ghost" data-science-ship="${ship.id}">Attempt Science Action</button>
                    <div class="hint" id="science-error-${ship.id}"></div>` : '<div class="hint">No Science Officer action available this round.</div>';
                return `<div class="science-action-card">
                    <div class="detail-section" style="color:${teamColor(ship.teamId)}">${ship.name} (Team ${ship.teamId + 1})</div>
                    ${pendingText}
                    ${actionControls}
                </div>`;
            }).join('')}`;

        for (const ship of scienceShips) {
            const actionSelect = document.getElementById(`science-action-${ship.id}`);
            if (!actionSelect) continue;
            const balancePanel = document.getElementById(`science-balance-${ship.id}`);
            const targetPanel = document.getElementById(`science-target-${ship.id}`);
            const modeSelect = document.getElementById(`science-balance-mode-${ship.id}`);
            const shiftPanel = document.getElementById(`science-shift-${ship.id}`);
            actionSelect.addEventListener('change', () => {
                const targetMode = actionSelect.value === 'Target System';
                balancePanel.hidden = targetMode;
                targetPanel.hidden = !targetMode;
            });
            modeSelect.addEventListener('change', () => { shiftPanel.hidden = modeSelect.value === 'even'; });
            actionPanel.querySelector(`[data-science-ship="${ship.id}"]`).addEventListener('click', async () => {
                const action = actionSelect.value;
                const payload = { shipId: ship.id, crewIndex: Number(document.getElementById(`science-crew-${ship.id}`).value), action,
                    balanceEvenly: action === 'Balance' && modeSelect.value === 'even' };
                if (action === 'Balance' && !payload.balanceEvenly) {
                    payload.fromQuadrant = document.getElementById(`science-from-${ship.id}`).value;
                    payload.toQuadrant = document.getElementById(`science-to-${ship.id}`).value;
                    payload.amount = Number(document.getElementById(`science-amount-${ship.id}`).value);
                }
                if (action === 'Target System') {
                    payload.targetShipId = document.getElementById(`science-enemy-${ship.id}`).value;
                    payload.system = document.getElementById(`science-system-${ship.id}`).value;
                }
                try { await api(`/${roomCode}/science`, payload); await fetchState(); }
                catch (err) { document.getElementById(`science-error-${ship.id}`).textContent = `Could not attempt action: ${err.message}`; }
            });
        }
    }

    if (pending) {
        document.getElementById('btn-confirm-move').addEventListener('click', confirmMove);
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
            o.arc === 'AllArcs' || o.arc === selectedTargetArcs.get(o.targetShipId));
        if (!eligible.length) continue;
        const closest = eligible.reduce((best, o) => o.range < best.range ? o : best);
        selectedShots.set(widx, closest.targetShipId);
    }

    const usableIndices = [...weapons.keys()].filter(i => !fired.has(i));
    if (activeWeapon === null || fired.has(activeWeapon) || !weapons.has(activeWeapon))
        activeWeapon = usableIndices[0] ?? null;

    const chipIndices = new Set([...weapons.keys(), ...fired, ...(actor.weapons || []).map((w, i) => w.limitedFireUses > 0 && w.usesRemaining === 0 ? i : null).filter(i => i !== null)]);
    const chips = [...chipIndices].sort((a, b) => a - b).map(widx => {
        const w = actor.weapons?.[widx];
        if (!w) return '';
        const arcLabel = w.arc && w.arc !== 'AllArcs' ? ` (${w.arc})` : '';
        const dmg = ` · ${w.damage}`;
        const ammo = w.limitedFireUses > 0 ? ` · ${w.usesRemaining}/${w.limitedFireUses}` : '';
        const exhausted = w.limitedFireUses > 0 && w.usesRemaining === 0;
        const isFired = fired.has(widx);
        return `<button class="weapon-chip${activeWeapon === widx ? ' active' : ''}${isFired ? ' fired' : ''}" data-weapon="${widx}" ${(exhausted || isFired) ? `disabled title="${isFired ? 'Already fired this round' : 'Out of ammunition'}"` : ''}>${w.name}${arcLabel}${dmg}${ammo}${isFired ? ' · FIRED' : ''}</button>`;
    }).join('');

    actionPanel.innerHTML = `
        <div class="actor-name" style="color:${teamColor(actor.teamId)}">${actor.name}</div>
        <div class="hint">Choose a weapon and target, then fire it. Hits are resolved in order; damage waits until all ships finish Gunnery.</div>
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
            grid.setTargets(opts.map(o => [o.targetShipId, o.hitChancePercent]));
            const activeArcOption = opts.find(o => o.arcChoiceRequired);
            if (activeArcOption) {
                const selected = selectedTargetArcs.get(activeArcOption.targetShipId);
                arcChoiceEl.innerHTML = `<span class="hint">This target is on an arc boundary. Choose one firing arc for all attacks against it this round.</span><div class="weapon-chips">${activeArcOption.arcChoices.map(arc => `<button class="weapon-chip arc-choice${selected === arc ? ' active' : ''}" data-arc="${arc}" data-target="${activeArcOption.targetShipId}">${arc}</button>`).join('')}</div>`;
                arcChoiceEl.querySelectorAll('[data-arc]').forEach(button => button.addEventListener('click', () => {
                    selectedTargetArcs.set(button.dataset.target, button.dataset.arc);
                    refresh();
                }));
            } else {
                arcChoiceEl.innerHTML = '';
            }
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
                const status = fired.has(widx) ? ' · fired' : '';
                return `<span class="shot-item">${label} → ${t?.name ?? '?'}${rangeStr}${status}</span>`;
            }).join('')}</div>`;
        }

        const targetId = activeWeapon === null ? null : selectedShots.get(activeWeapon);
        const activeOption = activeWeapon === null ? null : (weapons.get(activeWeapon) || []).find(o => o.targetShipId === targetId);
        const missingArcChoice = !!activeOption?.arcChoiceRequired && !selectedTargetArcs.has(targetId);
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
                    o.arc === 'AllArcs' || o.arc === selectedTargetArcs.get(o.targetShipId));
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
        const response = await api(`/${roomCode}/fire`, { weaponIndex: activeWeapon, targetShipId, arc });
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
    const maxVis = inc * 3; // show up to 3 increments
    let perspective = ship;
    if (state?.phase === 'Helm' && pendingMove?.actorId === ship.id) {
        perspective = {
            ...ship,
            q: pendingMove.option.position.q,
            r: pendingMove.option.position.r,
            facing: pendingMove.option.finalFacing
        };
    }
    grid.setRangeHexesOnly(rangeBandHexes(perspective, w.minRange ?? 1, maxVis, w.arc, inc));
}

function clearWeaponRange() {
    grid.setRangeHexesOnly([]);
}

function showShipDetails(ship) {
    const shieldTotal = Object.values(ship.shields || {}).reduce((a, b) => a + b, 0);
    const shieldMax = ship.shieldCapacity ?? (ship.maxShield ?? 0) * 4;
    const arcs = ['forward', 'starboard', 'aft', 'port'];

    // Reset the pin if it no longer refers to this ship.
    if (pinnedWeapon && pinnedWeapon.shipId !== ship.id) {
        pinnedWeapon = null;
        clearWeaponRange();
    }

    const weaponsHtml = (ship.weapons || []).map((w, i) => `
        <div class="detail-weapon dw-hoverable" data-weapon-index="${i}">
            <span class="dw-name">${w.name}</span>
            <span class="dw-meta">${w.arc} · inc ${w.rangeIncrement ?? 5} · ${w.damage}${w.type ? ` · ${w.type}` : ''}${w.limitedFireUses > 0 ? ` · ${w.usesRemaining}/${w.limitedFireUses} shots` : ''}</span>
        </div>`).join('');

    const systemsHtml = (ship.damagedSystems || []).length
        ? `<div class="detail-section">Damaged Systems</div><ul class="detail-list detail-list-damaged">${ship.damagedSystems.map(s => {
            const effective = (ship.systems || []).find(x => x.system === s.system)?.effectiveCondition;
            return `<li>${s.system} — ${s.condition}${effective && effective !== s.condition ? ` (held: ${effective})` : ''}</li>`;
        }).join('')}</ul>`
        : '';

    const crewHtml = (ship.crew || []).map(crew => {
        const bonus = crew.roleBonus == null ? '' : ` ${crew.roleBonus >= 0 ? '+' : ''}${crew.roleBonus}`;
        return `<li>${crew.role}${bonus}</li>`;
    }).join('');

    shipDetailsEl.innerHTML = `
        <div class="sd-header" style="--team-color:${teamColor(ship.teamId)}">
            <span class="sd-name">${ship.name}</span>
            <button class="btn btn-ghost sd-close" title="Close">×</button>
        </div>
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
        <div class="detail-section">Shield Arcs</div>
        <div class="shield-arcs">${arcs.map(a => `
            <div class="sa-row"><span>${a[0].toUpperCase() + a.slice(1)}</span><b>${ship.shields?.[a] ?? 0}</b></div>`).join('')}</div>
        <div class="detail-section">Weapons</div>
        ${weaponsHtml || '<div class="hint">No weapons.</div>'}
        <div class="detail-section">Crew</div>
        ${crewHtml ? `<ul class="detail-list">${crewHtml}</ul>` : '<div class="hint">No crew recorded.</div>'}
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
    if (state.freeAttack) return;

    // During Helm, clicking your own ship offers the Turn In Place stunt.
    if (state.phase === 'Helm' && ship.id === state.currentActorId) {
        if (state.pendingStunt) {
            const option = (state.options?.items || []).find(m => m.position.q === ship.q && m.position.r === ship.r);
            if (option) setPendingMove(option);
        } else if (currentActor()?.helmAction === 'Pilot') showTurnInPlacePicker(ship);
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
