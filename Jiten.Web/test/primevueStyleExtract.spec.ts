import { describe, expect, it } from 'vitest';
import {
  currentPrimevueStylesheet,
  insertStylesheetLink,
  mergePrimevueStyles,
  stripPrimevueStyles,
} from '../server/utils/primevueStyles';

const tag = (name: string, css: string, attrs = ' type="text/css" ') =>
  `<style${attrs}data-primevue-style-id="${name}">${css}</style>`;

describe('primevue style extraction', () => {
  it('strips inline css but keeps stub tags with their attributes', () => {
    const head = [`<meta charset="utf-8">${tag('base', '.p-a{x:1}')}${tag('button-style', '.p-b{y:2}')}`];
    const extracted = stripPrimevueStyles(head);

    expect(extracted).toEqual([
      ['base', '.p-a{x:1}'],
      ['button-style', '.p-b{y:2}'],
    ]);
    expect(head[0]).toContain('<style type="text/css" data-primevue-style-id="base"></style>');
    expect(head[0]).toContain('<style type="text/css" data-primevue-style-id="button-style"></style>');
    expect(head[0]).not.toContain('.p-a{x:1}');
    expect(head[0]).toContain('<meta charset="utf-8">');
  });

  it('leaves non-primevue styles untouched', () => {
    const head = [`<style>.tw{a:1}</style>${tag('base', '.p-a{x:1}')}`];
    stripPrimevueStyles(head);
    expect(head[0]).toContain('<style>.tw{a:1}</style>');
  });

  it('handles multiline css and empty chunks', () => {
    const head = [undefined as unknown as string, tag('global-style', '.a{\n b:2;\n}')];
    const extracted = stripPrimevueStyles(head);
    expect(extracted).toEqual([['global-style', '.a{\n b:2;\n}']]);
  });

  it('inserts the link exactly before the first stub', () => {
    const head = [`<link rel="preload" href="x">${tag('base', '')}${tag('badge-style', '')}`];
    stripPrimevueStyles(head);
    insertStylesheetLink(head, '/pv-styles/abc.css');
    expect(head[0]).toMatch(
      /<link rel="preload" href="x"><link rel="stylesheet" href="\/pv-styles\/abc\.css"><style[^>]*data-primevue-style-id="base"/,
    );
  });

  it('grows the superset across renders and keeps first-seen order and a stable hash', () => {
    mergePrimevueStyles([
      ['base', '.p-a{x:1}'],
      ['button-style', '.p-b{y:2}'],
    ]);
    const first = currentPrimevueStylesheet();
    expect(first.css).toBe('.p-a{x:1}\n.p-b{y:2}');
    expect(first.hash).toMatch(/^[0-9a-f]{12}$/);

    // Re-merging the same names (even with different content) must not change anything
    mergePrimevueStyles([['base', '.p-DIFFERENT{}']]);
    expect(currentPrimevueStylesheet()).toEqual(first);

    mergePrimevueStyles([['chip-style', '.p-c{z:3}']]);
    const grown = currentPrimevueStylesheet();
    expect(grown.css).toBe('.p-a{x:1}\n.p-b{y:2}\n.p-c{z:3}');
    expect(grown.hash).not.toBe(first.hash);
  });
});
