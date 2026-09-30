<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Community login' });
  useSeoMeta({ robots: 'noindex' });

  const route = useRoute();
  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const communityUrl = useRuntimeConfig().public.communityUrl as string;

  type State = 'working' | 'needs-name' | 'redirecting' | 'error';
  const state = ref<State>('working');
  const errorMessage = ref('');

  const sso = typeof route.query.sso === 'string' ? route.query.sso : '';
  const sig = typeof route.query.sig === 'string' ? route.query.sig : '';

  const suggestedName = computed(() => {
    const userName: string = auth.user?.userName ?? '';
    return /^[A-Za-z0-9_](?:[A-Za-z0-9]|[._-](?![._-])){0,18}[A-Za-z0-9]$/.test(userName) ? userName : '';
  });

  const signIn = async () => {
    state.value = 'working';
    try {
      const { returnUrl } = await $api<{ returnUrl: string }>('community/sso', { method: 'POST', body: { sso, sig } });
      state.value = 'redirecting';
      window.location.replace(returnUrl);
    } catch (err) {
      const e = err as { status?: number; data?: { code?: string; message?: string } };
      if (e?.status === 409 && e.data?.code === 'display_name_required') {
        state.value = 'needs-name';
        return;
      }
      errorMessage.value = e?.data?.message || 'Something went wrong while logging you in to the Jiten Community. Try again from the Jiten Community.';
      state.value = 'error';
    }
  };

  onMounted(() => {
    if (!sso || !sig) {
      errorMessage.value = 'This page only works when you log in from the Jiten Community.';
      state.value = 'error';
      return;
    }
    signIn();
  });
</script>

<template>
  <div class="container mx-auto px-4 py-10 md:py-16 flex justify-center">
    <Card class="w-full max-w-md">
      <template #title>
        <div class="flex flex-col gap-1">
          <span v-if="state === 'needs-name'" class="text-sm font-normal text-muted-color">Joining Jiten Community</span>
          <h1 class="text-2xl font-semibold">{{ state === 'needs-name' ? 'Choose a display name' : 'Jiten Community' }}</h1>
        </div>
      </template>
      <template #content>
        <div v-if="state === 'working' || state === 'redirecting'" class="flex items-center gap-3" role="status">
          <ProgressSpinner style="width: 1.5rem; height: 1.5rem; margin: 0" stroke-width="6" class="shrink-0" aria-hidden="true" />
          <span>{{ state === 'redirecting' ? 'Taking you to the Jiten Community...' : 'Logging you in to the Jiten Community...' }}</span>
        </div>

        <div v-else-if="state === 'needs-name'" class="flex flex-col gap-6">
          <p class="text-gray-600 dark:text-gray-300 leading-relaxed">
            Your public name on the Jiten Community (forums) and anywhere else Jiten shows your name. Your login username stays private.
          </p>
          <DisplayNameForm
            :initial="suggestedName"
            submit-label="Continue to Jiten Community"
            submit-icon="pi pi-arrow-right"
            submit-icon-pos="right"
            stacked
            @saved="signIn"
          />
          <p class="text-sm text-muted-color border-t border-surface-200 dark:border-surface-700 pt-4">
            You can change it later in your account settings.
          </p>
        </div>

        <div v-else class="flex flex-col gap-4">
          <Message severity="error" :closable="false">{{ errorMessage }}</Message>
          <a :href="communityUrl" class="text-primary-600 dark:text-primary-400 hover:underline">Back to the Jiten Community</a>
        </div>
      </template>
    </Card>
  </div>
</template>
