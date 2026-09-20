// Repo Shelf extension popup: one-click capture with reason/tags, an explicit
// pending queue when the local service is offline, and retry/discard actions.
//
// The URL parser below mirrors server/normalize.js on purpose: the popup must
// normalize captures even when the local service is unreachable.

const STR = {
  en: {
    notConfigured: 'This extension is not paired with your local Repo Shelf yet.',
    openOptions: 'Open options',
    reasonPh: 'Why are you saving this? (optional)',
    tagsPh: 'tags, comma separated (optional)',
    manualLabel: 'Not a repository page. Paste a GitHub repository URL:',
    urlPh: 'https://github.com/owner/repo',
    save: 'Save to Repo Shelf',
    saving: 'Saving…',
    saved: 'Saved.',
    already: 'Already on your shelf — annotations preserved.',
    queued: 'Local service unreachable — capture queued below. Nothing was lost.',
    invalidUrl: 'Not a valid GitHub repository URL.',
    unauthorized: 'Pairing token rejected. Check the options page.',
    retryable: 'Failed, but you can retry safely.',
    pendingTitle: 'Pending captures (retry or discard explicitly)',
    retry: 'Retry',
    discard: 'Discard',
    openApp: 'Open Repo Shelf',
    options: 'Options',
    queueFull: 'Pending queue is full (50). Retry or discard entries first.',
  },
  'zh-CN': {
    notConfigured: '扩展尚未与本地 Repo Shelf 配对。',
    openOptions: '打开选项页',
    reasonPh: '为什么收藏它？（可选）',
    tagsPh: '标签，逗号分隔（可选）',
    manualLabel: '当前不是仓库页面。粘贴 GitHub 仓库 URL：',
    urlPh: 'https://github.com/owner/repo',
    save: '保存到 Repo Shelf',
    saving: '保存中…',
    saved: '已保存。',
    already: '已在货架中——已有标注已保留。',
    queued: '本地服务不可达——已加入待处理队列，不会丢失。',
    invalidUrl: '不是有效的 GitHub 仓库 URL。',
    unauthorized: '配对令牌被拒绝，请检查选项页。',
    retryable: '失败了，但可以安全重试。',
    pendingTitle: '待处理的收藏（请显式重试或丢弃）',
    retry: '重试',
    discard: '丢弃',
    openApp: '打开 Repo Shelf',
    options: '选项',
    queueFull: '待处理队列已满（50 条），请先重试或丢弃。',
  },
};

const PENDING_CAP = 50;

let lang = 'en';
function t(key) {
  return (STR[lang] && STR[lang][key]) || STR.en[key] || key;
}

function parseRepoUrl(input) {
  if (typeof input !== 'string' || !input.trim()) return null;
  let text = input.trim();
  if (!/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(text)) text = `https://${text}`;
  let url;
  try {
    url = new URL(text);
  } catch {
    return null;
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
  const host = url.hostname.toLowerCase();
  if (host !== 'github.com' && host !== 'www.github.com') return null;
  let segments;
  try {
    segments = url.pathname.split('/').filter(Boolean).map((s) => decodeURIComponent(s));
  } catch {
    return null;
  }
  if (segments.length < 2) return null;
  const [owner] = segments;
  const repo = segments[1].replace(/\.git$/i, '');
  if (!/^[a-z0-9](?:[a-z0-9-]{0,38})$/i.test(owner)) return null;
  if (!/^[a-z0-9._-]{1,100}$/i.test(repo) || repo === '.' || repo === '..') return null;
  return { owner, repo, fullName: `${owner}/${repo}`, url: `https://github.com/${owner}/${repo}` };
}

async function getConfig() {
  const { rsConfig, rsLang } = await chrome.storage.local.get(['rsConfig', 'rsLang']);
  const config = rsConfig || {};
  return {
    serverUrl: (config.serverUrl || 'http://127.0.0.1:4790').replace(/\/+$/, ''),
    token: config.token || '',
    langPref: rsLang || 'auto',
  };
}

async function getQueue() {
  const { pendingQueue } = await chrome.storage.local.get('pendingQueue');
  return Array.isArray(pendingQueue) ? pendingQueue : [];
}

async function setQueue(queue) {
  await chrome.storage.local.set({ pendingQueue: queue });
  const count = queue.length;
  await chrome.action.setBadgeText({ text: count ? String(count) : '' });
  await chrome.action.setBadgeBackgroundColor({ color: '#cf222e' });
}

function showMessage(kind, text) {
  const el = document.getElementById('message');
  el.className = `msg ${kind}`;
  el.textContent = text;
  el.classList.remove('hidden');
}

function splitCsv(text) {
  return text.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
}

async function postCapture(config, payload) {
  const res = await fetch(`${config.serverUrl}/api/repos`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-reposhelf-token': config.token },
    body: JSON.stringify(payload),
  });
  const json = await res.json().catch(() => null);
  return { status: res.status, json };
}

async function renderPending(config) {
  const queue = await getQueue();
  const section = document.getElementById('pending');
  const list = document.getElementById('pending-list');
  list.innerHTML = '';
  section.classList.toggle('hidden', queue.length === 0);
  document.getElementById('pending-title').textContent = t('pendingTitle');
  queue.forEach((item, index) => {
    const box = document.createElement('div');
    box.className = 'pending-item';
    const url = document.createElement('div');
    url.className = 'url';
    url.textContent = item.url + (item.reason ? ` — ${item.reason}` : '');
    const row = document.createElement('div');
    row.className = 'row';
    const retry = document.createElement('button');
    retry.textContent = t('retry');
    retry.className = 'primary';
    retry.addEventListener('click', async () => {
      retry.disabled = true;
      try {
        const { status } = await postCapture(config, item);
        if (status === 201 || status === 200 || status === 400) {
          // 201/200: saved. 400 invalid URL: never retryable, drop with notice.
          const q = await getQueue();
          q.splice(index, 1);
          await setQueue(q);
        }
        if (status === 401) showMessage('err', t('unauthorized'));
      } catch {
        // Still offline: keep the entry.
      } finally {
        retry.disabled = false;
        renderPending(config);
      }
    });
    const discard = document.createElement('button');
    discard.textContent = t('discard');
    discard.addEventListener('click', async () => {
      const q = await getQueue();
      q.splice(index, 1);
      await setQueue(q);
      renderPending(config);
    });
    row.append(retry, discard);
    box.append(url, row);
    list.append(box);
  });
}

async function detectCurrentTabUrl() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return tab?.url || null;
  } catch {
    return null;
  }
}

async function init() {
  const config = await getConfig();
  lang = config.langPref === 'auto' ? ((navigator.language || '').toLowerCase().startsWith('zh') ? 'zh-CN' : 'en') : config.langPref;

  document.getElementById('input-reason').placeholder = t('reasonPh');
  document.getElementById('input-tags').placeholder = t('tagsPh');
  document.getElementById('input-url').placeholder = t('urlPh');
  document.getElementById('manual-label').textContent = t('manualLabel');
  document.getElementById('btn-save').textContent = t('save');
  document.getElementById('btn-open-app').textContent = t('openApp');
  document.getElementById('btn-options').textContent = t('options');
  document.getElementById('not-configured-text').textContent = t('notConfigured');
  document.getElementById('btn-open-options').textContent = t('openOptions');

  document.getElementById('btn-open-options').addEventListener('click', () => chrome.runtime.openOptionsPage());
  document.getElementById('btn-options').addEventListener('click', () => chrome.runtime.openOptionsPage());
  document.getElementById('btn-open-app').addEventListener('click', async () => {
    await chrome.tabs.create({ url: config.serverUrl });
  });

  await renderPending(config);

  if (!config.token) {
    document.getElementById('not-configured').classList.remove('hidden');
    return;
  }
  document.getElementById('capture').classList.remove('hidden');

  let detected = parseRepoUrl((await detectCurrentTabUrl()) || '');
  const detectedBox = document.getElementById('detected');
  const manualBox = document.getElementById('manual');
  if (detected) {
    detectedBox.classList.remove('hidden');
    document.getElementById('repo-owner').textContent = `${detected.owner}/`;
    document.getElementById('repo-name').textContent = detected.repo;
  } else {
    manualBox.classList.remove('hidden');
  }

  document.getElementById('btn-save').addEventListener('click', async () => {
    // Auto-detected tab URL is reused; manual input is re-parsed every time
    // so consecutive captures of different repositories work.
    const target = detected || parseRepoUrl(document.getElementById('input-url').value.trim());
    if (!target) {
      showMessage('err', t('invalidUrl'));
      return;
    }
    const payload = {
      url: target.url,
      reason: document.getElementById('input-reason').value.trim(),
      tags: splitCsv(document.getElementById('input-tags').value),
    };
    const btn = document.getElementById('btn-save');
    btn.disabled = true;
    btn.textContent = t('saving');
    try {
      const { status, json } = await postCapture(config, payload);
      if (status === 201) {
        showMessage('ok', t('saved'));
      } else if (status === 200) {
        showMessage('info', t('already'));
      } else if (status === 401) {
        showMessage('err', t('unauthorized'));
      } else if (status === 400) {
        showMessage('err', json?.error?.message || t('invalidUrl'));
      } else {
        showMessage('err', json?.error?.message ? `${json.error.message} ${t('retryable')}` : t('retryable'));
      }
    } catch {
      // Local service offline: queue explicitly, never silently drop.
      const queue = await getQueue();
      if (queue.length >= PENDING_CAP) {
        showMessage('err', t('queueFull'));
      } else {
        queue.push({ ...payload, queuedAt: new Date().toISOString() });
        await setQueue(queue);
        showMessage('info', t('queued'));
        await renderPending(config);
      }
    } finally {
      btn.disabled = false;
      btn.textContent = t('save');
    }
  });
}

init();
