import { WebPlugin } from '@capacitor/core';

import type {
  PolarisOnnxPlugin,
  CreateSessionOptions,
  CreateSessionResult,
  RunOptions,
  RunResult,
} from './definitions';

/**
 * Web fallback: there is no native runtime in a plain browser, so this
 * throws "unavailable". On the web the Polaris UI keeps using ONNX
 * Runtime Web (the shim's guard `if (!PolarisOnnx) return` means the
 * shim never installs, so the page's own `ort` stays in charge).
 */
export class PolarisOnnxWeb extends WebPlugin implements PolarisOnnxPlugin {
  private notNative(): never {
    throw this.unimplemented('PolarisOnnx native runtime is not available on web.');
  }
  async createSession(_options: CreateSessionOptions): Promise<CreateSessionResult> {
    return this.notNative();
  }
  async run(_options: RunOptions): Promise<RunResult> {
    return this.notNative();
  }
  async releaseSession(_options: { handle: string }): Promise<void> {
    return this.notNative();
  }
  async info(): Promise<{ version: string; providers: string[] }> {
    return { version: 'web-unavailable', providers: [] };
  }
}
