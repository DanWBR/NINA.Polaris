/*
 * M3 - "Aim" helper. Standalone, dependency-free page.
 *
 * - Polar align: points you at the celestial pole (alt = |latitude|,
 *   az = 0 N / 180 S). Pure geometry, exact.
 * - Target: converts a target RA/Dec to alt/az for "now" at the
 *   observer's location (client-side astronomy, exact to <~arcmin for
 *   pointing purposes), and guides you to it.
 * - Level: a bubble level for the tripod (accelerometer/tilt).
 *
 * Sensors use the standard DeviceOrientation web APIs (works in the
 * Capacitor WebView and in a desktop/mobile browser). The astronomy is
 * exact; the device-frame mapping (heading/tilt) is best-effort and
 * depends on a calibrated phone compass -- documented for the operator.
 *
 * Self-contained: observer lat/lon + target RA/Dec come from the query
 * string (?lat=&lon=&ra=&dec=&name=), localStorage, manual inputs, or
 * (lat/lon) the browser geolocation. The connected Polaris app can pass
 * them via the query string later without any change here.
 */
(function () {
  'use strict';

  var D2R = Math.PI / 180, R2D = 180 / Math.PI;
  var state = {
    mode: 'pole',
    lat: null, lon: null,
    ra: null, dec: null, name: '',
    heading: null, pitch: null, roll: null,
    sensorsOk: false,
  };

  var el = function (id) { return document.getElementById(id); };
  var stage = el('stage'), enableOverlay = el('enableOverlay');

  // ---------- persistence / inputs ----------
  function loadInitial() {
    var q = new URLSearchParams(location.search);
    var ls = function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } };
    state.lat = num(q.get('lat')) ?? num(ls('aim.lat'));
    state.lon = num(q.get('lon')) ?? num(ls('aim.lon'));
    state.ra = num(q.get('ra')); state.dec = num(q.get('dec'));
    state.name = q.get('name') || '';
    if (state.lat != null) el('inLat').value = state.lat;
    if (state.lon != null) el('inLon').value = state.lon;
    if (state.ra != null) el('inRa').value = state.ra;
    if (state.dec != null) el('inDec').value = state.dec;
    if (state.name) el('inName').value = state.name;
    if (state.ra != null && state.dec != null) state.mode = 'target';
  }
  function num(v) { if (v == null || v === '') return null; var n = parseFloat(v); return isFinite(n) ? n : null; }

  function wireInputs() {
    el('inLat').addEventListener('change', function () {
      state.lat = num(this.value); try { localStorage.setItem('aim.lat', this.value); } catch (e) {} });
    el('inLon').addEventListener('change', function () {
      state.lon = num(this.value); try { localStorage.setItem('aim.lon', this.value); } catch (e) {} });
    el('inRa').addEventListener('change', function () { state.ra = num(this.value); });
    el('inDec').addEventListener('change', function () { state.dec = num(this.value); });
    el('inName').addEventListener('change', function () { state.name = this.value; });
  }

  // Best-effort auto-location (only if not already provided).
  function maybeGeolocate() {
    if (state.lat != null && state.lon != null) return;
    if (!navigator.geolocation) return;
    navigator.geolocation.getCurrentPosition(function (p) {
      if (state.lat == null) { state.lat = p.coords.latitude; el('inLat').value = state.lat.toFixed(4); }
      if (state.lon == null) { state.lon = p.coords.longitude; el('inLon').value = state.lon.toFixed(4); }
    }, function () {}, { maximumAge: 600000, timeout: 8000 });
  }

  // ---------- astronomy ----------
  function julianDate(date) { return date.getTime() / 86400000 + 2440587.5; }
  function gmstDeg(jd) {
    var T = (jd - 2451545.0) / 36525.0;
    var g = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
          + 0.000387933 * T * T - (T * T * T) / 38710000.0;
    return ((g % 360) + 360) % 360;
  }
  // RA in hours, Dec/lat/lon in degrees (lon east-positive). az from N, CW.
  function raDecToAltAz(raHours, decDeg, latDeg, lonDeg, date) {
    var lst = gmstDeg(julianDate(date)) + lonDeg;       // degrees
    var ha = (lst - raHours * 15.0) * D2R;              // hour angle rad
    var dec = decDeg * D2R, lat = latDeg * D2R;
    var sinAlt = Math.sin(dec) * Math.sin(lat) + Math.cos(dec) * Math.cos(lat) * Math.cos(ha);
    sinAlt = Math.max(-1, Math.min(1, sinAlt));
    var alt = Math.asin(sinAlt);
    var cosA = (Math.sin(dec) - Math.sin(lat) * sinAlt) / (Math.cos(lat) * Math.cos(alt));
    cosA = Math.max(-1, Math.min(1, cosA));
    var A = Math.acos(cosA) * R2D;
    var az = (Math.sin(ha) > 0) ? (360 - A) : A;
    return { az: az, alt: alt * R2D };
  }
  function polePos(latDeg) {
    if (latDeg == null) return null;
    return latDeg >= 0
      ? { az: 0, alt: Math.abs(latDeg), name: 'North celestial pole' }
      : { az: 180, alt: Math.abs(latDeg), name: 'South celestial pole' };
  }

  // The az/alt we should be pointing at, per mode.
  function aimTarget() {
    if (state.mode === 'pole') return polePos(state.lat);
    if (state.mode === 'target') {
      if (state.ra == null || state.dec == null || state.lat == null || state.lon == null) return null;
      var p = raDecToAltAz(state.ra, state.dec, state.lat, state.lon, new Date());
      p.name = state.name || 'Target';
      return p;
    }
    return null;
  }

  // ---------- sensors ----------
  function screenAngle() {
    var a = (screen.orientation && screen.orientation.angle) || window.orientation || 0;
    return a;
  }
  function onOrientation(e) {
    state.sensorsOk = true;
    // Heading: iOS exposes a true-north compass heading directly.
    if (typeof e.webkitCompassHeading === 'number' && !isNaN(e.webkitCompassHeading)) {
      state.heading = e.webkitCompassHeading;
    } else if (e.absolute && typeof e.alpha === 'number') {
      // alpha is CCW from north-ish; convert to CW compass heading and
      // compensate for the screen rotation.
      state.heading = (360 - e.alpha + screenAngle()) % 360;
    }
    // Tilt: beta = front/back (deg). Holding the phone upright like a
    // viewfinder, the back roughly points at altitude (beta - 90)... we
    // expose beta-90 clamped to 0..90 as "aim altitude".
    if (typeof e.beta === 'number') {
      // beta ~0 = phone flat (alt 0); beta ~90 = phone vertical (alt 90).
      state.pitch = clampAlt(e.beta);
      state.roll = e.gamma || 0;
      state._beta = e.beta; state._gamma = e.gamma || 0;
    }
    render();
  }
  // Map device beta (-180..180) to a 0..90 "pointing up" altitude. Flat
  // on a table (beta~0) -> 0; vertical (beta~90) -> 90.
  function clampAlt(beta) {
    var b = beta;
    if (b > 180) b -= 360;
    return Math.max(0, Math.min(90, b));
  }

  async function enableSensors() {
    var hint = el('enableHint');
    try {
      var DOE = window.DeviceOrientationEvent;
      if (DOE && typeof DOE.requestPermission === 'function') {
        var res = await DOE.requestPermission(); // iOS 13+, needs user gesture
        if (res !== 'granted') { hint.textContent = 'Permission denied. You can still use manual inputs.'; }
      }
      window.addEventListener('deviceorientationabsolute', onOrientation, true);
      window.addEventListener('deviceorientation', onOrientation, true);
      enableOverlay.hidden = true; stage.hidden = false;
      maybeGeolocate();
      render();
      // If no sensor event arrives shortly, still show the UI (numbers
      // work; the dial just won't track until sensors report).
      setTimeout(function () { if (!state.sensorsOk) hint.textContent = ''; }, 1500);
    } catch (err) {
      hint.textContent = 'Sensor error: ' + (err && err.message || err);
      enableOverlay.hidden = true; stage.hidden = false;
    }
  }

  // ---------- ticks ----------
  function buildTicks() {
    var g = el('ticks'), parts = '';
    for (var deg = 0; deg < 360; deg += 15) {
      var major = (deg % 90 === 0);
      var r1 = 100, r2 = major ? 88 : 93;
      var a = deg * D2R;
      var x1 = Math.sin(a) * r1, y1 = -Math.cos(a) * r1;
      var x2 = Math.sin(a) * r2, y2 = -Math.cos(a) * r2;
      parts += '<line class="tick' + (major ? ' major' : '') + '" x1="' + x1.toFixed(1) +
        '" y1="' + y1.toFixed(1) + '" x2="' + x2.toFixed(1) + '" y2="' + y2.toFixed(1) + '"/>';
    }
    g.innerHTML = parts;
  }

  // ---------- render ----------
  function fmtAz(a) { return a == null ? '--' : (a.toFixed(0) + '° ' + compass16(a)); }
  function compass16(a) {
    var dirs = ['N','NNE','NE','ENE','E','ESE','SE','SSE','S','SSW','SW','WSW','W','WNW','NW','NNW'];
    return dirs[Math.round(a / 22.5) % 16];
  }
  function angDiff(a, b) { var d = ((a - b + 540) % 360) - 180; return d; }

  function render() {
    var pole = state.mode === 'pole', tgt = state.mode === 'target', lvl = state.mode === 'level';
    el('aimView').style.display = lvl ? 'none' : '';
    el('tiltView').style.display = lvl ? 'none' : '';
    el('levelView').hidden = !lvl;

    var aim = aimTarget();
    el('roTargetName').textContent = aim ? aim.name : (tgt ? 'Set RA/Dec below' : 'Set latitude below');
    el('roTargetAzAlt').textContent = aim ? (fmtAz(aim.az) + '  /  ' + aim.alt.toFixed(0) + '°') : '--';
    el('roHeading').textContent = state.heading == null ? '--' : fmtAz(state.heading);
    el('roPitch').textContent = state.pitch == null ? '--' : (state.pitch.toFixed(0) + '°');

    // Compass ring rotates so N points at true north relative to top.
    if (state.heading != null)
      el('compassRot').setAttribute('transform', 'rotate(' + (-state.heading).toFixed(1) + ')');

    // Target marker at bearing (targetAz - heading) from the top pointer.
    var tm = el('targetMarker'), dot = tm.querySelector('.tgt');
    if (aim && state.heading != null) {
      var bearing = angDiff(aim.az, state.heading); // -180..180, 0 = aligned
      tm.setAttribute('transform', 'rotate(' + bearing.toFixed(1) + ')');
      var azAligned = Math.abs(bearing) <= 3;
      dot.classList.toggle('aligned', azAligned);
      var cta = el('aimCta');
      if (azAligned) { cta.textContent = 'Heading locked'; cta.classList.add('aligned'); }
      else {
        cta.classList.remove('aligned');
        cta.textContent = (bearing > 0 ? 'Turn right ' : 'Turn left ') + Math.abs(bearing).toFixed(0) + '°';
      }
    } else {
      el('aimCta').textContent = state.heading == null ? 'Waiting for compass…' : 'Set target / location';
    }

    // Altitude tilt bar (0..90 across the track).
    if (aim) {
      var pct = Math.max(0, Math.min(1, aim.alt / 90));
      el('tiltTarget').style.left = (pct * 100) + '%';
    }
    if (state.pitch != null) {
      var npct = Math.max(0, Math.min(1, state.pitch / 90));
      el('tiltNow').style.left = (npct * 100) + '%';
      var tcta = el('tiltCta');
      if (aim) {
        var dAlt = aim.alt - state.pitch;
        if (Math.abs(dAlt) <= 3) { tcta.textContent = 'Altitude locked'; tcta.classList.add('aligned'); }
        else { tcta.classList.remove('aligned');
          tcta.textContent = (dAlt > 0 ? 'Tilt up ' : 'Tilt down ') + Math.abs(dAlt).toFixed(0) + '°'; }
      }
    }

    // Bubble level (tripod). gamma=left/right, beta=front/back around flat.
    if (lvl && state._beta != null) {
      var bx = Math.max(-1, Math.min(1, (state._gamma || 0) / 30));
      var by = Math.max(-1, Math.min(1, ((state._beta || 0)) / 30));
      el('bubble').style.transform = 'translate(' + (bx * 80) + 'px,' + (by * 80) + 'px)';
      var off = Math.sqrt((state._gamma || 0) * (state._gamma || 0) + (state._beta || 0) * (state._beta || 0));
      el('levelReadout').textContent = off < 1 ? 'Level' :
        ('Off by ' + off.toFixed(1) + '°');
    }
  }

  // ---------- modes ----------
  function wireModes() {
    document.querySelectorAll('.mode').forEach(function (b) {
      b.addEventListener('click', function () {
        document.querySelectorAll('.mode').forEach(function (x) { x.classList.remove('active'); });
        b.classList.add('active');
        state.mode = b.getAttribute('data-mode');
        render();
      });
    });
  }

  // ---------- init ----------
  buildTicks();
  loadInitial();
  wireInputs();
  wireModes();
  // reflect initial mode in the buttons
  document.querySelectorAll('.mode').forEach(function (b) {
    b.classList.toggle('active', b.getAttribute('data-mode') === state.mode);
  });
  el('enableBtn').addEventListener('click', enableSensors);
  // Keep target alt/az fresh (sky moves) even without sensor events.
  setInterval(function () { if (!stage.hidden) render(); }, 1000);
})();
