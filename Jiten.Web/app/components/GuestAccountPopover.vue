<script setup lang="ts">
  import Popover from 'primevue/popover';
  import Button from 'primevue/button';

  const props = defineProps<{
    message: string;
    /** Credited on signup_completed when the visitor registers after clicking through. */
    prompt: string;
    redirect?: string;
  }>();

  const { authLink } = useGuestPrompt();
  const popover = ref<InstanceType<typeof Popover> | null>(null);

  function show(event: Event, target?: HTMLElement) {
    popover.value?.show(event, target);
  }

  function onCreate() {
    markGuestPrompt(props.prompt);
    popover.value?.hide();
  }

  defineExpose({ show, hide: () => popover.value?.hide() });
</script>

<template>
  <Popover ref="popover">
    <div class="flex flex-col gap-3 max-w-72 p-1">
      <p class="text-sm text-gray-700 dark:text-gray-300">{{ message }}</p>
      <div class="flex items-center gap-3">
        <Button as="router-link" :to="authLink('/register', redirect)" label="Create an account" size="small" @click="onCreate" />
        <NuxtLink :to="authLink('/login', redirect)" class="text-sm font-medium text-primary-700 dark:text-primary-300 hover:underline" @click="popover?.hide()"
          >Log in</NuxtLink
        >
      </div>
    </div>
  </Popover>
</template>
