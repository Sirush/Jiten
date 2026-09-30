import { describe, expect, it } from 'vitest';
import { spliceAsParagraph, toDiscordMarkdown } from '../app/utils/siteUpdateMarkdown';

describe('toDiscordMarkdown', () => {
  it('collapses image tags to bare URLs', () => {
    const body = 'Before\n\n![shot](https://cdn.jiten.moe/site-updates/a.webp)\n\n![](https://cdn.jiten.moe/site-updates/b.webp "title")';
    expect(toDiscordMarkdown('T', body)).toBe(
      '**T**\n\nBefore\n\nhttps://cdn.jiten.moe/site-updates/a.webp\n\nhttps://cdn.jiten.moe/site-updates/b.webp',
    );
  });

  it('leaves links and other markdown untouched', () => {
    const body = '## Heading\n\n- [link](https://jiten.moe) and **bold**';
    expect(toDiscordMarkdown('T', body)).toBe(`**T**\n\n${body}`);
  });
});

describe('spliceAsParagraph', () => {
  it('opens a paragraph when inserting mid-sentence', () => {
    expect(spliceAsParagraph('abc def', 3, 3, 'IMG')).toEqual({ text: 'abc\n\nIMG\n\ndef', cursor: 8 });
    expect(spliceAsParagraph('abc  def', 4, 4, 'IMG')).toEqual({ text: 'abc\n\nIMG\n\ndef', cursor: 8 });
  });

  it('keeps indentation when inserting at a line start', () => {
    expect(spliceAsParagraph('abc\n  code', 4, 4, 'IMG')).toEqual({ text: 'abc\n\nIMG\n\n  code', cursor: 8 });
  });

  it('reuses existing line breaks', () => {
    expect(spliceAsParagraph('abc\n\ndef', 5, 5, 'IMG')).toEqual({ text: 'abc\n\nIMG\n\ndef', cursor: 8 });
    expect(spliceAsParagraph('abc\ndef\n', 4, 4, 'IMG')).toEqual({ text: 'abc\n\nIMG\n\ndef\n', cursor: 8 });
    expect(spliceAsParagraph('', 0, 0, 'IMG')).toEqual({ text: 'IMG', cursor: 3 });
    expect(spliceAsParagraph('abc', 3, 3, 'IMG')).toEqual({ text: 'abc\n\nIMG', cursor: 8 });
  });

  it('replaces the selection', () => {
    expect(spliceAsParagraph('a\n\nOLD\n\nb', 3, 6, 'IMG')).toEqual({ text: 'a\n\nIMG\n\nb', cursor: 6 });
  });
});
