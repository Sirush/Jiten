<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useAuthStore } from '~/stores/authStore';
  import { useDisplayProfileStore } from '~/stores/displayProfileStore';
  import { DEFAULT_DISPLAY_VALUES, MAX_DISPLAY_PROFILE_NAME, MAX_DISPLAY_PROFILES, exportDisplayProfile, parseDisplayProfileImport } from '~/utils/displayProfile';

  const auth = useAuthStore();
  const profiles = useDisplayProfileStore();
  const toast = useToast();
  const confirm = useConfirm();

  const errorText = (error: unknown, fallback: string) => {
    const data = (error as { data?: { error?: unknown } } | null)?.data;
    return typeof data?.error === 'string' ? data.error : fallback;
  };

  const profileOptions = computed(() => profiles.profiles.map((p) => ({ label: p.name, value: p.id })));
  const activeId = computed({
    get: () => profiles.activeId,
    set: (id: string | null) => {
      if (id) profiles.switchTo(id);
    },
  });

  const nameDialog = ref<{ mode: 'create' | 'rename' | 'offer'; name: string; source: 'current' | 'defaults' } | null>(null);
  const busy = ref(false);

  const openCreate = () => (nameDialog.value = { mode: 'create', name: '', source: 'current' });
  const openRename = () => (nameDialog.value = { mode: 'rename', name: profiles.activeProfile?.name ?? '', source: 'current' });
  const openOffer = () => (nameDialog.value = { mode: 'offer', name: 'This browser', source: 'current' });

  const dialogTitle = computed(() => {
    if (nameDialog.value?.mode === 'rename') return 'Rename profile';
    if (nameDialog.value?.mode === 'offer') return 'Save this browser’s settings';
    return 'New display profile';
  });

  const submitName = async () => {
    const dialog = nameDialog.value;
    const name = dialog?.name.trim();
    if (!dialog || !name) return;
    busy.value = true;
    try {
      if (dialog.mode === 'rename') await profiles.rename(profiles.activeId!, name);
      else if (dialog.mode === 'offer') await profiles.acceptLocalOffer(name);
      else if (dialog.source === 'defaults') await profiles.create(name, DEFAULT_DISPLAY_VALUES, null);
      else await profiles.create(name, profiles.readValues(), profiles.readStatColumns());
      nameDialog.value = null;
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Could not save the profile', detail: errorText(error, 'Please try again.'), life: 5000 });
    } finally {
      busy.value = false;
    }
  };

  const confirmDelete = () => {
    const profile = profiles.activeProfile;
    if (!profile) return;
    confirm.require({
      header: `Delete “${profile.name}”?`,
      message: 'All the devices using this profile will switch to the next one available.',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Delete', severity: 'danger' },
      accept: async () => {
        try {
          await profiles.remove(profile.id);
        } catch (error) {
          toast.add({ severity: 'error', summary: 'Could not delete the profile', detail: errorText(error, 'Please try again.'), life: 5000 });
        }
      },
    });
  };

  const exportActive = () => {
    const name = profiles.activeProfile?.name ?? 'Jiten display settings';
    const blob = new Blob([exportDisplayProfile(name, profiles.readValues(), profiles.readStatColumns())], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `jiten-display-${name.replace(/[^\w-]+/g, '-').toLowerCase() || 'profile'}.json`;
    link.click();
    URL.revokeObjectURL(url);
  };

  const fileInput = ref<HTMLInputElement | null>(null);
  const importFile = async (event: Event) => {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    const parsed = parseDisplayProfileImport(await file.text());
    if (!parsed) {
      toast.add({ severity: 'error', summary: 'Not a Jiten display profile', detail: 'Choose a file exported from this page.', life: 5000 });
      return;
    }

    if (!auth.isAuthenticated) {
      // The stat editor needs an account, so a guest keeps the built-in card stats.
      profiles.applyValues(parsed.values, null);
      toast.add({ severity: 'success', summary: `Applied “${parsed.name}”`, life: 3000 });
      return;
    }

    try {
      await profiles.create(parsed.name, parsed.values, parsed.statColumns);
      toast.add({ severity: 'success', summary: `Imported “${parsed.name}”`, detail: 'It is now your active profile.', life: 3000 });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Could not import the profile', detail: errorText(error, 'Please try again.'), life: 5000 });
    }
  };
</script>

<template>
  <div class="flex flex-col gap-3">
    <template v-if="auth.isAuthenticated">
      <div v-if="profiles.status === 'loading' || profiles.status === 'idle'" class="flex items-center gap-2 text-sm text-surface-600 dark:text-surface-400">
        <i class="pi pi-spin pi-spinner" aria-hidden="true" />
        Loading your display profiles…
      </div>

      <Message v-else-if="profiles.status === 'error'" severity="error" :closable="false">
        <div class="flex flex-wrap items-center gap-3">
          <span>Your display profiles could not be loaded. Changes here stay on this device for now.</span>
          <Button label="Try again" size="small" severity="secondary" @click="profiles.init()" />
        </div>
      </Message>

      <template v-else>
        <div class="flex flex-col gap-3 md:flex-row md:items-end">
          <div class="flex min-w-0 flex-1 flex-col gap-1">
            <label for="activeDisplayProfile" class="text-sm font-medium text-surface-900 dark:text-surface-0">Profile on this device</label>
            <Select v-model="activeId" :options="profileOptions" option-label="label" option-value="value" input-id="activeDisplayProfile" class="w-full md:max-w-sm" />
          </div>
          <div class="flex flex-wrap gap-2">
            <Button label="New" icon="pi pi-plus" size="small" severity="secondary" outlined :disabled="!profiles.canCreate" @click="openCreate" />
            <Button label="Rename" icon="pi pi-pencil" size="small" severity="secondary" outlined @click="openRename" />
            <Button
              label="Delete"
              icon="pi pi-trash"
              size="small"
              severity="danger"
              outlined
              :disabled="profiles.profiles.length <= 1"
              @click="confirmDelete"
            />
            <Button label="Export" icon="pi pi-download" size="small" severity="secondary" outlined @click="exportActive" />
            <Button label="Import" icon="pi pi-upload" size="small" severity="secondary" outlined :disabled="!profiles.canCreate" @click="fileInput?.click()" />
          </div>
        </div>

        <p class="text-sm text-surface-600 dark:text-surface-400" aria-live="polite">
          <template v-if="profiles.saveState === 'saving'">Saving…</template>
          <template v-else-if="profiles.saveState === 'error'">
            Your last change could not be saved to your account. It is still applied here.
            <button type="button" class="font-medium text-primary-700 dark:text-primary-300 underline cursor-pointer" @click="profiles.saveActiveNow()">
              Retry
            </button>
          </template>
          <template v-else>
            Changes save to “{{ profiles.activeProfile?.name }}” and apply on every device using it. {{ profiles.profiles.length }} of
            {{ MAX_DISPLAY_PROFILES }} profiles used.
          </template>
        </p>

        <Message v-if="profiles.localOffer" severity="info" :closable="false">
          <div class="flex flex-col gap-2">
            <span>
              This browser had different display settings before you signed in. Your account’s “{{ profiles.activeProfile?.name }}” profile is now in use.
            </span>
            <div class="flex flex-wrap gap-2">
              <Button label="Save them as a new profile" size="small" :disabled="!profiles.canCreate" @click="openOffer" />
              <Button label="Discard" size="small" severity="secondary" text @click="profiles.dismissLocalOffer()" />
            </div>
          </div>
        </Message>
      </template>
    </template>

    <Message v-else severity="secondary" :closable="false">
      <div class="flex flex-col gap-2">
        <span>
          These settings are saved in this browser only.
          <NuxtLink to="/login" class="font-medium">Sign in</NuxtLink>
          to keep them on your account and switch between up to {{ MAX_DISPLAY_PROFILES }} profiles.
        </span>
        <div>
          <Button label="Import a profile file" icon="pi pi-upload" size="small" severity="secondary" outlined @click="fileInput?.click()" />
        </div>
      </div>
    </Message>

    <input ref="fileInput" type="file" accept="application/json,.json" class="hidden" @change="importFile" />

    <Dialog :visible="!!nameDialog" modal :header="dialogTitle" class="w-[min(28rem,92vw)]" @update:visible="(v: boolean) => !v && (nameDialog = null)">
      <form v-if="nameDialog" class="flex flex-col gap-4" @submit.prevent="submitName">
        <div class="flex flex-col gap-1">
          <label for="profileName" class="text-sm font-medium">Name</label>
          <InputText id="profileName" v-model="nameDialog.name" :maxlength="MAX_DISPLAY_PROFILE_NAME" autofocus />
        </div>
        <fieldset v-if="nameDialog.mode === 'create'" class="flex flex-col gap-2">
          <legend class="mb-1 text-sm font-medium">Start from</legend>
          <div class="flex items-center gap-2">
            <RadioButton v-model="nameDialog.source" input-id="profileSourceCurrent" value="current" />
            <label for="profileSourceCurrent" class="text-sm cursor-pointer">A copy of the current settings</label>
          </div>
          <div class="flex items-center gap-2">
            <RadioButton v-model="nameDialog.source" input-id="profileSourceDefaults" value="defaults" />
            <label for="profileSourceDefaults" class="text-sm cursor-pointer">Jiten’s default settings</label>
          </div>
        </fieldset>
        <div class="flex justify-end gap-2">
          <Button type="button" label="Cancel" severity="secondary" outlined @click="nameDialog = null" />
          <Button type="submit" :label="nameDialog.mode === 'rename' ? 'Rename' : 'Save'" :loading="busy" :disabled="!nameDialog.name.trim()" />
        </div>
      </form>
    </Dialog>
  </div>
</template>
