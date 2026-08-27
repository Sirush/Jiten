import { RequestStatus } from '~/types';

export function getRequestStatusText(status: RequestStatus): string {
  switch (status) {
    case RequestStatus.Open:
      return 'Open';
    case RequestStatus.InProgress:
      return 'In Progress';
    case RequestStatus.Completed:
      return 'Completed';
    case RequestStatus.Rejected:
      return 'Rejected';
    default:
      return 'Unknown';
  }
}

export function getRequestStatusSeverity(status: RequestStatus): 'info' | 'warn' | 'success' | 'danger' | 'secondary' {
  switch (status) {
    case RequestStatus.Open:
      return 'info';
    case RequestStatus.InProgress:
      return 'warn';
    case RequestStatus.Completed:
      return 'success';
    case RequestStatus.Rejected:
      return 'danger';
    default:
      return 'secondary';
  }
}
