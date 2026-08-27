<script setup lang="ts">
  import { NotificationType } from '~/types';
  import type { NotificationDto } from '~/types/types';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Notifications - Jiten' });

  const { notifications, totalCount, isLoading, fetchNotifications, markAsRead, markAllAsRead, deleteNotification, unreadCount, fetchUnreadCount } =
    useNotifications();
  const router = useRouter();
  const toast = useToast();

  const unreadOnly = ref(false);
  const offset = ref(0);
  const limit = 20;

  const filterOptions = [
    { label: 'All', value: false },
    { label: 'Unread', value: true },
  ];

  async function loadNotifications() {
    await fetchNotifications({
      unreadOnly: unreadOnly.value,
      offset: offset.value,
      limit,
    });
  }

  watch([unreadOnly], () => {
    offset.value = 0;
    loadNotifications();
  });

  watch(offset, () => loadNotifications());

  async function handleClick(notification: NotificationDto) {
    if (!notification.isRead) {
      await markAsRead(notification.id);
    }
    if (notification.linkUrl?.startsWith('/')) {
      router.push(notification.linkUrl);
    }
  }

  async function handleDelete(notification: NotificationDto) {
    await deleteNotification(notification.id);
  }

  async function handleMarkAllRead() {
    await markAllAsRead();
    toast.add({ severity: 'success', summary: 'All notifications marked as read', life: 2000, group: 'bottom' });
  }

  function getNotificationIcon(type: NotificationType): string {
    switch (type) {
      case NotificationType.RequestCompleted:
        return 'pi pi-check-circle';
      case NotificationType.RequestStatusChanged:
        return 'pi pi-info-circle';
      case NotificationType.RequestFileUploaded:
        return 'pi pi-upload';
      case NotificationType.SiteUpdate:
        return 'pi pi-megaphone';
      default:
        return 'pi pi-bell';
    }
  }

  function getNotificationIconClass(type: NotificationType): string {
    switch (type) {
      case NotificationType.RequestCompleted:
        return 'text-green-500';
      case NotificationType.RequestStatusChanged:
        return 'text-blue-500';
      case NotificationType.RequestFileUploaded:
        return 'text-orange-500';
      case NotificationType.SiteUpdate:
        return 'text-purple-500';
      default:
        return 'text-muted-color';
    }
  }

  function formatTimeAgo(dateString: string): string {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    if (diffDays < 30) return `${diffDays}d ago`;
    const diffMonths = Math.floor(diffDays / 30);
    return `${diffMonths}mo ago`;
  }

  function onPageChange(event: { first: number }) {
    offset.value = event.first;
  }

  onMounted(() => {
    loadNotifications();
    fetchUnreadCount();
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex items-center justify-between mb-6">
      <h1 class="text-2xl font-bold">Notifications</h1>
      <Button v-if="unreadCount > 0" label="Mark all as read" icon="pi pi-check" severity="secondary" size="small" @click="handleMarkAllRead" />
    </div>

    <div class="flex gap-3 mb-4">
      <SelectButton v-model="unreadOnly" :options="filterOptions" option-label="label" option-value="value" />
    </div>

    <div v-if="isLoading" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <div v-else-if="notifications.length === 0" class="text-center py-12 text-muted-color">
      <i class="pi pi-bell-slash text-4xl mb-3" />
      <p v-if="unreadOnly">No unread notifications.</p>
      <p v-else>No notifications yet.</p>
    </div>

    <div v-else class="flex flex-col gap-2">
      <Card
        v-for="notification in notifications"
        :key="notification.id"
        class="shadow-sm cursor-pointer hover:bg-surface-50 dark:hover:bg-surface-800 transition-colors"
        :class="{ '!bg-primary-50 dark:!bg-primary-950/20': !notification.isRead }"
        @click="handleClick(notification)"
      >
        <template #content>
          <div class="flex items-start gap-3">
            <i :class="[getNotificationIcon(notification.type), getNotificationIconClass(notification.type), 'text-lg mt-0.5']" />
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2">
                <span class="font-semibold">{{ notification.title }}</span>
                <span v-if="!notification.isRead" class="shrink-0 w-2 h-2 rounded-full bg-primary" />
              </div>
              <!-- eslint-disable-next-line vue/no-v-html -->
              <p class="text-sm text-muted-color mt-1 break-words" v-html="parseCustomMeaningHtml(notification.message)" />
              <span class="text-xs text-muted-color mt-1">{{ formatTimeAgo(notification.createdAt) }}</span>
            </div>
            <Button v-tooltip.top="'Delete'" icon="pi pi-trash" severity="danger" text rounded size="small" @click.stop="handleDelete(notification)" />
          </div>
        </template>
      </Card>
    </div>

    <Paginator v-if="totalCount > limit" :rows="limit" :total-records="totalCount" :first="offset" class="mt-4" @page="onPageChange" />
  </div>
</template>
