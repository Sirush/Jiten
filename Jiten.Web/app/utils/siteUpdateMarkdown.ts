const MARKDOWN_IMAGE = /!\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g;

/** Discord renders no Markdown images but embeds a bare image URL, so image tags collapse to their URL. */
export function toDiscordMarkdown(title: string, body: string): string {
  return `**${title}**\n\n${body.replace(MARKDOWN_IMAGE, '$1')}`;
}

export interface TextSplice {
  text: string;
  cursor: number;
}

function padding(existingNewlines: number): string {
  return '\n'.repeat(Math.max(0, 2 - existingNewlines));
}

/** Replaces the [start, end) selection with `insert` as its own paragraph, so an image never renders inline with text. */
export function spliceAsParagraph(text: string, start: number, end: number, insert: string): TextSplice {
  let before = text.slice(0, start);
  let after = text.slice(end);
  if (before.length > 0 && !before.endsWith('\n')) {
    before = before.replace(/[ \t]+$/, '');
    after = after.replace(/^[ \t]+/, '');
  }
  const lead = before.length === 0 ? '' : padding(/\n{0,2}$/.exec(before)![0].length);
  const trail = after.length === 0 ? '' : padding(/^\n{0,2}/.exec(after)![0].length);
  return { text: before + lead + insert + trail + after, cursor: before.length + lead.length + insert.length };
}
