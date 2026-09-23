export const MEDIA_STATUS_OPTIONS = [
    {label: 'Planning', value: 'planning'},
    {label: 'Ongoing', value: 'ongoing'},
    {label: 'Completed', value: 'completed'},
    {label: 'Dropped', value: 'dropped'},
    {label: 'Without status', value: 'nostatus'},
    {label: 'Ignored', value: 'ignore'},
] as const;

export type MediaStatusToken = (typeof MEDIA_STATUS_OPTIONS)[number]['value'];

const TOKEN_ORDER: readonly string[] = MEDIA_STATUS_OPTIONS.map((option) => option.value);

export const mediaStatusLabel = (token: string): string => MEDIA_STATUS_OPTIONS.find((option) => option.value === token)?.label ?? 'Status';

export const normaliseStatusTokens = (tokens: readonly string[]): MediaStatusToken[] =>
    TOKEN_ORDER.filter((token) => tokens.includes(token)) as MediaStatusToken[];

export const parseStatusFilter = (raw: string | null | undefined): MediaStatusToken[] =>
    normaliseStatusTokens(
        (raw ?? '')
            .split(',')
            .map((token) => token.trim().toLowerCase())
            .filter(Boolean)
    );

export const serialiseStatusFilter = (tokens: readonly string[]): string | null => {
    const normalised = normaliseStatusTokens(tokens);
    return normalised.length > 0 ? normalised.join(',') : null;
};

export const statusFilterHasFav = (raw: string | null | undefined): boolean =>
    (raw ?? '')
        .split(',')
        .some((token) => token.trim().toLowerCase() === 'fav');
