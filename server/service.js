// Shared service bootstrap used by both the CLI entry (index.js) and the
// Electron desktop shell (desktop/main.js).

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { loadConfig } from './config.js';
import { openDatabase, getSetting, setSetting } from './db.js';
import { createGitHubClient } from './github.js';
import { createJobManager } from './jobs.js';
import { createApp } from './http.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.join(__dirname, '..');

export function createService(env = process.env) {
  const config = loadConfig(env);
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

  return {
    config,
    db,
    pairingToken,
    /** @returns {Promise<void>} rejects with EADDRINUSE etc. */
    listen() {
      return new Promise((resolve, reject) => {
        const onError = (err) => reject(err);
        server.once('error', onError);
        server.listen(config.port, config.host, () => {
          server.removeListener('error', onError);
          resolve();
        });
      });
    },
    close() {
      return new Promise((resolve) => server.close(resolve));
    },
  };
}
