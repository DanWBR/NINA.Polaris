/**
 * On-device local LLM backend for Canopus.
 *
 * The plugin bundles llama.cpp's `llama-server` and runs the GGUF model on a
 * loopback port. The Canopus "on this device" tier then points its existing
 * OpenAI provider (provider-local.js) at `http://127.0.0.1:<port>/v1` -- no new
 * agent, no new transport, the phone just becomes the inference host.
 *
 * Findings that shaped this (canopus-eval/MOBILE.md, verified on a Xiaomi Pad 7):
 *  - llama.cpp (not ORT GenAI) with a 4B Q4_0 model: ~3.3s per warm turn, CPU only.
 *  - `--no-mmap` is mandatory on Android (mmap'd weights get page-cache-reclaimed;
 *    343x swing). The model must load resident.
 *  - llama-server's prefix cache pays the ~2500-token tool catalog once per session
 *    (~60s cold), then reuses the KV cache (~19 tokens re-processed) for later turns.
 *  - The server must run in a foreground service or Doze/OEM power managers kill it.
 */

export interface StartOptions {
  /** Loopback port for llama-server. Default 8823. */
  port?: number;
  /** Generation threads. Default = ceil(cores/2), leaving room for the UI. */
  threads?: number;
  /** Context window. Default 8192 (the local-tier ceiling from the eval). */
  contextSize?: number;
}

export interface StartResult {
  /** OpenAI base to hand the Canopus provider, e.g. "http://127.0.0.1:8823/v1". */
  url: string;
  port: number;
}

export interface StatusResult {
  /** The model file is present on disk and the right size. */
  modelReady: boolean;
  /** llama-server is up and answering /health. */
  running: boolean;
  /** OpenAI base when running, else "". */
  url: string;
  /** Absolute model path, or "" if not downloaded. */
  modelPath: string;
  /** Bytes on disk for the model (0 if absent). */
  modelBytes: number;
}

export interface DownloadModelOptions {
  /** HTTPS URL of the GGUF (e.g. a release asset or the Polaris host). */
  url: string;
  /** Expected total bytes, used to detect a complete prior download and skip. */
  expectedBytes?: number;
  /** Optional sha256 (hex) to verify the finished file. */
  sha256?: string;
}

export interface DownloadModelResult {
  modelPath: string;
  bytes: number;
}

export interface ProgressEvent {
  receivedBytes: number;
  totalBytes: number;
  /** 0-100, -1 when total is unknown. */
  percent: number;
}

export interface PolarisLlamaPlugin {
  /** Stream the GGUF to app storage. Resumes/skips if already complete. Emits 'downloadProgress'. */
  downloadModel(options: DownloadModelOptions): Promise<DownloadModelResult>;
  /** Delete the downloaded model to reclaim space. */
  deleteModel(): Promise<void>;
  /** Start llama-server (in a foreground service) against the downloaded model. Resolves when /health is ready. */
  start(options?: StartOptions): Promise<StartResult>;
  /** Stop llama-server and the foreground service. */
  stop(): Promise<void>;
  /** Current model + server state. */
  status(): Promise<StatusResult>;

  addListener(
    eventName: 'downloadProgress',
    listenerFunc: (event: ProgressEvent) => void,
  ): Promise<{ remove: () => Promise<void> }>;
}
