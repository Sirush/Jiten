import type { NotificationDto, PaginatedResponse } from '~/types/types';

let sharedPollInterval: ReturnType<typeof setInterval> | null = null;
let pollRefCount = 0;
const sharedUnreadCount = ref(0);

export function useNotifications() {
  const { $api } = useNuxtApp();

  const notifications = ref<NotificationDto[]>([]);
  const totalCount = ref(0);
  const unreadCount = sharedUnreadCount;
  const isLoading = ref(false);
  const error = ref<Error | null>(null);

  const fetchNotifications = async (
    params: {
      unreadOnly?: boolean;
      offset?: number;
      limit?: number;
    } = {}
  ) => {
    isLoading.value = true;
    error.value = null;
    try {
      const result = await $api<PaginatedResponse<NotificationDto[]>>('notifications', {
        query: {
          unreadOnly: params.unreadOnly ?? false,
          offset: params.offset ?? 0,
          limit: params.limit ?? 20,
        },
      });
      notifications.value = result?.data ?? [];
      totalCount.value = result?.totalItems ?? 0;
    } catch (e) {
      error.value = e as Error;
      notifications.value = [];
      totalCount.value = 0;
    } finally {
      isLoading.value = false;
    }
  };

  const fetchUnreadCount = async () => {
    try {
      const result = await $api<{ count: number }>('notifications/unread-count');
      unreadCount.value = result?.count ?? 0;
    } catch {
      // Silently swallow — polling resilience
    }
  };

  const markAsRead = async (id: number): Promise<boolean> => {
    try {
      await $api(`notifications/${id}/read`, { method: 'POST' });
      const notification = notifications.value.find((n) => n.id === id);
      if (notification) {
        notification.isRead = true;
        notification.readAt = new Date().toISOString();
      }
      unreadCount.value = Math.max(0, unreadCount.value - 1);
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const markAllAsRead = async (): Promise<boolean> => {
    try {
      await $api('notifications/read-all', { method: 'POST' });
      notifications.value.forEach((n) => {
        n.isRead = true;
        n.readAt = new Date().toISOString();
      });
      unreadCount.value = 0;
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const deleteNotification = async (id: number): Promise<boolean> => {
    try {
      await $api(`notifications/${id}`, { method: 'DELETE' });
      const idx = notifications.value.findIndex((n) => n.id === id);
      if (idx !== -1) {
        const removed = notifications.value[idx];
        if (!removed.isRead) unreadCount.value = Math.max(0, unreadCount.value - 1);
        notifications.value.splice(idx, 1);
        totalCount.value = Math.max(0, totalCount.value - 1);
      }
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const startPolling = () => {
    if (import.meta.server) return;
    pollRefCount++;
    if (sharedPollInterval) return;
    fetchUnreadCount();
    sharedPollInterval = setInterval(fetchUnreadCount, 600_000);
  };

  const stopPolling = () => {
    pollRefCount = Math.max(0, pollRefCount - 1);
    if (pollRefCount === 0 && sharedPollInterval) {
      clearInterval(sharedPollInterval);
      sharedPollInterval = null;
    }
  };

  return {
    notifications,
    totalCount,
    unreadCount,
    isLoading,
    error,
    fetchNotifications,
    fetchUnreadCount,
    markAsRead,
    markAllAsRead,
    deleteNotification,
    startPolling,
    stopPolling,
  };
}
