<script setup lang="ts">
  import type Popover from 'primevue/popover';

  const uid = useId();
  const popover = ref<InstanceType<typeof Popover> | null>(null);
  const open = ref(false);
</script>

<template>
  <Button
    severity="secondary"
    text
    rounded
    size="small"
    class="-my-2 min-h-11 min-w-11 shrink-0 justify-center sm:pointer-fine:min-h-9 sm:pointer-fine:min-w-9"
    aria-label="Chart settings"
    aria-haspopup="dialog"
    :aria-expanded="open"
    :aria-controls="open ? `${uid}-settings` : undefined"
    @click="popover?.toggle($event)"
  >
    <i class="pi pi-cog" aria-hidden="true" />
  </Button>

  <Popover ref="popover" @show="open = true" @hide="open = false">
    <div :id="`${uid}-settings`" role="dialog" aria-label="Chart settings" class="flex w-max max-w-[calc(100vw_-_2rem)] flex-col gap-4 p-1">
      <slot />
    </div>
  </Popover>
</template>
