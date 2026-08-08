<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';
  import { useLegalStore } from '~/stores/legalStore';

  const auth = useAuthStore();
  const legal = useLegalStore();

  const expanded = ref(false);
  const consentTicked = ref(false);
  const accepting = ref(false);

  const visible = computed(() => auth.isAuthenticated && legal.fetched && legal.cguPending);
  const elapsed = computed(() => legal.status?.cgu.phase === 'elapsed');

  const effectiveDateLabel = computed(() => {
    const raw = legal.status?.cgu.effectiveDate;
    if (!raw) return null;
    const d = new Date(raw);
    if (Number.isNaN(d.getTime())) return null;
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  });

  onMounted(() => {
    legal.ensure();
  });

  // NoticeShownAt must mean "this was displayed", so it is recorded only once the banner actually renders.
  watch(
    visible,
    (v) => {
      if (v && !elapsed.value) legal.recordNoticeShown();
    },
    { immediate: true }
  );

  async function accept() {
    if (!consentTicked.value || accepting.value) return;
    accepting.value = true;
    try {
      await legal.acceptCgu();
    } catch {
      // A conflict means the version changed under us; the next status fetch shows the new one.
    } finally {
      accepting.value = false;
    }
  }

  async function hideForever() {
    try {
      await legal.dismissCgu();
    } catch {
      legal.remindLater();
    }
  }
</script>

<template>
  <div
    v-if="visible"
    class="legal-banner border-l-4 border-primary-500 bg-white dark:bg-gray-900 border-y border-r border-gray-200 dark:border-gray-700 rounded-r-lg shadow-sm"
  >
    <!-- After the user's own notice period: one quiet line, permanently hideable. -->
    <div v-if="elapsed" class="flex items-center gap-2 px-4 py-2 text-sm text-gray-600 dark:text-gray-400">
      <Icon name="material-symbols:balance-rounded" class="text-primary-500 shrink-0" />
      <span class="flex-grow">
        Our terms were updated.
        <NuxtLink to="/terms" class="underline hover:text-primary-600 dark:hover:text-primary-400">Review them</NuxtLink>
      </span>
      <button
        aria-label="Hide permanently"
        class="text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 cursor-pointer text-lg leading-none px-1"
        @click="hideForever"
      >
        &times;
      </button>
    </div>

    <template v-else>
      <div class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
        <Icon name="material-symbols:balance-rounded" class="text-primary-500 shrink-0" />
        <span class="flex-grow text-sm text-gray-700 dark:text-gray-300 min-w-48">
          We've updated our terms.
          <template v-if="effectiveDateLabel">
            They apply to your account from <span class="font-medium">{{ effectiveDateLabel }}</span
            >; nothing changes for you before then.</template
          >
        </span>
        <div class="flex items-center gap-2">
          <Button :label="expanded ? 'Hide details' : 'Review changes'" size="small" outlined @click="expanded = !expanded" />
          <Button label="Later" size="small" text severity="secondary" @click="legal.remindLater()" />
        </div>
      </div>

      <div v-if="expanded" class="px-4 pb-4 pt-1 border-t border-gray-100 dark:border-gray-800 text-sm text-gray-700 dark:text-gray-300">
        <h3 class="font-semibold text-base text-gray-900 dark:text-white mt-2">We have updated our terms</h3>
        <p class="mt-1.5">
          Jiten now has a paid option, Jiten+, so we’ve updated the terms to reflect it. We also corrected a few outdated provisions. Most of the changes do not
          affect ordinary use of Jiten; the changes that are more restrictive are called out explicitly below. <br />
          Here is the short version:
        </p>

        <h4 class="font-semibold mt-3">New promises, in writing</h4>
        <ul class="list-disc ml-5 mt-1 space-y-0.5">
          <li>
            Every feature that is free today stays free. This used to be a statement in the homepage but now is a commitment written in the terms (with narrow
            exceptions for legal or third-party changes, and it covers features, not storage quantities).
          </li>
          <li>If you ever subscribe and then stop, nothing you uploaded gets deleted.</li>
          <li>Exporting your own data is free, and always will be, on any plan.</li>
        </ul>

        <h4 class="font-semibold mt-3">Things we corrected</h4>
        <ul class="list-disc ml-5 mt-1 space-y-0.5">
          <li>
            The old terms said you could not use Jiten's content for anything commercial which contradicted our own licence. Decks, vocabulary and frequency
            lists have always been CC BY-SA, and commercial use has always been allowed. The terms now say so properly. What is not allowed is reselling access
            to Jiten itself.
          </li>
          <li>The old terms described Jiten as a non-commercial project. That stopped being accurate when Jiten+ arrived.</li>
        </ul>

        <h4 class="font-semibold mt-3">Things the law requires us to add</h4>
        <ul class="list-disc ml-5 mt-1 space-y-0.5">
          <li>Sale terms for Jiten+: renewal, cancellation, refunds and your right to withdraw.</li>
          <li>A free consumer mediator you can turn to if we cannot resolve a complaint between us.</li>
          <li>A legal notice page saying who actually runs Jiten.</li>
        </ul>

        <h4 class="font-semibold mt-3">Things that are more restrictive, so you know</h4>
        <ul class="list-disc ml-5 mt-1 space-y-0.5">
          <li>Our liability is capped at what you have paid us, or 50 EUR, whichever is higher.</li>
          <li>Clearer rules on bulk scraping and on reselling API access. Ordinary use is unaffected.</li>
        </ul>

        <p class="mt-3">
          We have also updated the privacy policy to describe how payments work and what Stripe receives. You don’t need to accept the updated privacy policy
          separately; it’s provided for your information.
        </p>

        <p class="mt-3">
          <NuxtLink to="/terms" class="underline hover:text-primary-600 dark:hover:text-primary-400">Read the full Terms of Use</NuxtLink>
          <span class="mx-2 text-gray-400">·</span>
          <NuxtLink to="/cgv" class="underline hover:text-primary-600 dark:hover:text-primary-400">Read the Terms of Sale</NuxtLink>
          <span class="mx-2 text-gray-400">·</span>
          <NuxtLink to="/terms-fr" class="underline hover:text-primary-600 dark:hover:text-primary-400">Version française</NuxtLink>
        </p>

        <div class="mt-4 flex items-start gap-2">
          <Checkbox v-model="consentTicked" binary input-id="legal-banner-consent" />
          <label for="legal-banner-consent" class="cursor-pointer select-none">I have read and accept the updated Terms of Use</label>
        </div>
        <div class="mt-3 flex flex-wrap gap-2">
          <Button label="Accept" size="small" :disabled="!consentTicked" :loading="accepting" @click="accept" />
          <Button label="Remind me later" size="small" text severity="secondary" @click="legal.remindLater()" />
        </div>
      </div>
    </template>
  </div>
</template>
