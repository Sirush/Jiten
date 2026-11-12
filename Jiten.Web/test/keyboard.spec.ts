import { describe, expect, it } from 'vitest';
import { isTypingTarget } from '../app/utils/keyboard';

describe('isTypingTarget', () => {
  it('recognises fields and comboboxes', () => {
    expect(isTypingTarget({ tagName: 'INPUT' })).toBe(true);
    expect(isTypingTarget({ tagName: 'textarea' })).toBe(true);
    expect(isTypingTarget({ tagName: 'DIV', isContentEditable: true })).toBe(true);
    expect(isTypingTarget({ tagName: 'SPAN', getAttribute: () => 'combobox' })).toBe(true);
    expect(isTypingTarget({ tagName: 'BUTTON', getAttribute: () => null })).toBe(false);
    expect(isTypingTarget(null)).toBe(false);
  });
});
