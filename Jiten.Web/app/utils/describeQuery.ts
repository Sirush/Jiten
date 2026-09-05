const JAPANESE = /[぀-ヿ㐀-鿿]/;
// Particles and punctuation that turn a Japanese string into a clause rather than a title.
const JAPANESE_CLAUSE = /[、。をがでに]|もの|系|っぽい|みたいな|ような/;
// Quotes, brackets and shouted punctuation are title dressing; nobody types them into a description.
const JAPANESE_TITLE_MARK = /[「」『』【】《》〈〉～〜！？!?]/;
const FUNCTION_WORD = /^(a|an|the|about|in|with|of|where|who|and|for|on|at|from|to|that|by|into|like)$/i;

/**
 * Whether typed text reads as a description of media rather than a title. Light novel titles
 * are full sentences, so this is only a hint for pre-fetching description matches alongside
 * the title search, never a reason to skip it.
 */
export function looksLikeDescription(text: string): boolean {
  const trimmed = text.trim();
  if (trimmed.length < 6) return false;
  if (JAPANESE.test(trimmed)) {
    if (JAPANESE_TITLE_MARK.test(trimmed)) return false;
    return trimmed.length >= 8 && JAPANESE_CLAUSE.test(trimmed);
  }
  const words = trimmed.split(/\s+/).filter(Boolean);
  if (words.length >= 5) return true;
  if (words.length < 3) return false;
  // Titles are mostly Title Case; a lowercase phrase with a function word is prose.
  const capitalised = words.filter((w) => /^[A-Z]/.test(w)).length;
  return words.some((w) => FUNCTION_WORD.test(w)) && capitalised * 2 < words.length;
}

export function readDescribeQuery(value: unknown): string | null {
  const raw = Array.isArray(value) ? value[0] : value;
  if (typeof raw !== 'string') return null;
  const trimmed = raw.trim();
  return trimmed.length >= 2 ? trimmed : null;
}
