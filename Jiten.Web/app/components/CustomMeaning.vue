<script setup lang="ts">
  import type { UserCustomMeaningDto } from '~/types';
  import Button from 'primevue/button';
  import Textarea from 'primevue/textarea';
  import { useToast } from 'primevue/usetoast';
  import { extractApiError } from '~/utils/toast';

  const props = defineProps<{
    wordId: number;
    // When true, shows controls to add/edit/delete (used on the vocabulary page and study card).
    editable?: boolean;
  }>();

  const MAX_LENGTH = 500;

  const { $api } = useNuxtApp();
  const authStore = useAuthStore();
  const toast = useToast();

  const meaning = ref<string | null>(null);
  const loaded = ref(false);
  const editing = ref(false);
  const draft = ref('');
  const saving = ref(false);
  const deleting = ref(false);
  const confirmingDelete = ref(false);

  const canSave = computed(() => {
    const t = draft.value.trim();
    return t.length > 0 && t.length <= MAX_LENGTH && !saving.value && t !== (meaning.value ?? '');
  });

  const draftPreview = computed(() => parseCustomMeaningHtml(draft.value));

  async function load() {
    if (!authStore.isAuthenticated) {
      loaded.value = true;
      return;
    }
    try {
      const dto = await $api<UserCustomMeaningDto | null>(`user/custom-meanings/${props.wordId}`);
      meaning.value = dto?.text ?? null;
    } catch {
      meaning.value = null;
    } finally {
      loaded.value = true;
    }
  }

  watch(
    () => props.wordId,
    () => {
      meaning.value = null;
      loaded.value = false;
      editing.value = false;
      load();
    }
  );

  onMounted(load);

  function startEditing() {
    draft.value = meaning.value ?? '';
    editing.value = true;
    confirmingDelete.value = false;
  }

  async function save() {
    if (!canSave.value) return;
    saving.value = true;
    try {
      const dto = await $api<UserCustomMeaningDto>(`user/custom-meanings/${props.wordId}`, {
        method: 'PUT',
        body: { text: draft.value.trim() },
      });
      meaning.value = dto.text;
      editing.value = false;
    } catch (e) {
      // Editor stays open so the unsaved draft isn't lost.
      toast.add({
        severity: 'error',
        summary: 'Could not save note',
        detail: extractApiError(e, 'Your note was not saved. Please try again.'),
        life: 6000,
      });
    } finally {
      saving.value = false;
    }
  }

  async function remove() {
    deleting.value = true;
    try {
      await $api(`user/custom-meanings/${props.wordId}`, { method: 'DELETE' });
      meaning.value = null;
      editing.value = false;
      confirmingDelete.value = false;
    } catch (e) {
      confirmingDelete.value = false;
      toast.add({
        severity: 'error',
        summary: 'Could not delete note',
        detail: extractApiError(e, 'Your note was not deleted. Please try again.'),
        life: 6000,
      });
    } finally {
      deleting.value = false;
    }
  }
</script>

<template>
  <ClientOnly>
    <div v-if="authStore.isAuthenticated && loaded">
      <!-- Edit form -->
      <div
        v-if="editing"
        class="rounded-xl border border-primary-300 dark:border-primary-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-3 w-full text-left"
        @click.stop
        @pointerdown.stop
      >
        <label class="text-xs text-surface-400 block mb-1">Custom notes, meanings</label>
        <Textarea
          v-model="draft"
          rows="4"
          class="w-full"
          :maxlength="MAX_LENGTH"
          autofocus
          placeholder="A note or definition that will always be shown for this word"
        />
        <div class="flex justify-between items-center mb-2">
          <div class="text-xs text-surface-400">
            Formatting:
            <code class="bg-surface-100 dark:bg-surface-800 px-1 rounded">**bold**</code>
            <code class="bg-surface-100 dark:bg-surface-800 px-1 rounded ml-1">*italic*</code>
            <code class="bg-surface-100 dark:bg-surface-800 px-1 rounded ml-1">- list</code>
          </div>
          <div class="text-xs text-surface-400">{{ draft.length }}/{{ MAX_LENGTH }}</div>
        </div>
        <div v-if="draft.trim()" class="mb-3">
          <label class="text-xs text-surface-400 block mb-1">Preview</label>
          <div class="border-l-4 border-primary-500 pl-3 py-2 bg-primary-50 dark:bg-primary-950/40 rounded-r text-sm break-words" v-html="draftPreview" />
        </div>
        <div class="flex gap-2 justify-end items-center">
          <template v-if="meaning != null">
            <template v-if="confirmingDelete">
              <span class="text-xs text-surface-500 mr-auto">Delete this note?</span>
              <Button severity="danger" size="small" icon="pi pi-trash" label="Delete" :loading="deleting" @click="remove" />
              <Button text size="small" label="Keep" :disabled="deleting" @click="confirmingDelete = false" />
            </template>
            <Button v-else severity="danger" text size="small" icon="pi pi-trash" label="Delete" @click="confirmingDelete = true" />
          </template>
          <template v-if="!confirmingDelete">
            <Button text size="small" label="Cancel" @click="editing = false" />
            <Button size="small" icon="pi pi-check" label="Save" :loading="saving" :disabled="!canSave" @click="save" />
          </template>
        </div>
      </div>

      <!-- Display -->
      <template v-else>
        <div v-if="meaning != null" class="group relative border-l-4 border-primary-500 pl-3 pr-2 py-2 bg-primary-50 dark:bg-primary-950/40 rounded-r">
          <div class="flex items-start gap-2">
            <span class="text-xs tracking-wide text-primary-600 dark:text-primary-400 font-semibold mt-0.5">Notes</span>
            <button
              v-if="editable"
              class="ml-auto inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors shrink-0 cursor-pointer"
              title="Edit your notes"
              @click.stop="startEditing"
              @pointerdown.stop
            >
              <i class="pi pi-pencil text-sm" />
            </button>
          </div>
          <div class="break-words text-sm md:text-base" v-html="parseCustomMeaningHtml(meaning)" />
        </div>
        <button
          v-else-if="editable"
          class="text-xs text-surface-400 hover:text-primary-500 transition-colors inline-flex items-center gap-1 cursor-pointer"
          @click.stop="startEditing"
          @pointerdown.stop
        >
          <i class="pi pi-plus text-xs" />
          Add your own notes
        </button>
      </template>
    </div>
  </ClientOnly>
</template>
