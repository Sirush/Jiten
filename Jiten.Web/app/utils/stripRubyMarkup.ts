export function stripRubyMarkup(text: string): string {
  return text.replace(/([一-鿿０-ｚ々ヵヶ]+)\[([぀-ゟ゠-ヿ]+)\]/g, '$2');
}
