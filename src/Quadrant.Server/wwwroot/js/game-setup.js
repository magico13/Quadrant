// Fleet setup and combat crew role selection.
// Classic page script; shares the game client global scope.

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

// ── Setup ──
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
