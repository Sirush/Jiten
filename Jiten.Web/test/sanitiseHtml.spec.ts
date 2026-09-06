import { describe, expect, it } from 'vitest';
import { escapeHtml, sanitiseHtml } from '../app/utils/sanitiseHtml';
import { parseCustomSentenceHtml } from '../app/utils/parseCustomSentence';

describe('parseCustomSentenceHtml', () => {
  it('escapes markup split across subtitle cues before marking the word', () => {
    const html = parseCustomSentenceHtml('<img src=x onerror=alert(1) > **食べる**');
    expect(html).not.toContain('<img');
    expect(html).toContain('&lt;img src=x onerror=alert(1) &gt;');
    expect(html).toContain('<span class="text-primary-500 dark:text-primary-500 font-bold">食べる</span>');
  });
});

describe('sanitiseHtml', () => {
  it('strips unquoted event handlers and javascript urls', () => {
    expect(sanitiseHtml('<img src=x onerror=alert(1)>')).toBe('<img src=x>');
    expect(sanitiseHtml('<a href="javascript:alert(1)">x</a>')).toBe('<a>x</a>');
  });
});

describe('escapeHtml', () => {
  it('escapes the five html-significant characters', () => {
    expect(escapeHtml(`<a href="x">&'</a>`)).toBe('&lt;a href=&quot;x&quot;&gt;&amp;&#39;&lt;/a&gt;');
  });
});
