import { registerPlugin } from '@capacitor/core';

import type { PolarisOnnxPlugin } from './definitions';

const PolarisOnnx = registerPlugin<PolarisOnnxPlugin>('PolarisOnnx', {
  web: () => import('./web').then((m) => new m.PolarisOnnxWeb()),
});

export * from './definitions';
export { PolarisOnnx };
