// Repo Shelf local service entry point (CLI / pnpm start).
// The desktop shell spawns this same file with ELECTRON_RUN_AS_NODE=1.

import { APP_VERSION } from './config.js';
import { createService } from './service.js';

const service = createService();

try {
  await service.listen();
} catch (err) {
  if (err.code === 'EADDRINUSE') {
    console.error(`Port ${service.config.port} is already in use. Set REPO_SHELF_PORT to choose another port.`);
    process.exit(1);
  }
  throw err;
}

console.log(`Repo Shelf ${APP_VERSION}`);
console.log(`Web UI:  http://${service.config.host}:${service.config.port}`);
console.log(`Data:    ${service.config.dataDir}`);

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    service.close().finally(() => process.exit(0));
    setTimeout(() => process.exit(0), 1500).unref();
  });
}
