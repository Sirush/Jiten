export function convertToRubyWithFurigana(text: string, displayFurigana: boolean): string {
  if (displayFurigana) {
    return text.replace(/([一-鿿０-ｚ々ヵヶ]+)\[([぀-ゟ゠-ヿ]+)]/g, (_match, kanji, furigana) => {
      return `<ruby lang="ja">${kanji}<rp>(</rp><rt>${furigana}</rt><rp>)</rp></ruby>`;
    });
  }

  return text.replace(/([一-鿿０-ｚヵヶ]+)\[([぀-ゟ゠-ヿ]+)]/g, (_match, kanji, _furigana) => {
    return `<ruby lang="ja">${kanji}</ruby>`;
  });
}
