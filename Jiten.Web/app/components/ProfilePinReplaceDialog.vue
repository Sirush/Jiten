<script setup lang="ts">
  const request = usePinReplaceRequest();

  const visible = computed({
    get: () => request.value !== null,
    set: (open: boolean) => {
      if (!open) request.value?.resolve(null);
    },
  });
</script>

<template>
  <Dialog v-model:visible="visible" modal header="Replace a pinned title" :style="{ width: '30rem' }" :breakpoints="{ '575px': '95vw' }" :draggable="false">
    <template v-if="request">
      <p class="mb-3 text-sm text-muted-color">
        You can pin up to {{ MAX_PROFILE_PINS }} titles. Pick one to swap for <span class="font-medium text-color" v-bind="japaneseTextAttrs(request.title)">{{ request.title }}</span>.
      </p>
      <ul class="flex flex-col gap-1">
        <li v-for="pin in request.pins" :key="pin.deckId">
          <button
            type="button"
            class="flex w-full items-center gap-3 rounded-md px-2 py-1.5 text-left transition-colors hover:bg-surface-100 focus-visible:bg-surface-100 dark:hover:bg-surface-800 dark:focus-visible:bg-surface-800"
            @click="request.resolve(pin.deckId)"
          >
            <ProfileDeckRow :deck="pin" />
          </button>
        </li>
      </ul>
    </template>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
    </template>
  </Dialog>
</template>
