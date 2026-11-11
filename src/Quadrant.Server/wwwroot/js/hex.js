// Shared team colors; additional team IDs receive distinct generated hues.
function teamColor(teamId) {
    const colors = ['#4fc3f7', '#ff7043', '#a5d66c', '#d49af0', '#ffd166'];
    return colors[teamId] || `hsl(${(teamId * 137.508) % 360} 70% 70%)`;
}

// Quadrant — Hex grid rendering (pointy-top, axial coordinates).
// This module is pure rendering + input; it knows nothing about game rules.

const Hex = (() => {
    const SQRT3 = Math.sqrt(3);

    // Pointy-top: axial (q, r) -> pixel center.
    function axialToPixel(q, r, size) {
        return {
            x: size * SQRT3 * (q + r / 2),
            y: size * 1.5 * r
        };
    }

    // Pixel -> axial (rounded to nearest hex center).
    function pixelToAxial(x, y, size) {
        const q = (SQRT3 / 3 * x - 1 / 3 * y) / size;
        const r = (2 / 3 * y) / size;
        return axialRound(q, r);
    }

    // Cube rounding to snap fractional axial coords to the nearest hex.
    function axialRound(q, r) {
        const x = q, z = r, y = -x - z;
        let rx = Math.round(x), ry = Math.round(y), rz = Math.round(z);
        const dx = Math.abs(rx - x), dy = Math.abs(ry - y), dz = Math.abs(rz - z);
        if (dx > dy && dx > dz) rx = -ry - rz;
        else if (dy > dz) ry = -rx - rz;
        else rz = -rx - ry;
        return { q: rx, r: rz };
    }

    // The six pointy-top neighbor directions, clockwise from NW.
    const DIRECTIONS = [
        { dq: 0, dr: -1 },   // 0: NW
        { dq: 1, dr: -1 },   // 1: NE
        { dq: 1, dr: 0 },    // 2: E
        { dq: 0, dr: 1 },    // 3: SE
        { dq: -1, dr: 1 },   // 4: SW
        { dq: -1, dr: 0 }    // 5: W
    ];

    function neighbor(q, r, dir) {
        const d = DIRECTIONS[((dir % 6) + 6) % 6];
        return { q: q + d.dq, r: r + d.dr };
    }

    // Distance between two axial hexes.
    function distance(a, b) {
        const dq = a.q - b.q, dr = a.r - b.r;
        return (Math.abs(dq) + Math.abs(dr) + Math.abs(dq + dr)) / 2;
    }

    // Corners of a pointy-top hex centered at (cx, cy).
    function corners(cx, cy, size) {
        const pts = [];
        for (let i = 0; i < 6; i++) {
            const angle = Math.PI / 180 * (60 * i - 30);
            pts.push({ x: cx + size * Math.cos(angle), y: cy + size * Math.sin(angle) });
        }
        return pts;
    }

    return { SQRT3, axialToPixel, pixelToAxial, neighbor, distance, corners, DIRECTIONS };
})();

// The grid renderer. Owns the canvas and draws the board + ships.
class HexGrid {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.size = 26;          // hex radius in px (world units, fixed for infinite grid)
        this.ships = [];         // [{id, q, r, facing, teamId, name, hull, maxHull, shields:{...}}]
        this.projectiles = [];
        this.highlightHexes = new Set();   // movement highlights (blue)
        this.restrictedHexes = new Set();
        this.rangeHexes = new Map();      // gunnery range: key -> band (1..3), faded per increment
        this.selectedShipId = null;
        this.targets = new Map(); // id -> hitChance (percent) for valid gunnery targets
        this.selectedGunneryTarget = null; // { sourceId, targetId }
        this.ghost = null;        // {q, r, facing, teamId, name} — translucent preview ship
        this.path = null;         // [{q, r}, ...] — hex route of the previewed move
        this.onHexClick = null;   // callback (q, r) => void
        this.onShipClick = null;  // callback (ship) => void — takes priority over onHexClick
        this.onShipHover = null;  // callback (ship | null) => void
        this._hoverShipId = null;

        // Camera state
        this.zoom = 1.0;
        this.panX = 0;
        this.panY = 0;
        this._dragging = false;
        this._dragStartX = 0;
        this._dragStartY = 0;
        this._panStartX = 0;
        this._panStartY = 0;

        this._resize();
        new ResizeObserver(() => { this._resize(); this.draw(); }).observe(canvas);
        canvas.addEventListener('mousemove', (e) => this._onMove(e));
        canvas.addEventListener('mousedown', (e) => this._onDown(e));
        canvas.addEventListener('mouseup', (e) => this._onUp(e));
        canvas.addEventListener('click', (e) => this._onClick(e));
        canvas.addEventListener('wheel', (e) => this._onWheel(e), { passive: false });
        canvas.addEventListener('touchstart', (e) => this._onTouchStart(e), { passive: false });
        canvas.addEventListener('touchmove', (e) => this._onTouchMove(e), { passive: false });
        canvas.addEventListener('touchend', (e) => this._onTouchEnd(e), { passive: false });
        canvas.addEventListener('touchcancel', (e) => { e.preventDefault(); this._touch = null; });
    }

    _resize() {
        const dpr = window.devicePixelRatio || 1;
        const rect = this.canvas.getBoundingClientRect();
        this.canvas.width = rect.width * dpr;
        this.canvas.height = rect.height * dpr;
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this._w = rect.width;
        this._h = rect.height;
        // Center axial (0,0) in the viewport.
        this.originX = this._w / 2;
        this.originY = this._h / 2;
    }

    // World pixel position of a hex center (before camera transform).
    _pixel(q, r) {
        const p = Hex.axialToPixel(q, r, this.size);
        return { x: p.x + this.originX, y: p.y + this.originY };
    }

    // Screen pixel -> world pixel (inverse of camera transform).
    _screenToWorld(sx, sy) {
        return { x: (sx - this.panX) / this.zoom, y: (sy - this.panY) / this.zoom };
    }

    _axialFromEvent(e) {
        const rect = this.canvas.getBoundingClientRect();
        const sx = e.clientX - rect.left;
        const sy = e.clientY - rect.top;
        const w = this._screenToWorld(sx, sy);
        return Hex.pixelToAxial(w.x - this.originX, w.y - this.originY, this.size);
    }

    // The ship occupying the given hex, if any.
    shipAt(q, r) {
        return this.ships.find(s => s.q === q && s.r === r) || null;
    }

    // --- Pan & Zoom handlers ---

    _touchPoints(touches) {
        const rect = this.canvas.getBoundingClientRect();
        return Array.from(touches, t => ({ x: t.clientX - rect.left, y: t.clientY - rect.top }));
    }

    _onTouchStart(e) {
        e.preventDefault();
        const points = this._touchPoints(e.touches);
        if (!this._touch) this._touch = { moved: false, start: points[0] };
        if (points.length > 1) this._touch.moved = true;
        this._touch.points = points;
    }

    _onTouchMove(e) {
        e.preventDefault();
        if (!this._touch) return;
        const points = this._touchPoints(e.touches);
        const previous = this._touch.points;
        if (points.length !== previous.length || !points.length) {
            this._touch.points = points;
            return;
        }
        if (points.length >= 2) {
            const mid = p => ({ x: (p[0].x + p[1].x) / 2, y: (p[0].y + p[1].y) / 2 });
            const distance = p => Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y);
            const oldMid = mid(previous), newMid = mid(points);
            const oldDistance = distance(previous);
            const newZoom = Math.max(0.3, Math.min(5, this.zoom * (oldDistance ? distance(points) / oldDistance : 1)));
            const scale = newZoom / this.zoom;
            this.panX = newMid.x - (oldMid.x - this.panX) * scale;
            this.panY = newMid.y - (oldMid.y - this.panY) * scale;
            this.zoom = newZoom;
            this._touch.moved = true;
        } else {
            const dx = points[0].x - previous[0].x, dy = points[0].y - previous[0].y;
            if (Math.hypot(points[0].x - this._touch.start.x, points[0].y - this._touch.start.y) > 6) this._touch.moved = true;
            this.panX += dx;
            this.panY += dy;
        }
        this._touch.points = points;
        this.draw();
    }

    _onTouchEnd(e) {
        e.preventDefault();
        if (!this._touch) return;
        if (e.touches.length) {
            this._touch.points = this._touchPoints(e.touches);
            this._touch.moved = true;
        } else {
            if (!this._touch.moved && e.changedTouches.length) {
                this._wasDrag = false;
                this._onClick(e.changedTouches[0]);
            }
            this._touch = null;
        }
    }

    _onDown(e) {
        if (e.button !== 0) return;
        this._dragging = true;
        this._wasDrag = false;
        this._dragStartX = e.clientX;
        this._dragStartY = e.clientY;
        this._panStartX = this.panX;
        this._panStartY = this.panY;
        this.canvas.style.cursor = 'grabbing';
    }

    _onUp(e) {
        this._dragging = false;
        this.canvas.style.cursor = 'crosshair';
    }

    _onWheel(e) {
        e.preventDefault();
        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;

        const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
        const newZoom = Math.max(0.3, Math.min(5.0, this.zoom * factor));
        const applied = newZoom / this.zoom;

        // Zoom toward the cursor: keep the world point under the mouse fixed.
        this.panX = mx - (mx - this.panX) * applied;
        this.panY = my - (my - this.panY) * applied;
        this.zoom = newZoom;
        this.draw();
    }

    resetCamera() {
        this.zoom = 1.0;
        this.panX = 0;
        this.panY = 0;
        this.draw();
    }

    // Smoothly pan so the given hex is centered in the viewport.
    centerOn(q, r) {
        const target = this._pixel(q, r);
        const targetPanX = this._w / 2 - target.x * this.zoom;
        const targetPanY = this._h / 2 - target.y * this.zoom;
        const startX = this.panX, startY = this.panY;
        const dur = 300;
        const t0 = performance.now();
        const step = (now) => {
            const t = Math.min((now - t0) / dur, 1);
            const ease = 1 - Math.pow(1 - t, 3); // ease-out cubic
            this.panX = startX + (targetPanX - startX) * ease;
            this.panY = startY + (targetPanY - startY) * ease;
            this.draw();
            if (t < 1) requestAnimationFrame(step);
        };
        requestAnimationFrame(step);
    }

    _inGrid(q, r) {
        // Infinite grid: all hexes are valid.
        return true;
    }

    _onMove(e) {
        if (this._dragging) {
            const dx = e.clientX - this._dragStartX;
            const dy = e.clientY - this._dragStartY;
            if (Math.abs(dx) > 3 || Math.abs(dy) > 3) this._wasDrag = true;
            this.panX = this._panStartX + dx;
            this.panY = this._panStartY + dy;
            this.draw();
            return;
        }
        const ax = this._axialFromEvent(e);
        this._hover = this._inGrid(ax.q, ax.r) ? ax : null;
        const hoverShip = this.shipAt(ax.q, ax.r);
        if (this.onShipHover) {
            // Fire on every move so the tooltip can follow the cursor; pass client coords.
            this.onShipHover(hoverShip, e.clientX, e.clientY);
        }
        this._hoverShipId = hoverShip?.id ?? null;
        this.draw();
    }

    _onClick(e) {
        if (this._wasDrag) { this._wasDrag = false; return; }
        const ax = this._axialFromEvent(e);
        if (!this._inGrid(ax.q, ax.r)) return;
        const ship = this.shipAt(ax.q, ax.r);
        if (ship && this.onShipClick) {
            this.onShipClick(ship);
        } else if (this.onHexClick) {
            this.onHexClick(ax.q, ax.r);
        }
    }

    setShips(ships) { this.ships = ships; this.draw(); }
    setProjectiles(projectiles) { this.projectiles = projectiles; this.draw(); }
    setHighlights(hexes, restricted = []) { this.highlightHexes = new Set(hexes.map(h => `${h.q},${h.r}`)); this.restrictedHexes = new Set(restricted.map(h => `${h.q},${h.r}`)); this.rangeHexes.clear(); this.draw(); }
    // Warm-colored hexes for gunnery range (kept separate from movement highlights).
    // Each hex may carry a `band` (1..3); the renderer fades each band.
    setRangeHexes(hexes) { this.rangeHexes = new Map(hexes.map(h => [`${h.q},${h.r}`, h.band || 1])); this.highlightHexes.clear(); this.restrictedHexes.clear(); this.draw(); }
    // Set the warm range overlay without touching movement highlights — used by the
    // ship-details weapon hover so it can layer over Helm-phase move previews.
    setRangeHexesOnly(hexes) { this.rangeHexes = new Map(hexes.map(h => [`${h.q},${h.r}`, h.band || 1])); this.draw(); }
    setSelected(id) { this.selectedShipId = id; this.draw(); }
    // Map of shipId -> hitChance percent for valid gunnery targets.
    setTargets(targets) {
        this.targets = new Map(targets);
        this.selectedGunneryTarget = null;
        this.draw();
    }
    setSelectedGunneryTarget(sourceId, targetId) {
        this.selectedGunneryTarget = sourceId && targetId ? { sourceId, targetId } : null;
        this.draw();
    }
    // Translucent preview ship (helm move confirmation). Pass null to clear.
    setGhost(ghost) { this.ghost = ghost; this.draw(); }
    // Hex route of the previewed move, drawn as a line through hex centers. Pass null to clear.
    setPath(hexes) { this.path = hexes && hexes.length ? hexes : null; this.draw(); }

    draw() {
        const ctx = this.ctx;
        ctx.clearRect(0, 0, this._w, this._h);

        // Apply camera transform.
        ctx.save();
        ctx.translate(this.panX, this.panY);
        ctx.scale(this.zoom, this.zoom);

        // Draw only visible hexes (infinite grid).
        // Check all four viewport corners in axial space to get a correct bounding box.
        const worldTL = this._screenToWorld(0, 0);
        const worldTR = this._screenToWorld(this._w, 0);
        const worldBL = this._screenToWorld(0, this._h);
        const worldBR = this._screenToWorld(this._w, this._h);
        const axTL = Hex.pixelToAxial(worldTL.x - this.originX, worldTL.y - this.originY, this.size);
        const axTR = Hex.pixelToAxial(worldTR.x - this.originX, worldTR.y - this.originY, this.size);
        const axBL = Hex.pixelToAxial(worldBL.x - this.originX, worldBL.y - this.originY, this.size);
        const axBR = Hex.pixelToAxial(worldBR.x - this.originX, worldBR.y - this.originY, this.size);
        // Expand by 1 hex margin to avoid edge clipping.
        const qMin = Math.min(axTL.q, axTR.q, axBL.q, axBR.q) - 1;
        const qMax = Math.max(axTL.q, axTR.q, axBL.q, axBR.q) + 1;
        const rMin = Math.min(axTL.r, axTR.r, axBL.r, axBR.r) - 1;
        const rMax = Math.max(axTL.r, axTR.r, axBL.r, axBR.r) + 1;

        for (let r = rMin; r <= rMax; r++) {
            for (let q = qMin; q <= qMax; q++) {
                const p = this._pixel(q, r);
                // Cull hexes outside the viewport.
                if (p.x < worldTL.x - this.size || p.x > worldBR.x + this.size) continue;
                if (p.y < worldTL.y - this.size || p.y > worldBR.y + this.size) continue;

                const key = `${q},${r}`;
                const isHover = this._hover && this._hover.q === q && this._hover.r === r;
                const isHighlight = this.highlightHexes.has(key);
                const rangeBand = this.rangeHexes.get(key); // undefined or 1..3

                ctx.beginPath();
                const cs = Hex.corners(p.x, p.y, this.size * 0.94);
                ctx.moveTo(cs[0].x, cs[0].y);
                for (let i = 1; i < 6; i++) ctx.lineTo(cs[i].x, cs[i].y);
                ctx.closePath();

                // Movement highlights are blue; gunnery range is warm (orange),
                // fading with each successive range increment.
                if (rangeBand !== undefined) {
                    const fillA = [0.26, 0.15, 0.08][rangeBand - 1] ?? 0.05;
                    const strokeA = [0.9, 0.55, 0.3][rangeBand - 1] ?? 0.15;
                    ctx.fillStyle = `rgba(255,138,42,${fillA})`;
                    ctx.fill();
                    ctx.strokeStyle = `rgba(255,138,42,${strokeA})`;
                } else if (this.restrictedHexes.has(key) && !isHighlight) {
                    ctx.fillStyle = 'rgba(79,195,247,0.06)';
                    ctx.fill();
                    ctx.strokeStyle = 'rgba(79,195,247,0.4)';
                } else if (isHighlight) {
                    ctx.fillStyle = 'rgba(79,195,247,0.18)';
                    ctx.fill();
                    ctx.strokeStyle = '#4fc3f7';
                } else {
                    ctx.fillStyle = isHover ? '#1f3350' : '#16202e';
                    ctx.fill();
                    ctx.strokeStyle = '#2a3a52';
                }
                ctx.lineWidth = 1 / this.zoom;
                ctx.stroke();
            }
        }

        // Draw the active weapon's chosen target path behind ship markers.
        if (this.selectedGunneryTarget) {
            const source = this.ships.find(ship => ship.id === this.selectedGunneryTarget.sourceId);
            const target = this.ships.find(ship => ship.id === this.selectedGunneryTarget.targetId);
            if (source && target) {
                const from = this._pixel(source.q, source.r);
                const to = this._pixel(target.q, target.r);
                ctx.save();
                ctx.beginPath();
                ctx.moveTo(from.x, from.y);
                ctx.lineTo(to.x, to.y);
                ctx.strokeStyle = '#ff4b4b';
                ctx.lineWidth = 4 / this.zoom;
                ctx.lineCap = 'round';
                ctx.globalAlpha = 0.9;
                ctx.stroke();
                ctx.restore();
            }
        }

        // Draw ships.
        for (const ship of this.ships) {
            const p = this._pixel(ship.q, ship.r);
            this._drawShip(p.x, p.y, ship);
        }
        for (const projectile of this.projectiles) {
            const p = this._pixel(projectile.q, projectile.r);
            this._drawProjectilePath(projectile);
            this._drawProjectile(p.x, p.y, projectile);
            if (projectile.previewPath?.length) {
                const end = projectile.previewPath[projectile.previewPath.length - 1];
                const ghost = this._pixel(end.q, end.r);
                this._drawProjectile(ghost.x, ghost.y,
                    { ...projectile, facing: projectile.previewFacing }, 0.35, false);
            }
        }

        // Movement path: a dashed line through the hex centers of the previewed route.
        if (this.path && this.path.length > 1) {
            const color = teamColor(this.ghost?.teamId ?? 0);
            ctx.save();
            ctx.strokeStyle = color;
            ctx.lineWidth = 2 / this.zoom;
            ctx.setLineDash([6, 5]);
            ctx.globalAlpha = 0.8;
            ctx.beginPath();
            const start = this._pixel(this.path[0].q, this.path[0].r);
            ctx.moveTo(start.x, start.y);
            for (let i = 1; i < this.path.length; i++) {
                const p = this._pixel(this.path[i].q, this.path[i].r);
                ctx.lineTo(p.x, p.y);
            }
            ctx.stroke();
            ctx.restore();
        }

        // Ghost preview ship on top (helm move confirmation).
        if (this.ghost) {
            const gp = this._pixel(this.ghost.q, this.ghost.r);
            ctx.save();
            ctx.globalAlpha = 0.5;
            this._drawShip(gp.x, gp.y, this.ghost);
            ctx.restore();
        }

        ctx.restore();
    }

    _drawProjectilePath(projectile) {
        if (!projectile.previewPath?.length) return;
        const ctx = this.ctx;
        ctx.save();
        ctx.strokeStyle = teamColor(projectile.teamId);
        ctx.lineWidth = 2 / this.zoom;
        ctx.globalAlpha = 0.55;
        ctx.setLineDash([5 / this.zoom, 4 / this.zoom]);
        ctx.beginPath();
        const start = this._pixel(projectile.q, projectile.r);
        ctx.moveTo(start.x, start.y);
        for (const step of projectile.previewPath) {
            const p = this._pixel(step.q, step.r);
            ctx.lineTo(p.x, p.y);
        }
        ctx.stroke();
        ctx.restore();
    }

    _drawProjectile(x, y, projectile, alpha = 1, label = true) {
        const ctx = this.ctx;
        const color = teamColor(projectile.teamId);
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate((projectile.facing * 60 - 30) * Math.PI / 180);
        ctx.globalAlpha = alpha;
        ctx.fillStyle = color;
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 1.5 / this.zoom;
        ctx.beginPath();
        ctx.moveTo(0, -this.size * 0.25);
        ctx.lineTo(this.size * 0.16, this.size * 0.2);
        ctx.lineTo(0, this.size * 0.1);
        ctx.lineTo(-this.size * 0.16, this.size * 0.2);
        ctx.closePath();
        ctx.fill();
        ctx.stroke();
        ctx.restore();
        if (!label) return;
        ctx.save();
        ctx.font = `${Math.max(8, this.size * 0.27)}px 'Segoe UI', sans-serif`;
        ctx.textAlign = 'center';
        ctx.fillStyle = color;
        ctx.fillText(`${projectile.name} - T${projectile.teamId + 1}`, x, y + this.size * 0.45);
        ctx.restore();
    }

    _drawShip(x, y, ship) {
        const ctx = this.ctx;
        const r = this.size * 0.42;
        const color = teamColor(ship.teamId);
        const isSelected = ship.id === this.selectedShipId;
        const opacity = ship.isDisabled || ship.isDestroyed ? 0.3 : 1;

        // Shield arcs: four quadrants relative to facing, all on ONE circle.
        // Forward = 60° (facing face), Starboard = 120° (right side),
        // Aft = 60° (opposite face), Port = 120° (left side).
        // Solid arcs = forward/aft, dashed arcs = starboard/port.
        ctx.save();
        const rot = ship.facing * 60 - 30;
        const shieldRad = r + 5;
        const shieldQuads = [
            { key: 'forward',   start: -120, end: -60, dash: [], lw: 2.5 },
            { key: 'starboard', start: -60,  end: 60,  dash: [4, 3], lw: 1 },
            { key: 'aft',       start: 60,   end: 120, dash: [], lw: 2.5 },
            { key: 'port',      start: 120,  end: 240, dash: [4, 3], lw: 1 }
        ];
        for (const sq of shieldQuads) {
            const val = ship.shields?.[sq.key] ?? 0;
            const frac = Math.max(0, val / (ship.maxShield || 1));
            if (frac <= 0) continue;
            ctx.beginPath();
            ctx.strokeStyle = color;
            ctx.lineWidth = sq.lw;
            ctx.setLineDash(sq.dash);
            ctx.globalAlpha = opacity * (0.2 + 0.35 * frac);
            const s = (sq.start + rot) * Math.PI / 180;
            const en = (sq.end + rot) * Math.PI / 180;
            ctx.arc(x, y, shieldRad, s, en);
            ctx.stroke();
        }
        ctx.setLineDash([]);
        ctx.restore();

        if (ship.isActive) {
            ctx.save();
            ctx.beginPath();
            const activeHex = Hex.corners(x, y, this.size * 1.02);
            ctx.moveTo(activeHex[0].x, activeHex[0].y);
            for (let i = 1; i < activeHex.length; i++) ctx.lineTo(activeHex[i].x, activeHex[i].y);
            ctx.closePath();
            ctx.strokeStyle = '#ffd54f';
            ctx.lineWidth = 3 / this.zoom;
            ctx.globalAlpha = 0.95;
            ctx.shadowColor = '#ffd54f';
            ctx.shadowBlur = 8 / this.zoom;
            ctx.stroke();
            ctx.restore();
        }

        // Ship body: elongated dart pointing in the facing direction.
        // Pointy-top hex directions are at screen angles F*60-120 degrees.
        // The dart is drawn pointing up (-90°), so rotate by (F*60-30).
        const facingAngle = (ship.facing * 60 - 30) * Math.PI / 180;
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate(facingAngle);

        const nose = r * 1.3;
        const wingSpan = r * 0.55;
        const rear = r * 0.45;

        ctx.beginPath();
        ctx.moveTo(0, -nose);
        ctx.lineTo(wingSpan, rear);
        ctx.lineTo(0, rear * 0.4);
        ctx.lineTo(-wingSpan, rear);
        ctx.closePath();

        const hullFrac = Math.max(0, ship.hull / ship.maxHull);
        ctx.fillStyle = color;
        ctx.globalAlpha = opacity * (0.35 + 0.55 * hullFrac);
        ctx.fill();
        ctx.globalAlpha = opacity;
        ctx.strokeStyle = isSelected ? '#ffffff' : color;
        ctx.lineWidth = isSelected ? 2 : 1.2;
        ctx.stroke();

        // Center spine for direction clarity.
        ctx.beginPath();
        ctx.moveTo(0, -nose * 0.8);
        ctx.lineTo(0, rear * 0.3);
        ctx.strokeStyle = '#ffffff';
        ctx.globalAlpha = opacity * 0.4;
        ctx.lineWidth = 1;
        ctx.stroke();

        ctx.restore();

        // Name label below the ship.
        ctx.save();
        ctx.font = `${Math.max(9, this.size * 0.35)}px 'Segoe UI', sans-serif`;
        ctx.textAlign = 'center';
        ctx.fillStyle = '#d5deea';
        ctx.globalAlpha = opacity * 0.9;
        if (ship.barrelRolledThisRound) {
            ctx.translate(x, y + r + 12);
            ctx.rotate(Math.PI);
            ctx.textBaseline = 'middle';
            ctx.fillText(ship.name, 0, 0);
        } else {
            ctx.fillText(ship.name, x, y + r + 16);
        }
        ctx.restore();

        // Valid gunnery target: pulsing ring + hit-chance badge.
        if (this.targets.has(ship.id)) {
            const chance = this.targets.get(ship.id);
            const tr = shieldRad + 4;
            ctx.save();
            ctx.beginPath();
            ctx.arc(x, y, tr, 0, Math.PI * 2);
            ctx.strokeStyle = '#ffd54f';
            ctx.lineWidth = 2 / this.zoom;
            ctx.setLineDash([6, 4]);
            ctx.globalAlpha = opacity * 0.9;
            ctx.stroke();
            ctx.setLineDash([]);

            // Hit-chance badge above the ship.
            const label = `${chance}%`;
            ctx.font = `bold ${Math.max(10, this.size * 0.4)}px 'Segoe UI', sans-serif`;
            const tw = ctx.measureText(label).width;
            const bx = x - tw / 2 - 5, by = y - tr - 20, bw = tw + 10, bh = 18;
            ctx.globalAlpha = opacity;
            ctx.fillStyle = 'rgba(10,14,23,0.85)';
            ctx.strokeStyle = '#ffd54f';
            ctx.lineWidth = 1 / this.zoom;
            ctx.beginPath();
            ctx.roundRect(bx, by, bw, bh, 4);
            ctx.fill();
            ctx.stroke();
            ctx.fillStyle = '#ffd54f';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(label, x, by + bh / 2 + 1);
            ctx.restore();
        }
    }
}
