// Options page: server URL, pairing token, language. Everything stays in
// chrome.storage.local (no Chrome sync) so the pairing token never leaves
// this machine.

const els = {
  serverUrl: document.getElementById('server-url'),
  token: document.getElementById('pairing-token'),
  lang: document.getElementById('lang'),
  save: document.getElementById('save'),
  test: document.getElementById('test'),
  message: document.getElementById('message'),
};

function show(kind, text) {
  els.message.className = `msg ${kind}`;
  els.message.textContent = text;
}

async function load() {
  const { rsConfig, rsLang } = await chrome.storage.local.get(['rsConfig', 'rsLang']);
  els.serverUrl.value = rsConfig?.serverUrl || 'http://127.0.0.1:4790';
  els.token.value = rsConfig?.token || '';
  els.lang.value = rsLang || 'auto';
}

els.save.addEventListener('click', async () => {
  const serverUrl = els.serverUrl.value.trim().replace(/\/+$/, '');
  if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(serverUrl)) {
    show('err', 'Server URL must be a loopback http address, e.g. http://127.0.0.1:4790');
    return;
  }
  await chrome.storage.local.set({
    rsConfig: { serverUrl, token: els.token.value.trim() },
    rsLang: els.lang.value,
  });
  show('ok', 'Saved. / 已保存。');
});

els.test.addEventListener('click', async () => {
  const serverUrl = els.serverUrl.value.trim().replace(/\/+$/, '');
  try {
    // /api/settings requires the pairing token, unlike the public /api/health.
    const res = await fetch(`${serverUrl}/api/settings`, {
      headers: { 'x-reposhelf-token': els.token.value.trim() },
    });
    if (res.ok) {
      show('ok', 'Connected. / 连接成功。');
    } else if (res.status === 401 || res.status === 403) {
      show('err', 'Reachable, but the token was rejected. / 服务可达，但令牌被拒绝。');
    } else {
      show('err', `Unexpected response: HTTP ${res.status}`);
    }
  } catch {
    show('err', 'Unreachable. Is the app running? / 无法连接，应用是否在运行？');
  }
});

load();
