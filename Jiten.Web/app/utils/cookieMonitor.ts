export function readCookie(name: string): string | null {
  if (typeof document === 'undefined') return null;

  let found: string | null = null;
  for (const part of document.cookie.split(';')) {
    const separator = part.indexOf('=');
    if (separator === -1) continue;
    if (part.slice(0, separator).trim() !== name) continue;

    const raw = part.slice(separator + 1).trim();
    if (!raw) continue;
    try {
      found = decodeURIComponent(raw);
    } catch {
      found = raw;
    }
  }
  return found;
}

export class CookieMonitor {
  private lastTokenValue: string | null = null;
  private lastRefreshTokenValue: string | null = null;
  private pollingInterval: number | null = null;
  private onChangeCallback: ((tokens: { token: string | null, refreshToken: string | null }) => void) | null = null;

  constructor(private useBroadcastChannel: boolean) {
    if (!useBroadcastChannel) {
      this.startPolling();
    }

    document.addEventListener('visibilitychange', this.handleVisibilityChange);
  }

  private getCookie(name: string): string | null {
    return readCookie(name);
  }

  private checkCookies() {
    const token = this.getCookie('token');
    const refreshToken = this.getCookie('refreshToken');

    if (token !== this.lastTokenValue || refreshToken !== this.lastRefreshTokenValue) {
      this.lastTokenValue = token;
      this.lastRefreshTokenValue = refreshToken;
      this.onChangeCallback?.({ token, refreshToken });
    }
  }

  private handleVisibilityChange = () => {
    if (!document.hidden) {
      this.checkCookies();
    }
  }

  private startPolling() {
    this.pollingInterval = window.setInterval(() => {
      this.checkCookies();
    }, 5000);
  }

  onChange(callback: (tokens: { token: string | null, refreshToken: string | null }) => void) {
    this.onChangeCallback = callback;
    this.lastTokenValue = this.getCookie('token');
    this.lastRefreshTokenValue = this.getCookie('refreshToken');
  }

  destroy() {
    if (this.pollingInterval !== null) {
      clearInterval(this.pollingInterval);
    }
    document.removeEventListener('visibilitychange', this.handleVisibilityChange);
  }
}
