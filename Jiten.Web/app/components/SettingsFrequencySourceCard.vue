<script setup lang="ts">
  const toast = useToast();
  const confirm = useConfirm();
  const srsStore = useSrsStore();
  const { $api } = useNuxtApp();

  const loading = ref(true);
  const saving = ref(false);
  const lists = ref<{ id: number; name: string }[]>([]);

  // 0 = global, positive = media type, negative = custom list id (see FrequencySourceSelect).
  const selected = ref(0);

  function fromSettings() {
    const listId = srsStore.studySettings.defaultFrequencyListId;
    if (listId) return -listId;
    return srsStore.studySettings.defaultFrequencyMediaType ?? 0;
  }

  function labelFor(value: number) {
    if (value === 0) return 'Global';
    if (value > 0) return getMediaTypeText(value);
    return lists.value.find((l) => l.id === -value)?.name ?? 'your list';
  }

  onMounted(async () => {
    await srsStore.fetchSettings();
    try {
      const saved = await $api<{ id: number; name: string; isSaved: boolean; status: string }[]>('frequency-lists');
      lists.value = saved.filter((l) => l.isSaved && l.status === 'ready').map((l) => ({ id: l.id, name: l.name }));
    } catch {
      // Custom lists are a Jiten Plus feature; without them the media types still work.
    }
    selected.value = fromSettings();
    loading.value = false;
  });

  function onChange(value: number) {
    const previous = selected.value;
    if (value === previous) return;
    selected.value = value;

    confirm.require({
      message: `Make ${labelFor(value)} your default frequency source? It will be used to show and order by rank everywhere on Jiten and in connected apps.`,
      header: 'Change frequency source',
      icon: 'pi pi-sort-numeric-down',
      accept: () => save(value, previous),
      reject: () => {
        selected.value = previous;
      },
    });
  }

  async function save(value: number, previous: number) {
    saving.value = true;
    try {
      await srsStore.updateSettings({ ...srsStore.studySettings, ...frequencySourcePatch(value) });
      toast.add({ severity: 'success', summary: `Ranks now come from ${labelFor(value)}`, life: 2000 });
    } catch (e) {
      selected.value = previous;
      toast.add({
        severity: 'error',
        summary: 'Could not save',
        detail: extractApiError(e, 'Your frequency source was not changed.'),
        life: 5000,
      });
    } finally {
      saving.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Frequency Source</h2>
    </template>
    <template #content>
      <div class="text-sm text-muted-color mb-4">
        By default, the frequency ranks are shown from the global corpus, which is an average of the frequency across the whole catalogue. You can choose a
        media type you often immerse in or one of your saved frequency lists (Jiten+ feature) to rank words in the way that matters the most for your learning.
      </div>

      <Skeleton v-if="loading" height="2.6rem" class="md:!w-72" />
      <FrequencySourceSelect
        v-else
        :model-value="selected"
        input-id="defaultFrequencySource"
        label="Default rank source"
        width-class="md:w-72"
        :lists="lists"
        :disabled="saving"
        @update:model-value="onChange"
      />

      <p class="text-xs text-muted-color mt-3">
        If the frequency for a word doesn't exist in your chosen source, it will keep its global rank. For custom lists, if your Jiten+ subscription lapses or
        you delete it, your rank will go back to the Global one by default.
      </p>
    </template>
  </Card>
</template>
