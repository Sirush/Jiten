<script setup lang="ts">
  const props = defineProps<{
    label: string;
    inputId?: string;
    changed?: boolean;
    count?: string;
    /** Edits to this field that are still waiting for review. */
    pending?: { id: number; isYours: boolean }[];
    deckId?: number;
    withdrawingId?: number | null;
  }>();

  const emit = defineEmits<{ undo: []; withdraw: [id: number] }>();

  const yours = computed(() => props.pending?.find((p) => p.isYours) ?? null);
  const others = computed(() => props.pending?.filter((p) => !p.isYours) ?? []);
</script>

<template>
  <div class="min-w-0 border-l-2 pl-3 transition-colors" :class="changed ? 'border-primary-500' : 'border-transparent'">
    <div class="flex items-center gap-2 mb-1 min-h-6">
      <label v-if="inputId" :for="inputId" class="text-xs font-semibold text-muted-color">{{ label }}</label>
      <span v-else class="text-xs font-semibold text-muted-color">{{ label }}</span>
      <span v-if="count" class="text-xs text-muted-color tabular-nums">{{ count }}</span>
      <button
        v-if="changed"
        type="button"
        class="ml-auto text-xs text-primary-600 dark:text-primary-400 hover:underline cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-primary-500"
        @click="emit('undo')"
      >
        Undo
      </button>
    </div>
    <div v-if="yours || others.length" class="mb-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs text-amber-800 dark:text-amber-300">
      <span v-if="yours" class="inline-flex items-center gap-1.5">
        <i class="pi pi-clock text-[0.7rem]" aria-hidden="true" />Your suggestion for this is waiting for review.
        <button
          type="button"
          class="text-primary-600 dark:text-primary-400 hover:underline cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-primary-500 disabled:opacity-60"
          :disabled="withdrawingId === yours.id"
          @click="emit('withdraw', yours.id)"
        >
          Withdraw
        </button>
      </span>
      <span v-if="others.length" class="inline-flex items-center gap-1.5">
        <i class="pi pi-users text-[0.7rem]" aria-hidden="true" />{{
          others.length === 1 ? 'Someone suggested a change here.' : `${others.length} suggestions here are waiting.`
        }}
        <NuxtLink :to="{ path: '/review', query: { deckId, edit: others[0]!.id } }">Check {{ others.length === 1 ? 'it' : 'them' }}</NuxtLink>
      </span>
    </div>
    <slot />
  </div>
</template>
