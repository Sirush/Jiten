export function useJitenAppSchema() {
  useSchemaOrg([
    {
      '@type': 'WebApplication',
      '@id': 'https://jiten.moe/#webapp',
      name: 'Jiten',
      url: 'https://jiten.moe',
      description:
        'Free vocabulary lists, difficulty ratings, Anki decks, personal coverage tracking and a built-in spaced-repetition system for Japanese media: anime, novels, visual novels, video games, manga, dramas and more.',
      applicationCategory: 'EducationalApplication',
      operatingSystem: 'Web',
      offers: { '@type': 'Offer', price: '0', priceCurrency: 'EUR' },
      publisher: { '@id': 'https://jiten.moe/#identity' },
    },
  ]);
}
