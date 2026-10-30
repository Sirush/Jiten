export function stripRubyMarkup(text: string): string {
  return text.replace(/([㐀-䶿一-鿿豈-﫿\u{20000}-\u{3FFFF}０-ｚ々ヵヶ]+)\[([぀-ゟ゠-ヿ]+)\]/gu, '$2');
}
