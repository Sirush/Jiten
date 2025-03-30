export function convertToRuby(text: string): string {
  const store = useJitenStore();

  if (store.displayFurigana) {
    return text.replace(/([\u4E00-\u9FFF\uFF10-\uFF5A々]+)\[([\u3040-\u309F\u30A0-\u30FF]+)]/g, (_match, kanji, furigana) => {
      return `<ruby lang="ja">${kanji}<rp>(</rp><rt>${furigana}</rt><rp>)</rp></ruby>`;
    });
  } else {
    return text.replace(/([\u4E00-\u9FFF\uFF10-\uFF5A]+)\[([\u3040-\u309F\u30A0-\u30FF]+)]/g, (_match, kanji, _furigana) => {
      return `<ruby lang="ja">${kanji}</ruby>`;
    });
  }
}
