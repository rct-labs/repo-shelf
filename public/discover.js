// Recommendations feed: per-project candidates from the desktop discovery
// service, triaged by mouse or keyboard (j/k move, a accept, s later,
// d dismiss). Candidate text is untrusted and always rendered as plain text.
import { t, getLanguage } from '/i18n.js';

const DISMISS_REASONS = ['not_relevant', 'too_heavy', 'already_have', 'low_quality'];
const RUN_POLL_MS = 1500;

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = String(text);
  return node;
}

function isTyping(target) {
  return target instanceof HTMLElement
    && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName));
}

/**
 * ctx: { api, toast(message, action?), openRepo(id), onLibraryChanged(), fmtTime(iso) }
 */
export function mount(root, ctx) {
  const state = {
    active: false,
    view: 'pending', // pending | later
    project: null, // project id filter, null = all
    projects: [],
    candidates: [],
    selected: 0,
    run: null,
    runTimer: null,
    menuFor: null, // candidate key with the dismiss menu open
    busy: false,
  };

  const key = (c) => `${c.projectId}/${c.githubId}`;
  const visible = () => state.candidates.filter((c) => state.project == null || c.projectId === state.project);

  // ---- data ---------------------------------------------------------------

  async function loadCandidates() {
    const [{ projects }, { candidates }] = await Promise.all([
      ctx.api('/api/projects'),
      ctx.api(`/api/discovery/candidates?state=${state.view}`),
    ]);
    state.projects = projects;
    state.candidates = candidates;
    if (state.project != null && !projects.some((p) => p.id === state.project)) state.project = null;
    state.selected = Math.min(state.selected, Math.max(0, visible().length - 1));
  }

  async function loadRun() {
    const { run } = await ctx.api('/api/discovery/runs/latest');
    state.run = run;
    clearTimeout(state.runTimer);
    if (run?.status === 'running' && state.active) {
      state.runTimer = setTimeout(pollRun, RUN_POLL_MS);
    }
  }

  async function pollRun() {
    const wasRunning = state.run?.status === 'running';
    try {
      await loadRun();
      if (wasRunning && state.run?.status !== 'running') await loadCandidates();
    } catch { /* keep the last known state */ }
    render();
  }

  async function refresh() {
    try {
      await Promise.all([loadCandidates(), loadRun()]);
      render();
    } catch (err) {
      renderError(err);
    }
  }

  // ---- actions ------------------------------------------------------------

  function explain(err) {
    if (err.code === 'ai_not_configured') return t('aiNotConfigured');
    if (err.code === 'no_active_projects') return t('noActiveProjects');
    if (err.code === 'run_in_progress') return t('runInProgress');
    return err.retryable ? `${err.message} ${t('retryableHint')}` : err.message;
  }

  async function runNow() {
    try {
      const { run } = await ctx.api('/api/discovery/runs', {
        method: 'POST',
        body: { lang: getLanguage() === 'zh-CN' ? 'zh' : 'en' },
      });
      state.run = run;
      ctx.toast(t('feedRunStarted'));
      render();
      clearTimeout(state.runTimer);
      state.runTimer = setTimeout(pollRun, RUN_POLL_MS);
    } catch (err) {
      ctx.toast(explain(err));
    }
  }

  async function cancelRun() {
    if (!state.run) return;
    await ctx.api(`/api/discovery/runs/${state.run.id}/cancel`, { method: 'POST' }).catch(() => {});
    pollRun();
  }

  async function decide(candidate, action, reason) {
    if (state.busy) return;
    state.busy = true;
    state.menuFor = null;
    const base = `/api/discovery/candidates/${candidate.projectId}/${candidate.githubId}`;
    try {
      if (action === 'accept') {
        const { repo } = await ctx.api(`${base}/accept`, { method: 'POST' });
        ctx.toast(t('toastAccepted'), { label: t('openIt'), onClick: () => ctx.openRepo(repo.id) });
        ctx.onLibraryChanged();
      } else if (action === 'later') {
        await ctx.api(`${base}/later`, { method: 'POST' });
        ctx.toast(t('toastLater'));
      } else {
        await ctx.api(`${base}/dismiss`, { method: 'POST', body: reason ? { reason } : {} });
        ctx.toast(t('toastDismissed'));
      }
      await loadCandidates();
    } catch (err) {
      ctx.toast(explain(err));
    } finally {
      state.busy = false;
      render();
    }
  }

  function openDismissMenu(candidate) {
    state.menuFor = key(candidate);
    render();
    root.querySelector('.dismiss-menu button')?.focus();
  }

  function closeDismissMenu() {
    state.menuFor = null;
    render();
  }

  // ---- rendering ----------------------------------------------------------

  function renderRunLine() {
    const line = el('div', 'feed-run');
    const text = el('span', 'feed-run-text');
    const run = state.run;
    if (!run) {
      text.textContent = t('feedNeverRun');
    } else if (run.status === 'running') {
      const p = run.progress || {};
      text.textContent = t('feedRunning', {
        done: p.projectsDone ?? 0, total: p.projectsTotal ?? 0, scored: p.scored ?? 0, inserted: p.inserted ?? 0,
      });
    } else {
      text.textContent = t('feedLastRun', {
        time: ctx.fmtTime(run.finishedAt || run.startedAt),
        status: t(`runStatus_${run.status}`),
      });
      if (run.progress?.rateLimitResetAt) {
        text.textContent += ` · ${t('feedRateLimited', { time: ctx.fmtTime(run.progress.rateLimitResetAt) })}`;
      } else if (run.status === 'failed' && run.error) {
        text.textContent += ` · ${run.error}`;
      }
    }
    line.append(text);
    const btn = el('button', 'btn btn-sm');
    btn.type = 'button';
    if (run?.status === 'running') {
      btn.textContent = t('feedRunCancel');
      btn.addEventListener('click', cancelRun);
    } else {
      btn.textContent = t('feedRunNow');
      btn.id = 'btn-run-now';
      btn.addEventListener('click', runNow);
    }
    line.append(btn);
    return line;
  }

  function renderFilters() {
    const bar = el('div', 'feed-filters');
    const count = (p) => (state.view === 'later' ? p.counts.later : p.counts.pending);
    const chip = (label, id, n) => {
      const b = el('button', 'feed-chip' + (state.project === id ? ' active' : ''));
      b.type = 'button';
      b.append(document.createTextNode(label + ' '), el('span', 'feed-chip-count', n));
      b.addEventListener('click', () => { state.project = id; state.selected = 0; render(); });
      return b;
    };
    bar.append(chip(t('feedAll'), null, state.projects.reduce((sum, p) => sum + count(p), 0)));
    for (const p of state.projects) bar.append(chip(p.name, p.id, count(p)));
    const spacer = el('span', 'feed-spacer');
    const toggle = el('button', 'btn btn-link btn-sm', state.view === 'later' ? t('feedPendingToggle') : t('feedLaterToggle'));
    toggle.type = 'button';
    toggle.id = 'btn-feed-view';
    toggle.addEventListener('click', () => {
      state.view = state.view === 'later' ? 'pending' : 'later';
      state.selected = 0;
      refresh();
    });
    bar.append(spacer, toggle);
    return bar;
  }

  function renderCard(c, index) {
    const card = el('article', 'feed-card' + (index === state.selected ? ' selected' : ''));
    card.dataset.key = key(c);
    card.addEventListener('click', () => {
      if (state.selected !== index) { state.selected = index; render(); }
    });

    const head = el('div', 'feed-card-head');
    const link = el('a', 'feed-card-name', c.fullName);
    link.href = c.htmlUrl;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    head.append(link);
    if (c.score != null) head.append(el('span', 'badge', t('score', { n: c.score })));
    card.append(head);

    if (c.description) card.append(el('div', 'feed-card-desc', c.description));

    const meta = el('div', 'result-meta');
    const bits = [c.language, c.stars != null ? `★ ${c.stars}` : null,
      c.starsPerMonth != null ? t('growthPerMonth', { n: Math.round(c.starsPerMonth) }) : null];
    meta.textContent = bits.filter(Boolean).join(' · ');
    card.append(meta);

    const why = el('div', 'feed-card-why');
    if (c.matchedNeed) {
      const need = el('div', 'feed-need');
      need.append(el('span', 'feed-label', `${t('matchedNeed')}: `), document.createTextNode(c.matchedNeed));
      why.append(need);
    }
    const reason = el('div', 'feed-reason');
    const ai = el('span', 'badge ai', 'AI');
    reason.append(ai, document.createTextNode(' ' + (c.reason || '')));
    why.append(reason);
    if (c.cost) why.append(el('span', `badge cost-${c.cost}`, t(`cost_${c.cost}`)));
    card.append(why);

    const actions = el('div', 'feed-actions');
    const action = (label, hotkey, cls, fn) => {
      const b = el('button', `btn btn-sm ${cls}`.trim());
      b.type = 'button';
      b.append(document.createTextNode(label + ' '), el('kbd', null, hotkey));
      b.addEventListener('click', (e) => { e.stopPropagation(); state.selected = index; fn(); });
      return b;
    };
    actions.append(action(t('accept'), 'a', 'btn-primary feed-accept', () => decide(c, 'accept')));
    if (c.state !== 'later') actions.append(action(t('later'), 's', 'feed-later', () => decide(c, 'later')));
    actions.append(action(t('dismiss'), 'd', 'feed-dismiss', () => openDismissMenu(c)));
    card.append(actions);

    if (state.menuFor === key(c)) {
      const menu = el('div', 'dismiss-menu');
      menu.append(el('div', 'hint', t('dismissReasonTitle')));
      DISMISS_REASONS.forEach((r, i) => {
        const b = el('button', 'btn btn-sm');
        b.type = 'button';
        b.dataset.reason = r;
        b.append(el('kbd', null, i + 1), document.createTextNode(' ' + t(`dismiss_${r}`)));
        b.addEventListener('click', (e) => { e.stopPropagation(); decide(c, 'dismiss', r); });
        menu.append(b);
      });
      card.append(menu);
    }
    return card;
  }

  function renderList() {
    const list = el('div', 'feed-list');
    const items = sortedVisible();
    if (state.projects.length === 0) {
      list.append(el('div', 'state-block', t('feedNoProjects')));
      return list;
    }
    if (items.length === 0) {
      list.append(el('div', 'state-block', state.view === 'later' ? t('feedEmptyLater') : t('feedEmpty')));
      return list;
    }
    // Grouped by project; the selection index runs over the flat sorted list.
    let group = null;
    items.forEach((c, i) => {
      if (group !== c.projectId) {
        group = c.projectId;
        const n = items.filter((x) => x.projectId === group).length;
        const h = el('h3', 'feed-group');
        h.append(document.createTextNode(c.projectName || ''), el('span', 'feed-chip-count', n));
        list.append(h);
      }
      list.append(renderCard(c, i));
    });
    return list;
  }

  function render() {
    root.innerHTML = '';
    root.append(renderRunLine(), renderFilters(), renderList(), el('div', 'feed-keys hint', t('feedKeysHint')));
    root.querySelector('.feed-card.selected')?.scrollIntoView({ block: 'nearest' });
  }

  function renderError(err) {
    root.innerHTML = '';
    root.append(el('div', 'state-block', explain(err)));
  }

  // Grouped by project (project order), best score first inside a group.
  function sortedVisible() {
    const order = state.projects.map((p) => p.id);
    return visible().sort((a, b) => order.indexOf(a.projectId) - order.indexOf(b.projectId) || (b.score ?? 0) - (a.score ?? 0));
  }

  // ---- keyboard -----------------------------------------------------------

  function onKey(e) {
    // offsetParent is null while the feed is hidden (e.g. the window is in bar mode).
    if (!state.active || !root.offsetParent || e.ctrlKey || e.altKey || e.metaKey) return;
    if (document.querySelector('dialog[open]')) return;
    const items = sortedVisible();
    const current = items[state.selected];
    if (state.menuFor && isTyping(e.target) && e.key !== 'Escape') {
      // Focus moved to an input (e.g. the omnibox): typing must not dismiss anything.
      closeDismissMenu();
      return;
    }
    if (state.menuFor) {
      const menuCandidate = items.find((c) => key(c) === state.menuFor);
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeDismissMenu(); return; }
      if (!menuCandidate) return;
      const n = Number(e.key);
      if (n >= 1 && n <= DISMISS_REASONS.length) { e.preventDefault(); decide(menuCandidate, 'dismiss', DISMISS_REASONS[n - 1]); return; }
      if (e.key === 'Enter') {
        e.preventDefault();
        decide(menuCandidate, 'dismiss', e.target?.dataset?.reason || 'not_relevant');
      }
      return;
    }
    if (isTyping(e.target)) return;
    if (e.key === 'j' || e.key === 'k') {
      e.preventDefault();
      if (!items.length) return;
      state.selected = Math.max(0, Math.min(items.length - 1, state.selected + (e.key === 'j' ? 1 : -1)));
      render();
      return;
    }
    if (!current) return;
    if (e.key === 'a') { e.preventDefault(); decide(current, 'accept'); }
    else if (e.key === 's' && current.state !== 'later') { e.preventDefault(); decide(current, 'later'); }
    else if (e.key === 'd') { e.preventDefault(); openDismissMenu(current); }
  }
  // Capture phase so Esc closes the dismiss menu before the app-level Esc handler.
  document.addEventListener('keydown', onKey, true);

  return {
    show() {
      state.active = true;
      refresh();
    },
    hide() {
      state.active = false;
      state.menuFor = null;
      clearTimeout(state.runTimer);
    },
    rerender() {
      if (state.active) render();
    },
    refresh,
  };
}
