import { LinkType, type DeckRelationshipType, type Genre, type MediaType } from '~/types/enums';
import {
  MetadataEditDoubtReason,
  MetadataEditStatus,
  MetadataField,
  type EditableDeck,
  type MetadataFieldChange,
  type MetadataLinkValue,
  type MetadataPublicStatus,
  type MetadataQueueConflict,
  type MetadataRelationshipValue,
  type MetadataTagValue,
  type MetadataViewerRole,
  type SubmitMetadataEdit,
  type SubmitMetadataEditBase,
} from '~/types/metadataEdits';
import { edgeId, edgeKey, genreKey, linkKey, savableEdges, tagKey } from '~/utils/deckMetadataPatch';
import { getGenreText } from '~/utils/genreMapper';
import { tagWeightText } from '~/utils/tags';
import { detectLinkTypeFromUrl, getLinkLabel, getLinkTypeText } from '~/utils/linkTypeMapper';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';
import { getInverseRelationshipType, getRelationshipRoleLabel } from '~/utils/relationshipRoles';

export const MAX_ALIASES = 15;
export const MAX_SOURCES = 5;
export const MAX_NOTE_LENGTH = 500;
export const MAX_DESCRIPTION_LENGTH = 2000;
export const MAX_ADDED_NAMES = 30;

/** Mirrors CharacterNameCheckService.MinNameLength and its InvalidLengthError, so a too-short name is answered without a request. */
export const MIN_NAME_LENGTH = 2;
export const NAME_LENGTH_ERROR = 'Names must be 2 to 20 characters long, not counting spaces.';

export function isNameTooShort(name: string): boolean {
  return [...name.replace(/[ \u3000]/g, '')].length < MIN_NAME_LENGTH;
}

const FIELD_ORDER: MetadataField[] = [
  MetadataField.RomajiTitle,
  MetadataField.EnglishTitle,
  MetadataField.ReleaseDate,
  MetadataField.Aliases,
  MetadataField.Links,
  MetadataField.Genres,
  MetadataField.Tags,
  MetadataField.Relationships,
  MetadataField.CharacterNames,
  MetadataField.OriginalTitle,
  MetadataField.MediaType,
  MetadataField.Description,
];

const FIELD_LABELS: Record<MetadataField, string> = {
  [MetadataField.OriginalTitle]: 'Original title',
  [MetadataField.RomajiTitle]: 'Romaji title',
  [MetadataField.EnglishTitle]: 'English title',
  [MetadataField.Aliases]: 'Aliases',
  [MetadataField.ReleaseDate]: 'Release date',
  [MetadataField.Description]: 'Description',
  [MetadataField.MediaType]: 'Media type',
  [MetadataField.Genres]: 'Genres',
  [MetadataField.Tags]: 'Tags',
  [MetadataField.Links]: 'External links',
  [MetadataField.Relationships]: 'Related decks',
  [MetadataField.CharacterNames]: 'Character names',
};

const PLURAL_FIELDS = new Set<MetadataField>([
  MetadataField.Aliases,
  MetadataField.Genres,
  MetadataField.Tags,
  MetadataField.Links,
  MetadataField.Relationships,
  MetadataField.CharacterNames,
]);

/** Fields that need a note on submit; the API rejects the edit without one. */
export const NOTE_REQUIRED_FIELDS = new Set<MetadataField>([MetadataField.OriginalTitle, MetadataField.Description, MetadataField.MediaType]);

export function fieldLabel(field: MetadataField | number): string {
  return FIELD_LABELS[field as MetadataField] ?? 'Unknown field';
}

export function sortFields(fields: Iterable<number>): MetadataField[] {
  const set = new Set(fields);
  return FIELD_ORDER.filter((f) => set.has(f));
}

export function sortChanges<T extends { field: number }>(changes: T[]): T[] {
  return [...changes].sort((a, b) => FIELD_ORDER.indexOf(a.field) - FIELD_ORDER.indexOf(b.field));
}

export function fieldsFromFlags(flags: number): MetadataField[] {
  return FIELD_ORDER.filter((f) => (flags & f) !== 0);
}

export function joinList(items: string[]): string {
  if (items.length <= 1) return items.join('');
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

const capitalise = (text: string) => (text ? text[0]!.toUpperCase() + text.slice(1) : text);

/** "Release date and aliases", lowercasing every label after the first. */
export function fieldPhrase(fields: number[]): string {
  return capitalise(joinList(sortFields(fields).map((f) => fieldLabel(f).toLowerCase())));
}

const readsPlural = (fields: number[]) => fields.length > 1 || PLURAL_FIELDS.has(fields[0] as MetadataField);

export type SubmitOutcomeKind = 'live' | 'pending' | 'mixed';

export interface SubmitOutcome {
  kind: SubmitOutcomeKind;
  title: string;
  body: string;
}

export function submitOutcome(appliedFields: number[], pendingFields: number[]): SubmitOutcome {
  const namesLive = appliedFields.includes(MetadataField.CharacterNames);
  const namesNote = namesLive ? ' Character names take effect the next time the deck is analysed.' : '';

  if (pendingFields.length === 0) {
    return {
      kind: 'live',
      title: 'Saved. Thanks for fixing this.',
      body: `It's on the deck page now and listed in the deck history under your name.${namesNote}`,
    };
  }

  if (appliedFields.length === 0) {
    return {
      kind: 'pending',
      title: 'Sent for review.',
      body: "An editor will check it first. You'll get a notification either way.",
    };
  }

  return {
    kind: 'mixed',
    title: `${fieldPhrase(appliedFields)} ${readsPlural(appliedFields) ? 'are' : 'is'} live.`,
    body: `${fieldPhrase(pendingFields)} will go live once an editor approves ${readsPlural(pendingFields) ? 'them' : 'it'}. You'll get a notification.${namesNote}`,
  };
}

export function revertSummary(revertedFields: number[], skippedFields: number[]): string {
  if (skippedFields.length === 0) return 'Edit reverted. The previous values are back on the deck.';
  if (revertedFields.length === 0) return 'Nothing was reverted. Every field in this edit was changed again since.';
  return `${fieldPhrase(revertedFields)} ${readsPlural(revertedFields) ? 'were' : 'was'} restored. ${fieldPhrase(skippedFields)} ${
    readsPlural(skippedFields) ? 'were' : 'was'
  } left alone because ${readsPlural(skippedFields) ? 'they had' : 'it had'} been changed again since.`;
}

const LINK_HOST_TEXT: Partial<Record<LinkType, string>> = {
  [LinkType.Vndb]: 'vndb.org',
  [LinkType.Tmdb]: 'themoviedb.org',
  [LinkType.Anilist]: 'anilist.co',
  [LinkType.Mal]: 'myanimelist.net',
  [LinkType.GoogleBooks]: 'Google Books',
  [LinkType.Imdb]: 'imdb.com',
  [LinkType.Igdb]: 'igdb.com',
  [LinkType.Syosetsu]: 'syosetu.com',
  [LinkType.Kakuyomu]: 'kakuyomu.jp',
  [LinkType.Bookmeter]: 'bookmeter.com',
  [LinkType.Amazon]: 'an Amazon store',
  [LinkType.YouTube]: 'youtube.com',
};

/** A typed link must be one the site detector classifies as that type; an empty URL is a row the form drops. */
export function linkUrlError(linkType: LinkType | number, url: string): string | null {
  const trimmed = url.trim();
  if (!trimmed) return null;

  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    return "That doesn't look like a link.";
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return 'Links must start with http:// or https://.';

  const host = LINK_HOST_TEXT[linkType as LinkType];
  if (linkType === LinkType.Web || !host) return null;
  if (detectLinkTypeFromUrl(trimmed) !== linkType) return `${getLinkTypeText(linkType as LinkType)} links must point to ${host}.`;
  return null;
}

export function aliasError(value: string, aliases: string[]): string | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  if (/https?:|www\./i.test(trimmed)) return "Aliases can't contain links.";
  if (aliases.includes(trimmed)) return 'That alias is already on this deck.';
  if (aliases.length >= MAX_ALIASES) return `This deck already has ${MAX_ALIASES} aliases. Remove one first.`;
  if (trimmed.length > 200) return 'Keep aliases under 200 characters.';
  return null;
}

function isSourceUrl(value: string): boolean {
  return /^https?:\/\/\S+\.\S+$/i.test(normaliseSourceUrl(value));
}

/** "vndb.org/v1" is accepted as https://vndb.org/v1, the same rule the API applies. */
export function normaliseSourceUrl(value: string): string {
  const trimmed = value.trim();
  if (trimmed && !trimmed.includes('://') && trimmed.includes('.') && !/\s/.test(trimmed)) return `https://${trimmed}`;
  return trimmed;
}

export interface MetadataDraftName {
  input: string;
  /** Surfaces the check said will be added (ok or flagged). */
  surfaces: string[];
}

export interface MetadataDraft {
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
  description: string;
  releaseDate: string;
  mediaType: number;
  aliases: string[];
  genres: number[];
  tags: MetadataTagValue[];
  links: { linkType: number; url: string }[];
  relationships: MetadataRelationshipValue[];
  characterNames: string[];
  addedNames: MetadataDraftName[];
}

type DraftKey = Exclude<keyof MetadataDraft, 'addedNames'>;
type ScalarKey = 'originalTitle' | 'romajiTitle' | 'englishTitle' | 'releaseDate' | 'mediaType' | 'description';

const DRAFT_KEY: Record<MetadataField, DraftKey> = {
  [MetadataField.OriginalTitle]: 'originalTitle',
  [MetadataField.RomajiTitle]: 'romajiTitle',
  [MetadataField.EnglishTitle]: 'englishTitle',
  [MetadataField.Aliases]: 'aliases',
  [MetadataField.ReleaseDate]: 'releaseDate',
  [MetadataField.Description]: 'description',
  [MetadataField.MediaType]: 'mediaType',
  [MetadataField.Genres]: 'genres',
  [MetadataField.Tags]: 'tags',
  [MetadataField.Links]: 'links',
  [MetadataField.Relationships]: 'relationships',
  [MetadataField.CharacterNames]: 'characterNames',
};

const SCALAR_FIELDS = [
  MetadataField.RomajiTitle,
  MetadataField.EnglishTitle,
  MetadataField.ReleaseDate,
  MetadataField.OriginalTitle,
  MetadataField.MediaType,
  MetadataField.Description,
] as const;
type ScalarField = (typeof SCALAR_FIELDS)[number];

const asScalarField = (field: MetadataField): ScalarField | undefined => SCALAR_FIELDS.find((f) => f === field);
const scalarKey = (field: ScalarField) => DRAFT_KEY[field] as ScalarKey;
const tidy = (value: string | number) => (typeof value === 'string' ? value.trim() : value);
const orNull = (value: string | number) => (value === '' ? null : value);

/** Puts one field of the draft back to what the form loaded; undoing names also drops the names typed in. */
export function resetDraftField(original: MetadataDraft, draft: MetadataDraft, field: MetadataField): void {
  const key = DRAFT_KEY[field];
  Object.assign(draft, { [key]: JSON.parse(JSON.stringify(original[key])) });
  if (field === MetadataField.CharacterNames) draft.addedNames = [];
}

export function draftFromEditable(deck: EditableDeck): MetadataDraft {
  return {
    originalTitle: deck.originalTitle ?? '',
    romajiTitle: deck.romajiTitle ?? '',
    englishTitle: deck.englishTitle ?? '',
    description: deck.description ?? '',
    releaseDate: deck.releaseDate ?? '',
    mediaType: deck.mediaType,
    aliases: [...(deck.aliases ?? [])],
    genres: [...(deck.genres ?? [])],
    tags: (deck.tags ?? []).map((t) => ({ ...t })),
    links: (deck.links ?? []).map((l) => ({ linkType: Number(l.linkType), url: l.url })),
    relationships: (deck.relationships ?? []).map((r) => ({
      sourceDeckId: r.sourceDeckId,
      targetDeckId: r.targetDeckId,
      relationshipType: r.relationshipType,
      otherDeckId: r.otherDeckId,
      otherDeckTitle: r.otherDeckTitle,
    })),
    characterNames: [...(deck.characterNames ?? [])],
    addedNames: [],
  };
}

const sortedKey = (values: string[]) => [...values].sort().join('\u0001');
const filledLinks = (links: { linkType: number; url: string }[]) =>
  links.map((l) => ({ linkType: Number(l.linkType), url: l.url.trim() })).filter((l) => l.url);

export interface MetadataSubmissionDraft {
  payload: Omit<SubmitMetadataEdit, 'sources' | 'note'>;
  fields: MetadataField[];
}

/** Only changed fields go into the payload; an omitted property leaves that field alone on the server. Each one carries what the form loaded in `base`. */
export function buildSubmission(original: MetadataDraft, draft: MetadataDraft): MetadataSubmissionDraft {
  const payload: MetadataSubmissionDraft['payload'] = {};
  const fields: MetadataField[] = [];
  const mark = (field: MetadataField) => fields.push(field);
  const base: SubmitMetadataEditBase = {};
  const edgePayload = (relationships: MetadataRelationshipValue[]) =>
    savableEdges(relationships).map((r) => ({ sourceDeckId: r.sourceDeckId, targetDeckId: r.targetDeckId, relationshipType: Number(r.relationshipType) }));

  for (const field of SCALAR_FIELDS) {
    const key = scalarKey(field);
    if (tidy(draft[key]) === tidy(original[key])) continue;
    Object.assign(payload, { [key]: tidy(draft[key]) });
    Object.assign(base, { [key]: original[key] });
    mark(field);
  }
  const aliases = draft.aliases.map((a) => a.trim()).filter(Boolean);
  if (sortedKey(aliases) !== sortedKey(original.aliases)) {
    payload.aliases = aliases;
    base.aliases = [...original.aliases];
    mark(MetadataField.Aliases);
  }
  if (linkKey(filledLinks(draft.links)) !== linkKey(filledLinks(original.links))) {
    payload.links = filledLinks(draft.links);
    base.links = filledLinks(original.links);
    mark(MetadataField.Links);
  }
  if (genreKey(draft.genres) !== genreKey(original.genres)) {
    payload.genres = [...draft.genres].sort((a, b) => a - b);
    base.genres = [...original.genres];
    mark(MetadataField.Genres);
  }
  if (tagKey(draft.tags) !== tagKey(original.tags)) {
    payload.tags = draft.tags.map((t) => ({ tagId: t.tagId, percentage: t.percentage }));
    base.tags = original.tags.map((t) => ({ tagId: t.tagId, percentage: t.percentage }));
    mark(MetadataField.Tags);
  }
  if (edgeKey(draft.relationships) !== edgeKey(original.relationships)) {
    payload.relationships = edgePayload(draft.relationships);
    base.relationships = edgePayload(original.relationships);
    mark(MetadataField.Relationships);
  }
  const kept = new Set(draft.characterNames);
  const removed = original.characterNames.filter((n) => !kept.has(n));
  const added = draft.addedNames.map((n) => n.input.trim()).filter(Boolean);
  if (removed.length || added.length) {
    if (added.length) payload.addCharacterNames = added;
    if (removed.length) payload.removeCharacterNames = removed;
    mark(MetadataField.CharacterNames);
  }

  if (fields.some((f) => f !== MetadataField.CharacterNames)) payload.base = base;
  return { payload, fields: sortFields(fields) };
}

/** Moves the form's starting point to the value someone saved meanwhile, keeping the contributor's own value in the draft. */
export function rebaseDraft(original: MetadataDraft, field: MetadataField, current: unknown): void {
  const scalar = asScalarField(field);
  if (scalar === undefined) return;
  Object.assign(original, { [scalarKey(scalar)]: scalar === MetadataField.MediaType ? Number(current) : current == null ? '' : String(current) });
}

export type DiffKind = 'same' | 'add' | 'remove' | 'gap';

export interface DiffSegment {
  kind: DiffKind;
  text: string;
}

/** Japanese has no spaces, so each kana or kanji is its own token; Latin text diffs by word. */
const DIFF_TOKEN = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}ー々]|[\p{L}\p{N}'’]+|\s+|[^\s\p{L}\p{N}]/gu;

/** Bounds the LCS table; descriptions are capped at 2,000 characters, so only a near-total rewrite reaches it. */
const MAX_DIFF_CELLS = 1_500_000;

export function textDiff(before: string | null, after: string | null): DiffSegment[] {
  const a = (before ?? '').match(DIFF_TOKEN) ?? [];
  const b = (after ?? '').match(DIFF_TOKEN) ?? [];

  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const ops: { kind: Exclude<DiffKind, 'gap'>; text: string }[] = a.slice(0, start).map((text) => ({ kind: 'same' as const, text }));
  const midA = a.slice(start, endA);
  const midB = b.slice(start, endB);
  const n = midA.length;
  const m = midB.length;

  if (n * m > MAX_DIFF_CELLS) {
    ops.push(...midA.map((text) => ({ kind: 'remove' as const, text })), ...midB.map((text) => ({ kind: 'add' as const, text })));
  } else {
    const width = m + 1;
    const table = new Uint16Array((n + 1) * width);
    for (let i = n - 1; i >= 0; i--)
      for (let j = m - 1; j >= 0; j--)
        table[i * width + j] = midA[i] === midB[j] ? table[(i + 1) * width + j + 1]! + 1 : Math.max(table[(i + 1) * width + j]!, table[i * width + j + 1]!);
    let i = 0;
    let j = 0;
    while (i < n && j < m) {
      if (midA[i] === midB[j]) {
        ops.push({ kind: 'same', text: midA[i++]! });
        j++;
      } else if (table[(i + 1) * width + j]! >= table[i * width + j + 1]!) ops.push({ kind: 'remove', text: midA[i++]! });
      else ops.push({ kind: 'add', text: midB[j++]! });
    }
    while (i < n) ops.push({ kind: 'remove', text: midA[i++]! });
    while (j < m) ops.push({ kind: 'add', text: midB[j++]! });
  }
  ops.push(...a.slice(endA).map((text) => ({ kind: 'same' as const, text })));

  return hunksToSegments(absorbShortEqualities(keepParagraphBreaks(toHunks(ops))));
}

const LINE_BREAK = /^\s*\n\s*$/;

/** A paragraph break that only changed how many blank lines it has stays unchanged text; the after side's breaks are kept. */
function keepParagraphBreaks(hunks: Hunk[]): Hunk[] {
  const merged: Hunk[] = [];
  for (const hunk of hunks) {
    const unchanged = hunk.kind === 'edit' && LINE_BREAK.test(hunk.removed) && LINE_BREAK.test(hunk.added) ? { kind: 'same' as const, text: hunk.added } : hunk;
    const last = merged[merged.length - 1];
    if (unchanged.kind === 'same' && last?.kind === 'same') last.text += unchanged.text;
    else merged.push({ ...unchanged });
  }
  return merged;
}

type Hunk = { kind: 'same'; text: string } | { kind: 'edit'; removed: string; added: string };

function toHunks(ops: { kind: Exclude<DiffKind, 'gap'>; text: string }[]): Hunk[] {
  const hunks: Hunk[] = [];
  for (const op of ops) {
    const last = hunks[hunks.length - 1];
    if (op.kind === 'same') {
      if (last?.kind === 'same') last.text += op.text;
      else hunks.push({ kind: 'same', text: op.text });
      continue;
    }
    const edit = last?.kind === 'edit' ? last : (hunks[hunks.push({ kind: 'edit', removed: '', added: '' }) - 1] as Extract<Hunk, { kind: 'edit' }>);
    if (op.kind === 'remove') edit.removed += op.text;
    else edit.added += op.text;
  }
  return hunks;
}

const editSize = (hunk: Hunk) => (hunk.kind === 'edit' ? hunk.removed.length + hunk.added.length : 0);

/** Folds an unchanged run no longer than the edits on both sides into them, so a rewritten sentence reads as one replacement instead of alternating word swaps. */
function absorbShortEqualities(hunks: Hunk[]): Hunk[] {
  let i = 1;
  while (i < hunks.length - 1) {
    const prev = hunks[i - 1]!;
    const same = hunks[i]!;
    const next = hunks[i + 1]!;
    if (prev.kind === 'edit' && same.kind === 'same' && next.kind === 'edit' && same.text.length <= Math.min(editSize(prev), editSize(next))) {
      hunks.splice(i - 1, 3, { kind: 'edit', removed: prev.removed + same.text + next.removed, added: prev.added + same.text + next.added });
      i = Math.max(1, i - 2);
    } else i++;
  }
  return hunks;
}

function hunksToSegments(hunks: Hunk[]): DiffSegment[] {
  return hunks.flatMap((hunk): DiffSegment[] => {
    if (hunk.kind === 'same') return [{ kind: 'same', text: hunk.text }];
    const segments: DiffSegment[] = [];
    if (hunk.removed) segments.push({ kind: 'remove', text: hunk.removed });
    if (hunk.added) segments.push({ kind: 'add', text: hunk.added });
    return segments;
  });
}

const visibleLength = (text: string) => text.replace(/\s+/g, '').length;

/** Below this share of kept text, a word-level diff is noise and the change shows as old text then new text. */
const REWRITE_KEPT_RATIO = 0.4;
const REWRITE_MIN_LENGTH = 40;

export function isRewrite(before: string, after: string, segments: DiffSegment[]): boolean {
  const longest = Math.max(visibleLength(before), visibleLength(after));
  if (longest < REWRITE_MIN_LENGTH) return false;
  const kept = segments.reduce((sum, s) => sum + (s.kind === 'same' ? visibleLength(s.text) : 0), 0);
  return kept / longest < REWRITE_KEPT_RATIO;
}

export function isWhitespaceOnlyChange(before: string, after: string): boolean {
  return before !== after && before.replace(/\s+/g, '') === after.replace(/\s+/g, '');
}

/** Keeps the first few changed parts with a little unchanged text around each, for timelines and feeds. */
export function diffExcerpt(segments: DiffSegment[], context = 20, maxEdit = 80, maxHunks = 2): DiffSegment[] {
  if (segments.length === 1) return segments;
  const out: DiffSegment[] = [];
  const head = (chars: string[]) => ({ kind: 'same' as const, text: wordHead(chars, context) });
  const tail = (chars: string[]) => ({ kind: 'same' as const, text: wordTail(chars, context) });
  const gap = { kind: 'gap' as const, text: '…' };
  let hunks = 0;

  for (let index = 0; index < segments.length; index++) {
    const segment = segments[index]!;
    const chars = [...segment.text];
    if (segment.kind !== 'same') {
      if (index === 0 || segments[index - 1]!.kind === 'same') hunks++;
      if (chars.length <= maxEdit) {
        out.push(segment);
        continue;
      }
      out.push({ kind: segment.kind, text: `${wordHead(chars, maxEdit).trimEnd()}…` });
      if (index === segments.length - 2 && segments[index + 1]!.kind === 'same') return out;
      continue;
    }

    const leading = index === 0;
    const truncated = index < segments.length - 1 && hunks === maxHunks;
    if (truncated) {
      out.push(head(chars), { kind: 'gap', text: ` … +${countHunks(segments.slice(index))} more` });
      return out;
    }
    const trailing = index === segments.length - 1;
    if (chars.length <= (leading || trailing ? context : context * 2)) out.push(segment);
    else if (leading) out.push(gap, tail(chars));
    else if (trailing) out.push(head(chars), gap);
    else out.push(head(chars), gap, tail(chars));
  }
  return out;
}

/** Cuts at the last space inside the limit so Latin words stay whole; text without spaces, like Japanese, cuts at the limit. */
function wordHead(chars: string[], limit: number): string {
  const text = chars.slice(0, limit).join('');
  if (chars.length <= limit || /\s/.test(chars[limit]!)) return text;
  const space = text.search(/\s\S*$/);
  return space > 0 ? text.slice(0, space + 1) : text;
}

function wordTail(chars: string[], limit: number): string {
  const text = chars.slice(-limit).join('');
  if (chars.length <= limit || /\s/.test(chars[chars.length - limit - 1]!)) return text;
  const space = text.search(/\s/);
  return space >= 0 && space < text.length - 1 ? text.slice(space) : text;
}

function countHunks(segments: DiffSegment[]): number {
  return segments.reduce((count, s, i) => count + (s.kind !== 'same' && (i === 0 || segments[i - 1]!.kind === 'same') ? 1 : 0), 0);
}

export interface DiffPiece {
  kind: DiffKind;
  text: string;
  /** A line break that was added or removed, drawn as a pilcrow because a bare newline has nothing to colour. */
  lineBreak?: boolean;
}

/** Splits changed segments around line breaks; in compact rows, where newlines collapse, unchanged breaks become spaces. */
export function diffPieces(segments: DiffSegment[], compact: boolean): DiffPiece[] {
  return segments.flatMap((segment): DiffPiece[] => {
    if (segment.kind === 'same' || segment.kind === 'gap') return [segment];
    return segment.text
      .split(/(\s*\n\s*)/)
      .filter(Boolean)
      .flatMap((part): DiffPiece[] => {
        if (!part.includes('\n')) return [{ kind: segment.kind, text: part }];
        const mark: DiffPiece = { kind: segment.kind, text: '¶', lineBreak: true };
        return segment.kind === 'add' && !compact ? [mark, { kind: 'same', text: part }] : [mark];
      });
  });
}

const sortStrings = (values: string[]) => [...values].sort((a, b) => a.localeCompare(b));

/** The form's pending changes in the API's FieldChange shape, so the diff panel renders like history and the queue. */
export function draftChanges(original: MetadataDraft, draft: MetadataDraft, fields: MetadataField[]): MetadataFieldChange[] {
  return fields.map((field): MetadataFieldChange => {
    const scalar = asScalarField(field);
    if (scalar !== undefined) {
      const key = scalarKey(scalar);
      return { field, before: orNull(original[key]), after: orNull(tidy(draft[key])) };
    }
    switch (field) {
      case MetadataField.Aliases:
        return { field, before: sortStrings(original.aliases), after: sortStrings(draft.aliases.map((a) => a.trim()).filter(Boolean)) };
      case MetadataField.Genres:
        return { field, before: [...original.genres].sort((a, b) => a - b), after: [...draft.genres].sort((a, b) => a - b) };
      case MetadataField.Tags:
        return { field, before: original.tags, after: draft.tags };
      case MetadataField.Links:
        return { field, before: filledLinks(original.links), after: filledLinks(draft.links) };
      case MetadataField.Relationships:
        return { field, before: original.relationships, after: draft.relationships };
      case MetadataField.CharacterNames: {
        const after = new Set(draft.characterNames);
        for (const name of draft.addedNames) for (const s of name.surfaces) after.add(s);
        return { field, before: sortStrings(original.characterNames), after: sortStrings([...after]) };
      }
      default:
        return { field, before: null, after: null };
    }
  });
}

export interface SubmissionCheckInput {
  draft: MetadataDraft;
  fields: MetadataField[];
  sources: string[];
  note: string;
}

export function sourcesError(sources: string[]): string | null {
  const filled = sources.map(normaliseSourceUrl).filter(Boolean);
  if (filled.some((s) => !isSourceUrl(s))) return 'Sources need to be links, such as https://vndb.org/v17.';
  return null;
}

export function submissionError({ draft, fields, sources, note }: SubmissionCheckInput): string | null {
  if (fields.length === 0) return 'Nothing changed yet.';
  if (draft.links.some((l) => linkUrlError(l.linkType, l.url))) return 'Fix the link errors before submitting.';
  if (fields.includes(MetadataField.OriginalTitle) && !draft.originalTitle.trim()) return "The original title can't be empty.";
  if (draft.description.length > MAX_DESCRIPTION_LENGTH) return `Keep the description under ${MAX_DESCRIPTION_LENGTH} characters.`;

  const sourceProblem = sourcesError(sources);
  if (sourceProblem) return sourceProblem;

  const noteFields = fields.filter((f) => NOTE_REQUIRED_FIELDS.has(f));
  if (noteFields.length && !note.trim()) return `Add a note explaining the change to ${joinList(noteFields.map((f) => fieldLabel(f).toLowerCase()))}.`;
  if (note.length > MAX_NOTE_LENGTH) return `Keep the note under ${MAX_NOTE_LENGTH} characters.`;
  return null;
}

/** The other deck's role, read from the deck the edit belongs to: the deck is the edge's source when the other deck is its target. */
export function relationshipLabel(r: MetadataRelationshipValue): string {
  const type = Number(r.relationshipType) as DeckRelationshipType;
  const perspective = r.targetDeckId === r.otherDeckId ? type : getInverseRelationshipType(type);
  return `${getRelationshipRoleLabel(perspective)}: ${r.otherDeckTitle}`;
}

export type ChangeView =
  | { kind: 'scalar'; before: string | null; after: string | null }
  | { kind: 'text'; before: string | null; after: string | null; delta: number }
  | { kind: 'list'; added: string[]; removed: string[]; changed: { label: string; before: string; after: string }[] };

const asArray = <T>(value: unknown): T[] => (Array.isArray(value) ? (value as T[]) : []);
const asText = (value: unknown): string | null => (value === null || value === undefined || value === '' ? null : String(value));

function keyedDiff<T>(before: T[], after: T[], key: (item: T) => string, label: (item: T) => string): { added: string[]; removed: string[] } {
  const beforeKeys = new Set(before.map(key));
  const afterKeys = new Set(after.map(key));
  return {
    added: after.filter((i) => !beforeKeys.has(key(i))).map(label),
    removed: before.filter((i) => !afterKeys.has(key(i))).map(label),
  };
}

export function describeChange(change: MetadataFieldChange): ChangeView {
  const { field, before, after } = change;
  switch (field) {
    case MetadataField.Description: {
      const b = asText(before);
      const a = asText(after);
      return { kind: 'text', before: b, after: a, delta: (a?.length ?? 0) - (b?.length ?? 0) };
    }
    case MetadataField.MediaType:
      return {
        kind: 'scalar',
        before: before == null ? null : getMediaTypeText(Number(before) as MediaType),
        after: after == null ? null : getMediaTypeText(Number(after) as MediaType),
      };
    case MetadataField.Aliases:
    case MetadataField.CharacterNames:
      return { kind: 'list', ...keyedDiff(asArray<string>(before), asArray<string>(after), String, String), changed: [] };
    case MetadataField.Genres:
      return {
        kind: 'list',
        ...keyedDiff(asArray<number>(before), asArray<number>(after), String, (g) => getGenreText(Number(g) as Genre)),
        changed: [],
      };
    case MetadataField.Tags: {
      const b = asArray<MetadataTagValue>(before);
      const a = asArray<MetadataTagValue>(after);
      const diff = keyedDiff(
        b,
        a,
        (t) => String(t.tagId),
        (t) => `${t.name} ${tagWeightText(t.percentage)}`
      );
      const beforeById = new Map(b.map((t) => [t.tagId, t]));
      const changed = a
        .filter((t) => beforeById.has(t.tagId) && beforeById.get(t.tagId)!.percentage !== t.percentage)
        .map((t) => ({ label: t.name, before: tagWeightText(beforeById.get(t.tagId)!.percentage), after: tagWeightText(t.percentage) }));
      return { kind: 'list', ...diff, changed };
    }
    case MetadataField.Links:
      return {
        kind: 'list',
        ...keyedDiff(
          asArray<MetadataLinkValue>(before),
          asArray<MetadataLinkValue>(after),
          (l) => `${l.linkType}|${l.url}`,
          (l) => `${getLinkLabel({ linkType: Number(l.linkType), url: l.url })}: ${l.url}`
        ),
        changed: [],
      };
    case MetadataField.Relationships:
      return {
        kind: 'list',
        ...keyedDiff(asArray<MetadataRelationshipValue>(before), asArray<MetadataRelationshipValue>(after), edgeId, relationshipLabel),
        changed: [],
      };
    default:
      return { kind: 'scalar', before: asText(before), after: asText(after) };
  }
}

const LIST_FIELDS = new Set<MetadataField>([
  MetadataField.Aliases,
  MetadataField.Genres,
  MetadataField.Tags,
  MetadataField.Links,
  MetadataField.Relationships,
  MetadataField.CharacterNames,
]);

export function isListField(field: MetadataField | number): boolean {
  return LIST_FIELDS.has(field as MetadataField);
}

export interface ConflictClash {
  label: string;
  current: string;
  edit: string;
}

const tagPercentages = (value: unknown) => new Map(asArray<MetadataTagValue>(value).map((t) => [t.tagId, t]));
const percentText = (tag: MetadataTagValue | undefined) => (tag ? tagWeightText(tag.percentage) : 'not on the deck');

/** The API merges list edits onto the current list, so only items both sides changed differently clash: a tag's percentage, or a typed link's URL. */
export function conflictClashes(conflict: MetadataQueueConflict): ConflictClash[] {
  if (conflict.field === MetadataField.Tags) {
    const before = tagPercentages(conflict.editBefore);
    const current = tagPercentages(conflict.current);
    const edit = tagPercentages(conflict.editAfter);
    const ids = new Set([...current.keys(), ...edit.keys()]);
    return [...ids]
      .filter((id) => {
        const now = current.get(id)?.percentage;
        return now !== edit.get(id)?.percentage && before.get(id)?.percentage !== now;
      })
      .map((id) => {
        const tag = current.get(id) ?? edit.get(id)!;
        return { label: tag.name, current: percentText(current.get(id)), edit: percentText(edit.get(id)) };
      });
  }

  if (conflict.field === MetadataField.Links) {
    const urlsByType = (value: unknown) => {
      const map = new Map<number, string[]>();
      for (const link of asArray<MetadataLinkValue>(value)) {
        const type = Number(link.linkType);
        if (type === LinkType.Web) continue;
        map.set(type, [...(map.get(type) ?? []), link.url].sort());
      }
      return map;
    };
    const current = urlsByType(conflict.current);
    const edit = urlsByType(conflict.editAfter);
    const types = new Set([...current.keys(), ...edit.keys()]);
    return [...types]
      .filter((type) => (current.get(type) ?? []).join(' ') !== (edit.get(type) ?? []).join(' '))
      .map((type) => ({
        label: getLinkTypeText(type as LinkType),
        current: current.get(type)?.join(', ') || 'none',
        edit: edit.get(type)?.join(', ') || 'none',
      }));
  }

  return [];
}

export function describeValue(field: MetadataField, value: unknown): string {
  switch (field) {
    case MetadataField.MediaType:
      return value == null ? 'empty' : getMediaTypeText(Number(value) as MediaType);
    case MetadataField.Aliases:
    case MetadataField.CharacterNames:
      return asArray<string>(value).join(', ') || 'none';
    case MetadataField.Genres:
      return (
        asArray<number>(value)
          .map((g) => getGenreText(Number(g) as Genre))
          .join(', ') || 'none'
      );
    case MetadataField.Tags:
      return (
        asArray<MetadataTagValue>(value)
          .map((t) => `${t.name} ${tagWeightText(t.percentage)}`)
          .join(', ') || 'none'
      );
    case MetadataField.Links:
      return (
        asArray<MetadataLinkValue>(value)
          .map((l) => l.url)
          .join(', ') || 'none'
      );
    case MetadataField.Relationships:
      return asArray<MetadataRelationshipValue>(value).map(relationshipLabel).join(', ') || 'none';
    default:
      return asText(value) ?? 'empty';
  }
}

export type StatusSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary';

const PUBLIC_STATUS: Record<MetadataPublicStatus, { label: string; severity: StatusSeverity }> = {
  live: { label: 'Live', severity: 'success' },
  pending: { label: 'Waiting for review', severity: 'warn' },
  rejected: { label: 'Rejected', severity: 'secondary' },
  reverted: { label: 'Reverted', severity: 'danger' },
  superseded: { label: 'Replaced by a later edit', severity: 'secondary' },
  withdrawn: { label: 'Withdrawn', severity: 'secondary' },
};

const EDITOR_STATUS: Record<MetadataEditStatus, { label: string; severity: StatusSeverity }> = {
  [MetadataEditStatus.Pending]: PUBLIC_STATUS.pending,
  [MetadataEditStatus.Applied]: { label: 'Live, unreviewed', severity: 'info' },
  [MetadataEditStatus.Approved]: { label: 'Approved', severity: 'success' },
  [MetadataEditStatus.Rejected]: PUBLIC_STATUS.rejected,
  [MetadataEditStatus.Reverted]: PUBLIC_STATUS.reverted,
  [MetadataEditStatus.Superseded]: PUBLIC_STATUS.superseded,
  [MetadataEditStatus.Withdrawn]: PUBLIC_STATUS.withdrawn,
};

/** Editors see Applied and Approved apart; everyone else only sees "Live". */
export function editStatus(item: { publicStatus: MetadataPublicStatus; status: MetadataEditStatus | null }, role: MetadataViewerRole) {
  if (role !== 'user' && item.status != null && EDITOR_STATUS[item.status]) return EDITOR_STATUS[item.status];
  return PUBLIC_STATUS[item.publicStatus] ?? PUBLIC_STATUS.live;
}

export const TIER_LABELS = ['Low risk', 'Medium risk', 'High risk'] as const;

export function tierLabel(tier: number | null | undefined): string {
  return tier == null ? '' : (TIER_LABELS[tier] ?? '');
}

export const DOUBT_REASONS: { value: MetadataEditDoubtReason; label: string }[] = [
  { value: MetadataEditDoubtReason.SourceDoesNotMatch, label: "Source doesn't match" },
  { value: MetadataEditDoubtReason.WrongValue, label: 'Wrong value' },
  { value: MetadataEditDoubtReason.NoSource, label: 'No source' },
  { value: MetadataEditDoubtReason.Other, label: 'Something else' },
];

export function doubtReasonLabel(reason: number | null | undefined): string {
  return DOUBT_REASONS.find((r) => r.value === reason)?.label ?? '';
}

export function contributorLevelLabel(level: string | null | undefined): string {
  if (!level) return '';
  return capitalise(level.toLowerCase());
}

/** Single-key shortcuts must never fire while the user is typing into a field or an open combobox. */
export function isTypingTarget(target: unknown): boolean {
  const el = target as { tagName?: string; isContentEditable?: boolean; getAttribute?: (name: string) => string | null } | null;
  if (!el) return false;
  if (el.isContentEditable) return true;
  const tag = (el.tagName ?? '').toUpperCase();
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  const role = el.getAttribute?.('role');
  return role === 'combobox' || role === 'textbox' || role === 'spinbutton';
}

const TOGGLE_CHIP_CLASS =
  'rounded-md border px-2.5 py-1 text-sm cursor-pointer transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500';
const TOGGLE_CHIP_ON = 'bg-primary-50 dark:bg-primary-900/40 border-primary-400 dark:border-primary-500 text-primary-700 dark:text-primary-200';
const TOGGLE_CHIP_OFF = 'bg-surface-0 dark:bg-surface-900 border-surface-300 dark:border-surface-600 hover:bg-surface-100 dark:hover:bg-surface-800';

/** Classes for a pressable filter or choice chip; pair with aria-pressed. */
export function toggleChipClass(on: boolean): string[] {
  return [TOGGLE_CHIP_CLASS, on ? TOGGLE_CHIP_ON : TOGGLE_CHIP_OFF];
}

export function lookedRightText(count: number): string {
  return `${count} ${count === 1 ? 'person says' : 'people say'} it looks right`;
}

export type QueueTab = 'pending' | 'unreviewed';
/** Contributors only ever see the pending queue; the unreviewed tab is a reviewer view. */
export type QueueMode = 'check' | 'review' | 'unreviewed';
export type QueueAction = 'next' | 'prev' | 'confirm' | 'doubt' | 'approve' | 'reject' | 'markReviewed' | 'revert';

export function queueTabFromQuery(value: unknown): QueueTab {
  return value === 'unreviewed' ? 'unreviewed' : 'pending';
}

export function queueMode(role: MetadataViewerRole, tab: QueueTab): QueueMode {
  if (role === 'user') return 'check';
  return tab === 'unreviewed' ? 'unreviewed' : 'review';
}

const QUEUE_KEYS: Record<QueueMode, Record<string, QueueAction>> = {
  check: { l: 'confirm', d: 'doubt' },
  review: { a: 'approve', r: 'reject' },
  unreviewed: { m: 'markReviewed', r: 'revert' },
};

export function queueShortcut(key: string, mode: QueueMode): QueueAction | null {
  const k = key.toLowerCase();
  if (k === 'j' || k === 's') return 'next';
  if (k === 'k') return 'prev';
  return QUEUE_KEYS[mode][k] ?? null;
}
