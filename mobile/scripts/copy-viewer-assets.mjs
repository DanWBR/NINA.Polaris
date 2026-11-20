// Copy the offline viewer's third-party and generated assets into www/files/vendor.
//
// The Files page (www/files) runs inside the app with no host, so everything it
// loads has to ship in the app bundle:
//   - OpenSeadragon, the same copy the Polaris UI vendors;
//   - the NINA.Polaris.Wasm bundle, which decodes and stretches FITS files.
//     It is a build output (dotnet publish of src/NINA.Polaris.Wasm, mirrored to
//     src/NINA.Polaris/wwwroot/js/wasm by deploy/build-wasm.ps1 or the deb build),
//     so build it before running this.
//
// www/files/vendor is gitignored. Run before `cap sync`:
//   node scripts/copy-viewer-assets.mjs        (or: npm run viewer:assets)

import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const mobile = join(dirname(fileURLToPath(import.meta.url)), '..');
const wwwroot = join(mobile, '..', 'src', 'NINA.Polaris', 'wwwroot');
const vendor = join(mobile, 'www', 'files', 'vendor');

rmSync(vendor, { recursive: true, force: true });
mkdirSync(vendor, { recursive: true });

const osd = join(wwwroot, 'js', 'lib', 'openseadragon', 'openseadragon.min.js');
cpSync(osd, join(vendor, 'openseadragon.min.js'));
console.log('copied OpenSeadragon');

const wasm = join(wwwroot, 'js', 'wasm');
if (existsSync(join(wasm, 'main.js'))) {
  cpSync(wasm, join(vendor, 'wasm'), { recursive: true });
  console.log('copied the NINA.Polaris.Wasm bundle');
} else {
  // Not fatal: the page still opens JPG and PNG, and says the FITS renderer is
  // missing. A release build must not ship like this, so the CI step checks.
  console.warn('WARNING: ' + wasm + ' has no main.js; build src/NINA.Polaris.Wasm first.');
  process.exitCode = process.env.VIEWER_ASSETS_REQUIRE_WASM ? 1 : 0;
}
