import { computed, type ComputedRef, type Ref } from 'vue';
import { type Franchise, type FranchiseNode, type FranchiseEdge, DeckRelationshipType } from '~/types';

export interface RelationCaption {
  label: string;
  otherId: number;
  otherTitle: string;
}

// ---- Relationship labels ----
// MUST match RelatedMediaDisplay's pills: the label names the OTHER deck's role ("Source: X"
// means X is the source). Derived mechanically from its table — forward (this node is the
// stored edge's source) = labels[T]; inverse (this node is the target) = labels[GetInverse(T)].
export const forwardLabels: Record<number, string> = {
  [DeckRelationshipType.Sequel]: 'Prequel',
  [DeckRelationshipType.Fandisc]: 'Source',
  [DeckRelationshipType.Spinoff]: 'Source',
  [DeckRelationshipType.SideStory]: 'Source',
  [DeckRelationshipType.Adaptation]: 'Adaptation',
  [DeckRelationshipType.Alternative]: 'Alternative',
};
export const inverseLabels: Record<number, string> = {
  [DeckRelationshipType.Sequel]: 'Sequel',
  [DeckRelationshipType.Fandisc]: 'Fandisc',
  [DeckRelationshipType.Spinoff]: 'Spinoff',
  [DeckRelationshipType.SideStory]: 'Side Story',
  [DeckRelationshipType.Adaptation]: 'Source',
  [DeckRelationshipType.Alternative]: 'Alternative',
};

// Shared node/edge logic for both the timeline and the constellation web view.
// View-specific concerns (timeline rows/columns/SVG measurement, web force sim/projection)
// stay in their respective components.
export function useFranchiseGraph(franchise: Ref<Franchise> | ComputedRef<Franchise>) {
  const localiseTitle = useLocaliseTitle();

  const nodes = computed<FranchiseNode[]>(() => franchise.value.nodes);
  const edges = computed<FranchiseEdge[]>(() => franchise.value.edges);

  const nodeById = computed<Map<number, FranchiseNode>>(() => {
    const m = new Map<number, FranchiseNode>();
    for (const n of nodes.value) m.set(n.deckId, n);
    return m;
  });

  // Unset release dates arrive as the DateOnly default (year 1); treat them as unknown.
  function releaseYear(node: FranchiseNode): number | null {
    const t = Date.parse(node.releaseDate);
    if (Number.isNaN(t)) return null;
    const year = new Date(t).getFullYear();
    return year <= 1 ? null : year;
  }

  function coverSrc(node: FranchiseNode): string {
    return !node.coverName || node.coverName === 'nocover.jpg' ? '/img/nocover.jpg' : node.coverName;
  }

  // Relation captions for a node, derived from its incident edges.
  function captionsFor(deckId: number): RelationCaption[] {
    const out: RelationCaption[] = [];
    for (const e of edges.value) {
      let label: string | undefined;
      let otherId: number | undefined;
      if (e.targetDeckId === deckId) {
        label = inverseLabels[e.relationshipType];
        otherId = e.sourceDeckId;
      } else if (e.sourceDeckId === deckId) {
        label = forwardLabels[e.relationshipType];
        otherId = e.targetDeckId;
      }
      if (label == null || otherId == null) continue;
      const other = nodeById.value.get(otherId);
      if (!other) continue;
      out.push({ label, otherId, otherTitle: localiseTitle(other) });
    }
    return out;
  }

  // Set of nodes incident to (and including) the active node. Reactive to the supplied ref.
  function useAdjacentNodes(activeNode: Ref<number | null>): ComputedRef<Set<number>> {
    return computed<Set<number>>(() => {
      const s = new Set<number>();
      if (activeNode.value == null) return s;
      s.add(activeNode.value);
      for (const e of edges.value) {
        if (e.sourceDeckId === activeNode.value) s.add(e.targetDeckId);
        else if (e.targetDeckId === activeNode.value) s.add(e.sourceDeckId);
      }
      return s;
    });
  }

  function edgeActive(e: FranchiseEdge, activeId: number | null): boolean {
    return activeId != null && (e.sourceDeckId === activeId || e.targetDeckId === activeId);
  }

  return {
    localiseTitle,
    nodes,
    edges,
    nodeById,
    releaseYear,
    coverSrc,
    captionsFor,
    useAdjacentNodes,
    edgeActive,
    forwardLabels,
    inverseLabels,
  };
}
