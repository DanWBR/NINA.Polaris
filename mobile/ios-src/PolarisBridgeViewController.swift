import UIKit
import WebKit
import Capacitor

// Turn on WKWebView's element Fullscreen API.
//
// WKWebView ships with it OFF: `requestFullscreen` (and the webkit-prefixed
// variant) are not even defined on Element, so the Polaris fullscreen badge
// fell through to its "not supported in this browser" error inside the iOS
// app, while the same badge works in the Android app because the Android
// WebView enables the API by default. Capacitor never touches the
// preference, so the app has to.
//
// The preference must be set on the configuration BEFORE the WKWebView is
// built: afterwards `webView.configuration` is a copy and writing to it
// changes nothing. `webView(with:configuration:)` is the last hook that
// still holds the real object.
//
// Set through the ObjC accessor instead of the typed
// `isElementFullscreenEnabled` property: that property is annotated
// macOS-only in several iOS SDKs, so the typed form does not always compile,
// while the underlying preference does exist on iOS. The `responds(to:)`
// guard turns an SDK without it into a no-op rather than an
// NSUnknownKeyException at launch.
//
// Registered in Main.storyboard by scripts/ios-postadd.sh. `@objc` fixes the
// runtime name so the storyboard finds the class without a module prefix.
@objc(PolarisBridgeViewController)
class PolarisBridgeViewController: CAPBridgeViewController {
    override func webView(with frame: CGRect, configuration: WKWebViewConfiguration) -> WKWebView {
        let prefs = configuration.preferences
        if prefs.responds(to: NSSelectorFromString("setElementFullscreenEnabled:")) {
            prefs.setValue(true, forKey: "elementFullscreenEnabled")
        }
        return super.webView(with: frame, configuration: configuration)
    }
}
