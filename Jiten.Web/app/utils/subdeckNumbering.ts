/**
 * Best-effort detection of the numbering scheme embedded in a batch of file names
 * (arabic incl. decimals/full-width, kanji, daiji, roman, circled, 上/中/下 ordinal sets),
 * used to prefill subdeck titles. Detection is a set operation: the winning number "slot"
 * is chosen by how index-like its values behave across the whole batch.
 */

export type NumberingKind = 'arabic' | 'kanji' | 'roman' | 'ordinal' | 'range';

export interface NumberDetection {
  value: number;
  /** Normalized text to use in a title: "7.5", "13", "1-3" */
  display: string;
  /** Matched token as it appears in the file name: "第七巻", "７．５", "Ⅶ" */
  raw: string;
  kind: NumberingKind;
  anchor: string | null;
  /** Span in the extension-stripped file name */
  start: number;
  end: number;
}

interface Candidate {
  value: number;
  display: string;
  kind: NumberingKind;
  anchor: string | null;
  nStart: number;
  nEnd: number;
  strength: number;
  penalty: number;
  ordinalToken?: string;
}

interface FileInfo {
  base: string;
  text: string;
  map: number[];
  candidates: Candidate[];
}

const LANG_CODES = new Set(['ja', 'jp', 'jpn', 'en', 'eng', 'und', 'ko', 'zh']);

export function stripFileExtension(name: string): string {
  // Never treat a trailing ".5" (decimal volume number) as an extension
  let base = name.replace(/\.(?!\d+$)[a-z0-9]{1,5}$/i, '');
  // Subtitle language double-extensions: "Show - 07.ja.srt"
  const lang = base.match(/\.([a-z]{2,3})$/i);
  if (lang && LANG_CODES.has(lang[1]!.toLowerCase())) base = base.slice(0, -lang[0].length);
  return base;
}

export function cleanFileName(name: string): string {
  return stripFileExtension(name).replace(/_+/g, ' ').replace(/\s+/g, ' ').trim();
}

/** NFKC-normalize (full-width digits, circled numbers, unicode roman numerals → ASCII/plain)
 *  keeping a normalized-index → original-index map so matches can be traced back. */
function nfkcWithMap(s: string): { text: string; map: number[] } {
  let text = '';
  const map: number[] = [];
  let idx = 0;
  for (const ch of s) {
    const n = ch.normalize('NFKC');
    for (let j = 0; j < n.length; j++) map.push(idx);
    text += n;
    idx += ch.length;
  }
  return { text, map };
}

const DAIJI: Record<string, string> = {
  壱: '一',
  壹: '一',
  弌: '一',
  弐: '二',
  貳: '二',
  貮: '二',
  参: '三',
  參: '三',
  肆: '四',
  伍: '五',
  陸: '六',
  漆: '七',
  柒: '七',
  捌: '八',
  玖: '九',
  拾: '十',
  什: '十',
  零: '〇',
};
const KANJI_DIGIT: Record<string, number> = { 〇: 0, 一: 1, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9 };
const KANJI_NUM_CHARS = '〇零一二三四五六七八九十百千壱壹弌弐貳貮参參肆伍陸漆柒捌玖拾什廿卅';

export function parseKanjiNumeral(s: string): number | null {
  let t = [...s].map((c) => DAIJI[c] ?? c).join('');
  t = t.replace(/廿/g, '二十').replace(/卅/g, '三十');
  const chars = [...t];
  if (chars.length === 0) return null;
  if (chars.every((c) => c in KANJI_DIGIT)) {
    // Positional digit string: 一九八四 → 1984, single 七 → 7
    return chars.reduce((acc, c) => acc * 10 + KANJI_DIGIT[c]!, 0);
  }
  let total = 0;
  let current = 0;
  for (const c of chars) {
    if (c in KANJI_DIGIT) current = current * 10 + KANJI_DIGIT[c]!;
    else if (c === '十') {
      total += (current || 1) * 10;
      current = 0;
    } else if (c === '百') {
      total += (current || 1) * 100;
      current = 0;
    } else if (c === '千') {
      total += (current || 1) * 1000;
      current = 0;
    } else return null;
  }
  const v = total + current;
  return v > 0 && v <= 9999 ? v : null;
}

function parseRoman(s: string): number | null {
  if (!/^X{0,3}(IX|IV|V?I{0,3})$/.test(s) || s.length === 0) return null;
  const vals: Record<string, number> = { I: 1, V: 5, X: 10 };
  let total = 0;
  for (let i = 0; i < s.length; i++) {
    const v = vals[s[i]!]!;
    const next = vals[s[i + 1] ?? ''] ?? 0;
    total += v < next ? -v : v;
  }
  return total > 0 ? total : null;
}

function formatValue(v: number): string {
  return String(v);
}

function parseNumberToken(tok: string): number | null {
  if (/^[\d.,]+$/.test(tok)) {
    const v = Number(tok.replace(',', '.'));
    return Number.isFinite(v) ? v : null;
  }
  return parseKanjiNumeral(tok);
}

const COUNTER = '(巻|話|章|回|部|集|期|幕|編|夜|冊|限|季)';
const NUM = `(\\d{1,4}(?:[.,]\\d)?|[${KANJI_NUM_CHARS}]{1,6})`;
const CJK_RE = /[㐀-鿿]/;
const CJK_OR_KANA_RE = /[㐀-鿿぀-ヿ]/;

function normalizeAnchor(word: string): string {
  const w = word.toLowerCase().replace(/s$/, '');
  if (w === 'v' || w.startsWith('vol')) return 'vol';
  if (w === 'e' || w.startsWith('ep')) return 'ep';
  if (w === 'chap' || w.startsWith('ch')) return 'ch';
  if (w === 'pt' || w === 'part') return 'part';
  if (w === 'disc' || w === 'disk' || w === 'cd') return 'disc';
  return w;
}

function extractCandidates(text: string): Candidate[] {
  const out: Candidate[] = [];
  const add = (c: Omit<Candidate, 'penalty'> & { penalty?: number }) => out.push({ penalty: 0, ...c });
  let m: RegExpExecArray | null;

  // SxxEyy — the episode slot is the candidate, whole match consumed so the season digits don't leak
  const sxe = /(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,3}(?:\.\d)?)(?=v\d|(?![a-z0-9]))/gi;
  while ((m = sxe.exec(text))) {
    const v = Number(m[2]);
    if (Number.isFinite(v))
      add({ value: v, display: formatValue(v), kind: 'arabic', anchor: 'sxxeyy', nStart: m.index, nEnd: m.index + m[0].length, strength: 5 });
  }

  // Ranges: "vol 1-3", "1-3巻"
  const rangeEn = /(?<![a-z])(vol(?:ume)?s?|ep(?:isode)?s?|ch(?:apter)?s?|chap|parts?|pts?)[.# ]*(\d{1,4})\s*[-〜~ー–]\s*(\d{1,4})(?!\d)/gi;
  while ((m = rangeEn.exec(text))) {
    add({
      value: Number(m[2]),
      display: `${Number(m[2])}-${Number(m[3])}`,
      kind: 'range',
      anchor: normalizeAnchor(m[1]!),
      nStart: m.index,
      nEnd: m.index + m[0].length,
      strength: 5,
    });
  }
  const rangeJp = new RegExp(`(\\d{1,4})\\s*[-〜~ー–]\\s*(\\d{1,4})\\s*${COUNTER}`, 'g');
  while ((m = rangeJp.exec(text))) {
    add({
      value: Number(m[1]),
      display: `${Number(m[1])}-${Number(m[2])}`,
      kind: 'range',
      anchor: `jp${m[3]}`,
      nStart: m.index,
      nEnd: m.index + m[0].length,
      strength: 5,
    });
  }

  // Japanese anchored: 第N巻 / N話 / 巻ノN / Nノ巻 / 其のN
  const jpPatterns: Array<{ re: RegExp; numGroup: number; anchorOf: (m: RegExpExecArray) => string; strength: number }> = [
    { re: new RegExp(`第\\s*${NUM}\\s*(?:${COUNTER})?`, 'g'), numGroup: 1, anchorOf: (mm) => `jp${mm[2] ?? '第'}`, strength: 5 },
    { re: new RegExp(`${NUM}\\s*${COUNTER}`, 'g'), numGroup: 1, anchorOf: (mm) => `jp${mm[2]}`, strength: 5 },
    { re: new RegExp(`(巻|話)\\s*ノ\\s*${NUM}`, 'g'), numGroup: 2, anchorOf: (mm) => `jp${mm[1]}`, strength: 5 },
    { re: new RegExp(`${NUM}\\s*ノ\\s*(巻|話)`, 'g'), numGroup: 1, anchorOf: (mm) => `jp${mm[2]}`, strength: 5 },
    { re: new RegExp(`其の?\\s*${NUM}`, 'g'), numGroup: 1, anchorOf: () => 'jp其', strength: 4 },
  ];
  for (const p of jpPatterns) {
    while ((m = p.re.exec(text))) {
      const v = parseNumberToken(m[p.numGroup]!);
      if (v === null) continue;
      const kind: NumberingKind = /^\d/.test(m[p.numGroup]!) ? 'arabic' : 'kanji';
      add({ value: v, display: formatValue(v), kind, anchor: p.anchorOf(m), nStart: m.index, nEnd: m.index + m[0].length, strength: p.strength });
    }
  }

  // English word anchors: vol/v/ep/e/ch/part/disc/track + number
  // A trailing "v2" release-version marker is allowed after the number (EP07v2)
  const enWord =
    /(?<![a-z])(vol(?:ume)?s?|ep(?:isode)?s?|ch(?:apter)?s?|chap|parts?|pts?|disc|disk|cd|track|v|e)[.# ]*(\d{1,4}(?:[.,]\d)?)(?=v\d|(?![0-9a-z]))/gi;
  while ((m = enWord.exec(text))) {
    const v = Number(m[2]!.replace(',', '.'));
    if (!Number.isFinite(v)) continue;
    const single = m[1]!.length === 1;
    add({
      value: v,
      display: formatValue(v),
      kind: 'arabic',
      anchor: normalizeAnchor(m[1]!),
      nStart: m.index,
      nEnd: m.index + m[0].length,
      strength: single ? 3 : 4,
    });
  }

  // "No.7" (dot/hash required — bare "no" is the romaji particle) and "#7"
  const noPat = /(?<![a-z])no\s*[.#]\s*(\d{1,4}(?:[.,]\d)?)(?![0-9a-z])/gi;
  while ((m = noPat.exec(text))) {
    const v = Number(m[1]!.replace(',', '.'));
    if (Number.isFinite(v)) add({ value: v, display: formatValue(v), kind: 'arabic', anchor: 'no', nStart: m.index, nEnd: m.index + m[0].length, strength: 3 });
  }
  const hashPat = /#\s*(\d{1,4}(?:\.\d)?)(?![0-9a-z])/g;
  while ((m = hashPat.exec(text))) {
    const v = Number(m[1]);
    if (Number.isFinite(v)) add({ value: v, display: formatValue(v), kind: 'arabic', anchor: '#', nStart: m.index, nEnd: m.index + m[0].length, strength: 3 });
  }

  // Roman numerals (uppercase whole tokens; Ⅶ became VII through NFKC)
  const romanRe = /(?<![A-Za-z])([IVX]{1,7})(?![A-Za-z])/g;
  while ((m = romanRe.exec(text))) {
    const v = parseRoman(m[1]!);
    if (v !== null) add({ value: v, display: formatValue(v), kind: 'roman', anchor: null, nStart: m.index, nEnd: m.index + m[0].length, strength: 2 });
  }

  // Bare kanji numerals, bounded by non-ideographs
  const kanjiRe = new RegExp(`[${KANJI_NUM_CHARS}]{1,6}`, 'g');
  while ((m = kanjiRe.exec(text))) {
    const before = text[m.index - 1];
    const after = text[m.index + m[0].length];
    if ((before && CJK_RE.test(before)) || (after && CJK_RE.test(after))) continue;
    const v = parseKanjiNumeral(m[0]);
    if (v !== null) add({ value: v, display: formatValue(v), kind: 'kanji', anchor: null, nStart: m.index, nEnd: m.index + m[0].length, strength: 2 });
  }

  // Ordinal tokens (resolved at set level): 上巻/中編/下, 前編/後編
  const ordSuffixed = /([上中下前後])(巻|編|篇)/g;
  while ((m = ordSuffixed.exec(text))) {
    add({ value: 0, display: m[0], kind: 'ordinal', anchor: null, nStart: m.index, nEnd: m.index + m[0].length, strength: 3, ordinalToken: m[1] });
  }
  const ordBare = /[上中下]/g;
  while ((m = ordBare.exec(text))) {
    const before = text[m.index - 1];
    const after = text[m.index + 1];
    if (after && CJK_OR_KANA_RE.test(after)) continue;
    if (before === '以') continue; // 以上/以下
    if (before && CJK_RE.test(before)) continue;
    add({ value: 0, display: m[0], kind: 'ordinal', anchor: null, nStart: m.index, nEnd: m.index + 1, strength: 2, ordinalToken: m[0] });
  }

  // Bare arabic numbers, with plausibility penalties
  const bare = /\d+(?:\.\d+)?/g;
  while ((m = bare.exec(text))) {
    const intPart = m[0].split('.')[0]!;
    if (intPart.length > 4) continue;
    const v = Number(m[0]);
    if (!Number.isFinite(v)) continue;
    add({
      value: v,
      display: formatValue(v),
      kind: 'arabic',
      anchor: null,
      nStart: m.index,
      nEnd: m.index + m[0].length,
      strength: 1,
      penalty: bareArabicPenalty(text, m.index, m.index + m[0].length, m[0], v),
    });
  }

  // Dedupe overlapping candidates: strongest, then longest, wins
  out.sort((a, b) => b.strength - a.strength || b.nEnd - b.nStart - (a.nEnd - a.nStart) || a.nStart - b.nStart);
  const kept: Candidate[] = [];
  for (const c of out) {
    if (!kept.some((k) => c.nStart < k.nEnd && k.nStart < c.nEnd)) kept.push(c);
  }
  return kept.sort((a, b) => a.nStart - b.nStart);
}

function bareArabicPenalty(text: string, start: number, end: number, token: string, value: number): number {
  // Part of a hex-looking token (CRC checksums in fansub names)
  let a = start;
  while (a > 0 && /[0-9a-z]/i.test(text[a - 1]!)) a--;
  let b = end;
  while (b < text.length && /[0-9a-z]/i.test(text[b]!)) b++;
  const surrounding = text.slice(a, b);
  if (surrounding.length >= 6 && /^[0-9a-f]+$/i.test(surrounding) && /[a-f]/i.test(surrounding)) return 3;
  const prev = text[start - 1]?.toLowerCase();
  if (prev === 'x' || prev === 'h') return 3; // x264 / h265
  if (/^(480|540|576|720|1080|1440|2160)$/.test(token)) {
    const next = text[end]?.toLowerCase();
    return next === 'p' || next === 'i' ? 3 : 2.5;
  }
  if (text.slice(end, end + 3).toLowerCase() === 'bit') return 2.5;
  if (token.length === 4 && Number.isInteger(value) && value >= 1900 && value <= 2099) return 2;
  return 0;
}

function commonPrefixLen(texts: string[]): number {
  let p = texts[0] ?? '';
  for (const t of texts) {
    let i = 0;
    while (i < p.length && i < t.length && p[i] === t[i]) i++;
    p = p.slice(0, i);
  }
  return p.length;
}

function commonSuffixLen(texts: string[], prefixLen: number): number {
  let s = texts[0] ?? '';
  for (const t of texts) {
    let i = 0;
    while (i < s.length && i < t.length && s[s.length - 1 - i] === t[t.length - 1 - i]) i++;
    s = s.slice(s.length - i);
  }
  let suffix = s.length;
  for (const t of texts) suffix = Math.min(suffix, t.length - prefixLen);
  return Math.max(0, suffix);
}

interface Slot {
  key: string;
  dPs: number[];
  perFile: (Candidate | null)[];
}

function avg(xs: number[]): number {
  return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : 0;
}

function finalize(c: Candidate, file: FileInfo): NumberDetection {
  const start = file.map[c.nStart] ?? 0;
  const end = c.nEnd < file.map.length ? file.map[c.nEnd]! : file.base.length;
  return { value: c.value, display: c.display, raw: file.base.slice(start, end), kind: c.kind, anchor: c.anchor, start, end };
}

export function detectNumbering(fileNames: string[]): (NumberDetection | null)[] {
  const n = fileNames.length;
  if (n === 0) return [];

  const files: FileInfo[] = fileNames.map((name) => {
    const base = stripFileExtension(name);
    const { text, map } = nfkcWithMap(base);
    return { base, text, map, candidates: extractCandidates(text) };
  });

  if (n === 1) {
    // A lone file has no set signal: only trust strongly anchored matches
    const best = files[0]!.candidates.filter((c) => c.kind !== 'ordinal' && c.strength >= 4).sort((a, b) => b.strength - a.strength || a.nStart - b.nStart)[0];
    return [best ? finalize(best, files[0]!) : null];
  }

  const texts = files.map((f) => f.text);
  const P = commonPrefixLen(texts);
  const S = commonSuffixLen(texts, P);

  // Group candidates into slots: anchored slots are position-free (grouped by anchor alone,
  // merging arabic/kanji/range), bare slots cluster by distance from the common prefix.
  const slots: Slot[] = [];
  files.forEach((f, fi) => {
    for (const c of f.candidates) {
      if (c.kind === 'ordinal') continue;
      const dP = c.nStart - P;
      let slot: Slot | undefined;
      if (c.anchor !== null) {
        slot = slots.find((s) => s.key === `a|${c.anchor}`);
        if (!slot) {
          slot = { key: `a|${c.anchor}`, dPs: [], perFile: new Array(n).fill(null) };
          slots.push(slot);
        }
      } else {
        slot = slots.find((s) => s.key.startsWith(`b|${c.kind}|`) && Math.abs(dP - avg(s.dPs)) <= 3);
        if (!slot) {
          slot = { key: `b|${c.kind}|${slots.length}`, dPs: [], perFile: new Array(n).fill(null) };
          slots.push(slot);
        }
      }
      const prev = slot.perFile[fi];
      if (!prev || c.strength > prev.strength) {
        slot.perFile[fi] = c;
        slot.dPs.push(dP);
      }
    }
  });

  let bestSlot: Slot | null = null;
  let bestScore = -Infinity;
  for (const slot of slots) {
    const cands = slot.perFile.filter((c): c is Candidate => c !== null);
    const filesWith = cands.length;
    const coverage = filesWith / n;
    if (filesWith < 2 || coverage < 0.6) continue;
    const values = cands.map((c) => c.value);
    const distinct = new Set(values).size / filesWith;
    if (distinct < 0.5) continue;
    const kinds = new Set(cands.map((c) => c.kind));
    if (kinds.has('roman') && new Set(values).size < 2) continue;

    let mono = 0;
    let pairs = 0;
    let last: number | null = null;
    for (const c of slot.perFile) {
      if (!c) continue;
      if (last !== null) {
        pairs++;
        if (c.value > last) mono += 1;
        else if (c.value === last) mono += 0.5;
      }
      last = c.value;
    }
    const monoFrac = pairs ? mono / pairs : 0;

    const maxStrength = Math.max(...cands.map((c) => c.strength));
    const anchorBonus = maxStrength >= 5 ? 2.5 : maxStrength >= 4 ? 2 : maxStrength >= 3 ? 1.5 : 0;
    let inMiddle = 0;
    slot.perFile.forEach((c, fi) => {
      if (c && c.nEnd > P && c.nStart < texts[fi]!.length - S) inMiddle++;
    });
    const middleBonus = 1.5 * (inMiddle / filesWith);
    const penalty = avg(cands.map((c) => c.penalty));

    const score = coverage * 3 + distinct * 2 + monoFrac * 2 + anchorBonus + middleBonus - penalty;
    if (score > bestScore) {
      bestScore = score;
      bestSlot = slot;
    }
  }

  if (bestSlot) {
    const chosen = bestSlot;
    return files.map((f, fi) => {
      const c = chosen.perFile[fi];
      return c ? finalize(c, f) : null;
    });
  }

  // Ordinal-set fallback: 上/下, 上/中/下, 前編/後編…
  const ords = files.map((f) => f.candidates.find((c) => c.kind === 'ordinal') ?? null);
  const withOrd = ords.filter((o) => o !== null).length;
  if (withOrd >= 2 && withOrd / n >= 0.6) {
    const tokens = new Set(ords.filter((o) => o !== null).map((o) => o!.ordinalToken));
    const hasMid = tokens.has('中');
    const mapToken = (t: string) => (t === '上' || t === '前' ? 1 : t === '中' ? 2 : hasMid ? 3 : 2);
    return files.map((f, fi) => {
      const o = ords[fi];
      if (!o) return null;
      const value = mapToken(o.ordinalToken!);
      const det = finalize({ ...o, value, display: String(value) }, f);
      return det;
    });
  }

  return files.map(() => null);
}

export interface SubdeckTitle {
  title: string;
  detected: boolean;
}

export function buildSubdeckTitles(fileNames: string[], label: string, opts?: { fallback?: 'sequential' | 'filename' }): SubdeckTitle[] {
  const detections = detectNumbering(fileNames);
  const fallback = opts?.fallback ?? 'sequential';
  return fileNames.map((name, i) => {
    const d = detections[i];
    if (d) return { title: label ? `${label} ${d.display}` : d.display, detected: true };
    if (fallback === 'filename') return { title: cleanFileName(name), detected: false };
    return { title: label ? `${label} ${i + 1}` : String(i + 1), detected: false };
  });
}
