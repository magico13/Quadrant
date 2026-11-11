// Gunnery targeting and combat completion rendering.
// Classic page script; shares the game client global scope.

// ── Gunnery ──
function gunneryChoiceKey(choice) {
    return `${choice.crewIndex ?? 'ship'}:${choice.fireAtWill ? 'fire-at-will' : choice.snapShot ? 'snap' : 'shoot'}`;
}

function currentGunneryOptions() {
    const choice = (state?.options?.gunners || []).find(gunner => gunneryChoiceKey(gunner) === selectedGunneryKey);
    return choice?.targets || state?.options?.items || [];
}

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
        selectedGunneryKey = null;
    }
    if (renderGunnery.round !== state.round) {
        selectedShots.clear();
        selectedTargetArcs.clear();
        activeWeapon = null;
        selectedGunneryKey = null;
        renderGunnery.round = state.round;
    }

    const gunners = state.options?.gunners || [];
    if (!gunners.some(gunner => gunneryChoiceKey(gunner) === selectedGunneryKey)) {
        const best = gunners.find(gunner => gunner.targets?.length) || gunners[0];
        selectedGunneryKey = best ? gunneryChoiceKey(best) : null;
    }
    const gunner = gunners.find(candidate => gunneryChoiceKey(candidate) === selectedGunneryKey);
    const options = gunner?.targets || [];

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
    const compareWeapons = (a, b) => (actor.weapons?.[b]?.averageDamage ?? 0) - (actor.weapons?.[a]?.averageDamage ?? 0) || a - b;
    const usableIndices = [...weapons.keys()].filter(i => !fired.has(i)).sort(compareWeapons);
    if (activeWeapon === null || fired.has(activeWeapon) || !weapons.has(activeWeapon))
        activeWeapon = usableIndices[0] ?? null;
    if (activeWeapon !== null && !selectedShots.has(activeWeapon)) {
        const opts = weapons.get(activeWeapon) || [];
        if (opts.length) assignGunneryTarget(activeWeapon, opts.reduce((best, o) => o.range < best.range ? o : best).targetShipId);
    }

    const chips = usableIndices.map(widx => {
        const w = actor.weapons?.[widx];
        if (!w) return '';
        const arcLabel = w.arc && w.arc !== 'AllArcs' ? ` (${w.arc}${w.broadArc ? ' + Broad Arc' : ''})` : '';
        const dmg = ` · ${w.damage} · avg ${Number(w.averageDamage ?? 0).toFixed(1).replace(/\.0$/, '')}`;
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
        <div class="hint">Choose a gunner, weapon and target, then fire. Damage applies after Gunnery.</div>
        ${actor.barrelRollActive ? '<div class="hint">Barrel Roll: port and starboard shields and weapon arcs are swapped for this Gunnery phase.</div>' : ''}
        <div class="hint">${actor.crewShotsRemaining} shot${actor.crewShotsRemaining === 1 ? '' : 's'} available.</div>
        ${gunners.length ? `<div class="eng-row"><label for="gunner-select">Gunner</label><select id="gunner-select">${gunners.map(g => `<option value="${gunneryChoiceKey(g)}" ${gunneryChoiceKey(g) === selectedGunneryKey ? 'selected' : ''}>${g.crewIndex == null ? 'Ship' : `${g.snapShot ? 'Crew' : 'Gunner'} ${g.crewIndex + 1}`} (${g.bonus >= 0 ? '+' : ''}${g.bonus})${g.fireAtWill ? ` · Fire at Will (${g.shots} left)` : g.snapShot ? ' · Snap Shot' : ''}</option>`).join('')}</select></div>` : ''}
        ${gunner && gunner.crewIndex != null && !gunner.fireAtWill && !gunner.snapShot && usableIndices.length > 1 ? '<button class="btn btn-ghost" id="btn-fire-at-will" title="Spend this gunner’s action for two shots at −4.">Fire at Will (−4)</button>' : ''}
        <div id="arc-choice" class="target-info"></div>
        <div class="weapon-chips">${chips || '<span class="hint">No weapons can fire.</span>'}</div>
        <div id="target-info" class="target-info"></div>
        <button class="btn btn-primary" id="btn-fire" disabled>Fire</button>
        <button class="btn btn-ghost" id="btn-end-fire">End Gunnery Turn</button>`;

    const fireBtn = document.getElementById('btn-fire');
    const targetInfo = document.getElementById('target-info');
    const arcChoiceEl = document.getElementById('arc-choice');

    document.getElementById('gunner-select')?.addEventListener('change', event => {
        selectedGunneryKey = event.target.value;
        renderGunnery();
    });
    document.getElementById('btn-fire-at-will')?.addEventListener('click', async event => {
        event.currentTarget.disabled = true;
        try {
            await api(`/${roomCode}/fire-at-will`, { crewIndex: gunner.crewIndex });
            selectedGunneryKey = null;
            await fetchState();
        } catch (err) {
            event.currentTarget.disabled = false;
            addLog('error', `Could not activate Fire at Will: ${err.message}`);
        }
    });

    function refresh() {
        // Highlight the targets reachable by the active weapon.
        if (activeWeapon !== null) {
            const opts = weapons.get(activeWeapon) || [];
            grid.setTargets(opts.map(o => [o.targetShipId, o.arcHitChances?.[selectedTargetArcs.get(o.targetShipId)] ?? o.hitChancePercent]));
            const activeArcOption = opts.find(o => o.targetShipId === selectedShots.get(activeWeapon) && o.arcChoiceRequired);
            if (activeArcOption) {
                const legalArcs = (activeArcOption.arcChoices || []).filter(arc => activeArcOption.arcHitChances?.[arc] != null);
                let selected = selectedTargetArcs.get(activeArcOption.targetShipId);
                if (!legalArcs.includes(selected) && legalArcs.length) {
                    selected = legalArcs.reduce((best, arc) => activeArcOption.arcHitChances[arc] > activeArcOption.arcHitChances[best] ? arc : best);
                    selectedTargetArcs.set(activeArcOption.targetShipId, selected);
                }
                arcChoiceEl.innerHTML = `<span class="hint">${activeArcOption.flyby ? 'Flyby firing arc' : 'Firing arc for this target'}</span><div class="weapon-chips">${legalArcs.map(arc => `<button class="weapon-chip arc-choice${selected === arc ? ' active' : ''}" data-arc="${arc}" data-target="${activeArcOption.targetShipId}">${arc} · ${activeArcOption.arcHitChances[arc]}%</button>`).join('')}</div>`;
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
        const assigned = activeWeapon !== null && selectedShots.has(activeWeapon)
            ? [[activeWeapon, selectedShots.get(activeWeapon)]] : [];
        if (assigned.length) {
            targetInfo.innerHTML += `<div class="shot-list">${assigned.map(([widx, tid]) => {
                const w = actor.weapons?.[widx];
                const label = w ? `${w.name}${w.arc && w.arc !== 'AllArcs' ? ` (${w.arc}${w.broadArc ? ' + Broad Arc' : ''})` : ''}` : widx;
                const t = state.ships.find(s => s.id === tid);
                const opt = (weapons.get(widx) || []).find(o => o.targetShipId === tid);
                const penalties = opt ? [opt.rangePenalty && `${opt.rangePenalty} range`, opt.arcPenalty && `${opt.arcPenalty} arc`].filter(Boolean).join(', ') : '';
                const rangeStr = opt ? ` [${opt.range} hex${opt.range !== 1 ? 'es' : ''}${penalties ? `, ${penalties}` : ''}]` : '';
                const chance = opt?.arcHitChances?.[selectedTargetArcs.get(tid)] ?? opt?.hitChancePercent;
                return `<span class="shot-item">${label} → ${t?.name ?? '?'}${chance != null ? ` · ${chance}%` : ''}${rangeStr}</span>`;
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

    actionPanel.querySelectorAll('.weapon-chip[data-weapon]:not(:disabled)').forEach(chip => {
        chip.addEventListener('click', () => {
            const idx = parseInt(chip.dataset.weapon, 10);
            activeWeapon = idx;
            if (activeWeapon !== null && !selectedShots.has(idx)) {
                const opts = weapons.get(idx) || [];
                if (opts.length) assignGunneryTarget(idx, opts.reduce((best, o) => o.range < best.range ? o : best).targetShipId);
            }
            actionPanel.querySelectorAll('.weapon-chip[data-weapon]').forEach(c =>
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
        fireBtn.disabled = true;
        try {
            const response = await api(`/${roomCode}/fire`, { weaponIndex: activeWeapon, targetShipId, arc, targetQuadrant,
                crewIndex: gunner?.crewIndex ?? null, fireAtWill: !!gunner?.fireAtWill });
            if (!response.ok) addLog('error', 'Weapon could not fire.');
            else selectedGunneryKey = null;
            await fetchState();
        } catch (err) {
            addLog('error', `Could not fire: ${err.message}`);
            refresh();
        }
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
