import { describe, expect, it } from 'vitest';
import { DeckRelationshipType, Genre, LinkType, MediaType } from '../app/types/enums';
import { MetadataEditStatus, MetadataField, type EditableDeck } from '../app/types/metadataEdits';
import {
  aliasError,
  noteHasReason,
  toggleNoteReason,
  approvalExclusion,
  leftOutPhrase,
  buildSubmission,
  conflictClashes,
  queueMode,
  queueShortcut,
  queueTabFromQuery,
  describeChange,
  draftChanges,
  draftFromEditable,
  editStatus,
  fieldsFromFlags,
  linkUrlError,
  relationshipLabel,
  revertSummary,
  submissionError,
  textDiff,
  diffExcerpt,
  diffPieces,
  isRewrite,
  isWhitespaceOnlyChange,
  normaliseSourceUrl,
  sourcesError,
  rebaseDraft,
  submitOutcome,
} from '../app/utils/metadataEdits';

const DECK_ID = 10;

const editable = (): EditableDeck => ({
  deckId: DECK_ID,
  coverName: 'nocover.jpg',
  mediaType: MediaType.VisualNovel,
  characterCount: 1000,
  releaseYear: 2002,
  originalTitle: 'ひぐらしのなく頃に',
  romajiTitle: 'Higurashi no Naku Koro ni',
  englishTitle: null,
  aliases: ['Higurashi'],
  releaseDate: '2002-08-01',
  description: 'A village.',
  genres: [Genre.Mystery, Genre.Horror],
  tags: [{ tagId: 1, name: 'Time Loop', percentage: 90 }],
  links: [{ linkType: LinkType.Vndb, url: 'https://vndb.org/v67' }],
  relationships: [
    {
      sourceDeckId: 20,
      targetDeckId: DECK_ID,
      relationshipType: DeckRelationshipType.Sequel,
      otherDeckId: 20,
      otherDeckTitle: '解',
      otherDeckMediaType: MediaType.VisualNovel,
    },
    {
      sourceDeckId: DECK_ID,
      targetDeckId: 30,
      relationshipType: DeckRelationshipType.SameSeries,
      otherDeckId: 30,
      otherDeckTitle: 'Legacy',
      otherDeckMediaType: MediaType.VisualNovel,
    },
  ],
  characterNames: ['前原圭一', '前原', '圭一'],
  franchise: null,
  historyCount: 3,
  pending: [],
  viewer: { role: 'user', hasDisplayName: true, emailConfirmed: true, banned: false },
});

describe('buildSubmission', () => {
  it('sends nothing for an untouched draft', () => {
    const original = draftFromEditable(editable());
    const { payload, fields } = buildSubmission(original, draftFromEditable(editable()));
    expect(payload).toEqual({});
    expect(fields).toEqual([]);
  });

  it('sends only the changed fields', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.releaseDate = '2002-08-10';
    draft.aliases.push(' ひぐらし ');
    draft.genres = [Genre.Horror, Genre.Mystery];

    const { payload, fields } = buildSubmission(original, draft);
    expect(payload).toEqual({
      releaseDate: '2002-08-10',
      aliases: ['Higurashi', 'ひぐらし'],
      base: { releaseDate: '2002-08-01', aliases: ['Higurashi'] },
    });
    expect(fields).toEqual([MetadataField.ReleaseDate, MetadataField.Aliases]);
  });

  it('clears the release date with an empty string', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.releaseDate = '';
    expect(buildSubmission(original, draft).payload).toEqual({ releaseDate: '', base: { releaseDate: '2002-08-01' } });
  });

  it('sends the picked deck for a franchise, without a base', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.franchise = { franchiseId: 7, originalTitle: 'ひぐらし', romajiTitle: null, englishTitle: null, withDeckId: 20 };

    const { payload, fields } = buildSubmission(original, draft);
    expect(payload).toEqual({ franchiseDeckId: 20 });
    expect(fields).toEqual([MetadataField.Franchise]);
    expect(describeChange(draftChanges(original, draft, fields)[0]!)).toEqual({
      kind: 'list',
      added: [{ key: '7', label: 'ひぐらし', href: '/franchise/7' }],
      removed: [],
      changed: [],
    });
  });

  it('describes a franchise the edit will start with the picked deck', () => {
    const change = { field: MetadataField.Franchise, before: null, after: { franchiseId: null, originalTitle: '解', romajiTitle: null, englishTitle: null, withDeckId: 20 } };
    expect(describeChange(change)).toEqual({
      kind: 'list',
      added: [{ key: 'with:20', label: '解', role: 'New franchise with', href: '/decks/media/20/detail' }],
      removed: [],
      changed: [],
    });
  });

  it('ignores blank link rows and trims urls', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.links.push({ linkType: LinkType.Anilist, url: '   ' });
    draft.links[0]!.url = ' https://vndb.org/v67 ';
    expect(buildSubmission(original, draft).fields).toEqual([]);
  });

  it('sends every savable edge and keeps legacy group rows out', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.relationships.push({
      sourceDeckId: 40,
      targetDeckId: DECK_ID,
      relationshipType: DeckRelationshipType.Fandisc,
      otherDeckId: 40,
      otherDeckTitle: '礼',
    });

    const { payload, fields } = buildSubmission(original, draft);
    expect(fields).toEqual([MetadataField.Relationships]);
    expect(payload.relationships).toEqual([
      { sourceDeckId: 20, targetDeckId: DECK_ID, relationshipType: DeckRelationshipType.Sequel },
      { sourceDeckId: 40, targetDeckId: DECK_ID, relationshipType: DeckRelationshipType.Fandisc },
    ]);
  });

  it('splits character names into added full names and removed surfaces', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.characterNames = ['前原圭一', '前原'];
    draft.addedNames = [{ input: '竜宮 レナ', surfaces: ['竜宮レナ', '竜宮', 'レナ'] }];

    const { payload, fields } = buildSubmission(original, draft);
    expect(fields).toEqual([MetadataField.CharacterNames]);
    expect(payload.addCharacterNames).toEqual(['竜宮 レナ']);
    expect(payload.removeCharacterNames).toEqual(['圭一']);

    const [change] = draftChanges(original, draft, fields);
    expect(describeChange(change!)).toMatchObject({ added: [{ label: 'レナ' }, { label: '竜宮' }, { label: '竜宮レナ' }], removed: [{ label: '圭一' }] });
  });

  it('detects a tag percentage change', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.tags[0]!.percentage = 40;
    const { payload, fields } = buildSubmission(original, draft);
    expect(payload.tags).toEqual([{ tagId: 1, percentage: 40 }]);
    const [change] = draftChanges(original, draft, fields);
    expect(describeChange(change!)).toEqual({ kind: 'list', added: [], removed: [], changed: [{ key: '1', label: 'Time Loop', before: '90%', after: '40%' }] });
  });
});

describe('submissionError', () => {
  const check = (mutate: (d: ReturnType<typeof draftFromEditable>) => void, sources: string[] = [], note = '') => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    mutate(draft);
    const { fields } = buildSubmission(original, draft);
    return submissionError({ original, draft, fields, sources, note });
  };

  it('accepts a factual change with or without a source', () => {
    expect(check((d) => (d.releaseDate = '2002-08-10'))).toBeNull();
    expect(check((d) => (d.releaseDate = '2002-08-10'), ['https://vndb.org/r1'])).toBeNull();
    expect(check((d) => (d.releaseDate = '2002-08-10'), ['not a link'])).toBe('Sources need to be links, such as https://vndb.org/v17.');
  });

  it('asks for a note on the original title, media type and description', () => {
    expect(
      check((d) => {
        d.mediaType = MediaType.Novel;
        d.description = 'New text.';
      }, [])
    ).toBe('Add a note explaining the change to media type and description.');
    expect(check((d) => (d.mediaType = MediaType.Novel), [], 'It is a novel.')).toBeNull();
  });

  it('rejects malformed sources and bad links', () => {
    expect(check((d) => (d.releaseDate = '2002-08-10'), ['not a link'])).toBe('Sources need to be links, such as https://vndb.org/v17.');
    expect(check((d) => (d.releaseDate = '2002-08-10'), ['vndb.org/r1'])).toBeNull();
    expect(check((d) => (d.links[0]!.url = 'https://anilist.co/anime/1'), ['https://vndb.org/r1'])).toBe('Fix the link errors before submitting.');
  });

  it('refuses an empty submission', () => {
    expect(check(() => {})).toBe('Nothing changed yet.');
  });
});

describe('linkUrlError', () => {
  it('checks the host for typed links', () => {
    expect(linkUrlError(LinkType.Vndb, 'https://vndb.org/v67')).toBeNull();
    expect(linkUrlError(LinkType.Vndb, 'https://example.com/v67')).toBe('VNDB links must point to vndb.org.');
    expect(linkUrlError(LinkType.Anilist, 'https://www.anilist.co/anime/1')).toBeNull();
    expect(linkUrlError(LinkType.Amazon, 'https://www.amazon.co.jp/dp/1')).toBeNull();
  });

  it('accepts any website for the generic type and ignores blank rows', () => {
    expect(linkUrlError(LinkType.Web, 'https://example.com')).toBeNull();
    expect(linkUrlError(LinkType.Vndb, '  ')).toBeNull();
  });

  it('rejects non-links and other schemes', () => {
    expect(linkUrlError(LinkType.Web, 'not a link')).toBe("That doesn't look like a link.");
    expect(linkUrlError(LinkType.Web, 'ftp://example.com')).toBe('Links must start with http:// or https://.');
  });
});

describe('aliasError', () => {
  it('blocks links, duplicates and a full list', () => {
    expect(aliasError('www.example.com', [])).toBe("Aliases can't contain links.");
    expect(aliasError('Higurashi', ['Higurashi'])).toBe('That alias is already on this deck.');
    expect(
      aliasError(
        'New',
        Array.from({ length: 15 }, (_, i) => `a${i}`)
      )
    ).toBe('This deck already has 15 aliases. Remove one first.');
    expect(aliasError('ひぐらし', ['Higurashi'])).toBeNull();
  });
});

describe('submitOutcome', () => {
  it('reports an all-live edit', () => {
    expect(submitOutcome([MetadataField.ReleaseDate], [])).toMatchObject({ kind: 'live', title: 'Saved. Thanks for helping improve the database.' });
  });

  it('reports an all-pending edit', () => {
    expect(submitOutcome([], [MetadataField.Genres])).toEqual({
      kind: 'pending',
      title: 'Sent for review. Thanks for helping improve the database.',
      body: "An editor will check it first and you'll be notified when it has been processed.",
    });
  });

  it('names what went live and what waits', () => {
    const outcome = submitOutcome([MetadataField.Aliases, MetadataField.ReleaseDate], [MetadataField.Genres]);
    expect(outcome.kind).toBe('mixed');
    expect(outcome.title).toBe('Release date and aliases are live.');
    expect(outcome.body).toBe("Genres will go live once an editor approves them. You'll get a notification.");
    expect(submitOutcome([MetadataField.ReleaseDate], [MetadataField.Description]).body).toMatch(/^Description will go live once an editor approves it\./);
    expect(submitOutcome([MetadataField.ReleaseDate], [MetadataField.Description]).title).toBe('Release date is live.');
  });

  it('mentions reanalysis when names went live', () => {
    expect(submitOutcome([MetadataField.CharacterNames], []).body).toContain('next time the deck is analysed');
  });
});

describe('revertSummary', () => {
  it('describes a partial revert', () => {
    expect(revertSummary([MetadataField.RomajiTitle], [MetadataField.EnglishTitle])).toBe(
      'Romaji title was restored. English title was left alone because it had been changed again since.'
    );
    expect(revertSummary([MetadataField.RomajiTitle], [])).toBe('Edit reverted. The previous values are back on the deck.');
  });
});

describe('relationshipLabel', () => {
  it('names the other deck from the edited deck', () => {
    const base = { otherDeckTitle: 'B', relationshipType: DeckRelationshipType.Sequel };
    expect(relationshipLabel({ ...base, sourceDeckId: 1, targetDeckId: 2, otherDeckId: 2 })).toBe('Prequel: B');
    expect(relationshipLabel({ ...base, sourceDeckId: 2, targetDeckId: 1, otherDeckId: 2 })).toBe('Sequel: B');
  });
});

describe('editStatus', () => {
  it('hides the review state from contributors', () => {
    const item = { publicStatus: 'live' as const, status: MetadataEditStatus.Applied };
    expect(editStatus(item, 'user').label).toBe('Live');
    expect(editStatus(item, 'editor').label).toBe('Live, unreviewed');
    expect(editStatus({ publicStatus: 'live', status: null }, 'editor').label).toBe('Live');
  });
});

describe('fieldsFromFlags', () => {
  it('returns fields in display order', () => {
    expect(fieldsFromFlags(MetadataField.Description | MetadataField.Aliases)).toEqual([MetadataField.Aliases, MetadataField.Description]);
  });
});

describe('conflictClashes', () => {
  const tag = (tagId: number, name: string, percentage: number) => ({ tagId, name, percentage });

  it('keeps only tags whose percentage both sides changed differently', () => {
    const clashes = conflictClashes({
      field: MetadataField.Tags,
      editBefore: [tag(1, 'Time Loop', 90), tag(2, 'Village', 50)],
      current: [tag(1, 'Time Loop', 80), tag(2, 'Village', 50), tag(3, 'School', 30)],
      editAfter: [tag(1, 'Time Loop', 40), tag(2, 'Village', 60), tag(3, 'School', 30)],
    });
    expect(clashes).toEqual([{ label: 'Time Loop', current: '80%', edit: '40%' }]);
  });

  it('compares typed links by URL and ignores generic websites', () => {
    const clashes = conflictClashes({
      field: MetadataField.Links,
      editBefore: [{ linkType: LinkType.Vndb, url: 'https://vndb.org/v1' }],
      current: [
        { linkType: LinkType.Vndb, url: 'https://vndb.org/v2' },
        { linkType: LinkType.Web, url: 'https://a.example' },
      ],
      editAfter: [
        { linkType: LinkType.Vndb, url: 'https://vndb.org/v3' },
        { linkType: LinkType.Web, url: 'https://b.example' },
      ],
    });
    expect(clashes).toEqual([{ label: 'VNDB', current: 'https://vndb.org/v2', edit: 'https://vndb.org/v3' }]);
  });

  it('has no item-level clashes for scalar fields', () => {
    expect(conflictClashes({ field: MetadataField.ReleaseDate, editBefore: 'a', current: 'b', editAfter: 'c' })).toEqual([]);
  });
});

describe('review queue modes', () => {
  it('reads the tab from the query and only gives reviewers the unreviewed view', () => {
    expect(queueTabFromQuery('unreviewed')).toBe('unreviewed');
    expect(queueTabFromQuery(['unreviewed'])).toBe('pending');
    expect(queueTabFromQuery(undefined)).toBe('pending');
    expect(queueMode('user', 'unreviewed')).toBe('check');
    expect(queueMode('reviewer', 'pending')).toBe('review');
    expect(queueMode('editor', 'unreviewed')).toBe('unreviewed');
  });

  it('maps keys to the actions of each mode', () => {
    expect(queueShortcut('J', 'unreviewed')).toBe('next');
    expect(queueShortcut('s', 'check')).toBe('next');
    expect(queueShortcut('k', 'review')).toBe('prev');
    expect(queueShortcut('l', 'check')).toBe('confirm');
    expect(queueShortcut('a', 'review')).toBe('approve');
    expect(queueShortcut('r', 'review')).toBe('reject');
    expect(queueShortcut('r', 'unreviewed')).toBe('revert');
    expect(queueShortcut('m', 'unreviewed')).toBe('markReviewed');
    expect(queueShortcut('a', 'unreviewed')).toBeNull();
    expect(queueShortcut('m', 'review')).toBeNull();
  });
});

describe('textDiff', () => {
  const render = (segments: { kind: string; text: string }[]) => segments.map((s) => (s.kind === 'same' ? s.text : `[${s.kind}:${s.text}]`)).join('');

  it('marks only the changed words in Latin text', () => {
    expect(render(textDiff('Yuji is from a famous family.', 'Yuji is from a famous sorcerer family.'))).toBe('Yuji is from a famous [add:sorcerer ]family.');
  });

  it('diffs Japanese one character at a time', () => {
    expect(render(textDiff('前原圭一は先生と話した。', '前原圭一は先輩と話した。'))).toBe('前原圭一は先[remove:生][add:輩]と話した。');
  });

  it('treats a missing side as a full add or remove', () => {
    expect(render(textDiff(null, 'New.'))).toBe('[add:New.]');
    expect(render(textDiff('Old.', ''))).toBe('[remove:Old.]');
  });

  it('cuts long unchanged runs down to the context around a change', () => {
    const long = 'a'.repeat(100);
    const excerpt = diffExcerpt(textDiff(`${long} old ${long}`, `${long} new ${long}`), 10);
    expect(excerpt[0]).toEqual({ kind: 'gap', text: '…' });
    expect(excerpt.at(-1)).toEqual({ kind: 'gap', text: '…' });
    expect(excerpt.filter((s) => s.kind === 'same').every((s) => s.text.length <= 10)).toBe(true);
  });

  it('folds short unchanged words between edits into one replacement', () => {
    expect(render(textDiff('The story follows a group of students.', 'One day Minakami witnessed a ritual.'))).not.toMatch(/\]\w+ \[/);
    expect(render(textDiff('She told the truth about it.', 'He hid the facts about it.'))).toBe('[remove:She told the truth][add:He hid the facts] about it.');
  });

  it('treats a mostly different text as a rewrite and a small edit as a diff', () => {
    const before = 'Subarashiki Hibi is a story told in seven chapters spanning one July in Tokyo.';
    const after = 'One day, Minakami Yuki witnesses a toy falling through the air on the rooftop.';
    expect(isRewrite(before, after, textDiff(before, after))).toBe(true);
    const edited = before.replace('seven', 'six');
    expect(isRewrite(before, edited, textDiff(before, edited))).toBe(false);
  });

  it('detects changes to line breaks and spacing alone', () => {
    expect(isWhitespaceOnlyChange('Escape!\n\nA remake', 'Escape!\nA remake')).toBe(true);
    expect(isWhitespaceOnlyChange('Escape!', 'Escape?')).toBe(false);
  });

  it('draws removed line breaks as a mark instead of an invisible struck newline', () => {
    const pieces = diffPieces(textDiff('escape!\n\nA remake', 'escape!A remake'), true);
    expect(pieces).toContainEqual({ kind: 'remove', text: '¶', lineBreak: true });
    expect(pieces.some((p) => p.text.includes('\n'))).toBe(false);
  });

  it('leaves a paragraph break unmarked when only its blank line count changed', () => {
    expect(textDiff('One.\n\nTwo.', 'One.\nTwo.')).toEqual([{ kind: 'same', text: 'One.\nTwo.' }]);
  });

  it('caps compact excerpts at a few changes and a bounded length', () => {
    const words = Array.from({ length: 40 }, (_, i) => `word${i}`);
    const changed = words.map((w, i) => (i % 8 === 0 ? `${w}x` : w));
    const excerpt = diffExcerpt(textDiff(words.join(' '), changed.join(' ')));
    expect(excerpt.at(-1)).toEqual({ kind: 'gap', text: ' … +3 more' });
    const added = diffExcerpt(textDiff('Short start.', `Short start. ${'Long added paragraph. '.repeat(20)}`));
    expect(added.find((s) => s.kind === 'add')!.text.length).toBeLessThanOrEqual(81);
  });

  it('cuts excerpt context at word boundaries', () => {
    const before = 'Patton turns to an unlikely ally: the most formidable and brutal warrior of them all.';
    const after = before.replace('ally:', 'ally: Rance,');
    const same = diffExcerpt(textDiff(before, after)).filter((s) => s.kind === 'same');
    expect(same[0]!.text).toBe(' an unlikely ally: ');
    expect(same.at(-1)!.text).toBe('the most formidable ');
  });
});

describe('sources', () => {
  it('adds https to a bare host and reports problems apart from other errors', () => {
    expect(normaliseSourceUrl(' vndb.org/v17 ')).toBe('https://vndb.org/v17');
    expect(normaliseSourceUrl('just words')).toBe('just words');
    expect(sourcesError([''])).toBeNull();
    expect(sourcesError(['just words'])).toBe('Sources need to be links, such as https://vndb.org/v17.');
  });
});

describe('approvalExclusion', () => {
  const changes = [
    { field: MetadataField.ReleaseDate, before: null, after: '2007-03-29' },
    { field: MetadataField.Genres, before: [1], after: [2, 3] },
    { field: MetadataField.Aliases, before: [], after: ['one', 'two'] },
  ];

  it('counts list items one by one and other fields as a whole', () => {
    expect(approvalExclusion(changes, 0, {})).toEqual({ wholeFields: [], items: [], total: 6, leftOut: 0 });
  });

  it('keeps single items apart from fields left out whole', () => {
    const exclusion = approvalExclusion(changes, MetadataField.ReleaseDate, { [MetadataField.Genres]: ['1', '2'], [MetadataField.Aliases]: ['one', 'two'] });
    expect(exclusion).toEqual({
      wholeFields: [MetadataField.ReleaseDate, MetadataField.Aliases],
      items: [{ field: MetadataField.Genres, keys: ['2', '1'] }],
      total: 6,
      leftOut: 5,
    });
    expect(leftOutPhrase(exclusion)).toBe('the release date and aliases and part of the genres');
  });
});

describe('review reasons', () => {
  it('adds and removes preset reasons around a hand-written note', () => {
    const note = toggleNoteReason(toggleNoteReason('Wrong edition', 'No source'), 'Already correct');
    expect(note).toBe('Wrong edition. No source. Already correct');
    expect(noteHasReason(note, 'No source')).toBe(true);
    expect(toggleNoteReason(note, 'No source')).toBe('Wrong edition. Already correct');
  });
});

describe('rebaseDraft', () => {
  it('moves only the starting value so the contributor keeps their own', () => {
    const original = draftFromEditable(editable());
    const draft = draftFromEditable(editable());
    draft.romajiTitle = 'Mine';
    rebaseDraft(original, MetadataField.RomajiTitle, 'Theirs');
    expect(original.romajiTitle).toBe('Theirs');
    expect(buildSubmission(original, draft).payload).toMatchObject({ romajiTitle: 'Mine', base: { romajiTitle: 'Theirs' } });
  });
});

describe('withdrawn edits', () => {
  it('read as withdrawn for everyone', () => {
    expect(editStatus({ publicStatus: 'withdrawn', status: null }, 'user').label).toBe('Withdrawn');
    expect(editStatus({ publicStatus: 'withdrawn', status: MetadataEditStatus.Withdrawn }, 'editor').label).toBe('Withdrawn');
  });
});
