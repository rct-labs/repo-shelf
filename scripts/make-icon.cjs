// Renders assets/icon.svg into the app icons with headless Chromium:
//   assets/icon.png (256), public/icon.png (favicon, 64) and a multi-size
//   assets/icon.ico (16–256, PNG-compressed entries, Vista+).
// Run: node scripts/make-icon.cjs [--preview <out.png>]

const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const root = path.join(__dirname, '..');
const svg = fs.readFileSync(path.join(root, 'assets', 'icon.svg'), 'utf8');
const SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];

function makeIco(images) {
  // images: [{ size, png }]; ICONDIR + ICONDIRENTRY[] + PNG payloads.
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  const entries = [];
  let offset = 6 + 16 * images.length;
  for (const { size, png } of images) {
    const e = Buffer.alloc(16);
    e.writeUInt8(size >= 256 ? 0 : size, 0);
    e.writeUInt8(size >= 256 ? 0 : size, 1);
    e.writeUInt16LE(1, 4); // planes
    e.writeUInt16LE(32, 6); // bpp
    e.writeUInt32LE(png.length, 8);
    e.writeUInt32LE(offset, 12);
    offset += png.length;
    entries.push(e);
  }
  return Buffer.concat([header, ...entries, ...images.map((i) => i.png)]);
}

async function render(page, size) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(
    `<html><body style="margin:0;background:transparent">` +
    svg.replace('<svg ', `<svg width="${size}" height="${size}" `) +
    `</body></html>`,
  );
  return page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const images = [];
  for (const size of SIZES) images.push({ size, png: await render(page, size) });

  const previewIdx = process.argv.indexOf('--preview');
  if (previewIdx >= 0) {
    // Side-by-side sheet on light and dark backgrounds for review.
    const cells = images.map(({ size, png }) =>
      `<img width="${size}" height="${size}" src="data:image/png;base64,${png.toString('base64')}">`).join('');
    await page.setViewportSize({ width: 900, height: 330 });
    await page.setContent(`<html><body style="margin:0;font:12px sans-serif">
      <div style="display:flex;gap:18px;align-items:end;padding:16px;background:#f3f3f3">${cells}</div>
      <div style="display:flex;gap:18px;align-items:end;padding:16px;background:#202020">${cells}</div></body></html>`);
    await page.screenshot({ path: process.argv[previewIdx + 1] });
  } else {
    const pick = (s) => images.find((i) => i.size === s).png;
    fs.writeFileSync(path.join(root, 'assets', 'icon.png'), pick(256));
    fs.writeFileSync(path.join(root, 'public', 'icon.png'), pick(64));
    fs.writeFileSync(path.join(root, 'assets', 'icon.ico'), makeIco(images));
    console.log('wrote assets/icon.png, public/icon.png, assets/icon.ico');
  }
  await browser.close();
})();
