const LINK_ICON = 'material-symbols:link';

/** Greasy Fork and Sleazy Fork path segments read `<id>-<slug>`, and the slug is the script or user name. */
const ID_SLUG = /^\d+-(.+)$/;

const slugName = (segments: string[]) => {
  const slug = segments.map((s) => ID_SLUG.exec(s)?.[1]).filter(Boolean).at(-1);
  return slug?.replace(/-/g, ' ');
};

const PLATFORMS: { hosts: string[]; name: string; icon?: string; account?: (segments: string[]) => string | undefined }[] = [
  { hosts: ['x.com', 'twitter.com'], name: 'X', icon: 'mdi:twitter' },
  { hosts: ['bsky.app'], name: 'Bluesky', icon: 'mdi:butterfly' },
  { hosts: ['youtube.com', 'youtu.be'], name: 'YouTube', icon: 'mdi:youtube' },
  { hosts: ['twitch.tv'], name: 'Twitch', icon: 'mdi:twitch' },
  { hosts: ['github.com'], name: 'GitHub', icon: 'mdi:github' },
  { hosts: ['discord.gg', 'discord.com'], name: 'Discord', icon: 'mdi:discord' },
  { hosts: ['reddit.com'], name: 'Reddit', icon: 'mdi:reddit' },
  { hosts: ['instagram.com'], name: 'Instagram', icon: 'mdi:instagram' },
  { hosts: ['tiktok.com'], name: 'TikTok' },
  { hosts: ['patreon.com'], name: 'Patreon', icon: 'mdi:patreon' },
  { hosts: ['ko-fi.com'], name: 'Ko-fi', icon: 'mdi:coffee' },
  { hosts: ['steamcommunity.com'], name: 'Steam', icon: 'mdi:steam' },
  { hosts: ['goodreads.com'], name: 'Goodreads', icon: 'mdi:goodreads' },
  { hosts: ['anilist.co'], name: 'AniList' },
  { hosts: ['myanimelist.net'], name: 'MyAnimeList' },
  { hosts: ['kitsu.app', 'kitsu.io'], name: 'Kitsu' },
  { hosts: ['vndb.org'], name: 'VNDB' },
  { hosts: ['bookmeter.com'], name: 'Bookmeter' },
  { hosts: ['backloggd.com'], name: 'Backloggd' },
  { hosts: ['letterboxd.com'], name: 'Letterboxd' },
  { hosts: ['note.com'], name: 'note' },
  { hosts: ['pixiv.net'], name: 'pixiv' },
  { hosts: ['nicovideo.jp'], name: 'niconico' },
  { hosts: ['bilibili.com'], name: 'bilibili' },
  { hosts: ['greasyfork.org'], name: 'Greasy Fork', icon: 'mdi:script-text-outline', account: slugName },
  { hosts: ['sleazyfork.org'], name: 'Sleazy Fork', icon: 'mdi:script-text-outline', account: slugName },
];

export interface ProfileLinkDisplay {
  icon: string;
  /** Null for sites outside the known platforms. */
  platform: string | null;
  /** The account part of a platform link, or host and last path segment for any other site; always derived from the URL, so a label can't disguise where it leads. */
  label: string;
}

function decode(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

export function describeProfileLink(url: string): ProfileLinkDisplay {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return { icon: LINK_ICON, platform: null, label: url };
  }

  const host = parsed.hostname.replace(/^www\./, '');
  const segments = parsed.pathname.split('/').filter(Boolean).map(decode);
  const platform = PLATFORMS.find((p) => p.hosts.some((h) => host === h || host.endsWith(`.${h}`)));
  if (!platform) {
    const path = segments.length > 1 ? `/…/${segments.at(-1)}` : segments.map((s) => `/${s}`).join('');
    return { icon: LINK_ICON, platform: null, label: `${host}${path}` };
  }

  const account = platform.account?.(segments) ?? segments.at(-1);
  return { icon: platform.icon ?? LINK_ICON, platform: platform.name, label: account ?? '' };
}
