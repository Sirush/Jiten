<script setup lang="ts">
  import type { Deck } from '~/types';
  import Popover from 'primevue/popover';
  import { entryProgressLabel } from '~/utils/mediaListEntry';

  const props = defineProps<{
    deck: Deck;
    hideProgress?: boolean;
  }>();

  const emit = defineEmits<{
    close: [];
  }>();

  const historyDeck = useHistoryDialogDeck();

  const popover = ref();
  const open = ref(false);
  const flow = useStatusFlow(() => props.deck, { close: () => popover.value?.hide(), isOpen: () => open.value, home: 'progress' });

  const progress = computed(() => (props.hideProgress ? null : entryProgressLabel(props.deck.listEntry, props.deck.characterCount)));

  function openHistory() {
    popover.value?.hide();
    historyDeck.value = props.deck;
  }

  const { menu, rememberTrigger, restoreFocus } = useMenuFocus();

  function toggle(event: Event) {
    rememberTrigger(event);
    popover.value?.toggle(event);
  }

  function onHide() {
    restoreFocus();
    open.value = false;
    flow.reset();
    emit('close');
  }
</script>

<template>
  <span class="inline-flex">
    <button
      type="button"
      class="inline-flex items-center gap-1.5 rounded px-2 py-1 text-xs font-medium text-primary-700 dark:text-primary-300 hover:bg-primary-50 dark:hover:bg-primary-900/30 transition-colors cursor-pointer"
      aria-haspopup="dialog"
      :aria-expanded="open"
      :aria-label="progress ? `Log progress, ${progress}` : 'Log progress'"
      @click.stop="toggle"
    >
      <i class="pi pi-pen-to-square text-[10px]" aria-hidden="true" />
      <span v-if="progress" class="tabular-nums">{{ progress }}</span>
      <span v-else>Log progress</span>
    </button>

    <Popover ref="popover" :pt="{ content: { class: 'p-1' } }" @show="open = true" @hide="onHide">
      <div ref="menu">
        <StatusFlowSteps :deck="deck" :flow="flow" @open-history="openHistory" />
      </div>
    </Popover>
  </span>
</template>
