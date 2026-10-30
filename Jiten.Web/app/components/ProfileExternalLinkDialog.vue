<script setup lang="ts">
  const props = defineProps<{ url: string | null; ownerName: string }>();
  const emit = defineEmits<{ close: [] }>();

  const visible = computed({
    get: () => props.url !== null,
    set: (open: boolean) => {
      if (!open) emit('close');
    },
  });

  /** The host is set apart so a look-alike address stands out from the path around it. */
  const parts = computed(() => {
    if (!props.url) return null;
    try {
      const parsed = new URL(props.url);
      return { scheme: `${parsed.protocol}//`, host: parsed.host, rest: props.url.slice(`${parsed.protocol}//${parsed.host}`.length) };
    } catch {
      return { scheme: '', host: '', rest: props.url };
    }
  });
</script>

<template>
  <Dialog v-model:visible="visible" modal header="You're leaving Jiten" :style="{ width: '32rem' }" :breakpoints="{ '575px': '95vw' }" :draggable="false">
    <div v-if="url && parts" class="flex flex-col gap-3 text-sm">
      <p class="m-0">
        <span class="font-medium" v-bind="japaneseTextAttrs(ownerName)">{{ ownerName }}</span> added this link. Jiten doesn't review or endorse the sites that
        members link to.
      </p>
      <p class="m-0 rounded-md border border-surface-200 bg-surface-50 px-3 py-2 font-mono text-xs break-all dark:border-surface-700 dark:bg-surface-800">
        <span class="text-muted-color">{{ parts.scheme }}</span><span class="font-bold text-surface-900 dark:text-surface-0">{{ parts.host }}</span
        ><span class="text-surface-700 dark:text-surface-200">{{ parts.rest }}</span>
      </p>
      <p class="m-0 text-muted-color">Check the address before you continue. Don't download files or enter a password on a site you don't trust.</p>
    </div>
    <template #footer>
      <Button label="Go back" severity="secondary" text autofocus @click="visible = false" />
      <Button
        v-if="url"
        as="a"
        :href="url"
        target="_blank"
        rel="nofollow ugc noopener noreferrer"
        label="Open link"
        icon="pi pi-external-link"
        icon-pos="right"
        @click="visible = false"
      />
    </template>
  </Dialog>
</template>
