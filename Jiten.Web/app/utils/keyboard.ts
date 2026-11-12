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
