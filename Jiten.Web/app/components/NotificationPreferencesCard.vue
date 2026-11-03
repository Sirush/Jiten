<script setup lang="ts">
  import type { NotificationGroup } from '~/types/enums';
  import { MUTABLE_NOTIFICATION_GROUPS } from '~/utils/notificationType';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';

  interface GroupPreference {
    group: Exclude<NotificationGroup, NotificationGroup.FromJiten>;
    enabled: boolean;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();

  const route = useRoute();
  const preferences = ref<GroupPreference[] | null>(null);
  const loadFailed = ref(false);
  const saving = ref<Set<number>>(new Set());
  const skeletonRows = Object.keys(MUTABLE_NOTIFICATION_GROUPS).length;

  /** The account page renders after its own fetch, too late for the router's hash scroll from the bell's Manage link. */
  function scrollIntoViewIfLinked() {
    if (route.hash === '#notifications') document.getElementById('notifications')?.scrollIntoView({ block: 'start' });
  }

  onMounted(async () => {
    scrollIntoViewIfLinked();
    try {
      preferences.value = await $api<GroupPreference[]>('user/settings/notifications');
    } catch {
      loadFailed.value = true;
    }
  });

  async function onToggle(preference: GroupPreference, enabled: boolean) {
    saving.value = new Set(saving.value).add(preference.group);
    try {
      preferences.value = await $api<GroupPreference[]>(`user/settings/notifications/${preference.group}`, {
        method: 'PUT',
        body: { enabled },
      });
    } catch (err) {
      preference.enabled = !enabled;
      toast.add({ severity: 'error', summary: 'Error', detail: apiErrorMessage(err, 'Failed to update notification settings.'), life: 4000 });
    } finally {
      const next = new Set(saving.value);
      next.delete(preference.group);
      saving.value = next;
    }
  }
</script>

<template>
  <Card id="notifications" class="scroll-mt-20">
    <template #title>
      <h3 class="text-lg font-semibold">Notifications</h3>
    </template>
    <template #content>
      <p class="text-gray-600 dark:text-gray-300 mb-4">Site updates and messages from Jiten always reach you.</p>
      <div class="flex flex-col gap-4">
        <template v-if="preferences">
          <div v-for="preference in preferences" :key="preference.group" class="flex items-center justify-between gap-4">
            <label :for="`notification-group-${preference.group}`" class="text-sm cursor-pointer">
              <span class="font-semibold">{{ MUTABLE_NOTIFICATION_GROUPS[preference.group].label }}</span>
              <span class="block text-gray-600 dark:text-gray-300">{{ MUTABLE_NOTIFICATION_GROUPS[preference.group].description }}</span>
            </label>
            <ToggleSwitch
              v-model="preference.enabled"
              :input-id="`notification-group-${preference.group}`"
              :disabled="saving.has(preference.group)"
              @update:model-value="onToggle(preference, $event)"
            />
          </div>
        </template>
        <p v-else-if="loadFailed" class="text-sm text-red-600 dark:text-red-400">Couldn't load your notification settings. Reload the page to try again.</p>
        <template v-else>
          <div v-for="i in skeletonRows" :key="i" class="flex items-center justify-between gap-4">
            <div class="flex-1">
              <Skeleton width="8rem" height="1rem" class="mb-1.5" />
              <Skeleton width="70%" height="1rem" />
            </div>
            <Skeleton width="2.5rem" height="1.5rem" border-radius="1rem" />
          </div>
        </template>
      </div>
    </template>
  </Card>
</template>
