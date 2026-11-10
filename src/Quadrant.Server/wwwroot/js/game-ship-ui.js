// Ship roster/details, facing picker, and damage report rendering.
// Classic page script; shares the game client global scope.

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
