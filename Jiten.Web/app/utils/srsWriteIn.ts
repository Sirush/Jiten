import { toKatakana } from 'wanakana';
import type { StudyCardDto } from '~/types';

export type WriteInMode = 'srs' | 'reading' | 'meaning';

export interface WriteInResult {
  ok: boolean;
  /** The user's input normalized to hiragana (reading) or lowercased (meaning). */
  normalized: string;
  /** What to reveal as the expected answer: the primary kana reading, or the first meaning gloss. */
  expected: string;
  /** Meaning mode: the input held no content word (only filler like "to") — not a gradeable answer. */
  invalid?: boolean;
}

/**
 * Canonical comparison form: fold romaji + hiragana + katakana to katakana. Using katakana (not
 * hiragana) keeps the long-vowel mark ー consistent — wanakana expands ー to a vowel when going
 * katakana→hiragana but preserves it from hiragana, so hiragana folding makes katakana-only words
 * (コーヒー) unmatchable by their hiragana/romaji spelling. Katakana folding makes script irrelevant.
 */
function canon(input: string): string {
  return toKatakana((input ?? '').trim(), { passRomaji: false });
}

/**
 * Candidate normalizations of a typed reading. The first is the literal parse; we also add an
 * apostrophe-tolerant parse where a bare `n` before a vowel/y is treated as ん (e.g. "renai" → れんあい
 * as well as れない), by inserting the apostrophe wanakana uses to mark ん. This only ever ADDS an
 * accepted spelling against the card's own readings — it can't let a genuinely wrong reading through,
 * since the alt is still a valid apostrophe-free romanization. Mostly relevant when the romaji IME is
 * off (when on, wanakana already converts each keystroke live).
 */
function readingCandidates(input: string): string[] {
  const trimmed = (input ?? '').trim();
  const base = canon(trimmed);
  const candidates = [base];
  if (/n[aiueoy]/i.test(trimmed)) {
    const alt = canon(trimmed.replace(/n(?=[aiueoy])/gi, "n'"));
    if (alt !== base) candidates.push(alt);
  }
  return candidates;
}

// CJK ideographs (incl. Ext-A and compatibility) — reading mode only makes sense for words that
// contain kanji; for pure-kana words the shown surface already IS the reading.
const KANJI_RE = /[㐀-鿿豈-﫿]/;
export function hasKanji(text: string): boolean {
  return KANJI_RE.test(text ?? '');
}

function primaryReading(card: StudyCardDto): string {
  return card.readings.find(r => r.formType === 1)?.text
    ?? card.readings[0]?.text
    ?? card.wordTextPlain;
}

/** Reading mode: any registered reading (or the surface form), compared exactly as hiragana. */
export function checkReading(input: string, card: StudyCardDto): WriteInResult {
  const candidates = readingCandidates(input);
  const norm = candidates[0] ?? '';
  const accepted = new Set<string>();
  for (const r of card.readings) if (r.text) accepted.add(canon(r.text));
  if (card.wordTextPlain) accepted.add(canon(card.wordTextPlain));
  const ok = candidates.some(c => c.length > 0 && accepted.has(c));
  return { ok, normalized: norm, expected: primaryReading(card) };
}

// Function words and dictionary-note tokens that don't count as a "content word" answer.
const STOPWORDS = new Set([
  'the', 'a', 'an', 'to', 'of', 'for', 'in', 'on', 'at', 'by', 'and', 'or', 'as',
]);

/** Content words in a piece of English text: lowercased, parenthetical notes & function words removed. */
function contentTokens(text: string): string[] {
  const cleaned = (text ?? '').replace(/\([^)]*\)/g, ' ').toLowerCase();
  const out: string[] = [];
  for (const raw of cleaned.split(/[^a-z'’-]+/)) {
    const w = raw.replace(/['’]s$/, '').replace(/^-+|-+$/g, '').trim();
    if (w.length >= 2 && !STOPWORDS.has(w)) out.push(w);
  }
  return out;
}

/** All content words across a card's meanings. */
export function extractContentWords(card: StudyCardDto): Set<string> {
  const set = new Set<string>();
  for (const def of card.definitions ?? [])
    for (const gloss of def.meanings ?? [])
      for (const w of contentTokens(gloss)) set.add(w);
  return set;
}

/**
 * Meaning mode: correct if any content word the user typed matches a content word from the
 * definitions (so "to eat" passes via "eat"). If the input held no content word at all — only
 * filler like "to"/"the" — it's flagged `invalid` so the UI can ask for a meaningful word rather
 * than marking it wrong.
 */
export function checkMeaning(input: string, card: StudyCardDto): WriteInResult {
  const accepted = extractContentWords(card);
  const typed = contentTokens(input);
  const norm = (input ?? '').trim().toLowerCase();
  const expected = card.definitions?.[0]?.meanings?.[0] ?? '';
  if (typed.length === 0) return { ok: false, invalid: true, normalized: norm, expected };
  return { ok: typed.some(t => accepted.has(t)), normalized: norm, expected };
}

/** Fisher–Yates shuffle (in place), returning the array for convenience. */
export function shuffleInPlace<T>(arr: T[]): T[] {
  for (let i = arr.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j]!, arr[i]!];
  }
  return arr;
}

// A short synthesized chime — rising two-tone for correct, low buzz for wrong. No audio asset.
let audioCtx: AudioContext | null = null;
export function playWriteInChime(correct: boolean): void {
  if (typeof window === 'undefined') return;
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;
    audioCtx ??= new Ctx();
    const ctx = audioCtx;
    if (ctx.state === 'suspended') void ctx.resume();
    const now = ctx.currentTime;
    const tones = correct ? [660, 990] : [196];
    tones.forEach((freq, i) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = correct ? 'sine' : 'sawtooth';
      osc.frequency.value = freq;
      const start = now + i * 0.09;
      const dur = correct ? 0.12 : 0.22;
      gain.gain.setValueAtTime(0.0001, start);
      gain.gain.exponentialRampToValueAtTime(correct ? 0.18 : 0.12, start + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, start + dur);
      osc.connect(gain).connect(ctx.destination);
      osc.start(start);
      osc.stop(start + dur + 0.02);
    });
  } catch {
    // Audio is a nicety — never let it break grading.
  }
}
