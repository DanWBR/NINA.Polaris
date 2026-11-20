// The app's pages are plain modules without a bundler, so they reach the
// plugin through the bridge (Capacitor.registerPlugin('PolarisSmb') or
// Capacitor.Plugins.PolarisSmb), not through this file. It exists because the
// Capacitor CLI expects a package entry point.
//
// API (Android and iOS):
//   discover({ timeoutMs? })                 -> { servers: [{ name, address }], onNetwork }
//   listShares({ server, host, name?, username?, password?, domain?, remember? })
//                                            -> { address, name, shares: [{ name, comment }] }
//   list({ ...server, share, path })         -> { address, entries: [{ name, dir, size, modified }] }
//   download({ ...server, share, path, id }) -> { address, path, size }   events: downloadProgress
//   cancelDownload({ id }), releaseFile({ path })
//   hasCredentials({ server }) -> { saved, username }, forgetCredentials({ server })
//   describeUri({ uri })                     -> { name, size }
// Errors carry a code: AUTH, NO_SHARE, NOT_FOUND, UNREACHABLE, CANCELLED or ERROR.
export {};
