// One-off local generator for the status-dot raster icons — run manually, not
// part of `npm run check` or CI. Uses only Node's built-in zlib, so it adds no
// dependency: a filled rounded square with a status-green circle, matching
// favicon.svg, encoded as a minimal PNG by hand (IHDR/IDAT/IEND, filter 0,
// RGBA, no palette).
import { deflateSync } from "node:zlib";
import { writeFileSync } from "node:fs";

const BG = [0x0b, 0x0f, 0x14, 0xff];
const DOT = [0x22, 0xc5, 0x5e, 0xff];
const TRANSPARENT = [0x0b, 0x0f, 0x14, 0x00];

function crc32(buf) {
  let c;
  const table = crc32.table ?? (crc32.table = makeTable());
  let crc = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    c = table[(crc ^ buf[i]) & 0xff];
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function makeTable() {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
}

function chunk(type, data) {
  const typeBuf = Buffer.from(type, "ascii");
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

function png(size, { rounded = true, transparent = false } = {}) {
  const raw = Buffer.alloc(size * (1 + size * 4));
  const cx = (size - 1) / 2;
  const cy = (size - 1) / 2;
  const dotR = size * 0.22;
  const cornerR = rounded ? size * 0.19 : 0;
  for (let y = 0; y < size; y++) {
    const rowStart = y * (1 + size * 4);
    raw[rowStart] = 0; // filter type: none
    for (let x = 0; x < size; x++) {
      const px = rowStart + 1 + x * 4;
      const dx = x - cx;
      const dy = y - cy;
      let color;
      if (Math.hypot(dx, dy) <= dotR) {
        color = DOT;
      } else if (
        rounded &&
        (x < cornerR || x >= size - cornerR) &&
        (y < cornerR || y >= size - cornerR)
      ) {
        const cornerCx = x < cornerR ? cornerR : size - cornerR - 1;
        const cornerCy = y < cornerR ? cornerR : size - cornerR - 1;
        color =
          Math.hypot(x - cornerCx, y - cornerCy) <= cornerR
            ? BG
            : transparent
              ? TRANSPARENT
              : BG;
      } else {
        color = transparent ? BG : BG;
      }
      raw[px] = color[0];
      raw[px + 1] = color[1];
      raw[px + 2] = color[2];
      raw[px + 3] = color[3];
    }
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type: RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  return Buffer.concat([
    signature,
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

const outDir = new URL("../public/", import.meta.url);
writeFileSync(new URL("favicon-16x16.png", outDir), png(16));
writeFileSync(new URL("favicon-32x32.png", outDir), png(32));
writeFileSync(new URL("apple-touch-icon.png", outDir), png(180));

// og-image: a wide status banner — background plus one centred dot. Not a
// screenshot; the page's own hero section is the "only demo we have".
function ogImage(width, height) {
  const raw = Buffer.alloc(height * (1 + width * 4));
  const cx = width / 2;
  const cy = height / 2;
  const dotR = height * 0.14;
  for (let y = 0; y < height; y++) {
    const rowStart = y * (1 + width * 4);
    raw[rowStart] = 0;
    for (let x = 0; x < width; x++) {
      const px = rowStart + 1 + x * 4;
      const dx = x - cx;
      const dy = y - cy;
      const color = Math.hypot(dx, dy) <= dotR ? DOT : BG;
      raw[px] = color[0];
      raw[px + 1] = color[1];
      raw[px + 2] = color[2];
      raw[px + 3] = 0xff;
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  return Buffer.concat([
    signature,
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}
writeFileSync(new URL("og-image.png", outDir), ogImage(1200, 630));

console.log(
  "Generated favicon-16x16.png, favicon-32x32.png, apple-touch-icon.png, og-image.png",
);
