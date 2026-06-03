/**
 * Native ONNX Runtime bridge. Binary tensors cross the bridge as
 * base64 strings (the JS shim packs/unpacks them). Element types use
 * ORT's string names: 'float32' | 'float16' | 'int32' | 'int64' |
 * 'uint8' | 'bool'.
 */
export interface PackedTensor {
  /** base64 of the raw little-endian tensor bytes */
  data: string;
  /** ORT element type name */
  type: string;
  /** tensor shape */
  dims: number[];
}

export interface CreateSessionOptions {
  /** base64 of the full .onnx model bytes */
  model: string;
  /** EP hints from the page (e.g. ['webgpu','wasm']); native maps to CoreML/NNAPI/XNNPACK/CPU */
  executionProviders?: string[];
}

export interface CreateSessionResult {
  /** opaque session handle for run()/releaseSession() */
  handle: string;
  inputNames: string[];
  outputNames: string[];
  /** which execution provider actually got selected (diagnostics) */
  provider?: string;
}

export interface RunOptions {
  handle: string;
  feeds: Record<string, PackedTensor>;
}

export interface RunResult {
  outputs: Record<string, PackedTensor>;
  /** inference wall time in ms (diagnostics) */
  ms?: number;
}

export interface PolarisOnnxPlugin {
  /** Build a session from model bytes; returns a handle + io names. */
  createSession(options: CreateSessionOptions): Promise<CreateSessionResult>;
  /** Run one inference. */
  run(options: RunOptions): Promise<RunResult>;
  /** Free a session. */
  releaseSession(options: { handle: string }): Promise<void>;
  /** Health/diagnostics: returns runtime version + available providers. */
  info(): Promise<{ version: string; providers: string[] }>;
}
