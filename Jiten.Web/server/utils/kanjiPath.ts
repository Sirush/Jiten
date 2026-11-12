const DOUBLE_ENCODED_KANJI = /^\/kanji\/((?:%25[0-9A-Fa-f]{2})+)\/?$/;

/** Returns the single-encoded kanji path for a double-encoded one (`/kanji/%25E7%25BF%258C`), or null for any other path. */
export function singleEncodedKanjiPath(path: string): string | null {
  const match = DOUBLE_ENCODED_KANJI.exec(path);
  if (!match) return null;

  const encoded = match[1]!.replace(/%25/g, '%').toUpperCase();
  try {
    const characters = [...decodeURIComponent(encoded)];
    if (characters.length !== 1 || characters[0]!.codePointAt(0)! < 0x80) return null;
  } catch {
    return null;
  }
  return `/kanji/${encoded}`;
}
