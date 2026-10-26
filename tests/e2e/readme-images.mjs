// Live, read-only verification of the reported repository's actual images.
import { chromium } from 'playwright';
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1300, height: 900 } });
  for (const file of (process.env.REPO_SHELF_INSTALLED ? [] : ['app.js', 'i18n.js', 'readme-urls.js'])) {
    await page.route(`**/${file}`, async route => route.fulfill({ contentType: 'text/javascript',
      body: await fs.readFile(new URL(`../../public/${file}`, import.meta.url), 'utf8'),
    }));
  }
  const localImages = [];
  page.on('request', req => {
    if (req.resourceType() === 'image' && new URL(req.url()).hostname === '127.0.0.1') localImages.push(req.url());
  });
  await page.goto(process.env.REPO_SHELF_URL || 'http://127.0.0.1:4790');
  await page.click('[data-tab="library"]');
  await page.locator('#omnibox').fill('diagram-design');
  await page.locator('#results-list li').filter({ hasText: 'cathrynlavery' }).first().click();
  const thumbnails = '.readme-render img[src*="docs/screenshots/thumbs/"]';
  await page.waitForFunction(selector => {
    const imgs = [...document.querySelectorAll(selector)];
    return imgs.length > 0 && imgs.every(img => img.complete && img.naturalWidth > 0);
  }, thumbnails, { timeout: 60000 });
  const images = await page.locator(thumbnails).count();
  assert.equal(localImages.length, 0, 'README images must not request localhost');
  console.log(`PASS: ${images} real gallery images loaded; no local image requests`);
  await page.locator(thumbnails).first().scrollIntoViewIfNeeded();
  await page.screenshot({ path: process.env.TEMP + '/repo-shelf-readme-fixed.png' });
} finally { await browser.close(); }
