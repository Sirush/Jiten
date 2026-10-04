import { describe, expect, it } from 'vitest';
import { linkifySelection, spliceAsParagraph, toDiscordMarkdown } from '../app/utils/siteUpdateMarkdown';

describe('toDiscordMarkdown', () => {
  it('collapses image tags to bare URLs', () => {
    const body = 'Before\n\n![shot](https://cdn.jiten.moe/site-updates/a.webp)\n\n![](https://cdn.jiten.moe/site-updates/b.webp "title")';
    expect(toDiscordMarkdown('T', body)).toBe(
      '**T**\n\nBefore\n\nhttps://cdn.jiten.moe/site-updates/a.webp\n\nhttps://cdn.jiten.moe/site-updates/b.webp',
    );
  });

  it('leaves absolute links and other markdown untouched', () => {
    const body = '## Heading\n\n- [link](https://jiten.moe) and **bold**, [mail](mailto:a@b.c), [cdn](//cdn.x/a)';
    expect(toDiscordMarkdown('T', body)).toBe(`**T**\n\n${body}`);
  });

  it('absolutises relative link targets', () => {
    const body = '[a](/decks/1) [b](decks/2 "t") [c](#update-3) [d](</x y>)\n\n[r]: /ref\n  [s]: <rel>';
    expect(toDiscordMarkdown('T', body)).toBe(
      '**T**\n\n[a](https://jiten.moe/decks/1) [b](https://jiten.moe/decks/2 "t") [c](https://jiten.moe/updates#update-3) [d](<https://jiten.moe/x%20y>)\n\n[r]: https://jiten.moe/ref\n  [s]: <https://jiten.moe/rel>',
    );
  });

  it('absolutises a link wrapped around an image', () => {
    expect(toDiscordMarkdown('T', '[![](https://cdn.x/a.webp)](/decks/1)')).toBe('**T**\n\n[https://cdn.x/a.webp](https://jiten.moe/decks/1)');
  });

  it('leaves code untouched', () => {
    const body = 'See `[a](/x)` and\n\n```\n[b](/y)\n[r]: /z\n```\n\n[c](/w)';
    expect(toDiscordMarkdown('T', body)).toBe(`**T**\n\n${body.replace('[c](/w)', '[c](https://jiten.moe/w)')}`);
  });
});

describe('linkifySelection', () => {
  it('wraps the selection in a link when a URL is pasted', () => {
    expect(linkifySelection('see this deck', 9, 13, ' https://jiten.moe/decks/1 ')).toEqual({
      text: 'see this [deck](https://jiten.moe/decks/1)',
      cursor: 42,
    });
  });

  it('brackets URLs containing parentheses', () => {
    expect(linkifySelection('x', 0, 1, 'https://en.wikipedia.org/wiki/A_(b)')?.text).toBe('[x](<https://en.wikipedia.org/wiki/A_(b)>)');
  });

  it('pastes normally otherwise', () => {
    expect(linkifySelection('abc', 1, 1, 'https://x.com')).toBeNull();
    expect(linkifySelection('abc', 0, 3, 'not a url')).toBeNull();
    expect(linkifySelection('abc', 0, 3, 'https://x.com and more')).toBeNull();
    expect(linkifySelection('abc', 0, 3, 'javascript:alert(1)')).toBeNull();
    expect(linkifySelection('a\n\nb', 0, 4, 'https://x.com')).toBeNull();
    expect(linkifySelection('a  b', 1, 3, 'https://x.com')).toBeNull();
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
