/**
 * Converts URLs in text to clickable anchor tags with security attributes.
 * Links open in new tab with nofollow and noopener for safety.
 * All non-URL text is HTML-escaped to prevent XSS attacks.
 */
export function autoLinkUrls(text: string): string {
  if (!text) return '';

  const urlRegex = /(https?:\/\/[^\s<>"{}|\\^`[\]]+)/gi;

  const parts: string[] = [];
  let lastIndex = 0;
  let match: RegExpExecArray | null;

  while ((match = urlRegex.exec(text)) !== null) {
    if (match.index > lastIndex) {
      parts.push(escapeHtml(text.slice(lastIndex, match.index)));
    }

    const url = match[0];
    const escapedUrl = escapeHtml(url);
    parts.push(`<a href="${escapedUrl}" target="_blank" rel="nofollow noopener" class="text-primary hover:underline">${escapedUrl}</a>`);

    lastIndex = match.index + url.length;
  }

  if (lastIndex < text.length) {
    parts.push(escapeHtml(text.slice(lastIndex)));
  }

  return parts.join('');
}

function escapeHtml(text: string): string {
  const map: Record<string, string> = {
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#039;',
  };
  return text.replace(/[&<>"']/g, (char) => map[char]);
}
