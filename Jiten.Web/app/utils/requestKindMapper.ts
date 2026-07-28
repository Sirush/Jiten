import { RequestKind } from '~/types';

export function getRequestKindText(kind: RequestKind): string {
  switch (kind) {
    case RequestKind.New: return 'New';
    case RequestKind.Update: return 'Update';
    default: return 'Unknown';
  }
}

export function getRequestKindIcon(kind: RequestKind): string {
  switch (kind) {
    case RequestKind.New: return 'pi pi-plus';
    case RequestKind.Update: return 'pi pi-refresh';
    default: return 'pi pi-question';
  }
}
