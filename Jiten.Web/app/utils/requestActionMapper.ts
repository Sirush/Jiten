import { RequestAction } from '~/types';

export function getRequestActionText(action: RequestAction): string {
  switch (action) {
    case RequestAction.RequestCreated:
      return 'Request created';
    case RequestAction.RequestDeleted:
      return 'Request deleted';
    case RequestAction.Upvoted:
      return 'Upvoted';
    case RequestAction.UpvoteRemoved:
      return 'Upvote removed';
    case RequestAction.Subscribed:
      return 'Subscribed';
    case RequestAction.Unsubscribed:
      return 'Unsubscribed';
    case RequestAction.CommentAdded:
      return 'Comment added';
    case RequestAction.CommentEdited:
      return 'Comment edited';
    case RequestAction.FileUploaded:
      return 'File uploaded';
    case RequestAction.FileDeletedByAdmin:
      return 'File deleted by admin';
    case RequestAction.StatusChangedToInProgress:
      return 'Marked in progress';
    case RequestAction.StatusChangedToCompleted:
      return 'Completed';
    case RequestAction.StatusChangedToRejected:
      return 'Rejected';
    case RequestAction.StatusChangedToOpen:
      return 'Reopened';
    case RequestAction.RequestEditedByAdmin:
      return 'Edited by admin';
    case RequestAction.RequestEditedByRequester:
      return 'Edited by requester';
    case RequestAction.ContributionValidated:
      return 'Contribution validated';
    case RequestAction.ContributionRevoked:
      return 'Contribution revoked';
    default:
      return 'Unknown action';
  }
}

export function getRequestActionIcon(action: RequestAction): string {
  switch (action) {
    case RequestAction.RequestCreated:
      return 'pi pi-plus';
    case RequestAction.RequestDeleted:
      return 'pi pi-trash';
    case RequestAction.Upvoted:
      return 'pi pi-thumbs-up';
    case RequestAction.UpvoteRemoved:
      return 'pi pi-thumbs-down';
    case RequestAction.Subscribed:
      return 'pi pi-bell';
    case RequestAction.Unsubscribed:
      return 'pi pi-bell-slash';
    case RequestAction.CommentAdded:
      return 'pi pi-comment';
    case RequestAction.CommentEdited:
      return 'pi pi-pencil';
    case RequestAction.FileUploaded:
      return 'pi pi-upload';
    case RequestAction.FileDeletedByAdmin:
      return 'pi pi-trash';
    case RequestAction.StatusChangedToInProgress:
      return 'pi pi-spinner';
    case RequestAction.StatusChangedToCompleted:
      return 'pi pi-check-circle';
    case RequestAction.StatusChangedToRejected:
      return 'pi pi-times-circle';
    case RequestAction.StatusChangedToOpen:
      return 'pi pi-replay';
    case RequestAction.RequestEditedByAdmin:
      return 'pi pi-pencil';
    case RequestAction.RequestEditedByRequester:
      return 'pi pi-pencil';
    case RequestAction.ContributionValidated:
      return 'pi pi-verified';
    case RequestAction.ContributionRevoked:
      return 'pi pi-ban';
    default:
      return 'pi pi-question-circle';
  }
}

export function getRequestActionSeverity(action: RequestAction): 'info' | 'warn' | 'success' | 'danger' | 'secondary' {
  switch (action) {
    case RequestAction.RequestCreated:
      return 'info';
    case RequestAction.RequestDeleted:
      return 'danger';
    case RequestAction.Upvoted:
      return 'success';
    case RequestAction.UpvoteRemoved:
      return 'secondary';
    case RequestAction.Subscribed:
      return 'info';
    case RequestAction.Unsubscribed:
      return 'secondary';
    case RequestAction.CommentAdded:
      return 'info';
    case RequestAction.CommentEdited:
      return 'info';
    case RequestAction.FileUploaded:
      return 'info';
    case RequestAction.FileDeletedByAdmin:
      return 'danger';
    case RequestAction.StatusChangedToInProgress:
      return 'warn';
    case RequestAction.StatusChangedToCompleted:
      return 'success';
    case RequestAction.StatusChangedToRejected:
      return 'danger';
    case RequestAction.StatusChangedToOpen:
      return 'info';
    case RequestAction.RequestEditedByAdmin:
      return 'warn';
    case RequestAction.RequestEditedByRequester:
      return 'info';
    case RequestAction.ContributionValidated:
      return 'success';
    case RequestAction.ContributionRevoked:
      return 'danger';
    default:
      return 'secondary';
  }
}
