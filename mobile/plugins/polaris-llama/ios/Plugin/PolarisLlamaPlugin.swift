import Foundation
import Capacitor
import CryptoKit

/**
 * Canopus on-device backend (iOS). iOS forbids spawning a subprocess, so instead
 * of exec'ing llama-server (the Android path) the model host runs IN-PROCESS:
 * `polaris_llama_start` (from the vendored llama.cpp xcframework) starts an
 * OpenAI-compatible server on 127.0.0.1 on a background thread. Because that is a
 * real loopback HTTP server, the Canopus client's existing provider drives it
 * unchanged, exactly like Android, no JS branch needed.
 *
 * The download / status / delete surface below is pure Foundation and is complete;
 * start/stop bind to the bridge, whose implementation ships in the xcframework
 * (see README + PolarisLlamaBridge.h).
 */
@objc(PolarisLlamaPlugin)
public class PolarisLlamaPlugin: CAPPlugin {

    private let defaultPort = 8823
    private var downloader: Downloader?

    private func modelDir() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("canopus", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
    private func modelFile() -> URL { modelDir().appendingPathComponent("model.gguf") }

    private func modelBytes() -> Int64 {
        let attrs = try? FileManager.default.attributesOfItem(atPath: modelFile().path)
        return (attrs?[.size] as? Int64) ?? 0
    }

    // ---- model download -----------------------------------------------------

    @objc func downloadModel(_ call: CAPPluginCall) {
        guard let urlStr = call.getString("url"), let url = URL(string: urlStr) else {
            call.reject("url is required"); return
        }
        let expected = Int64(call.getInt("expectedBytes") ?? 0)
        let sha = call.getString("sha256")
        let dest = modelFile()

        // Already complete? Skip.
        if FileManager.default.fileExists(atPath: dest.path),
           expected <= 0 || modelBytes() == expected {
            call.resolve(["modelPath": dest.path, "bytes": modelBytes()])
            return
        }

        let dl = Downloader(
            url: url, dest: dest,
            onProgress: { [weak self] received, total in
                self?.notifyListeners("downloadProgress", data: [
                    "receivedBytes": received,
                    "totalBytes": total,
                    "percent": total > 0 ? Int(received * 100 / total) : -1
                ])
            },
            onDone: { [weak self] in
                guard let self = self else { return }
                if let sha = sha, !sha.isEmpty, sha.lowercased() != self.sha256Hex(dest) {
                    try? FileManager.default.removeItem(at: dest)
                    call.reject("checksum mismatch"); return
                }
                call.resolve(["modelPath": dest.path, "bytes": self.modelBytes()])
                self.downloader = nil
            },
            onError: { [weak self] err in
                call.reject("download failed: \(err.localizedDescription)")
                self?.downloader = nil
            })
        self.downloader = dl
        dl.start()
    }

    @objc func deleteModel(_ call: CAPPluginCall) {
        try? FileManager.default.removeItem(at: modelFile())
        call.resolve()
    }

    // ---- server lifecycle (in-process, via the xcframework) -----------------

    @objc func start(_ call: CAPPluginCall) {
        let model = modelFile()
        guard FileManager.default.fileExists(atPath: model.path) else {
            call.reject("model not downloaded"); return
        }
        let port = Int32(call.getInt("port") ?? defaultPort)
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let threads = Int32(call.getInt("threads") ?? max(2, (cores + 1) / 2))
        let ctx = Int32(call.getInt("contextSize") ?? 8192)

        DispatchQueue.global(qos: .userInitiated).async {
            let rc = polaris_llama_start(model.path, port, threads, ctx)
            if rc != 0 { call.reject("llama server failed to start (code \(rc))"); return }
            // Wait until it is actually listening (weights load can take a while).
            let deadline = Date().addingTimeInterval(120)
            while Date() < deadline {
                if polaris_llama_is_running() != 0 {
                    let base = "http://127.0.0.1:\(port)"
                    call.resolve(["url": "\(base)/v1", "port": Int(port)])
                    return
                }
                Thread.sleep(forTimeInterval: 0.5)
            }
            polaris_llama_stop()
            call.reject("llama server did not become ready in time")
        }
    }

    @objc func stop(_ call: CAPPluginCall) {
        polaris_llama_stop()
        call.resolve()
    }

    @objc func status(_ call: CAPPluginCall) {
        let ready = FileManager.default.fileExists(atPath: modelFile().path) && modelBytes() > 0
        let running = polaris_llama_is_running() != 0
        let port = call.getInt("port") ?? defaultPort
        call.resolve([
            "modelReady": ready,
            "running": running,
            "url": running ? "http://127.0.0.1:\(port)/v1" : "",
            "modelPath": ready ? modelFile().path : "",
            "modelBytes": ready ? modelBytes() : 0
        ])
    }

    // ---- helpers ------------------------------------------------------------

    private func sha256Hex(_ url: URL) -> String {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return "" }
        defer { try? handle.close() }
        var hasher = SHA256()
        while case let chunk = handle.readData(ofLength: 1 << 16), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

/// URLSession download with progress reporting. Kept separate so the plugin stays
/// readable; the plugin holds a strong reference for the download's lifetime.
private class Downloader: NSObject, URLSessionDownloadDelegate {
    private let url: URL
    private let dest: URL
    private let onProgress: (Int64, Int64) -> Void
    private let onDone: () -> Void
    private let onError: (Error) -> Void
    private lazy var session: URLSession =
        URLSession(configuration: .default, delegate: self, delegateQueue: nil)

    init(url: URL, dest: URL,
         onProgress: @escaping (Int64, Int64) -> Void,
         onDone: @escaping () -> Void,
         onError: @escaping (Error) -> Void) {
        self.url = url; self.dest = dest
        self.onProgress = onProgress; self.onDone = onDone; self.onError = onError
    }

    func start() { session.downloadTask(with: url).resume() }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didWriteData bytesWritten: Int64, totalBytesWritten: Int64,
                    totalBytesExpectedToWrite: Int64) {
        onProgress(totalBytesWritten, totalBytesExpectedToWrite)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {
        do {
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: location, to: dest)
            onDone()
        } catch {
            onError(error)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didCompleteWithError error: Error?) {
        if let error = error { onError(error) }
        session.finishTasksAndInvalidate()   // release the session's retain on this delegate
    }
}
