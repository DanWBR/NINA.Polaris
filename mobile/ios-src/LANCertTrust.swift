import Foundation
import WebKit
import Capacitor

// Accept the Polaris SBC's self-signed HTTPS cert on the LAN.
//
// Polaris serves its UI over HTTPS with a self-signed cert (needed for a
// secure browsing context: WebGPU/WASM, geolocation, service workers). In a
// browser the user can tap "visit anyway", but that is only a per-session
// Safari override — it never reaches WKWebView, and WKWebView offers no such
// prompt. So the app's cross-origin <iframe> that hosts the Polaris UI failed
// its TLS handshake and rendered blank.
//
// Capacitor's navigation delegate (CAPWebViewDelegationHandler) never
// implements the server-trust auth-challenge callback, so WKWebView falls back
// to default handling, which rejects a self-signed cert. This extension ADDS
// that callback to the existing delegate (the class is `open` + `@objc`, and
// the base class does not declare this selector, so this is a new method — not
// an override). WKWebView's `respondsToSelector:` check then finds it and calls
// it, letting us accept the server trust.
//
// SCOPE: we only trust the cert for private / link-local / loopback hosts
// (LAN appliance). Any public host — e.g. the Polaris Relay over a real
// LettuceEncrypt cert — falls through to normal validation, so this does not
// weaken security off-LAN.
extension WebViewDelegationHandler {
    @objc(webView:didReceiveAuthenticationChallenge:completionHandler:)
    public func webView(_ webView: WKWebView,
                        didReceive challenge: URLAuthenticationChallenge,
                        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let serverTrust = challenge.protectionSpace.serverTrust,
              polarisIsLanHost(challenge.protectionSpace.host) else {
            // Not a LAN server-trust challenge → validate normally.
            completionHandler(.performDefaultHandling, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: serverTrust))
    }
}

/// True for private / link-local / loopback hosts (an IP literal or a name).
private func polarisIsLanHost(_ host: String) -> Bool {
    let h = host.lowercased()
    if h.isEmpty { return false }
    if h == "localhost" || h.hasSuffix(".local") { return true }

    // IPv6: loopback, link-local (fe80::/10) and unique-local (fc00::/7).
    if h == "::1" || h.hasPrefix("fe80:") || h.hasPrefix("fc") || h.hasPrefix("fd") {
        return true
    }

    // IPv4 private ranges + loopback + link-local.
    let octets = h.split(separator: ".").compactMap { Int($0) }
    if octets.count == 4, octets.allSatisfy({ (0...255).contains($0) }) {
        switch (octets[0], octets[1]) {
        case (10, _):                       return true          // 10.0.0.0/8
        case (172, 16...31):                return true          // 172.16.0.0/12
        case (192, 168):                    return true          // 192.168.0.0/16
        case (169, 254):                    return true          // 169.254.0.0/16
        case (127, _):                      return true          // loopback
        default:                            return false
        }
    }
    return false
}
