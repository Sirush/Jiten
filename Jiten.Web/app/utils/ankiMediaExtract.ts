/**
 * Finds the image and audio a card references. Anki stores both inline in the field HTML: images as
 * `<img src="file.jpg">`, audio as `[sound:file.mp3]`.
 */

/** Video containers whose audio track the sniffer would accept; not worth fetching megabytes for. */
const VIDEO_EXTENSIONS = /\.(mp4|mkv|mov|avi|webm)$/i;

const SVG_EXTENSION = /\.svg$/i;

const IMG_TAG = /<img\b[^>]*>/gi;
const SRC_ATTR = /\bsrc\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]+))/i;
const SOUND_TAG = /\[sound:([^\]]+)\]/g;

const NAMED_ENTITIES: Record<string, string> = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  nbsp: ' ',
};

function decodeEntities(raw: string): string {
  return raw.replace(/&(#x?[0-9a-fA-F]+|[a-zA-Z]+);/g, (match, body: string) => {
    if (body[0] === '#') {
      const code = body[1] === 'x' || body[1] === 'X' ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
      return Number.isFinite(code) && code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
    }
    return NAMED_ENTITIES[body.toLowerCase()] ?? match;
  });
}

export interface ExtractedMediaRef {
  /** The Anki media filename, exactly as AnkiConnect keys it. */
  filename: string;
  /** References past the first, which the import ignores. */
  extraRefs: number;
}

/**
 * First `<img>` whose source is a local Anki media file. Remote and inline sources are skipped: they are
 * not in the media folder, so `retrieveMediaFile` could not return them anyway.
 */
export function extractImageRef(html: string): ExtractedMediaRef | null {
  if (!html) return null;

  let first: string | null = null;
  let count = 0;

  for (const tag of html.match(IMG_TAG) ?? []) {
    const src = SRC_ATTR.exec(tag);
    if (!src) continue;

    const value = decodeEntities(src[2] ?? src[3] ?? src[4] ?? '').trim();
    if (!value) continue;
    if (/^(https?:)?\/\//i.test(value) || /^data:/i.test(value)) continue;
    if (SVG_EXTENSION.test(value)) continue;

    count++;
    first ??= value;
  }

  return first ? { filename: first, extraRefs: count - 1 } : null;
}

/** First `[sound:…]` reference, ignoring Anki's generated text-to-speech tags. */
export function extractAudioRef(html: string): ExtractedMediaRef | null {
  if (!html) return null;

  let first: string | null = null;
  let count = 0;

  SOUND_TAG.lastIndex = 0;
  for (let match = SOUND_TAG.exec(html); match !== null; match = SOUND_TAG.exec(html)) {
    const value = decodeEntities(match[1] ?? '').trim();
    if (!value) continue;
    if (value.startsWith('anki:')) continue;
    if (VIDEO_EXTENSIONS.test(value)) continue;

    count++;
    first ??= value;
  }

  return first ? { filename: first, extraRefs: count - 1 } : null;
}

/** AnkiConnect returns media as base64; the upload needs bytes. */
export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
