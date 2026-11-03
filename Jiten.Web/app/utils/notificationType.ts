import { NotificationType } from '~/types/enums';

const NOTIFICATION_STYLE: Partial<Record<NotificationType, { icon: string; colour: string }>> = {
  [NotificationType.RequestCompleted]: { icon: 'pi pi-check-circle', colour: 'text-green-500' },
  [NotificationType.RequestStatusChanged]: { icon: 'pi pi-info-circle', colour: 'text-blue-500' },
  [NotificationType.RequestFileUploaded]: { icon: 'pi pi-upload', colour: 'text-orange-500' },
  [NotificationType.SiteUpdate]: { icon: 'pi pi-megaphone', colour: 'text-purple-500' },
  [NotificationType.CommunityListMilestone]: { icon: 'pi pi-heart', colour: 'text-pink-500' },
  [NotificationType.MetadataEditApproved]: { icon: 'pi pi-check', colour: 'text-green-500' },
  [NotificationType.MetadataEditRejected]: { icon: 'pi pi-times-circle', colour: 'text-muted-color' },
  [NotificationType.MetadataEditReverted]: { icon: 'pi pi-undo', colour: 'text-red-500' },
};

export function getNotificationIcon(type: NotificationType): string {
  return NOTIFICATION_STYLE[type]?.icon ?? 'pi pi-bell';
}

export function getNotificationIconClass(type: NotificationType): string {
  return NOTIFICATION_STYLE[type]?.colour ?? 'text-muted-color';
}
