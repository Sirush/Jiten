<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Textarea from 'primevue/textarea';
  import ToggleSwitch from 'primevue/toggleswitch';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  useHead({ title: 'Send Notification - Jiten' });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const sendToEveryone = ref(false);
  const searchQuery = ref('');
  const searchResults = ref<{ userId: string; userName: string; email: string }[]>([]);
  const selectedUser = ref<{ userId: string; userName: string; email: string } | null>(null);
  const searching = ref(false);
  const title = ref('');
  const message = ref('');
  const linkUrl = ref('');
  const sending = ref(false);

  const isValid = computed(() => {
    if (!title.value.trim() || !message.value.trim()) return false;
    if (!sendToEveryone.value && !selectedUser.value) return false;
    return true;
  });

  async function searchUsers() {
    if (searchQuery.value.trim().length < 2) return;
    try {
      searching.value = true;
      searchResults.value = await $api<{ userId: string; userName: string; email: string }[]>(
        `/admin/search-users?query=${encodeURIComponent(searchQuery.value.trim())}`,
      );
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to search users'), life: 5000 });
    } finally {
      searching.value = false;
    }
  }

  function selectUser(user: { userId: string; userName: string; email: string }) {
    selectedUser.value = user;
    searchResults.value = [];
    searchQuery.value = '';
  }

  function clearUser() {
    selectedUser.value = null;
  }

  function resetForm() {
    title.value = '';
    message.value = '';
    linkUrl.value = '';
    selectedUser.value = null;
    searchQuery.value = '';
    searchResults.value = [];
  }

  async function doSend() {
    try {
      sending.value = true;
      const result = await $api<{ message: string; count: number }>('/admin/send-notification', {
        method: 'POST',
        body: {
          sendToEveryone: sendToEveryone.value,
          userId: sendToEveryone.value ? null : selectedUser.value?.userId,
          title: title.value.trim(),
          message: message.value.trim(),
          linkUrl: linkUrl.value.trim() || null,
        },
      });
      toast.add({
        severity: 'success',
        summary: 'Sent',
        detail: result.message,
        life: 5000,
      });
      resetForm();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to send notification'), life: 5000 });
    } finally {
      sending.value = false;
    }
  }

  function send() {
    if (sendToEveryone.value) {
      confirm.require({
        message: 'This will send a notification to every user. Are you sure?',
        header: 'Confirm',
        icon: 'pi pi-exclamation-triangle',
        acceptClass: 'p-button-danger',
        accept: doSend,
      });
    } else {
      doSend();
    }
  }
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Send Notification</h1>
    </div>

    <Card class="shadow-md max-w-xl">
      <template #content>
        <div class="flex flex-col gap-4">
          <div class="flex items-center gap-3">
            <ToggleSwitch v-model="sendToEveryone" />
            <label class="text-sm font-medium">{{ sendToEveryone ? 'Send to everyone' : 'Send to specific user' }}</label>
          </div>

          <div v-if="!sendToEveryone">
            <label class="block text-sm font-medium mb-1">User</label>
            <div v-if="selectedUser" class="flex items-center gap-2 p-2 bg-surface-100 dark:bg-surface-800 rounded">
              <span class="font-medium">{{ selectedUser.userName }}</span>
              <span class="text-sm text-surface-500 dark:text-surface-400">({{ selectedUser.email }})</span>
              <Button icon="pi pi-times" class="p-button-text p-button-sm p-button-danger ml-auto" @click="clearUser" />
            </div>
            <div v-else class="flex gap-2">
              <InputText
                v-model="searchQuery"
                placeholder="Search by username or email"
                class="flex-1"
                @keydown.enter="searchUsers"
              />
              <Button
                label="Search"
                icon="pi pi-search"
                :loading="searching"
                :disabled="searchQuery.trim().length < 2"
                @click="searchUsers"
              />
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

          <div>
            <label for="notifTitle" class="block text-sm font-medium mb-1">Title</label>
            <InputText id="notifTitle" v-model="title" class="w-full" placeholder="Notification title" />
          </div>

          <div>
            <label for="notifMessage" class="block text-sm font-medium mb-1">Message</label>
            <Textarea id="notifMessage" v-model="message" rows="4" class="w-full" placeholder="Notification message" />
          </div>

          <div>
            <label for="notifLink" class="block text-sm font-medium mb-1">Link URL (optional)</label>
            <InputText id="notifLink" v-model="linkUrl" class="w-full" placeholder="e.g. /requests/123" />
          </div>

          <div class="flex justify-end">
            <Button
              label="Send"
              icon="pi pi-send"
              class="p-button-primary"
              :loading="sending"
              :disabled="!isValid || sending"
              @click="send"
            />
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
