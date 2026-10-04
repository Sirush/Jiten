import { describe, expect, it } from 'vitest';
import { classifyLink } from '../app/utils/linkTarget';

describe('classifyLink', () => {
  it('keeps relative paths internal', () => {
    expect(classifyLink('/decks/1')).toEqual({ kind: 'internal', to: '/decks/1' });
    expect(classifyLink('decks/1')).toEqual({ kind: 'internal', to: '/decks/1' });
    expect(classifyLink('?page=2')).toEqual({ kind: 'internal', to: '?page=2' });
    expect(classifyLink('#a')).toEqual({ kind: 'internal', to: '#a' });
    expect(classifyLink('/updates#update-5')).toEqual({ kind: 'internal', to: '/updates#update-5' });
  });

  it('turns absolute links to the site into paths', () => {
    expect(classifyLink('https://jiten.moe/decks/1?x=1#y')).toEqual({ kind: 'internal', to: '/decks/1?x=1#y' });
    expect(classifyLink('https://www.jiten.moe/updates')).toEqual({ kind: 'internal', to: '/updates' });
    expect(classifyLink('http://JITEN.moe')).toEqual({ kind: 'internal', to: '/' });
    expect(classifyLink('//jiten.moe/decks')).toEqual({ kind: 'internal', to: '/decks' });
  });

  it('treats other origins as external', () => {
    expect(classifyLink('https://community.jiten.moe')).toEqual({ kind: 'external', href: 'https://community.jiten.moe' });
    expect(classifyLink('https://jiten.moe.evil.com/x')).toEqual({ kind: 'external', href: 'https://jiten.moe.evil.com/x' });
    expect(classifyLink('//cdn.x')).toEqual({ kind: 'external', href: 'https://cdn.x' });
    expect(classifyLink('/\\evil.com')).toEqual({ kind: 'external', href: 'https://evil.com' });
  });

  it('passes mailto and tel through as plain links', () => {
    expect(classifyLink('mailto:a@b.c')).toEqual({ kind: 'protocol', href: 'mailto:a@b.c' });
    expect(classifyLink('tel:+81')).toEqual({ kind: 'protocol', href: 'tel:+81' });
  });

  it('refuses script and unknown schemes', () => {
    expect(classifyLink('javascript:alert(1)')).toEqual({ kind: 'unsafe' });
    expect(classifyLink(' JavaScript:alert(1)')).toEqual({ kind: 'unsafe' });
    expect(classifyLink('java\tscript:alert(1)')).toEqual({ kind: 'unsafe' });
    expect(classifyLink('data:text/html,x')).toEqual({ kind: 'unsafe' });
    expect(classifyLink('')).toEqual({ kind: 'unsafe' });
    expect(classifyLink(undefined)).toEqual({ kind: 'unsafe' });
  });
});
