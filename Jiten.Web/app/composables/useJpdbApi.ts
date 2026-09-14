export const useJpdbApi = () => {
  const JpdbRateLimiter = {
    lastRequestTime: 0,
    minInterval: 500,

    async executeWithRateLimit<T>(fn: () => Promise<T>): Promise<T> {
      const now = Date.now();
      const elapsed = now - this.lastRequestTime;

      if (elapsed < this.minInterval) {
        await new Promise((resolve) => setTimeout(resolve, this.minInterval - elapsed));
      }

      this.lastRequestTime = Date.now();
      return await fn();
    },
  };

  interface VocabularyIdPair {
    id1: number;
    id2: number;
  }

  type JpdbCardState = 'known' | 'blacklisted' | 'suspended';

  interface JpdbStudiedCard {
    wordId: number;
    spelling: string;
    state: JpdbCardState;
  }

  class JpdbApiClient {
    private apiKey: string;

    constructor(apiKey: string) {
      if (!apiKey) {
        throw new Error('API key is required');
      }
      this.apiKey = apiKey;
    }

    async getStudiedCards(): Promise<JpdbStudiedCard[]> {
      try {
        const deckIds = await this.getUserDecks();
        deckIds.push('never-forget');
        deckIds.push('blacklist');

        const seen = new Set<string>();
        const uniqueVocab: VocabularyIdPair[] = [];
        for (const deckId of deckIds) {
          for (const vocab of await this.getDeckVocabulary(deckId)) {
            const key = `${vocab.id1}:${vocab.id2}`;
            if (seen.has(key)) continue;
            seen.add(key);
            uniqueVocab.push(vocab);
          }
        }

        return await this.lookupStudiedCards(uniqueVocab);
      } catch (error) {
        throw new Error(`Error getting studied cards: ${error}`);
      }
    }

    private async getUserDecks(): Promise<any[]> {
      const requestBody = { fields: ['id'] };

      const response = await this.makeApiRequest('https://jpdb.io/api/v1/list-user-decks', requestBody);

      const deckIds: any[] = [];
      if (response.decks && Array.isArray(response.decks)) {
        for (const deck of response.decks) {
          if (Array.isArray(deck) && deck.length > 0) {
            deckIds.push(deck[0]);
          }
        }
      }

      return deckIds;
    }

    private async getDeckVocabulary(deckId: number): Promise<VocabularyIdPair[]> {
      const requestBody = { id: deckId, fetch_occurences: false };

      const response = await this.makeApiRequest('https://jpdb.io/api/v1/deck/list-vocabulary', requestBody);

      const vocabularyPairs: VocabularyIdPair[] = [];
      if (response.vocabulary && Array.isArray(response.vocabulary)) {
        for (const vocabItem of response.vocabulary) {
          if (Array.isArray(vocabItem) && vocabItem.length >= 2) {
            vocabularyPairs.push({
              id1: vocabItem[0],
              id2: vocabItem[1],
            });
          }
        }
      }

      return vocabularyPairs;
    }

    private async lookupStudiedCards(vocabularyPairs: VocabularyIdPair[]): Promise<JpdbStudiedCard[]> {
      const chunkSize = 2500;
      const knownStates = new Set(['never-forget', 'known']);
      const result: JpdbStudiedCard[] = [];

      for (let i = 0; i < vocabularyPairs.length; i += chunkSize) {
        const chunk = vocabularyPairs.slice(i, i + chunkSize);
        const requestBody = {
          list: chunk.map((vp) => [vp.id1, vp.id2]),
          fields: ['vid', 'spelling', 'card_state'],
        };

        const response = await this.makeApiRequest('https://jpdb.io/api/v1/lookup-vocabulary', requestBody);

        if (response.vocabulary_info && Array.isArray(response.vocabulary_info)) {
          for (const vocabInfo of response.vocabulary_info) {
            if (!Array.isArray(vocabInfo) || vocabInfo.length < 3) continue;

            const wordId = vocabInfo[0];
            const spelling = vocabInfo[1];
            const states = vocabInfo[2];

            if (typeof spelling !== 'string' || !Array.isArray(states)) continue;
            // JPDB flags a spelling as redundant when another spelling of the same word is the studied one
            if (states.includes('redundant')) continue;

            let state: JpdbCardState | null = null;
            if (states.includes('blacklisted')) state = 'blacklisted';
            else if (states.includes('suspended')) state = 'suspended';
            else if (states.some((s: string) => knownStates.has(s))) state = 'known';

            if (state) result.push({ wordId, spelling, state });
          }
        }
      }

      return result;
    }

    private async makeApiRequest(url: string, requestBody: any): Promise<any> {
      return await JpdbRateLimiter.executeWithRateLimit(async () => {
        const response = await $fetch(url, {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${this.apiKey}`,
            'Content-Type': 'application/json',
          },
          body: requestBody,
        });

        return response;
      });
    }
  }

  return {
    JpdbApiClient,
  };
};
