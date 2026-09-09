// The host's profile must never be overwritten with the client's defaults.
//
// `settings` in app.js is the HOST's profile: location, focal length,
// auto-connect on startup, output paths. It used to be mirrored into
// localStorage and read back at start-up, and that broke in two ways at once.
//
// localStorage is keyed by ORIGIN, so changing the host's IP address means a
// different origin and no mirror at all. And an empty mirror is not empty
// settings, it is settings not read yet: the code defaults (latitude 0,
// auto-connect off) sat in the object until the profile arrived, so any save in
// that window pushed those defaults over the host's real values. Reported from
// the field: a new IP address asked for the coordinates again and the hardware
// stopped connecting at boot, permanently, because the host's own profile had
// been overwritten with zeros.
//
// The functions are cut out of app.js by name and run against a stub `this`,
// the same trick histo-math-test.mjs and install-linux-helpers-test.sh use.
//
//   node scripts/tests/host-settings-test.mjs

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const SRC = path.join(here, '..', '..', 'src', 'NINA.Polaris', 'wwwroot', 'js', 'app.js');
const src = fs.readFileSync(SRC, 'utf8');

let fails = 0;
const ok = (m) => console.log('  ok   ' + m);
const bad = (m) => { console.log('  FAIL ' + m); fails++; };

function lift(name) {
    // Some of these are declared `async name(`, so try both spellings.
    let start = src.indexOf(`\n        ${name}(`);
    if (start < 0) start = src.indexOf(`\n        async ${name}(`);
    if (start < 0) throw new Error(`not found in app.js: ${name}`);
    const end = src.indexOf('\n        },', start);
    if (end < 0) throw new Error(`unterminated: ${name}`);
    return src.slice(start + 1, end + '\n        },'.length).replace(/\r/g, '');
}

const app = eval('({' + ['saveSettings', 'saveSettingsToServer', 'dismissLocationSetup']
    .map(lift).join('\n') + '\n})');

function stub(over) {
    const calls = [];
    return Object.assign({
        _settingsLoaded: false,
        settings: { latitude: 0, longitude: 0, altitude: 0, autoConnectOnStartup: false },
        exposure: 1, gain: 100, offset: 0, binning: '1',
        showLocationSetup: true,
        toast() {},
        apiPost(url, _n, opts) { calls.push({ url, body: opts && opts.body }); return Promise.resolve({ ok: true }); },
        calls,
        saveSettings: app.saveSettings,
        saveSettingsToServer: app.saveSettingsToServer,
        dismissLocationSetup: app.dismissLocationSetup,
    }, over);
}

console.log('== the profile is not written before it has been read ==');
{
    // THE regression. A save fired in the window between page load and the
    // profile arriving would carry latitude 0 and auto-connect false.
    const s = stub();
    await s.saveSettingsToServer();
    (s.calls.length === 0)
        ? ok('an unloaded profile is not saved over')
        : bad(`PUT fired with defaults: ${s.calls[0].body}`);

    // Through the front door too, since that is what the settings panel calls.
    const s2 = stub();
    s2.saveSettings();
    (s2.calls.length === 0) ? ok('saveSettings is gated the same way')
                            : bad('saveSettings wrote the defaults');
}

console.log('== once read, saving works normally ==');
{
    const s = stub({
        _settingsLoaded: true,
        settings: { latitude: -23.5, longitude: -46.6, altitude: 760,
                    autoConnectOnStartup: true },
    });
    await s.saveSettingsToServer();
    if (s.calls.length !== 1) { bad(`expected one PUT, got ${s.calls.length}`); }
    else {
        const b = JSON.parse(s.calls[0].body);
        (b.latitude === -23.5 && b.longitude === -46.6)
            ? ok('the coordinates go to the host') : bad(`coords were ${b.latitude}, ${b.longitude}`);
        (b.autoConnectOnStartup === true)
            ? ok('auto-connect on startup goes to the host') : bad('auto-connect was dropped');
    }
}

console.log('== the host owns the answer to the location prompt ==');
{
    // It was a localStorage flag, so a new IP address asked again for something
    // the operator had already declined.
    const s = stub({ _settingsLoaded: true });
    s.dismissLocationSetup(true);
    (s.settings.locationPromptDismissed === true)
        ? ok('dismissing sets the profile field') : bad('dismissal was not recorded');
    (s.calls.length === 1 && JSON.parse(s.calls[0].body).locationPromptDismissed === true)
        ? ok('and it reaches the host') : bad('dismissal never left the browser');

    const s2 = stub({ _settingsLoaded: true });
    s2.dismissLocationSetup(false);
    (!s2.settings.locationPromptDismissed && s2.calls.length === 0)
        ? ok('"ask me later" records nothing') : bad('a temporary dismissal was persisted');
}

console.log('== nothing host-owned is left in localStorage ==');
{
    for (const key of ['nina-settings', 'nina-location-prompted']) {
        src.includes(`'${key}'`)
            ? bad(`app.js still uses localStorage key ${key}`)
            : ok(`${key} is gone`);
    }
}

console.log();
console.log(fails === 0 ? 'all checks passed' : `${fails} check(s) failed`);
process.exit(fails === 0 ? 0 : 1);
