const dangerous = /<\s*\/?\s*(script|iframe|object|embed|form|input|textarea|button)\b[^>]*>/gi;
const onHandlers = /\s+on\w+\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)/gi;
const scriptUrls = /\s+(href|src|action|formaction|xlink:href)\s*=\s*(?:"\s*javascript:[^"]*"|'\s*javascript:[^']*'|javascript:[^\s>]+)/gi;

/**
 * Lightweight HTML sanitiser for trusted sources (own API, app-generated markup).
 * Strips script/iframe/embed tags, inline event handlers and javascript: URLs as defense-in-depth.
 */
export function sanitiseHtml(html: string): string {
  if (!html) return '';
  return html.replace(dangerous, '').replace(onHandlers, '').replace(scriptUrls, '');
}

const entities: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

export function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => entities[c]!);
}
