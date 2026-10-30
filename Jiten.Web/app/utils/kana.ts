/** Katakana to hiragana, length preserved so offsets stay valid against the original text. */
export function foldKana(text: string): string {
  let out = '';
  for (const c of text) {
    const code = c.codePointAt(0)!;
    out += code >= 0x30a1 && code <= 0x30f6 ? String.fromCodePoint(code - 0x60) : c;
  }
  return out;
}
