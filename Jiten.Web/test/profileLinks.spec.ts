import { describe, expect, it } from 'vitest';
import { describeProfileLink } from '../app/utils/profileLinks';

describe('describeProfileLink', () => {
  it('names known platforms and keeps the account part', () => {
    expect(describeProfileLink('https://x.com/someone')).toEqual({ icon: 'mdi:twitter', platform: 'X', label: 'someone' });
    expect(describeProfileLink('https://www.youtube.com/@channel/')).toEqual({ icon: 'mdi:youtube', platform: 'YouTube', label: '@channel' });
    expect(describeProfileLink('https://anilist.co/user/Someone')).toEqual({ icon: 'material-symbols:link', platform: 'AniList', label: 'Someone' });
  });

  it('matches subdomains of a platform but not look-alike hosts', () => {
    expect(describeProfileLink('https://m.youtube.com/@channel').platform).toBe('YouTube');
    expect(describeProfileLink('https://notyoutube.com/@channel').platform).toBeNull();
  });

  it('shows host and last path segment for other sites', () => {
    expect(describeProfileLink('https://www.my-blog.example/posts/%E6%97%A5%E6%9C%AC')).toEqual({
      icon: 'material-symbols:link',
      platform: null,
      label: 'my-blog.example/…/日本',
    });
    expect(describeProfileLink('https://my-blog.example/about/').label).toBe('my-blog.example/about');
    expect(describeProfileLink('https://my-blog.example/').label).toBe('my-blog.example');
  });

  it('names a Greasy Fork script or user from its slug', () => {
    expect(describeProfileLink('https://greasyfork.org/en/scripts/552181-jiten-stats-everywhere')).toEqual({
      icon: 'mdi:script-text-outline',
      platform: 'Greasy Fork',
      label: 'jiten stats everywhere',
    });
    expect(describeProfileLink('https://greasyfork.org/en/scripts/552181-jiten-stats/code').label).toBe('jiten stats');
    expect(describeProfileLink('https://greasyfork.org/ja/users/12345-%E5%90%8D%E5%89%8D').label).toBe('名前');
    expect(describeProfileLink('https://greasyfork.org/en/scripts/552181').label).toBe('552181');
  });

  it('leaves the label empty for a bare platform link', () => {
    expect(describeProfileLink('https://github.com/').label).toBe('');
  });
});
