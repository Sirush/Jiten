<script setup lang="ts">
import { NotificationType } from '~/types';
import type { NotificationDto } from '~/types/types';

const { unreadCount, notifications, fetchNotifications, fetchUnreadCount, markAsRead, markAllAsRead } = useNotifications();
const router = useRouter();
const popover = ref();

const recentNotifications = ref<NotificationDto[]>([]);
const isLoadingRecent = ref(false);
const expandedIds = ref<Set<number>>(new Set());

async function togglePopover(event: Event) {
  popover.value.toggle(event);
  isLoadingRecent.value = true;
  expandedIds.value = new Set();
  await fetchNotifications({ limit: 10 });
  recentNotifications.value = notifications.value;
  isLoadingRecent.value = false;
}

// Clicking a notification expands its full message in place (long messages are otherwise clamped and
// unreadable in the dropdown). Following the link is a separate affordance shown while expanded.
async function toggleNotification(notification: NotificationDto) {
  const next = new Set(expandedIds.value);
  if (next.has(notification.id)) next.delete(notification.id);
  else next.add(notification.id);
  expandedIds.value = next;

  if (!notification.isRead) {
    await markAsRead(notification.id);
    const item = recentNotifications.value.find(n => n.id === notification.id);
    if (item) item.isRead = true;
  }
}

function openNotificationLink(notification: NotificationDto) {
  popover.value.hide();
  if (notification.linkUrl) {
    router.push(notification.linkUrl);
  }
}

async function handleMarkAllRead() {
  await markAllAsRead();
  recentNotifications.value.forEach(n => {
    n.isRead = true;
    n.readAt = new Date().toISOString();
  });
}

function getNotificationIcon(type: NotificationType): string {
  switch (type) {
    case NotificationType.RequestCompleted: return 'pi pi-check-circle';
    case NotificationType.RequestStatusChanged: return 'pi pi-info-circle';
    case NotificationType.RequestFileUploaded: return 'pi pi-upload';
    case NotificationType.SiteUpdate: return 'pi pi-megaphone';
    default: return 'pi pi-bell';
  }
}

function getNotificationIconClass(type: NotificationType): string {
  switch (type) {
    case NotificationType.RequestCompleted: return 'text-green-500';
    case NotificationType.RequestStatusChanged: return 'text-blue-500';
    case NotificationType.RequestFileUploaded: return 'text-orange-500';
    case NotificationType.SiteUpdate: return 'text-purple-500';
    default: return 'text-muted-color';
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

</script>

<template>
  <div class="relative inline-flex">
    <Button
      severity="secondary"
      :class="{ 'opacity-40': unreadCount === 0 }"
      @click="togglePopover"
      aria-label="Notifications"
    >
      <i class="pi pi-bell" />
    </Button>
    <Badge
      v-if="unreadCount > 0"
      :value="unreadCount > 99 ? '99+' : unreadCount"
      severity="danger"
      class="absolute -top-0.5 -right-0.5 pointer-events-none scale-75 origin-top-right"
    />

    <Popover ref="popover" :pt="{ root: { class: 'w-[90vw] max-w-sm' }, content: { class: 'p-0' } }">
      <div class="flex items-center justify-between px-4 py-3 border-b border-surface-200 dark:border-surface-700">
        <span class="font-semibold">Notifications</span>
        <a
          v-if="unreadCount > 0"
          href="#"
          class="text-sm text-primary hover:underline"
          @click.prevent="handleMarkAllRead"
        >
          Mark all as read
        </a>
      </div>

      <div v-if="isLoadingRecent" class="flex justify-center py-6">
        <ProgressSpinner style="width: 30px; height: 30px" />
      </div>

      <div v-else-if="recentNotifications.length === 0" class="text-center py-6 text-muted-color text-sm">
        No notifications yet
      </div>

      <div v-else class="max-h-80 overflow-y-auto">
        <div
          v-for="notification in recentNotifications"
          :key="notification.id"
          role="button"
          tabindex="0"
          class="flex items-start gap-3 px-4 py-3 cursor-pointer hover:bg-surface-50 dark:hover:bg-surface-800 transition-colors border-b border-surface-100 dark:border-surface-700 last:border-b-0"
          :class="{ 'bg-primary-50 dark:bg-primary-950/20': !notification.isRead }"
          :aria-expanded="expandedIds.has(notification.id)"
          @click="toggleNotification(notification)"
          @keydown.enter.prevent="toggleNotification(notification)"
          @keydown.space.prevent="toggleNotification(notification)"
        >
          <i :class="[getNotificationIcon(notification.type), getNotificationIconClass(notification.type), 'mt-0.5']" />
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2">
              <span class="text-sm font-medium truncate">{{ notification.title }}</span>
              <span
                v-if="!notification.isRead"
                class="shrink-0 w-2 h-2 rounded-full bg-primary"
              />
              <i
                class="pi ml-auto shrink-0 text-xs text-muted-color transition-transform"
                :class="expandedIds.has(notification.id) ? 'pi-chevron-up' : 'pi-chevron-down'"
              />
            </div>
            <!-- eslint-disable-next-line vue/no-v-html -->
            <p
              class="text-xs text-muted-color mt-0.5 break-words"
              :class="{ 'line-clamp-2': !expandedIds.has(notification.id) }"
              v-html="parseCustomMeaningHtml(notification.message)"
            />
            <div class="flex items-center gap-3 mt-1">
              <span class="text-xs text-muted-color">{{ formatTimeAgo(notification.createdAt) }}</span>
              <button
                v-if="notification.linkUrl && expandedIds.has(notification.id)"
                type="button"
                class="text-xs text-primary hover:underline"
                @click.stop="openNotificationLink(notification)"
              >
                Open <i class="pi pi-arrow-right text-[0.6rem]" />
              </button>
            </div>
          </div>
        </div>
      </div>

      <div class="px-4 py-2 border-t border-surface-200 dark:border-surface-700 text-center">
        <NuxtLink
          to="/notifications"
          class="text-sm text-primary hover:underline"
          @click="popover.hide()"
        >
          View all notifications
        </NuxtLink>
      </div>
    </Popover>
  </div>
</template>
