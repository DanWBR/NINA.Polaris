import { WebPlugin } from '@capacitor/core';

import type {
  PolarisLlamaPlugin,
  StartOptions,
  StartResult,
  StatusResult,
  DownloadModelOptions,
  DownloadModelResult,
} from './definitions';

/**
 * Web / unsupported-platform stub. The on-device backend needs a native host
 * (Android now, iOS via an in-process embed later). In a plain browser the user
 * should use the desktop "on this device" tier (a local Ollama) instead.
 */
export class PolarisLlamaWeb extends WebPlugin implements PolarisLlamaPlugin {
  private unsupported(): never {
    throw this.unavailable('The on-device model backend is only available in the Polaris mobile app.');
  }
  async downloadModel(_o: DownloadModelOptions): Promise<DownloadModelResult> {
    return this.unsupported();
  }
  async deleteModel(): Promise<void> {
    return this.unsupported();
  }
  async start(_o?: StartOptions): Promise<StartResult> {
    return this.unsupported();
  }
  async stop(): Promise<void> {
    return this.unsupported();
  }
  async status(): Promise<StatusResult> {
    return {
      modelReady: false, running: false, url: '', modelPath: '', modelBytes: 0,
      totalMemBytes: 0, availMemBytes: 0, lowMemory: false, batteryExempt: true,
    };
  }
  async requestBatteryExemption(): Promise<{ exempt: boolean }> {
    return { exempt: true };
  }
}
