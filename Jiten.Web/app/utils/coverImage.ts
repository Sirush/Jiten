// Canvas helpers for the admin cover tools (generate / rotate).
// Pure browser functions, no Nuxt context — callers resolve a Blob/File first
// (remote URLs go through the admin/proxy-image endpoint to avoid canvas tainting).

export const COVER_WIDTH = 400;
export const COVER_HEIGHT = 600; // 2:3 portrait, matches the display slot

const TITLE_FONT_STACK = '"Noto Sans JP Variable", "Noto Sans JP", sans-serif';

export type BackgroundStyle = 'gradient' | 'solid' | 'blurred';
export type VAlign = 'top' | 'center' | 'bottom';
export type HAlign = 'left' | 'center' | 'right';
export type Orientation = 'horizontal' | 'vertical';

export interface CoverPalette {
  dominant: string;
  accent: string;
  isDark: boolean;
  textColor: string;
  swatches: string[];
}

export interface CoverOptions {
  title: string;
  subtitle: string;
  style: BackgroundStyle;
  bgColor1: string;
  bgColor2: string;
  gradientAngle: number;
  blurAmount: number;
  scrimDarkness: number;
  textColor: string;
  autoFontSize: boolean;
  fontSize: number;
  fontWeight: number;
  vAlign: VAlign;
  hAlign: HAlign;
  orientation: Orientation;
  showDividers: boolean;
}

// ---------------------------------------------------------------------------
// Image loading / encoding
// ---------------------------------------------------------------------------

/** Load a Blob/File into a decoded HTMLImageElement. */
export function loadImage(source: Blob): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(source);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(img);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error('Failed to decode image'));
    };
    img.src = url;
  });
}

/** Ensure the Japanese title font is ready before drawing text to a canvas. */
export async function ensureTitleFont(): Promise<void> {
  if (typeof document === 'undefined' || !document.fonts) return;
  try {
    await Promise.all([
      document.fonts.load('700 64px "Noto Sans JP Variable"'),
      document.fonts.load('400 64px "Noto Sans JP Variable"'),
      document.fonts.load('700 64px "Noto Sans JP"'),
    ]);
    await document.fonts.ready;
  } catch {
    // Font loading is best-effort; fall back to the stack if it fails.
  }
}

/** Encode a canvas to a JPEG File for the existing cover upload path. */
export function canvasToFile(canvas: HTMLCanvasElement, name = 'cover.jpg'): Promise<File> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => {
        if (!blob) {
          reject(new Error('Failed to encode cover image'));
          return;
        }
        resolve(new File([blob], name, { type: 'image/jpeg' }));
      },
      'image/jpeg',
      0.92
    );
  });
}

/** Rotate an image by 90/180/270 degrees, returning a new canvas. */
export function rotateImage(img: HTMLImageElement, degrees: 90 | 180 | 270): HTMLCanvasElement {
  const iw = img.naturalWidth;
  const ih = img.naturalHeight;
  const swap = degrees === 90 || degrees === 270;
  const canvas = document.createElement('canvas');
  canvas.width = swap ? ih : iw;
  canvas.height = swap ? iw : ih;
  const ctx = canvas.getContext('2d')!;

  ctx.translate(canvas.width / 2, canvas.height / 2);
  ctx.rotate((degrees * Math.PI) / 180);
  ctx.drawImage(img, -iw / 2, -ih / 2);

  return canvas;
}

// ---------------------------------------------------------------------------
// Color helpers
// ---------------------------------------------------------------------------

function toHex(r: number, g: number, b: number): string {
  const h = (n: number) =>
    Math.max(0, Math.min(255, Math.round(n)))
      .toString(16)
      .padStart(2, '0');
  return `#${h(r)}${h(g)}${h(b)}`;
}

function hexToRgb(hex: string): { r: number; g: number; b: number } {
  const m = /^#?([\da-f]{2})([\da-f]{2})([\da-f]{2})$/i.exec(hex.trim());
  if (!m) return { r: 0, g: 0, b: 0 };
  return { r: parseInt(m[1], 16), g: parseInt(m[2], 16), b: parseInt(m[3], 16) };
}

function luminance(r: number, g: number, b: number): number {
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
}

/** Pick black or white text for best contrast against a background color. */
export function contrastText(hex: string): string {
  const { r, g, b } = hexToRgb(hex);
  return luminance(r, g, b) < 0.5 ? '#ffffff' : '#111111';
}

// ---------------------------------------------------------------------------
// Palette extraction
// ---------------------------------------------------------------------------

/** Extract dominant/accent/swatches from an image via downscaled histogram quantization. */
export function extractPalette(img: HTMLImageElement): CoverPalette {
  const sample = document.createElement('canvas');
  const scale = Math.min(1, 80 / Math.max(img.naturalWidth || 1, img.naturalHeight || 1));
  sample.width = Math.max(1, Math.round((img.naturalWidth || 1) * scale));
  sample.height = Math.max(1, Math.round((img.naturalHeight || 1) * scale));
  const ctx = sample.getContext('2d', { willReadFrequently: true })!;
  ctx.drawImage(img, 0, 0, sample.width, sample.height);

  const { data } = ctx.getImageData(0, 0, sample.width, sample.height);
  const buckets = new Map<number, { count: number; r: number; g: number; b: number }>();

  for (let i = 0; i < data.length; i += 4) {
    if (data[i + 3] < 128) continue;
    const r = data[i];
    const g = data[i + 1];
    const b = data[i + 2];
    const key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
    const bucket = buckets.get(key);
    if (bucket) {
      bucket.count++;
      bucket.r += r;
      bucket.g += g;
      bucket.b += b;
    } else {
      buckets.set(key, { count: 1, r, g, b });
    }
  }

  const sorted = [...buckets.values()].sort((a, b) => b.count - a.count);
  if (sorted.length === 0) {
    return { dominant: '#1f2937', accent: '#111827', isDark: true, textColor: '#ffffff', swatches: ['#1f2937', '#111827'] };
  }

  const avg = (bucket: { count: number; r: number; g: number; b: number }) => ({
    r: Math.round(bucket.r / bucket.count),
    g: Math.round(bucket.g / bucket.count),
    b: Math.round(bucket.b / bucket.count),
  });

  // Build a list of visually-distinct swatches from the most populous buckets.
  const swatchRgb: Array<{ r: number; g: number; b: number }> = [];
  for (const bucket of sorted) {
    const c = avg(bucket);
    const distinct = swatchRgb.every((s) => Math.abs(s.r - c.r) + Math.abs(s.g - c.g) + Math.abs(s.b - c.b) > 60);
    if (distinct) swatchRgb.push(c);
    if (swatchRgb.length >= 6) break;
  }

  const dom = swatchRgb[0];
  const acc = swatchRgb[1] ?? { r: Math.round(dom.r * 0.55), g: Math.round(dom.g * 0.55), b: Math.round(dom.b * 0.55) };
  const lum = luminance((dom.r + acc.r) / 2, (dom.g + acc.g) / 2, (dom.b + acc.b) / 2);

  return {
    dominant: toHex(dom.r, dom.g, dom.b),
    accent: toHex(acc.r, acc.g, acc.b),
    isDark: lum < 0.5,
    textColor: lum < 0.5 ? '#ffffff' : '#111111',
    swatches: swatchRgb.map((c) => toHex(c.r, c.g, c.b)),
  };
}

/** Sensible default options seeded from a palette + the title/subtitle. */
export function defaultCoverOptions(palette: CoverPalette, title: string, subtitle = ''): CoverOptions {
  return {
    title,
    subtitle,
    style: 'gradient',
    bgColor1: palette.dominant,
    bgColor2: palette.accent,
    gradientAngle: 145,
    blurAmount: 14,
    scrimDarkness: 0.55,
    textColor: palette.textColor,
    autoFontSize: true,
    fontSize: 48,
    fontWeight: 700,
    vAlign: 'center',
    hAlign: 'center',
    orientation: 'horizontal',
    showDividers: true,
  };
}

function randItem<T>(arr: T[]): T {
  return arr[Math.floor(Math.random() * arr.length)];
}

/** Re-roll palette pick + layout for quick variants. */
export function shuffleOptions(_prev: CoverOptions, palette: CoverPalette): Partial<CoverOptions> {
  const swatches = palette.swatches.length >= 2 ? palette.swatches : [palette.dominant, palette.accent];
  const c1 = randItem(swatches);
  let c2 = randItem(swatches);
  if (c2 === c1 && swatches.length > 1) c2 = swatches[(swatches.indexOf(c1) + 1) % swatches.length];

  return {
    bgColor1: c1,
    bgColor2: c2,
    gradientAngle: Math.floor(Math.random() * 360),
    hAlign: randItem<HAlign>(['left', 'center', 'right']),
    vAlign: randItem<VAlign>(['top', 'center', 'bottom']),
    textColor: contrastText(c1),
  };
}

// ---------------------------------------------------------------------------
// Text layout
// ---------------------------------------------------------------------------

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

// Split a string into wrap tokens: Latin/number runs stay atomic, CJK and other
// characters break individually, spaces are explicit break opportunities.
function tokenize(text: string): string[] {
  return text.match(/[A-Za-z0-9]+|\s+|[^A-Za-z0-9\s]/g) ?? [text];
}

function wrapSegment(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string[] {
  const tokens = tokenize(text);
  const lines: string[] = [];
  let current = '';
  for (const token of tokens) {
    const candidate = current + token;
    if (ctx.measureText(candidate).width > maxWidth && current.trim() !== '') {
      lines.push(current.trim());
      current = /^\s+$/.test(token) ? '' : token;
    } else {
      current = candidate;
    }
  }
  if (current.trim() !== '') lines.push(current.trim());
  return lines;
}

// Wrap text honoring explicit newlines, then width.
function wrapMulti(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string[] {
  return text
    .split('\n')
    .flatMap((seg) => (seg.trim() === '' ? [''] : wrapSegment(ctx, seg, maxWidth)))
    .filter((l, i, arr) => !(l === '' && (i === 0 || i === arr.length - 1)));
}

function applyTextShadow(ctx: CanvasRenderingContext2D, on: boolean) {
  if (on) {
    ctx.shadowColor = 'rgba(0, 0, 0, 0.55)';
    ctx.shadowBlur = 8;
    ctx.shadowOffsetY = 2;
  } else {
    ctx.shadowColor = 'transparent';
    ctx.shadowBlur = 0;
    ctx.shadowOffsetY = 0;
  }
}

const LINE_GAP = 1.25;
const SUB_RATIO = 0.55;

function drawHorizontalText(ctx: CanvasRenderingContext2D, o: CoverOptions, box: Box, shadow: boolean) {
  const title = o.title.trim();
  const subtitle = o.subtitle.trim();
  if (!title && !subtitle) return;

  const layoutAt = (size: number) => {
    const subSize = Math.max(12, Math.round(size * SUB_RATIO));
    ctx.font = `${o.fontWeight} ${size}px ${TITLE_FONT_STACK}`;
    const tLines = title ? wrapMulti(ctx, title, box.width) : [];
    const tWidth = Math.max(0, ...tLines.map((l) => ctx.measureText(l).width));
    ctx.font = `${o.fontWeight} ${subSize}px ${TITLE_FONT_STACK}`;
    const sLines = subtitle ? wrapMulti(ctx, subtitle, box.width) : [];
    const sWidth = Math.max(0, ...sLines.map((l) => ctx.measureText(l).width));
    const titleH = tLines.length * size * LINE_GAP;
    const subH = sLines.length ? sLines.length * subSize * LINE_GAP + size * 0.4 : 0;
    return { size, subSize, tLines, sLines, tWidth, sWidth, totalH: titleH + subH };
  };

  let res = layoutAt(o.autoFontSize ? 64 : o.fontSize);
  if (o.autoFontSize) {
    for (let size = 64; size >= 14; size -= 2) {
      res = layoutAt(size);
      if (res.totalH <= box.height && res.tWidth <= box.width && res.sWidth <= box.width) break;
    }
  }

  const startY = o.vAlign === 'top' ? box.y : o.vAlign === 'bottom' ? box.y + box.height - res.totalH : box.y + (box.height - res.totalH) / 2;
  const tx = o.hAlign === 'left' ? box.x : o.hAlign === 'right' ? box.x + box.width : box.x + box.width / 2;

  ctx.textAlign = o.hAlign;
  ctx.textBaseline = 'top';
  ctx.fillStyle = o.textColor;
  applyTextShadow(ctx, shadow);

  let y = startY;
  ctx.font = `${o.fontWeight} ${res.size}px ${TITLE_FONT_STACK}`;
  for (const line of res.tLines) {
    ctx.fillText(line, tx, y);
    y += res.size * LINE_GAP;
  }
  if (res.sLines.length) {
    y += res.size * 0.4;
    ctx.font = `${o.fontWeight} ${res.subSize}px ${TITLE_FONT_STACK}`;
    for (const line of res.sLines) {
      ctx.fillText(line, tx, y);
      y += res.subSize * LINE_GAP;
    }
  }
  applyTextShadow(ctx, false);
}

// Characters that read better rotated 90° in vertical text.
function shouldRotateVertical(ch: string): boolean {
  return /[A-Za-z0-9]/.test(ch) || 'ー—–-~〜=＝…'.includes(ch);
}

function buildColumns(text: string, maxCells: number): string[][] {
  const cols: string[][] = [];
  let cur: string[] = [];
  for (const ch of Array.from(text)) {
    if (ch === '\n') {
      if (cur.length) cols.push(cur);
      cur = [];
      continue;
    }
    if (cur.length >= maxCells) {
      cols.push(cur);
      cur = [];
    }
    cur.push(ch);
  }
  if (cur.length) cols.push(cur);
  return cols;
}

function drawVChar(ctx: CanvasRenderingContext2D, ch: string, cx: number, cy: number) {
  if (shouldRotateVertical(ch)) {
    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate(Math.PI / 2);
    ctx.fillText(ch, 0, 0);
    ctx.restore();
  } else {
    ctx.fillText(ch, cx, cy);
  }
}

function drawVerticalText(ctx: CanvasRenderingContext2D, o: CoverOptions, box: Box, shadow: boolean) {
  const title = o.title.trim();
  const subtitle = o.subtitle.trim();
  if (!title && !subtitle) return;

  let size = o.autoFontSize ? 72 : o.fontSize;
  let cols: string[][] = [];
  let subCols: string[][] = [];

  const compute = (s: number) => {
    const cellH = s * 1.05;
    const colW = s * 1.18;
    const subSize = Math.max(12, Math.round(s * 0.6));
    const subCellH = subSize * 1.05;
    const subColW = subSize * 1.18;
    cols = title ? buildColumns(title, Math.max(1, Math.floor(box.height / cellH))) : [];
    subCols = subtitle ? buildColumns(subtitle, Math.max(1, Math.floor(box.height / subCellH))) : [];
    const totalW = cols.length * colW + (subCols.length ? subCols.length * subColW + s * 0.3 : 0);
    const titleH = Math.max(0, ...cols.map((c) => c.length)) * cellH;
    const subH = Math.max(0, ...subCols.map((c) => c.length)) * subCellH;
    return { cellH, colW, subSize, subCellH, subColW, totalW, maxH: Math.max(titleH, subH) };
  };

  let m = compute(size);
  if (o.autoFontSize) {
    for (size = 72; size >= 14; size -= 2) {
      m = compute(size);
      if (m.totalW <= box.width && m.maxH <= box.height) break;
    }
  }

  const blockRight = o.hAlign === 'right' ? box.x + box.width : o.hAlign === 'left' ? box.x + m.totalW : box.x + (box.width + m.totalW) / 2;

  const colTop = (n: number, cellH: number) => {
    const colH = n * cellH;
    return o.vAlign === 'top' ? box.y : o.vAlign === 'bottom' ? box.y + box.height - colH : box.y + (box.height - colH) / 2;
  };

  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillStyle = o.textColor;
  applyTextShadow(ctx, shadow);

  // Title columns, right-to-left.
  ctx.font = `${o.fontWeight} ${size}px ${TITLE_FONT_STACK}`;
  cols.forEach((col, i) => {
    const cx = blockRight - (i + 0.5) * m.colW;
    const top = colTop(col.length, m.cellH);
    col.forEach((ch, k) => drawVChar(ctx, ch, cx, top + (k + 0.5) * m.cellH));
  });

  // Subtitle columns, left of the title block.
  if (subCols.length) {
    const subRight = blockRight - cols.length * m.colW - size * 0.3;
    ctx.font = `${o.fontWeight} ${m.subSize}px ${TITLE_FONT_STACK}`;
    subCols.forEach((col, i) => {
      const cx = subRight - (i + 0.5) * m.subColW;
      const top = colTop(col.length, m.subCellH);
      col.forEach((ch, k) => drawVChar(ctx, ch, cx, top + (k + 0.5) * m.subCellH));
    });
  }

  applyTextShadow(ctx, false);
}

// ---------------------------------------------------------------------------
// Backgrounds + entry point
// ---------------------------------------------------------------------------

function drawCover(ctx: CanvasRenderingContext2D, img: HTMLImageElement, w: number, h: number) {
  const iw = img.naturalWidth || w;
  const ih = img.naturalHeight || h;
  const scale = Math.max(w / iw, h / ih);
  const dw = iw * scale;
  const dh = ih * scale;
  ctx.drawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
}

function paintBackground(ctx: CanvasRenderingContext2D, o: CoverOptions, img: HTMLImageElement | undefined, w: number, h: number) {
  if (o.style === 'blurred' && img) {
    ctx.filter = `blur(${o.blurAmount}px)`;
    drawCover(ctx, img, w, h);
    ctx.filter = 'none';
    const scrim = ctx.createLinearGradient(0, 0, 0, h);
    scrim.addColorStop(0, `rgba(0,0,0,${(o.scrimDarkness * 0.45).toFixed(3)})`);
    scrim.addColorStop(0.5, `rgba(0,0,0,${(o.scrimDarkness * 0.75).toFixed(3)})`);
    scrim.addColorStop(1, `rgba(0,0,0,${o.scrimDarkness.toFixed(3)})`);
    ctx.fillStyle = scrim;
    ctx.fillRect(0, 0, w, h);
    return;
  }

  if (o.style === 'solid') {
    ctx.fillStyle = o.bgColor1;
    ctx.fillRect(0, 0, w, h);
  } else {
    // gradient
    const rad = ((o.gradientAngle % 360) * Math.PI) / 180;
    const len = (Math.abs(w * Math.cos(rad)) + Math.abs(h * Math.sin(rad))) / 2;
    const cx = w / 2;
    const cy = h / 2;
    const dx = Math.cos(rad) * len;
    const dy = Math.sin(rad) * len;
    const grad = ctx.createLinearGradient(cx - dx, cy - dy, cx + dx, cy + dy);
    grad.addColorStop(0, o.bgColor1);
    grad.addColorStop(1, o.bgColor2);
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, w, h);
  }

  if (o.showDividers) {
    ctx.strokeStyle = o.textColor;
    ctx.globalAlpha = 0.35;
    ctx.lineWidth = 2;
    const margin = 40;
    ctx.beginPath();
    ctx.moveTo(margin, h * 0.3);
    ctx.lineTo(w - margin, h * 0.3);
    ctx.moveTo(margin, h * 0.7);
    ctx.lineTo(w - margin, h * 0.7);
    ctx.stroke();
    ctx.globalAlpha = 1;
  }
}

/** Render a cover from options (+ source image for the blurred style). */
export function renderCover(o: CoverOptions, img?: HTMLImageElement, w = COVER_WIDTH, h = COVER_HEIGHT): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d')!;

  paintBackground(ctx, o, img, w, h);

  const margin = 36;
  const box: Box = { x: margin, y: margin, width: w - margin * 2, height: h - margin * 2 };
  const shadow = o.style === 'blurred';
  if (o.orientation === 'vertical') drawVerticalText(ctx, o, box, shadow);
  else drawHorizontalText(ctx, o, box, shadow);

  return canvas;
}
