/*
 * Offline canvas-based sky viewer. Stand-alone — no external dependencies,
 * works fully offline. Renders a stereographic projection of the celestial
 * sphere centred on the current target with:
 *   - bright stars from BRIGHT_STARS (dot radius scaled by magnitude)
 *   - constellation lines from CONSTELLATION_LINES
 *   - optional DSO markers (fetched separately and passed in via setDsoMarkers)
 *   - a coordinate grid (RA hours / Dec degrees)
 *   - a target reticle + a camera-FOV rectangle
 *   - optional mosaic-panel overlay
 *
 * Interaction:
 *   - drag → pan (re-centres RA/Dec)
 *   - wheel → zoom
 *   - click → invokes onClick(raHours, decDeg) so the host can pick a target
 *
 * The host (app.js) drives the viewer via:
 *   new OfflineSkyViewer(canvas, opts)
 *   viewer.setCenter(raHours, decDeg)
 *   viewer.setFov(degrees)
 *   viewer.setCameraFov({widthDeg, heightDeg})  // null to hide
 *   viewer.setDsoMarkers([{ra, dec, name, type, magnitude}])
 *   viewer.setMosaic(plan)                       // null to hide
 *   viewer.redraw()
 */
class OfflineSkyViewer {
    constructor(canvas, opts = {}) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.centerRaHours = opts.centerRaHours ?? 0;
        this.centerDecDeg = opts.centerDecDeg ?? 0;
        this.fovDeg = opts.fovDeg ?? 90;
        this.cameraFov = null;
        this.dsoMarkers = [];
        this.mosaicPlan = null;
        this.targetMarker = null;          // { raHours, decDeg, name }
        this.onClick = opts.onClick || null;
        this._dpr = window.devicePixelRatio || 1;
        this._resize();
        this._wireInput();

        // Re-fit on parent resize
        if ('ResizeObserver' in window) {
            this._ro = new ResizeObserver(() => { this._resize(); this.redraw(); });
            this._ro.observe(canvas);
        }
    }

    _resize() {
        const rect = this.canvas.getBoundingClientRect();
        this.canvas.width = Math.max(1, Math.floor(rect.width * this._dpr));
        this.canvas.height = Math.max(1, Math.floor(rect.height * this._dpr));
        this.cssW = rect.width;
        this.cssH = rect.height;
    }

    // ---- public setters ----
    setCenter(ra, dec) { this.centerRaHours = ra; this.centerDecDeg = dec; this.redraw(); }
    setFov(deg) { this.fovDeg = Math.max(0.5, Math.min(180, deg)); this.redraw(); }
    setCameraFov(fov) { this.cameraFov = fov; this.redraw(); }
    setDsoMarkers(list) { this.dsoMarkers = list || []; this.redraw(); }
    setMosaic(plan) { this.mosaicPlan = plan; this.redraw(); }
    setTarget(t) { this.targetMarker = t; this.redraw(); }

    // ---- coordinate transforms ----
    // Stereographic projection centred on (centerRaHours, centerDecDeg).
    // Returns {x, y} in CSS pixels relative to canvas origin, or null if
    // the point is on the far hemisphere.
    _project(raHours, decDeg) {
        const lambda = raHours * 15 * Math.PI / 180;
        const phi = decDeg * Math.PI / 180;
        const lambda0 = this.centerRaHours * 15 * Math.PI / 180;
        const phi0 = this.centerDecDeg * Math.PI / 180;
        const cosc = Math.sin(phi0) * Math.sin(phi) +
                     Math.cos(phi0) * Math.cos(phi) * Math.cos(lambda - lambda0);
        if (cosc < -0.1) return null; // far side
        const k = 2 / (1 + cosc);
        const x = k * Math.cos(phi) * Math.sin(lambda - lambda0);
        const y = k * (Math.cos(phi0) * Math.sin(phi) -
                       Math.sin(phi0) * Math.cos(phi) * Math.cos(lambda - lambda0));
        // Scale so that fovDeg matches the smaller canvas dimension
        const scale = Math.min(this.cssW, this.cssH) / (this.fovDeg * Math.PI / 180) / 2;
        return {
            x: this.cssW / 2 + x * scale,
            y: this.cssH / 2 - y * scale  // canvas y grows down; flip
        };
    }

    // Inverse: pixel → (raHours, decDeg) via stereographic unprojection.
    _unproject(px, py) {
        const scale = Math.min(this.cssW, this.cssH) / (this.fovDeg * Math.PI / 180) / 2;
        const x = (px - this.cssW / 2) / scale;
        const y = -(py - this.cssH / 2) / scale;
        const rho = Math.sqrt(x*x + y*y);
        if (rho === 0) return { raHours: this.centerRaHours, decDeg: this.centerDecDeg };
        const c = 2 * Math.atan(rho / 2);
        const phi0 = this.centerDecDeg * Math.PI / 180;
        const lambda0 = this.centerRaHours * 15 * Math.PI / 180;
        const phi = Math.asin(Math.cos(c) * Math.sin(phi0) + (y * Math.sin(c) * Math.cos(phi0)) / rho);
        const lambda = lambda0 + Math.atan2(x * Math.sin(c),
            rho * Math.cos(phi0) * Math.cos(c) - y * Math.sin(phi0) * Math.sin(c));
        let raHours = (lambda * 180 / Math.PI) / 15;
        while (raHours < 0) raHours += 24;
        while (raHours >= 24) raHours -= 24;
        return { raHours, decDeg: phi * 180 / Math.PI };
    }

    // ---- input ----
    _wireInput() {
        let dragging = false, lastX = 0, lastY = 0;
        this.canvas.addEventListener('mousedown', (e) => {
            dragging = true; lastX = e.offsetX; lastY = e.offsetY;
            this._dragMoved = false;
        });
        window.addEventListener('mousemove', (e) => {
            if (!dragging) return;
            const dx = e.offsetX - lastX, dy = e.offsetY - lastY;
            if (Math.abs(dx) + Math.abs(dy) > 3) this._dragMoved = true;
            // pan: shift the centre proportional to (dx, dy)
            const scale = Math.min(this.cssW, this.cssH) / (this.fovDeg * Math.PI / 180) / 2;
            const cosDec = Math.max(0.05, Math.cos(this.centerDecDeg * Math.PI / 180));
            this.centerRaHours -= (dx / scale) * 180 / Math.PI / 15 / cosDec;
            this.centerDecDeg  += (dy / scale) * 180 / Math.PI;
            while (this.centerRaHours < 0) this.centerRaHours += 24;
            while (this.centerRaHours >= 24) this.centerRaHours -= 24;
            this.centerDecDeg = Math.max(-89.5, Math.min(89.5, this.centerDecDeg));
            lastX = e.offsetX; lastY = e.offsetY;
            this.redraw();
        });
        window.addEventListener('mouseup', (e) => {
            if (dragging && !this._dragMoved && this.onClick) {
                const coords = this._unproject(e.offsetX, e.offsetY);
                this.onClick(coords.raHours, coords.decDeg);
            }
            dragging = false;
        });
        this.canvas.addEventListener('wheel', (e) => {
            e.preventDefault();
            const factor = e.deltaY > 0 ? 1.2 : 1/1.2;
            this.setFov(this.fovDeg * factor);
        }, { passive: false });
    }

    // ---- render ----
    redraw() {
        const ctx = this.ctx;
        ctx.save();
        ctx.scale(this._dpr, this._dpr);
        // Background
        ctx.fillStyle = '#000';
        ctx.fillRect(0, 0, this.cssW, this.cssH);

        this._drawGrid(ctx);
        this._drawConstellations(ctx);
        this._drawStars(ctx);
        this._drawDsos(ctx);
        this._drawTarget(ctx);
        this._drawCameraFov(ctx);
        this._drawMosaic(ctx);
        this._drawCompass(ctx);

        ctx.restore();
    }

    _drawGrid(ctx) {
        ctx.strokeStyle = 'rgba(80,100,140,0.3)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        // RA hour lines every 1h
        for (let ra = 0; ra < 24; ra++) {
            let prev = null;
            for (let d = -90; d <= 90; d += 5) {
                const p = this._project(ra, d);
                if (!p) { prev = null; continue; }
                if (prev) { ctx.moveTo(prev.x, prev.y); ctx.lineTo(p.x, p.y); }
                prev = p;
            }
        }
        // Dec circles every 10°
        for (let d = -80; d <= 80; d += 10) {
            let prev = null;
            for (let ra = 0; ra <= 24; ra += 0.25) {
                const p = this._project(ra, d);
                if (!p) { prev = null; continue; }
                if (prev) { ctx.moveTo(prev.x, prev.y); ctx.lineTo(p.x, p.y); }
                prev = p;
            }
        }
        ctx.stroke();
    }

    _drawConstellations(ctx) {
        if (!window.CONSTELLATION_LINES) return;
        ctx.strokeStyle = 'rgba(120,140,180,0.35)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (const seg of window.CONSTELLATION_LINES) {
            const a = this._project(seg[0], seg[1]);
            const b = this._project(seg[2], seg[3]);
            if (!a || !b) continue;
            ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y);
        }
        ctx.stroke();
    }

    _drawStars(ctx) {
        if (!window.BRIGHT_STARS) return;
        ctx.fillStyle = '#fff';
        // Star size = function of magnitude (brighter → larger)
        for (const s of window.BRIGHT_STARS) {
            const p = this._project(s[0], s[1]);
            if (!p) continue;
            if (p.x < -5 || p.x > this.cssW + 5 || p.y < -5 || p.y > this.cssH + 5) continue;
            const mag = s[2];
            // Magnitude → pixel radius: brightest ~4.5px, mag 4 ~0.8px
            const r = Math.max(0.6, 5 - mag * 0.9);
            ctx.beginPath();
            ctx.arc(p.x, p.y, r, 0, 2 * Math.PI);
            ctx.fill();
            // Label brightest few when zoomed in
            if (mag < 1.5 && this.fovDeg < 60 && s[3]) {
                ctx.fillStyle = 'rgba(200,210,230,0.8)';
                ctx.font = '11px system-ui';
                ctx.fillText(s[3], p.x + r + 3, p.y + 4);
                ctx.fillStyle = '#fff';
            }
        }
    }

    _drawDsos(ctx) {
        if (!this.dsoMarkers || this.dsoMarkers.length === 0) return;
        ctx.strokeStyle = '#a78bfa';
        ctx.fillStyle = 'rgba(167,139,250,0.85)';
        ctx.lineWidth = 1.2;
        ctx.font = '10px system-ui';
        for (const m of this.dsoMarkers) {
            const ra = m.ra ?? m.raHours; const dec = m.dec ?? m.decDeg;
            if (ra == null || dec == null) continue;
            const p = this._project(ra, dec);
            if (!p) continue;
            ctx.beginPath();
            ctx.arc(p.x, p.y, 6, 0, 2 * Math.PI);
            ctx.stroke();
            if (this.fovDeg < 90 && m.name) ctx.fillText(m.name, p.x + 8, p.y + 4);
        }
    }

    _drawTarget(ctx) {
        if (!this.targetMarker) return;
        const p = this._project(this.targetMarker.raHours, this.targetMarker.decDeg);
        if (!p) return;
        ctx.strokeStyle = '#fbbf24';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(p.x, p.y, 12, 0, 2 * Math.PI);
        ctx.moveTo(p.x - 18, p.y); ctx.lineTo(p.x - 6, p.y);
        ctx.moveTo(p.x + 6, p.y);  ctx.lineTo(p.x + 18, p.y);
        ctx.moveTo(p.x, p.y - 18); ctx.lineTo(p.x, p.y - 6);
        ctx.moveTo(p.x, p.y + 6);  ctx.lineTo(p.x, p.y + 18);
        ctx.stroke();
        if (this.targetMarker.name) {
            ctx.fillStyle = '#fbbf24';
            ctx.font = 'bold 12px system-ui';
            ctx.fillText(this.targetMarker.name, p.x + 22, p.y);
        }
    }

    _drawCameraFov(ctx) {
        if (!this.cameraFov || !this.targetMarker) return;
        const ra = this.targetMarker.raHours;
        const dec = this.targetMarker.decDeg;
        const halfW = this.cameraFov.widthDeg / 2;
        const halfH = this.cameraFov.heightDeg / 2;
        const cosDec = Math.cos(dec * Math.PI / 180) || 1e-6;
        // Four corners; cos(dec) correction on RA
        const raDeg = ra * 15;
        const corners = [
            [(raDeg - halfW/cosDec) / 15, dec - halfH],
            [(raDeg + halfW/cosDec) / 15, dec - halfH],
            [(raDeg + halfW/cosDec) / 15, dec + halfH],
            [(raDeg - halfW/cosDec) / 15, dec + halfH]
        ];
        ctx.strokeStyle = '#22c55e';
        ctx.lineWidth = 2;
        ctx.beginPath();
        const projected = corners.map(c => this._project(c[0], c[1]));
        if (projected.some(p => !p)) return;
        ctx.moveTo(projected[0].x, projected[0].y);
        for (let i = 1; i < projected.length; i++) ctx.lineTo(projected[i].x, projected[i].y);
        ctx.closePath();
        ctx.stroke();
    }

    _drawMosaic(ctx) {
        if (!this.mosaicPlan) return;
        ctx.strokeStyle = '#fbbf24';
        ctx.lineWidth = 1.5;
        const halfW = this.mosaicPlan.panelFovWidthDeg / 2;
        const halfH = this.mosaicPlan.panelFovHeightDeg / 2;
        for (const panel of this.mosaicPlan.panels) {
            const raDeg = panel.raHours * 15;
            const dec = panel.decDeg;
            const cosDec = Math.cos(dec * Math.PI / 180) || 1e-6;
            const corners = [
                [(raDeg - halfW/cosDec) / 15, dec - halfH],
                [(raDeg + halfW/cosDec) / 15, dec - halfH],
                [(raDeg + halfW/cosDec) / 15, dec + halfH],
                [(raDeg - halfW/cosDec) / 15, dec + halfH]
            ];
            const projected = corners.map(c => this._project(c[0], c[1]));
            if (projected.some(p => !p)) continue;
            ctx.beginPath();
            ctx.moveTo(projected[0].x, projected[0].y);
            for (let i = 1; i < projected.length; i++) ctx.lineTo(projected[i].x, projected[i].y);
            ctx.closePath();
            ctx.stroke();
        }
    }

    _drawCompass(ctx) {
        // Top-left readout of current centre + FOV
        ctx.fillStyle = 'rgba(20,30,50,0.7)';
        ctx.fillRect(6, 6, 200, 44);
        ctx.fillStyle = '#cbd5e1';
        ctx.font = '11px ui-monospace, Menlo, monospace';
        const ra = this.centerRaHours;
        const dec = this.centerDecDeg;
        const h = Math.floor(ra);
        const m = Math.floor((ra - h) * 60);
        const s = ((ra - h) * 60 - m) * 60;
        const decAbs = Math.abs(dec);
        const dd = Math.floor(decAbs);
        const dm = Math.floor((decAbs - dd) * 60);
        ctx.fillText(`RA  ${h}h ${m.toString().padStart(2,'0')}m ${s.toFixed(0).padStart(2,'0')}s`, 12, 22);
        ctx.fillText(`Dec ${dec < 0 ? '-' : '+'}${dd}° ${dm.toString().padStart(2,'0')}'`, 12, 36);
        ctx.fillText(`FOV ${this.fovDeg.toFixed(1)}°`, 130, 22);
    }

    destroy() {
        if (this._ro) try { this._ro.disconnect(); } catch {}
    }
}

window.OfflineSkyViewer = OfflineSkyViewer;
