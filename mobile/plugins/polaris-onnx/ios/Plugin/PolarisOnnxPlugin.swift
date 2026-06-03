import Foundation
import Capacitor
import WebKit
import onnxruntime_objc

/**
 * Native ONNX Runtime bridge (M1) for iOS. Runs the GraXpert .onnx
 * models through ONNX Runtime with the CoreML execution provider, so
 * the existing JS pipelines get Apple Neural Engine / GPU acceleration
 * without Safari's WebGPU memory cliff.
 *
 * Written against the `onnxruntime-objc` pod (see PolarisOnnx.podspec).
 * `mobile/` is outside NINA.sln and builds only on macOS with Xcode.
 */
@objc(PolarisOnnxPlugin)
public class PolarisOnnxPlugin: CAPPlugin {

    private var env: ORTEnv?
    private var sessions: [String: ORTSession] = [:]
    private var counter: Int = 1
    private let lock = NSLock()

    public override func load() {
        env = try? ORTEnv(loggingLevel: ORTLoggingLevel.warning)
        injectShimAtDocumentStart()
    }

    /// Inject onnx-native-shim.js into every page at document-start so the
    /// remote Polaris UI's unchanged onnx-pipelines.js uses native `ort`.
    private func injectShimAtDocumentStart() {
        guard
            let url = Bundle.main.url(forResource: "onnx-native-shim", withExtension: "js",
                                      subdirectory: "public"),
            let src = try? String(contentsOf: url, encoding: .utf8),
            let ucc = self.bridge?.webView?.configuration.userContentController
        else { return }
        let script = WKUserScript(source: src,
                                  injectionTime: .atDocumentStart,
                                  forMainFrameOnly: false)
        ucc.addUserScript(script)
    }

    @objc func info(_ call: CAPPluginCall) {
        call.resolve(["version": ORTVersion(), "providers": ["coreml", "xnnpack", "cpu"]])
    }

    @objc func createSession(_ call: CAPPluginCall) {
        guard let env = env, let b64 = call.getString("model"),
              let modelData = Data(base64Encoded: b64) else {
            call.reject("model (base64) required"); return
        }
        do {
            let opts = try ORTSessionOptions()
            try opts.setGraphOptimizationLevel(.all)
            var provider = "cpu"
            // Try CoreML (ANE/GPU). If the model/op set isn't supported it
            // silently runs the unsupported parts on CPU.
            if let coreml = try? ORTCoreMLExecutionProviderOptions() {
                if (try? opts.appendCoreMLExecutionProvider(with: coreml)) != nil {
                    provider = "coreml"
                }
            }
            // Write the model to a temp file (ORTSession takes a path).
            let tmp = FileManager.default.temporaryDirectory
                .appendingPathComponent("polaris-\(counter).onnx")
            try modelData.write(to: tmp)
            let session = try ORTSession(env: env, modelPath: tmp.path, sessionOptions: opts)
            try? FileManager.default.removeItem(at: tmp)

            lock.lock(); let handle = "s\(counter)"; counter += 1; sessions[handle] = session; lock.unlock()

            let inNames = (try? session.inputNames()) ?? []
            let outNames = (try? session.outputNames()) ?? []
            call.resolve([
                "handle": handle, "provider": provider,
                "inputNames": inNames, "outputNames": outNames
            ])
        } catch {
            call.reject("createSession failed: \(error.localizedDescription)")
        }
    }

    @objc func run(_ call: CAPPluginCall) {
        guard let handle = call.getString("handle"),
              let session = sessions[handle],
              let feeds = call.getObject("feeds") else {
            call.reject("unknown session handle / feeds"); return
        }
        let started = Date()
        do {
            var inputs: [String: ORTValue] = [:]
            for (name, raw) in feeds {
                guard let t = raw as? [String: Any] else { continue }
                inputs[name] = try toOrtValue(t)
            }
            let outNames = Set((try? session.outputNames()) ?? [])
            let results = try session.run(withInputs: inputs,
                                          outputNames: outNames,
                                          runOptions: nil)
            var outputs: [String: Any] = [:]
            for (name, value) in results {
                outputs[name] = try fromOrtValue(value)
            }
            call.resolve([
                "outputs": outputs,
                "ms": Date().timeIntervalSince(started) * 1000.0
            ])
        } catch {
            call.reject("run failed: \(error.localizedDescription)")
        }
    }

    @objc func releaseSession(_ call: CAPPluginCall) {
        if let h = call.getString("handle") { lock.lock(); sessions.removeValue(forKey: h); lock.unlock() }
        call.resolve()
    }

    // ---- tensor marshalling (base64 LE <-> ORTValue) ----

    private func toOrtValue(_ t: [String: Any]) throws -> ORTValue {
        guard let b64 = t["data"] as? String, let data = Data(base64Encoded: b64),
              let dims = t["dims"] as? [NSNumber] else {
            throw NSError(domain: "PolarisOnnx", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "bad tensor"])
        }
        let type = (t["type"] as? String) ?? "float32"
        let elem: ORTTensorElementDataType
        switch type {
        case "float32": elem = .float
        case "float16": elem = .float16
        case "int32":   elem = .int32
        case "int64":   elem = .int64
        case "uint8":   elem = .uInt8
        case "bool":    elem = .uInt8
        default: throw NSError(domain: "PolarisOnnx", code: 2,
                               userInfo: [NSLocalizedDescriptionKey: "unsupported type \(type)"])
        }
        let mutable = NSMutableData(data: data)
        return try ORTValue(tensorData: mutable, elementType: elem, shape: dims)
    }

    private func fromOrtValue(_ v: ORTValue) throws -> [String: Any] {
        let info = try v.tensorTypeAndShapeInfo()
        let data = try v.tensorData() as Data
        let type: String
        switch info.elementType {
        case .float:    type = "float32"
        case .float16:  type = "float16"
        case .int32:    type = "int32"
        case .int64:    type = "int64"
        case .uInt8:    type = "uint8"
        default:        type = "float32"
        }
        return [
            "data": data.base64EncodedString(),
            "type": type,
            "dims": info.shape
        ]
    }
}
