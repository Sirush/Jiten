export function franchiseChipClass(active: boolean): string[] {
  return [
    'relative flex min-h-11 max-w-full items-center gap-1.5 rounded-md border px-3 text-left text-sm font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 sm:pointer-fine:min-h-9',
    active
      ? 'border-primary-600 bg-primary-50 dark:border-primary-400 dark:bg-primary-950'
      : 'border-surface-300 hover:bg-surface-100 dark:border-surface-700 dark:hover:bg-surface-800',
  ];
}
