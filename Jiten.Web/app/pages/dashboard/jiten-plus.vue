<script setup lang="ts">
  useHead({ title: 'Jiten+ Management - Jiten' });
  definePageMeta({ middleware: ['auth-admin'] });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  interface UserResult {
    userId: string;
    userName: string;
    email: string;
  }

  interface PromoCode {
    codeId: number;
    code: string;
    description: string | null;
    durationDays: number;
    maxUses: number | null;
    currentUses: number;
    expiresAt: string | null;
    createdAt: string;
    isActive: boolean;
    grantsFullTier: boolean;
    redemptions: number;
  }

  interface DayGrant {
    userId: string;
    userName: string;
    grantedAt: string | null;
    days: number | null;
    grantsFullTier: boolean;
    remainingDays: number | null;
    thankYouMessage: string | null;
  }

  interface LifetimeGrant {
    userId: string;
    userName: string;
  }

  interface UsageRedemption {
    userId: string;
    userName: string;
    redeemedAt: string;
    remainingDays: number;
    fullyUsedAt: string | null;
  }

  interface CodeUsage {
    codeId: number;
    code: string;
    maxUses: number | null;
    currentUses: number;
    redemptions: UsageRedemption[];
  }

  // ---- Grant panel ----
  const searchQuery = ref('');
  const searchResults = ref<UserResult[]>([]);
  const selectedUser = ref<UserResult | null>(null);
  const searching = ref(false);

  const grantType = ref<'monthly' | 'yearly' | 'lifetime' | 'custom'>('yearly');
  const grantTypeOptions = [
    { label: 'Monthly (30 days)', value: 'monthly' },
    { label: 'Yearly (365 days)', value: 'yearly' },
    { label: 'Lifetime', value: 'lifetime' },
    { label: 'Custom days', value: 'custom' },
  ];
  const customDays = ref(30);
  const grantsFullTier = ref(true);
  const thankYouMessage = ref('');
  const granting = ref(false);

  const grantDays = computed(() => {
    switch (grantType.value) {
      case 'monthly':
        return 30;
      case 'yearly':
        return 365;
      case 'custom':
        return customDays.value;
      default:
        return null;
    }
  });

  const grantSummary = computed(() => {
    if (grantType.value === 'lifetime') return 'lifetime Jiten+';
    return `${grantDays.value} day${grantDays.value === 1 ? '' : 's'} of Jiten+`;
  });

  const canGrant = computed(() => {
    if (!selectedUser.value) return false;
    if (grantType.value === 'custom' && (!customDays.value || customDays.value < 1)) return false;
    return true;
  });

  async function searchUsers() {
    if (searchQuery.value.trim().length < 2) return;
    try {
      searching.value = true;
      searchResults.value = await $api<UserResult[]>(`/admin/search-users?query=${encodeURIComponent(searchQuery.value.trim())}`);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to search users'), life: 5000 });
    } finally {
      searching.value = false;
    }
  }

  function selectUser(user: UserResult) {
    selectedUser.value = user;
    searchResults.value = [];
    searchQuery.value = '';
  }

  // Lifetime is irreversible-by-mistake (a real oops in live testing), so it gets a distinct danger dialog
  // that requires typing the recipient's username. Day grants keep the lightweight confirm.
  const showLifetimeConfirm = ref(false);
  const lifetimeConfirmName = ref('');
  const canConfirmLifetime = computed(() => !!selectedUser.value && lifetimeConfirmName.value.trim() === selectedUser.value.userName);

  function confirmGrant() {
    if (grantType.value === 'lifetime') {
      lifetimeConfirmName.value = '';
      showLifetimeConfirm.value = true;
      return;
    }
    confirm.require({
      message: `Grant ${grantSummary.value} to ${selectedUser.value?.userName}?\n\nTier: ${grantsFullTier.value ? 'Full' : 'Trial'}${
        thankYouMessage.value.trim() ? `\nMessage: "${thankYouMessage.value.trim()}"` : ''
      }`,
      header: 'Confirm grant',
      icon: 'pi pi-gift',
      accept: doGrant,
    });
  }

  async function doGrantLifetime() {
    showLifetimeConfirm.value = false;
    await doGrant();
  }

  async function doGrant() {
    granting.value = true;
    try {
      await $api('/admin/jiten-plus/grant', {
        method: 'POST',
        body: {
          userIdOrName: selectedUser.value!.userId,
          kind: grantType.value === 'lifetime' ? 'lifetime' : 'days',
          days: grantType.value === 'lifetime' ? null : grantDays.value,
          grantsFullTier: grantsFullTier.value,
          thankYouMessage: thankYouMessage.value.trim() || null,
        },
      });
      toast.add({ severity: 'success', summary: 'Granted', detail: `${grantSummary.value} delivered.`, life: 5000 });
      selectedUser.value = null;
      thankYouMessage.value = '';
      await loadGrants();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Grant failed'), life: 5000 });
    } finally {
      granting.value = false;
    }
  }

  // ---- Resend billing email ----
  const resendQuery = ref('');
  const resendResults = ref<UserResult[]>([]);
  const resendUser = ref<UserResult | null>(null);
  const resendSearching = ref(false);
  const resendKind = ref<'lifetime-confirmed' | 'subscription-confirmed'>('lifetime-confirmed');
  const resendKindOptions = [
    { label: 'Lifetime purchase confirmation', value: 'lifetime-confirmed' },
    { label: 'Subscription confirmation', value: 'subscription-confirmed' },
  ];
  const resending = ref(false);

  async function searchResendUsers() {
    if (resendQuery.value.trim().length < 2) return;
    try {
      resendSearching.value = true;
      resendResults.value = await $api<UserResult[]>(`/admin/search-users?query=${encodeURIComponent(resendQuery.value.trim())}`);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to search users'), life: 5000 });
    } finally {
      resendSearching.value = false;
    }
  }

  function selectResendUser(user: UserResult) {
    resendUser.value = user;
    resendResults.value = [];
    resendQuery.value = '';
  }

  function confirmResend() {
    const label = resendKindOptions.find((o) => o.value === resendKind.value)!.label.toLowerCase();
    confirm.require({
      message: `Resend the ${label} email to ${resendUser.value?.userName} (${resendUser.value?.email})?\n\nIt is rebuilt from their current account data.`,
      header: 'Resend billing email',
      icon: 'pi pi-envelope',
      accept: doResend,
    });
  }

  async function doResend() {
    resending.value = true;
    try {
      await $api('/admin/jiten-plus/resend-email', {
        method: 'POST',
        body: { userIdOrName: resendUser.value!.userId, kind: resendKind.value },
      });
      toast.add({ severity: 'success', summary: 'Sent', detail: `Email sent to ${resendUser.value!.userName}.`, life: 5000 });
      resendUser.value = null;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Resend failed'), life: 8000 });
    } finally {
      resending.value = false;
    }
  }

  // ---- Promo codes ----
  const codes = ref<PromoCode[]>([]);
  const loadingCodes = ref(false);

  async function loadCodes() {
    loadingCodes.value = true;
    try {
      codes.value = await $api<PromoCode[]>('/admin/promo-codes');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load codes'), life: 5000 });
    } finally {
      loadingCodes.value = false;
    }
  }

  // Create single
  const showCreate = ref(false);
  const newCode = reactive({
    code: '',
    description: '',
    durationDays: 7,
    maxUses: null as number | null,
    expiresAt: null as Date | null,
    grantsFullTier: false,
  });
  const creating = ref(false);

  function openCreate() {
    Object.assign(newCode, { code: '', description: '', durationDays: 7, maxUses: null, expiresAt: null, grantsFullTier: false });
    showCreate.value = true;
  }

  async function createCode() {
    creating.value = true;
    try {
      await $api('/admin/promo-codes', {
        method: 'POST',
        body: {
          code: newCode.code.trim() || null,
          description: newCode.description.trim() || null,
          durationDays: newCode.durationDays,
          maxUses: newCode.maxUses,
          expiresAt: newCode.expiresAt ? newCode.expiresAt.toISOString() : null,
          grantsFullTier: newCode.grantsFullTier,
        },
      });
      toast.add({ severity: 'success', summary: 'Created', detail: 'Promo code created.', life: 4000 });
      showCreate.value = false;
      await loadCodes();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to create code'), life: 5000 });
    } finally {
      creating.value = false;
    }
  }

  // Bulk generate
  const showBulk = ref(false);
  const bulk = reactive({ count: 10, description: '', durationDays: 7, maxUses: 1 as number | null, expiresAt: null as Date | null, grantsFullTier: false });
  const bulkGenerating = ref(false);
  const bulkResult = ref<string[] | null>(null);

  function openBulk() {
    Object.assign(bulk, { count: 10, description: '', durationDays: 7, maxUses: 1, expiresAt: null, grantsFullTier: false });
    bulkResult.value = null;
    showBulk.value = true;
  }

  async function generateBulk() {
    bulkGenerating.value = true;
    try {
      const result = await $api<{ count: number; codes: string[] }>('/admin/promo-codes/bulk-generate', {
        method: 'POST',
        body: {
          count: bulk.count,
          description: bulk.description.trim() || null,
          durationDays: bulk.durationDays,
          maxUses: bulk.maxUses,
          expiresAt: bulk.expiresAt ? bulk.expiresAt.toISOString() : null,
          grantsFullTier: bulk.grantsFullTier,
        },
      });
      bulkResult.value = result.codes;
      toast.add({ severity: 'success', summary: 'Generated', detail: `${result.count} codes created.`, life: 4000 });
      await loadCodes();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to generate codes'), life: 5000 });
    } finally {
      bulkGenerating.value = false;
    }
  }

  function copyBulk() {
    if (!bulkResult.value) return;
    navigator.clipboard.writeText(bulkResult.value.join('\n'));
    toast.add({ severity: 'success', summary: 'Copied', detail: 'Codes copied to clipboard.', life: 3000 });
  }

  function confirmDeactivate(code: PromoCode) {
    confirm.require({
      message: `Deactivate code ${code.code}? Existing redemptions keep their days; the code just can't be redeemed again.`,
      header: 'Deactivate code',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: () => deactivateCode(code.codeId),
    });
  }

  async function deactivateCode(id: number) {
    try {
      await $api(`/admin/promo-codes/${id}`, { method: 'DELETE' });
      toast.add({ severity: 'success', summary: 'Deactivated', detail: 'Code deactivated.', life: 4000 });
      await loadCodes();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to deactivate'), life: 5000 });
    }
  }

  // Usage drill-down
  const showUsage = ref(false);
  const usage = ref<CodeUsage | null>(null);
  const loadingUsage = ref(false);

  async function viewUsage(code: PromoCode) {
    loadingUsage.value = true;
    showUsage.value = true;
    usage.value = null;
    try {
      usage.value = await $api<CodeUsage>(`/admin/promo-codes/${code.codeId}/usage`);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load usage'), life: 5000 });
    } finally {
      loadingUsage.value = false;
    }
  }

  // ---- Grants log ----
  const dayGrants = ref<DayGrant[]>([]);
  const lifetimeGrants = ref<LifetimeGrant[]>([]);
  const loadingGrants = ref(false);

  async function loadGrants() {
    loadingGrants.value = true;
    try {
      const result = await $api<{ dayGrants: DayGrant[]; lifetimeGrants: LifetimeGrant[] }>('/admin/jiten-plus/grants');
      dayGrants.value = result.dayGrants;
      lifetimeGrants.value = result.lifetimeGrants;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load grants'), life: 5000 });
    } finally {
      loadingGrants.value = false;
    }
  }

  function confirmRevoke(grant: LifetimeGrant) {
    confirm.require({
      message: `Revoke lifetime Jiten+ from ${grant.userName}? This is for mistaken contributor grants only — purchased lifetimes are rejected. The user is not notified.`,
      header: 'Revoke lifetime',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      acceptLabel: 'Revoke',
      accept: () => revokeLifetime(grant),
    });
  }

  async function revokeLifetime(grant: LifetimeGrant) {
    try {
      await $api('/admin/jiten-plus/revoke-lifetime', {
        method: 'POST',
        body: { userIdOrName: grant.userId },
      });
      toast.add({ severity: 'success', summary: 'Revoked', detail: `Lifetime revoked from ${grant.userName}.`, life: 4000 });
      await loadGrants();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to revoke lifetime'), life: 5000 });
    }
  }

  function formatDate(value: string | null) {
    return value ? new Date(value).toLocaleString() : '—';
  }

  onMounted(() => {
    loadCodes();
    loadGrants();
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Jiten+ Management</h1>
    </div>

    <!-- Grant panel -->
    <Card class="shadow-md mb-6">
      <template #title>
        <h2 class="text-xl font-semibold">Grant Jiten+ (reward)</h2>
      </template>
      <template #content>
        <div class="flex flex-col gap-4 max-w-2xl">
          <div>
            <label class="block text-sm font-medium mb-1">User</label>
            <div v-if="selectedUser" class="flex items-center gap-2 p-2 bg-surface-100 dark:bg-surface-800 rounded">
              <span class="font-medium">{{ selectedUser.userName }}</span>
              <span class="text-sm text-surface-500 dark:text-surface-400">({{ selectedUser.email }})</span>
              <Button icon="pi pi-times" class="p-button-text p-button-sm p-button-danger ml-auto" @click="selectedUser = null" />
            </div>
            <div v-else class="flex gap-2">
              <InputText v-model="searchQuery" placeholder="Search by username or email" class="flex-1" @keydown.enter="searchUsers" />
              <Button label="Search" icon="pi pi-search" :loading="searching" :disabled="searchQuery.trim().length < 2" @click="searchUsers" />
            </div>
            <div v-if="searchResults.length" class="mt-2 border border-surface-200 dark:border-surface-700 rounded overflow-hidden">
              <div
                v-for="user in searchResults"
                :key="user.userId"
                class="p-2 hover:bg-surface-100 dark:hover:bg-surface-800 cursor-pointer flex justify-between items-center"
                @click="selectUser(user)"
              >
                <span class="font-medium">{{ user.userName }}</span>
                <span class="text-sm text-surface-500 dark:text-surface-400">{{ user.email }}</span>
              </div>
            </div>
          </div>

          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label class="block text-sm font-medium mb-1">Grant</label>
              <Select v-model="grantType" :options="grantTypeOptions" option-label="label" option-value="value" class="w-full" />
            </div>
            <div v-if="grantType === 'custom'">
              <label class="block text-sm font-medium mb-1">Days</label>
              <InputNumber v-model="customDays" :min="1" :max="100000" class="w-full" />
            </div>
          </div>

          <div class="flex items-center gap-3">
            <ToggleSwitch v-model="grantsFullTier" input-id="grantFull" />
            <label for="grantFull" class="text-sm font-medium">
              Full tier {{ grantsFullTier ? '(includes storage features)' : '(trial — compute features only)' }}
            </label>
          </div>

          <div>
            <label class="block text-sm font-medium mb-1">Thank-you message (optional)</label>
            <Textarea
              v-model="thankYouMessage"
              rows="3"
              maxlength="1000"
              class="w-full"
              placeholder="A personal note the recipient will see in-app and by email"
            />
            <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">{{ thankYouMessage.length }}/1000</p>
          </div>

          <div class="flex justify-end">
            <Button label="Grant Jiten+" icon="pi pi-gift" :loading="granting" :disabled="!canGrant || granting" @click="confirmGrant" />
          </div>
        </div>
      </template>
    </Card>

    <!-- Resend billing email -->
    <Card class="shadow-md mb-6">
      <template #title>
        <h2 class="text-xl font-semibold">Resend billing email</h2>
      </template>
      <template #content>
        <div class="flex flex-col gap-4 max-w-2xl">
          <p class="text-sm text-surface-500 dark:text-surface-400">
            For when a purchase confirmation failed to send (see the billing-email alert for the user id). The email is rebuilt from the user's current billing
            data.
          </p>
          <div>
            <label class="block text-sm font-medium mb-1">User</label>
            <div v-if="resendUser" class="flex items-center gap-2 p-2 bg-surface-100 dark:bg-surface-800 rounded">
              <span class="font-medium">{{ resendUser.userName }}</span>
              <span class="text-sm text-surface-500 dark:text-surface-400">({{ resendUser.email }})</span>
              <Button icon="pi pi-times" class="p-button-text p-button-sm p-button-danger ml-auto" @click="resendUser = null" />
            </div>
            <div v-else class="flex gap-2">
              <InputText v-model="resendQuery" placeholder="Search by username, email or user id" class="flex-1" @keydown.enter="searchResendUsers" />
              <Button label="Search" icon="pi pi-search" :loading="resendSearching" :disabled="resendQuery.trim().length < 2" @click="searchResendUsers" />
            </div>
            <div v-if="resendResults.length" class="mt-2 border border-surface-200 dark:border-surface-700 rounded overflow-hidden">
              <div
                v-for="user in resendResults"
                :key="user.userId"
                class="p-2 hover:bg-surface-100 dark:hover:bg-surface-800 cursor-pointer flex justify-between items-center"
                @click="selectResendUser(user)"
              >
                <span class="font-medium">{{ user.userName }}</span>
                <span class="text-sm text-surface-500 dark:text-surface-400">{{ user.email }}</span>
              </div>
            </div>
          </div>
          <div>
            <label class="block text-sm font-medium mb-1">Email</label>
            <Select v-model="resendKind" :options="resendKindOptions" option-label="label" option-value="value" class="w-full" />
          </div>
          <div class="flex justify-end">
            <Button label="Resend email" icon="pi pi-envelope" :loading="resending" :disabled="!resendUser || resending" @click="confirmResend" />
          </div>
        </div>
      </template>
    </Card>

    <!-- Promo codes -->
    <Card class="shadow-md mb-6">
      <template #title>
        <div class="flex items-center justify-between">
          <h2 class="text-xl font-semibold">Promo codes</h2>
          <div class="flex gap-2">
            <Button label="Create" icon="pi pi-plus" size="small" @click="openCreate" />
            <Button label="Bulk generate" icon="pi pi-clone" size="small" severity="secondary" @click="openBulk" />
          </div>
        </div>
      </template>
      <template #content>
        <DataTable :value="codes" :loading="loadingCodes" :paginator="codes.length > 10" :rows="10" striped-rows>
          <Column field="code" header="Code" :sortable="true">
            <template #body="{ data }">
              <code class="font-mono">{{ data.code }}</code>
            </template>
          </Column>
          <Column field="durationDays" header="Days" :sortable="true" style="width: 90px" />
          <Column header="Tier" style="width: 90px">
            <template #body="{ data }">
              <Tag :value="data.grantsFullTier ? 'Full' : 'Trial'" :severity="data.grantsFullTier ? 'success' : 'info'" />
            </template>
          </Column>
          <Column header="Uses">
            <template #body="{ data }">{{ data.currentUses }}{{ data.maxUses ? ` / ${data.maxUses}` : '' }}</template>
          </Column>
          <Column header="Expires">
            <template #body="{ data }">{{ data.expiresAt ? formatDate(data.expiresAt) : 'Never' }}</template>
          </Column>
          <Column header="Status" style="width: 90px">
            <template #body="{ data }">
              <Tag :value="data.isActive ? 'Active' : 'Inactive'" :severity="data.isActive ? 'success' : 'secondary'" />
            </template>
          </Column>
          <Column field="description" header="Description" />
          <Column header="Actions" style="width: 130px">
            <template #body="{ data }">
              <div class="flex gap-2">
                <Button v-tooltip.top="'Usage'" icon="pi pi-chart-bar" size="small" severity="info" @click="viewUsage(data)" />
                <Button
                  v-tooltip.top="'Deactivate'"
                  icon="pi pi-ban"
                  size="small"
                  severity="danger"
                  :disabled="!data.isActive"
                  @click="confirmDeactivate(data)"
                />
              </div>
            </template>
          </Column>
        </DataTable>
      </template>
    </Card>

    <!-- Grants log -->
    <Card class="shadow-md">
      <template #title>
        <h2 class="text-xl font-semibold">Grants log</h2>
      </template>
      <template #content>
        <h3 class="font-semibold mb-2">Day grants</h3>
        <DataTable :value="dayGrants" :loading="loadingGrants" :paginator="dayGrants.length > 10" :rows="10" striped-rows class="mb-6">
          <Column field="userName" header="User" />
          <Column field="days" header="Days" style="width: 90px" />
          <Column header="Tier" style="width: 90px">
            <template #body="{ data }">
              <Tag :value="data.grantsFullTier ? 'Full' : 'Trial'" :severity="data.grantsFullTier ? 'success' : 'info'" />
            </template>
          </Column>
          <Column field="remainingDays" header="Remaining" style="width: 110px" />
          <Column header="Granted">
            <template #body="{ data }">{{ formatDate(data.grantedAt) }}</template>
          </Column>
          <Column field="thankYouMessage" header="Message" />
        </DataTable>

        <h3 class="font-semibold mb-2">Contributor lifetime grants</h3>
        <DataTable :value="lifetimeGrants" :loading="loadingGrants" :paginator="lifetimeGrants.length > 10" :rows="10" striped-rows>
          <Column field="userName" header="User" />
          <Column header="Type">
            <template #body>
              <Tag value="Lifetime" severity="success" />
            </template>
          </Column>
          <Column header="Actions" style="width: 120px">
            <template #body="{ data }">
              <Button
                v-tooltip.top="'Revoke a mistaken contributor grant'"
                label="Revoke"
                icon="pi pi-times"
                size="small"
                severity="danger"
                outlined
                @click="confirmRevoke(data)"
              />
            </template>
          </Column>
        </DataTable>
      </template>
    </Card>

    <!-- Create dialog -->
    <Dialog v-model:visible="showCreate" header="Create promo code" :modal="true" class="w-full md:w-1/2">
      <div class="flex flex-col gap-4">
        <div>
          <label class="block text-sm font-medium mb-1">Code (optional — generated if blank)</label>
          <InputText v-model="newCode.code" placeholder="e.g. LAUNCH2026" class="w-full" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Description</label>
          <InputText v-model="newCode.description" placeholder="Admin note" class="w-full" />
        </div>
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-sm font-medium mb-1">Duration (days)</label>
            <InputNumber v-model="newCode.durationDays" :min="1" :max="100000" class="w-full" />
          </div>
          <div>
            <label class="block text-sm font-medium mb-1">Max uses (blank = unlimited)</label>
            <InputNumber v-model="newCode.maxUses" :min="1" class="w-full" />
          </div>
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Expires at (optional)</label>
          <DatePicker v-model="newCode.expiresAt" show-time hour-format="24" class="w-full" />
        </div>
        <div class="flex items-center gap-3">
          <ToggleSwitch v-model="newCode.grantsFullTier" input-id="createFull" />
          <label for="createFull" class="text-sm font-medium">Grants Full tier</label>
        </div>
      </div>
      <template #footer>
        <Button label="Cancel" class="p-button-text" @click="showCreate = false" />
        <Button label="Create" icon="pi pi-check" :loading="creating" @click="createCode" />
      </template>
    </Dialog>

    <!-- Bulk dialog -->
    <Dialog v-model:visible="showBulk" header="Bulk generate codes" :modal="true" class="w-full md:w-1/2">
      <div v-if="!bulkResult" class="flex flex-col gap-4">
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-sm font-medium mb-1">How many</label>
            <InputNumber v-model="bulk.count" :min="1" :max="1000" class="w-full" />
          </div>
          <div>
            <label class="block text-sm font-medium mb-1">Duration (days)</label>
            <InputNumber v-model="bulk.durationDays" :min="1" :max="100000" class="w-full" />
          </div>
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Max uses per code</label>
          <InputNumber v-model="bulk.maxUses" :min="1" class="w-full" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Description</label>
          <InputText v-model="bulk.description" placeholder="e.g. Twitter giveaway" class="w-full" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Expires at (optional)</label>
          <DatePicker v-model="bulk.expiresAt" show-time hour-format="24" class="w-full" />
        </div>
        <div class="flex items-center gap-3">
          <ToggleSwitch v-model="bulk.grantsFullTier" input-id="bulkFull" />
          <label for="bulkFull" class="text-sm font-medium">Grants Full tier</label>
        </div>
      </div>
      <div v-else>
        <p class="mb-2 font-medium">{{ bulkResult.length }} codes generated:</p>
        <Textarea :model-value="bulkResult.join('\n')" rows="10" readonly class="w-full font-mono text-sm" />
      </div>
      <template #footer>
        <template v-if="!bulkResult">
          <Button label="Cancel" class="p-button-text" @click="showBulk = false" />
          <Button label="Generate" icon="pi pi-clone" :loading="bulkGenerating" @click="generateBulk" />
        </template>
        <template v-else>
          <Button label="Copy" icon="pi pi-copy" severity="secondary" @click="copyBulk" />
          <Button label="Done" icon="pi pi-check" @click="showBulk = false" />
        </template>
      </template>
    </Dialog>

    <!-- Lifetime grant confirmation (danger, type-to-confirm) -->
    <Dialog v-model:visible="showLifetimeConfirm" header="Grant LIFETIME Jiten+" :modal="true" class="w-full md:w-1/2">
      <div class="flex flex-col gap-4">
        <Message severity="error" :closable="false">
          <span class="font-semibold">This is irreversible by the recipient.</span>
          You are about to grant
          <span class="font-semibold">permanent</span>
          lifetime Jiten+ to
          <span class="font-semibold">{{ selectedUser?.userName }}</span
          >. It can only be undone by an admin revoke, and only for contributor grants. Make sure you didn't mean to grant a fixed number of days.
        </Message>
        <div>
          <label class="block text-sm font-medium mb-1">
            Type
            <span class="font-mono font-semibold">{{ selectedUser?.userName }}</span>
            to confirm
          </label>
          <InputText v-model="lifetimeConfirmName" placeholder="Recipient username" class="w-full" autocomplete="off" />
        </div>
      </div>
      <template #footer>
        <Button label="Cancel" class="p-button-text" @click="showLifetimeConfirm = false" />
        <Button
          label="Grant lifetime"
          icon="pi pi-gift"
          severity="danger"
          :loading="granting"
          :disabled="!canConfirmLifetime || granting"
          @click="doGrantLifetime"
        />
      </template>
    </Dialog>

    <!-- Usage dialog -->
    <Dialog v-model:visible="showUsage" header="Code usage" :modal="true" class="w-full md:w-2/3">
      <div v-if="loadingUsage" class="flex justify-center py-8"><ProgressSpinner style="width: 50px; height: 50px" /></div>
      <div v-else-if="usage">
        <p class="mb-4">
          <code class="font-mono">{{ usage.code }}</code>
          — {{ usage.currentUses }}{{ usage.maxUses ? ` / ${usage.maxUses}` : '' }} uses
        </p>
        <DataTable :value="usage.redemptions" :paginator="usage.redemptions.length > 10" :rows="10" striped-rows>
          <Column field="userName" header="User" />
          <Column header="Redeemed">
            <template #body="{ data }">{{ formatDate(data.redeemedAt) }}</template>
          </Column>
          <Column field="remainingDays" header="Remaining" />
          <Column header="Fully used">
            <template #body="{ data }">{{ data.fullyUsedAt ? formatDate(data.fullyUsedAt) : '—' }}</template>
          </Column>
        </DataTable>
        <p v-if="!usage.redemptions.length" class="text-center py-6 text-surface-500 dark:text-surface-400">No redemptions yet.</p>
      </div>
    </Dialog>
  </div>
</template>

<style scoped></style>
