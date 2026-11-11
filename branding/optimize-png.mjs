// Re-encodes 8-bit RGB/RGBA PNGs losslessly and smaller: drops an alpha channel that is opaque everywhere, picks each
// row's filter by the minimum-sum-of-absolute-differences heuristic, and deflates at level 9. Only node's own zlib.
// Usage: node optimize-png.mjs <file.png>...   (each file is rewritten only when the result is smaller)
import { readFileSync, writeFileSync } from 'node:fs';
import { deflateSync, inflateSync, crc32 } from 'node:zlib';

const SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

function readChunks(buf) {
  if (!buf.subarray(0, 8).equals(SIGNATURE)) throw new Error('not a PNG');
  const chunks = [];
  for (let at = 8; at < buf.length;) {
    const length = buf.readUInt32BE(at);
    chunks.push({ type: buf.toString('latin1', at + 4, at + 8), data: buf.subarray(at + 8, at + 8 + length) });
    at += 12 + length;
  }
  return chunks;
}

function paeth(a, b, c) {
  const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

function unfilter(raw, width, height, bpp) {
  const stride = width * bpp, out = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const type = raw[y * (stride + 1)], src = y * (stride + 1) + 1, row = y * stride, prev = row - stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? out[row + x - bpp] : 0, b = y > 0 ? out[prev + x] : 0, c = x >= bpp && y > 0 ? out[prev + x - bpp] : 0;
      const v = raw[src + x];
      out[row + x] = (type === 0 ? v : type === 1 ? v + a : type === 2 ? v + b : type === 3 ? v + ((a + b) >> 1) : v + paeth(a, b, c)) & 255;
    }
  }
  return out;
}

function filter(pixels, width, height, bpp) {
  const stride = width * bpp, out = Buffer.alloc((stride + 1) * height), line = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const row = y * stride, prev = row - stride;
    let best = null, bestSum = Infinity;
    for (let type = 0; type < 5; type++) {
      let sum = 0;
      for (let x = 0; x < stride; x++) {
        const a = x >= bpp ? pixels[row + x - bpp] : 0, b = y > 0 ? pixels[prev + x] : 0, c = x >= bpp && y > 0 ? pixels[prev + x - bpp] : 0;
        const v = pixels[row + x];
        const f = (type === 0 ? v : type === 1 ? v - a : type === 2 ? v - b : type === 3 ? v - ((a + b) >> 1) : v - paeth(a, b, c)) & 255;
        line[x] = f;
        sum += f < 128 ? f : 256 - f;
      }
      if (sum < bestSum) { bestSum = sum; best = { type, data: Buffer.from(line) }; }
    }
    out[y * (stride + 1)] = best.type;
    best.data.copy(out, y * (stride + 1) + 1);
  }
  return out;
}

function chunk(type, data) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'latin1');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])) >>> 0, 0);
  return Buffer.concat([head, data, crc]);
}

for (const file of process.argv.slice(2)) {
  const before = readFileSync(file);
  const chunks = readChunks(before);
  const ihdr = chunks.find((c) => c.type === 'IHDR').data;
  const width = ihdr.readUInt32BE(0), height = ihdr.readUInt32BE(4), depth = ihdr[8], colour = ihdr[9];
  if (depth !== 8 || (colour !== 2 && colour !== 6) || ihdr[12] !== 0) { console.log(`${file}: skipped (not 8-bit RGB/RGBA, or interlaced)`); continue; }
  let bpp = colour === 6 ? 4 : 3;
  let pixels = unfilter(inflateSync(Buffer.concat(chunks.filter((c) => c.type === 'IDAT').map((c) => c.data))), width, height, bpp);
  let outColour = colour;
  if (colour === 6 && pixels.every((v, i) => i % 4 !== 3 || v === 255)) {
    const rgb = Buffer.alloc(width * height * 3);
    for (let i = 0, j = 0; i < pixels.length; i += 4, j += 3) { rgb[j] = pixels[i]; rgb[j + 1] = pixels[i + 1]; rgb[j + 2] = pixels[i + 2]; }
    pixels = rgb; bpp = 3; outColour = 2;
  }
  const header = Buffer.from(ihdr);
  header[9] = outColour;
  const idat = deflateSync(filter(pixels, width, height, bpp), { level: 9, memLevel: 9 });
  const after = Buffer.concat([SIGNATURE, chunk('IHDR', header), chunk('IDAT', idat), chunk('IEND', Buffer.alloc(0))]);
  if (after.length < before.length) {
    writeFileSync(file, after);
    console.log(`${file}: ${before.length} -> ${after.length} bytes`);
  } else {
    console.log(`${file}: ${before.length} bytes, kept (the re-encoding was ${after.length})`);
  }
}
