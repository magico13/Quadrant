// Helm movement, stunts, and arc/range visualization helpers.
// Classic page script; shares the game client global scope.

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
