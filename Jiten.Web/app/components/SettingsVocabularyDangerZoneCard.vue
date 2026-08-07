<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const router = useRouter();

  async function goToFullBackup() {
    await router.replace({ query: { mode: 'export', option: 'complete-vocabulary' } });
    await nextTick();
    document.getElementById('vocabulary-transfer-panel')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  const showPurgeDialog = ref(false);
  const purgeBusy = ref(false);
  const purgeSince = ref<Date | null>(null);
  const purgeUntil = ref<Date | null>(null);
  const purgeDropEmptyArchives = ref(false);

  async function clearKnownWords() {
    confirm.require({
      message: 'Are you sure you want to clear all known words? This action cannot be undone.',
      header: 'Clear Known Words',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      rejectClass: 'p-button-secondary',
      accept: async () => {
        try {
          const result = await $api<{ removed: number }>('user/vocabulary/known-ids/clear', { method: 'DELETE' });
          toast.add({
            severity: 'success',
            summary: 'Known words cleared',
            detail: `Removed ${result?.removed ?? 0} known words from your account.`,
            life: 5000,
          });
          emit('changed');
        } catch (e) {
          console.error(e);
          toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to clear known words on server.'), life: 5000 });
        }
      },
      reject: () => {},
    });
  }

  const rebuildBusy = ref(false);

  function confirmRebuildActivity() {
    confirm.require({
      header: 'Rebuild activity history',
      message:
      'Recalculates your heatmap, streaks and activity totals from your existing reviews. ' +
      'Your reviews and cards are untouched, but the recalculated days will be under your current study timezone, ' +
      'so they can look different from your historical stats if you\'ve changed timezone in-between.',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Rebuild',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: () => rebuildActivity(),
    });
  }

  async function rebuildActivity() {
    rebuildBusy.value = true;
    try {
      const result = await $api<{ clearedDays: number }>('user/vocabulary/review-activity/rebuild', { method: 'POST' });
      toast.add({
        severity: 'success',
        summary: 'Rebuild queued',
        detail: `Cleared ${(result?.clearedDays ?? 0).toLocaleString()} stored day${result?.clearedDays === 1 ? '' : 's'}. Your study activity chart and streaks are being recalculated and will be back shortly.`,
        life: 6000,
      });
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Failed', detail: extractApiError(e, 'Could not rebuild your activity history.'), life: 5000 });
    } finally {
      rebuildBusy.value = false;
    }
  }

  function confirmPurge() {
    const range =
      purgeSince.value || purgeUntil.value
        ? `between ${purgeSince.value ? purgeSince.value.toLocaleDateString() : 'the beginning'} and ${purgeUntil.value ? purgeUntil.value.toLocaleDateString() : 'now'}`
        : 'you have ever done';
    const archiveNote = purgeDropEmptyArchives.value ? ' Removed cards left with no reviews will be deleted for good.' : '';
    confirm.require({
      header: 'Erase review history',
      message:
        `Every review ${range} will be deleted on all your cards. ` +
        `Your activity study chart, streaks and schedules are recalculated from what is left.${archiveNote} This cannot be undone.`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Erase history',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: () => purge(),
    });
  }

  async function purge() {
    purgeBusy.value = true;
    try {
      const query: Record<string, string> = {};
      if (purgeSince.value) query.since = purgeSince.value.toISOString();
      if (purgeUntil.value) query.until = purgeUntil.value.toISOString();
      if (purgeDropEmptyArchives.value) query.dropEmptyArchives = 'true';

      const result = await $api<{ deletedLogs: number; deletedCards: number; clearedArchives: number; droppedArchives: number }>(
        'user/vocabulary/review-history',
        {
          method: 'DELETE',
          query,
        }
      );

      const cards = result?.deletedCards ?? 0;
      const archives = result?.clearedArchives ?? 0;
      const dropped = result?.droppedArchives ?? 0;
      toast.add({
        severity: 'success',
        summary: 'Review history erased',
        detail:
          `Deleted ${(result?.deletedLogs ?? 0).toLocaleString()} reviews` +
          (cards > 0 ? `, and removed ${cards.toLocaleString()} card${cards === 1 ? '' : 's'} left with no history` : '') +
          (archives > 0 ? `, and cleared the history on ${archives.toLocaleString()} removed card${archives === 1 ? '' : 's'}` : '') +
          (dropped > 0 ? `. ${dropped.toLocaleString()} removed card${dropped === 1 ? '' : 's'} left Recently Removed for good` : '') +
          '. Your schedules are being recalculated in the background.',
        life: 6000,
      });
      showPurgeDialog.value = false;
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Failed', detail: extractApiError(e, 'Could not erase review history.'), life: 5000 });
    } finally {
      purgeBusy.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Danger Zone</h2>
    </template>
    <template #content>
      <Message severity="warn" :closable="false" class="mb-4">
        <div class="flex flex-col sm:flex-row sm:items-center gap-3">
          <span class="flex-1">
            Every button leads to irreversible changes. Before you press any of them, export a complete backup and keep it somewhere safe. It
            is the only way to get your vocabulary back if the result is not what you expected. Use them at your own risk.
          </span>
          <Button
            label="Export a full backup"
            icon="pi pi-database"
            severity="warn"
            size="small"
            class="shrink-0 self-start sm:self-auto"
            @click="goToFullBackup"
          />
        </div>
      </Message>

      <p class="mb-3">
        Clicking this button will <b>delete ALL your known words</b>. This action cannot be undone, and unlike removing individual cards, nothing is kept under
        Recently Removed.
      </p>
      <div class="flex">
        <Button severity="danger" icon="pi pi-trash" label="Clear All Known Words" @click="clearKnownWords" />
      </div>

      <Divider />

      <p class="mb-3">
        Recalculates your heatmap, streaks and activity totals from your existing reviews. Use it if a day or a streak looks wrong. Your reviews and cards are
        untouched, but the recalculated days will be under your <b>current</b> study timezone, so they can look different from your historical stats if you've
        changed timezone in-between.
      </p>
      <div class="flex">
        <Button severity="danger" outlined icon="pi pi-refresh" label="Rebuild Activity History" :loading="rebuildBusy" @click="confirmRebuildActivity" />
      </div>

      <Divider />

      <p class="mb-3">
        Deletes <b>all of your reviews</b> in a selected period of time, including the ones in your history (Recently Removed). They disappear from your
        heatmap, streaks and retention. Cards that keep part of their history are rescheduled from what is left; cards left with no review at all are removed,
        since a card without history is a word you have never studied. Words you marked as known, blacklisted or suspended are not affected. This cannot be
        undone.
      </p>
      <div class="flex">
        <Button severity="danger" outlined icon="pi pi-calendar-times" label="Erase Review History" @click="showPurgeDialog = true" />
      </div>

      <Dialog v-model:visible="showPurgeDialog" modal header="Erase review history" :style="{ width: '520px', maxWidth: '95vw' }">
        <p class="text-sm text-muted-color mb-4">Leave both dates empty to erase your whole history, or pick a range to erase only the reviews done in it.</p>

        <div class="flex flex-col sm:flex-row gap-3">
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">From</label>
            <DatePicker v-model="purgeSince" date-format="yy-mm-dd" show-icon fluid placeholder="The beginning" />
          </div>
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">To</label>
            <DatePicker v-model="purgeUntil" date-format="yy-mm-dd" show-icon fluid placeholder="Now" />
          </div>
        </div>

        <div class="flex items-start gap-2 mt-4">
          <Checkbox v-model="purgeDropEmptyArchives" input-id="purge-drop-archives" binary />
          <label for="purge-drop-archives" class="text-sm cursor-pointer">
            Also delete removed cards left with no reviews
            <span class="block text-xs text-muted-color mt-0.5">They will leave Recently Removed and can no longer be restored.</span>
          </label>
        </div>

        <template #footer>
          <div class="flex justify-end gap-2">
            <Button label="Cancel" severity="secondary" @click="showPurgeDialog = false" />
            <Button label="Erase history" icon="pi pi-trash" severity="danger" :loading="purgeBusy" @click="confirmPurge" />
          </div>
        </template>
      </Dialog>
    </template>
  </Card>
</template>
