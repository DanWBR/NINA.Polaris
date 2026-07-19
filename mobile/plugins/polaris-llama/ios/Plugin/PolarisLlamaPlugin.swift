import Foundation
import Capacitor

/**
 * iOS stub. iOS forbids spawning a subprocess, so `llama-server` cannot run
 * here; the on-device backend needs llama.cpp embedded in-process (an
 * xcframework) with the same start/stop/status/downloadModel surface. Until
 * that lands, every method reports unavailable and `status` says not-ready, so
 * the host UI keeps the on-device tier hidden on iOS.
 */
@objc(PolarisLlamaPlugin)
public class PolarisLlamaPlugin: CAPPlugin {

    private func unsupported(_ call: CAPPluginCall) {
        call.unavailable("The on-device model backend is not yet available on iOS (in-process llama.cpp embed pending).")
    }

    @objc func downloadModel(_ call: CAPPluginCall) { unsupported(call) }
    @objc func deleteModel(_ call: CAPPluginCall) { unsupported(call) }
    @objc func start(_ call: CAPPluginCall) { unsupported(call) }
    @objc func stop(_ call: CAPPluginCall) { unsupported(call) }

    @objc func status(_ call: CAPPluginCall) {
        call.resolve([
            "modelReady": false,
            "running": false,
            "url": "",
            "modelPath": "",
            "modelBytes": 0
        ])
    }
}
