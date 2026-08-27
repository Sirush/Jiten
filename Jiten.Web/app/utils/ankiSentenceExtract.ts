/**
 * Turns an Anki sentence field into a `**word**`-marked sentence that fits UserExampleSentence.Text.
 */

export const SENTENCE_MAX_LENGTH = 150;

/** `**` on each side of the marked span. */
const MARKER_COST = 4;

const MIN_SENTENCE_LENGTH = 5;

/** A fully styled field is styling, not a marker. */
const MAX_EXPLICIT_MARK_RATIO = 0.6;

/** Conjugation tails run a few kana past the stem; beyond this the highlight starts eating particles. */
const MAX_KANA_TAIL = 4;

const CLAUSE_END = /[。！？!?…]/;
const KANJI = /[㐀-䶿一-鿿豈-﫿]/;
const HIRAGANA = /[ぁ-ゟ]/;

const HIGHLIGHT_TAGS = new Set(['b', 'strong', 'mark']);
const DROPPED_CONTENT_TAGS = new Set(['rt', 'rp', 'script', 'style']);

const VOID_TAGS = new Set(['br', 'img', 'hr', 'input', 'meta', 'link', 'source', 'wbr']);

const NAMED_ENTITIES: Record<string, string> = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  nbsp: ' ',
};

interface MarkedChar {
  c: string;
  m: boolean;
}

export interface ExtractedSentence {
  text: string;
  /** Spans the field itself marked up (bold, coloured span, cloze), in `text` offsets. */
  markedRanges: Array<[number, number]>;
}

function decodeEntities(raw: string): string {
  return raw.replace(/&(#x?[0-9a-fA-F]+|[a-zA-Z]+);/g, (match, body: string) => {
    if (body[0] === '#') {
      const code = body[1] === 'x' || body[1] === 'X' ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
      return Number.isFinite(code) && code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
    }
    return NAMED_ENTITIES[body.toLowerCase()] ?? match;
  });
}

function isHighlightTag(name: string, attrs: string): boolean {
  if (HIGHLIGHT_TAGS.has(name)) return true;
  if (name !== 'span' && name !== 'font') return false;
  return (
    /style\s*=\s*"[^"]*(color|background)/i.test(attrs) ||
    /style\s*=\s*'[^']*(color|background)/i.test(attrs) ||
    /class\s*=\s*["'][^"']*(highlight|target|focus|expression)/i.test(attrs) ||
    /\scolor\s*=/i.test(attrs)
  );
}

/**
 * Walks the field's markup, keeping for every surviving character whether it sat inside a highlight
 * element. Carrying the flag per character means the later cleanup passes cannot desynchronise the
 * marked span from the text.
 */
function tokenise(html: string): MarkedChar[] {
  const out: MarkedChar[] = [];
  // Whether an element highlights is decided by its attributes, which only the opening tag carries,
  // so the open elements are tracked on a stack rather than counted.
  const open: Array<{ name: string; highlight: boolean; drop: boolean }> = [];
  let highlightDepth = 0;
  let dropDepth = 0;
  let i = 0;

  const pushText = (raw: string) => {
    if (dropDepth > 0) return;
    const decoded = decodeEntities(raw);
    for (const c of decoded) out.push({ c, m: highlightDepth > 0 });
  };

  while (i < html.length) {
    const lt = html.indexOf('<', i);
    if (lt < 0) {
      pushText(html.slice(i));
      break;
    }
    if (lt > i) pushText(html.slice(i, lt));

    const gt = html.indexOf('>', lt);
    if (gt < 0) {
      pushText(html.slice(lt));
      break;
    }

    const inner = html.slice(lt + 1, gt);
    const closing = inner.startsWith('/');
    const body = closing ? inner.slice(1) : inner;
    const nameMatch = /^[a-zA-Z][a-zA-Z0-9]*/.exec(body);

    if (nameMatch) {
      const name = nameMatch[0].toLowerCase();
      const attrs = body.slice(nameMatch[0].length);
      const selfClosing = body.trimEnd().endsWith('/');

      if (closing) {
        const at = open.findLastIndex((e) => e.name === name);
        if (at >= 0) {
          for (let depth = open.length - 1; depth >= at; depth--) {
            const entry = open.pop()!;
            if (entry.highlight) highlightDepth = Math.max(0, highlightDepth - 1);
            if (entry.drop) dropDepth = Math.max(0, dropDepth - 1);
          }
        }
      } else if (!selfClosing && !VOID_TAGS.has(name)) {
        const drop = DROPPED_CONTENT_TAGS.has(name);
        const highlight = !drop && isHighlightTag(name, attrs);
        open.push({ name, highlight, drop });
        if (highlight) highlightDepth++;
        if (drop) dropDepth++;
      }
    }

    i = gt + 1;
  }

  return out;
}

function applyCloze(chars: MarkedChar[]): MarkedChar[] {
  const text = chars.map((x) => x.c).join('');
  const pattern = /\{\{c\d+::(.*?)(?:::.*?)?\}\}/g;
  const out: MarkedChar[] = [];
  let last = 0;
  let match: RegExpExecArray | null;

  while ((match = pattern.exec(text)) !== null) {
    for (let i = last; i < match.index; i++) out.push(chars[i]!);
    const bodyStart = match.index + match[0].indexOf('::') + 2;
    const body = match[1] ?? '';
    for (let i = 0; i < body.length; i++) out.push({ c: chars[bodyStart + i]!.c, m: true });
    last = match.index + match[0].length;
  }

  if (last === 0) return chars;
  for (let i = last; i < chars.length; i++) out.push(chars[i]!);
  return out;
}

/** Drops `[sound:…]`, `[anki:tts…]` and Anki furigana readings in one pass, as the vocabulary import does. */
function stripBracketed(chars: MarkedChar[]): MarkedChar[] {
  const out: MarkedChar[] = [];
  let depth = 0;
  for (const ch of chars) {
    if (ch.c === '[') {
      depth++;
      continue;
    }
    if (ch.c === ']') {
      if (depth > 0) depth--;
      continue;
    }
    if (depth === 0) out.push(ch);
  }
  return out;
}

function normalise(chars: MarkedChar[]): MarkedChar[] {
  const out: MarkedChar[] = [];
  let pendingSpace = false;

  for (const ch of chars) {
    // A literal asterisk would break the `**` marker parse on the way back out.
    if (ch.c === '*') continue;

    if (/\s/.test(ch.c)) {
      if (out.length > 0) pendingSpace = true;
      continue;
    }
    if (pendingSpace) {
      out.push({ c: ' ', m: false });
      pendingSpace = false;
    }
    out.push(ch);
  }

  return out;
}

export function extractSentenceFromField(html: string): ExtractedSentence {
  const chars = normalise(stripBracketed(applyCloze(tokenise(html ?? ''))));
  const text = chars.map((x) => x.c).join('');

  const markedRanges: Array<[number, number]> = [];
  let start = -1;
  for (let i = 0; i <= chars.length; i++) {
    const marked = i < chars.length && chars[i]!.m;
    if (marked && start < 0) start = i;
    if (!marked && start >= 0) {
      markedRanges.push([start, i]);
      start = -1;
    }
  }

  return { text, markedRanges };
}

/** Katakana to hiragana, length preserved so offsets stay valid against the original text. */
export function foldKana(text: string): string {
  let out = '';
  for (const c of text) {
    const code = c.codePointAt(0)!;
    out += code >= 0x30a1 && code <= 0x30f6 ? String.fromCodePoint(code - 0x60) : c;
  }
  return out;
}

function findStem(haystack: string, form: string): [number, number] | null {
  for (let length = form.length - 1; length > 0; length--) {
    const prefix = form.slice(0, length);
    if (length < (KANJI.test(prefix) ? 1 : 2)) break;

    const at = haystack.indexOf(prefix);
    if (at < 0) continue;

    let tail = 0;
    while (tail < MAX_KANA_TAIL && HIRAGANA.test(haystack[at + length + tail] ?? '')) tail++;
    return [at, at + length + tail];
  }
  return null;
}

/**
 * Locates the studied word in the sentence. Explicit markup wins; otherwise the word's writings are
 * matched whole, then by stem so conjugated verbs still resolve (食べる against 食べたかった).
 */
export function locateWord(extracted: ExtractedSentence, candidates: string[]): [number, number] | null {
  const { text, markedRanges } = extracted;
  if (text.length === 0) return null;

  const explicit = markedRanges.find(([from, to]) => to > from && to - from <= text.length * MAX_EXPLICIT_MARK_RATIO);
  if (explicit) return explicit;

  const forms = [...new Set(candidates.filter(Boolean))].sort((a, b) => b.length - a.length);
  if (forms.length === 0) return null;

  const haystack = foldKana(text);

  for (const form of forms) {
    const at = haystack.indexOf(foldKana(form));
    if (at >= 0) return [at, at + form.length];
  }

  for (const form of forms) {
    const stem = findStem(haystack, foldKana(form));
    if (stem) return stem;
  }

  return null;
}

function clauseBoundsAround(text: string, start: number, end: number): [number, number] {
  let from = 0;
  for (let i = start - 1; i >= 0; i--) {
    if (CLAUSE_END.test(text[i]!)) {
      from = i + 1;
      break;
    }
  }

  let to = text.length;
  for (let i = end; i < text.length; i++) {
    if (CLAUSE_END.test(text[i]!)) {
      to = i + 1;
      break;
    }
  }

  return [from, to];
}

/**
 * Fits the sentence into the column: whole neighbouring clauses first, then a window around the word.
 * Returns null when the marked word alone cannot fit.
 */
export function truncateAroundWord(text: string, start: number, end: number): { text: string; start: number; end: number } | null {
  const budget = SENTENCE_MAX_LENGTH - MARKER_COST;
  if (end - start > budget) return null;
  if (text.length <= budget) return { text, start, end };

  const [clauseFrom, clauseTo] = clauseBoundsAround(text, start, end);

  if (clauseTo - clauseFrom <= budget) {
    let from = clauseFrom;
    let to = clauseTo;

    // Grow by whole neighbouring clauses; a dropped sentence needs no ellipsis.
    for (;;) {
      const [nextFrom] = from > 0 ? clauseBoundsAround(text, from - 1, from) : [from];
      const grewLeft = from > 0 && to - nextFrom <= budget;
      if (grewLeft) from = nextFrom;

      const [, nextTo] = to < text.length ? clauseBoundsAround(text, to, to + 1) : [0, to];
      const grewRight = to < text.length && nextTo - from <= budget;
      if (grewRight) to = nextTo;

      if (!grewLeft && !grewRight) break;
    }

    return { text: text.slice(from, to), start: start - from, end: end - from };
  }

  let available = budget - (end - start);
  const leftRoom = start - clauseFrom;
  const rightRoom = clauseTo - end;

  // Both sides get cut here by construction, so both ellipses are paid for up front.
  if (leftRoom > 0) available -= 1;
  if (rightRoom > 0) available -= 1;
  if (available < 0) return null;

  let left = Math.min(leftRoom, Math.floor(available / 2));
  const right = Math.min(rightRoom, available - left);
  left = Math.min(leftRoom, available - right);

  let from = start - left;
  let to = end + right;

  const comma = text.slice(from, Math.min(from + 5, start)).indexOf('、');
  if (comma >= 0) from += comma + 1;

  const tailComma = text.slice(Math.max(to - 5, end), to).lastIndexOf('、');
  if (tailComma >= 0) to = Math.max(to - 5, end) + tailComma;

  const prefix = from > clauseFrom ? '…' : '';
  const suffix = to < clauseTo ? '…' : '';

  return {
    text: prefix + text.slice(from, to) + suffix,
    start: start - from + prefix.length,
    end: end - from + prefix.length,
  };
}

export type SentenceSkipReason = 'empty' | 'noHighlight' | 'tooLong';

export interface SentenceBuildResult {
  text?: string;
  truncated?: boolean;
  skipped?: SentenceSkipReason;
}

/**
 * Field HTML plus the word's writings in, storable marked text out.
 * `candidates` should hold the Anki word field first, then the resolved form's writings.
 */
export function buildSentenceForImport(html: string, candidates: string[]): SentenceBuildResult {
  const extracted = extractSentenceFromField(html);
  if (extracted.text.length < MIN_SENTENCE_LENGTH) return { skipped: 'empty' };

  const located = locateWord(extracted, candidates);
  if (!located) return { skipped: 'noHighlight' };

  const fitted = truncateAroundWord(extracted.text, located[0], located[1]);
  if (!fitted) return { skipped: 'tooLong' };

  const marked = fitted.text.slice(0, fitted.start) + '**' + fitted.text.slice(fitted.start, fitted.end) + '**' + fitted.text.slice(fitted.end);

  return { text: marked, truncated: fitted.text.length !== extracted.text.length };
}
