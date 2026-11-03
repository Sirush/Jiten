<script setup lang="ts">
  import { debounce } from 'perfect-debounce';
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import type { MetadataContributor, MetadataContributorRole, MetadataEditMetrics, MetadataEditSettings } from '~/types/metadataEdits';
  import { formatDayMonth } from '~/utils/dateFormat';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { showErrorToast, showSuccessToast } from '~/utils/toast';
  import { contributorLevelLabel } from '~/utils/metadataEdits';

  useHead({ title: 'Metadata edit settings' });
  definePageMeta({ middleware: ['auth-admin'] });

  const PAGE_SIZE = 25;

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const metrics = ref<MetadataEditMetrics | null>(null);
  const metricsFailed = ref(false);

  async function loadMetrics() {
    metricsFailed.value = false;
    try {
      metrics.value = await $api<MetadataEditMetrics>('admin/metadata-edits/metrics');
    } catch {
      metricsFailed.value = true;
    }
  }

  /** "3 h" under two days, "4 d" after; the queue's age matters at that resolution, not to the minute. */
  function hoursText(hours: number | null): string {
    if (hours == null) return 'None';
    if (hours < 1) return 'Under 1 h';
    return hours < 48 ? `${Math.round(hours)} h` : `${Math.round(hours / 24)} d`;
  }

  const metricTiles = computed(() => {
    const m = metrics.value;
    if (!m) return [];
    const approvalRate = m.decided30d ? Math.round((m.approved30d / m.decided30d) * 100) : null;
    return [
      { label: 'Waiting for review', value: m.pending.toLocaleString(), hint: m.oldestPendingHours == null ? 'Queue is empty' : `Oldest ${hoursText(m.oldestPendingHours)}` },
      { label: 'Live, unreviewed', value: m.liveUnreviewed.toLocaleString(), hint: `${m.autoApplied30d.toLocaleString()} went live without review in 30 days` },
      { label: 'Median wait for a decision', value: hoursText(m.medianHoursToDecision30d), hint: 'Last 30 days' },
      {
        label: 'Decisions in 30 days',
        value: m.decided30d.toLocaleString(),
        hint: approvalRate == null ? 'None yet' : `${approvalRate}% approved, ${m.reverted30d.toLocaleString()} reverted`,
      },
      { label: 'Submissions in 30 days', value: m.submissions30d.toLocaleString(), hint: `${m.withdrawn30d.toLocaleString()} withdrawn by their authors` },
    ];
  });

  const settings = ref<MetadataEditSettings | null>(null);
  const settingsFailed = ref(false);
  const savingSettings = ref(false);

  async function loadSettings() {
    settingsFailed.value = false;
    try {
      settings.value = await $api<MetadataEditSettings>('admin/metadata-edits/settings');
    } catch {
      settingsFailed.value = true;
    }
  }

  async function saveSettings() {
    if (!settings.value) return;
    savingSettings.value = true;
    try {
      settings.value = (await $api<MetadataEditSettings>('admin/metadata-edits/settings', { method: 'PUT', body: settings.value })) ?? settings.value;
      showSuccessToast(toast, 'Settings saved');
    } catch (err) {
      showErrorToast(toast, "Settings weren't saved", apiErrorMessage(err, 'Try again in a moment.'));
    } finally {
      savingSettings.value = false;
    }
  }

  const THRESHOLDS: { key: keyof MetadataEditSettings; label: string }[] = [
    { key: 'establishedMinAccountDays', label: 'Established: account age (days)' },
    { key: 'establishedCountedEdits', label: 'Established: counted edits' },
    { key: 'establishedNoRevertDays', label: 'Established: revert window (days)' },
    { key: 'trustedCountedEdits', label: 'Trusted: counted edits' },
    { key: 'trustedNoRevertDays', label: 'Trusted: revert window (days)' },
  ];

  const AUTO_APPLY_OPTIONS = [
    { value: -1, label: 'Nothing, every edit waits for review' },
    { value: 0, label: 'Low risk fields' },
    { value: 1, label: 'Low and medium risk fields' },
    { value: 2, label: 'Every field' },
  ];

  const AUTO_APPLY_LEVELS: { key: 'establishedAutoApplyMaxTier' | 'trustedAutoApplyMaxTier'; label: string }[] = [
    { key: 'establishedAutoApplyMaxTier', label: 'Established' },
    { key: 'trustedAutoApplyMaxTier', label: 'Trusted' },
  ];

  const search = ref('');
  const levelFilter = ref('all');
  const statusFilter = ref('all');
  const sortBy = ref('counted');
  const offset = ref(0);

  const LEVEL_FILTERS = [
    { value: 'all', label: 'All levels' },
    { value: 'new', label: 'New' },
    { value: 'established', label: 'Established' },
    { value: 'trusted', label: 'Trusted' },
    { value: 'editor', label: 'Editor' },
  ];

  const STATUS_FILTERS = [
    { value: 'all', label: 'Everyone' },
    { value: 'reviewers', label: 'Reviewers and editors' },
    { value: 'adminTrusted', label: 'Trusted by admin' },
    { value: 'reverted', label: 'Has reverted edits' },
    { value: 'blocked', label: 'Blocked' },
    { value: 'banned', label: "Can't contribute" },
  ];

  const SORTS = [
    { value: 'counted', label: 'Most counted edits' },
    { value: 'accepted', label: 'Most accepted edits' },
    { value: 'reverted', label: 'Most reverted edits' },
    { value: 'checks', label: 'Most checks' },
    { value: 'missed', label: 'Most missed checks' },
    { value: 'level', label: 'Highest level' },
    { value: 'name', label: 'Name' },
    { value: 'newest', label: 'Newest account' },
  ];
  const contributors = ref<MetadataContributor[]>([]);
  const contributorTotal = ref(0);
  const contributorsLoading = ref(false);
  const contributorsFailed = ref(false);
  const busyUserId = ref<string | null>(null);

  async function loadContributors() {
    contributorsLoading.value = true;
    contributorsFailed.value = false;
    try {
      const page = await $api<{ total: number; items: MetadataContributor[] }>('admin/metadata-edits/contributors', {
        query: {
          search: search.value.trim() || undefined,
          level: levelFilter.value === 'all' ? undefined : levelFilter.value,
          status: statusFilter.value === 'all' ? undefined : statusFilter.value,
          sort: sortBy.value,
          offset: offset.value,
          limit: PAGE_SIZE,
        },
      });
      contributors.value = page.items;
      contributorTotal.value = page.total;
    } catch {
      contributorsFailed.value = true;
    } finally {
      contributorsLoading.value = false;
    }
  }

  const searchContributors = debounce(() => {
    offset.value = 0;
    loadContributors();
  }, 300);

  watch(search, () => searchContributors());
  watch([levelFilter, statusFilter, sortBy], () => {
    offset.value = 0;
    loadContributors();
  });

  function onPage(event: { first: number }) {
    offset.value = event.first;
    loadContributors();
  }

  const ROLE_OPTIONS: { value: MetadataContributorRole; label: string }[] = [
    { value: 'none', label: 'Contributor' },
    { value: 'reviewer', label: 'Reviewer' },
    { value: 'editor', label: 'Editor' },
  ];

  function roleOf(contributor: MetadataContributor): MetadataContributorRole {
    if (contributor.roles.includes('Editor')) return 'editor';
    if (contributor.roles.includes('Reviewer')) return 'reviewer';
    return 'none';
  }

  const isAdminAccount = (contributor: MetadataContributor) => contributor.roles.includes('Administrator');

  async function updateContributor(
    contributor: MetadataContributor,
    body: { adminTrusted?: boolean; clearBlock?: boolean; banned?: boolean; role?: MetadataContributorRole },
    done: string
  ) {
    busyUserId.value = contributor.userId;
    try {
      await $api(`admin/metadata-edits/contributors/${contributor.userId}`, { method: 'PUT', body });
      if (body.adminTrusted !== undefined) contributor.adminTrusted = body.adminTrusted;
      if (body.clearBlock) contributor.blocked = false;
      if (body.banned !== undefined) contributor.banned = body.banned;
      if (body.role) {
        const others = contributor.roles.filter((r) => r !== 'Editor' && r !== 'Reviewer');
        contributor.roles = body.role === 'none' ? others : [...others, body.role === 'editor' ? 'Editor' : 'Reviewer'];
      }
      showSuccessToast(toast, done);
    } catch (err) {
      showErrorToast(toast, "The change wasn't saved", apiErrorMessage(err, 'Try again in a moment.'));
    } finally {
      busyUserId.value = null;
    }
  }

  const rowMenu = ref<{ toggle: (event: Event) => void } | null>(null);
  const rowMenuTarget = ref<MetadataContributor | null>(null);

  const rowMenuItems = computed(() => {
    const c = rowMenuTarget.value;
    if (!c) return [];
    return [
      {
        label: 'Trusted by admin',
        icon: c.adminTrusted ? 'pi pi-check-square' : 'pi pi-stop',
        command: () => updateContributor(c, { adminTrusted: !c.adminTrusted }, c.adminTrusted ? 'Trust removed' : 'Marked as trusted'),
      },
      {
        label: "Can't contribute",
        icon: c.banned ? 'pi pi-check-square' : 'pi pi-stop',
        command: () => updateContributor(c, { banned: !c.banned }, c.banned ? 'Contributing turned back on' : 'Contributing turned off'),
      },
      { label: 'Clear block', icon: 'pi pi-unlock', visible: c.blocked, command: () => updateContributor(c, { clearBlock: true }, 'Block cleared') },
      { separator: true },
      { label: 'Revert all edits', icon: 'pi pi-undo', class: 'text-red-600 dark:text-red-400', command: () => revertAll(c) },
    ];
  });

  function openRowMenu(event: Event, contributor: MetadataContributor) {
    rowMenuTarget.value = contributor;
    rowMenu.value?.toggle(event);
  }

  const matched = (c: MetadataContributor) => c.checksConfirmedApproved + c.checksDoubtedRejected;
  const missed = (c: MetadataContributor) => c.checksConfirmedRejected + c.checksDoubtedApproved;

  function checksTooltip(c: MetadataContributor): string {
    if (!c.checks) return 'No checks yet.';
    const lines = [
      '**Said it looks right**',
      `${c.checksConfirmedApproved} approved, ${c.checksConfirmedRejected} rejected`,
      "**Said it doesn't look right**",
      `${c.checksDoubtedRejected} rejected, ${c.checksDoubtedApproved} approved`,
    ];
    const waiting = c.checks - matched(c) - missed(c);
    if (waiting > 0) lines.push('', `${waiting} still waiting for a decision`);
    return lines.join('\n');
  }

  function revertAll(contributor: MetadataContributor) {
    const name = contributor.displayName ?? contributor.email;
    confirm.require({
      header: `Revert every edit by ${name}?`,
      message: "Every live edit from this account will be reverted. Fields changed again since will be left alone. This can't be undone.",
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Revert all',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      rejectProps: { severity: 'secondary', text: true },
      accept: async () => {
        busyUserId.value = contributor.userId;
        try {
          const result = await $api<{ reverted: number; partial: number }>(`admin/metadata-edits/contributors/${contributor.userId}/revert-all`, {
            method: 'POST',
          });
          showSuccessToast(
            toast,
            `Reverted ${result.reverted} ${result.reverted === 1 ? 'edit' : 'edits'}`,
            result.partial ? `${result.partial} only partly, because some fields were changed again since.` : undefined
          );
          loadContributors();
        } catch (err) {
          showErrorToast(toast, "The edits weren't reverted", apiErrorMessage(err, 'Try again in a moment.'));
        } finally {
          busyUserId.value = null;
        }
      },
    });
  }

  onMounted(() => {
    loadMetrics();
    loadSettings();
    loadContributors();
  });
</script>

<template>
  <div class="container mx-auto p-4 flex flex-col gap-6">
    <div class="flex items-center gap-2">
      <Button icon="pi pi-arrow-left" text aria-label="Back to the dashboard" @click="navigateTo('/dashboard')" />
      <h1 class="m-0 text-2xl md:text-3xl font-bold">Metadata edit settings</h1>
    </div>

    <Card>
      <template #title>Review workload</template>
      <template #content>
        <div v-if="metricsFailed" class="flex items-center gap-2">
          <span>The numbers didn't load.</span>
          <Button label="Retry" size="small" text @click="loadMetrics" />
        </div>
        <div v-else-if="!metrics" class="grid grid-cols-2 lg:grid-cols-5 gap-4" aria-busy="true">
          <Skeleton v-for="i in 5" :key="i" height="4.5rem" />
        </div>
        <div v-else class="flex flex-col gap-4">
          <dl class="m-0 grid grid-cols-2 lg:grid-cols-5 gap-4">
            <div v-for="tile in metricTiles" :key="tile.label" class="flex flex-col gap-0.5 min-w-0">
              <dt class="text-xs font-semibold text-muted-color">{{ tile.label }}</dt>
              <dd class="m-0 text-2xl font-bold tabular-nums">{{ tile.value }}</dd>
              <dd class="m-0 text-xs text-muted-color">{{ tile.hint }}</dd>
            </div>
          </dl>
          <table class="w-full max-w-xl text-sm tabular-nums">
            <caption class="sr-only">Edits per week</caption>
            <thead>
              <tr class="text-xs text-muted-color text-left">
                <th class="font-semibold py-1">Week from</th>
                <th class="font-semibold py-1 text-right">Submissions</th>
                <th class="font-semibold py-1 text-right">Decisions</th>
                <th class="font-semibold py-1 text-right">Live without review</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="week in metrics.weeks" :key="week.weekStart" class="border-t border-surface-200 dark:border-surface-700">
                <td class="py-1">{{ formatDayMonth(week.weekStart) }}</td>
                <td class="py-1 text-right">{{ week.submissions.toLocaleString() }}</td>
                <td class="py-1 text-right">{{ week.decisions.toLocaleString() }}</td>
                <td class="py-1 text-right">{{ week.autoApplied.toLocaleString() }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </Card>

    <Card>
      <template #title>Trust levels</template>
      <template #content>
        <div v-if="settingsFailed" class="flex items-center gap-2">
          <span>The settings didn't load.</span>
          <Button label="Retry" size="small" text @click="loadSettings" />
        </div>
        <div v-else-if="!settings" class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4" aria-busy="true">
          <Skeleton v-for="i in 7" :key="i" height="4rem" />
        </div>
        <form v-else class="flex flex-col gap-5" @submit.prevent="saveSettings">
          <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            <div v-for="threshold in THRESHOLDS" :key="threshold.key" class="flex flex-col gap-1">
              <label :for="`threshold-${threshold.key}`" class="text-sm font-semibold">{{ threshold.label }}</label>
              <InputNumber v-model="settings[threshold.key]" :input-id="`threshold-${threshold.key}`" :min="0" :max="3650" input-class="w-28 tabular-nums" />
            </div>
          </div>
          <p class="m-0 text-sm text-muted-color max-w-3xl">
            A counted edit is one approved submission, at most one per deck per day. Three checks that match the outcome add one, up to half of a threshold.
            Inside each revert window, one reverted submission is forgiven per 10 counted edits. Jiten+ Full members start at Trusted.
          </p>

          <div class="flex flex-col gap-3">
            <h3 class="m-0 text-base font-semibold">Goes live without review</h3>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div v-for="level in AUTO_APPLY_LEVELS" :key="level.key" class="flex flex-col gap-1">
                <label :for="`auto-${level.key}`" class="text-sm font-semibold">{{ level.label }}</label>
                <Select
                  v-model="settings[level.key]"
                  :input-id="`auto-${level.key}`"
                  :options="AUTO_APPLY_OPTIONS"
                  option-label="label"
                  option-value="value"
                  class="w-full"
                />
              </div>
            </div>
            <p class="m-0 text-sm text-muted-color max-w-3xl">
              New accounts always wait for review; editors always go live. Low risk: dates, links, romaji and English titles, aliases. Medium risk: genres,
              tags, related decks, character names. High risk: original title, media type, description.
            </p>
          </div>

          <div><Button type="submit" label="Save settings" :loading="savingSettings" /></div>
        </form>
      </template>
    </Card>

    <Card>
      <template #title>Contributors</template>
      <template #subtitle>Sort by counted edits to find reviewer candidates.</template>
      <template #content>
        <div class="flex flex-col gap-3">
          <div class="flex flex-wrap gap-2">
            <IconField class="w-full sm:w-72">
              <InputIcon class="pi pi-search" />
              <InputText v-model="search" placeholder="Search by name or email" aria-label="Search contributors" class="w-full" />
            </IconField>
            <Select
              v-model="levelFilter"
              :options="LEVEL_FILTERS"
              option-label="label"
              option-value="value"
              aria-label="Filter by level"
              class="w-full sm:w-44"
            />
            <Select
              v-model="statusFilter"
              :options="STATUS_FILTERS"
              option-label="label"
              option-value="value"
              aria-label="Filter by status"
              class="w-full sm:w-56"
            />
            <Select v-model="sortBy" :options="SORTS" option-label="label" option-value="value" aria-label="Sort contributors" class="w-full sm:w-56 sm:ml-auto" />
          </div>

          <div v-if="contributorsFailed && !contributors.length" class="flex items-center gap-2">
            <span>Contributors didn't load.</span>
            <Button label="Retry" size="small" text @click="loadContributors" />
          </div>
          <DataTable v-else :value="contributors" :loading="contributorsLoading" data-key="userId" size="small" striped-rows>
            <template #empty>
              <span class="text-muted-color">{{ search.trim() || levelFilter !== 'all' || statusFilter !== 'all' ? 'No contributor matches these filters.' : 'Nobody has suggested an edit yet.' }}</span>
            </template>
            <Column header="Contributor">
              <template #body="{ data }">
                <div class="flex flex-col min-w-0 max-w-56">
                  <span class="font-medium truncate">{{ data.displayName ?? '(no display name)' }}</span>
                  <span class="text-xs text-muted-color truncate" :title="data.email">{{ data.email }}</span>
                </div>
              </template>
            </Column>
            <Column header="Level">
              <template #body="{ data }">
                <span class="flex flex-col gap-0.5 max-w-56">
                  <span class="flex flex-wrap items-center gap-1">
                    <Tag :value="contributorLevelLabel(data.level)" :severity="data.blocked || data.banned ? 'danger' : 'secondary'" />
                    <span class="text-xs text-muted-color">{{ data.levelReason }}</span>
                  </span>
                  <span v-if="data.adminTrusted && data.levelReason !== 'Trusted by admin'" class="text-xs text-muted-color">
                    Trusted by admin{{ data.blocked || data.banned ? ', overridden' : '' }}
                  </span>
                </span>
              </template>
            </Column>
            <Column header="Edits">
              <template #body="{ data }">
                <div class="flex flex-col text-sm tabular-nums whitespace-nowrap">
                  <span>{{ data.counted }} counted, {{ data.accepted }} accepted</span>
                  <span :class="data.reverted ? 'text-red-600 dark:text-red-400' : 'text-muted-color'" class="text-xs">{{ data.reverted }} reverted</span>
                </div>
              </template>
            </Column>
            <Column header="Checks">
              <template #body="{ data }">
                <Tooltip :content="checksTooltip(data)">
                  <div class="flex flex-col text-sm tabular-nums whitespace-nowrap">
                    <span>{{ data.checks }} {{ data.checks === 1 ? 'check' : 'checks' }}</span>
                    <span :class="missed(data) ? 'text-red-600 dark:text-red-400' : 'text-muted-color'" class="text-xs">
                      {{ matched(data) }} matched, {{ missed(data) }} missed
                    </span>
                  </div>
                </Tooltip>
              </template>
            </Column>
            <Column header="Role">
              <template #body="{ data }">
                <span v-if="isAdminAccount(data)" class="text-sm text-muted-color">Admin</span>
                <Select
                  v-else
                  :model-value="roleOf(data)"
                  :options="ROLE_OPTIONS"
                  option-label="label"
                  option-value="value"
                  size="small"
                  :disabled="busyUserId === data.userId"
                  :aria-label="`Role for ${data.displayName ?? data.email}`"
                  class="w-36"
                  @update:model-value="updateContributor(data, { role: $event }, 'Role updated')"
                />
              </template>
            </Column>
            <Column header-class="w-12">
              <template #body="{ data }">
                <Button
                  icon="pi pi-ellipsis-v"
                  text
                  rounded
                  severity="secondary"
                  :disabled="busyUserId === data.userId"
                  :aria-label="`More actions for ${data.displayName ?? data.email}`"
                  aria-haspopup="true"
                  @click="openRowMenu($event, data)"
                />
              </template>
            </Column>
          </DataTable>
          <Menu ref="rowMenu" :model="rowMenuItems" popup />
          <Paginator v-if="contributorTotal > PAGE_SIZE" :first="offset" :rows="PAGE_SIZE" :total-records="contributorTotal" @page="onPage" />
        </div>
      </template>
    </Card>
  </div>
</template>
