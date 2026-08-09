import type { Deck, DeckMetadataPatchResult, Genre, LinkType, Tag } from '~/types';
import {
  buildDeckMetadataPatch,
  isDeckMetadataPatchEmpty,
  relationshipExists,
  type DeckMetadataDraft,
} from '~/utils/deckMetadataPatch';
import { fromRole, type PerspectiveRelationship, type RelationshipRoleOption } from '~/utils/relationshipRoles';
import { DEFAULT_TAG_PERCENTAGE } from '~/utils/tags';

export interface DraftRelationship extends PerspectiveRelationship {
  targetTitle: string;
}

interface Draft extends DeckMetadataDraft {
  relationships: DraftRelationship[];
}

let tagVocabularyRequest: Promise<Tag[]> | null = null;

export function useDeckInlineEdit(getDeck: () => Deck) {
  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();

  const snapshot = (): Draft => {
    const deck = getDeck();
    return {
      originalTitle: deck.originalTitle ?? '',
      romajiTitle: deck.romajiTitle ?? '',
      englishTitle: deck.englishTitle ?? '',
      description: deck.description ?? '',
      hideDialoguePercentage: deck.hideDialoguePercentage ?? false,
      hideAverageSentenceLength: deck.hideAverageSentenceLength ?? false,
      genres: [...(deck.genres ?? [])],
      tags: (deck.tags ?? []).map((t) => ({ tagId: t.tagId, percentage: t.percentage, name: t.name })),
      links: (deck.links ?? []).map((l) => ({ linkType: Number(l.linkType) as LinkType, url: l.url })),
      relationships: (deck.relationships ?? []).map((r) => ({
        targetDeckId: r.targetDeckId,
        targetTitle: r.targetDeck ? localiseTitle(r.targetDeck) : `#${r.targetDeckId}`,
        relationshipType: r.relationshipType,
        isInverse: r.isInverse,
      })),
    };
  };

  const original = ref<Draft>(snapshot());
  const draft = ref<Draft>(snapshot());
  const saving = ref(false);

  const tagVocabulary = ref<Tag[]>([]);
  const tagsLoading = ref(false);

  const patch = computed(() => buildDeckMetadataPatch(getDeck().deckId, original.value, draft.value));
  const isDirty = computed(() => !isDeckMetadataPatchEmpty(patch.value));

  async function loadTagVocabulary() {
    if (tagVocabulary.value.length) return;
    tagsLoading.value = true;
    try {
      tagVocabularyRequest ??= $api<Tag[]>('admin/tags');
      tagVocabulary.value = await tagVocabularyRequest;
    } catch {
      tagVocabularyRequest = null;
    } finally {
      tagsLoading.value = false;
    }
  }

  const hasGenre = (genre: Genre) => draft.value.genres.includes(genre);

  function toggleGenre(genre: Genre) {
    const index = draft.value.genres.indexOf(genre);
    if (index === -1) draft.value.genres.push(genre);
    else draft.value.genres.splice(index, 1);
  }

  const hasTag = (tagId: number) => draft.value.tags.some((t) => t.tagId === tagId);

  function toggleTag(tag: Tag, percentage = DEFAULT_TAG_PERCENTAGE) {
    const index = draft.value.tags.findIndex((t) => t.tagId === tag.tagId);
    if (index === -1) draft.value.tags.push({ tagId: tag.tagId, percentage, name: tag.name });
    else draft.value.tags.splice(index, 1);
  }

  /** Adds a tag known by id, resolving its real name from the vocabulary so the chip isn't a guess. */
  async function addTagById(tagId: number, fallbackName: string) {
    if (hasTag(tagId)) return;
    await loadTagVocabulary();
    toggleTag(tagVocabulary.value.find((t) => t.tagId === tagId) ?? { tagId, name: fallbackName });
  }

  function removeTag(tagId: number) {
    const index = draft.value.tags.findIndex((t) => t.tagId === tagId);
    if (index !== -1) draft.value.tags.splice(index, 1);
  }

  function setTagPercentage(tagId: number, percentage: number) {
    const tag = draft.value.tags.find((t) => t.tagId === tagId);
    if (tag) tag.percentage = Math.min(100, Math.max(0, Math.round(percentage)));
  }

  /** Returns an error message, or null when the link was added. */
  function addLink(linkType: LinkType, url: string, replaceIndex: number | null = null): string | null {
    const trimmed = url.trim();
    if (!trimmed) return 'URL is required';
    if (!/^https?:\/\//i.test(trimmed)) return 'URL must start with http:// or https://';

    const duplicate = draft.value.links.some((l, i) => i !== replaceIndex && l.linkType === linkType && l.url === trimmed);
    if (duplicate) return 'This link is already on the deck';

    if (replaceIndex === null) draft.value.links.push({ linkType, url: trimmed });
    else draft.value.links.splice(replaceIndex, 1, { linkType, url: trimmed });
    return null;
  }

  function removeLink(index: number) {
    draft.value.links.splice(index, 1);
  }

  /** Returns an error message, or null when the relationship was added. */
  function addRelationship(targetDeckId: number, targetTitle: string, role: RelationshipRoleOption): string | null {
    if (targetDeckId === getDeck().deckId) return 'A deck cannot be related to itself';

    const relationship = { ...fromRole(targetDeckId, role), targetTitle };
    if (relationshipExists(draft.value.relationships, targetDeckId, relationship.relationshipType))
      return 'This relationship already exists';

    draft.value.relationships.push(relationship);
    return null;
  }

  function removeRelationship(index: number) {
    draft.value.relationships.splice(index, 1);
  }

  async function save(): Promise<DeckMetadataPatchResult> {
    saving.value = true;
    try {
      const result = await $api<DeckMetadataPatchResult>(`admin/deck/${getDeck().deckId}/metadata`, {
        method: 'PATCH',
        body: patch.value,
      });
      return result;
    } finally {
      saving.value = false;
    }
  }

  return {
    draft,
    isDirty,
    saving,
    save,
    tagVocabulary,
    tagsLoading,
    loadTagVocabulary,
    hasGenre,
    toggleGenre,
    hasTag,
    toggleTag,
    addTagById,
    removeTag,
    setTagPercentage,
    addLink,
    removeLink,
    addRelationship,
    removeRelationship,
  };
}
