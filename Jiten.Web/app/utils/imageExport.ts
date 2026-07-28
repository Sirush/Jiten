// Canvas helpers shared by the "Export as image" features (difficulty ranking, immersion plans).

/** Cached bitmap loader for one export surface; failures resolve to null so a missing cover never aborts the export. */
export function createBitmapLoader(): (url: string) => Promise<ImageBitmap | null> {
  const cache = new Map<string, ImageBitmap | null>();
  // Fetch via CORS + decode through a blob so the export canvas is never tainted.
  return async (url: string) => {
    if (cache.has(url)) return cache.get(url)!;
    let bmp: ImageBitmap | null = null;
    try {
      const resp = await fetch(url, { mode: 'cors' });
      bmp = await createImageBitmap(await resp.blob());
    } catch {
      bmp = null;
    }
    cache.set(url, bmp);
    return bmp;
  };
}

/** Draw bmp into (x,y,w,h) with object-fit: cover, clipped to a rounded rect. */
export function drawCoverImage(
  ctx: CanvasRenderingContext2D,
  bmp: ImageBitmap | null,
  x: number,
  y: number,
  w: number,
  h: number,
  fallback: string,
  radius = 4
) {
  ctx.save();
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, radius);
  ctx.clip();
  if (bmp) {
    const scale = Math.max(w / bmp.width, h / bmp.height);
    const sw = w / scale;
    const sh = h / scale;
    ctx.drawImage(bmp, (bmp.width - sw) / 2, (bmp.height - sh) / 2, sw, sh, x, y, w, h);
  } else {
    ctx.fillStyle = fallback;
    ctx.fillRect(x, y, w, h);
  }
  ctx.restore();
}

/** Truncate text with an ellipsis so it fits maxW in the current canvas font. */
export function fitCanvasText(ctx: CanvasRenderingContext2D, text: string, maxW: number): string {
  if (ctx.measureText(text).width <= maxW) return text;
  let lo = 0;
  let hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (ctx.measureText(text.slice(0, mid) + '…').width <= maxW) lo = mid;
    else hi = mid - 1;
  }
  return text.slice(0, lo) + '…';
}

export interface ExportPalette {
  bg: string;
  card: string;
  text: string;
  sub: string;
  foot: string;
  brand: string;
  line: string;
  fill: string;
  band: string;
}

const EXPORT_LIGHT: ExportPalette = {
  bg: '#ffffff',
  card: '#f4f5f7',
  text: '#1f2937',
  sub: '#6b7280',
  foot: '#9ca3af',
  brand: '#9333ea',
  line: '#d20ca3',
  fill: 'rgba(210, 12, 163, 0.30)',
  band: 'rgba(210, 12, 163, 0.10)',
};

const EXPORT_DARK: ExportPalette = {
  bg: '#18181b',
  card: '#27272a',
  text: '#e5e7eb',
  sub: '#a1a1aa',
  foot: '#71717a',
  brand: '#c084fc',
  line: '#f472d0',
  fill: 'rgba(244, 114, 208, 0.30)',
  band: 'rgba(244, 114, 208, 0.10)',
};

export function currentExportPalette(): ExportPalette {
  return document.documentElement.classList.contains('dark-mode') ? EXPORT_DARK : EXPORT_LIGHT;
}

// Logical CSS px; the canvas is scaled by EXPORT_SCALE, giving the 1200x630 OG-ratio output.
const EXPORT_SCALE = 2;
const EXPORT_W = 600;
const EXPORT_H = 315;
const EXPORT_PAD = 28;
const EXPORT_FONT = '"Noto Sans JP", sans-serif';
const COVER_W = 84;
const COVER_H = 118;

export interface SeriesCardOptions {
  palette: ExportPalette;
  logo: ImageBitmap | null;
  /** Small tracked-out label above the headline. */
  kicker: string;
  /** Drawn as a cover thumbnail plus title column; without it the headline starts at the left margin. */
  cover?: { bitmap: ImageBitmap | null; title: string };
  stat: string;
  statSuffix: string;
  subtitle: string;
  /** The emphasised series; `band` is the lighter one drawn behind it. */
  line: number[];
  band: number[];
  /** Value mapped to the top of the plot area. */
  max: number;
  footLeft: string;
  footRight: string;
}

/**
 * Renders the shared "stat + sparkline" share card. The curve is drawn rather than screenshotted,
 * so the export stays crisp at any scale.
 */
export function drawSeriesCard(options: SeriesCardOptions): HTMLCanvasElement {
  const { palette: pal, line, band, max } = options;

  const canvas = document.createElement('canvas');
  canvas.width = EXPORT_W * EXPORT_SCALE;
  canvas.height = EXPORT_H * EXPORT_SCALE;
  const ctx = canvas.getContext('2d')!;
  ctx.scale(EXPORT_SCALE, EXPORT_SCALE);
  ctx.textBaseline = 'alphabetic';
  ctx.imageSmoothingQuality = 'high';

  const letterSpacing = (v: number) => {
    (ctx as CanvasRenderingContext2D & { letterSpacing: string }).letterSpacing = `${v}px`;
  };

  ctx.fillStyle = pal.bg;
  ctx.fillRect(0, 0, EXPORT_W, EXPORT_H);

  const brand = 'jiten.moe';
  ctx.font = `800 15px ${EXPORT_FONT}`;
  const brandW = ctx.measureText(brand).width;
  if (options.logo) drawCoverImage(ctx, options.logo, EXPORT_W - EXPORT_PAD - brandW - 26, EXPORT_PAD - 6, 18, 18, pal.brand, 4);
  ctx.fillStyle = pal.brand;
  ctx.textAlign = 'right';
  ctx.fillText(brand, EXPORT_W - EXPORT_PAD, EXPORT_PAD + 8);
  ctx.textAlign = 'left';

  ctx.font = `700 11px ${EXPORT_FONT}`;
  ctx.fillStyle = pal.brand;
  letterSpacing(1.5);
  ctx.fillText(options.kicker, EXPORT_PAD, EXPORT_PAD + 8);
  letterSpacing(0);

  let textX = EXPORT_PAD;
  let statY = EXPORT_PAD + 62;
  let statSize = 46;
  let chartTop = EXPORT_PAD + 104;

  if (options.cover) {
    const coverY = EXPORT_PAD + 26;
    drawCoverImage(ctx, options.cover.bitmap, EXPORT_PAD, coverY, COVER_W, COVER_H, pal.card, 6);

    textX = EXPORT_PAD + COVER_W + 20;
    statY = coverY + 68;
    statSize = 36;
    chartTop = coverY + COVER_H + 22;

    ctx.font = `800 21px ${EXPORT_FONT}`;
    ctx.fillStyle = pal.text;
    ctx.fillText(fitCanvasText(ctx, options.cover.title, EXPORT_W - EXPORT_PAD - textX), textX, coverY + 22);
  }

  ctx.font = `800 ${statSize}px ${EXPORT_FONT}`;
  ctx.fillStyle = pal.line;
  ctx.fillText(options.stat, textX, statY);
  const statW = ctx.measureText(options.stat).width;
  ctx.font = `600 ${options.cover ? 15 : 16}px ${EXPORT_FONT}`;
  ctx.fillStyle = pal.sub;
  ctx.fillText(options.statSuffix, textX + statW + (options.cover ? 10 : 12), statY);

  ctx.font = `400 13px ${EXPORT_FONT}`;
  ctx.fillStyle = pal.sub;
  ctx.fillText(options.subtitle, textX, statY + 26);

  const chartX = EXPORT_PAD;
  const chartW = EXPORT_W - EXPORT_PAD * 2;
  const chartBottom = EXPORT_H - EXPORT_PAD - 18;
  const chartH = chartBottom - chartTop;

  const xAt = (i: number) => chartX + (line.length === 1 ? chartW / 2 : (i / (line.length - 1)) * chartW);
  const yAt = (value: number) => chartBottom - (Math.min(value, max) / max) * chartH;

  const area = (values: number[], fill: string) => {
    ctx.beginPath();
    ctx.moveTo(xAt(0), chartBottom);
    values.forEach((v, i) => ctx.lineTo(xAt(i), yAt(v)));
    ctx.lineTo(xAt(values.length - 1), chartBottom);
    ctx.closePath();
    ctx.fillStyle = fill;
    ctx.fill();
  };

  ctx.strokeStyle = pal.card;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(chartX, chartBottom);
  ctx.lineTo(chartX + chartW, chartBottom);
  ctx.stroke();

  area(band, pal.band);
  area(line, pal.fill);

  ctx.beginPath();
  line.forEach((v, i) => (i === 0 ? ctx.moveTo(xAt(i), yAt(v)) : ctx.lineTo(xAt(i), yAt(v))));
  ctx.strokeStyle = pal.line;
  ctx.lineWidth = 2.5;
  ctx.lineJoin = 'round';
  ctx.stroke();

  ctx.beginPath();
  ctx.arc(xAt(line.length - 1), yAt(line[line.length - 1] ?? 0), 4, 0, Math.PI * 2);
  ctx.fillStyle = pal.line;
  ctx.fill();

  ctx.font = `400 11px ${EXPORT_FONT}`;
  ctx.fillStyle = pal.foot;
  ctx.fillText(options.footLeft, chartX, EXPORT_H - EXPORT_PAD + 2);
  ctx.textAlign = 'right';
  ctx.fillText(options.footRight, chartX + chartW, EXPORT_H - EXPORT_PAD + 2);
  ctx.textAlign = 'left';

  return canvas;
}

/** Hand the finished canvas to the user: share sheet on mobile (desktops also expose Web Share but can't save from it), download elsewhere. */
export async function saveCanvasPng(canvas: HTMLCanvasElement, fileName: string, shareTitle: string) {
  const blob = await new Promise<Blob | null>((res) => canvas.toBlob(res, 'image/png'));
  if (!blob) throw new Error('Export produced an empty image');
  const file = new File([blob], fileName, { type: 'image/png' });

  const ua = navigator.userAgent;
  const isMobile =
    (navigator as Navigator & { userAgentData?: { mobile?: boolean } }).userAgentData?.mobile ??
    (/Android|iPhone|iPod/i.test(ua) || (/iPad|Macintosh/i.test(ua) && navigator.maxTouchPoints > 1));

  if (isMobile && navigator.canShare?.({ files: [file] })) {
    try {
      await navigator.share({ files: [file], title: shareTitle });
      return;
    } catch (err) {
      if ((err as Error)?.name === 'AbortError') return; // user dismissed the sheet
      // any other failure falls through to the download path
    }
  }

  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.download = fileName;
  link.href = url;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
