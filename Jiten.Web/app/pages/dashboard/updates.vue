<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Textarea from 'primevue/textarea';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { debounce } from 'perfect-debounce';
  import type { AdminSiteUpdate } from '~/types';

  useHead({ title: 'Site Updates - Jiten' });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const updates = ref<AdminSiteUpdate[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const publishingId = ref<number | null>(null);

  const editorOpen = ref(false);
  const editingId = ref<number | null>(null);
  const title = ref('');
  const body = ref('');
  const teaser = ref('');
  const showPreview = ref(true);

  // The preview re-parses markdown on every source change; debounced so typing stays responsive.
  const previewSource = ref('');
  const syncPreview = debounce((value: string) => {
    previewSource.value = value;
  }, 300);
  watch(body, (value) => syncPreview(value));

  const editingUpdate = computed(() => updates.value.find((u) => u.id === editingId.value) ?? null);
  const isPublished = computed(() => !!editingUpdate.value?.publishedAt);
  const isValid = computed(() => title.value.trim().length > 0 && body.value.trim().length > 0);

  async function load() {
    try {
      loading.value = true;
      updates.value = await $api<AdminSiteUpdate[]>('/admin/updates');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load updates'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  onMounted(load);

  function openNew() {
    editingId.value = null;
    title.value = '';
    body.value = '';
    previewSource.value = '';
    teaser.value = '';
    editorOpen.value = true;
  }

  function openEdit(update: AdminSiteUpdate) {
    editingId.value = update.id;
    title.value = update.title;
    body.value = update.bodyMarkdown;
    previewSource.value = update.bodyMarkdown;
    teaser.value = update.notificationTeaser ?? '';
    editorOpen.value = true;
  }

  function closeEditor() {
    editorOpen.value = false;
    editingId.value = null;
  }

  async function save() {
    try {
      saving.value = true;
      const payload = {
        title: title.value.trim(),
        bodyMarkdown: body.value,
        notificationTeaser: teaser.value.trim() || null,
      };

      if (editingId.value === null) {
        await $api('/admin/updates', { method: 'POST', body: payload });
      } else {
        await $api(`/admin/updates/${editingId.value}`, { method: 'PUT', body: payload });
      }

      toast.add({ severity: 'success', summary: 'Saved', detail: 'Update saved', life: 3000 });
      closeEditor();
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to save update'), life: 5000 });
    } finally {
      saving.value = false;
    }
  }

  function publish(update: AdminSiteUpdate) {
    confirm.require({
      message: `Publish "${update.title}"? This will notify every user, once. Editing it afterwards never re-notifies.`,
      header: 'Confirm publish',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: async () => {
        try {
          publishingId.value = update.id;
          const result = await $api<{ message: string; count: number }>(`/admin/updates/${update.id}/publish`, { method: 'POST' });
          toast.add({ severity: 'success', summary: 'Published', detail: result.message, life: 5000 });
          await load();
        } catch (e) {
          toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to publish update'), life: 5000 });
        } finally {
          publishingId.value = null;
        }
      },
    });
  }

  function remove(update: AdminSiteUpdate) {
    confirm.require({
      message: `Delete "${update.title}"? Notifications already sent will keep linking to the updates page.`,
      header: 'Confirm delete',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: async () => {
        try {
          await $api(`/admin/updates/${update.id}`, { method: 'DELETE' });
          toast.add({ severity: 'success', summary: 'Deleted', detail: 'Update deleted', life: 3000 });
          if (editingId.value === update.id) closeEditor();
          await load();
        } catch (e) {
          toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to delete update'), life: 5000 });
        }
      },
    });
  }

  function copyAsDiscord(update: AdminSiteUpdate) {
    const text = `**${update.title}**\n\n${update.bodyMarkdown}`;
    navigator.clipboard
      .writeText(text)
      .then(() => toast.add({ severity: 'success', summary: 'Copied', detail: 'Copied in Discord format', life: 3000 }))
      .catch(() => toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to copy', life: 3000 }));
  }

  const formatDate = (date?: string | null) => (date ? new Date(date).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '');
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Site Updates</h1>
      <Button label="New update" icon="pi pi-plus" class="ml-auto" @click="openNew" />
    </div>

    <Card v-if="editorOpen" class="shadow-md mb-6">
      <template #title>
        <div class="flex items-center gap-2">
          <span>{{ editingId === null ? 'New update' : 'Edit update' }}</span>
          <Tag v-if="isPublished" value="Published" severity="success" />
          <Button
            :label="showPreview ? 'Hide preview' : 'Show preview'"
            :icon="showPreview ? 'pi pi-eye-slash' : 'pi pi-eye'"
            text
            size="small"
            class="ml-auto"
            @click="showPreview = !showPreview"
          />
        </div>
      </template>
      <template #content>
        <div class="flex flex-col gap-4">
          <div>
            <label for="updateTitle" class="block text-sm font-medium mb-1">Title</label>
            <InputText id="updateTitle" v-model="title" class="w-full" maxlength="200" placeholder="What shipped" />
          </div>

          <div>
            <label for="updateTeaser" class="block text-sm font-medium mb-1">Notification teaser (optional)</label>
            <InputText id="updateTeaser" v-model="teaser" class="w-full" maxlength="300" placeholder="A new site update has been published." />
            <small class="text-surface-500 dark:text-surface-400">Shown as the notification body. Only sent on the first publish.</small>
          </div>

          <div class="grid grid-cols-1 gap-4" :class="showPreview ? 'lg:grid-cols-2' : ''">
            <div>
              <label for="updateBody" class="block text-sm font-medium mb-1">Body (Markdown)</label>
              <Textarea id="updateBody" v-model="body" rows="18" class="w-full font-mono text-sm" placeholder="## Heading&#10;&#10;- item" />
            </div>

            <div v-if="showPreview">
              <span class="block text-sm font-medium mb-1">Preview</span>
              <div class="border border-surface-200 dark:border-surface-700 rounded p-3 min-h-40 overflow-x-auto">
                <Suspense v-if="previewSource.trim()">
                  <MarkdownBody :source="previewSource" />
                  <template #fallback>
                    <span class="text-surface-500 dark:text-surface-400 text-sm">Rendering...</span>
                  </template>
                </Suspense>
                <span v-else class="text-surface-500 dark:text-surface-400 text-sm">Nothing to preview yet.</span>
              </div>
            </div>
          </div>

          <div class="flex justify-end gap-2">
            <Button label="Cancel" class="p-button-text" @click="closeEditor" />
            <Button label="Save" icon="pi pi-save" :loading="saving" :disabled="!isValid || saving" @click="save" />
          </div>
        </div>
      </template>
    </Card>

    <Card class="shadow-md">
      <template #content>
        <div v-if="loading" class="text-surface-500 dark:text-surface-400">Loading...</div>
        <div v-else-if="updates.length === 0" class="text-surface-500 dark:text-surface-400">No updates yet.</div>
        <div v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
          <div v-for="update in updates" :key="update.id" class="py-3 flex flex-col md:flex-row md:items-center gap-2">
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2 flex-wrap">
                <Tag v-if="update.publishedAt" value="Published" severity="success" />
                <Tag v-else value="Draft" severity="warn" />
                <span class="font-medium truncate">{{ update.title }}</span>
              </div>
              <div class="text-xs text-surface-500 dark:text-surface-400 mt-1">
                Created {{ formatDate(update.createdAt) }}
                <span v-if="update.publishedAt"> &middot; published {{ formatDate(update.publishedAt) }}</span>
                <span v-if="update.updatedAt"> &middot; edited {{ formatDate(update.updatedAt) }}</span>
                <span v-if="update.notifiedAt"> &middot; notified</span>
              </div>
            </div>

            <div class="flex items-center gap-1 shrink-0">
              <Button v-tooltip.top="'Edit'" icon="pi pi-pencil" text size="small" aria-label="Edit" @click="openEdit(update)" />
              <Button
                v-tooltip.top="'Copy in Discord format'"
                icon="pi pi-copy"
                text
                size="small"
                aria-label="Copy in Discord format"
                @click="copyAsDiscord(update)"
              />
              <Button
                v-if="!update.publishedAt"
                label="Publish"
                icon="pi pi-megaphone"
                size="small"
                :loading="publishingId === update.id"
                @click="publish(update)"
              />
              <NuxtLink v-else :to="`/updates#update-${update.id}`" target="_blank">
                <Button v-tooltip.top="'View on site'" icon="pi pi-external-link" text size="small" aria-label="View" />
              </NuxtLink>
              <Button v-tooltip.top="'Delete'" icon="pi pi-trash" text severity="danger" size="small" aria-label="Delete" @click="remove(update)" />
            </div>
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
