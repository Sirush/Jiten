import type { KnowledgeGrowth, ProfileVocabularyStats, StudyHeatmapResponse, UserAccomplishment } from '~/types';
import { drawCoverImage, fitCanvasText, type ExportPalette } from '~/utils/imageExport';
import { formatBucketDated } from '~/utils/journeyFormat';

const SCALE = 2;
const W = 640;
const PAD = 28;
const INNER = W - PAD * 2;
const FONT = '"Noto Sans JP", sans-serif';

const HEADER_H = 92;
const SECTION_GAP = 20;
const KICKER_H = 22;
const VOCAB_H = 116;
const GROWTH_CHART_H = 96;
const GROWTH_FOOT_H = 16;
const TILE_H = 62;
const TILE_GAP = 10;
const TILE_COLS = 3;
const STREAK_H = 26;
const MONTH_LABEL_H = 14;
const CELL = 9;
const CELL_GAP = 2;
const CELL_STEP = CELL + CELL_GAP;
const HEATMAP_H = 7 * CELL_STEP - CELL_GAP;
const FOOTER_H = 34;

interface AccentPalette {
  young: string;
  mature: string;
  mastered: string;
  streak: string;
  /** Heatmap ramp, from "no reviews" to the densest bucket. */
  heat: [string, string, string, string, string];
}

const LIGHT_ACCENTS: AccentPalette = {
  young: '#eab308',
  mature: '#22c55e',
  mastered: '#0d9488',
  streak: '#f97316',
  heat: ['#f3f4f6', '#e9d5ff', '#c084fc', '#a855f7', '#7e22ce'],
};

const DARK_ACCENTS: AccentPalette = {
  young: '#fde047',
  mature: '#4ade80',
  mastered: '#2dd4bf',
  streak: '#fb923c',
  heat: ['#1f2937', 'rgba(88, 28, 135, 0.6)', '#7e22ce', '#a855f7', '#c084fc'],
};

export interface ProfileShareCardOptions {
  username: string;
  vocabulary: ProfileVocabularyStats | null;
  growth: KnowledgeGrowth | null;
  /** The all-media accomplishment row; its six counters become the stat grid. */
  accomplishment: UserAccomplishment | null;
  heatmap: StudyHeatmapResponse | null;
  logo: ImageBitmap | null;
  palette: ExportPalette;
  isDark: boolean;
}

const num = (n: number) => n.toLocaleString();

function knownWordTotal(v: ProfileVocabularyStats) {
  return v.young + v.mature + v.mastered;
}

function heatmapWeeks(heatmap: StudyHeatmapResponse): number[][] {
  const counts = new Map(heatmap.days.map((d) => [d.date, d.reviewCount]));
  const year = heatmap.year;
  const jan1 = new Date(year, 0, 1);
  const startDay = jan1.getDay();
  const cur = new Date(year, 0, 1 + (startDay === 0 ? -6 : 1 - startDay));
  const dec31 = new Date(year, 11, 31);

  const weeks: number[][] = [];
  while (cur <= dec31) {
    const week: number[] = [];
    for (let d = 0; d < 7; d++) {
      const iso = `${cur.getFullYear()}-${String(cur.getMonth() + 1).padStart(2, '0')}-${String(cur.getDate()).padStart(2, '0')}`;
      // -1 marks a padding day outside the year so it can be left blank.
      week.push(cur.getFullYear() === year ? (counts.get(iso) ?? 0) : -1);
      cur.setDate(cur.getDate() + 1);
    }
    weeks.push(week);
  }
  return weeks;
}

/** Quartiles of the non-empty days, matching the on-screen heatmap buckets. */
function heatmapThresholds(heatmap: StudyHeatmapResponse): [number, number, number] {
  const counts = heatmap.days
    .map((d) => d.reviewCount)
    .filter((c) => c > 0)
    .sort((a, b) => a - b);
  if (counts.length === 0) return [1, 2, 3];
  const quantile = (q: number) => {
    const pos = (counts.length - 1) * q;
    const base = Math.floor(pos);
    const lo = counts[base]!;
    return lo + ((counts[base + 1] ?? lo) - lo) * (pos - base);
  };
  return [quantile(0.25), quantile(0.5), quantile(0.75)];
}

export function drawProfileShareCard(options: ProfileShareCardOptions): HTMLCanvasElement {
  const pal = options.palette;
  const accents = options.isDark ? DARK_ACCENTS : LIGHT_ACCENTS;

  const vocab = options.vocabulary && knownWordTotal(options.vocabulary) > 0 ? options.vocabulary : null;
  const growth = options.growth?.hasEnoughHistory && options.growth.points.length > 1 ? options.growth : null;
  const acc = options.accomplishment;
  const heat = options.heatmap && options.heatmap.totalReviews > 0 ? options.heatmap : null;

  const tiles = acc
    ? [
        { label: 'Completed', value: acc.completedDeckCount },
        { label: 'Characters', value: acc.totalCharacterCount },
        { label: 'Words', value: acc.totalWordCount },
        { label: 'Unique words', value: acc.uniqueWordCount },
        { label: '1-occurrence', value: acc.uniqueWordUsedOnceCount },
        { label: 'Unique kanji', value: acc.uniqueKanjiCount },
      ]
    : [];

  const tileRows = Math.ceil(tiles.length / TILE_COLS);
  const weeks = heat ? heatmapWeeks(heat) : [];

  const sectionHeights = [
    vocab ? VOCAB_H : 0,
    growth ? KICKER_H + GROWTH_CHART_H + GROWTH_FOOT_H : 0,
    tiles.length ? KICKER_H + tileRows * TILE_H + (tileRows - 1) * TILE_GAP : 0,
    heat ? KICKER_H + STREAK_H + MONTH_LABEL_H + HEATMAP_H : 0,
  ].filter((h) => h > 0);

  const H = PAD + HEADER_H + sectionHeights.reduce((sum, h) => sum + h, 0) + sectionHeights.length * SECTION_GAP + FOOTER_H + PAD;

  const canvas = document.createElement('canvas');
  canvas.width = W * SCALE;
  canvas.height = Math.ceil(H) * SCALE;
  const ctx = canvas.getContext('2d')!;
  ctx.scale(SCALE, SCALE);
  ctx.textBaseline = 'alphabetic';
  ctx.imageSmoothingQuality = 'high';

  const letterSpacing = (v: number) => {
    (ctx as CanvasRenderingContext2D & { letterSpacing: string }).letterSpacing = `${v}px`;
  };

  const kicker = (text: string, y: number) => {
    ctx.font = `700 11px ${FONT}`;
    ctx.fillStyle = pal.brand;
    letterSpacing(1.5);
    ctx.fillText(text, PAD, y);
    letterSpacing(0);
  };

  ctx.fillStyle = pal.bg;
  ctx.fillRect(0, 0, W, H);

  let y = PAD;

  ctx.font = `800 16px ${FONT}`;
  const brand = 'jiten.moe';
  const brandW = ctx.measureText(brand).width;
  const logoSize = 22;
  if (options.logo) drawCoverImage(ctx, options.logo, W - PAD - brandW - 8 - logoSize, y - 4, logoSize, logoSize, pal.brand);
  ctx.fillStyle = pal.brand;
  ctx.textAlign = 'right';
  ctx.fillText(brand, W - PAD, y + 12);
  ctx.textAlign = 'left';

  kicker('JAPANESE IMMERSION PROFILE', y + 12);

  ctx.font = `800 30px ${FONT}`;
  ctx.fillStyle = pal.text;
  ctx.fillText(fitCanvasText(ctx, options.username, INNER), PAD, y + 52);

  ctx.font = `500 13px ${FONT}`;
  ctx.fillStyle = pal.sub;
  ctx.fillText(new Date().toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' }), PAD, y + 74);
  y += HEADER_H;

  if (vocab) {
    const known = knownWordTotal(vocab);
    ctx.beginPath();
    ctx.roundRect(PAD, y, INNER, VOCAB_H, 12);
    ctx.fillStyle = pal.card;
    ctx.fill();

    const innerX = PAD + 20;
    const innerW = INNER - 40;

    ctx.font = `700 11px ${FONT}`;
    ctx.fillStyle = pal.brand;
    letterSpacing(1.5);
    ctx.fillText('VOCABULARY', innerX, y + 24);
    letterSpacing(0);

    ctx.font = `800 32px ${FONT}`;
    ctx.fillStyle = pal.text;
    ctx.fillText(num(known), innerX, y + 58);
    const knownW = ctx.measureText(num(known)).width;
    ctx.font = `600 14px ${FONT}`;
    ctx.fillStyle = pal.sub;
    ctx.fillText(known === 1 ? 'word known' : 'words known', innerX + knownW + 10, y + 58);

    const segments = [
      { label: 'Young', count: vocab.young, color: accents.young },
      { label: 'Mature', count: vocab.mature, color: accents.mature },
      { label: 'Mastered', count: vocab.mastered, color: accents.mastered },
    ].filter((s) => s.count > 0);

    const barY = y + 72;
    const barH = 10;
    ctx.save();
    ctx.beginPath();
    ctx.roundRect(innerX, barY, innerW, barH, barH / 2);
    ctx.fillStyle = pal.bg;
    ctx.fill();
    ctx.clip();
    let barX = innerX;
    for (const segment of segments) {
      const segW = Math.max(3, (segment.count / known) * innerW);
      ctx.fillStyle = segment.color;
      ctx.fillRect(barX, barY, segW, barH);
      barX += segW;
    }
    ctx.restore();

    let legendX = innerX;
    const legendY = y + 100;
    for (const segment of segments) {
      ctx.beginPath();
      ctx.arc(legendX + 4, legendY - 4, 4, 0, Math.PI * 2);
      ctx.fillStyle = segment.color;
      ctx.fill();

      ctx.font = `500 12px ${FONT}`;
      ctx.fillStyle = pal.sub;
      ctx.fillText(segment.label, legendX + 14, legendY);
      const labelW = ctx.measureText(segment.label).width;

      ctx.font = `700 12px ${FONT}`;
      ctx.fillStyle = pal.text;
      ctx.fillText(num(segment.count), legendX + 20 + labelW, legendY);
      legendX += 20 + labelW + ctx.measureText(num(segment.count)).width + 22;
    }

    if (vocab.wordSetMastered > 0) {
      ctx.font = `500 11px ${FONT}`;
      ctx.fillStyle = pal.foot;
      ctx.textAlign = 'right';
      ctx.fillText(`+ ${num(vocab.wordSetMastered)} mastered from word sets`, PAD + INNER - 20, legendY);
      ctx.textAlign = 'left';
    }

    y += VOCAB_H + SECTION_GAP;
  }

  if (growth) {
    const points = growth.points;
    const known = points.map((p) => p.knownWords);
    const combined = points.map((p) => p.knownWordsCombined);
    const startLabel = formatBucketDated(points[0]!.date, growth.granularity);
    const gained = (known[known.length - 1] ?? 0) - (known[0] ?? 0);

    kicker('WORDS LEARNED OVER TIME', y + 10);
    if (gained > 0) {
      ctx.font = `700 12px ${FONT}`;
      ctx.fillStyle = pal.line;
      ctx.textAlign = 'right';
      ctx.fillText(`+${num(gained)} since ${startLabel}`, PAD + INNER, y + 10);
      ctx.textAlign = 'left';
    }
    y += KICKER_H;

    const max = Math.max(...combined, 1);
    const chartBottom = y + GROWTH_CHART_H;
    const xAt = (i: number) => PAD + (i / (points.length - 1)) * INNER;
    const yAt = (value: number) => chartBottom - (Math.min(value, max) / max) * GROWTH_CHART_H;

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
    ctx.moveTo(PAD, chartBottom);
    ctx.lineTo(PAD + INNER, chartBottom);
    ctx.stroke();

    area(combined, pal.band);
    area(known, pal.fill);

    ctx.beginPath();
    known.forEach((v, i) => (i === 0 ? ctx.moveTo(xAt(i), yAt(v)) : ctx.lineTo(xAt(i), yAt(v))));
    ctx.strokeStyle = pal.line;
    ctx.lineWidth = 2.5;
    ctx.lineJoin = 'round';
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(xAt(known.length - 1), yAt(known[known.length - 1] ?? 0), 4, 0, Math.PI * 2);
    ctx.fillStyle = pal.line;
    ctx.fill();

    ctx.font = `400 11px ${FONT}`;
    ctx.fillStyle = pal.foot;
    ctx.fillText(startLabel, PAD, chartBottom + 14);
    ctx.textAlign = 'right';
    ctx.fillText('Today', PAD + INNER, chartBottom + 14);
    ctx.textAlign = 'left';

    y += GROWTH_CHART_H + GROWTH_FOOT_H + SECTION_GAP;
  }

  if (tiles.length) {
    kicker('TOTALS ACROSS ALL MEDIA', y + 10);
    y += KICKER_H;

    const tileW = (INNER - TILE_GAP * (TILE_COLS - 1)) / TILE_COLS;
    tiles.forEach((tile, i) => {
      const tx = PAD + (i % TILE_COLS) * (tileW + TILE_GAP);
      const ty = y + Math.floor(i / TILE_COLS) * (TILE_H + TILE_GAP);

      ctx.beginPath();
      ctx.roundRect(tx, ty, tileW, TILE_H, 10);
      ctx.fillStyle = pal.card;
      ctx.fill();

      ctx.textAlign = 'center';
      ctx.font = `800 21px ${FONT}`;
      ctx.fillStyle = pal.brand;
      ctx.fillText(fitCanvasText(ctx, num(tile.value), tileW - 16), tx + tileW / 2, ty + 32);

      ctx.font = `500 11px ${FONT}`;
      ctx.fillStyle = pal.sub;
      ctx.fillText(tile.label, tx + tileW / 2, ty + 50);
      ctx.textAlign = 'left';
    });

    y += tileRows * TILE_H + (tileRows - 1) * TILE_GAP + SECTION_GAP;
  }

  if (heat) {
    kicker(`STUDY ACTIVITY ${heat.year}`, y + 10);
    y += KICKER_H;

    ctx.font = `800 17px ${FONT}`;
    ctx.fillStyle = accents.streak;
    ctx.fillText(num(heat.currentStreak), PAD, y + 16);
    let statX = PAD + ctx.measureText(num(heat.currentStreak)).width + 6;

    const facts: [string, string][] = [
      ['day streak', ''],
      ['Longest', `${num(heat.longestStreak)} days`],
      ['Days studied', num(heat.totalReviewDays)],
      ['Reviews', num(heat.totalReviews)],
    ];
    facts.forEach(([label, value], i) => {
      if (i > 0) {
        ctx.font = `400 13px ${FONT}`;
        ctx.fillStyle = pal.foot;
        ctx.fillText('·', statX, y + 16);
        statX += ctx.measureText('·').width + 10;
      }
      ctx.font = `500 13px ${FONT}`;
      ctx.fillStyle = pal.sub;
      ctx.fillText(label, statX, y + 16);
      statX += ctx.measureText(label).width;
      if (value) {
        ctx.font = `700 13px ${FONT}`;
        ctx.fillStyle = pal.text;
        ctx.fillText(` ${value}`, statX, y + 16);
        statX += ctx.measureText(` ${value}`).width;
      }
      statX += 10;
    });
    y += STREAK_H;

    const gridW = weeks.length * CELL_STEP - CELL_GAP;
    const gridX = PAD + Math.max(0, (INNER - gridW) / 2);
    const thresholds = heatmapThresholds(heat);
    const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    ctx.font = `500 9px ${FONT}`;
    ctx.fillStyle = pal.foot;
    let lastMonth = -1;
    weeks.forEach((week, w) => {
      const firstInYear = week.findIndex((c) => c >= 0);
      if (firstInYear === -1) return;
      const dayOfYear = new Date(heat.year, 0, 1);
      dayOfYear.setDate(dayOfYear.getDate() + w * 7 + firstInYear - dayOfYearOffset(heat.year));
      const month = dayOfYear.getMonth();
      if (month !== lastMonth) {
        ctx.fillText(monthNames[month]!, gridX + w * CELL_STEP, y + 9);
        lastMonth = month;
      }
    });
    y += MONTH_LABEL_H;

    weeks.forEach((week, w) => {
      week.forEach((count, d) => {
        if (count < 0) return;
        ctx.beginPath();
        ctx.roundRect(gridX + w * CELL_STEP, y + d * CELL_STEP, CELL, CELL, 2);
        ctx.fillStyle =
          count <= 0
            ? accents.heat[0]
            : count < thresholds[0]
              ? accents.heat[1]
              : count < thresholds[1]
                ? accents.heat[2]
                : count < thresholds[2]
                  ? accents.heat[3]
                  : accents.heat[4];
        ctx.fill();
      });
    });
    y += HEATMAP_H + SECTION_GAP;
  }

  ctx.font = `500 12px ${FONT}`;
  ctx.fillStyle = pal.foot;
  ctx.textAlign = 'center';
  ctx.fillText(`jiten.moe/profile/${options.username}`, W / 2, H - PAD - 6);
  ctx.textAlign = 'left';

  return canvas;
}

/** Days between the grid's Monday origin and Jan 1, so a (week, weekday) pair maps back to a date. */
function dayOfYearOffset(year: number): number {
  const startDay = new Date(year, 0, 1).getDay();
  return startDay === 0 ? 6 : startDay - 1;
}
