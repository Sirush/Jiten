const japaneseChar = /[぀-ヿ㐀-䶿一-鿿豈-﫿ｦ-ﾟ]/gu;
const latinLetter = /[A-Za-z]/g;

/** Mostly-Japanese titles and descriptions follow the Japanese font; an English blurb quoting a name keeps the UI font. */
export function isMostlyJapanese(text: string | null | undefined): boolean {
  if (!text) return false;
  const japanese = text.match(japaneseChar)?.length ?? 0;
  const latin = text.match(latinLetter)?.length ?? 0;
  return japanese > 0 && japanese >= latin;
}

/** Bind with v-bind on the element holding a title or description; `ja-general` lets "words only" opt it out in main.css. */
export function japaneseTextAttrs(text: string | null | undefined): { lang?: string; class?: string } {
  return isMostlyJapanese(text) ? { lang: 'ja', class: 'ja-general' } : {};
}
