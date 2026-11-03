import { describe, expect, it } from 'vitest';
import { NotificationGroup, NotificationType } from '~/types/enums';
import { isFromJiten, MUTABLE_NOTIFICATION_GROUPS } from '~/utils/notificationType';

describe('notification groups', () => {
  it('pins only what Jiten sends on purpose', () => {
    const fromJiten = Object.values(NotificationType)
      .filter((v): v is NotificationType => typeof v === 'number')
      .filter(isFromJiten);
    expect(fromJiten.sort((a, b) => a - b)).toEqual([NotificationType.RequestAdminComment, NotificationType.General, NotificationType.SiteUpdate]);
  });

  it('offers every group except From Jiten as a mute toggle', () => {
    const groups = Object.keys(MUTABLE_NOTIFICATION_GROUPS).map(Number);
    expect(groups).toEqual([NotificationGroup.MediaRequests, NotificationGroup.MetadataEdits, NotificationGroup.CommunityLists]);
  });
});
