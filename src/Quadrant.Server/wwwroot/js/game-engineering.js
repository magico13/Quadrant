// Engineering phase and science officer action dialogs.
// Classic page script; shares the game client global scope.

// ── Engineering ──
function renderEngineering() {
    grid.setHighlights([]);
    grid.setTargets([]);
    const ships = state.ships.filter(s => !s.isDestroyed && !s.isDisabled);
    actionPanel.innerHTML = `
        <div class="actor-name">Engineering</div>
        <div class="hint">Each engineer takes one action. Divert and Hold It Together are once per ship each round. Patch progress carries over.</div>
        ${ships.map(ship => {
            const engineers = (ship.crew || []).map((crew, i) => ({ ...crew, i })).filter(c => c.role === 'Engineer' && !c.actionUsed);
            const used = new Set(ship.engineeringActionsUsed || []);
            const holdTargets = [];
            if (!used.has('Hold It Together')) for (const system of (ship.systems || []).filter(system => system.system !== 'WeaponsArray' && system.effectiveCondition !== 'None'))
                holdTargets.push(system.system);
            if (!used.has('Hold It Together')) for (const arc of (ship.weaponArcs || []).filter(arc => arc.effectiveCondition !== 'None'))
                holdTargets.push(`WeaponsArray|${arc.installedArc}`);
            const patchTargets = engineerPatchTargets(ship);
            const patchProgress = (ship.patches || []).filter(p => !p.appliedAt)
                .map(p => `${p.weaponArc ? `${p.weaponArc} ` : ''}${p.system === 'WeaponsArray' ? 'Weapons' : p.system} ${p.successfulChecks}/${p.requiredChecks}`);
            return `<div class="detail-section" style="color:${teamColor(ship.teamId)}">${ship.name} (Team ${ship.teamId + 1})</div>
                ${engineers.length ? `<div class="eng-row"><label>Engineer</label><select id="engineer-${ship.id}">${engineers.map(c => `<option value="${c.i}">${c.primaryRole} ${c.i + 1} (${c.roleBonus >= 0 ? '+' : ''}${c.roleBonus})</option>`).join('')}</select></div>
                ${!used.has('Divert') ? `<button class="btn btn-ghost" data-divert-ship="${ship.id}">Divert Power</button>` : ''}
                ${holdTargets.length ? `<button class="btn btn-ghost" data-hold-ship="${ship.id}">Hold It Together</button>` : ''}
                ${patchTargets.length ? `<button class="btn btn-ghost" data-patch-ship="${ship.id}">Patch System</button>` : ''}
                ${patchProgress.length ? `<div class="hint">Patch progress · ${patchProgress.map(reportEscape).join(' · ')}</div>` : ''}
                ${used.has('Divert') && !holdTargets.length && !patchTargets.length ? '<div class="hint">No available engineer action.</div>' : ''}` : '<div class="hint">No available engineer action.</div>'}`;
        }).join('')}
        <button class="btn btn-primary" id="btn-end-eng">End Engineering</button>`;
    for (const ship of ships) {
        const engineerSelect = document.getElementById(`engineer-${ship.id}`);
        if (!engineerSelect) continue;
        const holdButton = actionPanel.querySelector(`[data-hold-ship="${ship.id}"]`);
        const patchButton = actionPanel.querySelector(`[data-patch-ship="${ship.id}"]`);
        const divertButton = actionPanel.querySelector(`[data-divert-ship="${ship.id}"]`);
        const update = () => {
            const crew = ship.crew[Number(engineerSelect.value)];
            if (holdButton) holdButton.textContent = `Hold It Together (${crew.engineeringChances.hold}% success)`;
            if (divertButton) divertButton.textContent = `Divert Power (${crew.engineeringChances.divert}% success)`;
        };
        engineerSelect.addEventListener('change', update);
        update();
        divertButton?.addEventListener('click', () => startDivert(ship.id, Number(engineerSelect.value)));
        holdButton?.addEventListener('click', () => startHold(ship.id, Number(engineerSelect.value)));
        patchButton?.addEventListener('click', () => startPatch(ship.id, Number(engineerSelect.value)));
    }
    document.getElementById('btn-end-eng').addEventListener('click', async () => {
        await api(`/${roomCode}/end-engineering`);
        await fetchState();
    });
}

function holdShip() {
    return pendingHold ? state?.ships.find(ship => ship.id === pendingHold.shipId) : null;
}

function canContinueHold() {
    const pending = pendingHold;
    const ship = holdShip();
    const engineer = ship?.crew?.[pending?.crewIndex];
    return !!pending && !!ship && !!engineer && state.phase === 'Engineering' && !state.isComplete &&
        state.round === pending.round && !ship.isDestroyed && !ship.isDisabled && engineer.role === 'Engineer' &&
        !engineer.actionUsed && !(ship.engineeringActionsUsed || []).includes('Hold It Together');
}

function clearHold() {
    pendingHold = null;
    if (holdDialog.open) holdDialog.close();
}

function startHold(shipId, crewIndex) {
    const ship = state?.ships.find(candidate => candidate.id === shipId);
    if (state?.phase !== 'Engineering' || !ship || ship.isDestroyed || ship.isDisabled ||
        ship.crew[crewIndex]?.role !== 'Engineer' || ship.crew[crewIndex]?.actionUsed ||
        (ship.engineeringActionsUsed || []).includes('Hold It Together')) return;
    pendingHold = { shipId, crewIndex, round: state.round, target: null, weaponArc: null,
        visibleArc: null, submitting: false, error: '' };
    detailsShipId = null;
    hideShipDetails();
    renderHoldDialog();
}

function renderHoldDialog() {
    if (!canContinueHold()) {
        clearHold();
        renderEngineering();
        return;
    }
    const pending = pendingHold;
    const ship = holdShip();
    const engineer = ship.crew[pending.crewIndex];
    const chance = engineer.engineeringChances?.hold ?? null;
    holdDialog.innerHTML = `<div class="sd-header"><span class="sd-name" id="hold-title">Hold It Together · ${reportEscape(ship.name)}</span><button class="btn btn-ghost sd-close" id="btn-close-hold" aria-label="Close Hold It Together dialog" ${pending.submitting ? 'disabled' : ''}>×</button></div>
        ${ShipSchematic.render(ship, { mode: 'hold', selectedSystem: pending.target, selectedArc: pending.visibleArc })}
        <div class="system-picker-actions hold-dialog-actions"><button class="btn btn-primary" id="btn-confirm-hold" ${pending.submitting || !pending.target ? 'disabled' : ''}>${pending.submitting ? 'Attempting…' : `Confirm Hold It Together${chance != null ? ` (${chance}%)` : ''}`}</button><button class="btn btn-ghost" id="btn-cancel-hold" ${pending.submitting ? 'disabled' : ''}>Cancel</button></div>
        <div class="hint" id="hold-error" role="status">${reportEscape(pending.error)}</div>`;
    ShipSchematic.bind(holdDialog, ship, {
        mode: 'hold', selectedSystem: pending.target, selectedArc: pending.visibleArc,
        onSelect: (system, visibleArc, installedArc) => {
            pending.target = system;
            pending.visibleArc = visibleArc;
            pending.weaponArc = system === 'WeaponsArray' ? installedArc : null;
            pending.error = '';
            renderHoldDialog();
        }
    });
    document.getElementById('btn-confirm-hold').addEventListener('click', confirmHold);
    document.getElementById('btn-close-hold').addEventListener('click', () => { clearHold(); renderEngineering(); });
    document.getElementById('btn-cancel-hold').addEventListener('click', () => { clearHold(); renderEngineering(); });
    holdDialog.oncancel = event => {
        if (pendingHold?.submitting) { event.preventDefault(); return; }
        clearHold();
        renderEngineering();
    };
    if (!holdDialog.open) holdDialog.showModal();
}

async function confirmHold() {
    if (!canContinueHold() || pendingHold.submitting || !pendingHold.target) return;
    const pending = pendingHold;
    pending.submitting = true;
    pending.error = '';
    renderHoldDialog();
    try {
        await api(`/${roomCode}/engineering`, {
            shipId: pending.shipId, crewIndex: pending.crewIndex, action: 'Hold It Together',
            target: pending.target, weaponArc: pending.weaponArc
        });
    } catch (err) {
        pending.submitting = false;
        pending.error = `Could not attempt Hold It Together: ${err.message}`;
        renderHoldDialog();
        return;
    }
    clearHold();
    try { await fetchState(); }
    catch (err) { renderEngineering(); addLog('error', `Hold It Together was submitted, but state could not refresh: ${err.message}`); }
}

function engineerPatchTargets(ship) {
    const completed = new Set((ship.patches || []).filter(p => p.active).map(p => `${p.system}|${p.weaponArc || ''}`));
    const targets = (ship.systems || []).filter(s => s.system !== 'WeaponsArray' && s.condition !== 'None')
        .map(s => `${s.system}|`);
    for (const arc of (ship.weaponArcs || []).filter(a => a.condition !== 'None')) targets.push(`WeaponsArray|${arc.installedArc}`);
    return targets.filter(key => !completed.has(key));
}

function patchShip() {
    return pendingPatch ? state?.ships.find(ship => ship.id === pendingPatch.shipId) : null;
}

function canContinuePatch() {
    const pending = pendingPatch;
    const ship = patchShip();
    const engineer = ship?.crew?.[pending?.crewIndex];
    return !!pending && !!ship && !!engineer && state.phase === 'Engineering' && !state.isComplete &&
        state.round === pending.round && !ship.isDestroyed && !ship.isDisabled && engineer.role === 'Engineer' && !engineer.actionUsed;
}

function clearPatch() {
    pendingPatch = null;
    if (holdDialog.open) holdDialog.close();
}

function startPatch(shipId, crewIndex) {
    const ship = state?.ships.find(candidate => candidate.id === shipId);
    if (state?.phase !== 'Engineering' || !ship || ship.isDestroyed || ship.isDisabled ||
        ship.crew[crewIndex]?.role !== 'Engineer' || ship.crew[crewIndex]?.actionUsed || !engineerPatchTargets(ship).length) return;
    pendingPatch = { shipId, crewIndex, round: state.round, target: null, visibleArc: null, weaponArc: null,
        fast: false, submitting: false, error: '' };
    detailsShipId = null;
    hideShipDetails();
    renderPatchDialog();
}

function selectedPatchTarget(ship, pending = pendingPatch) {
    if (!pending?.target) return null;
    const arc = pending.target === 'WeaponsArray' ? pending.weaponArc : null;
    const crewTarget = ship.crew[pending.crewIndex].engineeringChances?.patchTargets?.find(t =>
        t.system === pending.target && (t.weaponArc || null) === arc);
    const progress = (ship.patches || []).find(p => p.system === pending.target && (p.weaponArc || null) === arc && (p.active || !p.appliedAt));
    return crewTarget ? { ...crewTarget,
        requiredChecks: progress?.requiredChecks ?? crewTarget.requiredChecks,
        successfulChecks: progress?.successfulChecks ?? crewTarget.successfulChecks ?? 0,
        fastSelected: progress?.fast ?? crewTarget.fastSelected,
        alreadyPatched: progress?.active ?? crewTarget.alreadyPatched,
        system: pending.target, weaponArc: arc } : null;
}

function patchChance(ship, pending = pendingPatch) {
    const target = selectedPatchTarget(ship, pending);
    if (!target) return null;
    return target.fastSelected ? target.fast : pending.fast ? target.fast : target.standard;
}

function patchOutcome(condition) {
    return ({ Glitching: 'Healthy', Malfunctioning: 'Glitching', Wrecked: 'Malfunctioning' })[condition] || 'Healthy';
}

function patchDescription(ship, pending = pendingPatch) {
    const target = selectedPatchTarget(ship, pending);
    if (!target) return 'Choose a damaged system or weapon arc.';
    const required = target.requiredChecks ?? Math.max(1,
        ({ Glitching: 1, Malfunctioning: 2, Wrecked: 3 })[target.condition] - (pending.fast && target.condition !== 'Glitching' ? 1 : 0));
    const progress = target.successfulChecks || 0;
    const remaining = Math.max(0, required - progress);
    return `${target.condition} → ${patchOutcome(target.condition)} · ${progress}/${required} successes${remaining ? ` · ${remaining} left` : ''}`;
}

function renderPatchDialog() {
    if (!canContinuePatch()) { clearPatch(); renderEngineering(); return; }
    const pending = pendingPatch;
    const ship = patchShip();
    const engineer = ship.crew[pending.crewIndex];
    const target = selectedPatchTarget(ship);
    const lockedFast = target?.fastSelected;
    const fastAvailable = !!target && ['Malfunctioning', 'Wrecked'].includes(target.condition);
    const chance = patchChance(ship);
    holdDialog.innerHTML = `<div class="sd-header"><span class="sd-name" id="patch-title">Patch System · ${reportEscape(ship.name)}</span><button class="btn btn-ghost sd-close" id="btn-close-patch" aria-label="Close Patch dialog" ${pending.submitting ? 'disabled' : ''}>×</button></div>
        ${ShipSchematic.render(ship, { mode: 'patch', selectedSystem: pending.target, selectedArc: pending.visibleArc })}
        ${fastAvailable ? `<label class="patch-fast-option"><input type="checkbox" id="patch-fast" ${pending.fast || lockedFast === true ? 'checked' : ''} ${pending.submitting || lockedFast != null ? 'disabled' : ''}> Fast</label>` : ''}
        <div class="hint patch-preview" id="patch-preview">${target ? reportEscape(patchDescription(ship)) : 'Select a damaged system.'}</div>
        <div class="patch-chance" id="patch-chance">Success chance: ${chance != null ? `${chance}%` : '—'}</div>
        <div class="system-picker-actions hold-dialog-actions"><button class="btn btn-primary" id="btn-confirm-patch" ${pending.submitting || !target || target.alreadyPatched || target.appliedAt ? 'disabled' : ''}>${pending.submitting ? 'Attempting…' : 'Confirm Patch'}</button><button class="btn btn-ghost" id="btn-cancel-patch" ${pending.submitting ? 'disabled' : ''}>Cancel</button></div>
        <div class="hint" id="patch-error" role="status">${reportEscape(pending.error)}</div>`;
    ShipSchematic.bind(holdDialog, ship, {
        mode: 'patch', selectedSystem: pending.target, selectedArc: pending.visibleArc,
        patchDescription: (system, visibleArc) => {
            const installedArc = system === 'WeaponsArray' ? ship.weaponArcs.find(a => a.arc === visibleArc)?.installedArc : null;
            const candidate = { ...pending, target: system, visibleArc, weaponArc: installedArc };
            return patchDescription(ship, candidate);
        },
        onSelect: (system, visibleArc, installedArc) => {
            pending.target = system;
            pending.visibleArc = visibleArc;
            pending.weaponArc = system === 'WeaponsArray' ? installedArc : null;
            const progress = selectedPatchTarget(ship);
            pending.fast = progress?.fastSelected ?? false;
            pending.error = '';
            renderPatchDialog();
        }
    });
    document.getElementById('patch-fast')?.addEventListener('change', event => {
        pending.fast = event.target.checked;
        renderPatchDialog();
    });
    document.getElementById('btn-confirm-patch').addEventListener('click', confirmPatch);
    document.getElementById('btn-close-patch').addEventListener('click', () => { clearPatch(); renderEngineering(); });
    document.getElementById('btn-cancel-patch').addEventListener('click', () => { clearPatch(); renderEngineering(); });
    holdDialog.oncancel = event => {
        if (pendingPatch?.submitting) { event.preventDefault(); return; }
        clearPatch();
        renderEngineering();
    };
    if (!holdDialog.open) holdDialog.showModal();
}

async function confirmPatch() {
    if (!canContinuePatch() || pendingPatch.submitting || !pendingPatch.target) return;
    const pending = pendingPatch;
    pending.submitting = true;
    pending.error = '';
    renderPatchDialog();
    try {
        await api(`/${roomCode}/engineering`, { shipId: pending.shipId, crewIndex: pending.crewIndex,
            action: 'Patch', target: pending.target, weaponArc: pending.weaponArc, fastPatch: pending.fast });
    } catch (err) {
        pending.submitting = false;
        pending.error = `Could not attempt Patch: ${err.message}`;
        renderPatchDialog();
        return;
    }
    clearPatch();
    try { await fetchState(); }
    catch (err) { renderEngineering(); addLog('error', `Patch was submitted, but state could not refresh: ${err.message}`); }
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
