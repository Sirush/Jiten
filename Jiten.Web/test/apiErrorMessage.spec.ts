import { describe, expect, it } from 'vitest';
import { apiErrorMessage } from '~/utils/apiErrorMessage';

describe('apiErrorMessage', () => {
  it('uses a plain string body', () => {
    expect(apiErrorMessage({ data: 'Maximum of 10 study decks reached.' }, 'fallback')).toBe('Maximum of 10 study decks reached.');
  });

  it('uses a message field', () => {
    expect(apiErrorMessage({ data: { message: 'Nope' } }, 'fallback')).toBe('Nope');
  });

  it('uses the detail of a problem response', () => {
    expect(apiErrorMessage({ data: { title: 'Bad Request', detail: 'Deck is locked.' } }, 'fallback')).toBe('Deck is locked.');
  });

  it('uses the title of a validation problem response', () => {
    const data = { title: 'One or more validation errors occurred.', status: 400, errors: { '$.decks[0].words[0].wordId': ['bad'] } };
    expect(apiErrorMessage({ data }, 'fallback')).toBe('One or more validation errors occurred.');
  });

  it('falls back when a problem response has neither detail nor errors', () => {
    expect(apiErrorMessage({ data: { title: 'Not Found', status: 404 } }, 'fallback')).toBe('fallback');
    expect(apiErrorMessage(null, 'fallback')).toBe('fallback');
  });
});
