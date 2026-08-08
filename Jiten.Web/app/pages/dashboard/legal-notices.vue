<script setup lang="ts">
  useHead({ title: 'Legal Notices - Jiten' });
  definePageMeta({ middleware: ['auth-admin'] });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  interface NoticeCandidate {
    userName: string;
    email: string;
    renewalDate: string;
    plan: string | null;
    status: 'would-send' | 'already-sent' | 'sent';
    sentAt: string | null;
    emailSubject: string;
    emailHtml: string;
  }

  interface NoticeResponse {
    dryRun: boolean;
    version: string;
    sent: number;
    skipped: number;
    subscribers: NoticeCandidate[];
  }

  const loading = ref(false);
  const sending = ref(false);
  const result = ref<NoticeResponse | null>(null);

  const pendingCount = computed(() => result.value?.subscribers.filter((s) => s.status === 'would-send').length ?? 0);

  async function loadDryRun() {
    loading.value = true;
    try {
      result.value = await $api<NoticeResponse>('/admin/legal/terms-change-notices?dryRun=true', { method: 'POST' });
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load candidates'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  function confirmSend() {
    const recipients = result.value?.subscribers.filter((s) => s.status === 'would-send') ?? [];
    confirm.require({
      message:
        `Send the Terms of Sale change notice (version ${result.value?.version}) to ${recipients.length} ` +
        `subscriber${recipients.length === 1 ? '' : 's'}?\n\n${recipients.map((r) => `${r.userName} <${r.email}>`).join('\n')}\n\n` +
        'Each send is logged; re-running never emails the same subscriber twice for the same renewal.',
      header: 'Send legal notices',
      icon: 'pi pi-envelope',
      acceptLabel: 'Send',
      accept: doSend,
    });
  }

  async function doSend() {
    sending.value = true;
    try {
      const response = await $api<NoticeResponse>('/admin/legal/terms-change-notices?dryRun=false', { method: 'POST' });
      result.value = response;
      toast.add({
        severity: 'success',
        summary: 'Notices sent',
        detail: `Sent ${response.sent}, skipped ${response.skipped} already covered.`,
        life: 6000,
      });
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Send failed'), life: 6000 });
    } finally {
      sending.value = false;
    }
  }

  // Preview dialog
  const showPreview = ref(false);
  const previewed = ref<NoticeCandidate | null>(null);

  function openPreview(candidate: NoticeCandidate) {
    previewed.value = candidate;
    showPreview.value = true;
  }

  function formatDate(value: string | null) {
    if (!value) return '—';
    return new Date(value).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  function statusSeverity(status: NoticeCandidate['status']) {
    return status === 'would-send' ? 'warn' : 'success';
  }

  function statusLabel(candidate: NoticeCandidate) {
    if (candidate.status === 'would-send') return 'Would send';
    if (candidate.status === 'sent') return 'Sent';
    return `Sent ${candidate.sentAt ? new Date(candidate.sentAt).toLocaleDateString() : ''}`.trim();
  }

  onMounted(loadDryRun);
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Legal Notices</h1>
    </div>

    <Card class="shadow-md">
      <template #title>
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h2 class="text-xl font-semibold">Terms-change notice (CGV art. 12.2)</h2>
          <Tag v-if="result" :value="`CGV version ${result.version}`" severity="info" />
        </div>
      </template>
      <template #content>
        <p class="mb-4 text-surface-600 dark:text-surface-400 text-sm">
          Written notice to recurring paid subscribers that updated Terms of Sale apply from their next renewal. Only
          accounts with a live paid Stripe subscription qualify — grant and promo-credit recipients, lifetime holders and
          cancelled subscribers are never emailed. Sends are logged per (subscriber, renewal date), so re-running is
          always safe — run it again after a new subscriber appears or after a terms version bump.
        </p>

        <DataTable :value="result?.subscribers ?? []" :loading="loading" striped-rows>
          <Column field="userName" header="User" />
          <Column field="email" header="Email" />
          <Column field="plan" header="Plan" style="width: 100px" />
          <Column header="Renews">
            <template #body="{ data }">{{ formatDate(data.renewalDate) }}</template>
          </Column>
          <Column header="Status" style="width: 150px">
            <template #body="{ data }">
              <Tag :value="statusLabel(data)" :severity="statusSeverity(data.status)" />
            </template>
          </Column>
          <Column header="" style="width: 120px">
            <template #body="{ data }">
              <Button label="Preview" icon="pi pi-eye" size="small" severity="secondary" outlined @click="openPreview(data)" />
            </template>
          </Column>
          <template #empty>
            <p class="text-center py-6 text-surface-500">
              {{ loading ? '' : 'No recurring paid subscribers need a notice right now.' }}
            </p>
          </template>
        </DataTable>

        <div class="flex flex-wrap justify-end gap-2 mt-4">
          <Button label="Refresh (dry run)" icon="pi pi-refresh" severity="secondary" :loading="loading" @click="loadDryRun" />
          <Button
            :label="pendingCount ? `Send ${pendingCount} notice${pendingCount === 1 ? '' : 's'}` : 'Nothing to send'"
            icon="pi pi-envelope"
            :disabled="!pendingCount || sending"
            :loading="sending"
            @click="confirmSend"
          />
        </div>
      </template>
    </Card>

    <!-- Email preview -->
    <Dialog v-model:visible="showPreview" header="Email preview" :modal="true" class="w-full md:w-2/3 lg:w-1/2">
      <div v-if="previewed" class="flex flex-col gap-3">
        <div class="text-sm">
          <div class="flex gap-2"><span class="w-16 text-surface-500 shrink-0">To</span><span class="font-medium">{{ previewed.userName }} &lt;{{ previewed.email }}&gt;</span></div>
          <div class="flex gap-2 mt-1"><span class="w-16 text-surface-500 shrink-0">Subject</span><span class="font-medium">{{ previewed.emailSubject }}</span></div>
        </div>
        <!-- Server-rendered email body; same HTML the mailer sends. -->
        <!-- eslint-disable-next-line vue/no-v-html -->
        <div class="border border-surface-200 dark:border-surface-700 rounded-lg p-4 bg-white dark:bg-surface-900 text-sm leading-relaxed" v-html="previewed.emailHtml" />
        <p class="text-xs text-surface-500">A plain-text version is generated automatically from this HTML at send time.</p>
      </div>
      <template #footer>
        <Button label="Close" class="p-button-text" @click="showPreview = false" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped></style>
