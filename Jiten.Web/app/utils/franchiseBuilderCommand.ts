import { linkChoiceByKey, linkChoices, type BuilderDeck, type LinkChoice } from '~/utils/franchiseBuilder';

const ROMAN = ['', 'i', 'ii', 'iii', 'iv', 'v', 'vi', 'vii', 'viii', 'ix', 'x', 'xi', 'xii', 'xiii', 'xiv', 'xv', 'xvi', 'xvii', 'xviii', 'xix', 'xx'];
const STOPWORDS = new Set(['the', 'of', 'a', 'an', 'no']);
const JAPANESE = /[぀-ヿ㐀-鿿]/;

function titleTokens(s: string): string[] {
  return s
    .toLowerCase()
    .replace(/([a-z])(?=\d)/g, '$1 ')
    .replace(/[:：'’,!?()~～\-–.](?![\d.])/g, ' ')
    .split(/\s+/)
    .filter(Boolean)
    .map((t) => {
      const m = t.match(/^(\d+)((?:-[\d.]+)?)$/);
      if (!m) return t;
      const n = Number(m[1]);
      return n > 0 && n <= 20 ? `${ROMAN[n]}${m[2] ?? ''}` : t;
    });
}

// Prefix matching skips numerals so "vii" never matches "viii" and "xiii" never matches "xiii-2".
const tokenHit = (t: string, x: string) => x === t || (t.length >= 3 && !/^[ivx]+$/.test(t) && x.startsWith(t) && /[a-z]/.test(x[t.length] ?? ''));

export interface CommandContext {
  decks: BuilderDeck[];
  board: Set<number>;
  name: (deckId: number) => string;
}

const deckTitles = (d: BuilderDeck) => [d.originalTitle, d.romajiTitle, d.englishTitle].filter((t): t is string => !!t);

/** `lookup` names what the caller can fetch from the server when the deck is not known locally yet. */
export function resolveDeckName(text: string, ctx: CommandContext): { id: number } | { error: string; lookup?: string } {
  const raw = text.trim();
  if (!raw) return { error: 'Missing a deck name.' };
  const idMatch = raw.match(/^#(\d+)$/);
  if (idMatch) {
    const id = +idMatch[1]!;
    return ctx.decks.some((d) => d.deckId === id) ? { id } : { error: `Looking up deck #${id}…`, lookup: `#${id}` };
  }
  let cands: { d: BuilderDeck; extra: number }[];
  if (JAPANESE.test(raw)) {
    cands = ctx.decks.flatMap((d) => {
      const hit = deckTitles(d).filter((t) => t.includes(raw));
      return hit.length ? [{ d, extra: Math.min(...hit.map((t) => t.length - raw.length)) }] : [];
    });
  } else {
    const tokensOf = new Map(ctx.decks.map((d) => [d.deckId, [...new Set(deckTitles(d).flatMap(titleTokens))]]));
    const counts = new Map<string, number>();
    for (const ts of tokensOf.values()) for (const t of ts) counts.set(t, (counts.get(t) ?? 0) + 1);
    // Words most titles share ("final fantasy") say nothing about which deck is meant.
    const generic = (t: string) => STOPWORDS.has(t) || (ctx.decks.length >= 4 && (counts.get(t) ?? 0) * 2 >= ctx.decks.length);
    const all = titleTokens(raw);
    const specific = all.filter((t) => !generic(t));
    const q = specific.length ? specific : all;
    if (!q.length) return { error: `“${raw}” is too vague. Add a number or subtitle.` };
    cands = [];
    for (const d of ctx.decks) {
      const dt = tokensOf.get(d.deckId)!;
      if (!q.every((t) => dt.some((x) => tokenHit(t, x)))) continue;
      cands.push({ d, extra: dt.filter((x) => !generic(x) && !q.some((t) => tokenHit(t, x))).length });
    }
  }
  if (!cands.length) return { error: `No deck matches “${raw}”.`, lookup: raw };
  const best = Math.min(...cands.map((c) => c.extra));
  let top = cands.filter((c) => c.extra === best);
  if (top.length > 1) {
    const boarded = top.filter((c) => ctx.board.has(c.d.deckId));
    if (boarded.length === 1) top = boarded;
  }
  if (top.length > 1)
    return {
      error: `“${raw}” could be ${top
        .slice(0, 4)
        .map((c) => ctx.name(c.d.deckId))
        .join(', ')}. Be more specific, or use #id.`,
    };
  return { id: top[0]!.d.deckId };
}

export type CommandItem = { kind: 'link'; subject: number; object: number; choice: LinkChoice } | { kind: 'error'; message: string; lookup?: string };

const errorItem = (r: { error: string; lookup?: string }): CommandItem => ({ kind: 'error', message: r.error, lookup: r.lookup });

// Longest first, so a phrase never loses to a shorter one it starts with.
const PHRASES = linkChoices.flatMap((c) => c.phrases.map((p) => [p, c] as const)).sort((a, b) => b[0].length - a[0].length);
const choiceByPhrase = new Map(PHRASES);
const escapeRegExp = (s: string) => s.replace(/[\\^$.*+?()[\]{}|/-]/g, '\\$&');
const PHRASE_RE = new RegExp(
  `\\s(?:is\\s+)?(?:an?\\s+|the\\s+)?(${PHRASES.map((p) => escapeRegExp(p[0])).join('|')})(?:\\s+(?:version\\s+)?(?:to|of|as|with|for))?(?=\\s)`,
  'gi'
);

/** Parses "a sequel to b" and "a > b > c" (a sequel chain); null for empty input. */
export function parseBuilderCommand(text: string, ctx: CommandContext): CommandItem[] | null {
  const t = text.trim();
  if (!t) return null;

  if (/[>→]/.test(t)) {
    const parts = t.split(/\s*[>→]\s*/).filter(Boolean);
    if (parts.length < 2) return [{ kind: 'error', message: 'Add a deck on both sides of “>”.' }];
    const res = parts.map((p) => resolveDeckName(p, ctx));
    const sequel = linkChoiceByKey('sequel')!;
    const items: CommandItem[] = [];
    for (let i = 0; i + 1 < res.length; i++) {
      const a = res[i]!;
      const b = res[i + 1]!;
      if ('error' in a) items.push(errorItem(a));
      else if ('error' in b) items.push(errorItem(b));
      else items.push({ kind: 'link', subject: b.id, object: a.id, choice: sequel });
    }
    return items;
  }

  const padded = ` ${t} `;
  let first: CommandItem | null = null;
  for (const m of padded.matchAll(PHRASE_RE)) {
    const left = padded.slice(0, m.index);
    const right = padded.slice(m.index! + m[0].length);
    if (!left.trim() || !right.trim()) continue;
    const a = resolveDeckName(left, ctx);
    const b = resolveDeckName(right, ctx);
    if ('error' in a) first ??= errorItem(a);
    else if ('error' in b) first ??= errorItem(b);
    else return [{ kind: 'link', subject: a.id, object: b.id, choice: choiceByPhrase.get(m[1]!.toLowerCase())! }];
  }
  return [first ?? { kind: 'error', message: 'Name two decks with a relationship between them, like “x-2 sequel to x”.' }];
}
