// Renders the limited formatting subset supported in user custom meanings:
//   **bold**, *italic*, "- " bullet lists, and preserved line breaks.
// User text is HTML-escaped FIRST so no raw markup can be injected; only the
// marker transforms below emit (app-controlled) tags. sanitiseHtml is a final
// defense-in-depth pass.

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function inlineFormat(text: string): string {
  return text
    .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*\n]+)\*/g, '<em>$1</em>');
}

export function parseCustomMeaningHtml(text: string): string {
  if (!text) return '';

  const blocks: string[] = [];
  let textLines: string[] = [];
  let listItems: string[] = [];

  const flushText = () => {
    if (textLines.length) {
      blocks.push(textLines.join('<br>'));
      textLines = [];
    }
  };
  const flushList = () => {
    if (listItems.length) {
      blocks.push(
        '<ul class="list-disc pl-5 my-1 space-y-0.5">'
        + listItems.map(i => `<li>${i}</li>`).join('')
        + '</ul>',
      );
      listItems = [];
    }
  };

  for (const rawLine of text.split('\n')) {
    const escaped = escapeHtml(rawLine);
    const listMatch = /^\s*-\s+(.*)$/.exec(escaped);
    if (listMatch) {
      flushText();
      listItems.push(inlineFormat(listMatch[1]!));
    } else {
      flushList();
      textLines.push(inlineFormat(escaped));
    }
  }
  flushText();
  flushList();

  return sanitiseHtml(blocks.join(''));
}
