import { computed, toValue, type ComputedRef, type MaybeRefOrGetter, type Ref } from 'vue';
import type { Franchise, FranchiseNode, FranchiseEdge } from '~/types';
import { useJitenStore } from '~/stores/jitenStore';
import { useAuthStore } from '~/stores/authStore';
import { franchiseUsableEdges } from '~/utils/franchiseLayout';
import { relatedMediaLabelFrom } from '~/utils/relationshipRoles';

export interface RelationCaption {
  label: string;
  otherId: number;
  otherTitle: string;
}

/** Node, edge and hover-focus state shared by the franchise views; layout stays in each view. */
export function useFranchiseGraph(
  franchise: Ref<Franchise> | ComputedRef<Franchise>,
  scope: { deckIds: MaybeRefOrGetter<number[] | null | undefined>; activeNode: Ref<number | null> }
) {
  const localiseTitle = useLocaliseTitle();
  const store = useJitenStore();
  const authStore = useAuthStore();
  const { activeNode } = scope;

  const edges = computed<FranchiseEdge[]>(() => franchiseUsableEdges(franchise.value.nodes, franchise.value.edges));
  const nodeById = computed(() => new Map(franchise.value.nodes.map((n) => [n.deckId, n])));

  const scopeSet = computed(() => {
    const ids = toValue(scope.deckIds);
    return ids ? new Set(ids) : null;
  });

  function outOfScope(deckId: number): boolean {
    return !!scopeSet.value && !scopeSet.value.has(deckId) && activeNode.value !== deckId;
  }

  function captionsFor(deckId: number): RelationCaption[] {
    const out: RelationCaption[] = [];
    for (const e of edges.value) {
      if (e.sourceDeckId !== deckId && e.targetDeckId !== deckId) continue;
      const otherId = e.sourceDeckId === deckId ? e.targetDeckId : e.sourceDeckId;
      const other = nodeById.value.get(otherId);
      if (other) out.push({ label: relatedMediaLabelFrom(e, deckId), otherId, otherTitle: localiseTitle(other) });
    }
    return out;
  }

  const adjacentNodes = computed(() => {
    const s = new Set<number>();
    const id = activeNode.value;
    if (id == null) return s;
    s.add(id);
    for (const e of edges.value) {
      if (e.sourceDeckId === id) s.add(e.targetDeckId);
      else if (e.targetDeckId === id) s.add(e.sourceDeckId);
    }
    return s;
  });

  const activeNodeData = computed(() => (activeNode.value == null ? null : (nodeById.value.get(activeNode.value) ?? null)));

  function edgeActive(e: FranchiseEdge): boolean {
    return activeNode.value != null && (e.sourceDeckId === activeNode.value || e.targetDeckId === activeNode.value);
  }

  function nodeDimmed(deckId: number): boolean {
    return activeNode.value != null && !adjacentNodes.value.has(deckId);
  }

  function showCoverage(node: FranchiseNode): boolean {
    return authStore.isAuthenticated && !store.hideCoverageBorders && (node.coverage !== 0 || node.uniqueCoverage !== 0);
  }

  return {
    localiseTitle,
    edges,
    nodeById,
    outOfScope,
    captionsFor,
    adjacentNodes,
    activeNodeData,
    edgeActive,
    nodeDimmed,
    showCoverage,
  };
}
