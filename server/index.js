// Repo Shelf local service entry point.

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { loadConfig, APP_VERSION } from './config.js';
import { openDatabase, getSetting, setSetting } from './db.js';
import { createGitHubClient } from './github.js';
import { createJobManager } from './jobs.js';
import { createApp } from './http.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.join(__dirname, '..');

const config = loadConfig();
fs.mkdirSync(config.dataDir, { recursive: true });

const db = openDatabase(path.join(config.dataDir, 'repo-shelf.db'));

let pairingToken = config.pairingTokenOverride || getSetting(db, 'pairing_token');
if (!pairingToken) {
  pairingToken = crypto.randomBytes(24).toString('hex');
  setSetting(db, 'pairing_token', pairingToken);
}

const gh = createGitHubClient({
  getToken: () => getSetting(db, 'github_token'),
  baseUrl: config.githubApiBase,
});
const jobs = createJobManager({ db, gh, maxRateLimitWaitMs: config.maxRateLimitWaitMs });

const server = createApp({
  db,
  gh,
  jobs,
  config,
  pairingToken,
  publicDir: path.join(rootDir, 'public'),
  vendorFiles: {
    '/vendor/marked.esm.js': path.join(rootDir, 'node_modules', 'marked', 'lib', 'marked.esm.js'),
    '/vendor/purify.es.mjs': path.join(rootDir, 'node_modules', 'dompurify', 'dist', 'purify.es.mjs'),
  },
});

server.on('error', (err) => {
  if (err.code === 'EADDRINUSE') {
    console.error(`Port ${config.port} is already in use. Set REPO_SHELF_PORT to choose another port.`);
    process.exit(1);
  }
  throw err;
});

server.listen(config.port, config.host, () => {
  console.log(`Repo Shelf ${APP_VERSION}`);
  console.log(`Web UI:  http://${config.host}:${config.port}`);
  console.log(`Data:    ${config.dataDir}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 1500).unref();
  });
}
