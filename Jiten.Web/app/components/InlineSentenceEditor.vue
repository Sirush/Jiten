<script setup lang="ts">
  import type { UserExampleSentenceDto } from '~/types';
  import Button from 'primevue/button';
  import Textarea from 'primevue/textarea';
  import InputText from 'primevue/inputtext';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    // Only needed for the create path (userSentenceId == null); unused when editing an existing custom sentence.
    wordId?: number;
    readingIndex?: number;
    initialText: string;
    initialSource: string;
    // The UserExampleSentence id when editing an existing custom sentence; null when the displayed
    // example is a corpus sentence (saving then creates a new custom sentence via the favourite endpoint).
    userSentenceId: number | null;
  }>();

  const emit = defineEmits<{
    saved: [dto: UserExampleSentenceDto];
    deleted: [];
    cancel: [];
  }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { limits: planLimits } = useJitenPlus();

  const text = ref(props.initialText);
  const source = ref(props.initialSource);
  const saving = ref(false);
  const deleting = ref(false);

  function hasValidMarkers(t: string): boolean {
    return /\*\*[^*]+\*\*/.test(t);
  }

  const markerHint = computed(() => (!text.value || hasValidMarkers(text.value) ? null : 'Mark words to highlight with **, e.g. **食べる**'));

  const previewHtml = computed(() => (hasValidMarkers(text.value) ? parseCustomSentenceHtml(text.value) : sanitiseHtml(text.value)));

  const canSave = computed(() => hasValidMarkers(text.value) && text.value.length <= 150 && !saving.value);

  async function save() {
    if (!canSave.value) return;
    saving.value = true;
    try {
      const body = { text: text.value, source: source.value || undefined };
      const dto =
        props.userSentenceId != null
          ? await $api<UserExampleSentenceDto>(`user/example-sentences/${props.userSentenceId}`, { method: 'PUT', body })
          : await $api<UserExampleSentenceDto>(`user/example-sentences/${props.wordId}/${props.readingIndex}/favourite`, { method: 'POST', body });
      emit('saved', dto);
    } catch {
      toast.add({
        severity: 'error',
        summary: props.userSentenceId != null ? 'Failed to save sentence' : `Maximum of ${planLimits.value.customSentencesPerWord} custom sentences reached`,
        life: 3000,
      });
    } finally {
      saving.value = false;
    }
  }

  async function remove() {
    if (props.userSentenceId == null) return;
    deleting.value = true;
    try {
      await $api(`user/example-sentences/${props.userSentenceId}`, { method: 'DELETE' });
      emit('deleted');
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to delete sentence', life: 3000 });
    } finally {
      deleting.value = false;
    }
  }
</script>

<template>
  <div
    class="rounded-xl border border-primary-300 dark:border-primary-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4 w-full text-left"
    @click.stop
    @pointerdown.stop
  >
    <div class="mb-2">
      <label class="text-xs text-surface-400 block mb-1">Sentence</label>
      <Textarea v-model="text" rows="2" class="w-full" lang="ja" :maxlength="150" placeholder="彼は毎日**走る**ことにしている" />
      <div class="flex justify-between">
        <div v-if="markerHint" class="text-xs text-orange-500">{{ markerHint }}</div>
        <div v-else />
        <div class="text-xs text-surface-400">{{ text.length }}/150</div>
      </div>
    </div>
    <div class="mb-2">
      <label class="text-xs text-surface-400 block mb-1">Source</label>
      <InputText v-model="source" class="w-full" :maxlength="150" placeholder="Naruto - Episode 1" />
    </div>
    <div v-if="hasValidMarkers(text)" class="mb-3">
      <label class="text-xs text-surface-400 block mb-1">Preview</label>
      <blockquote class="border-l-4 border-yellow-500 pl-4 py-2 bg-gray-50 dark:bg-gray-900 rounded-r text-sm">
        <div lang="ja" v-html="previewHtml" />
      </blockquote>
    </div>
    <div class="flex gap-2 justify-end">
      <Button v-if="userSentenceId != null" severity="danger" text size="small" icon="pi pi-trash" label="Delete" :loading="deleting" @click="remove" />
      <Button text size="small" label="Cancel" @click="emit('cancel')" />
      <Button size="small" icon="pi pi-check" label="Save" :loading="saving" :disabled="!canSave" @click="save" />
    </div>
  </div>
</template>
