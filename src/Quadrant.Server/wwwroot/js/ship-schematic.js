// Shared inspection schematic. System identifiers also serve as action targets.
const ShipSchematic = (() => {
    const names = { PowerCore: 'Core', LifeSupport: 'Life support', Sensors: 'Sensors', Engines: 'Engines', WeaponsArray: 'Weapons' };
    const scopes = { PowerCore: 'engineering actions', LifeSupport: 'captain actions', Sensors: 'science officer actions', Engines: 'pilot actions', WeaponsArray: 'gunnery actions' };
    const conditions = ['None', 'Glitching', 'Malfunctioning', 'Wrecked'];
    const escape = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const shieldDivertDescription = assigned =>
        `On success, restore ${assigned} SP to the selected quadrants.`;
    let inspectedShip = null;
    let selectedSystem = 'PowerCore';
    let selectedArc = null;

    function status(ship, system, arc = null) {
        if (system === 'WeaponsArray' && arc === 'AllArcs') return { condition: 'None', effective: 'None' };
        const entry = (system === 'WeaponsArray' && arc ? (ship.weaponArcs || []).find(s => s.arc === arc) : (ship.systems || []).find(s => s.system === system))
            || (ship.damagedSystems || []).find(s => s.system === system);
        const condition = conditions.includes(entry?.condition) ? entry.condition : 'None';
        const effective = conditions.includes(entry?.effectiveCondition) ? entry.effectiveCondition : condition;
        return { condition, effective };
    }

    function effects(ship, system, arc = null) {
        if (system === 'WeaponsArray' && arc === 'AllArcs') return `Turrets use the condition of the arc they fire through. ${(ship.weaponArcs || []).map(a => `${a.arc}: ${a.condition === 'None' ? 'Healthy' : a.condition}${a.condition !== a.effectiveCondition ? ` (held: ${a.effectiveCondition === 'None' ? 'Healthy' : a.effectiveCondition})` : ''}`).join('; ')}.`;
        const { condition, effective } = status(ship, system, arc);
        const scope = system === 'WeaponsArray' && arc ? `gunnery actions using the ${arc.toLowerCase()} array, including turret shots through that arc` : scopes[system];
        let text = effective === 'None' ? `No damage penalty to ${scope}.`
            : effective === 'Wrecked' ? `Automatic failure for ${scope}.`
            : `−${effective === 'Glitching' ? 2 : 4} to ${scope}.`;
        if (system === 'PowerCore') {
            if (effective === 'Malfunctioning') text += ' Also −2 to pilot, science officer and gunnery checks.';
            if (effective === 'Wrecked') text += ' Also −4 to pilot, science officer and gunnery checks.';
            text += ' Hold It Together ignores the core damage penalty.';
        }
        if (system === 'Sensors' && ['Malfunctioning', 'Wrecked'].includes(effective)) text += ' Target System (Push) is unavailable.';
        if (system === 'Engines' && ['Malfunctioning', 'Wrecked'].includes(effective)) text += ' Pilot stunts are unavailable.';
        if (condition !== effective) text += ` Hold It Together: ${condition} damage currently acts as ${effective === 'None' ? 'Healthy' : effective}.`;
        return text;
    }

    function targetingEffects(system, arc = null) {
        if (system === 'WeaponsArray') {
            return 'Upon critical, a firing arc is chosen at random. Attacks from weapons in that arc take −2, then −4 as damage worsens; Wrecked arcs fail automatically. Turrets use the arc they fire through.';
        }
        if (system === 'PowerCore') {
            return 'Upon critical, Engineer checks take −2. At Malfunctioning, all crew checks also take −2; at Wrecked, they take −4.';
        }
        if (system === 'Engines') {
            return 'Upon critical, all Pilot actions take −2. Further criticals increase the penalty; Pilot stunts are unavailable at Malfunctioning or Wrecked.';
        }
        if (system === 'Sensors') {
            return 'Upon critical, all Science Officer actions take −2. Further criticals increase the penalty; Target System is unavailable at Malfunctioning or Wrecked.';
        }
        if (system === 'LifeSupport') {
            return 'Upon critical, all Captain actions take −2. Further criticals increase the penalty; actions automatically fail when Wrecked.';
        }
        return arc ? `Upon critical, ${arc} weapon attacks are affected.` : 'Upon critical, this system is damaged.';
    }

    function render(ship, options = {}) {
        const targeting = options.mode === 'target';
        const balancing = options.mode === 'balance';
        const diverting = options.mode === 'divert';
        if (inspectedShip !== ship.id) { inspectedShip = ship.id; selectedSystem = 'PowerCore'; selectedArc = null; }
        const component = (system, label, x, y, extra = '', arc = null) => {
            const { condition, effective } = status(ship, system, arc);
            const diverted = ship.diverted?.[{ Engines: 'engines', Sensors: 'science', WeaponsArray: 'weapons' }[system]];
            const target = system === 'Sensors' ? 'Science' : system === 'WeaponsArray' ? 'Weapons' : system;
            const selected = targeting ? options.selectedSystem === system : diverting ? options.selectedTarget === target : selectedSystem === system && selectedArc === arc;
            return `<button type="button" class="schematic-component condition-${condition.toLowerCase()} ${extra}" style="--x:${x};--y:${y}" data-system="${system}" data-weapon-arc="${arc || ''}" aria-pressed="${selected}" title="${escape(targeting ? targetingEffects(system, arc) : effects(ship, system, arc))}">
                <span>${escape(label)}</span><strong>${arc === 'AllArcs' ? 'By firing arc' : condition === 'None' ? 'Healthy' : condition}</strong>
                ${condition !== effective ? `<small>Held: ${effective === 'None' ? 'Healthy' : effective}</small>` : ''}
                ${diverted ? '<small>Power diverted</small>' : ''}</button>`;
        };
        const mounts = [
            ['Forward', 50, 17], ['Port', 17, 50], ['Starboard', 83, 50], ['Aft', 50, 83], ['AllArcs', 77, 37]
        ].filter(([arc]) => (ship.weapons || []).some(w => w.arc === arc || (arc !== 'AllArcs' && w.arc === 'AllArcs'))).map(([arc, x, y]) =>
            component('WeaponsArray', arc === 'AllArcs' ? 'Turret' : `${arc} weapons`, x, y, 'schematic-mount', arc)).join('')
            || component('WeaponsArray', 'Weapons · no mounts', 77, 50, 'schematic-mount');
        const shield = (arc, x, y) => {
            const current = ship.shields?.[arc] ?? 0;
            const selected = balancing && options.selectedQuadrant === arc;
            return `<button type="button" class="schematic-shield shield-${arc} ${balancing ? 'balance-selectable' : ''} ${selected ? 'balance-selected' : ''}" style="--x:${x};--y:${y}" data-shield-quadrant="${arc}" aria-pressed="${selected}" ${balancing ? '' : 'disabled'} title="${balancing ? `Select ${arc} shield quadrant, ${current} SP` : `${arc} shields: ${current} SP`}"><span>${arc}</span><strong>${current} <small>SP</small></strong></button>`;
        };
        const shieldArcs = ['forward', 'starboard', 'aft', 'port'].map((arc, index) => {
            const current = Math.max(0, ship.shields?.[arc] ?? 0);
            const initial = ship.initialShields?.[arc] ?? ship.maxShield ?? (ship.shieldCapacity || 0) / 4;
            const fraction = Math.min(1, current / Math.max(1, initial));
            const opacity = current === 0 ? 0.22 : 0.18 + 0.82 * fraction;
            const point = degrees => {
                const radians = degrees * Math.PI / 180;
                return `${(310 + 245 * Math.cos(radians)).toFixed(3)} ${(285 + 245 * Math.sin(radians)).toFixed(3)}`;
            };
            const start = -131.5 + index * 90;
            return `<path data-shield-arc="${arc}" d="M${point(start)} A245 245 0 0 1 ${point(start + 83)}" opacity="${opacity.toFixed(3)}"${current === 0 ? ' stroke-dasharray="6 5"' : ''}/>`;
        }).join('');
        if (diverting) {
            const selectedArcs = new Set(options.selectedShieldArcs || []);
            const total = options.shieldDivertMax ?? 0;
            const shieldChoice = (arc, x, y) => {
                const current = Math.max(0, ship.shields?.[arc] ?? 0);
                const amount = Math.max(0, options.shieldAllocations?.[arc] ?? 0);
                const maxAmount = total;
                const minAmount = selectedArcs.size === 1 && total > 0 ? total : 0;
                const selected = selectedArcs.has(arc);
                const projected = current + amount;
                return `<div class="divert-shield-arc ${selected ? 'divert-shield-selected' : ''}" style="--x:${x};--y:${y}">
                    <button type="button" class="divert-shield-toggle" data-divert-shield="${arc}" aria-pressed="${selected}" ${total <= 0 ? 'disabled' : ''} title="${total <= 0 ? 'No shield points are available to divert.' : `${selected ? 'Remove' : 'Add'} ${arc} shields to the diversion allocation.`}">
                        <span>${arc}</span><strong>${current} <small>SP</small> → ${projected} <small>SP</small></strong>
                    </button>
                    ${selected && options.selectedTarget === 'Shields' ? `<div class="divert-arc-controls"><input type="range" min="${minAmount}" max="${maxAmount}" step="1" value="${Math.min(amount, maxAmount)}" data-divert-range="${arc}" aria-label="${arc} shield points to restore" ${total <= 0 ? 'disabled' : ''}><input type="number" min="${minAmount}" max="${maxAmount}" step="1" value="${Math.min(amount, maxAmount)}" data-divert-amount="${arc}" aria-label="${arc} shield points to restore" ${total <= 0 ? 'disabled' : ''}></div>` : ''}
                </div>`;
            };
            const selectionText = options.selectedTarget === 'Shields'
                ? shieldDivertDescription(options.allocatedShieldPoints ?? 0)
                : options.selectedTarget ? `Power selected for ${options.selectedTarget === 'Science' ? 'Science (Sensors)' : options.selectedTarget}.`
                    : total > 0 ? 'Select Weapons, Engines, Science · Sensors, or shield quadrants.' : 'Select a system. No shield points are available to divert.';
            return `<div class="ship-blueprint divert-blueprint">
                <div class="blueprint-caption">DIVERT POWER · FORWARD ↑</div>
                <div class="schematic-stage">
                    <svg class="schematic-drawing" viewBox="0 0 620 570" aria-hidden="true">
                        <g class="schematic-shield-lines">${shieldArcs}</g>
                        <g class="schematic-hull"><path d="M310 70 L365 160 L376 252 L449 352 L449 432 L365 398 L353 480 L267 480 L255 398 L171 432 L171 352 L244 252 L255 160 Z"/>
                            <path d="M310 70 V480 M255 160 H365 M244 252 H376 M255 398 H365 M171 352 L267 330 M449 352 L353 330 M281 447 V495 M310 447 V508 M339 447 V495"/>
                            <rect x="274" y="220" width="72" height="86" rx="16"/><circle cx="310" cy="263" r="24"/>
                        </g>
                    </svg>
                    ${component('WeaponsArray', 'Weapons', 60, 18, 'schematic-divert-system')}
                    ${component('Sensors', 'Science · Sensors', 50, 34, 'schematic-divert-system')}
                    ${component('Engines', 'Engines', 50, 70, 'schematic-divert-system')}
                    ${shieldChoice('forward', 18, 10)}${shieldChoice('port', 18, 30)}${shieldChoice('starboard', 82, 30)}${shieldChoice('aft', 50, 89)}
                </div>
                <div class="schematic-effect" aria-live="polite"><strong>Divert Power</strong><span>${escape(selectionText)}</span></div>
                ${ship.barrelRollActive ? '<div class="schematic-hint">Barrel Roll active: port and starboard shields are swapped.</div>' : ''}
            </div>`;
        }
        if (balancing) {
            const totalShields = ['forward', 'starboard', 'aft', 'port'].reduce((sum, a) => sum + Math.max(0, ship.shields?.[a] ?? 0), 0);
            const minimum = Math.ceil(totalShields / 10);
            const evenPossible = Math.floor(totalShields / 4) >= minimum;
            const balanceShield = (arc, x, y) => {
                const current = Math.max(0, ship.shields?.[arc] ?? 0);
                const isSource = (options.sourceQuadrant || '').toLowerCase() === arc;
                const isDest = (options.destinationQuadrant || '').toLowerCase() === arc;
                const selected = isSource || isDest;
                const amount = Math.max(0, options.amount ?? 0);
                let projected = current;
                if (options.balanceEvenly && options.evenPreview?.[arc] != null) projected = options.evenPreview[arc];
                else if (isSource) projected = Math.max(minimum, current - amount);
                else if (isDest) projected = current + amount;
                const role = isSource ? 'Source' : isDest ? 'Destination' : '';
                return `<div class="balance-arc ${selected ? 'balance-selected' : ''}" style="--x:${x};--y:${y}" data-balance-arc="${arc}" role="button" tabindex="0" aria-pressed="${selected}" aria-label="${arc}: ${current} shield points, preview ${projected}. Click to ${isSource ? 'change source' : isDest ? 'change destination' : 'select'}" title="${arc}: ${current} SP → ${projected} SP${role ? ` · ${role}` : ''}">
                    <span class="balance-arc-name">${arc}</span>${role ? `<span class="balance-role">${role}</span>` : ''}<span class="balance-values"><strong>${current}</strong><small>SP</small><span aria-hidden="true">→</span><strong class="balance-projected">${projected}</strong><small>SP</small></span></div>`;
            };
            return `<div class="ship-blueprint">
                <div class="blueprint-caption">BALANCE SHIELDS · FORWARD ↑</div>
                <div class="schematic-stage">
                    <svg class="schematic-drawing" viewBox="0 0 620 570" aria-hidden="true">
                        <g class="schematic-shield-lines">${shieldArcs}</g>
                        <g class="schematic-hull"><path d="M310 70 L365 160 L376 252 L449 352 L449 432 L365 398 L353 480 L267 480 L255 398 L171 432 L171 352 L244 252 L255 160 Z"/>
                            <path d="M310 70 V480 M255 160 H365 M244 252 H376 M255 398 H365 M171 352 L267 330 M449 352 L353 330 M281 447 V495 M310 447 V508 M339 447 V495"/>
                            <rect x="274" y="220" width="72" height="86" rx="16"/><circle cx="310" cy="263" r="24"/>
                        </g>
                    </svg>
                    ${balanceShield('forward', 72, 8)}${balanceShield('port', 17, 25)}${balanceShield('starboard', 83, 25)}${balanceShield('aft', 28, 92)}
                </div>
                <div class="balance-options"><button type="button" class="btn btn-ghost balance-evenly-btn" data-balance-evenly aria-pressed="${!!options.balanceEvenly}" ${evenPossible ? '' : 'disabled'} title="${evenPossible ? 'Distribute all shield points evenly; any remainder goes to Forward.' : 'There are not enough shield points to meet the minimum in every arc.'}">Balance evenly</button><span>Split across all four arcs · remainder to Forward</span></div>
                <div class="schematic-effect" aria-live="polite"></div>
                ${ship.barrelRollActive ? '<div class="schematic-hint">Barrel Roll active: port and starboard shields are swapped.</div>' : ''}
            </div>`;
        }
        return `<div class="ship-blueprint">
            <div class="blueprint-caption">SHIP SCHEMATIC · FORWARD ↑</div>
            <div class="schematic-stage">
                <svg class="schematic-drawing" viewBox="0 0 620 570" aria-hidden="true">
                    <g class="schematic-shield-lines">${shieldArcs}</g>
                    <g class="schematic-hull"><path d="M310 70 L365 160 L376 252 L449 352 L449 432 L365 398 L353 480 L267 480 L255 398 L171 432 L171 352 L244 252 L255 160 Z"/>
                        <path d="M310 70 V480 M255 160 H365 M244 252 H376 M255 398 H365 M171 352 L267 330 M449 352 L353 330 M281 447 V495 M310 447 V508 M339 447 V495"/>
                        <rect x="274" y="220" width="72" height="86" rx="16"/><circle cx="310" cy="263" r="24"/>
                        <path d="M291 126 L310 102 L329 126 M291 134 H329 M291 143 H329"/>
                    </g>
                </svg>
                ${shield('forward', 72, 5)}${shield('port', 9, 25)}${shield('starboard', 90, 25)}${shield('aft', 28, 94)}
                ${component('Sensors', 'Sensors', 50, 31)}
                ${component('PowerCore', 'Core', 50, 47)}
                ${component('LifeSupport', 'Life support', 50, 59)}
                ${component('Engines', 'Engines', 50, 71)}
                ${mounts}
            </div>
            <div class="schematic-legend"><span>● Healthy</span><span class="condition-glitching">● Glitching</span><span class="condition-malfunctioning">● Malfunctioning</span><span class="condition-wrecked">● Wrecked</span></div>
            <div class="schematic-effect" aria-live="polite"></div>
            ${ship.barrelRollActive ? '<div class="schematic-hint">Barrel Roll active: port and starboard shields and weapon arcs are swapped.</div>' : ''}
            <div class="hint schematic-hint">${targeting ? 'Choose a system. Any weapon component selects Weapons; the affected arc is determined randomly.' : balancing ? 'Select a source and destination shield arc. Adjust the transfer in the sidebar.' : 'Hover, focus or click a component for its effects. Weapon damage is tracked by arc; turrets use their firing arc.'}</div>
        </div>`;
    }

    function bind(root, ship, options = {}) {
        const targeting = options.mode === 'target';
        const balancing = options.mode === 'balance';
        const diverting = options.mode === 'divert';
        let targetSelection = options.selectedSystem || options.selectedTarget || null;
        let targetSelectionArc = options.selectedArc || null;
        const buttons = root.querySelectorAll('[data-system]');
        const describe = (system, arc = null) => {
            if (diverting && system) {
                const target = system === 'Sensors' ? 'Science' : system === 'WeaponsArray' ? 'Weapons' : system;
                const { condition } = status(ship, system, arc);
                const heading = document.createElement('strong');
                const detail = document.createElement('span');
                const panel = root.querySelector('.schematic-effect');
                heading.textContent = `${target === 'Science' ? 'Science · Sensors' : target}: ${condition === 'None' ? 'Healthy' : condition}`;
                detail.textContent = target === 'Engines' ? 'On success, Speed increases by 2 this round.'
                    : target === 'Weapons' ? 'On success, damage dice that roll 1 count as 2 this round.'
                    : 'On success, Science Officer checks gain +2 this round.';
                panel.replaceChildren(heading, detail);
                return;
            }
            if (targeting && system) {
                const panel = root.querySelector('.schematic-effect');
                panel.replaceChildren();
                const heading = document.createElement('strong');
                const detail = document.createElement('span');
                heading.textContent = system === 'WeaponsArray' && arc === 'AllArcs'
                    ? 'Turret Weapons'
                    : `${arc ? `${arc} ` : ''}${names[system]}`;
                detail.textContent = targetingEffects(system, arc);
                panel.append(heading, detail);
                return;
            }
            if (!system) {
                if (diverting) {
                    const panel = root.querySelector('.schematic-effect');
                    panel.replaceChildren();
                    const heading = document.createElement('strong');
                    const detail = document.createElement('span');
                    heading.textContent = 'Divert Power';
                    detail.textContent = 'Choose Weapons, Engines, Science · Sensors, or shield quadrants on the ship.';
                    panel.append(heading, detail);
                } else {
                    root.querySelector('.schematic-effect').textContent = 'Select a component, then confirm the action in the popup.';
                }
                return;
            }
            const { condition } = status(ship, system, arc);
            const panel = root.querySelector('.schematic-effect');
            panel.replaceChildren();
            const heading = document.createElement('strong');
            heading.textContent = arc === 'AllArcs' ? 'Turret: condition depends on firing arc' : `${arc ? `${arc} weapons` : names[system]}: ${condition === 'None' ? 'Healthy' : condition}`;
            const description = document.createElement('span');
            description.textContent = effects(ship, system, arc);
            panel.append(heading, description);
        };
        const describeSelection = () => {
            if (diverting && targetSelection === 'Shields') {
                const panel = root.querySelector('.schematic-effect');
                const heading = document.createElement('strong');
                const detail = document.createElement('span');
                const total = options.shieldDivertMax ?? 0;
                const assigned = options.allocatedShieldPoints ?? 0;
                heading.textContent = 'Divert Power';
                detail.textContent = shieldDivertDescription(assigned);
                panel.replaceChildren(heading, detail);
                return;
            }
            const system = diverting
                ? targetSelection === 'Science' ? 'Sensors' : targetSelection === 'Weapons' ? 'WeaponsArray' : targetSelection
                : targetSelection;
            describe(targeting || diverting ? system : selectedSystem, targeting ? targetSelectionArc : selectedArc);
        };
        buttons.forEach(button => {
            button.addEventListener('mouseenter', () => describe(button.dataset.system, button.dataset.weaponArc || null));
            button.addEventListener('mouseleave', describeSelection);
            button.addEventListener('focus', () => describe(button.dataset.system, button.dataset.weaponArc || null));
            button.addEventListener('blur', describeSelection);
            button.addEventListener('click', () => {
                if (targeting) {
                    targetSelection = button.dataset.system;
                    targetSelectionArc = button.dataset.weaponArc || null;
                    buttons.forEach(b => b.setAttribute('aria-pressed', String(b === button)));
                    describe(targetSelection, targetSelectionArc);
                    options.onSelect?.(targetSelection);
                    return;
                }
                if (diverting) {
                    targetSelection = button.dataset.system === 'Sensors' ? 'Science'
                        : button.dataset.system === 'WeaponsArray' ? 'Weapons' : button.dataset.system;
                    buttons.forEach(b => b.setAttribute('aria-pressed', String(b === button)));
                    options.onTargetSelect?.(targetSelection);
                    return;
                }
                selectedSystem = button.dataset.system;
                selectedArc = button.dataset.weaponArc || null;
                buttons.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.system === selectedSystem && (b.dataset.weaponArc || null) === selectedArc)));
                describe(selectedSystem, selectedArc);
            });
        });
        if (diverting) {
            root.querySelectorAll('[data-divert-shield]').forEach(button => button.addEventListener('click', () =>
                options.onShieldToggle?.(button.dataset.divertShield)));
            root.querySelectorAll('[data-divert-range], [data-divert-amount]').forEach(input => {
                input.addEventListener('click', event => event.stopPropagation());
                input.addEventListener('change', () => options.onShieldAmount?.(input.dataset.divertRange || input.dataset.divertAmount, Number(input.value) || 0));
            });
            return;
        }
        if (balancing) {
            const selectArc = arc => options.onArcSelect?.(arc);
            root.querySelectorAll('[data-balance-arc]').forEach(el => {
                el.addEventListener('click', () => selectArc(el.dataset.balanceArc));
                el.addEventListener('keydown', event => {
                    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); selectArc(el.dataset.balanceArc); }
                });
            });
            const evenly = root.querySelector('[data-balance-evenly]');
            if (evenly) {
                evenly.classList.toggle('btn-primary', !!options.balanceEvenly);
                if (!evenly.disabled) evenly.addEventListener('click', () => options.onBalanceEvenly?.());
            }
            const source = options.sourceQuadrant;
            const destination = options.destinationQuadrant;
            const panel = root.querySelector('.schematic-effect');
            const title = document.createElement('strong');
            const detail = document.createElement('span');
            title.textContent = 'Choose transfer';
            if (options.balanceEvenly) {
                title.textContent = 'Even distribution';
                detail.textContent = 'Any remainder goes to Forward.';
            } else if (source && destination) {
                const amount = Math.max(0, options.amount ?? 0);
                detail.textContent = `${source} → ${destination} · move ${amount} SP.`;
            } else if (source) {
                detail.textContent = `From ${source} · select a destination.`;
            } else {
                detail.textContent = 'Select a source arc, then a destination.';
            }
            panel.replaceChildren(title, detail);
            return;
        }
        describe(targeting || diverting ? targetSelection : selectedSystem, targeting ? targetSelectionArc : selectedArc);
    }

    return { render, bind };
})();
