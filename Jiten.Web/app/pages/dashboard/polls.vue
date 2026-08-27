<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Textarea from 'primevue/textarea';
  import InputNumber from 'primevue/inputnumber';
  import DatePicker from 'primevue/datepicker';
  import Dialog from 'primevue/dialog';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import type { AdminPoll } from '~/types';

  useHead({ title: 'Polls - Jiten' });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const polls = ref<AdminPoll[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const busyId = ref<number | null>(null);

  const editorOpen = ref(false);
  const editingId = ref<number | null>(null);
  const question = ref('');
  const description = ref('');
  const maxSelections = ref(1);
  const closesAt = ref<Date | null>(null);
  const options = ref<{ id: number | null; text: string }[]>([]);

  const editingPoll = computed(() => polls.value.find((p) => p.id === editingId.value) ?? null);
  const isPublished = computed(() => !!editingPoll.value?.publishedAt);
  const filledOptions = computed(() => options.value.filter((o) => o.text.trim().length > 0));
  const isValid = computed(() => question.value.trim().length > 0 && filledOptions.value.length >= 2);

  async function load() {
    try {
      loading.value = true;
      polls.value = await $api<AdminPoll[]>('/admin/polls');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load polls'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  onMounted(load);

  function openNew() {
    editingId.value = null;
    question.value = '';
    description.value = '';
    maxSelections.value = 1;
    closesAt.value = null;
    options.value = [
      { id: null, text: '' },
      { id: null, text: '' },
    ];
    editorOpen.value = true;
  }

  function openEdit(poll: AdminPoll) {
    editingId.value = poll.id;
    question.value = poll.question;
    description.value = poll.descriptionMarkdown ?? '';
    maxSelections.value = poll.maxSelections;
    closesAt.value = poll.closesAt ? new Date(poll.closesAt) : null;
    options.value = [...poll.options].sort((a, b) => a.sortOrder - b.sortOrder).map((o) => ({ id: o.id, text: o.text }));
    editorOpen.value = true;
  }

  function voteCountFor(optionId: number | null) {
    if (optionId === null) return 0;
    return editingPoll.value?.options.find((o) => o.id === optionId)?.voteCount ?? 0;
  }

  function addOption() {
    options.value.push({ id: null, text: '' });
  }

  function removeOption(index: number) {
    options.value.splice(index, 1);
  }

  function moveOption(index: number, delta: number) {
    const target = index + delta;
    if (target < 0 || target >= options.value.length) return;
    const [moved] = options.value.splice(index, 1);
    options.value.splice(target, 0, moved!);
  }

  async function save() {
    try {
      saving.value = true;
      const payload = {
        question: question.value.trim(),
        descriptionMarkdown: description.value.trim() || null,
        maxSelections: maxSelections.value,
        closesAt: closesAt.value ? closesAt.value.toISOString() : null,
        options: filledOptions.value.map((o, index) => ({ id: o.id, text: o.text.trim(), sortOrder: index })),
      };

      if (editingId.value === null) {
        await $api('/admin/polls', { method: 'POST', body: payload });
      } else {
        await $api(`/admin/polls/${editingId.value}`, { method: 'PUT', body: payload });
      }

      toast.add({ severity: 'success', summary: 'Saved', detail: 'Poll saved', life: 3000 });
      editorOpen.value = false;
      editingId.value = null;
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to save poll'), life: 5000 });
    } finally {
      saving.value = false;
    }
  }

  function publish(poll: AdminPoll) {
    confirm.require({
      message: `Publish "${poll.question}"? It will show up immediately in /polls.`,
      header: 'Confirm publish',
      icon: 'pi pi-exclamation-triangle',
      accept: () => run(poll.id, `/admin/polls/${poll.id}/publish`, 'Poll published'),
    });
  }

  function close(poll: AdminPoll) {
    confirm.require({
      message: `Close "${poll.question}"? Voting stops for good and everyone sees the results.`,
      header: 'Confirm close',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: () => run(poll.id, `/admin/polls/${poll.id}/close`, 'Poll closed'),
    });
  }

  function reopen(poll: AdminPoll) {
    confirm.require({
      message:
        poll.closedAt === null && poll.closesAt !== null
          ? `Reopen "${poll.question}"? The close date is removed and voting starts again.`
          : `Reopen "${poll.question}"? Voting starts again.`,
      header: 'Confirm reopen',
      icon: 'pi pi-exclamation-triangle',
      accept: () => run(poll.id, `/admin/polls/${poll.id}/reopen`, 'Poll reopened'),
    });
  }

  function remove(poll: AdminPoll) {
    confirm.require({
      message:
        poll.totalVoters > 0
          ? `Delete "${poll.question}"? The votes from ${poll.totalVoters} ${poll.totalVoters === 1 ? 'voter' : 'voters'} are deleted with it and cannot be recovered.`
          : `Delete "${poll.question}"? This cannot be undone.`,
      header: 'Confirm delete',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: async () => {
        try {
          await $api(`/admin/polls/${poll.id}`, { method: 'DELETE' });
          toast.add({ severity: 'success', summary: 'Deleted', detail: 'Poll deleted', life: 3000 });
          if (editingId.value === poll.id) editorOpen.value = false;
          await load();
        } catch (e) {
          toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to delete poll'), life: 5000 });
        }
      },
    });
  }

  async function run(id: number, url: string, successDetail: string) {
    try {
      busyId.value = id;
      await $api(url, { method: 'POST' });
      toast.add({ severity: 'success', summary: 'Done', detail: successDetail, life: 3000 });
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Action failed'), life: 5000 });
    } finally {
      busyId.value = null;
    }
  }

  const formatDate = (date?: string | null) => (date ? new Date(date).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '');

  const expandedId = ref<number | null>(null);

  function toggleResults(poll: AdminPoll) {
    expandedId.value = expandedId.value === poll.id ? null : poll.id;
  }

  function shareOf(poll: AdminPoll, count: number) {
    return poll.totalVoters === 0 ? 0 : (count / poll.totalVoters) * 100;
  }

  const sortedOptionsOf = (poll: AdminPoll) => [...poll.options].sort((a, b) => a.sortOrder - b.sortOrder);
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Polls</h1>
      <Button label="New poll" icon="pi pi-plus" class="ml-auto" @click="openNew" />
    </div>

    <Card class="shadow-md">
      <template #content>
        <div v-if="loading" class="text-surface-500 dark:text-surface-400">Loading...</div>
        <div v-else-if="polls.length === 0" class="text-surface-500 dark:text-surface-400">No polls yet.</div>
        <div v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
          <div v-for="poll in polls" :key="poll.id" class="py-3 flex flex-col gap-2">
            <div class="flex flex-col md:flex-row md:items-center gap-2">
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 flex-wrap">
                  <Tag v-if="!poll.publishedAt" value="Draft" severity="warn" />
                  <Tag v-else-if="poll.isClosed" value="Closed" severity="secondary" />
                  <Tag v-else value="Open" severity="success" />
                  <span class="font-medium truncate">{{ poll.question }}</span>
                </div>
                <div class="text-xs text-surface-500 dark:text-surface-400 mt-1">
                  {{ poll.options.length }} options &middot; {{ poll.totalVoters }} {{ poll.totalVoters === 1 ? 'voter' : 'voters' }}
                  <span v-if="poll.maxSelections > 1">&middot; up to {{ poll.maxSelections }} picks</span>
                  <span v-if="poll.publishedAt">&middot; published {{ formatDate(poll.publishedAt) }}</span>
                  <span v-if="poll.closesAt">&middot; closes {{ formatDate(poll.closesAt) }}</span>
                  <span v-if="poll.closedAt">&middot; closed {{ formatDate(poll.closedAt) }}</span>
                </div>
              </div>

              <div class="flex items-center gap-1 shrink-0">
                <Button
                  v-tooltip.top="'Results'"
                  icon="pi pi-chart-bar"
                  text
                  size="small"
                  aria-label="Results"
                  :class="expandedId === poll.id ? 'p-button-primary' : ''"
                  @click="toggleResults(poll)"
                />
                <Button v-tooltip.top="'Edit'" icon="pi pi-pencil" text size="small" aria-label="Edit" @click="openEdit(poll)" />
                <Button v-if="!poll.publishedAt" label="Publish" icon="pi pi-megaphone" size="small" :loading="busyId === poll.id" @click="publish(poll)" />
                <Button
                  v-else-if="!poll.isClosed"
                  label="Close"
                  icon="pi pi-lock"
                  size="small"
                  severity="secondary"
                  :loading="busyId === poll.id"
                  @click="close(poll)"
                />
                <Button v-else label="Reopen" icon="pi pi-lock-open" size="small" severity="secondary" :loading="busyId === poll.id" @click="reopen(poll)" />
                <Button v-tooltip.top="'Delete'" icon="pi pi-trash" text severity="danger" size="small" aria-label="Delete" @click="remove(poll)" />
              </div>
            </div>

            <div v-if="expandedId === poll.id" class="flex flex-col gap-2 md:pl-2 pb-1">
              <div v-for="option in sortedOptionsOf(poll)" :key="option.id" class="flex flex-col gap-1">
                <div class="flex items-baseline justify-between gap-2 text-sm">
                  <span class="min-w-0 break-words">{{ option.text }}</span>
                  <span class="shrink-0 tabular-nums text-surface-600 dark:text-surface-300">
                    {{ option.voteCount }} &middot; {{ shareOf(poll, option.voteCount).toFixed(0) }}%
                  </span>
                </div>
                <div class="relative h-2 w-full overflow-hidden rounded bg-surface-200 dark:bg-surface-700">
                  <div class="absolute inset-y-0 left-0 rounded bg-primary-500" :style="{ width: shareOf(poll, option.voteCount).toFixed(1) + '%' }" />
                </div>
              </div>
              <span v-if="poll.maxSelections > 1" class="text-xs text-surface-500 dark:text-surface-400">
                Up to {{ poll.maxSelections }} picks each, so shares can total more than 100%
              </span>
            </div>
          </div>
        </div>
      </template>
    </Card>

    <Dialog
      v-model:visible="editorOpen"
      modal
      :header="editingId === null ? 'New poll' : 'Edit poll'"
      :draggable="false"
      class="w-[42rem] max-w-[calc(100vw-2rem)]"
    >
      <div class="flex flex-col gap-4">
        <div>
          <label for="pollQuestion" class="block text-sm font-medium mb-1">Question</label>
          <InputText id="pollQuestion" v-model="question" class="w-full" maxlength="300" placeholder="What should we build next?" />
        </div>

        <div>
          <label for="pollDescription" class="block text-sm font-medium mb-1">Description (Markdown, optional)</label>
          <Textarea id="pollDescription" v-model="description" rows="3" class="w-full text-sm" maxlength="2000" />
        </div>

        <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label for="pollMaxSelections" class="block text-sm font-medium mb-1">Selections allowed</label>
            <InputNumber id="pollMaxSelections" v-model="maxSelections" :min="1" :max="20" :disabled="isPublished" show-buttons class="w-full" fluid />
            <small v-if="isPublished" class="text-surface-500 dark:text-surface-400">Frozen once published.</small>
          </div>
          <div>
            <label class="block text-sm font-medium mb-1">Closes at (optional)</label>
            <DatePicker v-model="closesAt" show-time hour-format="24" show-button-bar class="w-full" />
          </div>
        </div>

        <div class="flex flex-col gap-2">
          <span class="block text-sm font-medium">Options</span>
          <div v-for="(option, index) in options" :key="index" class="flex items-center gap-1">
            <InputText v-model="option.text" class="flex-1 min-w-0" maxlength="200" :placeholder="`Option ${index + 1}`" />
            <Button
              v-tooltip.top="'Move up'"
              icon="pi pi-chevron-up"
              text
              size="small"
              aria-label="Move up"
              :disabled="index === 0"
              @click="moveOption(index, -1)"
            />
            <Button
              v-tooltip.top="'Move down'"
              icon="pi pi-chevron-down"
              text
              size="small"
              aria-label="Move down"
              :disabled="index === options.length - 1"
              @click="moveOption(index, 1)"
            />
            <Button
              v-tooltip.top="voteCountFor(option.id) > 0 ? 'Has votes, cannot be removed' : 'Remove'"
              icon="pi pi-times"
              text
              severity="danger"
              size="small"
              aria-label="Remove option"
              :disabled="voteCountFor(option.id) > 0"
              @click="removeOption(index)"
            />
          </div>
          <div class="flex items-center justify-between gap-2">
            <Button label="Add option" icon="pi pi-plus" text size="small" @click="addOption" />
            <small v-if="filledOptions.length < 2" class="text-surface-500 dark:text-surface-400">Two options minimum.</small>
          </div>
        </div>
      </div>

      <template #footer>
        <Button label="Cancel" class="p-button-text" @click="editorOpen = false" />
        <Button label="Save" icon="pi pi-save" :loading="saving" :disabled="!isValid || saving" @click="save" />
      </template>
    </Dialog>
  </div>
</template>
