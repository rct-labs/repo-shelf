import { marked } from '/vendor/marked.esm.js';
import DOMPurify from '/vendor/purify.es.mjs';
import { t, getLanguage, setLanguage, detectLanguage, LANGUAGES } from '/i18n.js';

const TOKEN = document.querySelector('meta[name="repo-shelf-token"]')?.content || '';

// Theme: dark (default) / light, persisted in localStorage, mirrored to the host shell.
const THEME_KEY = 'repo-shelf-theme';

// Function declaration: hoisted, safe to call during module init.
function hostNotify(msg) {
  try { window.chrome?.webview?.postMessage(msg); } catch { /* not hosted in WebView2 */ }
}

function applyTheme(theme) {
  const t = theme === 'light' ? 'light' : 'dark';
  document.documentElement.dataset.theme = t;
  localStorage.setItem(THEME_KEY, t);
  hostNotify({ type: 'theme', mode: t });
}
applyTheme(localStorage.getItem(THEME_KEY) || 'dark');

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A') {
    node.setAttribute('target', '_blank');
    node.setAttribute('rel', 'noopener noreferrer');
  }
});

// ---------------------------------------------------------------------------
// API helper
// ---------------------------------------------------------------------------

class ApiError extends Error {
  constructor(status, body) {
    super(body?.error?.message || `HTTP ${status}`);
    this.code = body?.error?.code || 'unknown';
    this.status = status;
    this.retryable = Boolean(body?.error?.retryable);
  }
}

async function api(path, { method = 'GET', body } = {}) {
  let res;
  try {
    res = await fetch(path, {
      method,
      headers: {
        'x-reposhelf-token': TOKEN,
        ...(body !== undefined ? { 'content-type': 'application/json' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new ApiError(0, { error: { code: 'service_offline', message: t('loadError') } });
  }
  const json = await res.json().catch(() => null);
  if (!res.ok || !json?.ok) throw new ApiError(res.status, json);
  return json.data;
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

const state = {
  q: '',
  filters: { status: '', tag: '', language: '', project: '', archived: '' },
  items: [],
  total: 0,
  offset: 0,
  pageSize: 50,
  selectedId: null,
  selectedRepo: null,
  activeJobId: null,
  jobTimer: null,
  libraryEmpty: false,
  detected: null, // { valid, fullName, found, repo }
};

const $ = (sel) => document.querySelector(sel);

// ---------------------------------------------------------------------------
// i18n
// ---------------------------------------------------------------------------

const STATUS_KEYS = ['inbox', 'to_investigate', 'tried', 'adopted', 'dismissed'];

function applyStaticI18n() {
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    el.textContent = t(el.dataset.i18n);
  });
  $('#omnibox').placeholder = t('omniboxPlaceholder');
  $('#settings-gh-token').placeholder = t('settingsTokenPlaceholder');
  $('#settings-ds-key').placeholder = t('settingsTokenPlaceholderShort');
  $('#btn-filter').title = t('filters');
}

function fillLanguageSwitch() {
  const select = $('#lang-switch');
  select.innerHTML = '';
  for (const { id, label } of LANGUAGES) {
    select.append(new Option(label, id));
  }
  select.value = getLanguage();
}

function fillStaticSelects() {
  const archived = $('#filter-archived');
  const currentA = archived.value;
  archived.innerHTML = '';
  archived.append(new Option(`${t('filterArchived')}: ${t('archivedAny')}`, ''));
  archived.append(new Option(t('archivedYes'), 'true'));
  archived.append(new Option(t('archivedNo'), 'false'));
  archived.value = currentA;

  const mode = $('#restore-mode');
  const currentMode = mode.value;
  mode.innerHTML = '';
  mode.append(new Option(t('restoreMerge'), 'merge'));
  mode.append(new Option(t('restoreOverwrite'), 'overwrite'));
  mode.value = currentMode || 'merge';

  const status = $('#filter-status');
  const current = status.value;
  status.innerHTML = '';
  status.append(new Option(`${t('filterStatus')}: ${t('archivedAny')}`, ''));
  for (const s of STATUS_KEYS) status.append(new Option(t(`status_${s}`), s));
  status.value = current;

  $('#btn-clear-filters').textContent = t('clearFilters');
}

function applyI18n() {
  applyStaticI18n();
  fillLanguageSwitch();
  fillStaticSelects();
}

// ---------------------------------------------------------------------------
// Toast + confirm
// ---------------------------------------------------------------------------

let toastTimer = null;
function toast(message) {
  const el = $('#toast');
  el.textContent = message;
  el.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add('hidden'), 2600);
}

function confirmDialog({ title, message }) {
  return new Promise((resolve) => {
    const dialog = $('#modal-confirm');
    $('#confirm-title').textContent = title;
    $('#confirm-message').textContent = message;
    const btn = $('#btn-confirm');
    const cleanup = () => {
      btn.removeEventListener('click', onClick);
      dialog.removeEventListener('close', onClose);
    };
    const onClick = () => { cleanup(); resolve(true); };
    const onClose = () => { cleanup(); resolve(false); };
    btn.addEventListener('click', () => { dialog.close(); onClick(); });
    dialog.addEventListener('close', onClose, { once: true });
    dialog.showModal();
  });
}

// ---------------------------------------------------------------------------
// Modes (bar <-> compact <-> expanded)
// ---------------------------------------------------------------------------

function setMode(mode) {
  const app = $('#app');
  if (app.classList.contains(`mode-${mode}`)) return;
  app.classList.remove('mode-bar', 'mode-compact', 'mode-expanded');
  app.classList.add(`mode-${mode}`);
  $('#detail-pane').classList.toggle('hidden', mode !== 'expanded');
  hostNotify({ type: 'resize', mode });
  if (mode !== 'expanded') {
    state.selectedId = null;
    state.selectedRepo = null;
    document.querySelectorAll('.result-item.active').forEach((el) => el.classList.remove('active'));
  }
}

function expandApp() { setMode('expanded'); }
function collapseApp() { setMode('compact'); }

// ---------------------------------------------------------------------------
// Search + results
// ---------------------------------------------------------------------------

function statusDot(status) {
  const dot = document.createElement('span');
  dot.className = `dot dot-${status}`;
  dot.title = t(`status_${status}`);
  return dot;
}

function renderResults({ append = false } = {}) {
  const list = $('#results-list');
  if (!append) list.innerHTML = '';
  const frag = document.createDocumentFragment();
  const slice = append ? state.items.slice(-state.pageSize) : state.items;
  for (const item of slice) {
    const { repo, snippet } = item;
    const li = document.createElement('li');
    li.className = 'result-item' + (repo.id === state.selectedId ? ' active' : '');
    li.dataset.id = repo.id;

    const name = document.createElement('div');
    name.className = 'result-name';
    name.append(statusDot(repo.annotation.status));
    const owner = document.createElement('span');
    owner.className = 'owner';
    owner.textContent = `${repo.owner}/`;
    name.append(owner, document.createTextNode(repo.name));
    li.append(name);

    const meta = document.createElement('div');
    meta.className = 'result-meta';
    const bits = [repo.language, repo.stars != null ? `★ ${repo.stars}` : null].filter(Boolean);
    if (repo.archived) bits.push(t('archivedYes'));
    if (repo.stale) bits.push(t('stale'));
    meta.textContent = bits.join(' · ');
    if (meta.textContent) li.append(meta);

    if (snippet) {
      const sn = document.createElement('div');
      sn.className = 'result-snippet';
      sn.innerHTML = snippet; // server-escaped, contains only <mark> markup
      li.append(sn);
    }
    li.addEventListener('click', () => selectRepo(repo.id));
    frag.append(li);
  }
  list.append(frag);

  $('#results-count').textContent = t('resultsCount', { count: state.total });
  $('#results-more').classList.toggle('hidden', state.items.length >= state.total);

  const stateBlock = $('#results-state');
  if (state.total === 0) {
    stateBlock.textContent = state.libraryEmpty && !state.q && !hasActiveFilters()
      ? t('emptyLibrary')
      : t('emptyResults');
    stateBlock.classList.remove('hidden');
  } else {
    stateBlock.classList.add('hidden');
  }
}

function hasActiveFilters() {
  return Object.values(state.filters).some(Boolean);
}

async function runSearch({ append = false } = {}) {
  if (!append) {
    state.offset = 0;
    state.items = [];
  }
  const params = new URLSearchParams();
  if (state.q) params.set('q', state.q);
  for (const [k, v] of Object.entries(state.filters)) if (v) params.set(k, v);
  params.set('limit', String(state.pageSize));
  params.set('offset', String(state.offset));
  try {
    const data = await api(`/api/repos?${params}`);
    state.total = data.total;
    state.items = append ? state.items.concat(data.items) : data.items;
    renderResults({ append });
  } catch (err) {
    const stateBlock = $('#results-state');
    stateBlock.textContent = err.retryable ? `${err.message} ${t('retryableHint')}` : err.message;
    stateBlock.classList.remove('hidden');
  }
}

let searchDebounce = null;
let lookupSeq = 0;

function onOmniboxInput() {
  clearTimeout(searchDebounce);
  searchDebounce = setTimeout(async () => {
    const text = $('#omnibox').value.trim();
    // Any typing or focus leaves bar mode and shows the list.
    const app = $('#app');
    if (app.classList.contains('mode-bar')) setMode('compact');
    const looksLikeUrl = /github\.com|^[a-z0-9-]+\/[a-z0-9._-]+$/i.test(text);
    if (looksLikeUrl) {
      const seq = ++lookupSeq;
      try {
        const data = await api(`/api/repos/lookup?url=${encodeURIComponent(text)}`);
        if (seq !== lookupSeq) return; // stale
        if (data.valid) {
          state.detected = data;
          state.q = '';
          renderSaveBanner();
          renderResults();
          return;
        }
      } catch { /* fall through to search */ }
    }
    state.detected = null;
    renderSaveBanner();
    state.q = text;
    runSearch();
  }, 200);
}

function renderSaveBanner() {
  const banner = $('#save-banner');
  const d = state.detected;
  if (!d || !d.valid) {
    banner.classList.add('hidden');
    return;
  }
  banner.classList.remove('hidden');
  const text = $('#save-banner-text');
  const btn = $('#btn-banner-action');
  text.innerHTML = '';
  text.append(t('repoDetected') + ' ');
  const code = document.createElement('span');
  code.className = 'mono';
  code.textContent = d.fullName;
  text.append(code);
  if (d.found) {
    const badge = document.createElement('span');
    badge.className = 'badge ok';
    badge.style.marginLeft = '6px';
    badge.textContent = t('alreadySaved');
    text.append(badge);
    btn.textContent = t('openIt');
    btn.onclick = () => {
      $('#omnibox').value = '';
      state.detected = null;
      renderSaveBanner();
      state.q = '';
      runSearch();
      selectRepo(d.repo.id);
    };
  } else {
    btn.textContent = t('saveIt');
    btn.onclick = saveDetected;
  }
}

async function saveDetected() {
  const d = state.detected;
  if (!d?.valid) return;
  const btn = $('#btn-banner-action');
  btn.disabled = true;
  btn.textContent = t('savingIn');
  try {
    const result = await api('/api/repos', { method: 'POST', body: { url: `https://github.com/${d.fullName}` } });
    const repoId = result.repo.id;
    toast(result.outcome === 'created' ? t('addCreated') : t('addAlready'));
    $('#omnibox').value = '';
    state.detected = null;
    renderSaveBanner();
    await loadFilters();
    await runSearch();
    selectRepo(repoId);
  } catch (err) {
    toast(err.retryable ? `${err.message} ${t('retryableHint')}` : err.message);
  } finally {
    btn.disabled = false;
    btn.textContent = t('saveIt');
  }
}

// ---------------------------------------------------------------------------
// Filter chips + popover
// ---------------------------------------------------------------------------

const FILTER_SELECTS = { status: '#filter-status', tag: '#filter-tag', language: '#filter-language', project: '#filter-project', archived: '#filter-archived' };

function renderChips() {
  const box = $('#chips');
  box.innerHTML = '';
  const active = Object.entries(state.filters).filter(([, v]) => v);
  box.classList.toggle('hidden', active.length === 0);
  $('#btn-filter').classList.toggle('active', active.length > 0);
  for (const [key, value] of active) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    const label = key === 'status' ? t(`status_${value}`)
      : key === 'archived' ? (value === 'true' ? t('archivedYes') : t('archivedNo'))
        : value;
    chip.append(label + ' ');
    const x = document.createElement('button');
    x.type = 'button';
    x.textContent = '×';
    x.addEventListener('click', () => {
      state.filters[key] = '';
      $(FILTER_SELECTS[key]).value = '';
      renderChips();
      runSearch();
    });
    chip.append(x);
    box.append(chip);
  }
}

function bindFilters() {
  $('#btn-filter').addEventListener('click', (e) => {
    e.stopPropagation();
    $('#filter-popover').classList.toggle('hidden');
  });
  document.addEventListener('click', (e) => {
    if (!$('#filter-popover').classList.contains('hidden')
      && !e.target.closest('#filter-popover') && !e.target.closest('#btn-filter')) {
      $('#filter-popover').classList.add('hidden');
    }
  });
  for (const [key, sel] of Object.entries(FILTER_SELECTS)) {
    $(sel).addEventListener('change', (e) => {
      state.filters[key] = e.target.value;
      renderChips();
      runSearch();
    });
  }
  $('#btn-clear-filters').addEventListener('click', () => {
    state.filters = { status: '', tag: '', language: '', project: '', archived: '' };
    for (const sel of Object.values(FILTER_SELECTS)) $(sel).value = '';
    renderChips();
    runSearch();
  });
}

async function loadFilters() {
  try {
    const data = await api('/api/filters');
    state.libraryEmpty = data.total === 0;
    const fill = (sel, values, labelKey) => {
      const select = $(sel);
      const current = select.value;
      select.innerHTML = '';
      select.append(new Option(`${t(labelKey)}: ${t('archivedAny')}`, ''));
      for (const v of values) select.append(new Option(v, v));
      select.value = current;
    };
    fill('#filter-tag', data.tags, 'filterTag');
    fill('#filter-language', data.languages, 'filterLanguage');
    fill('#filter-project', data.projects, 'filterProject');
    fillStaticSelects();
  } catch {
    state.libraryEmpty = false;
  }
}

// ---------------------------------------------------------------------------
// Detail panel
// ---------------------------------------------------------------------------

function fmtTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(getLanguage() === 'zh-CN' ? 'zh-CN' : 'en');
}

function refreshBadge(repo) {
  if (repo.refreshStatus === 'not_found') return ['refresh_not_found', 'warn'];
  if (repo.refreshStatus === 'inaccessible') return ['refresh_inaccessible', 'warn'];
  if (repo.refreshStatus === 'error') return ['refresh_error', 'warn'];
  if (repo.stale) return ['stale', 'warn'];
  return null;
}

function badgeEl(text, kind = '') {
  const span = document.createElement('span');
  span.className = `badge ${kind}`.trim();
  span.textContent = text;
  return span;
}

function renderMarkdownInto(container, markdown) {
  container.innerHTML = DOMPurify.sanitize(marked.parse(markdown || '', { async: false }));
}

function renderDetail(repo) {
  const pane = $('#detail-content');
  pane.innerHTML = '';

  const back = document.createElement('button');
  back.className = 'detail-back';
  back.type = 'button';
  back.textContent = t('backToList');
  back.addEventListener('click', collapseApp);
  pane.append(back);

  const head = document.createElement('div');
  head.className = 'detail-head';
  const title = document.createElement('div');
  title.className = 'detail-title';
  const link = document.createElement('a');
  link.href = repo.htmlUrl;
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
  link.textContent = repo.fullName;
  title.append(link);
  head.append(title);
  if (repo.description) {
    const desc = document.createElement('div');
    desc.className = 'detail-desc';
    desc.textContent = repo.description;
    head.append(desc);
  }
  const badges = document.createElement('div');
  badges.className = 'result-badges';
  if (repo.archived) badges.append(badgeEl(t('archivedYes'), 'warn'));
  if (repo.starredUpstream) badges.append(badgeEl(t('starred'), 'ok'));
  else if (repo.seenInImport) badges.append(badgeEl(t('unstarred'), 'warn'));
  const rb = refreshBadge(repo);
  if (rb) badges.append(badgeEl(t(rb[0]), rb[1]));
  head.append(badges);

  const meta = document.createElement('dl');
  meta.className = 'meta-grid';
  for (const [k, v] of [
    [t('stars'), repo.stars ?? '—'],
    [t('license'), repo.licenseId ?? '—'],
    [t('branch'), repo.defaultBranch ?? '—'],
    [t('pushedAt'), fmtTime(repo.pushedAt)],
    [t('savedAt'), fmtTime(repo.createdAt)],
    [t('fetchedAt'), fmtTime(repo.fetchedAt)],
  ]) {
    const wrap = document.createElement('div');
    const dt = document.createElement('dt');
    dt.textContent = k;
    const dd = document.createElement('dd');
    dd.textContent = String(v);
    wrap.append(dt, dd);
    meta.append(wrap);
  }
  head.append(meta);
  if (repo.topics.length) {
    const topics = document.createElement('div');
    topics.className = 'result-badges';
    for (const topic of repo.topics) topics.append(badgeEl(topic));
    head.append(topics);
  }
  pane.append(head);

  const actions = document.createElement('div');
  actions.className = 'detail-actions';
  const openBtn = document.createElement('a');
  openBtn.className = 'btn';
  openBtn.textContent = t('openOnGithub');
  openBtn.href = repo.htmlUrl;
  openBtn.target = '_blank';
  openBtn.rel = 'noopener noreferrer';
  const refreshBtn = document.createElement('button');
  refreshBtn.className = 'btn';
  refreshBtn.textContent = t('refresh');
  refreshBtn.addEventListener('click', () => refreshRepo(repo.id));
  const deleteBtn = document.createElement('button');
  deleteBtn.className = 'btn btn-danger';
  deleteBtn.textContent = t('delete');
  deleteBtn.addEventListener('click', () => removeRepo(repo.id));
  actions.append(openBtn, refreshBtn, deleteBtn);
  pane.append(actions);

  // Annotation form
  const form = document.createElement('div');
  form.className = 'annotation-form';
  form.innerHTML = `
    <label class="field"><span></span><input id="d-reason" type="text" maxlength="2000"></label>
    <label class="field"><span></span><textarea id="d-notes" maxlength="100000"></textarea></label>
    <label class="field"><span></span><input id="d-tags" type="text"><small class="hint"></small></label>
    <label class="field"><span></span><input id="d-projects" type="text"><small class="hint"></small></label>
    <label class="field"><span></span><select id="d-status" class="select"></select></label>
    <div class="modal-actions"><button id="d-save" class="btn btn-primary" type="button"></button></div>`;
  const [reasonLabel, notesLabel, tagsLabel, projectsLabel, statusLabel] = form.querySelectorAll('.field > span');
  reasonLabel.textContent = t('detailReason');
  notesLabel.textContent = t('detailNotes');
  tagsLabel.textContent = t('detailTags');
  projectsLabel.textContent = t('detailProjects');
  statusLabel.textContent = t('filterStatus');
  form.querySelectorAll('.hint').forEach((h) => { h.textContent = t('commaSeparated'); });
  form.querySelector('#d-reason').value = repo.annotation.reason;
  form.querySelector('#d-reason').placeholder = t('detailReasonPlaceholder');
  form.querySelector('#d-notes').value = repo.annotation.notes;
  form.querySelector('#d-notes').placeholder = t('detailNotesPlaceholder');
  form.querySelector('#d-tags').value = repo.annotation.tags.join(', ');
  form.querySelector('#d-projects').value = repo.annotation.projects.join(', ');
  const statusSelect = form.querySelector('#d-status');
  for (const s of STATUS_KEYS) statusSelect.append(new Option(t(`status_${s}`), s));
  statusSelect.value = repo.annotation.status;
  const saveBtn = form.querySelector('#d-save');
  saveBtn.textContent = t('saveAnnotation');
  saveBtn.addEventListener('click', () => saveAnnotation(repo.id));
  pane.append(form);

  // AI summary (never overwrites the user's own fields; stored separately)
  const aiBox = document.createElement('div');
  aiBox.className = 'ai-summary';
  const aiHead = document.createElement('h3');
  aiHead.append(badgeEl('AI', 'ai'), document.createTextNode(' ' + t('aiSummary')));
  const aiBtn = document.createElement('button');
  aiBtn.className = 'btn btn-link';
  aiBtn.type = 'button';
  const existing = repo.generated?.summary;
  aiBtn.textContent = existing ? t('aiRegenerate') : t('aiGenerate');
  aiBtn.addEventListener('click', () => generateSummary(repo.id, aiBtn));
  aiHead.append(aiBtn);
  aiBox.append(aiHead);
  const aiBody = document.createElement('div');
  aiBody.className = 'ai-body';
  if (existing) {
    renderMarkdownInto(aiBody, existing.content);
    const meta2 = document.createElement('p');
    meta2.className = 'hint';
    meta2.textContent = `${existing.model} · ${fmtTime(existing.createdAt)} · ${t('aiDisclaimer')}`;
    aiBox.append(aiBody, meta2);
  } else {
    const hint = document.createElement('p');
    hint.className = 'hint';
    hint.textContent = t('aiDisclaimer');
    aiBox.append(hint);
  }
  pane.append(aiBox);

  // README
  const readmeSection = document.createElement('div');
  readmeSection.className = 'readme-render';
  const readmeTitle = document.createElement('h2');
  readmeTitle.textContent = t('readme');
  readmeSection.append(readmeTitle);
  if (repo.hasReadme && repo.readme) {
    const rendered = document.createElement('div');
    renderMarkdownInto(rendered, repo.readme);
    readmeSection.append(rendered);
    if (repo.readmeTruncated) {
      const trunc = document.createElement('p');
      trunc.className = 'hint';
      trunc.textContent = t('readmeTruncated');
      readmeSection.append(trunc);
    }
  } else {
    const note = document.createElement('p');
    note.className = 'hint';
    note.textContent = t('noReadme');
    readmeSection.append(note);
  }
  pane.append(readmeSection);
}

async function generateSummary(id, btn) {
  btn.disabled = true;
  btn.textContent = t('aiWorking');
  try {
    const { summary } = await api(`/api/repos/${id}/summarize`, {
      method: 'POST',
      body: { lang: getLanguage() === 'zh-CN' ? 'zh' : 'en' },
    });
    if (state.selectedRepo?.id === id) {
      state.selectedRepo.generated = { summary };
      renderDetail(state.selectedRepo);
    }
    toast(t('aiSummary') + ' ✓');
  } catch (err) {
    if (err.code === 'ai_not_configured') {
      toast(t('aiNotConfigured'));
      $('#btn-settings').click();
    } else {
      toast(err.retryable ? `${err.message} ${t('retryableHint')}` : err.message);
    }
  } finally {
    btn.disabled = false;
    btn.textContent = state.selectedRepo?.generated?.summary ? t('aiRegenerate') : t('aiGenerate');
  }
}

async function selectRepo(id) {
  state.selectedId = id;
  expandApp();
  document.querySelectorAll('.result-item').forEach((el) => el.classList.toggle('active', Number(el.dataset.id) === id));
  try {
    const { repo } = await api(`/api/repos/${id}`);
    state.selectedRepo = repo;
    renderDetail(repo);
  } catch (err) {
    toast(err.message);
  }
}

function splitCsv(text) {
  return text.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
}

async function saveAnnotation(id) {
  try {
    await api(`/api/repos/${id}/annotation`, {
      method: 'PATCH',
      body: {
        reason: $('#d-reason').value.trim(),
        notes: $('#d-notes').value,
        tags: splitCsv($('#d-tags').value),
        projects: splitCsv($('#d-projects').value),
        status: $('#d-status').value,
      },
    });
    toast(t('toastSaved'));
    await loadFilters();
    await runSearch();
    await selectRepo(id);
  } catch (err) {
    toast(err.retryable ? `${err.message} ${t('retryableHint')}` : err.message);
  }
}

async function refreshRepo(id) {
  try {
    const { repo } = await api(`/api/repos/${id}/refresh`, { method: 'POST' });
    toast(t('toastRefreshed'));
    const summary = state.selectedRepo?.generated?.summary;
    if (summary) repo.generated = { summary };
    state.selectedRepo = repo;
    renderDetail(repo);
    runSearch();
  } catch (err) {
    toast(err.retryable ? `${err.message} ${t('retryableHint')}` : err.message);
  }
}

async function removeRepo(id) {
  const ok = await confirmDialog({ title: t('deleteConfirmTitle'), message: t('deleteConfirm') });
  if (!ok) return;
  try {
    await api(`/api/repos/${id}`, { method: 'DELETE' });
    toast(t('toastDeleted'));
    collapseApp();
    await loadFilters();
    await runSearch();
  } catch (err) {
    toast(err.message);
  }
}

// ---------------------------------------------------------------------------
// Import modal (stars + Chrome bookmarks)
// ---------------------------------------------------------------------------

function renderJob(job) {
  $('#import-progress').classList.remove('hidden');
  const statusEl = $('#import-status');
  statusEl.className = 'form-message';
  statusEl.textContent = t('importProgress', job);
  if (job.status === 'done') {
    statusEl.textContent += ` ${t('importDone')}`;
    statusEl.classList.add('ok');
  } else if (job.status === 'failed') {
    statusEl.textContent += ` ${t('importFailed', { error: job.error || '' })}`;
    statusEl.classList.add('err');
  } else if (job.status === 'cancelled') {
    statusEl.textContent += ` ${t('importCancelled')}`;
  }
  $('#btn-import-start').classList.toggle('hidden', job.status === 'running');
  $('#btn-import-cancel').classList.toggle('hidden', job.status !== 'running');
  $('#btn-import-resume').classList.toggle('hidden', !(job.status === 'failed' || job.status === 'cancelled') || job.type !== 'stars');
  const failures = $('#import-failures');
  failures.innerHTML = '';
  for (const f of (job.failures || []).slice(-8)) {
    const li = document.createElement('li');
    li.textContent = `${f.subject}: ${f.code}`;
    failures.append(li);
  }
}

async function pollJob(id) {
  clearInterval(state.jobTimer);
  state.activeJobId = id;
  state.jobTimer = setInterval(async () => {
    try {
      const { job } = await api(`/api/jobs/${id}`);
      renderJob(job);
      if (job.status !== 'running') {
        clearInterval(state.jobTimer);
        state.activeJobId = null;
        await loadFilters();
        await runSearch();
      }
    } catch {
      clearInterval(state.jobTimer);
    }
  }, 800);
}

function bindImportModal() {
  const dialog = $('#modal-import');
  $('#btn-import').addEventListener('click', () => {
    $('#import-progress').classList.add('hidden');
    $('#btn-import-start').classList.remove('hidden');
    $('#btn-import-cancel').classList.add('hidden');
    $('#btn-import-resume').classList.add('hidden');
    dialog.showModal();
  });
  $('#form-import').addEventListener('submit', async (e) => {
    e.preventDefault();
    try {
      const { job } = await api('/api/jobs/import-stars', {
        method: 'POST',
        body: { username: $('#import-username').value.trim(), includeReadme: $('#import-readme').checked },
      });
      renderJob(job);
      pollJob(job.id);
    } catch (err) {
      $('#import-progress').classList.remove('hidden');
      const statusEl = $('#import-status');
      statusEl.className = 'form-message err';
      statusEl.textContent = err.message;
    }
  });
  $('#btn-import-bookmarks').addEventListener('click', async () => {
    try {
      const { job } = await api('/api/import/chrome-bookmarks', { method: 'POST', body: {} });
      renderJob(job);
      pollJob(job.id);
    } catch (err) {
      $('#import-progress').classList.remove('hidden');
      const statusEl = $('#import-status');
      statusEl.className = 'form-message err';
      statusEl.textContent = err.message;
    }
  });
  $('#btn-import-cancel').addEventListener('click', async () => {
    if (state.activeJobId) await api(`/api/jobs/${state.activeJobId}/cancel`, { method: 'POST' }).catch(() => {});
  });
  $('#btn-import-resume').addEventListener('click', async () => {
    if (!state.activeJobId) return;
    const { job } = await api(`/api/jobs/${state.activeJobId}/resume`, { method: 'POST' }).catch(() => ({ job: null }));
    if (job) {
      renderJob(job);
      pollJob(job.id);
    }
  });
}

// ---------------------------------------------------------------------------
// Settings modal
// ---------------------------------------------------------------------------

function bindSettingsModal() {
  const dialog = $('#modal-settings');
  $('#btn-settings').addEventListener('click', async () => {
    try {
      const data = await api('/api/settings');
      $('#settings-version').textContent = data.version;
      $('#settings-datadir').textContent = data.dataDir;
      $('#settings-token').textContent = data.pairingToken;
      $('#settings-gh-state').textContent = data.githubTokenSet ? t('settingsGithubTokenSet') : t('settingsGithubTokenUnset');
      $('#settings-ds-state').textContent = data.deepseekKeySet ? t('settingsDeepseekSet') : t('settingsDeepseekUnset');
      $('#settings-gh-token').value = '';
      $('#settings-ds-key').value = '';
      dialog.showModal();
    } catch (err) {
      toast(err.message);
    }
  });
  $('#btn-copy-token').addEventListener('click', async () => {
    await navigator.clipboard.writeText($('#settings-token').textContent).catch(() => {});
    $('#btn-copy-token').textContent = t('copied');
    setTimeout(() => { $('#btn-copy-token').textContent = t('copy'); }, 1500);
  });
  const bindKey = (inputSel, saveSel, clearSel, stateSel, path, setKey) => {
    $(saveSel).addEventListener('click', async () => {
      try {
        await api(path, { method: 'PUT', body: { token: $(inputSel).value } });
        $(stateSel).textContent = t(setKey);
        $(inputSel).value = '';
      } catch (err) {
        toast(err.message);
      }
    });
    $(clearSel).addEventListener('click', async () => {
      await api(path, { method: 'DELETE' }).catch(() => {});
      $(stateSel).textContent = t(setKey === 'settingsGithubTokenSet' ? 'settingsGithubTokenUnset' : 'settingsDeepseekUnset');
    });
  };
  bindKey('#settings-gh-token', '#btn-save-gh-token', '#btn-clear-gh-token', '#settings-gh-state', '/api/settings/github-token', 'settingsGithubTokenSet');
  bindKey('#settings-ds-key', '#btn-save-ds-key', '#btn-clear-ds-key', '#settings-ds-state', '/api/settings/deepseek-key', 'settingsDeepseekSet');

  $('#btn-export').addEventListener('click', async () => {
    try {
      const res = await fetch('/api/export', { headers: { 'x-reposhelf-token': TOKEN } });
      const blob = await res.blob();
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = (res.headers.get('content-disposition') || '').match(/filename="([^"]+)"/)?.[1] || 'repo-shelf-export.json';
      a.click();
      URL.revokeObjectURL(a.href);
    } catch {
      toast(t('errorGeneric'));
    }
  });
  $('#btn-restore').addEventListener('click', () => $('#restore-file').click());
  $('#restore-file').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    try {
      const text = await file.text();
      const res = await fetch(`/api/restore?mode=${$('#restore-mode').value}`, {
        method: 'POST',
        headers: { 'x-reposhelf-token': TOKEN, 'content-type': 'application/json' },
        body: text,
      });
      const json = await res.json();
      if (!res.ok || !json.ok) throw new ApiError(res.status, json);
      $('#restore-result').textContent = t('restoreResult', json.data);
      await loadFilters();
      await runSearch();
    } catch (err) {
      $('#restore-result').textContent = err.message;
    } finally {
      e.target.value = '';
    }
  });
  $('#btn-refresh-all').addEventListener('click', async () => {
    try {
      await api('/api/jobs/refresh-all', { method: 'POST' });
      $('#refresh-all-state').textContent = t('refreshAllStarted');
    } catch (err) {
      toast(err.message);
    }
  });
}

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------

function bindModals() {
  document.querySelectorAll('[data-close]').forEach((btn) => {
    btn.addEventListener('click', () => btn.closest('dialog').close());
  });
}

function init() {
  setLanguage(detectLanguage());
  applyI18n();
  $('#lang-switch').addEventListener('change', async (e) => {
    setLanguage(e.target.value);
    applyI18n();
    renderChips();
    renderResults();
    if (state.selectedRepo) renderDetail(state.selectedRepo);
  });
  $('#omnibox').addEventListener('input', onOmniboxInput);
  $('#omnibox').addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && state.detected?.valid) {
      e.preventDefault();
      $('#btn-banner-action').click();
    }
  });
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    if (document.querySelector('dialog[open]')) return; // dialogs close natively
    if (!$('#filter-popover').classList.contains('hidden')) {
      $('#filter-popover').classList.add('hidden');
      return;
    }
    const app = $('#app');
    if (app.classList.contains('mode-expanded')) {
      collapseApp();
    } else if (app.classList.contains('mode-compact')) {
      // Compact -> bar: collapse to just the omnibox strip.
      setMode('bar');
    } else if ($('#omnibox').value) {
      $('#omnibox').value = '';
      state.q = '';
      state.detected = null;
      renderSaveBanner();
      runSearch();
    }
  });
  $('#omnibox').addEventListener('focus', () => {
    if ($('#app').classList.contains('mode-bar')) setMode('compact');
  });
  $('#btn-theme').addEventListener('click', () => {
    applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');
  });
  $('#btn-more').addEventListener('click', () => {
    state.offset += state.pageSize;
    runSearch({ append: true });
  });
  bindFilters();
  bindModals();
  bindImportModal();
  bindSettingsModal();
  loadFilters().then(() => runSearch());
  $('#omnibox').focus();
}

init();
