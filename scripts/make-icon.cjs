// Generates the app/tray icons: assets/icon.png (256x256) and assets/icon.ico.
// A simple "shelf" mark: rounded blue square with three light shelf bars.
// Run once: node scripts/make-icon.js

const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const SIZE = 256;
const BG = [9, 105, 218]; // #0969da
const FG = [235, 243, 255];

function roundedRectContains(x, y, w, h, r) {
  if (x < 0 || y < 0 || x >= w || y >= h) return false;
  const cx = x < r ? r : x >= w - r ? w - r - 1 : x;
  const cy = y < r ? r : y >= h - r ? h - r - 1 : y;
  const dx = x - cx;
  const dy = y - cy;
  return dx * dx + dy * dy <= r * r;
}

function buildPixels() {
  const px = Buffer.alloc(SIZE * SIZE * 4, 0);
  for (let y = 0; y < SIZE; y += 1) {
    for (let x = 0; x < SIZE; x += 1) {
      const off = (y * SIZE + x) * 4;
      if (!roundedRectContains(x, y, SIZE, SIZE, 56)) continue; // transparent corners
      px[off] = BG[0]; px[off + 1] = BG[1]; px[off + 2] = BG[2]; px[off + 3] = 255;
    }
  }
  // Three shelf bars.
  const barLeft = 52;
  const barRight = SIZE - 52;
  const barHeight = 26;
  for (const top of [62, 115, 168]) {
    for (let y = top; y < top + barHeight; y += 1) {
      for (let x = barLeft; x < barRight; x += 1) {
        if (!roundedRectContains(x - barLeft, y - top, barRight - barLeft, barHeight, 13)) continue;
        const off = (y * SIZE + x) * 4;
        px[off] = FG[0]; px[off + 1] = FG[1]; px[off + 2] = FG[2]; px[off + 3] = 255;
      }
    }
  }
  return px;
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buf) {
  let crc = 0xFFFFFFFF;
  for (const b of buf) crc = CRC_TABLE[(crc ^ b) & 0xFF] ^ (crc >>> 8);
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function makePng() {
  const px = buildPixels();
  const raw = Buffer.alloc(SIZE * (SIZE * 4 + 1));
  for (let y = 0; y < SIZE; y += 1) {
    raw[y * (SIZE * 4 + 1)] = 0; // filter: none
    px.copy(raw, y * (SIZE * 4 + 1) + 1, y * SIZE * 4, (y + 1) * SIZE * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(SIZE, 0);
  ihdr.writeUInt32BE(SIZE, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function makeIco(png) {
  // ICO with a single 256x256 PNG-compressed image (Vista+).
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(1, 4);
  const entry = Buffer.alloc(16);
  entry[0] = 0; // width 256 -> 0
  entry[1] = 0; // height 256 -> 0
  entry[2] = 0;
  entry[3] = 0;
  entry.writeUInt16LE(1, 4); // planes
  entry.writeUInt16LE(32, 6); // bpp
  entry.writeUInt32LE(png.length, 8);
  entry.writeUInt32LE(6 + 16, 12);
  return Buffer.concat([header, entry, png]);
}

const outDir = path.join(__dirname, '..', 'assets');
fs.mkdirSync(outDir, { recursive: true });
const png = makePng();
fs.writeFileSync(path.join(outDir, 'icon.png'), png);
fs.writeFileSync(path.join(outDir, 'icon.ico'), makeIco(png));
console.log('wrote assets/icon.png and assets/icon.ico');
