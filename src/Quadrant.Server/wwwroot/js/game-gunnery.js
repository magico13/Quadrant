// Gunnery targeting and combat completion rendering.
// Classic page script; shares the game client global scope.

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
