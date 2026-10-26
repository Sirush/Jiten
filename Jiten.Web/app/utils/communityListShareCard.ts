import { drawCoverImage, fitCanvasText, type ExportPalette } from '~/utils/imageExport';

export interface CommunityListShareCardOptions {
  palette: ExportPalette;
  logo: ImageBitmap | null;
  name: string;
  byline: string;
  genres: string[];
  items: { title: string; cover: ImageBitmap | null }[];
  url: string;
  footRight: string;
}

// Logical CSS px; the canvas is drawn at SCALE for a 1200 px wide export.
const SCALE = 2;
const W = 600;
const PAD = 28;
const COLS = 5;
const COL_GAP = 12;
const ROW_GAP = 14;
const COVER_RATIO = 1.4;
const TITLE_H = 21;
const GRID_TOP = 146;
const FOOT_H = 52;
const FONT = '"Noto Sans JP", sans-serif';

/** Top-of-list share card: header, a 5-wide cover grid with rank badges, and the list URL. */
export function drawCommunityListShareCard(options: CommunityListShareCardOptions): HTMLCanvasElement {
  const { palette: pal, items } = options;
  const colW = (W - PAD * 2 - COL_GAP * (COLS - 1)) / COLS;
  const coverH = Math.round(colW * COVER_RATIO);
  const rows = Math.max(1, Math.ceil(items.length / COLS));
  const rowH = coverH + TITLE_H;
  const H = GRID_TOP + rows * rowH + (rows - 1) * ROW_GAP + FOOT_H;

  const canvas = document.createElement('canvas');
  canvas.width = W * SCALE;
  canvas.height = H * SCALE;
  const ctx = canvas.getContext('2d')!;
  ctx.scale(SCALE, SCALE);
  ctx.textBaseline = 'alphabetic';
  ctx.imageSmoothingQuality = 'high';
  const letterSpacing = (v: number) => {
    (ctx as CanvasRenderingContext2D & { letterSpacing: string }).letterSpacing = `${v}px`;
  };

  ctx.fillStyle = pal.bg;
  ctx.fillRect(0, 0, W, H);

  ctx.font = `700 11px ${FONT}`;
  ctx.fillStyle = pal.brand;
  letterSpacing(1.5);
  ctx.fillText('COMMUNITY LIST', PAD, PAD + 8);
  letterSpacing(0);

  const brand = 'jiten.moe';
  ctx.font = `800 15px ${FONT}`;
  const brandW = ctx.measureText(brand).width;
  if (options.logo) drawCoverImage(ctx, options.logo, W - PAD - brandW - 26, PAD - 6, 18, 18, pal.brand, 4);
  ctx.textAlign = 'right';
  ctx.fillText(brand, W - PAD, PAD + 8);
  ctx.textAlign = 'left';

  ctx.font = `800 24px ${FONT}`;
  ctx.fillStyle = pal.text;
  ctx.fillText(fitCanvasText(ctx, options.name, W - PAD * 2), PAD, 78);

  ctx.font = `400 13px ${FONT}`;
  ctx.fillStyle = pal.sub;
  ctx.fillText(options.byline, PAD, 102);
  let chipX = PAD;
  const chipY = 112;
  ctx.font = `500 11px ${FONT}`;
  for (const genre of options.genres) {
    const textW = ctx.measureText(genre).width;
    ctx.fillStyle = pal.card;
    ctx.beginPath();
    ctx.roundRect(chipX, chipY, textW + 16, 20, 5);
    ctx.fill();
    ctx.fillStyle = pal.text;
    ctx.fillText(genre, chipX + 8, chipY + 14);
    chipX += textW + 22;
  }

  items.forEach((item, index) => {
    const col = index % COLS;
    const row = Math.floor(index / COLS);
    const x = PAD + col * (colW + COL_GAP);
    const y = GRID_TOP + row * (rowH + ROW_GAP);

    drawCoverImage(ctx, item.cover, x, y, colW, coverH, pal.card, 6);

    const label = String(index + 1);
    ctx.font = `800 12px ${FONT}`;
    const badgeW = Math.max(22, ctx.measureText(label).width + 10);
    ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
    ctx.beginPath();
    ctx.roundRect(x + 6, y + 6, badgeW, 22, 11);
    ctx.fill();
    ctx.fillStyle = '#ffffff';
    ctx.textAlign = 'center';
    ctx.fillText(label, x + 6 + badgeW / 2, y + 21);
    ctx.textAlign = 'left';

    ctx.font = `500 12px ${FONT}`;
    ctx.fillStyle = pal.text;
    ctx.fillText(fitCanvasText(ctx, item.title, colW), x, y + coverH + 16);
  });

  ctx.font = `400 11px ${FONT}`;
  ctx.fillStyle = pal.sub;
  ctx.fillText(options.url, PAD, H - PAD + 6);
  ctx.textAlign = 'right';
  ctx.fillText(options.footRight, W - PAD, H - PAD + 6);
  ctx.textAlign = 'left';

  return canvas;
}
