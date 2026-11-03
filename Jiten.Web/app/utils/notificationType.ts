import { NotificationGroup, NotificationType } from '~/types/enums';

const NOTIFICATION_STYLE: Partial<Record<NotificationType, { icon: string; colour: string }>> = {
  [NotificationType.RequestCompleted]: { icon: 'pi pi-check-circle', colour: 'text-green-500' },
  [NotificationType.RequestStatusChanged]: { icon: 'pi pi-info-circle', colour: 'text-blue-500' },
  [NotificationType.RequestFileUploaded]: { icon: 'pi pi-upload', colour: 'text-orange-500' },
  [NotificationType.RequestAdminComment]: { icon: 'pi pi-comment', colour: 'text-primary' },
  [NotificationType.General]: { icon: 'pi pi-envelope', colour: 'text-primary' },
  [NotificationType.SiteUpdate]: { icon: 'pi pi-megaphone', colour: 'text-purple-500' },
  [NotificationType.CommunityListMilestone]: { icon: 'pi pi-heart', colour: 'text-pink-500' },
  [NotificationType.MetadataEditApproved]: { icon: 'pi pi-check', colour: 'text-green-500' },
  [NotificationType.MetadataEditRejected]: { icon: 'pi pi-times-circle', colour: 'text-muted-color' },
  [NotificationType.MetadataEditReverted]: { icon: 'pi pi-undo', colour: 'text-red-500' },
};

/** Mirrors NotificationGroups.FromJitenTypes in Jiten.Core/Data/Notification.cs. */
const FROM_JITEN_TYPES: ReadonlySet<NotificationType> = new Set([
  NotificationType.General,
  NotificationType.SiteUpdate,
  NotificationType.RequestAdminComment,
]);

export const MUTABLE_NOTIFICATION_GROUPS: Record<Exclude<NotificationGroup, NotificationGroup.FromJiten>, { label: string; description: string }> = {
  [NotificationGroup.MediaRequests]: {
    label: 'Media requests',
    description: 'Status changes, uploads and completions on requests you made or follow.',
  },
  [NotificationGroup.MetadataEdits]: {
    label: 'Metadata edits',
    description: 'When your edits to deck details are approved, rejected or reverted.',
  },
  [NotificationGroup.CommunityLists]: {
    label: 'Community lists',
    description: 'Favourite milestones on lists you made.',
  },
};

export function getNotificationIcon(type: NotificationType): string {
  return NOTIFICATION_STYLE[type]?.icon ?? 'pi pi-bell';
}

export function getNotificationIconClass(type: NotificationType): string {
  return NOTIFICATION_STYLE[type]?.colour ?? 'text-muted-color';
}

export function isFromJiten(type: NotificationType): boolean {
  return FROM_JITEN_TYPES.has(type);
}
