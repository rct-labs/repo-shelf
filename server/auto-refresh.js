import { getSetting, setSetting } from './db.js';
import { refreshRepo } from './repos.js';

export function createAutoRefresh({ db, gh, busy = () => false }) {
  let running = null;
  let timer = null;
  let stopped = false;
  async function run(now = Date.now()) {
    if (busy() || Date.parse(getSetting(db, 'auto_refresh_next_at')) > now) return 0;
    const ids = db.prepare(`SELECT id FROM repos
      WHERE fetched_at IS NULL OR julianday(fetched_at) IS NULL OR julianday(fetched_at) <= julianday(?)
      ORDER BY fetched_at, id LIMIT 20`).all(new Date(now - 86400000).toISOString());
    setSetting(db, 'auto_refresh_next_at', new Date(now + 3600000).toISOString());
    let updated = 0;
    for (const { id } of ids) {
      if (stopped || busy()) break;
      try { await refreshRepo(db, gh, id); updated++; }
      catch (err) {
        setSetting(db, 'auto_refresh_error', err.message);
        if (['github_rate_limited', 'github_unauthorized', 'github_forbidden', 'upstream_unavailable'].includes(err.code)) {
          setSetting(db, 'auto_refresh_next_at', new Date(now + Math.max(3600000, err.retryAfterMs || 0)).toISOString());
          break;
        }
      }
    }
    return updated;
  }
  function runOnce(now) {
    if (running || stopped) return Promise.resolve(0);
    running = run(now).finally(() => { running = null; });
    return running;
  }
  async function tick() {
    try { await runOnce(); }
    catch (err) { setSetting(db, 'auto_refresh_error', err.message); }
    if (!stopped) timer = setTimeout(tick, 300000).unref();
  }
  return {
    runOnce,
    start() { if (!timer) timer = setTimeout(tick, 30000).unref(); },
    async stop() { stopped = true; clearTimeout(timer); await running; },
  };
}
