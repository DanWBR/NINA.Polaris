import { registerPlugin } from '@capacitor/core';

import type { PolarisLlamaPlugin } from './definitions';

/**
 * On iOS (and the web fallback) this resolves to the stub in web.ts until the
 * in-process llama.cpp embed lands; on Android it binds to the native plugin.
 */
const PolarisLlama = registerPlugin<PolarisLlamaPlugin>('PolarisLlama', {
  web: () => import('./web').then((m) => new m.PolarisLlamaWeb()),
});

export * from './definitions';
export { PolarisLlama };
