// Background service worker: restores the pending-capture badge after browser
// restarts so queued work is never silently forgotten.

async function refreshBadge() {
  const { pendingQueue } = await chrome.storage.local.get('pendingQueue');
  const count = Array.isArray(pendingQueue) ? pendingQueue.length : 0;
  await chrome.action.setBadgeText({ text: count ? String(count) : '' });
  await chrome.action.setBadgeBackgroundColor({ color: '#cf222e' });
}

chrome.runtime.onStartup.addListener(refreshBadge);
chrome.runtime.onInstalled.addListener(refreshBadge);
