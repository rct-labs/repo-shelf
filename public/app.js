import { marked } from '/vendor/marked.esm.js';
import DOMPurify from '/vendor/purify.es.mjs';
import { t, getLanguage, setLanguage, detectLanguage, LANGUAGES } from '/i18n.js';

const TOKEN = document.querySelector('meta[name="repo-shelf-token"]')?.content || '';

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
  $('#search-input').placeholder = t('searchPlaceholder');
  $('#settings-gh-token').placeholder = t('settingsTokenPlaceholder');
  $('#add-url').placeholder = t('addUrlPlaceholder');
  $('#add-reason').placeholder = t('detailReasonPlaceholder');
}

function fillLanguageSwitch() {
  const select = $('#lang-switch');
  select.innerHTML = '';
  for (const { id, label } of LANGUAGES) {
    const opt = document.createElement('option');
    opt.value = id;
    opt.textContent = label;
    select.append(opt);
  }
  select.value = getLanguage();
}

function fillStaticSelects() {
  const status = $('#filter-status');
  const current = status.value;
  status.innerHTML = '';
  status.append(new Option(`${t('filterStatus')}: ${t('archivedAny')}`, ''));
  for (const s of STATUS_KEYS) status.append(new Option(t(`status_${s}`), s));
  status.value = current;

  const archived = $('#filter-archived');
  const currentA = archived.value;
  archived.innerHTML = '';
  archived.append(new Option(`${t('filterArchived')}: ${t('archivedAny')}`, ''));
  archived.append(new Option(t('archivedYes'), 'true'));
  archived.append(new Option(t('archivedNo'), 'false'));
  archived.value = currentA;

  $('#btn-clear-filters').textContent = t('clearFilters');
  const mode = $('#restore-mode');
  const currentMode = mode.value;
  mode.innerHTML = '';
  mode.append(new Option(t('restoreMerge'), 'merge'));
  mode.append(new Option(t('restoreOverwrite'), 'overwrite'));
  mode.value = currentMode || 'merge';
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
    const onClick = () => {
      btn.removeEventListener('click', onClick);
      dialog.removeEventListener('close', onClose);
      resolve(true);
    };
    const onClose = () => {
      btn.removeEventListener('click', onClick);
      resolve(false);
    };
    btn.addEventListener('click', () => { dialog.close(); onClick(); });
    dialog.addEventListener('close', onClose, { once: true });
    dialog.showModal();
  });
}

// ---------------------------------------------------------------------------
// Search + results
// ---------------------------------------------------------------------------

function badge(text, kind = '') {
  const span = document.createElement('span');
  span.className = `badge ${kind}`.trim();
  span.textContent = text;
  return span;
}

function refreshBadge(repo) {
  if (repo.refreshStatus === 'not_found') return badge(t('refresh_not_found'), 'warn');
  if (repo.refreshStatus === 'inaccessible') return badge(t('refresh_inaccessible'), 'warn');
  if (repo.refreshStatus === 'error') return badge(t('refresh_error'), 'warn');
  if (repo.stale) return badge(t('stale'), 'warn');
  return null;
}

function renderResults({ append = false } = {}) {
  const list = $('#results-list');
  if (!append) list.innerHTML = '';
  const frag = document.createDocumentFragment();
  for (const item of state.items.slice(append ? state.items.length - state.pageSize : 0)) {
    const { repo, snippet, matchedFields } = item;
    const li = document.createElement('li');
    li.className = 'result-item' + (repo.id === state.selectedId ? ' active' : '');
    li.dataset.id = repo.id;

    const name = document.createElement('div');
    name.className = 'result-name';
    const owner = document.createElement('span');
    owner.className = 'owner';
    owner.textContent = `${repo.owner}/`;
    name.append(owner, document.createTextNode(repo.name));
    li.append(name);

    const badges = document.createElement('div');
    badges.className = 'result-badges';
    badges.append(badge(t(`status_${repo.annotation.status}`)));
    if (repo.language) badges.append(badge(repo.language));
    if (repo.archived) badges.append(badge(t('archivedYes'), 'warn'));
    const rb = refreshBadge(repo);
    if (rb) badges.append(rb);
    if (repo.seenInImport && !repo.starredUpstream) {
      badges.append(badge(t('unstarred'), 'warn'));
    }
    li.append(badges);

    if (snippet) {
      const sn = document.createElement('div');
      sn.className = 'result-snippet';
      sn.innerHTML = snippet; // server-escaped, contains only <mark> markup
      li.append(sn);
    }
    if (matchedFields && matchedFields.length) {
      const mf = document.createElement('div');
      mf.className = 'matched-fields';
      mf.textContent = t('matchedIn', { fields: matchedFields.map((f) => t(`field_${f}`)).join(', ') });
      li.append(mf);
    }
    li.addEventListener('click', () => selectRepo(repo.id));
    frag.append(li);
  }
  list.append(frag);

  $('#results-count').textContent = t('resultsCount', { count: state.total });
  const moreBox = $('#results-more');
  moreBox.classList.toggle('hidden', state.items.length >= state.total);

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
function onSearchInput() {
  clearTimeout(searchDebounce);
  searchDebounce = setTimeout(() => {
    state.q = $('#search-input').value.trim();
    runSearch();
  }, 200);
}

// ---------------------------------------------------------------------------
// Detail panel
// ---------------------------------------------------------------------------

function fmtTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(getLanguage() === 'zh-CN' ? 'zh-CN' : 'en');
}

function renderDetail(repo) {
  const pane = $('#detail-content');
  pane.innerHTML = '';

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
  if (repo.archived) badges.append(badge(t('archivedYes'), 'warn'));
  if (repo.starredUpstream) badges.append(badge(t('starred'), 'ok'));
  else if (repo.seenInImport) badges.append(badge(t('unstarred'), 'warn'));
  const rb = refreshBadge(repo);
  if (rb) badges.append(rb);
  if (repo.readmeTruncated) badges.append(badge(t('readmeTruncated'), 'warn'));
  head.append(badges);

  const meta = document.createElement('dl');
  meta.className = 'meta-grid';
  const metaRows = [
    [t('stars'), repo.stars ?? '—'],
    [t('license'), repo.licenseId ?? '—'],
    [t('branch'), repo.defaultBranch ?? '—'],
    [t('pushedAt'), fmtTime(repo.pushedAt)],
    [t('savedAt'), fmtTime(repo.createdAt)],
    [t('fetchedAt'), fmtTime(repo.fetchedAt)],
  ];
  for (const [k, v] of metaRows) {
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
    for (const topic of repo.topics) topics.append(badge(topic));
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

  // README
  const readmeSection = document.createElement('div');
  readmeSection.className = 'readme-render';
  const readmeTitle = document.createElement('h2');
  readmeTitle.textContent = t('readme');
  readmeSection.append(readmeTitle);
  if (repo.hasReadme && repo.readme) {
    const rendered = document.createElement('div');
    rendered.innerHTML = DOMPurify.sanitize(marked.parse(repo.readme, { async: false }));
    readmeSection.append(rendered);
  } else if (repo.hasReadme) {
    const note = document.createElement('p');
    note.className = 'hint';
    note.textContent = t('loading');
    readmeSection.append(note);
  } else {
    const note = document.createElement('p');
    note.className = 'hint';
    note.textContent = t('noReadme');
    readmeSection.append(note);
  }
  pane.append(readmeSection);
}

async function selectRepo(id) {
  state.selectedId = id;
  document.querySelectorAll('.result-item').forEach((el) => el.classList.toggle('active', Number(el.dataset.id) === id));
  $('#detail-empty').classList.add('hidden');
  try {
    const { repo } = await api(`/api/repos/${id}`);
    state.selectedRepo = repo;
    $('#detail-content').classList.remove('hidden');
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
    state.selectedId = null;
    state.selectedRepo = null;
    $('#detail-content').classList.add('hidden');
    $('#detail-empty').classList.remove('hidden');
    $('#detail-empty').textContent = t('detailEmpty');
    await loadFilters();
    await runSearch();
  } catch (err) {
    toast(err.message);
  }
}

// ---------------------------------------------------------------------------
// Filters
// ---------------------------------------------------------------------------

async function loadFilters() {
  try {
    const data = await api('/api/filters');
    state.libraryEmpty = data.total === 0;
    const keep = (sel) => $(sel).value;
    const fill = (sel, values, labelKey) => {
      const select = $(sel);
      const current = keep(sel);
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

function bindFilters() {
  const map = { status: '#filter-status', tag: '#filter-tag', language: '#filter-language', project: '#filter-project', archived: '#filter-archived' };
  for (const [key, sel] of Object.entries(map)) {
    $(sel).addEventListener('change', (e) => {
      state.filters[key] = e.target.value;
      runSearch();
    });
  }
  $('#btn-clear-filters').addEventListener('click', () => {
    state.filters = { status: '', tag: '', language: '', project: '', archived: '' };
    for (const sel of Object.values(map)) $(sel).value = '';
    runSearch();
  });
}

// ---------------------------------------------------------------------------
// Add modal
// ---------------------------------------------------------------------------

function bindAddModal() {
  const dialog = $('#modal-add');
  $('#btn-add').addEventListener('click', () => {
    $('#add-message').classList.add('hidden');
    dialog.showModal();
    $('#add-url').focus();
  });
  $('#form-add').addEventListener('submit', async (e) => {
    e.preventDefault();
    const msg = $('#add-message');
    msg.classList.add('hidden');
    const submitBtn = $('#form-add .btn-primary');
    submitBtn.disabled = true;
    try {
      const result = await api('/api/repos', {
        method: 'POST',
        body: {
          url: $('#add-url').value.trim(),
          reason: $('#add-reason').value.trim(),
          tags: splitCsv($('#add-tags').value),
        },
      });
      msg.className = 'form-message ok';
      msg.textContent = result.outcome === 'created' ? t('addCreated') : t('addAlready');
      await loadFilters();
      await runSearch();
      setTimeout(() => {
        dialog.close();
        $('#form-add').reset();
        selectRepo(result.repo.id);
      }, 600);
    } catch (err) {
      msg.className = 'form-message err';
      msg.textContent = err.retryable ? `${t('addRetryable')}` : err.message;
    } finally {
      submitBtn.disabled = false;
    }
  });
}

// ---------------------------------------------------------------------------
// Import modal
// ---------------------------------------------------------------------------

function renderJob(job) {
  const progress = $('#import-progress');
  progress.classList.remove('hidden');
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
  $('#btn-import-resume').classList.toggle('hidden', !(job.status === 'failed' || job.status === 'cancelled'));
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
      const statusEl = $('#import-status');
      $('#import-progress').classList.remove('hidden');
      statusEl.className = 'form-message err';
      statusEl.textContent = err.message;
    }
  });
  $('#btn-import-cancel').addEventListener('click', async () => {
    if (state.activeJobId) await api(`/api/jobs/${state.activeJobId}/cancel`, { method: 'POST' }).catch(() => {});
  });
  $('#btn-import-resume').addEventListener('click', async () => {
    if (state.activeJobId) {
      const { job } = await api(`/api/jobs/${state.activeJobId}/resume`, { method: 'POST' }).catch((err) => ({ job: null, err }));
      if (job) {
        renderJob(job);
        pollJob(job.id);
      }
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
      $('#settings-gh-token').value = '';
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
  $('#btn-save-gh-token').addEventListener('click', async () => {
    try {
      await api('/api/settings/github-token', { method: 'PUT', body: { token: $('#settings-gh-token').value } });
      $('#settings-gh-state').textContent = t('settingsGithubTokenSet');
      $('#settings-gh-token').value = '';
    } catch (err) {
      toast(err.message);
    }
  });
  $('#btn-clear-gh-token').addEventListener('click', async () => {
    await api('/api/settings/github-token', { method: 'DELETE' }).catch(() => {});
    $('#settings-gh-state').textContent = t('settingsGithubTokenUnset');
  });
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
      const mode = $('#restore-mode').value;
      const res = await fetch(`/api/restore?mode=${mode}`, {
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
  $('#detail-empty').textContent = t('detailEmpty');
  $('#lang-switch').addEventListener('change', async (e) => {
    setLanguage(e.target.value);
    applyI18n();
    renderResults();
    if (state.selectedRepo) renderDetail(state.selectedRepo);
  });
  $('#search-input').addEventListener('input', onSearchInput);
  $('#btn-more').addEventListener('click', () => {
    state.offset += state.pageSize;
    runSearch({ append: true });
  });
  bindFilters();
  bindModals();
  bindAddModal();
  bindImportModal();
  bindSettingsModal();
  loadFilters().then(() => runSearch());
}

init();
