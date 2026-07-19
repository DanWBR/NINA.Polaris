#ifndef POLARIS_LLAMA_BRIDGE_H
#define POLARIS_LLAMA_BRIDGE_H

// C contract for the in-process llama.cpp server on iOS.
//
// iOS forbids spawning a subprocess, so the Android path (exec llama-server)
// cannot be reused. Instead the model host runs IN-PROCESS: this bridge starts
// llama.cpp's OpenAI-compatible server on a loopback port on a background
// thread. Because it is a real HTTP server on 127.0.0.1, the Canopus client's
// existing provider (provider-local.js, HTTP to localhost) drives it UNCHANGED,
// exactly like Android. Only the start mechanism differs, not the transport.
//
// The implementation lives in the vendored llama.cpp xcframework (built from
// server.cpp + libllama with these entry points; see README). This header is
// what the Swift plugin links against.

#ifdef __cplusplus
extern "C" {
#endif

/// Start the in-process server against `model_path` on 127.0.0.1:`port`.
/// Mirrors the validated flags: weights resident (no mmap), the model's chat
/// template (jinja) for tool calls, `threads` generation threads, `ctx_size`
/// context, and KV prefix reuse so the tool catalog is paid once per session.
/// Returns 0 on success (server bound and listening), non-zero on failure.
/// Non-blocking: the server runs on its own thread.
int polaris_llama_start(const char *model_path, int port, int threads, int ctx_size);

/// Stop the server and free the model. Safe to call when not running.
void polaris_llama_stop(void);

/// 1 if the server is currently listening, 0 otherwise.
int polaris_llama_is_running(void);

#ifdef __cplusplus
}
#endif

#endif /* POLARIS_LLAMA_BRIDGE_H */
