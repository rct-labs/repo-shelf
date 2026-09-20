import os from 'node:os';
import path from 'node:path';

export const APP_NAME = 'repo-shelf';
export const APP_VERSION = '0.1.0';
export const DEFAULT_PORT = 4790;

export function defaultDataDir(platform = process.platform, home = os.homedir(), env = process.env) {
  if (platform === 'win32') {
    return path.join(env.APPDATA || path.join(home, 'AppData', 'Roaming'), APP_NAME);
  }
  if (platform === 'darwin') {
    return path.join(home, 'Library', 'Application Support', APP_NAME);
  }
  return path.join(env.XDG_DATA_HOME || path.join(home, '.local', 'share'), APP_NAME);
}

export function loadConfig(env = process.env) {
  const port = Number.parseInt(env.REPO_SHELF_PORT || '', 10);
  const maxRateWait = Number.parseInt(env.REPO_SHELF_MAX_RATE_WAIT_MS || '', 10);
  return {
    host: '127.0.0.1',
    port: Number.isInteger(port) && port > 0 && port < 65536 ? port : DEFAULT_PORT,
    dataDir: env.REPO_SHELF_DATA_DIR
      ? path.resolve(env.REPO_SHELF_DATA_DIR)
      : defaultDataDir(process.platform, os.homedir(), env),
    // Overridable so tests can point the client at a fixture server.
    githubApiBase: (env.REPO_SHELF_GITHUB_API || 'https://api.github.com').replace(/\/+$/, ''),
    // Test/dev convenience: fixes the pairing token for a run without persisting it.
    pairingTokenOverride: env.REPO_SHELF_TOKEN || null,
    maxRateLimitWaitMs: Number.isInteger(maxRateWait) && maxRateWait >= 0 ? maxRateWait : 90_000,
  };
}
