// Optional design tool. Normal Windows builds consume the checked-in ICO.
// Usage: node tools/Build-AppIcon.cjs [directory containing sharp] [preview directory]
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require(require.resolve('sharp', { paths: process.argv[2] ? [process.argv[2]] : [__dirname] }));
const root = path.resolve(__dirname, '..');
const assets = path.join(root, 'assets');
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

async function render(size) {
  const source = size <= 24 ? 'app-icon-small.svg' : size <= 32 ? 'app-icon-32.svg' : size <= 64 ? 'app-icon-compact.svg' : 'app-icon.svg';
  const svg = fs.readFileSync(path.join(assets, source));
  const scale = size <= 128 ? 4 : 2;
  const high = await sharp(svg, { density: Math.max(72, 72 * size * scale / 256) }).resize(size * scale, size * scale).png().toBuffer();
  const raw = await sharp(high).resize(size, size, { kernel: 'lanczos3' }).ensureAlpha().raw().toBuffer();
  for (let p = 0; p < raw.length; p += 4) if (raw[p + 3] < 2) raw.fill(0, p, p + 4);
  for (const p of [0, (size - 1) * 4, size * (size - 1) * 4, (size * size - 1) * 4]) raw.fill(0, p, p + 4);
  if (size >= 64) for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const p = (y * size + x) * 4;
    if (raw[p + 3] < 251 || raw[p] + raw[p + 1] + raw[p + 2] > 755) continue;
    let seed = (Math.imul(x + 1, 374761393) ^ Math.imul(y + 1, 668265263)) >>> 0;
    seed = Math.imul(seed ^ (seed >>> 13), 1274126177) >>> 0;
    const noise = (seed % 3) - 1;
    for (let c = 0; c < 3; c++) raw[p + c] = Math.max(0, Math.min(255, raw[p + c] + noise));
  }
  return { raw, png: await sharp(raw, { raw: { width: size, height: size, channels: 4 } }).png().toBuffer() };
}

function bitmap(raw, size) {
  const maskStride = Math.ceil(size / 32) * 4;
  const dib = Buffer.alloc(40 + size * size * 4 + maskStride * size);
  dib.writeUInt32LE(40, 0); dib.writeInt32LE(size, 4); dib.writeInt32LE(size * 2, 8);
  dib.writeUInt16LE(1, 12); dib.writeUInt16LE(32, 14); dib.writeUInt32LE(size * size * 4, 20);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const source = (y * size + x) * 4, target = 40 + ((size - y - 1) * size + x) * 4;
    dib[target] = raw[source + 2]; dib[target + 1] = raw[source + 1]; dib[target + 2] = raw[source]; dib[target + 3] = raw[source + 3];
    if (raw[source + 3] === 0) dib[40 + size * size * 4 + (size - y - 1) * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
  }
  return dib;
}

async function main() {
  const preview = process.argv[3] ? path.resolve(process.argv[3]) : null;
  if (preview) fs.mkdirSync(preview, { recursive: true });
  const frames = [];
  for (const size of sizes) {
    const { raw, png } = await render(size);
    if (preview) fs.writeFileSync(path.join(preview, `app-${size}.png`), png);
    frames.push({ size, dib: bitmap(raw, size) });
  }
  const directory = Buffer.alloc(6 + frames.length * 16);
  directory.writeUInt16LE(1, 2); directory.writeUInt16LE(frames.length, 4);
  let offset = directory.length;
  frames.forEach((frame, index) => {
    const entry = 6 + index * 16;
    directory[entry] = frame.size === 256 ? 0 : frame.size;
    directory[entry + 1] = directory[entry];
    directory.writeUInt16LE(1, entry + 4); directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(frame.dib.length, entry + 8); directory.writeUInt32LE(offset, entry + 12);
    offset += frame.dib.length;
  });
  const ico = Buffer.concat([directory, ...frames.map(frame => frame.dib)]);
  fs.writeFileSync(path.join(assets, 'app-icon.ico'), ico);
  const hero = await render(512);
  fs.mkdirSync(path.join(root, 'docs', 'images'), { recursive: true });
  fs.writeFileSync(path.join(root, 'docs', 'images', 'app-icon.png'), hero.png);
  const result = { ico: path.join(assets, 'app-icon.ico'), frames: sizes, bytes: ico.length, sha256: crypto.createHash('sha256').update(ico).digest('hex') };
  if (preview) fs.writeFileSync(path.join(preview, 'icon-build.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
